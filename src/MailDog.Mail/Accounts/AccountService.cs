using MailDog.Contracts.Dto;
using MailDog.Mail.Storage;

namespace MailDog.Mail.Accounts;

public sealed class AccountService
{
    private readonly SqliteStore _store;
    private readonly SecretStore _secrets;

    public AccountService(SqliteStore store, SecretStore secrets)
    {
        _store = store;
        _secrets = secrets;
    }

    public IReadOnlyList<AccountDto> GetAccounts()
        => _store.GetAccounts().Select(ToDto).ToArray();

    public AccountRecord? GetRecord(string accountId) => _store.GetAccount(accountId);

    public AccountRecord? FindByEmail(string email) => _store.FindAccountByEmail(email);

    public AccountDto AddPasswordAccount(MailProvider provider, string email, string secret, string? displayName)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedEmail) || !normalizedEmail.Contains('@', StringComparison.Ordinal))
        {
            throw new ArgumentException("请输入有效的邮箱地址。", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("授权码不能为空。", nameof(secret));
        }

        if (_store.FindAccountByEmail(normalizedEmail) is not null)
        {
            throw new InvalidOperationException("该邮箱账户已存在。");
        }

        var id = Guid.NewGuid().ToString("N");
        var secretRef = $"account:{id}:password";
        _secrets.Save(secretRef, "imap_password", secret.Trim());

        var record = new AccountRecord(
            id,
            provider,
            normalizedEmail,
            string.IsNullOrWhiteSpace(displayName) ? normalizedEmail : displayName.Trim(),
            "password",
            secretRef,
            DateTimeOffset.UtcNow,
            null,
            AccountStatus.Ok,
            null);

        _store.UpsertAccount(record);
        return ToDto(record);
    }

    public AccountDto AddOAuthAccount(MailProvider provider, string email, string displayName, string refreshSecretReference)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var existing = _store.FindAccountByEmail(normalizedEmail);
        if (existing is not null)
        {
            _store.UpsertAccount(existing with
            {
                DisplayName = displayName,
                AuthKind = "oauth2",
                SecretRef = refreshSecretReference,
                Status = AccountStatus.Ok
            });

            return ToDto(_store.GetAccount(existing.Id)!);
        }

        var id = Guid.NewGuid().ToString("N");
        var record = new AccountRecord(
            id,
            provider,
            normalizedEmail,
            string.IsNullOrWhiteSpace(displayName) ? normalizedEmail : displayName,
            "oauth2",
            refreshSecretReference,
            DateTimeOffset.UtcNow,
            null,
            AccountStatus.Ok,
            null);

        _store.UpsertAccount(record);
        return ToDto(record);
    }

    public string? GetPassword(AccountRecord account)
        => account.SecretRef is null ? null : _secrets.TryLoad(account.SecretRef, out _);

    public string? GetRefreshToken(AccountRecord account)
        => account.SecretRef is null ? null : _secrets.TryLoad(account.SecretRef, out _);

    public void SetStatus(string accountId, AccountStatus status, DateTimeOffset? lastSyncUtc = null)
        => _store.UpdateAccountStatus(accountId, status, lastSyncUtc);

    public void RemoveAccount(string accountId)
    {
        var account = _store.GetAccount(accountId);
        if (account is null)
        {
            return;
        }

        _store.DeleteAccount(accountId);

        if (account.SecretRef is not null)
        {
            _secrets.Delete(account.SecretRef);
        }
    }

    public static AccountDto ToDto(AccountRecord account)
        => new()
        {
            Id = account.Id,
            Provider = account.Provider,
            Email = account.Email,
            DisplayName = account.DisplayName,
            Status = account.Status
        };
}
