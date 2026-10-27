using System.Diagnostics;
using DiskLens.Core;

namespace DiskLens.Check;

/// queries 自检：造一棵刁钻的合成树，用最笨的办法（暴力扫描、全排序）算标准答案，再和被测代码逐项对。
/// 标准答案故意不碰 FileTree.Children / LiveChildren / CountedValue，只认 Parent 指针和标志位：
/// 被测代码依赖的那几条捷径（子节点排序、尾部删除节点裁剪）出了岔子才查得出来。
static class QueryChecks
{
    const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
    const long Stamp = 116444736000000000L + 1_700_000_000_000L * 10_000; // 2023 年某个时刻的 FILETIME
    const long Day = 864_000_000_000L, DayMs = 86_400_000; // 一天：FILETIME 刻度 / 毫秒
    static readonly Metric[] Both = [Metric.Alloc, Metric.Size];

    public static void Run(CheckContext ctx)
    {
        // 上次中途崩溃可能留下 junction，.NET 的递归清理不爱删它，先摘掉再让 Sandbox() 清场
        string leftover = Path.Combine(CheckContext.SandboxDir("queries"), "live", "jct");
        if (Directory.Exists(leftover)) RemoveJunction(leftover);
        string sandbox = ctx.Sandbox();
        var fx = BuildFixture(ctx, Path.Combine(sandbox, "syn"));
        ctx.Info($"合成树 {fx.Tree.Count:N0} 个节点，{fx.Deleted.Count} 棵已删除的子树，版本 {fx.Tree.Version}");
        ViewSuite(ctx, fx);
        SmallViews(ctx, fx);
        DetailsSuite(ctx, fx);
        LiveDetails(ctx, sandbox);
        LargestSuite(ctx, fx);
        ChildrenSuite(ctx, fx);
        RecentSuite(ctx);
        OwnerSuite(ctx);
        RelatedSuite(ctx);
        Performance(ctx, sandbox);
    }

    // ======================= 合成树 =======================

    sealed class Fixture
    {
        public FileTree Tree = null!;
        public Oracle Oracle = null!;
        public readonly Dictionary<string, int> Id = [];
        public readonly List<int> Deleted = [];
    }

    static long Cluster(long size) => (size + 4095) / 4096 * 4096;

    static Fixture BuildFixture(CheckContext ctx, string rootPath)
    {
        var fx = new Fixture();
        var t = new FileTree(rootPath);
        var rnd = new Random(20261016);
        int Dir(string key, int parent, string name, long alloc = 0, NodeFlags extra = 0)
        {
            int id = t.AddNode(parent, name, NodeFlags.Directory | extra, 0, alloc, Stamp);
            if (key.Length > 0) fx.Id[key] = id;
            return id;
        }
        int Fil(int parent, string name, long size, long alloc = -1, NodeFlags flags = 0, string key = "")
        {
            int id = t.AddNode(parent, name, flags, size, alloc >= 0 ? alloc : Cluster(size), Stamp);
            if (key.Length > 0) fx.Id[key] = id;
            return id;
        }

        // ---- 用户目录：大文件、稀疏文件、一堆图片、两万个小文件 ----
        int users = Dir("users", 0, "Users", 4096);
        int alice = Dir("alice", users, "alice", 8192);
        int videos = Dir("videos", alice, "Videos");
        for (int i = 0; i < 30; i++) Fil(videos, $"clip{i:D2}.mp4", rnd.NextInt64(200 * MB, 2 * GB));
        Fil(videos, "sparse.vhdx", 40 * GB, 2 * GB, NodeFlags.Sparse, "sparse"); // 逻辑 40 GB 实占 2 GB：两种度量下排名天差地别
        int photos = Dir("photos", alice, "Photos");
        string[] picExt = ["jpg", "png", "heic"];
        for (int i = 0; i < 900; i++) Fil(photos, $"IMG_{i:D4}.{picExt[i % 3]}", rnd.NextInt64(1 * MB, 9 * MB));
        int docs = Dir("docs", alice, "Documents");
        string[] docExt = ["pdf", "docx", "xlsx", "txt", "md"];
        for (int i = 0; i < 400; i++) Fil(docs, $"doc{i}.{docExt[i % 5]}", rnd.NextInt64(10 * KB, 3 * MB));
        Fil(docs, "secret.docx", 2 * MB, flags: NodeFlags.Encrypted, key: "enc");
        Fil(docs, "cloud.pdf", 5 * MB, 0, NodeFlags.Cloud, "cloud"); // 仅云端：Alloc 度量下是 0
        Fil(docs, "packed.xlsx", 9 * MB, 3 * MB, NodeFlags.Compressed, "packed");
        int code = Dir("code", alice, "Code");
        int nm = Dir("nm", code, "node_modules", 4096);
        for (int i = 0; i < 20000; i++) Fil(nm, $"m{i}.js", rnd.NextInt64(100, 6000)); // 大量小文件，占用大量并列
        int repo = Dir("repo", code, "repo");
        for (int i = 0; i < 200; i++) Fil(repo, $"f{i}.cs", rnd.NextInt64(1000, 60000));
        for (int i = 0; i < 3; i++) Fil(repo, $"objects{i}.pack", rnd.NextInt64(300 * MB, 600 * MB));

        // ---- 单目录 3000 个子项：大小两两不同，压缩文件让两种度量的排名错开；还有零值和常驻小文件 ----
        int flat = Dir("flat", 0, "Flat3000", 16 * KB);
        var perm = Enumerable.Range(0, 3000).OrderBy(_ => rnd.Next()).ToArray();
        for (int i = 0; i < 3000; i++)
        {
            long size = 5 * MB + perm[i] * 11_003L;
            bool packed = i % 7 == 0;
            Fil(flat, $"blob{i:D4}.{(i % 2 == 0 ? "bin" : "dat")}", size, packed ? size / 3 + 1 : Cluster(size),
                packed ? NodeFlags.Compressed : 0);
        }
        for (int i = 0; i < 20; i++) Fil(flat, $"empty{i}.tmp", 0, 0);
        for (int i = 0; i < 5; i++) Fil(flat, $"resident{i}.ini", 300 + i, 0); // 常驻小文件：Size 度量下有值，Alloc 度量下是 0

        // ---- Windows：硬链接对、三链接组 ----
        int win = Dir("windows", 0, "Windows", 4096);
        int sys32 = Dir("sys32", win, "System32", 4096);
        int sxs = Dir("sxs", win, "WinSxS", 4096);
        for (int i = 0; i < 400; i++) Fil(sys32, $"lib{i:D3}.dll", rnd.NextInt64(10 * KB, 6 * MB));
        for (int i = 0; i < 100; i++)
        {
            long size = rnd.NextInt64(20 * KB, 3 * MB);
            int a = Fil(sys32, $"hl{i:D3}.dll", size, key: i == 0 ? "hlA" : "");
            int b = Fil(sxs, $"hl{i:D3}.dll", size, key: i == 0 ? "hlB" : "");
            t.ResolveHardLinkGroup([a, b]);
        }
        int pf = Dir("pf", 0, "Program Files", 4096);
        int app = Dir("app", pf, "App", 4096);
        for (int i = 0; i < 200; i++) Fil(app, $"a{i}.{(i % 3 == 0 ? "exe" : "dll")}", rnd.NextInt64(50 * KB, 20 * MB));
        int sh1 = Fil(sys32, "shared.dll", 3 * MB, key: "sh1");
        int sh2 = Fil(sxs, "shared.dll", 3 * MB, key: "sh2");
        int sh3 = Fil(app, "shared.dll", 3 * MB, key: "sh3");
        ctx.Equal(sh3, t.ResolveHardLinkGroup([sh1, sh2, sh3]), "夹具：Program Files 那份应该是计入的链接");
        int pd = Dir("pd", 0, "ProgramData", 4096);
        int cache = Dir("cache", pd, "Cache");
        int cache2 = Dir("cache2", pd, "Cache2");
        for (int i = 0; i < 2000; i++) Fil(cache, $"c{i}.tmp", rnd.NextInt64(512, 80_000));
        int twinA = Fil(cache, "twin.bin", 700 * KB, key: "twinA");
        int twinB = Fil(cache2, "twin.bin", 700 * KB, key: "twinB");
        t.ResolveHardLinkGroup([twinA, twinB]);

        // ---- 虚拟节点、元数据、系统文件 ----
        int meta = Dir("meta", 0, "$NTFS 元数据", 0, NodeFlags.Virtual);
        fx.Id["mft"] = t.AddNode(meta, "$MFT", NodeFlags.Metafile, 1200 * MB, 1200 * MB, 0);
        t.AddNode(meta, "$LogFile", NodeFlags.Metafile, 64 * MB, 64 * MB, 0);
        fx.Id["unacc"] = t.AddNode(0, "未统计（系统保留或无权访问）", NodeFlags.Virtual, 5 * GB, 5 * GB, 0);
        Fil(0, "pagefile.sys", 8 * GB, flags: NodeFlags.Hidden | NodeFlags.System, key: "pagefile");
        Fil(0, "hiberfil.sys", 6 * GB, flags: NodeFlags.Hidden | NodeFlags.System);

        // ---- 边角：零值目录、空目录自占、重解析点、无权访问、深链、独生子链 ----
        int zero = Dir("zero", 0, "ZeroDir");
        for (int i = 0; i < 30; i++) Fil(zero, $"z{i}.txt", 0, 0);
        for (int i = 0; i < 5; i++) Fil(zero, $"r{i}.ini", 300, 0);
        Dir("emptyIdx", 0, "EmptyIdx", 8192);
        Dir("junction", 0, "Junction", 0, NodeFlags.Reparse);
        Dir("denied", 0, "Denied", 0, NodeFlags.Inaccessible);
        int cur = Dir("deep", 0, "Deep");
        for (int i = 0; i < 300; i++) cur = Dir("", cur, $"d{i}");
        Fil(cur, "bottom.bin", 1 * MB, key: "deepBottom");
        cur = Dir("chain", 0, "Chain");
        for (int i = 0; i < 8; i++) cur = Dir("", cur, $"c{i}");
        Fil(cur, "big.bin", 3 * GB);

        // ---- 扩展名：40 种（超过 15 要合成「*」），再来恰好 15 和 16 种的目录卡边界 ----
        int zoo = Dir("zoo", 0, "ExtZoo");
        for (int e = 0; e < 40; e++)
            for (int k = 0; k < 40 - e; k++)
                Fil(zoo, $"z{e:D2}_{k}.x{e:D2}", (41 - e) * MB + k * 997, e % 5 == 0 ? 4096 : -1, e % 5 == 0 ? NodeFlags.Sparse : 0);
        foreach (int kinds in new[] { 15, 16 })
        {
            int d = Dir($"ext{kinds}", 0, $"Ext{kinds}");
            for (int e = 0; e < kinds; e++)
                for (int k = 0; k < 3; k++) Fil(d, $"f{k}.q{e:D2}", (e + 1) * 100_000L + k);
        }

        // ---- 稍后整棵删掉的目录 ----
        int trash = Dir("trash", 0, "Trash", 4096);
        for (int i = 0; i < 100; i++) Fil(trash, $"t{i}.log", rnd.NextInt64(1 * MB, 8 * MB));
        for (int s = 0; s < 3; s++)
        {
            int sub = Dir("", trash, $"sub{s}", 4096);
            for (int i = 0; i < 50; i++) Fil(sub, $"u{i}.dat", rnd.NextInt64(100 * KB, 2 * MB));
        }

        t.Seal();
        var purposes = new byte[t.Count];
        for (int i = 0; i < purposes.Length; i++) purposes[i] = (byte)(i % Purposes.Count); // 目录也各有各的用途，聚合节点继承谁一目了然
        purposes[fx.Id["unacc"]] = Purposes.Unexplained;
        purposes[fx.Id["mft"]] = Purposes.System;
        t.SetPurposes(purposes);

        void Delete(int id)
        {
            t.MarkDeleted(id);
            fx.Deleted.Add(id);
        }
        foreach (int c in t.Children(flat).ToArray().Take(3)) Delete(c); // 最大的三个：排序数组前面的元素被删
        foreach (int c in t.Children(photos).ToArray().Take(5)) Delete(c);
        Delete(trash);
        Delete(sh3); // 计入的链接被删：计入身份要转给 System32 那份
        Delete(twinB); // 副本被删：twinA 变回普通文件
        fx.Tree = t;
        fx.Oracle = new Oracle(t, independent: true);
        return fx;
    }

    // ======================= 标准答案（笨办法） =======================

    sealed class Oracle
    {
        public readonly FileTree T;
        readonly List<int>[]? _kids;

        /// independent=false 时借用 Children()：只给两百万节点的大树做抽查用，建父子表太费内存。
        public Oracle(FileTree t, bool independent)
        {
            T = t;
            if (!independent) return;
            _kids = new List<int>[t.Count];
            for (int i = 0; i < t.Count; i++) _kids[i] = [];
            for (int i = 1; i < t.Count; i++) _kids[t.Parent(i)].Add(i);
        }

        public IEnumerable<int> Live(int i) =>
            (_kids != null ? _kids[i].AsEnumerable() : T.Children(i).ToArray()).Where(c => !T.IsDeleted(c));

        /// 对父目录的贡献：硬链接副本是 0。
        public long Counted(int i, Metric m) => T.Has(i, NodeFlags.HardLinkDup) ? 0 : T.Value(i, m);

        /// 子树里所有未删除节点（含自己）。
        public List<int> Members(int root)
        {
            var all = new List<int>();
            var stack = new Stack<int>();
            stack.Push(root);
            while (stack.TryPop(out int n))
            {
                all.Add(n);
                foreach (int c in Live(n)) stack.Push(c);
            }
            return all;
        }

