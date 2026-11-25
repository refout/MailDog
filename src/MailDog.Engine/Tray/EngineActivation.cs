using MailDog.Contracts.Ipc;
using MailDog.Contracts.Messages;

namespace MailDog.Engine.Tray;

/// <summary>
/// Linux 下 maildog:// 会再启动一个 Engine；单实例互斥发现已有进程时，
/// 通过本机管道把激活 URI 转给常驻进程，避免点通知只打印一句“已在运行”。
/// </summary>
internal static class EngineActivation
{
    public static bool Send(string uri)
    {
        try
        {
            return SendAsync(uri).GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }

    public static bool SendEml(string path)
        => Send($"maildog://eml?path={Uri.EscapeDataString(path)}");

    private static async Task<bool> SendAsync(string uri)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var client = await IpcClient.TryConnectAsync(TimeSpan.FromSeconds(2), cts.Token).ConfigureAwait(false);

        if (client is null)
        {
            return false;
        }

        try
        {
            await client.HelloAsync("MailDog.Engine", cts.Token).ConfigureAwait(false);
            await client.RequestAsync<OkResponse>(
                IpcMessageTypes.Activate,
                new ActivateRequest { Uri = uri },
                TimeSpan.FromSeconds(2),
                cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
