using System.Net;
using System.Net.Http;
using MailDog.UI.Services;

namespace MailDog.UI.Tests;

public sealed class CrashReportUploaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "maildog-crash-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FailedUploadQueuesAndFlushesOnRetry()
    {
        Directory.CreateDirectory(_directory);
        var handler = new StubHandler(false, true);
        using var client = new HttpClient(handler);
        var uploader = new CrashReportUploader(client, Path.Combine(_directory, "pending"));
        var endpoint = new Uri("https://crash.example.com/report");

        Assert.False(await uploader.CaptureAsync(endpoint, "{\"stack\":\"boom\"}"));
        Assert.Equal(1, uploader.PendingCount());

        Assert.Equal(1, await uploader.FlushPendingAsync(endpoint));
        Assert.Equal(0, uploader.PendingCount());
    }

    [Fact]
    public async Task SuccessfulUploadDoesNotQueue()
    {
        Directory.CreateDirectory(_directory);
        var handler = new StubHandler(true);
        using var client = new HttpClient(handler);
        var uploader = new CrashReportUploader(client, Path.Combine(_directory, "pending"));

        Assert.True(await uploader.CaptureAsync(new Uri("https://crash.example.com/report"), "{}"));
        Assert.Equal(0, uploader.PendingCount());
    }

    [Theory]
    [InlineData("https://crash.example.com/report", true)]
    [InlineData("http://localhost:8080/report", true)]
    [InlineData("http://127.0.0.1:8080/report", true)]
    [InlineData("http://crash.example.com/report", false)]
    [InlineData("not a uri", false)]
    public void EndpointValidationRequiresTlsOrLoopback(string value, bool accepted)
    {
        var endpoint = CrashReportUploader.ResolveEndpoint(value, _directory);
        Assert.Equal(accepted, endpoint is not null);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch
        {
            // 清理失败不影响测试结论。
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly bool[] _results;
        private int _index;

        public StubHandler(params bool[] results)
        {
            _results = results;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var ok = _results[Math.Min(_index++, _results.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(
                ok ? HttpStatusCode.OK : HttpStatusCode.InternalServerError));
        }
    }
}
