using System.Globalization;
using System.Text;

namespace DiskLens.Core;

/// 搜索语法解析（DESIGN §5）：只把一行字拆成条件，不碰树。
/// 语法错误走返回值带回中文提示（点名哪个词），不抛异常——前端是边打边搜的，半截的词天天有。
internal static class SearchQuery
{
    public const int MaxLength = 4096;
    public const int MaxTerms = 100;

    /// 声明顺序就是判定顺序：便宜的排前面，AND 才能早点短路。
    public enum Kind : byte { Type, Ext, Size, Name, Wildcard, Path, PathWildcard }

    public sealed class Term
    {
        public Kind Kind { get; init; }
        public bool Negative { get; init; }
        /// 小写。Name/Path 是子串，Wildcard/PathWildcard 是模式。
        public string Text { get; init; } = "";
        public bool Dir { get; init; }
        public string[] Extensions { get; init; } = [];
        /// Size 的闭区间。
        public long Min { get; init; }
        public long Max { get; init; } = long.MaxValue;
        /// 每次查询开始时由索引按树里的扩展名表填好，下标是 ExtId，判定时不用再比字符串。
        public bool[]? ExtOk { get; set; }
    }

    /// 返回 null 表示语法没问题。blank=true：什么都没写（空串、只有空白、空引号或竖线）。
    /// groups：引号外的 | 把条件分成几组，组内同时满足、满足任一组即可。Windows 文件名里不会有 |，不会和名字撞。
    public static string? TryParse(string query, out Term[][] groups, out bool blank)
    {
        groups = [];
        blank = false;
        if (query.Length > MaxLength) return L.T($"搜索词太长了（最多 {MaxLength} 个字符）", $"The search is too long (up to {MaxLength} characters)", $"検索語が長すぎます（最大 {MaxLength} 文字）");
        if (query.Contains('\0')) return L.T("搜索词里有无效字符", "The search contains invalid characters", "検索語に無効な文字が含まれています");

        var tokens = new List<(string Text, bool Negative, int Group)>();
        var word = new StringBuilder();
        bool quoted = false, negative = false;
        int start = -1, group = 0; // start：当前词在原文里的起点，引号没闭合时要指给用户看
        for (int i = 0; i < query.Length; i++)
        {
            char c = query[i];
            if (!quoted && char.IsWhiteSpace(c)) { Flush(); continue; }
            if (!quoted && c == '|') { Flush(); group++; continue; }
            if (start < 0)
            {
                start = i;
                if (c == '-') { negative = true; continue; } // 只有词首、没加引号的 - 才是「排除」；"-b" 是在找 -b
            }
            if (c == '"') quoted = !quoted;
            else word.Append(c);
        }
        if (quoted) return L.T($"搜索词「{Shown(query[start..])}」的双引号没有闭合", $"The double quote in \"{Shown(query[start..])}\" isn't closed", $"検索語「{Shown(query[start..])}」の二重引用符が閉じていません");
        Flush();

        if (tokens.Count == 0) { blank = true; return null; }
        if (tokens.Count > MaxTerms) return L.T($"搜索条件太多了（最多 {MaxTerms} 个）", $"Too many search terms (up to {MaxTerms})", $"検索条件が多すぎます（最大 {MaxTerms} 個）");
        var parsed = new List<Term[]>();
        foreach (var bucket in tokens.GroupBy(t => t.Group)) // 空组（a || b、结尾的 |）自然没有
        {
            var list = new List<Term>();
            foreach (var (text, isNegative, _) in bucket)
                if (ParseToken(text, isNegative, list) is { } error) return error;
            parsed.Add([.. list.OrderBy(t => t.Kind)]);
        }
        groups = [.. parsed];
        return null;

        void Flush()
        {
            if (start < 0) return;
            if (word.Length > 0) tokens.Add((word.ToString(), negative, group));
            else if (negative) tokens.Add(("-", false, group)); // 后面什么都没有的 -（或 -""）没法排除什么，就是在找连字符
            word.Clear();
            negative = false;
            start = -1;
        }
    }

