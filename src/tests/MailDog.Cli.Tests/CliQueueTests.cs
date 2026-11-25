using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Serialization;

namespace MailDog.Cli.Tests;

public sealed class CliQueueTests
{
    [Fact]
    public async Task QueuesDraftAndSendThenApprovesThem()
    {
        var queueDirectory = Path.Combine(Path.GetTempPath(), $"maildog-queue-{Guid.NewGuid():N}");
        var pipeName = $"MailDog.Test.{Guid.NewGuid():N}";
        Directory.CreateDirectory(queueDirectory);
        Environment.SetEnvironmentVariable("MAILDOG_PENDING_DIR", queueDirectory);

        await using var engine = new FakeEngine(pipeName);
        await using var client = await IpcClient.TryConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None, pipeName);

        Assert.NotNull(client);
        await client.HelloAsync("test", CancellationToken.None);

        try
        {
            var draftArgs = Arguments.Parse(
            [
                "draft", "--account", "test@example.com", "--to", "to@example.com",
                "--subject", "草稿主题", "--text", "正文", "--queue", "--json"
            ]);

            Assert.Equal(0, await Commands.DraftAsync(client, draftArgs, CancellationToken.None));
            var draftItems = CliPending.List();
            Assert.Single(draftItems);
            Assert.Equal("draft", draftItems[0].Kind);

            var approveDraft = Arguments.Parse(["approve", draftItems[0].Id, "--json"]);
            Assert.Equal(0, await Commands.ApproveAsync(client, approveDraft, CancellationToken.None));
            Assert.Empty(CliPending.List());

            var sendArgs = Arguments.Parse(
            [
                "send", "--account", "test@example.com", "--to", "to@example.com",
                "--subject", "发送主题", "--text", "正文", "--queue", "--json"
            ]);

            Assert.Equal(0, await Commands.SendAsync(client, sendArgs, CancellationToken.None));
            var sendItems = CliPending.List();
            Assert.Single(sendItems);
            Assert.Equal("send", sendItems[0].Kind);

            var approveSend = Arguments.Parse(["approve", sendItems[0].Id, "--json"]);
            Assert.Equal(0, await Commands.ApproveAsync(client, approveSend, CancellationToken.None));
            Assert.Empty(CliPending.List());

            Assert.Contains(IpcMessageTypes.SaveDraft, engine.RequestTypes);
            Assert.Contains(IpcMessageTypes.SendMail, engine.RequestTypes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAILDOG_PENDING_DIR", null);
            Directory.Delete(queueDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SendAtAndSnoozeAreForwardedToEngine()
    {
        var pipeName = $"MailDog.Test.{Guid.NewGuid():N}";
        await using var engine = new FakeEngine(pipeName);
        await using var client = await IpcClient.TryConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None, pipeName);

        Assert.NotNull(client);
        await client.HelloAsync("test", CancellationToken.None);

        var sendAt = DateTimeOffset.Now.AddHours(2);
        var sendArgs = Arguments.Parse(
        [
            "send", "--account", "test@example.com", "--to", "to@example.com",
            "--subject", "计划发送", "--text", "正文", "--confirm", "--json",
            "--send-at", sendAt.ToString("yyyy-MM-dd HH:mm")
        ]);

        Assert.Equal(0, await Commands.SendAsync(client, sendArgs, CancellationToken.None));
        Assert.NotNull(engine.LastSend);
        Assert.NotNull(engine.LastSend.ScheduledAtUtc);
        Assert.Equal(sendAt.Minute, engine.LastSend.ScheduledAtUtc.Value.ToLocalTime().Minute);

        var until = DateTimeOffset.Now.AddHours(3);
        var snoozeArgs = Arguments.Parse(
        [
            "snooze", "42", "--until", until.ToString("yyyy-MM-dd HH:mm"), "--confirm", "--json"
        ]);

        Assert.Equal(0, await Commands.SnoozeAsync(client, snoozeArgs, CancellationToken.None));
        Assert.NotNull(engine.LastSnooze);
        Assert.Equal(42, engine.LastSnooze.MessageId);
    }

    [Fact]
    public async Task OpenUiIsForwardedToEngine()
    {
        var pipeName = $"MailDog.Test.{Guid.NewGuid():N}";
        await using var engine = new FakeEngine(pipeName);
        await using var client = await IpcClient.TryConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None, pipeName);

        Assert.NotNull(client);
        await client.HelloAsync("test", CancellationToken.None);

        var args = Arguments.Parse(["open", "--json"]);
        Assert.Equal(0, await Commands.OpenUiAsync(client, args, CancellationToken.None));
        Assert.Contains(IpcMessageTypes.OpenUi, engine.RequestTypes);
    }

    private sealed class FakeEngine : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly NamedPipeServerStream _pipe;
        private readonly Task _loop;

        public FakeEngine(string pipeName)
        {
            _pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _loop = RunAsync();
        }

        public ConcurrentQueue<string> RequestTypes { get; } = new();

        public SendMailRequest? LastSend { get; private set; }

        public SnoozeMessageRequest? LastSnooze { get; private set; }

        private async Task RunAsync()
        {
            await _pipe.WaitForConnectionAsync(_cts.Token);

            while (!_cts.IsCancellationRequested)
            {
                var payload = await IpcFrame.ReadAsync(_pipe, _cts.Token);
                if (payload is null)
                {
                    return;
                }

                var request = IpcJson.Deserialize(payload);
                RequestTypes.Enqueue(request.Type);

                switch (request.Type)
                {
                    case IpcMessageTypes.Hello:
                        await ReplyAsync(
                            request,
                            IpcMessageTypes.HelloAck,
                            new HelloAck
                            {
                                ProtocolVersion = IpcProtocol.Version,
                                EngineVersion = "test",
                                Accounts = [TestAccount()]
                            },
                            IpcJsonContext.Default.HelloAck);
                        break;
                    case IpcMessageTypes.GetAccounts:
                        await ReplyAsync(
                            request,
                            IpcMessageTypes.Accounts,
                            new AccountsResponse { Items = [TestAccount()] },
                            IpcJsonContext.Default.AccountsResponse);
                        break;
                    case IpcMessageTypes.SaveDraft:
                        await ReplyAsync(
                            request,
                            IpcMessageTypes.DraftSaved,
                            new DraftSavedResponse { FolderId = 100, FolderName = "Drafts" },
                            IpcJsonContext.Default.DraftSavedResponse);
                        break;
                    case IpcMessageTypes.SendMail:
                        LastSend = request.Body.Deserialize(IpcJsonContext.Default.SendMailRequest);
                        await ReplyAsync(
                            request,
                            IpcMessageTypes.SendMailResult,
                            new SendMailResponse { OutboxId = 7, Queued = true, Status = "queued" },
                            IpcJsonContext.Default.SendMailResponse);
                        break;
                    case IpcMessageTypes.SnoozeMessage:
                        LastSnooze = request.Body.Deserialize(IpcJsonContext.Default.SnoozeMessageRequest);
                        await ReplyAsync(
                            request,
                            IpcMessageTypes.Ok,
                            new OkResponse { Affected = 1 },
                            IpcJsonContext.Default.OkResponse);
                        break;
                    case IpcMessageTypes.OpenUi:
                        await ReplyAsync(
                            request,
                            IpcMessageTypes.Ok,
                            new OkResponse(),
                            IpcJsonContext.Default.OkResponse);
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected request: {request.Type}");
                }
            }
        }

        private async Task ReplyAsync<T>(
            IpcEnvelope request,
            string type,
            T body,
            JsonTypeInfo<T> typeInfo)
        {
            var envelope = new IpcEnvelope
            {
                Type = type,
                RequestId = request.RequestId,
                Body = JsonSerializer.SerializeToElement(body, typeInfo)
            };

            await IpcFrame.WriteAsync(_pipe, IpcJson.Serialize(envelope), _cts.Token);
        }

        private static AccountDto TestAccount()
            => new()
            {
                Id = "account-1",
                Email = "test@example.com",
                DisplayName = "Test",
                Provider = MailProvider.Custom,
                Status = AccountStatus.Ok
            };

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _pipe.DisposeAsync();

            try
            {
                await _loop;
            }
            catch
            {
                // 测试收尾，管道被关闭属于预期。
            }

            _cts.Dispose();
        }
    }
}
