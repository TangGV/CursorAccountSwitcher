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

    public static IEnumerable<CursorProcess> Enumerate(string processName = "Cursor.exe")
    {
        var result = new List<CursorProcess>();
        try
        {
            var safeName = processName.Replace("'", "");
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, CommandLine FROM Win32_Process WHERE Name = '" + safeName + "'");

            foreach (var obj in searcher.Get())
            {
                try
                {
                    var pid = Convert.ToInt32(obj["ProcessId"] ?? 0);
                    var parent = Convert.ToInt32(obj["ParentProcessId"] ?? 0);
                    var cmd = obj["CommandLine"] as string ?? string.Empty;
                    var isInstance = IsInstanceCommandLine(cmd);

                    IntPtr handle = IntPtr.Zero;
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        handle = process.MainWindowHandle;
                    }
                    catch
                    {
                    }

                    result.Add(new CursorProcess(pid, parent, handle, ExtractUserDataDir(cmd), isInstance));
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

        return result;
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
