namespace DiskLens.Core;

/// 同一个 FileId 出现不止一次 = 同一个文件的多个硬链接。256 片各自加锁，扫描线程之间几乎不会撞车。
/// 每个文件登记一条（FileId → 首个节点），几百万文件就是几百万条，所以用普通 Dictionary 而不是
/// ConcurrentDictionary —— 后者每条一个堆对象，GC 扫起来要命。
internal sealed class HardLinks
{
    const int Shards = 256;
    readonly Dictionary<UInt128, int>[] _first = new Dictionary<UInt128, int>[Shards];
    readonly Dictionary<UInt128, List<int>>?[] _groups = new Dictionary<UInt128, List<int>>?[Shards];

    public HardLinks()
    {
        for (int i = 0; i < Shards; i++) _first[i] = new();
    }

    public void Register(UInt128 id, int node)
    {
        // NTFS 的 FileId 高 64 位恒为 0、低 48 位是连续的记录号；乘个黄金分割数再取高字节，片间才均匀
        int shard = (int)((((ulong)id ^ (ulong)(id >> 64)) * 0x9E3779B97F4A7C15UL) >> 56);
        lock (_first[shard])
        {
            if (_first[shard].TryAdd(id, node)) return;
            var groups = _groups[shard] ??= new();
            if (!groups.TryGetValue(id, out var list)) groups[id] = list = [_first[shard][id]];
            list.Add(node);
        }
    }

    /// 所有出现过不止一次的文件，每组是它在树里的全部节点。取走后登记表就没用了，顺手放掉让 GC 回收。
    public List<int[]> TakeGroups()
    {
        var result = new List<int[]>();
        foreach (var shard in _groups)
            if (shard != null)
                foreach (var list in shard.Values) result.Add(list.ToArray());
        Array.Clear(_first);
        Array.Clear(_groups);
        return result;
    }
}

internal static class WalkLinks
{
    const uint FileReadAttributes = 0x80, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    const int FileStandardInfo = 1, FileAttributeTagInfo = 9;

    /// 遍历结束后逐组复核：目录项里的大小对多链接文件可能是过期的，要打开文件问一次真实值，
    /// 全组统一改过去，再交给 FileTree 按路径挑出计入的那个链接。
    public static void Resolve(FileTree tree, HardLinks links, ScanCounters counters, int parallelism, CancellationToken ct)
    {
        var groups = links.TakeGroups();
        if (groups.Count == 0) return;
        var truth = new (bool Ok, long Size, long Alloc)[groups.Count];
        // 每组要开一次文件，系统盘上有十几万组，串行得好几秒。此刻树没有写者，GetPath 并发读是安全的。
        Parallel.For(0, groups.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = parallelism }, i =>
        {
            // 第一个链接打不开（目录没权限、刚被删）就换下一个，别整组放弃
            foreach (int id in groups[i])
                if (TryReadTruth(tree.GetPath(id), out long size, out long alloc)) { truth[i] = (true, size, alloc); return; }
        });
        for (int i = 0; i < groups.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ids = groups[i];
            if (truth[i].Ok)
            {
                foreach (int id in ids) tree.SetSizes(id, truth[i].Size, truth[i].Alloc);
            }
            // 全组的链接都打不开（被独占 / 没权限）：沿用目录项的值，记进清单，不弹警告惊动用户
            else counters.Record(ScanIssueKind.Error, tree.GetPath(ids[0]), L.T("硬链接的真实大小无法核实（每个链接都打不开），沿用目录项的值", "Couldn't verify the real size of a hard link (none of its links could be opened); using the directory entry's value", "ハードリンクの実際のサイズを確認できません（どのリンクも開けません）。ディレクトリ エントリの値を使います"));
            tree.ResolveHardLinkGroup(ids);
            counters.HardLinkDups += ids.Length - 1;
            // 省下的字节 = 所有副本自己的占用；全组大小一致时就是 (组大小 - 1) * alloc，与 RefreshSummary 同口径
            foreach (int id in ids)
                if (tree.Has(id, NodeFlags.HardLinkDup)) counters.HardLinkSavedBytes += tree.Alloc(id);
        }
    }

    static bool TryReadTruth(string path, out long size, out long alloc)
    {
        size = alloc = 0;
        // 只要属性权限，全共享；OPEN_REPARSE_POINT 保证打开的是链接本身，不去触发云文件下载
        using var handle = ScanNative.CreateFileW(ScanNative.LongPath(path), FileReadAttributes, 7, IntPtr.Zero, 3,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) return false;
        var standard = new byte[24]; // FILE_STANDARD_INFO：AllocationSize(0) EndOfFile(8)
        if (!ScanNative.GetFileInformationByHandleEx(handle, FileStandardInfo, standard, standard.Length)) return false;
        alloc = Math.Max(0, ScanNative.I64(standard, 0));
        size = Math.Max(0, ScanNative.I64(standard, 8));
        var tag = new byte[8]; // FILE_ATTRIBUTE_TAG_INFO：FileAttributes(0) ReparseTag(4)
        if (ScanNative.GetFileInformationByHandleEx(handle, FileAttributeTagInfo, tag, tag.Length)
            && WalkReader.NeedsRealAlloc(ScanNative.Flags(ScanNative.U32(tag, 0)), ScanNative.U32(tag, 4), size, alloc, true)
            && ScanNative.TryCompressedSize(path, out long real, out _))
            alloc = real;
        return true;
    }
}
