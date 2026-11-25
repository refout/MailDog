using System.Text.Json;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Serialization;

namespace MailDog.Cli;

/// <summary>只读命令的实现。全部经命名管道交给 Engine，命令行自己不开数据库、不发信。</summary>
internal static class Commands
{
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(60);

    public static async Task<int> AccountsAsync(IpcClient client, Arguments args, CancellationToken cancellationToken)
    {
        var accounts = (await client.RequestAsync<AccountsResponse>(
            IpcMessageTypes.GetAccounts,
            new GetAccountsRequest(),
            null,
            cancellationToken).ConfigureAwait(false)).Items;

        if (accounts.Length == 0)
        {
            if (!args.Json)
            {
                Console.WriteLine("还没有添加邮箱账号。先在界面里加一个，或者用 outlook / QQ 授权码登录。");
            }

            return 0;
        }

        foreach (var account in accounts)
        {
            var unread = await CountUnreadAsync(client, account.Id, cancellationToken).ConfigureAwait(false);

            if (args.Json)
            {
                Console.WriteLine(Output.AccountJson(account, unread));
            }
            else
            {
                var name = !string.IsNullOrWhiteSpace(account.DisplayName)
                    && !string.Equals(account.DisplayName, account.Email, StringComparison.OrdinalIgnoreCase)
                    ? $"（{account.DisplayName}）"
                    : string.Empty;
                Console.WriteLine($"{account.Email}{name}  {account.Status}  未读 {unread}");
            }
        }

        return 0;
    }

    public static async Task<int> FoldersAsync(IpcClient client, Arguments args, CancellationToken cancellationToken)
    {
        var account = await FindAccountAsync(client, args.Value("--account"), cancellationToken).ConfigureAwait(false);
        AccountDto[] accounts = account is null
            ? await GetAccountsAsync(client, cancellationToken).ConfigureAwait(false)
            : [account];

        foreach (var item in accounts)
        {
            var folders = await GetFoldersAsync(client, item.Id, cancellationToken).ConfigureAwait(false);

            if (args.Json)
            {
                foreach (var folder in folders)
                {
                    Console.WriteLine(Output.FolderJson(folder));
                }

                continue;
            }

            Console.WriteLine($"{item.Email}");

            foreach (var folder in folders)
            {
                var unread = folder.UnreadCount > 0 ? $"  未读 {folder.UnreadCount}" : string.Empty;
                Console.WriteLine($"  {folder.Role,-8} {folder.Name}  （共 {folder.TotalCount} 封，id {folder.Id}）{unread}");
            }
        }

        return 0;
    }

    public static async Task<int> ListAsync(IpcClient client, Arguments args, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(args.IntValue("--limit", 20), 1, 500);
        var scope = await ResolveScopeAsync(client, args, cancellationToken).ConfigureAwait(false);

        var page = await client.RequestAsync<MessagesPageResponse>(
            IpcMessageTypes.GetMessages,
            new GetMessagesRequest
            {
                FolderId = scope.FolderId ?? 0,
                Role = scope.Role,
                Limit = limit
            },
            null,
            cancellationToken).ConfigureAwait(false);

        var items = args.Has("--unread")
            ? page.Items.Where(item => Output.IsUnread(item.Flags)).ToArray()
            : page.Items;

        if (args.Json)
        {
            foreach (var item in items)
            {
                Console.WriteLine(Output.SummaryJson(item));
            }

            return 0;
        }

        if (items.Length == 0)
        {
            Console.WriteLine($"{scope.Label}：没有邮件。");
            return 0;
        }

        Console.WriteLine($"{scope.Label}（{items.Length} 封{(page.HasMore ? "，还有更多" : string.Empty)}）");

        foreach (var item in items)
        {
            Console.WriteLine(Output.SummaryLine(item));
        }

        return 0;
    }

