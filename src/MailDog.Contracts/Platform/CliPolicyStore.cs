using System.Text;
using System.Text.Json;
using MailDog.Contracts.Dto;
using MailDog.Contracts.Serialization;

namespace MailDog.Contracts.Platform;

/// <summary>
/// CLI 权限策略文件。UI 写入，CLI 读取；文件缺失或损坏时按默认全权限处理，
/// 避免升级后所有自动化脚本突然失效。
/// </summary>
public static class CliPolicyStore
{
    public const string FileName = "cli-policy.json";

    public static string Path
        => System.IO.Path.Combine(ProductPaths.DataDirectory, FileName);

    public static CliPolicyDto Load(string? path = null)
    {
        var target = path ?? Path;

        try
        {
            if (!File.Exists(target))
            {
                return new CliPolicyDto();
            }

            var json = File.ReadAllText(target, Encoding.UTF8);
            var policy = JsonSerializer.Deserialize(json, IpcJsonContext.Default.CliPolicyDto);
            return policy ?? new CliPolicyDto();
        }
        catch
        {
            return new CliPolicyDto();
        }
    }

    public static void Save(CliPolicyDto policy, string? path = null)
    {
        var target = path ?? Path;
        var directory = System.IO.Path.GetDirectoryName(target);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(policy, IpcJsonContext.Default.CliPolicyDto);
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, target, true);
    }
}
