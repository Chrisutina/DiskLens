using System.Diagnostics;
using DiskLens.Core;

namespace DiskLens.Check;

// 保护规则（DESIGN §7）与 DeleteService.Check 的自检：全是合成树，路径取本机真实的特殊文件夹，一个磁盘文件都不碰。
static partial class DeleteChecks
{
    static void ProtectionRules(CheckContext ctx)
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\');
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd('\\');
        string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData).TrimEnd('\\');
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).TrimEnd('\\');
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\');
        string vol = Path.GetPathRoot(win)!;
        string own = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string repo = CheckContext.RepoRoot;

        var sims = new Dictionary<string, Sim>(StringComparer.OrdinalIgnoreCase);
        Sim For(string path)
        {
            string v = Path.GetPathRoot(path)!;
            return sims.TryGetValue(v, out var s) ? s : sims[v] = new Sim(v);
        }
        // B 拦截、W 放行但警告、A 放行无警告
        var cases = new List<(Sim Sim, int Id, char Want, string Path, string? Reason)>();
        void Dir(char want, string path, string? reason = null, NodeFlags flags = 0) { var s = For(path); cases.Add((s, s.Dir(path, flags), want, path, reason)); }
        void File(char want, string path, string? reason = null, NodeFlags flags = 0, long size = 1000) { var s = For(path); cases.Add((s, s.File(path, size, flags), want, path, reason)); }

        // —— Windows 核心 ——
        Dir('B', win, "Windows 目录");
        foreach (string part in new[] { "System32", "SysWOW64", "WinSxS", "Installer", "servicing", "Boot", "SystemApps", "SystemResources", "Fonts", "assembly", "Microsoft.NET" })
        {
            Dir('B', Path.Combine(win, part), "核心组件");
            File('B', Path.Combine(win, part, "sub", "x.dll"), "核心组件");
        }
        File('B', Path.Combine(vol, "WINDOWS", "SYSTEM32", "CONFIG", "SAM"), "核心组件"); // 大小写不敏感
        // —— Windows 其余内容：警告 ——
        Dir('W', Path.Combine(win, "Temp"));
        File('W', Path.Combine(win, "Temp", "x.tmp"));
        File('W', Path.Combine(win, "SoftwareDistribution", "Download", "a.cab"));
        File('W', Path.Combine(win, "Logs", "CBS", "CBS.log"));
        // —— 程序与数据根目录 ——
        Dir('B', pf, "根目录");
        if (!pf86.Equals(pf, StringComparison.OrdinalIgnoreCase)) Dir('B', pf86, "根目录");
        Dir('B', pd, "根目录");
        Dir('B', Path.Combine(vol, "Users"), "根目录");
        Dir('W', Path.Combine(pf, "Foo"));
        File('W', Path.Combine(pf, "Foo", "bar.exe"));
        Dir('W', Path.Combine(pf86, "Foo"));
        Dir('W', Path.Combine(pd, "Foo"));
        File('W', Path.Combine(pd, "Foo", "x.dat"));
        // —— 用户目录 ——
        Dir('B', profile, "用户配置文件");
        Dir('B', Path.Combine(vol, "Users", "Public"), "用户配置文件");
        File('A', Path.Combine(profile, "Documents", "a.docx"));
        File('A', Path.Combine(profile, "Downloads", "setup.exe"));
        Dir('A', Path.Combine(profile, "Desktop"));
        File('A', Path.Combine(local, "Temp", "x.tmp"));
        Dir('A', Path.Combine(local, "Foo"));
        Dir('W', roaming, "漫游");
        File('W', Path.Combine(roaming, "Foo", "bar.cfg"), "漫游");
        // —— 密钥与凭据 ——
        Dir('B', Path.Combine(profile, ".ssh"), "密钥");
        File('B', Path.Combine(profile, ".ssh", "id_rsa"), "密钥");
        File('B', Path.Combine(profile, "Documents", "proj", ".ssh", "key"), "密钥");
        File('B', Path.Combine(profile, ".gnupg", "private-keys-v1.d", "x.key"), "密钥");
        File('W', Path.Combine(profile, ".aws", "credentials"), "凭据");
        File('W', Path.Combine(profile, ".kube", "config"), "凭据");
        Dir('W', Path.Combine(profile, ".azure"), "凭据");
        // —— 卷根下的系统目录与文件 ——
        Dir('B', Path.Combine(vol, "$Recycle.Bin"), "清空回收站");
        File('B', Path.Combine(vol, "$Recycle.Bin", "S-1-5-21-1", "$IABC.txt"), "清空回收站");
        Dir('B', Path.Combine(vol, "System Volume Information"), "存储设置");
        File('B', Path.Combine(vol, "System Volume Information", "x"), "存储设置");
        Dir('B', Path.Combine(vol, "Recovery"), "存储设置");
        File('B', Path.Combine(vol, "Recovery", "WindowsRE", "Winre.wim"), "存储设置");
        File('B', Path.Combine(vol, "Boot", "BCD"), "存储设置");
        Dir('B', Path.Combine(vol, "EFI"), "存储设置");
        foreach (string name in new[] { "bootmgr", "BOOTNXT", "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp", "PAGEFILE.SYS" })
            File('B', Path.Combine(vol, name), "系统");
        Dir('B', Path.Combine(vol, "Windows.old"), "存储设置");
        File('B', Path.Combine(vol, "Windows.old", "Windows", "x"), "存储设置");
        Dir('B', Path.Combine(vol, "$WinREAgent"), "元数据");
        File('B', Path.Combine(vol, "$Windows.~BT", "Sources", "x"), "存储设置");
        Dir('B', Path.Combine(vol, "ESD"), "存储设置");
        // —— 首版拦过头的：同名但不在卷根、或者只是名字以 $ 开头的用户文件 ——
        File('A', Path.Combine(profile, "Downloads", "$100 budget.xlsx"));
        File('A', Path.Combine(vol, "Backup", "pagefile.sys"));
        File('A', Path.Combine(vol, "Backup", "hiberfil.sys"));
        File('A', Path.Combine(vol, "Data", "Boot", "x.cfg"));
        Dir('A', Path.Combine(vol, "Data", "EFI"));
        File('A', Path.Combine(vol, "Data", "Recovery", "x"));
        File('A', Path.Combine(vol, "Data", "$tmp", "a.txt"));
        File('A', Path.Combine(profile, "Documents", "copy", "$RECYCLE.BIN", "x"));
        File('A', Path.Combine(vol, "Games", "Foo", "save.dat"));
        // —— DiskLens 自己 ——
        Dir('B', own, "DiskLens");
        File('B', Path.Combine(own, "DiskLens.Core.dll"), "DiskLens");
        Dir('B', Path.GetDirectoryName(own)!, "DiskLens");
        File('A', Path.Combine(repo, ".sandbox", "delete", "x.txt"));
        // —— 节点自带的标志 ——
        string work = Path.Combine(vol, "Work");
        File('B', Path.Combine(work, "virtual.bin"), "虚拟", NodeFlags.Virtual);
        File('B', Path.Combine(work, "meta.sys"), "元数据", NodeFlags.Metafile);
        Dir('B', Path.Combine(work, "vdir"), "虚拟", NodeFlags.Virtual);
        File('B', Path.Combine(work, "vdir", "child.txt"), "虚拟或元数据目录内");
        File('W', Path.Combine(work, "sys.dat"), "系统属性", NodeFlags.System);
        Dir('W', Path.Combine(work, "unreadable"), "没能读取", NodeFlags.Inaccessible);
        Dir('W', Path.Combine(work, "junc"), "链接", NodeFlags.Reparse);
        File('W', Path.Combine(work, "placeholder.docx"), "云", NodeFlags.Cloud);
        File('W', Path.Combine(work, "huge.iso"), "10 GB", 0, 11L << 30);
        string cloud = Path.Combine(work, "cloud-dir");
        Dir('W', cloud, "云", NodeFlags.Reparse | NodeFlags.Cloud);
        File('A', Path.Combine(cloud, "inside.txt")); // 云占位目录里的内容是正经文件，不该被一刀切拦掉
        string many = Path.Combine(work, "many");
        for (int i = 0; i < 100_001; i++) For(many).File(Path.Combine(many, $"f{i}.dat"), 1);
        Dir('W', many, "10 万个文件");
        {
            var s = For(work);
            int l1 = s.File(Path.Combine(work, "link-a.dat"), 5000);
            int l2 = s.File(Path.Combine(work, "link-b.dat"), 5000);
            s.Tree.ResolveHardLinkGroup([l1, l2]);
            cases.Add((s, l1, 'W', "link-a.dat", "硬链接"));
        }

        foreach (var s in sims.Values) s.Done();
        foreach (var (sim, id, want, path, reason) in cases)
        {
            var r = Protection.Evaluate(sim.Tree, id);
            char got = r.Blocked ? 'B' : r.Warning != null ? 'W' : 'A';
            string text = r.Blocked ? r.Reason ?? "" : r.Warning ?? "";
            ctx.True(got == want, $"保护规则 {path}：期望 {want}，实际 {got}（{text}）");
            if (reason != null && got == want) ctx.True(text.Contains(reason), $"保护规则 {path}：说明应含「{reason}」，实际「{text}」");
            if (r.Blocked) ctx.True(!string.IsNullOrEmpty(r.Reason), $"保护规则 {path}：拦截必须给理由");
        }

        // 扫描根自己
        var rootSim = For(vol);
        ctx.True(Protection.Evaluate(rootSim.Tree, FileTree.RootId).Blocked, "保护规则：扫描根不能删");

        // 路径写法：大小写、正反斜杠、\\?\ 前缀、结尾反斜杠
        foreach (string p in new[] { @"\\?\" + win + @"\System32", win.Replace('\\', '/') + "/system32/x.dll", win.ToUpperInvariant() + @"\SYSTEM32\", win + @"\system32\..\System32", vol })
            ctx.True(Protection.EvaluatePath(p).Blocked, $"EvaluatePath 写法变体：{p}");
        ctx.True(!Protection.EvaluatePath(Path.Combine(vol, "Games", "x")).Blocked, "EvaluatePath：普通路径放行");
        Throws<ArgumentException>(ctx, () => Protection.EvaluatePath(@"relative\path"), "EvaluatePath：相对路径拒绝");
        Throws<ArgumentException>(ctx, () => Protection.EvaluatePath(vol + @"a.txt:stream"), "EvaluatePath：备用数据流拒绝");

        // 子树里藏着受保护的条目：整次删除拦下，理由点名那个条目；警告也要往上提
        var g = new Sim(vol);
        int backup = g.Dir(Path.Combine(vol, "Work", "backup"));
        g.File(Path.Combine(vol, "Work", "backup", "a.txt"));
        int deepKey = g.File(Path.Combine(vol, "Work", "backup", "old", ".ssh", "id_ed25519"));
        int clean = g.Dir(Path.Combine(vol, "Work", "clean"));
        g.File(Path.Combine(vol, "Work", "clean", "b.txt"));
        var tree = g.Done();
        var check = DeleteService.Check(tree, [backup, clean]);
        var ib = check.Items.Single(i => i.Id == backup);
        ctx.True(ib.Blocked && ib.Reason != null && ib.Reason.Contains(".ssh"), $"子树含 .ssh：整个目录被拦且点名（{ib.Reason}）");
        ctx.True(!check.Items.Single(i => i.Id == clean).Blocked, "子树干净的目录放行");
        _ = deepKey;

        var gw = new Sim(Path.GetPathRoot(roaming)!);
        string appData = Path.GetDirectoryName(roaming)!;
        int appId = gw.Dir(appData);
        gw.File(Path.Combine(roaming, "Foo", "x.cfg"));
        var warnTree = gw.Done();
        var wc = DeleteService.Check(warnTree, [appId]);
        ctx.True(!wc.Items[0].Blocked && wc.Items[0].Warning != null && wc.Items[0].Warning!.Contains("漫游"), $"目录里含漫游配置：警告要提上来（{wc.Items[0].Warning}）");
    }

    static void CheckApi(CheckContext ctx)
    {
        string vol = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        string work = Path.Combine(vol, "Work");

        // 合计与单项
        var s = new Sim(vol);
        int a = s.File(Path.Combine(work, "a.bin"), 5000);
        int d = s.Dir(Path.Combine(work, "dir"));
        int b = s.File(Path.Combine(work, "dir", "b.bin"), 4097);
        int sys = s.File(Path.Combine(vol, "Windows", "System32", "k.dll"), 100);
        var tree = s.Done();
        var c = DeleteService.Check(tree, [a, d, b, sys, a]);
        ctx.Equal(3, c.Items.Length, "Check：重复的和被已选目录包含的节点不重复列");
        ctx.True(c.AnyBlocked && !c.AnyWarning, "Check：AnyBlocked / AnyWarning");
        ctx.Equal(Alloc(5000) + Alloc(4097), c.TotalAlloc, "Check：被拦的不计入占用合计");
        ctx.Equal(5000L + 4097, c.TotalSize, "Check：逻辑大小合计");
        ctx.Equal(2L, c.TotalFiles, "Check：文件数合计");
        var ia = c.Items.Single(i => i.Id == a);
        ctx.True(!ia.IsDir && ia.Size == 5000 && ia.Alloc == Alloc(5000) && ia.Files == 1 && ia.Path.EndsWith("a.bin"), "Check：单项字段");
        var idr = c.Items.Single(i => i.Id == d);
        ctx.True(idr.IsDir && idr.Files == 1 && idr.Alloc == Alloc(4097), "Check：目录项字段");

        // 单次删除合计超过 10 GB 要警告，哪怕每一项都没超
        var big = new Sim(vol);
        int x = big.File(Path.Combine(work, "x.iso"), 6L << 30);
        int y = big.File(Path.Combine(work, "y.iso"), 6L << 30);
        int z = big.File(Path.Combine(work, "z.txt"), 10);
        var bt = big.Done();
        var single = DeleteService.Check(bt, [x]);
        ctx.True(!single.AnyWarning, "Check：单个 6 GB 不警告");
        var pair = DeleteService.Check(bt, [x, y, z]);
        ctx.True(pair.AnyWarning && pair.Items.Any(i => i.Warning != null && i.Warning.Contains("10 GB")), "Check：两个 6 GB 合计超 10 GB，要把警告挂到某一项上（前端只列有 Warning 的项）");
        ctx.Equal(12L << 30, pair.TotalAlloc - Alloc(10), "Check：占用合计");

        // 硬链接副本对合计贡献 0
        var h = new Sim(vol);
        int h1 = h.File(Path.Combine(work, "a", "f.dat"), 8000);
        int h2 = h.File(Path.Combine(work, "b", "f.dat"), 8000);
        h.Tree.ResolveHardLinkGroup([h1, h2]);
        var ht = h.Done();
        ctx.Equal(0L, DeleteService.Check(ht, [h2]).TotalAlloc, "Check：硬链接副本不计占用");
        ctx.Equal(Alloc(8000), DeleteService.Check(ht, [h1]).TotalAlloc, "Check：计入的那个链接按占用算");

        // 边界
        var empty = DeleteService.Check(tree, []);
        ctx.True(empty.Items.Length == 0 && empty.TotalAlloc == 0 && !empty.AnyBlocked && !empty.AnyWarning, "Check：空选择");
        Throws<ArgumentException>(ctx, () => DeleteService.Check(tree, [9999]), "Check：越界节点");
        Throws<ArgumentException>(ctx, () => DeleteService.Check(tree, [-1]), "Check：负数节点");
        lock (tree.Sync) tree.MarkDeleted(a);
        Throws<ArgumentException>(ctx, () => DeleteService.Check(tree, [a]), "Check：已删除节点");
        Throws<ArgumentException>(ctx, () => DeleteService.Check(tree, Enumerable.Range(0, 10_001).ToArray()), "Check：超过 10000 个条目");
        Throws<ArgumentException>(ctx, () => DeleteService.Delete(tree, [a], true, CancellationToken.None), "Delete：已删除节点");

        // 规模：三十万个节点的 Check 是在持有树锁时跑的，必须快
        var scale = new Sim(vol);
        for (int i = 0; i < 3000; i++)
            for (int j = 0; j < 100; j++) scale.File(Path.Combine(work, "proj", "d" + i, $"f{j}.dat"), 100);
        var st = scale.Done();
        int projId;
        lock (st.Sync) projId = st.FindByPath(Path.Combine(work, "proj"));
        var sw = Stopwatch.StartNew();
        var big2 = DeleteService.Check(st, [projId]);
        ctx.Info($"Check 三十万个节点的子树（持锁）：{sw.ElapsedMilliseconds} ms");
        ctx.True(!big2.AnyBlocked && big2.TotalFiles == 300_000, "Check：三十万文件的统计");
        ctx.True(sw.ElapsedMilliseconds < 3000, $"Check：三十万个节点应在 3 秒内（实际 {sw.ElapsedMilliseconds} ms）");
    }
}