    public static async Task<int> SearchAsync(IpcClient client, Arguments args, CancellationToken cancellationToken)
    {
        if (args.Positionals.Count == 0 || string.IsNullOrWhiteSpace(args.Positionals[0]))
        {
            throw new UsageException("search 需要一个关键词，例如 maildog search 发票");
        }

        var query = string.Join(' ', args.Positionals);
        var limit = Math.Clamp(args.IntValue("--limit", 30), 1, 200);
        var account = await FindAccountAsync(client, args.Value("--account"), cancellationToken).ConfigureAwait(false);
        var folderIds = args.Value("--folder") is { } folderKey
            ? await ResolveFolderIdsAsync(client, account, folderKey, cancellationToken).ConfigureAwait(false)
            : null;

        // 跨文件夹搜索才会带回摘要；按文件夹过滤放在本地做，免得再补一轮 IPC。
        var response = await client.RequestAsync<MessageSearchResponse>(
            IpcMessageTypes.SearchMessages,
            new SearchMessagesRequest
            {
                Query = query,
                Limit = limit,
                AllFolders = true
            },
            null,
            cancellationToken).ConfigureAwait(false);

        var items = response.Items;

        if (account is not null)
        {
            items = [.. items.Where(item => string.Equals(item.AccountId, account.Id, StringComparison.OrdinalIgnoreCase))];
        }

        if (folderIds is not null)
        {
            items = [.. items.Where(item => folderIds.Contains(item.FolderId))];
        }

        items = [.. items.Take(limit)];

        if (args.Json)
        {
            foreach (var item in items)
            {
                Console.WriteLine(Output.SummaryJson(item));
            }

            return 0;
        }

        if (items.Length == 0)
        {
            Console.WriteLine($"没找到包含「{query}」的邮件。");
            return 0;
        }

        Console.WriteLine($"包含「{query}」的邮件（{items.Length} 封）");

        foreach (var item in items)
        {
            Console.WriteLine(Output.SummaryLine(item));
        }

        return 0;
    }

    public static async Task<int> ShowAsync(IpcClient client, Arguments args, CancellationToken cancellationToken)
    {
        var messageId = ParseLong(args.Positionals, 0, "show <邮件 id>");
        var maxChars = Math.Max(0, args.IntValue("--max-chars", 0));
        var body = await GetBodyAsync(client, messageId, cancellationToken).ConfigureAwait(false);

        var rawText = body.PlainText ?? string.Empty;
        var rawHtml = body.Html ?? string.Empty;

        if (args.Json)
        {
            var jsonText = args.Has("--headers") ? null : TruncateIfAny(body.PlainText, maxChars);
            var jsonHtml = args.Has("--headers") ? null : TruncateIfAny(body.Html, maxChars);
            Console.WriteLine(BuildBodyJson(body, jsonText, jsonHtml));
            return 0;
        }

        Console.WriteLine($"ID      {body.MessageId}");
        Console.WriteLine($"主题    {(string.IsNullOrWhiteSpace(body.Subject) ? "（无主题）" : body.Subject)}");
        Console.WriteLine($"发件人  {(string.IsNullOrWhiteSpace(body.FromName) ? body.FromAddress : $"{body.FromName} <{body.FromAddress}>")}");
        Console.WriteLine($"收件人  {string.Join(", ", body.ToAddresses)}");

        if (body.CcAddresses.Length > 0)
        {
            Console.WriteLine($"抄送    {string.Join(", ", body.CcAddresses)}");
        }

        Console.WriteLine($"时间    {Output.LocalTime(body.Date)}");

        if (body.Attachments.Length > 0)
        {
            Console.WriteLine("附件：");

            for (var index = 0; index < body.Attachments.Length; index++)
            {
                var attachment = body.Attachments[index];
                var inline = attachment.IsInline ? "（内嵌）" : string.Empty;
                Console.WriteLine($"  {index}) {attachment.FileName}  {attachment.Size} 字节{inline}");
            }
        }

        if (args.Has("--headers"))
        {
            return 0;
        }

        Console.WriteLine();

        if (args.Has("--html"))
        {
            if (string.IsNullOrWhiteSpace(rawHtml))
            {
                Console.WriteLine("（这封邮件没有 HTML 正文）");
                return 0;
            }

            WriteRawBody(rawHtml, maxChars);

            if (body.Truncated)
            {
                Console.WriteLine("（正文来自服务器，Engine 端也做了长度截断）");
            }

            return 0;
        }

        if (!string.IsNullOrWhiteSpace(rawText))
        {
            WriteRawBody(rawText, maxChars);
        }
        else if (!string.IsNullOrWhiteSpace(rawHtml))
        {
            Console.WriteLine("（这封邮件没有纯文本正文，以下是原始 HTML 正文）");
            Console.WriteLine();
            WriteRawBody(rawHtml, maxChars);
        }
        else
        {
            Console.WriteLine("（这封邮件没有正文）");
            return 0;
        }

        if (body.Truncated)
        {
            Console.WriteLine("（正文来自服务器，Engine 端也做了长度截断）");
        }

        return 0;
    }

    private static void WriteRawBody(string value, int maxChars)
    {
        if (maxChars > 0 && value.Length > maxChars)
        {
            Console.WriteLine(value[..maxChars].TrimEnd());
            Console.WriteLine($"…（已截断，共 {value.Length} 字；用 --max-chars 0 看全文）");
        }
        else
        {
            Console.WriteLine(value);
        }
    }

