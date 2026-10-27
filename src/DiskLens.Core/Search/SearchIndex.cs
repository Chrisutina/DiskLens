using System.Diagnostics;
using System.Text;
using static DiskLens.Core.SearchQuery;

namespace DiskLens.Core;

/// 名字索引（DESIGN §5）：所有名字转小写后首尾相接，之间隔一个 '\0'，查询时在这一大块字符上做向量化子串扫描。
/// 建好之后索引只读，Query 之间没有任何共享的可变状态（路径缓存、堆、计数都是每次查询、每个线程自己的），
/// 所以并发查询天然安全；节点删除不重建索引，靠查询时看 Deleted 位。
/// 智能匹配（SearchRequest.Smart）另加两样：名字里的汉字按拼音首字母也能对上（第一次用到才建，只收带汉字的名字）；
/// 一条精确结果都没有时，把名字词放宽成近似匹配再找一遍。
public sealed class SearchIndex
{
    const int ChunkSize = 1 << 16;   // 每段 65536 个节点：段内连续扫，段间并行
    const int MaxLimit = 1000;
    const int MinFuzzy = 3;          // 近似匹配只放宽这么长以上的词：两个字母按顺序出现，什么名字都像
    static readonly int Workers = Math.Clamp(Environment.ProcessorCount, 1, 8);

    public FileTree Tree { get; }
    readonly int _count;
    readonly char[] _names;          // 小写名字，'\0' 分隔
    readonly int[] _offsets;         // 第 n 个名字的起点；_offsets[_count] 是总长。每个名字至少占一格（'\0'），所以严格递增，能二分
    readonly string _rootPath;       // 小写根路径，根节点自己的路径
    readonly string _rootPrefix;     // 同上，保证以 '\' 结尾，拼子节点用
    Pinyin? _pinyin;                 // 拼音首字母，第一次智能搜索时才建

    SearchIndex(FileTree tree, char[] names, int[] offsets)
    {
        Tree = tree;
        _names = names;
        _offsets = offsets;
        _count = offsets.Length - 1;
        _rootPath = tree.RootPath.ToLowerInvariant();
        _rootPrefix = _rootPath.EndsWith('\\') ? _rootPath : _rootPath + "\\";
    }

