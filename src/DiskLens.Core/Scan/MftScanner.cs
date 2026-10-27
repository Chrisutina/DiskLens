using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace DiskLens.Core;

/// 一次 MFT 扫描的内部数字，给自检和排查用；公开接口只返回树。
internal sealed class MftStats
{
    internal int Slots, InUse, Directories, Extensions, Corrupt, Links, Nodes, Orphans, LinkGroups;
    internal long ReadBytes, Used, Counted;
    internal double ReadMs, AssembleMs, BuildMs;
}

public static class MftScanner
{
    const int BlockBytes = 4 * 1024 * 1024, PoolSize = 8;

    readonly record struct Block(int Index, int FirstRecord, int Length, byte[] Buffer);

    public static bool CanScan(string rootPath, out string reason)
    {
        if (!Volumes.IsElevated()) { reason = L.T("MFT 深度扫描需要管理员权限，请以管理员身份重新启动", "MFT deep scan needs administrator rights; restart as administrator", "MFT ディープスキャンには管理者権限が必要です。管理者として再起動してください"); return false; }
        if (!Volumes.IsVolumeRoot(rootPath)) { reason = L.T("MFT 深度扫描只支持卷根；文件夹请使用常规扫描", "MFT deep scan works only on volume roots; use a regular scan for folders", "MFT ディープスキャンはボリュームのルートでのみ使えます。フォルダーには通常のスキャンを使ってください"); return false; }
        string? root = Volumes.GetVolumeRoot(rootPath);
        if (root is not { Length: 3 } || root[1] != ':' || Volumes.GetDriveType(rootPath) is not ("fixed" or "removable"))
        { reason = L.T("MFT 深度扫描只支持带盘符的本地固定盘或可移动盘", "MFT deep scan works only on local fixed or removable drives with a drive letter", "MFT ディープスキャンはドライブ文字のあるローカルの固定ドライブかリムーバブル ドライブでのみ使えます"); return false; }
        if (!Volumes.GetFileSystem(rootPath).Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        { reason = L.T("MFT 深度扫描只支持 NTFS 文件系统", "MFT deep scan works only on NTFS", "MFT ディープスキャンは NTFS でのみ使えます"); return false; }
        reason = "";
        return true;
    }

    /// 返回的树没有 Seal（ScanService 还要加「未统计」节点）。出错一律抛异常，由 ScanService 降级为 Walk。
    public static FileTree Scan(string volumeRoot, ScanCounters counters, CancellationToken ct) =>
        Scan(volumeRoot, counters, ct, out _);

    internal static FileTree Scan(string volumeRoot, ScanCounters counters, CancellationToken ct, out MftStats stats)
    {
        ct.ThrowIfCancellationRequested();
        if (!CanScan(volumeRoot, out string reason)) throw new InvalidOperationException(reason);
        string root = ScanNative.NormalizePath(volumeRoot);
        using var volume = ScanNative.CreateFileW(@"\\.\" + root[..2], 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (volume.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("无法只读打开 NTFS 卷", "Couldn't open the NTFS volume read-only", "NTFS ボリュームを読み取り専用で開けません"));
        // 缓冲区留足：Win8 之后 NTFS_VOLUME_DATA_BUFFER 后面还跟着扩展数据，版本号在里面
        byte[] info = new byte[512];
        if (!ScanNative.DeviceIoControl(volume, 0x90064, null, 0, info, info.Length, out int returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("无法读取 NTFS 卷参数及版本", "Couldn't read the NTFS volume parameters and version", "NTFS ボリュームのパラメーターとバージョンを読み取れません"));
        var geometry = MftGeometry.FromVolumeData(info.AsSpan(0, returned));
        return ScanDevice(MftDevice.FromHandle(volume), geometry, root, counters, ct, out stats);
    }

    /// 和真实卷共用的全部逻辑；设备读取是个委托，所以不用管理员，拿一块内存镜像也能把整条流水线跑一遍。
    internal static FileTree ScanDevice(MftDeviceRead read, MftGeometry g, string root, ScanCounters counters,
        CancellationToken ct, out MftStats stats)
    {
        stats = new MftStats();
        var watch = Stopwatch.StartNew();
        counters.Phase = "prepare";
        var device = new MftDevice(read, g.BytesPerSector);
        var stream = MftVolume.Locate(device, g);
        ct.ThrowIfCancellationRequested();
        var issues = new MftIssues(counters, root);
        var table = ReadRecords(stream, g, counters, issues, root, ct, stats);
        stats.ReadMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        table.MergeExtensions(issues, ct);
        Summarize(table, stats);
        stats.AssembleMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var tree = MftTreeBuilder.Build(table, root, counters, ct, stats);
        stats.BuildMs = watch.Elapsed.TotalMilliseconds;
        issues.Summarize();
        stats.Corrupt = issues.Count;
        Reconcile(tree, g, counters, stats);
        return tree;
    }

    static MftTable ReadRecords(MftStream stream, MftGeometry g, ScanCounters c, MftIssues issues, string root,
        CancellationToken ct, MftStats stats)
    {
        int size = g.RecordSize, count = checked((int)(g.MftValidLength / size));
        var table = new MftTable(count, size, g.VolumeBytes, g.BytesPerCluster);
        int perBlock = Math.Max(1, BlockBytes / size), blocks = (count + perBlock - 1) / perBlock;
        var results = new MftBlockResult?[blocks];
        int workers = Math.Max(1, Math.Min(blocks, Math.Clamp(Environment.ProcessorCount, 2, 24)));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stop.Token;
        // 缓冲池 + 有界队列：读线程永远不会比解析任务领先超过 PoolSize 块，内存就封顶在 PoolSize × 4 MB
        using var free = new BlockingCollection<byte[]>();
        for (int i = 0; i < Math.Min(PoolSize, blocks); i++) free.Add(new byte[perBlock * size]);
        using var ready = new BlockingCollection<Block>(PoolSize);
        Exception? failure = null;
        var tasks = new Task[workers];
        for (int w = 0; w < workers; w++)
            tasks[w] = Task.Factory.StartNew(() =>
            {
                try
                {
                    var parser = new MftBlockParser(table, c, issues);
                    foreach (var b in ready.GetConsumingEnumerable(token))
                    {
                        results[b.Index] = parser.Parse(b.Buffer, b.Length, b.FirstRecord);
                        free.Add(b.Buffer);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                    stop.Cancel();   // 解析线程出了 bug：别让读线程在等缓冲区里干耗着
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        Exception? readerFailure = null;
        try
        {
            c.Phase = "mft";
            long total = (long)count * size, done = 0;
            for (int b = 0; b < blocks; b++)
            {
                token.ThrowIfCancellationRequested();
                byte[] buffer = free.Take(token);
                int first = b * perBlock, length = Math.Min(perBlock, count - first) * size;
                stream.Read((long)first * size, buffer.AsSpan(0, length));
                ready.Add(new Block(b, first, length, buffer), token);
                done += length;
                c.Percent = (double)done / total;
                c.CurrentPath = L.T($"{root}$MFT · 记录 {first + length / size:N0} / {count:N0}", $"{root}$MFT · record {first + length / size:N0} / {count:N0}", $"{root}$MFT · レコード {first + length / size:N0} / {count:N0}");
            }
            stats.ReadBytes = done;
        }
        catch (Exception ex)
        {
            readerFailure = ex;
            stop.Cancel();
        }
        finally { ready.CompleteAdding(); }
        Task.WaitAll(tasks);
        // 解析线程的异常才是根因，读线程多半只是被它喊停时抛的取消
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        if (readerFailure != null) ExceptionDispatchInfo.Capture(readerFailure).Throw();
        ct.ThrowIfCancellationRequested();
        c.Phase = "tree";
        table.Gather(results);
        return table;
    }

    static void Summarize(MftTable t, MftStats stats)
    {
        stats.Slots = t.Count;
        for (int i = 0; i < t.Count; i++)
        {
            byte s = t.State[i];
            if ((s & MftTable.InUse) == 0) continue;
            if ((s & MftTable.Ext) != 0) stats.Extensions++;
            else
            {
                stats.InUse++;
                if ((s & MftTable.Dir) != 0) stats.Directories++;
            }
        }
    }

    /// 返回前对账：卷位图说用了多少，树里的占用加起来是多少。
    static void Reconcile(FileTree tree, MftGeometry g, ScanCounters counters, MftStats stats)
    {
        long counted = 0;
        for (int i = 0; i < tree.Count; i++)
            if (!tree.Has(i, NodeFlags.HardLinkDup)) counted += tree.Alloc(i);
        long used = (g.TotalClusters - g.FreeClusters) * g.BytesPerCluster;
        stats.Used = used;
        stats.Counted = counted;
        if (used > 0 && Math.Abs(counted - used) > used * 0.005)
            counters.Warn(L.T(
                $"MFT 对账差额超过 0.5%：卷已用 {used:N0} 字节，MFT 统计 {counted:N0} 字节（{(counted > used ? "多" : "少")}了 {Math.Abs(counted - used):N0}）；"
                    + "扫描期间文件在变化、存在损坏记录或有未覆盖的元数据都会造成差额",
                $"The MFT totals differ by more than 0.5%: the volume uses {used:N0} bytes, the MFT counts {counted:N0} bytes ({Math.Abs(counted - used):N0} {(counted > used ? "more" : "less")}); "
                    + "files changing during the scan, damaged records or uncovered metadata can all cause the difference",
                $"MFT の照合差が 0.5% を超えています：ボリュームの使用量 {used:N0} バイト、MFT の集計 {counted:N0} バイト（{Math.Abs(counted - used):N0} バイト{(counted > used ? "多い" : "少ない")}）。"
                    + "スキャン中のファイルの変化、壊れたレコード、カバーしていないメタデータのいずれも差の原因になります"));
    }
}
