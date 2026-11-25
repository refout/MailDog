using MailDog.Contracts.Dto;
using MailDog.Mail.Storage;
using MimeKit;

namespace MailDog.Mail.Mime;

public sealed record ResolvedMailIdentity(string Address, string DisplayName);

/// <summary>
/// 校验发信地址：只允许账户主地址或设置中为该账户配置的别名。
/// 这样即使 UI 传了任意字符串，也不会变成任意发件人伪造。
/// </summary>
public static class MailIdentityResolver
{
    public static ResolvedMailIdentity Resolve(
        AccountRecord account,
        AppSettingsDto settings,
        string? fromAddress,
        string? fromName = null)
    {
        var address = (fromAddress ?? string.Empty).Trim();
        if (address.Length == 0
            || string.Equals(address, account.Email, StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedMailIdentity(
                account.Email,
                string.IsNullOrWhiteSpace(fromName) ? account.DisplayName : fromName.Trim());
        }

        var identity = (settings.AccountIdentities ?? [])
            .FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.AccountId)
                && string.Equals(item.AccountId, account.Id, StringComparison.Ordinal)
                && string.Equals(item.Address?.Trim(), address, StringComparison.OrdinalIgnoreCase));
        if (identity is null)
        {
            throw new InvalidOperationException("该发信身份未配置或不属于当前账户，请先在设置中检查别名。");
        }

        if (!MailboxAddress.TryParse(identity.Address, out var mailbox)
            || !string.Equals(mailbox.Address, identity.Address.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"发信身份地址格式不正确：{identity.Address}");
        }

        var displayName = !string.IsNullOrWhiteSpace(identity.DisplayName)
            ? identity.DisplayName.Trim()
            : !string.IsNullOrWhiteSpace(fromName)
                ? fromName.Trim()
                : identity.Address.Trim();
        return new ResolvedMailIdentity(identity.Address.Trim(), displayName);
    }
}
