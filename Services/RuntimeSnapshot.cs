using CursorAccountSwitcher.Models;

namespace CursorAccountSwitcher.Services;

/// <summary>
/// 一次渲染/操作里复用的运行状态快照。
///
/// 之前每个账号都单独查一遍 WMI 进程表，账号一多每次刷新要好几秒；
/// 现在进程表和窗口表各枚举一次，账号数量不影响耗时。
/// 另外窗口句柄直接来自 EnumWindows，隐藏到托盘的窗口也能找到并重新显示出来。
/// </summary>
public sealed class RuntimeSnapshot
{
    private readonly Dictionary<string, List<CursorProcesses.CursorProcess>> _processes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<int, NativeWindows.WindowInfo> _windows;
    private readonly HashSet<int> _trackedCodexPids;

    public RuntimeSnapshot()
    {
        _windows = NativeWindows.Snapshot();
        _trackedCodexPids = CodexInstanceTracker.TrackedPids();
    }

    private List<CursorProcesses.CursorProcess> Processes(string name)
    {
        if (!_processes.TryGetValue(name, out var list))
        {
            list = CursorProcesses.Enumerate(name).ToList();
            _processes[name] = list;
        }

        return list;
    }

    /// <summary>该账号当前占用的主进程 PID。Codex 主账号是「没有被记录的主进程」。</summary>
    public IReadOnlyList<int> PidsOf(AccountProfile profile) => profile.App switch
    {
        AppKind.Codex => profile.IsMain
            ? Processes(AppCatalog.ProcessName(AppKind.Codex))
                .Where(p => p.IsInstance && !_trackedCodexPids.Contains(p.Pid))
                .Select(p => p.Pid)
                .ToList()
            : CodexInstanceTracker.PidsFor(profile.Id),
        _ => Processes(AppCatalog.ProcessName(profile.App))
            .Where(p => p.IsInstance
                        && CursorProcesses.BelongsTo(p, profile.DataDir, AppCatalog.DefaultDataDir(profile.App)))
            .Select(p => p.Pid)
            .ToList(),
    };

    public bool IsRunning(AccountProfile profile) => PidsOf(profile).Count > 0;

    /// <summary>进程还在，并且主窗口是显示状态（最小化也算显示）。</summary>
    public bool HasVisibleWindow(IReadOnlyList<int> pids) =>
        pids.Any(pid => _windows.TryGetValue(pid, out var window) && window.Visible);

    /// <summary>把这些进程里能找到的窗口显示出来并切到前台。</summary>
    public bool TryShowAny(IReadOnlyList<int> pids)
    {
        foreach (var pid in pids)
        {
            if (_windows.TryGetValue(pid, out var window) && NativeWindows.TryShowAndActivate(window.Handle))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>该进程的主窗口（可能处于隐藏状态）。</summary>
    public bool TryGetWindow(int pid, out NativeWindows.WindowInfo window) =>
        _windows.TryGetValue(pid, out window);
}
