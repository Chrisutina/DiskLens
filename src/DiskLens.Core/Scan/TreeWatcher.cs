using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace DiskLens.Core;

/// 扫描完成后盯着扫描范围的变化（实时状态）：别的程序删掉的文件和目录，树里跟着标删除，地图、列表、搜索同步消失。
/// - 只把「删除」和「改名掉的旧名字」落进树：这两种能按路径精确对上节点；落之前还要确认磁盘上真没了
///   （编辑器保存常常是先删再建同名文件，那不算删除）。
/// - 新建、改名出来的新名字没法不重扫就加进树，只计数提示「统计可能过期」；文件内容变大变小不跟踪。
/// - DiskLens 自己正在删的时候（同一扇写门被占着）先攒着，等删完再处理：那时它们已经被标掉了，按路径找不到，自然跳过，
///   这次删除的释放量也不会被这里抢走。
/// - 跟丢了（通知缓冲溢出、通知中断、积压太多）只记一笔 Overflow，提示重扫。系统不给变化通知的位置 Watching=false，地图不会自己变。
///   DiskLens 自己大批删除时缓冲照样会溢出，那时丢的多半是自己删的（树里早标掉了），不报跟丢。
/// - 扫描开始前就开始盯（ScanService）：树还没建好时只攒着，Attach 之后再落树。扫描读过的目录里随后被删的、
///   扫完到换上新结果之间被删的，都还在队列里，不会永远漏掉；扫描时就没读到的，按路径找不到节点，自然跳过。
public sealed class TreeWatcher : IDisposable
{
    const int BatchPerFlush = 5000, PathsPerLock = 200, MaxQueued = 200_000;
    FileTree? _tree; // Attach 之前是 null；读用 Volatile.Read，计时器线程上也看得到
    readonly int _scanId;
    readonly FileSystemWatcher? _fsw;
    readonly Timer? _timer;
    readonly ConcurrentQueue<string> _deleted = new();
    long _removed, _other;
    int _queued;
    long _reportedRemoved = -1, _reportedOther = -1;
    bool _reportedOverflow;
    volatile bool _overflow, _disposed;
    int _flushing;

    public bool Watching { get; }
    public event Action<TreeChangeDto>? Changed;

    public TreeWatcher(FileTree tree, int scanId, string root, int intervalMs = 300) : this(scanId, root, intervalMs) => Attach(tree);

