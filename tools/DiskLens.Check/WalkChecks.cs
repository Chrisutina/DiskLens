using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskLens.Core;

namespace DiskLens.Check;

/// 常规扫描（Walk）与卷信息的自检。真实文件系统上的用例全在沙箱里；对真实卷只做只读枚举。
/// 加 --volume X: 会再只读扫一遍整卷并打印对账结果（不做断言）。
static partial class WalkChecks
{
    const long SparseLength = 1L << 30;

    public static void Run(CheckContext ctx)
    {
        CleanLeftovers(ctx);
        string sandbox = ctx.Sandbox();

        PathUnits(ctx);
        PolicyUnits(ctx);
        ParserUnits(ctx);
        ChainUnits(ctx);
        VolumeUnits(ctx);

        try
        {
            SourceTree(ctx, sandbox);
            ClassEquivalence(ctx, sandbox);
            OracleCases(ctx, sandbox);
            KnownSizes(ctx, sandbox);
            HardLinkCases(ctx, sandbox);
            ReparseCases(ctx, sandbox);
            CompactCase(ctx, sandbox);
            DeniedCase(ctx, sandbox);
            CancelCases(ctx, sandbox);
        }
        finally
        {
            // 链接留在沙箱里下次清理会碍事；拒绝 ACE 更是谁也删不掉
            CleanLeftovers(ctx);
        }

        if (ctx.Option("oracle") is { } oracle) OracleReport(ctx, oracle);
        if (ctx.Option("volume") is { } volume) VolumeReport(ctx, volume);
    }

    /// 上次崩在半路可能留下：悬空 junction（.NET 递归删它会炸）、带拒绝 ACE 的目录（谁也删不掉）。
    static void CleanLeftovers(CheckContext ctx)
    {
        string old = ctx.Sandbox(clean: false);
        RemoveLink(Path.Combine(old, "source", "junction"));
        RemoveLink(Path.Combine(old, "cycle", "back"));
        RemoveLink(Path.Combine(old, "junction-root", "link"));
        RemoveLink(Path.Combine(old, "symlink", "link"));
        RemoveLink(Path.Combine(old, "mount", "volume"));
        RemoveLink(Path.Combine(old, "mount", "junction"));
        RestoreAcl(Path.Combine(old, "denied", "locked"));
    }

    static FileTree ScanSealed(string root, out ScanCounters counters)
    {
        counters = new ScanCounters();
        var tree = WalkScanner.Scan(root, counters, CancellationToken.None);
        tree.Seal();
        return tree;
    }

    // ---------------------------------------------------------------------------------------------
    // 主用例（保留首版的全部断言）：硬链接、稀疏、压缩、junction、长路径、取消、ScanService
    // ---------------------------------------------------------------------------------------------

