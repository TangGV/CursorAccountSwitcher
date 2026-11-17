using System.IO;
using CursorAccountSwitcher.Models;

namespace CursorAccountSwitcher.Services;

/// <summary>Cursor 与 Antigravity 共用的路径、进程名和副账号隔离参数。</summary>
public static class AppCatalog
{
    public static AppKind Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "antigravity" => AppKind.Antigravity,
        "codex" => AppKind.Codex,
        _ => AppKind.Cursor,
    };

    public static string ToKey(AppKind kind) => kind switch
    {
        AppKind.Antigravity => "antigravity",
        AppKind.Codex => "codex",
        _ => "cursor",
    };

    public static string Label(AppKind kind) => kind switch
    {
        AppKind.Antigravity => "Antigravity",
        AppKind.Codex => "Codex",
        _ => "Cursor",
    };

    public static string ProcessName(AppKind kind) => kind switch
    {
        AppKind.Antigravity => "Antigravity.exe",
        AppKind.Codex => "ChatGPT.exe",
        _ => "Cursor.exe",
    };

    public static string DefaultDataDir(AppKind kind) => kind switch
    {
        AppKind.Antigravity => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Antigravity"),
        AppKind.Codex => CodexLocator.DefaultCodexHome,
        _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor"),
    };

    public static string ProfilesDir(AppKind kind) => kind switch
    {
        AppKind.Antigravity => Path.Combine(AppPrefs.RootDir, "antigravity"),
        AppKind.Codex => Path.Combine(AppPrefs.RootDir, "codex"),
        _ => AppPrefs.DefaultProfilesDir,
    };

    /// <summary>
    /// 副账号沙箱根目录（userdata 与 home 的共同父目录）。只承认切换器自己目录下的路径。
    /// </summary>
    public static string? SandboxRoot(AccountProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.HomeDir) || string.IsNullOrWhiteSpace(profile.DataDir))
        {
            return null;
        }

        string homeParent;
        string dataParent;
        try
        {
            homeParent = Path.GetDirectoryName(Path.GetFullPath(profile.HomeDir).TrimEnd('\\', '/')) ?? "";
            dataParent = Path.GetDirectoryName(Path.GetFullPath(profile.DataDir).TrimEnd('\\', '/')) ?? "";
        }
        catch
        {
            return null;
        }

        if (!string.Equals(homeParent, dataParent, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var root = Path.GetFullPath(AppPrefs.RootDir);
        if (!homeParent.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return homeParent;
    }

    public static string BrowseDir(AccountProfile profile) => SandboxRoot(profile) ?? profile.DataDir;

    /// <summary>
    /// Antigravity 副账号的进程环境。主账号返回 null，沿用真实用户目录和现有登录。
    ///
    /// 窗口数据靠 --user-data-dir。对话目录是 os.homedir()/.gemini/antigravity，
    /// 所以副账号还要换 USERPROFILE。语言服务发现 SSH_CONNECTION 后会把登录写进该目录，
    /// 而不是机器上那条共享凭据。
    /// </summary>
    public static IReadOnlyDictionary<string, string>? IsolatedEnvironment(AccountProfile profile)
    {
        if (profile.App != AppKind.Antigravity || profile.IsMain || string.IsNullOrWhiteSpace(profile.HomeDir))
        {
            return null;
        }

        var home = Path.GetFullPath(profile.HomeDir);
        var realHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.Equals(home.TrimEnd('\\'), realHome.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var drive = Path.GetPathRoot(home);
        if (string.IsNullOrEmpty(drive))
        {
            return null;
        }

        var driveNoSlash = drive.TrimEnd('\\');
        var rest = home.Length > driveNoSlash.Length ? home[driveNoSlash.Length..] : "\\";
        if (!rest.StartsWith('\\'))
        {
            rest = "\\" + rest;
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = home,
            ["HOME"] = home,
            ["HOMEDRIVE"] = driveNoSlash,
            ["HOMEPATH"] = rest,
            ["SSH_CONNECTION"] = "127.0.0.1 1234 127.0.0.1 22",
        };
    }
}