    /// 树还没有：先盯着、攒着，扫完了 Attach
    public TreeWatcher(int scanId, string root, int intervalMs = 300)
    {
        _scanId = scanId;
        try
        {
            _fsw = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,
            };
            _fsw.Deleted += (_, e) => Queue(e.FullPath);
            _fsw.Renamed += (_, e) => { Queue(e.OldFullPath); Interlocked.Increment(ref _other); };
            _fsw.Created += (_, _) => Interlocked.Increment(ref _other);
            _fsw.Error += (_, e) => Lost(e.GetException().Message);
            _fsw.EnableRaisingEvents = true;
            Watching = true;
            _timer = new Timer(_ => Flush(), null, intervalMs, intervalMs);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException or UnauthorizedAccessException or Win32Exception)
        {
            _fsw?.Dispose();
            _fsw = null;
            Trace.TraceWarning("没法跟踪 {0} 的变化：{1}", root, ex.Message);
        }
    }

    /// 自己删大批文件时写门一直被占着，这边只攒不处理；攒太多就不攒了，记成跟丢，别把内存吃光
    void Queue(string path)
    {
        if (Interlocked.Increment(ref _queued) > MaxQueued) { Interlocked.Decrement(ref _queued); Lost(L.T("积压的删除通知太多", "Too many pending delete notifications", "未処理の削除通知が多すぎます")); }
        else _deleted.Enqueue(path);
    }

    /// 跟丢了一些变化。写门被占着说明 DiskLens 自己正在删，丢的多半是自己删的那些，不报。
    // ponytail: 那段时间里别的程序删的也一起漏了，要等下次扫描；真要分清得在删除结束后对一遍账
    internal void Lost(string why)
    {
        Trace.TraceWarning("磁盘变化通知跟丢了：{0}", why);
        if (Volatile.Read(ref _tree) is not { } tree || DeleteService.GateOf(tree).CurrentCount > 0) _overflow = true;
    }

    /// 扫描结果换上了：从这里开始把攒下的删除落进这棵树。只能接一次
    public void Attach(FileTree tree)
    {
        if (Interlocked.CompareExchange(ref _tree, tree, null) != null) throw new InvalidOperationException(L.T("已经接上了一棵树", "A tree is already attached", "ツリーはすでに接続されています"));
    }

    /// 当前累计状态；也记成「已报告」，计时器不会再报一遍一样的。要先 Attach
    public TreeChangeDto Snapshot()
    {
        var tree = Volatile.Read(ref _tree) ?? throw new InvalidOperationException(L.T("还没接上扫描结果", "No scan result attached yet", "スキャン結果がまだ接続されていません"));
        lock (_deleted)
        {
            (_reportedRemoved, _reportedOther, _reportedOverflow) = (Interlocked.Read(ref _removed), Interlocked.Read(ref _other), _overflow);
            return Make(tree, _reportedRemoved, _reportedOther, _reportedOverflow);
        }
    }

    /// 自检和计时器用：处理攒下的删除，有变化就发 Changed。还没接上树就只攒着
    public void Flush()
    {
        if (_disposed || Volatile.Read(ref _tree) is not { } tree || Interlocked.Exchange(ref _flushing, 1) == 1) return;
        try
        {
            if (!_deleted.IsEmpty)
            {
                var gate = DeleteService.GateOf(tree);
                if (gate.Wait(0))
                {
                    try { ApplyDeletions(tree); }
                    finally { gate.Release(); }
                }
            }
            Report(tree);
        }
        catch (Exception ex) { Trace.TraceWarning("处理磁盘变化失败：{0}", ex.Message); }
        finally { Volatile.Write(ref _flushing, 0); }
    }

    void ApplyDeletions(FileTree tree)
    {
        var paths = new List<string>();
        while (paths.Count < BatchPerFlush && _deleted.TryDequeue(out var p)) { paths.Add(p); Interlocked.Decrement(ref _queued); }
        var candidates = new List<(int Id, string Path)>();
        var distinct = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // 分片持锁：别的程序一口气删几千个文件时，界面的查询照样能插进来
        // ponytail: FindChild 在子项里线性找名字，十万级子项的目录里大批删除会慢；真遇到就给目录建名字索引
        for (int i = 0; i < distinct.Count; i += PathsPerLock)
            lock (tree.Sync)
                foreach (string p in distinct.Skip(i).Take(PathsPerLock))
                {
                    int n = tree.FindByPath(p);
                    if (n > FileTree.RootId && !tree.IsDeleted(n) && !tree.Has(n, NodeFlags.Virtual | NodeFlags.Metafile)) candidates.Add((n, p));
                }
        // 磁盘 IO 在树锁外。删了又马上建回同名的（编辑器保存），不当删除
        var gone = candidates.Where(c => NativeFs.Probe(c.Path) == PathState.Missing).Select(c => c.Id).ToList();
        if (gone.Count == 0) return;
        var (_, entries) = DeleteService.ApplyDeleted(tree, gone);
        Interlocked.Add(ref _removed, entries);
    }

    void Report(FileTree tree)
    {
        TreeChangeDto change;
        lock (_deleted)
        {
            long removed = Interlocked.Read(ref _removed), other = Interlocked.Read(ref _other);
            bool overflow = _overflow;
            if (removed == _reportedRemoved && other == _reportedOther && overflow == _reportedOverflow) return;
            (_reportedRemoved, _reportedOther, _reportedOverflow) = (removed, other, overflow);
            change = Make(tree, removed, other, overflow);
        }
        Changed?.Invoke(change);
    }

    TreeChangeDto Make(FileTree tree, long removed, long other, bool overflow)
    {
        int version;
        lock (tree.Sync) version = tree.Version;
        return new(_scanId, Watching, removed, other, overflow, version);
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _fsw?.Dispose();
    }
}
