using System.Globalization;

namespace DiskLens.Core;

/// 界面语言（DESIGN §8.8）：后台发给界面的文字（错误、清理建议、保护原因、删除结果、扫描警告、脚本里的说明……）都走 L.T，
/// 三种写法摆在同一处，编译器保证一个不漏；插值各写各的，语序不用迁就中文。
/// 宿主启动时按保存的设置（没有就按系统界面语言）设好；界面换语言时桥接 app.setLanguage 跟着改。默认中文：自检全按中文断言。
public static class L
{
    public enum Lang { Zh, En, Ja }

    static int _current; // 0 = Zh

    public static Lang Current
    {
        get => (Lang)Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, (int)value);
    }

    public static string T(string zh, string en, string ja) => Current switch { Lang.En => en, Lang.Ja => ja, _ => zh };

    /// 英文的单复数：Plural(2, "item") → "2 items"（数字按千分位）
    public static string Plural(long n, string one, string? many = null) => $"{n:N0} {(n == 1 ? one : many ?? one + "s")}";

    /// 界面那边的语言代码：zh-CN / en / ja
    public static string Code => Current switch { Lang.En => "en", Lang.Ja => "ja", _ => "zh-CN" };

    public static Lang? Parse(string? code) => code switch { "zh-CN" => Lang.Zh, "en" => Lang.En, "ja" => Lang.Ja, _ => null };

    /// 启动语言：保存过的选择优先；没有就看系统界面语言——中文（简繁都算）用中文、日文用日文，其余一律英文
    public static Lang Resolve(string? saved, string? uiCulture)
    {
        if (Parse(saved) is { } chosen) return chosen;
        string c = (uiCulture ?? "").ToLowerInvariant();
        return c.Length == 0 || c.StartsWith("zh", StringComparison.Ordinal) ? Lang.Zh : c.StartsWith("ja", StringComparison.Ordinal) ? Lang.Ja : Lang.En;
    }

    public static Lang FromSystem() => Resolve(null, CultureInfo.CurrentUICulture.Name);
}