    public static async Task<int> AttachmentAsync(IpcClient client, Arguments args, CancellationToken cancellationToken)
    {
        if (args.Positionals.Count < 2)
        {
            throw new UsageException("用法：maildog attachment <邮件 id> <附件序号> [--out 目录]");
        }

        var messageId = ParseLong(args.Positionals, 0, "attachment <邮件 id> <附件序号>");
        var index = (int)ParseLong(args.Positionals, 1, "attachment <邮件 id> <附件序号>", 0);

        var body = await GetBodyAsync(client, messageId, cancellationToken).ConfigureAwait(false);

        if (index < 0 || index >= body.Attachments.Length)
        {
            throw new CliException(2, $"这封邮件没有第 {index} 个附件（共 {body.Attachments.Length} 个）。");
        }

        var attachment = body.Attachments[index];
        var directory = args.Value("--out") is { } outDir ? Path.GetFullPath(outDir) : Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileName(attachment.FileName);
        var destination = UniquePath(Path.Combine(directory, fileName));

        var response = await client.RequestAsync<AttachmentSavedResponse>(
            IpcMessageTypes.DownloadAttachment,
            new DownloadAttachmentRequest
            {
                MessageId = messageId,
                Index = index,
                DestinationPath = destination
            },
            LongTimeout,
            cancellationToken).ConfigureAwait(false);

        if (args.Json)
        {
            Console.WriteLine("{\"path\":" + Output.Str(response.Path)
                + ",\"fileName\":" + Output.Str(response.FileName)
                + ",\"size\":" + Output.Num(response.Size) + "}");
        }
        else
        {
            Console.WriteLine($"已保存：{response.Path}（{response.Size} 字节）");
        }

        return 0;
    }

    public static async Task<int> DraftAsync(IpcClient client, Arguments args, CancellationToken cancellationToken)
    {
        var compose = await ReadComposeAsync(client, args, requireRecipients: false, cancellationToken).ConfigureAwait(false);
        PrintCompose("保存草稿", compose, args);

        var request = new SaveDraftRequest
        {
            AccountId = compose.Account.Id,
            To = compose.To,
            Cc = compose.Cc,
            Bcc = compose.Bcc,
            Subject = compose.Subject,
            BodyText = compose.Text,
            BodyHtml = compose.Html,
            IsMarkdown = compose.Html is null,
            AttachmentPaths = compose.Attachments
        };

        if (args.Queue)
        {
            return QueueForApproval(
                "draft",
                $"草稿：{compose.Subject}",
                JsonSerializer.Serialize(request, IpcJsonContext.Default.SaveDraftRequest),
                args);
        }

        if (!args.Has("--confirm"))
        {
            if (!args.Json)
            {
                Console.WriteLine("这是预览，没有写入草稿箱。确认后加 --confirm 再执行，或用 --queue 放进待确认队列。");
            }

            return 0;
        }

        var response = await client.RequestAsync<DraftSavedResponse>(
            IpcMessageTypes.SaveDraft,
            request,
            LongTimeout,
            cancellationToken).ConfigureAwait(false);

        PrintDraftSaved(response, args);
        return 0;
    }

    public static async Task<int> SendAsync(IpcClient client, Arguments args, CancellationToken cancellationToken)
    {
        var compose = await ReadComposeAsync(client, args, requireRecipients: true, cancellationToken).ConfigureAwait(false);
        PrintCompose("发送邮件", compose, args);

        var request = new SendMailRequest
        {
            AccountId = compose.Account.Id,
            To = compose.To,
            Cc = compose.Cc,
            Bcc = compose.Bcc,
            Subject = compose.Subject,
            BodyText = compose.Text,
            BodyHtml = compose.Html,
            IsMarkdown = compose.Html is null,
            AttachmentPaths = compose.Attachments,
            SkipSendChecks = args.Has("--force-send")
        };

        if (args.Queue)
        {
            return QueueForApproval(
                "send",
                $"发送：{compose.Subject} → {compose.To}",
                JsonSerializer.Serialize(request, IpcJsonContext.Default.SendMailRequest),
                args);
        }

        if (!args.Has("--confirm"))
        {
            if (!args.Json)
            {
                Console.WriteLine("这是预览，没有发出。确认后加 --confirm 再执行，或用 --queue 放进待确认队列。");
            }

            return 0;
        }

        var response = await client.RequestAsync<SendMailResponse>(
            IpcMessageTypes.SendMail,
            request,
            LongTimeout,
            cancellationToken).ConfigureAwait(false);

        return PrintSendResponse(response, args);
    }

