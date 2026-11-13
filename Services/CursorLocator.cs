using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace CursorAccountSwitcher.Services;

/// <summary>
/// 定位 Cursor 的可执行文件。
///
/// 一台机器上经常同时存在多份 Cursor：迁移到其它盘的旧安装、Program Files 里的新安装、
/// 用户目录里的绿色版。**不能用“第一个找到的”**——旧版和新版连后端的协议不一样，
/// 用旧版启动独立实例会持续 Premature close / TLS 中断。所以这里收集所有候选，
/// 按安装版本号从高到低挑一个。
/// </summary>
public static class CursorLocator
{
    /// <summary>一个可用的 Cursor 安装。</summary>
    public sealed record CursorInstall(string ExePath, string Version, bool IsRunning)
    {
        public string Display => string.IsNullOrWhiteSpace(Version)
            ? ExePath
            : $"v{Version} · {ExePath}";
    }

    private static CursorInstall? _cached;

    /// <summary>挑选应该用来启动实例的 Cursor 可执行文件；找不到返回 null。</summary>
    public static string? Find() => FindInstall()?.ExePath;

    /// <summary>挑选应该用来启动实例的 Cursor 安装（含版本，用于界面提示）；找不到返回 null。</summary>
    public static CursorInstall? FindInstall()
    {
        var installs = CollectInstalls();
        if (installs.Count == 0)
        {
            _cached = null;
            return null;
        }

        // 版本高的优先（协议兼容、不会连不上后端）；同版本里优先正在运行的那份，
        // 再优先 Program Files 这类“正式安装”位置。
        var chosen = installs
            .OrderByDescending(i => ParseVersion(i.Version))
            .ThenByDescending(i => i.IsRunning)
            .ThenByDescending(i => IsProgramFiles(i.ExePath))
            .First();

        _cached = chosen;
        return chosen;
    }

    /// <summary>收集本机所有能用的 Cursor 可执行文件（已去重）。</summary>
    public static IReadOnlyList<CursorInstall> CollectInstalls()
    {
        var runningPaths = CollectRunningExePaths();
        var found = new Dictionary<string, CursorInstall>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in EnumerateCandidates())
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (!File.Exists(full) || found.ContainsKey(full))
            {
                continue;
            }

            found[full] = new CursorInstall(
                full,
                ReadVersion(full),
                runningPaths.Any(p => PathsEqual(p, full)));
        }

        return found.Values.ToList();
    }

    private static bool IsProgramFiles(string path) =>
        path.Contains(@"\Program Files", StringComparison.OrdinalIgnoreCase);

    private static Version ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Version(0, 0);
        }

        // ProductVersion 可能是 "3.30.8" 或 "3.30.8 (user setup)"。
        var digits = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(digits, out var version) ? version : new Version(0, 0);
    }

    private static string ReadVersion(string exePath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(exePath).ProductVersion ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>正在运行的 Cursor 主进程 exe 路径（用户此刻在用哪一份，这是最强信号）。</summary>
    private static List<string> CollectRunningExePaths()
    {
        var result = new List<string>();
        try
        {
            foreach (var process in Process.GetProcessesByName("Cursor"))
            {
                try
                {
                    var fileName = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(fileName) && !result.Contains(fileName!, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add(fileName!);
                    }
                }
                catch
                {
                    // 权限不足或进程已退出，忽略。
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private static IEnumerable<string?> EnumerateCandidates()
    {
        // 1. 正在运行的实例：用户实际在用的那份。
        foreach (var path in CollectRunningExePaths())
        {
            yield return path;
        }

        // 2. 注册表卸载项 / App Paths 里的安装路径。
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (var subKey in new[]
                         {
                             @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{A9C8B4F9-9A0B-4E5C-9E0B-C8B5D2E2A8F0}_is1",
                             @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Cursor.exe",
                         })
                {
                    string? installLocation = null;
                    string? defaultValue = null;
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var key = baseKey.OpenSubKey(subKey);
                        defaultValue = key?.GetValue(null) as string;
                        installLocation = key?.GetValue("InstallLocation") as string;
                    }
                    catch
                    {
                    }

                    // App Paths 的默认值就是 exe；卸载项的默认值可能是显示名，必须过滤。
                    if (defaultValue is not null
                        && defaultValue.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return defaultValue;
                    }

                    if (!string.IsNullOrWhiteSpace(installLocation))
                    {
                        yield return NormalizeExe(installLocation!);
                    }
                }
            }
        }

        // 3. 常规安装位置（Program Files 里的正式安装）。
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Cursor", "Cursor.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Cursor", "Cursor.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "cursor", "Cursor.exe");

        // 4. 快捷方式指向的目标：放最后，因为桌面快捷方式常常指向被迁移走的旧安装。
        foreach (var lnk in EnumerateShortcuts())
        {
            string? target = null;
            try
            {
                var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                dynamic shortcut = shell!.GetType().InvokeMember(
                    "CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null, shell, new object[] { lnk })!;
                target = shortcut.TargetPath as string;
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(target) && target!.EndsWith("Cursor.exe", StringComparison.OrdinalIgnoreCase))
            {
                yield return target;
            }
        }
    }

    private static string? NormalizeExe(string path)
    {
        if (path.EndsWith("Cursor.exe", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var exe = Path.Combine(path, "Cursor.exe");
        return File.Exists(exe) ? exe : null;
    }

    private static IEnumerable<string> EnumerateShortcuts()
    {
        var folders = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Microsoft\Windows\Start Menu\Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs"),
        };

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "Cursor*.lnk", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
