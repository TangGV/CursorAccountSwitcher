using System.IO;
using Microsoft.Data.Sqlite;

namespace CursorAccountSwitcher.Services;

/// <summary>
/// 同步「会话体验类」数据：最近打开的项目、终端历史、编辑器布局等。
///
/// 这些键存在各自数据目录的 state.vscdb 里，不同账号默认是空白的。
/// 只同步白名单内的 UI 状态键，绝不触碰 cursorAuth/* 登录凭证和
/// 任何带账号 ID 的 key（避免把 A 账号的 ID 写到 B 账号里）。
/// </summary>
public static class SessionSync
{
    /// <summary>
    /// 允许同步的 key（白名单）。全部是纯 UI 状态，不含账号身份信息。
    /// </summary>
    private static readonly string[] WhitelistedKeys =
    {
        "history.recentlyOpenedPathsList",
        "workspaceMetadata.entries",
        "terminal.history.entries.commands",
        "terminal.history.entries.dirs",
        "cursor/glass.additionalProjects",
        "workbench.customize.primitiveSourceSnapshot.mcps.v3",
        "workbench.customize.primitiveSourceSnapshot.subagents.v3",
    };

    /// <summary>判断某个 key 是否允许同步：白名单内，且不含账号相关标记。</summary>
    private static bool IsSyncable(string key)
    {
        if (!WhitelistedKeys.Contains(key))
        {
            return false;
        }

        // 防御性检查：任何带 auth0 / google-oauth2 等身份标记的键都不碰。
        return !key.Contains("auth0", StringComparison.OrdinalIgnoreCase)
               && !key.Contains("oauth2", StringComparison.OrdinalIgnoreCase)
               && !key.Contains("cursorAuth", StringComparison.OrdinalIgnoreCase);
    }

    public sealed record SessionSyncResult(int Copied, int Skipped, string? Error);

    /// <summary>
    /// 把主账号的会话类 UI 状态复制到目标账号。
    /// 目标已有值且非空时跳过（保留目标账号自己的使用轨迹），只在目标为空时填充。
    /// </summary>
    public static SessionSyncResult SyncFromMain(string mainDataDir, string targetDataDir)
    {
        var mainDb = Path.Combine(mainDataDir, "User", "globalStorage", "state.vscdb");
        var targetDb = Path.Combine(targetDataDir, "User", "globalStorage", "state.vscdb");

        if (!File.Exists(mainDb))
        {
            return new SessionSyncResult(0, 0, "主账号 state.vscdb 不存在");
        }

        // 目标库必须已存在（即目标实例至少启动过一次），否则不主动创建，
        // 交给 Cursor 自己初始化，避免写出结构不完整的库。
        if (!File.Exists(targetDb))
        {
            return new SessionSyncResult(0, 0, "目标账号尚未初始化");
        }

        var copied = 0;
        var skipped = 0;

        try
        {
            var values = ReadWhitelisted(mainDb);
            if (values.Count == 0)
            {
                return new SessionSyncResult(0, 0, null);
            }

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = targetDb,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            };

            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            foreach (var (key, value) in values)
            {
                if (!IsSyncable(key))
                {
                    continue;
                }

                if (HasNonEmptyValue(connection, key))
                {
                    skipped++;
                    continue;
                }

                using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT INTO ItemTable (key, value) VALUES ($k, $v) " +
                    "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
                insert.Parameters.AddWithValue("$k", key);
                insert.Parameters.AddWithValue("$v", value);
                insert.ExecuteNonQuery();
                copied++;
            }
        }
        catch (Exception ex)
        {
            return new SessionSyncResult(copied, skipped, ex.Message);
        }

        return new SessionSyncResult(copied, skipped, null);
    }

    private static Dictionary<string, string> ReadWhitelisted(string dbPath)
    {
        var result = new Dictionary<string, string>();

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        var placeholders = string.Join(",", WhitelistedKeys.Select((_, i) => $"$k{i}"));
        command.CommandText = $"SELECT key, value FROM ItemTable WHERE key IN ({placeholders})";
        for (var i = 0; i < WhitelistedKeys.Length; i++)
        {
            command.Parameters.AddWithValue($"$k{i}", WhitelistedKeys[i]);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var raw = reader.GetValue(1);
            var value = raw switch
            {
                string s => s,
                byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                _ => raw.ToString() ?? string.Empty,
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static bool HasNonEmptyValue(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = $k";
        command.Parameters.AddWithValue("$k", key);

        var raw = command.ExecuteScalar();
        if (raw is null)
        {
            return false;
        }

        var text = raw switch
        {
            string s => s,
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            _ => raw.ToString() ?? string.Empty,
        };

        return !string.IsNullOrWhiteSpace(text);
    }
}
