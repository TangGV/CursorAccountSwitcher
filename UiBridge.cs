using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using CursorAccountSwitcher.Models;
using CursorAccountSwitcher.Services;
using Microsoft.Web.WebView2.Core;

namespace CursorAccountSwitcher;

/// <summary>
/// WebView2 界面与本地服务之间的桥。前端用 JSON 发请求，这里回 JSON。
/// 所有跨进程/文件系统操作都集中在这里，前端只负责渲染。
/// </summary>
public sealed class UiBridge
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Window _window;
    private readonly CoreWebView2 _core;
    private readonly ProfileStore _store;
    private readonly AppPrefs _prefs;
    private readonly DispatcherTimer _statusTimer;
    // 查进程表要走 WMI，几百毫秒到几秒不等，不能挂在界面线程上。
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public UiBridge(Window window, CoreWebView2 core)
    {
        _window = window;
        _core = core;
        _prefs = AppPrefs.Load();
        _store = ProfileStore.Load();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => _ = PushAsync();
        _statusTimer.Start();

        window.Closed += (_, _) => _statusTimer.Stop();
    }

    private AppKind CurrentApp => AppCatalog.Parse(_prefs.ActiveApp);

    // ------------------------------------------------------------------ 桥

    public void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;
        try
        {
            raw = e.TryGetWebMessageAsString();
        }
        catch
        {
            return;
        }

        _ = HandleAsync(raw);    }

    private async Task HandleAsync(string raw)
    {
        long id = 0;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt64();
            var method = root.GetProperty("method").GetString() ?? "";
            var arg = root.TryGetProperty("arg", out var a) ? a : default;

            var data = await DispatchAsync(method, arg);
            await ReplyAsync(id, true, data, null);
        }
        catch (Exception ex)
        {
            await ReplyAsync(id, false, null, ex.Message);
        }
    }

    private async Task ReplyAsync(long id, bool ok, object? data, string? error)
    {
        var payload = new JsonObject
        {
            ["id"] = id,
            ["ok"] = ok,
        };
        if (ok)
        {
            payload["data"] = data is null ? null : JsonSerializer.SerializeToNode(data, Json);
        }
        else
        {
            payload["error"] = error ?? "未知错误";
        }

        var json = payload.ToJsonString(Json);
        // 参数化注入，避免手动拼接字符串时的转义问题。
        var script = $"window.__resolve({JsonSerializer.Serialize(json)});";
        try
        {
            await _core.ExecuteScriptAsync(script);
        }
        catch
        {
            // 页面已关闭，忽略。
        }
    }

    private async Task<object?> DispatchAsync(string method, JsonElement arg) => method switch
    {
        "getSettings" => GetSettings(),
        "setSettings" => SetSettings(arg),
        "listAccounts" => await ListAccountsAsync(arg),
        "launch" => await LaunchAsync(ArgString(arg, "id")),
        "open" => await OpenWindowAsync(ArgString(arg, "id")),
        "restart" => await RestartAsync(ArgString(arg, "id")),
        "close" => await CloseAsync(ArgString(arg, "id")),
        "openProject" => await OpenProjectAsync(ArgString(arg, "id")),
        "add" => AddAccount(arg),
        "rename" => RenameAccount(arg),
        "remove" => await RemoveAccountAsync(arg),
        "openDir" => OpenDir(ArgString(arg, "id")),
        "copyDir" => CopyDir(ArgString(arg, "id")),
        "openPrefs" => OpenPrefs(),
        _ => throw new InvalidOperationException($"未知方法：{method}"),
    };

    private static string ArgString(JsonElement arg, string name)
    {
        if (arg.ValueKind != JsonValueKind.Object) return "";
        return arg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
    }

    // ------------------------------------------------------------------ 设置

    private object GetSettings()
    {
        var cursorInstall = FindInstall(AppKind.Cursor);
        var antigravityInstall = FindInstall(AppKind.Antigravity);
        var codexInstall = FindInstall(AppKind.Codex);
        return new
        {
            activeApp = AppCatalog.ToKey(CurrentApp),
            appLabel = "账号",
            supportsWindowMode = true,
            supportsSync = true,
            syncSettings = _prefs.SyncSettingsFromMain,
            syncSession = _prefs.SyncSessionFromMain,
            useClassicWindow = _prefs.UseClassicWindow,
            installPath = cursorInstall?.Path,
            installVersion = cursorInstall?.Version ?? "",
            installLabel = cursorInstall is null ? "" : Path.GetFileName(Path.GetDirectoryName(cursorInstall.Value.Path)),
            otherVersions = OtherVersions(AppKind.Cursor, cursorInstall?.Path),
            cursorVersion = cursorInstall?.Version ?? "",
            antigravityPath = antigravityInstall?.Path,
            antigravityVersion = antigravityInstall?.Version ?? "",
            codexPath = codexInstall?.Path,
            codexVersion = codexInstall?.Version ?? "",
        };
    }

    private List<string> OtherVersions(AppKind app, string? chosenPath)
    {
        if (chosenPath is null) return new List<string>();

        IEnumerable<(string Path, string Version)> installs = app switch
        {
            AppKind.Antigravity => AntigravityLocator.CollectInstalls().Select(i => (i.ExePath, i.Version)),
            AppKind.Codex => Array.Empty<(string, string)>(),
            _ => CursorLocator.CollectInstalls().Select(i => (i.ExePath, i.Version)),
        };

        return installs
            .Where(i => !PathsEqual(i.Path, chosenPath))
            .Select(i => i.Version)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct()
            .ToList();
    }

    private object SetSettings(JsonElement arg)
    {
        if (arg.ValueKind == JsonValueKind.Object)
        {
            if (arg.TryGetProperty("syncSettings", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _prefs.SyncSettingsFromMain = a.GetBoolean();
            if (arg.TryGetProperty("syncSession", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _prefs.SyncSessionFromMain = b.GetBoolean();
            if (arg.TryGetProperty("useClassicWindow", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _prefs.UseClassicWindow = c.GetBoolean();
            if (arg.TryGetProperty("activeApp", out var app) && app.ValueKind == JsonValueKind.String)
                _prefs.ActiveApp = AppCatalog.ToKey(AppCatalog.Parse(app.GetString()));
            _prefs.Save();
        }
        return new { ok = true };
    }

    public void SavePrefs() => _prefs.Save();

    // ------------------------------------------------------------------ 账号

    private async Task<object?> ListAccountsAsync(JsonElement arg)
    {
        var deep = arg.ValueKind == JsonValueKind.Object
                   && arg.TryGetProperty("deep", out var d)
                   && d.ValueKind == JsonValueKind.True;

        // 查进程/读数据库都在后台线程做，避免界面卡住。
        // 档案列表先在界面线程拷一份，后台线程只读这份副本。
        var profiles = _store.Profiles.ToList();

        await _refreshGate.WaitAsync();
        try
        {
            return await Task.Run(() => (object?)ListDtos(profiles, deep));
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private List<object> ListDtos(bool deep) => ListDtos(_store.Profiles.ToList(), deep);

    private List<object> ListDtos(IReadOnlyList<AccountProfile> profiles, bool deep)
    {
        // 进程表和窗口表各查一次，账号数量不影响耗时。
        var snapshot = new RuntimeSnapshot();

        return profiles
            .Select((profile, index) => (profile, index))
            .OrderBy(x => x.profile.App switch
            {
                AppKind.Antigravity => 1,
                AppKind.Codex => 2,
                _ => 0,
            })
            .ThenBy(x => x.profile.IsMain ? 0 : 1)
            .ThenBy(x => x.index)
            .Select(x => ToDto(x.profile, snapshot, deep))
            .ToList();
    }

    /// <summary>定时把状态推给前端（保持卡片上的运行状态实时）。</summary>
    private async Task PushAsync()
    {
        try
        {
            if (!await _refreshGate.WaitAsync(0))
            {
                // 上一次刷新还没完成，这一拍跳过。
                return;
            }

            List<object> items;
            var profiles = _store.Profiles.ToList();
            try
            {
                items = await Task.Run(() => ListDtos(profiles, deep: false));
            }
            finally
            {
                _refreshGate.Release();
            }

            var json = JsonSerializer.Serialize(items, Json);
            var script = $"window.__updateList && window.__updateList({JsonSerializer.Serialize(json)});";
            await _core.ExecuteScriptAsync(script);
        }
        catch
        {
            // 页面未就绪或已关闭时忽略。
        }
    }

    private object ToDto(AccountProfile p, RuntimeSnapshot snapshot, bool deep)
    {
        var pids = snapshot.PidsOf(p);
        var isRunning = pids.Count > 0;
        // 进程在但窗口不可见：用户关掉的是窗口，应用还挂在托盘里。
        var isHidden = isRunning && !snapshot.HasVisibleWindow(pids);

        if (deep)
        {
            if (p.App == AppKind.Antigravity)
            {
                p.CachedEmail = AntigravityLoginReader.ReadUsername(p.DataDir);
                p.Membership = null;
            }
            else if (p.App == AppKind.Codex)
            {
                var info = CodexLoginReader.Read(p.DataDir);
                p.CachedEmail = info.Email;
                p.Membership = info.Plan;
            }
            else
            {
                var info = StateDbReader.Read(p.DataDir);
                p.CachedEmail = info?.Email;
                p.Membership = info?.Membership;
            }
        }

        return new
        {
            id = p.Id,
            name = p.Name,
            app = AppCatalog.ToKey(p.App),
            dataDir = p.DataDir,
            isMain = p.IsMain,
            isRunning,
            isHidden,
            hasLogin = p.HasLogin,
            displayEmail = p.DisplayEmail,
            membershipText = p.MembershipText,
            statusText = StatusText(p, isRunning, isHidden),
            dataDirExists = SafeExists(p.DataDir),
        };
    }

    private static string StatusText(AccountProfile p, bool isRunning, bool isHidden)
    {
        if (isHidden)
        {
            return p.HasLogin ? "已在后台 · 窗口已关闭" : "后台运行 · 未登录";
        }

        if (isRunning)
        {
            return p.HasLogin ? "运行中" : "运行中 · 未登录";
        }

        return p.HasLogin ? "已登录 · 未启动" : "未登录";
    }

    private static bool SafeExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private AccountProfile FindProfile(string id)
    {
        var profile = _store.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null)
        {
            throw new InvalidOperationException("找不到这个账号，可能已被移除。");
        }
        return profile;
    }

    // ------------------------------------------------------------------ 启动 / 关闭

    private async Task<object?> LaunchAsync(string id)
    {
        var profile = FindProfile(id);

        var exe = RequireExe(profile.App);

        if (profile.App == AppKind.Codex)
        {
            return await LaunchCodexAsync(profile, exe);
        }

        var runningMessage = await TryShowRunningAsync(profile);
        if (runningMessage is not null)
        {
            return new { message = runningMessage };
        }

        return await StartInstanceAsync(profile, exe);
    }

    /// <summary>
    /// 已经在跑就不再开新窗口，而是把现有窗口显示出来并切到前台。
    /// 用户点窗口的关闭按钮只是把窗口藏起来（进程还在托盘），这时也能恢复。
    /// 返回 null 表示没在运行，可以真正启动。
    /// </summary>
    private static async Task<string?> TryShowRunningAsync(AccountProfile profile) =>
        await Task.Run(() =>
        {
            var snapshot = new RuntimeSnapshot();
            var pids = snapshot.PidsOf(profile);
            if (pids.Count == 0)
            {
                return null;
            }

            if (snapshot.TryShowAny(pids))
            {
                return $"「{profile.Name}」已经在运行，已切换到它的窗口。";
            }

            return $"「{profile.Name}」的进程还在后台运行，但没找到窗口。可以先关掉它再重新启动。";
        }).ConfigureAwait(true);

    /// <summary>真正启动一个新实例。调用方已经确认它没在运行。</summary>
    private async Task<object?> StartInstanceAsync(AccountProfile profile, string exe)
    {
        if (profile.App == AppKind.Codex)
        {
            return await LaunchCodexAsync(profile, exe);
        }

        PrepareDataDir(profile);

        try
        {
            InstanceService.Launch(
                exe,
                profile.DataDir,
                DefaultDir(profile),
                WindowModeFor(profile),
                environment: AppCatalog.IsolatedEnvironment(profile));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("启动失败：" + ex.Message);
        }

        await Task.Delay(2200);
        return new { message = $"正在启动「{profile.Name}」…" };
    }

    /// <summary>
    /// 启动 Codex 副实例。商店版会忽略 --user-data-dir，窗口状态仍共享，
    /// 但 CODEX_HOME 生效：登录、会话、配置都落在该账号自己的目录里。
    /// </summary>
    private async Task<object?> LaunchCodexAsync(AccountProfile profile, string exe)
    {
        // 查进程表要走 WMI，放到后台线程，别卡住界面。
        var existing = await Task.Run<IReadOnlyList<int>>(() => profile.IsMain
            ? CodexInstanceTracker.UntrackedMainPids()
            : CodexInstanceTracker.PidsFor(profile.Id)).ConfigureAwait(true);

        if (existing.Count > 0)
        {
            // 进程还在（窗口可能已被关到托盘），只做恢复显示，不开新窗口。
            var shown = await Task.Run(() => CodexInstanceTracker.TryActivate(existing)).ConfigureAwait(true);

            return new
            {
                message = shown
                    ? $"「{profile.Name}」已经在运行，已切换到它的窗口。"
                    : $"「{profile.Name}」的进程还在后台运行，但没找到窗口。可以先关掉它再重新启动。",
            };
        }

        PrepareDataDir(profile);

        var before = await Task.Run(() => CursorProcesses.Enumerate(AppCatalog.ProcessName(AppKind.Codex))
            .Where(p => p.IsInstance)
            .Select(p => p.Pid)
            .ToHashSet()).ConfigureAwait(true);

        try
        {
            InstanceService.Launch(
                exe,
                profile.DataDir,
                DefaultDir(profile),
                mode: null,
                environment: profile.IsMain ? null : CodexEnvironment(profile));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("启动失败：" + ex.Message);
        }

        if (!profile.IsMain)
        {
            var started = await WaitForNewCodexMainAsync(before);
            if (started is int pid)
            {
                CodexInstanceTracker.Record(profile.Id, pid);
            }
        }

        await Task.Delay(1200);
        return new { message = $"正在启动「{profile.Name}」…" };
    }

    /// <summary>
    /// 等新的 Codex 主进程出现，并把它记成这个副账号的实例。
    /// 命令行里没有数据目录可核对，所以启动时先记下已有 PID，只认新出现的那个。
    /// </summary>
    private static async Task<int?> WaitForNewCodexMainAsync(HashSet<int> before)
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(400);

            var started = await Task.Run(() => CursorProcesses.Enumerate(AppCatalog.ProcessName(AppKind.Codex))
                .Where(p => p.IsInstance && !before.Contains(p.Pid))
                .Select(p => p.Pid)
                .FirstOrDefault()).ConfigureAwait(true);

            if (started > 0)
            {
                return started;
            }
        }

        return null;
    }

    /// <summary>Codex 副账号的进程环境：只换 CODEX_HOME。</summary>
    private static IReadOnlyDictionary<string, string> CodexEnvironment(AccountProfile profile) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CODEX_HOME"] = Path.GetFullPath(profile.DataDir),
        };

    /// <summary>启动前准备：确保目录存在，按偏好同步设置与会话。</summary>
    private void PrepareDataDir(AccountProfile profile)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(profile.HomeDir))
            {
                Directory.CreateDirectory(profile.HomeDir);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("创建数据目录失败：" + ex.Message);
        }

        if (profile.IsMain) return;

        try
        {
            Directory.CreateDirectory(profile.DataDir);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("创建数据目录失败：" + ex.Message);
        }

        if (profile.App != AppKind.Cursor)
        {
            return;
        }

        if (_prefs.SyncSettingsFromMain)
        {
            var result = SettingsSync.SyncFromMain(DefaultDir(profile), profile.DataDir, profile.SyncedHashes);
            if (result.Synced.Count > 0 || result.Skipped.Count > 0)
            {
                _store.Save();
            }
        }

        if (_prefs.SyncSessionFromMain)
        {
            SessionSync.SyncFromMain(DefaultDir(profile), profile.DataDir);
        }
    }

    /// <summary>
    /// 「打开」：只把已有窗口显示出来并切到前台，不启动新进程。
    /// 进程不在运行时给出提示，由用户决定用「重启」拉起来。
    /// </summary>
    private async Task<object?> OpenWindowAsync(string id)
    {
        var profile = FindProfile(id);

        // 枚举进程和窗口都要走系统 API，放到后台线程，别卡住界面。
        var result = await Task.Run(() =>
        {
            var snapshot = new RuntimeSnapshot();
            var pids = snapshot.PidsOf(profile);

            if (pids.Count == 0)
            {
                return $"「{profile.Name}」没有在运行，用「重启」或「启动」把它拉起来。";
            }

            if (!snapshot.TryShowAny(pids))
            {
                return $"没找到「{profile.Name}」的窗口，它可能在后台运行。用「重启」重新打开。";
            }

            return "";
        });

        if (result.Length > 0)
        {
            throw new InvalidOperationException(result);
        }

        return new { message = $"已切到「{profile.Name}」的窗口" };
    }

    /// <summary>「重启」：先关掉现有实例，再按当前偏好重新启动。</summary>
    private async Task<object?> RestartAsync(string id)
    {
        var profile = FindProfile(id);
        var exe = RequireExe(profile.App);

        if (await CloseInstanceAsync(profile))
        {
            // 等进程真正退干净，否则新进程会把请求转给旧实例然后就退出。
            await WaitUntilStoppedAsync(profile);
        }

        return await StartInstanceAsync(profile, exe);
    }

    /// <summary>等进程真正消失（关闭/重启后进程收尾需要一点时间）。</summary>
    private static async Task WaitUntilStoppedAsync(AccountProfile profile)
    {
        for (var i = 0; i < 15; i++)
        {
            var stillRunning = await Task.Run(() => new RuntimeSnapshot().IsRunning(profile))
                .ConfigureAwait(true);

            if (!stillRunning)
            {
                return;
            }

            await Task.Delay(400);
        }
    }

    private async Task<object?> CloseAsync(string id)
    {
        var profile = FindProfile(id);

        if (!await CloseInstanceAsync(profile))
        {
            throw new InvalidOperationException($"没有找到「{profile.Name}」的运行实例。");
        }

        // 从点关闭到进程真正消失有一段收尾时间，等它落定再回前端，
        // 否则卡片要等定时刷新才变，看起来像「已经关了还显示运行中」。
        await WaitUntilStoppedAsync(profile);
        return new { message = $"已关闭「{profile.Name}」" };
    }

    /// <summary>
    /// 结束某个账号的实例。只发 WM_CLOSE 不够：Electron 应用关窗口多半只是隐藏到托盘，
    /// 进程会一直挂着，卡片就永远显示「运行中」。所以先礼后刀，进程没退就强杀整棵进程树。
    /// 返回是否真的找到了实例。
    /// </summary>
    private async Task<bool> CloseInstanceAsync(AccountProfile profile) =>
        await Task.Run(() =>
        {
            var snapshot = new RuntimeSnapshot();
            var pids = snapshot.PidsOf(profile);
            if (pids.Count == 0)
            {
                return false;
            }

            if (profile.App == AppKind.Codex)
            {
                foreach (var pid in pids)
                {
                    var window = snapshot.TryGetWindow(pid, out var info) ? info : default;

                    // 窗口已经藏起来了：WM_CLOSE 只会让它再隐藏一次，直接结束进程。
                    if (window.Visible && NativeWindows.TryCloseGracefully(pid, window.Handle, 4000))
                    {
                        continue;
                    }

                    NativeWindows.KillTree(pid);
                }

                if (!profile.IsMain)
                {
                    CodexInstanceTracker.Forget(profile.Id);
                }

                return true;
            }

            InstanceService.CloseProcesses(profile.DataDir, DefaultDir(profile), AppCatalog.ProcessName(profile.App));
            return true;
        }).ConfigureAwait(true);

    private Task<object?> OpenProjectAsync(string id)
    {
        var profile = FindProfile(id);

        var exe = RequireExe(profile.App);

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"选择要用「{profile.Name}」打开的项目文件夹",
        };

        if (dialog.ShowDialog(_window) != true)
        {
            return Task.FromResult<object?>(new { message = "" });
        }

        PrepareDataDir(profile);
        InstanceService.Launch(
            exe,
            profile.DataDir,
            DefaultDir(profile),
            WindowModeFor(profile),
            dialog.FolderName,
            profile.App == AppKind.Codex && !profile.IsMain
                ? CodexEnvironment(profile)
                : AppCatalog.IsolatedEnvironment(profile));
        return Task.FromResult<object?>(new { message = "已发出打开请求" });
    }

    /// <summary>
    /// 当前偏好的窗口模式。Codex 没有这些窗口参数，返回 null。
    /// </summary>
    private InstanceService.WindowMode? WindowModeFor(AccountProfile profile) => profile.App switch
    {
        AppKind.Cursor => _prefs.UseClassicWindow
            ? InstanceService.WindowMode.Editor
            : InstanceService.WindowMode.Agent,
        _ => null,
    };

    // ------------------------------------------------------------------ 增删改

    private object AddAccount(JsonElement arg)
    {
        var name = ArgString(arg, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("名字不能为空。");
        }

        var requested = ArgString(arg, "app");
        var app = string.IsNullOrWhiteSpace(requested) ? CurrentApp : AppCatalog.Parse(requested);
        var profile = _store.Add(name.Trim(), app);
        return new { id = profile.Id, name = profile.Name };
    }

    private object RenameAccount(JsonElement arg)
    {
        var profile = FindProfile(ArgString(arg, "id"));
        var name = ArgString(arg, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("名字不能为空。");
        }

        profile.Name = name.Trim();
        _store.Save();
        return new { ok = true };
    }

    private async Task<object?> RemoveAccountAsync(JsonElement arg)
    {
        var profile = FindProfile(ArgString(arg, "id"));

        if (profile.IsMain)
        {
            throw new InvalidOperationException("主账号不能移除。");
        }

        if (await Task.Run(() => new RuntimeSnapshot().IsRunning(profile)))
        {
            throw new InvalidOperationException("这个账号正在运行，请先关闭它再移除。");
        }

        var deleteData = arg.ValueKind == JsonValueKind.Object
                         && arg.TryGetProperty("deleteData", out var d)
                         && d.ValueKind == JsonValueKind.True;

        var dataPath = AppCatalog.SandboxRoot(profile) ?? profile.DataDir;
        if (deleteData && SafeExists(dataPath))
        {
            try
            {
                Directory.Delete(dataPath, recursive: true);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("删除数据目录失败：" + ex.Message);
            }
        }

        _store.Remove(profile);
        return new { ok = true };
    }

    // ------------------------------------------------------------------ 杂项

    private object OpenDir(string id)
    {
        var profile = FindProfile(id);
        OpenPath(AppCatalog.BrowseDir(profile));
        return new { ok = true };
    }

    private object CopyDir(string id)
    {
        var profile = FindProfile(id);
        Clipboard.SetText(AppCatalog.BrowseDir(profile));
        return new { ok = true };
    }

    private object OpenPrefs()
    {
        OpenPath(AppPrefs.RootDir);
        return new { ok = true };
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("打开失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------ 安装定位

    private static string DefaultDir(AccountProfile profile) => AppCatalog.DefaultDataDir(profile.App);

    private string RequireExe(AppKind app)
    {
        var install = FindInstall(app);
        if (install is null)
        {
            throw new InvalidOperationException($"没找到 {AppCatalog.Label(app)} 的安装位置。");
        }

        return install.Value.Path;
    }

    private readonly record struct LocatedInstall(string Path, string Version);

    private static LocatedInstall? FindInstall(AppKind app)
    {
        switch (app)
        {
            case AppKind.Antigravity:
            {
                var found = AntigravityLocator.FindInstall();
                return found is null ? null : new LocatedInstall(found.ExePath, found.Version);
            }
            case AppKind.Codex:
            {
                var found = CodexLocator.FindInstall();
                return found is null ? null : new LocatedInstall(found.ExePath, found.Version);
            }
            default:
            {
                var cursor = CursorLocator.FindInstall();
                return cursor is null ? null : new LocatedInstall(cursor.ExePath, cursor.Version);
            }
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
