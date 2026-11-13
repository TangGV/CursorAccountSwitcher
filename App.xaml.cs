using System.Windows;

namespace CursorAccountSwitcher;

public partial class App : Application
{
    private const string MutexName = @"Global\CursorAccountSwitcher.SingleInstance";

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
