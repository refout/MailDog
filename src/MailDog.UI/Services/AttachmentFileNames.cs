using System.Collections.Generic;
using System.IO;

namespace MailDog.UI.Services;

/// <summary>附件落盘时统一净化文件名，避免路径穿越、非法字符和覆盖已有文件。</summary>
public static class AttachmentFileNames
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private const string CrossPlatformInvalidChars = "<>:\"/\\|?*";

    public static string Sanitize(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return "attachment";
        }

        var chars = new char[name.Length];
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            chars[index] = char.IsControl(character)
                || CrossPlatformInvalidChars.Contains(character)
                || Path.GetInvalidFileNameChars().Contains(character)
                ? '_'
                : character;
        }

        name = new string(chars).Trim().TrimEnd('.', ' ');
        if (name.Length == 0)
        {
            return "attachment";
        }

        var extension = Path.GetExtension(name);
        var baseName = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(baseName))
        {
            name = "_" + name;
        }

        if (name.Length <= 180)
        {
            return name;
        }

        var suffix = extension.Length is > 1 and < 40 ? extension : string.Empty;
        var baseLength = Math.Max(1, 180 - suffix.Length);
        return name[..Math.Min(baseLength, name.Length)] + suffix;
    }

    /// <summary>在目标目录里生成不覆盖已有文件的路径；null 集合仅检查磁盘。</summary>
    public static string CreateUniquePath(
        string directory,
        string? fileName,
        ISet<string>? reservedPaths = null)
    {
        var safeName = Sanitize(fileName);
        var baseName = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);

        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var candidateName = attempt == 0
                ? safeName
                : $"{baseName} ({attempt}){extension}";
            var path = Path.Combine(directory, candidateName);
            if (!File.Exists(path)
                && (reservedPaths is null || !reservedPaths.Contains(path)))
            {
                reservedPaths?.Add(path);
                return path;
            }
        }

        var fallback = Path.Combine(directory, $"attachment-{Guid.NewGuid():N}{extension}");
        reservedPaths?.Add(fallback);
        return fallback;
    }
}
