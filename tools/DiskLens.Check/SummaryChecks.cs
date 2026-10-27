using System.Text.Json;
using System.Text.Json.Nodes;
using DiskLens.Core;

namespace DiskLens.Check;

/// 扫描摘要持久化（dise §3.2）：摘要文件只在沙箱里读写。
static class SummaryChecks
{
    public static void Run(CheckContext ctx)
    {
        string sandbox = ctx.Sandbox();
        string scope = Fixture(sandbox, "scope"), doomed = Fixture(sandbox, "doomed"), target = Fixture(sandbox, "target");
        // 扫描报告里放一条跳过的链接：往返比对的计数不全是 0
        string link = Path.Combine(scope, "junction");
        WalkChecks.MakeJunction(link, target);
        try
        {
            var store = new SummaryStore(Path.Combine(sandbox, "data", "scan-summaries-user.json"));
            ctx.True(store.List() is [], "还没有摘要文件：空列表，不抛");
            var first = RoundTrip(ctx, store, scope);
            LatestPerScope(ctx, store, scope, first);
            UnfinishedScansKeep(ctx, store, scope, doomed);
            Limits(ctx, sandbox, first);
            AtomicReplace(ctx, sandbox, first);
            BadFiles(ctx, sandbox, scope, first);
            Concurrent(ctx, sandbox, first);
            WriteFailure(ctx, sandbox, scope);
            Comparison(ctx, sandbox, scope);
        }
        finally { WalkChecks.RemoveLink(link); }
    }

    static ScanSummaryDto RoundTrip(CheckContext ctx, SummaryStore store, string scope)
    {
        using var service = new ScanService(store);
        SavedSummaryDto[]? atDone = null;
        service.Completed += _ => atDone = store.List(); // 比 ScanAndWait 自己的处理先挂上，先跑
        var scanned = ServiceChecks.ScanAndWait(service, scope);
        ctx.True(scanned.LinksSkipped == 1 && scanned.Files > 0, "夹具扫描：有文件、有一个跳过的链接");
        ctx.True(atDone is [{ } d] && Same(d, scanned), "先落盘再发完成事件：收到完成事件时已经读得到这一次");
        ctx.True(store.List() is [{ } s] && Same(s, scanned),
            "保存/读取往返：根路径、模式、文件系统、完成时刻、耗时、文件数、占用、卷容量、用途字节、扫描报告的计数都对得上");
        ctx.True(new SummaryStore(store.FilePath).List() is [{ } again] && Same(again, scanned), "换个实例读（相当于重启）：一样");
        string json = File.ReadAllText(store.FilePath);
        ctx.True(json.StartsWith("{\"version\":2,", StringComparison.Ordinal) && !json.Contains("scanId") && !json.Contains("rootId")
            && !json.Contains("treeVersion"), "文件带版本号；不存扫描号、节点 ID、树版本");
        return scanned;
    }

    static void LatestPerScope(CheckContext ctx, SummaryStore store, string scope, ScanSummaryDto first)
    {
        using (var service = new ScanService(store))
        {
            var second = ServiceChecks.ScanAndWait(service, scope);
            ctx.True(store.List() is [{ } s] && s.CompletedAtMs == second.CompletedAtMs, "同一范围再扫一次：只留最近一次");
        }
        long latest = store.List()[0].CompletedAtMs;
        string upper = scope.ToUpperInvariant();
        store.Save(first with { RootPath = upper, CompletedAtMs = latest + 1 });
        ctx.True(store.List() is [{ } u] && u.RootPath == upper, "范围按根路径认、不区分大小写：只差大小写的算同一个范围");
        ctx.True(store.Save(first with { CompletedAtMs = latest - 60_000 }) && store.List() is [{ } kept] && kept.RootPath == upper
            && kept.CompletedAtMs == latest + 1, "更早完成的扫描晚落盘：不把这个范围更新的一次盖掉");
    }

