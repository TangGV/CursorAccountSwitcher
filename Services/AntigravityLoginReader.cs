using System.IO;
using System.Text.Json;

namespace CursorAccountSwitcher.Services;

/// <summary>从 Antigravity 数据目录的 app_storage.json 读取上次登录名。只读这一个展示字段。</summary>
public static class AntigravityLoginReader
{
    public static string? ReadUsername(string userDataDir)
    {
        var path = Path.Combine(userDataDir, "app_storage.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("agy.onboarding.lastLoginUsername", out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var text = value.GetString()?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
