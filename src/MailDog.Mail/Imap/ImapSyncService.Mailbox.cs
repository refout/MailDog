using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Messages;
using MailDog.Mail.Accounts;
using MailDog.Mail.Auth;
using MailDog.Mail.Mime;
using MailDog.Mail.Storage;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace MailDog.Mail.Imap;

public sealed partial class ImapSyncService
{
    public async Task<(string FileName, long Size)> SaveAttachmentAsync(
        long messageId,
        int attachmentIndex,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var message = _store.GetMessage(messageId)
            ?? throw new KeyNotFoundException("邮件不存在。");

        var account = _accounts.GetRecord(message.AccountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var folderRecord = _store.GetFolder(message.FolderId)
            ?? throw new KeyNotFoundException("文件夹不存在。");

        var session = await GetSessionAsync(account.Id, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            var folder = session.Client!.GetFolder(folderRecord.FullName);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

            try
            {
                var mime = await folder.GetMessageAsync(
                    ToUniqueId(message.Uid),
                    cancellationToken).ConfigureAwait(false);

                var part = MimeConverter.FindAttachment(mime, attachmentIndex)
                    ?? throw new InvalidOperationException("附件不存在，请刷新邮件后重试。");

                if (part.Content is null)
                {
                    throw new InvalidOperationException("附件内容为空。");
                }

                var directory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await using var stream = File.Create(destinationPath);
                await part.Content.DecodeToAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                return (part.FileName ?? "attachment", stream.Length);
            }
            finally
            {
                await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>把 .eml 或 mbox 批量追加到指定 IMAP 文件夹；失败单封跳过，不中断整批。</summary>
    public async Task<MailboxImportResultDto> ImportMailboxAsync(
        string accountId,
        long folderId,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var account = _accounts.GetRecord(accountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var folderRecord = _store.GetFolder(folderId)
            ?? throw new KeyNotFoundException("文件夹不存在。");
        if (!string.Equals(folderRecord.AccountId, accountId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("目标文件夹不属于该账户。");
        }

        var session = await GetSessionAsync(accountId, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);
            var folder = session.Client!.GetFolder(folderRecord.FullName);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);

            try
            {
                foreach (var path in paths)
                {
                    try
                    {
                        await using var stream = File.OpenRead(path);
                        var extension = Path.GetExtension(path);
                        if (string.Equals(extension, ".mbox", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(extension, ".mbx", StringComparison.OrdinalIgnoreCase))
                        {
                            var parser = new MimeParser(stream, MimeFormat.Mbox);
                            while (!parser.IsEndOfStream)
                            {
                                var message = parser.ParseMessage(cancellationToken);
                                if (await TryAppendAsync(folder, message, errors, cancellationToken).ConfigureAwait(false))
                                {
                                    imported++;
                                }
                                else
                                {
                                    skipped++;
                               }
                            }
                        }
                        else
                        {
                            var message = await MimeMessage.LoadAsync(stream, cancellationToken).ConfigureAwait(false);
                            if (await TryAppendAsync(folder, message, errors, cancellationToken).ConfigureAwait(false))
                            {
                                imported++;
                            }
                            else
                            {
                                skipped++;
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        skipped++;
                        if (errors.Count < 8)
                        {
                            errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
                        }
                    }
                }
            }
            finally
            {
                await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            session.Gate.Release();
        }

        return new MailboxImportResultDto
        {
            Imported = imported,
            Skipped = skipped,
            Errors = errors.ToArray(),
            Message = $"已导入 {imported} 封" + (skipped > 0 ? $"，跳过 {skipped} 封" : string.Empty) + "。"
        };
    }

    private static async Task<bool> TryAppendAsync(
        IMailFolder folder,
        MimeMessage message,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            await folder.AppendAsync(message, MessageFlags.None, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            if (errors.Count < 8)
            {
                errors.Add($"{message.Subject}: {exception.Message}");
            }

            return false;
        }
    }

    /// <summary>把账户（或单个文件夹）全部邮件导出为 mbox；逐封从 IMAP 拉原始 MIME。</summary>
    public async Task<MailboxExportResultDto> ExportMailboxAsync(
        string accountId,
        long? folderId,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var account = _accounts.GetRecord(accountId)
            ?? throw new KeyNotFoundException("账户不存在。");
        var messages = _store.GetMessagesForExport(accountId, folderId);
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var session = await GetSessionAsync(accountId, cancellationToken);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var exported = 0;

        try
        {
            await EnsureConnectedAsync(session, account, cancellationToken).ConfigureAwait(false);

            await using var writer = new StreamWriter(
                File.Create(destinationPath),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            foreach (var group in messages.GroupBy(message => message.FolderId))
            {
                var folderRecord = _store.GetFolder(group.Key);
                if (folderRecord is null)
                {
                    continue;
                }

                var folder = session.Client!.GetFolder(folderRecord.FullName);
                await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);

                try
                {
                    foreach (var message in group)
                    {
                        try
                        {
                            var mime = await folder.GetMessageAsync(
                                ToUniqueId(message.Uid),
                                cancellationToken).ConfigureAwait(false);
                            var sender = mime.Sender ?? mime.From.Mailboxes.FirstOrDefault();
                            var senderAddress = sender?.Address ?? "maildog@localhost";
                            await writer.WriteAsync(MboxFormat.CreateSeparator(senderAddress, mime.Date))
                                .ConfigureAwait(false);

                            await using var buffer = new MemoryStream();
                            await mime.WriteToAsync(buffer, cancellationToken).ConfigureAwait(false);
                            await writer.WriteAsync(MboxFormat.NormalizeMessage(Encoding.UTF8.GetString(buffer.ToArray()))
                                + "\n\n").ConfigureAwait(false);
                            exported++;
                        }
                        catch
                        {
                            // 单封失败不影响其余邮件；结果消息里显示导出数量。
                        }
                    }
                }
                finally
                {
                    await folder.CloseAsync(false, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            session.Gate.Release();
        }

        var size = new FileInfo(destinationPath).Length;
        return new MailboxExportResultDto
        {
            Path = destinationPath,
            Size = size,
            Messages = exported,
            Message = $"已导出 {exported} 封邮件到 mbox。"
        };
    }
}
