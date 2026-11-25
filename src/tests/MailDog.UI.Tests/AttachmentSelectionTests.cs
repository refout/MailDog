using MailDog.Contracts.Dto;
using MailDog.Html;
using MailDog.UI.ViewModels;

namespace MailDog.UI.Tests;

public sealed class AttachmentSelectionTests
{
    [Fact]
    public async Task SelectAllAndSaveSelectedTracksSelection()
    {
        var reader = new ReaderViewModel();
        var savedCounts = new List<int>();
        reader.SaveAttachments = attachments =>
        {
            savedCounts.Add(attachments.Count);
            return Task.CompletedTask;
        };
        reader.Apply(
            new MailBodyDto
            {
                MessageId = 42,
                PlainText = "正文",
                Attachments =
                [
                    CreateAttachment("a.txt"),
                    CreateAttachment("b.txt")
                ]
            },
            "发件人",
            "现在",
            new HtmlSanitizer(),
            new HtmlDocumentBuilder(),
            allowRemoteImages: false,
            darkMode: false,
            backgroundHex: "#FFFFFF",
            preferPlainText: true);

        Assert.Equal(2, reader.Attachments.Length);
        reader.SelectAllAttachmentsCommand.Execute(null);
        Assert.Equal(2, reader.SelectedAttachmentCount);
        Assert.True(reader.HasSelectedAttachments);

        await reader.SaveSelectedAttachmentsCommand.ExecuteAsync(null);
        Assert.Equal(new[] { 2 }, savedCounts);

        reader.ClearAttachmentSelectionCommand.Execute(null);
        Assert.Equal(0, reader.SelectedAttachmentCount);
        Assert.False(reader.HasSelectedAttachments);
    }

    private static AttachmentDto CreateAttachment(string name)
        => new()
        {
            FileName = name,
            MimeType = "text/plain",
            Size = 10
        };
}
