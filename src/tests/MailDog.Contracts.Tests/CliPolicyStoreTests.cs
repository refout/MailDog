using MailDog.Contracts.Dto;
using MailDog.Contracts.Platform;

namespace MailDog.Contracts.Tests;

public sealed class CliPolicyStoreTests
{
    [Fact]
    public void RoundTripsPolicyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"maildog-policy-{Guid.NewGuid():N}.json");

        try
        {
            var policy = new CliPolicyDto
            {
                ReadOnly = true,
                RedactBody = false,
                AuditRetentionDays = 7,
                AllowedCommands = ["draft"]
            };

            CliPolicyStore.Save(policy, path);
            var loaded = CliPolicyStore.Load(path);

            Assert.True(loaded.ReadOnly);
            Assert.False(loaded.RedactBody);
            Assert.Equal(7, loaded.AuditRetentionDays);
            Assert.Equal(["draft"], loaded.AllowedCommands);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BrokenFileFallsBackToFullPermissionDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"maildog-policy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "not json");

        try
        {
            var loaded = CliPolicyStore.Load(path);

            Assert.False(loaded.ReadOnly);
            Assert.True(loaded.RedactBody);
            Assert.True(loaded.IsAllowed("send"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
