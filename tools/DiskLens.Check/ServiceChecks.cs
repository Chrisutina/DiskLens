using System.Reflection;
using DiskLens.Core;

namespace DiskLens.Check;

static class ServiceChecks
{
    public static void Run(CheckContext ctx)
    {
        OperationChecks.Run(ctx);
        string sandbox = ctx.Sandbox();
        File.WriteAllText(Path.Combine(sandbox, "fixture.txt"), "fixture");
        using var service = new ScanService();
        using var done = new ManualResetEventSlim();
        string? failure = null;
        service.Completed += _ => done.Set();
        service.Failed += e => { failure = e.Message; done.Set(); };
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        service.Start(new(sandbox, ScanMode.Walk));
        ctx.True(done.Wait(TimeSpan.FromSeconds(20)) && failure == null, "沙箱扫描完成：" + failure);
        if (service.Summary is not { } scanned) return;

        // 最近文件的时间基准：结果换上那一刻记下的完成时刻，按树身份取，别的树要不到
        long completed = service.CompletedAtMs(service.Tree!);
        ctx.True(before <= completed && completed <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "完成时刻落在开始扫描和收到完成事件之间");
        ctx.Equal(completed, scanned.CompletedAtMs, "汇总带完成时刻，和最近文件的时间基准是同一个数");
        ctx.Equal(completed, service.RefreshSummary()?.CompletedAtMs, "刷新汇总不改完成时刻");
        bool refused = false;
        try { service.CompletedAtMs(new FileTree(sandbox, 1)); }
        catch (InvalidOperationException) { refused = true; }
        ctx.True(refused, "不是当前结果的树要完成时刻：报错，不拿当前结果的时刻充数");

        // 用同一棵 20 万节点内存树量汇总，不靠磁盘冷热缓存改变样本。
        var tree = new FileTree(sandbox, 200_001);
        for (int i = 0; i < 200_000; i++) tree.AddNode(0, $"f{i}.txt", 0, 100, 4096, 0);
        tree.Seal();
        Set(service, "_tree", tree);
        // 扫描报告的计数是扫描时的数字：树里一个无权访问的目录都没有，刷新也不能把它们重算成 0
        Set(service, "_summary", scanned with { InaccessibleDirs = 7, LinksSkipped = 3, MountPointsSkipped = 2, Errors = 5 });
        var first = ctx.Time("20万节点首次汇总", service.RefreshSummary);
        var second = ctx.Time("同树重复汇总", service.RefreshSummary);
        ctx.True(first?.Files == 200_000 && second?.Files == 200_000, "汇总文件数正确");
        ctx.True(second is { InaccessibleDirs: 7, LinksSkipped: 3, MountPointsSkipped: 2, Errors: 5 },
            "刷新汇总保留扫描时的拒绝访问、链接、挂载点、错误计数（和异常清单的总数对得上）");
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() => { lock (tree.Sync) { held.Set(); release.Wait(); } });
        held.Wait();
        var refresh = Task.Run(service.RefreshSummary);
        Thread.Sleep(50);
        var cancel = Task.Run(service.Cancel);
        bool responsive = cancel.Wait(TimeSpan.FromMilliseconds(200));
        release.Set();
        Task.WaitAll(holder, refresh, cancel);
        ctx.Info($"长查询持树锁时 cancel 在 200ms 内返回：{responsive}");
        if (!ctx.Flag("baseline")) ctx.True(responsive, "summary 等树锁时不占服务锁，cancel 保持响应");

