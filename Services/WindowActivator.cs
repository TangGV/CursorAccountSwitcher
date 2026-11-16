using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace CursorAccountSwitcher.Services;

/// <summary>把已经在运行的 Cursor 实例窗口切到前台。</summary>
public static class WindowActivator
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const int SwRestore = 9;

    /// <summary>
    /// 找到属于指定数据目录的 Cursor 窗口并激活。找不到合适窗口时返回 false。
    /// </summary>
    public static bool TryActivate(string userDataDir, string defaultDataDir, string processName = "Cursor.exe")
    {
        foreach (var process in CursorProcesses.Enumerate(processName))
        {
            if (!process.IsInstance || process.MainWindowHandle == IntPtr.Zero)
            {
                continue;
            }

            if (!CursorProcesses.BelongsTo(process, userDataDir, defaultDataDir))
            {
                continue;
            }

            return Activate(process.MainWindowHandle);
        }

        return false;
    }

    /// <summary>
    /// 激活窗口。Windows 对前台窗口有抢占限制，先 AttachThreadInput 提升成功率。
    /// </summary>
    private static bool Activate(IntPtr hWnd)
    {
        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SwRestore);
        }

        if (SetForegroundWindow(hWnd))
        {
            return true;
        }

        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();

        var attached = false;
        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            attached = AttachThreadInput(currentThread, foregroundThread, true);
        }

        try
        {
            BringWindowToTop(hWnd);
            return SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }
}
