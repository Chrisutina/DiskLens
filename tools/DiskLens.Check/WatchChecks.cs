using System.Collections.Concurrent;
using System.Diagnostics;
using DiskLens.Core;

namespace DiskLens.Check;

// 实时状态（TreeWatcher）：扫描之后别的程序删掉的东西，树里跟着标删除；DiskLens 自己删的不重复算。
// 磁盘上的事全在 .sandbox/watch 里。计时器关掉、手动 Flush；通知是异步到的，靠「哨兵」确认前面的通知都处理过了。
static class WatchChecks
{
    public static void Run(CheckContext ctx)
    {
        string box = ctx.Sandbox();
        var bare = new FileTree(box, 1);
        bare.Seal();
        using (var none = new TreeWatcher(bare, 1, Path.Combine(box, "不存在"), Timeout.Infinite))
        {
            none.Flush();
            ctx.True(!none.Watching && none.Snapshot() is { Watching: false, RemovedEntries: 0, OtherChanges: 0 }, "目录不存在：不跟踪，也不抛");
        }
        External(ctx, Path.Combine(box, "external"));
        Service(ctx, Path.Combine(box, "service"));
    }

    static void External(CheckContext ctx, string root)
    {
        string a = Put(root, "a.txt", 5000), b = Put(root, "b.txt", 10), keep = Put(root, "keep.txt", 10);
        string saved = Put(root, "saved.txt", 10), mine = Put(root, "mine.txt", 3000), held = Put(root, "held.txt", 10);
        string dir = Path.Combine(root, "dir");
        for (int i = 0; i < 3; i++) Put(dir, $"f{i}.txt", 100);
        var sentinels = new Queue<string>(Enumerable.Range(0, 8).Select(i => Put(root, $"s{i}.txt", 1)));
        var tree = WalkScanner.Scan(root, new ScanCounters(), CancellationToken.None);
        tree.Seal();
        int Id(string path) { lock (tree.Sync) return tree.FindByPath(path); }
        bool Gone(int id) { lock (tree.Sync) return tree.IsDeleted(id); }
        var ids = new[] { a, b, keep, saved, mine, held, dir }.ToDictionary(p => p, Id);

        using var watcher = new TreeWatcher(tree, 7, root, Timeout.Infinite);
        var events = new List<TreeChangeDto>();
        watcher.Changed += c => { lock (events) events.Add(c); };
        ctx.True(watcher.Watching && watcher.Snapshot() is { ScanId: 7, Watching: true, RemovedEntries: 0, OtherChanges: 0, Overflow: false },
            "本地目录：在跟踪，刚开始什么都没变");

        // 删一个扫描前就在的哨兵，等它从树里消失：通知按顺序到，它前面的通知也都处理过了
        bool Settle()
        {
            string s = sentinels.Dequeue();
            int id = Id(s);
            File.Delete(s);
            return Until(() => { watcher.Flush(); return Gone(id); });
        }
        long removed = 0;

        int version = tree.Version;
        File.Delete(a);
        ctx.True(Settle(), "哨兵按时从树里消失");
        removed += 2;
        ctx.True(Gone(ids[a]) && !Gone(ids[keep]), "别的程序删的文件：树里跟着标删除，没删的照旧");
        ctx.True(watcher.Snapshot() is { } s1 && s1.RemovedEntries == removed && s1.TreeVersion == tree.Version && tree.Version > version,
            "累计删掉的条目数对得上，带回最新树版本");
        lock (events) ctx.True(events.Count > 0 && events[^1].RemovedEntries == removed, "有变化就发 Changed，数字和快照一致");

        Directory.Delete(dir, true);
        ctx.True(Settle() && Gone(ids[dir]), "整个目录被删：目录节点标删除");
        removed += 4 + 1;
        ctx.Equal(removed, watcher.Snapshot().RemovedEntries, "目录被删：目录自己加里面 3 个文件，每项只算一次");

        long other = watcher.Snapshot().OtherChanges;
        File.Delete(saved);
        File.WriteAllText(saved, "新内容");
        ctx.True(Settle() && !Gone(ids[saved]), "删了又马上建回同名文件（编辑器保存）：不当删除");
        removed += 1;
        ctx.True(watcher.Snapshot().OtherChanges > other, "建回来的文件记成「新建」，提示统计可能过期");

        other = watcher.Snapshot().OtherChanges;
        File.Move(b, Path.Combine(root, "b2.txt"));
        ctx.True(Settle() && Gone(ids[b]), "改名：旧名字从树里消失");
        removed += 2;
        ctx.True(watcher.Snapshot().OtherChanges > other, "改名出来的新名字记成「新建或改名」");

        var r = DeleteService.Delete(tree, [ids[mine]], true, CancellationToken.None);
        ctx.True(r.Ok && r.FreedBytes > 0 && r.AlreadyGone == 0, "DiskLens 自己删：释放量照常算在这次删除上");
        ctx.True(Settle(), "自己删完之后再处理通知");
        removed += 1;
        ctx.Equal(removed, watcher.Snapshot().RemovedEntries, "自己删的不会被跟踪再算一遍");

        var gate = DeleteService.GateOf(tree);
        gate.Wait();
        try
        {
            File.Delete(held);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 600) { watcher.Flush(); Thread.Sleep(30); }
            ctx.True(!Gone(ids[held]), "DiskLens 正在删（写门被占着）：外部删除先攒着不处理");
            watcher.Lost("自检：模拟通知缓冲溢出");
            ctx.True(!watcher.Snapshot().Overflow, "自己正在删时缓冲溢出：丢的多半是自己删的，不报跟丢");
        }
        finally { gate.Release(); }
        ctx.True(Settle() && Gone(ids[held]), "写门放开后：攒着的删除照样落进树");
        removed += 2;
        ctx.Equal(removed, watcher.Snapshot().RemovedEntries, "最后累计");
        ctx.True(!watcher.Snapshot().Overflow, "没有跟丢");
        watcher.Lost("自检：模拟通知缓冲溢出");
        ctx.True(watcher.Snapshot().Overflow, "没人在删时缓冲溢出：报跟丢，提示重扫");
    }

    static void Service(CheckContext ctx, string root)
    {
        string gone = Put(root, "gone.txt", 10), stay = Put(root, "stay.txt", 10);
        using var service = new ScanService();
        using var changes = new BlockingCollection<TreeChangeDto>();
        service.TreeChanged += changes.Add;
        var first = ServiceChecks.ScanAndWait(service, root);
        ctx.True(changes.TryTake(out var initial, 5000) && initial is { Watching: true, RemovedEntries: 0 } && initial.ScanId == first.ScanId,
            "扫描完成：先发一条初始状态，带这次扫描的编号");
        long files = first.Files;
        File.Delete(gone);
        ctx.True(Next(changes, c => c.ScanId == first.ScanId && c.RemovedEntries == 1), "别的程序删了文件：计时器自己处理，发 tree.changed");
        ctx.Equal(files - 1, service.RefreshSummary()?.Files, "刷新汇总：文件数跟着少一个");

        var second = ServiceChecks.ScanAndWait(service, root);
        ctx.True(Next(changes, c => c.ScanId == second.ScanId && c.RemovedEntries == 0), "重扫完成：换成跟踪新结果，编号是新扫描的");
        File.Delete(stay);
        ctx.True(Next(changes, c => c.ScanId == second.ScanId && c.RemovedEntries == 1), "新结果照样跟踪");

        // 树建好了、跟踪还没接上的那一下（完成事件发出的时候）别的程序删了文件：跟踪是扫描前就开始的，攒着的照样落进树
        string window = Put(root, "window.txt", 10);
        void DeleteOnDone(ScanSummaryDto _) => File.Delete(window);
        service.Completed += DeleteOnDone;
        ScanSummaryDto third;
        try { third = ServiceChecks.ScanAndWait(service, root); }
        finally { service.Completed -= DeleteOnDone; }
        ctx.True(!File.Exists(window) && Next(changes, c => c.ScanId == third.ScanId && c.RemovedEntries == 1),
            "扫完到开始跟踪之间删掉的：不用重扫也同步进树");
        var tree = service.Tree!;
        lock (tree.Sync) ctx.True(tree.FindByPath(window) is var n && (n < 0 || tree.IsDeleted(n)), "树里那个文件标删除了");
    }

    static bool Next(BlockingCollection<TreeChangeDto> changes, Func<TreeChangeDto, bool> match)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
            if (changes.TryTake(out var c, 200) && match(c)) return true;
        return false;
    }

    static bool Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) return false;
            Thread.Sleep(20);
        }
        return true;
    }

    static string Put(string dir, string name, int size)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }
}