    /// 取消、失败的扫描不覆盖已有摘要。进度在扫描线程上同步发，第一条（扫描开始）一来就动手，一定赶在遍历之前
    static void UnfinishedScansKeep(CheckContext ctx, SummaryStore store, string scope, string doomed)
    {
        using (var service = new ScanService(store)) ServiceChecks.ScanAndWait(service, doomed);
        ctx.Equal(2, store.List().Length, "两个范围各有一条摘要");
        string before = JsonSerializer.Serialize(store.List(), Json.Options);
        using (var service = new ScanService(store))
        {
            int once = 0;
            service.Progress += _ => { if (Interlocked.Exchange(ref once, 1) == 0) service.Cancel(); };
            ctx.True(Finish(service, scope) is (null, { Cancelled: true }), "一开始就取消：以取消结束");
        }
        using (var service = new ScanService(store))
        {
            int once = 0;
            service.Progress += _ => { if (Interlocked.Exchange(ref once, 1) == 0) Directory.Delete(doomed, recursive: true); };
            ctx.True(Finish(service, doomed) is (null, { Cancelled: false }), "开始后扫描根被删：以失败结束");
        }
        ctx.Equal(before, JsonSerializer.Serialize(store.List(), Json.Options), "取消、失败的扫描不覆盖已有摘要");
    }

