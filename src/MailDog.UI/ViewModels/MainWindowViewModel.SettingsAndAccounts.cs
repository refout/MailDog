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
    private void PreviewNotificationSound()
    {
        if (NotificationSoundEnabled)
        {
            SystemSoundPlayer.Play(NotificationSound);
        }
    }

    [RelayCommand]
    private async Task AddPasswordAccountAsync()
    {
        if (SelectedProviderIndex == 0)
        {
            await StartOAuthAsync();
            return;
        }

        var isCustom = SelectedProviderIndex == 2;
        if (isCustom
            && (string.IsNullOrWhiteSpace(CustomImapHost)
                || string.IsNullOrWhiteSpace(CustomSmtpHost)
                || !int.TryParse(CustomImapPortText, out var imapPort)
                || !int.TryParse(CustomSmtpPortText, out var smtpPort)
                || imapPort is < 1 or > 65535
                || smtpPort is < 1 or > 65535))
        {
            AddAccountStatus = "请填写有效的服务器地址和端口（1-65535）。";
            return;
        }

        IsAddingAccount = true;
        AddAccountStatus = "正在验证授权码…";

        try
        {
            var response = await _engine.RequestAsync<AccountResponse>(
                IpcMessageTypes.AddPasswordAccount,
                new AddPasswordAccountRequest
                {
                    Provider = isCustom ? MailProvider.Custom : MailProvider.QQ,
                    Email = AddAccountEmail.Trim(),
                    Secret = AddAccountSecret.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(AddAccountDisplayName)
                        ? null
                        : AddAccountDisplayName.Trim(),
                    ImapHost = isCustom ? CustomImapHost.Trim() : null,
                    ImapPort = isCustom ? int.Parse(CustomImapPortText) : 0,
                    ImapStartTls = isCustom && CustomImapStartTls,
                    SmtpHost = isCustom ? CustomSmtpHost.Trim() : null,
                    SmtpPort = isCustom ? int.Parse(CustomSmtpPortText) : 0,
                    SmtpStartTls = isCustom && CustomSmtpStartTls
                },
                TimeSpan.FromSeconds(60));

            AddAccountStatus = $"账户 {response.Account.Email} 已添加。";
            AddAccountEmail = string.Empty;
            AddAccountDisplayName = string.Empty;
            AddAccountSecret = string.Empty;
            CustomImapHost = string.Empty;
            CustomSmtpHost = string.Empty;

            await LoadAccountsAsync();
            CloseOverlay();
        }
        catch (Exception exception)
        {
            AddAccountStatus = GetErrorMessage(exception);
        }
        finally
        {
            IsAddingAccount = false;
        }
    }

    [RelayCommand]
    private async Task StartOAuthAsync()
    {
        IsAddingAccount = true;
        AddAccountStatus = "正在打开浏览器…";

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.StartOAuth,
                new StartOAuthRequest
                {
                    Provider = MailProvider.Outlook,
                    LoginHint = string.IsNullOrWhiteSpace(AddAccountEmail) ? null : AddAccountEmail.Trim()
                },
                TimeSpan.FromSeconds(15));
        }
        catch (Exception exception)
        {
            IsAddingAccount = false;
            AddAccountStatus = GetErrorMessage(exception);
        }
    }

    private AppSettingsDto BuildSettings()
        => new()
        {
            Theme = Theme,
            Density = Density,
            Material = Material,
            BackgroundColor = string.IsNullOrWhiteSpace(BackgroundColorHex) ? null : BackgroundColorHex.Trim(),
            BackgroundOpacity = Math.Clamp(BackgroundOpacity, 40, 100),
            NotificationsEnabled = NotificationsEnabled,
            NotificationSoundEnabled = NotificationSoundEnabled,
            NotificationSound = NotificationSound,
            NotificationShowSubject = NotificationShowSubject,
            NotificationShowPreview = NotificationShowPreview,
            NotificationMerge = NotificationMerge,
            NotificationInboxOnly = NotificationInboxOnly,
            NotificationQuietEnabled = NotificationQuietEnabled,
            NotificationQuietStart = NotificationQuietStart,
            NotificationQuietEnd = NotificationQuietEnd,
            SyncIntervalMinutes = SyncIntervalValues[Math.Clamp(SyncIntervalIndex, 0, SyncIntervalValues.Length - 1)],
            CacheDays = _settings.CacheDays,
            MarkReadOnOpen = MarkReadOnOpen,
            RunAtStartup = RunAtStartup,
            ShowRemoteImages = ShowRemoteImages,
            SaveSentCopy = SaveSentCopy,
            Signature = SignatureText,
            AccountSignatures = _accountSignatures
                .Select(pair => new AccountSignatureDto
                {
                    AccountId = pair.Key,
                    Signature = pair.Value
                })
                .ToArray(),
            AccountIdentities = _accountIdentities.ToArray(),
            Templates = Templates
                .Select(item => item.Template)
                .ToArray(),
            AccountNotificationSounds = _accountNotificationSounds
                .Select(pair => new AccountNotificationSoundDto
                {
                    AccountId = pair.Key,
                    Sound = pair.Value
                })
                .ToArray(),
            AppLockEnabled = _savedAppLockEnabled,
            AppLockHash = _appLockHash,
            AppLockSalt = _appLockSalt,
            AppLockTimeoutMinutes = _savedAppLockTimeoutMinutes,
            SendDelaySeconds = SendDelayIndex switch
            {
                0 => 0,
                1 => 5,
                2 => 10,
                _ => 30
            },
            RenderMode = RenderModeIndex == 1 ? "plain" : "html"
        };

    private void QueueSettingsSave()
    {
        if (_suppressAutoSave || _disposed)
        {
            return;
        }

        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
        var cts = new CancellationTokenSource();
        _autoSaveCts = cts;
        SettingsSaveHint = "正在保存…";
        _ = SaveSettingsDebouncedAsync(cts);
    }

    private async Task SaveSettingsDebouncedAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(500, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var settings = BuildSettings();

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.UpdateSettings,
                new UpdateSettingsRequest { Settings = settings });

            _settings = settings;
            _allowRemoteImages = settings.ShowRemoteImages;
            await Dispatcher.UIThread.InvokeAsync(() => SettingsSaveHint = "已自动保存");
        }
        catch (Exception exception)
        {
            var message = GetErrorMessage(exception);
            await Dispatcher.UIThread.InvokeAsync(() => SettingsSaveHint = $"保存失败：{message}");
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        IsSettingsOpen = true;
    }

    private IReadOnlyList<AccountSignatureDto> BuildAccountSignatures()
        => _accountSignatures
            .Select(pair => new AccountSignatureDto
            {
                AccountId = pair.Key,
                Signature = pair.Value
            })
            .ToArray();

    private IReadOnlyList<MailTemplateDto> BuildTemplateDtos()
        => Templates.Select(item => item.Template).ToArray();

    private void RebuildQuickReplyTargets()
    {
        QuickReplyTargets.Clear();
        foreach (var template in Templates.Where(item => item.IsQuickReply))
        {
            QuickReplyTargets.Add(new QuickReplyTargetViewModel(template.Template, QuickReply));
        }

        OnPropertyChanged(nameof(HasQuickReplyTargets));
    }

    private void ReloadTemplateList(IReadOnlyList<MailTemplateDto> templates)
    {
        Templates.Clear();
        foreach (var template in templates)
        {
            Templates.Add(new MailTemplateViewModel(template, EditTemplate, DeleteTemplate));
        }

        OnPropertyChanged(nameof(HasTemplates));
        RebuildQuickReplyTargets();
    }

    [RelayCommand]
    private void NewTemplate()
    {
        _editingTemplateId = null;
        TemplateEditorTitle = "新建模板";
        TemplateName = string.Empty;
        TemplateSubject = string.Empty;
        TemplateBody = string.Empty;
        TemplateIsQuickReply = false;
        TemplateHint = null;
        IsTemplateEditorOpen = true;
    }

    private void EditTemplate(MailTemplateViewModel template)
    {
        _editingTemplateId = template.Id;
        TemplateEditorTitle = "编辑模板";
        TemplateName = template.Name;
        TemplateSubject = template.Subject;
        TemplateBody = template.Body;
        TemplateIsQuickReply = template.IsQuickReply;
        TemplateHint = null;
        IsTemplateEditorOpen = true;
    }

    private void DeleteTemplate(MailTemplateViewModel template)
    {
        Templates.Remove(template);
        if (string.Equals(_editingTemplateId, template.Id, StringComparison.Ordinal))
        {
            IsTemplateEditorOpen = false;
            _editingTemplateId = null;
        }

        OnPropertyChanged(nameof(HasTemplates));
        RebuildQuickReplyTargets();
        QueueSettingsSave();
        TemplateHint = $"已删除模板“{template.Name}”。";
    }

    [RelayCommand]
    private void CancelTemplateEdit()
    {
        IsTemplateEditorOpen = false;
        _editingTemplateId = null;
        TemplateHint = null;
    }

    [RelayCommand]
    private void SaveTemplate()
    {
        var name = TemplateName.Trim();
        if (name.Length == 0)
        {
            TemplateHint = "请填写模板名称。";
            return;
        }

        if (string.IsNullOrWhiteSpace(TemplateBody))
        {
            TemplateHint = "模板正文不能为空。";
            return;
        }

        var id = _editingTemplateId ?? Guid.NewGuid().ToString("N");
        var updated = new MailTemplateDto
        {
            Id = id,
            Name = name,
            Subject = TemplateSubject.Trim(),
            Body = TemplateBody.Replace("\r\n", "\n").TrimEnd(),
            IsQuickReply = TemplateIsQuickReply
        };

        var index = Templates.ToList().FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        var viewModel = new MailTemplateViewModel(updated, EditTemplate, DeleteTemplate);
        if (index >= 0)
        {
            Templates[index] = viewModel;
        }
        else
        {
            Templates.Add(viewModel);
        }

        IsTemplateEditorOpen = false;
        _editingTemplateId = null;
        TemplateHint = $"已保存模板“{name}”。";
        OnPropertyChanged(nameof(HasTemplates));
        RebuildQuickReplyTargets();
        QueueSettingsSave();
    }

    [RelayCommand]
    private void QuickReply(QuickReplyTargetViewModel target)
    {
        if (IsComposeOpen)
        {
            StatusMessage = "请先关闭当前正在撰写的邮件。";
            return;
        }

        if (SelectedMessage is null || _currentBody is null)
        {
            StatusMessage = "请先打开一封邮件，再使用快速回复。";
            return;
        }

        OpenCompose(ComposeMode.Reply, target.Template);
    }

    private void RebuildTagMenus()
    {
        TagMenuTargets.Clear();
        TagFilters.Clear();
        foreach (var tag in Tags)
        {
            TagMenuTargets.Add(new TagMenuViewModel(tag.Tag, AddCheckedTag, RemoveCheckedTag));
            TagFilters.Add(new TagFilterViewModel(tag.Tag, SelectTagFilter));
        }

        if (SelectedTagFilter is not null
            && Tags.All(tag => tag.Id != SelectedTagFilter.Id))
        {
            SelectedTagFilter = null;
        }

        OnPropertyChanged(nameof(HasTags));
    }

    private void LoadTags(IReadOnlyList<TagDto> tags)
    {
        Tags.Clear();
        foreach (var tag in tags.OrderBy(item => item.Name, StringComparer.CurrentCulture))
        {
            Tags.Add(new TagViewModel(tag, EditTag, DeleteTag));
        }

        RebuildTagMenus();
    }

    [RelayCommand]
    private void NewTag()
    {
        _editingTagId = null;
        TagEditorTitle = "新建标签";
        TagName = string.Empty;
        TagColor = "#0F7B0F";
        TagHint = null;
        IsTagEditorOpen = true;
    }

    private void EditTag(TagViewModel tag)
    {
        _editingTagId = tag.Id;
        TagEditorTitle = "编辑标签";
        TagName = tag.Name;
        TagColor = tag.Color;
        TagHint = null;
        IsTagEditorOpen = true;
    }

    private async void DeleteTag(TagViewModel tag)
    {
        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.DeleteTag,
                new DeleteTagRequest { TagId = tag.Id });
            Tags.Remove(tag);
            RebuildTagMenus();
            await RefreshTagsForMessagesAsync(Messages.Select(message => message.Id).ToArray());
            TagHint = $"已删除标签“{tag.Name}”。";
            StatusMessage = TagHint;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    [RelayCommand]
    private void CancelTagEdit()
    {
        IsTagEditorOpen = false;
        _editingTagId = null;
        TagHint = null;
    }

    [RelayCommand]
    private async Task SaveTagAsync()
    {
        var name = TagName.Trim();
        if (name.Length == 0)
        {
            TagHint = "请填写标签名称。";
            return;
        }

        if (TagColor.Length != 7 || !TagColor.StartsWith('#') || !TagColor[1..].All(Uri.IsHexDigit))
        {
            TagHint = "颜色必须是 #RRGGBB 格式。";
            return;
        }

        try
        {
            var response = await _engine.RequestAsync<TagsResponse>(
                IpcMessageTypes.SaveTag,
                new SaveTagRequest
                {
                    Tag = new TagDto
                    {
                        Id = _editingTagId ?? 0,
                        Name = name,
                        Color = TagColor.ToUpperInvariant()
                    }
                });

            var saved = response.Items.FirstOrDefault();
            if (saved is not null)
            {
                var existing = Tags.FirstOrDefault(tag => tag.Id == saved.Id);
                if (existing is not null)
                {
                    Tags.Remove(existing);
                }

                Tags.Add(new TagViewModel(saved, EditTag, DeleteTag));
                RebuildTagMenus();
                await RefreshTagsForMessagesAsync(Messages.Select(message => message.Id).ToArray());
                TagHint = $"已保存标签“{saved.Name}”。";
                StatusMessage = TagHint;
            }

            IsTagEditorOpen = false;
            _editingTagId = null;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            TagHint = "标签保存失败。";
        }
    }

    private async void AddCheckedTag(TagMenuViewModel tag)
    {
        await SetCheckedTagsAsync(tag, add: true);
    }

    private async void RemoveCheckedTag(TagMenuViewModel tag)
    {
        await SetCheckedTagsAsync(tag, add: false);
    }

    private async Task SetCheckedTagsAsync(TagMenuViewModel tag, bool add)
    {
        var targets = CheckedTargets();
        if (targets.Length == 0)
        {
            StatusMessage = "请先勾选邮件。";
            return;
        }

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.SetMessageTags,
                new SetMessageTagsRequest
                {
                    MessageIds = targets.Select(message => message.Id).ToArray(),
                    TagIds = [tag.Id],
                    Add = add
                });

            await RefreshTagsForMessagesAsync(Messages.Select(message => message.Id).ToArray());
            StatusMessage = add
                ? $"已给 {targets.Length} 封邮件添加标签“{tag.Name}”。"
                : $"已从 {targets.Length} 封邮件移除标签“{tag.Name}”。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void SelectTagFilter(TagFilterViewModel tag)
    {
        SelectedTagFilter = tag;
        StatusMessage = $"正在显示标签“{tag.Name}”下的邮件。";
    }

    [RelayCommand]
    private void ClearTagFilter()
    {
        SelectedTagFilter = null;
        StatusMessage = "已显示全部邮件。";
    }

    partial void OnSelectedTagFilterChanged(TagFilterViewModel? value)
    {
        OnPropertyChanged(nameof(TagFilterText));
        ApplyFilter();
    }

    private async Task RefreshTagsForMessagesAsync(IReadOnlyList<long> messageIds)
    {
        if (!_engine.IsConnected || messageIds.Count == 0)
        {
            return;
        }

        try
        {
            var response = await _engine.RequestAsync<MessageTagsResponse>(
                IpcMessageTypes.GetMessageTags,
                new GetMessageTagsRequest { MessageIds = messageIds.ToArray() });

            var definitions = Tags.ToDictionary(tag => tag.Id, tag => tag.Tag);
            var assignments = response.Items.ToDictionary(item => item.MessageId, item => item.TagIds);
            foreach (var message in _allMessages.Where(message => messageIds.Contains(message.Id)))
            {
                var tagIds = assignments.GetValueOrDefault(message.Id, []);
                message.SetTags(tagIds
                    .Select(tagId => definitions.GetValueOrDefault(tagId))
                    .Where(tag => tag is not null)
                    .Select(tag => tag!));
            }

            if (SelectedTagFilter is not null)
            {
                ApplyFilter();
            }
        }
        catch
        {
            // 标签是辅助信息，加载失败不阻断邮件列表。
        }
    }

    partial void OnSelectedSignatureAccountChanged(AccountViewModel? value)
    {
        if (value is null)
        {
            _suppressSignatureSync = true;
            AccountSignatureText = string.Empty;
            _suppressSignatureSync = false;
            RebuildIdentitySettings();
            return;
        }

        _suppressSignatureSync = true;
        AccountSignatureText = _accountSignatures.GetValueOrDefault(value.Id) ?? string.Empty;
        _suppressSignatureSync = false;
        RebuildIdentitySettings();
    }

    partial void OnAccountSignatureTextChanged(string value)
    {
        if (_suppressSignatureSync || SelectedSignatureAccount is null)
        {
            return;
        }

        _accountSignatures[SelectedSignatureAccount.Id] = value;
        QueueSettingsSave();
    }

    private void RebuildIdentitySettings()
    {
        AccountIdentities.Clear();
        var account = SelectedSignatureAccount ?? Accounts.FirstOrDefault();
        if (account is null)
        {
            OnPropertyChanged(nameof(HasAccountIdentities));
            return;
        }

        foreach (var identity in _accountIdentities.Where(item =>
                     string.Equals(item.AccountId, account.Id, StringComparison.Ordinal)))
        {
            AccountIdentities.Add(new AccountIdentityViewModel(
                identity,
                account.Email,
                SaveIdentity,
                DeleteIdentity));
        }

        OnPropertyChanged(nameof(HasAccountIdentities));
    }

    private bool IsIdentityAddressAvailable(string address, string? ignoredId)
    {
        if (SelectedSignatureAccount is null
            || string.Equals(address, SelectedSignatureAccount.Email, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !_accountIdentities.Any(item =>
            string.Equals(item.AccountId, SelectedSignatureAccount.Id, StringComparison.Ordinal)
            && !string.Equals(item.Id, ignoredId, StringComparison.Ordinal)
            && string.Equals(item.Address, address, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryNormalizeIdentityAddress(string text, out string address)
    {
        address = text.Trim();
        if (address.Length == 0 || !MailAddress.TryCreate(address, out var parsed))
        {
            return false;
        }

        address = parsed.Address;
        return true;
    }

    [RelayCommand]
    private void AddIdentity()
    {
        if (SelectedSignatureAccount is null)
        {
            IdentityHint = "请先选择账户。";
            return;
        }

        if (!TryNormalizeIdentityAddress(NewIdentityAddress, out var address))
        {
            IdentityHint = "请填写有效的别名邮箱地址。";
            return;
        }

        if (!IsIdentityAddressAvailable(address, null))
        {
            IdentityHint = "该地址与账户主地址或已有别名重复。";
            return;
        }

        var identity = new AccountIdentityDto
        {
            Id = Guid.NewGuid().ToString("N"),
            AccountId = SelectedSignatureAccount.Id,
            Address = address,
            DisplayName = NewIdentityName.Trim(),
            Signature = NewIdentitySignature.Replace("\r\n", "\n").TrimEnd()
        };
        _accountIdentities.Add(identity);
        RebuildIdentitySettings();
        NewIdentityAddress = string.Empty;
        NewIdentityName = string.Empty;
        NewIdentitySignature = string.Empty;
        IdentityHint = $"已添加发信身份 {identity.Address}。";
        QueueSettingsSave();
    }

    private void SaveIdentity(AccountIdentityViewModel row)
    {
        if (SelectedSignatureAccount is null
            || !string.Equals(row.AccountId, SelectedSignatureAccount.Id, StringComparison.Ordinal))
        {
            IdentityHint = "账户已切换，请重新打开这条发信身份。";
            return;
        }

        if (!TryNormalizeIdentityAddress(row.Address, out var address))
        {
            IdentityHint = "请填写有效的别名邮箱地址。";
            return;
        }

        if (!IsIdentityAddressAvailable(address, row.Id))
        {
            IdentityHint = "该地址与账户主地址或已有别名重复。";
            return;
        }

        var index = _accountIdentities.FindIndex(item =>
            string.Equals(item.Id, row.Id, StringComparison.Ordinal));
        var updated = new AccountIdentityDto
        {
            Id = row.Id,
            AccountId = row.AccountId,
            Address = address,
            DisplayName = row.DisplayName.Trim(),
            Signature = row.Signature.Replace("\r\n", "\n").TrimEnd()
        };
        if (index >= 0)
        {
            _accountIdentities[index] = updated;
        }
        else
        {
            _accountIdentities.Add(updated);
        }

        RebuildIdentitySettings();
        IdentityHint = $"已保存发信身份 {address}。";
        QueueSettingsSave();
    }

    private void DeleteIdentity(AccountIdentityViewModel row)
    {
        _accountIdentities.RemoveAll(item => string.Equals(item.Id, row.Id, StringComparison.Ordinal));
        RebuildIdentitySettings();
        IdentityHint = $"已删除发信身份 {row.Address}。";
        QueueSettingsSave();
    }

    private IReadOnlyList<AccountIdentityDto> BuildAccountIdentities()
    {
        if (Accounts.Count == 0)
        {
            return _accountIdentities.ToArray();
        }

        var activeAccountIds = Accounts.Select(account => account.Id)
            .ToHashSet(StringComparer.Ordinal);
        return _accountIdentities
            .Where(identity => activeAccountIds.Contains(identity.AccountId))
            .ToArray();
    }

    private void RebuildAccountSoundSettings()
    {
        _suppressAccountSoundSync = true;
        try
        {
            var activeIds = Accounts.Select(account => account.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in _accountNotificationSounds.Keys
                         .Where(accountId => !activeIds.Contains(accountId))
                         .ToArray())
            {
                _accountNotificationSounds.Remove(stale);
            }

            AccountSoundSettings.Clear();
            foreach (var account in Accounts)
            {
                var sound = _accountNotificationSounds.GetValueOrDefault(account.Id) ?? string.Empty;
                var display = string.IsNullOrWhiteSpace(account.DisplayName) ? account.Email : account.DisplayName;
                AccountSoundSettings.Add(new AccountSoundViewModel(
                    account.Id,
                    display,
                    sound,
                    OnAccountSoundChanged));
            }
        }
        finally
        {
            _suppressAccountSoundSync = false;
        }

        OnPropertyChanged(nameof(HasAccountSoundSettings));
    }

    private void OnAccountSoundChanged(AccountSoundViewModel setting)
    {
        if (_suppressAccountSoundSync)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(setting.Sound))
        {
            _accountNotificationSounds.Remove(setting.AccountId);
        }
        else
        {
            _accountNotificationSounds[setting.AccountId] = setting.Sound;
        }

        QueueSettingsSave();
    }

    [RelayCommand]
    private void SaveAppLock()
    {
        var enabling = AppLockEnabled;

        if (!enabling)
        {
            if (_appLockHash.Length > 0
                && !AppLockService.Verify(AppLockCurrentPin, _appLockSalt, _appLockHash))
            {
                AppLockStatus = "关闭应用锁需要输入当前 PIN。";
                return;
            }

            _appLockHash = string.Empty;
            _appLockSalt = string.Empty;
            _savedAppLockEnabled = false;
            IsAppLocked = false;
            AppLockCurrentPin = string.Empty;
            AppLockPin = string.Empty;
            AppLockPinConfirm = string.Empty;
            AppLockStatus = "应用锁已关闭。";
            OnPropertyChanged(nameof(HasAppLockPin));
            QueueSettingsSave();
            return;
        }

        var updatingPin = AppLockPin.Length > 0;
        if (_appLockHash.Length > 0 && updatingPin
            && !AppLockService.Verify(AppLockCurrentPin, _appLockSalt, _appLockHash))
        {
            AppLockStatus = "修改 PIN 需要先输入当前 PIN。";
            return;
        }

        if (updatingPin || _appLockHash.Length == 0)
        {
            if (AppLockPin.Trim().Length < 4)
            {
                AppLockStatus = "PIN 至少需要 4 位，且不能只由空格组成。";
                return;
            }

            if (!string.Equals(AppLockPin, AppLockPinConfirm, StringComparison.Ordinal))
            {
                AppLockStatus = "两次输入的新 PIN 不一致。";
                return;
            }

            _appLockSalt = AppLockService.CreateSalt();
            _appLockHash = AppLockService.Hash(AppLockPin, _appLockSalt);
            AppLockPin = string.Empty;
            AppLockPinConfirm = string.Empty;
            AppLockCurrentPin = string.Empty;
            OnPropertyChanged(nameof(HasAppLockPin));
        }
        else if (_appLockHash.Length == 0)
        {
            AppLockStatus = "请先设置 PIN。";
            return;
        }

        _savedAppLockEnabled = true;
        _savedAppLockTimeoutMinutes = AppLockTimeoutValues[
            Math.Clamp(AppLockTimeoutIndex, 0, AppLockTimeoutValues.Length - 1)];
        AppLockCurrentPin = string.Empty;
        AppLockStatus = "应用锁设置已保存。";
        StartLockTimer();
        QueueSettingsSave();
    }

    [RelayCommand]
    private void LockNow()
    {
        if (!_savedAppLockEnabled || _appLockHash.Length == 0)
        {
            AppLockStatus = "请先在设置中启用并保存应用锁。";
            return;
        }

        IsAppLocked = true;
        LockPin = string.Empty;
        UnlockError = null;
    }

    [RelayCommand]
    private void Unlock()
    {
        if (AppLockService.Verify(LockPin, _appLockSalt, _appLockHash))
        {
            IsAppLocked = false;
            LockPin = string.Empty;
            UnlockError = null;
            _lastActivity = DateTimeOffset.Now;
            return;
        }

        UnlockError = "PIN 不正确。";
    }

    public void NoteUserActivity()
    {
        _lastActivity = DateTimeOffset.Now;
    }

    private void StartLockTimer()
    {
        if (_lockTimer is not null)
        {
            return;
        }

        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _lockTimer.Tick += (_, _) =>
        {
            var timeoutMinutes = _savedAppLockTimeoutMinutes;
            if (!_savedAppLockEnabled || IsAppLocked || timeoutMinutes <= 0 || _appLockHash.Length == 0)
            {
                return;
            }

            if (DateTimeOffset.Now - _lastActivity >= TimeSpan.FromMinutes(timeoutMinutes))
            {
                LockNow();
            }
        };
        _lockTimer.Start();
    }

    [RelayCommand]
    private void OpenAddAccount()
    {
        ResetAccountForm();
        ShowAddAccountForm = true;
        SettingsSectionIndex = 4;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void BeginAddAccount()
    {
        ResetAccountForm();
        ShowAddAccountForm = true;
    }

    public async Task ReauthorizeAccountAsync(AccountViewModel account)
    {
        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.StartOAuth,
                new StartOAuthRequest
                {
                    Provider = account.Provider,
                    LoginHint = account.Email
                },
                TimeSpan.FromSeconds(15));

            StatusMessage = $"已在浏览器中打开 {account.Email} 的重新授权页面，完成后账户会自动更新。";
            HasError = false;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    public async Task SetDefaultAccountAsync(AccountViewModel account)
    {
        try
        {
            var response = await _engine.RequestAsync<AccountPreferencesResponse>(
                IpcMessageTypes.UpdateAccountPreferences,
                new UpdateAccountPreferencesRequest
                {
                    Preferences = new AccountPreferencesDto
                    {
                        DefaultAccountId = account.Id,
                        AccountOrder = string.Join(',', Accounts.Select(item => item.Id))
                    }
                });

            foreach (var item in Accounts)
            {
                item.IsDefault = string.Equals(
                    response.Preferences.DefaultAccountId,
                    item.Id,
                    StringComparison.Ordinal);
            }

            StatusMessage = $"已将 {account.Email} 设为默认发信账户。";
            HasError = false;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    public async Task MoveAccountAsync(AccountViewModel account, int offset)
    {
        var index = Accounts.IndexOf(account);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Accounts.Count)
        {
            return;
        }

        Accounts.Move(index, target);
        try
        {
            await _engine.RequestAsync<AccountPreferencesResponse>(
                IpcMessageTypes.UpdateAccountPreferences,
                new UpdateAccountPreferencesRequest
                {
                    Preferences = new AccountPreferencesDto
                    {
                        DefaultAccountId = Accounts.FirstOrDefault(item => item.IsDefault)?.Id,
                        AccountOrder = string.Join(',', Accounts.Select(item => item.Id))
                    }
                });
            StatusMessage = "账户顺序已保存。";
            HasError = false;
        }
        catch (Exception exception)
        {
            ShowError(exception);
            await LoadAccountsAsync();
        }
    }

    public void BeginEditAccount(AccountViewModel account)
    {
        EditingAccountId = account.Id;
        AddAccountEmail = account.Email;
        AddAccountDisplayName = account.DisplayName;
        AddAccountSecret = string.Empty;
        SelectedProviderIndex = account.Provider switch
        {
            MailProvider.Outlook => 0,
            MailProvider.Custom => 2,
            _ => 1
        };

        if (account.Server is { } server)
        {
            CustomImapHost = server.ImapHost;
            CustomImapPortText = server.ImapPort.ToString();
            CustomImapStartTls = server.ImapStartTls;
            CustomSmtpHost = server.SmtpHost;
            CustomSmtpPortText = server.SmtpPort.ToString();
            CustomSmtpStartTls = server.SmtpStartTls;
        }
        else
        {
            CustomImapHost = string.Empty;
            CustomImapPortText = "993";
            CustomImapStartTls = false;
            CustomSmtpHost = string.Empty;
            CustomSmtpPortText = "465";
            CustomSmtpStartTls = false;
        }

        AddAccountStatus = null;
        ShowAddAccountForm = true;
        SettingsSectionIndex = 4;
        IsSettingsOpen = true;
    }

    public void RequestDeleteAccount(AccountViewModel account)
    {
        foreach (var item in Accounts)
        {
            item.IsDeleteConfirmVisible = item == account;
            item.DeleteError = null;
        }
    }

    public void CancelDeleteAccount(AccountViewModel account)
    {
        account.IsDeleteConfirmVisible = false;
        account.DeleteError = null;
    }

    public async Task DeleteAccountAsync(AccountViewModel account)
    {
        if (account.IsDeleting)
        {
            return;
        }

        var activeFolderDeleted = SelectedFolder is not null
                                  && account.Folders.Contains(SelectedFolder);

        account.IsDeleting = true;
        account.DeleteError = null;

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.RemoveAccount,
                new RemoveAccountRequest { AccountId = account.Id },
                TimeSpan.FromSeconds(30));

            account.IsDeleteConfirmVisible = false;
            _accountIdentities.RemoveAll(identity =>
                string.Equals(identity.AccountId, account.Id, StringComparison.Ordinal));

            if (activeFolderDeleted)
            {
                SelectedFolder = null;
            }

            await LoadAccountsAsync();
            QueueSettingsSave();

            if (Accounts.Count == 0)
            {
                ResetAfterAccountDeletion();
                return;
            }

            if (SelectedFolder is not null)
            {
                await LoadMessagesAsync(SelectedFolder);
            }
            else
            {
                var unified = SelectedUnifiedFolder ?? UnifiedFolders.FirstOrDefault();
                if (SelectedUnifiedFolder is null)
                {
                    SelectedUnifiedFolder = unified;
                }
                else if (unified is not null)
                {
                    await LoadRoleMessagesAsync(unified.Role, unified.Name);
                }
            }

            StatusMessage = $"账户 {account.Email} 已删除。";
        }
        catch (Exception exception)
        {
            account.DeleteError = GetErrorMessage(exception);
            account.IsDeleteConfirmVisible = true;
        }
        finally
        {
            account.IsDeleting = false;
        }
    }

    private void ResetAfterAccountDeletion()
    {
        SelectedAccount = null;
        SelectedFolder = null;
        SelectedUnifiedFolder = null;
        Folders.Clear();
        _foldersById.Clear();
        _folderRoles.Clear();
        RecalculateUnifiedCounts();
        _activeScope = null;
        _activeRole = null;
        _activeFolderId = null;
        _allMessages.Clear();
        Messages.Clear();
        SelectedMessage = null;
        _currentBody = null;
        Reader.Reset();
        FolderTitle = string.Empty;
        IsEmpty = false;
        StatusMessage = "账户已删除。";
    }

    private void ResetAccountForm()
    {
        EditingAccountId = null;
        AddAccountEmail = string.Empty;
        AddAccountDisplayName = string.Empty;
        AddAccountSecret = string.Empty;
        CustomImapHost = string.Empty;
        CustomImapPortText = "993";
        CustomImapStartTls = false;
        CustomSmtpHost = string.Empty;
        CustomSmtpPortText = "465";
        CustomSmtpStartTls = false;
        AddAccountStatus = null;
    }

    [RelayCommand]
    private void ShowAccountList()
    {
        ShowAddAccountForm = false;
        EditingAccountId = null;
        AddAccountStatus = null;
    }

    [RelayCommand]
    private async Task SaveAccountAsync()
    {
        if (IsAddingAccount)
        {
            return;
        }

        if (IsEditingAccount)
        {
            await UpdateAccountAsync();
            return;
        }

        await AddPasswordAccountAsync();
    }

    private async Task UpdateAccountAsync()
    {
        var isCustom = SelectedProviderIndex == 2;
        if (isCustom
            && (string.IsNullOrWhiteSpace(CustomImapHost)
                || string.IsNullOrWhiteSpace(CustomSmtpHost)
                || !int.TryParse(CustomImapPortText, out var imapPort)
                || !int.TryParse(CustomSmtpPortText, out var smtpPort)
                || imapPort is < 1 or > 65535
                || smtpPort is < 1 or > 65535))
        {
            AddAccountStatus = "请填写有效的服务器地址和端口（1-65535）。";
            return;
        }

        IsAddingAccount = true;
        AddAccountStatus = "正在保存账户设置…";

        try
        {
            var response = await _engine.RequestAsync<AccountResponse>(
                IpcMessageTypes.UpdateAccount,
                new UpdateAccountRequest
                {
                    AccountId = EditingAccountId ?? string.Empty,
                    DisplayName = string.IsNullOrWhiteSpace(AddAccountDisplayName)
                        ? null
                        : AddAccountDisplayName.Trim(),
                    Secret = string.IsNullOrWhiteSpace(AddAccountSecret) ? null : AddAccountSecret.Trim(),
                    ImapHost = isCustom ? CustomImapHost.Trim() : null,
                    ImapPort = isCustom ? int.Parse(CustomImapPortText) : 0,
                    ImapStartTls = isCustom && CustomImapStartTls,
                    SmtpHost = isCustom ? CustomSmtpHost.Trim() : null,
                    SmtpPort = isCustom ? int.Parse(CustomSmtpPortText) : 0,
                    SmtpStartTls = isCustom && CustomSmtpStartTls
                },
                TimeSpan.FromSeconds(60));

            AddAccountStatus = $"账户 {response.Account.Email} 已更新。";
            await LoadAccountsAsync();
            ShowAccountList();
        }
        catch (Exception exception)
        {
            AddAccountStatus = GetErrorMessage(exception);
        }
        finally
        {
            IsAddingAccount = false;
        }
    }

}
