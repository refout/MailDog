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
