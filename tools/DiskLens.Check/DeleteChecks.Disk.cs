using System.Diagnostics;
using DiskLens.Core;

namespace DiskLens.Check;

// 真删磁盘的用例，全部发生在 .sandbox/delete 里。
static partial class DeleteChecks
{
    static void Same(CheckContext ctx, string expected, string actual, string what) =>
        ctx.True(string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase), $"{what}：期望 {expected}，实际 {actual}");

    static void Throws<T>(CheckContext ctx, Action action, string what) where T : Exception
    {
        try { action(); ctx.Fail($"{what}：应抛 {typeof(T).Name}，实际没抛"); }
        catch (T) { ctx.True(true, what); }
        catch (Exception ex) { ctx.Fail($"{what}：应抛 {typeof(T).Name}，实际抛 {ex.GetType().Name}：{ex.Message}"); }
    }

    /// 每个用例单独兜底：一个炸了别拖垮后面的。
    static void Case(CheckContext ctx, string name, Action body)
    {
        try { body(); }
        catch (Exception ex) { ctx.Fail($"{name} 未捕获的异常：{ex.GetType().Name}：{ex.Message}"); }
    }

    static void PermanentBasics(CheckContext ctx, string root)
    {
        Put(Path.Combine(root, "keep.txt"), 100);
        string file = Put(Path.Combine(root, "del file, 1.bin"), 5000); // 空格和逗号
        Put(Path.Combine(root, "中文目录", "文档 (副本).txt"), 10);
        string dir = Path.Combine(root, "del-dir");
        Put(Path.Combine(dir, "a.txt"), 10);
        Put(Path.Combine(dir, "b", "c.txt"), 4097);
        Dir(Path.Combine(dir, "empty"));
        File.SetAttributes(Put(Path.Combine(dir, "ro.txt"), 5), FileAttributes.ReadOnly); // 只读文件
        File.SetAttributes(Dir(Path.Combine(dir, "ro-dir")), FileAttributes.ReadOnly); // 只读目录
        File.SetAttributes(Put(Path.Combine(dir, "hidden.dat"), 7), FileAttributes.Hidden | FileAttributes.System);

        var disk = Disk.Scan(root);
        var tree = disk.Tree;
        long before = tree.Alloc(FileTree.RootId);
        var result = Delete(tree, true, disk[file], disk[dir]);

        long freed = Alloc(5000) + Alloc(10) + Alloc(4097) + Alloc(5) + Alloc(7);
        ctx.True(result.Ok, "永久删除文件和目录：Ok");
        ctx.Equal(0, result.Failed, "永久删除：没有失败项");
        ctx.Equal(9, result.Deleted, "永久删除：文件 + 目录里 7 个条目 + 目录自己 = 9");
        ctx.Equal(freed, result.FreedBytes, "永久删除：FreedBytes 等于树的净释放");
        ctx.Equal(before - freed, tree.Alloc(FileTree.RootId), "永久删除：树的占用同步减少");
        ctx.True(!Exists(file) && !Exists(dir), "永久删除：磁盘上都没了（含只读文件、只读目录、系统隐藏文件）");
        ctx.True(Exists(Path.Combine(root, "keep.txt")) && Exists(Path.Combine(root, "中文目录", "文档 (副本).txt")), "没选的文件原样留着");
        ctx.Equal(tree.Version, result.TreeVersion, "结果带回最新的树版本");
        Consistent(ctx, tree, "永久删除");

        // 已经不在磁盘上的节点再删一次：当作成功，不报错
        string ghost = Put(Path.Combine(root, "ghost.txt"), 10);
        var disk3 = Disk.Scan(root);
        File.Delete(ghost);
        var again = Delete(disk3.Tree, true, disk3[ghost]);
        ctx.True(again.Ok && again.Failed == 0, "磁盘上已经没了的节点：当作删成功");
        ctx.True(again is { AlreadyGone: 1, Deleted: 0, FreedBytes: 0 } && again.Message!.Contains("已经不在"),
            "别的程序先删了：单独计「已经不在」，不算这次删除的条目和释放量");
        lock (disk3.Tree.Sync) ctx.True(disk3.Tree.IsDeleted(disk3[ghost]), "别的程序先删了：树里照样标掉");
        Consistent(ctx, disk3.Tree, "删已不存在的节点");
    }

    static void LockedFiles(CheckContext ctx, string root)
    {
        string dir = Path.Combine(root, "dir");
        string a = Put(Path.Combine(dir, "a.txt"), 10);
        string locked = Put(Path.Combine(dir, "locked.txt"), 3000);
        string b = Put(Path.Combine(dir, "sub", "b.txt"), 20);
        string other = Put(Path.Combine(root, "other.txt"), 30);
        var disk = Disk.Scan(root);
        var tree = disk.Tree;

        OpResultDto result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            result = Delete(tree, true, disk[dir], disk[other]);

        ctx.True(!result.Ok, "被占用：整体不算成功");
        ctx.Equal(1, result.Failed, "被占用：恰好一个失败项");
        ctx.Equal(1, result.Errors.Length, "被占用：Errors 记了一条");
        ctx.True(result.Errors.Length == 1 && string.Equals(result.Errors[0].Path, locked, StringComparison.OrdinalIgnoreCase)
            && result.Errors[0].Message.Length > 0, "被占用：错误指向被占用的那个文件，且有说明");
        ctx.True(!Exists(a) && !Exists(b) && !Exists(other) && !Exists(Path.Combine(dir, "sub")), "被占用：能删的都删了");
        ctx.True(Exists(locked) && Exists(dir), "被占用：被占用的文件和它的父目录保留");
        ctx.Equal(Alloc(10) + Alloc(20) + Alloc(30), result.FreedBytes, "被占用：只统计真删掉的");
        lock (tree.Sync)
            ctx.True(tree.IsValid(disk[locked]) && tree.IsValid(disk[dir]) && tree.IsDeleted(disk[a]) && tree.IsDeleted(disk[Path.Combine(dir, "sub")]),
                "被占用：树里保留了没删掉的节点");
        Consistent(ctx, tree, "被占用");

        var again = Delete(tree, true, disk[dir]);
        ctx.True(again.Ok && !Exists(dir), "放开占用后再删一次：干净");
        ctx.Equal(Alloc(3000), again.FreedBytes, "放开占用后：释放量");
        Consistent(ctx, tree, "放开占用后");
    }

    static void Junctions(CheckContext ctx, string root)
    {
        string data = Dir(Path.Combine(root, "data"));
        string important = Put(Path.Combine(data, "important.txt"), 123);
        string deep = Put(Path.Combine(data, "sub", "deep.txt"), 456);
        string area = Dir(Path.Combine(root, "area"));
        string link = Path.Combine(area, "link-to-data");
        if (!Junction(link, data)) { ctx.Info("mklink /J 失败，跳过 junction 用例"); return; }
        Put(Path.Combine(area, "plain.txt"), 50);

        bool TargetIntact() => File.Exists(important) && new FileInfo(important).Length == 123
            && File.Exists(deep) && new FileInfo(deep).Length == 456;

        // 1) junction 本身就是删除目标
        var disk = Disk.Scan(area);
        ctx.True(disk.Tree.Has(disk[link], NodeFlags.Reparse), "镜像里 junction 标了重解析点");
        var r1 = Delete(disk.Tree, true, disk[link]);
        ctx.True(r1.Ok && r1.Failed == 0, "junction 作为目标：删除成功");
        ctx.True(!Exists(link), "junction 作为目标：链接没了");
        ctx.True(TargetIntact(), "junction 作为目标：目标目录和里面的文件原封不动");
        ctx.Equal(0L, r1.FreedBytes, "junction 作为目标：链接本身不占空间");
        Consistent(ctx, disk.Tree, "junction 作为目标");

        // 2) 子树里带 junction 的目录：含一个指回上级的环
        string area2 = Dir(Path.Combine(root, "area2"));
        string victim = Path.Combine(area2, "victim");
        Put(Path.Combine(victim, "a.txt"), 10);
        Put(Path.Combine(victim, "nested", "b.txt"), 20);
        bool linked = Junction(Path.Combine(victim, "nested", "jump"), data) && Junction(Path.Combine(victim, "loop"), area2);
        ctx.True(linked, "造出子树里的 junction（含指回上级的环）");
        var d2 = Disk.Scan(area2);
        var r2 = Delete(d2.Tree, true, d2[victim]);
        ctx.True(r2.Ok && r2.Failed == 0, "子树里的 junction：删除成功");
        ctx.True(!Exists(victim), "子树里的 junction：整棵子树连同链接都没了");
        ctx.True(Directory.Exists(area2), "子树里的 junction：环指向的上级目录还在");
        ctx.True(TargetIntact(), "子树里的 junction：目标原封不动");
        ctx.Equal(Alloc(10) + Alloc(20), r2.FreedBytes, "子树里的 junction：只统计真实文件");
        Consistent(ctx, d2.Tree, "子树里的 junction");

        // 3) 符号链接需要特权或开发者模式，造不出来就跳过
        string symDir = Path.Combine(area, "sym-dir"), symFile = Path.Combine(area, "sym-file.txt");
        bool dirOk = Cmd("mklink", "/D", symDir, data) == "";
        bool fileOk = Cmd("mklink", symFile, important) == "";
        if (!dirOk && !fileOk) { ctx.Info("没有创建符号链接的权限，跳过符号链接用例"); return; }
        var d3 = Disk.Scan(area);
        var ids = new List<int>();
        if (dirOk) ids.Add(d3[symDir]);
        if (fileOk) ids.Add(d3[symFile]);
        var r3 = Delete(d3.Tree, true, ids.ToArray());
        ctx.True(r3.Ok && r3.Failed == 0, "符号链接：删除成功");
        ctx.True(!Exists(symDir) && !Exists(symFile), "符号链接：链接没了");
        ctx.True(TargetIntact(), "符号链接：目标原封不动");
    }

    static void Contents(CheckContext ctx, string root)
    {
        string target = Path.Combine(root, "target");
        string a = Put(Path.Combine(target, "a.txt"), 10);
        string c = Put(Path.Combine(target, "c.txt"), 4097);
        string b = Put(Path.Combine(target, "sub", "b.txt"), 20);
        string locked = Put(Path.Combine(target, "sub", "locked.txt"), 3000);
        string empty = Dir(Path.Combine(target, "empty-dir"));
        var disk = Disk.Scan(root);
        var tree = disk.Tree;

        OpResultDto r;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            r = DeleteService.DeleteContents(tree, disk[target], CancellationToken.None);
        ctx.True(!r.Ok && r.Failed == 1, "DeleteContents：被占用的跳过并计一个失败");
        ctx.True(Directory.Exists(target), "DeleteContents：目录本身保留");
        ctx.True(!Exists(a) && !Exists(c) && !Exists(b) && !Exists(empty), "DeleteContents：能删的都删了");
        ctx.True(Exists(locked) && Directory.Exists(Path.Combine(target, "sub")), "DeleteContents：被占用的文件和所在目录保留");
        ctx.Equal(Alloc(10) + Alloc(4097) + Alloc(20), r.FreedBytes, "DeleteContents：释放量");
        Consistent(ctx, tree, "DeleteContents 被占用");

        var r2 = DeleteService.DeleteContents(tree, disk[target], CancellationToken.None);
        ctx.True(r2.Ok && Directory.Exists(target) && Directory.GetFileSystemEntries(target).Length == 0, "DeleteContents：放开后清空，目录本身仍在");
        lock (tree.Sync) ctx.True(tree.IsValid(disk[target]), "DeleteContents：目标目录节点没被标删除");
        Consistent(ctx, tree, "DeleteContents 清空");

        Throws<ArgumentException>(ctx, () => DeleteService.DeleteContents(tree, 99_999, CancellationToken.None), "DeleteContents：节点不存在");
        string plain = Put(Path.Combine(root, "plain.txt"), 1);
        var d2 = Disk.Scan(root);
        Throws<ArgumentException>(ctx, () => DeleteService.DeleteContents(d2.Tree, d2[plain], CancellationToken.None), "DeleteContents：目标是文件");
    }

    static void HardLinks(CheckContext ctx, string root)
    {
        string first = Put(Path.Combine(root, "a", "file.dat"), 8000);
        string second = Path.Combine(root, "b", "file-link.dat");
        Dir(Path.GetDirectoryName(second)!);
        if (!CreateHardLinkW(second, first, IntPtr.Zero)) { ctx.Info("创建硬链接失败，跳过硬链接用例"); return; }
        Put(Path.Combine(root, "c", "other.txt"), 10);
        var disk = Disk.Scan(root, d => d.Tree.ResolveHardLinkGroup([d[first], d[second]]));
        var tree = disk.Tree;
        lock (tree.Sync) ctx.True(!tree.Has(disk[first], NodeFlags.HardLinkDup) && tree.Has(disk[second], NodeFlags.HardLinkDup), "硬链接：路径小的 a\\ 计入，b\\ 是副本");
        long total = tree.Alloc(FileTree.RootId);

        var r1 = Delete(tree, true, disk[first]);
        ctx.True(r1.Ok && !Exists(first) && Exists(second), "硬链接：删掉一个链接，另一个还在");
        ctx.Equal(0L, r1.FreedBytes, "硬链接：还有别的链接时净释放为 0");
        ctx.Equal(total, tree.Alloc(FileTree.RootId), "硬链接：树的总占用不变（计入身份转给了另一个链接）");
        Consistent(ctx, tree, "硬链接删一个");

        var r2 = Delete(tree, true, disk[second]);
        ctx.True(r2.Ok && !Exists(second), "硬链接：最后一个链接删掉");
        ctx.Equal(Alloc(8000), r2.FreedBytes, "硬链接：最后一个链接才真正释放");
        ctx.Equal(total - Alloc(8000), tree.Alloc(FileTree.RootId), "硬链接：树的总占用减少");
        Consistent(ctx, tree, "硬链接删光");
    }

    static void Reconciling(CheckContext ctx, string root)
    {
        string a1 = Put(Path.Combine(root, "a1.txt"), 10);
        string a2 = Put(Path.Combine(root, "a2.txt"), 5000);
        string a3 = Put(Path.Combine(root, "a3.txt"), 30);
        string dir = Path.Combine(root, "d");
        Put(Path.Combine(dir, "e1.bin"), 4097);
        Put(Path.Combine(dir, "sub", "e2.bin"), 20);
        string keep = Put(Path.Combine(root, "keep", "f.txt"), 40);
        var disk = Disk.Scan(root);
        var tree = disk.Tree;

        File.Delete(a2);
        Directory.Delete(dir, true);
        long freed = DeleteService.Reconcile(tree, FileTree.RootId);
        ctx.Equal(Alloc(5000) + Alloc(4097) + Alloc(20), freed, "Reconcile：释放量等于磁盘上已消失的那些");
        lock (tree.Sync)
            ctx.True(tree.IsDeleted(disk[a2]) && tree.IsDeleted(disk[dir]) && tree.IsDeleted(disk[Path.Combine(dir, "sub", "e2.bin")])
                && tree.IsValid(disk[a1]) && tree.IsValid(disk[a3]) && tree.IsValid(disk[keep]), "Reconcile：只标消失的");
        Consistent(ctx, tree, "Reconcile");
        ctx.Equal(0L, DeleteService.Reconcile(tree, FileTree.RootId), "Reconcile：再跑一遍没有新变化");

        // 只对一个子目录对账，别处的变化不动
        File.Delete(a1);
        File.Delete(keep);
        long partial = DeleteService.Reconcile(tree, disk[Path.Combine(root, "keep")]);
        ctx.Equal(Alloc(40), partial, "Reconcile 子目录：只处理它的子孙");
        lock (tree.Sync) ctx.True(tree.IsValid(disk[a1]), "Reconcile 子目录：范围之外的变化先不管");
        DeleteService.Reconcile(tree, FileTree.RootId);
        Consistent(ctx, tree, "Reconcile 全树");

        // 目录整个没了
        string gone = Put(Path.Combine(root, "gone-dir", "x", "y.txt"), 10);
        var d2 = Disk.Scan(root);
        Directory.Delete(Path.Combine(root, "gone-dir"), true);
        long g = DeleteService.Reconcile(d2.Tree, d2[Path.Combine(root, "gone-dir")]);
        ctx.Equal(Alloc(10), g, "Reconcile：目录本身消失，整棵子树标删除");
        Consistent(ctx, d2.Tree, "Reconcile 目录消失");
        _ = gone;
    }

    static void LongPaths(CheckContext ctx, string root)
    {
        string deep = root;
        for (int i = 0; i < 8; i++) deep = Path.Combine(deep, new string((char)('a' + i), 40));
        string leaf1 = Put(Path.Combine(deep, "leaf-1.txt"), 100);
        string leaf2 = Put(Path.Combine(deep, "leaf-2.txt"), 200);
        Put(Path.Combine(root, "short.txt"), 10);
        ctx.True(leaf1.Length > 330, "造出超过 260 字符的路径");
        var disk = Disk.Scan(root);
        var tree = disk.Tree;

        ctx.Equal(0L, DeleteService.Reconcile(tree, FileTree.RootId), "长路径：Reconcile 不把还在的深层文件当成已消失");
        Consistent(ctx, tree, "长路径 Reconcile");

        var r1 = Delete(tree, true, disk[leaf1]);
        ctx.True(r1.Ok && !Exists(leaf1) && Exists(leaf2), "长路径：永久删除深层文件");
        ctx.Equal(Alloc(100), r1.FreedBytes, "长路径：释放量");
        Consistent(ctx, tree, "长路径删文件");

        var r2 = Delete(tree, true, disk[Path.Combine(root, new string('a', 40))]);
        ctx.True(r2.Ok && r2.Failed == 0, "长路径：永久删除整棵深层子树");
        ctx.True(!Exists(Path.Combine(root, new string('a', 40))), "长路径：子树连根没了");
        Consistent(ctx, tree, "长路径删子树");

        // 回收站：外壳对超长路径不一定认，但结果必须自洽——要么真进了回收站，要么原样留着并报错
        string deep2 = Path.Combine(root, new string('r', 40), new string('s', 40), new string('t', 40), new string('u', 40), new string('v', 40), new string('w', 40), new string('x', 40));
        string tiny = Put(Path.Combine(deep2, "DiskLens自检-可删除-长路径.txt"), 3);
        var d2 = Disk.Scan(root);
        var r3 = Delete(d2.Tree, false, d2[tiny]);
        if (r3.Ok) ctx.True(!Exists(tiny), "长路径回收站：报告成功则文件必须已经不在");
        else ctx.True(Exists(tiny) && r3.Errors.Length == 1 && r3.Errors[0].Message.Length > 0, "长路径回收站：失败则文件原样在，并有一条说明");
        Consistent(ctx, d2.Tree, "长路径回收站");
    }

    static void BlockedInSandbox(CheckContext ctx, string root)
    {
        // 沙箱里放真的 .ssh 目录：规则按名字拦，被拦时磁盘上一个文件都不许少
        string proj = Path.Combine(root, "proj");
        string key = Put(Path.Combine(proj, ".ssh", "id_rsa"), 10);
        string src = Put(Path.Combine(proj, "src", "main.c"), 20);
        string loose = Put(Path.Combine(root, "loose.txt"), 5);
        var disk = Disk.Scan(root);
        var tree = disk.Tree;

        var r = Delete(tree, true, disk[proj]);
        ctx.True(!r.Ok && r.Failed >= 1 && r.Deleted == 0, "含 .ssh 的目录：整体被拦，一个都不删");
        ctx.True(Exists(key) && Exists(src), "含 .ssh 的目录：磁盘上原样");
        ctx.True(r.Errors.Length >= 1 && r.Errors[0].Message.Contains("受保护") || r.Errors.Length >= 1 && r.Errors[0].Message.Contains("密钥"), "含 .ssh 的目录：理由写明了受保护");

        var r2 = Delete(tree, true, disk[Path.Combine(proj, ".ssh")]);
        ctx.True(!r2.Ok && Exists(key), ".ssh 本身被拦");

        var check = DeleteService.Check(tree, [disk[proj], disk[loose]]);
        ctx.True(check.AnyBlocked, "Check：AnyBlocked");
        var itemProj = check.Items.Single(i => i.Id == disk[proj]);
        var itemLoose = check.Items.Single(i => i.Id == disk[loose]);
        ctx.True(itemProj.Blocked && !string.IsNullOrEmpty(itemProj.Reason), "Check：含 .ssh 的目录标 Blocked 并给理由");
        ctx.True(!itemLoose.Blocked, "Check：同批的普通文件不受牵连");
        ctx.Equal(Alloc(5), check.TotalAlloc, "Check：被拦的不计入合计");

        var ok = Delete(tree, true, disk[Path.Combine(proj, "src")], disk[loose]);
        ctx.True(ok.Ok && !Exists(src) && !Exists(loose), "同级的普通目录和文件照常能删");
        Consistent(ctx, tree, "被拦之后");
    }

    static void Recycling(CheckContext ctx, string root)
    {
        // 只用几个字节的小文件，名字带标记，方便用户事后在回收站里认出来清掉；绝不清空回收站
        string f1 = Put(Path.Combine(root, "DiskLens自检-可删除-1.txt"), 7);
        string f2 = Put(Path.Combine(root, "DiskLens自检-可删除 2, x.txt"), 9);
        string d = Path.Combine(root, "DiskLens自检-可删除-dir");
        Put(Path.Combine(d, "inner.txt"), 5);
        string keep = Put(Path.Combine(root, "keep.txt"), 5);
        var disk = Disk.Scan(root);
        var tree = disk.Tree;
        long before = tree.Alloc(FileTree.RootId);

        var r = Delete(tree, false, disk[f1], disk[f2], disk[d]);
        ctx.True(r.Ok && r.Failed == 0, "回收站：三个条目一次提交，全部成功");
        ctx.True(!Exists(f1) && !Exists(f2) && !Exists(d), "回收站：删除后路径不存在");
        ctx.True(Exists(keep), "回收站：没选的原样");
        ctx.Equal(0L, r.FreedBytes, "回收站：FreedBytes 为 0（空间要清空回收站才释放）");
        ctx.Equal(4, r.Deleted, "回收站：文件 2 个 + 目录 1 个 + 目录里 1 个 = 4");
        ctx.True(r.Message != null && r.Message.Contains("回收站"), "回收站：Message 写明了回收站");
        ctx.Equal(before - Alloc(7) - Alloc(9) - Alloc(5), tree.Alloc(FileTree.RootId), "回收站：树里照样原地扣掉这些节点");
        Consistent(ctx, tree, "回收站");

        // 不存在的路径不应该让整个批次失败
        string again = Put(Path.Combine(root, "DiskLens自检-可删除-3.txt"), 3);
        var d2 = Disk.Scan(root);
        File.Delete(again);
        var r2 = Delete(d2.Tree, false, d2[again]);
        ctx.True(r2.Ok, "回收站：磁盘上已经没了的条目当作成功");
        ctx.True(r2 is { AlreadyGone: 1, Deleted: 0 }, "回收站：先被别的程序删掉的单独计，不算移进回收站的");
        Consistent(ctx, d2.Tree, "回收站删已不存在的");
    }

    static void Concurrency(CheckContext ctx, string root)
    {
        // 删除期间树锁不能被磁盘 IO 长期占着：另一个线程拿锁的最长等待要短
        string big = Path.Combine(root, "big");
        for (int d = 0; d < 30; d++)
            for (int f = 0; f < 100; f++)
                File.WriteAllBytes(Path.Combine(Dir(Path.Combine(big, "d" + d)), $"f{f}.dat"), new byte[10]);
        var disk = Disk.Scan(root);
        var tree = disk.Tree;
        int bigId = disk[big];

        long worst = 0;
        var whole = Stopwatch.StartNew();
        var task = Task.Run(() => Delete(tree, true, bigId));
        var sw = new Stopwatch();
        while (!task.IsCompleted)
        {
            sw.Restart();
            lock (tree.Sync) { }
            worst = Math.Max(worst, sw.ElapsedMilliseconds);
            Thread.Sleep(1);
        }
        var r = task.Result;
        ctx.Info($"永久删除 3000 个文件（深度约 11 层）：{whole.ElapsedMilliseconds} ms");
        ctx.True(r.Ok && r.Deleted == 3000 + 30 + 1, $"3000 个文件的目录：全部删除（实际 {r.Deleted}，失败 {r.Failed}）");
        ctx.Info($"删除 3000 个文件期间，另一线程等树锁最久 {worst} ms");
        ctx.True(worst < 250, $"磁盘 IO 在锁外：等锁最久 {worst} ms 应小于 250 ms");
        Consistent(ctx, tree, "并发删除");

        // 取消：不管取消落在哪一步，树和磁盘都得一致
        string big2 = Path.Combine(root, "big2");
        for (int d = 0; d < 30; d++)
            for (int f = 0; f < 200; f++)
                File.WriteAllBytes(Path.Combine(Dir(Path.Combine(big2, "d" + d)), $"f{f}.dat"), new byte[10]);
        var d2 = Disk.Scan(root);
        using var cts = new CancellationTokenSource();
        int id2 = d2[big2];
        var t2 = Task.Run(() => DeleteService.Delete(d2.Tree, [id2], true, cts.Token));
        Thread.Sleep(15);
        cts.Cancel();
        OpResultDto? cancelled = null;
        try { cancelled = t2.Result; }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException) { ctx.Info("取消发生在开始之前，没有部分结果"); }
        if (cancelled != null)
        {
            bool complete = !Exists(big2);
            ctx.True(complete || (!cancelled.Ok && cancelled.Message != null && cancelled.Message.Contains("取消")), "取消：要么已删完，要么 Ok=false 且说明已取消");
        }
        Consistent(ctx, d2.Tree, "取消之后");

        // 两个线程同时删同一批节点：靠操作门串行，结果不乱
        string race = Path.Combine(root, "race");
        for (int i = 0; i < 400; i++) Put(Path.Combine(race, "g" + (i % 8), $"r{i}.dat"), 10);
        var d3 = Disk.Scan(root);
        int raceId = d3[race];
        var both = new[] { Task.Run(() => Delete(d3.Tree, true, raceId)), Task.Run(() => Delete(d3.Tree, true, raceId)) };
        int threw = 0;
        var results = new List<OpResultDto>();
        foreach (var t in both)
        {
            try { results.Add(t.Result); }
            catch (AggregateException ex) when (ex.InnerException is ArgumentException) { threw++; } // 后到的发现节点已删：合法
        }
        ctx.True(!Exists(race), "同时删同一个目录：最终删干净");
        ctx.True(threw + results.Count == 2 && results.Count >= 1 && results.All(x => x.Ok || x.Failed == 0), $"同时删同一个目录：两个调用都有交代（抛 {threw}，返回 {results.Count}）");
        Consistent(ctx, d3.Tree, "并发删同一目录");
    }

    static void Scale(CheckContext ctx, string root)
    {
        // 同一目录下一万多个文件：DeleteContents 不能被条目数上限卡死（缓存目录动辄几万个）
        int count = int.TryParse(ctx.Option("scale"), out int n) ? n : 12_000; // check delete --scale 40000 看更大的量
        string flat = Dir(Path.Combine(root, "flat"));
        for (int i = 0; i < count; i++) File.WriteAllBytes(Path.Combine(flat, $"f{i:D5}.tmp"), new byte[1]);
        var disk = Disk.Scan(root);
        var tree = disk.Tree;
        var sw = Stopwatch.StartNew();
        var r = DeleteService.DeleteContents(tree, disk[flat], CancellationToken.None);
        ctx.Info($"DeleteContents {count:N0} 个同级文件：{sw.Elapsed.TotalSeconds:F1} 秒");
        ctx.True(r.Ok && r.Deleted == count && r.Failed == 0, $"DeleteContents {count:N0} 个同级文件：全部删除（Deleted={r.Deleted}，Failed={r.Failed}）");
        ctx.Equal(count * Cluster, r.FreedBytes, "大目录：释放量");
        Consistent(ctx, tree, "大目录 DeleteContents");
        ctx.True(sw.Elapsed < TimeSpan.FromSeconds(60), "大目录：一分钟内完成");
    }

    static void Guards(CheckContext ctx, string root)
    {
        string outer = Dir(Path.Combine(root, "outer"));
        string dir = Dir(Path.Combine(outer, "inner"));
        Put(Path.Combine(dir, "x.txt"), 1);

        // 守卫在手：目录不能被改名，放手之后可以
        string moved = dir + "-moved";
        var guard = NativeFs.OpenGuard(dir);
        Throws<IOException>(ctx, () => Directory.Move(dir, moved), "守卫在手：目录不能被改名");
        ctx.True(Directory.Exists(dir) && !Directory.Exists(moved), "守卫在手：目录原地不动");
        guard.Dispose();
        Directory.Move(dir, moved);
        Directory.Move(moved, dir);
        ctx.True(Directory.Exists(dir), "放手之后可以改名");

        // 守卫链：祖先也被钉住
        using (var set = new NativeFs.GuardSet())
        {
            var chain = set.AcquireChain(dir);
            Throws<IOException>(ctx, () => Directory.Move(outer, outer + "-moved"), "守卫链：祖先目录也不能被改名");
            set.ReleaseChain(chain);
            ctx.True(chain.Count >= 2, "守卫链包含从卷根到目标的每一层");
        }
        Directory.Move(outer, outer + "-moved");
        Directory.Move(outer + "-moved", outer);

        // 仅属性访问的句柄是否也拦得住改名：这决定了 OpenGuard 里为什么要带 LIST_DIRECTORY
        using (var weak = CreateFileW(@"\?\" + dir, 0x80, 3, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero))
        {
            bool blocked;
            try { Directory.Move(dir, moved); Directory.Move(moved, dir); blocked = false; }
            catch (IOException) { blocked = true; }
            ctx.Info($"只带 FILE_READ_ATTRIBUTES 的句柄{(blocked ? "能" : "不能")}挡住改名（首版的祖先守卫就是这么开的）");
        }

        // 链接做祖先或目标：守卫拒绝
        string data = Dir(Path.Combine(root, "data"));
        string link = Path.Combine(root, "link");
        if (!Junction(link, data)) { ctx.Info("mklink /J 失败，跳过链接守卫用例"); return; }
        Throws<IOException>(ctx, () => NativeFs.OpenGuard(link).Dispose(), "守卫拒绝 junction 本身");
        Put(Path.Combine(data, "inside", "f.txt"), 1);
        using (var set = new NativeFs.GuardSet())
            Throws<IOException>(ctx, () => set.AcquireChain(Path.Combine(link, "inside")), "守卫链拒绝穿过 junction 的路径");
        ctx.True(File.Exists(Path.Combine(data, "inside", "f.txt")), "拒绝之后目标原样");
        Throws<System.ComponentModel.Win32Exception>(ctx, () => NativeFs.OpenGuard(Path.Combine(root, "no-such-dir")), "守卫：目录不存在抛 Win32Exception");
        string file = Put(Path.Combine(root, "plain.txt"), 1);
        Throws<IOException>(ctx, () => NativeFs.OpenGuard(file), "守卫：路径是文件时拒绝");
    }
}
