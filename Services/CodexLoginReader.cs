using System.IO;
using System.Text.Json;

namespace CursorAccountSwitcher.Services;

/// <summary>从 Codex 的 auth.json 读取登录邮箱与套餐。只解析 id_token 的 claims，不打印 token。</summary>
public static class CodexLoginReader
{
    public sealed record LoginInfo(string? Email, string? Plan);

    public static LoginInfo Read(string codexHome)
    {
        var path = Path.Combine(codexHome, "auth.json");
        if (!File.Exists(path))
        {
            return new LoginInfo(null, null);
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("tokens", out var tokens)
                || tokens.ValueKind != JsonValueKind.Object)
            {
                return new LoginInfo(null, null);
            }

            foreach (var name in new[] { "id_token", "access_token" })
            {
                if (!tokens.TryGetProperty(name, out var tokenEl) || tokenEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var claims = ReadClaims(tokenEl.GetString());
                if (claims is null)
                {
                    continue;
                }

                var email = ReadEmail(claims.RootElement);
                if (string.IsNullOrWhiteSpace(email))
                {
                    continue;
                }

                return new LoginInfo(email, ReadPlan(claims.RootElement));
            }

            return new LoginInfo(null, null);
        }
        catch
        {
            return new LoginInfo(null, null);
        }
    }

    private static JsonDocument? ReadClaims(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return null;
        }

        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2: payload += "=="; break;
            case 3: payload += "="; break;
        }

        try
        {
            return JsonDocument.Parse(Convert.FromBase64String(payload));
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadEmail(JsonElement claims)
    {
        if (claims.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String)
        {
            return email.GetString();
        }

        // 企业号有时把邮箱放在 profile 声明里。
        if (claims.TryGetProperty("https://api.openai.com/profile", out var profile)
            && profile.ValueKind == JsonValueKind.Object
            && profile.TryGetProperty("email", out var nested)
            && nested.ValueKind == JsonValueKind.String)
        {
            return nested.GetString();
        }

        return null;
    }

    /// <summary>ChatGPT 套餐，例如 pro / plus / team。</summary>
    private static string? ReadPlan(JsonElement claims)
    {
        if (claims.TryGetProperty("https://api.openai.com/auth", out var auth)
            && auth.ValueKind == JsonValueKind.Object
            && auth.TryGetProperty("chatgpt_plan_type", out var plan)
            && plan.ValueKind == JsonValueKind.String)
        {
            return plan.GetString();
        }

        return null;
    }
}
