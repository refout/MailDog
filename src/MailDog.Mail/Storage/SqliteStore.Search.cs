using System.Globalization;
using System.Text;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Platform;
using MailDog.Contracts.Serialization;
using Microsoft.Data.Sqlite;

namespace MailDog.Mail.Storage;

public sealed partial class SqliteStore
{
    /// <summary>全文搜索当前文件夹或统一文件夹中的邮件，返回匹配的消息行 ID。</summary>
    public IReadOnlyList<long> SearchMessageIds(string query, long? folderId, FolderRole? role, int limit = 200)
    {
        var text = query.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            var filters = new List<string>();
            if (folderId is { } id)
            {
                filters.Add("m.folder_id = $folderId");
                command.Parameters.AddWithValue("$folderId", id);
            }

            if (role is { } folderRole)
            {
                filters.Add("f.role = $role");
                command.Parameters.AddWithValue("$role", folderRole.ToString());
            }

            var scope = filters.Count == 0 ? string.Empty : " AND " + string.Join(" AND ", filters);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

            if (text.Length < 3)
            {
                // trigram 索引只能匹配三个及以上字符，短词回退到 LIKE。
                command.CommandText = $"""
                    SELECT m.id
                    FROM messages m
                    JOIN folders f ON f.id = m.folder_id
                    LEFT JOIN bodies b ON b.message_row_id = m.id
                    WHERE (
                        m.subject LIKE $like OR
                        m.from_name LIKE $like OR
                        m.from_addr LIKE $like OR
                        m.to_addrs LIKE $like OR
                        m.cc_addrs LIKE $like OR
                        m.preview LIKE $like OR
                        b.text_plain LIKE $like
                    ){scope}
                    ORDER BY m.date_utc DESC, m.id DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$like", $"%{text}%");
            }
            else
            {
                var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(term => $"\"{term.Replace("\"", "\"\"")}\"");
                command.CommandText = $"""
                    SELECT m.id
                    FROM message_fts
                    JOIN messages m ON m.id = message_fts.message_id
                    JOIN folders f ON f.id = m.folder_id
                    WHERE message_fts MATCH $match{scope}
                    ORDER BY m.date_utc DESC, m.id DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$match", string.Join(" ", terms));
            }

            var result = new List<long>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(reader.GetInt64(0));
            }

