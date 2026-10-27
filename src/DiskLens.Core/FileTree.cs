using System.Text;

namespace DiskLens.Core;

/// 位值和前端 types.ts 的 F 位一一对应，别随手挪。
[Flags]
public enum NodeFlags : ushort
{
    None = 0,
    Directory = 1,
    HardLinkDup = 2,
    Reparse = 4,
    Inaccessible = 8,
    Hidden = 16,
    System = 32,
    Compressed = 64,
    Sparse = 128,
    Cloud = 256,
    Metafile = 512,
    Deleted = 1024,
    Virtual = 2048,
    Encrypted = 4096,
}

/// 只出现在 tree.view 结果里，树本身从来不存这两位。
public static class ViewFlags
{
    public const int Aggregate = 1 << 13;
    public const int Unexpanded = 1 << 14;
}

/// 整棵扫描树，结构数组存储，下标即节点 ID，0 是根。
/// 不变量：子节点下标一定大于父节点下标，所以聚合倒序扫一遍就完事，不用递归。
/// 构建期只追加、不加锁（调用方自己串行）；Seal 之后才能查询，读写都要持有 Sync。
public sealed class FileTree
{
    public const int NoNode = -1;
    public const int RootId = 0;

    public readonly object Sync = new();
    public string RootPath { get; }
    public int Count => _count;
    public bool IsSealed { get; private set; }
    /// 每次 Seal / MarkDeleted 都会 +1，前端和各种缓存靠它判断数据是否过期。
    public int Version { get; private set; }

    int _count;
    int[] _parent, _nameOff, _ext, _files, _dirs;
    ushort[] _nameLen;
    NodeFlags[] _flags;
    byte[] _cat;
    long[] _size, _alloc, _mtime;
    char[] _names;
    int _namesLen;

    int[] _childStart = [], _childCount = [], _children = [];
    byte[] _purpose = [], _role = [];
    Dictionary<int, OwnerRoot> _ownerRoots = [];
    Owner[] _owners = [];

    readonly List<byte> _extCat = [FileCategories.Other];
    readonly List<string> _extNames = [""];
    readonly Dictionary<string, int> _extIds = new(StringComparer.Ordinal);
    readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _extLookup;

    // 只有多链接文件才进来，稀疏存放；同组成员共享同一个数组
    readonly Dictionary<int, int[]> _linkGroups = new();

