using System.Globalization;
using System.Text;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Platform;
using MailDog.Contracts.Serialization;
using Microsoft.Data.Sqlite;

namespace MailDog.Mail.Storage;

public sealed partial class SqliteStore
{
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

    /// <summary>删除超出本地缓存天数的邮件，跳过已加旗标与草稿。</summary>
    public int PruneMessages(int cacheDays)
    {
        var days = Math.Clamp(cacheDays, 1, 3650);
        var cutoff = ToDb(DateTimeOffset.UtcNow.AddDays(-days));

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                DELETE FROM messages
                WHERE date_utc < $cutoff
                  AND (flags & 18) = 0;
                """;
            command.Parameters.AddWithValue("$cutoff", cutoff);
            return command.ExecuteNonQuery();
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
            var newIds = new List<long>();

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
                             to_addrs, cc_addrs, date_utc, size, flags, has_attachments, preview, body_state,
                             thread_key)
                        VALUES
                            ($accountId, $folderId, $uid, $messageId, $subject, $fromName, $fromAddr,
                             $toAddrs, $ccAddrs, $dateUtc, $size, $flags, $hasAttachments, $preview, 0,
                             $threadKey)
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
                            thread_key = COALESCE(excluded.thread_key, messages.thread_key),
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
                    command.Parameters.AddWithValue("$threadKey", (object?)message.ThreadKey ?? DBNull.Value);
                    command.ExecuteNonQuery();

                    long rowId;

                    using (var rowIdCommand = _connection.CreateCommand())
                    {
                        rowIdCommand.Transaction = transaction;
                        rowIdCommand.CommandText = "SELECT id FROM messages WHERE folder_id = $folderId AND uid = $uid;";
                        rowIdCommand.Parameters.AddWithValue("$folderId", folderId);
                        rowIdCommand.Parameters.AddWithValue("$uid", message.Uid);
                        rowId = Convert.ToInt64(rowIdCommand.ExecuteScalar());
                        UpdateMessageFts(transaction, rowId);
                    }

                    if (!existing.Contains(message.Uid))
                    {
                        newUids.Add(message.Uid);
                        newIds.Add(rowId);
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

            return new NewMailResult(newUids.Count, latest, newIds);
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
                       to_addrs, cc_addrs, date_utc, size, flags, has_attachments, preview, body_state,
                       thread_key
                FROM messages
                WHERE folder_id = $folderId
                  AND NOT EXISTS (
                      SELECT 1 FROM snoozed_messages s
                      WHERE s.message_id = messages.id AND s.until_utc > $now
                  )
                  AND (
                      $beforeDate IS NULL
                      OR date_utc < $beforeDate
                      OR (date_utc = $beforeDate AND id < $beforeId)
                  )
                ORDER BY date_utc DESC, id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$folderId", folderId);
            command.Parameters.AddWithValue("$now", ToDb(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$beforeDate", (object?)ToDbNullable(beforeDate) ?? DBNull.Value);
            command.Parameters.AddWithValue("$beforeId", beforeId ?? 0);
            command.Parameters.AddWithValue("$limit", pageSize + 1);

            return ReadPage(command, pageSize);
        }
    }

    /// <summary>按最近来信聚合联系人，供写信页收件人自动补全。</summary>
    public IReadOnlyList<ContactSuggestionDto> QueryContacts(int limit)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT from_addr,
                       COALESCE(MAX(NULLIF(TRIM(from_name), '')), ''),
                       MAX(date_utc)
                FROM messages
                WHERE TRIM(from_addr) <> ''
                GROUP BY LOWER(TRIM(from_addr))
                ORDER BY MAX(date_utc) DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

            var items = new List<ContactSuggestionDto>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new ContactSuggestionDto
                {
                    Address = reader.GetString(0).Trim(),
                    Name = reader.GetString(1).Trim(),
                    LastSeen = FromDb(reader.GetString(2))
                });
            }

            return items;
        }
    }

    /// <summary>按文件夹角色跨账户查询邮件（统一收件箱等）。</summary>
    public MailListPage QueryMessagesByRole(FolderRole role, DateTimeOffset? beforeDate, long? beforeId, int limit)
    {
        var pageSize = Math.Clamp(limit, 1, 200);

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT m.id, m.account_id, m.folder_id, m.uid, m.message_id, m.subject, m.from_name, m.from_addr,
                       m.to_addrs, m.cc_addrs, m.date_utc, m.size, m.flags, m.has_attachments, m.preview, m.body_state,
                       m.thread_key
                FROM messages m
                JOIN folders f ON f.id = m.folder_id
                WHERE f.role = $role
                  AND NOT EXISTS (
                      SELECT 1 FROM snoozed_messages s
                      WHERE s.message_id = m.id AND s.until_utc > $now
                  )
                  AND (
                      $beforeDate IS NULL
                      OR m.date_utc < $beforeDate
                      OR (m.date_utc = $beforeDate AND m.id < $beforeId)
                  )
                ORDER BY m.date_utc DESC, m.id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$role", role.ToString());
            command.Parameters.AddWithValue("$now", ToDb(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$beforeDate", (object?)ToDbNullable(beforeDate) ?? DBNull.Value);
            command.Parameters.AddWithValue("$beforeId", beforeId ?? 0);
            command.Parameters.AddWithValue("$limit", pageSize + 1);

            return ReadPage(command, pageSize);
        }
    }

    private static MailListPage ReadPage(SqliteCommand command, int pageSize)
    {
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

    /// <summary>导出 mbox 时按账户/文件夹读取全部本地索引；正文仍由 IMAP 按需拉取。</summary>
    public IReadOnlyList<MessageRecord> GetMessagesForExport(string accountId, long? folderId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, account_id, folder_id, uid, message_id, subject, from_name, from_addr,
                       to_addrs, cc_addrs, date_utc, size, flags, has_attachments, preview, body_state,
                       thread_key
                FROM messages
                WHERE account_id = $accountId AND ($folderId IS NULL OR folder_id = $folderId)
                ORDER BY date_utc ASC, id ASC;
                """;
            command.Parameters.AddWithValue("$accountId", accountId);
            command.Parameters.AddWithValue("$folderId", (object?)folderId ?? DBNull.Value);

            var result = new List<MessageRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(ReadMessage(reader));
            }

