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
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    partial void OnSelectedAccountChanged(AccountViewModel? value)
    {
        if (_suppressSelectionHandlers || value is null)
        {
            return;
        }

        // 账户文件夹已在启动时全部加载；选中账户只负责展开该节点。
        value.IsExpanded = true;
    }

    partial void OnSelectedFolderChanged(FolderViewModel? value)
    {
        if (_suppressSelectionHandlers || value is null)
        {
            return;
        }

        // 文件夹与统一文件夹互斥。
        if (SelectedUnifiedFolder is not null)
        {
            _suppressSelectionHandlers = true;
            try
            {
                SelectedUnifiedFolder = null;
            }
            finally
            {
                _suppressSelectionHandlers = false;
            }
        }

        UpdateActiveStates();
        SetSidebarSelectionSilently(value);
        _ = LoadMessagesAsync(value);
    }

    partial void OnSelectedSidebarItemChanged(object? value)
    {
        if (_suppressSidebarSelection)
        {
            return;
        }

        switch (value)
        {
            case AccountViewModel account:
                // 账户行只负责展开，不改变邮件范围；把选中态还给当前文件夹。
                account.IsExpanded = true;
                SetSidebarSelectionSilently(SelectedFolder);
                break;

            case FolderViewModel folder:
                SelectedFolder = folder;
                break;
        }
    }

    private void SetSidebarSelectionSilently(object? item)
    {
        _suppressSidebarSelection = true;
        try
        {
            SelectedSidebarItem = item;
        }
        finally
        {
            _suppressSidebarSelection = false;
        }
    }

    partial void OnSelectedUnifiedFolderChanged(UnifiedFolderViewModel? value)
    {
        if (_suppressSelectionHandlers || value is null)
        {
            return;
        }

        if (SelectedFolder is not null)
        {
            _suppressSelectionHandlers = true;
            try
            {
                SelectedFolder = null;
            }
            finally
            {
                _suppressSelectionHandlers = false;
            }
        }

        UpdateActiveStates();
        SetSidebarSelectionSilently(null);
        _ = LoadRoleMessagesAsync(value.Role, value.Name);
    }

    private void UpdateActiveStates()
    {
        foreach (var folder in UnifiedFolders)
        {
            folder.IsActive = SelectedUnifiedFolder is not null
                              && folder.Role == SelectedUnifiedFolder.Role;
        }
    }

    partial void OnSelectedMessageChanged(MailSummaryViewModel? value)
    {
        RebuildMoveTargets();

        if (_suppressSelectionHandlers)
        {
            return;
        }

        // 列表刷新、筛选或标记已读都可能让列表暂时取消选中，此时不应清空阅读区。
        if (value is null)
        {
            return;
        }

        if (_currentBody is not null && _currentBody.MessageId == value.Id)
        {
            return;
        }

        _ = LoadBodyAsync(value);
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchResultIds = null;
        if (IsGlobalSearch)
        {
            RemoveSearchExtras();
        }

        var term = value.Trim();
        foreach (var message in _allMessages)
        {
            message.SearchQuery = term;
        }

        ApplyFilter();
        QueueSearchSelectionSync();

        if (term.Length == 0)
        {
            _searchCts?.Cancel();
            return;
        }

        _ = SearchBodyAsync(term);
    }

    partial void OnIsGlobalSearchChanged(bool value)
    {
        _searchCts?.Cancel();
        _searchResultIds = null;
        if (!value)
        {
            RemoveSearchExtras();
        }

        ApplyFilter();
        QueueSearchSelectionSync();

        var term = SearchText.Trim();
        if (term.Length > 0)
        {
            _ = SearchBodyAsync(term);
        }
    }

    /// <summary>搜索条件变化后让列表选中项与阅读区保持一致，避免筛选结果和阅读区显示不同邮件。</summary>
    private void SynchronizeSearchSelection()
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            if (SelectedMessage is { } current && Messages.Contains(current))
            {
                return;
            }

            if (Messages.Count > 0)
            {
                SelectedMessage = Messages[0];
                return;
            }

            SelectedMessage = null;
            _currentBody = null;
            Reader.Reset();
            return;
        }

        if (SelectedMessage is null && Messages.Count > 0)
        {
            SelectedMessage = Messages[0];
        }
    }

    /// <summary>列表容器会在集合重建后重置选中项，下一轮 UI 刷新再对齐一次。</summary>
    private void QueueSearchSelectionSync()
    {
        SynchronizeSearchSelection();
        Dispatcher.UIThread.Post(SynchronizeSearchSelection, DispatcherPriority.Background);
    }

    private async Task SearchBodyAsync(string term)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var folderId = _activeFolderId;
        var role = _activeRole;
        var global = IsGlobalSearch;

        try
        {
            var response = await _engine.RequestAsync<MessageSearchResponse>(
                IpcMessageTypes.SearchMessages,
                new SearchMessagesRequest
                {
                    FolderId = global ? 0 : folderId ?? 0,
                    Role = global ? null : role,
                    Query = term,
                    AllFolders = global
                },
                TimeSpan.FromSeconds(30),
                cts.Token);

            if (cts.IsCancellationRequested
                || !string.Equals(SearchText.Trim(), term, StringComparison.Ordinal)
                || global != IsGlobalSearch
                || (!global && (folderId != _activeFolderId || role != _activeRole)))
            {
                return;
            }

            if (global)
            {
                AddSearchSummaries(response.Items);
            }

            _searchResultIds = new HashSet<long>(response.MessageIds);
            ApplyFilter();
            QueueSearchSelectionSync();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // 正文索引不可用时仍保留主题/发件人/预览的本地搜索。
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
            }

            cts.Dispose();
        }
    }

    partial void OnThemeChanged(string value)
    {
        ApplyTheme();
        _readerAppearanceDirty = true;
        QueueSettingsSave();
    }

    partial void OnDensityChanged(string value)
    {
        ThemeService.ApplyDensity(value);
        foreach (var message in _allMessages)
        {
            message.ShowPreview = ShowMessagePreview;
        }

        _readerAppearanceDirty = true;
        QueueSettingsSave();
    }

    partial void OnShowRemoteImagesChanged(bool value)
    {
        _allowRemoteImages = value;
        QueueSettingsSave();
    }

    partial void OnThemeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsThemeSystem));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
        Theme = value switch
        {
            1 => "light",
            2 => "dark",
            _ => "system"
        };
    }

    private void OnEngineStarting()
    {
        IsBusy = true;
        StatusMessage = "正在启动 MailDog 后台…";
    }

    private async Task ConnectAsync()
    {
        IsBusy = true;
        HasError = false;
        StatusMessage = "正在连接 MailDog 后台…";

        try
        {
            await _engine.ConnectAsync(CancellationToken.None);
            _syncErrors.Clear();
            IsConnected = true;
            UpdateOfflineState();
            await LoadSettingsAsync();
            await LoadAccountsAsync();
            await LoadTagsAsync();
            await LoadContactsAsync();
            StatusMessage = null;
        }
        catch (Exception exception)
        {
            IsConnected = false;
            ShowError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadSettingsAsync()
    {
        var response = await _engine.RequestAsync<SettingsResponse>(
            IpcMessageTypes.GetSettings,
            new GetSettingsRequest());

        _settings = response.Settings;

        _suppressAutoSave = true;
        try
        {
            Theme = _settings.Theme;
            Density = ThemeService.IsNormalDensity(_settings.Density) ? "normal" : "compact";
            ThemeIndex = _settings.Theme switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0
            };
            MaterialIndex = _settings.Material switch
            {
                "mica" when IsMicaAvailable => 1,
                "acrylic" when IsAcrylicAvailable => 2,
                _ => 0
            };
            BackgroundColorHex = _settings.BackgroundColor ?? string.Empty;
            BackgroundOpacity = _settings.BackgroundOpacity;
            NotificationsEnabled = _settings.NotificationsEnabled;
            NotificationSoundEnabled = _settings.NotificationSoundEnabled;
            NotificationSound = _settings.NotificationSound is "mail" or "reminder" or "asterisk"
                or "warning" or "beep" or "question" or "start"
                ? _settings.NotificationSound
                : "default";
            NotificationShowSubject = _settings.NotificationShowSubject;
            NotificationShowPreview = _settings.NotificationShowPreview;
            NotificationMerge = _settings.NotificationMerge;
            NotificationInboxOnly = _settings.NotificationInboxOnly;
            NotificationQuietEnabled = _settings.NotificationQuietEnabled;
            NotificationQuietStart = _settings.NotificationQuietStart;
            NotificationQuietEnd = _settings.NotificationQuietEnd;
            var intervalIndex = Array.IndexOf(SyncIntervalValues, _settings.SyncIntervalMinutes);
            SyncIntervalIndex = intervalIndex >= 0 ? intervalIndex : 0;
            MarkReadOnOpen = _settings.MarkReadOnOpen;
            RunAtStartup = _settings.RunAtStartup;
            ShowRemoteImages = _settings.ShowRemoteImages;
            SaveSentCopy = _settings.SaveSentCopy;
            SignatureText = _settings.Signature ?? string.Empty;
            _accountSignatures.Clear();
            foreach (var item in _settings.AccountSignatures)
            {
                if (!string.IsNullOrWhiteSpace(item.AccountId))
                {
                    _accountSignatures[item.AccountId] = item.Signature;
                }
            }

            _accountIdentities.Clear();
            foreach (var identity in _settings.AccountIdentities)
            {
                if (!string.IsNullOrWhiteSpace(identity.AccountId)
                    && !string.IsNullOrWhiteSpace(identity.Address))
                {
                    _accountIdentities.Add(identity);
                }
            }

            _accountNotificationSounds.Clear();
            foreach (var item in _settings.AccountNotificationSounds)
            {
                if (!string.IsNullOrWhiteSpace(item.AccountId)
                    && !string.IsNullOrWhiteSpace(item.Sound))
                {
                    _accountNotificationSounds[item.AccountId] = item.Sound;
                }
            }

            SelectedSignatureAccount ??= Accounts.FirstOrDefault();
            ReloadTemplateList(_settings.Templates);
            SendDelayIndex = _settings.SendDelaySeconds switch
            {
                0 => 0,
                5 => 1,
                10 => 2,
                _ => 3
            };
            RenderModeIndex = _settings.RenderMode == "plain" || !HtmlRendering.IsAvailable ? 1 : 0;
            _appLockHash = _settings.AppLockHash;
            _appLockSalt = _settings.AppLockSalt;
            _savedAppLockEnabled = _settings.AppLockEnabled;
            var timeoutIndex = Array.IndexOf(AppLockTimeoutValues, _settings.AppLockTimeoutMinutes);
            _savedAppLockTimeoutMinutes = timeoutIndex >= 0 ? _settings.AppLockTimeoutMinutes : 15;
            AppLockEnabled = _settings.AppLockEnabled;
            AppLockTimeoutIndex = timeoutIndex >= 0 ? timeoutIndex : 2;
            OnPropertyChanged(nameof(HasAppLockPin));
            if (_savedAppLockEnabled && _appLockHash.Length > 0)
            {
                IsAppLocked = true;
            }

            StartLockTimer();
            _allowRemoteImages = _settings.ShowRemoteImages;
            ApplyTheme();
            SettingsSaveHint = "所有更改会自动保存";
        }
        finally
        {
            _suppressAutoSave = false;
        }

        await LoadRulesAsync();
    }

    private async Task LoadTagsAsync()
    {
        try
        {
            var response = await _engine.RequestAsync<TagsResponse>(
                IpcMessageTypes.GetTags,
                new GetTagsRequest());
            LoadTags(response.Items);
        }
        catch (Exception exception)
        {
            // 标签是辅助数据，加载失败时仍允许正常收发邮件。
            StatusMessage = $"标签加载失败：{GetErrorMessage(exception)}";
        }
    }

    private async Task LoadContactsAsync()
    {
        try
        {
            var response = await _engine.RequestAsync<ContactsResponse>(
                IpcMessageTypes.ListContacts,
                new ListContactsRequest { Limit = 500 });
            _contacts = response.Items;
        }
        catch
        {
            // 联系人补全属于辅助功能，不影响写信与发送。
            _contacts = [];
        }
    }

    private ComposeAccount[] BuildComposeAccounts()
    {
        var ordered = Accounts
            .OrderBy(account => account.IsDefault ? 0 : 1)
            .Select(account => new ComposeAccount(account.Id, account.Email, account.DisplayName))
            .ToArray();
        return ordered;
    }

    /// <summary>
    /// 重新读取账户并重建 UI 列表。
    /// 登录成功时引擎会连续广播 AccountChanged 与 AuthState.Connected，二者都可能触发刷新；
    /// 这里用单飞门闩串行化，避免一个刷新在 RefreshFolderTreeAsync 中枚举 Accounts 时被另一次 Clear 打断。
    /// 版本号让排队中的旧请求直接退出，减少无意义的重复重建。
    /// </summary>
    private async Task LoadAccountsAsync()
    {
        var version = Interlocked.Increment(ref _accountsLoadVersion);
        await _accountsLoadGate.WaitAsync().ConfigureAwait(true);

        try
        {
            if (version != Volatile.Read(ref _accountsLoadVersion))
            {
                return;
            }

            var response = await _engine.RequestAsync<AccountsResponse>(
                IpcMessageTypes.GetAccounts,
                new GetAccountsRequest());
            var preferencesResponse = await _engine.RequestAsync<AccountPreferencesResponse>(
                IpcMessageTypes.GetAccountPreferences,
                new GetAccountPreferencesRequest());
            var preferences = preferencesResponse.Preferences;

            var selectedId = SelectedAccount?.Id;

            _suppressSelectionHandlers = true;
            try
            {
                // 先完成两次 IPC 再动集合，UI 线程不会看到只有半截账户的中间状态。
                Accounts.Clear();
                var orderIds = preferences.AccountOrder.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var orderedItems = response.Items
                    .OrderBy(account =>
                    {
                        var index = Array.IndexOf(orderIds, account.Id);
                        return index < 0 ? int.MaxValue : index;
                    })
                    .ToArray();

                foreach (var account in orderedItems)
                {
                    Accounts.Add(new AccountViewModel(account)
                    {
                        IsDefault = string.Equals(preferences.DefaultAccountId, account.Id, StringComparison.Ordinal)
                    });
                }

                ShowWelcome = Accounts.Count == 0;
                SelectedAccount = selectedId is null
                    ? Accounts.FirstOrDefault()
                    : Accounts.FirstOrDefault(account => account.Id == selectedId) ?? Accounts.FirstOrDefault();
                if (SelectedSignatureAccount is null
                    || !Accounts.Any(account => string.Equals(account.Id, SelectedSignatureAccount.Id, StringComparison.Ordinal)))
                {
                    SelectedSignatureAccount = Accounts.FirstOrDefault();
                }
            }
            finally
            {
                _suppressSelectionHandlers = false;
            }

            if (SelectedAccount is not null)
            {
                await RefreshFolderTreeAsync();
                ExpandSelectedAccount();

                // 首次进入默认打开统一收件箱；保留用户当前的文件夹选择。
                if (SelectedFolder is null && SelectedUnifiedFolder is null)
                {
                    SelectedUnifiedFolder = UnifiedFolders.FirstOrDefault();
                }
            }

            RebuildAccountSoundSettings();
            RebuildIdentitySettings();
        }
        finally
        {
            _accountsLoadGate.Release();
        }
    }

    /// <summary>加载所有账户的文件夹，构建左侧两级树与统一文件夹计数。</summary>
    private async Task RefreshFolderTreeAsync()
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            IsBusy = true;
            HasError = false;

            Folders.Clear();
            _foldersById.Clear();
            _folderRoles.Clear();

            // 账户列表可能在等待文件夹 IPC 时被登录事件重载；先取快照，避免 foreach 枚举被修改。
            foreach (var account in Accounts.ToArray())
            {
                var response = await _engine.RequestAsync<FoldersResponse>(
                    IpcMessageTypes.GetFolders,
                    new GetFoldersRequest { AccountId = account.Id },
                    TimeSpan.FromSeconds(45));

                ApplyFolders(account, response.Items);

                foreach (var folder in account.Folders)
                {
                    Folders.Add(folder);
                    _foldersById[folder.Id] = folder;
                    _folderRoles[folder.Id] = folder.Role;
                }
            }

            RecalculateUnifiedCounts();
            UpdateActiveStates();
            RebuildMoveTargets();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>增量合并文件夹，保留已有实例以维持选中项与展开状态。</summary>
    private static void ApplyFolders(AccountViewModel account, FolderDto[] items)
    {
        var existing = account.Folders.ToDictionary(folder => folder.Id);
        var incoming = items.Select(folder => folder.Id).ToHashSet();

        for (var index = account.Folders.Count - 1; index >= 0; index--)
        {
            if (!incoming.Contains(account.Folders[index].Id))
            {
                account.Folders.RemoveAt(index);
            }
        }

        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];

            if (existing.TryGetValue(item.Id, out var folder))
            {
                folder.UnreadCount = item.UnreadCount;
                var current = account.Folders.IndexOf(folder);
                var target = Math.Min(index, account.Folders.Count - 1);

                if (current != target)
                {
                    account.Folders.Move(current, target);
                }

                continue;
            }

            account.Folders.Insert(
                Math.Min(index, account.Folders.Count),
                new FolderViewModel(item));
        }

        account.NotifyFoldersChanged();
    }

    private void RecalculateUnifiedCounts()
    {
        foreach (var unified in UnifiedFolders)
        {
            unified.UnreadCount = Folders
                .Where(folder => folder.Role == unified.Role)
                .Sum(folder => folder.UnreadCount);
        }
    }

    private void ExpandSelectedAccount()
    {
        foreach (var account in Accounts)
        {
            account.IsExpanded = account == SelectedAccount;
        }
    }

    private Task LoadMessagesAsync(FolderViewModel folder)
    {
        if (!IsConnected)
        {
            return Task.CompletedTask;
        }

        var scope = $"folder:{folder.Id}";
        var sameScope = _activeScope == scope;
        var previousId = sameScope ? SelectedMessage?.Id : null;

        _activeScope = scope;
        _activeRole = null;
        _activeFolderId = folder.Id;

        return LoadScopeAsync(scope, folder.Name, sameScope, previousId);
    }

    /// <summary>加载统一文件夹：跨账户按角色查询。</summary>
    private Task LoadRoleMessagesAsync(FolderRole role, string title)
    {
        if (!IsConnected)
        {
            return Task.CompletedTask;
        }

        var scope = $"role:{role}";
        var sameScope = _activeScope == scope;
        var previousId = sameScope ? SelectedMessage?.Id : null;

        _activeScope = scope;
        _activeRole = role;
        _activeFolderId = null;

        return LoadScopeAsync(scope, title, sameScope, previousId);
    }

    private GetMessagesRequest BuildMessagesRequest(
        int limit,
        DateTimeOffset? beforeDate = null,
        long? beforeId = null)
        => new()
        {
            FolderId = _activeFolderId ?? 0,
            Role = _activeRole,
            BeforeDate = beforeDate,
            BeforeId = beforeId,
            Limit = limit
        };

    private async Task LoadScopeAsync(string scope, string title, bool sameScope, long? previousId)
    {
        try
        {
            IsBusy = true;
            HasError = false;
            FolderTitle = title;
            _searchResultIds = null;
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                _ = SearchBodyAsync(SearchText.Trim());
            }

            var response = await _engine.RequestAsync<MessagesPageResponse>(
                IpcMessageTypes.GetMessages,
                BuildMessagesRequest(50));

            // 加载期间切换了文件夹，丢弃过期结果。
            if (_activeScope != scope)
            {
                return;
            }

            // 数据到手后再替换列表，避免加载过程中列表出现空白闪烁。
            _suppressSelectionHandlers = true;
            try
            {
                SelectedMessage = null;
                Messages.Clear();

                if (!sameScope)
                {
                    foreach (var message in _allMessages)
                    {
                        message.IsSelected = false;
                    }
                }

                _allMessages.Clear();

                if (!sameScope)
                {
                    _currentBody = null;
                    Reader.Reset();
                }
            }
            finally
            {
                _suppressSelectionHandlers = false;
            }

            NotifySelectionState();

            AddMessages(response.Items);
            _ = RefreshTagsForMessagesAsync(response.Items.Select(item => item.Id).ToArray());
            HasMore = response.HasMore;
            _nextDate = response.NextDate;
            _nextId = response.NextId;
            ApplyFilter(previousId);

            if (Messages.Count > 0)
            {
                if (SelectedMessage is null)
                {
                    SelectedMessage = Messages[0];
                }
                else if (_currentBody?.MessageId != SelectedMessage.Id)
                {
                    _ = LoadBodyAsync(SelectedMessage);
                }
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>增量刷新当前范围：只合并最新一页，不清空列表，保留滚动位置与选中项。</summary>
    private async Task RefreshMessagesAsync()
    {
        if (!IsConnected || _activeScope is null)
        {
            return;
        }

        var scope = _activeScope;

        try
        {
            var response = await _engine.RequestAsync<MessagesPageResponse>(
                IpcMessageTypes.GetMessages,
                BuildMessagesRequest(50));

            if (_activeScope != scope)
            {
                return;
            }

            AddMessages(response.Items);
            _ = RefreshTagsForMessagesAsync(response.Items.Select(item => item.Id).ToArray());
            ApplyFilter();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    /// <summary>从会话菜单查看较早的一封邮件，不改变列表中的会话选中项。</summary>
    public async Task ShowThreadMessageAsync(MailSummaryViewModel message)
    {
        if (_allMessages.Contains(message))
        {
            await LoadBodyAsync(message);
        }
    }

    private async Task LoadBodyAsync(MailSummaryViewModel message)
    {
        Reader.Reset();
        Reader.Subject = string.IsNullOrWhiteSpace(message.Subject) ? "（无主题）" : message.Subject;
        Reader.SenderLine = string.IsNullOrWhiteSpace(message.SecondarySenderAddress)
            ? message.Sender
            : $"{message.Sender} <{message.SenderAddress}>";
        Reader.SenderName = message.Sender;
        Reader.SenderAddress = message.SecondarySenderAddress;
        Reader.AvatarInitial = message.AvatarInitial;
        Reader.AvatarBrush = message.AvatarBrush;
        Reader.DateText = message.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        Reader.IsLoading = true;
        _currentBody = null;

        try
        {
            var response = await _engine.RequestAsync<MessageBodyResponse>(
                IpcMessageTypes.GetMessageBody,
                new GetMessageBodyRequest { MessageId = message.Id },
                TimeSpan.FromSeconds(IpcProtocol.BodyRequestTimeoutSeconds));

            _currentBody = response.Body;
            RenderCurrentBody();

            if (OperatingSystem.IsWindows())
            {
                _ = RecordRecentAsync(message);
            }

            if (_settings.MarkReadOnOpen && !message.IsRead)
            {
                await MarkReadAsync(message, true);
            }
        }
        catch (Exception exception)
        {
            Reader.Error = GetErrorMessage(exception);
            Reader.PlainText = "无法加载邮件正文。";
            Reader.UseHtml = false;
        }
        finally
        {
            Reader.IsLoading = false;
        }
    }

    private async Task RecordRecentAsync(MailSummaryViewModel message)
    {
        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.AddRecent,
                new AddRecentRequest
                {
                    MessageId = message.Id,
                    AccountId = message.AccountId,
                    FolderId = message.FolderId,
                    Subject = message.Subject,
                    From = message.SenderAddress
                }).ConfigureAwait(false);
        }
        catch
        {
            // 最近列表失败不影响阅读。
        }
    }

    private void RenderCurrentBody(bool? preferPlain = null)
    {
        if (_currentBody is null)
        {
            return;
        }

        var (background, dark) = GetReaderAppearance();

        Reader.Apply(
            _currentBody,
            Reader.SenderLine,
            Reader.DateText,
            _sanitizer,
            _builder,
            _allowRemoteImages,
            dark,
            background,
            preferPlain ?? (RenderModeIndex == 1 || !HtmlRendering.IsAvailable));
    }

    private (string Background, bool Dark) GetReaderAppearance()
    {
        var themeDark = IsDarkTheme();
        var baseColor = ThemeService.ResolveSurfaceColor(BackgroundColorHex, themeDark);

        // 与窗口的 MailDogBrushSurfacePane 保持一致：基色上提 5%。
        var layer = AdjustColor(baseColor, 0.05);
        var dark = (0.2126 * layer.R) + (0.7152 * layer.G) + (0.0722 * layer.B) < 140;

        return ($"#{layer.R:X2}{layer.G:X2}{layer.B:X2}", dark);
    }

    private static Color AdjustColor(Color color, double delta)
    {
        static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

        var offset = delta * 255;
        return Color.FromRgb(Clamp(color.R + offset), Clamp(color.G + offset), Clamp(color.B + offset));
    }

    private async Task MarkReadAsync(MailSummaryViewModel message, bool isRead)
    {
        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.MarkRead,
                new MarkReadRequest { MessageId = message.Id, IsRead = isRead });

            message.IsRead = isRead;

            AdjustFolderUnread(message.FolderId, isRead ? -1 : 1);
            RecalculateUnifiedCounts();

            // 只有“未读”筛选依赖已读状态，其它筛选不需要重建列表。
            if (FilterIndex == 1)
            {
                ApplyFilter();
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

}
