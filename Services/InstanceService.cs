using System.Diagnostics;
using System.IO;

namespace CursorAccountSwitcher.Services;

/// <summary>判断某个数据目录对应的 Cursor 实例是否在运行，并负责启动与关闭。</summary>
public static class InstanceService
{
    /// <summary>
    /// 窗口模式。
    ///
    /// Agent：传 --glass，打开 Cursor 的 Agent 面板（对话优先的界面）。
    /// Editor：传 --classic，打开传统编辑器窗口。
    /// </summary>
    public enum WindowMode
    {
        Agent,
        Editor,
    }

    /// <summary>统计使用指定数据目录的主进程数（0 或 1，异常情况下可能更多）。</summary>
    public static int CountProcesses(string userDataDir, string defaultDataDir, string processName = "Cursor.exe") =>
        CursorProcesses.Enumerate(processName)
            .Count(p => p.IsInstance && CursorProcesses.BelongsTo(p, userDataDir, defaultDataDir));

    /// <summary>
    /// 启动某个数据目录对应的实例。openPath 非空时用它打开指定文件夹。
    /// mode 为空时不加 Cursor 的窗口参数。environment 非空时直接启动并把这些变量传给进程。
    /// </summary>
    public static void Launch(
        string exePath,
        string userDataDir,
        string defaultDataDir,
        WindowMode? mode,
        string? openPath = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var isMain = CursorProcesses.PathsEqual(userDataDir, defaultDataDir);
        var fullDataDir = Path.GetFullPath(userDataDir);

        if (environment is null)
        {
            var args = new List<string>();
            if (!isMain)
            {
                args.Add($"--user-data-dir=\"{fullDataDir}\"");
            }

            if (mode is WindowMode windowMode)
            {
                // Agent 模式要显式 --glass，否则可能被上次的窗口偏好带回编辑器界面。
                args.Add(windowMode == WindowMode.Agent ? "--glass" : "--classic");
            }

            if (!string.IsNullOrWhiteSpace(openPath))
            {
                if (mode is not null)
                {
                    args.Add("--new-window");
                }

                args.Add($"\"{openPath}\"");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = string.Join(' ', args),
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
            });
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
        };

        if (!isMain)
        {
            startInfo.ArgumentList.Add($"--user-data-dir={fullDataDir}");
        }

        if (!string.IsNullOrWhiteSpace(openPath))
        {
            startInfo.ArgumentList.Add(openPath);
        }

        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        Process.Start(startInfo);
    }

    /// <summary>
    /// 关闭指定数据目录对应的 Cursor 实例。只关主进程，子进程会随之退出。
    /// 返回关闭的实例数。
    ///
    /// 优先发 WM_CLOSE 让应用自己保存并退出；窗口藏在托盘里（或者对 WM_CLOSE 只隐藏不退出）
    /// 时再强杀整个进程树，避免留下占着数据目录的残留进程。
    /// </summary>
    public static int CloseProcesses(string userDataDir, string defaultDataDir, string processName = "Cursor.exe")
    {
        var tracked = CursorProcesses.Enumerate(processName).ToList();
        var mine = tracked
            .Where(p => p.IsInstance && CursorProcesses.BelongsTo(p, userDataDir, defaultDataDir))
            .ToList();
        var myPids = mine.Select(p => p.Pid).ToHashSet();

        // 语言服务是另一个进程名，父进程属于这个数据目录时一起关掉。
        var serverPids = processName.Equals("Antigravity.exe", StringComparison.OrdinalIgnoreCase)
            ? CursorProcesses.Enumerate("language_server.exe")
                .Where(p => myPids.Contains(p.ParentPid))
                .Select(p => p.Pid)
                .ToList()
            : new List<int>();

        var windows = NativeWindows.Snapshot();
        var killed = 0;
        foreach (var pid in myPids)
        {
            var window = windows.TryGetValue(pid, out var info) ? info : default;

            // 窗口已经藏起来了：WM_CLOSE 只会让它再隐藏一次，直接结束进程。
            if (window.Visible && NativeWindows.TryCloseGracefully(pid, window.Handle, 5000))
            {
                killed++;
                continue;
            }

            NativeWindows.KillTree(pid);
            killed++;
        }

        foreach (var pid in serverPids)
        {
            NativeWindows.KillTree(pid);
        }

        return killed;
    }
}
