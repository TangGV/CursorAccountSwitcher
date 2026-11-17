using System.IO;
using Microsoft.Win32;

namespace CursorAccountSwitcher.Services;

/// <summary>
/// 定位 Codex 桌面版（商店版 ChatGPT.exe）。它是 MSIX 应用，
/// 通过 WindowsApps 下的执行别名启动，才能保留包身份。
/// </summary>
public static class CodexLocator
{
    public sealed record Install(string ExePath, string Version, string Label)
    {
        public string Display => string.IsNullOrWhiteSpace(Version)
            ? $"{Label} · {ExePath}"
            : $"{Label} v{Version} · {ExePath}";
    }

    /// <summary>Codex 的默认数据目录（CODEX_HOME）。</summary>
    public static string DefaultCodexHome { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    private const string AliasName = "chatgpt-desktop.exe";

    public static Install? FindInstall()
    {
        foreach (var path in EnumerateCandidates())
        {
            var exe = Normalize(path);
            if (exe is null)
            {
                continue;
            }

            return new Install(exe, ReadVersion(exe), "Codex");
        }

        return null;
    }

    /// <summary>所有可用于启动的路径（别名优先，包内 exe 兜底）。</summary>
    private static IEnumerable<string> EnumerateCandidates()
    {
        // 1. 商店应用的执行别名：带包身份，是正常入口。
        var alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", AliasName);
        if (File.Exists(alias))
        {
            yield return alias;
        }

        // 2. 正在运行的 Codex 主进程：用户此刻在用的那一份。
        foreach (var name in new[] { "ChatGPT", "Codex" })
        {
            System.Diagnostics.Process[] processes;
            try
            {
                processes = System.Diagnostics.Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }

            foreach (var process in processes)
            {
                string? fileName = null;
                try
                {
                    fileName = process.MainModule?.FileName;
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }

                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    yield return fileName!;
                }
            }
        }

        // 3. 安装目录约定位置，按包版本倒序。
        var windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        if (!Directory.Exists(windowsApps))
        {
            yield break;
        }

        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(windowsApps, "OpenAI.Codex_*");
        }
        catch
        {
            yield break;
        }

        foreach (var dir in dirs.OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(dir, "app", "ChatGPT.exe");
        }
    }

    private static string? Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            path = Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }

        var name = Path.GetFileName(path);
        if (name.Equals(AliasName, StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(path) ? path : null;
        }

        if (!name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 只要包根的启动器，不要 resources 里的 codex.exe 之类的子组件。
        if (!File.Exists(path)
            || !path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\resources\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return path;
    }

    /// <summary>从包目录名取版本，例如 OpenAI.Codex_26.1111.2228.0_x64__2p2nqsd0c76g0。</summary>
    private static string ReadVersion(string exePath)
    {
        if (exePath.EndsWith(AliasName, StringComparison.OrdinalIgnoreCase))
        {
            var newest = Directory.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"))
                ? EnumerateCandidates()
                    .Where(p => p.EndsWith("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
                    .Select(ReadVersion)
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                : null;
            return newest ?? string.Empty;
        }

        try
        {
            var appDir = Path.GetDirectoryName(exePath);
            var packageDir = Path.GetDirectoryName(appDir) ?? string.Empty;
            var folder = Path.GetFileName(packageDir);
            var underscore = folder.IndexOf('_');
            if (underscore < 0)
            {
                return string.Empty;
            }

            var rest = folder[(underscore + 1)..];
            var next = rest.IndexOf('_');
            return next > 0 ? rest[..next] : rest;
        }
        catch
        {
            return string.Empty;
        }
    }
}