        public (long[] Cats, long[] Purs, ExtStatDto[] Exts) Compose(int root, Metric m)
        {
            var cats = new long[FileCategories.Count];
            var purs = new long[Purposes.Count];
            var ext = new Dictionary<string, (long Size, long Alloc, long Files)>();
            foreach (int i in Members(root))
            {
                if (T.IsDir(i)) continue;
                bool counted = i == root || !T.Has(i, NodeFlags.HardLinkDup);
                if (counted)
                {
                    cats[T.Category(i)] += T.Value(i, m);
                    purs[T.Purpose(i)] += T.Value(i, m);
                }
                if (T.Has(i, NodeFlags.Virtual)) continue;
                ext.TryGetValue(T.Ext(i), out var s);
                ext[T.Ext(i)] = (s.Size + (counted ? T.Size(i) : 0), s.Alloc + (counted ? T.Alloc(i) : 0), s.Files + 1);
            }
            var rows = ext.Select(kv => new ExtStatDto(kv.Key, FileCategories.FromExtension(kv.Key), kv.Value.Size, kv.Value.Alloc, kv.Value.Files))
                .OrderByDescending(r => m == Metric.Alloc ? r.Alloc : r.Size).ThenBy(r => r.Ext, StringComparer.Ordinal).ToList();
            if (rows.Count > 15)
            {
                var rest = rows.Skip(15).ToList();
                rows = rows.Take(15).ToList();
                rows.Add(new ExtStatDto("*", FileCategories.Other, rest.Sum(r => r.Size), rest.Sum(r => r.Alloc), rest.Sum(r => r.Count)));
            }
            return (cats, purs, rows.ToArray());
        }
    }

    // ======================= tree.view =======================

    static void ViewSuite(CheckContext ctx, Fixture fx)
    {
        var o = fx.Oracle;
        var t = o.T;
        string[] rootKeys = ["", "users", "flat", "windows", "nm", "deep", "chain", "photos", "zoo", "meta", "sparse"];
        int views = 0;
        foreach (var metric in Both)
            foreach (var key in rootKeys)
            {
                int root = key.Length == 0 ? 0 : fx.Id[key];
                foreach (int budget in new[] { 1, 100, 250, 1000, 2000, 5000, 12000, 50_000 })
                {
                    var req = new ViewRequest(root, metric, budget);
                    var view = TreeQueries.BuildView(t, req);
                    VerifyView(ctx, o, req, view, $"视图[{(key.Length == 0 ? "根" : key)}/{metric}/{budget}]");
                    views++;
                    if (budget == 5000) ctx.True(view.Nodes.SequenceEqual(TreeQueries.BuildView(t, req).Nodes), $"视图[{key}/{metric}/{budget}] 重复调用结果应一致");
                }
            }
        ctx.Info($"{views} 个视图的不变量全部核对完毕");

        // 夹具自检：两种度量的排名确实不一样，否则"按 size 重排"测不出东西
        int flat = fx.Id["flat"];
        var byAlloc = o.Live(flat).OrderByDescending(c => o.Counted(c, Metric.Alloc)).ThenBy(c => c).Take(50).ToArray();
        var bySize = o.Live(flat).OrderByDescending(c => o.Counted(c, Metric.Size)).ThenBy(c => c).Take(50).ToArray();
        ctx.True(!byAlloc.SequenceEqual(bySize), "夹具：Flat3000 前 50 名在两种度量下应当不同");

        // 单目录 3000 个子项：1000 的预算被位置卡住，2000 的预算被 1500 封顶卡住
        foreach (var metric in Both)
        {
            var kids = o.Live(flat).Select(c => o.Counted(c, metric)).Where(v => v > 0).OrderByDescending(v => v).ToArray();
            long flatTotal = t.Value(flat, metric);

            // 聚合节点的值（上千个子项之和）比任何单个文件都大，按值排序后会排在最前面，所以按 Id 把真实节点和聚合节点分开看
            var tight = TreeQueries.BuildView(t, new ViewRequest(flat, metric, 1000));
            var tightReal = tight.Nodes.Skip(1).Where(n => n.Id >= 0).Select(n => n.V).ToArray();
            ctx.Equal(1000, tight.Nodes.Length, $"Flat3000/{metric} 预算 1000 应恰好用满");
            ctx.True(tightReal.SequenceEqual(kids.Take(998)), $"Flat3000/{metric} 预算 1000：单独展示的就是最大的 998 个");
            var tightAgg = tight.Nodes.Single(n => n.Id == -1);
            ctx.True(tightAgg.V == flatTotal - kids.Take(998).Sum() && tightAgg.C == kids.Length - 998, $"Flat3000/{metric} 预算 1000：聚合节点收下剩余的 {kids.Length - 998} 项");
            ctx.Equal(-1, tight.Nodes[1].Id, $"Flat3000/{metric}：聚合节点比任何单个文件都大，紧跟在根后面");

            var capped = TreeQueries.BuildView(t, new ViewRequest(flat, metric, 2000));
            ctx.Equal(1502, capped.Nodes.Length, $"Flat3000/{metric} 预算 2000：根 + 1500 + 聚合");
            ctx.True(capped.Nodes.Skip(1).Where(n => n.Id >= 0).Select(n => n.V).SequenceEqual(kids.Take(1500)), $"Flat3000/{metric}：1500 封顶后留下的是最大的 1500 个");
            var cappedAgg = capped.Nodes.Single(n => n.Id == -1);
            ctx.True(cappedAgg.C == kids.Length - 1500 && cappedAgg.V == flatTotal - kids.Take(1500).Sum()
                     && cappedAgg.N == $"其余 {kids.Length - 1500} 项", $"Flat3000/{metric}：封顶后聚合节点 = {kids.Length - 1500} 项 + 目录自身的 16 KB");
        }
    }

    /// 一个视图的全部不变量（DESIGN §10.1）。违例只收集前几条，免得一个 bug 刷出上万行。
    static void VerifyView(CheckContext ctx, Oracle o, ViewRequest req, ViewDto v, string label)
    {
        var t = o.T;
        var m = req.Metric;
        int budget = Math.Clamp(req.MaxNodes, 100, 50_000);
        long total = t.Value(req.NodeId, m);
        long threshold = total / ((long)budget * 4);
        var nodes = v.Nodes;
        var problems = new List<string>();
        int bad = 0;
        void Bad(string s) { if (bad++ < 6) problems.Add(s); }
        bool IsAgg(ViewNodeDto d) => (d.F & ViewFlags.Aggregate) != 0;

        if (v.RootId != req.NodeId || v.Metric != m || v.Total != total || v.TreeVersion != t.Version) Bad("头部字段与请求或树不符");
        if (nodes.Length < 1 || nodes.Length > budget)
        {
            ctx.True(false, $"{label}：节点数 {nodes.Length} 不在 [1,{budget}]");
            return;
        }
        var kidsIn = new List<int>[nodes.Length];
        for (int i = 0; i < nodes.Length; i++) kidsIn[i] = [];
        if (nodes[0].P != -1 || nodes[0].Id != req.NodeId || nodes[0].V != total || IsAgg(nodes[0])) Bad("根节点字段不对");
        for (int i = 1; i < nodes.Length; i++)
        {
            int p = nodes[i].P;
            if (p < 0 || p >= i) Bad($"#{i} 的父下标 {p} 不在 [0,{i})");
            else kidsIn[p].Add(i);
        }
        if (bad > 0)
        {
            ctx.True(false, $"{label}：{bad} 处违例：{string.Join("；", problems)}");
            return;
        }

        var seen = new HashSet<int>();
        var expandedValues = new List<long>();
        var starved = new List<(int Index, long V)>(); // 有够格的子节点却没展开的目录
        for (int i = 0; i < nodes.Length; i++)
        {
            var d = nodes[i];
            var kids = kidsIn[i];
            if (IsAgg(d))
            {
                if (d.Id != -1 || d.F != ViewFlags.Aggregate || d.K != 0) Bad($"#{i} 聚合节点的 id/F/K 不对：{d.Id}/{d.F}/{d.K}");
                if (kids.Count != 0) Bad($"#{i} 聚合节点不该有子节点");
                if (i == 0) continue;
                var pd = nodes[d.P];
                if (IsAgg(pd) || !t.IsDir(pd.Id))
                {
                    Bad($"#{i} 聚合节点的父节点不是真实目录");
                    continue;
                }
                if (d.U != pd.U) Bad($"#{i} 聚合节点的用途 {d.U} ≠ 父节点的 {pd.U}");
                if (d.V <= 0) Bad($"#{i} 聚合节点的值 {d.V} 应 > 0");
                if (d.N != (d.C > 0 ? $"其余 {d.C} 项" : "目录自身")) Bad($"#{i} 聚合节点名字「{d.N}」与项数 {d.C} 对不上");
                int siblings = kidsIn[d.P].Count, aggs = kidsIn[d.P].Count(k => IsAgg(nodes[k]));
                if (aggs != 1) Bad($"#{d.P} 下有 {aggs} 个聚合节点");
                if (d.P != 0 && aggs == siblings) Bad($"#{i} 是孤零零的聚合节点（视图根以外的目录一块都放不下就该保持 Unexpanded）");
                int positive = o.Live(pd.Id).Count(c => o.Counted(c, m) > 0);
                if (d.C != positive - (siblings - aggs)) Bad($"#{i} 聚合项数 {d.C} ≠ 值>0 的子项 {positive} − 已展示 {siblings - aggs}");
                continue;
            }

            if (!t.IsValid(d.Id))
            {
                Bad($"#{i} id {d.Id} 不存在或已删除");
                continue;
            }
            if (!seen.Add(d.Id)) Bad($"#{i} id {d.Id} 重复出现");
            if (i > 0)
            {
                var pd = nodes[d.P];
                if (IsAgg(pd) || t.Parent(d.Id) != pd.Id) Bad($"#{i} 的树父节点 {t.Parent(d.Id)} ≠ 视图父节点 {pd.Id}");
                if (t.Has(d.Id, NodeFlags.HardLinkDup)) Bad($"#{i} 是硬链接副本，计入值为 0，不该进视图");
                if (d.V <= 0 || d.V < threshold) Bad($"#{i} 值 {d.V} 应并进聚合（阈值 {threshold}）");
                if (d.V != o.Counted(d.Id, m)) Bad($"#{i} 值 {d.V} ≠ 树里的 {o.Counted(d.Id, m)}");
            }
            if (d.N != t.GetName(d.Id) || d.K != t.Category(d.Id) || d.U != t.Purpose(d.Id)) Bad($"#{i} 名字/类别/用途与树不一致");
            if ((d.F & ~ViewFlags.Unexpanded) != (int)t.Flags(d.Id)) Bad($"#{i} 标志 {d.F} 与树里的 {(int)t.Flags(d.Id)} 不一致");
            int liveCount = t.IsDir(d.Id) ? o.Live(d.Id).Count() : 0;
            if (d.C != liveCount) Bad($"#{i} 子节点数 {d.C} ≠ {liveCount}");
            bool unexpanded = (d.F & ViewFlags.Unexpanded) != 0;
            if (unexpanded != (d.C > 0 && kids.Count == 0)) Bad($"#{i} Unexpanded={unexpanded}，但子节点数 {d.C}、视图里的子节点 {kids.Count}");
            int realKids = kids.Count(k => !IsAgg(nodes[k]));
            if (realKids > 1500) Bad($"#{i} 单独展示了 {realKids} 个子节点，超过 1500");
            if (d.C == 0) continue;

            // 留下的必须是最大的那批；放弃了够格的子节点，只能是 1500 封顶或预算用完
            var keptIds = kids.Where(k => !IsAgg(nodes[k])).Select(k => nodes[k].Id).ToHashSet();
            long minKept = realKids == 0 ? long.MaxValue : kids.Where(k => !IsAgg(nodes[k])).Min(k => nodes[k].V);
            bool dropped = false;
            foreach (int c in o.Live(d.Id))
            {
                if (keptIds.Contains(c)) continue;
                long cv = o.Counted(c, m);
                if (cv > minKept) Bad($"#{i} 的子节点 {c}（值 {cv}）比留下的最小值 {minKept} 还大却没留下");
                if (cv > 0 && cv >= threshold) dropped = true;
            }
            if (unexpanded)
            {
                if (dropped) starved.Add((i, d.V));
            }
            else
            {
                expandedValues.Add(d.V);
                if (dropped && realKids != 1500 && nodes.Length != budget) Bad($"#{i} 放弃了够格的子节点，但既没到 1500 也没用完预算（{nodes.Length}/{budget}）");
            }
        }
        long minExpanded = expandedValues.Count == 0 ? long.MaxValue : expandedValues.Min();
        foreach (var (index, value) in starved)
        {
            if (nodes.Length < budget - 1) Bad($"#{index} 有够格的子节点却没展开，预算还剩 {budget - nodes.Length}");
            if (value > minExpanded) Bad($"#{index} 值 {value} 比已展开的目录（最小 {minExpanded}）还大却没展开，优先级错了");
        }

        for (int i = 0; i < nodes.Length; i++)
        {
            var ks = kidsIn[i];
            if (ks.Count == 0) continue;
            long sum = 0, prev = long.MaxValue;
            for (int j = 0; j < ks.Count; j++)
            {
                if (j > 0 && ks[j] != ks[j - 1] + 1) Bad($"#{i} 的子节点在数组里不连续");
                long cv = nodes[ks[j]].V;
                if (cv > prev) Bad($"#{i} 的子节点没按值降序：{prev} 后面跟着 {cv}");
                sum += cv;
                prev = cv;
            }
            if (sum != nodes[i].V) Bad($"#{i} 子之和 {sum} ≠ 自己的 {nodes[i].V}（差 {nodes[i].V - sum}）");
        }
        ctx.True(bad == 0, $"{label}：{bad} 处违例：{string.Join("；", problems)}");
    }

    // ======================= 小树：精确期望 =======================

