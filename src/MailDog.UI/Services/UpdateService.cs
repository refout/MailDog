using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using MailDog.Contracts.Platform;

namespace MailDog.UI.Services;

/// <summary>一次检查的结果；失败时 <see cref="Error"/> 有值，界面只给一句"稍后再试"。</summary>
public sealed record UpdateCheckResult(
    bool HasUpdate,
    string LatestVersion,
    string Notes,
    string? InstallerUrl,
    long InstallerSize,
    string? Sha256,
    string? PortableUrl,
    string ReleasePageUrl,
    string? Error);

/// <summary>
/// 更新检查与下载安装。只认 GitHub 的 releases/latest，不去猜别的下载渠道。
/// 装好的版本（在 Programs\MailDog 或 Program Files\MailDog 下）走静默安装；
/// 便携版只提示去下载页手动替换，避免把别人的绿色版改成安装版。
/// </summary>
public sealed class UpdateService
{
    private const string LatestEndpoint = "https://api.github.com/repos/refout/MailDog/releases/latest";
    private const string ReleasesPage = "https://github.com/refout/MailDog/releases/latest";

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public UpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MailDog-Updater");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
    }

    /// <summary>当前是从安装包装出来的，还是解压出来直接跑的。</summary>
    public static bool IsInstalledLayout()
    {
        var directory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var localPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");

        return directory.StartsWith(localPrograms, StringComparison.OrdinalIgnoreCase)
            || directory.StartsWith(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MailDog"),
                StringComparison.OrdinalIgnoreCase)
            || directory.StartsWith(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MailDog"),
                StringComparison.OrdinalIgnoreCase);
    }

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(LatestEndpoint, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return Empty(currentVersion, "GitHub 上还没有发布记录。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return Empty(currentVersion, $"GitHub 返回 {response.StatusCode}。");
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
            var latest = tag.TrimStart('v', 'V');
            var notes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? string.Empty : string.Empty;
            var page = root.TryGetProperty("html_url", out var pageElement)
                ? pageElement.GetString() ?? ReleasesPage
                : ReleasesPage;

            string? installerUrl = null;
            string? portableUrl = null;
            string? digest = null;
            long installerSize = 0;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;

                    if (name.EndsWith("-win-x64-setup.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        installerUrl = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                        installerSize = asset.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
                        digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
                    }
                    else if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        portableUrl = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
                    }
                }
            }

            var hasUpdate = CompareVersions(latest, currentVersion) > 0;

            return new UpdateCheckResult(
                hasUpdate,
                latest,
                notes,
                installerUrl,
                installerSize,
                NormalizeDigest(digest),
                portableUrl,
                page,
                null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            // 网络不通、超时、GitHub 改了返回：都只当"这次没查到"，不打扰用户。
            return Empty(currentVersion, "网络不通或 GitHub 暂时不可用。");
        }
    }

    /// <summary>下载安装包并校验 SHA-256，返回落盘路径；校验不通过会删掉文件并返回 null。</summary>
    public async Task<string?> DownloadInstallerAsync(
        UpdateCheckResult result,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(result.InstallerUrl))
        {
            return null;
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MailDog",
            "updates");

        Directory.CreateDirectory(directory);

        var fileName = $"MailDog-{Sanitize(result.LatestVersion)}-win-x64-setup.exe";
        var path = Path.Combine(directory, fileName);

        try
        {
            using var response = await _http.GetAsync(
                result.InstallerUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? result.InstallerSize;
            var buffer = new byte[81920];
            var written = 0L;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var target = File.Create(path);

            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                written += read;

                if (total > 0)
                {
                    progress?.Report(Math.Clamp((double)written / total, 0, 1));
                }
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(result.Sha256)
                && !string.Equals(actual, result.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                return null;
            }

            return path;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            TryDelete(path);
            return null;
        }
    }

    /// <summary>静默升级：等安装包跑完，再用新版本把自己拉起来。</summary>
    public async Task<bool> RunInstallerAsync(string installerPath, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = false,
            // /SILENT 只留进度条不弹问题；/CLOSEAPPLICATIONS 让 Restart Manager 关掉正在跑的 Engine 与界面；
            // /LAUNCHAPP=1 交给安装器在装完后拉起新版本，避免旧进程被关掉后无人启动。
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /SP- /LAUNCHAPP=1"
        };

        using var process = Process.Start(startInfo);

        if (process is null)
        {
            return false;
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            return false;
        }

        // 安装包与界面同目录，装完直接启新版本；当前进程随后退出。
        var uiPath = Path.Combine(AppContext.BaseDirectory, ProductPaths.UiExecutableName);

        if (File.Exists(uiPath))
        {
            Process.Start(new ProcessStartInfo(uiPath) { UseShellExecute = false });
        }

        return true;
    }

    public static string ReleasePageUrl => ReleasesPage;

    private static UpdateCheckResult Empty(string currentVersion, string error)
        => new(false, currentVersion, string.Empty, null, 0, null, null, ReleasesPage, error);

    private static string? NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        return digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest["sha256:".Length..].Trim()
            : digest.Trim();
    }

    private static string Sanitize(string version)
    {
        var builder = new System.Text.StringBuilder();

        foreach (var character in version)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '.' or '-' ? character : '_');
        }

        return builder.Length == 0 ? "latest" : builder.ToString();
    }

    /// <summary>按数字段比较，0.1.3 与 0.1.3.0 视为相同，v 前缀由调用方先去掉。</summary>
    private static int CompareVersions(string left, string right)
    {
        var leftParts = left.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var length = Math.Max(leftParts.Length, rightParts.Length);

        for (var index = 0; index < length; index++)
        {
            var leftValue = index < leftParts.Length
                && int.TryParse(leftParts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLeft)
                ? parsedLeft
                : 0;

            var rightValue = index < rightParts.Length
                && int.TryParse(rightParts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRight)
                ? parsedRight
                : 0;

            if (leftValue != rightValue)
            {
                return leftValue < rightValue ? -1 : 1;
            }
        }

        return 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉就留着，下次下载会覆盖。
        }
    }
}
