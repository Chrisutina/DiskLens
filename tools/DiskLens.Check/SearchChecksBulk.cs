using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DiskLens.Core;

namespace DiskLens.Check;

// 搜索自检的另外两块：随机树上拿暴力实现对拍（语法、排序、作用域、删除、路径一次对齐），
// 以及 200 万节点的构建与查询耗时（Release 才断言阈值，Debug 只打印）。
// 用法：... -- search [--nodes 200000]   缩小合成树，改代码时快速跑一遍。
static class SearchChecksBulk
{
    public static void Run(CheckContext ctx)
    {
        Differential(ctx);
        DeepPaths(ctx);
        Bulk(ctx);
    }

    // ================= 随机树对拍 =================

    static readonly string[] Words = ["report", "draft", "img", "photo", "log", "tmp", "a", "b", "ab", "报告", "照片", "Old", "NEW",
        "x y", "node", "cache", "ünï", "data", "a-b", "-x", "Ünï"];
    static readonly string[] Extensions = ["", ".pdf", ".txt", ".log", ".png", ".jpg", ".dat", ".TXT", ".Docx", ".tar.gz", ".a", ".B"];

    /// 小词表 + 大量并列的占用 + 虚拟分组 + 带空格/连字符/中文/音标的名字：让每条语法都有机会撞上边界。
    static FileTree RandomTree(Random rnd, int nodes, string root)
    {
        var tree = new FileTree(root);
        var dirs = new List<int> { 0 };
        for (int i = 1; i < nodes; i++)
        {
            int parent = dirs[rnd.Next(dirs.Count)];
            bool isDir = rnd.Next(5) == 0;
            string word = Words[rnd.Next(Words.Length)];
            string name = isDir
                ? word + (rnd.Next(3) == 0 ? rnd.Next(10).ToString() : "")
                : word + (rnd.Next(2) == 0 ? "_" + Words[rnd.Next(Words.Length)] : "") + Extensions[rnd.Next(Extensions.Length)];
            var flags = (isDir ? NodeFlags.Directory : NodeFlags.None) | (rnd.Next(50) == 0 ? NodeFlags.Virtual : NodeFlags.None);
            long alloc = rnd.Next(5) * 4096L; // 故意大量并列，Top-K 的稳定性才测得到
            int id = tree.AddNode(parent, name, flags, isDir ? 0 : rnd.Next(4) * 3000L, isDir ? 0 : alloc, 0);
            if (isDir) dirs.Add(id);
        }
        tree.Seal();
        return tree;
    }

