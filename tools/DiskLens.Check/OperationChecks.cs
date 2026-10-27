using DiskLens.Core;

namespace DiskLens.Check;

static class OperationChecks
{
    public static void Run(CheckContext ctx)
    {
        var store = new OperationStore();
        var events = new List<OperationStatusDto>();
        store.Changed += p => { lock (events) events.Add(p); };
        var (entry, created) = store.Register("first", "delete", "same request");
        ctx.True(created && store.Status("first") is { Status: "queued", FreedBytes: null }, "任务在执行前可查询，释放量尚未知");
        ctx.True(store.Status("absent") is null, "不存在的任务查询返回 null");
        Reject(ctx, () => store.Cancel("absent"), "不存在的任务不能取消");
        Reject(ctx, () => store.Status(null!), "无效任务编号拒绝");
        Reject(ctx, () => store.Register("", "delete", "x"), "空编号不能生成新任务");
        Reject(ctx, () => store.Register(new string('a', 129), "delete", "x"), "过长编号拒绝");
        var duplicate = store.Register("first", "delete", "same request");
        ctx.True(!duplicate.Created && ReferenceEquals(entry, duplicate.Entry), "相同编号和参数复用原任务");
        Reject(ctx, () => store.Register("first", "delete", "changed request"), "相同编号不同参数拒绝");
        Reject(ctx, () => store.Register("first", "cleanup", "same request"), "相同编号不同操作拒绝");

        var cancel = store.Cancel("first");
        ctx.True(cancel.Status == "cancelling" && entry.Token.IsCancellationRequested, "排队任务立即接受取消");
        entry.Fail(new OperationCanceledException(), "操作已取消");
        ctx.Equal("cancelled", store.Status("first")!.Status, "排队取消终态可查询");
        ctx.True(entry.ResultAsync().IsCompleted, "取消后原请求不再无限等待");
        try { entry.ResultAsync().GetAwaiter().GetResult(); ctx.Fail("取消请求应报告取消"); }
        catch (OperationCanceledException) { }

        var running = store.Register("partial", "delete", "partial").Entry;
        running.Start();
        running.Report(new("fixture", 3, 1, 0, 0, 4));
        store.Cancel("partial");
        var partial = new OpResultDto(false, 4096, 4, 0, [], "操作已取消；已完成的条目已更新", 2);
        running.Report(new(null, 3, 1, 0, 2, 4, 4096));
        running.Complete(partial);
        var snapshot = store.Status("partial")!;
        ctx.True(snapshot is { Status: "cancelled", Completed: 1, Skipped: 2, ProcessedEntries: 4, FreedBytes: 4096 }, "运行中取消保留部分结果与真实计数");
        ctx.True(ReferenceEquals(partial, running.ResultAsync().GetAwaiter().GetResult()), "重复查询保留同一结果");
        running.Report(new("late event", 99, 0, 0, 0, 0));
        store.Cancel("partial");
        ctx.Equal(snapshot, store.Status("partial"), "终态不会被迟到进度或取消覆盖");

        var external = store.Register("terminal", "cleanup", "open command").Entry;
        external.Start();
        external.External = true;
        external.Complete(new(true, 0, 0, 0, [], "已在新终端打开清理命令", 1));
        ctx.True(store.Status("terminal") is { Status: "external", FreedBytes: null, Completed: 0 }, "外部终端仅报告已打开，不能虚报清理完成");
        store.Cancel("terminal");
        ctx.True(!external.Token.IsCancellationRequested, "取消终态不会再触发取消令牌或副作用");
        ctx.True(store.Register(null, "delete", "legacy").Entry.Snapshot.OperationId.Length > 0, "旧调用自动生成任务编号");

        int executed = 0;
        var duplicates = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            var registration = store.Register("concurrent", "delete", "same");
            if (registration.Created)
            {
                Interlocked.Increment(ref executed);
                registration.Entry.Start();
                registration.Entry.Complete(new(true, 0, 0, 0, [], null, 1));
            }
            return await registration.Entry.ResultAsync().WaitAsync(TimeSpan.FromSeconds(5));
        })).ToArray();
        ctx.True(Task.WaitAll(duplicates, TimeSpan.FromSeconds(10)), "并发重复请求均得到结果");
        ctx.Equal(1, executed, "20 个并发同编号请求只执行一次");
        lock (events)
            foreach (var group in events.GroupBy(e => e.OperationId))
            {
                var timestamps = group.Select(e => e.UpdatedAtMs).Order().ToArray();
                ctx.Equal(timestamps.Length, timestamps.Distinct().Count(), "每次状态更新使用唯一时间戳：" + group.Key);
            }

        var full = new OperationStore();
        for (int i = 0; i < 1024; i++) full.Register(i.ToString(), "delete", i.ToString());
        Reject(ctx, () => full.Register("overflow", "delete", "new"), "满 1024 个任务拒绝新的任务");
        ctx.True(!full.Register("0", "delete", "0").Created && full.Status("0") != null, "满额仍保留旧编号和结果，不能重复执行");

        var failedSubscriber = new OperationStore();
        failedSubscriber.Changed += _ => throw new InvalidOperationException("模拟界面已关闭");
        var survivor = failedSubscriber.Register("survivor", "delete", "same").Entry;
        survivor.Start();
        survivor.Complete(new(true, 0, 0, 0, [], "done", 1));
        ctx.True(survivor.ResultAsync().GetAwaiter().GetResult().Ok && failedSubscriber.Status("survivor")!.Status == "completed",
            "进度订阅者异常不能让任务永远停在排队或丢失结果");
    }

    static void Reject(CheckContext ctx, Action action, string description)
    {
        bool refused = false;
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { refused = true; }
        ctx.True(refused, description);
    }
}
