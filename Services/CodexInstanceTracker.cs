using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CursorAccountSwitcher.Services;

/// <summary>
/// 记录由切换器启动的 Codex 副实例。
///
/// Codex 桌面版命令行里不带数据目录，没法像 Cursor 那样从参数反查归属，
/// 所以在启动时记下 PID 和进程启动时间（防 PID 复用），之后据此判断是否还在跑。
/// 主账号不记录：没被记录的主进程就是用户自己开的主账号。
/// </summary>
public static class CodexInstanceTracker
{
    private const string ProcessName = "ChatGPT.exe";
    private const string FileName = "codex-instances.json";

    private sealed class Entry
    {
        public int Pid { get; set; }
        public long StartTicks { get; set; }
    }

    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(AppPrefs.RootDir, FileName);

    public static void Record(string profileId, int pid)
    {
        lock (Gate)
        {
            var map = Load();
            map[profileId] = new Entry { Pid = pid, StartTicks = ReadStartTicks(pid) };
            Save(map);
        }
    }

    public static void Forget(string profileId)
    {
        lock (Gate)
        {
            var map = Load();
            if (map.Remove(profileId))
            {
                Save(map);
            }
        }
    }

    /// <summary>该副账号的实例是否还在运行。</summary>
    public static bool IsRunning(string profileId)
    {
        lock (Gate)
        {
            var map = Load();
            return map.TryGetValue(profileId, out var entry) && IsAlive(entry);
        }
    }

    /// <summary>当前已被记录（即属于某个副账号）的主进程 PID。</summary>
    public static HashSet<int> TrackedPids()
    {
        lock (Gate)
        {
            var map = Load();
            var alive = new HashSet<int>();
            var dirty = false;

            foreach (var key in map.Keys.ToList())
            {
                if (IsAlive(map[key]))
                {
                    alive.Add(map[key].Pid);
                }
                else
                {
                    map.Remove(key);
                    dirty = true;
                }
            }

            if (dirty)
            {
                Save(map);
            }

            return alive;
        }
    }

    /// <summary>某副账号占用中的主进程 PID（不在运行时返回空）。</summary>
    public static IReadOnlyList<int> PidsFor(string profileId)
    {
        lock (Gate)
        {
            var map = Load();
            if (!map.TryGetValue(profileId, out var entry) || !IsAlive(entry))
            {
                return Array.Empty<int>();
            }

            return new[] { entry.Pid };
        }
    }

    /// <summary>没有被切换器接管的主进程（即用户自己打开的主账号）。</summary>
    public static IReadOnlyList<int> UntrackedMainPids()
    {
        var tracked = TrackedPids();
        return CursorProcesses.Enumerate(ProcessName)
            .Where(p => p.IsInstance && !tracked.Contains(p.Pid))
            .Select(p => p.Pid)
            .ToList();
    }

    /// <summary>按 PID 找到窗口并切到前台（窗口藏在托盘里也会先显示出来）。</summary>
    public static bool TryActivate(IReadOnlyList<int> pids)
    {
        if (pids.Count == 0)
        {
            return false;
        }

        var windows = NativeWindows.Snapshot();

        foreach (var pid in pids)
        {
            if (windows.TryGetValue(pid, out var window) && NativeWindows.TryShowAndActivate(window.Handle))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAlive(Entry entry)
    {
        if (entry.Pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(entry.Pid);
            if (process.HasExited
                || !string.Equals(process.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return process.StartTime.ToUniversalTime().Ticks == entry.StartTicks;
        }
        catch
        {
            return false;
        }
    }

    private static long ReadStartTicks(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(json) ?? new Dictionary<string, Entry>();
            }
        }
        catch
        {
        }

        return new Dictionary<string, Entry>();
    }

    private static void Save(Dictionary<string, Entry> map)
    {
        try
        {
            Directory.CreateDirectory(AppPrefs.RootDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
