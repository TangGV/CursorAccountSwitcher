using System.Windows;

namespace CursorAccountSwitcher;

public partial class App : Application
{
    // 用 Local\ 而不是 Global\：多用户 / 远程会话各开一份互不影响，
    // Global\ 会让同一台机器上第二个用户完全打不开。
    private const string MutexName = @"Local\CursorAccountSwitcher.SingleInstance";

    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Cursor 账号切换器已经在运行了。", "Cursor 账号切换器",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
