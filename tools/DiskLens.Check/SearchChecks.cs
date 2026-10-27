using DiskLens.Core;

namespace DiskLens.Check;

// 搜索自检：小树逐条验语法；随机树对拍暴力实现、深目录、200 万节点量速度在 SearchChecksBulk。
static class SearchChecks
{
    public static void Run(CheckContext ctx)
    {
        Words(ctx);
        Wildcards(ctx);
        Paths(ctx);
        Filters(ctx);
        Sizes(ctx);
        Quotes(ctx);
        Ranking(ctx);
        Scopes(ctx);
        Deletion(ctx);
        Smart(ctx);
        Errors(ctx);
        Fuzz(ctx);
        Threads(ctx);
        SearchChecksBulk.Run(ctx);
    }

    /// 夹具里每个名字都唯一，测试里直接按名字取 ID。
    /// 占用（alloc）是故意造的：有并列、有稀疏文件（size 大 alloc 小）、有刚好等于 2gb / 1mb 的边界。
    sealed class Fixture
    {
        public readonly FileTree Tree = new(@"X:\");
        public readonly Dictionary<string, int> Id = new();
        public readonly SearchIndex Index;

        public Fixture()
        {
            int docs = Dir(0, "Docs");
            File(docs, "Report Final.PDF", 3_000_000, 3_002_368);
            File(docs, "report_draft.docx", 120_000, 122_880);
            File(docs, "报告.docx", 90_000, 90_112);
            File(docs, "notes.txt", 2_000, 4_096);
            int archive = Dir(docs, "Archive");
            File(archive, "old report.pdf", 700_000, 700_416);
            File(archive, "img_01.png", 5_000, 8_192);
            File(archive, "img_2.png", 5_000, 8_192);
            File(archive, "img_ab.png", 5_000, 8_192);
            int weird = Dir(docs, "weird.pdf"); // 目录叫 .pdf：ext: 不该选中它，通配符 *.pdf 该选中
            File(weird, "inside.txt", 10, 4_096);

            int media = Dir(0, "Media");
            File(media, "movie.MKV", 2_147_483_648, 2_147_487_744);
            File(media, "clip.mp4", 52_428_800, 52_428_800);
            File(media, "song.mp3", 4_000_000, 4_001_792);
            int photos = Dir(media, "Photos");
            File(photos, "holiday photo.jpg", 3_500_000, 3_502_080);
            File(photos, "假期照片.jpg", 3_500_000, 3_502_080);

            int downloads = Dir(0, "Downloads");
            File(downloads, "setup.exe", 30_000_000, 30_003_200);
            File(downloads, "big.iso", 1_610_612_736, 1_610_616_832); // size 刚好 1.5gb
            File(downloads, "debug.log", 1_048_576, 1_048_576);        // size 刚好 1mb
            File(downloads, "app.log", 1_048_576, 1_048_576);
            File(downloads, "archive.tar.gz", 8_000_000, 8_003_584);
            File(downloads, "a-b.txt", 100, 4_096);
            File(downloads, "sparse.dat", 10_000_000, 4_096); // 稀疏：size 大 alloc 小
            File(downloads, "packed.dat", 5_000, 8_192);

            int group = Add("$NTFS 元数据", Tree.AddNode(0, "$NTFS 元数据", NodeFlags.Directory | NodeFlags.Virtual, 0, 0, 0));
            Add("$MFT", Tree.AddNode(group, "$MFT", NodeFlags.Metafile, 262_144_000, 262_144_000, 0));
            Add("未统计", Tree.AddNode(0, "未统计", NodeFlags.Virtual, 10L << 30, 10L << 30, 0));
            File(0, "noext", 1_234, 4_096);
            File(0, "Zed.TXT", 777, 4_096);
            File(0, "Ünïcödé.Txt", 50, 4_096);
            Id["X:\\"] = 0;
            Tree.Seal();
            Index = SearchIndex.Build(Tree, default);
        }

        int Add(string name, int id) => Id[name] = id;
        int Dir(int parent, string name) => Add(name, Tree.AddNode(parent, name, NodeFlags.Directory, 0, 0, 0));
        int File(int parent, string name, long size, long alloc) => Add(name, Tree.AddNode(parent, name, 0, size, alloc, 0));

        /// 能被搜到的节点：没删、不是虚拟，也不是根（Id 0，扫描目标本身）。
        public IEnumerable<int> Live() =>
            Enumerable.Range(1, Tree.Count - 1).Where(n => !Tree.Has(n, NodeFlags.Deleted | NodeFlags.Virtual));
    }

    static SearchResultDto Run(Fixture fx, string query, Metric metric = Metric.Alloc, int? scope = null, int limit = 300) =>
        fx.Index.Query(new(query, scope, metric, limit), default);

    static string[] Names(SearchResultDto r) => r.Items.Select(i => i.Name).ToArray();

    static SearchResultDto Expect(CheckContext ctx, Fixture fx, string query, string[] names, string what,
        Metric metric = Metric.Alloc, int? scope = null)
    {
        var r = Run(fx, query, metric, scope);
        ctx.True(r.Error == null, $"{what}（{query}）不该报错，实际：{r.Error}");
        ctx.Equal(string.Join(" | ", names), string.Join(" | ", Names(r)), $"{what}（{query}）");
        ctx.Equal((long)names.Length, r.Total, $"{what}（{query}）Total");
        ctx.True(!r.Truncated, $"{what}（{query}）不该被截断");
        return r;
    }

