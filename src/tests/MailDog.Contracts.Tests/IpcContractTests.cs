using MailDog.Contracts.Dto;
using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;
using MailDog.Contracts.Serialization;

namespace MailDog.Contracts.Tests;

public sealed class IpcContractTests
{
    [Fact]
    public void AllIpcMessagesAreRegisteredAndHaveMetadata()
    {
        var assembly = typeof(IIpcMessage).Assembly;
        var messageTypes = assembly.GetTypes()
            .Where(type => type.IsPublic && !type.IsAbstract && typeof(IIpcMessage).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(messageTypes);

        var registered = IpcCodecRegistry.RegisteredTypes.Values.ToHashSet();
        foreach (var type in messageTypes)
        {
            Assert.Contains(type, registered);
            Assert.NotNull(IpcJsonContext.Default.GetTypeInfo(type));
        }

        Assert.Equal(messageTypes.Length, IpcCodecRegistry.RegisteredTypes.Count);
    }

    [Fact]
    public void EveryRegisteredMessageRoundTrips()
    {
        foreach (var (typeName, type) in IpcCodecRegistry.RegisteredTypes)
        {
            var instance = (IIpcMessage)Activator.CreateInstance(type)!;
            var envelope = IpcCodecRegistry.Create(typeName, instance, requestId: 42);
            var payload = IpcJson.Serialize(envelope);
            var parsed = IpcJson.Deserialize(payload);

            Assert.Equal(typeName, parsed.Type);
            Assert.Equal(42, parsed.RequestId);
            Assert.IsType(type, IpcCodecRegistry.Read(parsed));
        }
    }

    [Fact]
    public void AccountServerConfigurationRoundTrips()
    {
        var response = new AccountsResponse
        {
            Items =
            [
                new AccountDto
                {
                    Id = "acc-1",
                    Provider = MailProvider.Custom,
                    Email = "user@example.com",
                    DisplayName = "自定义邮箱",
                    Status = AccountStatus.Ok,
                    Server = new AccountServerDto
                    {
                        ImapHost = "imap.example.com",
                        ImapPort = 143,
                        ImapStartTls = true,
                        SmtpHost = "smtp.example.com",
                        SmtpPort = 587,
                        SmtpStartTls = true
                    }
                }
            ]
        };

        var envelope = IpcCodecRegistry.Create(IpcMessageTypes.Accounts, response);
        var parsed = Assert.IsType<AccountsResponse>(
            IpcCodecRegistry.Read(IpcJson.Deserialize(IpcJson.Serialize(envelope))));
        var account = Assert.Single(parsed.Items);

        Assert.Equal(MailProvider.Custom, account.Provider);
        Assert.NotNull(account.Server);
        Assert.Equal("imap.example.com", account.Server.ImapHost);
        Assert.Equal(143, account.Server.ImapPort);
        Assert.True(account.Server.ImapStartTls);
        Assert.Equal(587, account.Server.SmtpPort);
        Assert.True(account.Server.SmtpStartTls);
    }

    [Fact]
    public void UnknownTypeThrows()
    {
        var envelope = new IpcEnvelope { Type = "not-registered", RequestId = 1, Body = default };

        Assert.Throws<NotSupportedException>(() => IpcCodecRegistry.Read(envelope));
    }

    [Fact]
    public void BatchMoveAndDeleteRequestsRoundTrip()
    {
        var move = new MoveMessagesRequest
        {
            MessageIds = [101, 102],
            TargetFolderId = 88
        };
        var moveEnvelope = IpcCodecRegistry.Create(IpcMessageTypes.MoveMessages, move);
        var parsedMove = Assert.IsType<MoveMessagesRequest>(
            IpcCodecRegistry.Read(IpcJson.Deserialize(IpcJson.Serialize(moveEnvelope))));
        Assert.Equal([101L, 102L], parsedMove.MessageIds);
        Assert.Equal(88, parsedMove.TargetFolderId);

        var delete = new DeleteMessagesRequest
        {
            MessageIds = [201, 202, 203],
            Permanent = true
        };
        var deleteEnvelope = IpcCodecRegistry.Create(IpcMessageTypes.DeleteMessages, delete);
        var parsedDelete = Assert.IsType<DeleteMessagesRequest>(
            IpcCodecRegistry.Read(IpcJson.Deserialize(IpcJson.Serialize(deleteEnvelope))));
        Assert.Equal([201L, 202L, 203L], parsedDelete.MessageIds);
        Assert.True(parsedDelete.Permanent);
    }

    [Fact]
    public void TemplatesAndAccountSignaturesRoundTripInsideSettings()
    {
        var settings = new AppSettingsDto
        {
            Signature = "默认签名",
            AccountSignatures =
            [
                new AccountSignatureDto { AccountId = "acc-1", Signature = "账户签名" }
            ],
            Templates =
            [
                new MailTemplateDto
                {
                    Id = "t-1",
                    Name = "客户跟进",
                    Subject = "关于 {{subject}}",
                    Body = "您好，{{account}}",
                    IsQuickReply = true
                }
            ]
        };

        var envelope = IpcCodecRegistry.Create(
            IpcMessageTypes.UpdateSettings,
            new UpdateSettingsRequest { Settings = settings });
        var parsed = Assert.IsType<UpdateSettingsRequest>(
            IpcCodecRegistry.Read(IpcJson.Deserialize(IpcJson.Serialize(envelope))));

        var signature = Assert.Single(parsed.Settings.AccountSignatures);
        Assert.Equal("acc-1", signature.AccountId);
        Assert.Equal("账户签名", signature.Signature);

        var template = Assert.Single(parsed.Settings.Templates);
        Assert.Equal("客户跟进", template.Name);
        Assert.Equal("关于 {{subject}}", template.Subject);
        Assert.True(template.IsQuickReply);
    }

    [Fact]
    public void OAuthDtosAreCoveredBySourceGeneratedContext()
    {
        Assert.NotNull(IpcJsonContext.Default.GetTypeInfo(typeof(OAuthTokenResponse)));
        Assert.NotNull(IpcJsonContext.Default.GetTypeInfo(typeof(OAuthTokenError)));
    }

    [Fact]
    public void ErrorResponseUsesConcreteCode()
    {
        var error = IpcCodecRegistry.Error(IpcErrorCodes.NotFound, "missing");

        Assert.Equal(IpcErrorCodes.NotFound, error.Code);
        Assert.Equal("missing", error.Message);
    }
}
