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
    private readonly SemaphoreSlim _accountsLoadGate = new(1, 1);
    private int _accountsLoadVersion;
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
    [NotifyPropertyChangedFor(nameof(SearchScopeIndex))]
    private bool _isGlobalSearch;

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public string SearchScopeLabel => IsGlobalSearch ? "全部邮箱" : "当前文件夹";

    public int SearchScopeIndex
    {
        get => IsGlobalSearch ? 1 : 0;
        set
        {
            var isGlobalSearch = value == 1;
            if (IsGlobalSearch != isGlobalSearch)
            {
                IsGlobalSearch = isGlobalSearch;
            }
        }
    }

    public string SearchScopeAutomationName => IsGlobalSearch
        ? "搜索范围：全部邮箱"
        : "搜索范围：当前文件夹";

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

    private CliCallsViewModel? _cliCalls;

    /// <summary>设置页内嵌的 CLI 调用记录；不另开窗口。</summary>
    public CliCallsViewModel CliCalls => _cliCalls ??= new CliCallsViewModel();

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

}
