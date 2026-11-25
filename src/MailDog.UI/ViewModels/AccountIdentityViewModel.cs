using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailDog.Contracts.Dto;

namespace MailDog.UI.ViewModels;

/// <summary>设置页里可编辑的别名发信身份；保存按钮触发整页自动保存。</summary>
public sealed partial class AccountIdentityViewModel : ObservableObject
{
    private readonly Action<AccountIdentityViewModel> _save;
    private readonly Action<AccountIdentityViewModel> _delete;

    public AccountIdentityViewModel(
        AccountIdentityDto identity,
        string accountEmail,
        Action<AccountIdentityViewModel> save,
        Action<AccountIdentityViewModel> delete)
    {
        Id = identity.Id;
        AccountId = identity.AccountId;
        AccountEmail = accountEmail;
        _address = identity.Address;
        _displayName = identity.DisplayName;
        _signature = identity.Signature;
        _save = save;
        _delete = delete;
    }

    public string Id { get; }

    public string AccountId { get; }

    public string AccountEmail { get; }

    [ObservableProperty]
    private string _address;

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private string _signature;

    public string Label => string.IsNullOrWhiteSpace(Address) ? "未填写地址" : Address;

    partial void OnAddressChanged(string value) => OnPropertyChanged(nameof(Label));

    public AccountIdentityDto ToDto()
        => new()
        {
            Id = Id,
            AccountId = AccountId,
            Address = Address.Trim(),
            DisplayName = DisplayName.Trim(),
            Signature = Signature.Replace("\r\n", "\n").TrimEnd()
        };

    [RelayCommand]
    private void Save() => _save(this);

    [RelayCommand]
    private void Delete() => _delete(this);
}
