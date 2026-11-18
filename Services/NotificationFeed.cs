using System.IO;
using System.Text;
using CursorAccountSwitcher.Models;
using Microsoft.Data.Sqlite;

namespace CursorAccountSwitcher.Services;

/// <summary>
/// 读取 Windows 通知中心数据库，取出 Cursor / Antigravity / Codex 的通知。
///
/// 为什么需要这个：Windows 点击 toast 时按 AUMID 路由到注册的 COM 激活器。
/// 实测 Cursor 是 CLSID {E058B543-…} → LocalServer32 = Cursor.exe（不带任何参数），
/// 而所有实例共用同一个 AUMID（Anysphere.Cursor），所以点击必然打开默认数据目录那个实例。
/// AUMID 由应用内部硬编码，外部改不了，所以「点通知跳到发通知的那个账号」只能由切换器自己记归属。
/// </summary>
public static class NotificationFeed
{
    /// <summary>一条通知。</summary>
    public sealed record Item(long Id, AppKind App, string Title, string Body, DateTime Arrival);

    private static string DbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Microsoft\Windows\Notifications\wpndatabase.db");

    /// <summary>只关心最近这段时间的通知，翻太旧的没有意义。</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>
    /// 读最近的通知。取不到（文件被占用、结构变化）时返回空列表，不影响界面。
    /// </summary>
    public static IReadOnlyList<Item> ReadRecent(int limit = 40)
    {
        if (!File.Exists(DbPath))
        {
            return Array.Empty<Item>();
        }

        var items = new List<Item>();
        var cutoff = DateTime.Now - MaxAge;

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            // Notification.HandlerId 关联的是 NotificationHandler.RecordId。
            command.CommandText = @"
                SELECT n.Id, n.Payload, n.ArrivalTime, h.PrimaryId
                FROM Notification n
                JOIN NotificationHandler h ON n.HandlerId = h.RecordId
                WHERE n.Type = 'toast'
                ORDER BY n.ArrivalTime DESC
                LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var aumid = reader.IsDBNull(3) ? "" : reader.GetString(3);
                var app = AppOfAumid(aumid);
                if (app is null)
                {
                    continue;
                }

                var payload = reader.IsDBNull(1) ? "" : Decode(reader.GetValue(1));
                var (title, body) = ParseToast(payload);
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var arrival = reader.IsDBNull(2) ? DateTime.Now : FromFileTime(reader.GetInt64(2));
                if (arrival < cutoff)
                {
                    continue;
                }

                items.Add(new Item(reader.GetInt64(0), app.Value, title, body, arrival));
            }
        }
        catch
        {
            // 通知库被系统独占、或版本结构不同：当作没有通知。
        }

        return items;
    }

    private static AppKind? AppOfAumid(string aumid)
    {
        if (aumid.Contains("Cursor", StringComparison.OrdinalIgnoreCase))
        {
            return AppKind.Cursor;
        }

        if (aumid.Contains("Antigravity", StringComparison.OrdinalIgnoreCase))
        {
            return AppKind.Antigravity;
        }

        if (aumid.Contains("Codex", StringComparison.OrdinalIgnoreCase)
            || aumid.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase))
        {
            return AppKind.Codex;
        }

        return null;
    }

    private static string Decode(object raw) => raw switch
    {
        string s => s,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        _ => raw.ToString() ?? string.Empty,
    };

    /// <summary>
    /// 从 toast XML 里取标题和正文。
    /// Cursor 的格式是 &lt;text&gt;Done • 会话标题&lt;/text&gt;&lt;text&gt;Open Cursor to …&lt;/text&gt;，
    /// 第一个 text 里的「Done • / Input needed • 」前缀要去掉。
    /// </summary>
    private static (string Title, string Body) ParseToast(string xml)
    {
        var texts = new List<string>();
        var index = 0;

        while (texts.Count < 2)
        {
            var open = xml.IndexOf("<text>", index, StringComparison.OrdinalIgnoreCase);
            if (open < 0)
            {
                break;
            }

            open += "<text>".Length;
            var close = xml.IndexOf("</text>", open, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                break;
            }

            texts.Add(Unescape(xml[open..close]));
            index = close + "</text>".Length;
        }

        if (texts.Count == 0)
        {
            return ("", "");
        }

        var title = texts[0];
        var sep = title.IndexOf(" • ", StringComparison.Ordinal);
        if (sep >= 0)
        {
            title = title[(sep + 3)..];
        }

        var body = texts.Count > 1 ? texts[1] : "";
        return (title.Trim(), body.Trim());
    }

    private static string Unescape(string text) => text
        .Replace("&lt;", "<")
        .Replace("&gt;", ">")
        .Replace("&quot;", "\"")
        .Replace("&apos;", "'")
        .Replace("&amp;", "&");

    private static DateTime FromFileTime(long fileTime)
    {
        try
        {
            return DateTime.FromFileTimeUtc(fileTime).ToLocalTime();
        }
        catch
        {
            return DateTime.Now;
        }
    }
}
