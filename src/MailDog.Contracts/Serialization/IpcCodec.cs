using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MailDog.Contracts.Ipc;

namespace MailDog.Contracts.Serialization;

public interface IIpcCodec
{
    string Type { get; }

    Type MessageType { get; }

    JsonElement Serialize(IIpcMessage message);

    IIpcMessage Deserialize(JsonElement body);
}

internal sealed class IpcCodec<T> : IIpcCodec where T : IIpcMessage
{
    private readonly JsonTypeInfo<T> _typeInfo;

    public IpcCodec(string type, JsonTypeInfo<T> typeInfo)
    {
        Type = type;
        _typeInfo = typeInfo;
    }

    public string Type { get; }

    public Type MessageType => typeof(T);

    public JsonElement Serialize(IIpcMessage message)
    {
        if (message is not T typed)
        {
            throw new ArgumentException($"Expected {typeof(T).Name}, got {message.GetType().Name}.", nameof(message));
        }

        return JsonSerializer.SerializeToElement(typed, _typeInfo);
    }

    public IIpcMessage Deserialize(JsonElement body)
        => JsonSerializer.Deserialize(body, _typeInfo)
           ?? throw new JsonException($"Failed to deserialize {typeof(T).Name}.");
}
