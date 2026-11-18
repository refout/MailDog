using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
using MailDog.Html;
using MailDog.UI.Services;

namespace MailDog.UI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly EngineClient _engine;
    private readonly HtmlSanitizer _sanitizer = new();
    private readonly HtmlDocumentBuilder _builder = new();
    private readonly List<MailSummaryViewModel> _allMessages = [];
    private readonly Dictionary<long, FolderViewModel> _foldersById = [];
    private readonly Dictionary<long, FolderRole> _folderRoles = [];
    private readonly Dictionary<string, string> _syncErrors = [];
    private AppSettingsDto _settings = new();
    private MailBodyDto? _currentBody;
    private DateTimeOffset? _nextDate;
    private long? _nextId;
    private long? _activeFolderId;
    private FolderRole? _activeRole;
    private string? _activeScope;
    private HashSet<long>? _searchResultIds;
    private CancellationTokenSource? _searchCts;
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
        Reader.DownloadAttachment = DownloadAttachmentAsync;
        Reader.PreviewAttachment = OpenAttachmentAsync;
        Reader.LoadAttachmentBytes = LoadAttachmentBytesAsync;
        Accounts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAccounts));
        RefreshBackgroundPresets();
        BuildCalendar();

        UnifiedFolders.Add(new UnifiedFolderViewModel(FolderRole.Inbox, "收件箱", Icon.MailInbox));
        UnifiedFolders.Add(new UnifiedFolderViewModel(FolderRole.Sent, "已发送", Icon.Send));
        UnifiedFolders.Add(new UnifiedFolderViewModel(FolderRole.Drafts, "草稿", Icon.Drafts));
        UnifiedFolders.Add(new UnifiedFolderViewModel(FolderRole.Junk, "垃圾邮件", Icon.Warning));
        UnifiedFolders.Add(new UnifiedFolderViewModel(FolderRole.Trash, "已删除", Icon.Delete));
        UnifiedFolders.Add(new UnifiedFolderViewModel(FolderRole.Archive, "归档", Icon.Archive));
    }

    public ObservableCollection<AccountViewModel> Accounts { get; } = [];

    public bool HasAccounts => Accounts.Count > 0;

    /// <summary>请求窗口置前并激活（用于点击系统通知后跳转）。</summary>
    public event Action? ShowRequested;

    /// <summary>请求列表滚动到指定邮件（跳转到某个时间范围时使用）。</summary>
    public event Action<MailSummaryViewModel>? ScrollToMessageRequested;

    public ObservableCollection<FolderViewModel> Folders { get; } = [];

    /// <summary>阅读区“移动到”菜单项，仅包含当前邮件同账户的其他可写文件夹。</summary>
    public ObservableCollection<MoveTargetViewModel> MoveTargets { get; } = [];

    public bool HasMoveTargets => MoveTargets.Count > 0;

    public ObservableCollection<MailSummaryViewModel> Messages { get; } = [];

    public ReaderViewModel Reader { get; } = new();

    public string[] ProviderOptions { get; } = ["Outlook", "QQ 邮箱", "自定义服务器"];

    public static int[] SyncIntervalValues { get; } = [1, 5, 15, 30, 60];

    public string AppVersion => typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string VersionText => $"版本 {AppVersion}";

    public string RuntimeVersion => Environment.Version.ToString();

    public string RuntimeText => $".NET {RuntimeVersion}";

    public ObservableCollection<BackgroundPresetViewModel> BackgroundPresets { get; } = [];

    /// <summary>顶部统一文件夹：聚合所有账户的收件箱 / 已发送 / 草稿等。</summary>
    public ObservableCollection<UnifiedFolderViewModel> UnifiedFolders { get; } = [];

    [ObservableProperty]
    private UnifiedFolderViewModel? _selectedUnifiedFolder;

    /// <summary>左侧账户树的选中项：账户（仅展开）或文件夹。</summary>
    [ObservableProperty]
    private object? _selectedSidebarItem;

    private bool _suppressSidebarSelection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverlayOpen))]
    [NotifyPropertyChangedFor(nameof(OverlayTitle))]
    [NotifyPropertyChangedFor(nameof(IsReaderHtmlVisible))]
    [NotifyPropertyChangedFor(nameof(IsWelcomeVisible))]
    private bool _isSettingsOpen;

    public bool IsOverlayOpen => IsSettingsOpen;

    public bool IsWelcomeVisible => ShowWelcome && !IsSettingsOpen;

    public string OverlayTitle => "设置";

    public bool IsReaderHtmlVisible => Reader.UseHtml && !IsOverlayOpen && !IsComposeOpen;

    [ObservableProperty]
    private AccountViewModel? _selectedAccount;

    [ObservableProperty]
    private FolderViewModel? _selectedFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedMessageFolder))]
    [NotifyPropertyChangedFor(nameof(SelectedMessageInTrash))]
    [NotifyPropertyChangedFor(nameof(SelectedMessageInDrafts))]
    [NotifyPropertyChangedFor(nameof(DeleteActionText))]
    private MailSummaryViewModel? _selectedMessage;

    public FolderViewModel? SelectedMessageFolder => SelectedMessage is null
        ? null
        : _foldersById.GetValueOrDefault(SelectedMessage.FolderId);

    public bool SelectedMessageInTrash => SelectedMessageFolder?.Role == FolderRole.Trash;

    public bool SelectedMessageInDrafts => SelectedMessageFolder?.Role == FolderRole.Drafts;

    public string DeleteActionText => SelectedMessageInTrash ? "永久删除" : "删除";

    [ObservableProperty]
    private bool _isFolderEditorOpen;

    [ObservableProperty]
    private string _folderEditorTitle = "新建文件夹";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmitFolderEdit))]
    private string _folderEditorName = string.Empty;

    [ObservableProperty]
    private bool _isFolderDeleteConfirmOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderDeletePrompt))]
    private string _folderDeleteName = string.Empty;

    private string? _folderEditorAccountId;
    private FolderViewModel? _folderEditorTarget;
    private FolderViewModel? _folderDeleteTarget;

    public bool CanSubmitFolderEdit => !string.IsNullOrWhiteSpace(FolderEditorName);

    public string FolderDeletePrompt => $"删除文件夹“{FolderDeleteName}”？服务器上的该文件夹和邮件也会被删除。";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private int _filterIndex;

    [ObservableProperty]
    private int _sortFieldIndex;

    [ObservableProperty]
    private bool _sortDescending = true;

    [ObservableProperty]
    private int _jumpRangeIndex;

    [ObservableProperty]
    private DateTime? _customDate = DateTime.Today;

    public string CustomDateText => CustomDate is { } date ? $"{date:yyyy/M/d}" : "选择日期";

    /// <summary>日期选择面板是否展开（面板显示在跳转下拉菜单之外）。</summary>
    [ObservableProperty]
    private bool _isDatePanelOpen;

    /// <summary>日历面板当前显示的月份（每月 1 日）。</summary>
    [ObservableProperty]
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    public ObservableCollection<CalendarDayViewModel> CalendarDays { get; } = [];

    public string CalendarMonthText => $"{CalendarMonth:yyyy年M月}";

    partial void OnCalendarMonthChanged(DateTime value)
    {
        OnPropertyChanged(nameof(CalendarMonthText));
        BuildCalendar();
    }

    [RelayCommand]
    private void PreviousMonth()
    {
        CalendarMonth = CalendarMonth.AddMonths(-1);
    }

    [RelayCommand]
    private void NextMonth()
    {
        CalendarMonth = CalendarMonth.AddMonths(1);
    }

    /// <summary>生成 6 行 × 7 列的日期网格，周日开头。</summary>
    private void BuildCalendar()
    {
        CalendarDays.Clear();

        var first = new DateTime(CalendarMonth.Year, CalendarMonth.Month, 1);
        var start = first.AddDays(-(int)first.DayOfWeek);
        var selected = CustomDate?.Date;
        var today = DateTime.Today;

        // 只保留覆盖整月的周数，避免多出一整行下个月日期。
        var daysInMonth = DateTime.DaysInMonth(first.Year, first.Month);
        var usedDays = (int)first.DayOfWeek + daysInMonth;
        var cellCount = (usedDays + 6) / 7 * 7;

        for (var offset = 0; offset < cellCount; offset++)
        {
            var date = start.AddDays(offset);
            CalendarDays.Add(new CalendarDayViewModel(
                date,
                date.Month == first.Month,
                selected is not null && date == selected,
                date == today));
        }
    }


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReaderHtmlVisible))]
    private bool _isComposeOpen;

    [ObservableProperty]
    private ComposeViewModel? _compose;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfflineTitle))]
    [NotifyPropertyChangedFor(nameof(OfflineActionText))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OfflineTitle))]
    [NotifyPropertyChangedFor(nameof(OfflineActionText))]
    private bool _isOffline;

    [ObservableProperty]
    private string? _offlineMessage;

    public string OfflineTitle => IsConnected ? "离线模式" : "后台连接断开";

    public string OfflineActionText => IsConnected ? "重试同步" : "重新连接";

    partial void OnIsConnectedChanged(bool value)
        => UpdateOfflineState();

    partial void OnIsOfflineChanged(bool value)
    {
        if (!value)
        {
            OfflineMessage = null;
        }
    }

    private void UpdateOfflineState()
    {
        if (!IsConnected)
        {
            IsOffline = true;
            OfflineMessage = "无法连接 MailDog 后台，当前显示已加载的本地缓存。";
            return;
        }

        if (_syncErrors.Count == 0)
        {
            IsOffline = false;
            return;
        }

        IsOffline = true;
        OfflineMessage = _syncErrors.Count == 1
            ? $"{_syncErrors.Values.First()} 当前显示已缓存的邮件。"
            : $"{_syncErrors.Count} 个账户同步失败，当前显示已缓存的邮件。";
    }

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
    [NotifyPropertyChangedFor(nameof(IsCustomProvider))]
    [NotifyPropertyChangedFor(nameof(IsPasswordProvider))]
    [NotifyPropertyChangedFor(nameof(IsOAuthLoginVisible))]
    [NotifyPropertyChangedFor(nameof(IsAccountSaveVisible))]
    [NotifyPropertyChangedFor(nameof(AccountFormDescription))]
    private int _selectedProviderIndex;

    [ObservableProperty]
    private string _addAccountEmail = string.Empty;

    [ObservableProperty]
    private string _addAccountDisplayName = string.Empty;

    [ObservableProperty]
    private string _addAccountSecret = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingAccount))]
    [NotifyPropertyChangedFor(nameof(AccountFormTitle))]
    [NotifyPropertyChangedFor(nameof(AccountFormDescription))]
    [NotifyPropertyChangedFor(nameof(AccountSubmitText))]
    [NotifyPropertyChangedFor(nameof(IsProviderSelectorEnabled))]
    [NotifyPropertyChangedFor(nameof(IsOAuthLoginVisible))]
    [NotifyPropertyChangedFor(nameof(IsAccountSaveVisible))]
    [NotifyPropertyChangedFor(nameof(AccountSecretPlaceholder))]
    private string? _editingAccountId;

    public bool IsEditingAccount => EditingAccountId is not null;

    public string AccountFormTitle => IsEditingAccount ? "编辑账户" : "添加账户";

    public string AccountFormDescription => IsEditingAccount
        ? IsPasswordProvider
            ? "修改发件人名称、授权码或自定义服务器。邮箱地址和登录方式不可更改。"
            : "修改发件人名称。邮箱地址和登录方式不可更改。"
        : "连接 Outlook、QQ 或自定义 IMAP/SMTP 服务器。";

    public string AccountSubmitText => IsEditingAccount ? "保存修改" : "连接";

    public bool IsProviderSelectorEnabled => !IsEditingAccount;

    public bool IsOAuthLoginVisible => IsOutlookProvider && !IsEditingAccount;

    public bool IsAccountSaveVisible => IsEditingAccount || IsPasswordProvider;

    public string AccountSecretPlaceholder => IsEditingAccount
        ? "留空则保持当前授权码"
        : "邮箱授权码或密码";

    [ObservableProperty]
    private string _customImapHost = string.Empty;

    [ObservableProperty]
    private string _customImapPortText = "993";

    [ObservableProperty]
    private bool _customImapStartTls;

    [ObservableProperty]
    private string _customSmtpHost = string.Empty;

    [ObservableProperty]
    private string _customSmtpPortText = "465";

    [ObservableProperty]
    private bool _customSmtpStartTls;

    public bool IsCustomProvider => SelectedProviderIndex == 2;

    public bool IsPasswordProvider => SelectedProviderIndex is 1 or 2;

    [ObservableProperty]
    private string? _addAccountStatus;

    [ObservableProperty]
    private bool _isAddingAccount;

    [ObservableProperty]
    private string _theme = "system";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompactDensity))]
    [NotifyPropertyChangedFor(nameof(IsNormalDensity))]
    private string _density = "compact";

    public bool IsCompactDensity
    {
        get => !ThemeService.IsNormalDensity(Density);
        set
        {
            if (value)
            {
                Density = "compact";
            }
        }
    }

    public bool IsNormalDensity
    {
        get => ThemeService.IsNormalDensity(Density);
        set
        {
            if (value)
            {
                Density = "normal";
            }
        }
    }

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

    [ObservableProperty]
    private StorageSummaryDto? _storageSummary;

    [ObservableProperty]
    private bool _isStorageBusy;

    public ObservableCollection<OutboxItemViewModel> OutboxItems { get; } = [];

    public bool HasOutboxItems => OutboxItems.Count > 0;

    [ObservableProperty]
    private bool _isOutboxBusy;

    [ObservableProperty]
    private string _outboxStatus = "发件箱中的待发送和失败邮件会显示在这里。";

    [ObservableProperty]
    private bool _isBackupBusy;

    [ObservableProperty]
    private string _backupStatus = "备份包含本地数据库；恢复会在下次启动 Engine 时生效。";

    [ObservableProperty]
    private string _storageStatus = "打开“本地文件”后读取磁盘占用。";

    partial void OnStorageSummaryChanged(StorageSummaryDto? value)
    {
        OnPropertyChanged(nameof(StorageDataDirectory));
        OnPropertyChanged(nameof(StorageDatabasePath));
        OnPropertyChanged(nameof(StorageLogDirectory));
        OnPropertyChanged(nameof(StorageAttachmentCacheDirectory));
        OnPropertyChanged(nameof(StorageOutboxDirectory));
        OnPropertyChanged(nameof(StorageNativeDirectory));
        OnPropertyChanged(nameof(StorageTempDirectory));
        OnPropertyChanged(nameof(StorageDatabaseSizeText));
        OnPropertyChanged(nameof(StorageLogSizeText));
        OnPropertyChanged(nameof(StorageAttachmentSizeText));
        OnPropertyChanged(nameof(StorageOutboxSizeText));
        OnPropertyChanged(nameof(StorageNativeSizeText));
        OnPropertyChanged(nameof(StorageTempSizeText));
        OnPropertyChanged(nameof(StorageTotalSizeText));
        OnPropertyChanged(nameof(StorageIndexText));
        OnPropertyChanged(nameof(StoragePendingOutboxText));
    }

    public bool IsAppearanceSection => SettingsSectionIndex == 0;

    public bool IsNotificationsSection => SettingsSectionIndex == 1;

    public bool IsReadingSection => SettingsSectionIndex == 2;

    public bool IsSyncSection => SettingsSectionIndex == 3;

    public bool IsAccountSection => SettingsSectionIndex == 4;

    public bool IsStorageSection => SettingsSectionIndex == 5;

    public bool IsAccountListVisible => IsAccountSection && !ShowAddAccountForm;

    public bool IsAddAccountFormVisible => IsAccountSection && ShowAddAccountForm;

    public bool IsAboutSection => SettingsSectionIndex == 6;

    public string StorageDataDirectory => StorageSummary?.DataDirectory ?? "尚未读取";

    public string StorageDatabasePath => StorageSummary?.DatabasePath ?? "尚未读取";

    public string StorageLogDirectory => StorageSummary?.LogDirectory ?? "尚未读取";

    public string StorageAttachmentCacheDirectory => StorageSummary?.AttachmentCacheDirectory ?? "尚未读取";

    public string StorageOutboxDirectory => StorageSummary?.OutboxDirectory ?? "尚未读取";

    public string StorageNativeDirectory => StorageSummary?.NativeDirectory ?? "尚未读取";

    public string StorageTempDirectory => StorageSummary?.TempDirectory ?? "尚未读取";

    public string StorageDatabaseSizeText => FormatStorageSize(StorageSummary?.DatabaseBytes ?? 0);

    public string StorageLogSizeText => FormatStorageSize(StorageSummary?.LogBytes ?? 0);

    public string StorageAttachmentSizeText => FormatStorageSize(StorageSummary?.AttachmentCacheBytes ?? 0);

    public string StorageOutboxSizeText => FormatStorageSize(StorageSummary?.OutboxBytes ?? 0);

    public string StorageNativeSizeText => FormatStorageSize(StorageSummary?.NativeBytes ?? 0);

    public string StorageTempSizeText => FormatStorageSize(StorageSummary?.TempBytes ?? 0);

    public string StorageTotalSizeText => FormatStorageSize(StorageSummary?.TotalBytes ?? 0);

    public string StorageIndexText
        => StorageSummary is null
            ? "邮件索引尚未读取"
            : $"{StorageSummary.MessageCount} 封邮件 · {StorageSummary.BodyCount} 封正文已缓存";

    public string StoragePendingOutboxText
        => StorageSummary is null
            ? "待发送邮件尚未读取"
            : $"待发送/失败邮件 {StorageSummary.PendingOutboxCount} 封";

    private static string FormatStorageSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024):0.#} MB";
        }

        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }

    [ObservableProperty]
    private bool _showAddAccountForm;

    partial void OnShowAddAccountFormChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAccountListVisible));
        OnPropertyChanged(nameof(IsAddAccountFormVisible));
    }

    partial void OnSettingsSectionIndexChanged(int value)
    {
        if (value != 4)
        {
            ShowAddAccountForm = false;
            EditingAccountId = null;
        }

        OnPropertyChanged(nameof(IsAppearanceSection));
        OnPropertyChanged(nameof(IsNotificationsSection));
        OnPropertyChanged(nameof(IsReadingSection));
        OnPropertyChanged(nameof(IsSyncSection));
        OnPropertyChanged(nameof(IsAccountSection));
        OnPropertyChanged(nameof(IsStorageSection));
        OnPropertyChanged(nameof(IsAccountListVisible));
        OnPropertyChanged(nameof(IsAddAccountFormVisible));
        OnPropertyChanged(nameof(IsAboutSection));

        if (value == 5)
        {
            _ = RefreshStorageAsync();
        }
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
    private bool _runAtStartup;

    [ObservableProperty]
    private bool _showRemoteImages;

    [ObservableProperty]
    private bool _saveSentCopy = true;

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
        OnPropertyChanged(nameof(IsSoundWarning));
        OnPropertyChanged(nameof(IsSoundBeep));
        OnPropertyChanged(nameof(IsSoundQuestion));
        OnPropertyChanged(nameof(IsSoundStart));
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

    partial void OnRunAtStartupChanged(bool value)
    {
        QueueSettingsSave();
    }

    partial void OnSaveSentCopyChanged(bool value)
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
        IsEmpty = Messages.Count == 0 && !value && _activeScope is not null;
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

    public bool IsSoundWarning
    {
        get => NotificationSound == "warning";
        set
        {
            if (value)
            {
                NotificationSound = "warning";
            }
        }
    }

    public bool IsSoundBeep
    {
        get => NotificationSound == "beep";
        set
        {
            if (value)
            {
                NotificationSound = "beep";
            }
        }
    }

    public bool IsSoundQuestion
    {
        get => NotificationSound == "question";
        set
        {
            if (value)
            {
                NotificationSound = "question";
            }
        }
    }

    public bool IsSoundStart
    {
        get => NotificationSound == "start";
        set
        {
            if (value)
            {
                NotificationSound = "start";
            }
        }
    }

    public bool IsQqProvider => SelectedProviderIndex == 1;

    public bool IsOutlookProvider => SelectedProviderIndex == 0;

    [ObservableProperty]
    private bool _isBatchBusy;

    [ObservableProperty]
    private bool _isSelectionMode;

    partial void OnIsSelectionModeChanged(bool value)
    {
        foreach (var message in _allMessages)
        {
            message.ShowSelection = value;

            if (!value)
            {
                message.IsSelected = false;
            }
        }

        NotifySelectionState();
    }

    [RelayCommand]
    private void ToggleSelectionMode() => IsSelectionMode = !IsSelectionMode;

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

    public bool IsFilterAll
    {
        get => FilterIndex == 0;
        set
        {
            if (value)
            {
                FilterIndex = 0;
            }
        }
    }

    public bool IsFilterUnread
    {
        get => FilterIndex == 1;
        set
        {
            if (value)
            {
                FilterIndex = 1;
            }
        }
    }

    public bool IsFilterFlagged
    {
        get => FilterIndex == 2;
        set
        {
            if (value)
            {
                FilterIndex = 2;
            }
        }
    }

    public bool IsFilterAttachments
    {
        get => FilterIndex == 3;
        set
        {
            if (value)
            {
                FilterIndex = 3;
            }
        }
    }

    partial void OnFilterIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterUnread));
        OnPropertyChanged(nameof(IsFilterFlagged));
        OnPropertyChanged(nameof(IsFilterAttachments));
        ApplyFilter();
    }

    public bool IsSortByDate
    {
        get => SortFieldIndex == 0;
        set
        {
            if (value)
            {
                SortFieldIndex = 0;
            }
        }
    }

    public bool IsSortBySender
    {
        get => SortFieldIndex == 1;
        set
        {
            if (value)
            {
                SortFieldIndex = 1;
            }
        }
    }

    public bool IsSortByCategory
    {
        get => SortFieldIndex == 2;
        set
        {
            if (value)
            {
                SortFieldIndex = 2;
            }
        }
    }

    public bool IsSortBySize
    {
        get => SortFieldIndex == 3;
        set
        {
            if (value)
            {
                SortFieldIndex = 3;
            }
        }
    }

    public bool IsSortByImportance
    {
        get => SortFieldIndex == 4;
        set
        {
            if (value)
            {
                SortFieldIndex = 4;
            }
        }
    }

    public bool IsSortBySubject
    {
        get => SortFieldIndex == 5;
        set
        {
            if (value)
            {
                SortFieldIndex = 5;
            }
        }
    }

    public bool IsSortByType
    {
        get => SortFieldIndex == 6;
        set
        {
            if (value)
            {
                SortFieldIndex = 6;
            }
        }
    }

    public bool IsSortNewestFirst
    {
        get => SortDescending;
        set
        {
            if (value)
            {
                SortDescending = true;
            }
        }
    }

    public bool IsSortOldestFirst
    {
        get => !SortDescending;
        set
        {
            if (value)
            {
                SortDescending = false;
            }
        }
    }

    partial void OnSortFieldIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSortByDate));
        OnPropertyChanged(nameof(IsSortBySender));
        OnPropertyChanged(nameof(IsSortByCategory));
        OnPropertyChanged(nameof(IsSortBySize));
        OnPropertyChanged(nameof(IsSortByImportance));
        OnPropertyChanged(nameof(IsSortBySubject));
        OnPropertyChanged(nameof(IsSortByType));
        ApplyFilter();
    }

    partial void OnSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSortNewestFirst));
        OnPropertyChanged(nameof(IsSortOldestFirst));
        ApplyFilter();
    }

    public string JumpLabel => JumpRangeIndex switch
    {
        1 => "今天",
        2 => "昨天",
        3 => "上周",
        4 => "上个月",
        5 => CustomDate is { } date
            ? $"{date:M/d}"
            : "自定义",
        6 => "去年",
        _ => "跳转"
    };

    public bool IsJumpToday
    {
        get => JumpRangeIndex == 1;
        set
        {
            if (value)
            {
                JumpRangeIndex = 1;
            }
        }
    }

    public bool IsJumpYesterday
    {
        get => JumpRangeIndex == 2;
        set
        {
            if (value)
            {
                JumpRangeIndex = 2;
            }
        }
    }

    public bool IsJumpLastWeek
    {
        get => JumpRangeIndex == 3;
        set
        {
            if (value)
            {
                JumpRangeIndex = 3;
            }
        }
    }

    public bool IsJumpLastMonth
    {
        get => JumpRangeIndex == 4;
        set
        {
            if (value)
            {
                JumpRangeIndex = 4;
            }
        }
    }

    public bool IsJumpLastYear
    {
        get => JumpRangeIndex == 6;
        set
        {
            if (value)
            {
                JumpRangeIndex = 6;
            }
        }
    }

    partial void OnJumpRangeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(JumpLabel));
        OnPropertyChanged(nameof(IsJumpToday));
        OnPropertyChanged(nameof(IsJumpYesterday));
        OnPropertyChanged(nameof(IsJumpLastWeek));
        OnPropertyChanged(nameof(IsJumpLastMonth));
        OnPropertyChanged(nameof(IsJumpLastYear));
        JumpToRange();
    }

    partial void OnCustomDateChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(JumpLabel));
        OnPropertyChanged(nameof(CustomDateText));
        BuildCalendar();
    }

    [RelayCommand]
    private void ApplyCustomRange()
    {
        if (CustomDate is null)
        {
            return;
        }

        if (JumpRangeIndex == 5)
        {
            JumpToRange();
        }
        else
        {
            JumpRangeIndex = 5;
        }
    }

    /// <summary>跳转到指定时间范围的第一封邮件：不改变筛选，只选中并滚动到该邮件。</summary>
    private void JumpToRange()
    {
        if (Messages.Count == 0 || GetJumpRange() is not { } range)
        {
            return;
        }

        // 范围内没有邮件时，退化为定位到时间上最接近的一封。
        var target = Messages.FirstOrDefault(message => InRange(message.Date.ToLocalTime(), range))
                     ?? Messages.OrderBy(message => DistanceTo(message.Date.ToLocalTime(), range)).First();

        SelectedMessage = target;
        ScrollToMessageRequested?.Invoke(target);
    }

    private static bool InRange(DateTimeOffset value, (DateTimeOffset Start, DateTimeOffset End) range)
        => value >= range.Start && value < range.End;

    private static TimeSpan DistanceTo(DateTimeOffset value, (DateTimeOffset Start, DateTimeOffset End) range)
        => value < range.Start
            ? range.Start - value
            : value >= range.End
                ? value - range.End
                : TimeSpan.Zero;


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
        await TakePendingNavigationAsync();
    }

    [RelayCommand]
    private async Task RetryConnectionAsync()
    {
        await ConnectAsync();
    }

    [RelayCommand]
    private async Task RetryOfflineAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!IsConnected)
        {
            await ConnectAsync();
            return;
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Accounts.Count == 0)
        {
            return;
        }

        try
        {
            // 统一文件夹跨账户，立即同步需要覆盖全部账户。
            foreach (var account in Accounts)
            {
                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.SyncAccount,
                    new SyncAccountRequest { AccountId = account.Id });
            }

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
        if (!HasMore || _activeScope is null || IsLoadingMore)
        {
            return;
        }

        var scope = _activeScope;
        IsLoadingMore = true;

        try
        {
            var response = await _engine.RequestAsync<MessagesPageResponse>(
                IpcMessageTypes.GetMessages,
                BuildMessagesRequest(50, _nextDate, _nextId));

            if (_activeScope != scope)
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
    private async Task DeleteSelectedAsync()
    {
        if (SelectedMessage is null)
        {
            return;
        }

        var message = SelectedMessage;
        var permanent = SelectedMessageInTrash;
        StatusMessage = permanent ? "正在永久删除邮件…" : "正在删除邮件…";

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.DeleteMessage,
                new DeleteMessageRequest { MessageId = message.Id, Permanent = permanent });

            RemoveMessageFromView(message);
            await ReloadCurrentFolderAsync();
            StatusMessage = permanent ? "邮件已永久删除。" : "邮件已移至已删除。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    [RelayCommand]
    private async Task ArchiveSelectedAsync()
    {
        if (SelectedMessage is null)
        {
            return;
        }

        var source = SelectedMessageFolder;
        if (source is null)
        {
            StatusMessage = "找不到邮件所在的文件夹。";
            return;
        }

        var archive = Folders.FirstOrDefault(folder =>
            string.Equals(folder.AccountId, source.AccountId, StringComparison.Ordinal)
            && folder.Role == FolderRole.Archive
            && folder.Id != source.Id);

        if (archive is null)
        {
            StatusMessage = "当前账户没有归档文件夹，请先在服务器上创建归档文件夹。";
            return;
        }

        await MoveSelectedToFolderAsync(archive.Id);
    }

    [RelayCommand]
    private async Task MoveSelectedToFolderAsync(long targetFolderId)
    {
        if (SelectedMessage is null)
        {
            return;
        }

        var message = SelectedMessage;
        var target = Folders.FirstOrDefault(folder => folder.Id == targetFolderId
            && string.Equals(folder.AccountId, message.AccountId, StringComparison.Ordinal));
        if (target is null)
        {
            StatusMessage = "只能把邮件移动到同一账户的文件夹。";
            return;
        }

        StatusMessage = $"正在移动到“{target.Name}”…";

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.MoveMessage,
                new MoveMessageRequest { MessageId = message.Id, TargetFolderId = targetFolderId });

            RemoveMessageFromView(message);
            await ReloadCurrentFolderAsync();
            StatusMessage = $"邮件已移动到“{target.Name}”。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void RemoveMessageFromView(MailSummaryViewModel message)
    {
        _allMessages.Remove(message);
        Messages.Remove(message);
        if (ReferenceEquals(SelectedMessage, message))
        {
            SelectedMessage = null;
            _currentBody = null;
            Reader.Reset();
        }

        ApplyFilter();
    }

    private void RebuildMoveTargets()
    {
        MoveTargets.Clear();
        if (SelectedMessage is null)
        {
            OnPropertyChanged(nameof(HasMoveTargets));
            return;
        }

        var targets = Folders
            .Where(folder => string.Equals(folder.AccountId, SelectedMessage.AccountId, StringComparison.Ordinal)
                             && folder.Id != SelectedMessage.FolderId
                             && folder.Role != FolderRole.Drafts)
            .OrderBy(folder => folder.Role == FolderRole.Other ? 1 : 0)
            .ThenBy(folder => folder.Name, StringComparer.CurrentCulture);

        foreach (var folder in targets)
        {
            MoveTargets.Add(new MoveTargetViewModel(folder, MoveSelectedToFolderCommand));
        }

        OnPropertyChanged(nameof(HasMoveTargets));
        OnPropertyChanged(nameof(SelectedMessageFolder));
        OnPropertyChanged(nameof(SelectedMessageInTrash));
        OnPropertyChanged(nameof(SelectedMessageInDrafts));
        OnPropertyChanged(nameof(DeleteActionText));
    }

    public void BeginCreateFolder(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return;
        }

        _folderEditorAccountId = accountId;
        _folderEditorTarget = null;
        FolderEditorTitle = "新建文件夹";
        FolderEditorName = string.Empty;
        IsFolderDeleteConfirmOpen = false;
        IsFolderEditorOpen = true;
    }

    public void BeginRenameFolder(FolderViewModel folder)
    {
        if (folder.Role != FolderRole.Other)
        {
            StatusMessage = "系统文件夹不能重命名。";
            return;
        }

        _folderEditorAccountId = folder.AccountId;
        _folderEditorTarget = folder;
        FolderEditorTitle = $"重命名“{folder.Name}”";
        FolderEditorName = folder.Name;
        IsFolderDeleteConfirmOpen = false;
        IsFolderEditorOpen = true;
    }

    public void RequestDeleteFolder(FolderViewModel folder)
    {
        if (folder.Role != FolderRole.Other)
        {
            StatusMessage = "系统文件夹不能删除。";
            return;
        }

        _folderDeleteTarget = folder;
        FolderDeleteName = folder.Name;
        IsFolderEditorOpen = false;
        IsFolderDeleteConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelFolderEdit()
    {
        IsFolderEditorOpen = false;
        _folderEditorTarget = null;
    }

    [RelayCommand]
    private void CancelFolderDelete()
    {
        IsFolderDeleteConfirmOpen = false;
        _folderDeleteTarget = null;
    }

    [RelayCommand]
    private async Task SubmitFolderEditAsync()
    {
        var name = FolderEditorName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "文件夹名称不能为空。";
            return;
        }

        var target = _folderEditorTarget;
        if (target is null && string.IsNullOrWhiteSpace(_folderEditorAccountId))
        {
            StatusMessage = "找不到要创建文件夹的账户。";
            return;
        }

        try
        {
            if (target is null)
            {
                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.CreateFolder,
                    new CreateFolderRequest { AccountId = _folderEditorAccountId!, Name = name });
                StatusMessage = $"文件夹“{name}”已创建。";
            }
            else
            {
                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.RenameFolder,
                    new RenameFolderRequest { FolderId = target.Id, Name = name });
                StatusMessage = $"文件夹已重命名为“{name}”。";
            }

            IsFolderEditorOpen = false;
            _folderEditorTarget = null;
            await RefreshFolderTreeAsync();
            await RefreshMessagesAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    [RelayCommand]
    private async Task ConfirmDeleteFolderAsync()
    {
        var folder = _folderDeleteTarget;
        if (folder is null)
        {
            return;
        }

        var wasSelected = SelectedFolder?.Id == folder.Id;
        StatusMessage = $"正在删除文件夹“{folder.Name}”…";

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.DeleteFolder,
                new DeleteFolderRequest { FolderId = folder.Id });

            IsFolderDeleteConfirmOpen = false;
            _folderDeleteTarget = null;

            if (wasSelected)
            {
                SelectedFolder = null;
                SelectedMessage = null;
                _currentBody = null;
                Reader.Reset();
                _allMessages.Clear();
                Messages.Clear();
            }

            await RefreshFolderTreeAsync();
            StatusMessage = $"文件夹“{folder.Name}”已删除。";

            if (wasSelected)
            {
                var account = Accounts.FirstOrDefault(item => item.Id == folder.AccountId);
                if (account is not null)
                {
                    SelectedAccount = account;
                    SelectedUnifiedFolder = null;
                    SelectedFolder = account.Folders.FirstOrDefault(item => item.Role == FolderRole.Inbox)
                                     ?? account.Folders.FirstOrDefault();
                }
            }
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

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
                draft.AccountId);
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
        IsComposeOpen = true;
    }

    [RelayCommand]
    private void NewMail() => OpenCompose(ComposeMode.New);

    [RelayCommand]
    private void Reply() => OpenCompose(ComposeMode.Reply);

    [RelayCommand]
    private void ReplyAll() => OpenCompose(ComposeMode.ReplyAll);

    [RelayCommand]
    private void Forward() => OpenCompose(ComposeMode.Forward);

    private void OpenCompose(ComposeMode mode)
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
            mode == ComposeMode.New ? null : _currentBody);

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

    [RelayCommand]
    private void ShowImages()
    {
        _allowRemoteImages = true;
        RenderCurrentBody();
    }

    private async Task<AttachmentBytesDto> LoadAttachmentBytesAsync(AttachmentViewModel attachment)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("尚未连接 Engine。");
        }

        var response = await _engine.RequestAsync<AttachmentBytesResponse>(
            IpcMessageTypes.ReadAttachment,
            new ReadAttachmentRequest
            {
                MessageId = attachment.MessageId,
                Index = attachment.Index
            },
            TimeSpan.FromSeconds(60));

        return response.Attachment;
    }

    private async Task DownloadAttachmentAsync(AttachmentViewModel attachment)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow;

            if (window is null)
            {
                return;
            }

            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存附件",
                SuggestedFileName = attachment.FileName,
                DefaultExtension = Path.GetExtension(attachment.FileName).TrimStart('.'),
                ShowOverwritePrompt = true
            });

            var path = file?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var response = await _engine.RequestAsync<AttachmentSavedResponse>(
                IpcMessageTypes.DownloadAttachment,
                new DownloadAttachmentRequest
                {
                    MessageId = attachment.MessageId,
                    Index = attachment.Index,
                    DestinationPath = path
                },
                TimeSpan.FromSeconds(120));

            HasError = false;
            StatusMessage = $"附件已保存：{response.Path}";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private async Task OpenAttachmentAsync(AttachmentViewModel attachment)
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MailDog",
                "attachments");
            Directory.CreateDirectory(directory);

            var fileName = Path.GetFileName(attachment.FileName);
            var path = Path.Combine(directory, $"{attachment.MessageId}-{attachment.Index}-{fileName}");

            var response = await _engine.RequestAsync<AttachmentSavedResponse>(
                IpcMessageTypes.DownloadAttachment,
                new DownloadAttachmentRequest
                {
                    MessageId = attachment.MessageId,
                    Index = attachment.Index,
                    DestinationPath = path
                },
                TimeSpan.FromSeconds(120));

            Process.Start(new ProcessStartInfo(response.Path)
            {
                UseShellExecute = true
            });

            StatusMessage = $"已打开附件：{response.FileName}";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    [RelayCommand]
    private void SelectAllMessages()
    {
        if (Messages.Count == 0)
        {
            return;
        }

        if (!IsSelectionMode)
        {
            IsSelectionMode = true;
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

            if (changed > 0)
            {
                foreach (var message in targets)
                {
                    AdjustFolderUnread(message.FolderId, isRead ? -1 : 1);
                }

                RecalculateUnifiedCounts();
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
            SyncIntervalMinutes = SyncIntervalValues[Math.Clamp(SyncIntervalIndex, 0, SyncIntervalValues.Length - 1)],
            CacheDays = _settings.CacheDays,
            MarkReadOnOpen = MarkReadOnOpen,
            RunAtStartup = RunAtStartup,
            ShowRemoteImages = ShowRemoteImages,
            SaveSentCopy = SaveSentCopy,
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

            if (activeFolderDeleted)
            {
                SelectedFolder = null;
            }

            await LoadAccountsAsync();

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

    [RelayCommand]
    private void OpenAbout()
    {
        SettingsSectionIndex = 6;
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
    private async Task RefreshStorageAsync()
    {
        if (IsStorageBusy)
        {
            return;
        }

        IsStorageBusy = true;
        StorageStatus = "正在读取本地数据…";

        try
        {
            await LoadStorageSummaryCoreAsync();
            await LoadOutboxAsync();
            StorageStatus = "本地数据统计已更新。";
        }
        catch (Exception exception)
        {
            StorageStatus = $"读取失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsStorageBusy = false;
        }
    }

    private async Task LoadStorageSummaryCoreAsync()
    {
        var response = await _engine.RequestAsync<StorageSummaryResponse>(
            IpcMessageTypes.GetStorageSummary,
            new GetStorageSummaryRequest(),
            TimeSpan.FromSeconds(30));

        StorageSummary = response.Summary;
    }

    private async Task LoadOutboxAsync()
    {
        var response = await _engine.RequestAsync<OutboxListResponse>(
            IpcMessageTypes.ListOutbox,
            new ListOutboxRequest(),
            TimeSpan.FromSeconds(30));

        OutboxItems.Clear();
        foreach (var item in response.Items)
        {
            OutboxItems.Add(new OutboxItemViewModel(item));
        }

        OnPropertyChanged(nameof(HasOutboxItems));
        OutboxStatus = OutboxItems.Count == 0
            ? "没有待发送或发送失败的邮件。"
            : $"共 {OutboxItems.Count} 封待发送/失败邮件。";
    }

    public async Task RefreshOutboxAsync()
    {
        if (IsOutboxBusy)
        {
            return;
        }

        IsOutboxBusy = true;
        try
        {
            await LoadOutboxAsync();
        }
        catch (Exception exception)
        {
            OutboxStatus = $"读取发件箱失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsOutboxBusy = false;
        }
    }

    public async Task RetryOutboxAsync(OutboxItemViewModel item)
    {        if (IsOutboxBusy)
        {
            return;
        }

        IsOutboxBusy = true;
        OutboxStatus = "正在重试发送…";

        try
        {
            var response = await _engine.RequestAsync<SendMailResponse>(
                IpcMessageTypes.RetryOutbox,
                new RetryOutboxRequest { OutboxId = item.Id },
                TimeSpan.FromSeconds(75));

            OutboxStatus = response.Sent
                ? "邮件已发送。"
                : response.Queued
                    ? $"仍在发件箱：{response.Error ?? "稍后自动重试"}"
                    : $"发送失败：{response.Error ?? "未知错误"}";

            await LoadOutboxAsync();
        }
        catch (Exception exception)
        {
            OutboxStatus = $"重试失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsOutboxBusy = false;
        }
    }

    public async Task CreateBackupAsync(string destinationPath)
    {
        if (IsBackupBusy)
        {
            return;
        }

        IsBackupBusy = true;
        BackupStatus = "正在创建数据库备份…";
        try
        {
            var response = await _engine.RequestAsync<BackupResponse>(
                IpcMessageTypes.CreateBackup,
                new CreateBackupRequest { DestinationPath = destinationPath },
                TimeSpan.FromSeconds(120));

            BackupStatus = $"备份已保存：{response.Result.Path}（{FormatStorageSize(response.Result.Size)}）";
            await LoadStorageSummaryCoreAsync();
        }
        catch (Exception exception)
        {
            BackupStatus = $"备份失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsBackupBusy = false;
        }
    }

    public async Task RestoreBackupAsync(string archivePath)
    {
        if (IsBackupBusy)
        {
            return;
        }

        IsBackupBusy = true;
        BackupStatus = "正在校验备份…";
        try
        {
            var response = await _engine.RequestAsync<RestoreResponse>(
                IpcMessageTypes.RestoreBackup,
                new RestoreBackupRequest { ArchivePath = archivePath },
                TimeSpan.FromSeconds(60));

            BackupStatus = response.Result.RestartRequired
                ? "恢复已安排。请从托盘完全退出 MailDog 后重新打开，恢复将在下次启动时生效；当前数据库会先自动保留一份副本。"
                : "恢复已完成。";
        }
        catch (Exception exception)
        {
            BackupStatus = $"恢复失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsBackupBusy = false;
        }
    }

    public async Task ExportSelectedMessageAsync(string destinationPath)
    {
        if (SelectedMessage is not { } message)
        {
            return;
        }

        try
        {
            var response = await _engine.RequestAsync<MessageExportResponse>(
                IpcMessageTypes.ExportMessage,
                new ExportMessageRequest
                {
                    MessageId = message.Id,
                    DestinationPath = destinationPath
                },
                TimeSpan.FromSeconds(120));

            StatusMessage = $"已导出邮件：{response.Path}（{FormatStorageSize(response.Size)}）";
            HasError = false;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    public async Task DeleteOutboxAsync(OutboxItemViewModel item)
    {
        if (IsOutboxBusy)
        {
            return;
        }

        IsOutboxBusy = true;

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.DeleteOutbox,
                new DeleteOutboxRequest { OutboxId = item.Id });

            OutboxStatus = "已从发件箱删除。";
            await LoadOutboxAsync();
        }
        catch (Exception exception)
        {
            OutboxStatus = $"删除失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsOutboxBusy = false;
        }
    }

    private async Task CleanStorageAsync(CleanStorageRequest request)
    {
        if (IsStorageBusy)
        {
            return;
        }

        IsStorageBusy = true;
        StorageStatus = "正在清理…";

        try
        {
            var response = await _engine.RequestAsync<StorageCleanupResponse>(
                IpcMessageTypes.CleanStorage,
                request,
                TimeSpan.FromSeconds(60));

            try
            {
                await LoadStorageSummaryCoreAsync();
            }
            catch
            {
                // 清理已成功，统计刷新失败不应改变清理结果。
            }

            var skipped = response.Result.SkippedFiles > 0
                ? $"，{response.Result.SkippedFiles} 个文件被占用未清理"
                : string.Empty;
            StorageStatus = $"{response.Result.Message} 释放 {FormatStorageSize(response.Result.FreedBytes)}{skipped}";
        }
        catch (Exception exception)
        {
            StorageStatus = $"清理失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsStorageBusy = false;
        }
    }

    [RelayCommand]
    private Task CleanAttachmentCacheAsync()
        => CleanStorageAsync(new CleanStorageRequest { CleanAttachmentCache = true });

    [RelayCommand]
    private Task CleanLogsAsync()
        => CleanStorageAsync(new CleanStorageRequest { CleanLogs = true });

    [RelayCommand]
    private Task CleanTempFilesAsync()
        => CleanStorageAsync(new CleanStorageRequest { CleanTempFiles = true });

    [RelayCommand]
    private void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            StorageStatus = "目录不存在或尚未读取。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            StorageStatus = $"打开目录失败：{GetErrorMessage(exception)}";
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
        ApplyFilter();

        var term = value.Trim();
        if (term.Length == 0)
        {
            _searchCts?.Cancel();
            return;
        }

        _ = SearchBodyAsync(term);
    }

    private async Task SearchBodyAsync(string term)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var folderId = _activeFolderId;
        var role = _activeRole;

        try
        {
            var response = await _engine.RequestAsync<MessageSearchResponse>(
                IpcMessageTypes.SearchMessages,
                new SearchMessagesRequest
                {
                    FolderId = folderId ?? 0,
                    Role = role,
                    Query = term
                },
                TimeSpan.FromSeconds(30),
                cts.Token);

            if (cts.IsCancellationRequested
                || !string.Equals(SearchText.Trim(), term, StringComparison.Ordinal)
                || folderId != _activeFolderId
                || role != _activeRole)
            {
                return;
            }

            _searchResultIds = new HashSet<long>(response.MessageIds);
            ApplyFilter();
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
                "mica" => 1,
                "acrylic" => 2,
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
            var intervalIndex = Array.IndexOf(SyncIntervalValues, _settings.SyncIntervalMinutes);
            SyncIntervalIndex = intervalIndex >= 0 ? intervalIndex : 0;
            MarkReadOnOpen = _settings.MarkReadOnOpen;
            RunAtStartup = _settings.RunAtStartup;
            ShowRemoteImages = _settings.ShowRemoteImages;
            SaveSentCopy = _settings.SaveSentCopy;
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

    private ComposeAccount[] BuildComposeAccounts()
    {
        var ordered = Accounts
            .OrderBy(account => account.IsDefault ? 0 : 1)
            .Select(account => new ComposeAccount(account.Id, account.Email, account.DisplayName))
            .ToArray();
        return ordered;
    }

    private async Task LoadAccountsAsync()
    {
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
            StatusMessage = "正在加载文件夹…";

            Folders.Clear();
            _foldersById.Clear();
            _folderRoles.Clear();

            foreach (var account in Accounts)
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

            if (Accounts.Count == 0)
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
            StatusMessage = "正在加载邮件…";
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
            ApplyFilter();
        }
        catch (Exception exception)
        {
            ShowError(exception);
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

        var filtered = query.ToList();
        filtered.Sort(CompareMessages);

        // 集合没有变化时保持原样，避免列表与选中项被重建导致阅读区闪烁。
        if (filtered.Count == Messages.Count && filtered.SequenceEqual(Messages))
        {
            IsEmpty = Messages.Count == 0 && !IsBusy && _activeScope is not null;
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

        IsEmpty = Messages.Count == 0 && !IsBusy && _activeScope is not null;
        NotifySelectionState();
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
            switch (message)
            {
                case NewMailEvent newMail:
                    if (IsFolderInActiveScope(newMail.FolderId))
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

            StatusMessage = message;
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
            StatusMessage = message;
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
            StatusMessage = OfflineMessage;
        }
        else
        {
            StatusMessage = null;
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
