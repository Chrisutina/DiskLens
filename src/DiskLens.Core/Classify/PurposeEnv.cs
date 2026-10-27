using Microsoft.Win32;

namespace DiskLens.Core;

/// 分类器眼里的外部世界：环境变量、特殊文件夹、下载目录、~/.npmrc。
/// 抽成一个小类只为测试能塞假环境——不然每条规则都得赌这台机器刚好装了对应的东西。
internal sealed class PurposeEnv
{
    public static readonly PurposeEnv Real = new();

    public Func<string, string?> Var { get; init; } = Environment.GetEnvironmentVariable;
    public Func<Environment.SpecialFolder, string?> Folder { get; init; } = f => Environment.GetFolderPath(f);
    /// .NET 没有 Downloads 这个特殊文件夹，想知道它被搬去哪了只能读注册表。
    public Func<string?> Downloads { get; init; } = RealDownloads;
    /// npm 的缓存目录经常只写在 ~/.npmrc 里，环境变量里压根没有（本机就是这样）。
    public Func<string, string?> NpmRc { get; init; } = RealNpmRc;
    /// pnpm 10 起把 storeDir 之类写在 %LOCALAPPDATA%\pnpm\config\config.yaml，同样不走环境变量。
    public Func<string, string?> PnpmConfig { get; init; } = RealPnpmConfig;

    static string? RealPnpmConfig(string key)
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm", "config", "config.yaml");
            return YamlValue(File.ReadAllText(file), key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// ponytail: 只认顶层的 `key: value` 一行，够读 storeDir 了；嵌套、多行、锚点一概不管，要认再换真解析器。
    /// 双引号里的 \\ 是转义的反斜杠，单引号和裸值按字面。
    internal static string? YamlValue(string text, string key)
    {
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0 || line[0] == '#' || !line[..colon].Trim().Equals(key, StringComparison.Ordinal)) continue;
            var v = line[(colon + 1)..].Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') return v[1..^1].ToString().Replace(@"\\", @"\");
            if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'') return v[1..^1].ToString();
            return v.IsEmpty ? null : v.ToString();
        }
        return null;
    }

    static string? RealDownloads()
    {
        try
        {
            // REG_EXPAND_SZ：GetValue 会替我们把 %USERPROFILE% 展开
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                "{374DE290-123F-4565-9164-39C4925E467B}", null) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static string? RealNpmRc(string key)
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npmrc");
            return NpmRcValue(File.ReadAllText(file), key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// .npmrc 同一个键写了多次时后面的赢；只取这一个键的值，文件里别的东西（比如令牌）一概不碰。
    /// ponytail: 只读用户级这一份，不展开 ${VAR} 和 ~，项目级、全局级 .npmrc 也不看。
    internal static string? NpmRcValue(string text, string key)
    {
        string? found = null;
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0 || line[0] is ';' or '#') continue;
            if (line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) found = line[(eq + 1)..].Trim().ToString();
        }
        return found;
    }
}
