using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Html;
using MailDog.UI.Services;

namespace MailDog.UI.ViewModels;

public sealed partial class ComposeViewModel
{
    public async Task<bool> SaveDraftAsync()
    {
        if (IsBusy || IsQueued)
        {
            return false;
        }

        if (SelectedAccount is null)
        {
            Error = "请选择发件账户。";
            return false;
        }

        if (IsEmpty)
        {
            _draftSaved = true;
            return true;
        }

        IsBusy = true;
        Error = null;
        Status = "正在保存草稿…";

        try
        {
            var response = await _engine.RequestAsync<DraftSavedResponse>(
                IpcMessageTypes.SaveDraft,
                BuildDraftRequest(),
                TimeSpan.FromSeconds(60));

            _draftSaved = true;
            Status = $"已保存到{response.FolderName}。";
            Completed?.Invoke(this, Status!);
            return true;
        }
        catch (Exception exception) when (exception is IpcException or InvalidOperationException or TimeoutException)
        {
            Error = exception.Message;
            Status = null;
            return false;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!CanSend)
        {
            return;
        }

        if (SelectedAccount is null)
        {
            Error = "请选择发件账户。";
            return;
        }

        if (string.IsNullOrWhiteSpace(ToRecipientsValue))
        {
            Error = "请填写至少一个收件人。";
            return;
        }

        var scheduledAt = ResolveSchedule(out var scheduleError);
        if (scheduleError is not null)
        {
            Error = scheduleError;
            return;
        }

        IsBusy = true;
        Error = null;
        Status = "正在发送…";

        try
        {
            var response = await _engine.RequestAsync<SendMailResponse>(
                IpcMessageTypes.SendMail,
                BuildSendRequest(_sendChecksConfirmed, scheduledAt),
                TimeSpan.FromSeconds(75));

            if (response.Status == SendStatuses.ConfirmRequired)
            {
                SendWarning = response.Error ?? "发送前检查未通过。";
                Status = null;
                return;
            }

            if (response.Sent)
            {
                _sent = true;
                IsQueued = false;
                Status = "邮件已发送。";

                if (_draftMessageId is { } draftId)
                {
                    _ = DeleteOriginalDraftAsync(draftId);
                }

                Completed?.Invoke(this, Status!);
                CloseRequested?.Invoke();
                return;
            }

            if (response.Queued)
            {
                IsQueued = true;
                Error = response.Error;

                if (response.ScheduledAtUtc is { } scheduled)
                {
                    Status = $"已安排在 {scheduled.LocalDateTime:M月d日 HH:mm} 发出。";
                    StartScheduledWindow(response.OutboxId);
                }
                else if (response.DelaySeconds > 0)
                {
                    Status = $"已放进发件箱，{response.DelaySeconds} 秒后发出。";
                    StartUndoWindow(response.DelaySeconds, response.OutboxId);
                }
                else
                {
                    Status = "已加入发件箱，将在后台自动重试。";
                }

                Completed?.Invoke(this, Status!);
                return;
            }

            Error = response.Error ?? "发送失败。";
            Status = null;
        }
        catch (Exception exception) when (exception is IpcException or InvalidOperationException or TimeoutException)
        {
            Error = exception.Message;
            Status = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>撤回还没发出去的那封：直接从发件箱里删掉。</summary>
    [RelayCommand]
    private async Task UndoQueuedSendAsync()
    {
        if (_undoOutboxId <= 0)
        {
            return;
        }

        var outboxId = _undoOutboxId;

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.DeleteOutbox,
                new DeleteOutboxRequest { OutboxId = outboxId },
                TimeSpan.FromSeconds(20));

            IsQueued = false;
            Status = "已撤回，邮件没有发出。";
            Error = null;
        }
        catch (Exception exception) when (exception is IpcException or InvalidOperationException or TimeoutException)
        {
            Error = exception.Message;
        }
        finally
        {
            EndUndoWindow();
        }
    }

    private void StartScheduledWindow(long outboxId)
    {
        EndUndoWindow();
        _undoOutboxId = outboxId;
        UndoHint = "计划发送前可以撤回";
    }

    private void StartUndoWindow(int seconds, long outboxId)
    {
        EndUndoWindow();

        _undoOutboxId = outboxId;
        var left = Math.Max(seconds, 1);
        UndoHint = $"还剩 {left} 秒可以撤回";

        _undoTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _undoTimer.Tick += (_, _) =>
        {
            left--;

            if (left <= 0)
            {
                EndUndoWindow();
                Status = "已交给后台发送。";
                return;
            }

            UndoHint = $"还剩 {left} 秒可以撤回";
        };
        _undoTimer.Start();
    }

