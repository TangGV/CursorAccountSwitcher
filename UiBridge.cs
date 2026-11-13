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

    private string? _exePath;
    private CursorLocator.CursorInstall? _install;

    public UiBridge(Window window, CoreWebView2 core)
    {
        _window = window;
        _core = core;
        _prefs = AppPrefs.Load();
        _store = ProfileStore.Load();

        ResolveInstall();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => _ = PushAsync();
        _statusTimer.Start();

        window.Closed += (_, _) => _statusTimer.Stop();
    }

    private static string DefaultDataDir => ProfileStore.DefaultDataDir;

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
        "activate" => Activate(ArgString(arg, "id")),
        "close" => await CloseAsync(ArgString(arg, "id")),
        "openProject" => await OpenProjectAsync(ArgString(arg, "id")),
        "add" => AddAccount(arg),
        "rename" => RenameAccount(arg),
        "remove" => RemoveAccount(arg),
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

    private object GetSettings() => new
    {
        syncSettings = _prefs.SyncSettingsFromMain,
        syncSession = _prefs.SyncSessionFromMain,
        useClassicWindow = _prefs.UseClassicWindow,
        installPath = _exePath,
        installVersion = _install?.Version ?? "",
        installLabel = _exePath is null ? "" : Path.GetFileName(Path.GetDirectoryName(_exePath)),
        otherVersions = OtherVersions(),
    };

    private List<string> OtherVersions()
    {
        if (_install is null) return new List<string>();
        return CursorLocator.CollectInstalls()
            .Where(i => !PathsEqual(i.ExePath, _install.ExePath))
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
            _prefs.Save();
        }
        return new { ok = true };
    }

    public void SavePrefs() => _prefs.Save();

    // ------------------------------------------------------------------ 账号

    private Task<object?> ListAccountsAsync(JsonElement arg)
    {
        var deep = arg.ValueKind == JsonValueKind.Object
                   && arg.TryGetProperty("deep", out var d)
                   && d.ValueKind == JsonValueKind.True;

        var items = _store.Profiles.Select(p => ToDto(p, deep)).ToList();
        return Task.FromResult<object?>(items);
    }

    /// <summary>定时把状态推给前端（保持卡片上的运行状态实时）。</summary>
    private async Task PushAsync()
    {
        try
        {
            var items = _store.Profiles.Select(p => ToDto(p, deep: false)).ToList();
            var json = JsonSerializer.Serialize(items, Json);
            var script = $"window.__updateList && window.__updateList({JsonSerializer.Serialize(json)});";
            await _core.ExecuteScriptAsync(script);
        }
        catch
        {
            // 页面未就绪或已关闭时忽略。
        }
    }

    private object ToDto(AccountProfile p, bool deep)
    {
        var count = InstanceService.CountProcesses(p.DataDir, DefaultDataDir);
        var isRunning = count > 0;

        if (deep)
        {
            var info = StateDbReader.Read(p.DataDir);
            p.CachedEmail = info?.Email;
            p.Membership = info?.Membership;
        }

        return new
        {
            id = p.Id,
            name = p.Name,
            dataDir = p.DataDir,
            isMain = p.IsMain,
            isRunning,
            hasLogin = p.HasLogin,
            displayEmail = p.DisplayEmail,
            membershipText = p.MembershipText,
            statusText = isRunning
                ? (p.HasLogin ? "运行中" : "运行中 · 未登录")
                : (p.HasLogin ? "已登录 · 未启动" : "未登录"),
            dataDirExists = SafeExists(p.DataDir),
        };
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

        if (_exePath is null)
        {
            ResolveInstall();
            if (_exePath is null)
            {
                throw new InvalidOperationException("没找到 Cursor 的安装位置。");
            }
        }

        if (InstanceService.CountProcesses(profile.DataDir, DefaultDataDir) > 0)
        {
            WindowActivator.TryActivate(profile.DataDir, DefaultDataDir);
            return new { message = $"「{profile.Name}」已经在运行，已切换到它的窗口。" };
        }

        PrepareDataDir(profile);

        try
        {
            InstanceService.Launch(_exePath, profile.DataDir, DefaultDataDir, CurrentWindowMode());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("启动失败：" + ex.Message);
        }

        await Task.Delay(2200);
        return new { message = $"正在启动「{profile.Name}」…" };
    }

    /// <summary>启动前准备：确保目录存在，按偏好同步设置与会话。</summary>
    private void PrepareDataDir(AccountProfile profile)
    {
        if (profile.IsMain) return;

        try
        {
            Directory.CreateDirectory(profile.DataDir);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("创建数据目录失败：" + ex.Message);
        }

        if (_prefs.SyncSettingsFromMain)
        {
            var result = SettingsSync.SyncFromMain(DefaultDataDir, profile.DataDir, profile.SyncedHashes);
            if (result.Synced.Count > 0 || result.Skipped.Count > 0)
            {
                _store.Save();
            }
        }

        if (_prefs.SyncSessionFromMain)
        {
            SessionSync.SyncFromMain(DefaultDataDir, profile.DataDir);
        }
    }

    private object Activate(string id)
    {
        var profile = FindProfile(id);
        if (InstanceService.CountProcesses(profile.DataDir, DefaultDataDir) == 0)
        {
            throw new InvalidOperationException($"「{profile.Name}」没有在运行。");
        }

        if (!WindowActivator.TryActivate(profile.DataDir, DefaultDataDir))
        {
            throw new InvalidOperationException($"没找到「{profile.Name}」的窗口，请从任务栏切换过去。");
        }

        return new { ok = true };
    }

    private Task<object?> CloseAsync(string id)
    {
        var profile = FindProfile(id);
        var count = InstanceService.CloseProcesses(profile.DataDir, DefaultDataDir);
        if (count == 0)
        {
            throw new InvalidOperationException($"没有找到「{profile.Name}」的运行实例。");
        }
        return Task.FromResult<object?>(new { message = $"正在关闭「{profile.Name}」…" });
    }

    private Task<object?> OpenProjectAsync(string id)
    {
        var profile = FindProfile(id);

        if (_exePath is null)
        {
            ResolveInstall();
            if (_exePath is null) throw new InvalidOperationException("没找到 Cursor 的安装位置。");
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"选择要用「{profile.Name}」打开的项目文件夹",
        };

        if (dialog.ShowDialog(_window) != true)
        {
            return Task.FromResult<object?>(new { message = "" });
        }

        PrepareDataDir(profile);
        InstanceService.Launch(_exePath, profile.DataDir, DefaultDataDir, CurrentWindowMode(), dialog.FolderName);
        return Task.FromResult<object?>(new { message = "已发出打开请求" });
    }

    /// <summary>当前偏好的窗口模式。</summary>
    private InstanceService.WindowMode CurrentWindowMode() =>
        _prefs.UseClassicWindow ? InstanceService.WindowMode.Editor : InstanceService.WindowMode.Agent;

    // ------------------------------------------------------------------ 增删改

    private object AddAccount(JsonElement arg)
    {
        var name = ArgString(arg, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("名字不能为空。");
        }

        var profile = _store.Add(name.Trim());
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

    private object RemoveAccount(JsonElement arg)
    {
        var profile = FindProfile(ArgString(arg, "id"));

        if (profile.IsMain)
        {
            throw new InvalidOperationException("主账号不能移除。");
        }

        if (InstanceService.CountProcesses(profile.DataDir, DefaultDataDir) > 0)
        {
            throw new InvalidOperationException("这个账号正在运行，请先关闭它再移除。");
        }

        var deleteData = arg.ValueKind == JsonValueKind.Object
                         && arg.TryGetProperty("deleteData", out var d)
                         && d.ValueKind == JsonValueKind.True;

        if (deleteData && SafeExists(profile.DataDir))
        {
            try
            {
                Directory.Delete(profile.DataDir, recursive: true);
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
        OpenPath(profile.DataDir);
        return new { ok = true };
    }

    private object CopyDir(string id)
    {
        var profile = FindProfile(id);
        Clipboard.SetText(profile.DataDir);
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

    private void ResolveInstall()
    {
        _install = CursorLocator.FindInstall();
        _exePath = _install?.ExePath;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
