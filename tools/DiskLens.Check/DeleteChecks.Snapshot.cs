using DiskLens.Core;

namespace DiskLens.Check;

// 确认框快照与令牌（DESIGN §9.2）：确认时拍下对象和身份，执行时只删确认过的那一份。
static partial class DeleteChecks
{
    static void Snapshots(CheckContext ctx, string root)
    {
        // 正常路径：快照里的目录和文件都删掉，树和磁盘对得上
        string dir = Path.Combine(root, "plain");
        string a = Put(Path.Combine(dir, "a.txt"), 10), b = Put(Path.Combine(dir, "sub", "b.txt"), 20);
        string single = Put(Path.Combine(root, "single.txt"), 30);
        var disk = Disk.Scan(root);
        var snap = DeletionSnapshot.Capture(disk.Tree, [disk[dir], disk[single]], contents: false, CancellationToken.None);
        ctx.True(snap.Matches(disk.Tree, [disk[single], disk[dir]]), "快照：同一棵树、同一组目标（顺序无关）才算匹配");
        ctx.True(!snap.Matches(Disk.Scan(root).Tree, [disk[single], disk[dir]]), "快照：换了一棵树就不匹配");
        var done = snap.Delete(permanent: true, CancellationToken.None);
        ctx.True(done.Ok && !Exists(dir) && !Exists(single) && !Exists(a) && !Exists(b), "快照：确认过的目录和文件都删了：" + done.Message);
        Consistent(ctx, disk.Tree, "快照删除");

        // 被别的程序独占打开的文件：确认照样能核对（只读属性，不参与共享检查），删除时它失败、其余照删
        string busyDir = Path.Combine(root, "busy");
        string locked = Put(Path.Combine(busyDir, "locked.tmp"), 3000), free = Put(Path.Combine(busyDir, "free.tmp"), 40);
        disk = Disk.Scan(root);
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            snap = DeletionSnapshot.Capture(disk.Tree, [disk[busyDir]], contents: true, CancellationToken.None);
            ctx.Equal(2, snap.Roots.Count, "独占打开的文件：清空目录的快照照样拍得下（两个子项都在清单里）");
            done = snap.Delete(permanent: true, CancellationToken.None);
        }
        ctx.True(!done.Ok && done.Failed == 1 && Exists(locked) && !Exists(free) && Exists(busyDir), "独占打开的文件：只有它失败，其余删掉，目录保留");
        Consistent(ctx, disk.Tree, "独占文件");
        File.Delete(locked);

        // 确认之后文件被换成同名的另一个：身份对不上，不删
        string swapDir = Path.Combine(root, "swap");
        string swapped = Put(Path.Combine(swapDir, "x.txt"), 50), keep = Put(Path.Combine(swapDir, "y.txt"), 60);
        disk = Disk.Scan(root);
        snap = DeletionSnapshot.Capture(disk.Tree, [disk[swapped], disk[keep]], contents: false, CancellationToken.None);
        File.Delete(swapped);
        Put(swapped, 50); // 同名、同大小，但已经是另一个文件
        done = snap.Delete(permanent: true, CancellationToken.None);
        ctx.True(!done.Ok && Exists(swapped) && !Exists(keep), "确认后被替换的文件不删，没被动过的照删");
        ctx.True(done.Errors.Any(e => e.Message.Contains("替换")), "替换的原因写进了错误：" + string.Join(" / ", done.Errors.Select(e => e.Message)));

        // 身份刚核对完、还没删的那一瞬间被换成同名的新文件：删掉的必须是核对过的那一个（已被挪走），新来的不能动
        string raceDir = Path.Combine(root, "race");
        string raced = Put(Path.Combine(raceDir, "r.txt"), 50), movedAway = Path.Combine(raceDir, "r-moved.txt");
        disk = Disk.Scan(root);
        snap = DeletionSnapshot.Capture(disk.Tree, [disk[raced]], contents: false, CancellationToken.None);
        int swaps = 0;
        NativeFs.AfterVerify = p =>
        {
            if (!string.Equals(p, raced, StringComparison.OrdinalIgnoreCase)) return;
            File.Move(raced, movedAway);
            Put(raced, 50);
            swaps++;
        };
        try { done = snap.Delete(permanent: true, CancellationToken.None); }
        finally { NativeFs.AfterVerify = null; }
        ctx.True(swaps == 1 && done.Ok && Exists(raced) && !Exists(movedAway),
            $"核对和删除之间被换成同名新文件：新文件留着，删掉的是核对过的那一个（换了 {swaps} 次）：{done.Message}");

