using System.IO;
using System.Windows;
using CursorAccountSwitcher.Services;
using Microsoft.Web.WebView2.Core;

namespace CursorAccountSwitcher;

public partial class MainWindow : Window
{
    private UiBridge? _bridge;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            // 用户数据放在自己的目录，别和 Cursor 的混在一起。
            var userDataFolder = Path.Combine(AppPrefs.RootDir, "webview");
            Directory.CreateDirectory(userDataFolder);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await Browser.EnsureCoreWebView2Async(env);

            var core = Browser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;

            // 只允许加载本地界面，禁止任何外部导航。
            core.NavigationStarting += (_, args) =>
            {
                if (!args.Uri.StartsWith("https://app.local/", StringComparison.OrdinalIgnoreCase))
                {
                    args.Cancel = true;
                }
            };
            core.NewWindowRequested += (_, args) => args.Handled = true;

            _bridge = new UiBridge(this, core);
            core.WebMessageReceived += _bridge.OnWebMessage;

            var uiRoot = Path.Combine(AppContext.BaseDirectory, "assets", "ui");
            core.SetVirtualHostNameToFolderMapping(
                "app.local", uiRoot, CoreWebView2HostResourceAccessKind.DenyCors);

            // 注入 native 桥
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.__native = function (json) { window.chrome.webview.postMessage(json); };");

            core.Navigate("https://app.local/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "界面初始化失败：\n\n" + ex.Message +
                "\n\n如果提示缺少 WebView2 运行时，请安装 Microsoft Edge WebView2 Runtime 后重试。",
                "Cursor 账号切换器", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _bridge?.SavePrefs();
        base.OnClosed(e);
    }
}