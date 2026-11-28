using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MailDog.Contracts.Dto;

namespace MailDog.UI.ViewModels;

public sealed partial class AccountViewModel : ObservableObject
{
    public AccountViewModel(AccountDto account)
    {
        Id = account.Id;
        Email = account.Email;
        DisplayName = account.DisplayName;
        Provider = account.Provider;
        Server = account.Server;
        ProviderText = account.Provider switch
        {
            MailProvider.Outlook => "Outlook",
            MailProvider.Custom => "自定义",
            _ => "QQ 邮箱"
        };
        StatusText = account.Status switch
        {
            AccountStatus.AuthRequired => "需重新登录",
            AccountStatus.ProtocolBlocked => "邮箱协议受限",
            AccountStatus.Offline => "离线",
            AccountStatus.Error => "同步失败",
            _ => "已连接"
        };
        StatusBrush = new SolidColorBrush(account.Status switch
        {
            AccountStatus.Ok => Color.Parse("#0F7B0F"),
            AccountStatus.AuthRequired => Color.Parse("#9D5D00"),
            AccountStatus.ProtocolBlocked => Color.Parse("#9D5D00"),
            _ => Color.Parse("#8A8A8A")
        });
        var initialSource = string.IsNullOrWhiteSpace(DisplayName) ? Email : DisplayName;
        Initial = string.IsNullOrWhiteSpace(initialSource)
            ? "?"
            : initialSource[..1].ToUpperInvariant();
    }

    public string Id { get; }

    public string Email { get; }

    public string DisplayName { get; }

    /// <summary>侧栏账户主标题：显示名与邮箱相同时退回邮箱类型，避免两行重复地址。</summary>
    public string Title => string.IsNullOrWhiteSpace(DisplayName)
        || string.Equals(DisplayName.Trim(), Email, StringComparison.OrdinalIgnoreCase)
            ? ProviderText
            : DisplayName;

    public MailProvider Provider { get; }

    public AccountServerDto? Server { get; }

    public string ProviderText { get; }

    public bool CanReauthorize => Provider == MailProvider.Outlook;

    public string StatusText { get; }

    public IBrush StatusBrush { get; }

    public string Initial { get; }

    /// <summary>该账户下的文件夹（树的第二级）。</summary>
    public ObservableCollection<FolderViewModel> Folders { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isDefault;

    public bool HasFolders => Folders.Count > 0;

    public void NotifyFoldersChanged() => OnPropertyChanged(nameof(HasFolders));

    /// <summary>瞬态同步进度，只用于左侧账户树；设置页不绑定，避免状态出现/消失造成布局跳动。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSyncStatus))]
    private string? _syncStatus;

    public bool HasSyncStatus => !string.IsNullOrEmpty(SyncStatus);

    [ObservableProperty]
    private bool _isDeleteConfirmVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeleteError))]
    private string? _deleteError;

    public bool HasDeleteError => !string.IsNullOrEmpty(DeleteError);

    [ObservableProperty]
    private bool _isDeleting;
}