    static void SourceTree(CheckContext ctx, string sandbox)
    {
        string root = Directory.CreateDirectory(Path.Combine(sandbox, "source")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(sandbox, "outside")).FullName;
        File.WriteAllBytes(Path.Combine(outside, "must-not-enter.bin"), new byte[65536]);
        Directory.CreateDirectory(Path.Combine(root, "中文, sub"));
        string first = Path.Combine(root, "z-original.bin"), link = Path.Combine(root, "a-link.bin");
        File.WriteAllBytes(first, new byte[9000]);
        ctx.True(CreateHardLinkW(link, first, IntPtr.Zero), "构造 NTFS 硬链接");
        File.WriteAllBytes(Path.Combine(root, "中文, sub", "small.txt"), new byte[123]);
        File.WriteAllBytes(Path.Combine(root, "empty.txt"), []);
        string sparse = Path.Combine(root, "sparse.bin");
        using (var file = File.OpenHandle(sparse, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            ctx.True(Control(file, 0x900C4, []), "设置稀疏属性");
            RandomAccess.SetLength(file, SparseLength); // 1 GB 逻辑长度，磁盘上只占下面写的两小块
            RandomAccess.Write(file, Enumerable.Repeat((byte)0x5a, 4096).ToArray(), 0);
            RandomAccess.Write(file, Enumerable.Repeat((byte)0x5a, 4096).ToArray(), SparseLength / 2);
            RandomAccess.FlushToDisk(file);
        }
        string compressed = Path.Combine(root, "compressed.txt");
        using (var file = File.OpenHandle(compressed, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            ctx.True(Control(file, 0x9C040, [1, 0]), "设置 NTFS 压缩属性");
            RandomAccess.Write(file, new byte[256 * 1024], 0);
            RandomAccess.FlushToDisk(file);
        }
        string junction = Path.Combine(root, "junction");
        MakeJunction(junction, outside);
        string longDir = root;
        for (int i = 0; i < 6; i++) longDir = Path.Combine(longDir, "long-path-" + new string('x', 45));
        Directory.CreateDirectory(longDir);
        File.WriteAllBytes(Path.Combine(longDir, "long.txt"), new byte[77]);

        var counters = new ScanCounters();
        var tree = ctx.Time("真实 Walk 扫描", () => WalkScanner.Scan(root, counters, CancellationToken.None));
        tree.Seal();
        int a = tree.FindByPath(link), z = tree.FindByPath(first), s = tree.FindByPath(sparse), c = tree.FindByPath(compressed);
        ctx.True(a > 0 && z > 0 && s > 0 && c > 0, "真实文件全部可定位");
        if (a > 0 && z > 0)
        {
            ctx.Equal(9000L, tree.Size(a), "硬链接逻辑大小");
            ctx.True(!tree.Has(a, NodeFlags.HardLinkDup) && tree.Has(z, NodeFlags.HardLinkDup), "路径最小的硬链接计入");
            ctx.Equal(2, tree.HardLinkGroup(a)?.Length ?? 0, "硬链接完整分组");
            ctx.Equal(1L, counters.HardLinkDups, "硬链接副本计数");
            ctx.Equal(Allocation(first), counters.HardLinkSavedBytes, "硬链接节省的字节 = 副本自己的占用");
        }
        if (s > 0)
        {
            ctx.Equal(SparseLength, tree.Size(s), "稀疏文件逻辑长度");
            ctx.Equal(Allocation(sparse), tree.Alloc(s), "稀疏文件实际占用");
            ctx.True(tree.Alloc(s) < tree.Size(s), "稀疏区域未算成占用");
            ctx.True(tree.Alloc(s) <= 1024 * 1024 && tree.Has(s, NodeFlags.Sparse), "1 GB 稀疏文件只占几个簇，且带稀疏标志");
        }
        if (c > 0)
        {
            ctx.Equal(Allocation(compressed), tree.Alloc(c), "压缩文件实际占用");
            ctx.True(tree.Alloc(c) < tree.Size(c) && tree.Has(c, NodeFlags.Compressed), "压缩后比原文件小，且带压缩标志");
        }
        int j = tree.FindByPath(junction);
        ctx.True(j > 0 && tree.Has(j, NodeFlags.Reparse), "junction 保留节点");
        if (j > 0) ctx.Equal(0, tree.Children(j).Length, "junction 目标未遍历");
        ctx.Equal(1L, counters.LinksSkipped, "junction 记成跳过的链接");
        ctx.Equal(0L, counters.MountPointsSkipped, "junction 不是卷挂载点");
        ctx.True(counters.Page(ScanIssueKind.Link, 0, 10) is (1, 1, [var skipped]) && skipped.Path == junction && skipped.Reason.Contains("联接点"),
            "跳过的 junction 进了链接清单：路径和原因");
        ctx.Equal(7L, tree.Files(0), "文件条目数含硬链接而不含 junction 目标");
        int deepest = tree.FindByPath(Path.Combine(longDir, "long.txt"));
        ctx.True(deepest > 0, "超过 MAX_PATH 的目录可扫描");
        ctx.True(deepest > 0 && tree.GetPath(deepest).Length > 260, "长路径用例的路径确实超过 260 个字符");
        long expected = Allocation(first) + Allocation(sparse) + Allocation(compressed)
            + Allocation(Path.Combine(root, "中文, sub", "small.txt")) + Allocation(Path.Combine(longDir, "long.txt"));
        ctx.Equal(expected, tree.Alloc(0), "整树实际占用不重复计硬链接");
        ctx.Equal(9000L * 1 + 123 + SparseLength + 256L * 1024 + 77, tree.Size(0), "整树逻辑大小不重复计硬链接");
        ctx.Equal(0L, counters.Errors, "干净的沙箱树没有错误");
        ctx.Equal(0L, counters.InaccessibleDirs, "干净的沙箱树没有无权访问的目录");
        ctx.Equal(0, counters.Warnings.Length, "干净的树不弹任何警告（首版每次扫描都固定弹一条）");

        // 带 \\?\ 的根路径：树根要还原成普通路径，结果与普通写法完全一致
        var prefixed = ScanSealed(@"\\?\" + root, out _);
        ctx.Equal(root, prefixed.RootPath, @"\\?\ 前缀的扫描根还原成普通路径");
        ctx.True(prefixed.Alloc(0) == tree.Alloc(0) && prefixed.Files(0) == tree.Files(0), @"\\?\ 前缀与普通写法扫出同样的树");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { WalkScanner.Scan(root, new ScanCounters(), cancelled.Token); ctx.Fail("扫描忽略预取消"); }
            catch (OperationCanceledException) { ctx.True(true, "预取消中止扫描"); }
        }
        using (var service = new ScanService())
        {
            var done = new TaskCompletionSource<ScanSummaryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Completed += value => done.TrySetResult(value);
            service.Failed += error => done.TrySetException(new Exception(error.Message));
            var start = service.Start(new ScanStartRequest(root));
            var summary = done.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            ctx.Equal(ScanMode.Walk, start.Mode, "文件夹自动选择 Walk");
            ctx.Equal(expected, summary.ScannedAlloc, "ScanService 汇总占用");
            ctx.True(service.Tree?.IsSealed == true && !service.IsScanning, "仅发布封存且完整的树");
            ctx.Equal(0L, summary.UnaccountedBytes, "文件夹不混入整卷未统计");
            ctx.True(summary.GapParts is [], "文件夹没有未解释容量，也就没有拆分");
            ctx.True(summary.LinksSkipped == 1 && summary.MountPointsSkipped == 0 && summary.Errors == 0 && summary.InaccessibleDirs == 0,
                "汇总带扫描报告的四类计数：1 个跳过的链接，没有挂载点、错误和拒绝访问");
            ctx.Equal(summary.ScannedAlloc, service.RefreshSummary()?.ScannedAlloc ?? -1, "删除前 RefreshSummary 保持统计");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 三种目录信息类读真实目录：偏移量文档会写错，真实数据不会
    // ---------------------------------------------------------------------------------------------

    static List<(string Name, WalkEntry Entry)> ReadRaw(string dir, int level)
    {
        using var handle = ScanNative.CreateFileW(ScanNative.LongPath(dir), 1 | 0x100000, 7, IntPtr.Zero, 3, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "打开目录失败：" + dir);
        var batch = new WalkBatch();
        int used = level;
        int error = WalkReader.ReadEntries((int infoClass, byte[] buffer, out int err) =>
        {
            if (ScanNative.GetFileInformationByHandleEx(handle, infoClass, buffer, buffer.Length)) { err = 0; return true; }
            err = Marshal.GetLastWin32Error();
            return false;
        }, ref used, new byte[128 * 1024], batch, CancellationToken.None);
        if (error != 0 || used != level) throw new Win32Exception(error, $"信息类等级 {level} 读取失败（实际用了 {used}）");
        return batch.Entries.Select(e => (new string(batch.Name(e)), e)).OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
    }

    static void ClassEquivalence(CheckContext ctx, string sandbox)
    {
        string dir = Path.Combine(sandbox, "source");
        var all = Enumerable.Range(0, WalkReader.LevelCount).Select(level => ReadRaw(dir, level)).ToArray();
        ctx.True(all[0].Count >= 7, $"真实目录读到 {all[0].Count} 项");
        for (int level = 1; level < all.Length; level++)
        {
            ctx.Equal(all[0].Count, all[level].Count, $"等级 {level} 与等级 0 条目数一致");
            for (int i = 0; i < Math.Min(all[0].Count, all[level].Count); i++)
            {
                var (name, x) = all[0][i];
                var (otherName, y) = all[level][i];
                ctx.Equal(name, otherName, $"等级 {level} 第 {i} 项同名");
                ctx.True(x.Flags == y.Flags && x.Size == y.Size && x.Alloc == y.Alloc && x.Modified == y.Modified && x.Tag == y.Tag,
                    $"等级 {level} 的 {name}：标志 / 大小 / 占用 / 时间 / 重解析标签与等级 0 逐项一致");
            }
        }
        var junction = all[0].First(x => x.Name == "junction").Entry;
        ctx.Equal(0xA0000003u, junction.Tag, "真实 junction 的重解析标签（Extd 专用字段）");
        ctx.Equal(0xA0000003u, all[1].First(x => x.Name == "junction").Entry.Tag, "真实 junction 的标签在 FileIdBoth 的 EaSize 里");
        ctx.Equal(0xA0000003u, all[2].First(x => x.Name == "junction").Entry.Tag, "真实 junction 的标签在 FileFull 的 EaSize 里");
        var original = all[0].First(x => x.Name == "z-original.bin").Entry;
        var hardLink = all[0].First(x => x.Name == "a-link.bin").Entry;
        ctx.True(original.FileId != 0 && original.FileId == hardLink.FileId, "同一文件的两个硬链接 FileId 相同且非 0");
        ctx.True(all[1].First(x => x.Name == "z-original.bin").Entry.FileId == (ulong)original.FileId, "FileIdBoth 的 64 位 FileId 与 Extd 低 64 位一致");
        ctx.True(all[0].First(x => x.Name == "empty.txt").Entry.FileId != original.FileId, "不同文件 FileId 不同");
    }

    // ---------------------------------------------------------------------------------------------
    // 暴力标准答案对拍：沙箱树 + 本仓库 src（几千个真实文件，含各种簇对齐）
    // ---------------------------------------------------------------------------------------------

    static void OracleCases(CheckContext ctx, string sandbox)
    {
        foreach (var (name, path) in new[] { ("沙箱 source", Path.Combine(sandbox, "source")), ("仓库 src", Path.Combine(CheckContext.RepoRoot, "src")) })
        {
            var tree = ctx.Time($"扫描 {name}", () => ScanSealed(path, out _));
            var truth = Oracle(path);
            ctx.Info($"{name}：{truth.Files:N0} 个文件，{truth.Dirs:N0} 个目录，占用 {truth.Alloc:N0} B");
            ctx.Equal(truth.Files, tree.Files(0), $"{name} 文件条目数与暴力标准答案一致");
            ctx.Equal(truth.Dirs, tree.Dirs(0), $"{name} 目录数与暴力标准答案一致");
            ctx.Equal(truth.Size, tree.Size(0), $"{name} 逻辑大小与暴力标准答案一致");
            ctx.Equal(truth.Alloc, tree.Alloc(0), $"{name} 实际占用与暴力标准答案一致");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // 已知大小的多层目录：size 精确，alloc 与簇对齐 / FileStandardInfo 一致
    // ---------------------------------------------------------------------------------------------

    static void KnownSizes(CheckContext ctx, string sandbox)
    {
        string root = Directory.CreateDirectory(Path.Combine(sandbox, "known")).FullName;
        long cluster = ClusterSize(root);
        (string Rel, long Size)[] files =
        [
            ("zero.bin", 0), ("one.bin", 1), ("cluster.bin", cluster), ("cluster-plus-1.bin", cluster + 1),
            ("l1/mid.bin", 65536), ("l1/l2/big.bin", 1_000_001), ("l1/l2/l3/tail.bin", 10), ("中文 sp ace/文件.txt", 5000),
        ];
        long sumSize = 0, sumAlloc = 0;
        foreach (var (rel, size) in files) WriteBytes(Path.GetFullPath(Path.Combine(root, rel)), size);
        Directory.CreateDirectory(Path.Combine(root, "empty-dir"));

        var tree = ScanSealed(root, out var counters);
        foreach (var (rel, size) in files)
        {
            string path = Path.GetFullPath(Path.Combine(root, rel));
            int id = tree.FindByPath(path);
            ctx.True(id > 0, $"{rel} 可定位");
            if (id <= 0) continue;
            long alloc = tree.Alloc(id);
            ctx.Equal(size, tree.Size(id), $"{rel} 逻辑大小精确");
            ctx.Equal(Allocation(path), alloc, $"{rel} 占用与 FileStandardInfo 一致");
            if (size == 0) ctx.Equal(0L, alloc, $"{rel} 空文件不占簇");
            else if (size >= cluster) ctx.Equal(RoundUp(size, cluster), alloc, $"{rel} 占用 = 向上取整到簇（{cluster} B）");
            // 小文件的数据可以常驻在 MFT 记录里，此时 AllocationSize 是大小按 8 字节对齐，不是簇的倍数
            else ctx.True(alloc == RoundUp(size, 8) || alloc == cluster, $"{rel} 小文件占用要么按 8 字节对齐（常驻）要么一簇，实际 {alloc}");
            sumSize += size;
            sumAlloc += alloc;
        }
        ctx.Equal(sumSize, tree.Size(0), "多层目录逻辑大小 = 各文件之和");
        ctx.Equal(sumAlloc, tree.Alloc(0), "多层目录占用 = 各文件之和");
        ctx.Equal((long)files.Length, tree.Files(0), "文件数");
        ctx.Equal(5L, tree.Dirs(0), "子孙目录数（l1、l2、l3、中文 sp ace、empty-dir）");
        int l1 = tree.FindByPath(Path.Combine(root, "l1"));
        ctx.Equal(65536L + 1_000_001 + 10, tree.Size(l1), "中间层目录的聚合大小");
        ctx.Equal(files.Length, (int)counters.Files, "进度计数器的文件数");
        ctx.Equal(5L, counters.Dirs, "进度计数器的目录数");
        ctx.Equal(sumAlloc, counters.Bytes, "进度计数器的字节数 = 占用之和");
        ctx.Equal(0L, counters.Errors + counters.InaccessibleDirs + counters.LinksSkipped + counters.MountPointsSkipped, "没有错误、拒绝访问和跳过");
        ctx.Equal(0, counters.Warnings.Length, "没有警告");
    }

    // ---------------------------------------------------------------------------------------------
    // 硬链接：跨目录三链接、大小写、目录项过期
    // ---------------------------------------------------------------------------------------------

    static void HardLinkCases(CheckContext ctx, string sandbox)
    {
        string root = Directory.CreateDirectory(Path.Combine(sandbox, "links")).FullName;
        string P(string rel) => Path.GetFullPath(Path.Combine(root, rel));

        // 三个链接分在三个目录；路径最小的 dirA 计入，另外两个是副本
        string second = P("dirB/second.dat"), first = P("dirA/first.dat"), third = P("dirC/deep/third.dat");
        WriteBytes(second, 20000);
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(Path.GetDirectoryName(third)!);
        ctx.True(CreateHardLinkW(first, second, IntPtr.Zero), "构造三链接硬链接（1）");
        ctx.True(CreateHardLinkW(third, second, IntPtr.Zero), "构造三链接硬链接（2）");

        // 比较要不分大小写：B-upper 排在 a-lower 之后；误用 Ordinal 的话大写 B(0x42) 会排到小写 a(0x61) 前面
        string upper = P("case/B-upper.dat"), lower = P("case/a-lower.dat");
        WriteBytes(upper, 5000);
        ctx.True(CreateHardLinkW(lower, upper, IntPtr.Zero), "构造大小写用例硬链接");

        // 目录项过期：文件在写入中、句柄没关，另一个链接的目录项还是旧大小。必须以 FileStandardInfo 为准。
        string live = P("stale-a/live.dat"), liveLink = P("stale-b/live-link.dat");
        WriteBytes(live, 100);
        Directory.CreateDirectory(Path.GetDirectoryName(liveLink)!);
        ctx.True(CreateHardLinkW(liveLink, live, IntPtr.Zero), "构造写入中硬链接");
        const long liveLength = 100 + 1_300_000;
        long liveAlloc;
        FileTree tree;
        ScanCounters counters;
        using (var writer = new FileStream(live, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            writer.Seek(0, SeekOrigin.End);
            writer.Write(new byte[1_300_000]);
            writer.Flush();
            liveAlloc = Allocation(live);
            long listed = new DirectoryInfo(Path.GetDirectoryName(liveLink)!).EnumerateFiles().First().Length;
            ctx.Info($"写入中的文件：另一个链接的目录项报 {listed:N0} B，真实 {liveLength:N0} B（"
                + (listed != liveLength ? "目录项已过期，复核路径被触发" : "这次目录项没过期，复核路径没被触发") + "）");
            tree = ScanSealed(root, out counters);
        }

        int a = tree.FindByPath(first), b = tree.FindByPath(second), c = tree.FindByPath(third);
        ctx.True(a > 0 && b > 0 && c > 0, "三链接全部可定位");
        if (a > 0 && b > 0 && c > 0)
        {
            ctx.Equal(3, tree.HardLinkGroup(a)?.Length ?? 0, "三链接完整分组");
            ctx.True(!tree.Has(a, NodeFlags.HardLinkDup) && tree.Has(b, NodeFlags.HardLinkDup) && tree.Has(c, NodeFlags.HardLinkDup),
                "三个链接里只有路径最小的 dirA 计入");
            ctx.Equal(a, tree.CountedLink(b), "副本指向计入位置");
            ctx.Equal(20000L, tree.Size(b), "副本保留自己的大小用于显示");
        }
        int up = tree.FindByPath(upper), low = tree.FindByPath(lower);
        ctx.True(up > 0 && low > 0 && !tree.Has(low, NodeFlags.HardLinkDup) && tree.Has(up, NodeFlags.HardLinkDup),
            "路径比较不分大小写：a-lower 排在 B-upper 之前，计入的是 a-lower");
        int liveNode = tree.FindByPath(live), linkNode = tree.FindByPath(liveLink);
        ctx.True(liveNode > 0 && linkNode > 0, "写入中的文件两个链接都可定位");
        if (liveNode > 0 && linkNode > 0)
        {
            ctx.Equal(liveLength, tree.Size(liveNode), "写入中的文件：计入链接的大小是真实大小");
            ctx.Equal(liveLength, tree.Size(linkNode), "写入中的文件：副本链接也被改成真实大小");
            ctx.Equal(liveAlloc, tree.Alloc(liveNode), "写入中的文件：占用是真实占用");
        }
        long alloc20k = Allocation(second), alloc5k = Allocation(upper);
        ctx.Equal(alloc20k + alloc5k + liveAlloc, tree.Alloc(0), "整树占用：每个文件只计一次");
        ctx.Equal(liveLength + 20000 + 5000, tree.Size(0), "整树逻辑大小：每个文件只计一次");
        ctx.Equal(7L, tree.Files(0), "文件条目数含全部链接");
        ctx.Equal(2L + 1 + 1, counters.HardLinkDups, "硬链接副本计数 = 各组大小 - 1 之和");
        ctx.Equal(2 * alloc20k + alloc5k + liveAlloc, counters.HardLinkSavedBytes, "硬链接节省字节 = 各副本占用之和");
        ctx.Equal(0L, counters.Errors, "硬链接复核没出错");
    }

    // ---------------------------------------------------------------------------------------------
    // 重解析点：指回自己的 junction 不能死循环；直接扫 junction 要列出目标；符号链接
    // ---------------------------------------------------------------------------------------------

    static void ReparseCases(CheckContext ctx, string sandbox)
    {
        string cycle = Directory.CreateDirectory(Path.Combine(sandbox, "cycle")).FullName;
        WriteBytes(Path.Combine(cycle, "a.txt"), 10);
        string back = Path.Combine(cycle, "back");
        MakeJunction(back, cycle); // 指回自己：进去就是无底洞
        var timer = Stopwatch.StartNew();
        var tree = ScanSealed(cycle, out var counters);
        ctx.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"指回父目录的 junction 不会死循环（{timer.ElapsedMilliseconds} ms）");
        ctx.Equal(1L, tree.Files(0), "成环 junction：只统计真实文件");
        ctx.Equal(1L, counters.LinksSkipped, "成环 junction 记一次跳过");
        int node = tree.FindByPath(back);
        ctx.True(node > 0 && tree.Has(node, NodeFlags.Reparse) && tree.Children(node).Length == 0, "成环 junction 只留一个不展开的节点");

        // 用户明确要扫的路径本身是 junction：列出目标内容，且不算「跳过」
        string target = Path.Combine(sandbox, "junction-root", "target");
        WriteBytes(Path.Combine(target, "x.bin"), 777);
        string linkRoot = Path.Combine(sandbox, "junction-root", "link");
        MakeJunction(linkRoot, target);
        var viaLink = ScanSealed(linkRoot, out var viaCounters);
        ctx.True(viaLink.FindByPath(Path.Combine(linkRoot, "x.bin")) > 0, "把 junction 本身当扫描根，列出目标内容");
        ctx.Equal(0L, viaCounters.LinksSkipped + viaCounters.MountPointsSkipped, "扫描根是 junction 不算跳过");
        ctx.Equal(777L, viaLink.Size(0), "扫描根是 junction：大小来自目标");

        // 目录符号链接：需要开发者模式或管理员，创建不了就跳过
        string symTarget = Path.Combine(sandbox, "symlink", "target");
        WriteBytes(Path.Combine(symTarget, "y.bin"), 55);
        string symlink = Path.Combine(sandbox, "symlink", "link");
        if (CreateSymbolicLinkW(symlink, symTarget, 0x1 | 0x2))
        {
            var viaSym = ScanSealed(Path.Combine(sandbox, "symlink"), out var symCounters);
            ctx.Equal(1L, symCounters.LinksSkipped, "目录符号链接记一次跳过（算链接）");
            ctx.Equal(1L, viaSym.Files(0), "目录符号链接的目标不重复统计");
            int symNode = viaSym.FindByPath(symlink);
            ctx.True(symNode > 0 && viaSym.Has(symNode, NodeFlags.Reparse) && viaSym.Children(symNode).Length == 0, "符号链接只留一个不展开的节点");
        }
        else ctx.Info($"无法创建目录符号链接（错误 {Marshal.GetLastWin32Error()}：需要开发者模式或管理员），跳过符号链接用例");

        // 卷挂载点和联接点是同一个标签，只差替代名。指向一个不存在的卷：设得上、进不去、删得掉，不会真把别的卷挂进沙箱
        string mount = Directory.CreateDirectory(Path.Combine(sandbox, "mount")).FullName;
        WriteBytes(Path.Combine(mount, "real.bin"), 10);
        MakeJunction(Path.Combine(mount, "junction"), cycle);
        if (MakeMountPoint(Path.Combine(mount, "volume"), FakeVolume, ""))
        {
            var mounted = ScanSealed(mount, out var mountCounters);
            ctx.Equal(1L, mountCounters.MountPointsSkipped, @"替代名是 \??\Volume{…} 的挂载点记成卷挂载点");
            ctx.Equal(1L, mountCounters.LinksSkipped, "旁边的 junction 仍然算链接，两类分开计");
            ctx.True(mountCounters.Page(ScanIssueKind.MountPoint, 0, 10) is (1, 1, [var m]) && m.Path == Path.Combine(mount, "volume")
                && m.Reason.Contains("卷挂载点"), "卷挂载点进了挂载点清单，不混进链接清单");
            ctx.True(mountCounters.Page(ScanIssueKind.Link, 0, 10) is (1, 1, [var j]) && j.Path == Path.Combine(mount, "junction"), "junction 进了链接清单");
            ctx.Equal(1L, mounted.Files(0), "卷挂载点和 junction 都没进去");
            int vol = mounted.FindByPath(Path.Combine(mount, "volume"));
            ctx.True(vol > 0 && mounted.Has(vol, NodeFlags.Reparse) && mounted.Children(vol).Length == 0, "卷挂载点只留一个不展开的节点");
        }
        else ctx.Info($"这个卷上设不了挂载点（错误 {Marshal.GetLastWin32Error()}），跳过卷挂载点用例");
    }

    const string FakeVolume = @"\??\Volume{00000000-0000-0000-0000-000000000000}\";

    // ---------------------------------------------------------------------------------------------
    // WOF（CompactOS）：数据在命名流里；目录项里没有重解析属性、没有标签，占用是 0，只能靠「有大小却占 0」重查
    // ---------------------------------------------------------------------------------------------

    static void CompactCase(CheckContext ctx, string sandbox)
    {
        string root = Directory.CreateDirectory(Path.Combine(sandbox, "wof")).FullName;
        // compact /exe 只处理可执行文件的扩展名，所以叫 .exe；内容只是字节，不会被执行
        string file = Path.Combine(root, "packed.exe"), hardLink = Path.Combine(root, "packed-link.exe");
        File.WriteAllBytes(file, Enumerable.Range(0, 256 * 1024).Select(i => (byte)(i % 7)).ToArray());
        int exit = RunTool("compact.exe", "/c", "/exe:lzx", file);
        var raw = ReadRaw(root, 0).First(x => x.Name == "packed.exe").Entry;
        long real = CompressedApi(file);
        ctx.Info($"WOF 文件：compact 退出码 {exit}；目录项标志 {raw.Flags}、标签 {raw.Tag:X8}、大小 {raw.Size:N0}、占用 {raw.Alloc:N0}；GetCompressedFileSizeW {real:N0}");
        if (exit != 0 || real >= 256 * 1024)
        {
            ctx.Info("这个卷上 WOF 压缩不可用，跳过 WOF 用例");
            return;
        }
        var tree = ScanSealed(root, out var counters);
        int id = tree.FindByPath(file);
        ctx.True(id > 0 && !tree.IsDir(id), "WOF 文件按普通文件统计");
        if (id > 0)
        {
            ctx.Equal(256L * 1024, tree.Size(id), "WOF 文件逻辑大小不变");
            ctx.Equal(real, tree.Alloc(id), "WOF 文件占用取自 GetCompressedFileSizeW，而不是目录项");
            ctx.True(tree.Alloc(id) < tree.Size(id), "WOF 压缩后占用小于逻辑大小");
        }
        ctx.Equal(0L, counters.LinksSkipped, "WOF 文件不是被跳过的目录");
        var truth = Oracle(root);
        ctx.Equal(truth.Alloc, tree.Alloc(0), "WOF 目录与暴力标准答案一致");

        // 硬链接的 WOF 文件：全组改成真实大小前，FileStandardInfo 给的占用同样靠不住
        ctx.True(CreateHardLinkW(hardLink, file, IntPtr.Zero), "构造 WOF 文件的硬链接");
        var linked = ScanSealed(root, out var linkedCounters);
        ctx.Equal(real, linked.Alloc(0), "硬链接的 WOF 文件只按真实占用计一次");
        ctx.Equal(1L, linkedCounters.HardLinkDups, "WOF 硬链接副本计数");
        ctx.Equal(real, linkedCounters.HardLinkSavedBytes, "WOF 硬链接节省的是压缩后的占用，不是 0");
    }

    // ---------------------------------------------------------------------------------------------
    // 无权限目录：用 icacls 按 SID 拒绝列目录；扫描不能中断，别的目录照常统计
    // ---------------------------------------------------------------------------------------------

    static void DeniedCase(CheckContext ctx, string sandbox)
    {
        if (Volumes.IsElevated())
        {
            ctx.Info("当前是管理员进程：备份特权会绕过 ACL，无权限目录用例无法构造，跳过");
            return;
        }
        string root = Directory.CreateDirectory(Path.Combine(sandbox, "denied")).FullName;
        WriteBytes(Path.Combine(root, "open", "ok.txt"), 1000);
        string locked = Path.Combine(root, "locked");
        WriteBytes(Path.Combine(locked, "secret.bin"), 5000);
        try
        {
            bool set = DenyListing(locked);
            ctx.True(set, "icacls 按 SID 设置拒绝 ACE（OI)(CI)(RD)");
            if (!set) return;
            bool denied = false;
            try { Directory.GetFileSystemEntries(locked); }
            catch (UnauthorizedAccessException) { denied = true; }
            ctx.True(denied, "前置条件：拒绝 ACE 真的挡住了列目录");

            var tree = ScanSealed(root, out var counters);
            int node = tree.FindByPath(locked);
            ctx.True(node > 0 && tree.Has(node, NodeFlags.Inaccessible), "无权限目录标 Inaccessible");
            ctx.Equal(0, node > 0 ? tree.Children(node).Length : -1, "无权限目录里没有节点");
            ctx.Equal(1L, counters.InaccessibleDirs, "无权访问的目录计数");
            ctx.Equal(0L, counters.Errors, "拒绝访问是常态，不算错误");
            ctx.Equal(1L, tree.Files(0), "旁边的目录照常统计，扫描没被中断");
            ctx.Equal(1000L, tree.Size(0), "无权限目录里的内容不计入");
            ctx.True(counters.Warnings.Length == 1 && counters.Warnings[0].Contains("1 个目录无权访问"),
                "只有一条汇总警告，而不是每个目录一条：" + string.Join(" | ", counters.Warnings));
            ctx.True(counters.Page(ScanIssueKind.Denied, 0, 10) is (1, 1, [var entry]) && entry.Path == locked && entry.Reason.Contains("拒绝访问"),
                "拒绝访问的目录进了清单：路径和原因");

            // 同一个夹具走一遍 ScanService：汇总的计数和 scan.issues 的总数、条目对得上
            using var service = new ScanService();
            var done = new TaskCompletionSource<ScanSummaryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Completed += value => done.TrySetResult(value);
            service.Failed += error => done.TrySetException(new Exception(error.Message));
            service.Start(new ScanStartRequest(root, ScanMode.Walk));
            var summary = done.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            var issues = service.Issues(new ScanIssuesRequest(ScanIssueKind.Denied, ScanId: summary.ScanId));
            ctx.True(summary is { InaccessibleDirs: 1, Errors: 0, LinksSkipped: 0, MountPointsSkipped: 0 }, "汇总：1 个拒绝访问入口，没有错误和跳过");
            ctx.True(issues is { Total: 1, Recorded: 1, Offset: 0, Truncated: false, Items: [{ Path: var p }] } && p == locked && issues.ScanId == summary.ScanId,
                "scan.issues 的拒绝访问清单：总数等于汇总、路径就是那个目录、不截断");
            ctx.True(service.Issues(new ScanIssuesRequest(ScanIssueKind.Error)) is { Total: 0, Items: [], Truncated: false }, "没有错误时清单为空");
        }
        finally { RestoreAcl(locked); }

        var again = ScanSealed(root, out var recovered);
        ctx.Equal(0L, recovered.InaccessibleDirs, "恢复权限后不再有无权访问的目录");
        ctx.Equal(2L, again.Files(0), "恢复权限后被拒绝的目录能读到");
        ctx.Equal(6000L, again.Size(0), "恢复权限后大小完整");
    }

    // ---------------------------------------------------------------------------------------------
    // 取消：扫描中途取消要很快停下，并且所有工作线程都真的停了
    // ---------------------------------------------------------------------------------------------

    static void CancelCases(CheckContext ctx, string sandbox)
    {
        // 只读枚举真实的 Windows 目录：体量够大，取消一定发生在途中
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        using var cts = new CancellationTokenSource();
        var counters = new ScanCounters();
        var task = Task.Run(() => WalkScanner.Scan(windows, counters, cts.Token));
        SpinWait.SpinUntil(() => Interlocked.Read(ref counters.Files) >= 500 || task.IsCompleted, TimeSpan.FromSeconds(20));
        ctx.True(!task.IsCompleted, "取消用例的前置条件：Windows 目录没有在取消前扫完");
        var timer = Stopwatch.StartNew();
        cts.Cancel();
        bool cancelled = false;
        try { task.Wait(TimeSpan.FromSeconds(15)); }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException) { cancelled = true; }
        ctx.True(cancelled, "扫描中途取消抛 OperationCanceledException");
        ctx.True(timer.ElapsedMilliseconds < 3000, $"取消后很快停下（{timer.ElapsedMilliseconds} ms）");
        long frozen = Interlocked.Read(ref counters.Files);
        Thread.Sleep(300);
        ctx.Equal(frozen, Interlocked.Read(ref counters.Files), "取消后所有工作线程都已停手");
    }

    // ---------------------------------------------------------------------------------------------
    // --volume X:：只读扫一遍整卷，打印对账结果（不做断言）
    // ---------------------------------------------------------------------------------------------

    static void VolumeReport(CheckContext ctx, string volume)
    {
        string root = volume.TrimEnd('\\', '/') + "\\";
        ctx.Info($"整卷只读 Walk：{root}（{Volumes.GetFileSystem(root)}，{Volumes.GetDriveType(root)}，SSD={Volumes.IsSsd(root)?.ToString() ?? "未知"}）");
        var counters = new ScanCounters();
        var clock = Stopwatch.StartNew();
        var tree = WalkScanner.Scan(root, counters, CancellationToken.None);
        double seconds = clock.Elapsed.TotalSeconds;
        tree.Seal();
        var (total, free) = Volumes.GetSpace(root);
        long used = total - free, alloc = tree.Alloc(0), diff = used - alloc;
        ctx.Info($"文件 {tree.Files(0):N0}，目录 {tree.Dirs(0):N0}，耗时 {seconds:F1} s（{tree.Files(0) / Math.Max(seconds, 0.001):N0} 文件/秒）");
        ctx.Info($"树 alloc 总和 {alloc:N0} B，卷已用 {used:N0} B（总 {total:N0}，空闲 {free:N0}）");
        ctx.Info($"差值（已用 - 已统计）{diff:N0} B = {diff / (double)Math.Max(used, 1):P2} 的已用空间；逻辑大小总和 {tree.Size(0):N0} B");
        ctx.Info($"无权访问的目录 {counters.InaccessibleDirs:N0}，跳过的链接 {counters.LinksSkipped:N0}、卷挂载点 {counters.MountPointsSkipped:N0}，"
            + $"硬链接副本 {counters.HardLinkDups:N0}（省 {counters.HardLinkSavedBytes:N0} B），错误 {counters.Errors:N0}");
        foreach (var part in ScanService.SplitGap(root, ScanMode.Walk, Volumes.GetFileSystem(root), Math.Max(0, diff)))
            ctx.Info($"  差值拆分：{part.Kind} {part.Bytes:N0} B（{part.Basis}）");
        foreach (string warning in counters.Warnings.Take(5)) ctx.Info("警告：" + warning);
        // 差值去哪了：根下最大的几项，和无权访问的目录（非管理员看不到卷影、更新暂存区之类）
        var top = tree.Children(0);
        for (int i = 0; i < Math.Min(8, top.Length); i++)
            ctx.Info($"  根下第 {i + 1} 大：{tree.GetName(top[i]),-30} {tree.Alloc(top[i]),18:N0} B{(tree.Has(top[i], NodeFlags.Inaccessible) ? "（无权访问）" : "")}");
        for (int i = 1, shown = 0; i < tree.Count && shown < 8; i++)
            if (tree.IsDir(i) && tree.Has(i, NodeFlags.Inaccessible)) { ctx.Info("  无权访问：" + tree.GetPath(i)); shown++; }
    }

    /// --oracle 路径：对任意目录跑 Walk 和暴力标准答案，打印差值和不一致的文件（不做断言）。
    /// 活的卷上目录在两次遍历之间会变，差值里会混进别的进程刚写的文件；静止的目录上应当逐字节一致。
    static void OracleReport(CheckContext ctx, string path)
    {
        ScanCounters counters = new();
        var tree = ctx.Time($"Walk {path}", () => ScanSealed(path, out counters));
        var truth = ctx.Time("暴力标准答案", () => Oracle(path));
        ctx.Info($"Walk：{tree.Files(0):N0} 文件，{tree.Dirs(0):N0} 目录，大小 {tree.Size(0):N0} B，占用 {tree.Alloc(0):N0} B；"
            + $"硬链接副本 {counters.HardLinkDups:N0}，无权访问 {counters.InaccessibleDirs:N0}，错误 {counters.Errors:N0}");
        ctx.Info($"标准答案：{truth.Files:N0} 文件，{truth.Dirs:N0} 目录，大小 {truth.Size:N0} B，占用 {truth.Alloc:N0} B");
        ctx.Info($"差值（标准答案 - Walk）：文件 {truth.Files - tree.Files(0):N0}，目录 {truth.Dirs - tree.Dirs(0):N0}，"
            + $"大小 {truth.Size - tree.Size(0):N0} B，占用 {truth.Alloc - tree.Alloc(0):N0} B");
        if (tree.Alloc(0) != truth.Alloc || tree.Size(0) != truth.Size || tree.Files(0) != truth.Files) OracleDiff(ctx, path, tree);
        else ctx.Info("文件数、大小与占用和标准答案逐字节一致");
    }

    /// 总数对不上时，逐文件找出是哪些文件的大小 / 占用和句柄查到的真值不一致。
    static void OracleDiff(CheckContext ctx, string root, FileTree tree)
    {
        var diffs = new List<(string Path, long WalkSize, long WalkAlloc, long TruthSize, long TruthAlloc, uint Attributes)>();
        var unopenedSamples = new List<string>();
        int unique = 0, unopened = 0, missing = 0;
        long unopenedAlloc = 0;
        EachFile(root, entry =>
        {
            int node = tree.FindByPath(entry.FullName);
            if (node <= 0) { missing++; return; }
            if (Truth(entry.FullName, ref unique) is not { } t)
            {
                unopened++;
                unopenedAlloc += tree.Alloc(node);
                if (unopenedSamples.Count < 5) unopenedSamples.Add(entry.FullName);
                return;
            }
            if (tree.Size(node) != t.Size || tree.Alloc(node) != t.Alloc)
                diffs.Add((entry.FullName, tree.Size(node), tree.Alloc(node), t.Size, t.Alloc, t.Attributes));
        });
        ctx.Info($"逐文件比对：{diffs.Count:N0} 个文件的值不一致；{unopened:N0} 个文件标准答案打不开（Walk 按目录项计入 {unopenedAlloc:N0} B）；{missing:N0} 个没在树里");
        foreach (string sample in unopenedSamples) ctx.Info("  打不开：" + sample);
        foreach (var d in diffs.OrderByDescending(d => Math.Abs(d.WalkAlloc - d.TruthAlloc) + Math.Abs(d.WalkSize - d.TruthSize)).Take(12))
            ctx.Info($"  不一致：{d.Path}  Walk 大小/占用 {d.WalkSize:N0}/{d.WalkAlloc:N0}；真值 {d.TruthSize:N0}/{d.TruthAlloc:N0}；属性 {d.Attributes:X}");
    }
}