    public FileTree(string rootPath, int capacity = 1 << 16)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        RootPath = rootPath;
        capacity = Math.Max(capacity, 16);
        _parent = new int[capacity];
        _nameOff = new int[capacity];
        _ext = new int[capacity];
        _files = new int[capacity];
        _dirs = new int[capacity];
        _nameLen = new ushort[capacity];
        _flags = new NodeFlags[capacity];
        _cat = new byte[capacity];
        _size = new long[capacity];
        _alloc = new long[capacity];
        _mtime = new long[capacity];
        _names = new char[capacity * 12];
        _extLookup = _extIds.GetAlternateLookup<ReadOnlySpan<char>>();
        AddCore(NoNode, rootPath, NodeFlags.Directory, 0, 0, 0);
    }

    // ---------------- 构建期 ----------------

    /// <param name="mtime">FILETIME（UTC，100ns），未知传 0</param>
    public int AddNode(int parent, ReadOnlySpan<char> name, NodeFlags flags, long size, long alloc, long mtime)
    {
        if (IsSealed) throw new InvalidOperationException("树已封存，不能再添加节点");
        if ((uint)parent >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(parent));
        return AddCore(parent, name, flags, size, alloc, mtime);
    }

    public void SetSizes(int i, long size, long alloc)
    {
        RequireBuilding();
        _size[i] = size;
        _alloc[i] = alloc;
    }

    public void AddFlags(int i, NodeFlags flags)
    {
        RequireBuilding();
        _flags[i] |= flags;
        _cat[i] = CategoryFor(_flags[i], NameSpan(i), _ext[i]);
    }

    /// 同一文件的多个链接：完整路径（OrdinalIgnoreCase）最小的计入，其余标 HardLinkDup。
    /// 返回计入的那个节点。规则两种扫描模式共用，所以两边每个目录的数字能直接对。
    public int ResolveHardLinkGroup(ReadOnlySpan<int> nodes)
    {
        RequireBuilding();
        if (nodes.Length == 0) return NoNode;
        if (nodes.Length == 1) return nodes[0];
        var group = nodes.ToArray();
        int best = PickCanonical(group, includeDeleted: true);
        foreach (int n in group)
        {
            if (n == best) _flags[n] &= ~NodeFlags.HardLinkDup;
            else _flags[n] |= NodeFlags.HardLinkDup;
            _linkGroups[n] = group;
        }
        return best;
    }

    /// 聚合大小和计数，生成排好序的子节点数组。之后树只能查询和 MarkDeleted。
    public void Seal()
    {
        if (IsSealed) return;
        int n = _count;
        for (int i = n - 1; i > 0; i--)
        {
            int p = _parent[i];
            var f = _flags[i];
            if ((f & NodeFlags.HardLinkDup) == 0)
            {
                _size[p] += _size[i];
                _alloc[p] += _alloc[i];
            }
            _files[p] += _files[i];
            _dirs[p] += _dirs[i] + (IsRealDir(f) ? 1 : 0);
        }
        BuildChildren();
        IsSealed = true;
        Version++;
    }

    // ---------------- 查询期 ----------------

    public int Parent(int i) => _parent[i];
    public NodeFlags Flags(int i) => _flags[i];
    public bool Has(int i, NodeFlags f) => (_flags[i] & f) != 0;
    public bool IsDir(int i) => (_flags[i] & NodeFlags.Directory) != 0;
    public bool IsDeleted(int i) => (_flags[i] & NodeFlags.Deleted) != 0;
    public bool IsValid(int i) => (uint)i < (uint)_count && !IsDeleted(i);
    public long Size(int i) => _size[i];
    public long Alloc(int i) => _alloc[i];
    public long Value(int i, Metric m) => m == Metric.Alloc ? _alloc[i] : _size[i];
    /// 对父目录的贡献：硬链接副本是 0。
    public long CountedValue(int i, Metric m) => Has(i, NodeFlags.HardLinkDup) ? 0 : Value(i, m);
    public long MTime(int i) => _mtime[i];
    public long Files(int i) => _files[i];
    public long Dirs(int i) => _dirs[i];
    public byte Category(int i) => _cat[i];
    public int ExtId(int i) => _ext[i];
    public string Ext(int i) => _extNames[_ext[i]];
    public string ExtName(int extId) => _extNames[extId];
    public byte ExtCategory(int extId) => _extCat[extId];
    public int ExtCount => _extNames.Count;

    public ReadOnlySpan<char> NameSpan(int i) => _names.AsSpan(_nameOff[i], _nameLen[i]);
    public string GetName(int i) => new(NameSpan(i));

    /// 搜索索引直接吃原始名字池，省得两百万次 new string。
    public ReadOnlySpan<char> NamePool => _names.AsSpan(0, _namesLen);
    public int NameOffset(int i) => _nameOff[i];
    public int NameLength(int i) => _nameLen[i];

    /// 按 alloc 降序；已删除的排在最后。
    public ReadOnlySpan<int> Children(int i)
    {
        RequireSealed();
        return _children.AsSpan(_childStart[i], _childCount[i]);
    }

    /// 去掉尾部已删除节点后的子节点。
    public ReadOnlySpan<int> LiveChildren(int i)
    {
        var c = Children(i);
        int n = c.Length;
        while (n > 0 && IsDeleted(c[n - 1])) n--;
        return c[..n];
    }

    public int Depth(int i)
    {
        int d = 0;
        for (int p = _parent[i]; p != NoNode; p = _parent[p]) d++;
        return d;
    }

    public bool IsAncestorOrSelf(int ancestor, int node)
    {
        for (int x = node; x != NoNode; x = _parent[x])
            if (x == ancestor) return true;
        return false;
    }

    /// 虚拟分组目录（如「$NTFS 元数据」）不出现在真实路径里。
    public string GetPath(int i)
    {
        if (i == RootId) return RootPath;
        int depth = 0;
        for (int x = i; x != RootId; x = _parent[x]) depth++;
        Span<int> chain = depth <= 256 ? stackalloc int[depth] : new int[depth];
        int k = depth;
        for (int x = i; x != RootId; x = _parent[x]) chain[--k] = x;
        var sb = new StringBuilder(RootPath.Length + depth * 16);
        sb.Append(RootPath);
        foreach (int x in chain)
        {
            if (x != i && Has(x, NodeFlags.Virtual)) continue;
            if (sb[^1] != '\\') sb.Append('\\');
            sb.Append(NameSpan(x));
        }
        return sb.ToString();
    }

    /// 不区分大小写；找不到返回 NoNode。会透明穿过一层虚拟分组目录。
    public int FindByPath(string path)
    {
        RequireSealed();
        var p = Normalize(path);
        var root = Normalize(RootPath);
        if (!p.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return NoNode;
        if (p.Length == root.Length) return RootId;
        if (p[root.Length] != '\\') return NoNode;
        int cur = RootId;
        var rest = p.AsSpan(root.Length + 1);
        foreach (var r in rest.Split('\\'))
        {
            var seg = rest[r];
            if (seg.IsEmpty) continue;
            cur = FindChild(cur, seg);
            if (cur == NoNode) return NoNode;
        }
        return cur;

        static string Normalize(string s)
        {
            s = s.Replace('/', '\\');
            if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) s = @"\\" + s[8..];
            else if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
            return s.TrimEnd('\\');
        }
    }

    public int FindChild(int parent, ReadOnlySpan<char> name)
    {
        foreach (int c in LiveChildren(parent))
            if (NameSpan(c).Equals(name, StringComparison.OrdinalIgnoreCase)) return c;
        foreach (int c in LiveChildren(parent))
        {
            if (!Has(c, NodeFlags.Virtual) || !IsDir(c)) continue;
            foreach (int g in LiveChildren(c))
                if (NameSpan(g).Equals(name, StringComparison.OrdinalIgnoreCase)) return g;
        }
        return NoNode;
    }

    /// 同组的所有链接节点（含自己）；不是硬链接返回 null。
    public int[]? HardLinkGroup(int i) => _linkGroups.TryGetValue(i, out var g) ? g : null;

    /// 硬链接组里当前计入大小的那个节点；不是硬链接返回自己。
    public int CountedLink(int i)
    {
        var g = HardLinkGroup(i);
        if (g == null) return i;
        foreach (int n in g)
            if (!IsDeleted(n) && !Has(n, NodeFlags.HardLinkDup)) return n;
        return NoNode;
    }

    /// 用途类别 U。分类器跑之前给默认值：虚拟叶子是「未解释空间」，元文件是「系统与恢复」，其余「未识别」。
    public byte Purpose(int i)
    {
        if (i < _purpose.Length) return _purpose[i];
        var f = _flags[i];
        if ((f & NodeFlags.Virtual) != 0 && (f & NodeFlags.Directory) == 0) return Purposes.Unexplained;
        return (f & NodeFlags.Metafile) != 0 ? Purposes.System : Purposes.Unknown;
    }

    /// 数据角色（Roles）。分类器跑之前一律是未知。
    public byte Role(int i) => i < _role.Length ? (byte)(_role[i] & Classification.RoleMask) : Roles.Unknown;

    /// 用途的证据等级（和角色挤在同一个字节的高 2 位）。分类器跑之前一律是未识别。
    public Evidence PurposeEvidence(int i) => i < _role.Length ? (Evidence)(_role[i] >> 6) : Evidence.Unknown;

    /// 这次分类认出的全部所属对象。
    public IReadOnlyList<Owner> Owners => _owners;

    /// 节点最近的所属对象：从自己往上找第一个所属对象的根（root 返回那个根）；一路都没有就是 null。调用方持有 Sync。
    public Owner? OwnerOf(int i, out int root)
    {
        for (int x = i; x != NoNode; x = _parent[x])
            if (_ownerRoots.TryGetValue(x, out var r))
            {
                root = x;
                return _owners[r.Owner];
            }
        root = NoNode;
        return null;
    }

    /// 所属对象的这个根是凭什么认出来的（详情里的依据用）。
    internal OwnerSource OwnerSourceAt(int root) => _ownerRoots[root].Source;

    /// 节点归到这个根所属对象名下的证据：位置规则、项目标记、安装信息、包名是已确认，按目录名猜的是推测。
    public Evidence OwnerEvidenceAt(int root) => _ownerRoots[root].Evidence;

    /// 所属对象的全部根（根节点 → 哪个对象、凭什么认出来的），按对象聚合时遍历用。调用方持有 Sync。
    internal IReadOnlyDictionary<int, OwnerRoot> OwnerRoots => _ownerRoots;

    /// 只换用途、不碰角色；正常流程用 SetClassification，这个留给自检塞手造的用途。调用方持有 Sync。
    public void SetPurposes(byte[] purposes)
    {
        RequireSealed();
        if (purposes.Length != _count) throw new ArgumentException("用途数组长度必须等于节点数", nameof(purposes));
        foreach (byte u in purposes)
            if (u >= Purposes.Count) throw new ArgumentOutOfRangeException(nameof(purposes), "用途编号越界");
        _purpose = purposes;
        Version++;
    }

    /// PurposeClassifier 算完整棵树后一次性写入用途、角色、证据和所属对象。调用方持有 Sync。
    public void SetClassification(Classification c)
    {
        if (c.RoleBits.Length != c.Purposes.Length) throw new ArgumentException("角色数组长度必须等于节点数", nameof(c));
        foreach (byte b in c.RoleBits)
            if ((b & Classification.RoleMask) >= Roles.Count || b >> 6 > (int)Evidence.Confirmed)
                throw new ArgumentOutOfRangeException(nameof(c), "角色或证据编号越界");
        foreach (var (node, root) in c.OwnerRoots)
            if ((uint)node >= (uint)c.Purposes.Length || (uint)root.Owner >= (uint)c.Owners.Length)
                throw new ArgumentOutOfRangeException(nameof(c), "所属对象的根越界");
        SetPurposes(c.Purposes);
        _role = c.RoleBits;
        _ownerRoots = c.OwnerRoots;
        _owners = c.Owners;
    }

    /// 整棵树按用途汇总（只算计入的、未删除的非目录节点，含「未统计」虚拟叶子）。
    public long[] PurposeTotals(Metric m)
    {
        var t = new long[Purposes.Count];
        for (int i = 1; i < _count; i++)
        {
            var f = _flags[i];
            if ((f & (NodeFlags.Directory | NodeFlags.Deleted | NodeFlags.HardLinkDup)) != 0) continue;
            t[Purpose(i)] += m == Metric.Alloc ? _alloc[i] : _size[i];
        }
        return t;
    }

    /// 整棵树按类别汇总（只算计入的、未删除的文件）。
    public long[] CategoryTotals(Metric m)
    {
        var t = new long[FileCategories.Count];
        for (int i = 1; i < _count; i++)
        {
            var f = _flags[i];
            if ((f & (NodeFlags.Directory | NodeFlags.Deleted | NodeFlags.HardLinkDup)) != 0) continue;
            t[_cat[i]] += m == Metric.Alloc ? _alloc[i] : _size[i];
        }
        return t;
    }

    /// 标记删除整棵子树，从祖先扣掉它的计入值，返回净释放的 alloc。
    /// 子树里如果有硬链接组的计入链接、而组里还有活着的链接，计入身份会转给活着的链接，
    /// 所以删一个还有别处链接的文件，净释放是 0 —— 这才是磁盘上真实发生的事。
    public long MarkDeleted(int i)
    {
        RequireSealed();
        if (i <= RootId || i >= _count) throw new ArgumentOutOfRangeException(nameof(i));
        if (IsDeleted(i)) return 0;

        bool dup = Has(i, NodeFlags.HardLinkDup);
        long s = dup ? 0 : _size[i], a = dup ? 0 : _alloc[i];
        AdjustAncestors(i, -s, -a, -_files[i], -(_dirs[i] + (IsRealDir(_flags[i]) ? 1 : 0)));

        List<int>? countedLinks = null;
        var stack = new Stack<int>();
        stack.Push(i);
        while (stack.Count > 0)
        {
            int n = stack.Pop();
            _flags[n] |= NodeFlags.Deleted;
            if (_linkGroups.ContainsKey(n) && !Has(n, NodeFlags.HardLinkDup)) (countedLinks ??= []).Add(n);
            foreach (int c in Children(n))
                if (!IsDeleted(c)) stack.Push(c);
        }

        long promoted = 0;
        if (countedLinks != null)
        {
            foreach (int n in countedLinks)
            {
                int heir = PickCanonical(_linkGroups[n], includeDeleted: false);
                if (heir == NoNode) continue;
                _flags[n] |= NodeFlags.HardLinkDup;
                _flags[heir] &= ~NodeFlags.HardLinkDup;
                AdjustAncestors(heir, _size[heir], _alloc[heir], 0, 0);
                promoted += _alloc[heir];
                ResortAncestors(heir);
            }
        }

        ResortAncestors(i);
        Version++;
        return a - promoted;
    }

    /// 卷空间变化后校正“未统计”叶子；调用方持有 Sync。
    public void UpdateVirtualSizes(int i, long size, long alloc)
    {
        RequireSealed();
        if (!IsValid(i) || !Has(i, NodeFlags.Virtual) || IsDir(i) || Children(i).Length != 0)
            throw new ArgumentException("只能更新虚拟叶子节点", nameof(i));
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ArgumentOutOfRangeException.ThrowIfNegative(alloc);
        if (_size[i] == size && _alloc[i] == alloc) return;
        AdjustAncestors(i, size - _size[i], alloc - _alloc[i], 0, 0);
        _size[i] = size;
        _alloc[i] = alloc;
        ResortAncestors(i);
        Version++;
    }

    // ---------------- 内部 ----------------

    int AddCore(int parent, ReadOnlySpan<char> name, NodeFlags flags, long size, long alloc, long mtime)
    {
        int i = _count;
        if (i == _parent.Length) Grow();
        if (name.Length > ushort.MaxValue) name = name[..ushort.MaxValue];
        if (_namesLen + name.Length > _names.Length)
            Array.Resize(ref _names, Math.Max(_names.Length * 2, _namesLen + name.Length));
        name.CopyTo(_names.AsSpan(_namesLen));
        _nameOff[i] = _namesLen;
        _nameLen[i] = (ushort)name.Length;
        _namesLen += name.Length;

        bool fileLike = (flags & (NodeFlags.Directory | NodeFlags.Virtual)) == 0;
        _parent[i] = parent;
        _flags[i] = flags;
        _size[i] = size;
        _alloc[i] = alloc;
        _mtime[i] = mtime;
        _files[i] = fileLike ? 1 : 0;
        _dirs[i] = 0;
        _ext[i] = fileLike ? ExtIdFor(name) : 0;
        _cat[i] = CategoryFor(flags, name, _ext[i]);
        _count = i + 1;
        return i;
    }

    void Grow()
    {
        int n = _parent.Length * 2;
        Array.Resize(ref _parent, n);
        Array.Resize(ref _nameOff, n);
        Array.Resize(ref _ext, n);
        Array.Resize(ref _files, n);
        Array.Resize(ref _dirs, n);
        Array.Resize(ref _nameLen, n);
        Array.Resize(ref _flags, n);
        Array.Resize(ref _cat, n);
        Array.Resize(ref _size, n);
        Array.Resize(ref _alloc, n);
        Array.Resize(ref _mtime, n);
    }

    int ExtIdFor(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return 0;
        var e = name[(dot + 1)..];
        if (e.Length > 16 || e.Contains(' ')) return 0;
        Span<char> lower = stackalloc char[16];
        int len = e.ToLowerInvariant(lower);
        var key = lower[..len];
        if (_extLookup.TryGetValue(key, out int id)) return id;
        var s = new string(key);
        id = _extNames.Count;
        _extNames.Add(s);
        _extCat.Add(FileCategories.FromExtension(s));
        _extIds[s] = id;
        return id;
    }

    byte CategoryFor(NodeFlags f, ReadOnlySpan<char> name, int ext)
    {
        if ((f & NodeFlags.Directory) != 0) return FileCategories.Folder;
        if ((f & NodeFlags.Virtual) != 0) return FileCategories.Unaccounted;
        if ((f & NodeFlags.Metafile) != 0 || FileCategories.IsSystemFileName(name)) return FileCategories.System;
        return _extCat[ext];
    }

    static bool IsRealDir(NodeFlags f) => (f & (NodeFlags.Directory | NodeFlags.Virtual)) == NodeFlags.Directory;

    void BuildChildren()
    {
        int n = _count;
        var count = new int[n];
        for (int i = 1; i < n; i++) count[_parent[i]]++;
        var start = new int[n];
        int acc = 0;
        for (int i = 0; i < n; i++)
        {
            start[i] = acc;
            acc += count[i];
        }
        var children = new int[acc];
        var fill = (int[])start.Clone();
        for (int i = 1; i < n; i++) children[fill[_parent[i]]++] = i;
        _childStart = start;
        _childCount = count;
        _children = children;

        var keys = new long[acc];
        for (int k = 0; k < acc; k++) keys[k] = SortKey(children[k]);
        for (int p = 0; p < n; p++)
            if (count[p] > 1)
            {
                Array.Sort(keys, children, start[p], count[p]);
                OrderTiesById(keys.AsSpan(start[p], count[p]), children.AsSpan(start[p], count[p]));
            }
    }

    /// Array.Sort 不稳定：同占用的兄弟每次排出来的先后可能不一样，删一个文件后 treemap 里同尺寸的块会互换位置。
    /// 键相同的连续段再按 ID 升序排一下，两种排序路径（Seal、删除后重排）就都是同一个确定的顺序。
    static void OrderTiesById(ReadOnlySpan<long> keys, Span<int> items)
    {
        for (int a = 0; a < keys.Length;)
        {
            int b = a + 1;
            while (b < keys.Length && keys[b] == keys[a]) b++;
            if (b - a > 1) items[a..b].Sort();
            a = b;
        }
    }

    long SortKey(int i) => (_flags[i] & NodeFlags.Deleted) != 0 ? long.MaxValue : -CountedValue(i, Metric.Alloc);

    void ResortSegment(int p)
    {
        int s = _childStart[p], c = _childCount[p];
        if (c < 2) return;
        var seg = _children.AsSpan(s, c);
        var keys = new long[c];
        for (int k = 0; k < c; k++) keys[k] = SortKey(seg[k]);
        keys.AsSpan().Sort(seg);
        OrderTiesById(keys, seg);
    }

    void ResortAncestors(int i)
    {
        for (int p = _parent[i]; p != NoNode; p = _parent[p]) ResortSegment(p);
    }

    void AdjustAncestors(int i, long ds, long da, int df, int dd)
    {
        for (int p = _parent[i]; p != NoNode; p = _parent[p])
        {
            _size[p] += ds;
            _alloc[p] += da;
            _files[p] += df;
            _dirs[p] += dd;
        }
    }

    int PickCanonical(int[] group, bool includeDeleted)
    {
        int best = NoNode;
        string? bestPath = null;
        foreach (int n in group)
        {
            if (!includeDeleted && IsDeleted(n)) continue;
            var path = GetPath(n);
            if (bestPath == null || ComparePaths(path, bestPath) < 0)
            {
                best = n;
                bestPath = path;
            }
        }
        return best;

        static int ComparePaths(string a, string b)
        {
            int c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a, b);
        }
    }

    void RequireSealed()
    {
        if (!IsSealed) throw new InvalidOperationException("树还没 Seal，不能查询");
    }

    void RequireBuilding()
    {
        if (IsSealed) throw new InvalidOperationException("树已封存，不能修改结构");
    }
}
