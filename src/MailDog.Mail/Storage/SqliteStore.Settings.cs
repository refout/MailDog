using System.Globalization;
using System.Text;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Platform;
using MailDog.Contracts.Serialization;
using Microsoft.Data.Sqlite;

namespace MailDog.Mail.Storage;

public sealed partial class SqliteStore
{
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
            Density = values.GetValueOrDefault("density") == "normal" ? "normal" : "compact",
            Material = values.GetValueOrDefault("material", "mica"),
            BackgroundColor = string.IsNullOrWhiteSpace(values.GetValueOrDefault("backgroundColor"))
                ? null
                : values.GetValueOrDefault("backgroundColor"),
            BackgroundOpacity = int.TryParse(values.GetValueOrDefault("backgroundOpacity"), out var opacity)
                ? Math.Clamp(opacity, 40, 100)
                : 85,
            NotificationsEnabled = !values.TryGetValue("notifications", out var notifications) || notifications != "0",
            NotificationSoundEnabled = !values.TryGetValue("notificationSound", out var notificationSound) || notificationSound != "0",
            NotificationSound = values.GetValueOrDefault("notificationSoundName") is "mail" or "reminder" or "asterisk"
                ? values.GetValueOrDefault("notificationSoundName")!
                : "default",
            NotificationShowSubject = !values.TryGetValue("notificationSubject", out var notificationSubject) || notificationSubject != "0",
            NotificationShowPreview = values.GetValueOrDefault("notificationPreview") == "1",
            NotificationMerge = !values.TryGetValue("notificationMerge", out var notificationMerge) || notificationMerge != "0",
            NotificationInboxOnly = !values.TryGetValue("notificationInboxOnly", out var notificationInboxOnly) || notificationInboxOnly != "0",
            NotificationQuietEnabled = values.GetValueOrDefault("notificationQuiet") == "1",
            NotificationQuietStart = NormalizeClock(values.GetValueOrDefault("notificationQuietStart"), "22:00"),
            NotificationQuietEnd = NormalizeClock(values.GetValueOrDefault("notificationQuietEnd"), "08:00"),
            SyncIntervalMinutes = int.TryParse(values.GetValueOrDefault("syncIntervalMinutes"), out var interval)
                ? Math.Clamp(interval, 1, 60)
                : 1,
            CacheDays = int.TryParse(values.GetValueOrDefault("cacheDays"), out var days) ? days : 90,
            MarkReadOnOpen = !values.TryGetValue("markReadOnOpen", out var markRead) || markRead != "0",
            RunAtStartup = values.GetValueOrDefault("runAtStartup") == "1",
            ShowRemoteImages = values.GetValueOrDefault("showRemoteImages") == "1",
            SaveSentCopy = !values.TryGetValue("saveSentCopy", out var saveSentCopy) || saveSentCopy != "0",
            Signature = values.GetValueOrDefault("signature") ?? string.Empty,
            AccountSignatures = SettingsJson.DeserializeAccountSignatures(values.GetValueOrDefault("accountSignatures")),
            AccountIdentities = SettingsJson.DeserializeAccountIdentities(values.GetValueOrDefault("accountIdentities")),
            Templates = SettingsJson.DeserializeTemplates(values.GetValueOrDefault("templates")),
            AccountNotificationSounds = SettingsJson.DeserializeAccountNotificationSounds(
                values.GetValueOrDefault("accountNotificationSounds")),
            AppLockEnabled = values.GetValueOrDefault("appLockEnabled") == "1",
            AppLockHash = values.GetValueOrDefault("appLockHash") ?? string.Empty,
            AppLockSalt = values.GetValueOrDefault("appLockSalt") ?? string.Empty,
            AppLockTimeoutMinutes = int.TryParse(values.GetValueOrDefault("appLockTimeoutMinutes"), out var lockTimeout)
                ? Math.Clamp(lockTimeout, 0, 240)
                : 15,
            SendDelaySeconds = int.TryParse(values.GetValueOrDefault("sendDelaySeconds"), out var sendDelay)
                ? Math.Clamp(sendDelay, 0, 30)
                : 10,
            RenderMode = values.GetValueOrDefault("renderMode") == "plain" ? "plain" : "html"
        };
    }

    public void SaveSettings(AppSettingsDto settings)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            UpsertSetting(transaction, "theme", settings.Theme);
            UpsertSetting(transaction, "density", settings.Density == "normal" ? "normal" : "compact");
            UpsertSetting(transaction, "material", settings.Material);
            UpsertSetting(transaction, "backgroundColor", settings.BackgroundColor ?? string.Empty);
            UpsertSetting(transaction, "backgroundOpacity", settings.BackgroundOpacity.ToString(CultureInfo.InvariantCulture));
            UpsertSetting(transaction, "notifications", settings.NotificationsEnabled ? "1" : "0");
            UpsertSetting(transaction, "notificationSound", settings.NotificationSoundEnabled ? "1" : "0");
            UpsertSetting(transaction, "notificationSoundName", settings.NotificationSound);
            UpsertSetting(transaction, "notificationSubject", settings.NotificationShowSubject ? "1" : "0");
            UpsertSetting(transaction, "notificationPreview", settings.NotificationShowPreview ? "1" : "0");
            UpsertSetting(transaction, "notificationMerge", settings.NotificationMerge ? "1" : "0");
            UpsertSetting(transaction, "notificationInboxOnly", settings.NotificationInboxOnly ? "1" : "0");
            UpsertSetting(transaction, "notificationQuiet", settings.NotificationQuietEnabled ? "1" : "0");
            UpsertSetting(transaction, "notificationQuietStart", settings.NotificationQuietStart);
            UpsertSetting(transaction, "notificationQuietEnd", settings.NotificationQuietEnd);
            UpsertSetting(transaction, "syncIntervalMinutes", settings.SyncIntervalMinutes.ToString(CultureInfo.InvariantCulture));
            UpsertSetting(transaction, "cacheDays", settings.CacheDays.ToString(CultureInfo.InvariantCulture));
            UpsertSetting(transaction, "markReadOnOpen", settings.MarkReadOnOpen ? "1" : "0");
            UpsertSetting(transaction, "runAtStartup", settings.RunAtStartup ? "1" : "0");
            UpsertSetting(transaction, "showRemoteImages", settings.ShowRemoteImages ? "1" : "0");
            UpsertSetting(transaction, "saveSentCopy", settings.SaveSentCopy ? "1" : "0");
            UpsertSetting(transaction, "signature", settings.Signature);
            UpsertSetting(transaction, "accountSignatures", SettingsJson.SerializeAccountSignatures(settings.AccountSignatures));
            UpsertSetting(transaction, "accountIdentities", SettingsJson.SerializeAccountIdentities(settings.AccountIdentities));
            UpsertSetting(transaction, "templates", SettingsJson.SerializeTemplates(settings.Templates));
            UpsertSetting(transaction, "accountNotificationSounds",
                SettingsJson.SerializeAccountNotificationSounds(settings.AccountNotificationSounds));
            UpsertSetting(transaction, "appLockEnabled", settings.AppLockEnabled ? "1" : "0");
            UpsertSetting(transaction, "appLockHash", settings.AppLockHash);
            UpsertSetting(transaction, "appLockSalt", settings.AppLockSalt);
            UpsertSetting(transaction, "appLockTimeoutMinutes", settings.AppLockTimeoutMinutes.ToString(CultureInfo.InvariantCulture));
            UpsertSetting(transaction, "sendDelaySeconds", settings.SendDelaySeconds.ToString(CultureInfo.InvariantCulture));
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

    public void SetSetting(string key, string value)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            UpsertSetting(transaction, key, value);
            transaction.Commit();
        }
    }

    // 标签

    public IReadOnlyList<TagDto> GetTags()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, name, color FROM tags ORDER BY name COLLATE NOCASE;";
            using var reader = command.ExecuteReader();
            var result = new List<TagDto>();
            while (reader.Read())
            {
                result.Add(new TagDto
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    Color = reader.GetString(2)
                });
            }

            return result;
        }
    }

    public TagDto SaveTag(TagDto tag)
    {
        var name = tag.Name.Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("标签名称不能为空。");
        }

        var color = NormalizeTagColor(tag.Color);
        lock (_gate)
        {
            using (var duplicate = _connection.CreateCommand())
            {
                duplicate.CommandText = """
                    SELECT 1 FROM tags
                    WHERE name = $name COLLATE NOCASE AND id <> $id
                    LIMIT 1;
                    """;
                duplicate.Parameters.AddWithValue("$name", name);
                duplicate.Parameters.AddWithValue("$id", tag.Id);
                if (duplicate.ExecuteScalar() is not null)
                {
                    throw new InvalidOperationException($"标签“{name}”已存在。");
                }
            }

            using var transaction = _connection.BeginTransaction();
            long id;
            if (tag.Id > 0)
            {
                using var update = _connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE tags SET name = $name, color = $color WHERE id = $id;";
                update.Parameters.AddWithValue("$name", name);
                update.Parameters.AddWithValue("$color", color);
                update.Parameters.AddWithValue("$id", tag.Id);
                if (update.ExecuteNonQuery() == 0)
                {
                    throw new KeyNotFoundException("标签不存在。");
                }

                id = tag.Id;
            }
            else
            {
                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO tags (name, color, created_utc)
                    VALUES ($name, $color, $created);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$name", name);
                insert.Parameters.AddWithValue("$color", color);
                insert.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
                id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            transaction.Commit();
            return new TagDto { Id = id, Name = name, Color = color };
        }
    }

    public void DeleteTag(long tagId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM tags WHERE id = $id;";
            command.Parameters.AddWithValue("$id", tagId);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<MessageTagAssignmentDto> GetMessageTags(IReadOnlyList<long> messageIds)
    {
        if (messageIds.Count == 0)
        {
            return [];
        }

        var wanted = messageIds.ToHashSet();
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT message_id, tag_id FROM message_tags;";
            using var reader = command.ExecuteReader();
            var grouped = new Dictionary<long, List<long>>();
            while (reader.Read())
            {
                var messageId = reader.GetInt64(0);
                if (wanted.Contains(messageId))
                {
                    if (!grouped.TryGetValue(messageId, out var tagList))
                    {
                        tagList = [];
                        grouped[messageId] = tagList;
                    }

                    tagList.Add(reader.GetInt64(1));
                }
            }

            return grouped
                .Select(pair => new MessageTagAssignmentDto
                {
                    MessageId = pair.Key,
                    TagIds = pair.Value.ToArray()
                })
                .ToArray();
        }
    }

    public int SetMessageTags(IReadOnlyList<long> messageIds, IReadOnlyList<long> tagIds, bool add)
    {
        if (messageIds.Count == 0 || tagIds.Count == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var affected = 0;
            foreach (var messageId in messageIds.Distinct())
            {
                foreach (var tagId in tagIds.Distinct())
                {
                    using var command = _connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = add
                        ? """
                          INSERT OR IGNORE INTO message_tags (message_id, tag_id)
                          VALUES ($messageId, $tagId);
                          """
                        : """
                          DELETE FROM message_tags
                          WHERE message_id = $messageId AND tag_id = $tagId;
                          """;
                    command.Parameters.AddWithValue("$messageId", messageId);
                    command.Parameters.AddWithValue("$tagId", tagId);
                    affected += command.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            return affected;
        }
    }

    private static string NormalizeTagColor(string? color)
        => color is { Length: 7 } && color[0] == '#'
           && color[1..].All(Uri.IsHexDigit)
            ? color.ToUpperInvariant()
            : "#0F7B0F";

    // 本地来信规则

    public IReadOnlyList<RuleDto> GetRules()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, account_id, field, pattern, mark_read, flag, mute, enabled, hits, move_to_junk, target_folder_id
                FROM rules
                ORDER BY id;
                """;

            using var reader = command.ExecuteReader();
            var result = new List<RuleDto>();

            while (reader.Read())
            {
                result.Add(new RuleDto
                {
                    Id = reader.GetInt64(0),
                    AccountId = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Field = reader.GetString(2),
                    Pattern = reader.GetString(3),
                    MarkRead = reader.GetInt32(4) != 0,
                    Flag = reader.GetInt32(5) != 0,
                    Mute = reader.GetInt32(6) != 0,
                    Enabled = reader.GetInt32(7) != 0,
                    Hits = reader.GetInt32(8),
                    MoveToJunk = !reader.IsDBNull(9) && reader.GetInt32(9) != 0,
                    TargetFolderId = reader.IsDBNull(10) ? null : reader.GetInt64(10)
                });
            }

            return result;
        }
    }

    /// <summary>新增或更新一条规则，返回规则 id。</summary>
    public long SaveRule(RuleDto rule)
    {
        var field = rule.Field is "subject" or "any" ? rule.Field : "from";
        var pattern = rule.Pattern.Trim();
        var accountId = string.IsNullOrWhiteSpace(rule.AccountId) ? null : rule.AccountId;

        lock (_gate)
        {
            if (rule.Id > 0)
            {
                using var update = _connection.CreateCommand();
                update.CommandText = """
                    UPDATE rules
                    SET account_id = $accountId, field = $field, pattern = $pattern,
                        mark_read = $markRead, flag = $flag, mute = $mute, enabled = $enabled,
                        move_to_junk = $moveToJunk, target_folder_id = $targetFolderId
                    WHERE id = $id;
                    """;
                update.Parameters.AddWithValue("$accountId", (object?)accountId ?? DBNull.Value);
                update.Parameters.AddWithValue("$field", field);
                update.Parameters.AddWithValue("$pattern", pattern);
                update.Parameters.AddWithValue("$markRead", rule.MarkRead ? 1 : 0);
                update.Parameters.AddWithValue("$flag", rule.Flag ? 1 : 0);
                update.Parameters.AddWithValue("$mute", rule.Mute ? 1 : 0);
                update.Parameters.AddWithValue("$moveToJunk", rule.MoveToJunk ? 1 : 0);
                update.Parameters.AddWithValue("$targetFolderId", (object?)rule.TargetFolderId ?? DBNull.Value);
                update.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
                update.Parameters.AddWithValue("$id", rule.Id);
                update.ExecuteNonQuery();
                return rule.Id;
            }

            using var insert = _connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO rules (account_id, field, pattern, mark_read, flag, mute, enabled, hits, created_utc, move_to_junk, target_folder_id)
                VALUES ($accountId, $field, $pattern, $markRead, $flag, $mute, $enabled, 0, $created, $moveToJunk, $targetFolderId)
                RETURNING id;
                """;
            insert.Parameters.AddWithValue("$accountId", (object?)accountId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$field", field);
            insert.Parameters.AddWithValue("$pattern", pattern);
            insert.Parameters.AddWithValue("$markRead", rule.MarkRead ? 1 : 0);
            insert.Parameters.AddWithValue("$flag", rule.Flag ? 1 : 0);
            insert.Parameters.AddWithValue("$mute", rule.Mute ? 1 : 0);
            insert.Parameters.AddWithValue("$enabled", rule.Enabled ? 1 : 0);
            insert.Parameters.AddWithValue("$moveToJunk", rule.MoveToJunk ? 1 : 0);
            insert.Parameters.AddWithValue("$targetFolderId", (object?)rule.TargetFolderId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$created", ToDb(DateTimeOffset.UtcNow));
            return Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public bool DeleteRule(long id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM rules WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public void IncrementRuleHits(IReadOnlyList<long> ruleIds)
    {
        if (ruleIds.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();

            foreach (var id in ruleIds)
            {
                using var command = _connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE rules SET hits = hits + 1 WHERE id = $id;";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

}