    /// 树 Seal 之后构建；调用方持有 tree.Sync。
    public static SearchIndex Build(FileTree tree, CancellationToken ct)
    {
        if (!tree.IsSealed) throw new InvalidOperationException(L.T("扫描尚未完成", "The scan isn't finished yet", "スキャンがまだ終わっていません"));
        int count = tree.Count;
        var offsets = new int[count + 1];
        int total = 0;
        for (int n = 0; n < count; n++)
        {
            offsets[n] = total;
            total = checked(total + tree.NameLength(n) + 1);
        }
        offsets[count] = total;

        var names = new char[total]; // 新数组全是 0，名字之间的 '\0' 不用再写
        int chunks = (count + ChunkSize - 1) / ChunkSize;
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Workers }, chunk =>
        {
            int end = Math.Min(count, (chunk + 1) * ChunkSize);
            for (int n = chunk * ChunkSize; n < end; n++)
            {
                var name = tree.NameSpan(n);
                name.ToLowerInvariant(names.AsSpan(offsets[n], name.Length)); // 逐字符的等长映射，目标正好够大
            }
        });
        return new(tree, names, offsets);
    }

    /// 调用方持有 tree.Sync。语法错误放进 Error，不抛异常；ScopeId 不存在或已删除抛 ArgumentException（§2.3）。
    public SearchResultDto Query(SearchRequest req, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();
        string query = req.Query ?? "";
        // scope = -1：不用沿父链查；first：子孙的下标一定比祖先大，之前的节点不用看。
        // 根（Id 0）就是扫描目标本身，搜出来只会占掉第一名，所以从 1 起。
        int scope = -1, first = 1;
        if (req.ScopeId is int id)
        {
            TreeQueries.RequireNode(Tree, id);
            first = id + 1;
            if (id != FileTree.RootId) scope = id; // 根的子孙就是除根以外的全部节点，省掉每个节点一趟父链
        }

        string? error = TryParse(query, out var groups, out bool blank);
        if (error != null || blank) return Finish(0, [], error, false);
        foreach (var t in groups.SelectMany(g => g))
            if (t.Kind == Kind.Ext) t.ExtOk = ExtTable(t.Extensions);

        int limit = Math.Clamp(req.Limit, 0, MaxLimit);
        var pinyin = req.Smart ? LazyInitializer.EnsureInitialized(ref _pinyin, BuildPinyin) : null;
        var (matches, picked) = Run(new Plan(groups, scope, first, req.Metric, req.Sort == SearchSort.Relevance, pinyin, fuzzy: false), limit, ct);
        bool fuzzy = false;
        // 一条都没对上：把名字词放宽成近似匹配再找一遍。只有一组、有够长的名字词才试；近似结果一律按相似度排
        if (matches == 0 && req.Smart && groups is [var only] && only.Any(t => t is { Kind: Kind.Name, Negative: false, Text.Length: >= MinFuzzy }))
        {
            (matches, picked) = Run(new Plan(groups, scope, first, req.Metric, relevance: true, pinyin, fuzzy: true), limit, ct);
            fuzzy = matches > 0;
        }
        return Finish(matches, picked.Select(x => DtoFactory.FileItem(Tree, x.Id)).ToArray(), null, fuzzy);

        SearchResultDto Finish(long total, FileItemDto[] items, string? failure, bool near) =>
            new(query, total, (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds), total > items.Length, items, failure, near);
    }

    // ---------------- 执行 ----------------

    sealed class Plan
    {
        /// 引号外的 | 分出的几组；只有一组时，种子词已经从里面拿掉（种子命中就算这一条满足了）
        public readonly Term[][] Groups;
        public readonly string? Seed;
        public readonly int Scope, First;
        public readonly Metric Metric;
        public readonly bool Relevance, Fuzzy;
        public readonly Pinyin? Pinyin;

        public Plan(Term[][] groups, int scope, int first, Metric metric, bool relevance, Pinyin? pinyin, bool fuzzy)
        {
            (Groups, Scope, First, Metric, Relevance, Pinyin, Fuzzy) = (groups, scope, first, metric, relevance, pinyin, fuzzy);
            // 取最长的正向子串词当种子：越长越稀，要检查的候选越少。几组之间是「或」、近似匹配不是子串，这两种只能全扫
            if (groups.Length != 1 || fuzzy) return;
            var terms = groups[0];
            int at = -1;
            for (int i = 0; i < terms.Length; i++)
                if (terms[i] is { Kind: Kind.Name, Negative: false } t && (at < 0 || t.Text.Length > terms[at].Text.Length)) at = i;
            if (at < 0) return;
            Seed = terms[at].Text;
            Groups = [[.. terms[..at], .. terms[(at + 1)..]]];
        }
    }

    (long Matches, List<(int Id, long Value)> Picked) Run(Plan plan, int limit, CancellationToken ct)
    {
        var workers = new List<Worker>();
        int chunks = (_count + ChunkSize - 1) / ChunkSize;
        if (chunks == 1)
            workers.Add(Scan(0, new Worker(limit), plan, ct));
        else
            Parallel.For(0, chunks, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Workers },
                () => new Worker(limit), (chunk, _, w) => Scan(chunk, w, plan, ct), w => { lock (workers) workers.Add(w); });

        // 每个线程各留了自己的前 K 名，全局前 K 名一定都在里面
        long matches = 0;
        var picked = new List<(int Id, long Value)>();
        foreach (var w in workers)
        {
            matches += w.Matches;
            w.Best.AppendTo(picked);
        }
        picked.Sort((a, b) => a.Value != b.Value ? b.Value.CompareTo(a.Value) : a.Id.CompareTo(b.Id));
        if (picked.Count > limit) picked.RemoveRange(limit, picked.Count - limit);
        return (matches, picked);
    }

    /// 每个线程一份：前 K 名、命中数、路径缓冲。
    sealed class Worker(int limit)
    {
        public readonly TopK Best = new(limit);
        public long Matches;
        // 路径缓冲里存着「上一个父目录的前缀 + 当前名字」：兄弟节点只需要换掉名字。这就是 DESIGN 说的目录路径缓存，
        // 每线程一份，所以不用加锁，也不会随索引常驻内存。
        public char[] Buffer = new char[256];
        public int[] Chain = new int[32];
        public int CachedParent = -2, PrefixLength;

        public void Reserve(int length)
        {
            if (length > Buffer.Length) Array.Resize(ref Buffer, Math.Max(length, Buffer.Length * 2));
        }
    }

    Worker Scan(int chunk, Worker w, Plan p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int start = Math.Max(chunk * ChunkSize, p.First), end = Math.Min(_count, (chunk + 1) * ChunkSize);
        if (start >= end) return w;
        if (p.Seed == null)
        {
            for (int n = start; n < end; n++)
            {
                if ((n & 0x1FFF) == 0) ct.ThrowIfCancellationRequested();
                Consider(n, p, w);
            }
            return w;
        }

        int position = _offsets[start], stop = _offsets[end], node = start, hits = 0;
        while (position < stop)
        {
            int hit = _names.AsSpan(position, stop - position).IndexOf(p.Seed.AsSpan());
            if (hit < 0) break;
            if ((++hits & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            int found = position + hit;
            // 命中密集时下一处多半就在紧挨着的名字里，先看一眼，省掉二分
            if (found >= _offsets[node + 1])
            {
                int i = Array.BinarySearch(_offsets, node + 1, end - node - 1, found);
                node = i >= 0 ? i : ~i - 1;
            }
            Consider(node, p, w);
            position = _offsets[++node]; // 同一个名字里多次命中只算一次，直接跳到下一个名字
        }

        // 拼音首字母那份也扫一遍；名字本身就含种子的上面已经算过，不重复
        if (p.Pinyin is { } py && Latin(p.Seed))
        {
            int k = py.Lower(start), last = py.Lower(end);
            position = py.Offsets[k];
            stop = py.Offsets[last];
            while (position < stop)
            {
                int hit = py.Names.AsSpan(position, stop - position).IndexOf(p.Seed.AsSpan());
                if (hit < 0) break;
                if ((++hits & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                int found = position + hit;
                if (found >= py.Offsets[k + 1])
                {
                    int i = Array.BinarySearch(py.Offsets, k + 1, last - k - 1, found);
                    k = i >= 0 ? i : ~i - 1;
                }
                if (NameOf(py.Nodes[k]).IndexOf(p.Seed.AsSpan()) < 0) Consider(py.Nodes[k], p, w);
                position = py.Offsets[++k];
            }
        }
        return w;
    }

    void Consider(int n, Plan p, Worker w)
    {
        int group = Accept(n, p, w);
        if (group < 0) return;
        w.Matches++;
        long value = Tree.Value(n, p.Metric);
        w.Best.Offer(n, p.Relevance ? Rank(Score(n, p, group), value) : value);
    }

    /// 相关度在高位、占用在低位拼成一个排序键：先比相关度，同档再比占用。
    // ponytail: 占用超过 256 TB 的条目在同一档里算并列；真有这么大的单个条目再拆成两级比较
    static long Rank(int score, long value) => ((long)Math.Min(score, 0x7FFF) << 48) | Math.Min(value, (1L << 48) - 1);

    /// 第一个满足的组；都不满足返回 -1
    int Accept(int n, Plan p, Worker w)
    {
        var flags = Tree.Flags(n);
        if ((flags & (NodeFlags.Deleted | NodeFlags.Virtual)) != 0) return -1;
        if (p.Scope >= 0 && !IsBelow(n, p.Scope)) return -1;
        for (int g = 0; g < p.Groups.Length; g++)
            if (Matches(n, flags, p.Groups[g], p, w)) return g;
        return -1;
    }

    bool Matches(int n, NodeFlags flags, Term[] terms, Plan p, Worker w)
    {
        foreach (var t in terms)
        {
            bool hit;
            switch (t.Kind)
            {
                case Kind.Type: hit = ((flags & NodeFlags.Directory) != 0) == t.Dir; break;
                case Kind.Ext: hit = (flags & NodeFlags.Directory) == 0 && t.ExtOk![Tree.ExtId(n)]; break;
                case Kind.Size: long value = Tree.Value(n, p.Metric); hit = value >= t.Min && value <= t.Max; break;
                case Kind.Name: hit = Loose(p, t) ? Near(n, t.Text, p) > 0 : NameHas(n, t.Text, p); break;
                case Kind.Wildcard: hit = WildcardMatch(NameOf(n), t.Text); break;
                case Kind.Path: hit = PathOf(n, w).IndexOf(t.Text.AsSpan()) >= 0; break;
                default: hit = WildcardMatch(PathOf(n, w), t.Text); break;
            }
            if (hit == t.Negative) return false;
        }
        return true;
    }

    /// 近似匹配时放宽的词：正向、够长的名字词；排除词照旧精确
    static bool Loose(Plan p, Term t) => p.Fuzzy && !t.Negative && t.Text.Length >= MinFuzzy;

    bool NameHas(int n, string text, Plan p) =>
        NameOf(n).IndexOf(text.AsSpan()) >= 0 || p.Pinyin is { } py && Latin(text) && py.Of(n).IndexOf(text.AsSpan()) >= 0;

    static bool Latin(string text) => text.AsSpan().IndexOfAnyInRange('a', 'z') >= 0;

    // ---------------- 相关度 ----------------

    /// 这一组里每个正向名字词对得多准，加起来
    int Score(int n, Plan p, int group)
    {
        int score = group == 0 && p.Seed != null ? Exact(n, p.Seed, p) : 0;
        foreach (var t in p.Groups[group])
            if (t is { Kind: Kind.Name, Negative: false }) score += Loose(p, t) ? Near(n, t.Text, p) : Exact(n, t.Text, p);
        return score;
    }

    /// 名字整个就是这个词（或者去掉扩展名就是）1000 > 开头就是 700 > 在词首 500 > 在中间 300；只在拼音首字母里对上的打八折
    int Exact(int n, string term, Plan p)
    {
        int best = Tier(NameOf(n), term) * 100;
        if (best < 1000 && p.Pinyin is { } py && Latin(term)) best = Math.Max(best, Tier(py.Of(n), term) * 80);
        return best;
    }

    static int Tier(ReadOnlySpan<char> name, string term)
    {
        int at = name.IndexOf(term.AsSpan());
        if (at < 0) return 0;
        if (name.Length == term.Length || at == 0 && name.LastIndexOf('.') == term.Length) return 10;
        if (at == 0) return 7;
        while (at > 0)
        {
            // 前面是分隔符、或者从汉字切到字母（报告report）都算词首
            char before = name[at - 1];
            if (!char.IsLetterOrDigit(before) || (before > '\x7f') != (term[0] > '\x7f')) return 5;
            int next = name[(at + 1)..].IndexOf(term.AsSpan());
            at = next < 0 ? -1 : at + 1 + next;
        }
        return 3;
    }

    /// 近似匹配：词里的字符按顺序出现在名字（或拼音首字母）里，中间可以夹别的。挨得越紧、越靠前分越高，最多 300（低于任何精确匹配）；
    /// 跨度超过词长两倍不算。拼错一个字母的（dowmload）对不上，漏字母的（dwnld）对得上
    int Near(int n, string term, Plan p)
    {
        int best = Subsequence(NameOf(n), term);
        if (p.Pinyin is { } py && Latin(term)) best = Math.Max(best, Subsequence(py.Of(n), term) * 8 / 10);
        return best;
    }

    static int Subsequence(ReadOnlySpan<char> name, string term)
    {
        int span = int.MaxValue, from = 0;
        for (int s = name.IndexOf(term[0]); s >= 0;)
        {
            int i = s, k = 0;
            for (; i < name.Length && k < term.Length; i++)
                if (name[i] == term[k]) k++;
            if (k < term.Length) break; // 从这里起凑不齐，再往后起更凑不齐
            if (i - s < span) (span, from) = (i - s, s);
            if (span == term.Length) break;
            int next = name[(s + 1)..].IndexOf(term[0]);
            s = next < 0 ? -1 : s + 1 + next;
        }
        return span > term.Length * 2 ? 0 : Math.Max(1, 300 - (span - term.Length) * 20 - from);
    }

    // ---------------- 拼音首字母 ----------------

    /// 名字里带汉字的节点，汉字换成拼音首字母后的小写名字：「微信截图.png」→「wxjt.png」。只认 GB2312 一级汉字（按拼音排序的 3755 个常用字）；
    /// 二级汉字、繁体字原样留着，对不上字母。
    sealed class Pinyin(int[] nodes, char[] names, int[] offsets)
    {
        public readonly int[] Nodes = nodes;     // 升序
        public readonly char[] Names = names;    // '\0' 分隔
        public readonly int[] Offsets = offsets; // Names 里第 k 个的起点；Offsets[Nodes.Length] 是总长

        public ReadOnlySpan<char> Of(int node)
        {
            int k = Array.BinarySearch(Nodes, node);
            return k < 0 ? default : Names.AsSpan(Offsets[k], Offsets[k + 1] - Offsets[k] - 1);
        }

        /// 第一个节点号不小于 node 的下标
        public int Lower(int node)
        {
            int k = Array.BinarySearch(Nodes, node);
            return k >= 0 ? k : ~k;
        }
    }

    // ponytail: 单线程顺序建，两百万个名字几十毫秒；只在第一次智能搜索时付一次
    Pinyin BuildPinyin()
    {
        var table = Initials.Value;
        var nodes = new List<int>();
        var offsets = new List<int>();
        var names = new StringBuilder();
        for (int n = 0; n < _count; n++)
        {
            var name = NameOf(n);
            if (name.IndexOfAnyInRange('一', '鿿') < 0) continue;
            nodes.Add(n);
            offsets.Add(names.Length);
            foreach (char c in name) names.Append(c is >= '一' and <= '鿿' && table[c - 0x4E00] != 0 ? table[c - 0x4E00] : c);
            names.Append('\0');
        }
        offsets.Add(names.Length);
        var chars = new char[names.Length];
        names.CopyTo(0, chars, 0, chars.Length);
        return new([.. nodes], chars, [.. offsets]);
    }

    // GB2312 一级汉字按拼音排序：每个声母打头第一个字的编码，最后一个是一级汉字的结尾。i、u、v 没有字打头
    static readonly int[] Bounds = [0xB0A1, 0xB0C5, 0xB2C1, 0xB4EE, 0xB6EA, 0xB7A2, 0xB8C1, 0xB9FE, 0xBBF7, 0xBFA6, 0xC0AC, 0xC2E8,
        0xC4C3, 0xC5B6, 0xC5BE, 0xC6DA, 0xC8BB, 0xC8F6, 0xCBFA, 0xCDDA, 0xCEF4, 0xD1B9, 0xD4D1, 0xD7FA];
    const string Letters = "abcdefghjklmnopqrstwxyz";

    /// U+4E00..U+9FFF 每个字的拼音首字母；不是一级汉字的为 0
    static readonly Lazy<char[]> Initials = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gb = Encoding.GetEncoding(936);
        var table = new char[0x9FFF - 0x4E00 + 1];
        var one = new char[1];
        var bytes = new byte[4];
        for (int i = 0; i < table.Length; i++)
        {
            one[0] = (char)(0x4E00 + i);
            if (gb.GetBytes(one, 0, 1, bytes, 0) != 2) continue;
            int code = bytes[0] << 8 | bytes[1];
            if (code < Bounds[0] || code >= Bounds[^1]) continue;
            int k = Array.BinarySearch(Bounds, code);
            table[i] = Letters[k >= 0 ? k : ~k - 1];
        }
        return table;
    });

    /// scope 的严格子孙（DESIGN：「它的子孙」，不含自己；首版用 IsAncestorOrSelf，把作用域目录本人也搜出来了）。
    /// 父节点下标一定更小，掉到 scope 以下就不用再往上找了。
    bool IsBelow(int n, int scope)
    {
        for (int x = Tree.Parent(n); x >= scope; x = Tree.Parent(x))
            if (x == scope) return true;
        return false;
    }

    ReadOnlySpan<char> NameOf(int n) => _names.AsSpan(_offsets[n], _offsets[n + 1] - _offsets[n] - 1);

    bool[] ExtTable(string[] extensions)
    {
        var ok = new bool[Tree.ExtCount];
        for (int id = 0; id < ok.Length; id++) ok[id] = Array.IndexOf(extensions, Tree.ExtName(id)) >= 0;
        return ok;
    }

    // ---------------- 路径 ----------------
    // 路径必须和界面上显示的（FileTree.GetPath）逐字一致：虚拟分组不进路径，根带不带尾部反斜杠都要拼对。
    // 首版两样都拼错了——界面显示一套、搜的是另一套，搜得到才怪。

    // ponytail: 路径词的全表扫描每个节点都拷一遍完整路径，200 万节点单核约 150 ms；嫌慢就按目录缓存「前缀本身含不含这个词」，含就不用拷名字了。
    ReadOnlySpan<char> PathOf(int n, Worker w)
    {
        if (n == FileTree.RootId) return _rootPath;
        int parent = Tree.Parent(n);
        if (parent != w.CachedParent)
        {
            w.PrefixLength = WritePrefix(parent, w);
            w.CachedParent = parent;
        }
        var name = NameOf(n);
        w.Reserve(w.PrefixLength + name.Length);
        name.CopyTo(w.Buffer.AsSpan(w.PrefixLength));
        return w.Buffer.AsSpan(0, w.PrefixLength + name.Length);
    }

    /// 把 dir 的路径加上结尾的 '\' 写进缓冲，返回长度。
    int WritePrefix(int dir, Worker w)
    {
        int depth = 0, length = _rootPrefix.Length;
        for (int x = dir; x > FileTree.RootId; x = Tree.Parent(x))
        {
            if (Tree.Has(x, NodeFlags.Virtual)) continue;
            if (depth == w.Chain.Length) Array.Resize(ref w.Chain, depth * 2);
            w.Chain[depth++] = x;
            length += _offsets[x + 1] - _offsets[x]; // 名字加一个 '\'，正好是 '\0' 占的那一格
        }
        w.Reserve(length);
        var buffer = w.Buffer.AsSpan();
        _rootPrefix.CopyTo(buffer);
        int at = _rootPrefix.Length;
        for (int i = depth - 1; i >= 0; i--)
        {
            var name = NameOf(w.Chain[i]);
            name.CopyTo(buffer[at..]);
            at += name.Length;
            buffer[at++] = '\\';
        }
        return at;
    }

    // ---------------- 前 K 名 ----------------

    /// 定长堆：只留下目前最好的 K 条（值大的好，值相同 ID 小的好），根是其中最差的一条，新来的不如它就直接丢。
    sealed class TopK(int capacity)
    {
        readonly int[] _ids = new int[capacity];
        readonly long[] _values = new long[capacity];
        int _count;

        static bool Better(int idA, long valueA, int idB, long valueB) => valueA != valueB ? valueA > valueB : idA < idB;

        public void Offer(int id, long value)
        {
            if (_ids.Length == 0) return;
            int i;
            if (_count < _ids.Length)
            {
                i = _count++;
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (!Better(_ids[parent], _values[parent], id, value)) break;
                    (_ids[i], _values[i]) = (_ids[parent], _values[parent]);
                    i = parent;
                }
            }
            else
            {
                if (!Better(id, value, _ids[0], _values[0])) return;
                i = 0;
                while (true)
                {
                    int child = 2 * i + 1;
                    if (child >= _count) break;
                    if (child + 1 < _count && Better(_ids[child], _values[child], _ids[child + 1], _values[child + 1])) child++; // 取更差的那个孩子
                    if (!Better(id, value, _ids[child], _values[child])) break;
                    (_ids[i], _values[i]) = (_ids[child], _values[child]);
                    i = child;
                }
            }
            (_ids[i], _values[i]) = (id, value);
        }

        public void AppendTo(List<(int Id, long Value)> list)
        {
            for (int i = 0; i < _count; i++) list.Add((_ids[i], _values[i]));
        }
    }
}
