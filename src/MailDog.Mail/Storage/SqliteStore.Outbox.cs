using System.Globalization;
using System.Text;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Platform;
using MailDog.Contracts.Serialization;
using Microsoft.Data.Sqlite;

namespace MailDog.Mail.Storage;

public sealed partial class SqliteStore
{
    // Outbox

    public long InsertOutbox(OutboxRecord record, IReadOnlyList<OutboxAttachmentRecord> attachments)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            long id;
            using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO outbox
                        (account_id, to_addrs, cc_addrs, bcc_addrs, subject, body_text, in_reply_to,
                         message_id, status, attempts, next_attempt_utc, last_error, created_utc, sent_utc,
                         is_markdown, body_html, from_address, from_name)
                    VALUES
                        ($accountId, $toAddrs, $ccAddrs, $bccAddrs, $subject, $bodyText, $inReplyTo,
                         $messageId, $status, $attempts, $nextAttemptUtc, $lastError, $createdUtc, $sentUtc,
                         $isMarkdown, $bodyHtml, $fromAddress, $fromName)
                    RETURNING id;
                    """;
                command.Parameters.AddWithValue("$accountId", record.AccountId);
                command.Parameters.AddWithValue("$toAddrs", record.ToAddresses);
                command.Parameters.AddWithValue("$ccAddrs", record.CcAddresses);
                command.Parameters.AddWithValue("$bccAddrs", record.BccAddresses);
                command.Parameters.AddWithValue("$subject", record.Subject);
                command.Parameters.AddWithValue("$bodyText", record.BodyText);
                command.Parameters.AddWithValue("$inReplyTo", (object?)record.InReplyTo ?? DBNull.Value);
                command.Parameters.AddWithValue("$messageId", record.MessageId);
                command.Parameters.AddWithValue("$status", record.Status);
                command.Parameters.AddWithValue("$attempts", record.Attempts);
                command.Parameters.AddWithValue("$nextAttemptUtc", ToDb(record.NextAttemptUtc));
                command.Parameters.AddWithValue("$lastError", (object?)record.LastError ?? DBNull.Value);
                command.Parameters.AddWithValue("$createdUtc", ToDb(record.CreatedUtc));
                command.Parameters.AddWithValue("$sentUtc", record.SentUtc is { } sent ? ToDb(sent) : DBNull.Value);
                command.Parameters.AddWithValue("$isMarkdown", record.IsMarkdown ? 1 : 0);
                command.Parameters.AddWithValue("$bodyHtml", (object?)record.BodyHtml ?? DBNull.Value);
                command.Parameters.AddWithValue("$fromAddress", record.FromAddress);
                command.Parameters.AddWithValue("$fromName", record.FromName);
                id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            foreach (var attachment in attachments)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO outbox_attachments (outbox_id, file_name, stored_path, mime_type, size)
                    VALUES ($outboxId, $fileName, $storedPath, $mimeType, $size);
                    """;
                command.Parameters.AddWithValue("$outboxId", id);
                command.Parameters.AddWithValue("$fileName", attachment.FileName);
                command.Parameters.AddWithValue("$storedPath", attachment.StoredPath);
                command.Parameters.AddWithValue("$mimeType", attachment.MimeType);
                command.Parameters.AddWithValue("$size", attachment.Size);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            return id;
        }
    }

    public OutboxRecord? GetOutbox(long id)
    {
        lock (_gate)
        {
            OutboxRecord? result = null;

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT id, account_id, to_addrs, cc_addrs, bcc_addrs, subject, body_text, in_reply_to,
                           message_id, status, attempts, next_attempt_utc, last_error, created_utc, sent_utc,
                           is_markdown, body_html, from_address, from_name
                    FROM outbox WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", id);

                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    result = ReadOutbox(reader);
                }
            }

            return result is null
                ? null
                : result with { Attachments = LoadOutboxAttachments(result.Id) };
        }
    }

    public IReadOnlyList<OutboxRecord> GetActiveOutbox(int limit = 100)
    {
        lock (_gate)
        {
            var rows = new List<OutboxRecord>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT id, account_id, to_addrs, cc_addrs, bcc_addrs, subject, body_text, in_reply_to,
                           message_id, status, attempts, next_attempt_utc, last_error, created_utc, sent_utc,
                           is_markdown, body_html, from_address, from_name
                    FROM outbox
                    WHERE status IN ('pending', 'sending', 'failed')
                    ORDER BY created_utc DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(ReadOutbox(reader));
                }
            }

            return rows
                .Select(row => row with { Attachments = LoadOutboxAttachments(row.Id) })
                .ToArray();
        }
    }

    public bool DeleteOutbox(long id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM outbox WHERE id = $id AND status != 'sending';";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<OutboxRecord> GetDueOutbox(DateTimeOffset now, int limit)
    {        lock (_gate)
        {
            var rows = new List<OutboxRecord>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT id, account_id, to_addrs, cc_addrs, bcc_addrs, subject, body_text, in_reply_to,
                           message_id, status, attempts, next_attempt_utc, last_error, created_utc, sent_utc,
                           is_markdown, body_html, from_address, from_name
                    FROM outbox
                    WHERE status = 'pending' AND next_attempt_utc <= $now
                    ORDER BY next_attempt_utc
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$now", ToDb(now));
                command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(ReadOutbox(reader));
                }
            }

            return rows
                .Select(row => row with { Attachments = LoadOutboxAttachments(row.Id) })
                .ToArray();
        }
    }

    /// <summary>标记一次发送尝试，返回累计尝试次数。</summary>
    public int BeginOutboxAttempt(long id, DateTimeOffset nextAttemptUtc)
    {
        lock (_gate)
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE outbox
                    SET status = 'sending', attempts = attempts + 1, next_attempt_utc = $next
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$next", ToDb(nextAttemptUtc));
                command.ExecuteNonQuery();
            }

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT attempts FROM outbox WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                var value = command.ExecuteScalar();
                return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
        }
    }

    public void CompleteOutbox(long id, DateTimeOffset sentUtc)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox
                SET status = 'sent', sent_utc = $sent, last_error = NULL
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$sent", ToDb(sentUtc));
            command.ExecuteNonQuery();
        }
    }

    public void FailOutbox(long id, DateTimeOffset nextAttemptUtc, string error)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox
                SET status = 'pending', next_attempt_utc = $next, last_error = $error
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$next", ToDb(nextAttemptUtc));
            command.Parameters.AddWithValue("$error", error);
            command.ExecuteNonQuery();
        }
    }

    public void MarkOutboxFailed(long id, string error)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox
                SET status = 'failed', last_error = $error
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$error", error);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>进程崩溃时可能留下 sending 状态，启动后重新排队。</summary>
    public int RecoverInterruptedOutbox()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE outbox
                SET status = 'pending', next_attempt_utc = $now
                WHERE status = 'sending';
                """;
            command.Parameters.AddWithValue("$now", ToDb(DateTimeOffset.UtcNow));
            return command.ExecuteNonQuery();
        }
    }

    // Pending flags

    public void QueuePendingFlag(long messageId, string flag, bool value, DateTimeOffset nextAttemptUtc)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            using (var delete = _connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM pending_flags WHERE message_id = $messageId AND flag = $flag;";
                delete.Parameters.AddWithValue("$messageId", messageId);
                delete.Parameters.AddWithValue("$flag", flag);
                delete.ExecuteNonQuery();
            }

            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO pending_flags
                        (message_id, flag, value, attempts, next_attempt_utc, last_error, created_utc)
                    VALUES ($messageId, $flag, $value, 0, $next, NULL, $created);
                    """;
                insert.Parameters.AddWithValue("$messageId", messageId);
                insert.Parameters.AddWithValue("$flag", flag);
                insert.Parameters.AddWithValue("$value", value ? 1 : 0);
                insert.Parameters.AddWithValue("$next", ToDb(nextAttemptUtc));
                insert.Parameters.AddWithValue("$created", ToDb(DateTimeOffset.UtcNow));
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<PendingFlagRecord> GetDuePendingFlags(DateTimeOffset now, int limit)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, message_id, flag, value, attempts, next_attempt_utc, last_error, created_utc
                FROM pending_flags
                WHERE next_attempt_utc <= $now
                ORDER BY next_attempt_utc
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$now", ToDb(now));
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));

            using var reader = command.ExecuteReader();
            var items = new List<PendingFlagRecord>();
            while (reader.Read())
            {
                items.Add(new PendingFlagRecord(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetInt32(3) != 0,
                    reader.GetInt32(4),
                    FromDb(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    FromDb(reader.GetString(7))));
            }

            return items;
        }
    }

    public void CompletePendingFlag(long id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM pending_flags WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void FailPendingFlag(long id, DateTimeOffset nextAttemptUtc, string error)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE pending_flags
                SET attempts = attempts + 1, next_attempt_utc = $next, last_error = $error
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$next", ToDb(nextAttemptUtc));
            command.Parameters.AddWithValue("$error", error);
            command.ExecuteNonQuery();
        }
    }

    private OutboxRecord ReadOutbox(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetInt32(10),
            FromDb(reader.GetString(11)),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            FromDb(reader.GetString(13)),
            reader.IsDBNull(14) ? null : FromDb(reader.GetString(14)),
            [],
            reader.GetInt32(15) != 0,
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.GetString(17),
            reader.GetString(18));

    private OutboxAttachmentRecord[] LoadOutboxAttachments(long outboxId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, file_name, stored_path, mime_type, size
            FROM outbox_attachments WHERE outbox_id = $id ORDER BY id;
            """;
        command.Parameters.AddWithValue("$id", outboxId);

        using var reader = command.ExecuteReader();
        var result = new List<OutboxAttachmentRecord>();
        while (reader.Read())
        {
            result.Add(new OutboxAttachmentRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4)));
        }

        return result.ToArray();
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

}
