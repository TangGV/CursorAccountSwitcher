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

    /// <summary>统计使用指定数据目录的 Cursor 主进程数（0 或 1，异常情况下可能更多）。</summary>
    public static int CountProcesses(string userDataDir, string defaultDataDir) =>
        CursorProcesses.Enumerate()
            .Count(p => p.IsInstance && CursorProcesses.BelongsTo(p, userDataDir, defaultDataDir));

    /// <summary>启动某个数据目录对应的 Cursor 实例。openPath 非空时用它打开指定文件夹。</summary>
    public static void Launch(
        string exePath,
        string userDataDir,
        string defaultDataDir,
        WindowMode mode,
        string? openPath = null)
    {
        var isMain = CursorProcesses.PathsEqual(userDataDir, defaultDataDir);

        var args = new List<string>();
        if (!isMain)
        {
            args.Add($"--user-data-dir=\"{Path.GetFullPath(userDataDir)}\"");
        }

        // Agent 模式要显式 --glass，否则可能被上次的窗口偏好带回编辑器界面。
        args.Add(mode == WindowMode.Agent ? "--glass" : "--classic");

        if (!string.IsNullOrWhiteSpace(openPath))
        {
            args.Add("--new-window");
            args.Add($"\"{openPath}\"");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = string.Join(' ', args),
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
        };

        Process.Start(startInfo);
    }

    /// <summary>
    /// 关闭指定数据目录对应的 Cursor 实例。只关主进程，子进程会随之退出。
    /// 返回关闭的实例数。
    /// </summary>
    public static int CloseProcesses(string userDataDir, string defaultDataDir)
    {
        var killed = 0;
        foreach (var process in CursorProcesses.Enumerate()
                     .Where(p => p.IsInstance && CursorProcesses.BelongsTo(p, userDataDir, defaultDataDir)))
        {
            try
            {
                using var proc = Process.GetProcessById(process.Pid);
                if (proc.CloseMainWindow() && proc.WaitForExit(5000))
                {
                    killed++;
                    continue;
                }

                proc.Kill(entireProcessTree: true);
                killed++;
            }
            catch
            {
            }
        }

        return killed;
    }
}
