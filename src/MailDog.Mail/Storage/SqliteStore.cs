using System.Globalization;
using System.Text;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Platform;
using MailDog.Contracts.Serialization;
using Microsoft.Data.Sqlite;

namespace MailDog.Mail.Storage;

/// <summary>
/// 单连接 SQLite 仓储。Engine 是唯一读写者，所有调用在同一进程内串行化。
/// </summary>
public sealed partial class SqliteStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public string DatabasePath { get; }

    /// <summary>本次启动发生 schema 升级时，升级前数据库快照的路径；没有升级则为 null。</summary>
    public string? LastMigrationBackupPath { get; private set; }

    public SqliteStore(string databasePath)
    {
        DatabasePath = databasePath;
        SqliteNativeBootstrap.EnsureLoaded();
        SQLitePCL.Batteries_V2.Init();

        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Shared;Pooling=False");
        _connection.Open();

        Execute("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA temp_store = MEMORY;
            """);

        Migrate();
    }

    public static string DefaultDatabasePath
        => Path.Combine(ProductPaths.DataDirectory, "maildog.db");

    public IReadOnlyList<AccountRecord> GetAccounts()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, provider, email, display_name, auth_kind, secret_ref, created_utc,
                       last_sync_utc, status, token_expires_utc
                FROM accounts
                ORDER BY created_utc;
                """;

            using var reader = command.ExecuteReader();
            var items = new List<AccountRecord>();
            while (reader.Read())
            {
                items.Add(ReadAccount(reader));
            }

            return items;
        }
    }

    public AccountRecord? GetAccount(string accountId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, provider, email, display_name, auth_kind, secret_ref, created_utc,
                       last_sync_utc, status, token_expires_utc
                FROM accounts WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", accountId);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadAccount(reader) : null;
        }
    }

    public AccountRecord? FindAccountByEmail(string email)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, provider, email, display_name, auth_kind, secret_ref, created_utc,
                       last_sync_utc, status, token_expires_utc
                FROM accounts WHERE email = $email COLLATE NOCASE LIMIT 1;
                """;
            command.Parameters.AddWithValue("$email", email);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadAccount(reader) : null;
        }
    }

    public void UpsertAccount(AccountRecord account)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO accounts
                    (id, provider, email, display_name, auth_kind, secret_ref, created_utc,
                     last_sync_utc, status, token_expires_utc)
                VALUES
                    ($id, $provider, $email, $displayName, $authKind, $secretRef, $createdUtc,
                     $lastSyncUtc, $status, $tokenExpiresUtc)
                ON CONFLICT(id) DO UPDATE SET
                    provider = excluded.provider,
                    email = excluded.email,
                    display_name = excluded.display_name,
                    auth_kind = excluded.auth_kind,
                    secret_ref = excluded.secret_ref,
                    last_sync_utc = excluded.last_sync_utc,
                    status = excluded.status,
                    token_expires_utc = excluded.token_expires_utc;
                """;
            command.Parameters.AddWithValue("$id", account.Id);
            command.Parameters.AddWithValue("$provider", account.Provider.ToString());
            command.Parameters.AddWithValue("$email", account.Email);
            command.Parameters.AddWithValue("$displayName", account.DisplayName);
            command.Parameters.AddWithValue("$authKind", account.AuthKind);
            command.Parameters.AddWithValue("$secretRef", (object?)account.SecretRef ?? DBNull.Value);
            command.Parameters.AddWithValue("$createdUtc", ToDb(account.CreatedUtc));
            command.Parameters.AddWithValue("$lastSyncUtc", (object?)ToDbNullable(account.LastSyncUtc) ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", account.Status.ToString());
            command.Parameters.AddWithValue("$tokenExpiresUtc", (object?)ToDbNullable(account.TokenExpiresUtc) ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public void UpsertAccountServer(AccountServerRecord server)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO account_servers
                    (account_id, imap_host, imap_port, imap_starttls, smtp_host, smtp_port, smtp_starttls)
                VALUES
                    ($accountId, $imapHost, $imapPort, $imapStartTls, $smtpHost, $smtpPort, $smtpStartTls)
                ON CONFLICT(account_id) DO UPDATE SET
                    imap_host = excluded.imap_host,
                    imap_port = excluded.imap_port,
                    imap_starttls = excluded.imap_starttls,
                    smtp_host = excluded.smtp_host,
                    smtp_port = excluded.smtp_port,
                    smtp_starttls = excluded.smtp_starttls;
                """;
            command.Parameters.AddWithValue("$accountId", server.AccountId);
            command.Parameters.AddWithValue("$imapHost", server.ImapHost);
            command.Parameters.AddWithValue("$imapPort", server.ImapPort);
            command.Parameters.AddWithValue("$imapStartTls", server.ImapStartTls ? 1 : 0);
            command.Parameters.AddWithValue("$smtpHost", server.SmtpHost);
            command.Parameters.AddWithValue("$smtpPort", server.SmtpPort);
            command.Parameters.AddWithValue("$smtpStartTls", server.SmtpStartTls ? 1 : 0);
            command.ExecuteNonQuery();
        }
    }

    public AccountServerRecord? GetAccountServer(string accountId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT account_id, imap_host, imap_port, imap_starttls, smtp_host, smtp_port, smtp_starttls
                FROM account_servers WHERE account_id = $accountId;
                """;
            command.Parameters.AddWithValue("$accountId", accountId);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new AccountServerRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3) != 0,
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6) != 0);
        }
    }
    public void UpdateAccountStatus(string accountId, AccountStatus status, DateTimeOffset? lastSyncUtc = null)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE accounts
                SET status = $status,
                    last_sync_utc = COALESCE($lastSyncUtc, last_sync_utc)
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", accountId);
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$lastSyncUtc", (object?)ToDbNullable(lastSyncUtc) ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public void UpdateAccountTokens(string accountId, DateTimeOffset? expiresUtc)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE accounts SET token_expires_utc = $expires WHERE id = $id;";
            command.Parameters.AddWithValue("$id", accountId);
            command.Parameters.AddWithValue("$expires", (object?)ToDbNullable(expiresUtc) ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 删除账户及其本地数据；返回需要由上层清理的 outbox 附件文件路径。
    /// folders/messages/bodies/attachments/account_servers 由外键级联删除，
    /// outbox 与 pending_flags 没有账户外键，必须在这里显式清理。
    /// </summary>
    public IReadOnlyList<string> DeleteAccount(string accountId)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var attachmentPaths = new List<string>();

            using (var select = _connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT oa.stored_path
                    FROM outbox_attachments oa
                    JOIN outbox o ON o.id = oa.outbox_id
                    WHERE o.account_id = $id;
                    """;
                select.Parameters.AddWithValue("$id", accountId);

                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    attachmentPaths.Add(reader.GetString(0));
                }
            }

            using (var flags = _connection.CreateCommand())
            {
                flags.Transaction = transaction;
                flags.CommandText = """
                    DELETE FROM pending_flags
                    WHERE message_id IN (SELECT id FROM messages WHERE account_id = $id);
                    """;
                flags.Parameters.AddWithValue("$id", accountId);
                flags.ExecuteNonQuery();
            }

            using (var outbox = _connection.CreateCommand())
            {
                outbox.Transaction = transaction;
                outbox.CommandText = "DELETE FROM outbox WHERE account_id = $id;";
                outbox.Parameters.AddWithValue("$id", accountId);
                outbox.ExecuteNonQuery();
            }

            using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM accounts WHERE id = $id;";
                command.Parameters.AddWithValue("$id", accountId);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            return attachmentPaths;
        }
    }

    public StorageCountsRecord GetStorageCounts()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM messages),
                    (SELECT COUNT(*) FROM bodies),
                    (SELECT COUNT(*) FROM outbox
                     WHERE status IN ('pending', 'sending', 'failed'));
                """;

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return new StorageCountsRecord(0, 0, 0);
            }

            return new StorageCountsRecord(
                Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture));
        }
    }

    public IReadOnlyList<FolderRecord> GetFolders(string accountId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, account_id, full_name, display_name, role, uid_validity,
                       last_seen_uid, unread_count, total_count
                FROM folders
                WHERE account_id = $accountId
                ORDER BY CASE role
                    WHEN 'Inbox' THEN 0
                    WHEN 'Sent' THEN 1
                    WHEN 'Drafts' THEN 2
                    WHEN 'Archive' THEN 3
                    WHEN 'Junk' THEN 4
                    WHEN 'Trash' THEN 5
                    ELSE 6
                END, full_name;
                """;
            command.Parameters.AddWithValue("$accountId", accountId);

            using var reader = command.ExecuteReader();
            var items = new List<FolderRecord>();
            while (reader.Read())
            {
                items.Add(ReadFolder(reader));
            }

            return items;
        }
    }

    public FolderRecord? GetFolder(long folderId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, account_id, full_name, display_name, role, uid_validity,
                       last_seen_uid, unread_count, total_count
                FROM folders WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", folderId);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadFolder(reader) : null;
        }
    }

    public void UpsertFolders(string accountId, IEnumerable<FolderRecord> folders)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var folder in folders)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO folders
                        (account_id, full_name, display_name, role, uid_validity,
                         last_seen_uid, unread_count, total_count)
                    VALUES
                        ($accountId, $fullName, $displayName, $role, $uidValidity,
                         $lastSeenUid, $unreadCount, $totalCount)
                    ON CONFLICT(account_id, full_name) DO UPDATE SET
                        display_name = excluded.display_name,
                        role = excluded.role,
                        uid_validity = CASE
                            WHEN folders.uid_validity = 0 THEN excluded.uid_validity
                            ELSE folders.uid_validity
                        END,
                        unread_count = excluded.unread_count,
                        total_count = excluded.total_count;
                    """;
                command.Parameters.AddWithValue("$accountId", accountId);
                command.Parameters.AddWithValue("$fullName", folder.FullName);
                command.Parameters.AddWithValue("$displayName", folder.DisplayName);
                command.Parameters.AddWithValue("$role", folder.Role.ToString());
                command.Parameters.AddWithValue("$uidValidity", folder.UidValidity);
                command.Parameters.AddWithValue("$lastSeenUid", folder.LastSeenUid);
                command.Parameters.AddWithValue("$unreadCount", folder.UnreadCount);
                command.Parameters.AddWithValue("$totalCount", folder.TotalCount);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public void UpdateFolderSyncState(long folderId, long uidValidity, long lastSeenUid)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE folders
                SET uid_validity = $uidValidity,
                    last_seen_uid = $lastSeenUid
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", folderId);
            command.Parameters.AddWithValue("$uidValidity", uidValidity);
            command.Parameters.AddWithValue("$lastSeenUid", lastSeenUid);
            command.ExecuteNonQuery();
        }
    }

    public void ResetFolderMessages(long folderId)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            using (var delete = _connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM messages WHERE folder_id = $id;";
                delete.Parameters.AddWithValue("$id", folderId);
                delete.ExecuteNonQuery();
            }

            using (var reset = _connection.CreateCommand())
            {
                reset.Transaction = transaction;
                reset.CommandText = """
                    UPDATE folders
                    SET last_seen_uid = 0, unread_count = 0, total_count = 0
                    WHERE id = $id;
                    """;
                reset.Parameters.AddWithValue("$id", folderId);
                reset.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }
}

public sealed record NewMailResult(int Count, MailSummaryDto? Latest, IReadOnlyList<long>? NewIds = null);
