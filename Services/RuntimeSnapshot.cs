using CursorAccountSwitcher.Models;

namespace CursorAccountSwitcher.Services;

/// <summary>
/// 一次渲染/操作里复用的运行状态快照。
///
/// 进程表和窗口表各枚举一次，账号数量不影响耗时。
///
/// 三个应用的归属判断现在统一按命令行里的 --user-data-dir 走：
/// Codex 商店版的主进程命令行同样带这个参数（实测 26.1111+），
/// 所以不再需要「启动时记 PID」那套猜测，异常退出也不会留下假记录。
/// </summary>
public sealed class RuntimeSnapshot
{
    private readonly Dictionary<string, List<CursorProcesses.CursorProcess>> _processes;
    private readonly Dictionary<int, NativeWindows.WindowInfo> _windows;
    private readonly Lazy<HashSet<int>> _mainWindowPids;

    public RuntimeSnapshot()
    {
        // 一次 WMI 查询把所有应用的进程都取回来，避免每个应用各付一次固定开销。
        _processes = CursorProcesses.EnumerateMany(ProcessNames);
        _windows = NativeWindows.Snapshot();
        _mainWindowPids = new Lazy<HashSet<int>>(() =>
            _windows.Where(w => w.Value.Visible).Select(w => w.Key).ToHashSet());
    }

    /// <summary>需要查询的进程名。language_server 单独列出来，Antigravity 关闭时一起处理。</summary>
    private static readonly string[] ProcessNames =
    {
        AppCatalog.ProcessName(AppKind.Cursor),
        AppCatalog.ProcessName(AppKind.Antigravity),
        AppCatalog.ProcessName(AppKind.Codex),
        "language_server.exe",
    };

    private List<CursorProcesses.CursorProcess> Processes(AppKind app) =>
        _processes.TryGetValue(AppCatalog.ProcessName(app), out var list)
            ? list
            : new List<CursorProcesses.CursorProcess>();

    /// <summary>该账号当前占用的主进程 PID。</summary>
    public IReadOnlyList<int> PidsOf(AccountProfile profile)
    {
        var defaultDir = AppCatalog.DefaultDataDir(profile.App);

        return Processes(profile.App)
            .Where(p => p.IsInstance && CursorProcesses.BelongsTo(p, profile.DataDir, defaultDir))
            .Select(p => p.Pid)
            .ToList();
    }

    public bool IsRunning(AccountProfile profile) => PidsOf(profile).Count > 0;

    /// <summary>Antigravity 数据目录下的 language_server 进程（主进程退出后可能残留）。</summary>
    public IReadOnlyList<int> LanguageServerPids(IReadOnlyList<int> ownerPids)
    {
        if (ownerPids.Count == 0 || !_processes.TryGetValue("language_server.exe", out var servers))
        {
            return Array.Empty<int>();
        }

        var owners = ownerPids.ToHashSet();
        return servers.Where(s => owners.Contains(s.ParentPid)).Select(s => s.Pid).ToList();
    }

    /// <summary>进程还在，并且主窗口是显示状态（最小化也算显示）。</summary>
    public bool HasVisibleWindow(IReadOnlyList<int> pids) => pids.Any(_mainWindowPids.Value.Contains);

    /// <summary>把这些进程里能找到的窗口显示出来并尽力切到前台。</summary>
    public bool TryShowAny(IReadOnlyList<int> pids)
    {
        var shown = false;

        foreach (var pid in pids)
        {
            // 一个账号理论上只有一个主进程，但异常情况下可能有多个，全都恢复。
            if (_windows.TryGetValue(pid, out var window) && NativeWindows.TryShowAndActivate(window.Handle))
            {
                shown = true;
            }
        }

        return shown;
    }

    /// <summary>该进程的主窗口（可能处于隐藏状态）。</summary>
    public bool TryGetWindow(int pid, out NativeWindows.WindowInfo window) =>
        _windows.TryGetValue(pid, out window);
}
