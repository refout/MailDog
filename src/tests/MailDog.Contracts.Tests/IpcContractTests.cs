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
    public void TagRequestsRoundTrip()
    {
        var save = new SaveTagRequest
        {
            Tag = new TagDto { Id = 7, Name = "客户", Color = "#AABBCC" }
        };
        var saveEnvelope = IpcCodecRegistry.Create(IpcMessageTypes.SaveTag, save);
        var parsedSave = Assert.IsType<SaveTagRequest>(
            IpcCodecRegistry.Read(IpcJson.Deserialize(IpcJson.Serialize(saveEnvelope))));
        Assert.Equal(7, parsedSave.Tag.Id);
        Assert.Equal("客户", parsedSave.Tag.Name);
        Assert.Equal("#AABBCC", parsedSave.Tag.Color);

        var set = new SetMessageTagsRequest
        {
            MessageIds = [11, 12],
            TagIds = [7, 8],
            Add = false
        };
        var setEnvelope = IpcCodecRegistry.Create(IpcMessageTypes.SetMessageTags, set);
        var parsedSet = Assert.IsType<SetMessageTagsRequest>(
            IpcCodecRegistry.Read(IpcJson.Deserialize(IpcJson.Serialize(setEnvelope))));
        Assert.Equal([11L, 12L], parsedSet.MessageIds);
        Assert.Equal([7L, 8L], parsedSet.TagIds);
        Assert.False(parsedSet.Add);

        var response = new MessageTagsResponse
        {
            Items = [new MessageTagAssignmentDto { MessageId = 11, TagIds = [7, 8] }]
        };
        var responseEnvelope = IpcCodecRegistry.Create(IpcMessageTypes.MessageTags, response);
        var parsedResponse = Assert.IsType<MessageTagsResponse>(
            IpcCodecRegistry.Read(IpcJson.Deserialize(IpcJson.Serialize(responseEnvelope))));
        var assignment = Assert.Single(parsedResponse.Items);
        Assert.Equal(11, assignment.MessageId);
        Assert.Equal([7L, 8L], assignment.TagIds);
    }

    [Fact]
    public void BackupRequestsCarryOptionalPassword()
    {
        var create = new CreateBackupRequest { DestinationPath = "C:\\backup.mdog", Password = "secret" };
        var createEnvelope = IpcCodecRegistry.Create(IpcMessageTypes.CreateBackup, create);
        var parsedCreate = Assert.IsType<CreateBackupRequest>(
            IpcCodecRegistry.Read(IpcJson.Deserialize(IpcJson.Serialize(createEnvelope))));
        Assert.Equal("C:\\backup.mdog", parsedCreate.DestinationPath);
        Assert.Equal("secret", parsedCreate.Password);

        var restore = new RestoreBackupRequest { ArchivePath = "C:\\backup.mdog", Password = "secret" };
        var restoreEnvelope = IpcCodecRegistry.Create(IpcMessageTypes.RestoreBackup, restore);
        var parsedRestore = Assert.IsType<RestoreBackupRequest>(
            IpcCodecRegistry.Read(IpcJson.Deserialize(IpcJson.Serialize(restoreEnvelope))));
        Assert.Equal("C:\\backup.mdog", parsedRestore.ArchivePath);
        Assert.Equal("secret", parsedRestore.Password);
    }

    [Fact]
    public void SettingsJsonFallsBackWhenStoredCollectionsAreCorrupt()
    {
        Assert.Empty(SettingsJson.DeserializeTemplates("{not-json"));
        Assert.Empty(SettingsJson.DeserializeAccountSignatures("[1,2,"));
        Assert.Empty(SettingsJson.DeserializeTemplates(null));
    }

    [Fact]
    public void MailChangedEventRoundTrips()
    {
        var envelope = IpcCodecRegistry.Create(
            IpcMessageTypes.MailChanged,
            new MailChangedEvent
            {
                AccountId = "account-1",
                MessageId = 42,
                FolderId = 7,
                Change = "moved"
            });

        var parsed = Assert.IsType<MailChangedEvent>(
            IpcCodecRegistry.Read(IpcJson.Deserialize(IpcJson.Serialize(envelope))));

        Assert.Equal("account-1", parsed.AccountId);
        Assert.Equal(42, parsed.MessageId);
        Assert.Equal(7, parsed.FolderId);
        Assert.Equal("moved", parsed.Change);
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
