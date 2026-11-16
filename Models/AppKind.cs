using System.Text.Json;
using System.Text.Json.Serialization;

namespace CursorAccountSwitcher.Models;

/// <summary>切换器能启动的应用。旧账号档案没有该字段时视为 Cursor。</summary>
public enum AppKind
{
    Cursor,
    Antigravity,
}

/// <summary>读写账号档案里的应用字段。缺字段、数字、无法识别的字符串都落回 Cursor，避免整份档案读失败。</summary>
public sealed class AppKindJsonConverter : JsonConverter<AppKind>
{
    public override AppKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return string.Equals(reader.GetString(), "antigravity", StringComparison.OrdinalIgnoreCase)
                ? AppKind.Antigravity
                : AppKind.Cursor;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n))
        {
            return n == (int)AppKind.Antigravity ? AppKind.Antigravity : AppKind.Cursor;
        }

        return AppKind.Cursor;
    }

    public override void Write(Utf8JsonWriter writer, AppKind value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value == AppKind.Antigravity ? "antigravity" : "cursor");
    }
}
