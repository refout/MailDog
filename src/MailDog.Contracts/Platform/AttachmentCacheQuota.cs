using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MailDog.Contracts.Platform;

/// <summary>附件拖出缓存：超过配额时按最后访问时间淘汰旧文件，刚拖出的文件受保护。</summary>
public static class AttachmentCacheQuota
{
    public const long DefaultLimitBytes = 512L * 1024 * 1024;

    public static int Enforce(
        string directory,
        long limitBytes = DefaultLimitBytes,
        IReadOnlyCollection<string>? protectedPaths = null)
    {
        if (!Directory.Exists(directory) || limitBytes <= 0)
        {
            return 0;
        }

        var protectedSet = new HashSet<string>(
            (protectedPaths ?? [])
                .Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);

        var files = new List<(string Path, long Length, DateTime LastWrite)>();
        long total = 0;

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(path);
                files.Add((path, info.Length, info.LastWriteTimeUtc));
                total += info.Length;
            }
            catch
            {
                // 正在被其他进程占用的文件跳过。
            }
        }

        if (total <= limitBytes)
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in files
                     .Where(file => !protectedSet.Contains(Path.GetFullPath(file.Path)))
                     .OrderBy(file => file.LastWrite))
        {
            if (total <= limitBytes)
            {
                break;
            }

            try
            {
                File.Delete(file.Path);
                total -= file.Length;
                removed++;
            }
            catch
            {
                // 文件被占用或权限不足时继续尝试更旧的文件。
            }
        }

        return removed;
    }
}
