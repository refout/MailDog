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
        ProviderText = account.Provider == MailProvider.Outlook ? "Outlook" : "QQ 邮箱";
        StatusText = account.Status switch
        {
            AccountStatus.AuthRequired => "需重新登录",
            AccountStatus.Offline => "离线",
            AccountStatus.Error => "同步失败",
            _ => "已连接"
        };
        StatusBrush = new SolidColorBrush(account.Status switch
        {
            AccountStatus.Ok => Color.Parse("#0F7B0F"),
            AccountStatus.AuthRequired => Color.Parse("#9D5D00"),
            _ => Color.Parse("#8A8A8A")
        });
        Initial = string.IsNullOrWhiteSpace(DisplayName)
            ? "?"
            : DisplayName[..1].ToUpperInvariant();
    }

    public string Id { get; }

    public string Email { get; }

    public string DisplayName { get; }

    /// <summary>显示名与邮箱相同时不再重复展示邮箱。</summary>
    public bool ShowEmail => !string.Equals(DisplayName, Email, StringComparison.OrdinalIgnoreCase);

    public string ProviderText { get; }

    public string StatusText { get; }

    public IBrush StatusBrush { get; }

    public string Initial { get; }

    /// <summary>该账户下的文件夹（树的第二级）。</summary>
    public ObservableCollection<FolderViewModel> Folders { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;

    public bool HasFolders => Folders.Count > 0;

    public void NotifyFoldersChanged() => OnPropertyChanged(nameof(HasFolders));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSyncStatus))]
    private string? _syncStatus;

    public bool HasSyncStatus => !string.IsNullOrEmpty(SyncStatus);
}