    /// 本仓库自己的目录：真实的名字（点开头、带空格、长短不一）和真实的深度，随机树编不出来的那种。只读，不写任何东西。
    static FileTree RealTree(string root, int max)
    {
        var tree = new FileTree(root);
        var pending = new Stack<(string Path, int Id)>();
        pending.Push((root, 0));
        while (pending.Count > 0 && tree.Count < max)
        {
            var (path, id) = pending.Pop();
            List<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(path).EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (tree.Count >= max) break;
                bool dir = entry is DirectoryInfo, link = entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                long size = 0;
                try { if (entry is FileInfo file) size = file.Length; }
                catch (IOException) { } // 枚举到读大小之间文件没了（编译产物天天在变），当 0
                int child = tree.AddNode(id, entry.Name, dir ? NodeFlags.Directory : NodeFlags.None, size, (size + 4095) & ~4095L, 0);
                if (dir && !link) pending.Push((entry.FullName, child)); // 不顺着链接往里走
            }
        }
        tree.Seal();
        return tree;
    }

    static void Differential(CheckContext ctx)
    {
        int queries = 0, nonEmpty = 0, failed = 0;
        var trees = new (string Label, Func<FileTree> Make, int Queries)[]
        {
            ("随机树 1", () => RandomTree(new Random(1), 1500, @"X:\"), 600),
            ("随机树 2", () => RandomTree(new Random(2), 2400, @"X:\Data"), 600),
            ("随机树 3", () => RandomTree(new Random(3), 900, @"\\server\share\"), 600),
            ("本仓库目录", () => RealTree(CheckContext.RepoRoot, 8000), 300),
        };
        int treeNo = 0;
        foreach (var (label, make, rounds) in trees)
        {
            var rnd = new Random(20261016 + ++treeNo);
            var tree = make();
            var index = SearchIndex.Build(tree, default);
            // 索引不重建：删除靠查询时看 Deleted 位
            for (int k = tree.Count / 20; k > 0; k--) tree.MarkDeleted(1 + rnd.Next(tree.Count - 1));
            for (int q = 0; q < rounds; q++)
            {
                var metric = rnd.Next(2) == 0 ? Metric.Alloc : Metric.Size;
                var gen = new Gen(rnd, tree, metric);
                var specs = Enumerable.Range(0, rnd.Next(1, 4)).Select(_ => gen.Term()).OrderBy(_ => rnd.Next()).ToList();
                string text = string.Join(rnd.Next(5) == 0 ? "  " : " ", specs.Select(s => s.Text));
                int? scope = rnd.Next(4) == 0 ? gen.Pick() : null;
                int limit = new[] { 0, 1, 5, 300, 1000 }[rnd.Next(5)];

                var expected = Enumerable.Range(1, tree.Count - 1) // 根（Id 0）不参与搜索
                    .Where(n => !tree.IsDeleted(n) && !tree.Has(n, NodeFlags.Virtual)
                        && (scope == null || n != scope && tree.IsAncestorOrSelf(scope.Value, n)) && specs.All(s => s.Hit(n)))
                    .ToArray();
                var top = expected.OrderByDescending(n => tree.Value(n, metric)).ThenBy(n => n).Take(limit).ToArray();

                var r = index.Query(new(text, scope, metric, limit), default);
                queries++;
                if (expected.Length > 0) nonEmpty++;
                bool same = r.Error == null && r.Total == expected.Length && r.Items.Select(i => i.Id).SequenceEqual(top)
                    && r.Truncated == expected.Length > top.Length;
                if (same) { ctx.True(true, ""); continue; }
                if (++failed <= 6)
                    ctx.Fail($"对拍不一致（{label}，scope={scope?.ToString() ?? "无"}，{metric}，limit={limit}）「{text}」：" +
                        $"期望 {expected.Length} 条 [{string.Join(',', top.Take(8))}]，实际 {r.Total} 条 [{string.Join(',', r.Items.Take(8).Select(i => i.Id))}] {r.Error}");
            }
        }
        if (failed > 6) ctx.Fail($"对拍还有 {failed - 6} 条不一致没有打印");
        ctx.Info($"对拍（3 棵随机树 + 本仓库目录）{queries} 条查询，其中 {nonEmpty} 条有命中，不一致 {failed} 条");
        ctx.True(nonEmpty * 5 > queries, "对拍的查询至少五分之一要有命中，否则这个测试是空转");
    }

    /// 随机生成「查询片段 + 暴力判定」：判定只用 FileTree 的公开读取接口和 BCL 字符串/正则，不碰搜索实现。
    sealed class Gen(Random rnd, FileTree tree, Metric metric)
    {
        readonly int[] _live = Enumerable.Range(1, tree.Count - 1).Where(n => !tree.IsDeleted(n) && !tree.Has(n, NodeFlags.Virtual)).ToArray();

        public int Pick() => _live[rnd.Next(_live.Length)];

        public (string Text, Func<int, bool> Hit) Term() => rnd.Next(12) switch
        {
            0 or 1 => Sub(false),
            2 => Sub(true),
            3 => Wild(false),
            4 => Wild(true),
            5 => Ext(),
            6 or 7 => Size(),
            8 => Kind(),
            9 => PathSub(),
            10 => PathWild(),
            _ => DirFile(),
        };

        // 词里有空白、以 - 开头时要加引号；前缀（-）必须留在引号外面
        static string Quote(string prefix, string text) =>
            text.Any(char.IsWhiteSpace) || text.StartsWith('-') ? $"{prefix}\"{text}\"" : prefix + text;

        // 只翻转「大写再转回小写还是它自己」的字符；ı、ſ、K（开尔文符号）这类怪字符翻了会变成另一个字母，不是引擎的错
        string RandomCase(string s) => string.Concat(s.Select(c =>
            rnd.Next(2) == 0 && char.ToLowerInvariant(char.ToUpperInvariant(c)) == char.ToLowerInvariant(c) ? char.ToUpperInvariant(c) : c));

        string Fragment()
        {
            if (rnd.Next(3) == 0) return Words[rnd.Next(Words.Length)];
            string name = tree.GetName(Pick());
            int at = rnd.Next(name.Length), len = rnd.Next(1, Math.Min(4, name.Length - at) + 1);
            return name.Substring(at, len);
        }

        bool NameHas(int n, string lower) => tree.GetName(n).ToLowerInvariant().Contains(lower, StringComparison.Ordinal);

        (string, Func<int, bool>) Sub(bool negative)
        {
            string word = Fragment(), lower = word.ToLowerInvariant();
            return (Quote(negative ? "-" : "", RandomCase(word)), negative ? n => !NameHas(n, lower) : n => NameHas(n, lower));
        }

        static Func<int, bool> WholeMatch(string lowerPattern, Func<int, string> subject)
        {
            var regex = new Regex("^" + string.Concat(lowerPattern.Select(c => c == '*' ? ".*" : c == '?' ? "." : Regex.Escape(c.ToString()))) + "$",
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
            return n => regex.IsMatch(subject(n));
        }

        (string, Func<int, bool>) Wild(bool negative)
        {
            var sb = new StringBuilder(tree.GetName(Pick()).ToLowerInvariant());
            for (int i = 0; i < sb.Length; i++) if (rnd.Next(8) == 0) sb[i] = '?';
            if (rnd.Next(2) == 0 && sb.Length > 2) { int a = rnd.Next(sb.Length - 1); sb.Remove(a, rnd.Next(1, sb.Length - a)); sb.Insert(a, '*'); }
            if (rnd.Next(4) == 0) sb.Insert(0, '*');
            if (rnd.Next(4) == 0) sb.Append('*');
            string pattern = sb.ToString();
            if (pattern.AsSpan().IndexOfAny('*', '?') < 0) pattern += "*"; // 没有通配符就是子串了，语义不同
            var hit = WholeMatch(pattern, n => tree.GetName(n).ToLowerInvariant());
            return (Quote(negative ? "-" : "", RandomCase(pattern)), negative ? n => !hit(n) : hit);
        }

        (string, Func<int, bool>) Ext()
        {
            var known = Enumerable.Range(1, tree.ExtCount - 1).Select(tree.ExtName).ToArray();
            var picked = Enumerable.Range(0, rnd.Next(1, 4)).Select(_ => known[rnd.Next(known.Length)]).ToArray();
            string text = "ext:" + string.Join(new[] { ",", ";", ", ", " ; " }[rnd.Next(4)], picked.Select(e => (rnd.Next(3) == 0 ? "." : "") + RandomCase(e)));
            bool negative = rnd.Next(4) == 0;
            return (Quote(negative ? "-" : "", text), n => (!tree.IsDir(n) && picked.Contains(tree.Ext(n))) != negative);
        }

        (string, Func<int, bool>) Size()
        {
            long A() => rnd.Next(3) == 0 ? rnd.Next(0, 20000) : tree.Value(Pick(), metric);
            // 同一个数随机换着写：字节、k、KB、m、MB
            string Fmt(long v) =>
                v > 0 && v % (1 << 20) == 0 && rnd.Next(2) == 0 ? (v >> 20) + (rnd.Next(2) == 0 ? "m" : "MB")
                : v > 0 && v % 1024 == 0 && rnd.Next(2) == 0 ? (v >> 10) + (rnd.Next(2) == 0 ? "k" : "KB")
                : v.ToString();
            long a = A(), b = A(), lo = Math.Min(a, b), hi = Math.Max(a, b);
            string text;
            Func<long, bool> test;
            switch (rnd.Next(6))
            {
                case 0: text = ">" + Fmt(a); test = v => v > a; break;
                case 1: text = "<" + Fmt(a); test = v => v < a; break;
                case 2: text = ">=" + Fmt(a); test = v => v >= a; break;
                case 3: text = "<=" + Fmt(a); test = v => v <= a; break;
                case 4: text = Fmt(a); test = v => v == a; break;
                default: text = Fmt(lo) + "-" + Fmt(hi); test = v => v >= lo && v <= hi; break;
            }
            bool negative = rnd.Next(4) == 0;
            return (Quote(negative ? "-" : "", "size:" + text), n => test(tree.Value(n, metric)) != negative);
        }

        (string, Func<int, bool>) Kind()
        {
            bool dir = rnd.Next(2) == 0, negative = rnd.Next(3) == 0, shorthand = rnd.Next(2) == 0;
            string text = (negative ? "-" : "") + (shorthand ? (dir ? "dir:" : "file:") : (dir ? "type:dir" : "type:file"));
            return (text, n => (tree.IsDir(n) == dir) != negative);
        }

        (string, Func<int, bool>) DirFile()
        {
            bool dir = rnd.Next(2) == 0;
            string word = Fragment(), lower = word.ToLowerInvariant();
            return (Quote("", (dir ? "dir:" : "file:") + RandomCase(word)), n => tree.IsDir(n) == dir && NameHas(n, lower));
        }

        string[] PathSegments(out string lower)
        {
            lower = tree.GetPath(Pick()).ToLowerInvariant();
            return lower.Split('\\');
        }

        (string, Func<int, bool>) PathSub()
        {
            var seg = PathSegments(out _);
            int i = rnd.Next(seg.Length), j = rnd.Next(i, seg.Length);
            string text = string.Join('\\', seg[i..(j + 1)]);
            if (i > 0 && rnd.Next(3) == 0) text = "\\" + text;
            if (rnd.Next(3) == 0) text += "\\";
            bool drive = text.Length >= 2 && text[1] == ':' && char.IsAsciiLetter(text[0]);
            if (!text.Contains('\\') && !drive) text += "\\"; // 没有反斜杠就成名字词了
            bool negative = rnd.Next(4) == 0;
            return (Quote(negative ? "-" : "", RandomCase(text)), n => tree.GetPath(n).ToLowerInvariant().Contains(text, StringComparison.Ordinal) != negative);
        }

        (string, Func<int, bool>) PathWild()
        {
            PathSegments(out string path);
            var sb = new StringBuilder(path);
            int a = rnd.Next(sb.Length), len = rnd.Next(1, Math.Min(sb.Length - a, 12) + 1);
            sb.Remove(a, len).Insert(a, '*');
            for (int k = rnd.Next(3); k > 0; k--) sb[rnd.Next(sb.Length)] = '?';
            if (rnd.Next(3) == 0) sb.Insert(0, '*');
            string pattern = sb.ToString();
            if (!pattern.Contains('\\')) pattern += "\\*";
            var hit = WholeMatch(pattern, n => tree.GetPath(n).ToLowerInvariant());
            bool negative = rnd.Next(4) == 0;
            return (Quote(negative ? "-" : "", RandomCase(pattern)), negative ? n => !hit(n) : hit);
        }
    }

    // ================= 深目录 =================

    /// 路径远超默认缓冲（256 字符）、层数远超默认链长（32），中间还夹着虚拟分组。
    static void DeepPaths(CheckContext ctx)
    {
        var tree = new FileTree(@"X:\");
        int dir = 0;
        var nodes = new List<int>();
        for (int d = 0; d < 150; d++)
        {
            var flags = d == 60 ? NodeFlags.Directory | NodeFlags.Virtual : NodeFlags.Directory;
            dir = tree.AddNode(dir, $"level-{d:000}-" + new string('x', 10), flags, 0, 0, 0);
            nodes.Add(dir);
            nodes.Add(tree.AddNode(dir, $"file{d:000}.txt", 0, 100, 4096, 0));
        }
        tree.Seal();
        var index = SearchIndex.Build(tree, default);
        int checkedCount = 0;
        foreach (int n in nodes.Where(n => !tree.Has(n, NodeFlags.Virtual)))
        {
            string path = tree.GetPath(n).ToLowerInvariant();
            var r = index.Query(new("\"" + path + "\"", null, Metric.Alloc, 1000), default);
            ctx.True(r.Error == null && r.Items.Any(i => i.Id == n && i.Path == tree.GetPath(n)), $"深目录：节点 {n} 用自己显示的路径（{path.Length} 字符）搜不到自己：{r.Error}");
            checkedCount++;
        }
        ctx.True(tree.GetPath(nodes[^1]).Length > 2500 && !tree.GetPath(nodes[^1]).Contains("level-060"), "深目录夹具本身：路径够长，虚拟分组不在路径里");
        ctx.True(checkedCount > 250, "深目录：检查了足够多的节点");
    }

    // ================= 200 万节点 =================

    static readonly string[] Vocab = ["report", "config", "backup", "cache", "temp", "tmp", "log", "data", "user", "system", "image", "photo", "video",
        "music", "document", "update", "driver", "service", "module", "index", "build", "release", "debug", "source", "test", "media", "archive",
        "session", "profile", "setting", "template", "plugin", "library", "resource", "package", "install", "download", "notes", "project", "draft",
        "final", "node", "old", "new"];
    static readonly string[] Chinese = ["报告", "照片", "文档", "下载", "项目", "备份", "视频", "音乐", "设计", "会议", "发票", "合同", "笔记", "截图", "素材"];
    static readonly string[] Syllables = ["ka", "ri", "to", "mi", "su", "ne", "vo", "la", "xe", "pu", "da", "gi", "ho", "zu", "ba", "fe", "yo", "ci", "wa", "ten", "lor", "man", "pex", "qui", "rol"];
    static readonly (string Ext, int Weight)[] ExtMix = [(".dll", 20), (".js", 10), (".png", 8), (".json", 8), (".xml", 6), (".txt", 5), (".dat", 4), (".jpg", 4),
        (".log", 3), (".cache", 3), ("", 3), (".exe", 2), (".tmp", 2), (".pdf", 1), (".zip", 1), (".mp4", 1), (".iso", 1)];

    static string Pseudo(Random rnd)
    {
        var sb = new StringBuilder();
        for (int k = rnd.Next(2, 5); k > 0; k--) sb.Append(Syllables[rnd.Next(Syllables.Length)]);
        return sb.ToString();
    }

    static string Stem(Random rnd) => rnd.Next(20) switch
    {
        < 10 => Pseudo(rnd) + (rnd.Next(3) == 0 ? "_" + rnd.Next(1000) : ""),
        < 15 => Vocab[rnd.Next(Vocab.Length)] + "_" + Pseudo(rnd),
        < 18 => Chinese[rnd.Next(Chinese.Length)] + rnd.Next(100),
        _ => Vocab[rnd.Next(Vocab.Length)] + (rnd.Next(2) == 0 ? "-" : " ") + rnd.Next(100),
    };

    static (FileTree Tree, int Big) BigTree(int nodes)
    {
        var rnd = new Random(7);
        var tree = new FileTree(@"C:\", nodes + 16);
        string[] top = ["Windows", "Users", "Program Files", "Program Files (x86)", "ProgramData", "Dev", "Games", "项目", "文档", "Temp"];
        var dirs = new List<int>();
        foreach (string t in top) dirs.Add(tree.AddNode(0, t, NodeFlags.Directory, 0, 0, 0));
        int system32 = tree.AddNode(dirs[0], "System32", NodeFlags.Directory, 0, 0, 0);
        dirs.Add(system32);
        int weightTotal = ExtMix.Sum(e => e.Weight), batch = 0;
        while (tree.Count < nodes)
        {
            // 大部分挑已有目录，少部分接着最近建的目录往深里走；隔一阵硬往 System32 里塞一批，路径词才有成规模的命中
            int parent = ++batch % 400 == 0 ? system32 : rnd.Next(7) == 0 ? dirs[^(1 + rnd.Next(Math.Min(dirs.Count, 64)))] : dirs[rnd.Next(dirs.Count)];
            for (int k = 1 + (int)(-Math.Log(1 - rnd.NextDouble()) * 5); k > 0 && tree.Count < nodes; k--)
            {
                int pick = rnd.Next(weightTotal);
                string ext = "";
                foreach (var (e, w) in ExtMix) { if (pick < w) { ext = e; break; } pick -= w; }
                long size = (long)Math.Exp(Math.Pow(rnd.NextDouble(), 4) * 21 + rnd.NextDouble() * 6);
                tree.AddNode(parent, Stem(rnd) + ext, 0, size, (size + 4095) & ~4095L, 133_000_000_000_000_000L + rnd.Next());
            }
            if (rnd.Next(10) < 6 && tree.Count < nodes)
                dirs.Add(tree.AddNode(parent, rnd.Next(4) == 0 ? Vocab[rnd.Next(Vocab.Length)] : Stem(rnd), NodeFlags.Directory, 0, 0, 0));
        }
        tree.Seal();
        return (tree, Enumerable.Range(1, top.Length).MaxBy(i => tree.Files(i))); // 作用域用文件最多的顶层目录，别选到一个小的
    }

    static void Bulk(CheckContext ctx)
    {
        int nodes = int.TryParse(ctx.Option("nodes"), out int given) && given > 1000 ? given : 2_000_000;
        var watch = Stopwatch.StartNew();
        var (tree, big) = BigTree(nodes);
        ctx.Info($"合成 {tree.Count:N0} 个节点用了 {watch.ElapsedMilliseconds:N0} ms；{Environment.ProcessorCount} 个逻辑核心，{(IsRelease ? "Release" : "Debug，只打印不断言阈值")}");

        SearchIndex index = null!;
        var first = Stopwatch.StartNew();
        index = SearchIndex.Build(tree, default);
        double cold = first.Elapsed.TotalMilliseconds;
        double build = Settled(500, () => { var sw = Stopwatch.StartNew(); index = SearchIndex.Build(tree, default); return sw.Elapsed.TotalMilliseconds; });
        ctx.Info($"构建索引：首次 {cold:F0} ms，中位 {build:F0} ms（目标 < 500 ms）");
        Limit(ctx, build, 500, "构建索引");

        bool Has(int n, string s) => tree.NameSpan(n).Contains(s, StringComparison.OrdinalIgnoreCase);
        // 下标在 10%~30% 之间、子孙最多的目录：作用域起点落在某个分段中间，子孙又跨好几个分段，scope+1 的裁剪和分段边界都会碰到
        int mid = Enumerable.Range(tree.Count / 10, tree.Count / 5).Where(tree.IsDir).MaxBy(tree.Files);
        (string Label, string Query, double LimitMs, int? Scope, Func<int, bool> Oracle)[] cases =
        [
            ("单个子串", "report", 50, null, n => Has(n, "report")),
            ("只有过滤词", "ext:dll size:>1mb", 150, null, n => !tree.IsDir(n) && tree.Ext(n) == "dll" && tree.Alloc(n) > 1 << 20),
            ("通配符", "*.log", 150, null, n => tree.NameSpan(n).EndsWith(".log", StringComparison.OrdinalIgnoreCase)),
            ("子串 + 排除 + 扩展名", "config -backup ext:json", 50, null, n => Has(n, "config") && !Has(n, "backup") && !tree.IsDir(n) && tree.Ext(n) == "json"),
            ("只有排除词", "-tmp", 150, null, n => !Has(n, "tmp")),
            // 以下不在 DESIGN 的性能目标里，但都是真实会敲的：路径词全表、命中极密的子串、带作用域
            ("路径词（全表）", @"windows\system32", 150, null, n => tree.GetPath(n).Contains(@"windows\system32", StringComparison.OrdinalIgnoreCase)),
            ("极密子串 e", "e", 50, null, n => Has(n, "e")),
            ("作用域内的子串", "report", 50, big, n => n != big && tree.IsAncestorOrSelf(big, n) && Has(n, "report")),
            ("名字词 + 路径词", @"report users\", 50, null, n => Has(n, "report") && tree.GetPath(n).Contains(@"users\", StringComparison.OrdinalIgnoreCase)),
            ("中段作用域·子串", "a", 50, mid, n => n != mid && tree.IsAncestorOrSelf(mid, n) && Has(n, "a")),
            ("中段作用域·全表", "type:file", 150, mid, n => n != mid && tree.IsAncestorOrSelf(mid, n) && !tree.IsDir(n)),
        ];

        var firstIds = new List<int[]>();
        foreach (var (label, query, limitMs, scope, oracle) in cases)
        {
            SearchResultDto Query() => index.Query(new(query, scope, Metric.Alloc, 300), default);
            var sw = Stopwatch.StartNew();
            var r = Query();
            double coldMs = sw.Elapsed.TotalMilliseconds;
            double median = Settled(limitMs, () => { var s = Stopwatch.StartNew(); Query(); return s.Elapsed.TotalMilliseconds; });
            ctx.Info($"{label}「{query}」：命中 {r.Total:N0}，首次 {coldMs:F1} ms，中位 {median:F1} ms（目标 < {limitMs} ms）");
            Limit(ctx, median, limitMs, label + "「" + query + "」");

            // 对拍：命中数和前 300 名必须和暴力实现完全一致（也顺带验证了并行分段的边界）
            var live = Enumerable.Range(1, tree.Count - 1).AsParallel() // 根（Id 0）不参与搜索
                .Where(n => !tree.IsDeleted(n) && !tree.Has(n, NodeFlags.Virtual) && oracle(n)).ToArray();
            var top = live.OrderByDescending(n => tree.Alloc(n)).ThenBy(n => n).Take(300).ToArray();
            ctx.Equal((long)live.Length, r.Total, $"{label}「{query}」命中数");
            ctx.True(r.Items.Select(i => i.Id).SequenceEqual(top), $"{label}「{query}」前 300 名与暴力实现一致");
            firstIds.Add(top);
        }

        // 智能匹配（顶栏默认开着）：第一次要先建拼音首字母那份；再量相关度排序、拼音种子、近似匹配的全表扫描
        var firstSmart = Stopwatch.StartNew();
        var wd = index.Query(new("wd", null, Metric.Alloc, 300, Smart: true, Sort: SearchSort.Relevance), default);
        ctx.Info($"智能匹配首次（含建拼音首字母）「wd」：命中 {wd.Total:N0}，{firstSmart.Elapsed.TotalMilliseconds:F0} ms");
        ctx.True(wd.Total > 0 && wd.Items.All(i => i.Name.StartsWith("文档", StringComparison.Ordinal)), "「wd」找到的都是「文档…」（拼音首字母）");
        (string Label, string Query, double LimitMs)[] smart = [("相关度·子串", "report", 80), ("拼音种子", "wd", 80), ("近似匹配全表", "rprtz", 400)];
        foreach (var (label, query, limitMs) in smart)
        {
            SearchResultDto Smart() => index.Query(new(query, null, Metric.Alloc, 300, true, SearchSort.Relevance), default);
            var r = Smart();
            double median = Settled(limitMs, () => { var s = Stopwatch.StartNew(); Smart(); return s.Elapsed.TotalMilliseconds; });
            ctx.Info($"智能匹配·{label}「{query}」：命中 {r.Total:N0}{(r.Fuzzy ? "（近似）" : "")}，中位 {median:F1} ms（目标 < {limitMs} ms）");
            Limit(ctx, median, limitMs, $"智能匹配·{label}「{query}」");
        }

        // 并发：同一个索引同时跑不同查询，结果不能串
        int bad = 0;
        Parallel.For(0, 24, new ParallelOptions { MaxDegreeOfParallelism = 8 }, k =>
        {
            var (_, query, _, scope, _) = cases[k % cases.Length];
            var r = index.Query(new(query, scope, Metric.Alloc, 300), default);
            if (!r.Items.Select(i => i.Id).SequenceEqual(firstIds[k % cases.Length])) Interlocked.Increment(ref bad);
        });
        ctx.Equal(0, bad, "200 万节点上并发查询结果一致");

        // 取消：跑完了也行，但要抛就只能抛 OperationCanceledException（不能被 Parallel 包成 AggregateException）
        int canceled = 0, wrong = 0;
        for (int k = 0; k < 40; k++)
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMicroseconds(k * 100));
            try { index.Query(new(k % 2 == 0 ? @"windows\system32 -tmp" : "-tmp", null, Metric.Alloc, 300), cts.Token); }
            catch (OperationCanceledException) { canceled++; }
            catch (Exception ex)
            {
                if (wrong++ == 0) ctx.Fail($"取消查询时抛了 {ex.GetType().Name}，应该是 OperationCanceledException");
            }
        }
        ctx.Info($"取消 40 次：{canceled} 次被打断，其余跑完了");
        ctx.True(canceled > 0 && wrong == 0, "至少有一次被取消打断，且没有别的异常");
    }

    // 不用 const：否则两种配置各有一边是「无法访问的代码」警告
    static readonly bool IsRelease =
#if DEBUG
        false;
#else
        true;
#endif

    static void Limit(CheckContext ctx, double ms, double limit, string what)
    {
        if (IsRelease) ctx.True(ms < limit, $"{what} 应 < {limit} ms，实际 {ms:F1} ms");
    }

    static double Median(int runs, Func<double> once)
    {
        var t = new double[runs];
        for (int i = 0; i < runs; i++) t[i] = once();
        Array.Sort(t);
        return t[runs / 2];
    }

    /// 量一轮取中位数；超了阈值再量最多两轮取最好的——同一台机器上别人正在编译时，别误报。
    static double Settled(double limit, Func<double> once)
    {
        double best = Median(7, once);
        for (int again = 0; again < 2 && best >= limit; again++) best = Math.Min(best, Median(7, once));
        return best;
    }
}