    private static int QueueForApproval(string kind, string summary, string requestJson, Arguments args)
    {
        var item = CliPending.Create(kind, summary, requestJson);

        if (args.Json)
        {
            Console.WriteLine("{\"pendingId\":" + Output.Str(item.Id)
                + ",\"kind\":" + Output.Str(item.Kind)
                + ",\"status\":\"queued\",\"summary\":" + Output.Str(item.Summary) + "}");
        }
        else
        {
            Console.WriteLine($"已放进待确认队列：{item.Id}");
            Console.WriteLine($"类型：{item.Kind}；{item.Summary}");
            Console.WriteLine($"用 maildog approve {item.Id} 确认；用 maildog deny {item.Id} 拒绝。");
        }

        return 0;
    }

    private static int PrintSendResponse(SendMailResponse response, Arguments args)
    {
        if (response.Status == SendStatuses.ConfirmRequired)
        {
            if (args.Json)
            {
                Console.WriteLine("{\"status\":\"confirm_required\",\"message\":" + Output.Str(response.Error ?? "") + "}");
            }
            else
            {
                Console.WriteLine("发送前检查没通过：");
                Console.WriteLine(response.Error ?? "（没有更多说明）");
                Console.WriteLine("确认无误后加 --force-send 重新执行。");
            }

            return 6;
        }

        if (args.Json)
        {
            Console.WriteLine("{\"outboxId\":" + Output.Num(response.OutboxId)
                + ",\"sent\":" + Output.Bool(response.Sent)
                + ",\"queued\":" + Output.Bool(response.Queued)
                + ",\"status\":" + Output.Str(response.Status)
                + ",\"delaySeconds\":" + Output.Num(response.DelaySeconds)
                + ",\"error\":" + Output.Str(response.Error) + "}");
        }
        else if (response.Sent)
        {
            Console.WriteLine("已交给 SMTP 服务器发出。");
        }
        else if (response.Queued)
        {
            if (response.DelaySeconds > 0)
            {
                Console.WriteLine($"已放进发件箱（id {response.OutboxId}），{response.DelaySeconds} 秒后发出；要撤回就执行 maildog cancel {response.OutboxId}。");
            }
            else
            {
                Console.WriteLine($"已放进发件箱（id {response.OutboxId}），后台会继续重试。");
            }
        }
        else
        {
            Console.Error.WriteLine($"没有发出去：{response.Error ?? response.Status}");
        }

        return response.Sent || response.Queued ? 0 : 3;
    }

    private static void PrintDraftSaved(DraftSavedResponse response, Arguments args)
    {
        if (args.Json)
        {
            Console.WriteLine("{\"folderId\":" + Output.Num(response.FolderId)
                + ",\"folder\":" + Output.Str(response.FolderName) + "}");
        }
        else
        {
            Console.WriteLine($"已存草稿：{response.FolderName}（文件夹 id {response.FolderId}）");
        }
    }

    public static int Pending(Arguments args)
    {
        var items = CliPending.List();

        if (items.Count == 0)
        {
            if (!args.Json)
            {
                Console.WriteLine("待确认队列为空。");
            }

            return 0;
        }

        foreach (var item in items)
        {
            if (args.Json)
            {
                Console.WriteLine("{\"pendingId\":" + Output.Str(item.Id)
                    + ",\"kind\":" + Output.Str(item.Kind)
                    + ",\"created\":" + Output.Str(Output.Iso(item.CreatedUtc))
                    + ",\"summary\":" + Output.Str(item.Summary) + "}");
            }
            else
            {
                Console.WriteLine($"{item.Id}  {item.Kind}  {item.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {item.Summary}");
            }
        }

        return 0;
    }

    public static int Deny(Arguments args)
    {
        var id = args.Positionals.FirstOrDefault()
            ?? throw new UsageException("用法：maildog deny <pendingId>");

        if (!CliPending.Delete(id))
        {
            throw new CliException(2, $"待确认项不存在：{id}");
        }

        if (args.Json)
        {
            Console.WriteLine("{\"pendingId\":" + Output.Str(id) + ",\"status\":\"denied\"}");
        }
        else
        {
            Console.WriteLine($"已拒绝待确认项：{id}");
        }

        return 0;
    }

    public static async Task<int> ApproveAsync(IpcClient client, Arguments args, CancellationToken cancellationToken)
    {
        var id = args.Positionals.FirstOrDefault()
            ?? throw new UsageException("用法：maildog approve <pendingId>");
        var item = CliPending.Read(id)
            ?? throw new CliException(2, $"待确认项不存在：{id}");

        if (string.Equals(item.Kind, "send", StringComparison.OrdinalIgnoreCase))
        {
            var request = JsonSerializer.Deserialize(item.RequestJson, IpcJsonContext.Default.SendMailRequest)
                ?? throw new CliException(2, $"待确认项内容损坏：{id}");
            var response = await client.RequestAsync<SendMailResponse>(
                IpcMessageTypes.SendMail,
                request,
                LongTimeout,
                cancellationToken).ConfigureAwait(false);
            var exitCode = PrintSendResponse(response, args);

            if (exitCode == 0)
            {
                CliPending.Delete(id);
            }

            return exitCode;
        }

        if (string.Equals(item.Kind, "draft", StringComparison.OrdinalIgnoreCase))
        {
            var request = JsonSerializer.Deserialize(item.RequestJson, IpcJsonContext.Default.SaveDraftRequest)
                ?? throw new CliException(2, $"待确认项内容损坏：{id}");
            var response = await client.RequestAsync<DraftSavedResponse>(
                IpcMessageTypes.SaveDraft,
                request,
                LongTimeout,
                cancellationToken).ConfigureAwait(false);
            PrintDraftSaved(response, args);
            CliPending.Delete(id);
            return 0;
        }

        throw new CliException(2, $"待确认项类型未知：{item.Kind}");
    }