    static string? ParseToken(string text, bool negative, List<Term> into)
    {
        string t = text.ToLowerInvariant();
        string shown = Shown(negative ? "-" + text : text);
        int colon = t.IndexOf(':');
        bool drive = colon == 1 && char.IsAsciiLetter(t[0]); // c: 是盘符（正在敲路径），不是过滤词
        if (colon < 0 || drive || t.Contains('\\'))
        {
            AddPattern(into, t, negative, path: drive || t.Contains('\\'));
            return null;
        }

        string key = t[..colon], value = t[(colon + 1)..];
        switch (key)
        {
            case "ext":
                // 带点、带 *（顺手敲的 *.mp4）、逗号后带空格都认
                var extensions = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => x.TrimStart('*', '.')).Where(x => x.Length > 0).Distinct().ToArray();
                if (extensions.Length == 0) return L.T($"「{shown}」缺少扩展名。示例：ext:mp4,mkv", $"\"{shown}\" is missing an extension. Example: ext:mp4,mkv", $"「{shown}」に拡張子がありません。例：ext:mp4,mkv");
                into.Add(new Term { Kind = Kind.Ext, Negative = negative, Extensions = extensions });
                return null;
            case "type":
                if (value is not ("file" or "dir")) return L.T($"「{shown}」只能是 type:file 或 type:dir", $"\"{shown}\" must be type:file or type:dir", $"「{shown}」は type:file か type:dir のどちらかです");
                into.Add(new Term { Kind = Kind.Type, Negative = negative, Dir = value == "dir" });
                return null;
            case "file" or "dir":
                // file:/dir: 是 type: 的简写；后面跟名字就是「在文件/目录里找」。取反只有一个维度说得清，带名字时不让取反。
                if (value.Length > 0 && negative)
                    return L.T($"「{shown}」不能取反。想排除名字请写 -{value}，想排除{(key == "dir" ? "目录" : "文件")}请写 -type:{key}",
                        $"\"{shown}\" can't be negated. To exclude a name, write -{value}; to exclude {(key == "dir" ? "folders" : "files")}, write -type:{key}",
                        $"「{shown}」は否定できません。名前を除外するには -{value}、{(key == "dir" ? "フォルダー" : "ファイル")}を除外するには -type:{key} と書いてください");
                into.Add(new Term { Kind = Kind.Type, Negative = negative, Dir = key == "dir" });
                if (value.Length > 0) AddPattern(into, value, false, path: false);
                return null;
            case "size":
                if (!TryParseSize(value, out long min, out long max))
                    return L.T($"「{shown}」看不懂。示例：size:>1gb、size:<=500k、size:100mb-2gb", $"Can't understand \"{shown}\". Examples: size:>1gb, size:<=500k, size:100mb-2gb", $"「{shown}」を解釈できません。例：size:>1gb、size:<=500k、size:100mb-2gb");
                into.Add(new Term { Kind = Kind.Size, Negative = negative, Min = min, Max = max });
                return null;
            default:
                return L.T($"无法识别搜索条件「{shown}」。示例：ext:mp4 size:>1gb type:file", $"Unknown search term \"{shown}\". Example: ext:mp4 size:>1gb type:file", $"検索条件「{shown}」を認識できません。例：ext:mp4 size:>1gb type:file");
        }
    }

    static void AddPattern(List<Term> into, string text, bool negative, bool path)
    {
        bool wild = text.AsSpan().IndexOfAny('*', '?') >= 0;
        // 纯 * 匹配一切，不用判（取反的 -* 要照判：它匹配不到任何东西）
        if (wild && !path && !negative && !text.AsSpan().ContainsAnyExcept('*')) return;
        var kind = path ? (wild ? Kind.PathWildcard : Kind.Path) : (wild ? Kind.Wildcard : Kind.Name);
        into.Add(new Term { Kind = kind, Negative = negative, Text = text });
    }

    static string Shown(string s) => s.Length <= 40 ? s : s[..40] + "…";

    // ---------------- size ----------------

    /// >1gb、>=500k、<10mb、<=1mb、100mb-2gb（含两端）、1gb（恰好）。引号里数字和单位之间可以有空格。
    static bool TryParseSize(string value, out long min, out long max)
    {
        (min, max) = (0, long.MaxValue);
        value = string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        long x;
        if (value.StartsWith(">=")) return TryBytes(value.AsSpan(2), out min);
        if (value.StartsWith("<=")) return TryBytes(value.AsSpan(2), out max);
        if (value.StartsWith('>')) { if (!TryBytes(value.AsSpan(1), out x) || x == long.MaxValue) return false; min = x + 1; return true; }
        if (value.StartsWith('<')) { if (!TryBytes(value.AsSpan(1), out x)) return false; max = x - 1; return true; } // <0 得到 -1：谁也不满足，不算错
        int dash = value.IndexOf('-');
        if (dash > 0) return TryBytes(value.AsSpan(0, dash), out min) && TryBytes(value.AsSpan(dash + 1), out max) && min <= max;
        if (!TryBytes(value, out x)) return false;
        (min, max) = (x, x);
        return true;
    }

    static bool TryBytes(ReadOnlySpan<char> text, out long bytes)
    {
        bytes = 0;
        int cut = 0;
        while (cut < text.Length && (char.IsAsciiDigit(text[cut]) || text[cut] == '.')) cut++;
        if (cut == 0 || !decimal.TryParse(text[..cut], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number)) return false;
        long unit;
        switch (text[cut..])
        {
            case "" or "b": unit = 1L; break;
            case "k" or "kb": unit = 1L << 10; break;
            case "m" or "mb": unit = 1L << 20; break;
            case "g" or "gb": unit = 1L << 30; break;
            case "t" or "tb": unit = 1L << 40; break;
            default: return false;
        }
        try { bytes = (long)(number * unit); return true; }
        catch (OverflowException) { return false; } // 9999999tb 这种：比 long 大，当语法错误
    }

    // ---------------- 通配符 ----------------

    /// 整个文本匹配模式：? 一个字符，* 任意一串（可以为空）。两边都已经是小写。
    /// 不用 Regex：名字有两百万个，编译再快也不如这个双指针回溯。
    public static bool WildcardMatch(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        int n = 0, p = 0, star = -1, retry = 0;
        while (n < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || text[n] == pattern[p])) { n++; p++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; retry = n; }
            else if (star >= 0) { p = star + 1; n = ++retry; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
