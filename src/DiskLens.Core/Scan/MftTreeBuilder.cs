namespace DiskLens.Core;

/// 从整卷快照建出 FileTree：规整名字 → 按父目录分桶 → 从根 BFS → 安置孤立记录 → 核对硬链接。
/// 全程单线程，没有递归；每个目录只会被展开一次，所以父链成环也不可能死循环。
internal sealed class MftTreeBuilder
{
    // MftLink.Dropped
    internal const byte DropName = 1, DropParent = 2, DropRoot = 3;

    const NodeFlags VirtualGroup = NodeFlags.Directory | NodeFlags.Virtual | NodeFlags.Metafile;

    /// 两个虚拟分组的名字（中、英、日）：建树时按界面语言取一个，PurposeRules.IsMetaGroup 三种都认
    internal static readonly string[] MetaNames = ["$NTFS 元数据", "$NTFS metadata", "$NTFS メタデータ"];
    internal static readonly string[] OrphanNames = ["$孤立记录", "$Orphaned records", "$孤立レコード"];
    static string OrphanName => L.T(OrphanNames[0], OrphanNames[1], OrphanNames[2]);

    readonly MftTable _t;
    readonly ScanCounters _c;
    readonly CancellationToken _ct;
    readonly int[] _recStart, _parStart;
    int[] _recLinks = [], _parLinks = [], _nodeOfLink = [];
    readonly Queue<(int Record, int Node)> _queue = new();
    FileTree _tree = null!;
    int _meta, _orphan = FileTree.NoNode;

    MftTreeBuilder(MftTable t, ScanCounters c, CancellationToken ct)
    {
        _t = t;
        _c = c;
        _ct = ct;
        _recStart = new int[t.Count + 1];
        _parStart = new int[t.Count + 1];
    }

    /// 返回的树没有 Seal：ScanService 还要往根下加「未统计」节点。
    internal static FileTree Build(MftTable t, string root, ScanCounters counters, CancellationToken ct, MftStats stats)
    {
        const byte mask = MftTable.InUse | MftTable.Ext | MftTable.Bad | MftTable.Dir;
        if (t.Count <= 5 || (t.State[5] & mask) != (MftTable.InUse | MftTable.Dir))
            throw new InvalidDataException(L.T("MFT 根目录记录缺失或无效", "The MFT root folder record is missing or invalid", "MFT のルート フォルダーのレコードがないか無効です"));
        counters.Phase = "tree";
        return new MftTreeBuilder(t, counters, ct).Run(root, stats);
    }

    FileTree Run(string root, MftStats stats)
    {
        var t = _t;
        GroupByRecord();
        Normalize();
        GroupByParent(out int kept);
        _nodeOfLink = new int[t.LinkCount];
        _tree = new FileTree(root, kept + 1024);
        // 根目录自己的 $INDEX_ALLOCATION 和属性（C:\ 通常是隐藏+系统）
        _tree.SetSizes(0, t.Size[5], t.Alloc[5]);
        _tree.AddFlags(0, t.Flags[5]);
        t.State[5] |= MftTable.Visited;
        _meta = _tree.AddNode(0, L.T(MetaNames[0], MetaNames[1], MetaNames[2]), VirtualGroup, 0, 0, 0);
        _queue.Enqueue((5, 0));
        Drain();
        stats.Orphans = PlaceOrphans();
        _c.Phase = "links";
        stats.LinkGroups = ResolveHardLinks();
        stats.Links = kept;
        stats.Nodes = _tree.Count;
        return _tree;
    }

    // ---------------- 名字规整 ----------------

    /// 计数排序：同一基记录的链接排在一起（扩展记录的名字合并后，原来的块顺序已经不对了）。
    void GroupByRecord()
    {
        var t = _t;
        var links = t.Links;
        for (int i = 0; i < t.LinkCount; i++)
            if ((uint)links[i].Record < (uint)t.Count) _recStart[links[i].Record + 1]++;
        for (int r = 0; r < t.Count; r++) _recStart[r + 1] += _recStart[r];
        _recLinks = new int[_recStart[t.Count]];
        var fill = new int[t.Count];
        Array.Copy(_recStart, fill, t.Count);
        for (int i = 0; i < t.LinkCount; i++)
            if ((uint)links[i].Record < (uint)t.Count) _recLinks[fill[links[i].Record]++] = i;
    }

