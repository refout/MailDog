using MailDog.Contracts.Dto;

namespace MailDog.Contracts.Tests;

public sealed class CliPolicyDtoTests
{
    [Fact]
    public void DefaultsAllowKnownWritesButNotReadOnly()
    {
        var policy = new CliPolicyDto();

        Assert.False(policy.ReadOnly);
        Assert.True(policy.RedactBody);
        Assert.Equal(30, policy.AuditRetentionDays);
        Assert.True(policy.IsAllowed("send"));
        Assert.True(policy.IsAllowed("approve"));
    }

    [Fact]
    public void ReadOnlyBlocksEveryWriteButStillAllowsReads()
    {
        var policy = new CliPolicyDto { ReadOnly = true };

        Assert.False(policy.IsAllowed("send"));
        Assert.False(policy.IsAllowed("mark"));
        Assert.True(policy.IsAllowed("list"));
        Assert.True(policy.IsAllowed("pending"));
    }

    [Fact]
    public void AllowListCanDisableOneWrite()
    {
        var policy = new CliPolicyDto
        {
            AllowedCommands = ["draft", "mark"]
        };

        Assert.True(policy.IsAllowed("draft"));
        Assert.False(policy.IsAllowed("send"));
        Assert.True(policy.IsAllowed("show"));
    }
}