    static void Words(CheckContext ctx)
    {
        var fx = new Fixture();
        string[] report = ["Report Final.PDF", "old report.pdf", "report_draft.docx"];
        Expect(ctx, fx, "report", report, "子串按占用降序");
        Expect(ctx, fx, "REPORT", report, "大写查询");
        Expect(ctx, fx, "rEpOrT", report, "混合大小写");
        Expect(ctx, fx, "report pdf", ["Report Final.PDF", "old report.pdf"], "两个词 AND");
        Expect(ctx, fx, "pdf report", ["Report Final.PDF", "old report.pdf"], "AND 与词序无关");
        Expect(ctx, fx, "report -draft", ["Report Final.PDF", "old report.pdf"], "排除词");
        Expect(ctx, fx, "-draft report", ["Report Final.PDF", "old report.pdf"], "排除词在前");
        Expect(ctx, fx, "报告", ["报告.docx"], "中文");
        Expect(ctx, fx, "照片", ["假期照片.jpg"], "中文子串");
        Expect(ctx, fx, "ÜNÏCÖDÉ", ["Ünïcödé.Txt"], "带音标字母也不区分大小写");
        Expect(ctx, fx, "nothing-like-this", [], "没有命中");
        Expect(ctx, fx, "元数据", [], "虚拟分组目录不参与搜索");
        Expect(ctx, fx, "未统计", [], "虚拟叶子不参与搜索");
        Expect(ctx, fx, "mft", ["$MFT"], "虚拟分组下的元文件照常搜得到");

        var r = Run(fx, "-report");
        ctx.True(r.Error == null, "只有排除词不报错");
        ctx.Equal(29L, r.Total, "只有排除词：全部可见节点（32，不含根）减去 3 个 report");
        ctx.True(Names(r).All(n => !n.Contains("report", StringComparison.OrdinalIgnoreCase)), "只有排除词：结果里没有 report");
        ctx.True(Names(r).Contains("Docs") && !Names(r).Contains("X:\\"), "只有排除词：目录参与，根不参与");

        r = Run(fx, "-b");
        ctx.Equal((long)fx.Live().Count(n => !fx.Tree.GetName(n).Contains('b', StringComparison.OrdinalIgnoreCase)), r.Total, "-b 的总数");

        // 单独一个 - 没法排除什么，是在找连字符（边打 -draft 敲到一半也不会报错）；-"" 同理。
        Expect(ctx, fx, "-", ["a-b.txt"], "单独的 - 按字面搜索");
        Expect(ctx, fx, "-\"\"", ["a-b.txt"], "- 后面只有空引号：还是单独的连字符");
        Expect(ctx, fx, "txt - b", ["a-b.txt"], "夹在词之间的 - 也是一个 AND 条件");
        r = Run(fx, "--b");
        ctx.True(r.Error == null && r.Total == 31 && !Names(r).Contains("a-b.txt"), "--b：只有第一个 - 是排除符号，排除的是含「-b」的名字");

        foreach (var empty in new[] { "", "   ", "\"\"", "\"\" \"\"", " \t " })
        {
            r = Run(fx, empty);
            ctx.True(r.Error == null && r.Total == 0 && r.Items.Length == 0 && !r.Truncated, $"空查询「{empty}」：无错误、无结果");
        }
    }

    static void Wildcards(CheckContext ctx)
    {
        var fx = new Fixture();
        Expect(ctx, fx, "*.log", ["debug.log", "app.log"], "*.log，并列按 ID 升序");
        Expect(ctx, fx, "*.LOG", ["debug.log", "app.log"], "模式大小写不敏感");
        Expect(ctx, fx, "img_??.png", ["img_01.png", "img_ab.png"], "? 恰好一个字符");
        Expect(ctx, fx, "img_?.png", ["img_2.png"], "? 一个字符");
        Expect(ctx, fx, "img_???.png", [], "? 不能少也不能多");
        Expect(ctx, fx, "report*", ["Report Final.PDF", "report_draft.docx"], "前缀通配");
        Expect(ctx, fx, "*final*", ["Report Final.PDF"], "两头通配");
        Expect(ctx, fx, "*.pdf", ["Report Final.PDF", "old report.pdf", "weird.pdf"], "整名匹配：目录名也算");
        Expect(ctx, fx, "report", report3(), "对照：无通配符是子串");
        Expect(ctx, fx, "rep?rt", [], "通配符整名匹配，不是子串");
        Expect(ctx, fx, "*rep?rt*", report3(), "两头加 * 才是子串");
        Expect(ctx, fx, "re*rt*.pdf", ["Report Final.PDF"], "中间 * 回溯");
        Expect(ctx, fx, "*.tar.gz", ["archive.tar.gz"], "多个点");
        Expect(ctx, fx, "报*.docx", ["报告.docx"], "中文通配");
        Expect(ctx, fx, "*.log -debug*", ["app.log"], "通配符加排除");
        Expect(ctx, fx, "**.log", ["debug.log", "app.log"], "连续 * 当一个");
        Expect(ctx, fx, "-*.log type:file ext:dat", ["packed.dat", "sparse.dat"], "取反通配符");

        var all = Run(fx, "*");
        ctx.Equal(32L, all.Total, "* 匹配所有可见节点（不含根）");
        ctx.Equal(32, all.Items.Length, "* 的条目数");
        ctx.True(all.Items.All(i => i.Id != 0), "根（Id 0）不出现在结果里");

        static string[] report3() => ["Report Final.PDF", "old report.pdf", "report_draft.docx"];
    }

