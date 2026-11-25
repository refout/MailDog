using MailDog.Contracts.Dto;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Tests;

public sealed class ComposeIdentityResolverTests
{
    [Fact]
    public void BuildsAccountDefaultThenDistinctAliases()
    {
        var account = new ComposeAccount("acc-1", "sender@example.com", "主地址");
        var identities = new[]
        {
            new AccountIdentityDto
            {
                Id = "alias-1",
                AccountId = "acc-1",
                Address = "alias@example.com",
                DisplayName = "别名"
            },
            new AccountIdentityDto
            {
                Id = "other-account",
                AccountId = "acc-2",
                Address = "other@example.com"
            },
            new AccountIdentityDto
            {
                Id = "duplicate",
                AccountId = "acc-1",
                Address = "SENDER@example.com"
            }
        };

        var result = ComposeIdentityResolver.BuildForAccount(account, identities);

        Assert.Equal(2, result.Count);
        Assert.True(result[0].IsAccountDefault);
        Assert.Equal("sender@example.com", result[0].Address);
        Assert.Equal("alias@example.com", result[1].Address);
        Assert.Equal("别名", result[1].DisplayName);
    }

    [Fact]
    public void PreferredAddressSelectsMatchingAlias()
    {
        var identities = ComposeIdentityResolver.BuildForAccount(
            new ComposeAccount("acc-1", "sender@example.com", "主地址"),
            [
                new AccountIdentityDto
                {
                    Id = "alias-1",
                    AccountId = "acc-1",
                    Address = "alias@example.com"
                }
            ]);

        var preferred = ComposeIdentityResolver.FindPreferred(identities, "ALIAS@example.com");
        Assert.NotNull(preferred);
        Assert.Equal("alias@example.com", preferred!.Address);

        var fallback = ComposeIdentityResolver.FindPreferred(identities, null);
        Assert.True(fallback!.IsAccountDefault);
    }
}
