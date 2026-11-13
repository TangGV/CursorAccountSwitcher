using System.IO;
using Microsoft.Data.Sqlite;

namespace CursorAccountSwitcher.Services;

/// <summary>从某个数据目录的 state.vscdb 里读取登录信息。Cursor 运行时也能只读打开（WAL 模式）。</summary>
public static class StateDbReader
{
    public sealed record LoginInfo(string? Email, string? Membership, string? AuthId);

    public static string GetStateDbPath(string userDataDir) =>
        Path.Combine(userDataDir, "User", "globalStorage", "state.vscdb");

    /// <summary>读取登录信息；文件不存在或打不开时返回 null。</summary>
    public static LoginInfo? Read(string userDataDir)
    {
        var dbPath = GetStateDbPath(userDataDir);
        if (!File.Exists(dbPath))
        {
            return null;
        }

        try
        {
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
            command.CommandText =
                "SELECT key, value FROM ItemTable WHERE key IN " +
                "('cursorAuth/cachedEmail','cursorAuth/stripeMembershipType','cursorAuth/stripeMembershipAuthId')";

            string? email = null;
            string? membership = null;
            string? authId = null;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var value = ReadValue(reader.GetValue(1));
                switch (key)
                {
                    case "cursorAuth/cachedEmail":
                        email = value;
                        break;
                    case "cursorAuth/stripeMembershipType":
                        membership = value;
                        break;
                    case "cursorAuth/stripeMembershipAuthId":
                        authId = value;
                        break;
                }
            }

            return new LoginInfo(email, membership, authId);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadValue(object raw) => raw switch
    {
        string s => s,
        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
        _ => raw.ToString() ?? string.Empty,
    };

    /// <summary>该数据目录是否已经有登录状态（存在 auth token）。</summary>
    public static bool HasAuthToken(string userDataDir)
    {
        var dbPath = GetStateDbPath(userDataDir);
        if (!File.Exists(dbPath))
        {
            return false;
        }

        try
        {
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
            command.CommandText =
                "SELECT COUNT(*) FROM ItemTable WHERE key = 'cursorAuth/refreshToken'";
            var result = command.ExecuteScalar();
            return Convert.ToInt64(result ?? 0L) > 0;
        }
        catch
        {
            return false;
        }
    }
}
