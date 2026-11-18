using System.IO;
using CursorAccountSwitcher.Models;
using Microsoft.Data.Sqlite;

namespace CursorAccountSwitcher.Services;

/// <summary>
/// 判断一条通知是哪个账号发出来的。
///
/// Windows 的通知只带应用级 AUMID（Anysphere.Cursor 等），不区分实例，所以只能反查：
///
/// 1. **会话标题反查**：通知正文的第一行是会话标题（如「Done • 修复登录按钮」）。
///    Cursor 把会话标题写进数据目录的 state.vscdb，所以能在哪个库里找到这个标题，
///    这条通知就属于哪个账号。实测准确；正在进行中的会话可能还没落库，走第 2 步。
///
/// 2. **写入时间兜底**：state.vscdb-wal 在实例有活动时会被写。取「写入时间最接近通知到达时间」
///    且时间差在合理范围内的那个实例。这能覆盖标题还没落库的情况。
/// </summary>
public static class NotificationAttribution
{
    public sealed record Result(string? ProfileId, string Method);

    /// <summary>时间兜底时，最佳候选比其它候选「明显更近」需要的倍数。</summary>
    private const double DominanceRatio = 3.0;

    /// <summary>最佳候选在这个时间内，直接认为就是它。</summary>
    private static readonly TimeSpan ImmediateWindow = TimeSpan.FromSeconds(90);

    /// <summary>
    /// 超过这个时间的通知不再按活跃时间判断：
    /// 那时的活跃状态与现在无关，硬猜只会指错账号。
    /// </summary>
    private static readonly TimeSpan AttributionHorizon = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 按数据目录最近的活跃时间挑实例。
    ///
    /// 不用固定时间窗口：Antigravity 的通知正文没有可反查的会话名，
    /// 而它的活跃文件写入时间与通知到达时间可能差几分钟。改成相对判断：
    /// 只有当某个实例明显比其它候选更近时才采用它，否则宁可判为「无法判断」。
    /// 通知太旧（超过 <see cref="AttributionHorizon"/>）时一律不猜。
    /// </summary>
    private static Result ByWriteTime(NotificationFeed.Item item, List<AccountProfile> candidates, string method = "活跃时间")
    {
        var deltas = new List<(string Id, TimeSpan Delta)>();

        foreach (var profile in candidates)
        {
            var written = LastActivity(profile);
            if (written is null)
            {
                continue;
            }

            deltas.Add((profile.Id, (item.Arrival - written.Value).Duration()));
        }

        if (deltas.Count == 0)
        {
            return new Result(null, "无法判断");
        }

        deltas.Sort((a, b) => a.Delta.CompareTo(b.Delta));
        var best = deltas[0];

        // 只有一个候选有活跃记录：用它。
        if (deltas.Count == 1)
        {
            return best.Delta <= AttributionHorizon
                ? new Result(best.Id, method)
                : new Result(null, "无法判断");
        }

        // 离通知时间很近，基本就是它。
        if (best.Delta <= ImmediateWindow)
        {
            return new Result(best.Id, method);
        }

        // 通知已经很久了，那时的活跃状态说明不了什么。
        if (best.Delta > AttributionHorizon)
        {
            return new Result(null, "无法判断");
        }

        // 否则要求明显领先第二名，避免在两个都不太活跃的账号之间瞎猜。
        var second = deltas[1];
        var dominant = best.Delta * DominanceRatio <= second.Delta
                       || (second.Delta - best.Delta) > TimeSpan.FromMinutes(5);

        return dominant
            ? new Result(best.Id, method)
            : new Result(null, "无法判断");
    }

    /// <summary>
    /// 找出这条通知属于哪个账号。返回 null 表示判不出来（交给界面显示成「未归属」）。
    /// </summary>
    public static Result Attribute(NotificationFeed.Item item, IReadOnlyList<AccountProfile> profiles)
    {
        var candidates = profiles.Where(p => p.App == item.App).ToList();
        if (candidates.Count == 0)
        {
            return new Result(null, "无同应用账号");
        }

        // 只有一个账号时不用猜，就是它。
        if (candidates.Count == 1)
        {
            return new Result(candidates[0].Id, "唯一账号");
        }

        if (item.App != AppKind.Cursor)
        {
            // Antigravity / Codex 的通知里没有可反查的会话标题（正文是固定文案），
            // 只能靠时间兜底。
            return ByWriteTime(item, candidates);
        }

        var title = item.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            return ByWriteTime(item, candidates);
        }

        var matched = new List<AccountProfile>();
        foreach (var profile in candidates)
        {
            if (StateDbReader.ContainsText(profile.DataDir, title))
            {
                matched.Add(profile);
            }
        }

        if (matched.Count == 1)
        {
            return new Result(matched[0].Id, "会话标题");
        }

        if (matched.Count > 1)
        {
            // 同一个标题在多个账号里都有（比如复制过去的会话）：用时间在候选里再筛一次。
            return ByWriteTime(item, matched, "标题+时间");
        }

        return ByWriteTime(item, candidates);
    }

    /// <summary>该数据目录最近的写入时间（按应用看不同的活跃信号）。</summary>
    private static DateTime? LastActivity(AccountProfile profile)
    {
        return profile.App switch
        {
            AppKind.Cursor => NewestWrite(profile.DataDir, new[]
            {
                Path.Combine("User", "globalStorage", "state.vscdb"),
                Path.Combine("User", "globalStorage", "state.vscdb-wal"),
            }),
            AppKind.Antigravity => NewestWrite(profile.DataDir, new[]
            {
                "app_storage.json",
                Path.Combine("logs"),
            }),
            AppKind.Codex => NewestWrite(profile.DataDir, new[]
            {
                ".codex-global-state.json",
                ".codex-global-state.json.bak",
                Path.Combine("sentry", "scope_v3.json"),
            }),
            _ => null,
        };
    }

    /// <summary>取这些相对路径里最新的写入时间。目录取其中最新文件的写入时间。</summary>
    private static DateTime? NewestWrite(string root, IEnumerable<string> relativePaths)
    {
        DateTime? newest = null;

        foreach (var relative in relativePaths)
        {
            var path = Path.Combine(root, relative);

            try
            {
                if (File.Exists(path))
                {
                    var time = File.GetLastWriteTime(path);
                    if (newest is null || time > newest)
                    {
                        newest = time;
                    }
                }
                else if (Directory.Exists(path))
                {
                    // 目录只看最近改过的几个文件，别把整个 logs 递归一遍。
                    var latest = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                        .Select(f =>
                        {
                            try { return File.GetLastWriteTime(f); }
                            catch { return DateTime.MinValue; }
                        })
                        .DefaultIfEmpty(DateTime.MinValue)
                        .Max();

                    if (latest > DateTime.MinValue && (newest is null || latest > newest))
                    {
                        newest = latest;
                    }
                }
            }
            catch
            {
            }
        }

        return newest;
    }
}