    private void EndUndoWindow()
    {
        _undoTimer?.Stop();
        _undoTimer = null;
        _undoOutboxId = 0;
        UndoHint = null;
    }

    /// <summary>用户看过提醒后仍然要发：跳过发送前检查，整封重新提交。</summary>
    [RelayCommand]
    private async Task SendAnywayAsync()
    {
        _sendChecksConfirmed = true;
        SendWarning = null;
        await SendAsync();
    }

    [RelayCommand]
    private void DismissSendWarning()
    {
        SendWarning = null;
        _sendChecksConfirmed = false;
    }

    [RelayCommand]
    private async Task SaveDraft()
    {
        if (await SaveDraftAsync())
        {
            CloseRequested?.Invoke();
        }
    }

    [RelayCommand]
    private async Task DiscardAsync()
    {
        if (!CanCloseSilently && !IsEmpty && !await SaveDraftAsync())
        {
            return;
        }

        _draftSaved = true;
        CloseRequested?.Invoke();
    }

    private DateTimeOffset? ResolveSchedule(out string? error)
    {
        error = null;
        var now = DateTimeOffset.Now;

        switch (SelectedScheduleOption)
        {
            case "1 小时后":
                return now.AddHours(1);
            case "今晚 20:00":
                return new DateTimeOffset(now.Year, now.Month, now.Day, 20, 0, 0, now.Offset)
                    .AddDays(now.Hour >= 20 ? 1 : 0);
            case "明天 08:00":
                return new DateTimeOffset(now.Year, now.Month, now.Day, 8, 0, 0, now.Offset).AddDays(1);
            case "自定义时间":
                if (!DateTimeOffset.TryParse(CustomScheduleText, out var custom))
                {
                    error = "自定义时间格式不对，请用 2026-11-26 08:30 这样的格式。";
                    return null;
                }

                if (custom <= now.AddMinutes(1))
                {
                    error = "计划发送时间必须至少在 1 分钟之后。";
                    return null;
                }

                return custom;
            default:
                return null;
        }
    }

    private SendMailRequest BuildSendRequest(bool skipChecks = false, DateTimeOffset? scheduledAt = null)
        => new()
        {
            AccountId = SelectedAccount!.Id,
            FromAddress = SelectedIdentity?.Address ?? SelectedAccount.Email,
            FromName = SelectedIdentity?.DisplayName ?? SelectedAccount.DisplayName,
            To = ToRecipientsValue.Trim(),
            Cc = CcRecipientsValue.Trim(),
            Bcc = BccRecipientsValue.Trim(),
            Subject = SubjectText.Trim(),
            BodyText = BodyText,
            BodyHtml = BodyHtml,
            InReplyTo = Mode is ComposeMode.Reply or ComposeMode.ReplyAll ? _original?.RfcMessageId : null,
            AttachmentPaths = Attachments.Select(item => item.Path).ToArray(),
            SkipSendChecks = skipChecks,
            ScheduledAtUtc = scheduledAt?.ToUniversalTime()
        };

    private SaveDraftRequest BuildDraftRequest()
        => new()
        {
            AccountId = SelectedAccount!.Id,
            FromAddress = SelectedIdentity?.Address ?? SelectedAccount.Email,
            FromName = SelectedIdentity?.DisplayName ?? SelectedAccount.DisplayName,
            To = ToRecipientsValue.Trim(),
            Cc = CcRecipientsValue.Trim(),
            Bcc = BccRecipientsValue.Trim(),
            Subject = SubjectText.Trim(),
            BodyText = BodyText,
            BodyHtml = BodyHtml,
            ReplaceDraftMessageId = _draftMessageId,
            AttachmentPaths = Attachments.Select(item => item.Path).ToArray()
        };

    private async Task DeleteOriginalDraftAsync(long draftMessageId)
    {
        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.DeleteMessage,
                new DeleteMessageRequest { MessageId = draftMessageId, Permanent = true },
                TimeSpan.FromSeconds(20));
        }
        catch (Exception)
        {
            // 邮件已经发出；旧草稿删除失败不应影响发送结果，下次同步会保留可见草稿。
        }
    }

}
