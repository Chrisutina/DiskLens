using System.Collections.Concurrent;

namespace DiskLens.Core;

/// 扫描器只往这里写，ScanService 每 120 ms 读一次发进度事件。
/// 一律按目录或按块批量累加 —— 24 个线程每个文件抢一次同一条缓存行，慢得能让你怀疑人生。
public sealed class ScanCounters
{
    public long Files, Dirs, Bytes;
    /// 扫描报告的四类计数（dise §4.3）。只许经 Record / RecordLink 加，别直接改：清单和总数靠这个对得上。
    public long InaccessibleDirs, LinksSkipped, MountPointsSkipped, Errors;
    public long HardLinkDups, HardLinkSavedBytes;
    public readonly long[] CategoryBytes = new long[FileCategories.Count];

    volatile string _phase = "prepare";
    volatile string? _currentPath;
    long _percentBits = BitConverter.DoubleToInt64Bits(-1);
    readonly ConcurrentQueue<string> _warnings = new();

    /// 阶段一变就同步回调一次（ScanService 借它马上发进度）。回调跑在扫描线程上，别在里面干重活。
    public Action? PhaseChanged;

    /// prepare | mft | walk | tree | links | finalize
    public string Phase
    {
        get => _phase;
        set
        {
            if (_phase == value) return;
            _phase = value;
            PhaseChanged?.Invoke();
        }
    }

    public string? CurrentPath { get => _currentPath; set => _currentPath = value; }

    /// 0..1，未知为 -1。
    public double Percent
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _percentBits));
        set => Interlocked.Exchange(ref _percentBits, BitConverter.DoubleToInt64Bits(value));
    }

    public void AddBatch(long files, long dirs, long bytes, ReadOnlySpan<long> categoryBytes)
    {
        if (files != 0) Interlocked.Add(ref Files, files);
        if (dirs != 0) Interlocked.Add(ref Dirs, dirs);
        if (bytes != 0) Interlocked.Add(ref Bytes, bytes);
        for (int k = 0; k < categoryBytes.Length && k < CategoryBytes.Length; k++)
            if (categoryBytes[k] != 0) Interlocked.Add(ref CategoryBytes[k], categoryBytes[k]);
    }

    /// 异常清单每类最多记这么多条；计数照样往上数，所以总数始终准确，几十万个拒绝访问也不会把内存吃光。
    public const int MaxIssues = 10_000;
    readonly List<ScanIssueDto>[] _issues = [[], [], [], []];

    /// 记一条异常：对应的计数加一，前 MaxIssues 条连路径和原因一起留下。只在出事时调（每个目录至多一次），不在逐文件的热路径上。
    public void Record(ScanIssueKind kind, string path, string reason)
    {
        if (Interlocked.Increment(ref Count(kind)) > MaxIssues) return;
        var list = _issues[(int)kind];
        lock (list) list.Add(new(path, reason));
    }

    /// 已记下的清单按路径排好，翻页才稳、读起来也顺。扫描结束、发布结果之前调一次。
    public void SortIssues()
    {
        foreach (var list in _issues)
            lock (list)
                list.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path) is int c and not 0 ? c
                    : string.CompareOrdinal(a.Reason, b.Reason));
    }

    /// 一页清单：[offset, offset + limit) 和已记下部分的交集。Total 是准确总数，Recorded 是实际记下的条数（≤ MaxIssues）。
    public (long Total, int Recorded, ScanIssueDto[] Items) Page(ScanIssueKind kind, int offset, int limit)
    {
        var list = _issues[(int)kind];
        lock (list)
        {
            int from = Math.Clamp(offset, 0, list.Count);
            return (Interlocked.Read(ref Count(kind)), list.Count, list.GetRange(from, Math.Clamp(limit, 0, list.Count - from)).ToArray());
        }
    }

    /// 没进去的名称代理目录记一笔。联接点和卷挂载点共用一个标签，volume 说明它指向的是不是一个卷（null = 读不到，分不清）。
    public void RecordLink(string path, uint tag, bool? volume)
    {
        if (tag == ScanNative.TagMountPoint && volume == true)
        {
            Record(ScanIssueKind.MountPoint, path, L.T("卷挂载点：这里挂着另一个卷，按规则不进入，它的内容不属于本次扫描", "Volume mount point: another volume is mounted here; skipped by rule, and its contents aren't part of this scan", "ボリュームのマウント ポイント：別のボリュームがマウントされています。規則により中に入らず、その内容は今回のスキャンに含まれません"));
            return;
        }
        Record(ScanIssueKind.Link, path, tag switch
        {
            ScanNative.TagMountPoint when volume == null => L.T("联接点或卷挂载点（读不到目标，按链接算）：按规则不进入", "Junction or volume mount point (target unreadable, treated as a link): skipped by rule", "ジャンクションまたはボリュームのマウント ポイント（リンク先を読めないためリンクとして扱います）：規則により中に入りません"),
            ScanNative.TagMountPoint => L.T("联接点（junction）：指向别处的目录，按规则不进入，免得重复统计或绕成环", "Junction: a folder pointing elsewhere; skipped by rule to avoid double counting or loops", "ジャンクション：別の場所を指すフォルダーです。二重計上やループを避けるため、規則により中に入りません"),
            ScanNative.TagSymlink => L.T("目录符号链接：按规则不进入，免得重复统计或绕成环", "Directory symbolic link: skipped by rule to avoid double counting or loops", "ディレクトリのシンボリック リンク：二重計上やループを避けるため、規則により中に入りません"),
            0 => L.T("重解析目录（文件系统没给出类型）：不知道指向哪里，按规则不进入", "Reparse folder (no type from the file system): its target is unknown, so it's skipped by rule", "再解析フォルダー（ファイル システムが種類を返しません）：リンク先が分からないため、規則により中に入りません"),
            _ => L.T($"链接类重解析点（标签 0x{tag:X8}）：按规则不进入", $"Link-type reparse point (tag 0x{tag:X8}): skipped by rule", $"リンク系の再解析ポイント（タグ 0x{tag:X8}）：規則により中に入りません"),
        });
    }

    ref long Count(ScanIssueKind kind)
    {
        switch (kind)
        {
            case ScanIssueKind.Denied: return ref InaccessibleDirs;
            case ScanIssueKind.Link: return ref LinksSkipped;
            case ScanIssueKind.MountPoint: return ref MountPointsSkipped;
            default: return ref Errors;
        }
    }

    public void Warn(string message)
    {
        if (_warnings.Count < 50) _warnings.Enqueue(message);
    }

    public string[] Warnings => _warnings.ToArray();

    public long[] SnapshotCategories()
    {
        var a = new long[CategoryBytes.Length];
        for (int k = 0; k < a.Length; k++) a[k] = Interlocked.Read(ref CategoryBytes[k]);
        return a;
    }
}