    /// 决定每个链接算不算数。算数的 Dropped == 0。
    void Normalize()
    {
        var t = _t;
        var links = t.Links;
        for (int r = 0; r < t.Count; r++)
        {
            int s = _recStart[r], e = _recStart[r + 1];
            if (s == e) continue;
            if ((r & 0xFFFF) == 0) _ct.ThrowIfCancellationRequested();
            if (r == 5)
            {
                // 根目录那个指向自己的 "." 名字
                for (int k = s; k < e; k++) links[_recLinks[k]].Dropped = DropRoot;
                continue;
            }
            // 有任何非 DOS 名字，就扔掉纯 DOS 短名：它们只是同一个链接的另一个叫法，不是硬链接
            bool anyLong = false;
            for (int k = s; k < e && !anyLong; k++) anyLong = links[_recLinks[k]].Namespace != 2;
            bool directory = (t.State[r] & MftTable.Dir) != 0, haveParent = false;
            for (int k = s; k < e; k++)
            {
                ref var l = ref links[_recLinks[k]];
                if (anyLong && l.Namespace == 2) { l.Dropped = DropName; continue; }
                if (IsDuplicate(links, s, k, ref l)) { l.Dropped = DropName; continue; }
                // 序列号对不上说明父目录的记录已被复用：这个链接指向的是别人，不能信
                if (!ParentOk(l.Parent, r)) { l.Dropped = DropParent; continue; }
                // 目录不可能有第二个父目录，多出来的是损坏数据；认了就会出现一个目录两处挂载
                if (directory && haveParent) { l.Dropped = DropName; continue; }
                haveParent = true;
            }
        }
    }

    bool IsDuplicate(MftLink[] links, int s, int k, ref MftLink l)
    {
        var chars = _t.Chars;
        for (int j = s; j < k; j++)
        {
            ref var o = ref links[_recLinks[j]];
            if (o.Dropped == DropName || ((o.Parent ^ l.Parent) & MftRecordParser.ReferenceMask) != 0 || o.NameLength != l.NameLength) continue;
            if (chars.AsSpan(o.NameOffset, o.NameLength).SequenceEqual(chars.AsSpan(l.NameOffset, l.NameLength))) return true;
        }
        return false;
    }

    bool ParentOk(ulong parent, int self)
    {
        const byte mask = MftTable.InUse | MftTable.Ext | MftTable.Bad | MftTable.Dir;
        ulong p = parent & MftRecordParser.ReferenceMask;
        if (p >= (ulong)_t.Count || p == (ulong)self) return false;
        return (_t.State[p] & mask) == (MftTable.InUse | MftTable.Dir) && _t.Sequence[p] == (ushort)(parent >> 48);
    }

    void GroupByParent(out int kept)
    {
        var t = _t;
        var links = t.Links;
        kept = 0;
        for (int i = 0; i < t.LinkCount; i++)
        {
            if (links[i].Record < 0 || links[i].Dropped != 0) continue;
            _parStart[(int)(links[i].Parent & MftRecordParser.ReferenceMask) + 1]++;
            kept++;
        }
        for (int r = 0; r < t.Count; r++) _parStart[r + 1] += _parStart[r];
        _parLinks = new int[kept];
        var fill = new int[t.Count];
        Array.Copy(_parStart, fill, t.Count);
        for (int i = 0; i < t.LinkCount; i++)
        {
            if (links[i].Record < 0 || links[i].Dropped != 0) continue;
            _parLinks[fill[(int)(links[i].Parent & MftRecordParser.ReferenceMask)]++] = i;
        }
    }