        // 确认时已经不在的条目记成缺席：执行时还不在就算删掉了；冒出同名新文件则拒绝
        string goneDir = Path.Combine(root, "gone");
        string vanished = Put(Path.Combine(goneDir, "v.tmp"), 70), reborn = Put(Path.Combine(goneDir, "r.tmp"), 80);
        string stay = Put(Path.Combine(goneDir, "s.tmp"), 90);
        disk = Disk.Scan(root);
        File.Delete(vanished);
        File.Delete(reborn);
        snap = DeletionSnapshot.Capture(disk.Tree, [disk[goneDir]], contents: true, CancellationToken.None);
        ctx.Equal(3, snap.Roots.Count, "扫描之后消失的文件不让整单确认失败");
        Put(reborn, 80);
        done = snap.Delete(permanent: true, CancellationToken.None);
        ctx.True(Exists(reborn) && !Exists(stay) && !Exists(vanished), "缺席的照旧算没了；确认后冒出来的同名文件不删");
        ctx.True(done.Failed == 1, "只有冒出来的那个算失败：" + string.Join(" / ", done.Errors.Select(e => e.Message)));

        // 回收站是整个目录搬走：确认后目录里多出东西，就得重新确认
        string grown = Path.Combine(root, "grown");
        Put(Path.Combine(grown, "old.txt"), 10);
        disk = Disk.Scan(root);
        snap = DeletionSnapshot.Capture(disk.Tree, [disk[grown]], contents: false, CancellationToken.None);
        int grownId = disk[grown];
        ctx.True(!ThrowsIo(() => snap.ValidateRoot(grownId, recycling: true, CancellationToken.None)), "回收站核对：内容没变时通过");
        Put(Path.Combine(grown, "new.txt"), 10);
        ctx.True(ThrowsIo(() => snap.ValidateRoot(grownId, recycling: true, CancellationToken.None)), "回收站核对：确认后目录里多了文件就拒绝这一项");
        ctx.True(!ThrowsIo(() => snap.ValidateRoot(grownId, recycling: false, CancellationToken.None)),
            "永久删除只删确认过的子项，多出来的东西留着，不需要拒绝");
        int inner = disk[Path.Combine(grown, "old.txt")];
        ctx.True(ThrowsIo(() => snap.ValidateRoot(inner, recycling: true, CancellationToken.None)), "不是确认过的目标（哪怕在它里面）一律拒绝");

        // 令牌：一次性、按树和清单匹配、限量、换扫描清空
        var store = new ConfirmationStore();
        var entry = new ConfirmationStore.Entry(disk.Tree, "user-temp", null, true, null);
        string token = store.Issue(entry);
        ctx.True(store.Take(token).Matches(disk.Tree, "user-temp", null, true), "令牌：取回的就是发出去的那份");
        ctx.True(ThrowsArg(() => store.Take(token)), "令牌：用过一次就作废");
        ctx.True(ThrowsArg(() => store.Take(null)), "令牌：没带令牌不能执行");
        ctx.True(!entry.Matches(disk.Tree, "user-temp", null, false), "令牌：永久删除开关变了不算同一次确认");
        ctx.True(!entry.Matches(disk.Tree, "user-temp", [1], true), "令牌：整条和只选一部分不是同一次确认");
        ctx.True(!entry.Matches(disk.Tree, "old-downloads", null, true), "令牌：换了建议不算");
        ctx.True(!entry.Matches(Disk.Scan(root).Tree, "user-temp", null, true), "令牌：换了扫描树不算");
        var picked = new ConfirmationStore.Entry(disk.Tree, null, [3, 1, 2], null, null);
        ctx.True(picked.Matches(disk.Tree, null, [1, 2, 3], null), "令牌：节点集合相同、顺序无关");
        string first = store.Issue(entry);
        for (int i = 0; i < 32; i++) store.Issue(entry);
        ctx.True(ThrowsArg(() => store.Take(first)), "令牌：最多留 32 个，最旧的被挤掉");
        string last = store.Issue(entry);
        store.Clear();
        ctx.True(ThrowsArg(() => store.Take(last)), "令牌：换扫描时全部作废");
    }

    static bool ThrowsIo(Action action)
    {
        try { action(); return false; }
        catch (IOException) { return true; }
    }

    static bool ThrowsArg(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
    }
}