            return result;
        }
    }

    /// <summary>跨文件夹全文搜索并返回邮件摘要，供“全部邮箱”搜索直接合并列表。</summary>
    public IReadOnlyList<MailSummaryDto> SearchMessageSummaries(
        string query,
        long? folderId,
        FolderRole? role,
        int limit = 200)
    {
        var text = query.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            var filters = new List<string>();
            if (folderId is { } id)
            {
                filters.Add("m.folder_id = $folderId");
                command.Parameters.AddWithValue("$folderId", id);
            }

            if (role is { } folderRole)
            {
                filters.Add("f.role = $role");
                command.Parameters.AddWithValue("$role", folderRole.ToString());
            }

            filters.Add("""
                NOT EXISTS (
                    SELECT 1 FROM snoozed_messages s
                    WHERE s.message_id = m.id AND s.until_utc > $now
                )
                """);
            command.Parameters.AddWithValue("$now", ToDb(DateTimeOffset.UtcNow));

            var scope = filters.Count == 0 ? string.Empty : " AND " + string.Join(" AND ", filters);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            var fields = """
                m.id, m.account_id, m.folder_id, m.uid, m.message_id, m.subject, m.from_name, m.from_addr,
                m.to_addrs, m.cc_addrs, m.date_utc, m.size, m.flags, m.has_attachments, m.preview,
                m.body_state, m.thread_key
                """;

            if (text.Length < 3)
            {
                command.CommandText = $"""
                    SELECT {fields}
                    FROM messages m
                    JOIN folders f ON f.id = m.folder_id
                    LEFT JOIN bodies b ON b.message_row_id = m.id
                    WHERE (
                        m.subject LIKE $like OR
                        m.from_name LIKE $like OR
                        m.from_addr LIKE $like OR
                        m.to_addrs LIKE $like OR
                        m.cc_addrs LIKE $like OR
                        m.preview LIKE $like OR
                        b.text_plain LIKE $like
                    ){scope}
                    ORDER BY m.date_utc DESC, m.id DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$like", $"%{text}%");
            }
            else
            {
                var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(term => $"\"{term.Replace("\"", "\"\"")}\"");
                command.CommandText = $"""
                    SELECT {fields}
                    FROM message_fts
                    JOIN messages m ON m.id = message_fts.message_id
                    JOIN folders f ON f.id = m.folder_id
                    WHERE message_fts MATCH $match{scope}
                    ORDER BY m.date_utc DESC, m.id DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$match", string.Join(" ", terms));
            }

            var result = new List<MailSummaryDto>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(ToSummary(ReadMessage(reader)));
            }

            return result;
        }
    }

    private void UpdateMessageFts(SqliteTransaction? transaction, long messageId)
    {
        string subject;
        string sender;
        string recipients;
        string body;

        using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT m.subject, m.from_name, m.from_addr, m.to_addrs, m.cc_addrs,
                       COALESCE(b.text_plain, '')
                FROM messages m
                LEFT JOIN bodies b ON b.message_row_id = m.id
                WHERE m.id = $id;
                """;
            query.Parameters.AddWithValue("$id", messageId);
            using var reader = query.ExecuteReader();
            if (!reader.Read())
            {
                return;
            }

            subject = reader.GetString(0);
            sender = $"{reader.GetString(1)} {reader.GetString(2)}";
            recipients = $"{reader.GetString(3)} {reader.GetString(4)}";
            body = reader.GetString(5);
        }

        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM message_fts WHERE message_id = $id;";
            delete.Parameters.AddWithValue("$id", messageId);
            delete.ExecuteNonQuery();
        }

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO message_fts (message_id, subject, sender, recipients, body)
                VALUES ($id, $subject, $sender, $recipients, $body);
                """;
            insert.Parameters.AddWithValue("$id", messageId);
            insert.Parameters.AddWithValue("$subject", subject);
            insert.Parameters.AddWithValue("$sender", sender);
            insert.Parameters.AddWithValue("$recipients", recipients);
            insert.Parameters.AddWithValue("$body", body.Length > 20000 ? body[..20000] : body);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>生成一致性数据库快照，供备份打包；快照是独立 SQLite 文件。</summary>
    public void BackupTo(string destinationPath)
    {
        lock (_gate)
        {
            var fullPath = Path.GetFullPath(destinationPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            using var command = _connection.CreateCommand();
            command.CommandText = $"VACUUM INTO '{fullPath.Replace("'", "''")}';";
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
                          AND NOT EXISTS (
                              SELECT 1 FROM snoozed_messages s
                              WHERE s.message_id = messages.id AND s.until_utc > $now
                          )
                    ),
                    total_count = (
                        SELECT COUNT(*) FROM messages
                        WHERE folder_id = $id
                          AND NOT EXISTS (
                              SELECT 1 FROM snoozed_messages s
                              WHERE s.message_id = messages.id AND s.until_utc > $now
                          )
                    )
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", folderId);
            command.Parameters.AddWithValue("$seen", (int)MailFlags.Seen);
            command.Parameters.AddWithValue("$now", ToDb(DateTimeOffset.UtcNow));
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

        if (version is > 0 and < 15)
        {
            LastMigrationBackupPath = BackupBeforeMigration(version);
        }

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
            version = 2;
        }

        if (version < 3)
        {
            Execute("""
                CREATE TABLE IF NOT EXISTS outbox (
                    id               INTEGER PRIMARY KEY AUTOINCREMENT,
                    account_id       TEXT NOT NULL,
                    to_addrs         TEXT NOT NULL DEFAULT '',
                    cc_addrs         TEXT NOT NULL DEFAULT '',
                    bcc_addrs        TEXT NOT NULL DEFAULT '',
                    subject          TEXT NOT NULL DEFAULT '',
                    body_text        TEXT NOT NULL DEFAULT '',
                    in_reply_to      TEXT,
                    message_id       TEXT NOT NULL,
                    status           TEXT NOT NULL DEFAULT 'pending',
                    attempts         INTEGER NOT NULL DEFAULT 0,
                    next_attempt_utc TEXT NOT NULL,
                    last_error       TEXT,
                    created_utc      TEXT NOT NULL,
                    sent_utc         TEXT
                );

                CREATE INDEX IF NOT EXISTS ix_outbox_status_next
                    ON outbox(status, next_attempt_utc);

                CREATE TABLE IF NOT EXISTS outbox_attachments (
                    id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    outbox_id  INTEGER NOT NULL REFERENCES outbox(id) ON DELETE CASCADE,
                    file_name  TEXT NOT NULL,
                    stored_path TEXT NOT NULL,
                    mime_type  TEXT NOT NULL,
                    size       INTEGER NOT NULL DEFAULT 0
                );

                PRAGMA user_version = 3;
                """);
        }

        if (version < 4)
        {
            Execute("""
                CREATE TABLE IF NOT EXISTS pending_flags (
                    id               INTEGER PRIMARY KEY AUTOINCREMENT,
                    message_id       INTEGER NOT NULL,
                    flag             TEXT NOT NULL,
                    value            INTEGER NOT NULL,
                    attempts         INTEGER NOT NULL DEFAULT 0,
                    next_attempt_utc TEXT NOT NULL,
                    last_error       TEXT,
                    created_utc      TEXT NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ix_pending_flags_message_flag
                    ON pending_flags(message_id, flag);

                PRAGMA user_version = 4;
                """);
        }

        if (version < 5)
        {
            Execute("""
                CREATE TABLE IF NOT EXISTS account_servers (
                    account_id     TEXT PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
                    imap_host      TEXT NOT NULL,
                    imap_port      INTEGER NOT NULL,
                    imap_starttls  INTEGER NOT NULL DEFAULT 0,
                    smtp_host      TEXT NOT NULL,
                    smtp_port      INTEGER NOT NULL,
                    smtp_starttls  INTEGER NOT NULL DEFAULT 0
                );

                PRAGMA user_version = 5;
                """);
        }

        if (version < 6)
        {
            Execute("""
                ALTER TABLE outbox ADD COLUMN is_markdown INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version = 6;
                """);
        }

        if (version < 7)
        {
            Execute("""
                CREATE VIRTUAL TABLE IF NOT EXISTS message_fts USING fts5(
                    message_id UNINDEXED,
                    subject,
                    sender,
                    recipients,
                    body,
                    tokenize='trigram'
                );

                INSERT INTO message_fts (message_id, subject, sender, recipients, body)
                SELECT m.id, m.subject, m.from_name || ' ' || m.from_addr,
                       m.to_addrs || ' ' || m.cc_addrs, COALESCE(b.text_plain, '')
                FROM messages m
                LEFT JOIN bodies b ON b.message_row_id = m.id;

                PRAGMA user_version = 7;
                """);
        }

        if (version < 8)
        {
            Execute("""
                ALTER TABLE outbox ADD COLUMN body_html TEXT;
                PRAGMA user_version = 8;
                """);
        }

        if (version < 9)
        {
            Execute("""
                ALTER TABLE messages ADD COLUMN thread_key TEXT;
                CREATE INDEX IF NOT EXISTS ix_messages_account_thread
                    ON messages(account_id, thread_key);
                PRAGMA user_version = 9;
                """);
        }

        if (version < 10)
        {
            Execute("""
                CREATE TABLE IF NOT EXISTS rules (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    account_id  TEXT,
                    field       TEXT NOT NULL DEFAULT 'from',
                    pattern     TEXT NOT NULL,
                    mark_read   INTEGER NOT NULL DEFAULT 0,
                    flag        INTEGER NOT NULL DEFAULT 0,
                    mute        INTEGER NOT NULL DEFAULT 0,
                    enabled     INTEGER NOT NULL DEFAULT 1,
                    hits        INTEGER NOT NULL DEFAULT 0,
                    created_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_rules_enabled ON rules(enabled);
                PRAGMA user_version = 10;
                """);
        }

        if (version < 11)
        {
            Execute("""
                CREATE TABLE IF NOT EXISTS tags (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    name        TEXT NOT NULL UNIQUE,
                    color       TEXT NOT NULL DEFAULT '#0F7B0F',
                    created_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS message_tags (
                    message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                    tag_id     INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                    PRIMARY KEY (message_id, tag_id)
                );

                CREATE INDEX IF NOT EXISTS ix_message_tags_tag ON message_tags(tag_id);
                PRAGMA user_version = 11;
                """);
        }

        if (version < 12)
        {
            Execute("""
                ALTER TABLE outbox ADD COLUMN from_address TEXT NOT NULL DEFAULT '';
                ALTER TABLE outbox ADD COLUMN from_name TEXT NOT NULL DEFAULT '';
                PRAGMA user_version = 12;
                """);
        }

        if (version < 13)
        {
            Execute("""
                CREATE TABLE IF NOT EXISTS snoozed_messages (
                    message_id  INTEGER PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
                    until_utc   TEXT NOT NULL,
                    created_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_snoozed_until ON snoozed_messages(until_utc);
                PRAGMA user_version = 13;
                """);
        }
        if (version < 14)
        {
            Execute("""
                ALTER TABLE rules ADD COLUMN move_to_junk INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version = 14;
                """);
        }

        if (version < 15)
        {
            Execute("""
                ALTER TABLE rules ADD COLUMN target_folder_id INTEGER;
                PRAGMA user_version = 15;
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

    /// <summary>
    /// 在写 schema 前用 VACUUM INTO 生成一致快照。失败不阻塞启动，
    /// 路径固定为“库名.pre-v{旧版本号}.bak”，重复升级不会覆盖最早的一份。
    /// </summary>
    private string? BackupBeforeMigration(int version)
    {
        if (string.IsNullOrEmpty(DatabasePath) || DatabasePath == ":memory:")
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(DatabasePath) ?? string.Empty;
            var file = Path.GetFileNameWithoutExtension(DatabasePath);
            var backup = Path.Combine(directory, $"{file}.pre-v{version}.bak");
            if (File.Exists(backup))
            {
                return backup;
            }

            var escaped = backup.Replace("'", "''", StringComparison.Ordinal);
            Execute($"VACUUM INTO '{escaped}';");
            return File.Exists(backup) ? backup : null;
        }
        catch
        {
            return null;
        }
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
            reader.GetInt32(15),
            reader.IsDBNull(16) ? null : reader.GetString(16));

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
            Size = message.Size,
            ThreadKey = message.ThreadKey
        };

    private static MailProvider ParseProvider(string value)
        => value switch
        {
            "QQ" => MailProvider.QQ,
            "Custom" => MailProvider.Custom,
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

    /// <summary>把 "HH:mm" 规整成两位小时与两位分钟，读坏值回落到默认。</summary>
    private static string NormalizeClock(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var parts = value.Trim().Split(':');

        if (parts.Length != 2
            || !int.TryParse(parts[0], out var hour)
            || !int.TryParse(parts[1], out var minute)
            || hour is < 0 or > 23
            || minute is < 0 or > 59)
        {
            return fallback;
        }

        return $"{hour:00}:{minute:00}";
    }

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

    /// <summary>补齐缺失的全文索引行；用于升级或异常退出后的后台修复。</summary>
    public int BackfillSearchIndex()
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO message_fts (message_id, subject, sender, recipients, body)
                SELECT m.id, m.subject, m.from_name || ' ' || m.from_addr,
                       m.to_addrs || ' ' || m.cc_addrs, COALESCE(b.text_plain, '')
                FROM messages m
                LEFT JOIN bodies b ON b.message_row_id = m.id
                WHERE NOT EXISTS (
                    SELECT 1 FROM message_fts f
                    WHERE CAST(f.message_id AS INTEGER) = m.id
                )
                RETURNING message_id;
                """;
            var added = 0;
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    added++;
                }
            }

            transaction.Commit();
            return added;
        }
    }

    /// <summary>压缩数据库：先截断 WAL，再执行 VACUUM 回收空洞。</summary>
    public void Compact()
    {
        lock (_gate)
        {
            Execute("PRAGMA wal_checkpoint(TRUNCATE); VACUUM;");
        }
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
