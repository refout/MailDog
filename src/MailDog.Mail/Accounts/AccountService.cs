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

    public AccountDto AddPasswordAccount(
        MailProvider provider,
        string email,
        string secret,
        string? displayName,
        ProviderConfig? serverConfig = null)
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

        if (provider == MailProvider.Custom)
        {
            if (serverConfig is null)
            {
                throw new ArgumentException("请填写自定义服务器地址。");
            }

            ValidateServerConfig(serverConfig);
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

        if (serverConfig is not null)
        {
            _store.UpsertAccountServer(new AccountServerRecord(
                id,
                serverConfig.ImapHost,
                serverConfig.ImapPort,
                serverConfig.ImapStartTls,
                serverConfig.SmtpHost,
                serverConfig.SmtpPort,
                serverConfig.SmtpStartTls));
        }

        return ToDto(record);
    }

    public ProviderConfig GetServerConfig(AccountRecord account)
    {
        var stored = _store.GetAccountServer(account.Id);
        if (stored is null)
        {
            return ProviderCatalog.Get(account.Provider);
        }

        return new ProviderConfig(
            stored.ImapHost,
            stored.ImapPort,
            stored.ImapStartTls,
            stored.SmtpHost,
            stored.SmtpPort,
            stored.SmtpStartTls);
    }

    private static void ValidateServerConfig(ProviderConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ImapHost) || string.IsNullOrWhiteSpace(config.SmtpHost))
        {
            throw new ArgumentException("IMAP 与 SMTP 服务器地址不能为空。");
        }

        if (config.ImapPort is < 1 or > 65535 || config.SmtpPort is < 1 or > 65535)
        {
            throw new ArgumentException("端口必须在 1 到 65535 之间。");
        }
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

    public AccountPreferencesDto GetPreferences()
    {
        var accounts = _store.GetAccounts();
        var order = NormalizeOrder(_store.GetSetting("accountOrder"), accounts);
        var defaultId = _store.GetSetting("defaultAccountId");
        if (string.IsNullOrWhiteSpace(defaultId) || accounts.All(account => account.Id != defaultId))
        {
            defaultId = accounts.FirstOrDefault()?.Id;
        }

        return new AccountPreferencesDto
        {
            DefaultAccountId = defaultId,
            AccountOrder = string.Join(',', order)
        };
    }

    public AccountPreferencesDto UpdatePreferences(AccountPreferencesDto preferences)
    {
        var accounts = _store.GetAccounts();
        var defaultId = preferences.DefaultAccountId;
        if (string.IsNullOrWhiteSpace(defaultId) || accounts.All(account => account.Id != defaultId))
        {
            defaultId = accounts.FirstOrDefault()?.Id;
        }

        var order = NormalizeOrder(preferences.AccountOrder, accounts);
        if (!string.IsNullOrWhiteSpace(defaultId))
        {
            _store.SetSetting("defaultAccountId", defaultId);
        }

        _store.SetSetting("accountOrder", string.Join(',', order));
        return new AccountPreferencesDto
        {
            DefaultAccountId = defaultId,
            AccountOrder = string.Join(',', order)
        };
    }

    private static List<string> NormalizeOrder(string? rawOrder, IReadOnlyList<AccountRecord> accounts)
    {
        var ordered = new List<string>();
        foreach (var id in (rawOrder ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (accounts.Any(account => account.Id == id) && !ordered.Contains(id, StringComparer.Ordinal))
            {
                ordered.Add(id);
            }
        }

        foreach (var account in accounts)
        {
            if (!ordered.Contains(account.Id, StringComparer.Ordinal))
            {
                ordered.Add(account.Id);
            }
        }

        return ordered;
    }

    public void RemoveAccount(string accountId)
    {
        var account = _store.GetAccount(accountId);
        if (account is null)
        {
            return;
        }

        var outboxAttachments = _store.DeleteAccount(accountId);

        if (account.SecretRef is not null)
        {
            _secrets.Delete(account.SecretRef);
        }

        DeleteOutboxAttachments(outboxAttachments);
    }

    private static void DeleteOutboxAttachments(IReadOnlyList<string> paths)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    directories.Add(directory);
                }
            }
            catch
            {
                // 清理失败不能阻断账户删除；残留文件由本地文件管理页处理。
            }
        }

        foreach (var directory in directories)
        {
            try
            {
                if (Directory.Exists(directory)
                    && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch
            {
                // 可能是其他进程仍在占用，留待后续清理。
            }
        }
    }

    /// <summary>更新密码账户的显示名、授权码或自定义服务器；邮箱地址与登录方式不可在此修改。</summary>
    public AccountDto UpdatePasswordAccount(
        string accountId,
        string? displayName,
        string? secret,
        ProviderConfig? serverConfig)
    {
        var account = _store.GetAccount(accountId)
            ?? throw new ArgumentException("账户不存在。", nameof(accountId));

        if (account.AuthKind != "password")
        {
            throw new InvalidOperationException("该账户不是密码登录账户，请重新完成授权。");
        }

        if (!string.IsNullOrWhiteSpace(secret))
        {
            if (account.SecretRef is null)
            {
                throw new InvalidOperationException("账户缺少凭据引用，无法保存授权码。");
            }

            _secrets.Save(account.SecretRef, "imap_password", secret.Trim());
        }

        if (serverConfig is not null)
        {
            if (account.Provider != MailProvider.Custom)
            {
                throw new InvalidOperationException("只有自定义服务器账户可以修改服务器地址。");
            }

            ValidateServerConfig(serverConfig);
            _store.UpsertAccountServer(new AccountServerRecord(
                account.Id,
                serverConfig.ImapHost,
                serverConfig.ImapPort,
                serverConfig.ImapStartTls,
                serverConfig.SmtpHost,
                serverConfig.SmtpPort,
                serverConfig.SmtpStartTls));
        }

        var updated = account with
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? account.Email : displayName.Trim(),
            Status = AccountStatus.Ok
        };

        _store.UpsertAccount(updated);
        return ToDto(updated);
    }

    /// <summary>更新 OAuth 账户的显示名。</summary>
    public AccountDto UpdateDisplayName(string accountId, string? displayName)
    {
        var account = _store.GetAccount(accountId)
            ?? throw new ArgumentException("账户不存在。", nameof(accountId));

        var updated = account with
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? account.Email : displayName.Trim(),
            Status = AccountStatus.Ok
        };

        _store.UpsertAccount(updated);
        return ToDto(updated);
    }

    public AccountDto ToDto(AccountRecord account)
    {
        var server = _store.GetAccountServer(account.Id);
        return new AccountDto
        {
            Id = account.Id,
            Provider = account.Provider,
            Email = account.Email,
            DisplayName = account.DisplayName,
            Status = account.Status,
            Server = server is null
                ? null
                : new AccountServerDto
                {
                    ImapHost = server.ImapHost,
                    ImapPort = server.ImapPort,
                    ImapStartTls = server.ImapStartTls,
                    SmtpHost = server.SmtpHost,
                    SmtpPort = server.SmtpPort,
                    SmtpStartTls = server.SmtpStartTls
                }
        };
    }
}