    static void Paths(CheckContext ctx)
    {
        var fx = new Fixture();
        Expect(ctx, fx, @"docs\archive", ["Archive", "old report.pdf", "img_01.png", "img_2.png", "img_ab.png"], "路径词：目录本身和它的后代");
        Expect(ctx, fx, @"DOCS\ARCHIVE", ["Archive", "old report.pdf", "img_01.png", "img_2.png", "img_ab.png"], "路径词不区分大小写");
        var inDocs = Run(fx, @"x:\docs\");
        ctx.Equal(11L, inDocs.Total, @"x:\docs\ 是 Docs 的全部后代（不含它自己）");
        ctx.True(!Names(inDocs).Contains("Docs"), @"x:\docs\ 不含 Docs 自己");
        Expect(ctx, fx, @"\photos\", ["holiday photo.jpg", "假期照片.jpg"], "路径词：根目录下一级也要拼对（不能出现双反斜杠）");
        Expect(ctx, fx, @"x:\media\photos", ["Photos", "holiday photo.jpg", "假期照片.jpg"], "带盘符的完整路径");
        Expect(ctx, fx, @"*\photos\*.jpg", ["holiday photo.jpg", "假期照片.jpg"], "路径通配符整条路径匹配");
        Expect(ctx, fx, @"*\photos", ["Photos"], "路径通配符：以 \\photos 结尾");
        Expect(ctx, fx, @"x:\docs\report*", ["Report Final.PDF", "report_draft.docx"], "盘符开头的路径通配符");
        // 空格把路径词和名字词分成两个词，AND 起来；路径词不要求以 \ 开头
        Expect(ctx, fx, @"photos\ jpg", ["holiday photo.jpg", "假期照片.jpg"], "路径词 + 名字词 AND（路径词没有前导反斜杠）");
        Expect(ctx, fx, @"\photos\ jpg", ["holiday photo.jpg", "假期照片.jpg"], "路径词 + 名字词 AND");
        Expect(ctx, fx, @"photos\jpg", [], "没有空格就是一个路径词，整串当子串找，路径里没有这一串");
        Expect(ctx, fx, @"\photos\ -holiday", ["假期照片.jpg"], "路径词 + 排除词");
        Expect(ctx, fx, @"x:\$mft", ["$MFT"], "虚拟分组不出现在路径里（和 GetPath 一致）");
        Expect(ctx, fx, @"元数据\", [], "路径里没有虚拟分组的名字");
        ctx.Equal(@"X:\$MFT", fx.Tree.GetPath(fx.Id["$MFT"]), "GetPath 的前提：夹具里 $MFT 的路径不含分组");
        ctx.Equal(32L, Run(fx, "x:").Total, "单个盘符加冒号按路径词处理：整棵树（根不算）");
        ctx.Equal(0L, Run(fx, "c:").Total, "别的盘符：没有结果，也不报错");
        ctx.True(Run(fx, "c:").Error == null, "c: 不是语法错误");

        // 根带尾部反斜杠时，根下一级自己的路径也要拼对（首版拼成 X:\\Docs，搜 x:\docs 找不到 Docs 本身）
        Expect(ctx, fx, @"x:\docs", ["Docs", "Report Final.PDF", "Archive", "old report.pdf", "report_draft.docx", "报告.docx",
            "img_01.png", "img_2.png", "img_ab.png", "notes.txt", "weird.pdf", "inside.txt"], "根下一级目录自己的路径");
        Expect(ctx, fx, @"x:\docs\ -\archive\", ["Report Final.PDF", "Archive", "report_draft.docx", "报告.docx", "notes.txt", "weird.pdf", "inside.txt"],
            "取反的路径词：排除 Archive 里面的（Archive 自己的路径不以 \\ 结尾，留下）");
        Expect(ctx, fx, @"-*\* type:file ext:log", [], "取反的路径通配符：所有路径都含 \\，全被排除");

        // 一致性：每个节点用自己显示的完整路径（GetPath，不含虚拟分组）都得搜得到自己
        foreach (int n in fx.Live())
        {
            string shown = fx.Tree.GetPath(n);
            var found = Run(fx, "\"" + shown + "\"");
            ctx.True(found.Error == null && found.Items.Any(i => i.Id == n && i.Path == shown), $"用显示的路径搜自己：{shown}");
        }

        // 根路径不带尾部反斜杠（扫描的是某个文件夹）时，拼法不一样
        var t = new FileTree(@"X:\Data");
        int sub = t.AddNode(0, "sub", NodeFlags.Directory, 0, 0, 0);
        t.AddNode(sub, "file.txt", 0, 10, 4096, 0);
        t.AddNode(0, "top.txt", 0, 20, 4096, 0);
        t.Seal();
        var index = SearchIndex.Build(t, default);
        var r = index.Query(new(@"x:\data\sub"), default);
        ctx.Equal("sub | file.txt", string.Join(" | ", Names(r)), "非盘符根：路径词");
        r = index.Query(new(@"data\top"), default);
        ctx.Equal("top.txt", string.Join(" | ", Names(r)), "非盘符根：根下的文件");
        r = index.Query(new(@"x:\data"), default);
        ctx.Equal(3L, r.Total, "非盘符根：路径都以 X:\\Data 开头，根自己不算");
    }

    static void Filters(CheckContext ctx)
    {
        var fx = new Fixture();
        Expect(ctx, fx, "ext:pdf", ["Report Final.PDF", "old report.pdf"], "ext 只选文件，不选叫 .pdf 的目录");
        Expect(ctx, fx, "ext:mp4,mkv", ["movie.MKV", "clip.mp4"], "ext 逗号分隔");
        Expect(ctx, fx, "ext:mp4;mkv", ["movie.MKV", "clip.mp4"], "ext 分号分隔");
        Expect(ctx, fx, "ext:*.mp4,*.mkv", ["movie.MKV", "clip.mp4"], "ext 顺手敲成通配符也认");
        Expect(ctx, fx, "* ext:iso", ["big.iso"], "纯 * 匹配一切，不影响别的条件");
        Expect(ctx, fx, "-*", [], "取反的纯 *：什么都排除掉");
        Expect(ctx, fx, "EXT:.MKV;.MP4", ["movie.MKV", "clip.mp4"], "ext 带点、大写");
        Expect(ctx, fx, "ext:txt", ["notes.txt", "inside.txt", "a-b.txt", "Zed.TXT", "Ünïcödé.Txt"], "同占用按 ID 升序");
        Expect(ctx, fx, "ext:gz", ["archive.tar.gz"], "只认最后一个扩展名");
        Expect(ctx, fx, "ext:iso size:>1gb", ["big.iso"], "只有过滤词");
        Expect(ctx, fx, "report ext:pdf", ["Report Final.PDF", "old report.pdf"], "名字词加 ext");
        Expect(ctx, fx, "-ext:pdf report", ["report_draft.docx"], "排除扩展名");
        Expect(ctx, fx, "ext:pdf ext:docx", [], "两个 ext 是 AND，同一个文件不可能有两个扩展名");

        var r = Run(fx, "type:dir");
        ctx.Equal(6L, r.Total, "type:dir 的个数（6 个目录，根和虚拟分组不算）");
        ctx.True(r.Items.All(i => i.IsDir), "type:dir 全是目录");
        ctx.True(IsDescending(r, i => i.Alloc), "type:dir 按占用降序");
        ctx.Equal(6L, Run(fx, "dir:").Total, "dir: 是 type:dir 的简写");
        r = Run(fx, "type:file");
        ctx.Equal(26L, r.Total, "type:file 的个数");
        ctx.True(r.Items.All(i => !i.IsDir), "type:file 全是文件");
        ctx.Equal(26L, Run(fx, "file:").Total, "file: 是 type:file 的简写");
        ctx.Equal(26L, Run(fx, "-type:dir").Total, "-type:dir = 文件");
        ctx.Equal(26L, Run(fx, "-dir:").Total, "-dir: = 文件");
        ctx.Equal(6L, Run(fx, "-file:").Total, "-file: = 目录（根不算）");
        Expect(ctx, fx, "dir:photos", ["Photos"], "dir: 后面带名字：只在目录里找");
        Expect(ctx, fx, "file:*.pdf", ["Report Final.PDF", "old report.pdf"], "file: 后面带通配符");
        Expect(ctx, fx, "type:dir *.pdf", ["weird.pdf"], "type:dir + 通配符");
        Expect(ctx, fx, "TYPE:FILE ext:LOG", ["debug.log", "app.log"], "关键字也不区分大小写");
        Expect(ctx, fx, "type:dir size:>1gb", ["Media", "Downloads"], "目录用聚合值参与 size 过滤（根不算）");
    }

    static void Sizes(CheckContext ctx)
    {
        var fx = new Fixture();
        Expect(ctx, fx, "size:>1gb type:file", ["movie.MKV", "big.iso"], "size:>1gb");
        Expect(ctx, fx, "size:>1GB type:file", ["movie.MKV", "big.iso"], "单位大写");
        Expect(ctx, fx, "size:>1G type:file", ["movie.MKV", "big.iso"], "单位 g");
        Expect(ctx, fx, "size:>=500k type:file",
            ["movie.MKV", "big.iso", "$MFT", "clip.mp4", "setup.exe", "archive.tar.gz", "song.mp3", "holiday photo.jpg", "假期照片.jpg",
             "Report Final.PDF", "debug.log", "app.log", "old report.pdf"], "size:>=500k，单位 k");
        Expect(ctx, fx, "size:>=500KB type:file ext:pdf", ["Report Final.PDF", "old report.pdf"], "单位 KB");
        Expect(ctx, fx, "size:<10mb type:file ext:log", ["debug.log", "app.log"], "size:<10mb");
        Expect(ctx, fx, "size:100mb-2gb type:file", ["big.iso", "$MFT"], "范围 100mb-2gb（alloc 下 movie.MKV 超出 2gb 一点点）");
        Expect(ctx, fx, "size:100mb-2gb type:file", ["movie.MKV", "big.iso", "$MFT"], "范围含上界（size 恰好 2gb）", Metric.Size);
        Expect(ctx, fx, "size:100MB-2GB type:file", ["movie.MKV", "big.iso", "$MFT"], "范围单位大写", Metric.Size);
        Expect(ctx, fx, "size:\"100 mb - 2 gb\" type:file", ["movie.MKV", "big.iso", "$MFT"], "范围里的空格（要加引号）被忽略", Metric.Size);

        // 边界：debug.log / app.log 的 size 恰好 1mb
        Expect(ctx, fx, "size:>1mb ext:log", [], "> 不含等于", Metric.Size);
        Expect(ctx, fx, "size:<1mb ext:log", [], "< 不含等于", Metric.Size);
        Expect(ctx, fx, "size:>=1mb ext:log", ["debug.log", "app.log"], ">= 含等于", Metric.Size);
        Expect(ctx, fx, "size:<=1mb ext:log", ["debug.log", "app.log"], "<= 含等于", Metric.Size);
        Expect(ctx, fx, "size:1mb ext:log", ["debug.log", "app.log"], "不带比较符：恰好等于", Metric.Size);
        Expect(ctx, fx, "size:1048576 ext:log", ["debug.log", "app.log"], "不写单位就是字节", Metric.Size);
        Expect(ctx, fx, "size:1048576b ext:log", ["debug.log", "app.log"], "单位 b", Metric.Size);
        Expect(ctx, fx, "size:1024k ext:log", ["debug.log", "app.log"], "1024k = 1mb", Metric.Size);
        Expect(ctx, fx, "size:1024kb ext:log", ["debug.log", "app.log"], "1024kb = 1mb", Metric.Size);
        Expect(ctx, fx, "size:>1.5gb type:file", ["movie.MKV"], "小数：big.iso 恰好 1.5gb，不算大于", Metric.Size);
        Expect(ctx, fx, "size:>=1.5GB type:file", ["movie.MKV", "big.iso"], "小数加大写单位", Metric.Size);
        Expect(ctx, fx, "size:>=1t", [], "tb 级别：没有这么大的（连根也没有）。", Metric.Size);
        Expect(ctx, fx, "size:<=0", [], "没有 0 字节的条目（夹具里的目录自身为 0 但聚合后不为 0）", Metric.Size);
        Expect(ctx, fx, "-size:>1mb type:file ext:log", ["debug.log", "app.log"], "取反区间", Metric.Size);
        Expect(ctx, fx, "size:\">=1 gb\" type:file", ["movie.MKV", "big.iso"], "引号里数字和单位之间可以有空格", Metric.Size);

        // size 过滤用的是当前度量，Metric 不同结果不同
        Expect(ctx, fx, "ext:dat size:>1mb", ["sparse.dat"], "Size 度量：稀疏文件逻辑上很大", Metric.Size);
        Expect(ctx, fx, "ext:dat size:>1mb", [], "Alloc 度量：稀疏文件占用很小");
    }

    static void Quotes(CheckContext ctx)
    {
        var fx = new Fixture();
        Expect(ctx, fx, "\"holiday photo\"", ["holiday photo.jpg"], "带空格的引号词");
        Expect(ctx, fx, "\"old report\"", ["old report.pdf"], "引号词按整串匹配");
        Expect(ctx, fx, "\"report old\"", [], "引号词词序不对就没有");
        Expect(ctx, fx, "report old", ["old report.pdf"], "不加引号是两个词 AND");
        Expect(ctx, fx, "report -\"old report\"", ["Report Final.PDF", "report_draft.docx"], "排除带空格的词");
        Expect(ctx, fx, "\"holiday photo\" ext:jpg", ["holiday photo.jpg"], "引号词加过滤");
        Expect(ctx, fx, "ext:\"jpg, png\" size:\">= 1 b\" \"holiday\"", ["holiday photo.jpg"], "过滤词的值也能加引号");
        Expect(ctx, fx, "\"-b\"", ["a-b.txt"], "引号里的 - 是字面的连字符");
        Expect(ctx, fx, "\"*.log\"", ["debug.log", "app.log"], "引号不关通配符");
        Expect(ctx, fx, "\"*_draft.*\" report", ["report_draft.docx"], "带通配符的引号词");
        Expect(ctx, fx, "  report   -draft  ", ["Report Final.PDF", "old report.pdf"], "多余空白");
        Expect(ctx, fx, "report\t-draft\r\n", ["Report Final.PDF", "old report.pdf"], "制表符和换行也是分隔符");
        Expect(ctx, fx, "re\"po\"rt", ["Report Final.PDF", "old report.pdf", "report_draft.docx"], "引号夹在词中间只是拼接");
    }

    static void Ranking(CheckContext ctx)
    {
        var fx = new Fixture();
        var r = Run(fx, "ext:txt", limit: 2);
        ctx.Equal("notes.txt | inside.txt", string.Join(" | ", Names(r)), "Limit=2：同值取 ID 小的");
        ctx.Equal(5L, r.Total, "Limit 不影响 Total");
        ctx.True(r.Truncated, "被截断要告诉前端");
        r = Run(fx, "ext:txt", limit: 5);
        ctx.True(!r.Truncated, "Limit 刚好等于 Total 不算截断");
        r = Run(fx, "ext:txt", limit: 0);
        ctx.True(r.Items.Length == 0 && r.Total == 5 && r.Truncated, "Limit=0 只数个数");
        r = Run(fx, "ext:txt", limit: -3);
        ctx.True(r.Items.Length == 0 && r.Total == 5, "负的 Limit 当 0");
        r = Run(fx, "*", limit: 1);
        ctx.Equal("Media", r.Items[0].Name, "Limit=1：最大的一个（根不算，是 Media）");

        Expect(ctx, fx, "ext:dat", ["packed.dat", "sparse.dat"], "Alloc 度量：packed 的占用大");
        Expect(ctx, fx, "ext:dat", ["sparse.dat", "packed.dat"], "Size 度量：sparse 的逻辑大小大", Metric.Size);

        r = Run(fx, "*");
        ctx.True(IsDescending(r, i => i.Alloc), "全量结果按 alloc 降序");
        for (int i = 1; i < r.Items.Length; i++)
            if (r.Items[i].Alloc == r.Items[i - 1].Alloc)
                ctx.True(r.Items[i].Id > r.Items[i - 1].Id, $"并列按 ID 升序：{r.Items[i - 1].Name} → {r.Items[i].Name}");
        var item = Run(fx, "holiday").Items[0];
        ctx.Equal(@"X:\Media\Photos\holiday photo.jpg", item.Path, "结果带完整路径");
        ctx.Equal(false, item.IsDir, "结果带 IsDir");
        ctx.Equal(3_500_000L, item.Size, "结果带 Size");
        ctx.Equal(3_502_080L, item.Alloc, "结果带 Alloc");
        ctx.Equal("*", Run(fx, "*").Query, "回显查询原文");
        ctx.True(r.TookMs >= 0, "TookMs 不为负");
    }

    static void Scopes(CheckContext ctx)
    {
        var fx = new Fixture();
        int docs = fx.Id["Docs"], media = fx.Id["Media"];
        var r = Run(fx, "*", scope: docs);
        ctx.Equal(11L, r.Total, "作用域 Docs：只有后代，不含 Docs 自己");
        ctx.True(!Names(r).Contains("Docs"), "作用域节点自己不在结果里");
        Expect(ctx, fx, "report", ["Report Final.PDF", "old report.pdf", "report_draft.docx"], "作用域内的子串", scope: docs);
        Expect(ctx, fx, "report", [], "作用域外的不出现", scope: media);
        Expect(ctx, fx, "type:dir", ["Archive", "weird.pdf"], "作用域内无名字词的全表过滤", scope: docs);
        Expect(ctx, fx, "*", ["movie.MKV", "clip.mp4", "Photos", "song.mp3", "holiday photo.jpg", "假期照片.jpg"], "作用域 Media 的全部后代（Photos 是目录，占用是两张照片之和）", scope: media);
        Expect(ctx, fx, "*", [], "作用域是文件：没有后代，不报错", scope: fx.Id["notes.txt"]);
        Expect(ctx, fx, "report", [], "作用域是文件（带名字词）", scope: fx.Id["Report Final.PDF"]);
        ctx.Equal(32L, Run(fx, "*", scope: 0).Total, "作用域是根：全部节点减去根自己");
        Expect(ctx, fx, "x:", fx.Live().OrderByDescending(n => fx.Tree.Alloc(n)).ThenBy(n => n).Select(n => fx.Tree.GetName(n)).ToArray(),
            "作用域是根 + 路径词：根自己不在结果里", scope: 0);
        Expect(ctx, fx, "report", ["Report Final.PDF", "old report.pdf", "report_draft.docx"], "作用域是根 + 子串", scope: 0);
        Expect(ctx, fx, "*", ["$MFT"], "作用域是虚拟分组：它的子节点照常搜到", scope: fx.Id["$NTFS 元数据"]);
        Expect(ctx, fx, @"photos\", ["holiday photo.jpg", "假期照片.jpg"], "路径词 + 作用域", scope: media);
        Expect(ctx, fx, @"photos\", [], "路径词 + 作用域（范围外）", scope: docs);

        foreach (int bad in new[] { -1, 999, int.MaxValue })
        {
            bool threw = false;
            try { Run(fx, "report", scope: bad); } catch (ArgumentException) { threw = true; }
            ctx.True(threw, $"作用域 {bad} 不存在：抛 ArgumentException（§2.3）");
        }
    }

    /// 「或」、相关度排序、拼音首字母、近似匹配（顶栏搜索默认开着的那几样）
    static void Smart(CheckContext ctx)
    {
        var fx = new Fixture();
        SearchResultDto Q(string query, bool smart = true, SearchSort sort = SearchSort.Relevance) =>
            fx.Index.Query(new(query, null, Metric.Alloc, 300, smart, sort), default);
        void Want(string query, string[] names, string what, bool smart = true, SearchSort sort = SearchSort.Relevance, bool fuzzy = false)
        {
            var r = Q(query, smart, sort);
            ctx.True(r.Error == null && r.Fuzzy == fuzzy && r.Total == names.Length, $"{what}（{query}）：没报错、Fuzzy={fuzzy}、Total={names.Length}，实际 {r.Error} / {r.Fuzzy} / {r.Total}");
            ctx.Equal(string.Join(" | ", names), string.Join(" | ", Names(r)), $"{what}（{query}）");
        }

        // 「或」：引号外的 | 分组，组内照旧同时满足
        Want("notes | setup", ["setup.exe", "notes.txt"], "两组满足任一组，按占用排", smart: false, sort: SearchSort.Size);
        Want("ext:mkv|ext:mp4", ["movie.MKV", "clip.mp4"], "| 两边不用空格", smart: false, sort: SearchSort.Size);
        Want("report -pdf | song", ["song.mp3", "report_draft.docx"], "每组各自排除；相关度：song.mp3 去掉扩展名就是 song，排第一", smart: false);
        Want("notes ||", ["notes.txt"], "空组不算", smart: false);
        ctx.True(Q(" | ", smart: false) is { Error: null, Total: 0, Items: [] }, "只有竖线：当作什么都没写");
        ctx.True(Q("\"a|b\"", smart: false) is { Error: null, Total: 0 }, "引号里的 | 是字面上的竖线（文件名里不会有），不是「或」");

        // 相关度：名字整个就是（或去掉扩展名就是）> 开头 > 词首 > 中间，同档按占用
        Want("archive", ["Archive", "archive.tar.gz"], "名字整个对上的目录排在更大的 archive.tar.gz 前面", smart: false);
        Want("archive", ["archive.tar.gz", "Archive"], "按占用排时照旧", smart: false, sort: SearchSort.Size);
        Want("report", ["Report Final.PDF", "report_draft.docx", "old report.pdf"], "开头对上的排在词首对上的前面", smart: false);

        // 拼音首字母：一级汉字换成声母，不开智能匹配时没有
        Want("bg", ["报告.docx"], "bg 找到「报告」");
        Want("bg", [], "不开智能匹配：bg 什么都没有", smart: false);
        Want("jqzp", ["假期照片.jpg"], "只在拼音里对上的种子词（名字扫描没命中，靠拼音那份扫到）");
        Want("bg docx", ["报告.docx"], "种子是 docx，bg 靠拼音判定；report_draft.docx 没有 bg，不算");
        Want("报告", ["报告.docx"], "汉字照常按名字搜");

        // 近似匹配：精确一条都没有才放宽，结果标 Fuzzy、按相似度排
        Want("dwnlds", ["Downloads"], "漏了字母的 dwnlds 找到 Downloads", fuzzy: true);
        Want("rprt", ["Report Final.PDF", "report_draft.docx", "old report.pdf"], "挨得一样紧的按占用，靠后开始的排后面", fuzzy: true);
        Want("rprt ext:pdf", ["Report Final.PDF", "old report.pdf"], "放宽的只是名字词，过滤词照旧", fuzzy: true);
        Want("rprt -final", ["report_draft.docx", "old report.pdf"], "排除词照旧精确", fuzzy: true);
        Want("rprt", [], "不开智能匹配就不放宽", smart: false);
        Want("zq", [], "两个字母以内的词不放宽：按顺序出现的两个字母，什么名字都像");
        Want("report", ["Report Final.PDF", "report_draft.docx", "old report.pdf"], "有精确结果就不放宽");
        Want("rprt | zzzz", [], "几组「或」的不放宽");

        fx.Tree.MarkDeleted(fx.Id["报告.docx"]);
        Want("bg", [], "删掉的不从拼音那份冒出来");
    }

    static void Deletion(CheckContext ctx)
    {
        var fx = new Fixture();
        fx.Tree.MarkDeleted(fx.Id["Archive"]);
        // 索引不重建，删除靠查询时看 Deleted 位
        Expect(ctx, fx, "report", ["Report Final.PDF", "report_draft.docx"], "删除目录后子树消失");
        Expect(ctx, fx, "img_*", [], "被删子树里的文件");
        Expect(ctx, fx, "ext:png", [], "被删子树里的扩展名");
        Expect(ctx, fx, @"docs\archive", [], "被删目录的路径词");
        Expect(ctx, fx, "archive", ["archive.tar.gz"], "同名但没被删的还在");
        ctx.Equal(5L, Run(fx, "type:dir").Total, "目录数少一个");
        ctx.Equal(27L, Run(fx, "*").Total, "总数少 5 个（Archive 自己 + 里面 4 个文件）");
        fx.Tree.MarkDeleted(fx.Id["big.iso"]);
        Expect(ctx, fx, "ext:iso", [], "删除单个文件");
        Expect(ctx, fx, "size:>1gb type:file", ["movie.MKV"], "删除后大小过滤也跟着变");
        bool threw = false;
        try { Run(fx, "*", scope: fx.Id["Archive"]); } catch (ArgumentException) { threw = true; }
        ctx.True(threw, "作用域指向已删除节点：抛 ArgumentException");
    }

    static void Errors(CheckContext ctx)
    {
        var fx = new Fixture();
        foreach (var (query, mention) in new (string, string)[]
        {
            ("size:abc", "size:abc"), ("size:>", "size:>"), ("size:", "size:"), ("size:1gb-100mb", "size:1gb-100mb"),
            ("size:5xb", "size:5xb"), ("size:1pb", "size:1pb"), ("size:>=-5", "size:>=-5"), ("size:1..5gb", "size:1..5gb"),
            ("size:99999999999999999999", "size:99999999999999999999"), ("size:>9999999tb", "size:>9999999tb"),
            ("type:foo", "type:foo"), ("type:", "type:"), ("ext:", "ext:"), ("ext:,;", "ext:,;"), ("ext:.", "ext:."),
            ("foo:bar", "foo:bar"), (":x", ":x"), ("-foo:bar", "-foo:bar"),
            ("report size:abc ext:pdf", "size:abc"), ("report \"unclosed", "\"unclosed"), ("\"", "\""),
            ("-dir:photos", "-dir:photos"), ("-file:x", "-file:x"),
        })
        {
            SearchResultDto r;
            try { r = Run(fx, query); }
            catch (Exception ex) { ctx.Fail($"语法错误「{query}」不该抛异常：{ex.GetType().Name} {ex.Message}"); continue; }
            ctx.True(r.Error != null, $"语法错误「{query}」要返回 Error");
            if (r.Error == null) continue;
            ctx.True(r.Total == 0 && r.Items.Length == 0 && !r.Truncated, $"语法错误「{query}」没有结果");
            ctx.True(r.Error.Contains(mention), $"错误信息要指出是哪个词「{mention}」，实际：{r.Error}");
            ctx.True(r.Error.Any(c => c >= 0x4E00 && c <= 0x9FFF), $"错误信息要是中文，实际：{r.Error}");
        }
        var long1 = Run(fx, new string('a', 5000));
        ctx.True(long1.Error != null && long1.Items.Length == 0, "超长查询报错");
        var nul = Run(fx, "re\0port");
        ctx.True(nul.Error != null && nul.Items.Length == 0, "含 \\0 的查询报错");
        var many = Run(fx, string.Join(' ', Enumerable.Repeat("a", 200)));
        ctx.True(many.Error != null, "条件太多报错");
        var ok = Run(fx, string.Join(' ', Enumerable.Repeat("a", 60)));
        ctx.True(ok.Error == null, "60 个条件没问题");
        ctx.True(Run(fx, string.Join(' ', Enumerable.Repeat("a", 100))).Error == null, "恰好 100 个条件没问题");
        ctx.True(Run(fx, string.Join(' ', Enumerable.Repeat("a", 101))).Error != null, "101 个条件报错");
        ctx.True(Run(fx, new string('a', 4096)).Error == null, "恰好 4096 个字符没问题");
        ctx.True(Run(fx, new string('a', 4097)).Error != null, "4097 个字符报错");

        // 看着像错的但其实是合法的
        foreach (var legal in new[] { "size:0", "size:<0", "size:>0", "size:.5k", "size:5.", "size:1.5gb", "size:>=0b", "c:\\users", "-ext:pdf", "c:", "a:b\\c", "size:1gb-1gb" })
        {
            var r = Run(fx, legal);
            ctx.True(r.Error == null, $"「{legal}」是合法的，实际：{r.Error}");
        }
        ctx.Equal(0L, Run(fx, "size:<0").Total, "size:<0 谁也不满足");
        ctx.True(Run(fx, "report", scope: null, limit: 5000).Items.Length <= 1000, "Limit 上限 1000");

        bool threw = false;
        try { fx.Index.Query(new("x", 0, Metric.Alloc, 5), new CancellationToken(true)); }
        catch (OperationCanceledException) { threw = true; }
        ctx.True(threw, "已取消的令牌：抛 OperationCanceledException");
        threw = false;
        try { SearchIndex.Build(fx.Tree, new CancellationToken(true)); }
        catch (OperationCanceledException) { threw = true; }
        ctx.True(threw, "Build 响应取消");
        threw = false;
        try { SearchIndex.Build(new FileTree(@"X:\"), default); }
        catch (InvalidOperationException) { threw = true; }
        ctx.True(threw, "树没封存就建索引：抛 InvalidOperationException");
        ctx.True(fx.Index.Query(new(null!), default).Error == null, "Query 为 null 当空");
    }

    /// 乱敲键盘也不能抛异常——前端是边打边搜的。
    static void Fuzz(CheckContext ctx)
    {
        var fx = new Fixture();
        var rnd = new Random(20261016);
        const string alphabet = "ab -:*?\\\"<>=.,;019extsizetypefiledir报";
        int thrown = 0;
        for (int i = 0; i < 4000; i++)
        {
            var sb = new System.Text.StringBuilder();
            for (int k = rnd.Next(1, 14); k > 0; k--) sb.Append(alphabet[rnd.Next(alphabet.Length)]);
            try { fx.Index.Query(new(sb.ToString(), rnd.Next(4) == 0 ? 1 : null, rnd.Next(2) == 0 ? Metric.Alloc : Metric.Size), default); }
            catch (Exception ex) { if (thrown++ < 3) ctx.Fail($"乱敲「{sb}」抛了 {ex.GetType().Name}：{ex.Message}"); }
        }
        ctx.Equal(0, thrown, "乱敲的查询全部不抛异常");
    }

    static void Threads(CheckContext ctx)
    {
        // 新建的索引还没有路径缓存：多个线程同时第一次用路径词，缓存得线程安全
        var fx = new Fixture();
        string[] queries = [@"docs\archive", @"\photos\", "report", "*.log", "ext:txt", @"*\photos\*.jpg", @"x:\docs\report*", "size:>1mb type:file"];
        var expected = queries.Select(q => string.Join("|", Run(new Fixture(), q).Items.Select(i => i.Id))).ToArray();
        int bad = 0;
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, w =>
        {
            for (int round = 0; round < 25; round++)
                for (int q = 0; q < queries.Length; q++)
                {
                    var got = string.Join("|", fx.Index.Query(new(queries[(q + w) % queries.Length]), default).Items.Select(i => i.Id));
                    if (got != expected[(q + w) % queries.Length]) Interlocked.Increment(ref bad);
                }
        });
        ctx.Equal(0, bad, "多线程并发查询结果一致");
    }

    static bool IsDescending(SearchResultDto r, Func<FileItemDto, long> value)
    {
        for (int i = 1; i < r.Items.Length; i++)
            if (value(r.Items[i]) > value(r.Items[i - 1])) return false;
        return true;
    }
}
