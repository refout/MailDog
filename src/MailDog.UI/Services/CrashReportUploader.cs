using System.Net.Http;
using System.Text;

namespace MailDog.UI.Services;

/// <summary>把崩溃摘要 POST 到配置的端点；失败时落盘，下次启动重试。</summary>
public sealed class CrashReportUploader
{
    public const string EndpointFileName = "crash-endpoint.txt";
    public const string PendingDirectoryName = "crash-pending";

    private readonly HttpClient _http;
    private readonly string _pendingDirectory;

    public CrashReportUploader(HttpClient http, string pendingDirectory)
    {
        _http = http;
        _pendingDirectory = pendingDirectory;
    }

    /// <summary>配置优先取环境变量，其次取数据目录里的 crash-endpoint.txt；只接受 https 或本机 http。</summary>
    public static Uri? ResolveEndpoint(string? configured, string dataDirectory)
    {
        var value = configured;
        if (string.IsNullOrWhiteSpace(value))
        {
            var file = Path.Combine(dataDirectory, EndpointFileName);
            if (File.Exists(file))
            {
                value = File.ReadAllText(file).Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var endpoint))
        {
            return null;
        }

        if (endpoint.Scheme == Uri.UriSchemeHttps)
        {
            return endpoint;
        }

        if (endpoint.Scheme == Uri.UriSchemeHttp &&
            (endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
             endpoint.Host == "127.0.0.1" ||
             endpoint.Host == "::1"))
        {
            return endpoint;
        }

        return null;
    }

    public async Task<bool> CaptureAsync(Uri endpoint, string payload, CancellationToken cancellationToken = default)
    {
        if (await TrySendAsync(endpoint, payload, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        try
        {
            Queue(payload);
        }
        catch
        {
            // 落盘失败也不能再抛出异常。
        }

        return false;
    }

    public async Task<bool> TrySendAsync(Uri endpoint, string payload, CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public string Queue(string payload)
    {
        Directory.CreateDirectory(_pendingDirectory);
        var path = Path.Combine(
            _pendingDirectory,
            $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, payload, Encoding.UTF8);
        return path;
    }

    public int PendingCount()
    {
        if (!Directory.Exists(_pendingDirectory))
        {
            return 0;
        }

        return Directory.EnumerateFiles(_pendingDirectory, "*.json").Count();
    }

    /// <summary>按文件名顺序重试待上传文件；成功一个删一个，返回成功数量。</summary>
    public async Task<int> FlushPendingAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_pendingDirectory))
        {
            return 0;
        }

        var sent = 0;
        foreach (var file in Directory.EnumerateFiles(_pendingDirectory, "*.json").OrderBy(path => path))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            string payload;
            try
            {
                payload = File.ReadAllText(file, Encoding.UTF8);
            }
            catch
            {
                continue;
            }

            if (await TrySendAsync(endpoint, payload, cancellationToken).ConfigureAwait(false))
            {
                sent++;
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // 删除失败下次再重试。
                }
            }
        }

        return sent;
    }
}
