using System.IO;
using System.Text.Json;

namespace CursorAccountSwitcher.Services;

/// <summary>应用自身的配置目录与持久化偏好。</summary>
public sealed class AppPrefs
{
    /// <summary>配置格式版本。用于把旧版本的默认值迁移到新默认值。</summary>
    private const int CurrentSchema = 2;

    /// <summary>切换器自己的配置根目录。</summary>
    public static string RootDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CursorAccountSwitcher");

    /// <summary>新建账号档案的默认数据目录。</summary>
    public static string DefaultProfilesDir { get; } = Path.Combine(RootDir, "profiles");

    private static string PrefsFile => Path.Combine(RootDir, "prefs.json");

    /// <summary>配置格式版本；0 表示旧版本文件（旧文件里没有这个字段）。</summary>
    public int SchemaVersion { get; set; }

    /// <summary>启动非主账号前，把主账号的设置/快捷键/代码片段同步过去。</summary>
    public bool SyncSettingsFromMain { get; set; } = true;

    /// <summary>启动前把主账号的最近项目、终端历史等会话类 UI 状态同步过去。</summary>
    public bool SyncSessionFromMain { get; set; } = true;

    /// <summary>
    /// 用编辑器窗口（--classic）启动，而不是 Agent 面板（--glass）。
    ///
    /// 默认 false：Cursor 3.x 的 Agent 面板是对话优先界面，启动即可开始和 Agent 对话。
    /// 打开这个开关则退回传统 VS Code 编辑器布局。
    /// </summary>
    public bool UseClassicWindow { get; set; }

    /// <summary>启动前自动刷新登录信息。</summary>
    public bool RefreshOnLaunch { get; set; } = true;

    public static AppPrefs Load()
    {
        try
        {
            if (File.Exists(PrefsFile))
            {
                var json = File.ReadAllText(PrefsFile);
                var loaded = JsonSerializer.Deserialize<AppPrefs>(json);
                if (loaded is not null)
                {
                    return Migrate(loaded);
                }
            }
        }
        catch
        {
            // 配置坏了就用默认值，不阻塞启动。
        }

        return new AppPrefs();
    }

    /// <summary>
    /// 旧版本（SchemaVersion 0）把「用编辑器窗口」写成了默认 true，
    /// 那时候是因为 glass 模式在旧版 Cursor 上有卡死问题。
    /// 新版默认改用 Agent 面板，这里把它纠正过来一次。
    /// </summary>
    private static AppPrefs Migrate(AppPrefs loaded)
    {
        if (loaded.SchemaVersion < CurrentSchema)
        {
            loaded.UseClassicWindow = false;
            loaded.SchemaVersion = CurrentSchema;
            loaded.Save();
        }

        return loaded;
    }

    public void Save()
    {
        try
        {
            SchemaVersion = CurrentSchema;
            Directory.CreateDirectory(RootDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PrefsFile, json);
        }
        catch
        {
            // 忽略写入失败。
        }
    }
}
