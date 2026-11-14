using System.Globalization;
using System.Text;
using MailDog.Contracts.Dto;
using Microsoft.Data.Sqlite;

namespace MailDog.Mail.Storage;

/// <summary>
/// 单连接 SQLite 仓储。Engine 是唯一读写者，所有调用在同一进程内串行化。
/// </summary>
public sealed class SqliteStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public SqliteStore(string databasePath)
    {
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
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MailDog",
            "maildog.db");

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

    public void DeleteAccount(string accountId)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM accounts WHERE id = $id;";
            command.Parameters.AddWithValue("$id", accountId);
            command.ExecuteNonQuery();
            transaction.Commit();
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

    public HashSet<long> GetExistingUids(long folderId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT uid FROM messages WHERE folder_id = $folderId;";
            command.Parameters.AddWithValue("$folderId", folderId);

            using var reader = command.ExecuteReader();
            var uids = new HashSet<long>();
            while (reader.Read())
            {
                uids.Add(reader.GetInt64(0));
            }

            return uids;
        }
    }

    public NewMailResult UpsertMessages(string accountId, long folderId, IReadOnlyList<MessageRecord> messages)
    {
        if (messages.Count == 0)
        {
            RecountFolder(folderId);
            return new NewMailResult(0, null);
        }

        lock (_gate)
        {
            var existing = new HashSet<long>();
            using (var query = _connection.CreateCommand())
            {
                query.CommandText = "SELECT uid FROM messages WHERE folder_id = $folderId;";
                query.Parameters.AddWithValue("$folderId", folderId);
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    existing.Add(reader.GetInt64(0));
                }
            }

            var newUids = new HashSet<long>();

            using (var transaction = _connection.BeginTransaction())
            {
                // 早期版本未请求 UID，遗留 uid=0 的无效行，重新同步时清理。
                using (var cleanup = _connection.CreateCommand())
                {
                    cleanup.Transaction = transaction;
                    cleanup.CommandText = "DELETE FROM messages WHERE folder_id = $folderId AND uid <= 0;";
                    cleanup.Parameters.AddWithValue("$folderId", folderId);
                    cleanup.ExecuteNonQuery();
                }

                foreach (var message in messages)
                {
                    using var command = _connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        INSERT INTO messages
                            (account_id, folder_id, uid, message_id, subject, from_name, from_addr,
                             to_addrs, cc_addrs, date_utc, size, flags, has_attachments, preview, body_state)
                        VALUES
                            ($accountId, $folderId, $uid, $messageId, $subject, $fromName, $fromAddr,
                             $toAddrs, $ccAddrs, $dateUtc, $size, $flags, $hasAttachments, $preview, 0)
                        ON CONFLICT(folder_id, uid) DO UPDATE SET
                            message_id = excluded.message_id,
                            subject = excluded.subject,
                            from_name = excluded.from_name,
                            from_addr = excluded.from_addr,
                            to_addrs = excluded.to_addrs,
                            cc_addrs = excluded.cc_addrs,
                            date_utc = excluded.date_utc,
                            size = excluded.size,
                            flags = excluded.flags,
                            has_attachments = excluded.has_attachments,
                            preview = CASE
                                WHEN messages.preview = '' THEN excluded.preview
                                ELSE messages.preview
                            END;
                        """;
                    command.Parameters.AddWithValue("$accountId", accountId);
                    command.Parameters.AddWithValue("$folderId", folderId);
                    command.Parameters.AddWithValue("$uid", message.Uid);
                    command.Parameters.AddWithValue("$messageId", (object?)message.MessageId ?? DBNull.Value);
                    command.Parameters.AddWithValue("$subject", message.Subject);
                    command.Parameters.AddWithValue("$fromName", message.FromName);
                    command.Parameters.AddWithValue("$fromAddr", message.FromAddress);
                    command.Parameters.AddWithValue("$toAddrs", message.ToAddresses);
                    command.Parameters.AddWithValue("$ccAddrs", message.CcAddresses);
                    command.Parameters.AddWithValue("$dateUtc", ToDb(message.Date));
                    command.Parameters.AddWithValue("$size", message.Size);
                    command.Parameters.AddWithValue("$flags", (int)message.Flags);
                    command.Parameters.AddWithValue("$hasAttachments", message.HasAttachments ? 1 : 0);
                    command.Parameters.AddWithValue("$preview", message.Preview);
                    command.ExecuteNonQuery();

                    if (!existing.Contains(message.Uid))
                    {
                        newUids.Add(message.Uid);
                    }
                }

                using (var updateFolder = _connection.CreateCommand())
                {
                    updateFolder.Transaction = transaction;
                    updateFolder.CommandText = """
                        UPDATE folders
                        SET last_seen_uid = MAX(last_seen_uid, $lastSeenUid)
                        WHERE id = $id;
                        """;
                    updateFolder.Parameters.AddWithValue("$id", folderId);
                    updateFolder.Parameters.AddWithValue("$lastSeenUid", messages.Max(message => message.Uid));
                    updateFolder.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            RecountFolder(folderId);

            MailSummaryDto? latest = null;
            if (newUids.Count > 0)
            {
                var page = QueryMessages(folderId, null, null, 1);
                latest = page.Items.FirstOrDefault();
            }

            return new NewMailResult(newUids.Count, latest);
        }
    }

    public MailListPage QueryMessages(long folderId, DateTimeOffset? beforeDate, long? beforeId, int limit)
    {
        var pageSize = Math.Clamp(limit, 1, 200);

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, account_id, folder_id, uid, message_id, subject, from_name, from_addr,
                       to_addrs, cc_addrs, date_utc, size, flags, has_attachments, preview, body_state
                FROM messages
                WHERE folder_id = $folderId
                  AND (
                      $beforeDate IS NULL
                      OR date_utc < $beforeDate
                      OR (date_utc = $beforeDate AND id < $beforeId)
                  )
                ORDER BY date_utc DESC, id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$folderId", folderId);
            command.Parameters.AddWithValue("$beforeDate", (object?)ToDbNullable(beforeDate) ?? DBNull.Value);
            command.Parameters.AddWithValue("$beforeId", beforeId ?? 0);
            command.Parameters.AddWithValue("$limit", pageSize + 1);

            using var reader = command.ExecuteReader();
            var items = new List<MailSummaryDto>();
            while (reader.Read())
            {
                items.Add(ToSummary(ReadMessage(reader)));
            }

            var hasMore = items.Count > pageSize;
            if (hasMore)
            {
                items.RemoveAt(items.Count - 1);
            }

            var last = items.LastOrDefault();
            return new MailListPage
            {
                Items = items.ToArray(),
                HasMore = hasMore,
                NextDate = last?.Date,
                NextId = last?.Id
            };
        }
    }

    public MessageRecord? GetMessage(long messageId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, account_id, folder_id, uid, message_id, subject, from_name, from_addr,
                       to_addrs, cc_addrs, date_utc, size, flags, has_attachments, preview, body_state
                FROM messages WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", messageId);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadMessage(reader) : null;
        }
    }

    public void UpdateSeen(long messageId, bool isRead)
    {
        UpdateFlag(messageId, MailFlags.Seen, isRead);
    }

    public void UpdateFlagged(long messageId, bool value)
    {
        UpdateFlag(messageId, MailFlags.Flagged, value);
    }

    public void UpdateFlag(long messageId, MailFlags flag, bool value)
    {
        lock (_gate)
        {
            long folderId;
            using (var lookup = _connection.CreateCommand())
            {
                lookup.CommandText = "SELECT folder_id FROM messages WHERE id = $id;";
                lookup.Parameters.AddWithValue("$id", messageId);
                var result = lookup.ExecuteScalar();
                if (result is null)
                {
                    return;
                }

                folderId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
            }

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = value
                    ? "UPDATE messages SET flags = flags | $flag WHERE id = $id;"
                    : "UPDATE messages SET flags = flags & ~$flag WHERE id = $id;";
                command.Parameters.AddWithValue("$flag", (int)flag);
                command.Parameters.AddWithValue("$id", messageId);
                command.ExecuteNonQuery();
            }

            RecountFolder(folderId);
        }
    }

    public BodyRecord? GetBody(long messageId)
    {
        lock (_gate)
        {
            string? html;
            string? plain;
            bool truncated;

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT html_raw, text_plain, truncated
                    FROM bodies WHERE message_row_id = $id;
                    """;
                command.Parameters.AddWithValue("$id", messageId);

                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                html = reader.IsDBNull(0) ? null : reader.GetString(0);
                plain = reader.IsDBNull(1) ? null : reader.GetString(1);
                truncated = reader.GetInt64(2) != 0;
            }

            var attachments = new List<AttachmentRecord>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT file_name, mime_type, size, content_id, is_inline
                    FROM attachments WHERE message_row_id = $id ORDER BY id;
                    """;
                command.Parameters.AddWithValue("$id", messageId);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    attachments.Add(new AttachmentRecord(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.GetInt64(4) != 0));
                }
            }

            return new BodyRecord(messageId, html, plain, truncated, attachments);
        }
    }

    public int GetBodyState(long messageId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT body_state FROM messages WHERE id = $id;";
            command.Parameters.AddWithValue("$id", messageId);
            var result = command.ExecuteScalar();
            return result is null ? BodyStates.NotFetched : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
    }

    public void SaveBody(long messageId, string? html, string? plainText, bool truncated, IReadOnlyList<AttachmentRecord> attachments)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            using (var body = _connection.CreateCommand())
            {
                body.Transaction = transaction;
                body.CommandText = """
                    INSERT INTO bodies (message_row_id, html_raw, text_plain, truncated, fetched_utc)
                    VALUES ($id, $html, $plain, $truncated, $fetched)
                    ON CONFLICT(message_row_id) DO UPDATE SET
                        html_raw = excluded.html_raw,
                        text_plain = excluded.text_plain,
                        truncated = excluded.truncated,
                        fetched_utc = excluded.fetched_utc;
                    """;
                body.Parameters.AddWithValue("$id", messageId);
                body.Parameters.AddWithValue("$html", (object?)html ?? DBNull.Value);
                body.Parameters.AddWithValue("$plain", (object?)plainText ?? DBNull.Value);
                body.Parameters.AddWithValue("$truncated", truncated ? 1 : 0);
                body.Parameters.AddWithValue("$fetched", ToDb(DateTimeOffset.UtcNow));
                body.ExecuteNonQuery();
            }

            using (var deleteAttachments = _connection.CreateCommand())
            {
                deleteAttachments.Transaction = transaction;
                deleteAttachments.CommandText = "DELETE FROM attachments WHERE message_row_id = $id;";
                deleteAttachments.Parameters.AddWithValue("$id", messageId);
                deleteAttachments.ExecuteNonQuery();
            }

            foreach (var attachment in attachments)
            {
                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO attachments (message_row_id, file_name, mime_type, size, content_id, is_inline)
                    VALUES ($id, $fileName, $mimeType, $size, $contentId, $isInline);
                    """;
                insert.Parameters.AddWithValue("$id", messageId);
                insert.Parameters.AddWithValue("$fileName", attachment.FileName);
                insert.Parameters.AddWithValue("$mimeType", attachment.MimeType);
                insert.Parameters.AddWithValue("$size", attachment.Size);
                insert.Parameters.AddWithValue("$contentId", (object?)attachment.ContentId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$isInline", attachment.IsInline ? 1 : 0);
                insert.ExecuteNonQuery();
            }

            using (var updateMessage = _connection.CreateCommand())
            {
                updateMessage.Transaction = transaction;
                updateMessage.CommandText = """
                    UPDATE messages
                    SET body_state = $state,
                        preview = CASE WHEN $preview = '' THEN preview ELSE $preview END
                    WHERE id = $id;
                    """;
                updateMessage.Parameters.AddWithValue("$id", messageId);
                updateMessage.Parameters.AddWithValue("$state", BodyStates.Cached);
                updateMessage.Parameters.AddWithValue("$preview", BuildPreview(plainText));
                updateMessage.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public void UpdateBodyState(long messageId, int state)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE messages SET body_state = $state WHERE id = $id;";
            command.Parameters.AddWithValue("$id", messageId);
            command.Parameters.AddWithValue("$state", state);
            command.ExecuteNonQuery();
        }
    }

    public AppSettingsDto GetSettings()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM settings;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                values[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return new AppSettingsDto
        {
            Theme = values.GetValueOrDefault("theme", "system"),
            Material = values.GetValueOrDefault("material", "mica"),
            BackgroundColor = string.IsNullOrWhiteSpace(values.GetValueOrDefault("backgroundColor"))
                ? null
                : values.GetValueOrDefault("backgroundColor"),
            BackgroundOpacity = int.TryParse(values.GetValueOrDefault("backgroundOpacity"), out var opacity)
                ? Math.Clamp(opacity, 40, 100)
                : 85,
            NotificationsEnabled = !values.TryGetValue("notifications", out var notifications) || notifications != "0",
            NotificationSoundEnabled = !values.TryGetValue("notificationSound", out var notificationSound) || notificationSound != "0",
            SyncIntervalMinutes = int.TryParse(values.GetValueOrDefault("syncIntervalMinutes"), out var interval)
                ? Math.Clamp(interval, 1, 60)
                : 1,
            CacheDays = int.TryParse(values.GetValueOrDefault("cacheDays"), out var days) ? days : 90,
            MarkReadOnOpen = !values.TryGetValue("markReadOnOpen", out var markRead) || markRead != "0",
            ShowRemoteImages = values.GetValueOrDefault("showRemoteImages") == "1",
            RenderMode = values.GetValueOrDefault("renderMode") == "plain" ? "plain" : "html"
        };
    }

    public void SaveSettings(AppSettingsDto settings)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            UpsertSetting(transaction, "theme", settings.Theme);
            UpsertSetting(transaction, "material", settings.Material);
            UpsertSetting(transaction, "backgroundColor", settings.BackgroundColor ?? string.Empty);
            UpsertSetting(transaction, "backgroundOpacity", settings.BackgroundOpacity.ToString(CultureInfo.InvariantCulture));
            UpsertSetting(transaction, "notifications", settings.NotificationsEnabled ? "1" : "0");
            UpsertSetting(transaction, "notificationSound", settings.NotificationSoundEnabled ? "1" : "0");
            UpsertSetting(transaction, "syncIntervalMinutes", settings.SyncIntervalMinutes.ToString(CultureInfo.InvariantCulture));
            UpsertSetting(transaction, "cacheDays", settings.CacheDays.ToString(CultureInfo.InvariantCulture));
            UpsertSetting(transaction, "markReadOnOpen", settings.MarkReadOnOpen ? "1" : "0");
            UpsertSetting(transaction, "showRemoteImages", settings.ShowRemoteImages ? "1" : "0");
            UpsertSetting(transaction, "renderMode", settings.RenderMode == "plain" ? "plain" : "html");
            transaction.Commit();
        }
    }

    public string? GetSetting(string key)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SaveSecret(string reference, string kind, byte[] protectedBlob)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO secrets (ref, kind, protected_blob, updated_utc)
                VALUES ($ref, $kind, $blob, $updated)
                ON CONFLICT(ref) DO UPDATE SET
                    kind = excluded.kind,
                    protected_blob = excluded.protected_blob,
                    updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$ref", reference);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$blob", protectedBlob);
            command.Parameters.AddWithValue("$updated", ToDb(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
        }
    }

    public bool TryGetSecret(string reference, out string kind, out byte[] protectedBlob)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT kind, protected_blob FROM secrets WHERE ref = $ref;";
            command.Parameters.AddWithValue("$ref", reference);

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                kind = reader.GetString(0);
                protectedBlob = (byte[])reader.GetValue(1);
                return true;
            }

            kind = string.Empty;
            protectedBlob = [];
            return false;
        }
    }

    public void DeleteSecret(string reference)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM secrets WHERE ref = $ref;";
            command.Parameters.AddWithValue("$ref", reference);
            command.ExecuteNonQuery();
        }
    }

    private void RecountFolder(long folderId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE folders
                SET unread_count = (
                        SELECT COUNT(*) FROM messages
                        WHERE folder_id = $id AND (flags & $seen) = 0
                    ),
                    total_count = (
                        SELECT COUNT(*) FROM messages WHERE folder_id = $id
                    )
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", folderId);
            command.Parameters.AddWithValue("$seen", (int)MailFlags.Seen);
            command.ExecuteNonQuery();
        }
    }

    private void UpsertSetting(SqliteTransaction transaction, string key, string value)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private void Migrate()
    {
        var version = GetUserVersion();

        if (version < 1)
        {
            CreateSchemaV1();
            version = 1;
        }

        if (version < 2)
        {
            // v1 缓存的正文/主题可能因缺少 GB2312/GBK 代码页而乱码，清空后按需重新抓取。
            Execute("""
                DELETE FROM bodies;
                UPDATE messages SET body_state = 0, preview = '';
                UPDATE folders SET last_seen_uid = 0;
                PRAGMA user_version = 2;
                """);
        }
    }

    private void CreateSchemaV1()
    {
        using var transaction = _connection.BeginTransaction();

        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS accounts (
                    id                 TEXT PRIMARY KEY,
                    provider           TEXT NOT NULL,
                    email              TEXT NOT NULL UNIQUE,
                    display_name       TEXT NOT NULL,
                    auth_kind          TEXT NOT NULL,
                    secret_ref         TEXT,
                    created_utc        TEXT NOT NULL,
                    last_sync_utc      TEXT,
                    status             TEXT NOT NULL DEFAULT 'Ok',
                    token_expires_utc  TEXT
                );

                CREATE TABLE IF NOT EXISTS folders (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    account_id    TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                    full_name     TEXT NOT NULL,
                    display_name  TEXT NOT NULL,
                    role          TEXT NOT NULL,
                    uid_validity  INTEGER NOT NULL DEFAULT 0,
                    last_seen_uid INTEGER NOT NULL DEFAULT 0,
                    unread_count  INTEGER NOT NULL DEFAULT 0,
                    total_count   INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(account_id, full_name)
                );

                CREATE TABLE IF NOT EXISTS messages (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    account_id      TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                    folder_id       INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
                    uid             INTEGER NOT NULL,
                    message_id      TEXT,
                    subject         TEXT NOT NULL DEFAULT '',
                    from_name       TEXT NOT NULL DEFAULT '',
                    from_addr       TEXT NOT NULL DEFAULT '',
                    to_addrs        TEXT NOT NULL DEFAULT '',
                    cc_addrs        TEXT NOT NULL DEFAULT '',
                    date_utc        TEXT NOT NULL,
                    size            INTEGER NOT NULL DEFAULT 0,
                    flags           INTEGER NOT NULL DEFAULT 0,
                    has_attachments INTEGER NOT NULL DEFAULT 0,
                    preview         TEXT NOT NULL DEFAULT '',
                    body_state      INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(folder_id, uid)
                );

                CREATE INDEX IF NOT EXISTS ix_messages_folder_date
                    ON messages(folder_id, date_utc DESC, id DESC);
                CREATE INDEX IF NOT EXISTS ix_messages_account_date
                    ON messages(account_id, date_utc DESC, id DESC);
                CREATE INDEX IF NOT EXISTS ix_messages_message_id
                    ON messages(message_id);

                CREATE TABLE IF NOT EXISTS bodies (
                    message_row_id INTEGER PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
                    html_raw       TEXT,
                    text_plain     TEXT,
                    truncated      INTEGER NOT NULL DEFAULT 0,
                    fetched_utc    TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS attachments (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_row_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                    file_name      TEXT NOT NULL,
                    mime_type      TEXT NOT NULL,
                    size           INTEGER NOT NULL DEFAULT 0,
                    content_id     TEXT,
                    is_inline      INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS secrets (
                    ref            TEXT PRIMARY KEY,
                    kind           TEXT NOT NULL,
                    protected_blob BLOB NOT NULL,
                    updated_utc    TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS settings (
                    key   TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private int GetUserVersion()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static AccountRecord ReadAccount(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            ParseProvider(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            FromDb(reader.GetString(6)),
            reader.IsDBNull(7) ? null : FromDb(reader.GetString(7)),
            ParseStatus(reader.GetString(8)),
            reader.IsDBNull(9) ? null : FromDb(reader.GetString(9)));

    private static FolderRecord ReadFolder(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseRole(reader.GetString(4)),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt32(7),
            reader.GetInt32(8));

    private static MessageRecord ReadMessage(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            FromDb(reader.GetString(10)),
            reader.GetInt32(11),
            (MailFlags)reader.GetInt32(12),
            reader.GetInt64(13) != 0,
            reader.GetString(14),
            reader.GetInt32(15));

    private static MailSummaryDto ToSummary(MessageRecord message)
        => new()
        {
            Id = message.Id,
            FolderId = message.FolderId,
            AccountId = message.AccountId,
            Subject = message.Subject,
            FromName = message.FromName,
            FromAddress = message.FromAddress,
            Preview = message.Preview,
            Date = message.Date,
            Flags = message.Flags,
            HasAttachments = message.HasAttachments,
            Size = message.Size
        };

    private static MailProvider ParseProvider(string value)
        => value switch
        {
            "QQ" => MailProvider.QQ,
            _ => MailProvider.Outlook
        };

    private static FolderRole ParseRole(string value)
        => value switch
        {
            "Inbox" => FolderRole.Inbox,
            "Sent" => FolderRole.Sent,
            "Drafts" => FolderRole.Drafts,
            "Trash" => FolderRole.Trash,
            "Junk" => FolderRole.Junk,
            "Archive" => FolderRole.Archive,
            _ => FolderRole.Other
        };

    private static AccountStatus ParseStatus(string value)
        => value switch
        {
            "AuthRequired" => AccountStatus.AuthRequired,
            "Offline" => AccountStatus.Offline,
            "Error" => AccountStatus.Error,
            _ => AccountStatus.Ok
        };

    private static string ToDb(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? ToDbNullable(DateTimeOffset? value)
        => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset FromDb(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string BuildPreview(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(160);
        var previousWasSpace = false;

        foreach (var character in plainText)
        {
            var isSpace = char.IsWhiteSpace(character);
            if (isSpace)
            {
                if (!previousWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(character);
            }

            previousWasSpace = isSpace;

            if (builder.Length >= 160)
            {
                break;
            }
        }

        return builder.ToString().Trim();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }
}

public sealed record NewMailResult(int Count, MailSummaryDto? Latest);
