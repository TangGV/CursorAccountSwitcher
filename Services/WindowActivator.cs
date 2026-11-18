using System.Runtime.InteropServices;

namespace CursorAccountSwitcher.Services;

/// <summary>
/// Win32 窗口前置。
///
/// 找窗口的逻辑在 <see cref="NativeWindows"/>：那里用 EnumWindows 枚举，
/// 隐藏到托盘的窗口也能拿到句柄。这里只负责「已知句柄，把它弄到用户眼前」。
/// </summary>
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

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private const int SwRestore = 9;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>
    /// 激活窗口。Windows 对前台窗口有抢占限制，先 AttachThreadInput 提升成功率。
    /// </summary>
    public static bool ActivateWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

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
            if (SetForegroundWindow(hWnd))
            {
                return true;
            }

            // 仍被前台锁定挡住：短暂置顶再取消。带 SWP_NOACTIVATE 只改变 Z 序，
            // 所以置顶后还要再试一次 SetForegroundWindow 拿焦点。
            SetWindowPos(hWnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            SetWindowPos(hWnd, HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
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