    public static async Task<int> MarkAsync(IpcClient client, Arguments args, CancellationToken cancellationToken)
    {
        var ids = ReadIds(args, "mark <邮件 id> [更多 id] [--unread] [--confirm]");
        var isRead = !args.Has("--unread");

        if (args.Json && !args.Has("--confirm"))
        {
            Console.WriteLine("{\"dryRun\":true,\"ids\":[" + string.Join(',', ids.Select(Output.Num))
                + "],\"isRead\":" + Output.Bool(isRead) + "}");
            return 0;
        }

        Console.WriteLine($"将把 {ids.Length} 封邮件标为「{(isRead ? "已读" : "未读")}」：{string.Join(", ", ids)}");

        if (!args.Has("--confirm"))
        {
            Console.WriteLine("这是预览，没有改动。确认后加 --confirm 再执行。");
            return 0;
        }

        var response = await client.RequestAsync<OkResponse>(
            IpcMessageTypes.MarkReadBatch,
            new MarkReadBatchRequest { MessageIds = ids, IsRead = isRead },
            LongTimeout,
            cancellationToken).ConfigureAwait(false);

        if (args.Json)
        {
            Console.WriteLine("{\"affected\":" + Output.Num(response.Affected) + "}");
        }
        else
        {
            Console.WriteLine($"已处理 {response.Affected} 封。");
        }

        return 0;
    }

    public static async Task<int> MoveAsync(IpcClient client, Arguments args, CancellationToken cancellationToken)
    {
        var ids = ReadIds(args, "move <邮件 id> [更多 id] --folder <目标文件夹> --confirm");
        var targetKey = args.Value("--folder")
            ?? throw new UsageException("move 需要 --folder 指定目标文件夹（role、id 或名字）。");

        var account = await FindAccountAsync(client, args.Value("--account"), cancellationToken).ConfigureAwait(false);
        var target = await ResolveTargetFolderAsync(client, account, targetKey, cancellationToken).ConfigureAwait(false);

        if (args.Json && !args.Has("--confirm"))
        {
            Console.WriteLine("{\"dryRun\":true,\"ids\":[" + string.Join(',', ids.Select(Output.Num))
                + "],\"folder\":" + Output.Num(target.Id) + ",\"folderName\":" + Output.Str(target.Name) + "}");
            return 0;
        }

        Console.WriteLine($"将把 {ids.Length} 封邮件移到 {target.Name}（id {target.Id}）：{string.Join(", ", ids)}");

        if (!args.Has("--confirm"))
        {
            Console.WriteLine("这是预览，没有移动。确认后加 --confirm 再执行。");
            return 0;
        }

        var affected = 0;

        foreach (var id in ids)
        {
            var response = await client.RequestAsync<OkResponse>(
                IpcMessageTypes.MoveMessage,
                new MoveMessageRequest { MessageId = id, TargetFolderId = target.Id },
                LongTimeout,
                cancellationToken).ConfigureAwait(false);

            affected += response.Affected;
        }

        if (args.Json)
        {
            Console.WriteLine("{\"affected\":" + Output.Num(affected)
                + ",\"folder\":" + Output.Num(target.Id) + "}");
        }
        else
        {
            Console.WriteLine($"已移动 {affected} 封到 {target.Name}。");
        }

        return 0;
    }

    /// <summary>撤回还停在发件箱的邮件（延迟发送窗口内）。</summary>
    public static async Task<int> CancelAsync(IpcClient client, Arguments args, CancellationToken cancellationToken)
    {
        var ids = ReadIds(args, "cancel <发件箱 id> [更多 id]");
        var affected = 0;

        foreach (var outboxId in ids)
        {
            var response = await client.RequestAsync<OkResponse>(
                IpcMessageTypes.DeleteOutbox,
                new DeleteOutboxRequest { OutboxId = outboxId },
                LongTimeout,
                cancellationToken).ConfigureAwait(false);

            affected += response.Affected;
        }

        if (args.Json)
        {
            Console.WriteLine("{\"affected\":" + Output.Num(affected) + "}");
        }
        else if (affected == 0)
        {
            Console.Error.WriteLine("没有撤回任何邮件：可能已经发出去了，或者 id 不对。");
        }
        else
        {
            Console.WriteLine($"已撤回 {affected} 封，邮件没有发出去。");
        }

        return affected > 0 ? 0 : 2;
    }

