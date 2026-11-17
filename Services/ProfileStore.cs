using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CursorAccountSwitcher.Models;

namespace CursorAccountSwitcher.Services;

/// <summary>账号档案的持久化存储（JSON）。</summary>
public sealed class ProfileStore
{
    private const string FileName = "accounts.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string FilePath => Path.Combine(AppPrefs.RootDir, FileName);

    public List<AccountProfile> Profiles { get; private set; } = new();

    /// <summary>默认数据目录（安装自带的那一个）。</summary>
    public static string DefaultDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor");

    public static ProfileStore Load()
    {
        var store = new ProfileStore();
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<List<AccountProfile>>(json, JsonOptions);
                if (loaded is { Count: > 0 })
                {
                    store.Profiles = loaded;
                }
            }
        }
        catch
        {
        }

        if (store.Profiles.Count == 0)
        {
            store.Profiles = CreateDefaultProfiles();
        }

        if (store.EnsureMains())
        {
            store.Save();
        }

        return store;
    }

    private static List<AccountProfile> CreateDefaultProfiles() => new()
    {
        new AccountProfile
        {
            Name = "主账号",
            DataDir = DefaultDataDir,
            IsMain = true,
            App = AppKind.Cursor,
        },
    };

    /// <summary>每个应用各保留一个主档案，并指回各自的默认数据目录。</summary>
    private bool EnsureMains()
    {
        var changed = false;
        changed |= EnsureMain(AppKind.Cursor, insertAtStart: true);
        changed |= EnsureMain(AppKind.Antigravity, insertAtStart: false);
        changed |= EnsureMain(AppKind.Codex, insertAtStart: false);
        return changed;
    }

    private bool EnsureMain(AppKind app, bool insertAtStart)
    {
        var changed = false;
        var dataDir = AppCatalog.DefaultDataDir(app);
        var mains = Profiles.Where(p => p.IsMain && p.App == app).ToList();
        if (mains.Count == 0)
        {
            var profile = new AccountProfile
            {
                Name = "主账号",
                DataDir = dataDir,
                IsMain = true,
                App = app,
            };
            if (insertAtStart)
            {
                Profiles.Insert(0, profile);
            }
            else
            {
                Profiles.Add(profile);
            }

            return true;
        }

        var main = mains[0];
        if (!string.Equals(main.DataDir, dataDir, StringComparison.OrdinalIgnoreCase))
        {
            main.DataDir = dataDir;
            changed = true;
        }

        if (!string.IsNullOrEmpty(main.HomeDir))
        {
            main.HomeDir = null;
            changed = true;
        }

        foreach (var extra in mains.Skip(1))
        {
            extra.IsMain = false;
            changed = true;
        }

        return changed;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPrefs.RootDir);
            var json = JsonSerializer.Serialize(Profiles, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
        }
    }

    /// <summary>新增一个非主账号档案。Antigravity 另带独立用户目录，Codex 用独立 CODEX_HOME。</summary>
    public AccountProfile Add(string name, AppKind app)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var profile = app switch
        {
            AppKind.Antigravity => new AccountProfile
            {
                Name = name,
                App = app,
                DataDir = Path.Combine(AppCatalog.ProfilesDir(app), id, "userdata"),
                HomeDir = Path.Combine(AppCatalog.ProfilesDir(app), id, "home"),
                IsMain = false,
            },
            AppKind.Codex => new AccountProfile
            {
                Name = name,
                App = app,
                DataDir = Path.Combine(AppCatalog.ProfilesDir(app), id, "home"),
                IsMain = false,
            },
            _ => new AccountProfile
            {
                Name = name,
                App = AppKind.Cursor,
                DataDir = Path.Combine(AppCatalog.ProfilesDir(AppKind.Cursor), id),
                IsMain = false,
            },
        };

        Profiles.Add(profile);
        Save();
        return profile;
    }

    public void Remove(AccountProfile profile)
    {
        if (profile.IsMain)
        {
            return;
        }

        Profiles.Remove(profile);
        Save();
    }
}