    static void SmallViews(CheckContext ctx, Fixture fx)
    {
        // 阈值边界：值恰好等于阈值的留下，少 1 的并进聚合；零值只并不计数；目录自身占用并进聚合并参与排序
        foreach (long own in new long[] { 0, 50_000 })
            foreach (var metric in Both)
            {
                var t = new FileTree(@"X:\");
                if (own > 0) t.SetSizes(0, own, own);
                long big = 4_800_000 - own - 60_000 - 12_000 - 11_999;
                t.AddNode(0, "D.bin", 0, big, big, 0);
                t.AddNode(0, "C.bin", 0, 60_000, 60_000, 0);
                t.AddNode(0, "A.bin", 0, 12_000, 12_000, 0);
                t.AddNode(0, "B.bin", 0, 11_999, 11_999, 0);
                t.AddNode(0, "Z.bin", 0, 0, 0, 0);
                t.Seal();
                var req = new ViewRequest(0, metric, 100);
                var v = TreeQueries.BuildView(t, req);
                VerifyView(ctx, new Oracle(t, true), req, v, $"阈值边界[own={own}/{metric}]");
                string order = string.Join('|', v.Nodes.Select(n => n.N));
                ctx.Equal(own == 0 ? @"X:\|D.bin|C.bin|A.bin|其余 1 项" : @"X:\|D.bin|其余 1 项|C.bin|A.bin", order,
                    $"阈值边界[own={own}/{metric}] 的节点顺序（聚合节点按值排进兄弟里）");
                var agg = v.Nodes.Single(n => n.Id == -1);
                ctx.Equal(own + 11_999, agg.V, $"阈值边界[own={own}/{metric}] 聚合节点的值");
                ctx.Equal(1, agg.C, $"阈值边界[own={own}/{metric}] 聚合项数不含零值");
            }

        // 只有目录自身占用：视图根例外地放一块「目录自身」
        {
            var t = new FileTree(@"X:\");
            t.SetSizes(0, 4096, 4096);
            for (int i = 0; i < 3; i++) t.AddNode(0, $"z{i}", 0, 0, 0, 0);
            t.Seal();
            var v = TreeQueries.BuildView(t, new ViewRequest(0));
            ctx.Equal(2, v.Nodes.Length, "只有自身占用的根：根 + 目录自身");
            ctx.True(v.Nodes[1].N == "目录自身" && v.Nodes[1].V == 4096 && v.Nodes[1].C == 0, "目录自身节点的名字、值、项数");
            ctx.True((v.Nodes[0].F & ViewFlags.Unexpanded) == 0, "放了聚合节点的根不算 Unexpanded");
            VerifyView(ctx, new Oracle(t, true), new ViewRequest(0), v, "只有自身占用的根");
        }
        // 全是零值：什么都画不出来，根保持 Unexpanded
        {
            var t = new FileTree(@"X:\");
            for (int i = 0; i < 3; i++) t.AddNode(0, $"z{i}", 0, 0, 0, 0);
            t.Seal();
            var v = TreeQueries.BuildView(t, new ViewRequest(0));
            ctx.True(v.Nodes.Length == 1 && (v.Nodes[0].F & ViewFlags.Unexpanded) != 0 && v.Nodes[0].C == 3, "全零的根：只有根自己，Unexpanded");
            var empty = new FileTree(@"X:\");
            empty.Seal();
            var e = TreeQueries.BuildView(empty, new ViewRequest(0));
            ctx.True(e.Nodes.Length == 1 && e.Nodes[0].C == 0 && e.Nodes[0].F == (int)NodeFlags.Directory && e.Total == 0, "空树的视图");
        }

        // 链：每层只有一个孩子。无自占时每层 1 个节点，把预算正好用满；有自占时每层 2 个（孩子 + 目录自身）
        foreach (long own in new long[] { 0, 4096 })
            foreach (var metric in Both)
            {
                var t = new FileTree(@"X:\");
                int p = 0;
                for (int i = 0; i < 250; i++) p = t.AddNode(p, $"c{i}", NodeFlags.Directory, own, own, 0);
                t.AddNode(p, "big.bin", 0, GB, GB, 0);
                t.AddNode(0, "tiny.txt", 0, 10, 4096, 0);
                t.Seal();
                var req = new ViewRequest(0, metric, 100);
                var v = TreeQueries.BuildView(t, req);
                VerifyView(ctx, new Oracle(t, true), req, v, $"链[own={own}/{metric}]");
                if (own == 0) ctx.Equal(100, v.Nodes.Length, $"链[own=0/{metric}] 预算应正好用满");
                else ctx.True(v.Nodes.Length is 99 or 100, $"链[own=4096/{metric}] 用掉 {v.Nodes.Length} 个，应为 99 或 100");
                var last = v.Nodes.Last(n => n.Id >= 0);
                ctx.True((last.F & ViewFlags.Unexpanded) != 0 && last.C > 0, $"链[own={own}/{metric}] 最深的那个目录没展开，应标 Unexpanded");
            }

        // 大量并列：1500 封顶、聚合、结果可重复
        foreach (var metric in Both)
        {
            var t = new FileTree(@"X:\");
            for (int i = 0; i < 2000; i++) t.AddNode(0, $"e{i:D4}.dat", 0, MB, MB, 0);
            for (int s = 0; s < 10; s++)
            {
                int d = t.AddNode(0, $"d{s}", NodeFlags.Directory, 0, 0, 0);
                for (int j = 0; j < 100; j++) t.AddNode(d, $"f{j}.bin", 0, 512 * KB, 512 * KB, 0);
            }
            t.Seal();
            var o = new Oracle(t, true);
            foreach (int budget in new[] { 100, 12_000 })
            {
                var req = new ViewRequest(0, metric, budget);
                var v = TreeQueries.BuildView(t, req);
                VerifyView(ctx, o, req, v, $"并列[{metric}/{budget}]");
                ctx.True(v.Nodes.SequenceEqual(TreeQueries.BuildView(t, req).Nodes), $"并列[{metric}/{budget}] 重复调用应一致");
                if (metric != Metric.Size || budget != 12_000) continue;
                // Size 度量自己排序，并列按 id 升序；1500 封顶后留下的是 10 个目录 + id 最小的 1490 个文件（Alloc 走树的顺序，并列次序不作保证）
                var fileIds = v.Nodes.Where(n => n.Id >= 0 && n.P == 0 && n.V == MB).Select(n => n.Id).ToArray();
                ctx.True(fileIds.SequenceEqual(Enumerable.Range(1, 1490)), $"并列[Size/12000] 留下的应是 id 最小的 1490 个文件，实际 {fileIds.Length} 个");
            }
        }

        // 视图根是文件或空目录；预算夹取；非法节点
        var tree = fx.Tree;
        foreach (var key in new[] { "deepBottom", "emptyIdx", "unacc" })
        {
            var req = new ViewRequest(fx.Id[key], Metric.Alloc);
            var v = TreeQueries.BuildView(tree, req);
            ctx.True(v.Nodes.Length == 1 && v.Nodes[0].C == 0, $"{key} 作为视图根只有它自己");
            VerifyView(ctx, fx.Oracle, req, v, $"视图根[{key}]");
        }
        ctx.True(TreeQueries.BuildView(tree, new ViewRequest(0, Metric.Alloc, 1)).Nodes.SequenceEqual(TreeQueries.BuildView(tree, new ViewRequest(0, Metric.Alloc, 100)).Nodes), "MaxNodes=1 按 100 处理");
        ctx.True(TreeQueries.BuildView(tree, new ViewRequest(0, Metric.Alloc, int.MinValue)).Nodes.Length <= 100, "MaxNodes 为负数按 100 处理");
        ctx.True(TreeQueries.BuildView(tree, new ViewRequest(0, Metric.Size, int.MaxValue)).Nodes.SequenceEqual(TreeQueries.BuildView(tree, new ViewRequest(0, Metric.Size, 50_000)).Nodes), "MaxNodes 超大按 50000 处理");

        int gone = fx.Deleted[0];
        foreach (int bad in new[] { -1, tree.Count, gone })
        {
            Throws<ArgumentException>(ctx, () => TreeQueries.BuildView(tree, new ViewRequest(bad)), $"BuildView({bad})");
            Throws<ArgumentException>(ctx, () => TreeQueries.GetDetails(tree, new DetailsRequest(bad)), $"GetDetails({bad})");
            Throws<ArgumentException>(ctx, () => TreeQueries.LargestFiles(tree, new LargestRequest(bad)), $"LargestFiles({bad})");
        }
    }

    static void Throws<T>(CheckContext ctx, Action f, string what) where T : Exception
    {
        Exception? caught = null;
        try { f(); }
        catch (Exception ex) { caught = ex; }
        ctx.True(caught is T, $"{what}：应抛 {typeof(T).Name}，实际 {caught?.GetType().Name ?? "没抛"}");
    }

    // ======================= tree.details =======================

    static void DetailsSuite(CheckContext ctx, Fixture fx)
    {
        var o = fx.Oracle;
        var t = o.T;
        string[] keys = ["", "users", "alice", "flat", "windows", "sys32", "nm", "photos", "videos", "sparse", "sh1", "sh2", "sh3", "twinA",
            "hlA", "hlB", "unacc", "meta", "mft", "pagefile", "zero", "emptyIdx", "junction", "denied", "deep", "deepBottom", "chain",
            "zoo", "ext15", "ext16", "enc", "cloud", "packed", "cache", "trash"];
        foreach (var metric in Both)
            foreach (var key in keys)
            {
                int id = key.Length == 0 ? 0 : fx.Id[key];
                if (t.IsDeleted(id))
                {
                    Throws<ArgumentException>(ctx, () => TreeQueries.GetDetails(t, new DetailsRequest(id, metric)), $"已删除的 {key} 的详情");
                    continue;
                }
                VerifyDetails(ctx, o, id, metric, $"详情[{(key.Length == 0 ? "根" : key)}/{metric}]");
            }

        // 下面这些不走标准答案，直接断言业务含义，免得标准答案和实现一起想岔了
        var d = Details(t, fx.Id["sh1"]);
        ctx.Equal(2, d.HardLinkCount, "删掉计入的链接后，shared.dll 还剩 2 个活链接");
        ctx.Equal(t.GetPath(fx.Id["sh1"]), d.HardLinkCountedAt, "计入身份转给 System32 那份");
        ctx.True(d.Attributes.Contains("硬链接") && (d.F & (int)NodeFlags.HardLinkDup) == 0, "System32 那份现在是计入的链接，带硬链接标签");
        d = Details(t, fx.Id["sh2"]);
        ctx.True(d.HardLinkCount == 2 && d.HardLinkCountedAt == t.GetPath(fx.Id["sh1"]) && (d.F & (int)NodeFlags.HardLinkDup) != 0, "WinSxS 那份仍是副本，指向 System32");
        d = Details(t, fx.Id["twinA"]);
        ctx.True(d.HardLinkCount == 1 && !d.Attributes.Contains("硬链接"), "同组其他链接都删了，就不再算硬链接");
        d = Details(t, fx.Id["hlB"]);
        ctx.True(d.HardLinkCount == 2 && d.HardLinkCountedAt == t.GetPath(fx.Id["hlA"]) && d.Attributes.Contains("硬链接"), "硬链接对：WinSxS 的是副本，计入位置是 System32");
        ctx.True(d.CategoryBytes[FileCategories.Executable] == t.Value(fx.Id["hlB"], Metric.Alloc) && d.ExtStats.Length == 1 && d.ExtStats[0].Alloc == t.Alloc(fx.Id["hlB"]),
            "副本自己的详情照算它自己（不然副本的详情永远是空的）");

        d = Details(t, fx.Id["unacc"]);
        ctx.True(d.IsProtected && d.Warning == null && d.Attributes.SequenceEqual(["虚拟节点"]), "虚拟节点：受保护，没有去问磁盘的警告");
        ctx.True(d.CategoryBytes[FileCategories.Unaccounted] == 5 * GB && d.PurposeBytes![Purposes.Unexplained] == 5 * GB && d.ExtStats.Length == 0, "未统计只进类别和用途，不进扩展名");
        d = Details(t, 0);
        ctx.True(d.CategoryBytes.SequenceEqual(t.CategoryTotals(Metric.Alloc)), "根节点的类别分布 = FileTree.CategoryTotals");
        ctx.True(d.PurposeBytes!.SequenceEqual(t.PurposeTotals(Metric.Alloc)), "根节点的用途分布 = FileTree.PurposeTotals");
        ctx.Equal(t.Files(0), d.ExtStats.Sum(e => e.Count), "扩展名条目数之和 = 文件数");
        d = Details(t, fx.Id["zoo"]);
        ctx.True(d.ExtStats.Length == 16 && d.ExtStats[^1].Ext == "*" && d.ExtStats[..15].All(e => e.Ext != "*"), "40 种扩展名：前 15 + 「*」");
        ctx.True(d.ExtStats.Sum(e => e.Count) == t.Files(fx.Id["zoo"]) && d.ExtStats.Sum(e => e.Alloc) == t.Alloc(fx.Id["zoo"]) && d.ExtStats.Sum(e => e.Size) == t.Size(fx.Id["zoo"]),
            "「*」把 15 名之外的数字补齐，总数对得上");
        ctx.Equal(15, Details(t, fx.Id["ext15"]).ExtStats.Length, "恰好 15 种扩展名不合成「*」");
        var e16 = Details(t, fx.Id["ext16"]).ExtStats;
        ctx.True(e16.Length == 16 && e16[^1].Ext == "*" && e16[^1].Count == 3, "16 种扩展名：「*」恰好收下排名最后的一种");
        d = Details(t, fx.Id["deepBottom"]);
        ctx.True(d.Ancestors.Length == 302 && d.Ancestors[0].Id == 0 && d.Ancestors[^1].Name == "d299" && d.Ancestors.All(a => a.Id != d.Id), "祖先链：根在前、不含自己、深 300 层也不炸");
        d = Details(t, fx.Id["pagefile"]);
        ctx.True(d.K == FileCategories.System && d.Attributes.SequenceEqual(["隐藏", "系统"]), "pagefile.sys：系统类别，隐藏 + 系统属性");
        d = Details(t, fx.Id["cloud"]);
        ctx.True(d.Attributes.SequenceEqual(["仅云端"]) && d.Alloc == 0 && d.Size == 5 * MB, "仅云端文件：占用 0、大小 5 MB");

        // 属性标签的顺序与内容
        ctx.True(TreeQueries.AttributeLabels(t, fx.Id["sparse"]).SequenceEqual(["稀疏"]), "AttributeLabels：稀疏");
        ctx.True(TreeQueries.AttributeLabels(t, fx.Id["enc"]).SequenceEqual(["加密"]), "AttributeLabels：加密");
        ctx.True(TreeQueries.AttributeLabels(t, fx.Id["denied"]).SequenceEqual(["无法访问"]), "AttributeLabels：无法访问");
    }

    static NodeDetailsDto Details(FileTree t, int id, Metric m = Metric.Alloc) => TreeQueries.GetDetails(t, new DetailsRequest(id, m));

    static void VerifyDetails(CheckContext ctx, Oracle o, int id, Metric m, string label)
    {
        var t = o.T;
        var d = Details(t, id, m);
        var problems = new List<string>();
        void Check(bool ok, string what) { if (!ok) problems.Add(what); }

        Check(d.Id == id && d.Name == t.GetName(id) && d.Path == t.GetPath(id) && d.IsDir == t.IsDir(id), "身份字段");
        Check(d.K == t.Category(id) && d.F == (int)t.Flags(id) && d.U == t.Purpose(id) && d.TreeVersion == t.Version, "K/F/U/版本");
        Check(d.Role == t.Role(id) && d.Evidence == t.PurposeEvidence(id), "角色/用途证据");
        var owner = t.OwnerOf(id, out int ownerRoot);
        Check(d.Owner == (owner is null ? null : new OwnerDto(owner.Kind, owner.Name, ownerRoot, t.GetPath(ownerRoot), t.OwnerEvidenceAt(ownerRoot))), "所属对象");
        Check(d.Size == t.Size(id) && d.Alloc == t.Alloc(id), "大小");
        // 暴力数一遍文件数/目录数，顺带验树本身（详情把它们原样透传）
        var members = o.Members(id);
        long files = members.Count(i => !t.IsDir(i) && !t.Has(i, NodeFlags.Virtual));
        long dirs = members.Count(i => i != id && t.IsDir(i) && !t.Has(i, NodeFlags.Virtual));
        Check(d.Files == files && d.Dirs == dirs, $"文件/目录数 {d.Files}/{d.Dirs} ≠ 暴力计数 {files}/{dirs}");

        long value = t.Value(id, m);
        int parent = t.Parent(id);
        static double Pct(long a, long b) => b > 0 ? Math.Clamp((double)a / b, 0, 1) : 0;
        Check(Math.Abs(d.PercentOfParent - Pct(value, parent < 0 ? value : t.Value(parent, m))) < 1e-12, "占父目录比例");
        Check(Math.Abs(d.PercentOfRoot - Pct(value, t.Value(0, m))) < 1e-12, "占根比例");
        var chain = new List<int>();
        for (int x = parent; x >= 0; x = t.Parent(x)) chain.Add(x);
        chain.Reverse();
        Check(d.Ancestors.Select(a => a.Id).SequenceEqual(chain) && d.Ancestors.All(a => a.Name == t.GetName(a.Id)), "祖先链（根在前、不含自己）");

        var live = o.Live(id).ToArray();
        var top = live.OrderByDescending(c => o.Counted(c, m)).ThenBy(c => c).Take(12).ToArray();
        Check(d.TopChildren.Select(c => c.Id).SequenceEqual(top), "最大的 12 个子项");
        Check(d.TopChildren.SequenceEqual(top.Select(c => DtoFactory.ChildItem(t, c))), "子项字段");
        Check(d.ChildCount == live.Length, $"子项总数 {d.ChildCount} ≠ {live.Length}");

        var (cats, purs, exts) = o.Compose(id, m);
        Check(d.CategoryBytes.SequenceEqual(cats), $"类别字节 [{string.Join(',', d.CategoryBytes)}] ≠ [{string.Join(',', cats)}]");
        Check(d.PurposeBytes != null && d.PurposeBytes.SequenceEqual(purs), "用途字节");
        if (!d.ExtStats.SequenceEqual(exts))
        {
            int at = Enumerable.Range(0, Math.Max(d.ExtStats.Length, exts.Length)).First(i => i >= d.ExtStats.Length || i >= exts.Length || d.ExtStats[i] != exts[i]);
            problems.Add($"扩展名统计第 {at} 行：{(at < d.ExtStats.Length ? d.ExtStats[at].ToString() : "无")} ≠ {(at < exts.Length ? exts[at].ToString() : "无")}");
        }

        var prot = Protection.Evaluate(t, id);
        Check(d.IsProtected == prot.Blocked && d.ProtectReason == prot.Reason, "保护判定");
        bool offDisk = t.Has(id, NodeFlags.Virtual | NodeFlags.Metafile);
        Check(offDisk ? !(d.Warning ?? "").Contains("磁盘上") : (d.Warning ?? "").Contains("找不到"), $"磁盘警告：{d.Warning ?? "空"}");
        Check(d.ModifiedMs == DtoFactory.UnixMs(t.MTime(id)) && d.CreatedMs == null && d.AccessedMs == null, "读不到磁盘时时间字段回落到扫描值");

        var group = t.HardLinkGroup(id);
        int links = group?.Count(t.IsValid) ?? 1;
        Check(d.HardLinkCount == Math.Max(1, links), $"硬链接数 {d.HardLinkCount} ≠ {links}");
        if (group != null)
        {
            var counted = group.Where(g => t.IsValid(g) && !t.Has(g, NodeFlags.HardLinkDup)).ToArray();
            Check(counted.Length == 1, $"活着的链接里计入的应恰有 1 个，实际 {counted.Length}（树的硬链接身份转移出了问题）");
            Check(d.HardLinkCountedAt == (counted.Length > 0 ? t.GetPath(counted[0]) : null), $"计入位置：{d.HardLinkCountedAt}");
        }
        else Check(d.HardLinkCountedAt == null, "不是硬链接就不该有计入位置");

        var attrs = new List<string>();
        void Add(NodeFlags f, string s) { if (t.Has(id, f)) attrs.Add(s); }
        Add(NodeFlags.Hidden, "隐藏"); Add(NodeFlags.System, "系统"); Add(NodeFlags.Compressed, "压缩"); Add(NodeFlags.Sparse, "稀疏");
        Add(NodeFlags.Reparse, "重解析点"); Add(NodeFlags.Cloud, "仅云端"); Add(NodeFlags.Encrypted, "加密"); Add(NodeFlags.Inaccessible, "无法访问");
        Add(NodeFlags.Metafile, "NTFS 元数据"); Add(NodeFlags.Virtual, "虚拟节点");
        if (links > 1) attrs.Add("硬链接");
        Check(d.Attributes.SequenceEqual(attrs), $"属性标签 [{string.Join(',', d.Attributes)}] ≠ [{string.Join(',', attrs)}]");

        if (id == 0)
        {
            Check(d.CategoryBytes.SequenceEqual(t.CategoryTotals(m)), "根的类别分布应等于 CategoryTotals");
            Check(d.PurposeBytes!.SequenceEqual(t.PurposeTotals(m)), "根的用途分布应等于 PurposeTotals");
        }
        ctx.True(problems.Count == 0, $"{label}：{string.Join("；", problems)}");
    }

    /// 在沙箱里造真文件，验证实时属性；没造出来的（比如没权限建 junction）就跳过那一项。
    static void LiveDetails(CheckContext ctx, string sandbox)
    {
        string dir = Path.Combine(sandbox, "live");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "hello");
        var created = new DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var modified = new DateTime(2023, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetCreationTimeUtc(file, created);
        File.SetLastWriteTimeUtc(file, modified);
        File.SetLastAccessTimeUtc(file, new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        string target = Path.Combine(dir, "target");
        Directory.CreateDirectory(target);
        string junctionPath = Path.Combine(dir, "jct");
        bool junction = MakeJunction(junctionPath, target);

        var t = new FileTree(dir);
        int real = t.AddNode(0, "a.txt", 0, 5, 4096, 0);
        int gone = t.AddNode(0, "gone.txt", 0, 10, 4096, Stamp);
        int link = junction ? t.AddNode(0, "jct", NodeFlags.Directory | NodeFlags.Reparse, 0, 0, 0) : -1;
        t.Seal();

        static long Ms(DateTime d) => new DateTimeOffset(d).ToUnixTimeMilliseconds();
        var d1 = Details(t, real);
        ctx.Equal<long?>(Ms(modified), d1.ModifiedMs, "实时读到的修改时间");
        ctx.Equal<long?>(Ms(created), d1.CreatedMs, "实时读到的创建时间");
        ctx.True(d1.AccessedMs != null, "实时读到了访问时间");
        ctx.True(!(d1.Warning ?? "").Contains("找不到"), "文件在，就没有「找不到」的警告");
        ctx.True(d1.LinkTarget == null, "普通文件没有链接目标");

        var d2 = Details(t, gone);
        ctx.True((d2.Warning ?? "").Contains("找不到"), "文件已不在磁盘上：警告里要说清楚");
        ctx.True(d2.ModifiedMs == DtoFactory.UnixMs(Stamp) && d2.CreatedMs == null && d2.AccessedMs == null, "找不到时修改时间回落到扫描值，其余为空");

        if (junction)
        {
            var d3 = Details(t, link);
            ctx.True(d3.LinkTarget != null && d3.LinkTarget.EndsWith("target", StringComparison.OrdinalIgnoreCase), $"junction 的目标：{d3.LinkTarget}");
            ctx.True(d3.Attributes.Contains("重解析点") && d3.ModifiedMs != null, "junction 本身读得到时间，带重解析点标签");
        }
        else ctx.Info("没能在沙箱里建 junction，跳过 LinkTarget 一项");

        // 读盘卡住（掉线的网络盘之类）不能把详情一起拖死：超时就放弃，带着说明回落到扫描时的数据
        var clock = Stopwatch.StartNew();
        var slow = TreeQueries.ReadLiveBounded(file, 200, _ =>
        {
            Thread.Sleep(3000);
            return default;
        });
        ctx.True(clock.ElapsedMilliseconds < 2000 && (slow.Warning ?? "").Contains("超时") && slow.Modified == null, $"读盘超时应在上限附近放弃（用了 {clock.ElapsedMilliseconds} ms）");
        var boom = TreeQueries.ReadLiveBounded(file, 2000, _ => throw new InvalidOperationException("boom"));
        ctx.True((boom.Warning ?? "").Contains("boom"), "读盘抛出意料之外的异常也只变成警告，不拖垮详情");
        ctx.Equal<long?>(Ms(modified), TreeQueries.ReadLiveBounded(file).Modified, "正常读盘不受超时包装影响");
        if (junction) RemoveJunction(junctionPath);
    }

    /// junction 让 .NET 的递归删除和杀软都犯过怵，自己先摘掉链接本身（非递归只删链接，不碰目标），顶不住就重试几次。
    static void RemoveJunction(string path)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: false);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }

    static bool MakeJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(10_000) && p.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    // ======================= tree.largest =======================

    static void LargestSuite(CheckContext ctx, Fixture fx)
    {
        var o = fx.Oracle;
        var t = o.T;
        string[] keys = ["", "users", "flat", "windows", "sys32", "sxs", "meta", "zoo", "nm", "deepBottom", "emptyIdx", "unacc", "hlB"];
        foreach (var metric in Both)
            foreach (var key in keys)
            {
                int root = key.Length == 0 ? 0 : fx.Id[key];
                var eligible = o.Members(root).Where(i => !t.IsDir(i) && !t.Has(i, NodeFlags.Virtual | NodeFlags.HardLinkDup))
                    .OrderByDescending(i => t.Value(i, metric)).ThenBy(i => i).ToArray();
                foreach (int limit in new[] { -5, 0, 1, 7, 100, 700, 1000, 5000 }) // 700 让 zoo 的截断线正好切在一大片并列的稀疏文件中间
                {
                    var got = TreeQueries.LargestFiles(t, new LargestRequest(root, metric, limit));
                    var expected = eligible.Take(Math.Clamp(limit, 1, 1000)).ToArray();
                    string label = $"大文件[{(key.Length == 0 ? "根" : key)}/{metric}/{limit}]";
                    ctx.True(got.Select(g => g.Id).SequenceEqual(expected), $"{label}：{got.Length} 项，应为 {expected.Length} 项且顺序一致");
                    ctx.True(got.SequenceEqual(expected.Select(i => DtoFactory.FileItem(t, i))), $"{label}：条目字段");
                }
            }
        var all = TreeQueries.LargestFiles(t, new LargestRequest(0, Metric.Alloc, 1000));
        ctx.True(all.All(i => !i.IsDir && (i.F & (int)(NodeFlags.Virtual | NodeFlags.HardLinkDup | NodeFlags.Deleted)) == 0), "大文件里没有目录、虚拟节点、硬链接副本、已删除节点");
        ctx.True(!all.Any(i => i.Id == fx.Id["unacc"] || i.Id == fx.Id["hlB"] || i.Id == fx.Id["sh2"]), "未统计、副本都不在榜上");
        ctx.True(all.Any(i => i.Id == fx.Id["mft"]), "NTFS 元数据文件是真实占用，照常上榜");
        ctx.True(TreeQueries.LargestFiles(t, new LargestRequest(fx.Id["windows"], Metric.Alloc, 1000)).Any(i => i.Id == fx.Id["sh1"]), "转移后计入的链接上榜");
    }

    // ======================= tree.children =======================

    /// 标准答案只认 Oracle 的子项（按父指针重建，不走 LiveChildren 那段共用切片），三种排序各自全排后逐页对。
    static void ChildrenSuite(CheckContext ctx, Fixture fx)
    {
        var o = fx.Oracle;
        var t = o.T;
        const int Page = 37;
        string[] keys = ["", "users", "flat", "windows", "sys32", "zoo", "nm", "emptyIdx", "hlB"];
        foreach (var metric in Both)
            foreach (var sort in Enum.GetValues<ChildSort>())
                foreach (var key in keys)
                {
                    int dir = key.Length == 0 ? 0 : fx.Id[key];
                    var live = o.Live(dir).ToArray();
                    var expected = (sort switch
                    {
                        ChildSort.Name => live.OrderByDescending(t.IsDir).ThenBy(t.GetName, StringComparer.OrdinalIgnoreCase),
                        ChildSort.Modified => live.OrderByDescending(t.MTime),
                        _ => live.OrderByDescending(c => o.Counted(c, metric)),
                    }).ThenBy(c => c).ToArray();
                    string label = $"子项[{(key.Length == 0 ? "根" : key)}/{metric}/{sort}]";
                    var all = new List<ChildEntryDto>();
                    for (int offset = 0; ; offset += Page)
                    {
                        var page = TreeQueries.Children(t, new ChildrenRequest(dir, metric, sort, offset, Page));
                        ctx.True(page.Total == live.Length && page.Offset == Math.Min(offset, live.Length), $"{label}：总数 {page.Total}、起点 {page.Offset}（请求 {offset}）");
                        all.AddRange(page.Items);
                        if (page.Items.Length < Page) break;
                    }
                    ctx.True(all.Select(e => e.Id).SequenceEqual(expected), $"{label}：分页拼起来 {all.Count} 项，应为 {expected.Length} 项且顺序一致");
                    ctx.True(all.All(e => e.Value == o.Counted(e.Id, metric) && e.ChildCount == (t.IsDir(e.Id) ? o.Live(e.Id).Count() : 0)
                        && e.Name == t.GetName(e.Id) && e.ModifiedMs == DtoFactory.UnixMs(t.MTime(e.Id))),
                        $"{label}：计入值和地图同口径（副本计 0），子项数只数未删除的，名字和修改时间照抄");
                }
        int flat = fx.Id["flat"], count = o.Live(flat).Count();
        ctx.True(TreeQueries.Children(t, new ChildrenRequest(flat, Limit: -5)).Items.Length == Math.Min(1, count), "Limit 夹到 1");
        ctx.True(TreeQueries.Children(t, new ChildrenRequest(flat, Limit: 5000)).Items.Length == Math.Min(500, count), "Limit 夹到 500");
        var past = TreeQueries.Children(t, new ChildrenRequest(flat, Offset: int.MaxValue));
        ctx.True(past.Items.Length == 0 && past.Offset == past.Total, "起点超出总数：空页，起点夹到总数");
        foreach (int bad in new[] { -1, t.Count, fx.Deleted[0] })
            Throws<ArgumentException>(ctx, () => TreeQueries.Children(t, new ChildrenRequest(bad)), $"Children({bad})：节点不存在或已删除");
        ctx.True(fx.Deleted.All(d => !TreeQueries.Children(t, new ChildrenRequest(t.Parent(d), Limit: 500)).Items.Any(e => e.Id == d)), "已删除的子树不出现在父目录的列表里");
        ctx.Info($"目录列表：flat 共 {count:N0} 个子项，分页每页 {Page} 项三种排序全对");
    }

    // ======================= tree.recent =======================

    static long FileTimeOfMs(long unixMs) => unixMs * 10_000 + DtoFactory.UnixEpochFileTime;

    /// 单独造一棵 X:\ 小树：修改时间要逐个卡边界，还得跑一遍分类器才有所属对象可看。
    static void RecentSuite(CheckContext ctx)
    {
        const long asOfMs = 1_760_000_000_000L; // 扫描完成时刻
        long asOf = FileTimeOfMs(asOfMs);
        var t = new FileTree(@"X:\");
        var id = new Dictionary<string, int>();
        var rnd = new Random(20261020);
        int Dir(int parent, string name) => id[name] = t.AddNode(parent, name, NodeFlags.Directory, 0, 4096, asOf - Day / 2); // 目录自己刚改过也不该上榜
        int Fil(int parent, string name, long size, long mtime, long alloc = -1, NodeFlags flags = 0) =>
            id[name] = t.AddNode(parent, name, flags, size, alloc >= 0 ? alloc : Cluster(size), mtime);

        int sys32 = Dir(Dir(0, "Windows"), "System32");
        for (int i = 0; i < 300; i++) // 大小只有 20 档，成片并列；时间散在 10 天里
            Fil(sys32, $"k{i:D3}.dll", rnd.NextInt64(1, 21) * 100 * KB, asOf - rnd.NextInt64(10 * Day));
        int docs = Dir(Dir(Dir(0, "Users"), "Alex"), "Documents");
        // 窗口按毫秒比、两头都闭：恰好 N 天前的算；再早 1 刻（100 ns）就落进上一毫秒，不算
        Fil(docs, "at7.txt", 2 * MB, FileTimeOfMs(asOfMs - 7 * DayMs));
        Fil(docs, "out7.txt", 2 * MB, FileTimeOfMs(asOfMs - 7 * DayMs) - 1);
        Fil(docs, "at1.txt", 2 * MB, FileTimeOfMs(asOfMs - DayMs));
        Fil(docs, "out1.txt", 2 * MB, FileTimeOfMs(asOfMs - DayMs) - 1);
        Fil(docs, "base.txt", 2 * MB, asOf + 9_999); // 和基准同一毫秒的最后一刻：算
        Fil(docs, "future.txt", 2 * MB, asOf + 10_000); // 晚一毫秒：时钟不准留下的未来时间，不算
        Fil(docs, "unknown.txt", 9 * GB, 0);
        Fil(docs, "tieA.bin", 3 * MB, asOf - Day); // 大小、时间都和 tieB 并列
        Fil(docs, "tieB.bin", 3 * MB, asOf - Day);
        Fil(docs, "sparse.vhdx", 40 * GB, asOf - 2 * Day, 1 * MB, NodeFlags.Sparse); // 两种度量下排名天差地别
        int hlA = Fil(sys32, "hl.dll", 5 * MB, asOf - Day / 3), hlB = Fil(docs, "hl-link.dll", 5 * MB, asOf - Day / 3);
        int counted = t.ResolveHardLinkGroup([hlA, hlB]), dup = counted == hlA ? hlB : hlA;
        Fil(0, "root.txt", 1 * KB, asOf - Day / 4);
        t.AddNode(0, "未统计", NodeFlags.Virtual, 5 * GB, 5 * GB, asOf - Day / 2); // 虚拟节点带个新时间也不能上榜
        int trash = Dir(0, "Trash");
        for (int i = 0; i < 20; i++) Fil(trash, $"t{i}.log", 50 * MB, asOf - Day / 5);
        Fil(docs, "gone.bin", 50 * GB, asOf - Day / 5);
        t.Seal();
        t.SetClassification(PurposeClassifier.Classify(t, PurposeChecks.FakeEnv(), CancellationToken.None));
        t.MarkDeleted(trash);
        t.MarkDeleted(id["gone.bin"]);
        var o = new Oracle(t, independent: true);

        // 标准答案：按毫秒比窗口，和实现里换算成 FILETIME 刻度的写法各算各的
        long Ms(int i) => (t.MTime(i) - DtoFactory.UnixEpochFileTime) / 10_000;
        int[] Expect(int scope, int days, RecentSort sort, Metric m) => o.Members(scope)
            .Where(i => !t.IsDir(i) && !t.Has(i, NodeFlags.Virtual | NodeFlags.HardLinkDup) && Ms(i) >= asOfMs - days * DayMs && Ms(i) <= asOfMs)
            .OrderByDescending(i => sort == RecentSort.Size ? t.Value(i, m) : t.MTime(i)).ThenBy(i => i).ToArray();
        RecentFilesDto Q(int days, RecentSort sort = RecentSort.Size, int? scope = null, Metric m = Metric.Alloc, int limit = 1000) =>
            TreeQueries.RecentFiles(t, new RecentRequest(days, scope, sort, m, limit), asOfMs);

        int[] scopes = [FileTree.RootId, id["Windows"], sys32, docs, dup, id["at7.txt"]];
        foreach (int scope in scopes)
            foreach (int days in new[] { 1, 7, 30 })
                foreach (var sort in new[] { RecentSort.Size, RecentSort.Modified })
                    foreach (var metric in Both)
                    {
                        var expected = Expect(scope, days, sort, metric);
                        foreach (int limit in new[] { -5, 0, 1, 7, 1000, 5000 })
                        {
                            var r = Q(days, sort, scope == FileTree.RootId ? null : scope, metric, limit);
                            var want = expected.Take(Math.Clamp(limit, 1, 1000)).ToArray();
                            ctx.True(r.Items.Select(x => x.Id).SequenceEqual(want) && r.Total == expected.Length,
                                $"最近文件[{t.GetName(scope)}/{days} 天/{sort}/{metric}/{limit}]：{r.Items.Length}/{r.Total} 项，应为 {want.Length}/{expected.Length} 项且顺序一致");
                        }
                    }

        bool Has(RecentFilesDto r, int node) => r.Items.Any(x => x.Id == node);
        int IndexOf(RecentFilesDto r, string name) => Array.FindIndex(r.Items, x => x.Id == id[name]);
        RecentFilesDto week = Q(7), day = Q(1), all = Q(3650);
        ctx.True(Has(week, id["at7.txt"]) && !Has(week, id["out7.txt"]) && Has(week, id["out1.txt"]), "7 天：恰好 7 天前改的算，再早 100 ns 不算");
        ctx.True(Has(day, id["at1.txt"]) && !Has(day, id["out1.txt"]) && !Has(day, id["at7.txt"]), "1 天：恰好 1 天前改的算，再早 100 ns 不算");
        ctx.True(Has(day, id["base.txt"]) && !Has(all, id["future.txt"]) && !Has(all, id["unknown.txt"]), "和基准同一毫秒的算；晚于基准、修改时间未知的不算");
        ctx.True(week.AsOfMs == asOfMs && week.Days == 7 && Q(0).Days == 1 && Q(-3).Days == 1 && Q(99_999).Days == 3650, "结果带基准时刻和夹过的天数");
        var again = Q(7);
        ctx.True(again.Total == week.Total && again.Items.SequenceEqual(week.Items), "同一份结果再查一次完全一样");

        RecentFilesDto bySize = Q(30, m: Metric.Size), byAlloc = Q(30), byTime = Q(30, RecentSort.Modified);
        ctx.True(bySize.Items[0].Id == id["sparse.vhdx"] && byAlloc.Items[0].Id != id["sparse.vhdx"], "按大小跟着度量走：稀疏文件内容大小第一、实际占用排不上第一");
        ctx.True(byTime.Items[0].Id == id["base.txt"] && byTime.Items.Zip(byTime.Items.Skip(1)).All(p => p.First.ModifiedMs >= p.Second.ModifiedMs), "按修改时间：新的在前");
        ctx.True(IndexOf(bySize, "tieB.bin") == IndexOf(bySize, "tieA.bin") + 1 && IndexOf(byTime, "tieB.bin") == IndexOf(byTime, "tieA.bin") + 1, "并列按 id 升序");

        var inDocs = Q(30, scope: docs);
        ctx.True(inDocs.Items.Length > 0 && inDocs.Items.All(x => x.DirPath == @"X:\Users\Alex\Documents"), "作用域：只出 Documents 里的");
        Throws<ArgumentException>(ctx, () => Q(7, scope: trash), "作用域是已删除的目录");
        Throws<ArgumentException>(ctx, () => Q(7, scope: 1_000_000), "作用域越界");
        Throws<ArgumentException>(ctx, () => TreeQueries.RecentFiles(t, new RecentRequest(), 0), "基准时刻为 0");
        ctx.True(Q(30, limit: 0).Items.Length == 1 && Q(30, limit: 5).Items.Length == 5 && Q(30, limit: 5).Total == byAlloc.Total, "上限夹到 1..1000，总数不受上限影响");

        ctx.True(all.Items.All(x => !t.IsDir(x.Id) && !t.Has(x.Id, NodeFlags.Virtual | NodeFlags.HardLinkDup | NodeFlags.Deleted)), "没有目录、虚拟节点、硬链接副本、已删除节点");
        ctx.True(!all.Items.Any(x => x.Path.StartsWith(@"X:\Trash\")) && !Has(all, id["gone.bin"]), "删掉的文件、删掉的目录里的文件都不出");
        ctx.True(Has(all, counted) && !Has(all, dup), "硬链接两份都在窗口里，只出计入的那一份");
        RecentFileDto Want(int i) => new(i, t.GetName(i), t.GetPath(i), t.GetPath(t.Parent(i)), t.Size(i), t.Alloc(i), Ms(i),
            t.Category(i), (int)t.Flags(i), t.Purpose(i), t.Role(i), DtoFactory.Owner(t, i));
        ctx.True(all.Items.All(x => x == Want(x.Id)), "条目字段和树上一致");
        var k0 = all.Items.Single(x => x.Id == id["k000.dll"]);
        ctx.True(k0.DirPath == @"X:\Windows\System32" && k0.Owner is { Kind: OwnerKind.System, Name: "Windows", RootPath: @"X:\Windows", Evidence: Evidence.Confirmed },
            $"System32 里的文件归 Windows（已确认）：{k0.Owner}");
        ctx.True(all.Items.Single(x => x.Id == id["root.txt"]).DirPath == @"X:\", "根下文件的所在目录是卷根");
        ctx.True(all.Items.Any(x => x.Owner is null), "认不出所属对象的条目 Owner 为 null");
    }

    static void OwnerSuite(CheckContext ctx)
    {
        var t = new FileTree(@"X:\");
        // 目录自己也占地方（大目录的索引）：对象的计入大小和地图上目录的大小同口径，这部分不能漏
        int Dir(int parent, string name) => t.AddNode(parent, name, NodeFlags.Directory, 0, 4096, Stamp);
        int Fil(int parent, string name, long size, long alloc = -1, NodeFlags flags = 0) =>
            t.AddNode(parent, name, flags, size, alloc >= 0 ? alloc : Cluster(size), Stamp);

        int windows = Dir(0, "Windows"), sys32 = Dir(windows, "System32");
        for (int i = 0; i < 10; i++) Fil(sys32, $"k{i}.dll", (i + 1) * 300 * KB);
        int pagefile = Fil(0, "pagefile.sys", 1 * GB); // 卷根的系统文件：Windows 的第二处根，而且根是个文件
        int steam = Dir(Dir(0, "Program Files (x86)"), "Steam");
        Fil(steam, "steam.exe", 7 * MB);
        int steamapps = Dir(steam, "steamapps"), portal = Dir(Dir(steamapps, "common"), "Portal 2"); // 嵌在 Steam 里的游戏
        Fil(portal, "portal2.exe", 20 * MB);
        int bin = Dir(portal, "bin");
        Fil(bin, "engine.dll", 9 * MB);
        Fil(bin, "tier0.dll", 3 * MB);
        Fil(Dir(Dir(steamapps, "shadercache"), "620"), "x.foz", 5 * MB);
        int alex = Dir(Dir(0, "Users"), "Alex");
        int localSteam = Dir(Dir(Dir(alex, "AppData"), "Local"), "Steam"); // 按名字并到同一个 Steam：第二处根
        Fil(Dir(localSteam, "htmlcache"), "c.bin", 4 * MB);
        int proj = Dir(Dir(alex, "Desktop"), "proj");
        Fil(proj, "package.json", 1 * KB);
        Fil(Dir(proj, "src"), "main.rs", 30 * KB);
        Fil(proj, "disk.vhdx", 40 * GB, 2 * MB, NodeFlags.Sparse); // 两种度量下排名天差地别
        int misc = Dir(0, "Misc");
        Fil(misc, "a.bin", 6 * MB);
        // 硬链接：计入的是完整路径最小的那个链接
        int shP = Fil(portal, "shared.dll", 8 * MB), shW = Fil(sys32, "shared.dll", 8 * MB); // Portal 2 和 Windows 各一个 → 共享
        int sameP = Fil(steam, "same.bin", 2 * MB), sameL = Fil(localSteam, "same.bin", 2 * MB); // 同一对象的两处根 → 只算一份
        int libM = Fil(misc, "lib.bin", 3 * MB), libP = Fil(proj, "lib.bin", 3 * MB); // 一边不归任何对象 → 也是共享
        int u1 = Fil(misc, "u1.bin", 1 * MB), u2 = Fil(Dir(misc, "sub"), "u2.bin", 1 * MB); // 两边都不归任何对象 → 未归属
        int old = Dir(portal, "old");
        int oldP = Fil(old, "legacy.dll", 4 * MB), oldW = Fil(windows, "legacy.dll", 4 * MB); // Portal 2 那个连目录删掉后只剩 Windows 的
        int[][] groups = [[shP, shW], [sameP, sameL], [libM, libP], [u1, u2], [oldP, oldW]];
        foreach (var g in groups) t.ResolveHardLinkGroup(g);
        int virt = t.AddNode(0, "未统计", NodeFlags.Virtual, 5 * GB, 5 * GB, Stamp);
        int junk = Dir(steam, "junk");
        Fil(junk, "t.bin", 50 * MB);
        t.Seal();
        t.SetClassification(PurposeClassifier.Classify(t, PurposeChecks.FakeEnv(), CancellationToken.None));
        t.MarkDeleted(old);
        t.MarkDeleted(junk);
        var o = new Oracle(t, independent: true);

        Owner? OwnerOf(int i) => t.OwnerOf(i, out _);
        ctx.True(OwnerOf(steam) is { Kind: OwnerKind.App, Name: "Steam" } && ReferenceEquals(OwnerOf(localSteam), OwnerOf(steam))
            && OwnerOf(portal) is { Kind: OwnerKind.Game, Name: "Portal 2" } && OwnerOf(pagefile) is { Name: "Windows" } && OwnerOf(sys32) is { Name: "Windows" }
            && OwnerOf(proj) is { Kind: OwnerKind.Project, Name: "proj" } && OwnerOf(misc) is null && OwnerOf(u2) is null && OwnerOf(virt) is null
            && t.Has(shW, NodeFlags.HardLinkDup) && t.Has(sameL, NodeFlags.HardLinkDup) && t.Has(libP, NodeFlags.HardLinkDup) && !t.Has(oldW, NodeFlags.HardLinkDup),
            "夹具前提：Steam 两处根并成一个、Portal 2 嵌在里面、Windows 有个文件根、Misc 不归任何对象、计入的链接是预想的那几个");

        // 标准答案（笨办法）：每个节点自己往上找最近的根；自身占用 = 计入值 − 活着的子项的计入值；硬链接组看所有活着的链接归谁
        long Own(int i, Metric m) => o.Counted(i, m) - o.Live(i).Sum(c => o.Counted(c, m));
        bool Split(int i) => !t.Has(i, NodeFlags.HardLinkDup) && t.HardLinkGroup(i) is { } g
            && g.Any(l => t.IsValid(l) && !ReferenceEquals(OwnerOf(l), OwnerOf(i)));
        OwnerUsageDto Q(int scope, Metric m = Metric.Alloc, int limit = 1000) =>
            TreeQueries.OwnerUsage(t, new OwnerUsageRequest(scope == FileTree.RootId ? null : scope, m, limit));
        string? Diff(int scope, Metric m)
        {
            var r = Q(scope, m);
            var owners = new Dictionary<Owner, long[]>(); // 内容大小、实际占用、各角色（按 m）
            var roots = new Dictionary<int, long[]>();
            long[] shared = new long[2], unowned = new long[2];
            foreach (int i in o.Members(scope))
            {
                var owner = t.OwnerOf(i, out int root);
                if (owner != null)
                {
                    owners.TryAdd(owner, new long[2 + Roles.Count]); // 碰到范围的对象和根，计入为 0 也要列出来
                    roots.TryAdd(root, new long[2]);
                }
                long s = Own(i, Metric.Size), a = Own(i, Metric.Alloc);
                if (Split(i)) { shared[0] += s; shared[1] += a; continue; }
                if (owner == null || !t.IsDir(i) && t.Has(i, NodeFlags.Virtual)) { unowned[0] += s; unowned[1] += a; continue; }
                var acc = owners[owner];
                acc[0] += s;
                acc[1] += a;
                acc[2 + t.Role(i)] += m == Metric.Alloc ? a : s;
                roots[root][0] += s;
                roots[root][1] += a;
            }
            int k = m == Metric.Alloc ? 1 : 0;
            var order = owners.OrderByDescending(x => x.Value[k]).ThenBy(x => x.Key.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key.Kind).ToArray();
            if (r.OwnerCount != order.Length || r.Items.Length != order.Length) return $"对象个数 {r.OwnerCount}/{r.Items.Length}，应为 {order.Length}";
            for (int n = 0; n < order.Length; n++)
            {
                var (want, acc) = (order[n].Key, order[n].Value);
                var got = r.Items[n];
                if (got.Kind != want.Kind || got.Name != want.Name || got.Evidence != want.Evidence) return $"第 {n} 个对象是 {got.Name}，应为 {want.Name}";
                if (got.SizeInScope != acc[0] || got.AllocInScope != acc[1]) return $"{want.Name} 计入 {got.SizeInScope}/{got.AllocInScope}，应为 {acc[0]}/{acc[1]}";
                if (!got.RoleBytesInScope.SequenceEqual(acc.Skip(2))) return $"{want.Name} 的角色构成不对";
                var wantRoots = roots.Where(x => ReferenceEquals(OwnerOf(x.Key), want)).OrderByDescending(x => x.Value[k]).ThenBy(x => x.Key)
                    .Select(x => new OwnerUsageRootDto(x.Key, t.GetPath(x.Key), x.Value[0], x.Value[1], t.OwnerEvidenceAt(x.Key)));
                if (!got.Roots.SequenceEqual(wantRoots)) return $"{want.Name} 的根 [{string.Join(", ", got.Roots.Select(x => x.Path))}] 不对";
            }
            if (r.SharedSizeInScope != shared[0] || r.SharedAllocInScope != shared[1]) return $"共享 {r.SharedSizeInScope}/{r.SharedAllocInScope}，应为 {shared[0]}/{shared[1]}";
            if (r.UnownedSizeInScope != unowned[0] || r.UnownedAllocInScope != unowned[1]) return $"未归属 {r.UnownedSizeInScope}/{r.UnownedAllocInScope}，应为 {unowned[0]}/{unowned[1]}";
            if (r.TotalSizeInScope != o.Counted(scope, Metric.Size) || r.TotalAllocInScope != o.Counted(scope, Metric.Alloc)) return "范围总量和作用域的计入值对不上";
            if (r.OwnedSizeInScope != r.Items.Sum(x => x.SizeInScope) || r.OwnedAllocInScope != r.Items.Sum(x => x.AllocInScope)) return "对象之和不对";
            if (r.OwnedSizeInScope + r.SharedSizeInScope + r.UnownedSizeInScope != r.TotalSizeInScope
                || r.OwnedAllocInScope + r.SharedAllocInScope + r.UnownedAllocInScope != r.TotalAllocInScope) return "对象 + 共享 + 未归属 ≠ 范围总量";
            return r.ScopeId == scope ? null : "ScopeId 没带回来";
        }

        int[] scopes = [FileTree.RootId, windows, sys32, pagefile, steam, steamapps, portal, bin, localSteam, proj, misc, shP, shW, libP, virt, t.Parent(steam), alex];
        foreach (int scope in scopes)
            foreach (var m in Both)
            {
                string? err = Diff(scope, m);
                ctx.True(err == null, $"按所属对象[{t.GetName(scope)}/{m}]与逐节点的笨办法一致{(err == null ? "" : "：" + err)}");
            }

        long V(int i, Metric m) => t.Value(i, m);
        OwnerUsageItemDto Item(OwnerUsageDto r, string name) => r.Items.Single(x => x.Name == name);
        foreach (var m in Both)
        {
            var all = Q(FileTree.RootId, m);
            OwnerUsageItemDto st = Item(all, "Steam"), p2 = Item(all, "Portal 2"), win = Item(all, "Windows");
            long Pick(OwnerUsageItemDto x) => m == Metric.Alloc ? x.AllocInScope : x.SizeInScope;
            long PickRoot(OwnerUsageRootDto x) => m == Metric.Alloc ? x.AllocInScope : x.SizeInScope;
            ctx.Equal(V(steam, m) - V(portal, m) + V(localSteam, m), Pick(st), $"嵌套对象扣除[{m}]：Steam = 程序目录那处 − 嵌在里面的 Portal 2 + AppData 那处");
            ctx.True(st.Roots.Select(x => x.Id).SequenceEqual([steam, localSteam]) && PickRoot(st.Roots[0]) == V(steam, m) - V(portal, m)
                && PickRoot(st.Roots[1]) == V(localSteam, m) && st.Roots.All(x => x.Evidence == Evidence.Inferred),
                $"Steam 的两处根[{m}]：同一对象两处之间的硬链接只算一份，记在计入的那处；两处都是推测");
            ctx.Equal(V(portal, m) - V(shP, m), Pick(p2), $"Portal 2[{m}]：去掉和 Windows 共享的 shared.dll");
            ctx.True(Pick(win) == V(windows, m) + V(pagefile, m) && win.Roots.Select(x => x.Id).SequenceEqual([pagefile, windows]),
                $"Windows[{m}]：目录和卷根的 pagefile.sys 两处；Portal 2 那个链接删掉后 legacy.dll 归 Windows，不再算共享");
            ctx.Equal(V(shP, m) + V(libM, m), m == Metric.Alloc ? all.SharedAllocInScope : all.SharedSizeInScope,
                $"共享容量单列[{m}]：Portal 2 / Windows 的 shared.dll + 项目 / 不归任何对象的 lib.bin");
            ctx.True(all.Items.All(x => x.RoleBytesInScope.Length == Roles.Count && x.RoleBytesInScope.Sum() == Pick(x) && x.RoleBytesInScope.All(b => b >= 0)),
                $"角色构成[{m}]：每个对象各角色之和 = 计入大小，没有负数");
        }
        var top = Q(FileTree.RootId);
        ctx.True(Q(FileTree.RootId, Metric.Size).Items[0].Name == "proj" && top.Items[0].Name == "Windows", "排序跟着度量走：稀疏的 vhdx 让 proj 按内容大小第一、按实际占用排不上第一");
        ctx.True(top.UnownedAllocInScope >= 5 * GB && Q(virt).UnownedAllocInScope == 5 * GB && Q(virt).Items.Length == 0, "「未统计」虚拟叶子算未归属");
        var inMisc = Q(misc);
        ctx.True(inMisc.Items.Length == 0 && inMisc.SharedAllocInScope == V(libM, Metric.Alloc) && inMisc.UnownedAllocInScope == V(misc, Metric.Alloc) - V(libM, Metric.Alloc),
            "Misc：两边都不归任何对象的硬链接算未归属，和项目共用的 lib.bin 算共享");
        var inWin = Q(windows);
        ctx.True(inWin.Items.Length == 1 && inWin.SharedAllocInScope == 0 && inWin.TotalAllocInScope == V(windows, Metric.Alloc) && inWin.Items[0].Roots.Select(x => x.Id).SequenceEqual([windows]),
            "作用域 Windows：只有副本在范围里的 shared.dll 不计，也不算共享；pagefile.sys 那处根不在范围里就不列");
        var inSteam = Q(steam);
        ctx.True(inSteam.Items.Select(x => x.Name).Order().SequenceEqual(["Portal 2", "Steam"]) && Item(inSteam, "Steam").Roots.Select(x => x.Id).SequenceEqual([steam])
            && inSteam.SharedAllocInScope == V(shP, Metric.Alloc), "作用域 Steam：AppData 那处不列；shared.dll 另一个链接在范围外也照样算共享");
        var inBin = Q(bin);
        ctx.True(inBin.Items.Length == 1 && inBin.Items[0].Name == "Portal 2" && inBin.Items[0].Roots.Single().Id == portal
            && inBin.Items[0].AllocInScope == V(bin, Metric.Alloc) && inBin.Items[0].Roots[0].AllocInScope == V(bin, Metric.Alloc),
            "作用域整个落在 Portal 2 里：根是上方的 Portal 2，字节只算作用域里的");
        ctx.True(Q(FileTree.RootId, limit: 1).Items.Length == 1 && Q(FileTree.RootId, limit: 0).Items.Length == 1 && Q(FileTree.RootId, limit: -5).Items.Length == 1
            && Q(FileTree.RootId, limit: 2).Items.Select(x => x.Name).SequenceEqual(top.Items.Take(2).Select(x => x.Name))
            && Q(FileTree.RootId, limit: 1).OwnerCount == top.OwnerCount && Q(FileTree.RootId, limit: 1).OwnedAllocInScope == top.OwnedAllocInScope
            && Q(FileTree.RootId, limit: 5000).Items.Length == top.OwnerCount, "上限夹到 1..1000，对象个数和对象之和不受上限影响");
        Throws<ArgumentException>(ctx, () => Q(junk), "作用域是已删除的目录");
        Throws<ArgumentException>(ctx, () => Q(1_000_000), "作用域越界");
    }

    // ======================= 关联详情 =======================

    const string NoRelation = "尚未识别关联对象";

    /// 标准答案（笨办法）：每个节点自己往上找最近的根，再照 RelatedDto 的口径筛、分组、排序。不碰 OwnerRoots 字典，也不碰子节点数组。
    static RelatedDto BruteRelated(FileTree t, int node, Metric m, int limit)
    {
        var owner = t.OwnerOf(node, out int root);
        if (owner == null) return new RelatedDto(node, null, 0, [], NoRelation);
        var ev = t.OwnerEvidenceAt(root);
        int RootOf(int x) { t.OwnerOf(x, out int r); return r; }
        RelatedItemDto Dto(int x, RelationKind k, Evidence e) => new(x, t.GetName(x), t.GetPath(x), t.IsDir(x), t.Size(x), t.Alloc(x), t.Role(x), k, e);
        var list = new List<RelatedItemDto>();
        if (root != node) list.Add(Dto(root, RelationKind.Root, ev));
        list.AddRange(Enumerable.Range(0, t.Count).Where(x => x != root && t.IsValid(x) && RootOf(x) == x && ReferenceEquals(t.OwnerOf(x, out _), owner))
            .OrderByDescending(x => t.Value(x, m)).ThenBy(x => x)
            .Select(x => Dto(x, RelationKind.OtherRoot, t.OwnerEvidenceAt(x) < ev ? t.OwnerEvidenceAt(x) : ev)));
        var modules = Enumerable.Range(1, t.Count - 1).Where(x => t.IsValid(x) && t.IsDir(x) && !t.Has(x, NodeFlags.Virtual) && t.Role(x) != Roles.Unknown
            && t.Role(x) != t.Role(t.Parent(x)) && x != root && RootOf(x) == root && !t.IsAncestorOrSelf(x, node)).ToArray();
        int total = list.Count + modules.Length;
        list.AddRange(modules.GroupBy(x => t.Role(x)).Select(g => g.OrderByDescending(x => t.Value(x, m)).ThenBy(x => x).Take(3).ToArray())
            .OrderByDescending(g => t.Value(g[0], m)).ThenBy(g => t.Role(g[0])).SelectMany(g => g).Select(x => Dto(x, RelationKind.RoleModule, ev)));
        var items = list.Take(Math.Clamp(limit, 1, 1000)).ToArray();
        return new RelatedDto(node, new OwnerDto(owner.Kind, owner.Name, root, t.GetPath(root), ev), total, items, items.Length == 0 ? NoRelation : null);
    }

    static string? DiffRelated(RelatedDto got, RelatedDto want)
    {
        static string Show(RelatedDto r) => string.Join(", ", r.Items.Select(x => $"{x.Name}/{x.Relation}/{x.Evidence}"));
        if (got.NodeId != want.NodeId || got.Owner != want.Owner) return $"所属对象 {got.Owner?.Name ?? "无"}，应为 {want.Owner?.Name ?? "无"}";
        if (got.Total != want.Total) return $"总数 {got.Total}，应为 {want.Total}";
        if (!got.Items.SequenceEqual(want.Items)) return $"条目 [{Show(got)}]，应为 [{Show(want)}]";
        return got.Note == want.Note ? null : $"说明「{got.Note}」，应为「{want.Note}」";
    }

    static void RelatedSuite(CheckContext ctx)
    {
        // 手造分类：角色、所属对象、证据全由夹具说了算，不跟着分类规则的改动漂
        var t = new FileTree(@"X:\");
        var roleOf = new Dictionary<int, byte>();
        int Add(int parent, string name, long size, byte? role, bool dir)
        {
            int n = t.AddNode(parent, name, dir ? NodeFlags.Directory : 0, size, dir ? 4096 : Cluster(size), Stamp);
            if (role is { } r) roleOf[n] = r;
            return n;
        }
        int Dir(int parent, string name, byte? role = null) => Add(parent, name, 0, role, true); // 不给角色就跟上级
        int Fil(int parent, string name, long size, byte? role = null) => Add(parent, name, size, role, false);

        int app = Dir(Dir(0, "Program Files"), "App", Roles.Program);
        int exe = Fil(app, "app.exe", 3 * MB);
        Fil(app, "debug.log", 4 * MB, Roles.Log); // 角色和目录不同的文件：只列目录，不列文件
        int plugins = Dir(app, "plugins", Roles.Extension);
        int p1Cache = Dir(Dir(plugins, "p1"), "cache", Roles.AppCache); // 模块里又换了角色：也是模块
        int deep = Fil(p1Cache, "c.bin", 20 * MB);
        int logs = Dir(app, "logs", Roles.Log);
        Fil(logs, "a.log", 1 * MB);
        int cache = Dir(app, "Cache", Roles.AppCache);
        Fil(cache, "x.bin", 10 * MB);
        int gpu = Dir(app, "GPUCache", Roles.AppCache); // 第 4 个缓存模块：每种角色只列最大的 3 个，它排不上
        Fil(gpu, "y.bin", 5 * MB);
        int code = Dir(app, "Code Cache", Roles.AppCache);
        Fil(code, "z.bin", 30 * MB);
        Fil(Dir(app, "res", Roles.Unknown), "r.bin", 90 * MB); // 角色未知：不算模块
        int conf = Dir(Dir(app, "sub"), "conf", Roles.Config); // sub 和上级同一种角色，不是模块；里面换了角色的 conf 才是
        Fil(conf, "s.ini", 1 * KB);
        int game = Dir(app, "Game", Roles.Game); // 嵌在里面的别的对象：整棵都不算 App 的
        int saves = Dir(game, "saves", Roles.Save);
        int sav = Fil(saves, "s1.sav", 2 * MB);
        int gone = Dir(app, "gone", Roles.Log);
        int goneFile = Fil(gone, "old.log", 70 * MB);
        int local = Dir(Dir(Dir(Dir(Dir(0, "Users"), "Alex"), "AppData"), "Local"), "App", Roles.UserData); // 同一对象的第二处根，按目录名并进来的
        Fil(Dir(local, "Cache", Roles.AppCache), "w.bin", 8 * MB);
        int dat = Fil(0, "App.dat", 50 * MB, Roles.UserData); // 第三处根，是个文件
        int oldApp = Dir(0, "OldApp", Roles.Program); // 第四处根，删掉了就不列
        Fil(oldApp, "o.exe", 1 * MB);
        int lonely = Dir(0, "Lonely", Roles.Program); // 只有一处根、里面没换过角色：有所属对象，没有关联
        int lonelyFile = Fil(lonely, "l.bin", 1 * MB);
        int miscFile = Fil(Dir(0, "Misc"), "m.bin", 1 * MB);
        t.Seal();
        var roles = new byte[t.Count];
        for (int i = 1; i < t.Count; i++) roles[i] = roleOf.TryGetValue(i, out var r) ? r : roles[t.Parent(i)];
        var ownerRoots = new Dictionary<int, OwnerRoot>
        {
            [app] = new(0, OwnerSource.PathRule), [local] = new(0, OwnerSource.AppDataDir), [dat] = new(0, OwnerSource.PathRule),
            [oldApp] = new(0, OwnerSource.ProgramDir), [game] = new(1, OwnerSource.GameLibrary), [lonely] = new(2, OwnerSource.AppDataDir),
        };
        Owner[] owners = [new(OwnerKind.App, "App", Evidence.Confirmed), new(OwnerKind.Game, "Game", Evidence.Confirmed), new(OwnerKind.App, "Lonely", Evidence.Inferred)];
        t.SetClassification(new Classification(new byte[t.Count], roles, ownerRoots, owners));
        t.MarkDeleted(gone);
        t.MarkDeleted(oldApp);

        RelatedDto R(int node, Metric m = Metric.Alloc, int limit = 50) => TreeQueries.Related(t, new RelatedRequest(node, m, limit));
        foreach (var m in Both)
            foreach (int limit in new[] { 1, 4, 1000 })
            {
                string? err = null;
                foreach (int n in Enumerable.Range(0, t.Count).Where(t.IsValid))
                    if ((err = DiffRelated(R(n, m, limit), BruteRelated(t, n, m, limit))) != null)
                    {
                        err = $"{t.GetName(n)}：{err}";
                        break;
                    }
                ctx.True(err == null, $"关联[{m}/上限 {limit}] 每个活着的节点都和逐节点往上找根的笨办法一致{(err == null ? "" : "：" + err)}");
            }

        bool Is(RelatedDto r, params (int Id, RelationKind Kind, Evidence Evidence)[] want) =>
            r.Items.Select(x => (x.Id, x.Relation, x.Evidence)).SequenceEqual(want) && r.Items.All(x => x.Role == t.Role(x.Id) && x.Path == t.GetPath(x.Id));
        const RelationKind Root = RelationKind.Root, Other = RelationKind.OtherRoot, Module = RelationKind.RoleModule;
        const Evidence Sure = Evidence.Confirmed, Guess = Evidence.Inferred;
        var fromExe = R(exe);
        ctx.True(fromExe.Owner is { Name: "App", RootId: var rid, Evidence: Sure } && rid == app && fromExe.Note == null && fromExe.Total == 10
            && Is(fromExe, (app, Root, Sure), (dat, Other, Sure), (local, Other, Guess),
                (code, Module, Sure), (p1Cache, Module, Sure), (cache, Module, Sure), (plugins, Module, Sure), (logs, Module, Sure), (conf, Module, Sure)),
            "同对象多处根 + 角色分组：所在的根、另两处根（AppData 那处按目录名并进来，关联只算推测；删掉的那处不列）、" +
            "缓存组只列最大的 3 个（GPUCache 排不上但算进总数）、组按组里最大的降序；未知角色、和上级同角色的目录、文件、嵌套的别的对象都不算模块");
        ctx.True(Is(R(deep), (app, Root, Sure), (dat, Other, Sure), (local, Other, Guess), (code, Module, Sure), (cache, Module, Sure), (gpu, Module, Sure),
                (logs, Module, Sure), (conf, Module, Sure)) && R(deep).Total == 8,
            "节点自己和包含它的模块不算「别的模块」：c.bin 所在的 cache、plugins 不列，GPUCache 递补进缓存组");
        ctx.True(Is(R(app), (dat, Other, Sure), (local, Other, Guess), (code, Module, Sure), (p1Cache, Module, Sure), (cache, Module, Sure),
                (plugins, Module, Sure), (logs, Module, Sure), (conf, Module, Sure)) && R(app).Total == 9,
            "节点就是根：没有「所在的根」那条，模块照列");
        ctx.True(Is(R(local), (app, Other, Guess), (dat, Other, Guess), (t.FindByPath(@"X:\Users\Alex\AppData\Local\App\Cache"), Module, Guess)),
            "从推测的那处根看：另两处根的关联都只算推测，它自己的缓存目录也是");
        ctx.True(Is(R(sav), (game, Root, Sure)) && Is(R(game), (saves, Module, Sure)) && R(game).Owner?.Name == "Game",
            "嵌在 App 里的 Game 只看自己的地盘：不把 App 的根和模块当成自己的");
        foreach (var (n, what) in new[] { (miscFile, "不归任何对象的文件"), (t.Parent(app), "所属对象上方的目录"), (FileTree.RootId, "扫描根") })
        {
            var r = R(n);
            ctx.True(r.Owner == null && r.Items.Length == 0 && r.Total == 0 && r.Note == NoRelation, $"没有所属对象（{what}）：空结果，说明是「{NoRelation}」");
        }
        var alone = R(lonely);
        ctx.True(alone.Owner?.Name == "Lonely" && alone.Items.Length == 0 && alone.Total == 0 && alone.Note == NoRelation && Is(R(lonelyFile), (lonely, Root, Guess)),
            "有所属对象但只有一处根、没换过角色：对象照给，关联为空并说明；里面的文件只关联到根");
        ctx.True(R(exe, limit: 1).Items.Length == 1 && R(exe, limit: 0).Items.Length == 1 && R(exe, limit: -5).Items.Length == 1 && R(exe, limit: 1).Total == 10
            && R(exe, limit: 4).Items.Select(x => x.Id).SequenceEqual([app, dat, local, code]) && R(exe, limit: 5000).Items.Length == 9 && R(exe).Items.Length == 9,
            "上限夹到 1..1000，按顺序截断，总数不受上限影响");
        foreach (int bad in new[] { gone, goneFile, oldApp, -1, t.Count, 1_000_000 })
        {
            string? msg = null;
            try { R(bad); }
            catch (ArgumentException ex) { msg = ex.Message; }
            ctx.True(msg == "节点不存在或已删除", $"Related({bad})：节点不存在或已删除时抛中文的 ArgumentException，实际「{msg ?? "没抛"}」");
        }

        // 接上真分类器：项目标记认出项目，node_modules、build 按目录名换了角色，就是项目的两个模块；src 和项目同角色，不算
        var p = new FileTree(@"X:\");
        int proj = p.AddNode(p.AddNode(p.AddNode(p.AddNode(0, "Users", NodeFlags.Directory, 0, 4096, Stamp), "Alex", NodeFlags.Directory, 0, 4096, Stamp),
            "Desktop", NodeFlags.Directory, 0, 4096, Stamp), "proj", NodeFlags.Directory, 0, 4096, Stamp);
        p.AddNode(proj, "package.json", 0, 1 * KB, 4096, Stamp);
        int main = p.AddNode(p.AddNode(proj, "src", NodeFlags.Directory, 0, 4096, Stamp), "main.ts", 0, 30 * KB, Cluster(30 * KB), Stamp);
        int nm = p.AddNode(proj, "node_modules", NodeFlags.Directory, 0, 4096, Stamp);
        p.AddNode(p.AddNode(nm, "lodash", NodeFlags.Directory, 0, 4096, Stamp), "index.js", 0, 5 * MB, Cluster(5 * MB), Stamp);
        int build = p.AddNode(proj, "build", NodeFlags.Directory, 0, 4096, Stamp);
        p.AddNode(build, "app.js", 0, 1 * MB, Cluster(1 * MB), Stamp);
        p.Seal();
        p.SetClassification(PurposeClassifier.Classify(p, PurposeChecks.FakeEnv(), CancellationToken.None));
        var real = TreeQueries.Related(p, new RelatedRequest(main));
        ctx.True(real.Owner is { Kind: OwnerKind.Project, Name: "proj" }
            && real.Items.Select(x => (x.Id, x.Relation, x.Role)).SequenceEqual([(proj, Root, (int)p.Role(proj)), (nm, Module, Roles.Dependency), (build, Module, Roles.BuildOutput)]),
            $"真分类器的项目：所在的根 + 依赖、构建产物两个模块，实际 [{string.Join(", ", real.Items.Select(x => $"{x.Name}/{x.Relation}/{Roles.Table[x.Role].Key}"))}]");
    }

    // ======================= 规模与性能 =======================

    static FileTree BuildBig(int target, string rootPath)
    {
        var t = new FileTree(rootPath, target + 64);
        var rnd = new Random(1234);
        var mrnd = new Random(4321); // 修改时间单独一个随机源：树形和以前一模一样，别的性能段的数字照旧可比
        string[] ext = ["mp4", "jpg", "png", "dll", "exe", "cs", "js", "json", "txt", "log", "pdf", "zip", "dat", "bin", "db", "tmp", "xml", "html",
            "css", "md", .. Enumerable.Range(0, 44).Select(i => $"e{i}")];
        var dirs = new List<int> { 0 };
        for (int i = 0; i < 100; i++) dirs.Add(t.AddNode(0, $"top{i}", NodeFlags.Directory, 0, 4096, 0));
        while (t.Count < target)
        {
            // 一部分均匀挑目录（有深度），一部分挑随机节点的父目录（越大的目录越容易被再次选中，长尾）
            int parent = rnd.Next(100) < 35 ? dirs[rnd.Next(dirs.Count)] : t.Parent(rnd.Next(1, t.Count));
            if (rnd.Next(100) < 9)
                dirs.Add(t.AddNode(parent, $"dir{t.Count}", NodeFlags.Directory, 0, rnd.Next(4) == 0 ? 4096 : 0, 0));
            else
            {
                long size = rnd.Next(20_000) == 0 ? rnd.NextInt64(GB, 30 * GB) : (long)Math.Exp(rnd.NextDouble() * 17);
                t.AddNode(parent, $"f{t.Count}.{ext[rnd.Next(ext.Length)]}", 0, size, Cluster(size), Stamp - mrnd.NextInt64(60 * Day));
            }
        }
        t.AddNode(0, "未统计", NodeFlags.Virtual, 3 * GB, 3 * GB, 0);
        t.Seal();
        var purposes = new byte[t.Count];
        for (int i = 0; i < purposes.Length; i++) purposes[i] = (byte)rnd.Next(Purposes.Count);
        t.SetPurposes(purposes);
        return t;
    }

    static double Median(int runs, Action f)
    {
        f(); // 预热，别把 JIT 算进去
        var ms = new double[runs];
        for (int i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            f();
            ms[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(ms);
        return ms[runs / 2];
    }

    static void AssertFast(CheckContext ctx, double ms, double limit, string what)
    {
#if DEBUG
        ctx.Info($"{what}：{ms:F1} ms（Debug 构建不卡线，{limit} ms 的上限只在 Release 下断言）");
#else
        ctx.True(ms < limit, $"{what}：{ms:F1} ms，应 < {limit} ms");
        ctx.Info($"{what}：{ms:F1} ms（上限 {limit} ms）");
#endif
    }

    static void Performance(CheckContext ctx, string sandbox)
    {
        var sw = Stopwatch.StartNew();
        var t = BuildBig(2_000_000, Path.Combine(sandbox, "big"));
        ctx.Info($"{t.Count:N0} 个节点的树（含 Seal 和用途）：{sw.ElapsedMilliseconds} ms");
        var o = new Oracle(t, independent: false);

        foreach (var metric in Both)
        {
            var req = new ViewRequest(0, metric, 12_000);
            ViewDto? view = null;
            double ms = Median(7, () => { lock (t.Sync) view = TreeQueries.BuildView(t, req); });
            AssertFast(ctx, ms, 100, $"BuildView({metric}, 12000) 中位耗时，{view!.Nodes.Length} 个节点");
            VerifyView(ctx, o, req, view, $"大树视图[{metric}/12000]");
            ctx.True(view.Nodes.Length > 6000, $"大树视图[{metric}] 应当把预算大致用满，实际 {view.Nodes.Length}");
        }
        // 钻进一个大目录再看一眼（非根视图根）
        int fat = Enumerable.Range(1, t.Count - 1).Where(t.IsDir).MaxBy(i => t.Children(i).Length);
        foreach (var metric in Both)
        {
            var req = new ViewRequest(fat, metric, 12_000);
            ViewDto? view = null;
            double ms = Median(5, () => { lock (t.Sync) view = TreeQueries.BuildView(t, req); });
            AssertFast(ctx, ms, 100, $"BuildView(子项最多的目录 {t.Children(fat).Length:N0} 项, {metric}) 中位耗时");
            VerifyView(ctx, o, req, view!, $"大树视图[fat/{metric}]");
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        NodeDetailsDto? details = null;
        double detailsMs = Median(5, () => details = TreeQueries.GetDetails(t, new DetailsRequest(0)));
        long allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / 6;
        AssertFast(ctx, detailsMs, 300, "根节点 GetDetails 中位耗时");
        ctx.True(allocated < 32 * MB, $"根节点 GetDetails 平均分配 {allocated / MB} MB，应 < 32 MB（遍历不该为每个节点分配字符串）");
        ctx.True(details!.CategoryBytes.SequenceEqual(t.CategoryTotals(Metric.Alloc)) && details.PurposeBytes!.SequenceEqual(t.PurposeTotals(Metric.Alloc)), "大树根详情的类别、用途分布与 FileTree 的汇总一致");
        ctx.Equal(t.Files(0), details.ExtStats.Sum(e => e.Count), "大树根详情的扩展名条目数之和 = 文件数");
        ctx.True(details.ExtStats.Length == 16 && details.ExtStats[^1].Ext == "*", "大树根详情：前 15 + 「*」");
        var sizeDetails = TreeQueries.GetDetails(t, new DetailsRequest(0, Metric.Size));
        ctx.True(sizeDetails.CategoryBytes.SequenceEqual(t.CategoryTotals(Metric.Size)) && sizeDetails.PurposeBytes!.SequenceEqual(t.PurposeTotals(Metric.Size)), "大树根详情（Size）与汇总一致");

        before = GC.GetAllocatedBytesForCurrentThread();
        FileItemDto[]? largest = null;
        double largestMs = Median(5, () => { lock (t.Sync) largest = TreeQueries.LargestFiles(t, new LargestRequest(0, Metric.Alloc, 100)); });
        allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / 6;
        ctx.Info($"LargestFiles(根, 100) 中位耗时 {largestMs:F1} ms，平均分配 {allocated / KB:N0} KB");
        ctx.True(allocated < 32 * MB, $"LargestFiles 平均分配 {allocated / MB} MB，应 < 32 MB");
        var expected = Enumerable.Range(1, t.Count - 1).Where(i => !t.IsDir(i) && !t.Has(i, NodeFlags.Virtual | NodeFlags.HardLinkDup | NodeFlags.Deleted))
            .OrderByDescending(i => t.Alloc(i)).ThenBy(i => i).Take(100).ToArray();
        ctx.True(largest!.Select(i => i.Id).SequenceEqual(expected), "大树上的 Top 100 与全量排序一致");

        // 最近文件：修改时间散在 Stamp 往前 60 天里，基准取 Stamp 之后半天；3650 天是每个文件都进窗口的最坏情况
        long asOfMs = (Stamp - DtoFactory.UnixEpochFileTime) / 10_000 + DayMs / 2;
        foreach (var (days, sort, limit) in new[] { (1, RecentSort.Size, 100), (7, RecentSort.Modified, 1000), (7, RecentSort.Size, 1000), (3650, RecentSort.Modified, 5000) })
        {
            var req = new RecentRequest(days, null, sort, Metric.Alloc, limit);
            RecentFilesDto? recent = null;
            double ms = Median(5, () => { lock (t.Sync) recent = TreeQueries.RecentFiles(t, req, asOfMs); });
            AssertFast(ctx, ms, 300, $"RecentFiles(根, {days} 天, {sort}, {limit}) 中位耗时，窗口内 {recent!.Total:N0} 个文件");
            long from = FileTimeOfMs(asOfMs - days * DayMs);
            var eligible = Enumerable.Range(1, t.Count - 1)
                .Where(i => !t.IsDir(i) && !t.Has(i, NodeFlags.Virtual | NodeFlags.HardLinkDup | NodeFlags.Deleted) && t.MTime(i) >= from)
                .ToArray();
            var top = eligible.OrderByDescending(i => sort == RecentSort.Size ? t.Alloc(i) : t.MTime(i)).ThenBy(i => i).Take(Math.Min(limit, 1000));
            ctx.True(recent.Total == eligible.Length && recent.Items.Select(i => i.Id).SequenceEqual(top), $"大树最近文件[{days} 天/{sort}/{limit}] 与暴力筛选排序一致");
        }

        // 按所属对象聚合：BuildBig 没有所属对象，放在最后手造一份（别的性能段不受影响）——每 29 个目录挑一个当根，
        // 根自然会互相嵌套；400 个对象随机分，一个对象有好些处根
        var ornd = new Random(2610);
        var owners = Enumerable.Range(0, 400).Select(k => new Owner((OwnerKind)(k % 10), $"o{k}", Evidence.Confirmed)).ToArray();
        var ownerRoots = new Dictionary<int, OwnerRoot>();
        for (int i = 1, dirs = 0; i < t.Count; i++)
            if (t.IsDir(i) && dirs++ % 29 == 0) ownerRoots[i] = new OwnerRoot(ornd.Next(owners.Length), OwnerSource.PathRule);
        var purposes = Enumerable.Range(0, t.Count).Select(i => t.Purpose(i)).ToArray();
        var roleBits = Enumerable.Range(0, t.Count).Select(_ => (byte)ornd.Next(Roles.Count)).ToArray();
        lock (t.Sync) t.SetClassification(new Classification(purposes, roleBits, ownerRoots, owners));
        foreach (var (scope, metric) in new[] { (FileTree.RootId, Metric.Alloc), (FileTree.RootId, Metric.Size), (fat, Metric.Alloc) })
        {
            var req = new OwnerUsageRequest(scope, metric, 100);
            OwnerUsageDto? usage = null;
            double ms = Median(5, () => { lock (t.Sync) usage = TreeQueries.OwnerUsage(t, req); });
            AssertFast(ctx, ms, 300, $"OwnerUsage({(scope == FileTree.RootId ? "根" : "子项最多的目录")}, {metric}, 100) 中位耗时，{usage!.OwnerCount} 个对象");
            ctx.True(usage.OwnedSizeInScope + usage.SharedSizeInScope + usage.UnownedSizeInScope == usage.TotalSizeInScope && usage.TotalSizeInScope == t.Size(scope)
                && usage.OwnedAllocInScope + usage.SharedAllocInScope + usage.UnownedAllocInScope == usage.TotalAllocInScope && usage.TotalAllocInScope == t.Alloc(scope),
                $"大树按所属对象[{metric}]：对象 + 共享 + 未归属 = 范围总量 = 地图上的大小");
        }
        // 笨办法对一遍整棵树：每个节点自己往上找根，目录自身占用 = 计入值 − 子项的计入值
        var brute = new long[owners.Length];
        long bruteUnowned = 0;
        for (int i = 0; i < t.Count; i++)
        {
            if (t.Has(i, NodeFlags.Deleted | NodeFlags.HardLinkDup)) continue;
            long own = t.Alloc(i);
            if (t.IsDir(i)) foreach (int c in o.Live(i)) own -= o.Counted(c, Metric.Alloc);
            var owner = t.IsDir(i) || !t.Has(i, NodeFlags.Virtual) ? t.OwnerOf(i, out _) : null;
            if (owner == null) bruteUnowned += own;
            else brute[int.Parse(owner.Name.AsSpan(1))] += own;
        }
        var full = TreeQueries.OwnerUsage(t, new OwnerUsageRequest(null, Metric.Alloc, 1000));
        ctx.True(full.UnownedAllocInScope == bruteUnowned && full.SharedAllocInScope == 0 && full.OwnerCount == owners.Length
            && full.Items.All(x => x.AllocInScope == brute[int.Parse(x.Name.AsSpan(1))]), $"大树按所属对象与逐节点往上找根的笨办法一致（{ownerRoots.Count:N0} 处根）");

        // 关联详情：先用上面这份分类，挑下标最小的根（往后扫得最长）里的一个文件和这个根自己；
        // 再换一份最坏的：卷根本身是根，地盘差不多是整棵树，角色随机，几乎每个目录都换了角色、都是候选模块
        int first = ownerRoots.Keys.Min();
        int inFirst = Enumerable.Range(first + 1, t.Count - first - 1).First(i => !t.IsDir(i) && t.OwnerOf(i, out int r) != null && r == first);
        RelatedBench(ctx, t, [(inFirst, Metric.Alloc, 50), (first, Metric.Size, 1000)], $"{ownerRoots.Count:N0} 处根");
        var wrnd = new Random(2620);
        var worst = new Dictionary<int, OwnerRoot> { [FileTree.RootId] = new(0, OwnerSource.PathRule) };
        for (int k = 0; k < 300; k++) worst[wrnd.Next(1, t.Count)] = new OwnerRoot(k % 2 == 0 ? 0 : 1 + k % 50, OwnerSource.AppDataDir); // 一半是同一对象的另几处根
        lock (t.Sync) t.SetClassification(new Classification(purposes, roleBits, worst, owners));
        int leaf = t.Count - 1;
        while (t.IsDir(leaf) || t.Has(leaf, NodeFlags.Virtual) || t.OwnerOf(leaf, out int lr) == null || lr != FileTree.RootId) leaf--;
        RelatedBench(ctx, t, [(leaf, Metric.Alloc, 100), (FileTree.RootId, Metric.Size, 1000)], "卷根是根");
    }

    static void RelatedBench(CheckContext ctx, FileTree t, (int Node, Metric Metric, int Limit)[] cases, string label)
    {
        foreach (var (node, metric, limit) in cases)
        {
            var req = new RelatedRequest(node, metric, limit);
            RelatedDto? rel = null;
            double ms = Median(5, () => { lock (t.Sync) rel = TreeQueries.Related(t, req); });
            AssertFast(ctx, ms, 100, $"Related({label}, 节点 {node}, {metric}, {limit}) 中位耗时，{rel!.Total:N0} 条关联、列出 {rel.Items.Length} 条");
            string? err = DiffRelated(rel, BruteRelated(t, node, metric, limit));
            ctx.True(err == null, $"大树关联[{label}/{node}/{metric}] 与逐节点往上找根的笨办法一致{(err == null ? "" : "：" + err)}");
        }
    }
}
