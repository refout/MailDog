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
    [RelayCommand]
    private async Task ResumeDraftAsync()
    {
        if (SelectedMessage is null || SelectedMessageFolder?.Role != FolderRole.Drafts)
        {
            return;
        }

        if (IsComposeOpen)
        {
            StatusMessage = "请先关闭当前正在撰写的邮件。";
            return;
        }

        var draft = SelectedMessage;
        var accounts = BuildComposeAccounts();

        try
        {
            var response = await _engine.RequestAsync<DraftContentResponse>(
                IpcMessageTypes.GetDraft,
                new GetDraftRequest { MessageId = draft.Id },
                TimeSpan.FromSeconds(60));

            var compose = new ComposeViewModel(
                _engine,
                accounts,
                ComposeMode.Draft,
                null,
                response.Draft,
                draft.AccountId,
                accountSignatures: BuildAccountSignatures(),
                templates: BuildTemplateDtos(),
                contacts: _contacts,
                identities: BuildAccountIdentities());
            MountCompose(compose);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void MountCompose(ComposeViewModel compose)
    {
        compose.Completed += (viewModel, message) =>
        {
            StatusMessage = message;
            _ = SyncAfterComposeAsync(viewModel.SelectedAccount?.Id);
        };

        compose.CloseRequested += CloseComposeInternal;
        Compose = compose;
        IsSettingsOpen = false;
        IsComposeOpen = true;
    }

    /// <summary>mailto: 协议或外部链接：用链接里的收件人、主题和正文预填撰写窗口。</summary>
    public void OpenMailto(string uri)
    {
        if (Accounts.Count == 0)
        {
            StatusMessage = "请先添加邮箱账户，再撰写邮件。";
            return;
        }

        if (IsComposeOpen)
        {
            StatusMessage = "请先关闭当前正在撰写的邮件。";
            return;
        }

        var fields = MailtoParser.Parse(uri);

        if (fields is null)
        {
            StatusMessage = "这个 mailto 链接无法解析。";
            return;
        }

        var compose = new ComposeViewModel(
            _engine,
            BuildComposeAccounts(),
            ComposeMode.New,
            null,
            signature: SignatureText,
            accountSignatures: BuildAccountSignatures(),
            templates: BuildTemplateDtos(),
            contacts: _contacts,
            identities: BuildAccountIdentities());

        compose.ToText = fields.GetValueOrDefault("to", string.Empty);
        compose.CcText = fields.GetValueOrDefault("cc", string.Empty);
        compose.BccText = fields.GetValueOrDefault("bcc", string.Empty);
        compose.SubjectText = fields.GetValueOrDefault("subject", string.Empty);
        compose.SetExternalBody(fields.GetValueOrDefault("body", string.Empty));

        MountCompose(compose);
    }

    /// <summary>直接打开 .eml：向 Engine 要只读预览，弹独立窗口，不改本地邮件库。</summary>
    public async Task OpenEmlFileAsync(string path)
    {
        if (!_engine.IsConnected)
        {
            StatusMessage = "后台尚未连接，暂时无法打开 .eml。";
            return;
        }

        try
        {
            var response = await _engine.RequestAsync<EmlPreviewResponse>(
                IpcMessageTypes.GetEmlPreview,
                new GetEmlPreviewRequest { Path = path },
                TimeSpan.FromSeconds(20)).ConfigureAwait(true);

            var window = new EmlViewerWindow(new EmlPreviewViewModel(response.Preview));
            window.Show();
        }
        catch (Exception exception)
        {
            StatusMessage = $"打开 .eml 失败：{GetErrorMessage(exception)}";
        }
    }

    [RelayCommand]
    private void NewMail() => OpenCompose(ComposeMode.New);

    [RelayCommand]
    private void Reply() => OpenCompose(ComposeMode.Reply);

    [RelayCommand]
    private void ReplyAll() => OpenCompose(ComposeMode.ReplyAll);

    [RelayCommand]
    private void Forward() => OpenCompose(ComposeMode.Forward);

    private void OpenCompose(ComposeMode mode) => OpenCompose(mode, null);

    private void OpenCompose(ComposeMode mode, MailTemplateDto? initialTemplate)
    {
        if (Accounts.Count == 0)
        {
            StatusMessage = "请先添加邮箱账户，再撰写邮件。";
            return;
        }

        if (mode != ComposeMode.New && _currentBody is null)
        {
            StatusMessage = "正文尚未加载完成，请稍后再试。";
            return;
        }

        var accounts = BuildComposeAccounts();

        var compose = new ComposeViewModel(
            _engine,
            accounts,
            mode,
            mode == ComposeMode.New ? null : _currentBody,
            signature: SignatureText,
            accountSignatures: BuildAccountSignatures(),
            templates: BuildTemplateDtos(),
            initialTemplate: initialTemplate,
            contacts: _contacts,
            identities: BuildAccountIdentities());

        MountCompose(compose);
    }

    [RelayCommand]
    private async Task CloseComposeAsync()
    {
        var compose = Compose;
        if (compose is null)
        {
            return;
        }

        if (!compose.CanCloseSilently && !compose.IsEmpty)
        {
            if (!await compose.SaveDraftAsync())
            {
                return;
            }
        }

        CloseComposeInternal();
    }

    private void CloseComposeInternal()
    {
        Compose = null;
        IsComposeOpen = false;
    }

    private async Task SyncAfterComposeAsync(string? accountId)
    {
        if (string.IsNullOrEmpty(accountId))
        {
            return;
        }

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.SyncAccount,
                new SyncAccountRequest { AccountId = accountId },
                TimeSpan.FromSeconds(10));
        }
        catch
        {
            // 同步失败不影响发送/保存草稿结果，下次轮询会补齐。
        }
    }
}
