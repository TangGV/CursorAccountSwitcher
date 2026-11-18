using System.Runtime.InteropServices;
using System.Text;

namespace CursorAccountSwitcher.Services;

/// <summary>
/// 按 PID 枚举顶层窗口。
///
/// Electron 应用（Cursor / Antigravity / Codex）的主窗口类名都是 Chrome_WidgetWin_1。
/// 窗口隐藏到托盘、被系统挂起或最小化时 Process.MainWindowHandle 会返回 0，
/// 只看它就会把「窗口还在、只是隐藏了」当成没有窗口。这里直接枚举，隐藏窗口也能拿到句柄。
/// </summary>
public static class NativeWindows
{
    public readonly record struct WindowInfo(IntPtr Handle, bool Visible, string Title);

    private const string MainWindowClass = "Chrome_WidgetWin_1";

    private const uint GwOwner = 4;      // GW_OWNER
    private const uint WmClose = 0x0010; // WM_CLOSE
    private const int SwShow = 5;        // SW_SHOW
    private const int SwRestore = 9;     // SW_RESTORE

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 一次枚举，返回 PID 到主窗口的映射。
    /// 同一进程有多个顶层窗口时，取最像主窗口的那个（可见、有标题优先）。
    /// </summary>
    public static Dictionary<int, WindowInfo> Snapshot()
    {
        var result = new Dictionary<int, WindowInfo>();

        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsMainWindowCandidate(hWnd))
                {
                    return true;
                }

                GetWindowThreadProcessId(hWnd, out var pid);
                if (pid == 0 || pid > int.MaxValue)
                {
                    return true;
                }

                var info = new WindowInfo(hWnd, IsWindowVisible(hWnd), ReadTitle(hWnd));
                var key = (int)pid;

                if (!result.TryGetValue(key, out var existing) || Rank(info) > Rank(existing))
                {
                    result[key] = info;
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败按「没有窗口」处理，调用方会显示成后台运行或未运行。
        }

        return result;
    }

    /// <summary>
    /// 把窗口显示出来（隐藏或最小化都能恢复）并尽力切到前台。
    /// 只要窗口真的存在且已经显示，就返回 true；前台切换失败不算失败，
    /// 否则会被误报成「没找到窗口」。
    /// </summary>
    public static bool TryShowAndActivate(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
        {
            return false;
        }

        try
        {
            if (IsIconic(hWnd))
            {
                ShowWindow(hWnd, SwRestore);
            }
            else if (!IsWindowVisible(hWnd))
            {
                ShowWindow(hWnd, SwShow);
            }
        }
        catch
        {
            return false;
        }

        // 抢不到前台（Windows 的前台锁定）时窗口也已经显示出来了，用户从任务栏就能点。
        WindowActivator.ActivateWindow(hWnd);
        return true;
    }

    public static bool TryShowAndActivate(int pid) =>
        Snapshot().TryGetValue(pid, out var window) && TryShowAndActivate(window.Handle);

    /// <summary>发 WM_CLOSE，让应用自己走退出流程（有未保存内容时还能弹提示）。</summary>
    public static void TryClose(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
        {
            return;
        }

        try
        {
            PostMessage(hWnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 先发 WM_CLOSE 等它自己退出。返回 true 表示进程已经没了。
    /// 窗口隐藏到托盘的应用收到 WM_CLOSE 只隐藏不退出，这时候会返回 false，
    /// 由调用方决定要不要强杀。
    /// </summary>
    public static bool TryCloseGracefully(int pid, IntPtr hWnd, int waitMs)
    {
        TryClose(hWnd);

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.HasExited || process.WaitForExit(waitMs);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>连同子进程一起结束。</summary>
    public static void KillTree(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static bool IsMainWindowCandidate(IntPtr hWnd)
    {
        var cls = new StringBuilder(64);
        if (GetClassName(hWnd, cls, cls.Capacity) == 0
            || !cls.ToString().Equals(MainWindowClass, StringComparison.Ordinal))
        {
            return false;
        }

        // 带 owner 的是工具窗或对话框，不是主窗口。
        return GetWindow(hWnd, GwOwner) == IntPtr.Zero;
    }

    /// <summary>可见、有标题的窗口更像主窗口。</summary>
    private static int Rank(in WindowInfo window) =>
        (window.Visible ? 4 : 0) + (window.Title.Length > 0 ? 2 : 0);

    private static string ReadTitle(IntPtr hWnd)
    {
        var text = new StringBuilder(256);
        GetWindowText(hWnd, text, text.Capacity);
        return text.ToString();
    }
}
