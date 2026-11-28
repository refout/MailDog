using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Mail;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Platform;
using MailDog.Html;
using MailDog.UI.Services;
using MailDog.UI.Views;

namespace MailDog.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void AdjustFolderUnread(long folderId, int delta)
    {
        if (_foldersById.TryGetValue(folderId, out var folder))
        {
            folder.UnreadCount = Math.Max(0, folder.UnreadCount + delta);
        }
    }

    private void AddMessages(IEnumerable<MailSummaryDto> messages)
    {
        foreach (var message in messages)
        {
            if (_allMessages.All(existing => existing.Id != message.Id))
            {
                var viewModel = new MailSummaryViewModel(message);
                viewModel.ShowSelection = IsSelectionMode;
                viewModel.ShowPreview = ShowMessagePreview;
                viewModel.SearchQuery = SearchText.Trim();
                viewModel.PropertyChanged += OnMessagePropertyChanged;
                _allMessages.Add(viewModel);
            }
        }

        NotifySelectionState();
    }

    /// <summary>全局搜索结果只保留在搜索期间；清空搜索或切回当前文件夹时移除。</summary>
    private void AddSearchSummaries(IEnumerable<MailSummaryDto> messages)
    {
        var existingIds = _allMessages.Select(message => message.Id).ToHashSet();
        AddMessages(messages);
        foreach (var message in _allMessages.Where(message =>
                     !existingIds.Contains(message.Id) && !IsMessageInActiveScope(message)))
        {
            if (_searchExtraMessages.All(extra => extra.Id != message.Id))
            {
                _searchExtraMessages.Add(message);
            }
        }
    }

    private bool IsMessageInActiveScope(MailSummaryViewModel message)
    {
        if (_activeRole is { } role)
        {
            return _foldersById.GetValueOrDefault(message.FolderId)?.Role == role;
        }

        return _activeFolderId is { } folderId && message.FolderId == folderId;
    }

    private void RemoveSearchExtras()
    {
        if (_searchExtraMessages.Count == 0)
        {
            return;
        }

        foreach (var message in _searchExtraMessages)
        {
            _allMessages.Remove(message);
            message.PropertyChanged -= OnMessagePropertyChanged;
        }

        _searchExtraMessages.Clear();
        if (SelectedMessage is { } selected && !_allMessages.Contains(selected))
        {
            SelectedMessage = null;
        }
    }

    private void ApplyFilter(long? preserveId = null)
    {
        if (string.IsNullOrWhiteSpace(SearchText) && _searchExtraMessages.Count > 0)
        {
            RemoveSearchExtras();
        }

        var query = _allMessages.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            var indexedIds = _searchResultIds;
            query = query.Where(message =>
                message.Subject.Contains(term, StringComparison.OrdinalIgnoreCase)
                || message.Sender.Contains(term, StringComparison.OrdinalIgnoreCase)
                || message.SenderAddress.Contains(term, StringComparison.OrdinalIgnoreCase)
                || message.Preview.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (indexedIds is not null && indexedIds.Contains(message.Id)));
        }

        query = FilterIndex switch
        {
            1 => query.Where(message => message.IsUnread),
            2 => query.Where(message => message.IsFlagged),
            3 => query.Where(message => message.HasAttachments),
            _ => query
        };

        if (SelectedTagFilter is { } tagFilter)
        {
            query = query.Where(message => message.HasTag(tagFilter.Id));
        }

        var filtered = query.ToList();
        filtered.Sort(CompareMessages);
        var visible = CollapseThreads(filtered);

        // 集合没有变化时保持原样，避免列表与选中项被重建导致阅读区闪烁。
        if (visible.Count == Messages.Count && visible.SequenceEqual(Messages))
        {
            IsEmpty = Messages.Count == 0 && !IsBusy && _activeScope is not null;
            EnsureInitialSelection();
            ClearReaderIfNoVisibleMessages();
            NotifySelectionState();
            return;
        }

        var keepId = preserveId ?? SelectedMessage?.Id;

        _suppressSelectionHandlers = true;
        try
        {
            // 原地增删，避免清空列表导致滚动位置与选中项跳变（滚到底部自动加载时需要）。
            var target = visible.ToHashSet();

            for (var index = Messages.Count - 1; index >= 0; index--)
            {
                if (!target.Contains(Messages[index]))
                {
                    Messages.RemoveAt(index);
                }
            }

            for (var index = 0; index < visible.Count; index++)
            {
                if (index >= Messages.Count)
                {
                    Messages.Add(visible[index]);
                }
                else if (!ReferenceEquals(Messages[index], visible[index]))
                {
                    var existing = Messages.IndexOf(visible[index]);
                    if (existing >= 0)
                    {
                        Messages.Move(existing, index);
                    }
                    else
                    {
                        Messages.Insert(index, visible[index]);
                    }
                }
            }

            if (SelectedMessage is null || !Messages.Contains(SelectedMessage))
            {
                SelectedMessage = keepId is null
                    ? null
                    : Messages.FirstOrDefault(message => message.Id == keepId);
            }
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }

        IsEmpty = Messages.Count == 0 && !IsBusy && _activeScope is not null;
        EnsureInitialSelection();
        ClearReaderIfNoVisibleMessages();
        NotifySelectionState();
    }

    /// <summary>把邮件按服务端线程键折叠，列表只保留每个会话最新的一封。</summary>
    private static List<MailSummaryViewModel> CollapseThreads(List<MailSummaryViewModel> filtered)
    {
        foreach (var message in filtered)
        {
            message.ThreadCount = 1;
            message.ThreadMessages = [message];
        }

        var groups = new Dictionary<string, List<MailSummaryViewModel>>(StringComparer.Ordinal);
        var representatives = new List<MailSummaryViewModel>();
        foreach (var message in filtered)
        {
            var key = ResolveThreadKey(message);
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
                representatives.Add(message);
            }

            group.Add(message);
        }

        foreach (var (_, group) in groups)
        {
            if (group.Count <= 1)
            {
                continue;
            }

            var representative = group[0];
            representative.ThreadMessages = group;
            representative.ThreadCount = group.Count;
        }

        return representatives;
    }

    private static string ResolveThreadKey(MailSummaryViewModel message)
    {
        if (!string.IsNullOrWhiteSpace(message.ThreadKey))
        {
            return $"h:{message.AccountId}:{message.ThreadKey}";
        }

        // 旧数据没有 References 头时，仅对带回复/转发前缀的主题做保守回退。
        var normalized = NormalizeThreadSubject(message.Subject);
        if (normalized.Length > 0
            && !string.Equals(normalized, message.Subject.Trim(), StringComparison.Ordinal))
        {
            return $"s:{message.AccountId}:{normalized.ToLowerInvariant()}";
        }

        return $"i:{message.Id}";
    }

    private static string NormalizeThreadSubject(string subject)
    {
        var value = subject.Trim();
        while (true)
        {
            var next = value;
            foreach (var prefix in ThreadSubjectPrefixes)
            {
                if (next.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    next = next[prefix.Length..].TrimStart();
                    break;
                }
            }

            if (string.Equals(next, value, StringComparison.Ordinal))
            {
                return value;
            }

            value = next;
        }
    }

    /// <summary>列表有邮件但没有选中项时，自动选中第一封；未读筛选避免连续自动标读。</summary>
    private void EnsureInitialSelection()
    {
        if (IsSelectionMode || FilterIndex == 1 || IsBusy || _suppressSelectionHandlers)
        {
            return;
        }

        if (SelectedMessage is null && Messages.Count > 0)
        {
            SelectedMessage = Messages[0];
        }
    }

    /// <summary>列表已经为空时同步清空阅读区，避免删除最后一封邮件后仍显示旧正文。</summary>
    private void ClearReaderIfNoVisibleMessages()
    {
        if (Messages.Count > 0 || IsBusy)
        {
            return;
        }

        SelectedMessage = null;
        if (_currentBody is null && !Reader.HasSelection)
        {
            return;
        }

        _currentBody = null;
        Reader.Reset();
    }

    /// <summary>返回当前“跳转”设定的本地时间区间；未选择时返回 null。</summary>
    private (DateTimeOffset Start, DateTimeOffset End)? GetJumpRange()
    {
        var today = DateTimeOffset.Now.Date;

        return JumpRangeIndex switch
        {
            1 => (new DateTimeOffset(today), new DateTimeOffset(today.AddDays(1))),
            2 => (new DateTimeOffset(today.AddDays(-1)), new DateTimeOffset(today)),
            3 => (new DateTimeOffset(LastWeekStart(today)), new DateTimeOffset(ThisWeekStart(today))),
            4 => (new DateTimeOffset(LastMonthStart(today)), new DateTimeOffset(ThisMonthStart(today))),
            5 => GetCustomRange(),
            6 => (
                new DateTimeOffset(new DateTime(today.Year - 1, 1, 1)),
                new DateTimeOffset(new DateTime(today.Year, 1, 1))),
            _ => null
        };
    }

    private (DateTimeOffset Start, DateTimeOffset End)? GetCustomRange()
    {
        if (CustomDate is null)
        {
            return null;
        }

        // 单日选择：只显示该日期当天的邮件。
        var day = CustomDate.Value.Date;
        return (new DateTimeOffset(day), new DateTimeOffset(day.AddDays(1)));
    }

    private static DateTime LastWeekStart(DateTime today)
        => ThisWeekStart(today).AddDays(-7);

    private static DateTime ThisWeekStart(DateTime today)
    {
        // 本周一。
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        return today.AddDays(-daysSinceMonday);
    }

    private static DateTime LastMonthStart(DateTime today)
        => new DateTime(today.Year, today.Month, 1).AddMonths(-1);

    private static DateTime ThisMonthStart(DateTime today)
        => new DateTime(today.Year, today.Month, 1);

    private int CompareMessages(MailSummaryViewModel left, MailSummaryViewModel right)
    {
        var result = SortFieldIndex switch
        {
            1 => string.Compare(left.Sender, right.Sender, StringComparison.CurrentCulture),
            2 => CategoryRank(left).CompareTo(CategoryRank(right)),
            3 => left.Size.CompareTo(right.Size),
            4 => ImportanceRank(left).CompareTo(ImportanceRank(right)),
            5 => string.Compare(left.Subject, right.Subject, StringComparison.CurrentCulture),
            6 => TypeRank(left).CompareTo(TypeRank(right)),
            _ => left.Date.CompareTo(right.Date)
        };

        // 主键相同时按日期倒序，保持稳定的视觉顺序。
        if (result == 0)
        {
            result = right.Date.CompareTo(left.Date);
        }

        return SortDescending ? -result : result;
    }

    private static int CategoryRank(MailSummaryViewModel message)
    {
        if (message.IsFlagged)
        {
            return 0;
        }

        return message.IsUnread ? 1 : message.HasAttachments ? 2 : 3;
    }

    private static int ImportanceRank(MailSummaryViewModel message)
    {
        if (message.IsFlagged)
        {
            return 0;
        }

        return message.IsUnread ? 1 : 2;
    }

    private static int TypeRank(MailSummaryViewModel message)
        => message.HasAttachments ? 0 : 1;

    private void OnReaderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReaderViewModel.UseHtml))
        {
            OnPropertyChanged(nameof(IsReaderHtmlVisible));
        }
    }

    /// <summary>新邮件所属文件夹是否落在当前浏览范围内（单文件夹或统一文件夹）。</summary>
    private bool IsFolderInActiveScope(long folderId)
    {
        if (_activeFolderId == folderId)
        {
            return true;
        }

        return _activeRole is { } role
               && _folderRoles.TryGetValue(folderId, out var folderRole)
               && folderRole == role;
    }

    private void OnEngineEvent(IIpcMessage message)
    {
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                switch (message)
                {
                    case NewMailEvent newMail:
                        if (IsFolderInActiveScope(newMail.FolderId))
                        {
                            await RefreshMessagesAsync();
                        }

                        break;

                    case MailChangedEvent changed:
                        if (IsFolderInActiveScope(changed.FolderId))
                        {
                            await RefreshMessagesAsync();
                        }

                        break;

                    case ShowMainWindowEvent show:
                        await HandleShowRequestAsync(show);
                        break;

                    case SyncStateEvent syncState:
                        HandleSyncState(syncState);
                        break;

                    case AuthStateEvent authState:
                        await HandleAuthStateAsync(authState);
                        break;

                    case AccountChangedEvent:
                        await LoadAccountsAsync();
                        break;
                }
            }
            catch (Exception exception)
            {
                // 广播处理是后台到 UI 的边界：单个事件失败不能留下未观察任务，也不能拖垮后续事件。
                ShowError(exception);
            }
        });
    }

    private async Task TakePendingNavigationAsync()
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            var pending = await _engine.RequestAsync<PendingNavigationResponse>(
                IpcMessageTypes.TakePendingNavigation,
                new TakePendingNavigationRequest());

            if (pending.ComposeUri is { Length: > 0 } composeUri)
            {
                OpenMailto(composeUri);
                return;
            }

            if (pending.EmlPath is { Length: > 0 } emlPath)
            {
                await OpenEmlFileAsync(emlPath);
                return;
            }

            if (pending.OpenAbout)
            {
                OpenAbout();

                if (pending.CheckUpdate)
                {
                    await CheckUpdateAsync();
                }
            }

            if (pending.HasTarget && pending.MessageId is { } messageId)
            {
                await NavigateToMessageAsync(messageId, pending.AccountId, pending.FolderId);
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task HandleShowRequestAsync(ShowMainWindowEvent show)
    {
        ShowRequested?.Invoke();

        if (show.ComposeUri is { Length: > 0 } composeUri)
        {
            OpenMailto(composeUri);
            return;
        }

        if (show.EmlPath is { Length: > 0 } emlPath)
        {
            await OpenEmlFileAsync(emlPath);
            return;
        }

        if (show.OpenAbout)
        {
            OpenAbout();

            if (show.CheckUpdate)
            {
                await CheckUpdateAsync();
            }

            return;
        }

        if (show.MessageId is { } messageId)
        {
            await NavigateToMessageAsync(messageId, show.AccountId, show.FolderId);
        }
    }

    private async Task NavigateToMessageAsync(long messageId, string? accountId, long? folderId)
    {
        if (!IsConnected)
        {
            return;
        }

        if (IsSettingsOpen)
        {
            CloseOverlay();
        }

        if (!string.IsNullOrEmpty(accountId) && SelectedAccount?.Id != accountId)
        {
            var account = Accounts.FirstOrDefault(item => item.Id == accountId);

            if (account is not null)
            {
                _suppressSelectionHandlers = true;
                try
                {
                    SelectedAccount = account;
                }
                finally
                {
                    _suppressSelectionHandlers = false;
                }

                await RefreshFolderTreeAsync();
                ExpandSelectedAccount();
            }
        }

        if (folderId is { } targetFolderId && SelectedFolder?.Id != targetFolderId)
        {
            var folder = Folders.FirstOrDefault(item => item.Id == targetFolderId);

            if (folder is not null)
            {
                _suppressSelectionHandlers = true;
                try
                {
                    SelectedUnifiedFolder = null;
                    SelectedFolder = folder;
                }
                finally
                {
                    _suppressSelectionHandlers = false;
                }

                UpdateActiveStates();
                SetSidebarSelectionSilently(folder);
                await LoadMessagesAsync(folder);
            }
        }

        // 清掉可能遮挡目标邮件的搜索与筛选条件。
        if (!string.IsNullOrEmpty(SearchText))
        {
            SearchText = string.Empty;
        }

        if (FilterIndex != 0)
        {
            FilterIndex = 0;
        }

        if (JumpRangeIndex != 0)
        {
            JumpRangeIndex = 0;
        }

        var message = Messages.FirstOrDefault(item => item.Id == messageId)
                      ?? _allMessages.FirstOrDefault(item => item.Id == messageId);

        if (message is not null)
        {
            SelectedMessage = message;
        }
    }

    private void HandleSyncState(SyncStateEvent syncState)
    {
        var account = Accounts.FirstOrDefault(item => item.Id == syncState.AccountId);

        if (syncState.State == SyncStateKind.Syncing)
        {
            _syncErrors.Remove(syncState.AccountId);
            UpdateOfflineState();

            var message = syncState.Message ?? "正在同步…";
            if (account is not null)
            {
                account.SyncStatus = message;
            }

            return;
        }

        if (syncState.State == SyncStateKind.Error)
        {
            var message = syncState.Message ?? "同步失败。";
            _syncErrors[syncState.AccountId] = message;
            UpdateOfflineState();

            if (account is not null)
            {
                account.SyncStatus = message;
            }

            HasError = true;
            return;
        }

        _syncErrors.Remove(syncState.AccountId);
        UpdateOfflineState();

        if (account is not null)
        {
            account.SyncStatus = null;
        }

        if (IsOffline)
        {
            HasError = true;
        }
        else
        {
            HasError = false;
        }

        // 任意账户同步结束后都刷新树与当前范围（统一文件夹可能包含该账户）。
        if (SelectedAccount is not null)
        {
            _ = ReloadCurrentFolderAsync();
        }
    }

    private async Task HandleAuthStateAsync(AuthStateEvent authState)
    {
        switch (authState.State)
        {
            case AuthStateKind.BrowserOpened:
                IsAddingAccount = true;
                AddAccountStatus = authState.Message ?? "请在浏览器中完成登录。";
                break;

            case AuthStateKind.Connected:
                IsAddingAccount = false;
                AddAccountStatus = "登录成功。";
                // OutlookAuthService 先广播 AccountChanged、再广播 Connected；
                // 列表由 AccountChanged 统一重载，这里不再重复触发第二次重建。
                CloseOverlay();
                break;

            case AuthStateKind.Failed:
                IsAddingAccount = false;
                AddAccountStatus = authState.Message ?? "登录失败。";
                break;

            case AuthStateKind.Required:
                await LoadAccountsAsync();
                break;
        }
    }

    private async Task ReloadCurrentFolderAsync()
    {
        if (SelectedAccount is null && SelectedUnifiedFolder is null)
        {
            return;
        }

        // 同步完成后只刷新文件夹树与当前列表，不改变用户选择。
        await RefreshFolderTreeAsync();
        await RefreshMessagesAsync();
    }

    private void OnEngineDisconnected(string reason)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            _syncErrors.Clear();
            IsConnected = false;
            HasError = true;
            UpdateOfflineState();
            OfflineMessage = $"与后台的连接已断开：{reason}。当前显示已加载的本地缓存。";
            StatusMessage = OfflineMessage;
        });
    }

    private void ShowError(Exception exception)
    {
        HasError = true;
        StatusMessage = GetErrorMessage(exception);
    }

    private static string GetErrorMessage(Exception exception)
        => exception switch
        {
            IpcException ipc => ipc.Message,
            TimeoutException => "请求超时，请重试。",
            _ => exception.Message
        };

    private void ApplyTheme()
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = Theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        RefreshBackgroundPresets();
    }

    public void RefreshBackgroundPresets()
    {
        var dark = Theme == "dark"
            || (Theme != "light" && Application.Current?.ActualThemeVariant == ThemeVariant.Dark);
        var active = BackgroundColorHex.Trim();

        (string Name, string Value)[] presets = dark
            ? [
                ("默认", string.Empty),
                ("石墨", "#2F3337"),
                ("藏青", "#24334D"),
                ("深蓝", "#1F3B5C"),
                ("深褐", "#3B2F27"),
                ("墨绿", "#233A2C"),
                ("深紫", "#342A40"),
                ("炭黑", "#121212")
            ]
            : [
                ("默认", string.Empty),
                ("冷灰", "#EDF1F5"),
                ("雾蓝", "#E8F1FA"),
                ("米白", "#FAF7F2"),
                ("暖沙", "#F7F1E6"),
                ("浅绿", "#EDF4ED"),
                ("浅青", "#E9F4F3"),
                ("淡紫", "#F1EDF7")
            ];

        BackgroundPresets.Clear();

        foreach (var preset in presets)
        {
            var preview = !string.IsNullOrEmpty(preset.Value)
                ? preset.Value
                : dark ? "#202020" : "#F3F3F3";
            var selected = active.Length == 0
                ? string.IsNullOrEmpty(preset.Value)
                : string.Equals(preset.Value, active, StringComparison.OrdinalIgnoreCase);

            BackgroundPresets.Add(new BackgroundPresetViewModel(
                preset.Name,
                preset.Value,
                preview,
                selected,
                value => BackgroundColorHex = value ?? string.Empty));
        }

        OnPropertyChanged(nameof(IsCustomBackgroundSelected));
    }

    private static bool IsDarkTheme()
        => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
        _lockTimer?.Stop();
        _engine.EventReceived -= OnEngineEvent;
        _engine.Disconnected -= OnEngineDisconnected;
        _engine.EngineStarting -= OnEngineStarting;
        Reader.PropertyChanged -= OnReaderPropertyChanged;
        _ = _engine.DisposeAsync();
    }
}
