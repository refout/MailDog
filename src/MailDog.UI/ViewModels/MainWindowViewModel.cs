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

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly string[] ThreadSubjectPrefixes =
        ["re:", "re：", "fwd:", "fw:", "回复：", "答复：", "转发："];

    private static readonly HttpClient UnsubscribeClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private readonly EngineClient _engine;
    private readonly UpdateService _updates = new();
    private UpdateCheckResult? _updateResult;
    private readonly HtmlSanitizer _sanitizer = new();
    private readonly HtmlDocumentBuilder _builder = new();
    private readonly List<MailSummaryViewModel> _allMessages = [];
    private readonly List<MailSummaryViewModel> _searchExtraMessages = [];
    private IReadOnlyList<ContactSuggestionDto> _contacts = [];
    private readonly Dictionary<long, FolderViewModel> _foldersById = [];
    private readonly Dictionary<long, FolderRole> _folderRoles = [];
    private readonly Dictionary<string, string> _syncErrors = [];
    private AppSettingsDto _settings = new();

    public ObservableCollection<AccountHealthViewModel> AccountHealth { get; } = [];
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
        Reader.SaveAttachments = SaveAttachmentsAsync;
        Accounts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAccounts));
            OnPropertyChanged(nameof(IsAccountSignatureVisible));
        };
        RefreshBackgroundPresets();
        BuildCalendar();

        UnifiedFolders.Add(new UnifiedFolderViewModel(FolderRole.Inbox, "所有收件箱", Icon.MailInbox));
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

    /// <summary>选择模式“移动到”菜单项；只有所选邮件同账户时才可批量移动。</summary>
    public ObservableCollection<MoveTargetViewModel> BatchMoveTargets { get; } = [];

    public bool HasBatchMoveTargets => BatchMoveTargets.Count > 0;

    public ObservableCollection<MailSummaryViewModel> Messages { get; } = [];

    public ReaderViewModel Reader { get; } = new();

    public string[] ProviderOptions { get; } = ["Outlook", "QQ 邮箱", "自定义服务器"];

    public static int[] SyncIntervalValues { get; } = [1, 5, 15, 30, 60];

    public string AppVersion => typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string VersionText => $"版本 {AppVersion}";

    public string PlatformText => OperatingSystem.IsWindows()
        ? "Windows 10 / 11（x64、Arm64）"
        : OperatingSystem.IsLinux()
            ? "Linux（x64）"
            : "当前平台";

    public string StartupTitle => OperatingSystem.IsWindows()
        ? "登录 Windows 时自动启动 MailDog"
        : "登录系统时自动启动 MailDog";

    public string StartupDescription => OperatingSystem.IsWindows()
        ? "开启后，登录 Windows 会自动启动 MailDog 并继续收取新邮件；关闭后会移除当前用户的启动项，不影响账户与邮件数据。"
        : "开启后，登录桌面会自动启动 MailDog 并继续收取新邮件；关闭后会移除当前用户的 autostart 项，不影响账户与邮件数据。";

    /// <summary>关于页直接展示的主要开源组件；完整清单见 docs/THIRD-PARTY-NOTICES.md。</summary>
    public IReadOnlyList<ThirdPartyNoticeViewModel> ThirdPartyNotices { get; } =
    [
        new("Avalonia", "跨平台桌面界面框架与控件库", "MIT"),
        new("Avalonia.Controls.WebView", "邮件正文 HTML 渲染", "MIT"),
        new("Avalonia.Angle.Windows.Natives", "WebView 图形后端（ANGLE）", "BSD-3-Clause"),
        new("CommunityToolkit.Mvvm", "MVVM 绑定、命令与消息机制", "MIT"),
        new("MailKit / MimeKit", "IMAP、SMTP 与 MIME 协议实现", "MIT"),
        new("Microsoft.Data.Sqlite", "SQLite 数据访问", "MIT"),
        new("SQLite", "嵌入式数据库引擎", "Public Domain"),
        new("SQLitePCLRaw", "SQLite 原生绑定", "Apache-2.0"),
        new("System.Security.Cryptography.ProtectedData", "账户凭据加密（DPAPI）", "MIT"),
        new("HtmlAgilityPack", "HTML 解析与内容清洗", "MIT"),
        new("SkiaSharp / HarfBuzzSharp", "2D 图形绘制与文本整形", "MIT"),
        new("FluentIcons.Avalonia", "Fluent 图标资源", "MIT"),
        new("AvaloniaRichEditor", "富文本编辑控件", "见包内许可")
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThirdPartyNoticesToggleText))]
    private bool _isThirdPartyNoticesExpanded;

    /// <summary>第三方许可区块的展开/收起按钮文案。</summary>
    public string ThirdPartyNoticesToggleText => IsThirdPartyNoticesExpanded ? "收起" : "展开";

    public string RuntimeVersion => Environment.Version.ToString();

    public string RuntimeText => $".NET {RuntimeVersion}";

    /// <summary>检查更新时的状态文案；未检查时不显示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpdateStatusVisible))]
    private string _updateStatusText = string.Empty;

    public bool IsUpdateStatusVisible => !string.IsNullOrWhiteSpace(UpdateStatusText);

    /// <summary>有可用新版本时，关于页才显示下载按钮。</summary>
    [ObservableProperty]
    private bool _hasUpdateAvailable;

    /// <summary>按钮文字：安装版是"下载并安装"，便携版是"去下载页"。</summary>
    [ObservableProperty]
    private string _updateActionText = "下载并安装";

    [ObservableProperty]
    private bool _isUpdateBusy;

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
    [NotifyPropertyChangedFor(nameof(IsReaderHtmlVisible))]
    [NotifyPropertyChangedFor(nameof(IsWelcomeVisible))]
    [NotifyPropertyChangedFor(nameof(IsStatusBarVisible))]
    private bool _isSettingsOpen;

    public bool IsOverlayOpen => IsSettingsOpen;

    public bool IsWelcomeVisible => ShowWelcome && !IsOverlayOpen;

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
    [NotifyPropertyChangedFor(nameof(HasSelectedMessage))]
    [NotifyPropertyChangedFor(nameof(HasThreadMessages))]
    private MailSummaryViewModel? _selectedMessage;

    public bool HasSelectedMessage => SelectedMessage is not null;

    public bool HasThreadMessages => SelectedMessage?.ThreadCount > 1;

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
    [NotifyPropertyChangedFor(nameof(SearchEmptyTitle))]
    [NotifyPropertyChangedFor(nameof(SearchEmptyHint))]
    [NotifyPropertyChangedFor(nameof(ReaderEmptyTitle))]
    [NotifyPropertyChangedFor(nameof(ReaderEmptyHint))]
    [NotifyPropertyChangedFor(nameof(HasSearchText))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchScopeLabel))]
    [NotifyPropertyChangedFor(nameof(SearchScopeAutomationName))]
    private bool _isGlobalSearch;

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public string SearchScopeLabel => IsGlobalSearch ? "全部邮箱" : "当前文件夹";

    public string SearchScopeAutomationName => IsGlobalSearch
        ? "搜索全部邮箱，点击切回当前文件夹"
        : "搜索当前文件夹，点击切换到全部邮箱";

    public string SearchEmptyTitle => string.IsNullOrWhiteSpace(SearchText)
        ? "这个文件夹没有邮件"
        : "没有找到匹配的邮件";

    public string SearchEmptyHint => string.IsNullOrWhiteSpace(SearchText)
        ? "同步完成后会自动刷新"
        : "换个关键词，或清空搜索条件再试";

    public string ReaderEmptyTitle => string.IsNullOrWhiteSpace(SearchText)
        ? "选择一封邮件开始阅读"
        : "没有匹配的邮件";

    public string ReaderEmptyHint => string.IsNullOrWhiteSpace(SearchText)
        ? "使用 ↑ ↓ 浏览，Enter 打开"
        : "换个关键词，或清空搜索条件";

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
    [NotifyPropertyChangedFor(nameof(IsStatusBarVisible))]
    private string? _statusMessage;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool IsStatusBarVisible => HasStatusMessage && !IsOverlayOpen && !IsComposeOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReaderHtmlVisible))]
    [NotifyPropertyChangedFor(nameof(IsStatusBarVisible))]
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
    [NotifyPropertyChangedFor(nameof(ShowMessagePreview))]
    private string _density = "compact";

    public bool ShowMessagePreview => ThemeService.IsNormalDensity(Density);

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
    [NotifyPropertyChangedFor(nameof(IsCustomBackgroundSelected))]
    private string _backgroundColorHex = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpacityText))]
    private int _backgroundOpacity = 85;

    [ObservableProperty]
    private int _settingsSectionIndex;

    [ObservableProperty]
    private string _settingsSaveHint = "所有更改会自动保存";

    public ObservableCollection<MailTemplateViewModel> Templates { get; } = [];

    public ObservableCollection<QuickReplyTargetViewModel> QuickReplyTargets { get; } = [];

    public bool HasTemplates => Templates.Count > 0;

    public bool HasQuickReplyTargets => QuickReplyTargets.Count > 0;

    [ObservableProperty]
    private string _templateName = string.Empty;

    [ObservableProperty]
    private string _templateSubject = string.Empty;

    [ObservableProperty]
    private string _templateBody = string.Empty;

    [ObservableProperty]
    private bool _templateIsQuickReply;

    [ObservableProperty]
    private bool _isTemplateEditorOpen;

    [ObservableProperty]
    private string _templateEditorTitle = "新建模板";

    [ObservableProperty]
    private string? _templateHint;

    private string? _editingTemplateId;

    [ObservableProperty]
    private AccountViewModel? _selectedSignatureAccount;

    [ObservableProperty]
    private string _accountSignatureText = string.Empty;

    private readonly Dictionary<string, string> _accountSignatures = new(StringComparer.Ordinal);
    private bool _suppressSignatureSync;

    private readonly List<AccountIdentityDto> _accountIdentities = [];

    public ObservableCollection<AccountIdentityViewModel> AccountIdentities { get; } = [];

    public bool HasAccountIdentities => AccountIdentities.Count > 0;

    [ObservableProperty]
    private string _newIdentityAddress = string.Empty;

    [ObservableProperty]
    private string _newIdentityName = string.Empty;

    [ObservableProperty]
    private string _newIdentitySignature = string.Empty;

    [ObservableProperty]
    private string? _identityHint;

    public bool HasIdentityHint => !string.IsNullOrWhiteSpace(IdentityHint);

    partial void OnIdentityHintChanged(string? value) => OnPropertyChanged(nameof(HasIdentityHint));

    private readonly Dictionary<string, string> _accountNotificationSounds = new(StringComparer.Ordinal);
    private bool _suppressAccountSoundSync;

    public ObservableCollection<AccountSoundViewModel> AccountSoundSettings { get; } = [];

    public bool HasAccountSoundSettings => AccountSoundSettings.Count > 0;

    public ObservableCollection<TagViewModel> Tags { get; } = [];

    public ObservableCollection<TagMenuViewModel> TagMenuTargets { get; } = [];

    public ObservableCollection<TagFilterViewModel> TagFilters { get; } = [];

    public bool HasTags => Tags.Count > 0;

    [ObservableProperty]
    private TagFilterViewModel? _selectedTagFilter;

    [ObservableProperty]
    private string _tagName = string.Empty;

    [ObservableProperty]
    private string _tagColor = "#0F7B0F";

    public IBrush TagColorBrush => new SolidColorBrush(TagViewModel.ParseColor(TagColor));

    partial void OnTagColorChanged(string value) => OnPropertyChanged(nameof(TagColorBrush));

    [ObservableProperty]
    private string? _tagHint;

    [ObservableProperty]
    private bool _isTagEditorOpen;

    [ObservableProperty]
    private string _tagEditorTitle = "新建标签";

    private long? _editingTagId;

    public string TagFilterText => SelectedTagFilter is null
        ? "全部邮件"
        : $"标签：{SelectedTagFilter.Name}";

    public static int[] AppLockTimeoutValues { get; } = [0, 5, 15, 30, 60];

    [ObservableProperty]
    private bool _appLockEnabled;

    [ObservableProperty]
    private string _appLockPin = string.Empty;

    [ObservableProperty]
    private string _appLockPinConfirm = string.Empty;

    [ObservableProperty]
    private string _appLockCurrentPin = string.Empty;

    [ObservableProperty]
    private int _appLockTimeoutIndex = 2;

    [ObservableProperty]
    private string? _appLockStatus;

    [ObservableProperty]
    private bool _isAppLocked;

    [ObservableProperty]
    private string _lockPin = string.Empty;

    [ObservableProperty]
    private string? _unlockError;

    private string _appLockHash = string.Empty;
    private string _appLockSalt = string.Empty;

    /// <summary>已保存的应用锁状态；界面上的开关只是待保存值，避免其它设置的自动保存顺手改掉应用锁。</summary>
    private bool _savedAppLockEnabled;
    private int _savedAppLockTimeoutMinutes = 15;

    private DateTimeOffset _lastActivity = DateTimeOffset.Now;
    private DispatcherTimer? _lockTimer;

    public bool HasAppLockPin => !string.IsNullOrEmpty(_appLockHash);

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

    /// <summary>仅用于本次加密备份/恢复，不写入设置。</summary>
    [ObservableProperty]
    private string _backupPassword = string.Empty;

    [ObservableProperty]
    private string _storageStatus = "打开“本地文件”后读取磁盘占用。";

    [ObservableProperty]
    private bool _isMigrationBusy;

    [ObservableProperty]
    private string _migrationStatus = "可导入 .eml/.mbox，或把当前账户导出为 mbox。";

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

    public bool IsStartupSection => SettingsSectionIndex == 6;

    public bool IsToolsSection => SettingsSectionIndex == 7;

    public bool IsTemplatesSection => SettingsSectionIndex == 8;

    public bool IsTagsSection => SettingsSectionIndex == 9;

    public bool IsAccountListVisible => IsAccountSection && !ShowAddAccountForm;

    public bool IsAccountSignatureVisible => IsAccountListVisible && HasAccounts;

    public bool IsAddAccountFormVisible => IsAccountSection && ShowAddAccountForm;

    public bool IsAboutSection => SettingsSectionIndex == 10;

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
        OnPropertyChanged(nameof(IsAccountSignatureVisible));
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
        OnPropertyChanged(nameof(IsStartupSection));
        OnPropertyChanged(nameof(IsToolsSection));
        OnPropertyChanged(nameof(IsTemplatesSection));
        OnPropertyChanged(nameof(IsTagsSection));
        OnPropertyChanged(nameof(IsAccountListVisible));
        OnPropertyChanged(nameof(IsAccountSignatureVisible));
        OnPropertyChanged(nameof(IsAddAccountFormVisible));
        OnPropertyChanged(nameof(IsAboutSection));

        if (value == 3)
        {
            _ = RefreshAccountHealthAsync();
        }

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

    public bool IsCustomBackgroundSelected
    {
        get
        {
            var active = BackgroundColorHex.Trim();
            return active.Length > 0
                && !BackgroundPresets.Any(p => !string.IsNullOrEmpty(p.Value)
                    && string.Equals(p.Value, active, StringComparison.OrdinalIgnoreCase));
        }
    }

    [ObservableProperty]
    private bool _notificationsEnabled = true;

    [ObservableProperty]
    private bool _notificationSoundEnabled = true;

    [ObservableProperty]
    private string _notificationSound = "default";

    /// <summary>通知里是否显示邮件主题。</summary>
    [ObservableProperty]
    private bool _notificationShowSubject = true;

    /// <summary>通知里是否附带正文摘要。</summary>
    [ObservableProperty]
    private bool _notificationShowPreview;

    /// <summary>同一批多封邮件合并成一条通知。</summary>
    [ObservableProperty]
    private bool _notificationMerge = true;

    /// <summary>只给收件箱弹通知。</summary>
    [ObservableProperty]
    private bool _notificationInboxOnly = true;

    /// <summary>免打扰时段总开关。</summary>
    [ObservableProperty]
    private bool _notificationQuietEnabled;

    /// <summary>免打扰开始时间，"HH:mm"。</summary>
    [ObservableProperty]
    private string _notificationQuietStart = "22:00";

    /// <summary>免打扰结束时间，"HH:mm"。</summary>
    [ObservableProperty]
    private string _notificationQuietEnd = "08:00";

    [ObservableProperty]
    private int _syncIntervalIndex;

    /// <summary>延迟发送的下标：0=立即，1=5 秒，2=10 秒，3=30 秒。</summary>
    [ObservableProperty]
    private int _sendDelayIndex = 2;

    [ObservableProperty]
    private bool _markReadOnOpen = true;

    [ObservableProperty]
    private bool _runAtStartup;

    [ObservableProperty]
    private bool _showRemoteImages;

    [ObservableProperty]
    private bool _saveSentCopy = true;

    [ObservableProperty]
    private string _signatureText = string.Empty;

    [ObservableProperty]
    private int _renderModeIndex;

    /// <summary>Windows 始终有 WebView2；Linux 需要系统 WebKitGTK/WPE，缺少时退回纯文本。</summary>
    public bool IsHtmlRenderAvailable => HtmlRendering.IsAvailable;

    [ObservableProperty]
    private bool _hasAccountHealth;

    [ObservableProperty]
    private string _accountHealthStatus = string.Empty;

    [ObservableProperty]
    private string _diagnosticsStatus = string.Empty;

    [ObservableProperty]
    private bool _isExportingDiagnostics;

    [ObservableProperty]
    private string _cliDownloadStatus = string.Empty;

    public string CliDownloadDirectory => AppContext.BaseDirectory;

    public string CliDownloadBundlePath => CliBundle.DownloadPath(AppContext.BaseDirectory, AppVersion);

    public bool HasCliDownload
        => File.Exists(Path.Combine(AppContext.BaseDirectory, ProductPaths.CliExecutableName))
            || File.Exists(CliDownloadBundlePath);

    /// <summary>工具页直接展示给用户复制的 CLI 说明文本。</summary>
    public string CliAgentInfo => BuildCliAgentInfo();

    public string CliUsageLogDisplayPath
        => Path.Combine(ProductPaths.DisplayDataDirectory, "logs", "cli-usage.log");

    public string CliCallsLogDisplayPath
        => Path.Combine(ProductPaths.DisplayDataDirectory, "logs", "cli-calls.jsonl");

    [ObservableProperty]
    private string _cliCopyStatus = string.Empty;

    [ObservableProperty]
    private bool _isDownloadingCli;

    /// <summary>Linux 的托盘显示取决于桌面环境，关于页保留“连后台一起退出”的可靠入口。</summary>
    public bool IsLinuxPlatform => OperatingSystem.IsLinux();

    public bool IsWindowsPlatform => OperatingSystem.IsWindows();

    [ObservableProperty]
    private string _backgroundExitStatus = string.Empty;

    [ObservableProperty]
    private bool _isBackgroundExiting;

    public bool HasBackgroundExitStatus => !string.IsNullOrWhiteSpace(BackgroundExitStatus);

    partial void OnBackgroundExitStatusChanged(string value)
        => OnPropertyChanged(nameof(HasBackgroundExitStatus));

    /// <summary>Windows 10 1809+ 支持亚克力，Windows 11 22H2+ 才支持云母。</summary>
    public bool IsWindowsMaterialAvailable =>
        OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= 17763;

    public bool IsMicaAvailable =>
        OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= 22000;

    public bool IsAcrylicAvailable => IsWindowsMaterialAvailable;

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
        RefreshBackgroundPresets();
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

    partial void OnNotificationShowSubjectChanged(bool value)
    {
        QueueSettingsSave();
    }

    partial void OnNotificationShowPreviewChanged(bool value)
    {
        QueueSettingsSave();
    }

    partial void OnNotificationMergeChanged(bool value)
    {
        QueueSettingsSave();
    }

    partial void OnNotificationInboxOnlyChanged(bool value)
    {
        QueueSettingsSave();
    }

    partial void OnNotificationQuietEnabledChanged(bool value)
    {
        QueueSettingsSave();
    }

    partial void OnNotificationQuietStartChanged(string value)
    {
        QueueSettingsSave();
    }

    partial void OnNotificationQuietEndChanged(string value)
    {
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

    partial void OnSendDelayIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSendDelayNone));
        OnPropertyChanged(nameof(IsSendDelay5Seconds));
        OnPropertyChanged(nameof(IsSendDelay10Seconds));
        OnPropertyChanged(nameof(IsSendDelay30Seconds));
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

    partial void OnSignatureTextChanged(string value)
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

    public bool IsSendDelayNone
    {
        get => SendDelayIndex == 0;
        set
        {
            if (value)
            {
                SendDelayIndex = 0;
            }
        }
    }

    public bool IsSendDelay5Seconds
    {
        get => SendDelayIndex == 1;
        set
        {
            if (value)
            {
                SendDelayIndex = 1;
            }
        }
    }

    public bool IsSendDelay10Seconds
    {
        get => SendDelayIndex == 2;
        set
        {
            if (value)
            {
                SendDelayIndex = 2;
            }
        }
    }

    public bool IsSendDelay30Seconds
    {
        get => SendDelayIndex == 3;
        set
        {
            if (value)
            {
                SendDelayIndex = 3;
            }
        }
    }

    public ObservableCollection<RuleViewModel> Rules { get; } = [];

    public ObservableCollection<RuleAccountOption> RuleAccounts { get; } = [];

    public ObservableCollection<FolderViewModel> RuleTargetFolders { get; } = [];

    [ObservableProperty]
    private RuleAccountOption? _selectedRuleAccount;

    [ObservableProperty]
    private FolderViewModel? _newRuleTargetFolder;

    partial void OnSelectedRuleAccountChanged(RuleAccountOption? value)
    {
        RuleTargetFolders.Clear();
        NewRuleTargetFolder = null;

        if (value?.Id is not { } accountId)
        {
            return;
        }

        foreach (var folder in Folders.Where(folder =>
                     string.Equals(folder.AccountId, accountId, StringComparison.Ordinal)
                     && folder.Role is not (FolderRole.Sent or FolderRole.Drafts)))
        {
            RuleTargetFolders.Add(folder);
        }
    }

    [ObservableProperty]
    private string _newRulePattern = string.Empty;

    [ObservableProperty]
    private int _newRuleFieldIndex;

    [ObservableProperty]
    private bool _newRuleMarkRead = true;

    [ObservableProperty]
    private bool _newRuleFlag;

    [ObservableProperty]
    private bool _newRuleMute;

    [ObservableProperty]
    private bool _newRuleMoveToJunk;

    [ObservableProperty]
    private string? _ruleHint;

    public bool HasRules => Rules.Count > 0;

    public bool HasNoRules => Rules.Count == 0;

    public bool HasRuleHint => !string.IsNullOrEmpty(RuleHint);

    public bool IsRuleFieldFrom
    {
        get => NewRuleFieldIndex == 0;
        set
        {
            if (value)
            {
                NewRuleFieldIndex = 0;
            }
        }
    }

    public bool IsRuleFieldSubject
    {
        get => NewRuleFieldIndex == 1;
        set
        {
            if (value)
            {
                NewRuleFieldIndex = 1;
            }
        }
    }

    public bool IsRuleFieldAny
    {
        get => NewRuleFieldIndex == 2;
        set
        {
            if (value)
            {
                NewRuleFieldIndex = 2;
            }
        }
    }

    partial void OnNewRuleFieldIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsRuleFieldFrom));
        OnPropertyChanged(nameof(IsRuleFieldSubject));
        OnPropertyChanged(nameof(IsRuleFieldAny));
    }

    partial void OnRuleHintChanged(string? value)
    {
        OnPropertyChanged(nameof(HasRuleHint));
    }

    [RelayCommand]
    private async Task AddRuleAsync()
    {
        var pattern = NewRulePattern.Trim();

        if (pattern.Length == 0)
        {
            RuleHint = "请输入要匹配的关键词";
            return;
        }

        if (!NewRuleMarkRead && !NewRuleFlag && !NewRuleMute && !NewRuleMoveToJunk && NewRuleTargetFolder is null)
        {
            RuleHint = "至少选择一个动作";
            return;
        }

        if (NewRuleTargetFolder is not null && SelectedRuleAccount?.Id is null)
        {
            RuleHint = "移动到文件夹的规则需要先选择具体账号";
            return;
        }

        var rule = new RuleDto
        {
            AccountId = SelectedRuleAccount?.Id,
            Field = NewRuleFieldIndex switch
            {
                1 => "subject",
                2 => "any",
                _ => "from"
            },
            Pattern = pattern,
            MarkRead = NewRuleMarkRead,
            Flag = NewRuleFlag,
            Mute = NewRuleMute,
            MoveToJunk = NewRuleMoveToJunk,
            TargetFolderId = NewRuleTargetFolder?.Id,
            Enabled = true
        };

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.SaveRule,
                new SaveRuleRequest { Rule = rule });

            NewRulePattern = string.Empty;
            NewRuleMarkRead = true;
            NewRuleFlag = false;
            NewRuleMute = false;
            NewRuleMoveToJunk = false;
            NewRuleTargetFolder = null;
            RuleHint = "规则已保存，新的来信会立即生效";
            await LoadRulesAsync();
        }
        catch (Exception exception)
        {
            RuleHint = $"保存规则失败：{exception.Message}";
        }
    }

    private async Task SaveRuleAsync(RuleViewModel item)
    {
        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.SaveRule,
                new SaveRuleRequest { Rule = item.ToDto() });
        }
        catch (Exception exception)
        {
            RuleHint = $"更新规则失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task PreviewRuleAsync(RuleViewModel? rule)
    {
        if (rule is null)
        {
            return;
        }

        try
        {
            var response = await _engine.RequestAsync<RuleMatchPreviewResponse>(
                IpcMessageTypes.PreviewRuleMatches,
                new PreviewRuleMatchesRequest { RuleId = rule.RuleId });

            var preview = response.Preview;
            if (preview.Matched == 0)
            {
                RuleHint = "这条规则在当前邮件里没有命中。";
                return;
            }

            var samples = preview.Samples
                .Select((sample, index) => $"{index + 1}. {sample.Date.ToLocalTime():MM-dd} {sample.Subject}")
                .ToArray();
            RuleHint = $"命中 {preview.Matched} 封（最多预览 {preview.ScanLimit} 封）"
                + (preview.TargetFolderName is null ? string.Empty : $"，目标：{preview.TargetFolderName}")
                + "：" + string.Join("；", samples);
        }
        catch (Exception exception)
        {
            RuleHint = $"预览失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ApplyRuleHistoryAsync(RuleViewModel? rule)
    {
        if (rule is null)
        {
            return;
        }

        try
        {
            var response = await _engine.RequestAsync<RuleHistoryApplyResponse>(
                IpcMessageTypes.ApplyRuleHistory,
                new ApplyRuleHistoryRequest { RuleId = rule.RuleId, Limit = 200 },
                TimeSpan.FromSeconds(30));
            RuleHint = response.Result.Message;
            await LoadRulesAsync();
        }
        catch (Exception exception)
        {
            RuleHint = $"回溯失败：{exception.Message}";
        }
    }

    private async Task DeleteRuleAsync(RuleViewModel item)
    {
        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.DeleteRule,
                new DeleteRuleRequest { Id = item.RuleId });

            Rules.Remove(item);
            NotifyRulesChanged();
            RuleHint = "规则已删除";
        }
        catch (Exception exception)
        {
            RuleHint = $"删除规则失败：{exception.Message}";
        }
    }

    /// <summary>规则集合变化后刷新空态与计数。</summary>
    private void NotifyRulesChanged()
    {
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(HasNoRules));
    }

    private async Task LoadRulesAsync()
    {
        if (Accounts.Count > 0)
        {
            var previous = SelectedRuleAccount?.Id;
            RuleAccounts.Clear();
            RuleAccounts.Add(new RuleAccountOption(null, "全部账号"));

            foreach (var account in Accounts)
            {
                RuleAccounts.Add(new RuleAccountOption(account.Id, account.Email));
            }

            SelectedRuleAccount = RuleAccounts.FirstOrDefault(o => o.Id == previous) ?? RuleAccounts[0];
        }

        RulesResponse response;

        try
        {
            response = await _engine.RequestAsync<RulesResponse>(
                IpcMessageTypes.GetRules,
                new GetRulesRequest());
        }
        catch (Exception exception)
        {
            RuleHint = $"读取规则失败：{exception.Message}";
            return;
        }

        Rules.Clear();

        foreach (var rule in response.Items)
        {
            var folderName = rule.TargetFolderId is { } folderId
                ? _foldersById.TryGetValue(folderId, out var folder) ? folder.Name : $"#{folderId}"
                : null;
            Rules.Add(new RuleViewModel(rule, SaveRuleAsync, DeleteRuleAsync, folderName));
        }

        NotifyRulesChanged();
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
        RebuildBatchMoveTargets();
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
        UpdateService.CleanupDownloadedInstallers();
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
            _ = RefreshTagsForMessagesAsync(response.Items.Select(item => item.Id).ToArray());
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
        if (SelectedMessage is not null)
        {
            await ToggleReadMessageAsync(SelectedMessage);
        }
    }

    /// <summary>稍后提醒：把选中邮件收起来，到点后回到未读。</summary>
    [RelayCommand]
    private async Task SnoozeSelectedAsync(string? preset)
    {
        if (SelectedMessage is null)
        {
            return;
        }

        var until = ResolveSnoozeTime(preset);
        if (until is null)
        {
            StatusMessage = "稍后提醒时间无效。";
            return;
        }

        await SnoozeMessageAsync(SelectedMessage, until.Value);
    }

    public async Task SnoozeMessageAsync(MailSummaryViewModel message, DateTimeOffset until)
    {
        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.SnoozeMessage,
                new SnoozeMessageRequest { MessageId = message.Id, UntilUtc = until });

            RemoveMessageFromView(message);
            await ReloadCurrentFolderAsync();
            StatusMessage = $"已稍后提醒，{until.LocalDateTime:M月d日 HH:mm} 回到未读。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private static DateTimeOffset? ResolveSnoozeTime(string? preset)
    {
        var now = DateTimeOffset.Now;

        return preset switch
        {
            "1h" => now.AddHours(1),
            "4h" => now.AddHours(4),
            "tonight" => new DateTimeOffset(now.Year, now.Month, now.Day, 20, 0, 0, now.Offset)
                .AddDays(now.Hour >= 20 ? 1 : 0),
            "tomorrow" => new DateTimeOffset(now.Year, now.Month, now.Day, 8, 0, 0, now.Offset)
                .AddDays(1),
            "weekend" => NextSaturdayMorning(now),
            _ => null
        };
    }

    private static DateTimeOffset NextSaturdayMorning(DateTimeOffset now)
    {
        var days = ((int)DayOfWeek.Saturday - (int)now.DayOfWeek + 7) % 7;
        var date = new DateTimeOffset(now.Year, now.Month, now.Day, 9, 0, 0, now.Offset)
            .AddDays(days == 0 ? 7 : days);
        return date;
    }

    /// <summary>按 List-Unsubscribe 退订；有 RFC 8058 一键退订时直接 POST，否则打开网页 / mailto。</summary>
    [RelayCommand]
    private async Task UnsubscribeSelectedAsync()
    {
        if (SelectedMessage is null)
        {
            return;
        }

        try
        {
            var response = await _engine.RequestAsync<UnsubscribeInfoResponse>(
                IpcMessageTypes.GetUnsubscribeInfo,
                new GetUnsubscribeInfoRequest { MessageId = SelectedMessage.Id },
                TimeSpan.FromSeconds(60));

            var info = response.Info;
            if (!info.Available)
            {
                StatusMessage = info.Error ?? "这封邮件没有提供退订方式。";
                return;
            }

            if (info.OneClickPost && info.Url is { } postUrl)
            {
                using var content = new StringContent(
                    "List-Unsubscribe=One-Click",
                    Encoding.UTF8,
                    "application/x-www-form-urlencoded");
                using var result = await UnsubscribeClient.PostAsync(postUrl, content);
                StatusMessage = result.IsSuccessStatusCode
                    ? "已发送一键退订请求。"
                    : $"退订服务返回 {result.StatusCode}；可以再打开网页退订。";
                return;
            }

            var target = info.Url ?? info.MailTo;
            if (target is null)
            {
                StatusMessage = "这封邮件的退订地址不可用。";
                return;
            }

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            StatusMessage = "已打开退订页面，请在浏览器里完成确认。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    /// <summary>标记垃圾邮件，并把发件人加入本地拦截规则（之后的新邮件直接进垃圾箱）。</summary>
    [RelayCommand]
    private async Task MarkJunkAndBlockSenderAsync()
    {
        if (SelectedMessage is null)
        {
            return;
        }

        var message = SelectedMessage;
        var junk = Folders.FirstOrDefault(folder =>
            string.Equals(folder.AccountId, message.AccountId, StringComparison.Ordinal)
            && folder.Role == FolderRole.Junk);

        try
        {
            if (junk is not null && message.FolderId != junk.Id)
            {
                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.MoveMessages,
                    new MoveMessagesRequest { MessageIds = [message.Id], TargetFolderId = junk.Id });
            }

            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.SaveRule,
                new SaveRuleRequest
                {
                    Rule = new RuleDto
                    {
                        AccountId = message.AccountId,
                        Field = "from",
                        Pattern = message.SenderAddress,
                        MoveToJunk = true,
                        Mute = true,
                        Enabled = true
                    }
                });

            await LoadRulesAsync();
            RemoveMessageFromView(message);
            await ReloadCurrentFolderAsync();
            StatusMessage = junk is null
                ? $"已记住发件人 {message.SenderAddress}；该账户没有垃圾文件夹，后续只静音通知。"
                : $"已标记垃圾邮件，并记住发件人 {message.SenderAddress}。";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    /// <summary>切换指定邮件的已读状态；列表行快捷操作与工具栏共用。</summary>
    public async Task ToggleReadMessageAsync(MailSummaryViewModel message)
        => await MarkReadAsync(message, !message.IsRead);

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
        if (SelectedMessage is not null)
        {
            await DeleteMessageAsync(SelectedMessage);
        }
    }

    /// <summary>删除指定邮件；列表行悬停快捷操作也走这里。</summary>
    public async Task DeleteMessageAsync(MailSummaryViewModel message)
    {
        var permanent = _foldersById.GetValueOrDefault(message.FolderId)?.Role == FolderRole.Trash;
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
        if (SelectedMessage is not null)
        {
            await ArchiveMessageAsync(SelectedMessage);
        }
    }

    /// <summary>把指定邮件移到同账户归档；列表行快捷操作与工具栏共用。</summary>
    public async Task ArchiveMessageAsync(MailSummaryViewModel message)
    {
        var source = _foldersById.GetValueOrDefault(message.FolderId);
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

        await MoveMessageToFolderAsync(message, archive.Id);
    }

    [RelayCommand]
    private async Task MoveSelectedToFolderAsync(long targetFolderId)
    {
        if (SelectedMessage is not null)
        {
            await MoveMessageToFolderAsync(SelectedMessage, targetFolderId);
        }
    }

    /// <summary>把指定邮件移动到同账户文件夹；列表行与阅读区共用。</summary>
    public async Task MoveMessageToFolderAsync(MailSummaryViewModel message, long targetFolderId)
    {
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
        RemoveMessagesFromView([message]);
    }

    private void RemoveMessagesFromView(IEnumerable<MailSummaryViewModel> messages)
    {
        var removed = messages.ToArray();
        if (removed.Length == 0)
        {
            return;
        }

        foreach (var message in removed)
        {
            _allMessages.Remove(message);
            Messages.Remove(message);
            message.IsSelected = false;
        }

        if (SelectedMessage is { } selected && removed.Contains(selected))
        {
            SelectedMessage = null;
            _currentBody = null;
            Reader.Reset();
        }

        ApplyFilter();
        NotifySelectionState();
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
        RebuildBatchMoveTargets();
    }

    private void RebuildBatchMoveTargets()
    {
        BatchMoveTargets.Clear();

        var checkedMessages = Messages.Where(message => message.IsSelected).ToArray();
        var accountIds = checkedMessages.Select(message => message.AccountId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (accountIds.Length == 1)
        {
            var sourceFolderIds = checkedMessages.Select(message => message.FolderId).ToHashSet();
            var accountId = accountIds[0];
            var targets = Folders
                .Where(folder => string.Equals(folder.AccountId, accountId, StringComparison.Ordinal)
                                 && folder.Role != FolderRole.Drafts
                                 && !sourceFolderIds.Contains(folder.Id))
                .OrderBy(folder => folder.Role == FolderRole.Other ? 1 : 0)
                .ThenBy(folder => folder.Name, StringComparer.CurrentCulture);

            foreach (var folder in targets)
            {
                BatchMoveTargets.Add(new MoveTargetViewModel(folder, BatchMoveToFolderCommand));
            }
        }

        OnPropertyChanged(nameof(HasBatchMoveTargets));
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
                SuggestedFileName = AttachmentFileNames.Sanitize(attachment.FileName),
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

    private static string AttachmentCacheDirectory()
        => Path.Combine(ProductPaths.DataDirectory, "attachments");

    /// <summary>批量保存到用户选择的文件夹；同名附件自动加序号，不覆盖已有文件。</summary>
    private async Task SaveAttachmentsAsync(IReadOnlyList<AttachmentViewModel> attachments)
    {
        if (!IsConnected || attachments.Count == 0)
        {
            return;
        }

        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        if (window is null)
        {
            return;
        }

        try
        {
            var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "保存附件",
                AllowMultiple = false
            });
            var directory = folders.Count == 0 ? null : folders[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var saved = 0;
            var errors = new List<string>();
            foreach (var attachment in attachments)
            {
                var path = AttachmentFileNames.CreateUniquePath(directory, attachment.FileName, reserved);
                try
                {
                    await _engine.RequestAsync<AttachmentSavedResponse>(
                        IpcMessageTypes.DownloadAttachment,
                        new DownloadAttachmentRequest
                        {
                            MessageId = attachment.MessageId,
                            Index = attachment.Index,
                            DestinationPath = path
                        },
                        TimeSpan.FromSeconds(120));
                    saved++;
                }
                catch (Exception exception)
                {
                    errors.Add($"{attachment.FileName}: {GetErrorMessage(exception)}");
                }
            }

            if (saved == attachments.Count)
            {
                HasError = false;
                StatusMessage = $"已保存 {saved} 个附件到 {directory}。";
                return;
            }

            HasError = true;
            var detail = errors.Count == 0 ? string.Empty : $"（{errors[0]}）";
            StatusMessage = $"已保存 {saved}/{attachments.Count} 个附件{detail}";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    /// <summary>拖出附件：先落到固定的本地缓存目录，再交给拖放层生成文件对象。</summary>
    public async Task<IReadOnlyList<string>> PrepareAttachmentDragFilesAsync(
        IReadOnlyList<AttachmentViewModel> attachments)
    {
        if (!IsConnected || attachments.Count == 0)
        {
            return [];
        }

        var directory = AttachmentCacheDirectory();
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        var errors = new List<string>();
        foreach (var attachment in attachments)
        {
            var path = Path.Combine(
                directory,
                AttachmentFileNames.Sanitize($"{attachment.MessageId}-{attachment.Index}-{attachment.FileName}"));
            try
            {
                await _engine.RequestAsync<AttachmentSavedResponse>(
                    IpcMessageTypes.DownloadAttachment,
                    new DownloadAttachmentRequest
                    {
                        MessageId = attachment.MessageId,
                        Index = attachment.Index,
                        DestinationPath = path
                    },
                    TimeSpan.FromSeconds(120));
                paths.Add(path);
            }
            catch (Exception exception)
            {
                errors.Add($"{attachment.FileName}: {GetErrorMessage(exception)}");
            }
        }

        AttachmentCacheQuota.Enforce(directory, AttachmentCacheQuota.DefaultLimitBytes, paths);

        if (paths.Count == 0)
        {
            HasError = true;
            StatusMessage = errors.Count == 0
                ? "没有可拖出的附件。"
                : $"附件准备失败：{errors[0]}";
        }
        else
        {
            HasError = false;
            StatusMessage = $"已准备 {paths.Count} 个附件，可拖到资源管理器。";
        }

        return paths;
    }

    private Task OpenAttachmentAsync(AttachmentViewModel attachment)
    {
        if (!IsConnected)
        {
            return Task.CompletedTask;
        }

        return OpenAttachmentCoreAsync(attachment);
    }

    private async Task OpenAttachmentCoreAsync(AttachmentViewModel attachment)
    {
        try
        {
            var directory = AttachmentCacheDirectory();
            Directory.CreateDirectory(directory);

            var path = Path.Combine(
                directory,
                AttachmentFileNames.Sanitize($"{attachment.MessageId}-{attachment.Index}-{attachment.FileName}"));

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

    [RelayCommand]
    private async Task BatchArchiveAsync()
    {
        var targets = CheckedTargets();
        if (targets.Length == 0)
        {
            return;
        }

        IsBatchBusy = true;
        var moved = new List<MailSummaryViewModel>();
        var missing = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var group in targets.GroupBy(message => message.AccountId))
            {
                var source = _foldersById.GetValueOrDefault(group.First().FolderId);
                var archive = Folders.FirstOrDefault(folder =>
                    string.Equals(folder.AccountId, group.Key, StringComparison.Ordinal)
                    && folder.Role == FolderRole.Archive
                    && folder.Id != source?.Id);

                if (archive is null)
                {
                    missing.Add(group.Key);
                    continue;
                }

                var batch = group.Where(message => message.FolderId != archive.Id).ToArray();
                if (batch.Length == 0)
                {
                    continue;
                }

                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.MoveMessages,
                    new MoveMessagesRequest
                    {
                        MessageIds = batch.Select(message => message.Id).ToArray(),
                        TargetFolderId = archive.Id
                    });

                moved.AddRange(batch);
            }

            if (moved.Count > 0)
            {
                RemoveMessagesFromView(moved);
                await ReloadCurrentFolderAsync();
            }

            StatusMessage = moved.Count == 0
                ? "没有可归档的邮件：所选邮件已在归档，或账户没有归档文件夹。"
                : missing.Count == 0
                    ? $"已归档 {moved.Count} 封邮件。"
                    : $"已归档 {moved.Count} 封邮件；{missing.Count} 个账户没有归档文件夹，已跳过。";
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

    [RelayCommand]
    private async Task BatchMarkJunkAsync()
    {
        var targets = CheckedTargets();
        if (targets.Length == 0)
        {
            return;
        }

        IsBatchBusy = true;
        var moved = new List<MailSummaryViewModel>();
        var missing = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var group in targets.GroupBy(message => message.AccountId))
            {
                var junk = Folders.FirstOrDefault(folder =>
                    string.Equals(folder.AccountId, group.Key, StringComparison.Ordinal)
                    && folder.Role == FolderRole.Junk);

                if (junk is null)
                {
                    missing.Add(group.Key);
                    continue;
                }

                var batch = group.Where(message => message.FolderId != junk.Id).ToArray();
                if (batch.Length == 0)
                {
                    continue;
                }

                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.MoveMessages,
                    new MoveMessagesRequest
                    {
                        MessageIds = batch.Select(message => message.Id).ToArray(),
                        TargetFolderId = junk.Id
                    });

                moved.AddRange(batch);
            }

            if (moved.Count > 0)
            {
                RemoveMessagesFromView(moved);
                await ReloadCurrentFolderAsync();
            }

            StatusMessage = moved.Count == 0
                ? "没有可标记的邮件：所选邮件已在垃圾邮件，或账户没有垃圾邮件文件夹。"
                : missing.Count == 0
                    ? $"已将 {moved.Count} 封邮件标记为垃圾邮件。"
                    : $"已标记 {moved.Count} 封邮件；{missing.Count} 个账户没有垃圾邮件文件夹，已跳过。";
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

    [RelayCommand]
    private async Task BatchDeleteAsync()
    {
        var targets = CheckedTargets();
        if (targets.Length == 0)
        {
            return;
        }

        IsBatchBusy = true;

        try
        {
            foreach (var group in targets.GroupBy(message =>
                         _foldersById.GetValueOrDefault(message.FolderId)?.Role == FolderRole.Trash))
            {
                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.DeleteMessages,
                    new DeleteMessagesRequest
                    {
                        MessageIds = group.Select(message => message.Id).ToArray(),
                        Permanent = group.Key
                    });
            }

            var permanentCount = targets.Count(message =>
                _foldersById.GetValueOrDefault(message.FolderId)?.Role == FolderRole.Trash);
            RemoveMessagesFromView(targets);
            await ReloadCurrentFolderAsync();
            StatusMessage = permanentCount == targets.Length
                ? $"已永久删除 {targets.Length} 封邮件。"
                : $"已删除 {targets.Length} 封邮件（{permanentCount} 封来自垃圾箱，已永久删除）。";
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

    [RelayCommand]
    private async Task BatchMoveToFolderAsync(long targetFolderId)
    {
        var targets = CheckedTargets();
        if (targets.Length == 0)
        {
            return;
        }

        var accountIds = targets.Select(message => message.AccountId).Distinct(StringComparer.Ordinal).ToArray();
        if (accountIds.Length != 1)
        {
            StatusMessage = "批量移动只支持同一个账户的邮件；跨账户请分开选择。";
            return;
        }

        var target = Folders.FirstOrDefault(folder => folder.Id == targetFolderId
            && string.Equals(folder.AccountId, accountIds[0], StringComparison.Ordinal)
            && folder.Role != FolderRole.Drafts);
        if (target is null)
        {
            StatusMessage = "只能把邮件移动到同一账户的文件夹。";
            return;
        }

        var batchMessages = targets.Where(message => message.FolderId != target.Id).ToArray();
        if (batchMessages.Length == 0)
        {
            StatusMessage = "所选邮件已经在目标文件夹。";
            return;
        }

        IsBatchBusy = true;

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.MoveMessages,
                new MoveMessagesRequest
                {
                    MessageIds = batchMessages.Select(message => message.Id).ToArray(),
                    TargetFolderId = target.Id
                });

            RemoveMessagesFromView(batchMessages);
            await ReloadCurrentFolderAsync();
            StatusMessage = $"已把 {batchMessages.Length} 封邮件移动到“{target.Name}”。";
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

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        if (IsExportingDiagnostics)
        {
            return;
        }

        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;

        if (window is null)
        {
            return;
        }

        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 MailDog 诊断包",
            SuggestedFileName = $"MailDog-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            ShowOverwritePrompt = true
        });

        var path = file?.TryGetLocalPath();

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        IsExportingDiagnostics = true;
        DiagnosticsStatus = "正在整理诊断包…";

        try
        {
            AccountHealthDto[] health = [];

            if (_engine.IsConnected)
            {
                try
                {
                    health = (await _engine.RequestAsync<AccountHealthResponse>(
                        IpcMessageTypes.GetAccountHealth,
                        new GetAccountHealthRequest()).ConfigureAwait(true)).Items;
                }
                catch
                {
                    // 健康快照取不到时仍然导出日志与配置。
                }
            }

            var accounts = Accounts
                .Select(account => new DiagnosticsExporter.AccountSnapshot(
                    account.Id,
                    account.Email,
                    account.DisplayName,
                    account.StatusText))
                .ToArray();

            var settings = BuildSettings();

            await Task.Run(() => DiagnosticsExporter.Export(
                path,
                AppVersion,
                settings,
                accounts,
                health)).ConfigureAwait(true);

            DiagnosticsStatus = $"已导出 {Path.GetFileName(path)}";
        }
        catch (Exception exception)
        {
            DiagnosticsStatus = $"导出失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsExportingDiagnostics = false;
        }
    }

    [RelayCommand]
    private async Task DownloadCliAsync()
    {
        if (IsDownloadingCli)
        {
            return;
        }

        var bundle = CliBundle.Find();

        if (bundle is null)
        {
            CliDownloadStatus = "当前构建没有附带 CLI 包，请使用发布版安装包。";
            return;
        }

        IsDownloadingCli = true;
        CliDownloadStatus = "正在下载并解压到安装目录…";

        var destination = CliBundle.DownloadPath(AppContext.BaseDirectory, AppVersion);

        try
        {
            await Task.Run(() => File.Copy(bundle, destination, true)).ConfigureAwait(true);

            try
            {
                await Task.Run(() => CliBundle.Extract(destination, AppContext.BaseDirectory)).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                CliDownloadStatus = $"已下载，但解压失败：{GetErrorMessage(exception)}；压缩包保留在 {destination}";
                OnPropertyChanged(nameof(HasCliDownload));
                OnPropertyChanged(nameof(CliAgentInfo));
                return;
            }

            try
            {
                File.Delete(destination);
            }
            catch
            {
                // 解压成功后压缩包删不掉就留着；下次下载会覆盖。
            }

            CliDownloadStatus = $"已解压到安装目录：{ProductPaths.CliExecutableName}、{(OperatingSystem.IsWindows() ? "maildog.cmd" : "maildog")}、maildog-cli/";
            CliCopyStatus = string.Empty;
            OnPropertyChanged(nameof(HasCliDownload));
            OnPropertyChanged(nameof(CliAgentInfo));
        }
        catch (Exception exception)
        {
            CliDownloadStatus = $"下载失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsDownloadingCli = false;
        }
    }

    /// <summary>给外部智能体复制一段可直接使用 CLI 的说明；不包含账号、口令或邮件内容。</summary>
    public string BuildCliAgentInfo()
    {
        var directory = AppContext.BaseDirectory;
        var bundle = CliDownloadBundlePath;
        var cli = Path.Combine(directory, ProductPaths.CliExecutableName);
        var shim = Path.Combine(directory, OperatingSystem.IsWindows() ? "maildog.cmd" : "maildog");
        var skill = Path.Combine(directory, "maildog-cli", "SKILL.md");
        var installed = File.Exists(cli) && File.Exists(shim) && File.Exists(skill);
        var hasBundle = File.Exists(bundle);
        var quote = "\"";
        var lines = new List<string>
        {
            $"MailDog CLI（版本 {AppVersion}）",
            $"安装目录：{directory}"
        };

        if (hasBundle)
        {
            lines.Add($"下载包：{bundle}");
        }

        if (installed)
        {
            lines.Add("当前文件已解压，可以直接使用。");
        }
        else if (hasBundle)
        {
            lines.Add("当前文件尚未解压，请先把下载包解压到安装目录，保持包内目录结构。");
            lines.Add("解压命令：");
            lines.Add(
                OperatingSystem.IsWindows()
                    ? $"Expand-Archive -LiteralPath {quote}{bundle}{quote} -DestinationPath {quote}{directory}{quote} -Force"
                    : $"tar -xzf {quote}{bundle}{quote} -C {quote}{directory}{quote}");
        }
        else
        {
            lines.Add("当前文件尚未解压，下载包也不在安装目录；请先在工具页重新下载。");
        }

        lines.Add(string.Empty);
        lines.Add($"CLI：{cli}");
        lines.Add($"启动脚本：{shim}");
        lines.Add($"Skill：{skill}");
        lines.Add(string.Empty);
        lines.Add("给智能体的使用约束：");
        lines.Add($"1. 先运行 {quote}{shim}{quote} schema --json 确认命令、参数和字段。");
        lines.Add("2. accounts / folders / list / search / show / attachment 可以直接执行；draft / send / mark / move 默认只预览，等用户明确确认后再加 --confirm；cancel 会立即撤回还没发出的邮件，也要先经用户确认。");
        lines.Add("3. send 返回退出码 6 时，把附件缺失或过大的提醒转述给用户；用户确认后才加 --force-send。");
        lines.Add("4. show 直接返回原始正文：--json 同时给出 text（原始纯文本）与 html（原始 HTML），缺失为 null，CLI 不做 HTML 转文本；默认全文，需要截断时显式加 --max-chars。--html 只是强制人读输出 HTML。");
        lines.Add("5. 不要直接读写 MailDog 的 SQLite 数据库、凭据文件或日志。客户端会在 logs 目录记录调用摘要，带 --json 时还会保存当时的 JSON 输出（可能含邮件正文）；这些记录只供用户在界面查看，智能体不要把日志当成邮件数据源。");
        lines.Add("6. 只引用与当前问题相关的邮件片段，不要把整封邮件或验证码、账单信息倒进对话。");
        lines.Add("7. 安装升级可能清掉安装目录顶层的 CLI；升级后重新下载即可。");

        return string.Join(Environment.NewLine, lines);
    }

    [RelayCommand]
    private async Task RefreshAccountHealthAsync()
    {
        if (!_engine.IsConnected)
        {
            AccountHealthStatus = "后台尚未连接。";
            return;
        }

        try
        {
            var response = await _engine.RequestAsync<AccountHealthResponse>(
                IpcMessageTypes.GetAccountHealth,
                new GetAccountHealthRequest()).ConfigureAwait(true);

            AccountHealth.Clear();

            foreach (var item in response.Items)
            {
                AccountHealth.Add(new AccountHealthViewModel(item));
            }

            HasAccountHealth = AccountHealth.Count > 0;
            AccountHealthStatus = HasAccountHealth ? string.Empty : "还没有账户。";
        }
        catch (Exception exception)
        {
            AccountHealthStatus = $"读取失败：{GetErrorMessage(exception)}";
        }
    }

    /// <summary>托盘“退出 MailDog”：后台没响应也把界面关掉，不留下看不见的窗口。</summary>
    public async Task QuitFromTrayAsync()
    {
        try
        {
            if (_engine.IsConnected)
            {
                await _engine.RequestAsync<OkResponse>(
                    IpcMessageTypes.Shutdown,
                    new ShutdownRequest(),
                    TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            }
        }
        catch
        {
            // 后台已经退出或管道断开时直接关界面。
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>Linux 无托盘时的退出入口：先让 Engine 收尾，再关掉界面。</summary>
    [RelayCommand]
    private async Task QuitBackgroundAsync()
    {
        if (IsBackgroundExiting)
        {
            return;
        }

        IsBackgroundExiting = true;
        BackgroundExitStatus = "正在退出后台…";

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.Shutdown,
                new ShutdownRequest(),
                TimeSpan.FromSeconds(5)).ConfigureAwait(true);

            BackgroundExitStatus = "后台已退出。";
            await Task.Delay(300).ConfigureAwait(true);

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        catch (Exception exception)
        {
            BackgroundExitStatus = $"退出失败：{GetErrorMessage(exception)}";
            IsBackgroundExiting = false;
        }
    }

    [RelayCommand]
    private void OpenAbout()
    {
        SettingsSectionIndex = 10;
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
    private async Task CheckUpdateAsync()
    {
        if (IsUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        HasUpdateAvailable = false;
        UpdateStatusText = "正在检查更新…";

        try
        {
            var result = await _updates.CheckAsync(AppVersion);
            _updateResult = result;

            if (result.Error is { } error)
            {
                // 检查失败只说一句，不弹窗、不反复重试。
                UpdateStatusText = $"检查更新失败：{error}";
                return;
            }

            if (!result.HasUpdate)
            {
                UpdateStatusText = "已是最新版本。";
                return;
            }

            var canInstall = UpdateService.IsInstalledLayout() && result.InstallerUrl is not null;
            UpdateActionText = canInstall ? "下载并安装" : "去下载页";
            UpdateStatusText = OperatingSystem.IsLinux()
                ? result.PortableUrl is not null
                    ? $"发现新版本 {result.LatestVersion}，Linux 包已发布，请下载 tar.gz 解压替换。"
                    : $"发现新版本 {result.LatestVersion}。"
                : $"发现新版本 {result.LatestVersion}。";
            HasUpdateAvailable = true;
        }
        catch (Exception ex)
        {
            UpdateStatusText = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    /// <summary>安装版下载并静默升级；便携版不擅自改成安装版，只把人送去下载页。</summary>
    [RelayCommand]
    private async Task RunUpdateActionAsync()
    {
        if (IsUpdateBusy || _updateResult is not { HasUpdate: true } result)
        {
            return;
        }

        if (!UpdateService.IsInstalledLayout() || result.InstallerUrl is null)
        {
            OpenReleasePage();
            return;
        }

        IsUpdateBusy = true;
        UpdateStatusText = "正在下载安装包…";

        try
        {
            var progress = new Progress<double>(value => UpdateStatusText = $"正在下载安装包… {value:P0}");
            var path = await _updates.DownloadInstallerAsync(result, progress);

            if (path is null)
            {
                UpdateStatusText = "下载或校验失败，请到发布页手动下载。";
                return;
            }

            UpdateStatusText = "正在安装，完成后会自动重新启动…";
            var installed = await _updates.RunInstallerAsync(path);

            if (!installed)
            {
                UpdateStatusText = "安装没有完成，请到发布页手动下载。";
                return;
            }

            // 新版本已经在跑，当前窗口退出即可。
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
        }
        catch
        {
            UpdateStatusText = "更新失败，请到发布页手动下载。";
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateService.ReleasePageUrl)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // 打开浏览器失败不影响关于页。
        }
    }

    [RelayCommand]
    private void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            StatusMessage = $"打开系统设置失败：{GetErrorMessage(exception)}";
        }
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
    private void ToggleThirdPartyNotices()
    {
        IsThirdPartyNoticesExpanded = !IsThirdPartyNoticesExpanded;
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
                new CreateBackupRequest
                {
                    DestinationPath = destinationPath,
                    Password = string.IsNullOrWhiteSpace(BackupPassword) ? null : BackupPassword
                },
                TimeSpan.FromSeconds(120));

            BackupStatus = response.Result.Encrypted
                ? $"加密备份已保存：{response.Result.Path}（{FormatStorageSize(response.Result.Size)}）"
                : $"备份已保存：{response.Result.Path}（{FormatStorageSize(response.Result.Size)}）";
            BackupPassword = string.Empty;
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
                new RestoreBackupRequest
                {
                    ArchivePath = archivePath,
                    Password = string.IsNullOrWhiteSpace(BackupPassword) ? null : BackupPassword
                },
                TimeSpan.FromSeconds(60));

            BackupStatus = response.Result.RestartRequired
                ? "恢复已安排。请从托盘完全退出 MailDog 后重新打开，恢复将在下次启动时生效；当前数据库会先自动保留一份副本。"
                : "恢复已完成。";
            BackupPassword = string.Empty;
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

    /// <summary>把当前邮件或整个会话渲染成适合打印的独立 HTML；正文沿用阅读区的净化流程。</summary>
    public async Task<string?> BuildPrintHtmlAsync(bool includeThread)
    {
        if (SelectedMessage is not { } selected)
        {
            return null;
        }

        var targets = includeThread && selected.ThreadCount > 1
            ? selected.ThreadMessages.OrderBy(message => message.Date).ToArray()
            : [selected];

        var content = new System.Text.StringBuilder(targets.Length * 4096);

        foreach (var message in targets)
        {
            try
            {
                var response = await _engine.RequestAsync<MessageBodyResponse>(
                    IpcMessageTypes.GetMessageBody,
                    new GetMessageBodyRequest { MessageId = message.Id },
                    TimeSpan.FromSeconds(IpcProtocol.BodyRequestTimeoutSeconds));

                var body = response.Body;
                var sanitized = _sanitizer.Sanitize(body.Html, _allowRemoteImages);
                var hasHtml = !string.IsNullOrWhiteSpace(sanitized.Html);

                content.Append("<section class=\"maildog-thread-message\">")
                    .Append("<div class=\"maildog-print-subject\">")
                    .Append(System.Net.WebUtility.HtmlEncode(
                        string.IsNullOrWhiteSpace(body.Subject) ? "（无主题）" : body.Subject))
                    .Append("</div>")
                    .Append("<div class=\"maildog-print-meta\">")
                    .Append(System.Net.WebUtility.HtmlEncode(
                        string.IsNullOrWhiteSpace(body.FromName) ? body.FromAddress : body.FromName))
                    .Append(" &lt;")
                    .Append(System.Net.WebUtility.HtmlEncode(body.FromAddress))
                    .Append("&gt; · ")
                    .Append(System.Net.WebUtility.HtmlEncode(body.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm")))
                    .Append("</div>");

                if (body.Attachments.Length > 0)
                {
                    content.Append("<div class=\"maildog-print-meta\">附件：")
                        .Append(System.Net.WebUtility.HtmlEncode(
                            string.Join("、", body.Attachments.Select(attachment => attachment.FileName))))
                        .Append("</div>");
                }

                content.Append("<div class=\"maildog-print-body\">");
                if (hasHtml)
                {
                    content.Append(sanitized.Html);
                }
                else
                {
                    var plain = string.IsNullOrWhiteSpace(body.PlainText)
                        ? "此邮件没有可显示的正文。"
                        : body.PlainText;
                    content.Append("<pre class=\"maildog-print-plain\">")
                        .Append(System.Net.WebUtility.HtmlEncode(plain))
                        .Append("</pre>");
                }

                content.Append("</div></section>");
            }
            catch (Exception exception)
            {
                content.Append("<section class=\"maildog-thread-message\"><p class=\"maildog-print-error\">")
                    .Append(System.Net.WebUtility.HtmlEncode($"这封邮件正文加载失败：{GetErrorMessage(exception)}"))
                    .Append("</p></section>");
            }
        }

        return _builder.Build(
            content.ToString(),
            darkMode: false,
            allowRemoteImages: _allowRemoteImages,
            bodyBackground: null,
            extraCss: PrintCss);
    }

    private const string PrintCss = """
        @page { size: A4; margin: 12mm; }
        .maildog-thread-message { margin: 0 0 22px; padding-bottom: 16px; border-bottom: 1px solid #dddddd; }
        .maildog-thread-message:last-child { border-bottom: none; }
        .maildog-print-subject { font-size: 18px; font-weight: 600; line-height: 1.35; margin: 0 0 6px; }
        .maildog-print-meta { color: #5c5c5c; font-size: 12px; margin: 0 0 12px; }
        .maildog-print-body { font-size: 14px; }
        .maildog-print-plain { white-space: pre-wrap; background: #f6f6f6; padding: 12px; border-radius: 6px; }
        .maildog-print-error { color: #a4262c; }
        @media print {
          body { padding: 0; background: #ffffff; }
          .maildog-mail { padding: 0; border-radius: 0; }
          a { color: #000000; text-decoration: underline; }
        }
        """;

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
    private Task CompactDatabaseAsync()
        => CleanStorageAsync(new CleanStorageRequest { CompactDatabase = true });

    public async Task ImportMailboxAsync(IReadOnlyList<string> paths)
    {
        if (IsMigrationBusy || paths.Count == 0)
        {
            return;
        }

        var account = SelectedAccount ?? Accounts.FirstOrDefault();
        var inbox = account?.Folders.FirstOrDefault(folder => folder.Role == FolderRole.Inbox)
            ?? account?.Folders.FirstOrDefault();
        if (account is null || inbox is null)
        {
            MigrationStatus = "请先添加账户并同步文件夹。";
            return;
        }

        IsMigrationBusy = true;
        MigrationStatus = $"正在导入 {paths.Count} 个文件…";

        try
        {
            var response = await _engine.RequestAsync<MailboxImportResponse>(
                IpcMessageTypes.ImportMailbox,
                new ImportMailboxRequest
                {
                    AccountId = account.Id,
                    FolderId = inbox.Id,
                    Paths = paths.ToArray()
                },
                TimeSpan.FromMinutes(10));

            var errorText = response.Result.Errors.Length > 0
                ? $" 示例：{string.Join("；", response.Result.Errors.Take(3))}"
                : string.Empty;
            MigrationStatus =
                $"{response.Result.Message}（目标：{account.DisplayName} / {inbox.Name}）{errorText}";
        }
        catch (Exception exception)
        {
            MigrationStatus = $"导入失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsMigrationBusy = false;
        }
    }

    public async Task ExportMailboxAsync(string destinationPath)
    {
        if (IsMigrationBusy)
        {
            return;
        }

        var account = SelectedAccount ?? Accounts.FirstOrDefault();
        if (account is null)
        {
            MigrationStatus = "请先添加账户。";
            return;
        }

        IsMigrationBusy = true;
        MigrationStatus = $"正在导出 {account.DisplayName} 的全部邮件…";

        try
        {
            var response = await _engine.RequestAsync<MailboxExportResponse>(
                IpcMessageTypes.ExportMailbox,
                new ExportMailboxRequest
                {
                    AccountId = account.Id,
                    DestinationPath = destinationPath
                },
                TimeSpan.FromMinutes(30));

            MigrationStatus =
                $"{response.Result.Message} 文件大小 {FormatStorageSize(response.Result.Size)}，"
                + $"路径：{response.Result.Path}";
        }
        catch (Exception exception)
        {
            MigrationStatus = $"导出失败：{GetErrorMessage(exception)}";
        }
        finally
        {
            IsMigrationBusy = false;
        }
    }

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
            else
            {
                StatusMessage = null;
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
            _ = RefreshTagsForMessagesAsync(response.Items.Select(item => item.Id).ToArray());
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
