using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CursorAccountSwitcher.Models;

/// <summary>
/// 一个 Cursor 账号档案。Main 使用默认数据目录，其余各自使用独立数据目录。
/// </summary>
public sealed class AccountProfile : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string? _cachedEmail;
    private string? _membership;
    private bool _isRunning;
    private int _processCount;

    /// <summary>稳定的内部标识，用于定位注册表记录与快捷方式。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>显示名，例如「公司号」「个人号」。</summary>
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    /// <summary>该档案的数据目录绝对路径。Main 为 %APPDATA%\Cursor。</summary>
    public string DataDir { get; set; } = string.Empty;

    /// <summary>是否为主档案（默认安装那一个，数据目录不可更改）。</summary>
    public bool IsMain { get; set; }

    /// <summary>这个档案属于哪个应用。旧数据没有该字段时视为 Cursor。</summary>
    [JsonConverter(typeof(AppKindJsonConverter))]
    public AppKind App { get; set; } = AppKind.Cursor;

    /// <summary>
    /// Antigravity 副账号的独立用户目录。对话和登录文件放在该目录的 .gemini 下。
    /// 主账号和 Cursor 为空，表示沿用真实用户目录。
    /// </summary>
    public string? HomeDir { get; set; }

    /// <summary>上次从该数据目录读到的登录邮箱，仅作展示与判断是否已登录。</summary>
    public string? CachedEmail
    {
        get => _cachedEmail;
        set
        {
            if (SetField(ref _cachedEmail, value))
            {
                OnPropertyChanged(nameof(DisplayEmail));
                OnPropertyChanged(nameof(HasLogin));
            }
        }
    }

    /// <summary>
    /// 上次同步写入该账号设置文件的哈希（文件名 -> SHA256）。
    /// 用于判断用户在另一个账号里有没有自己改过设置，避免被主账号覆盖。
    /// </summary>
    public Dictionary<string, string> SyncedHashes { get; set; } = new();

    /// <summary>订阅类型（free / pro / ultra 等），仅作展示。</summary>
    public string? Membership
    {
        get => _membership;
        set
        {
            if (SetField(ref _membership, value))
            {
                OnPropertyChanged(nameof(MembershipText));
            }
        }
    }

    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set => SetField(ref _isRunning, value);
    }

    [JsonIgnore]
    public int ProcessCount
    {
        get => _processCount;
        set => SetField(ref _processCount, value);
    }

    [JsonIgnore]
    public string DisplayEmail => string.IsNullOrWhiteSpace(CachedEmail) ? "尚未登录" : CachedEmail!;

    [JsonIgnore]
    public bool HasLogin => !string.IsNullOrWhiteSpace(CachedEmail);

    [JsonIgnore]
    public string MembershipText =>
        HasLogin && !string.IsNullOrWhiteSpace(Membership) ? Membership!.ToUpperInvariant() : "";

    [JsonIgnore]
    public string StatusText => IsRunning
        ? (HasLogin ? "运行中" : "运行中 · 未登录")
        : (HasLogin ? "已登录 · 未启动" : "未登录");

    /// <summary>主按钮文字：没启动时是「启动」，已在跑时是「切换过去」。</summary>
    [JsonIgnore]
    public string LaunchButtonText => IsRunning ? "切换过去" : "启动";

    public void RefreshStatusText()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(LaunchButtonText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
