using MailDog.Contracts.Dto;
using MailDog.Mail.Storage;

namespace MailDog.Mail.Tests;

public sealed class RuleMatcherTests
{
    [Fact]
    public void FromRuleMatchesAddressIgnoringCase()
    {
        var rule = new RuleDto { Field = "from", Pattern = "Noreply@" };

        Assert.True(RuleMatcher.Matches(rule, "随便一个标题", "通知", "noreply@example.com"));
    }

    [Fact]
    public void FromRuleMatchesDisplayName()
    {
        var rule = new RuleDto { Field = "from", Pattern = "GitHub" };

        Assert.True(RuleMatcher.Matches(rule, "MR merged", "GitHub Actions", "actions@github.com"));
    }

    [Fact]
    public void SubjectRuleIgnoresSender()
    {
        var rule = new RuleDto { Field = "subject", Pattern = "账单" };

        Assert.False(RuleMatcher.Matches(rule, "月度报告", "账单中心", "billing@example.com"));
        Assert.True(RuleMatcher.Matches(rule, "11 月账单已生成", "账单中心", "billing@example.com"));
    }

    [Fact]
    public void AnyRuleLooksAtBothFields()
    {
        var rule = new RuleDto { Field = "any", Pattern = "alert" };

        Assert.True(RuleMatcher.Matches(rule, "disk alert", "ops", "ops@example.com"));
        Assert.True(RuleMatcher.Matches(rule, "disk full", "Alert Bot", "bot@example.com"));
        Assert.False(RuleMatcher.Matches(rule, "daily report", "ops", "ops@example.com"));
    }

    [Fact]
    public void BlankPatternNeverMatches()
    {
        var rule = new RuleDto { Field = "any", Pattern = "   " };

        Assert.False(RuleMatcher.Matches(rule, "任意标题", "任意发件人", "any@example.com"));
    }
}