    static void Limits(CheckContext ctx, string sandbox, ScanSummaryDto template)
    {
        var store = new SummaryStore(Path.Combine(sandbox, "limits", "count.json"));
        for (int i = 0; i <= SummaryStore.MaxScopes; i++)
            store.Save(template with { RootPath = $@"X:\scope{i:D2}", CompletedAtMs = 1_000 + i });
        var all = store.List();
        ctx.Equal(SummaryStore.MaxScopes, all.Length, $"存过 {SummaryStore.MaxScopes + 1} 个范围：只留 {SummaryStore.MaxScopes} 个");
        ctx.True(all[0].RootPath == $@"X:\scope{SummaryStore.MaxScopes}" && all.All(s => s.RootPath != @"X:\scope00"), "丢的是最旧的那个");
        ctx.True(all.Zip(all.Skip(1)).All(p => p.First.CompletedAtMs > p.Second.CompletedAtMs), "按完成时刻新到旧");

        // 体积上限：根路径各 4 万个字符，32 条一百二十多万字节，超过 1 MB 就接着丢最旧的
        var big = new SummaryStore(Path.Combine(sandbox, "limits", "size.json"));
        for (int i = 0; i < SummaryStore.MaxScopes; i++)
            big.Save(template with { RootPath = @"X:\" + new string('a', 40_000) + i, CompletedAtMs = 1_000 + i });
        var kept = big.List();
        long size = new FileInfo(big.FilePath).Length;
        ctx.Info($"体积上限：留下 {kept.Length} 条、{size:N0} 字节");
        ctx.True(size <= SummaryStore.MaxBytes && kept.Length is > 1 and < SummaryStore.MaxScopes, "文件超过 1 MB 时丢最旧的，直到放得下");
        ctx.True(kept.Select(s => s.CompletedAtMs).SequenceEqual(
            Enumerable.Range(0, kept.Length).Select(i => 1_000L + SummaryStore.MaxScopes - 1 - i)), "留下的是最新的连续几条");
    }

    /// 原子替换：写到一半崩了、或者替换没成，正式文件都是完整的上一版；残留的临时文件不读，下次保存覆盖它
    static void AtomicReplace(CheckContext ctx, string sandbox, ScanSummaryDto template)
    {
        var store = new SummaryStore(Path.Combine(sandbox, "atomic", "summaries.json"));
        string temp = store.FilePath + ".tmp";
        store.Save(template with { RootPath = @"X:\old", CompletedAtMs = 1_000 });
        File.WriteAllText(temp, "{\"version\":1,\"summa"); // 上次写到一半崩了
        ctx.True(store.List() is [{ RootPath: @"X:\old" }], "残留的临时文件不读");
        bool saved;
        string seen;
        // 别的进程正开着正式文件：要么整份换上新的，要么还是完整的上一版（这时 Save 返回 false、只记警告）
        using (var reader = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            saved = store.Save(template with { RootPath = @"X:\new", CompletedAtMs = 2_000 });
            seen = new StreamReader(reader).ReadToEnd();
        }
        var now = store.List();
        ctx.Info($"别的句柄开着正式文件时保存：{(saved ? "换上了" : "没换成，只记了警告")}");
        ctx.True(seen.Contains(@"X:\\old") && !seen.Contains(@"X:\\new"), "已经开着的句柄读到的是完整的旧内容：整份换，不原地改");
        ctx.True(saved ? now is [{ RootPath: @"X:\new" }, { RootPath: @"X:\old" }] : now is [{ RootPath: @"X:\old" }],
            "替换要么成、要么不成，读到的都是完整的一版");
        ctx.True(store.Save(template with { RootPath = @"X:\new", CompletedAtMs = 2_000 })
            && store.List() is [{ RootPath: @"X:\new" }, { RootPath: @"X:\old" }] && !File.Exists(temp),
            "没人占着时保存：换上新的一版，临时文件被这次保存覆盖后改名用掉");
    }

    /// 损坏、版本不符、超限：当没有，坏文件改名成 .bad 留底（只留最近一个）；摘要文件坏了照样能扫描、照样存上
    static void BadFiles(CheckContext ctx, string sandbox, string scope, ScanSummaryDto template)
    {
        string dir = Path.Combine(sandbox, "bad");
        var store = new SummaryStore(Path.Combine(dir, "summaries.json"));
        store.Save(template);
        var good = store.List()[0];
        string Doc(int version, IEnumerable<SavedSummaryDto> items) => JsonSerializer.Serialize(new { version, summaries = items }, Json.Options);
        (string What, string Content)[] cases =
        [
            ("不是完整的 JSON", "{\"version\":1,\"summaries\":["),
            ("空文件", ""),
            ("版本号不符", Doc(SummaryStore.Version + 1, [good])),
            ("缺字段", """{"version":1,"summaries":[{"rootPath":"X:"}]}"""),
            ("同一范围出现两次（只差大小写）", Doc(SummaryStore.Version, [good, good with { RootPath = good.RootPath.ToUpperInvariant() }])),
            ($"超过 {SummaryStore.MaxScopes} 个范围",
                Doc(SummaryStore.Version, Enumerable.Range(0, SummaryStore.MaxScopes + 1).Select(i => good with { RootPath = $@"X:\{i}" }))),
            ($"超过 {SummaryStore.MaxBytes / 1024} KB", Doc(SummaryStore.Version, [good]) + new string(' ', SummaryStore.MaxBytes)),
        ];
        foreach (var (what, content) in cases)
        {
            File.WriteAllText(store.FilePath, content);
            ctx.True(store.List() is [] && !File.Exists(store.FilePath) && File.ReadAllText(store.FilePath + ".bad") == content
                && Directory.GetFiles(dir, "*.bad").Length == 1, $"{what}：当没有，坏文件改名成 .bad 留底（只留最近一个）");
        }
        File.WriteAllText(store.FilePath, "垃圾");
        using var service = new ScanService(store);
        var scanned = ServiceChecks.ScanAndWait(service, scope);
        ctx.True(store.List() is [{ } s] && s.CompletedAtMs == scanned.CompletedAtMs && File.ReadAllText(store.FilePath + ".bad") == "垃圾",
            "摘要文件坏了照样扫描成功：坏文件留底，新摘要存上");
    }

    /// 两个实例同时保存（共 32 个范围）：文件不坏，一个都不丢
    static void Concurrent(CheckContext ctx, string sandbox, ScanSummaryDto template)
    {
        string file = Path.Combine(sandbox, "concurrent", "summaries.json");
        SummaryStore[] stores = [new(file), new(file)];
        var saved = new bool[SummaryStore.MaxScopes];
        Parallel.For(0, saved.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            saved[i] = stores[i % 2].Save(template with { RootPath = $@"X:\c{i:D2}", CompletedAtMs = 1_000 + i }));
        var all = new SummaryStore(file).List();
        ctx.True(saved.All(x => x) && all.Length == SummaryStore.MaxScopes
            && all.Select(s => s.RootPath).Distinct().Count() == SummaryStore.MaxScopes, "并发保存：都存上了，文件完好、每个范围都在");
    }

    /// 存不下（数据目录建不起来）只记警告：扫描照样成功，读的一方当没有
    static void WriteFailure(CheckContext ctx, string sandbox, string scope)
    {
        string blocker = Path.Combine(sandbox, "blocker");
        File.WriteAllText(blocker, "这里是个文件，数据目录建不起来");
        var store = new SummaryStore(Path.Combine(blocker, "data", "summaries.json"));
        using var service = new ScanService(store);
        var scanned = ServiceChecks.ScanAndWait(service, scope);
        ctx.True(scanned.Files > 0 && ReferenceEquals(service.Summary, scanned) && store.List() is [], "摘要写盘失败：扫描照样成功，读到的是空列表");
    }

    /// 第 2 版的缓存对照（启动页用）：根下面的大项、认出的应用，覆盖前和同一范围的上一次比
    static void Comparison(CheckContext ctx, string sandbox, string scope)
    {
        var store = new SummaryStore(Path.Combine(sandbox, "compare", "summaries.json"));
        ScanSummaryDto first;
        using (var service = new ScanService(store)) first = ServiceChecks.ScanAndWait(service, scope);
        var a = store.List().Single();
        ctx.True(a is { PreviousAtMs: null, AppsComparedAtMs: null, IsAdmin: not null, TopEntries.Length: > 0, Apps: not null }
            && a.TopEntries.Any(t => t is { Name: "a.txt", IsDir: false, PreviousAlloc: null }), "第一次扫这个范围：记下大项和应用，没有可对照的上一次");
        string grown = Path.Combine(scope, "grown.bin");
        File.WriteAllBytes(grown, new byte[64 * 1024]);
        try
        {
            using (var service = new ScanService(store)) ServiceChecks.ScanAndWait(service, scope);
            var b = store.List().Single();
            ctx.True(b.PreviousAtMs == first.CompletedAtMs && b.PreviousAlloc == first.ScannedAlloc
                && b.TopEntries!.Single(t => t.Name == "a.txt").PreviousAlloc == a.TopEntries!.Single(t => t.Name == "a.txt").Alloc
                && b.TopEntries!.Single(t => t.Name == "grown.bin").PreviousAlloc == null,
                "再扫一次：带上一次的完成时刻和总占用，大项按名字对上旧占用，新冒出来的是 null");
            ctx.True(store.Knows(scope) && store.Knows(scope + "\\") && store.Knows(scope.ToUpperInvariant()) && !store.Knows(grown) && !store.Knows(@"C:\Windows"),
                "外壳只认记录里的位置：范围本身（不分大小写、尾斜杠），别的路径不认");
        }
        finally { File.Delete(grown); }

        // 对照规则：用合成的两条记录，不靠真实机器上装了什么
        SavedAppDto App(string name, long seen = 0) => new(OwnerKind.App, name, $@"X:\Apps\{name}", 100, Roles.Program, Evidence.Confirmed, true, seen, false);
        var prev = a with { RootPath = @"X:\cmp", CompletedAtMs = 1_000, Mode = ScanMode.Walk, IsAdmin = false, ScannedAlloc = 300,
            Apps = [App("Old", 500), App("Kept", 700)], TopEntries = [new("Apps", true, 300, 3, null)] };
        var next = prev with { CompletedAtMs = 2_000, ScannedAlloc = 510, Apps = [App("kept"), App("New")],
            TopEntries = [new("apps", true, 500, 5, null), new("Fresh", false, 10, 1, null)] };
        var c = SummaryStore.Compare(next, prev);
        ctx.True(c.Apps is [{ Name: "kept", FirstSeenMs: 700, IsNew: false, AbsentAtMs: null }, { Name: "New", FirstSeenMs: 2_000, IsNew: true, AbsentAtMs: 1_000 }]
            && c.RemovedApps is [{ Name: "Old", IsNew: false }] && c is { AppsComparedAtMs: 1_000, PreviousAtMs: 1_000, PreviousAlloc: 300 },
            "同模式同权限：对上的沿用第一次出现的时刻（名字不分大小写），新出现的标新并记下哪次还没有，上一次有这次没有的记成消失");
        var again = SummaryStore.Compare(next with { CompletedAtMs = 3_000 }, c);
        ctx.True(again.Apps is [{ Name: "kept", IsNew: false, AbsentAtMs: null }, { Name: "New", FirstSeenMs: 2_000, IsNew: true, AbsentAtMs: 1_000 }]
            && again.AppsComparedAtMs == 2_000, "再扫一次：上次对照出来的新应用照样算新，发现时刻和哪次还没有都沿用（启动页的「最近一周」不会一重扫就丢）");
        var older = SummaryStore.Compare(next with { CompletedAtMs = 3_000 }, c with { Apps = [.. c.Apps!.Select(x => x with { AbsentAtMs = null })] });
        ctx.True(older.Apps![1] is { Name: "New", IsNew: true, AbsentAtMs: 1_000 }, "旧版本的记录没有 AbsentAtMs：用发现那条记录的对照时刻补上");
        ctx.True(c.TopEntries is [{ Name: "apps", PreviousAlloc: 300 }, { Name: "Fresh", PreviousAlloc: null }], "大项按名字（不分大小写）对上一次的占用");
        var mft = SummaryStore.Compare(next with { Mode = ScanMode.Mft }, prev);
        ctx.True(mft.Apps is [{ FirstSeenMs: 700, IsNew: false }, { FirstSeenMs: 2_000, IsNew: false }] && mft is { RemovedApps: null, AppsComparedAtMs: null },
            "扫描方式不同：不对照新出现和消失的应用（认得出的本来就不一样），对上的照样沿用第一次出现的时刻");
        ctx.True(SummaryStore.Compare(next with { IsAdmin = true }, prev) is { AppsComparedAtMs: null, RemovedApps: null } admin
            && admin.Apps!.All(x => !x.IsNew), "权限不同：同样不对照");
        var legacy = SummaryStore.Compare(next, prev with { Apps = null, TopEntries = null, IsAdmin = null });
        ctx.True(legacy is { AppsComparedAtMs: null, PreviousAtMs: 1_000, RemovedApps: null } && legacy.Apps!.All(x => x is { IsNew: false, FirstSeenMs: 2_000 })
            && legacy.TopEntries!.All(x => x.PreviousAlloc == null), "上一次是第 1 版记录（没记应用和大项）：只对照总占用，谁都不算新");
        ctx.True(SummaryStore.Compare(next, null) is { PreviousAtMs: null, AppsComparedAtMs: null, RemovedApps: null } fresh
            && fresh.Apps!.All(x => x is { IsNew: false, FirstSeenMs: 2_000 }), "第一次：谁都不算新，第一次出现的时刻就是这次");

        // 第 1 版文件照读：对照字段是 null；再存一次写成第 2 版
        string v1File = Path.Combine(sandbox, "compare", "v1.json");
        var node = JsonSerializer.SerializeToNode(a with { RootPath = @"X:\v1", CompletedAtMs = 1_000 }, Json.Options)!.AsObject();
        foreach (string key in new[] { "isAdmin", "topEntries", "apps", "removedApps", "previousAtMs", "previousAlloc", "appsComparedAtMs" }) node.Remove(key);
        File.WriteAllText(v1File, new JsonObject { ["version"] = 1, ["summaries"] = new JsonArray(node) }.ToJsonString());
        var v1 = new SummaryStore(v1File);
        ctx.True(v1.List() is [{ RootPath: @"X:\v1", Apps: null, TopEntries: null, IsAdmin: null, PreviousAtMs: null }], "第 1 版的文件照读，对照字段是 null");
        ctx.True(v1.Save(first with { RootPath = @"X:\v1", CompletedAtMs = 2_000 }) && File.ReadAllText(v1File).StartsWith("{\"version\":2,", StringComparison.Ordinal)
            && v1.List() is [{ PreviousAtMs: 1_000, AppsComparedAtMs: null }], "再存一次写成第 2 版，对照上一次（第 1 版记录）的完成时刻");

        // 认应用：App/Game/Browser 类所属对象，同一对象嵌套的根只算外层，优先显示已确认的安装位置
        var t = new FileTree(@"X:\");
        int Dir(int parent, string name) => t.AddNode(parent, name, NodeFlags.Directory, 0, 4096, 0);
        void Fil(int parent, string name, long size) => t.AddNode(parent, name, 0, size, size, 0);
        int steam = Dir(Dir(0, "Program Files (x86)"), "Steam");
        Fil(steam, "steam.exe", 7 << 20);
        int portal = Dir(Dir(Dir(steam, "steamapps"), "common"), "Portal 2");
        Fil(portal, "portal2.exe", 20 << 20);
        int localSteam = Dir(Dir(Dir(Dir(Dir(0, "Users"), "Alex"), "AppData"), "Local"), "Steam");
        Fil(localSteam, "c.bin", 4 << 20);
        Fil(Dir(Dir(0, "Windows"), "System32"), "k.dll", 1 << 20);
        t.Seal();
        t.SetClassification(PurposeClassifier.Classify(t, PurposeChecks.FakeEnv(), CancellationToken.None));
        var (top, apps) = SummaryStore.Capture(t);
        ctx.Info("认出的应用：" + string.Join("；", apps.Select(x => $"{x.Kind} {x.Name} {x.Path} {x.Alloc} 安装位置={x.Installation}")));
        ctx.True(top is [{ Name: "Program Files (x86)" }, ..] && top.Length == 3 && top.All(x => x.IsDir), "大项：根下面的三项，占用大的在前");
        ctx.True(apps.Select(x => x.Name).Order().SequenceEqual(["Portal 2", "Steam"]) && apps.All(x => x.FirstSeenMs == 0 && !x.IsNew),
            "只认应用、游戏、浏览器（Windows 这种系统组件不算），对照的字段留给保存时填");
        var steamApp = apps.Single(x => x.Name == "Steam");
        ctx.True(steamApp is { Kind: OwnerKind.App } && steamApp.Alloc == t.CountedValue(steam, Metric.Alloc) + t.CountedValue(localSteam, Metric.Alloc),
            "Steam 两处根（程序目录、AppData）的占用相加");
        ctx.True(apps.Single(x => x.Name == "Portal 2") is { Kind: OwnerKind.Game } p2 && p2.Path == t.GetPath(portal), "嵌在 Steam 里的游戏单独列出");
        var appStore = new SummaryStore(Path.Combine(sandbox, "compare", "apps.json"));
        ctx.True(appStore.Save(first with { RootPath = @"X:\" }, t) && appStore.Knows(steamApp.Path) && appStore.Knows(@"X:\")
            && !appStore.Knows(@"X:\Users"),"认出的应用的位置也在记录里，外壳可以打开；别的位置不认");
    }

    static (ScanSummaryDto? Done, ScanErrorDto? Error) Finish(ScanService service, string path)
    {
        var end = new TaskCompletionSource<(ScanSummaryDto?, ScanErrorDto?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Completed += s => end.TrySetResult((s, null));
        service.Failed += e => end.TrySetResult((null, e));
        service.Start(new(path, ScanMode.Walk));
        return end.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }

    static string Fixture(string sandbox, string name)
    {
        string dir = Directory.CreateDirectory(Path.Combine(sandbox, name)).FullName;
        File.WriteAllText(Path.Combine(dir, "a.txt"), name);
        return dir;
    }

    static bool Same(SavedSummaryDto s, ScanSummaryDto x) =>
        s.RootPath == x.RootPath && s.Mode == x.Mode && s.FileSystem == x.FileSystem && s.IsVolumeRoot == x.IsVolumeRoot
        && s.CompletedAtMs == x.CompletedAtMs && x.CompletedAtMs > 0 && s.ElapsedMs == x.ElapsedMs && s.Files == x.Files
        && s.Dirs == x.Dirs && s.ScannedAlloc == x.ScannedAlloc && s.ScannedSize == x.ScannedSize && s.VolumeTotal == x.VolumeTotal
        && s.VolumeFree == x.VolumeFree && s.PurposeBytes.SequenceEqual(x.PurposeBytes!) && s.InaccessibleDirs == x.InaccessibleDirs
        && s.LinksSkipped == x.LinksSkipped && s.MountPointsSkipped == x.MountPointsSkipped && s.Errors == x.Errors
        && s.UnaccountedBytes == x.UnaccountedBytes && s.GapParts.SequenceEqual(x.GapParts!);
}