    // ---------------- 建树 ----------------

    void Drain()
    {
        int tick = 0;
        while (_queue.TryDequeue(out var work))
        {
            if ((++tick & 1023) == 0) _ct.ThrowIfCancellationRequested();
            Expand(work.Record, work.Node);
        }
    }

    void Expand(int record, int node)
    {
        var t = _t;
        var links = t.Links;
        for (int k = _parStart[record]; k < _parStart[record + 1]; k++)
        {
            int li = _parLinks[k], child = links[li].Record;
            if ((t.State[child] & (MftTable.Dir | MftTable.Visited)) == (MftTable.Dir | MftTable.Visited)) continue;
            // 0–15 号元文件（除根目录）统一收进虚拟目录，不管它们的父链怎么写
            int parent = child < 16 ? _meta : node;
            _nodeOfLink[li] = NewNode(child, parent, t.Chars.AsSpan(links[li].NameOffset, links[li].NameLength));
        }
    }

    int NewNode(int record, int parent, ReadOnlySpan<char> name)
    {
        var t = _t;
        bool directory = (t.State[record] & MftTable.Dir) != 0;
        var flags = t.Flags[record];
        if (directory) flags |= NodeFlags.Directory;
        // 元文件子树和孤立记录子树里的一切都带 Metafile：Protection 靠它拦删除
        if ((_tree.Flags(parent) & NodeFlags.Metafile) != 0) flags |= NodeFlags.Metafile;
        int id = _tree.AddNode(parent, name, flags, t.Size[record], t.Alloc[record], t.Mtime[record]);
        if (directory)
        {
            t.State[record] |= MftTable.Visited;
            // 名称代理：联接点、目录符号链接、卷挂载点。MFT 建树本来就不会穿过它们，按常规扫描的口径记成「按规则跳过」
            if ((t.Tag[record] & 0x20000000) != 0) _c.RecordLink(_tree.GetPath(id), t.Tag[record], (t.State[record] & MftTable.Mount) != 0);
            _queue.Enqueue((record, id));
        }
        return id;
    }

    // ---------------- 孤立记录 ----------------

    int OrphanGroup() => _orphan != FileTree.NoNode ? _orphan : _orphan = _tree.AddNode(0, OrphanName, VirtualGroup, 0, 0, 0);

    /// 返回孤立记录数（孤立子树的根）。
    int PlaceOrphans()
    {
        var t = _t;
        const byte mask = MftTable.InUse | MftTable.Ext | MftTable.Bad | MftTable.Dir | MftTable.Visited;
        int orphans = 0;
        // 第 1 遍：没有可信父链接的目录，是各个孤立子树的根
        for (int r = 0; r < t.Count; r++)
        {
            if ((r & 0xFFFF) == 0) _ct.ThrowIfCancellationRequested();
            if ((t.State[r] & mask) != (MftTable.InUse | MftTable.Dir) || KeptLinks(r) > 0) continue;
            ReportOrphan(AddOrphan(r));
            Drain();
            orphans++;
        }
        // 第 2 遍：剩下没到过的目录互相指着对方（A→B→A）。从任意一个切开，另一个自然挂到它下面
        for (int r = 0; r < t.Count; r++)
        {
            if ((r & 0xFFFF) == 0) _ct.ThrowIfCancellationRequested();
            if ((t.State[r] & mask) != (MftTable.InUse | MftTable.Dir)) continue;
            ReportOrphan(AddOrphan(r));
            Drain();
            orphans++;
        }
        // 第 3 遍：没有任何可信链接的文件。没有名字又没有内容的是 12–15 这类保留记录，不值得占一个节点
        for (int r = 0; r < t.Count; r++)
        {
            if ((r & 0xFFFF) == 0) _ct.ThrowIfCancellationRequested();
            if ((t.State[r] & mask) != MftTable.InUse || KeptLinks(r) > 0) continue;
            if (_recStart[r] == _recStart[r + 1] && t.Size[r] == 0 && t.Alloc[r] == 0) continue;
            int node = AddOrphan(r);
            if (r < 16) continue;   // 元文件段的无名记录进了「$NTFS 元数据」，不算孤立
            ReportOrphan(node);
            orphans++;
        }
        if (orphans > 0) _c.Warn(L.T($"{orphans:N0} 条 MFT 记录的父路径或序列号无法核实，已归入「{OrphanName}」并禁止直接删除",
            $"The parent path or sequence number of {L.Plural(orphans, "MFT record")} couldn't be verified; they were put under \"{OrphanName}\" and can't be deleted directly",
            $"{orphans:N0} 件の MFT レコードは親のパスまたはシーケンス番号を確認できないため、「{OrphanName}」に入れ、直接は削除できないようにしました"));
        return orphans;
    }

