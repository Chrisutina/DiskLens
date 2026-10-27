using System.Diagnostics;
using DiskLens.Core;

namespace DiskLens.Check;

// 真实卷：需要管理员权限（读 \\.\X: 原始卷）。没提权就打印跳过原因，不算失败。
//   --volume X:   整卷 MFT 扫描：记录数、耗时、峰值内存、树的 alloc 总和与 TotalClusters−FreeClusters 对账
//   --compare X:  同一卷 MFT 与 Walk 各扫一次，按顶层目录比 alloc 和文件数，打印差异最大的 20 个
static partial class MftChecks
{
    /// 返回可扫描的卷根（如 "C:\"）；不能扫时打印原因并返回 null。
    static string? RealRoot(CheckContext ctx, string option)
    {
        string? volume = ctx.Option(option);
        if (volume == null) return null;
        string root = volume.TrimEnd('\\', '/', ':') + @":\";
        if (!Volumes.IsElevated())
        {
            ctx.Info($"跳过 --{option} {volume}：当前进程没有提权，读不了原始卷（请在管理员终端里重跑这条命令）");
            return null;
        }
        if (!MftScanner.CanScan(root, out string reason))
        {
            ctx.Info($"跳过 --{option} {volume}：{reason}");
            return null;
        }
        return root;
    }

    static void RealVolume(CheckContext ctx)
    {
        if (ctx.Option("volume") == null)
        {
            ctx.Info("没有 --volume X:，跳过真实卷扫描（需要管理员权限）");
            return;
        }
        if (RealRoot(ctx, "volume") is not { } root) return;
        GC.Collect();
        var counters = new ScanCounters();
        var watch = Stopwatch.StartNew();
        var tree = MftScanner.Scan(root, counters, CancellationToken.None, out var stats);
        double scanMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        tree.Seal();
        double sealMs = watch.Elapsed.TotalMilliseconds;
        double peakMb = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0;

        long alloc = tree.Alloc(0), used = stats.Used;
        long diff = alloc - used;
        double percent = used > 0 ? diff * 100.0 / used : 0;
        var (total, free) = Volumes.GetSpace(root);
        ctx.Info($"{root} 记录槽 {stats.Slots:N0}：在用基记录 {stats.InUse:N0}（目录 {stats.Directories:N0}）、扩展记录 {stats.Extensions:N0}、跳过 {stats.Corrupt:N0}");
        ctx.Info($"树 {tree.Count:N0} 个节点，文件条目 {tree.Files(0):N0}，目录 {tree.Dirs(0):N0}，硬链接重复 {counters.HardLinkDups:N0}（省 {counters.HardLinkSavedBytes:N0} 字节），链接 {counters.LinksSkipped:N0}、卷挂载点 {counters.MountPointsSkipped:N0}，孤立记录子树 {stats.Orphans:N0}，错误 {counters.Errors:N0}");
        ctx.Info($"耗时：读取+解析 {stats.ReadMs:F0} ms，合并 {stats.AssembleMs:F0} ms，建树+硬链接 {stats.BuildMs:F0} ms，扫描合计 {scanMs:F0} ms，Seal {sealMs:F0} ms；读了 {stats.ReadBytes / 1048576.0:F0} MB 的 MFT");
        ctx.Info($"峰值内存：进程峰值工作集 {peakMb:F0} MB，托管堆 {GC.GetTotalMemory(false) / 1048576.0:F0} MB");
        ctx.Info($"对账：树的 alloc 总和 {alloc:N0}，TotalClusters−FreeClusters = {used:N0}，差值 {diff:+#,0;-#,0;0} 字节（{percent:+0.000;-0.000;0.000}%）；GetDiskFreeSpaceEx 的已用 {total - free:N0}");
        foreach (string warning in counters.Warnings) ctx.Info("警告：" + warning);

        ctx.True(tree.Count > 100 && stats.InUse > 100, "整卷扫出了像样的节点数");
        ctx.True(tree.FindByPath(root + "$MFT") > 0, "$MFT 在虚拟目录「$NTFS 元数据」里");
        ctx.True(Math.Abs(percent) <= 0.5, $"对账差值 {percent:+0.000;-0.000}% 在 0.5% 以内（DESIGN §4.4.7）");
    }

    static void Compare(CheckContext ctx)
    {
        if (ctx.Option("compare") == null) return;
        if (RealRoot(ctx, "compare") is not { } root) return;
        var watch = Stopwatch.StartNew();
        var mft = MftScanner.Scan(root, new ScanCounters(), CancellationToken.None);
        double mftMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var walk = WalkScanner.Scan(root, new ScanCounters(), CancellationToken.None);
        double walkMs = watch.Elapsed.TotalMilliseconds;
        mft.Seal();
        walk.Seal();
        ctx.Info($"{root} MFT 扫描 {mftMs:F0} ms，Walk 扫描 {walkMs:F0} ms（两次之间卷上的变化也会造成差异）");

        static Dictionary<string, (long Alloc, long Files)> TopLevel(FileTree tree)
        {
            var top = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
            foreach (int child in tree.Children(0))
                top[tree.GetName(child)] = (tree.Alloc(child), tree.IsDir(child) ? tree.Files(child) : 1);
            return top;
        }
        var a = TopLevel(mft);
        var b = TopLevel(walk);
        var rows = a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).Select(name =>
        {
            a.TryGetValue(name, out var x);
            b.TryGetValue(name, out var y);
            return (Name: name, MftAlloc: x.Alloc, WalkAlloc: y.Alloc, MftFiles: x.Files, WalkFiles: y.Files);
        }).ToList();
        int same = rows.Count(r => r.MftAlloc == r.WalkAlloc && r.MftFiles == r.WalkFiles);
        ctx.Info($"顶层 {rows.Count:N0} 项，其中 alloc 与文件数完全一致的 {same:N0} 项；差异最大的 20 项（按 alloc 差的绝对值，再按文件数差）：");
        Console.WriteLine($"  {"顶层项",-32} {"MFT alloc",18} {"Walk alloc",18} {"alloc 差",18} {"MFT 文件",12} {"Walk 文件",12} {"文件差",10}");
        foreach (var r in rows.OrderByDescending(r => Math.Abs(r.MftAlloc - r.WalkAlloc)).ThenByDescending(r => Math.Abs(r.MftFiles - r.WalkFiles)).Take(20))
        {
            string name = r.Name.Length > 30 ? r.Name[..29] + "…" : r.Name;
            Console.WriteLine($"  {name,-32} {r.MftAlloc,18:N0} {r.WalkAlloc,18:N0} {r.MftAlloc - r.WalkAlloc,18:+#,0;-#,0;0} {r.MftFiles,12:N0} {r.WalkFiles,12:N0} {r.MftFiles - r.WalkFiles,10:+#,0;-#,0;0}");
        }
        Console.WriteLine($"  {"合计",-32} {mft.Alloc(0),18:N0} {walk.Alloc(0),18:N0} {mft.Alloc(0) - walk.Alloc(0),18:+#,0;-#,0;0} {mft.Files(0),12:N0} {walk.Files(0),12:N0} {mft.Files(0) - walk.Files(0),10:+#,0;-#,0;0}");
        ctx.True(mft.Count > 100 && walk.Count > 100, "两种扫描都扫出了像样的节点数");
    }
}
