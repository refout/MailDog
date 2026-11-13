using System.Text.Json.Serialization;

namespace MailDog.Contracts.Dto;

/// <summary>
/// Microsoft identity platform token endpoint 响应。
/// 该类型必须出现在源生成上下文中，AOT 下不允许反射序列化。
/// </summary>
public sealed class OAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }
}

public sealed class OAuthTokenError
{
    [JsonPropertyName("error")]
    public string Error { get; init; } = string.Empty;

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }

    [JsonPropertyName("error_codes")]
    public int[]? ErrorCodes { get; init; }

    [JsonPropertyName("trace_id")]
    public string? TraceId { get; init; }

    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }
}