            return result;
        }
    }

    /// <summary>按规则扫描历史邮件；调用方用小 limit 做预览，用大 limit 做回溯。</summary>
    public IReadOnlyList<MessageRecord> FindRuleMatches(RuleDto rule, int limit = 500)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, account_id, folder_id, uid, message_id, subject, from_name, from_addr,
                       to_addrs, cc_addrs, date_utc, size, flags, has_attachments, preview, body_state,
                       thread_key
                FROM messages
                WHERE ($accountId IS NULL OR account_id = $accountId)
                ORDER BY date_utc DESC, id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue(
                "$accountId",
                string.IsNullOrWhiteSpace(rule.AccountId) ? DBNull.Value : rule.AccountId);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit + 1, 1, 5000));

            var matches = new List<MessageRecord>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var message = ReadMessage(reader);
                if (RuleMatcher.Matches(rule, message.Subject, message.FromName, message.FromAddress))
                {
                    matches.Add(message);
                    if (matches.Count >= limit)
                    {
                        break;
                    }
                }
            }

            return matches;
        }
    }

    public MessageRecord? GetMessage(long messageId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, account_id, folder_id, uid, message_id, subject, from_name, from_addr,
                       to_addrs, cc_addrs, date_utc, size, flags, has_attachments, preview, body_state,
                       thread_key
                FROM messages WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", messageId);

            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadMessage(reader) : null;
        }
    }

    public void MoveMessageToFolder(long messageId, long targetFolderId)
    {
        lock (_gate)
        {
            long sourceFolderId;
            string accountId;

            using (var lookup = _connection.CreateCommand())
            {
                lookup.CommandText = "SELECT folder_id, account_id FROM messages WHERE id = $id;";
                lookup.Parameters.AddWithValue("$id", messageId);
                using var reader = lookup.ExecuteReader();
                if (!reader.Read())
                {
                    throw new KeyNotFoundException("邮件不存在。");
                }

                sourceFolderId = reader.GetInt64(0);
                accountId = reader.GetString(1);
            }

            if (sourceFolderId == targetFolderId)
            {
                return;
            }

            using (var lookup = _connection.CreateCommand())
            {
                lookup.CommandText = "SELECT account_id FROM folders WHERE id = $id;";
                lookup.Parameters.AddWithValue("$id", targetFolderId);
                var targetAccount = lookup.ExecuteScalar() as string;
                if (targetAccount is null)
                {
                    throw new KeyNotFoundException("目标文件夹不存在。");
                }

                if (!string.Equals(targetAccount, accountId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("不能把邮件移动到其他账户的文件夹。");
                }
            }

            using var transaction = _connection.BeginTransaction();

            using (var move = _connection.CreateCommand())
            {
                move.Transaction = transaction;
                move.CommandText = """
                    UPDATE messages
                    SET folder_id = $targetFolderId,
                        uid = -id
                    WHERE id = $id;
                    """;
                move.Parameters.AddWithValue("$targetFolderId", targetFolderId);
                move.Parameters.AddWithValue("$id", messageId);
                move.ExecuteNonQuery();
            }

            using (var flags = _connection.CreateCommand())
            {
                flags.Transaction = transaction;
                flags.CommandText = "DELETE FROM pending_flags WHERE message_id = $id;";
                flags.Parameters.AddWithValue("$id", messageId);
                flags.ExecuteNonQuery();
            }

            transaction.Commit();

            RecountFolder(sourceFolderId);
            RecountFolder(targetFolderId);
        }
    }

    public void DeleteMessage(long messageId)
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
                    throw new KeyNotFoundException("邮件不存在。");
                }

                folderId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
            }

            using var transaction = _connection.BeginTransaction();

            using (var flags = _connection.CreateCommand())
            {
                flags.Transaction = transaction;
                flags.CommandText = "DELETE FROM pending_flags WHERE message_id = $id;";
                flags.Parameters.AddWithValue("$id", messageId);
                flags.ExecuteNonQuery();
            }

            using (var delete = _connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM messages WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", messageId);
                delete.ExecuteNonQuery();
            }

            transaction.Commit();

            RecountFolder(folderId);
        }
    }

    public void RenameFolder(long folderId, string fullName, string displayName)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE folders
                SET full_name = $fullName,
                    display_name = $displayName
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", folderId);
            command.Parameters.AddWithValue("$fullName", fullName);
            command.Parameters.AddWithValue("$displayName", displayName);
            command.ExecuteNonQuery();
        }
    }

    public void DeleteFolder(long folderId)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            using (var flags = _connection.CreateCommand())
            {
                flags.Transaction = transaction;
                flags.CommandText = """
                    DELETE FROM pending_flags
                    WHERE message_id IN (SELECT id FROM messages WHERE folder_id = $id);
                    """;
                flags.Parameters.AddWithValue("$id", folderId);
                flags.ExecuteNonQuery();
            }

            using (var delete = _connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM folders WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", folderId);
                delete.ExecuteNonQuery();
            }

            transaction.Commit();
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

    public void UpdateFlagMany(IReadOnlyList<long> messageIds, MailFlags flag, bool value)
    {
        if (messageIds.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            var folders = new HashSet<long>();

            foreach (var messageId in messageIds)
            {
                long folderId;

                using (var lookup = _connection.CreateCommand())
                {
                    lookup.CommandText = "SELECT folder_id FROM messages WHERE id = $id;";
                    lookup.Parameters.AddWithValue("$id", messageId);
                    var result = lookup.ExecuteScalar();
                    if (result is null)
                    {
                        continue;
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

                folders.Add(folderId);
            }

            foreach (var folderId in folders)
            {
                RecountFolder(folderId);
            }
        }
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

    /// <summary>把一封邮件暂时收进稍后提醒；本地先标已读，到期再恢复未读。</summary>
    public void SnoozeMessage(long messageId, DateTimeOffset untilUtc)
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
                    throw new KeyNotFoundException("邮件不存在。");
                }

                folderId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
            }

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO snoozed_messages (message_id, until_utc, created_utc)
                    VALUES ($id, $until, $created)
                    ON CONFLICT(message_id) DO UPDATE SET until_utc = excluded.until_utc;
                    """;
                command.Parameters.AddWithValue("$id", messageId);
                command.Parameters.AddWithValue("$until", ToDb(untilUtc));
                command.Parameters.AddWithValue("$created", ToDb(DateTimeOffset.UtcNow));
                command.ExecuteNonQuery();
            }

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "UPDATE messages SET flags = flags | $seen WHERE id = $id;";
                command.Parameters.AddWithValue("$seen", (int)MailFlags.Seen);
                command.Parameters.AddWithValue("$id", messageId);
                command.ExecuteNonQuery();
            }

            RecountFolder(folderId);
        }
    }

    /// <summary>取出已到期的稍后提醒，删除记录并把邮件重新标为未读。</summary>
    public IReadOnlyList<SnoozedMessageRecord> WakeDueSnoozes(DateTimeOffset now)
    {
        lock (_gate)
        {
            var due = new List<SnoozedMessageRecord>();
            var folderIds = new HashSet<long>();

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT s.message_id, m.account_id, m.folder_id
                    FROM snoozed_messages s
                    JOIN messages m ON m.id = s.message_id
                    WHERE s.until_utc <= $now;
                    """;
                command.Parameters.AddWithValue("$now", ToDb(now));

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    due.Add(new SnoozedMessageRecord(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetInt64(2)));
                }
            }

            foreach (var item in due)
            {
                using (var delete = _connection.CreateCommand())
                {
                    delete.CommandText = "DELETE FROM snoozed_messages WHERE message_id = $id;";
                    delete.Parameters.AddWithValue("$id", item.MessageId);
                    delete.ExecuteNonQuery();
                }

                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "UPDATE messages SET flags = flags & ~$seen WHERE id = $id;";
                    command.Parameters.AddWithValue("$seen", (int)MailFlags.Seen);
                    command.Parameters.AddWithValue("$id", item.MessageId);
                    command.ExecuteNonQuery();
                }

                folderIds.Add(item.FolderId);
            }

            foreach (var folderId in folderIds)
            {
                RecountFolder(folderId);
            }

            return due;
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

            UpdateMessageFts(transaction, messageId);
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

}