    int KeptLinks(int record)
    {
        int kept = 0;
        for (int k = _recStart[record]; k < _recStart[record + 1]; k++)
            if (_t.Links[_recLinks[k]].Dropped == 0) kept++;
        return kept;
    }

    /// 孤立子树的根记进「读取失败及其他错误」。路径写成「$孤立记录\名字」：它真正的位置正是不知道的那部分。
    void ReportOrphan(int node) => _c.Record(ScanIssueKind.Error, Path.Join(_tree.RootPath, OrphanName, _tree.GetName(node)),
        L.T($"父目录无法核实（父记录已不在用、序列号对不上或成环），已归入「{OrphanName}」",
            $"The parent folder couldn't be verified (parent record no longer in use, sequence number mismatch, or a loop); put under \"{OrphanName}\"",
            $"親フォルダーを確認できないため（親レコードが使用されていない、シーケンス番号が一致しない、またはループしている）、「{OrphanName}」に入れました"));

    int AddOrphan(int record)
    {
        var t = _t;
        var links = t.Links;
        int pick = -1;   // 优先用非 DOS、没被当重名扔掉的名字（父目录不可信的那种也行，我们只是要个名字）
        for (int k = _recStart[record]; k < _recStart[record + 1]; k++)
        {
            ref var l = ref links[_recLinks[k]];
            if (l.Dropped == DropName) continue;
            if (l.Namespace != 2) { pick = _recLinks[k]; break; }
            if (pick < 0) pick = _recLinks[k];
        }
        ReadOnlySpan<char> name = pick >= 0 ? t.Chars.AsSpan(links[pick].NameOffset, links[pick].NameLength) : (ReadOnlySpan<char>)L.T($"记录-{record}", $"record-{record}", $"レコード-{record}");
        return NewNode(record, record < 16 ? _meta : OrphanGroup(), name);
    }

    // ---------------- 硬链接 ----------------

    /// 一个文件记录有多个算数的链接就是硬链接；规则交给 FileTree，和 Walk 模式共用同一套。
    int ResolveHardLinks()
    {
        var t = _t;
        var links = t.Links;
        Span<int> small = stackalloc int[64];
        int groups = 0;
        for (int r = 0; r < t.Count; r++)
        {
            int s = _recStart[r], e = _recStart[r + 1];
            if (e - s < 2) continue;
            if ((r & 0xFFFF) == 0) _ct.ThrowIfCancellationRequested();
            int count = 0;
            for (int k = s; k < e; k++)
                if (links[_recLinks[k]].Dropped == 0 && _nodeOfLink[_recLinks[k]] != 0) count++;
            if (count < 2) continue;
            Span<int> nodes = count <= small.Length ? small[..count] : new int[count];
            int n = 0;
            for (int k = s; k < e; k++)
            {
                int node = links[_recLinks[k]].Dropped == 0 ? _nodeOfLink[_recLinks[k]] : 0;
                if (node != 0) nodes[n++] = node;
            }
            _tree.ResolveHardLinkGroup(nodes);
            _c.HardLinkDups += count - 1;
            _c.HardLinkSavedBytes += checked((count - 1L) * t.Alloc[r]);
            groups++;
        }
        return groups;
    }
}
