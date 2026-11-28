using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Platform;
using MailDog.Engine.Ipc;
using MailDog.Engine.Logging;
using MailDog.Engine.Mime;
using MailDog.Engine.Tray;
using MailDog.Mail.Accounts;
using MailDog.Mail.Auth;
using MailDog.Mail.Imap;
using MailDog.Mail.Smtp;
using MailDog.Mail.Storage;
using MailKit.Net.Imap;
using MailKit.Security;

namespace MailDog.Engine.Hosting;

public sealed partial class EngineHost
{
    /// <summary>要不要弹、弹一条还是合并，全在这里决定；真正弹窗放到批次末尾统一做。</summary>
    /// <summary>把本地规则套到这一批新邮件上，返回最新一封是否应该静音通知。</summary>
    private bool ApplyRules(NewMailEvent message)
    {
        try
        {
            if (message.NewMessageIds.Length == 0)
            {
                return false;
            }

            var rules = _store.GetRules();

            if (rules.Count == 0)
            {
                return false;
            }

            var hits = new List<long>();
            var mutedIds = new HashSet<long>();

            foreach (var id in message.NewMessageIds)
            {
                var record = _store.GetMessage(id);

                if (record is null)
                {
                    continue;
                }

                foreach (var rule in rules)
                {
                    if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Pattern))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(rule.AccountId) && rule.AccountId != message.AccountId)
                    {
                        continue;
                    }

                    if (!MatchesRule(rule, record))
                    {
                        continue;
                    }

                    hits.Add(rule.Id);

                    if (rule.Mute)
                    {
                        mutedIds.Add(id);
                    }

                    if (rule.MarkRead || rule.Flag)
                    {
                        ApplyRuleFlagsAsync(id, rule.MarkRead, rule.Flag);
                    }

                    if (rule.MoveToJunk || rule.TargetFolderId is not null)
                    {
                        ApplyRuleMoveAsync(id, message.AccountId, rule.TargetFolderId);
                    }
                }
            }

            if (hits.Count > 0)
            {
                _store.IncrementRuleHits(hits.Distinct().ToArray());
            }

            return message.Latest is not null && mutedIds.Contains(message.Latest.Id);
        }
        catch (Exception exception)
        {
            _log.Warn($"应用本地规则失败：{exception.Message}");
            return false;
        }
    }

    private static bool MatchesRule(RuleDto rule, MessageRecord record)
        => RuleMatcher.Matches(rule, record.Subject, record.FromName, record.FromAddress);

    private void ApplyRuleFlagsAsync(long messageId, bool markRead, bool flag)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (markRead)
                {
                    await _sync.SetSeenAsync(messageId, true, CancellationToken.None).ConfigureAwait(false);
                }

                if (flag)
                {
                    await _sync.SetFlaggedAsync(messageId, true, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                _log.Warn($"规则应用到邮件 #{messageId} 失败：{exception.Message}");
            }
        });
    }

    /// <summary>历史回溯：先返回接受数量，再在后台按批标旗标、逐封移动，避免 IPC 请求超时。</summary>
    private RuleHistoryApplyResultDto ApplyRuleHistory(long ruleId, int limit)
    {
        var rule = _store.GetRules().FirstOrDefault(item => item.Id == ruleId)
            ?? throw new KeyNotFoundException("规则不存在。");
        var matches = _store.FindRuleMatches(rule, Math.Clamp(limit, 1, 1000));
        if (matches.Count == 0)
        {
            return new RuleHistoryApplyResultDto { Accepted = 0, Message = "没有匹配的历史邮件。" };
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var ids = matches.Select(item => item.Id).ToArray();
                if (rule.MarkRead)
                {
                    await _sync.SetSeenManyAsync(ids, true, CancellationToken.None).ConfigureAwait(false);
                }

                if (rule.Flag)
                {
                    await _sync.SetFlaggedManyAsync(ids, true, CancellationToken.None).ConfigureAwait(false);
                }

                foreach (var message in matches)
                {
                    if (rule.TargetFolderId is not { } targetId && !rule.MoveToJunk)
                    {
                        continue;
                    }

                    var target = rule.TargetFolderId is { } id
                        ? _store.GetFolder(id)
                        : _store.GetFolders(message.AccountId).FirstOrDefault(folder => folder.Role == FolderRole.Junk);

                    if (target is null
                        || !string.Equals(target.AccountId, message.AccountId, StringComparison.Ordinal)
                        || message.FolderId == target.Id)
                    {
                        continue;
                    }

                    await _sync.MoveMessageAsync(message.Id, target.Id, CancellationToken.None).ConfigureAwait(false);
                    _ipc.Broadcast(IpcMessageTypes.MailChanged, new MailChangedEvent
                    {
                        AccountId = message.AccountId,
                        MessageId = message.Id,
                        FolderId = target.Id,
                        Change = "move"
                    });
                }

                _store.IncrementRuleHits([rule.Id]);
                _tray?.UpdateUnreadCount(TotalUnread());
                _log.Info($"规则 #{rule.Id} 回溯处理了 {matches.Count} 封历史邮件。");
            }
            catch (Exception exception)
            {
                _log.Warn($"规则 #{rule.Id} 回溯失败：{exception.Message}");
            }
        });

        return new RuleHistoryApplyResultDto
        {
            Accepted = matches.Count,
            Truncated = matches.Count >= Math.Clamp(limit, 1, 1000),
            Message = $"已开始处理 {matches.Count} 封匹配邮件，完成前可以继续使用其他功能。"
        };
    }

    private void ApplyRuleMoveAsync(long messageId, string accountId, long? targetFolderId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var target = targetFolderId is { } id
                    ? _store.GetFolder(id)
                    : _store.GetFolders(accountId).FirstOrDefault(folder => folder.Role == FolderRole.Junk);

                if (target is null)
                {
                    _log.Warn($"规则找不到目标文件夹，无法移动邮件 #{messageId}。");
                    return;
                }

                if (!string.Equals(target.AccountId, accountId, StringComparison.Ordinal))
                {
                    _log.Warn($"规则目标文件夹 {target.Id} 不属于账号 {accountId}，跳过移动邮件 #{messageId}。");
                    return;
                }

                if (_store.GetMessage(messageId)?.FolderId == target.Id)
                {
                    return;
                }

                await _sync.MoveMessageAsync(messageId, target.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log.Warn($"规则把邮件 #{messageId} 移动到目标文件夹失败：{exception.Message}");
            }
        });
    }

}
