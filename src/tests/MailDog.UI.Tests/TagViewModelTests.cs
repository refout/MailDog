using MailDog.Contracts.Dto;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Tests;

public sealed class TagViewModelTests
{
    [Fact]
    public void DeleteRequiresExplicitConfirmation()
    {
        var deleted = false;
        var tag = CreateTag(d => deleted = true);

        tag.AskDeleteCommand.Execute(null);
        Assert.True(tag.DeleteConfirmVisible);
        Assert.False(deleted);

        tag.CancelDeleteCommand.Execute(null);
        Assert.False(tag.DeleteConfirmVisible);
        Assert.False(deleted);

        tag.AskDeleteCommand.Execute(null);
        tag.ConfirmDeleteCommand.Execute(null);
        Assert.False(tag.DeleteConfirmVisible);
        Assert.True(deleted);
    }

    [Fact]
    public void InvalidColorFallsBackToDefaultBrush()
    {
        var tag = new TagViewModel(
            new TagDto { Id = 3, Name = "坏颜色", Color = "not-a-color" },
            _ => { },
            _ => { });

        Assert.Equal("坏颜色", tag.Name);
        Assert.Equal("not-a-color", tag.Color);
        Assert.NotNull(tag.Brush);
    }

    private static TagViewModel CreateTag(Action<TagViewModel> delete)
        => new(
            new TagDto { Id = 3, Name = "偏好", Color = "#AABBCC" },
            _ => { },
            delete);
}
