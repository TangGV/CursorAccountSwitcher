using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace CursorAccountSwitcher.Services;

/// <summary>定位 Antigravity 2.x 独立版（Antigravity.exe），不选 Antigravity IDE。</summary>
public static class AntigravityLocator
{
    public sealed record Install(string ExePath, string Version, bool IsRunning)
    {
        public string Display => string.IsNullOrWhiteSpace(Version)
            ? ExePath
            : $"v{Version} · {ExePath}";
    }

    public static Install? FindInstall()
    {
        var installs = CollectInstalls();
        if (installs.Count == 0)
        {
            return null;
        }

        return installs
            .OrderByDescending(i => ParseVersion(i.Version))
            .ThenByDescending(i => i.IsRunning)
            .ThenByDescending(i => i.ExePath.Contains(@"\Programs\antigravity\", StringComparison.OrdinalIgnoreCase))
            .First();
    }

    public static IReadOnlyList<Install> CollectInstalls()
    {
        var running = CollectRunningExePaths();
        var found = new Dictionary<string, Install>(StringComparer.OrdinalIgnoreCase);

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

            if (!File.Exists(full) || found.ContainsKey(full) || !IsHubExe(full))
            {
                continue;
            }

            found[full] = new Install(
                full,
                ReadVersion(full),
                running.Any(p => PathsEqual(p, full)));
        }

        return found.Values.ToList();
    }

    private static bool IsHubExe(string path) =>
        path.EndsWith(@"\Antigravity.exe", StringComparison.OrdinalIgnoreCase);

    private static Version ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Version(0, 0);
        }

        var digits = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).TrimEnd('.');
        return Version.TryParse(digits, out var version) ? version : new Version(0, 0);
    }

    private static string ReadVersion(string exePath)
    {
        try
        {
            var product = FileVersionInfo.GetVersionInfo(exePath).ProductVersion ?? string.Empty;
            if (Version.TryParse(product, out var version) && version.Build >= 0 && version.Revision == 0)
            {
                return $"{version.Major}.{version.Minor}.{version.Build}";
            }

            return product;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static List<string> CollectRunningExePaths()
    {
        var result = new List<string>();
        try
        {
            foreach (var process in Process.GetProcessesByName("Antigravity"))
            {
                try
                {
                    var fileName = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(fileName)
                        && IsHubExe(fileName)
                        && !result.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add(fileName);
                    }
                }
                catch
                {
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
        foreach (var path in CollectRunningExePaths())
        {
            yield return path;
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "antigravity", "Antigravity.exe");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Antigravity", "Antigravity.exe");

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                foreach (var path in ReadUninstall(hive, view))
                {
                    yield return path;
                }
            }
        }

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

            if (!string.IsNullOrWhiteSpace(target))
            {
                yield return target;
            }
        }
    }

    private static IEnumerable<string> ReadUninstall(RegistryHive hive, RegistryView view)
    {
        RegistryKey? root = null;
        try
        {
            root = RegistryKey.OpenBaseKey(hive, view)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        }
        catch
        {
            yield break;
        }

        if (root is null)
        {
            yield break;
        }

        using (root)
        {
            foreach (var name in root.GetSubKeyNames())
            {
                string? display = null;
                string? icon = null;
                string? location = null;
                try
                {
                    using var key = root.OpenSubKey(name);
                    display = key?.GetValue("DisplayName") as string;
                    icon = key?.GetValue("DisplayIcon") as string;
                    location = key?.GetValue("InstallLocation") as string;
                }
                catch
                {
                    continue;
                }

                if (display is null
                    || !display.Contains("Antigravity", StringComparison.OrdinalIgnoreCase)
                    || display.Contains("IDE", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fromIcon = ExeFromIcon(icon);
                if (fromIcon is not null)
                {
                    yield return fromIcon;
                }

                if (!string.IsNullOrWhiteSpace(location))
                {
                    yield return Path.Combine(location, "Antigravity.exe");
                }
            }
        }
    }

    private static string? ExeFromIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        var text = icon.Trim().Trim('"');
        var comma = text.LastIndexOf(',');
        if (comma > 0)
        {
            var head = text[..comma].Trim().Trim('"');
            if (head.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                text = head;
            }
        }

        if (text.EndsWith("Antigravity.exe", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return Path.Combine(text, "Antigravity.exe");
    }

    private static IEnumerable<string> EnumerateShortcuts()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
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
                files = Directory.GetFiles(folder, "Antigravity*.lnk", SearchOption.AllDirectories);
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