    /// <summary>让后台 Engine 正常收尾退出，Linux 无托盘时用它代替“右键退出”。</summary>
    public static async Task<int> QuitAsync(IpcClient client, Arguments args, CancellationToken cancellationToken)
    {
        await client.RequestAsync<OkResponse>(
            IpcMessageTypes.Shutdown,
            new ShutdownRequest(),
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);

        if (args.Json)
        {
            Console.WriteLine("{\"stopped\":true}");
        }
        else
        {
            Console.WriteLine("已请求 MailDog 后台退出。");
        }

        return 0;
    }

    private static async Task<AccountDto[]> GetAccountsAsync(IpcClient client, CancellationToken cancellationToken)
        => (await client.RequestAsync<AccountsResponse>(
            IpcMessageTypes.GetAccounts,
            new GetAccountsRequest(),
            null,
            cancellationToken).ConfigureAwait(false)).Items;

    private static async Task<AccountDto?> FindAccountAsync(IpcClient client, string? key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var accounts = await GetAccountsAsync(client, cancellationToken).ConfigureAwait(false);

        var match = accounts.FirstOrDefault(account =>
            string.Equals(account.Id, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(account.Email, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(account.DisplayName, key, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new CliException(2, $"没有找到账号：{key}（用 maildog accounts 看清单）");
    }

    private static async Task<FolderDto[]> GetFoldersAsync(IpcClient client, string accountId, CancellationToken cancellationToken)
        => (await client.RequestAsync<FoldersResponse>(
            IpcMessageTypes.GetFolders,
            new GetFoldersRequest { AccountId = accountId },
            null,
            cancellationToken).ConfigureAwait(false)).Items;

    private static async Task<int> CountUnreadAsync(IpcClient client, string accountId, CancellationToken cancellationToken)
    {
        try
        {
            var folders = await GetFoldersAsync(client, accountId, cancellationToken).ConfigureAwait(false);
            var inbox = folders.FirstOrDefault(folder => folder.Role == FolderRole.Inbox);
            return inbox?.UnreadCount ?? folders.Sum(folder => folder.UnreadCount);
        }
        catch (IpcCallException)
        {
            // 账号离线时拿不到文件夹，未读数按 0 显示，不影响账号清单本身。
            return 0;
        }
    }

    private static async Task<Scope> ResolveScopeAsync(IpcClient client, Arguments args, CancellationToken cancellationToken)
    {
        var account = await FindAccountAsync(client, args.Value("--account"), cancellationToken).ConfigureAwait(false);
        var folderKey = args.Value("--folder");

        if (string.IsNullOrWhiteSpace(folderKey))
        {
            return new Scope(null, FolderRole.Inbox, "收件箱");
        }

        if (long.TryParse(folderKey, out var folderId))
        {
            return new Scope(folderId, null, $"文件夹 {folderId}");
        }

        if (TryParseRole(folderKey) is { } role)
        {
            if (account is not null)
            {
                var folders = await GetFoldersAsync(client, account.Id, cancellationToken).ConfigureAwait(false);
                var match = folders.FirstOrDefault(folder => folder.Role == role)
                    ?? throw new CliException(2, $"账号 {account.Email} 下没有 {role} 文件夹。");

                return new Scope(match.Id, null, $"{account.Email} / {match.Name}");
            }

            return new Scope(null, role, role.ToString());
        }

        if (account is null)
        {
            throw new UsageException("按名字找文件夹时要带 --account，或者改用 inbox/sent/drafts/trash/junk/archive / 文件夹 id。");
        }

        var all = await GetFoldersAsync(client, account.Id, cancellationToken).ConfigureAwait(false);
        var byName = all.FirstOrDefault(folder =>
            string.Equals(folder.Name, folderKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(folder.FullName, folderKey, StringComparison.OrdinalIgnoreCase)
            || folder.Name.Contains(folderKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliException(2, $"账号 {account.Email} 下没有叫「{folderKey}」的文件夹。");

        return new Scope(byName.Id, null, byName.Name);
    }

    private static async Task<HashSet<long>?> ResolveFolderIdsAsync(
        IpcClient client,
        AccountDto? account,
        string folderKey,
        CancellationToken cancellationToken)
    {
        if (long.TryParse(folderKey, out var folderId))
        {
            return new HashSet<long> { folderId };
        }

        var role = TryParseRole(folderKey)
            ?? throw new UsageException("--folder 只接受 inbox/sent/drafts/trash/junk/archive、文件夹 id 或文件夹名");

        AccountDto[] accounts = account is null
            ? await GetAccountsAsync(client, cancellationToken).ConfigureAwait(false)
            : [account];

        var ids = new HashSet<long>();

        foreach (var item in accounts)
        {
            try
            {
                var folders = await GetFoldersAsync(client, item.Id, cancellationToken).ConfigureAwait(false);

                foreach (var folder in folders.Where(folder => folder.Role == role))
                {
                    ids.Add(folder.Id);
                }
            }
            catch (IpcCallException)
            {
                // 离线账号跳过，能查到的照常过滤。
            }
        }

        return ids;
    }

    private static FolderRole? TryParseRole(string value)
        => value.ToLowerInvariant() switch
        {
            "inbox" => FolderRole.Inbox,
            "sent" => FolderRole.Sent,
            "drafts" => FolderRole.Drafts,
            "trash" => FolderRole.Trash,
            "junk" => FolderRole.Junk,
            "archive" => FolderRole.Archive,
            _ => null
        };

    private static async Task<MailBodyDto> GetBodyAsync(IpcClient client, long messageId, CancellationToken cancellationToken)
        => (await client.RequestAsync<MessageBodyResponse>(
            IpcMessageTypes.GetMessageBody,
            new GetMessageBodyRequest { MessageId = messageId },
            LongTimeout,
            cancellationToken).ConfigureAwait(false)).Body;

    private static long ParseLong(IReadOnlyList<string> positionals, int index, string usage, long minimum = 1)
    {
        if (positionals.Count <= index || !long.TryParse(positionals[index], out var value) || value < minimum)
        {
            throw new UsageException($"用法：maildog {usage}（id 是正整数，从 list / search 里取；附件序号从 0 起）");
        }

        return value;
    }

    private static string Truncate(string value, int max)
        => max <= 0 ? value : Output.Truncate(value, max);

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");

            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return path;
    }

    private static string? TruncateIfAny(string? value, int maxChars)
        => value is null ? null : Truncate(value, maxChars);

    internal static string BuildBodyJson(MailBodyDto body, string? text, string? html)
    {
        var builder = new System.Text.StringBuilder(512);
        builder.Append("{\"id\":").Append(Output.Num(body.MessageId))
            .Append(",\"subject\":").Append(Output.Str(body.Subject))
            .Append(",\"from\":").Append(Output.Str(body.FromName))
            .Append(",\"fromAddress\":").Append(Output.Str(body.FromAddress))
            .Append(",\"to\":[").Append(string.Join(',', body.ToAddresses.Select(Output.Str)))
            .Append("],\"cc\":[").Append(string.Join(',', body.CcAddresses.Select(Output.Str)))
            .Append("],\"date\":").Append(Output.Str(Output.Iso(body.Date)))
            .Append(",\"truncated\":").Append(Output.Bool(body.Truncated))
            .Append(",\"fromCache\":").Append(Output.Bool(body.FromCache))
            .Append(",\"text\":").Append(Output.Str(text))
            .Append(",\"html\":").Append(Output.Str(html))
            .Append(",\"attachments\":[");

        for (var index = 0; index < body.Attachments.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(Output.AttachmentJson(body.Attachments[index], index));
        }

        builder.Append("]}");
        return builder.ToString();
    }

    private static async Task<Compose> ReadComposeAsync(
        IpcClient client,
        Arguments args,
        bool requireRecipients,
        CancellationToken cancellationToken)
    {
        var accountKey = args.Value("--account")
            ?? throw new UsageException("需要 --account 指定发信账号（用 maildog accounts 看清单）。");
        var account = await FindAccountAsync(client, accountKey, cancellationToken).ConfigureAwait(false)
            ?? throw new CliException(2, $"没有找到账号：{accountKey}");

        var to = JoinAddresses(args.Values("--to"));
        var cc = JoinAddresses(args.Values("--cc"));
        var bcc = JoinAddresses(args.Values("--bcc"));

        if (requireRecipients && to.Length == 0)
        {
            throw new UsageException("需要 --to 收件人。");
        }

        var inlineText = args.Value("--text");
        var bodyFile = args.Value("--body-file");

        if (inlineText is not null && bodyFile is not null)
        {
            throw new UsageException("--text 与 --body-file 只能选一个。");
        }

        var text = inlineText ?? string.Empty;

        if (bodyFile is not null)
        {
            if (!File.Exists(bodyFile))
            {
                throw new CliException(2, $"找不到正文文件：{bodyFile}");
            }

            text = File.ReadAllText(bodyFile);
        }

        var html = string.Empty;

        if (args.Value("--html-file") is { } htmlFile)
        {
            if (!File.Exists(htmlFile))
            {
                throw new CliException(2, $"找不到 HTML 文件：{htmlFile}");
            }

            html = File.ReadAllText(htmlFile);
        }

        var attachments = args.Values("--attach").ToArray();

        foreach (var attachment in attachments)
        {
            if (!File.Exists(attachment))
            {
                throw new CliException(2, $"找不到附件：{attachment}");
            }
        }

        return new Compose(
            account,
            to,
            cc,
            bcc,
            args.Value("--subject") ?? string.Empty,
            text,
            html.Length > 0 ? html : null,
            attachments);
    }

    private static void PrintCompose(string verb, Compose compose, Arguments args)
    {
        if (args.Json)
        {
            if (!args.Has("--confirm"))
            {
                Console.WriteLine("{\"dryRun\":true"
                    + ",\"account\":" + Output.Str(compose.Account.Email)
                    + ",\"to\":[" + string.Join(',', compose.To.Split(',', StringSplitOptions.TrimEntries).Select(Output.Str)) + "]"
                    + ",\"cc\":" + Output.Str(compose.Cc)
                    + ",\"bcc\":" + Output.Str(compose.Bcc)
                    + ",\"subject\":" + Output.Str(compose.Subject)
                    + ",\"attachments\":" + Output.Num(compose.Attachments.Length)
                    + ",\"textChars\":" + Output.Num(compose.Text.Length)
                    + "}");
            }

            return;
        }

        Console.WriteLine($"{verb}：");
        Console.WriteLine($"  账号    {compose.Account.Email}");
        Console.WriteLine($"  收件人  {(compose.To.Length == 0 ? "（空）" : compose.To)}");

        if (compose.Cc.Length > 0)
        {
            Console.WriteLine($"  抄送    {compose.Cc}");
        }

        if (compose.Bcc.Length > 0)
        {
            Console.WriteLine($"  密送    {compose.Bcc}");
        }

        Console.WriteLine($"  主题    {(compose.Subject.Length == 0 ? "（无主题）" : compose.Subject)}");
        Console.WriteLine($"  附件    {(compose.Attachments.Length == 0 ? "无" : string.Join(", ", compose.Attachments.Select(Path.GetFileName)))}");
        Console.WriteLine("  正文：");

        var preview = Output.Truncate(compose.Text.Trim(), 600);
        Console.WriteLine(preview.Length == 0 ? "  （空）" : string.Join('\n', preview.Split('\n').Select(line => "  " + line)));
    }

    private static string JoinAddresses(IReadOnlyList<string> values)
        => string.Join(", ", values
            .SelectMany(value => value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase));

    private static long[] ReadIds(Arguments args, string usage)
    {
        if (args.Positionals.Count == 0)
        {
            throw new UsageException($"用法：maildog {usage}（id 从 list / search 里取）");
        }

        var ids = new long[args.Positionals.Count];

        for (var index = 0; index < args.Positionals.Count; index++)
        {
            if (!long.TryParse(args.Positionals[index], out var id) || id <= 0)
            {
                throw new UsageException($"「{args.Positionals[index]}」不是邮件 id；id 从 list / search 里取。");
            }

            ids[index] = id;
        }

        return ids;
    }

    private static async Task<FolderDto> ResolveTargetFolderAsync(
        IpcClient client,
        AccountDto? account,
        string key,
        CancellationToken cancellationToken)
    {
        if (account is null)
        {
            throw new UsageException("move 要带 --account，目标文件夹得在某个账号里定位。");
        }

        var folders = await GetFoldersAsync(client, account.Id, cancellationToken).ConfigureAwait(false);

        if (long.TryParse(key, out var folderId))
        {
            return folders.FirstOrDefault(folder => folder.Id == folderId)
                ?? throw new CliException(2, $"账号 {account.Email} 下没有 id 为 {folderId} 的文件夹。");
        }

        if (TryParseRole(key) is { } role)
        {
            return folders.FirstOrDefault(folder => folder.Role == role)
                ?? throw new CliException(2, $"账号 {account.Email} 下没有 {role} 文件夹。");
        }

        return folders.FirstOrDefault(folder =>
            string.Equals(folder.Name, key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(folder.FullName, key, StringComparison.OrdinalIgnoreCase)
            || folder.Name.Contains(key, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliException(2, $"账号 {account.Email} 下没有叫「{key}」的文件夹。");
    }

    private sealed record Compose(
        AccountDto Account,
        string To,
        string Cc,
        string Bcc,
        string Subject,
        string Text,
        string? Html,
        string[] Attachments);

    private sealed record Scope(long? FolderId, FolderRole? Role, string Label);
}
