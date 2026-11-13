using System.Text.Json;

namespace MailDog.Contracts.Ipc;

/// <summary>所有跨进程消息的标记接口。</summary>
public interface IIpcMessage
{
}

/// <summary>命名管道上的统一信封；Type 决定 Body 的具体类型。</summary>
public sealed class IpcEnvelope
{
    public string Type { get; init; } = string.Empty;

    /// <summary>请求/响应关联 ID；主动事件为 0。</summary>
    public long RequestId { get; init; }

    public JsonElement Body { get; init; }
}

public sealed class IpcErrorBody
{
    public string Code { get; init; } = IpcErrorCodes.Internal;

    public string Message { get; init; } = string.Empty;

    public string? Detail { get; init; }
}
