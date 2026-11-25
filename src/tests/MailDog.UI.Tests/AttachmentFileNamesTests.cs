using MailDog.UI.Services;

namespace MailDog.UI.Tests;

public sealed class AttachmentFileNamesTests
{
    [Fact]
    public void SanitizesPathTraversalInvalidCharsAndReservedNames()
    {
        Assert.Equal("passwd", AttachmentFileNames.Sanitize("../../etc/passwd"));
        Assert.Equal("a_b.txt", AttachmentFileNames.Sanitize("a?b.txt"));
        Assert.Equal("_CON.txt", AttachmentFileNames.Sanitize("CON.txt"));
        Assert.Equal("attachment", AttachmentFileNames.Sanitize("..."));
        Assert.Equal("attachment", AttachmentFileNames.Sanitize("   "));
    }

    [Fact]
    public void CreateUniquePathAvoidsExistingAndReservedFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "maildog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var first = Path.Combine(directory, "报告.pdf");
            File.WriteAllText(first, "old");
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var second = AttachmentFileNames.CreateUniquePath(directory, "报告.pdf", reserved);
            Assert.Equal("报告 (1).pdf", Path.GetFileName(second));

            var third = AttachmentFileNames.CreateUniquePath(directory, "报告.pdf", reserved);
            Assert.Equal("报告 (2).pdf", Path.GetFileName(third));
            Assert.False(File.Exists(third));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
