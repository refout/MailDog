using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
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
    private bool _allowRemoteImages;
    private bool _initialized;
    private bool _suppressSelectionHandlers;
    private bool _disposed;

    public MainWindowViewModel(EngineClient engine)
    {
        _engine = engine;
        _engine.EventReceived += OnEngineEvent;
        _engine.Disconnected += OnEngineDisconnected;
        _engine.EngineStarting += OnEngineStarting;
        Reader.PropertyChanged += OnReaderPropertyChanged;
    }

    public ObservableCollection<AccountViewModel> Accounts { get; } = [];

    public ObservableCollection<FolderViewModel> Folders { get; } = [];

    public ObservableCollection<MailSummaryViewModel> Messages { get; } = [];

    public ReaderViewModel Reader { get; } = new();

    public string[] FilterOptions { get; } = ["全部", "未读", "已标记", "有附件"];

    public string[] ProviderOptions { get; } = ["Outlook", "QQ 邮箱"];

    public string[] ThemeOptions { get; } = ["跟随系统", "浅色", "深色"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverlayOpen))]
    [NotifyPropertyChangedFor(nameof(OverlayTitle))]
    [NotifyPropertyChangedFor(nameof(IsReaderHtmlVisible))]
    private bool _isSettingsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverlayOpen))]
    [NotifyPropertyChangedFor(nameof(OverlayTitle))]
    [NotifyPropertyChangedFor(nameof(IsReaderHtmlVisible))]
    private bool _isAddAccountOpen;

    public bool IsOverlayOpen => IsSettingsOpen || IsAddAccountOpen;

    public string OverlayTitle => IsSettingsOpen ? "设置" : "添加账户";

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
    [NotifyPropertyChangedFor(nameof(HasStatus))]
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
    private bool _isEmpty;

    [ObservableProperty]
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
    private bool _notificationsEnabled = true;

    [ObservableProperty]
    private string _entraClientId = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public bool IsQqProvider => SelectedProviderIndex == 1;

    public bool IsOutlookProvider => SelectedProviderIndex == 0;

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
        if (!HasMore || SelectedFolder is null)
        {
            return;
        }

        try
        {
            var response = await _engine.RequestAsync<MessagesPageResponse>(
                IpcMessageTypes.GetMessages,
                new GetMessagesRequest
                {
                    FolderId = SelectedFolder.Id,
                    BeforeDate = _nextDate,
                    BeforeId = _nextId,
                    Limit = 50
                });

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
    private void TogglePlainText()
    {
        if (_currentBody is null)
        {
            return;
        }

        if (Reader.UseHtml)
        {
            Reader.UseHtml = false;
            return;
        }

        RenderCurrentBody();
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
            IsAddAccountOpen = false;
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

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var settings = new AppSettingsDto
        {
            Theme = Theme,
            NotificationsEnabled = NotificationsEnabled,
            CacheDays = _settings.CacheDays,
            MarkReadOnOpen = _settings.MarkReadOnOpen,
            ShowRemoteImages = _allowRemoteImages,
            EntraClientId = string.IsNullOrWhiteSpace(EntraClientId) ? null : EntraClientId.Trim()
        };

        try
        {
            await _engine.RequestAsync<OkResponse>(
                IpcMessageTypes.UpdateSettings,
                new UpdateSettingsRequest { Settings = settings });

            _settings = settings;
            ApplyTheme();
            StatusMessage = "设置已保存。";
            HasError = false;
            IsSettingsOpen = false;
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        IsAddAccountOpen = false;
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void OpenAddAccount()
    {
        IsSettingsOpen = false;
        IsAddAccountOpen = true;
    }

    [RelayCommand]
    private void CloseOverlay()
    {
        IsSettingsOpen = false;
        IsAddAccountOpen = false;
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

        if (value is null)
        {
            Reader.Reset();
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
    }

    partial void OnThemeIndexChanged(int value)
    {
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
        Theme = _settings.Theme;
        ThemeIndex = _settings.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0
        };
        NotificationsEnabled = _settings.NotificationsEnabled;
        EntraClientId = _settings.EntraClientId ?? string.Empty;
        _allowRemoteImages = _settings.ShowRemoteImages;
        ApplyTheme();
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

        try
        {
            IsBusy = true;
            HasError = false;
            FolderTitle = folder.Name;
            StatusMessage = "正在加载邮件…";
            _allMessages.Clear();
            Messages.Clear();
            SelectedMessage = null;

            var response = await _engine.RequestAsync<MessagesPageResponse>(
                IpcMessageTypes.GetMessages,
                new GetMessagesRequest { FolderId = folder.Id, Limit = 50 });

            AddMessages(response.Items);
            HasMore = response.HasMore;
            _nextDate = response.NextDate;
            _nextId = response.NextId;
            ApplyFilter();

            if (Messages.Count == 0)
            {
                StatusMessage = "正在同步邮件…";
            }
            else
            {
                StatusMessage = null;
                SelectedMessage ??= Messages[0];
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

    private void RenderCurrentBody()
    {
        if (_currentBody is null)
        {
            return;
        }

        Reader.Apply(
            _currentBody,
            Reader.SenderLine,
            Reader.DateText,
            _sanitizer,
            _builder,
            _allowRemoteImages,
            IsDarkTheme());
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

            ApplyFilter();
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
                _allMessages.Add(new MailSummaryViewModel(message));
            }
        }
    }

    private void ApplyFilter()
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

        Messages.Clear();
        foreach (var message in query)
        {
            Messages.Add(message);
        }

        IsEmpty = Messages.Count == 0 && !IsBusy && SelectedFolder is not null;
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
        if (syncState.State == SyncStateKind.Syncing)
        {
            StatusMessage = syncState.Message ?? "正在同步…";
            return;
        }

        if (syncState.State == SyncStateKind.Error)
        {
            HasError = true;
            StatusMessage = syncState.Message ?? "同步失败。";
            return;
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
                IsAddAccountOpen = false;
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
        _engine.EventReceived -= OnEngineEvent;
        _engine.Disconnected -= OnEngineDisconnected;
        _engine.EngineStarting -= OnEngineStarting;
        Reader.PropertyChanged -= OnReaderPropertyChanged;
        _ = _engine.DisposeAsync();
    }
}
