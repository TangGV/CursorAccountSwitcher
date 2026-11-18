using System.Diagnostics;
using System.IO;
using System.Management;

namespace CursorAccountSwitcher.Services;

/// <summary>
/// 枚举 Cursor 进程，判断某个进程属于哪个数据目录。
/// Cursor 的主进程命令行形如：
///   "Cursor.exe"                          -> 默认数据目录
///   "Cursor.exe" --user-data-dir="..."    -> 指定数据目录
/// 子进程（--type=...）和 worker 脚本进程（...gitWorker.js）都不算独立实例。
/// </summary>
public static class CursorProcesses
{
    public sealed record CursorProcess(int Pid, int ParentPid, IntPtr MainWindowHandle, string? UserDataDir, bool IsInstance);

    public static IEnumerable<CursorProcess> Enumerate(string processName = "Cursor.exe") =>
        EnumerateMany(new[] { processName }).TryGetValue(processName, out var list)
            ? list
            : Array.Empty<CursorProcess>();

    /// <summary>
    /// 一次把多个进程名的信息全查回来，按进程名分组。
    ///
    /// WMI 每次查询都有约 600ms 的固定开销，逐个进程名查（Cursor / Antigravity / Codex …）
    /// 会线性叠加；合成一条 OR 查询后总耗时基本恒定。
    /// </summary>
    public static Dictionary<string, List<CursorProcess>> EnumerateMany(IEnumerable<string> processNames)
    {
        var grouped = new Dictionary<string, List<CursorProcess>>(StringComparer.OrdinalIgnoreCase);
        var names = processNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Replace("'", ""))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (names.Count == 0)
        {
            return grouped;
        }

        var filter = string.Join(" OR ", names.Select(n => $"Name = '{n}'"));
        var query = "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process WHERE " + filter;

        try
        {
            using var searcher = new ManagementObjectSearcher(query);

            foreach (var obj in searcher.Get())
            {
                try
                {
                    var pid = Convert.ToInt32(obj["ProcessId"] ?? 0);
                    var parent = Convert.ToInt32(obj["ParentProcessId"] ?? 0);
                    var name = obj["Name"] as string ?? string.Empty;
                    var cmd = obj["CommandLine"] as string ?? string.Empty;

                    IntPtr handle = IntPtr.Zero;
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        handle = process.MainWindowHandle;
                    }
                    catch
                    {
                    }

                    if (!grouped.TryGetValue(name, out var list))
                    {
                        list = new List<CursorProcess>();
                        grouped[name] = list;
                    }

                    list.Add(new CursorProcess(pid, parent, handle, ExtractUserDataDir(cmd), IsInstanceCommandLine(cmd)));
                }
                catch
                {
                }
                finally
                {
                    obj.Dispose();
                }
            }
        }
        catch
        {
        }

        return grouped;
    }

    /// <summary>只有主进程才算一个实例。</summary>
    private static bool IsInstanceCommandLine(string commandLine)
    {
        if (commandLine.Contains("--type=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 排除以脚本文件为参数的 worker 进程，例如 ...cursor-always-local\dist\gitWorker.js
        if (commandLine.Contains(".js\"", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains(".js ", StringComparison.OrdinalIgnoreCase)
            || commandLine.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>从命令行里抽出 --user-data-dir 的值；没有则返回 null（表示默认数据目录）。</summary>
    public static string? ExtractUserDataDir(string commandLine)
    {
        const string flag = "--user-data-dir=";
        var index = commandLine.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var rest = commandLine[(index + flag.Length)..].TrimStart();
        if (rest.Length == 0)
        {
            return null;
        }

        if (rest[0] == '"')
        {
            var end = rest.IndexOf('"', 1);
            return end > 1 ? rest[1..end] : null;
        }

        var space = rest.IndexOf(' ');
        return space < 0 ? rest : rest[..space];
    }

    /// <summary>该进程的命令行是否属于指定的数据目录。</summary>
    public static bool BelongsTo(CursorProcess process, string userDataDir, string defaultDataDir)
    {
        if (process.UserDataDir is null)
        {
            return PathsEqual(userDataDir, defaultDataDir);
        }

        return PathsEqual(process.UserDataDir, userDataDir);
    }

    public static bool PathsEqual(string a, string b)
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
