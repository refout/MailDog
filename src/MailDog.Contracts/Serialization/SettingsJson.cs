using System.Text.Json;
using MailDog.Contracts.Dto;

namespace MailDog.Contracts.Serialization;

/// <summary>settings 表里的复合字段统一走源生成 JSON，避免 AOT 下反射序列化。</summary>
public static class SettingsJson
{
    public static string SerializeAccountNotificationSounds(
        IReadOnlyList<AccountNotificationSoundDto> sounds)
        => JsonSerializer.Serialize(
            (sounds ?? []).ToArray(),
            IpcJsonContext.Default.AccountNotificationSoundDtoArray);

    public static IReadOnlyList<AccountNotificationSoundDto> DeserializeAccountNotificationSounds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(
                json,
                IpcJsonContext.Default.AccountNotificationSoundDtoArray) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeTemplates(IReadOnlyList<MailTemplateDto> templates)
        => JsonSerializer.Serialize(
            (templates ?? []).ToArray(),
            IpcJsonContext.Default.MailTemplateDtoArray);

    public static IReadOnlyList<MailTemplateDto> DeserializeTemplates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, IpcJsonContext.Default.MailTemplateDtoArray) ?? [];
        }
        catch (JsonException)
        {
            // 设置损坏时回退为空列表，避免整个 Engine 启动失败。
            return [];
        }
    }

    public static string SerializeAccountSignatures(IReadOnlyList<AccountSignatureDto> signatures)
        => JsonSerializer.Serialize(
            (signatures ?? []).ToArray(),
            IpcJsonContext.Default.AccountSignatureDtoArray);

    public static IReadOnlyList<AccountSignatureDto> DeserializeAccountSignatures(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, IpcJsonContext.Default.AccountSignatureDtoArray) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeAccountIdentities(IReadOnlyList<AccountIdentityDto> identities)
        => JsonSerializer.Serialize(
            (identities ?? []).ToArray(),
            IpcJsonContext.Default.AccountIdentityDtoArray);

    public static IReadOnlyList<AccountIdentityDto> DeserializeAccountIdentities(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(json, IpcJsonContext.Default.AccountIdentityDtoArray) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
