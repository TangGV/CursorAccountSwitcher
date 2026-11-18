using System.Diagnostics;
using System.IO;

namespace CursorAccountSwitcher.Services;

/// <summary>启动实例。关闭逻辑集中在 UiBridge，按「显示 → WM_CLOSE → 强杀」的顺序处理。</summary>
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

    /// <summary>
    /// 启动某个数据目录对应的实例。openPath 非空时用它打开指定文件夹。
    /// mode 为空时不加 Cursor 的窗口参数。environment 非空时直接启动并把这些变量传给进程。
    ///
    /// 副账号始终带 --user-data-dir：这是归属判断的依据，Codex 商店版同样认这个参数。
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
}
