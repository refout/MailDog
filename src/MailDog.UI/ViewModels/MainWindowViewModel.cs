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
    private AppSettingsDto _settings = new();
    private MailBodyDto? _currentBody;
    private DateTimeOffset? _nextDate;
    private long? _nextId;
    private long? _activeFolderId;
    private FolderRole? _activeRole;
    private string? _activeScope;
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

    public ObservableCollection<MailSummaryViewModel> Messages { get; } = [];

    public ReaderViewModel Reader { get; } = new();

    public string[] ProviderOptions { get; } = ["Outlook", "QQ 邮箱"];

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

    public bool IsAccountSection => SettingsSectionIndex == 4;

    public bool IsAccountListVisible => IsAccountSection && !ShowAddAccountForm;

    public bool IsAddAccountFormVisible => IsAccountSection && ShowAddAccountForm;

    public bool IsAboutSection => SettingsSectionIndex == 5;

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
        }

        OnPropertyChanged(nameof(IsAppearanceSection));
        OnPropertyChanged(nameof(IsNotificationsSection));
        OnPropertyChanged(nameof(IsReadingSection));
        OnPropertyChanged(nameof(IsSyncSection));
        OnPropertyChanged(nameof(IsAccountSection));
        OnPropertyChanged(nameof(IsAccountListVisible));
        OnPropertyChanged(nameof(IsAddAccountFormVisible));
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
    private void ShowImages()
    {
        _allowRemoteImages = true;
        RenderCurrentBody();
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
        ShowAddAccountForm = true;
        SettingsSectionIndex = 4;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void BeginAddAccount() => ShowAddAccountForm = true;

    [RelayCommand]
    private void ShowAccountList()
    {
        ShowAddAccountForm = false;
        AddAccountStatus = null;
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
        _ = LoadMessagesAsync(value);
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
        _ = LoadRoleMessagesAsync(value.Role, value.Name);
    }

    private void UpdateActiveStates()
    {
        foreach (var folder in Folders)
        {
            folder.IsActive = SelectedFolder is not null && folder.Id == SelectedFolder.Id;
        }

        foreach (var folder in UnifiedFolders)
        {
            folder.IsActive = SelectedUnifiedFolder is not null
                              && folder.Role == SelectedUnifiedFolder.Role;
        }
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
                or "warning" or "beep" or "question" or "start"
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
        if (SelectedAccount is null)
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