        GapParts(ctx);
        IssueLog(ctx);
        IssuesFollowScan(ctx, sandbox);
        PhaseEvents(ctx, sandbox);
        CancelEvents(ctx);
    }

    /// 小文件夹扫得比 120 ms 的采样间隔还快，每个阶段照样各有一条进度（dise §4.2）；终态之后不再冒出进度。
    static void PhaseEvents(CheckContext ctx, string sandbox)
    {
        using var service = new ScanService();
        var log = new List<string>();
        service.Progress += p => { lock (log) log.Add(p.Phase + (p.Cancelling ? "（取消中）" : "")); };
        service.Completed += _ => { lock (log) log.Add("完成"); };
        var summary = ScanAndWait(service, sandbox);
        Thread.Sleep(300); // 再等过两个采样周期：终态之后定时采样不能再冒出来
        string[] events;
        lock (log) events = [.. log];
        var phases = events.Where((e, i) => i == 0 || e != events[i - 1]).ToArray();
        ctx.Info($"{summary.ElapsedMs} ms 的扫描收到 {events.Length} 条事件：{string.Join(" → ", phases)}");
        ctx.Equal("prepare → walk → links → finalize → 完成", string.Join(" → ", phases), "每个阶段一变就有一条进度，最后是完成，之后没有进度");
    }

    /// 取消：接受的同时就有一条「取消中」的进度；之后的进度都标着取消中；后台真正停下才发 scan.error，之后再没有进度。
    /// 只读扫真实的 Windows 目录，体量够大，取消一定发生在途中。
    static void CancelEvents(CheckContext ctx)
    {
        using var service = new ScanService();
        ctx.True(!service.Cancel(), "没有扫描在跑时取消：返回 false");
        var log = new List<ScanProgressDto?>(); // null 是终态事件
        var ended = new TaskCompletionSource<(ScanErrorDto Error, bool Scanning)>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Progress += p => { lock (log) log.Add(p); };
        service.Failed += e =>
        {
            bool scanning = service.IsScanning;
            lock (log) log.Add(null);
            ended.TrySetResult((e, scanning));
        };
        service.Completed += _ => ended.TrySetException(new Exception("扫描在取消之前就结束了"));
        service.Start(new(Environment.GetFolderPath(Environment.SpecialFolder.Windows), ScanMode.Walk));
        SpinWait.SpinUntil(() => { lock (log) return log.Any(p => p is { Phase: "walk", Files: > 500 }); }, TimeSpan.FromSeconds(20));
        int before;
        lock (log) before = log.Count;
        bool accepted = service.Cancel();
        ScanProgressDto?[] atCancel;
        lock (log) atCancel = [.. log];
        ctx.True(accepted, "扫描进行中取消：被接受");
        ctx.True(atCancel.Skip(before).Any(p => p is { Cancelling: true }), "Cancel 返回之前就已经发出「取消中」的进度，不等下一次采样");
        var (error, scanningAtEnd) = ended.Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        Thread.Sleep(300);
        ScanProgressDto?[] all;
        lock (log) all = [.. log];
        int first = Array.FindIndex(all, p => p is { Cancelling: true });
        ctx.True(error.Cancelled, "终态是 scan.error（cancelled=true）");
        ctx.True(!scanningAtEnd, "发终态事件时服务已经不在扫描中");
        ctx.True(all[^1] == null && all.Count(p => p == null) == 1, "终态事件只有一条，之后不再有进度");
        ctx.True(all.Take(first).All(p => p is { Cancelling: false }) && all[first..^1].All(p => p is { Cancelling: true }),
            "取消之前的进度不带取消中，取消之后的每一条都带");
        ctx.True(all[first..^1].All(p => p!.Phase == all[first]!.Phase), "取消中阶段停在被打断的那一步");
    }

    /// 清单记录上限：并发记录时总数始终准确，记下的条数封顶在上限，翻页和越界都不出错。
    static void IssueLog(CheckContext ctx)
    {
        var c = new ScanCounters();
        int over = ScanCounters.MaxIssues + 5;
        Parallel.For(0, over, i => c.Record(ScanIssueKind.Denied, $@"X:\d{i:D6}", "拒绝访问"));
        c.Record(ScanIssueKind.Error, @"X:\e", "坏了");
        c.SortIssues();
        var (total, recorded, items) = c.Page(ScanIssueKind.Denied, 0, 1000);
        ctx.Equal((long)over, total, "并发记录：总数准确，不受上限影响");
        ctx.Equal((long)over, c.InaccessibleDirs, "计数器和清单总数是同一个数");
        ctx.Equal(ScanCounters.MaxIssues, recorded, "记下的条数封顶在上限");
        ctx.True(items.Length == 1000 && items.Select(x => x.Path).SequenceEqual(items.Select(x => x.Path).Order(StringComparer.OrdinalIgnoreCase)),
            "一页 1000 条，按路径排好");
        ctx.Equal(3, c.Page(ScanIssueKind.Denied, ScanCounters.MaxIssues - 3, 1000).Items.Length, "翻到已记下的末尾只剩 3 条");
        ctx.Equal(0, c.Page(ScanIssueKind.Denied, ScanCounters.MaxIssues + 100, 10).Items.Length, "翻过已记下的部分是空页，不抛");
        ctx.True(c.Page(ScanIssueKind.Link, 0, 10) is (0, 0, []), "没有的种类：总数 0、空清单");
        ctx.True(c.Page(ScanIssueKind.Error, 0, 10) is (1, 1, [{ Path: @"X:\e", Reason: "坏了" }]), "各类分开记");
    }

    /// scan.issues 只认当前结果那次扫描：换扫描后旧清单作废，翻页的截断标志说实话。
    static void IssuesFollowScan(CheckContext ctx, string sandbox)
    {
        string withLink = Directory.CreateDirectory(Path.Combine(sandbox, "with-link")).FullName;
        string plain = Directory.CreateDirectory(Path.Combine(sandbox, "plain")).FullName;
        File.WriteAllText(Path.Combine(plain, "a.txt"), "a");
        string link = Path.Combine(withLink, "junction");
        WalkChecks.MakeJunction(link, plain);
        try
        {
            using var service = new ScanService();
            ctx.True(Throws<InvalidOperationException>(() => service.Issues(new(ScanIssueKind.Link))), "还没有扫描结果时要清单：报错，不给空清单冒充");
            var first = ScanAndWait(service, withLink);
            var page = service.Issues(new(ScanIssueKind.Link, ScanId: first.ScanId));
            ctx.True(page is { Total: 1, Recorded: 1, Truncated: false, Items: [{ Path: var p }] } && p == link && page.ScanId == first.ScanId,
                "第一次扫描的链接清单：1 条，就是那个 junction");
            var empty = service.Issues(new(ScanIssueKind.Link, Offset: 1, ScanId: first.ScanId));
            ctx.True(empty is { Total: 1, Offset: 1, Items: [], Truncated: false }, "翻过末尾：空页，总数照旧");
            var head = service.Issues(new(ScanIssueKind.Link, Limit: 0));
            ctx.True(head is { Items.Length: 1 }, "Limit 夹到至少 1");
            ctx.True(Throws<ArgumentException>(() => service.Issues(new((ScanIssueKind)9))), "未知的异常种类报错");

            var second = ScanAndWait(service, plain);
            ctx.True(Throws<ArgumentException>(() => service.Issues(new(ScanIssueKind.Link, ScanId: first.ScanId))),
                "换了扫描之后拿旧扫描的编号要清单：报错，不拿新扫描的清单充数");
            ctx.True(service.Issues(new(ScanIssueKind.Link)) is { Total: 0, Items: [] } now && now.ScanId == second.ScanId,
                "不带编号：给当前结果那次扫描的清单，并写明是哪次");
        }
        finally { WalkChecks.RemoveLink(link); }
    }

    internal static ScanSummaryDto ScanAndWait(ScanService service, string path)
    {
        var done = new TaskCompletionSource<ScanSummaryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnDone(ScanSummaryDto s) => done.TrySetResult(s);
        void OnFail(ScanErrorDto e) => done.TrySetException(new Exception(e.Message));
        service.Completed += OnDone;
        service.Failed += OnFail;
        try
        {
            service.Start(new(path, ScanMode.Walk));
            return done.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        }
        finally
        {
            service.Completed -= OnDone;
            service.Failed -= OnFail;
        }
    }

    static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    static void Set(ScanService service, string field, object value) =>
        typeof(ScanService).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, value);

    /// 未解释容量的拆分（dise §4.3 第 4 项）：有依据的才拆出来，其余原因未确认；各份之和等于差额。只读问系统盘 $MFT 的大小。
    static void GapParts(CheckContext ctx)
    {
        string system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        long mft = Volumes.MftBytes(system);
        ctx.True(mft > 0, $"不用管理员也能问到系统盘 $MFT 的大小（{mft:N0} B）");
        const long gap = 1L << 40;
        var walk = ScanService.SplitGap(system, ScanMode.Walk, "NTFS", gap);
        ctx.True(walk is [{ Kind: GapKind.NtfsMetadata }, { Kind: GapKind.Unconfirmed }] && walk[0].Bytes == mft,
            "常规扫描：$MFT 算有依据的一份，剩下的原因未确认");
        ctx.Equal(gap, walk.Sum(p => p.Bytes), "各份之和等于差额");
        ctx.True(walk.All(p => p.Basis.Length > 0) && walk[1].Basis.StartsWith("原因未确认", StringComparison.Ordinal), "每份都写了依据，余数写明原因未确认");
        ctx.True(ScanService.SplitGap(system, ScanMode.Walk, "NTFS", 4096) is [{ Kind: GapKind.NtfsMetadata, Bytes: 4096 }],
            "差额比 $MFT 还小：有依据的那份封顶在差额，不凭空多出一份");
        ctx.True(ScanService.SplitGap(system, ScanMode.Mft, "NTFS", gap) is [{ Kind: GapKind.Unconfirmed, Bytes: gap }],
            "MFT 扫描已经把元数据算进树里，差额全部原因未确认");
        ctx.True(ScanService.SplitGap(system, ScanMode.Walk, "exFAT", gap) is [{ Kind: GapKind.Unconfirmed, Bytes: gap }], "不是 NTFS 就没有 $MFT 可问");
        ctx.True(ScanService.SplitGap(system, ScanMode.Walk, "NTFS", 0).Length == 0, "没有差额就没有拆分");
    }
}
