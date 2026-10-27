using DiskLens.Core;

namespace DiskLens.Check;

static partial class DeleteChecks
{
    static void Progress(CheckContext ctx, string root)
    {
        string dir = Path.Combine(root, "whole");
        Put(Path.Combine(dir, "a.bin"), 10);
        Put(Path.Combine(dir, "sub", "b.bin"), 20);
        string single = Put(Path.Combine(root, "single.bin"), 30);
        var disk = Disk.Scan(root);
        var snapshot = DeletionSnapshot.Capture(disk.Tree, [disk[dir], disk[single]], false, CancellationToken.None);
        var progress = new List<DeleteProgressDto>();
        var result = snapshot.Delete(true, CancellationToken.None, progress.Add);
        ctx.True(result.Ok && progress[^1] is { Total: 2, Completed: 2, Failed: 0, Skipped: 0, ProcessedEntries: 5 }, "删除进度：确认目标与文件/目录分别计数");
        ctx.True(progress.Any(p => p.CurrentPath == dir) && progress.Any(p => p.CurrentPath == single), "删除进度包含当前确认目标");
        ctx.True(progress.Take(progress.Count - 1).All(p => p.FreedBytes is null) && progress[^1].FreedBytes == result.FreedBytes,
            "删除进度：对账前释放量未知，最终和删除结果一致");
        Consistent(ctx, disk.Tree, "进度删除");

        string a = Put(Path.Combine(root, "cancel-a.bin")), b = Put(Path.Combine(root, "cancel-b.bin")), c = Put(Path.Combine(root, "cancel-c.bin"));
        disk = Disk.Scan(root);
        snapshot = DeletionSnapshot.Capture(disk.Tree, [disk[a], disk[b], disk[c]], false, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        progress.Clear();
        result = snapshot.Delete(true, cancel.Token, p =>
        {
            progress.Add(p);
            if (p.Completed == 1) cancel.Cancel();
        });
        ctx.True(!result.Ok && !Exists(a) && Exists(b) && Exists(c), "完成第一项后取消，保留所有后续目标");
        ctx.True(progress[^1] is { Total: 3, Completed: 1, Failed: 0, Skipped: 2, ProcessedEntries: 1 }, "部分取消：完成和跳过不伪报为失败");
        ctx.True(result.Deleted == 1 && result.FreedBytes > 0 && progress[^1].FreedBytes == result.FreedBytes, "部分取消对已经删除的条目照常对账");
        Consistent(ctx, disk.Tree, "进度取消");

        string locked = Put(Path.Combine(root, "locked.bin")), free = Put(Path.Combine(root, "free.bin"));
        disk = Disk.Scan(root);
        snapshot = DeletionSnapshot.Capture(disk.Tree, [disk[locked], disk[free]], false, CancellationToken.None);
        progress.Clear();
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            result = snapshot.Delete(true, CancellationToken.None, progress.Add);
        ctx.True(!result.Ok && Exists(locked) && !Exists(free), "进度反馈不影响失败项目之外的执行");
        ctx.True(progress[^1] is { Total: 2, Completed: 1, Failed: 1, Skipped: 0, ProcessedEntries: 2 }, "失败目标和完成目标计数明确");
        Consistent(ctx, disk.Tree, "进度失败");

        disk = Disk.Scan(root);
        snapshot = DeletionSnapshot.Capture(disk.Tree, [disk[b], disk[c]], false, CancellationToken.None);
        using var before = new CancellationTokenSource();
        progress.Clear();
        result = snapshot.Delete(true, before.Token, p => { progress.Add(p); before.Cancel(); });
        ctx.True(Exists(b) && Exists(c) && result.Deleted == 0 && progress[^1] is { Completed: 0, Skipped: 2 }, "首次进度时取消，尚未开始的文件都保留");
    }
}
