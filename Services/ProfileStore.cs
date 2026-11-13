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
            store.Save();
        }

        store.EnsureMainProfile();
        return store;
    }

    private static List<AccountProfile> CreateDefaultProfiles() => new()
    {
        new AccountProfile
        {
            Name = "主账号",
            DataDir = DefaultDataDir,
            IsMain = true,
        },
    };

    /// <summary>始终保证存在且仅有一个主档案，并且它指向默认数据目录。</summary>
    private void EnsureMainProfile()
    {
        var mains = Profiles.Where(p => p.IsMain).ToList();
        if (mains.Count == 0)
        {
            Profiles.Insert(0, new AccountProfile
            {
                Name = "主账号",
                DataDir = DefaultDataDir,
                IsMain = true,
            });
            return;
        }

        var main = mains[0];
        main.DataDir = DefaultDataDir;
        foreach (var extra in mains.Skip(1))
        {
            extra.IsMain = false;
        }
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

    /// <summary>新增一个非主账号档案，数据目录放在 profiles 子目录下。</summary>
    public AccountProfile Add(string name)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(AppPrefs.DefaultProfilesDir, id);

        var profile = new AccountProfile
        {
            Name = name,
            DataDir = dir,
            IsMain = false,
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
