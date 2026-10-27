using System.Runtime.InteropServices;
using System.Text;

namespace DiskLens.Core;

/// 清理规则眼里的外部世界：用户把缓存搬去了哪、卷根和系统目录在哪。
/// 读取一律走 P 的 PurposeEnv，「写歪的环境变量」也用 PurposeRules.Full 判断——两边脾气一致，省得吵架。
internal sealed class CleanupPaths
{
    readonly PurposeEnv _env;

    /// 卷根，如 C:\（Path.Combine 直接可用）。
    public string Volume { get; }
    public string Windows { get; }
    public string Local { get; }
    /// 用户配置文件目录；DisplayPath 里写成 ~，脚本里写成 $env:USERPROFILE。
    public string User { get; }
    public string ProgramData { get; }
    public string Temp { get; }

    // 读文件和注册表的几项在构造时一次读完：Analyze 是持着树锁跑规则的，别在锁里碰磁盘。
    /// ~/.npmrc 里的 cache、store-dir。
    public string? NpmCache { get; }
    public string? NpmStore { get; }
    /// pnpm config.yaml 里的 storeDir。
    public string? PnpmStore { get; }
    /// 注册表里的「下载」文件夹（可能被用户搬走）。
    public string? DownloadsDir { get; }

    public CleanupPaths(PurposeEnv env, string volumeRoot)
    {
        _env = env;
        Volume = Path.TrimEndingDirectorySeparator(Path.GetFullPath(volumeRoot)); // C:\ 的根斜杠它自己会留着
        User = Var("USERPROFILE") ?? Folder(Environment.SpecialFolder.UserProfile) ?? "";
        Local = Var("LOCALAPPDATA") ?? Folder(Environment.SpecialFolder.LocalApplicationData) ?? "";
        Windows = Var("SystemRoot") ?? Var("windir") ?? Folder(Environment.SpecialFolder.Windows) ?? "";
        ProgramData = Var("ProgramData") ?? Folder(Environment.SpecialFolder.CommonApplicationData) ?? "";
        Temp = Var("TEMP") ?? Var("TMP") ?? Resolve(Path.GetTempPath()) ?? "";
        NpmCache = Resolve(env.NpmRc("cache"));
        NpmStore = Resolve(env.NpmRc("store-dir"));
        PnpmStore = Resolve(env.PnpmConfig("storeDir"));
        DownloadsDir = Resolve(env.Downloads());
    }

    public string? Var(string name) => Resolve(_env.Var(name));
    public string? Folder(Environment.SpecialFolder folder) => Resolve(_env.Folder(folder));

    /// 任何一段为空就整条作废，省得拼出「\Temp」这种以假乱真的相对路径。
    public static string? Join(string? root, params string[] parts) =>
        string.IsNullOrEmpty(root) ? null : Path.Combine([root, .. parts]);

    static string? Resolve(string? value) => PurposeRules.Full(value) is { } full ? LongName(full) : null;

    /// 用户名超过 8 个字符时 %TEMP% 默认是 C:\Users\ALEXAN~1\... 这种 8.3 短名，
    /// 而树里存的是长名，FindByPath 一比就对不上——用户临时文件这条规则会在大多数机器上静悄悄地失效。
    static string LongName(string path)
    {
        if (!path.Contains('~')) return path; // 短名一定带 ~，没有就别白跑一趟文件系统
        var sb = new StringBuilder(path.Length + 64);
        uint n = GetLongPathNameW(path, sb, (uint)sb.Capacity);
        if (n > sb.Capacity)
        {
            sb.EnsureCapacity((int)n);
            n = GetLongPathNameW(path, sb, (uint)sb.Capacity);
        }
        return n == 0 || n > sb.Capacity ? path : sb.ToString(0, (int)n); // 路径不存在时返回 0，原样放行
    }

    /// 给人看的路径：用户目录前缀写成 ~，不在用户目录下的写完整路径。
    public string Display(string path)
    {
        if (User.Length == 0) return path;
        if (path.Equals(User, StringComparison.OrdinalIgnoreCase)) return "~";
        return path.StartsWith(User + "\\", StringComparison.OrdinalIgnoreCase) ? "~" + path[User.Length..] : path;
    }

    /// DisplayPath（DESIGN §6.1）：多处用「, 」连，超过两处写前两处加「等 N 处」。
    public string DisplayList(IReadOnlyList<string> paths)
    {
        var shown = paths.Select(Display).ToArray();
        return shown.Length > 2 ? L.T($"{shown[0]}, {shown[1]} 等 {shown.Length} 处", $"{shown[0]}, {shown[1]} and more ({shown.Length} locations)", $"{shown[0]}、{shown[1]} など {shown.Length} か所") : string.Join(", ", shown);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint size);
}
