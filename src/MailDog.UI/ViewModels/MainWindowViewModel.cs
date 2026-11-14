using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Html;
using MailDog.UI.Services;

namespace MailDog.UI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly EngineClient _engine;
    private readonly HtmlSanitizer _sanitizer = new();
    private readonly HtmlDocumentBuilder _builder = new();
    private readonly List<MailSummaryViewModel> _allMessages = [];
    private AppSettingsDto _settings = new();
    private MailBodyDto? _currentBody;
    private DateTimeOffset? _nextDate;
    private long? _nextId;
    private long? _activeFolderId;
    private bool _allowRemoteImages;
    private bool _initialized;
    private bool _suppressSelectionHandlers;
    private bool _suppressAutoSave;
    private bool _readerAppearanceDirty;
    private CancellationTokenSource? _autoSaveCts;
    private bool _disposed;

    public MainWindowViewModel(EngineClient engine)
    {
        _engine = engine;
        _engine.EventReceived += OnEngineEvent;
        _engine.Disconnected += OnEngineDisconnected;
        _engine.EngineStarting += OnEngineStarting;
        Reader.PropertyChanged += OnReaderPropertyChanged;
        Accounts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAccounts));
        RefreshBackgroundPresets();
    }

    public ObservableCollection<AccountViewModel> Accounts { get; } = [];

    public bool HasAccounts => Accounts.Count > 0;

    public ObservableCollection<FolderViewModel> Folders { get; } = [];

    public ObservableCollection<MailSummaryViewModel> Messages { get; } = [];

    public ReaderViewModel Reader { get; } = new();

    public string[] FilterOptions { get; } = ["全部", "未读", "已标记", "有附件"];

    public string[] ProviderOptions { get; } = ["Outlook", "QQ 邮箱"];

    public static int[] SyncIntervalValues { get; } = [1, 5, 15, 30, 60];

    public string AppVersion => typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string VersionText => $"版本 {AppVersion}";

    public string RuntimeVersion => Environment.Version.ToString();

    public string RuntimeText => $".NET {RuntimeVersion}";

    public ObservableCollection<BackgroundPresetViewModel> BackgroundPresets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverlayOpen))]
    [NotifyPropertyChangedFor(nameof(OverlayTitle))]
    [NotifyPropertyChangedFor(nameof(IsReaderHtmlVisible))]
    [NotifyPropertyChangedFor(nameof(IsWelcomeVisible))]
    private bool _isSettingsOpen;

    public bool IsOverlayOpen => IsSettingsOpen;

    public bool IsWelcomeVisible => ShowWelcome && !IsSettingsOpen;

    public string OverlayTitle => "设置";

    public bool IsReaderHtmlVisible => Reader.UseHtml && !IsOverlayOpen;

    [ObservableProperty]
    private AccountViewModel? _selectedAccount;

    [ObservableProperty]
    private FolderViewModel? _selectedFolder;

    [ObservableProperty]
    private MailSummaryViewModel? _selectedMessage;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private int _filterIndex;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _folderTitle = "收件箱";

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private bool _isLoadingMore;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcomeVisible))]
    private bool _showWelcome;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQqProvider))]
    [NotifyPropertyChangedFor(nameof(IsOutlookProvider))]
    private int _selectedProviderIndex;

    [ObservableProperty]
    private string _addAccountEmail = string.Empty;

    [ObservableProperty]
    private string _addAccountSecret = string.Empty;

    [ObservableProperty]
    private string? _addAccountStatus;

    [ObservableProperty]
    private bool _isAddingAccount;

    [ObservableProperty]
    private string _theme = "system";

    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Material))]
    private int _materialIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundColorValue))]
    private string _backgroundColorHex = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpacityText))]
    private int _backgroundOpacity = 85;

    [ObservableProperty]
    private int _settingsSectionIndex;

    [ObservableProperty]
    private string _settingsSaveHint = "所有更改会自动保存";

    public bool IsAppearanceSection => SettingsSectionIndex == 0;

    public bool IsNotificationsSection => SettingsSectionIndex == 1;

    public bool IsReadingSection => SettingsSectionIndex == 2;

    public bool IsSyncSection => SettingsSectionIndex == 3;

    public bool IsAddAccountSection => SettingsSectionIndex == 4;

    public bool IsAboutSection => SettingsSectionIndex == 5;

    partial void OnSettingsSectionIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsAppearanceSection));
        OnPropertyChanged(nameof(IsNotificationsSection));
        OnPropertyChanged(nameof(IsReadingSection));
        OnPropertyChanged(nameof(IsSyncSection));
        OnPropertyChanged(nameof(IsAddAccountSection));
        OnPropertyChanged(nameof(IsAboutSection));
    }

    public Color BackgroundColorValue
    {
        get => Color.TryParse(BackgroundColorHex, out var color)
            ? color
            : Theme == "light" || (Theme != "dark" && Application.Current?.ActualThemeVariant != ThemeVariant.Dark)
                ? Color.Parse("#F3F3F3")
                : Color.Parse("#202020");
        set => BackgroundColorHex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
    }

    [ObservableProperty]
    private bool _notificationsEnabled = true;

    [ObservableProperty]
    private bool _notificationSoundEnabled = true;

    [ObservableProperty]
    private string _notificationSound = "default";

    [ObservableProperty]
    private int _syncIntervalIndex;

    [ObservableProperty]
    private bool _markReadOnOpen = true;

    [ObservableProperty]
    private bool _showRemoteImages;

    [ObservableProperty]
    private int _renderModeIndex;

    public string Material => MaterialIndex switch
    {
        1 => "mica",
        2 => "acrylic",
        _ => "none"
    };

    public string OpacityText => $"{BackgroundOpacity}%";

    public double BackgroundOpacityValue
    {
        get => BackgroundOpacity;
        set => BackgroundOpacity = (int)Math.Round(value);
    }

    partial void OnBackgroundOpacityChanged(int value)
    {
        OnPropertyChanged(nameof(BackgroundOpacityValue));
        _readerAppearanceDirty = true;
        QueueSettingsSave();
    }

    partial void OnMaterialIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsMaterialNone));
        OnPropertyChanged(nameof(IsMaterialMica));
        OnPropertyChanged(nameof(IsMaterialAcrylic));
        _readerAppearanceDirty = true;
        QueueSettingsSave();
    }

    partial void OnBackgroundColorHexChanged(string value)
    {
        _readerAppearanceDirty = true;
        QueueSettingsSave();
    }

    partial void OnNotificationsEnabledChanged(bool value)
    {
        QueueSettingsSave();
    }

    partial void OnNotificationSoundEnabledChanged(bool value)
    {
        QueueSettingsSave();
    }

    partial void OnNotificationSoundChanged(string value)
    {
        OnPropertyChanged(nameof(IsSoundDefault));
        OnPropertyChanged(nameof(IsSoundMail));
        OnPropertyChanged(nameof(IsSoundReminder));
        OnPropertyChanged(nameof(IsSoundAsterisk));
        QueueSettingsSave();
    }

    partial void OnSyncIntervalIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSync1Minute));
        OnPropertyChanged(nameof(IsSync5Minutes));
        OnPropertyChanged(nameof(IsSync15Minutes));
        OnPropertyChanged(nameof(IsSync30Minutes));
        OnPropertyChanged(nameof(IsSync60Minutes));
        QueueSettingsSave();
    }

    partial void OnMarkReadOnOpenChanged(bool value)
    {
        QueueSettingsSave();
    }

    public bool IsHtmlRenderMode
    {
        get => RenderModeIndex == 0;
        set
        {
            if (value)
            {
                RenderModeIndex = 0;
            }
        }
    }

    public bool IsPlainRenderMode
    {
        get => RenderModeIndex == 1;
        set
        {
            if (value)
            {
                RenderModeIndex = 1;
            }
        }
    }

    partial void OnRenderModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsHtmlRenderMode));
        OnPropertyChanged(nameof(IsPlainRenderMode));
        QueueSettingsSave();
        RenderCurrentBody();
    }

    partial void OnIsBusyChanged(bool value)
    {
        IsEmpty = Messages.Count == 0 && !value && SelectedFolder is not null;
    }

    public bool IsThemeSystem
    {
        get => ThemeIndex == 0;
        set
        {
            if (value)
            {
                ThemeIndex = 0;
            }
        }
    }

    public bool IsThemeLight
    {
        get => ThemeIndex == 1;
        set
        {
            if (value)
            {
                ThemeIndex = 1;
            }
        }
    }

    public bool IsThemeDark
    {
        get => ThemeIndex == 2;
        set
        {
            if (value)
            {
                ThemeIndex = 2;
            }
        }
    }

    public bool IsMaterialNone
    {
        get => MaterialIndex == 0;
        set
        {
            if (value)
            {
                MaterialIndex = 0;
            }
        }
    }

    public bool IsMaterialMica
    {
        get => MaterialIndex == 1;
        set
        {
            if (value)
            {
                MaterialIndex = 1;
            }
        }
    }

    public bool IsMaterialAcrylic
    {
        get => MaterialIndex == 2;
        set
        {
            if (value)
            {
                MaterialIndex = 2;
            }
        }
    }

    public bool IsSync1Minute
    {
        get => SyncIntervalIndex == 0;
        set
        {
            if (value)
            {
                SyncIntervalIndex = 0;
            }
        }
    }

    public bool IsSync5Minutes
    {
        get => SyncIntervalIndex == 1;
        set
        {
            if (value)
            {
                SyncIntervalIndex = 1;
            }
        }
    }

    public bool IsSync15Minutes
    {
        get => SyncIntervalIndex == 2;
        set
        {
            if (value)
            {
                SyncIntervalIndex = 2;
            }
        }
    }

    public bool IsSync30Minutes
    {
        get => SyncIntervalIndex == 3;
        set
        {
            if (value)
            {
                SyncIntervalIndex = 3;
            }
        }
    }

    public bool IsSync60Minutes
    {
        get => SyncIntervalIndex == 4;
        set
        {
            if (value)
            {
                SyncIntervalIndex = 4;
            }
        }
    }

    public bool IsSoundDefault
    {
        get => NotificationSound == "default";
        set
        {
            if (value)
            {
                NotificationSound = "default";
            }
        }
    }

    public bool IsSoundMail
    {
        get => NotificationSound == "mail";
        set
        {
            if (value)
            {
                NotificationSound = "mail";
            }
        }
    }

    public bool IsSoundReminder
    {
        get => NotificationSound == "reminder";
        set
        {
            if (value)
            {
                NotificationSound = "reminder";
            }
        }
    }

    public bool IsSoundAsterisk
    {
        get => NotificationSound == "asterisk";
        set
        {
            if (value)
            {
                NotificationSound = "asterisk";
            }
        }
    }

    public bool IsQqProvider => SelectedProviderIndex == 1;

    public bool IsOutlookProvider => SelectedProviderIndex == 0;

    [ObservableProperty]
    private bool _isBatchBusy;

    public int CheckedCount => Messages.Count(message => message.IsSelected);

    public bool HasCheckedMessages => CheckedCount > 0;

    public string CheckedText => $"已选 {CheckedCount} 封";

    public bool IsAllChecked
    {
        get => Messages.Count > 0 && Messages.All(message => message.IsSelected);
        set
        {
            foreach (var message in Messages)
            {
                message.IsSelected = value;
            }

            NotifySelectionState();
        }
    }

    private void NotifySelectionState()
    {
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(HasCheckedMessages));
        OnPropertyChanged(nameof(CheckedText));
        OnPropertyChanged(nameof(IsAllChecked));
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MailSummaryViewModel.IsSelected))
        {
            NotifySelectionState();
        }
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ConnectAsync();
    }

    [RelayCommand]
    private async Task RetryConnectionAsync()
    {
        await ConnectAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedAccount is null)
        {
            return;
        }

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.SyncAccount,
                new SyncAccountRequest { AccountId = SelectedAccount.Id });
            StatusMessage = "正在同步…";
            HasError = false;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (!HasMore || SelectedFolder is null || IsLoadingMore)
        {
            return;
        }

        IsLoadingMore = true;

        try
        {
            var folderId = SelectedFolder.Id;

            var response = await _engine.RequestAsync<MessagesPageResponse>(
                IpcMessageTypes.GetMessages,
                new GetMessagesRequest
                {
                    FolderId = folderId,
                    BeforeDate = _nextDate,
                    BeforeId = _nextId,
                    Limit = 50
                });

            if (_activeFolderId != folderId)
            {
                // 加载期间切换了文件夹，丢弃过期结果。
                return;
            }

            AddMessages(response.Items);
            HasMore = response.HasMore;
            _nextDate = response.NextDate;
            _nextId = response.NextId;
            ApplyFilter();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    [RelayCommand]
    private async Task ToggleReadAsync()
    {
        if (SelectedMessage is null)
        {
            return;
        }

        await MarkReadAsync(SelectedMessage, !SelectedMessage.IsRead);
    }

    [RelayCommand]
    private async Task ToggleFlagAsync()
    {
        if (SelectedMessage is null)
        {
            return;
        }

        var value = !SelectedMessage.IsFlagged;

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.SetFlag,
                new SetFlagRequest { MessageId = SelectedMessage.Id, Value = value });

            SelectedMessage.IsFlagged = value;
            ApplyFilter();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    [RelayCommand]
    private void ShowImages()
    {
        _allowRemoteImages = true;
        RenderCurrentBody();
    }

    [RelayCommand]
    private void SelectAllMessages()
    {
        if (Messages.Count == 0)
        {
            return;
        }

        var select = !IsAllChecked;

        foreach (var message in Messages)
        {
            message.IsSelected = select;
        }

        NotifySelectionState();
    }

    [RelayCommand]
    private void ClearCheckedMessages()
    {
        if (!HasCheckedMessages)
        {
            return;
        }

        foreach (var message in Messages)
        {
            message.IsSelected = false;
        }

        NotifySelectionState();
    }

    [RelayCommand]
    private Task MarkCheckedReadAsync() => SetCheckedReadAsync(true);

    [RelayCommand]
    private Task MarkCheckedUnreadAsync() => SetCheckedReadAsync(false);

    [RelayCommand]
    private Task FlagCheckedAsync() => SetCheckedFlagAsync(true);

    [RelayCommand]
    private Task UnflagCheckedAsync() => SetCheckedFlagAsync(false);

    private async Task SetCheckedReadAsync(bool isRead)
    {
        var targets = CheckedTargets();
        if (targets.Length == 0)
        {
            return;
        }

        IsBatchBusy = true;

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.MarkReadBatch,
                new MarkReadBatchRequest
                {
                    MessageIds = targets.Select(message => message.Id).ToArray(),
                    IsRead = isRead
                });

            var changed = 0;

            foreach (var message in targets)
            {
                if (message.IsRead != isRead)
                {
                    message.IsRead = isRead;
                    changed++;
                }

                message.IsSelected = false;
            }

            if (SelectedFolder is not null && changed > 0)
            {
                SelectedFolder.UnreadCount = Math.Max(
                    0,
                    SelectedFolder.UnreadCount + (isRead ? -changed : changed));
            }

            if (FilterIndex == 1)
            {
                ApplyFilter();
            }

            StatusMessage = $"已更新 {targets.Length} 封邮件";
            NotifySelectionState();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsBatchBusy = false;
        }
    }

    private async Task SetCheckedFlagAsync(bool value)
    {
        var targets = CheckedTargets();
        if (targets.Length == 0)
        {
            return;
        }

        IsBatchBusy = true;

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.SetFlagBatch,
                new SetFlagBatchRequest
                {
                    MessageIds = targets.Select(message => message.Id).ToArray(),
                    Value = value
                });

            foreach (var message in targets)
            {
                message.IsFlagged = value;
                message.IsSelected = false;
            }

            if (FilterIndex == 2)
            {
                ApplyFilter();
            }

            StatusMessage = $"已更新 {targets.Length} 封邮件的旗标";
            NotifySelectionState();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsBatchBusy = false;
        }
    }

    private MailSummaryViewModel[] CheckedTargets()
        => IsBatchBusy
            ? []
            : Messages.Where(message => message.IsSelected).ToArray();

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

        IsAddingAccount = true;
        AddAccountStatus = "正在验证授权码…";

        try
        {
            var response = await _engine.RequestAsync<AccountResponse>(
                IpcMessageTypes.AddPasswordAccount,
                new AddPasswordAccountRequest
                {
                    Provider = MailProvider.QQ,
                    Email = AddAccountEmail.Trim(),
                    Secret = AddAccountSecret.Trim()
                },
                TimeSpan.FromSeconds(45));

            AddAccountStatus = $"账户 {response.Account.Email} 已添加。";
            AddAccountEmail = string.Empty;
            AddAccountSecret = string.Empty;

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
            Material = Material,
            BackgroundColor = string.IsNullOrWhiteSpace(BackgroundColorHex) ? null : BackgroundColorHex.Trim(),
            BackgroundOpacity = Math.Clamp(BackgroundOpacity, 40, 100),
            NotificationsEnabled = NotificationsEnabled,
            NotificationSoundEnabled = NotificationSoundEnabled,
            NotificationSound = NotificationSound,
            SyncIntervalMinutes = SyncIntervalValues[Math.Clamp(SyncIntervalIndex, 0, SyncIntervalValues.Length - 1)],
            CacheDays = _settings.CacheDays,
            MarkReadOnOpen = MarkReadOnOpen,
            ShowRemoteImages = ShowRemoteImages,
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

    [RelayCommand]
    private void OpenAddAccount()
    {
        SettingsSectionIndex = 4;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void OpenAbout()
    {
        SettingsSectionIndex = 5;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void BackFromOverlay()
    {
        CloseOverlay();
    }

    [RelayCommand]
    private void CloseOverlay()
    {
        IsSettingsOpen = false;
        RefreshReaderAppearance();
    }

    private void RefreshReaderAppearance()
    {
        if (!_readerAppearanceDirty)
        {
            return;
        }

        _readerAppearanceDirty = false;
        RenderCurrentBody();
    }

    [RelayCommand]
    private void OpenProjectPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/refout/MailDog")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // 打开浏览器失败时不影响关于页。
        }
    }

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

        _ = LoadFoldersAsync(value);
    }

    partial void OnSelectedFolderChanged(FolderViewModel? value)
    {
        if (_suppressSelectionHandlers || value is null)
        {
            return;
        }

        _ = LoadMessagesAsync(value);
    }

    partial void OnSelectedMessageChanged(MailSummaryViewModel? value)
    {
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
        ApplyFilter();
    }

    partial void OnFilterIndexChanged(int value)
    {
        ApplyFilter();
    }

    partial void OnThemeChanged(string value)
    {
        ApplyTheme();
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
            IsConnected = true;
            await LoadSettingsAsync();
            await LoadAccountsAsync();
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
            ThemeIndex = _settings.Theme switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0
            };
            MaterialIndex = _settings.Material switch
            {
                "mica" => 1,
                "acrylic" => 2,
                _ => 0
            };
            BackgroundColorHex = _settings.BackgroundColor ?? string.Empty;
            BackgroundOpacity = _settings.BackgroundOpacity;
            NotificationsEnabled = _settings.NotificationsEnabled;
            NotificationSoundEnabled = _settings.NotificationSoundEnabled;
            NotificationSound = _settings.NotificationSound is "mail" or "reminder" or "asterisk"
                ? _settings.NotificationSound
                : "default";
            var intervalIndex = Array.IndexOf(SyncIntervalValues, _settings.SyncIntervalMinutes);
            SyncIntervalIndex = intervalIndex >= 0 ? intervalIndex : 0;
            MarkReadOnOpen = _settings.MarkReadOnOpen;
            ShowRemoteImages = _settings.ShowRemoteImages;
            RenderModeIndex = _settings.RenderMode == "plain" ? 1 : 0;
            _allowRemoteImages = _settings.ShowRemoteImages;
            ApplyTheme();
            SettingsSaveHint = "所有更改会自动保存";
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    private async Task LoadAccountsAsync()
    {
        var response = await _engine.RequestAsync<AccountsResponse>(
            IpcMessageTypes.GetAccounts,
            new GetAccountsRequest());

        var selectedId = SelectedAccount?.Id;

        _suppressSelectionHandlers = true;
        try
        {
            Accounts.Clear();
            foreach (var account in response.Items)
            {
                Accounts.Add(new AccountViewModel(account));
            }

            ShowWelcome = Accounts.Count == 0;
            SelectedAccount = selectedId is null
                ? Accounts.FirstOrDefault()
                : Accounts.FirstOrDefault(account => account.Id == selectedId) ?? Accounts.FirstOrDefault();
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }

        if (SelectedAccount is not null)
        {
            await LoadFoldersAsync(SelectedAccount);
        }
    }

    private async Task LoadFoldersAsync(AccountViewModel account, long? preserveFolderId = null)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            IsBusy = true;
            HasError = false;
            StatusMessage = "正在加载文件夹…";

            var response = await _engine.RequestAsync<FoldersResponse>(
                IpcMessageTypes.GetFolders,
                new GetFoldersRequest { AccountId = account.Id },
                TimeSpan.FromSeconds(45));

            _suppressSelectionHandlers = true;
            try
            {
                Folders.Clear();
                foreach (var folder in response.Items)
                {
                    Folders.Add(new FolderViewModel(folder));
                }

                var target = preserveFolderId is not null
                    ? Folders.FirstOrDefault(folder => folder.Id == preserveFolderId)
                    : null;

                target ??= Folders.FirstOrDefault(folder => folder.Role == FolderRole.Inbox)
                           ?? Folders.FirstOrDefault();

                SelectedFolder = target;
            }
            finally
            {
                _suppressSelectionHandlers = false;
            }

            if (SelectedFolder is not null)
            {
                await LoadMessagesAsync(SelectedFolder);
            }
            else
            {
                StatusMessage = "正在同步文件夹…";
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

    private async Task LoadMessagesAsync(FolderViewModel folder)
    {
        if (!IsConnected)
        {
            return;
        }

        var sameFolder = _activeFolderId == folder.Id;
        var previousId = sameFolder ? SelectedMessage?.Id : null;

        try
        {
            IsBusy = true;
            HasError = false;
            FolderTitle = folder.Name;
            StatusMessage = "正在加载邮件…";

            _suppressSelectionHandlers = true;
            try
            {
                SelectedMessage = null;
                Messages.Clear();

                if (!sameFolder)
                {
                    foreach (var message in _allMessages)
                    {
                        message.IsSelected = false;
                    }
                }

                _allMessages.Clear();

                if (!sameFolder)
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
            _activeFolderId = folder.Id;

            var response = await _engine.RequestAsync<MessagesPageResponse>(
                IpcMessageTypes.GetMessages,
                new GetMessagesRequest { FolderId = folder.Id, Limit = 50 });

            AddMessages(response.Items);
            HasMore = response.HasMore;
            _nextDate = response.NextDate;
            _nextId = response.NextId;
            ApplyFilter(previousId);

            if (Messages.Count == 0)
            {
                StatusMessage = "正在同步邮件…";
            }
            else
            {
                StatusMessage = null;

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

    private async Task LoadBodyAsync(MailSummaryViewModel message)
    {
        Reader.Reset();
        Reader.Subject = message.Subject;
        Reader.SenderLine = string.IsNullOrWhiteSpace(message.SenderAddress)
            ? message.Sender
            : $"{message.Sender} <{message.SenderAddress}>";
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
            preferPlain ?? RenderModeIndex == 1);
    }

    private (string Background, bool Dark) GetReaderAppearance()
    {
        var themeDark = IsDarkTheme();
        var baseColor = Color.TryParse(BackgroundColorHex, out var custom)
            ? custom
            : themeDark ? Color.Parse("#202020") : Color.Parse("#F3F3F3");

        // 与窗口的 MailDogBrushBgLayer 保持一致：基色上提 5%。
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

            if (SelectedFolder is not null)
            {
                SelectedFolder.UnreadCount = Math.Max(
                    0,
                    SelectedFolder.UnreadCount + (isRead ? -1 : 1));
            }

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

    private void AddMessages(IEnumerable<MailSummaryDto> messages)
    {
        foreach (var message in messages)
        {
            if (_allMessages.All(existing => existing.Id != message.Id))
            {
                var viewModel = new MailSummaryViewModel(message);
                viewModel.PropertyChanged += OnMessagePropertyChanged;
                _allMessages.Add(viewModel);
            }
        }

        NotifySelectionState();
    }

    private void ApplyFilter(long? preserveId = null)
    {
        var query = _allMessages.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            query = query.Where(message =>
                message.Subject.Contains(term, StringComparison.OrdinalIgnoreCase)
                || message.Sender.Contains(term, StringComparison.OrdinalIgnoreCase)
                || message.SenderAddress.Contains(term, StringComparison.OrdinalIgnoreCase)
                || message.Preview.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        query = FilterIndex switch
        {
            1 => query.Where(message => message.IsUnread),
            2 => query.Where(message => message.IsFlagged),
            3 => query.Where(message => message.HasAttachments),
            _ => query
        };

        var filtered = query.ToList();

        // 集合没有变化时保持原样，避免列表与选中项被重建导致阅读区闪烁。
        if (filtered.Count == Messages.Count && filtered.SequenceEqual(Messages))
        {
            IsEmpty = Messages.Count == 0 && !IsBusy && SelectedFolder is not null;
            NotifySelectionState();
            return;
        }

        var keepId = preserveId ?? SelectedMessage?.Id;

        _suppressSelectionHandlers = true;
        try
        {
            // 原地增删，避免清空列表导致滚动位置与选中项跳变（滚到底部自动加载时需要）。
            var target = filtered.ToHashSet();

            for (var index = Messages.Count - 1; index >= 0; index--)
            {
                if (!target.Contains(Messages[index]))
                {
                    Messages.RemoveAt(index);
                }
            }

            for (var index = 0; index < filtered.Count; index++)
            {
                if (index >= Messages.Count)
                {
                    Messages.Add(filtered[index]);
                }
                else if (!ReferenceEquals(Messages[index], filtered[index]))
                {
                    var existing = Messages.IndexOf(filtered[index]);
                    if (existing >= 0)
                    {
                        Messages.Move(existing, index);
                    }
                    else
                    {
                        Messages.Insert(index, filtered[index]);
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

        IsEmpty = Messages.Count == 0 && !IsBusy && SelectedFolder is not null;
        NotifySelectionState();
    }

    private void OnReaderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReaderViewModel.UseHtml))
        {
            OnPropertyChanged(nameof(IsReaderHtmlVisible));
        }
    }

    private void OnEngineEvent(IIpcMessage message)
    {
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            switch (message)
            {
                case NewMailEvent newMail:
                    if (SelectedFolder?.Id == newMail.FolderId)
                    {
                        await LoadMessagesAsync(SelectedFolder);
                    }

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
        });
    }

    private void HandleSyncState(SyncStateEvent syncState)
    {
        var account = Accounts.FirstOrDefault(item => item.Id == syncState.AccountId);

        if (syncState.State == SyncStateKind.Syncing)
        {
            var message = syncState.Message ?? "正在同步…";
            if (account is not null)
            {
                account.SyncStatus = message;
            }

            StatusMessage = message;
            return;
        }

        if (syncState.State == SyncStateKind.Error)
        {
            var message = syncState.Message ?? "同步失败。";
            if (account is not null)
            {
                account.SyncStatus = message;
            }

            HasError = true;
            StatusMessage = message;
            return;
        }

        if (account is not null)
        {
            account.SyncStatus = null;
        }

        StatusMessage = null;
        HasError = false;

        if (SelectedAccount is not null && SelectedAccount.Id == syncState.AccountId)
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
                await LoadAccountsAsync();
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
        if (SelectedAccount is null)
        {
            return;
        }

        var folderId = SelectedFolder?.Id;
        await LoadFoldersAsync(SelectedAccount, folderId);
    }

    private void OnEngineDisconnected(string reason)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsConnected = false;
            HasError = true;
            StatusMessage = $"与后台的连接已断开：{reason}";
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

            BackgroundPresets.Add(new BackgroundPresetViewModel(
                preset.Name,
                preset.Value,
                preview,
                value => BackgroundColorHex = value ?? string.Empty));
        }
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
        _engine.EventReceived -= OnEngineEvent;
        _engine.Disconnected -= OnEngineDisconnected;
        _engine.EngineStarting -= OnEngineStarting;
        Reader.PropertyChanged -= OnReaderPropertyChanged;
        _ = _engine.DisposeAsync();
    }
}
