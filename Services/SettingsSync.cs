using System.IO;
using System.Security.Cryptography;

namespace CursorAccountSwitcher.Services;

/// <summary>
/// 把主账号的用户设置同步到其他账号的数据目录。
/// 只同步“编辑器体验类”文件，不动登录凭证（globalStorage/state.vscdb）。
/// </summary>
public static class SettingsSync
{
    /// <summary>需要同步的 User 目录下的相对文件。</summary>
    private static readonly string[] FilesToSync =
    {
        "settings.json",
        "keybindings.json",
        "tasks.json",
        "argv.json",
    };

    /// <summary>需要同步的 User 目录下的相对文件夹。</summary>
    private static readonly string[] FoldersToSync =
    {
        "snippets",
    };

    public sealed record SyncResult(
        IReadOnlyList<string> Synced,
        IReadOnlyList<string> Skipped,
        IReadOnlyList<string> Failed);

    /// <summary>
    /// 从主数据目录同步到目标数据目录，避免冲掉目标账号里用户自己的改动。
    ///
    /// 覆盖规则：
    ///   - 目标文件不存在 → 复制
    ///   - 目标文件仍是上次同步写入的内容（没被改过）→ 用主账号版本覆盖
    ///   - 目标文件已被改动（内容既不是上次写入的，也不等于主账号当前内容）→ 跳过
    ///
    /// 没有历史哈希记录时（首次运行或手工放进来的文件），用修改时间兜底：
    /// 目标比源新就认为用户改过，跳过。
    /// </summary>
    public static SyncResult SyncFromMain(
        string mainDataDir,
        string targetDataDir,
        IDictionary<string, string> syncedHashes)
    {
        var synced = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();

        var sourceUser = Path.Combine(mainDataDir, "User");
        var targetUser = Path.Combine(targetDataDir, "User");

        if (!Directory.Exists(sourceUser))
        {
            return new SyncResult(synced, skipped, failed);
        }

        Directory.CreateDirectory(targetUser);

        foreach (var file in FilesToSync)
        {
            SyncFile(Path.Combine(sourceUser, file), Path.Combine(targetUser, file),
                file, syncedHashes, synced, skipped, failed);
        }

        foreach (var folder in FoldersToSync)
        {
            var srcDir = Path.Combine(sourceUser, folder);
            if (!Directory.Exists(srcDir))
            {
                continue;
            }

            try
            {
                SyncDirectory(srcDir, Path.Combine(targetUser, folder), folder,
                    syncedHashes, synced, skipped, failed);
            }
            catch
            {
                failed.Add(folder + "/");
            }
        }

        return new SyncResult(synced, skipped, failed);
    }

    private static void SyncFile(
        string source,
        string target,
        string displayName,
        IDictionary<string, string> syncedHashes,
        List<string> synced,
        List<string> skipped,
        List<string> failed)
    {
        if (!File.Exists(source))
        {
            return;
        }

        try
        {
            if (File.Exists(target))
            {
                var targetHash = ComputeHash(target);
                var sourceHash = ComputeHash(source);

                // 内容一致，什么都不用做。
                if (targetHash == sourceHash)
                {
                    syncedHashes[displayName] = targetHash;
                    return;
                }

                var lastWritten = syncedHashes.TryGetValue(displayName, out var h) ? h : null;
                if (lastWritten is not null)
                {
                    // 上次写入后目标被独立改动过 → 保留用户在那个账号里的设置。
                    if (targetHash != lastWritten)
                    {
                        skipped.Add(displayName);
                        return;
                    }
                }
                else if (File.GetLastWriteTimeUtc(target) > File.GetLastWriteTimeUtc(source))
                {
                    // 没有历史记录时用修改时间兜底。
                    skipped.Add(displayName);
                    return;
                }
            }

            File.Copy(source, target, overwrite: true);
            syncedHashes[displayName] = ComputeHash(target);
            synced.Add(displayName);
        }
        catch
        {
            failed.Add(displayName);
        }
    }

    private static void SyncDirectory(
        string source,
        string target,
        string prefix,
        IDictionary<string, string> syncedHashes,
        List<string> synced,
        List<string> skipped,
        List<string> failed)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source))
        {
            var name = prefix + "/" + Path.GetFileName(file);
            SyncFile(file, Path.Combine(target, Path.GetFileName(file)), name,
                syncedHashes, synced, skipped, failed);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            SyncDirectory(dir, Path.Combine(target, Path.GetFileName(dir)),
                prefix + "/" + Path.GetFileName(dir),
                syncedHashes, synced, skipped, failed);
        }
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    /// <summary>目标数据目录是否已经初始化过（存在自己的 settings.json 或登录凭证）。</summary>
    public static bool IsInitialized(string dataDir)
    {
        var userDir = Path.Combine(dataDir, "User");
        if (!Directory.Exists(userDir))
        {
            return false;
        }

        return File.Exists(Path.Combine(userDir, "settings.json"))
               || StateDbReader.HasAuthToken(dataDir);
    }
}
