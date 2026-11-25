using MailDog.Contracts.Dto;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Tests;

public sealed class MailSummaryTagTests
{
    [Fact]
    public void SetTagsControlsPreviewAndTagRows()
    {
        var message = new MailSummaryViewModel(CreateMail());
        Assert.True(message.ShowPreviewRow);
        Assert.False(message.ShowTagRow);
        Assert.False(message.HasTags);

        message.SetTags(
        [
            new TagDto { Id = 1, Name = "客户", Color = "#AABBCC" },
            new TagDto { Id = 2, Name = "项目", Color = "#0F7B0F" }
        ]);

        Assert.True(message.HasTags);
        Assert.True(message.ShowTagRow);
        Assert.False(message.ShowPreviewRow);
        Assert.Equal(2, message.Tags.Count);
        Assert.True(message.HasTag(1));
        Assert.False(message.HasTag(3));
        Assert.Equal("客户", message.Tags[0].Name);

        message.SetTags([]);
        Assert.False(message.HasTags);
        Assert.False(message.ShowTagRow);
        Assert.True(message.ShowPreviewRow);
    }

    [Fact]
    public void TagColorFallsBackWhenInvalid()
    {
        var chip = new MessageTagChipViewModel(new TagDto { Id = 1, Name = "坏颜色", Color = "red" });
        Assert.Equal("red", chip.Color);
        Assert.NotNull(chip.Background);
        Assert.NotNull(chip.Border);
        Assert.NotNull(chip.Foreground);
    }

    private static MailSummaryDto CreateMail()
        => new()
        {
            Id = 10,
            FolderId = 20,
            AccountId = "account-1",
            Subject = "主题",
            FromName = "Alice",
            FromAddress = "alice@example.com",
            Preview = "预览",
            Date = DateTimeOffset.UtcNow,
            Flags = MailFlags.None
        };
}
