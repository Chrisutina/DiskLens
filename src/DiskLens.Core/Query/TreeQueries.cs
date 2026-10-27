namespace DiskLens.Core;

/// 视图裁剪、节点详情、大文件三个查询（DESIGN §10.1 / §9.3）。
public static class TreeQueries
{
    // §10.1：预算夹在这个范围；单个目录最多单独展示 1500 个子节点，其余并进「其余 N 项」
    const int MinViewNodes = 100, MaxViewNodes = 50_000, MaxKeptPerDir = 1500;
    const int ExtStatLimit = 15, TopChildLimit = 12;

    /// 调用方持有 tree.Sync。
    /// 不变量（自检 QueryChecks 逐条验过）：父在子前；同父的子节点按 v 降序（聚合节点也按值排进去）；
    /// 子之和 = 父；总数 ≤ 预算；有子节点却没展开的目录才带 Unexpanded。
    public static ViewDto BuildView(FileTree tree, ViewRequest req)
    {
        RequireNode(tree, req.NodeId);
        var metric = req.Metric;
        int budget = Math.Clamp(req.MaxNodes, MinViewNodes, MaxViewNodes);
        long total = tree.Value(req.NodeId, metric);
        long threshold = total / ((long)budget * 4);

        // 选取阶段只记整数，名字和 DTO 最后一次性生成；Unexpanded 也要等到那时才知道该不该标。
        var ids = new int[budget];
        var parents = new int[budget];
        var counts = new int[budget];
        var values = new long[budget];
        var expanded = new bool[budget];
        int n = 0;
        // 值最大的目录先展开；同值先入先出，免得结果随堆的心情变。
        var heap = new PriorityQueue<int, (long NegValue, int Index)>();
        int[] sortIds = [];
        long[] sortKeys = [];

        Append(req.NodeId, -1, total);
        while (n < budget && heap.TryDequeue(out int vi, out _))
        {
            int slots = budget - n;
            long dv = values[vi];
            var kids = tree.LiveChildren(ids[vi]);
            // Alloc：Children 本来就是按占用降序，直接用；Size 才需要重排（只排值得单独画的那部分）。
            // ponytail: 信任 DESIGN §3 对 Children 排序的承诺，树的排序出 bug 这里会跟着错；QueryChecks 用独立的父指针标准答案兜底。
            ReadOnlySpan<int> ordered = kids;
            int positive = -1;
            if (metric != Metric.Alloc)
            {
                int m = SortBySize(tree, kids, threshold, ref sortIds, ref sortKeys, out positive);
                ordered = sortIds.AsSpan(0, m);
            }

            int kept = 0, i = 0;
            long keptSum = 0;
            for (; i < ordered.Length && kept < MaxKeptPerDir; i++)
            {
                long v = tree.CountedValue(ordered[i], metric);
                if (v <= 0 || v < threshold) break;
                // 没留够聚合节点的位置就不收：总和对不上比少画一块严重得多
                long left = dv - keptSum - v;
                if (kept + 1 + (left > 0 ? 1 : 0) > slots) break;
                kept++;
                keptSum += v;
            }
            if (positive < 0) positive = FirstNonPositive(tree, kids, i);

            long rest = dv - keptSum;
            // 一块都放不下就别展开：孤零零一块「其余」比保持 Unexpanded 还没用，还白占预算。视图根例外，总得画点东西。
            if (kept == 0 && (vi != 0 || rest <= 0))
            {
                // ponytail: 只剩 1 个位置时放弃整个循环，放得下的只有独生子链；嫌亏就按目录是否独生子再挑一遍。
                if (slots <= 1) break;
                continue;
            }

            expanded[vi] = true;
            int merged = positive - kept;
            bool aggregatePending = rest > 0;
            for (int j = 0; j < kept; j++)
            {
                int child = ordered[j];
                long v = tree.CountedValue(child, metric);
                // 聚合节点跟着值排进兄弟里（并列时排在后面），前端那边的"降序"才名副其实
                if (aggregatePending && rest > v)
                {
                    AppendAggregate(vi, rest, merged);
                    aggregatePending = false;
                }
                Append(child, vi, v);
            }
            if (aggregatePending) AppendAggregate(vi, rest, merged);
        }

        var nodes = new ViewNodeDto[n];
        for (int k = 0; k < n; k++)
        {
            int id = ids[k];
            if (id < 0)
            {
                nodes[k] = new ViewNodeDto(-1, parents[k], counts[k] > 0 ? L.T($"其余 {counts[k]} 项", L.Plural(counts[k], "other item"), $"その他 {counts[k]} 項目") : L.T("目录自身", "The folder itself", "フォルダー自身"), values[k],
                    ViewFlags.Aggregate, FileCategories.Other, counts[k], nodes[parents[k]].U);
                continue;
            }
            int flags = (int)tree.Flags(id);
            if (counts[k] > 0 && !expanded[k]) flags |= ViewFlags.Unexpanded;
            nodes[k] = new ViewNodeDto(id, parents[k], tree.GetName(id), values[k], flags, tree.Category(id), counts[k], tree.Purpose(id));
        }
        return new ViewDto(req.NodeId, metric, total, tree.Version, nodes);

        void Append(int id, int parent, long value)
        {
            int index = n++;
            ids[index] = id;
            parents[index] = parent;
            values[index] = value;
            int count = tree.IsDir(id) ? tree.LiveChildren(id).Length : 0;
            counts[index] = count;
            if (count > 0) heap.Enqueue(index, (-value, index));
        }

        void AppendAggregate(int parent, long value, int merged)
        {
            int index = n++;
            ids[index] = -1;
            parents[index] = parent;
            values[index] = value;
            counts[index] = merged;
        }
    }

    /// 非 Alloc 度量下的子节点顺序：只收值得单独画的（>0 且不小于阈值），按值降序、并列按 id 升序。
    /// 排好的 id 放在 ids 前 m 个里（返回 m）；positive 是值大于 0 的子节点总数，聚合节点的「其余 N 项」要用。
    static int SortBySize(FileTree tree, ReadOnlySpan<int> kids, long threshold,
        ref int[] ids, ref long[] keys, out int positive)
    {
        int m = 0;
        positive = 0;
        foreach (int c in kids)
        {
            long v = tree.CountedValue(c, Metric.Size);
            if (v <= 0) continue;
            positive++;
            if (v < threshold) continue;
            if (m == ids.Length)
            {
                Array.Resize(ref ids, Math.Max(256, m * 2));
                Array.Resize(ref keys, ids.Length);
            }
            ids[m] = c;
            keys[m++] = -v; // 升序排就是值降序
        }
        Array.Sort(keys, ids, 0, m);
        for (int s = 0; s < m;)
        {
            int e = s + 1;
            while (e < m && keys[e] == keys[s]) e++;
            if (e - s > 1) Array.Sort(ids, s, e - s);
            s = e;
        }
        return m;
    }

    /// 按占用降序的子节点里，第一个「计入值 ≤ 0」的位置（二分；from 之前保证都是正的）。
    static int FirstNonPositive(FileTree tree, ReadOnlySpan<int> sorted, int from)
    {
        int lo = from, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (tree.CountedValue(sorted[mid], Metric.Alloc) > 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// 调用方不要持有 tree.Sync；内部分两段加锁，磁盘在锁外读（慢盘、网络盘不该把整棵树卡住）。
    public static NodeDetailsDto GetDetails(FileTree tree, DetailsRequest req)
    {
        string path;
        bool onDisk;
        lock (tree.Sync)
        {
            RequireNode(tree, req.NodeId);
            path = tree.GetPath(req.NodeId);
            // 虚拟节点和 NTFS 元数据在磁盘上没有正经对应物，拿假路径去问文件系统只会收获一条莫名其妙的警告
            onDisk = !tree.Has(req.NodeId, NodeFlags.Virtual | NodeFlags.Metafile);
        }
        var live = onDisk ? ReadLiveBounded(path) : default;

        lock (tree.Sync)
        {
            int n = req.NodeId;
            RequireNode(tree, n); // 两段之间可能被删了
            var metric = req.Metric;
            int parent = tree.Parent(n);

            var ancestors = new AncestorDto[tree.Depth(n)];
            for (int p = parent, k = ancestors.Length - 1; p != FileTree.NoNode; p = tree.Parent(p), k--)
                ancestors[k] = new(p, tree.GetName(p));

            var comp = Compose(tree, n, metric);
            var protection = Protection.Evaluate(tree, n);
            var group = tree.HardLinkGroup(n);
            int counted = group == null ? FileTree.NoNode : tree.CountedLink(n);
            string? countedAt = counted != FileTree.NoNode ? tree.GetPath(counted) : null;
            string warning = string.Join(L.T("；", "; ", "；"), new[] { protection.Warning, live.Warning }.Where(s => !string.IsNullOrEmpty(s)));

            long value = tree.Value(n, metric);
            var top = new TopK(TopChildLimit);
            var liveKids = tree.LiveChildren(n);
            foreach (int c in liveKids) top.Add(c, tree.CountedValue(c, metric));

            return new(n, tree.GetName(n), path, tree.IsDir(n), tree.Category(n), (int)tree.Flags(n),
                AttributeLabels(tree, n), tree.Size(n), tree.Alloc(n), tree.Files(n), tree.Dirs(n),
                live.Modified ?? DtoFactory.UnixMs(tree.MTime(n)), live.Created, live.Accessed,
                Percent(value, parent < 0 ? value : tree.Value(parent, metric)),
                Percent(value, tree.Value(FileTree.RootId, metric)),
                ancestors, top.ToSortedArray().Select(i => DtoFactory.ChildItem(tree, i)).ToArray(), liveKids.Length,
                comp.Exts, comp.Categories, protection.Blocked, protection.Reason, warning.Length > 0 ? warning : null,
                live.LinkTarget, group == null ? 1 : Math.Max(1, LiveLinkCount(tree, group)), countedAt, tree.Version,
                tree.Purpose(n), comp.Purposes, PurposeClassifier.Explain(tree, n), tree.Role(n), tree.PurposeEvidence(n),
                DtoFactory.Owner(tree, n));
        }
    }

    internal readonly record struct LiveInfo(long? Modified, long? Created, long? Accessed, string? LinkTarget, string? Warning);

    /// 读磁盘只是锦上添花：掉线的网络盘、休眠的硬盘能让一次读取卡几十秒，不设上限的话详情面板就得陪着等。
    internal static LiveInfo ReadLiveBounded(string path, int timeoutMs = 2000, Func<string, LiveInfo>? read = null)
    {
        read ??= ReadLive;
        var task = Task.Run(() =>
        {
            try { return read(path); }
            catch (Exception ex) { return new LiveInfo(null, null, null, null, L.T("无法实时读取文件属性：", "Couldn't read live file attributes: ", "ファイルの属性をリアルタイムで読み取れません：") + ex.Message); }
        });
        // ponytail: 超时只是不再等，卡住的读取线程要等系统放它走；真要掐断得换可取消的 I/O。
        return task.Wait(timeoutMs) ? task.Result
            : new LiveInfo(null, null, null, null, L.T("读取磁盘超时（网络盘掉线或硬盘休眠了？），下面显示的是扫描时的数据", "Reading the disk timed out (network drive offline or disk asleep?); showing data from the scan below", "ディスクの読み取りがタイムアウトしました（ネットワーク ドライブの切断やディスクのスリープ？）。以下はスキャン時のデータです"));
    }

    static LiveInfo ReadLive(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            FileSystemInfo fs = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
            long? modified = UnixMs(fs.LastWriteTimeUtc), created = UnixMs(fs.CreationTimeUtc), accessed = UnixMs(fs.LastAccessTimeUtc);
            string? target = null;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // 读不出目标不该连累已经拿到的时间
                try { target = fs.LinkTarget; }
                catch (IOException) { }
            }
            return new(modified, created, accessed, target, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return new(null, null, null, null, L.T("磁盘上已经找不到它了（扫描之后被删除或移动过），下面显示的是扫描时的数据", "It's no longer on disk (deleted or moved after the scan); showing data from the scan below", "ディスク上に見つかりません（スキャン後に削除または移動されました）。以下はスキャン時のデータです")); }
        catch (UnauthorizedAccessException)
        { return new(null, null, null, null, L.T("没有权限读取它的实时属性，下面显示的是扫描时的数据", "No permission to read its live attributes; showing data from the scan below", "リアルタイムの属性を読み取る権限がありません。以下はスキャン時のデータです")); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        { return new(null, null, null, null, L.T("无法实时读取文件属性：", "Couldn't read live file attributes: ", "ファイルの属性をリアルタイムで読み取れません：") + ex.Message); }

        static long? UnixMs(DateTime utc)
        {
            long ms = new DateTimeOffset(utc).ToUnixTimeMilliseconds();
            return ms > 0 ? ms : null; // 1601 年的"没有"不算时间
        }
    }

    /// 调用方持有 tree.Sync。排除目录、虚拟节点、硬链接副本；并列按 id 升序，所以结果是确定的。
    public static FileItemDto[] LargestFiles(FileTree tree, LargestRequest req)
    {
        RequireNode(tree, req.NodeId);
        var best = new TopK(Math.Clamp(req.Limit, 1, 1000));
        var walker = new LeafWalker(tree, req.NodeId);
        while (walker.MoveNext(out int n))
        {
            if (tree.Has(n, NodeFlags.Virtual | NodeFlags.HardLinkDup)) continue;
            best.Add(n, tree.Value(n, req.Metric));
        }
        return best.ToSortedArray().Select(n => DtoFactory.FileItem(tree, n)).ToArray();
    }

    /// 目录列表（dise §5.2），口径见 ChildrenRequest / ChildrenDto。调用方持有 tree.Sync。
    /// LiveChildren 是树里共用的数组切片，先复制再排序。
    public static ChildrenDto Children(FileTree tree, ChildrenRequest req)
    {
        RequireNode(tree, req.NodeId);
        int[] kids = tree.LiveChildren(req.NodeId).ToArray();
        var metric = req.Metric;
        Comparison<int> order = req.Sort switch
        {
            ChildSort.Name => (a, b) => Tie(tree.IsDir(b).CompareTo(tree.IsDir(a)), StringComparer.OrdinalIgnoreCase.Compare(tree.GetName(a), tree.GetName(b)), a, b),
            ChildSort.Modified => (a, b) => Tie(tree.MTime(b).CompareTo(tree.MTime(a)), 0, a, b),
            _ => (a, b) => Tie(tree.CountedValue(b, metric).CompareTo(tree.CountedValue(a, metric)), 0, a, b),
        };
        Array.Sort(kids, order);
        int offset = Math.Clamp(req.Offset, 0, kids.Length), limit = Math.Clamp(req.Limit, 1, 500);
        var items = new ChildEntryDto[Math.Min(limit, kids.Length - offset)];
        for (int i = 0; i < items.Length; i++)
        {
            int c = kids[offset + i];
            items[i] = new(c, tree.GetName(c), tree.IsDir(c), tree.CountedValue(c, metric), tree.Size(c), tree.Alloc(c),
                DtoFactory.UnixMs(tree.MTime(c)), tree.Category(c), (int)tree.Flags(c), tree.Purpose(c),
                tree.IsDir(c) ? tree.LiveChildren(c).Length : 0);
        }
        return new(req.NodeId, metric, req.Sort, kids.Length, offset, items);

        static int Tie(int first, int second, int a, int b) => first != 0 ? first : second != 0 ? second : a.CompareTo(b);
    }

    const long TicksPerMs = 10_000, DayMs = 86_400_000;

    /// 最近文件（dise §6），口径见 RecentRequest / RecentFilesDto。调用方持有 tree.Sync。
    /// asOfMs 必须是扫描完成时刻：拿查询当下当基准的话，同一份结果隔一会儿再查，窗口就悄悄挪了。
    public static RecentFilesDto RecentFiles(FileTree tree, RecentRequest req, long asOfMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(asOfMs); // 0 多半是忘了记扫描完成时刻，宁可报错也别静悄悄返回空表
        int root = req.ScopeId ?? FileTree.RootId;
        RequireNode(tree, root);
        int days = Math.Clamp(req.Days, 1, 3650);
        // 窗口按毫秒比（和 ModifiedMs 同精度），换成 FILETIME 刻度：上界放宽到这一毫秒的最后一刻
        long from = (asOfMs - days * DayMs) * TicksPerMs + DtoFactory.UnixEpochFileTime;
        long to = (asOfMs + 1) * TicksPerMs - 1 + DtoFactory.UnixEpochFileTime;
        bool bySize = req.Sort == RecentSort.Size;
        var best = new TopK(Math.Clamp(req.Limit, 1, 1000));
        long total = 0;
        var walker = new LeafWalker(tree, root);
        while (walker.MoveNext(out int n))
        {
            long mtime = tree.MTime(n);
            if (mtime < from || mtime > to || tree.Has(n, NodeFlags.Virtual | NodeFlags.HardLinkDup)) continue;
            total++;
            best.Add(n, bySize ? tree.Value(n, req.Metric) : mtime);
        }
        var items = best.ToSortedArray().Select(n => new RecentFileDto(n, tree.GetName(n), tree.GetPath(n), tree.GetPath(tree.Parent(n)),
            tree.Size(n), tree.Alloc(n), (tree.MTime(n) - DtoFactory.UnixEpochFileTime) / TicksPerMs, tree.Category(n), (int)tree.Flags(n),
            tree.Purpose(n), tree.Role(n), DtoFactory.Owner(tree, n))).ToArray();
        return new RecentFilesDto(asOfMs, days, total, items);
    }

    /// 按所属对象聚合（dise §5.3），口径见 OwnerUsageRequest / OwnerUsageDto。调用方持有 tree.Sync。
    /// 顺着下标扫一趟作用域：父下标永远小于子下标，每个节点的桶（最近的根）直接抄父节点的，碰到根换成它自己。
    /// 字节记「自身占用」：节点的计入值记进自己的桶、再从父节点的桶里扣掉，目录自身的占用就这样留在它自己的桶里，不用逐个目录减子项。
    public static OwnerUsageDto OwnerUsage(FileTree tree, OwnerUsageRequest req)
    {
        int scope = req.ScopeId ?? FileTree.RootId;
        RequireNode(tree, scope);
        bool byAlloc = req.Metric == Metric.Alloc;
        var roots = tree.OwnerRoots;
        var owners = tree.Owners;

        // 桶号：0 = 不在范围里；1 = 不归任何对象；k + 2 = slots[k] 那处根
        var near = new int[tree.Count];
        var slots = new List<(int Node, int Owner)>();
        if (!roots.ContainsKey(scope)) // 作用域整个落在某个对象里：上方最近的那个根也算一处
            for (int x = tree.Parent(scope); x != FileTree.NoNode; x = tree.Parent(x))
                if (roots.TryGetValue(x, out var r))
                {
                    slots.Add((x, r.Owner));
                    break;
                }
        near[scope] = slots.Count + 1;
        foreach (var (node, r) in roots)
            if (!tree.IsDeleted(node) && InSubtree(tree, node, scope))
            {
                slots.Add((node, r.Owner));
                near[node] = slots.Count + 1;
            }

        int rc = Roles.Count;
        var size = new long[slots.Count + 2];
        var alloc = new long[slots.Count + 2];
        var roles = new long[(slots.Count + 2) * rc];
        var linked = new List<int>();
        for (int i = scope, count = tree.Count; i < count; i++)
        {
            var f = tree.Flags(i);
            int own = near[i], up = 0, p = tree.Parent(i);
            if (i != scope)
            {
                up = near[p];
                if (up == 0 || (f & NodeFlags.Deleted) != 0) continue;
                if (own == 0) near[i] = own = up;
            }
            if ((f & NodeFlags.HardLinkDup) != 0) continue; // 副本：父目录的计入值里本来就没有它
            long s = tree.Size(i), a = tree.Alloc(i), v = byAlloc ? a : s;
            if ((f & NodeFlags.Directory) == 0)
            {
                if ((f & NodeFlags.Virtual) != 0) own = 1; // 「未统计」虚拟叶子不归任何对象
                else if (tree.HardLinkGroup(i) != null) linked.Add(i);
            }
            size[own] += s;
            alloc[own] += a;
            roles[own * rc + tree.Role(i)] += v;
            if (i == scope) continue;
            size[up] -= s;
            alloc[up] -= a;
            roles[up * rc + tree.Role(p)] -= v;
        }

        // 硬链接组里活着的链接不全归同一个对象：这份字节挪去共享，不硬塞给计入那一份所在的对象
        Owner? OwnerAt(int n) => near[n] switch { 0 => tree.OwnerOf(n, out _), 1 => null, var b => owners[slots[b - 2].Owner] };
        long sharedSize = 0, sharedAlloc = 0;
        foreach (int n in linked)
        {
            var mine = OwnerAt(n);
            bool split = false;
            foreach (int l in tree.HardLinkGroup(n)!)
                if (l != n && !tree.IsDeleted(l) && !ReferenceEquals(OwnerAt(l), mine)) { split = true; break; }
            if (!split) continue;
            int b = near[n];
            long s = tree.Size(n), a = tree.Alloc(n);
            size[b] -= s;
            alloc[b] -= a;
            roles[b * rc + tree.Role(n)] -= byAlloc ? a : s;
            sharedSize += s;
            sharedAlloc += a;
        }

        var key = byAlloc ? alloc : size;
        var groups = Enumerable.Range(2, slots.Count).GroupBy(b => slots[b - 2].Owner)
            .Select(g => (Owner: owners[g.Key], Buckets: g.ToArray(), Value: g.Sum(b => key[b])))
            .OrderByDescending(g => g.Value).ThenBy(g => g.Owner.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Owner.Kind)
            .ToArray();
        var items = groups.Take(Math.Clamp(req.Limit, 1, 1000)).Select(g =>
        {
            var roleBytes = new long[rc];
            foreach (int b in g.Buckets)
                for (int r = 0; r < rc; r++) roleBytes[r] += roles[b * rc + r];
            var rootDtos = g.Buckets.OrderByDescending(b => key[b]).ThenBy(b => slots[b - 2].Node).Select(b =>
            {
                int node = slots[b - 2].Node;
                return new OwnerUsageRootDto(node, tree.GetPath(node), size[b], alloc[b], tree.OwnerEvidenceAt(node));
            }).ToArray();
            return new OwnerUsageItemDto(g.Owner.Kind, g.Owner.Name, g.Owner.Evidence, g.Buckets.Sum(b => size[b]), g.Buckets.Sum(b => alloc[b]),
                roleBytes, rootDtos);
        }).ToArray();
        return new OwnerUsageDto(scope, tree.CountedValue(scope, Metric.Size), tree.CountedValue(scope, Metric.Alloc),
            size.Skip(2).Sum(), alloc.Skip(2).Sum(), sharedSize, sharedAlloc, size[1], alloc[1], groups.Length, items);
    }

    const int ModulesPerRole = 3;
    static string NoRelation => L.T("尚未识别关联对象", "No related items identified yet", "関連項目はまだ識別されていません");

    /// 关联详情（dise §8.1），口径见 RelatedRequest / RelatedDto。调用方持有 tree.Sync。
    /// 所在那处根的地盘顺着下标扫一趟：父下标永远小于子下标，父节点在地盘里、自己又不是另一处根，就在地盘里；
    /// 嵌在里面的根（别的对象的，或同一对象的另一处）连同子树一起跳过。
    /// ponytail: 小地盘也从根往后扫到底（200 万节点 3～10 ms）；嫌慢就学 LeafWalker，子树小时改走显式栈。
    public static RelatedDto Related(FileTree tree, RelatedRequest req)
    {
        RequireNode(tree, req.NodeId);
        int node = req.NodeId;
        var owner = DtoFactory.Owner(tree, node);
        if (owner == null) return new RelatedDto(node, null, 0, [], NoRelation);
        int root = owner.RootId, me = tree.OwnerRoots[root].Owner;
        var m = req.Metric;
        var found = new List<(int Id, RelationKind Kind, Evidence Evidence)>();
        if (root != node) found.Add((root, RelationKind.Root, owner.Evidence));
        foreach (int x in tree.OwnerRoots.Where(r => r.Value.Owner == me && r.Key != root && !tree.IsDeleted(r.Key)).Select(r => r.Key)
                     .OrderByDescending(x => tree.Value(x, m)).ThenBy(x => x))
        {
            var e = tree.OwnerEvidenceAt(x);
            found.Add((x, RelationKind.OtherRoot, e < owner.Evidence ? e : owner.Evidence));
        }

        var best = new int[Roles.Count * ModulesPerRole]; // 每种角色最大的几个模块，-1 是空位
        Array.Fill(best, -1);
        int modules = 0;
        var mark = new byte[tree.Count]; // 1 = 在地盘里，2 = 嵌在里面的另一处根
        foreach (int x in tree.OwnerRoots.Keys)
            if (x > root) mark[x] = 2;
        mark[root] = 1;
        for (int i = root + 1, count = tree.Count; i < count; i++)
        {
            int p = tree.Parent(i);
            if (mark[i] == 2 || mark[p] != 1 || tree.IsDeleted(i)) continue;
            mark[i] = 1;
            byte role = tree.Role(i);
            if (role == Roles.Unknown || role == tree.Role(p) || !tree.IsDir(i) || tree.Has(i, NodeFlags.Virtual) || tree.IsAncestorOrSelf(i, node)) continue;
            modules++;
            // 插进这种角色的前几名：下标升序扫，值相同的先到先占，并列自然按 Id 升序
            long v = tree.Value(i, m);
            for (int k = role * ModulesPerRole, end = k + ModulesPerRole; k < end; k++)
            {
                if (best[k] >= 0 && tree.Value(best[k], m) >= v) continue;
                Array.Copy(best, k, best, k + 1, end - k - 1);
                best[k] = i;
                break;
            }
        }
        int total = found.Count + modules;
        foreach (int r in Enumerable.Range(0, Roles.Count).Where(r => best[r * ModulesPerRole] >= 0)
                     .OrderByDescending(r => tree.Value(best[r * ModulesPerRole], m)).ThenBy(r => r))
            for (int k = r * ModulesPerRole; k < (r + 1) * ModulesPerRole && best[k] >= 0; k++)
                found.Add((best[k], RelationKind.RoleModule, owner.Evidence));

        var items = found.Take(Math.Clamp(req.Limit, 1, 1000)).Select(x => new RelatedItemDto(x.Id, tree.GetName(x.Id), tree.GetPath(x.Id),
            tree.IsDir(x.Id), tree.Size(x.Id), tree.Alloc(x.Id), tree.Role(x.Id), x.Kind, x.Evidence)).ToArray();
        return new RelatedDto(node, owner, total, items, items.Length == 0 ? NoRelation : null);
    }

    /// n 在 root 的子树里（含 root 自己）。父下标永远小于子下标，往上走到比 root 小就可以停了。
    static bool InSubtree(FileTree tree, int n, int root)
    {
        while (n > root) n = tree.Parent(n);
        return n == root;
    }

    static readonly (NodeFlags Flag, string Zh, string En, string Ja)[] FlagLabels =
    [
        (NodeFlags.Hidden, "隐藏", "Hidden", "隠しファイル"), (NodeFlags.System, "系统", "System", "システム"),
        (NodeFlags.Compressed, "压缩", "Compressed", "圧縮"), (NodeFlags.Sparse, "稀疏", "Sparse", "スパース"),
        (NodeFlags.Reparse, "重解析点", "Reparse point", "再解析ポイント"), (NodeFlags.Cloud, "仅云端", "Cloud only", "クラウドのみ"),
        (NodeFlags.Encrypted, "加密", "Encrypted", "暗号化"), (NodeFlags.Inaccessible, "无法访问", "Inaccessible", "アクセス不可"),
        (NodeFlags.Metafile, "NTFS 元数据", "NTFS metadata", "NTFS メタデータ"), (NodeFlags.Virtual, "虚拟节点", "Virtual item", "仮想の項目"),
    ];

    public static string[] AttributeLabels(FileTree tree, int node)
    {
        RequireNode(tree, node);
        var labels = new List<string>();
        foreach (var (flag, zh, en, ja) in FlagLabels) if (tree.Has(node, flag)) labels.Add(L.T(zh, en, ja));
        // 同组的链接删得只剩自己，磁盘上就已经不是硬链接了，标签别留着骗人
        if (tree.HardLinkGroup(node) is { } group && LiveLinkCount(tree, group) > 1) labels.Add(L.T("硬链接", "Hard link", "ハードリンク"));
        return labels.ToArray();
    }

    static int LiveLinkCount(FileTree tree, int[] group)
    {
        int count = 0;
        foreach (int g in group) if (tree.IsValid(g)) count++;
        return count;
    }

    sealed record Composition(ExtStatDto[] Exts, long[] Categories, long[] Purposes);

    /// 一趟扫完子树：扩展名统计、类别、用途。口径和 FileTree.CategoryTotals / PurposeTotals 一致：
    /// 只算计入的叶子（含「未统计」虚拟叶子），目录自身占用不算 —— 根节点的详情才对得上汇总。
    /// 查询根本身是硬链接副本时照算它自己，不然副本的详情永远是空的。
    static Composition Compose(FileTree tree, int root, Metric metric)
    {
        var categories = new long[FileCategories.Count];
        var purposes = new long[Purposes.Count];
        // 按扩展名编号开数组而不是字典：两百万个叶子走字典太慢，更不能为每个叶子造字符串
        int extCount = tree.ExtCount;
        var extSize = new long[extCount];
        var extAlloc = new long[extCount];
        var extFiles = new long[extCount];

        var walker = new LeafWalker(tree, root);
        while (walker.MoveNext(out int n))
        {
            bool counted = n == root || !tree.Has(n, NodeFlags.HardLinkDup);
            if (counted)
            {
                long v = tree.Value(n, metric);
                categories[tree.Category(n)] += v;
                purposes[tree.Purpose(n)] += v;
            }
            if (tree.Has(n, NodeFlags.Virtual)) continue; // 虚拟叶子不是文件，不进扩展名统计
            int e = tree.ExtId(n);
            extFiles[e]++; // 条目数：硬链接的每个链接都算一个，和 Files(i) 同口径；字节数只算计入的
            if (!counted) continue;
            extSize[e] += tree.Size(n);
            extAlloc[e] += tree.Alloc(n);
        }

        var key = metric == Metric.Alloc ? extAlloc : extSize;
        var present = Enumerable.Range(0, extCount).Where(e => extFiles[e] > 0).ToArray();
        var top = present.OrderByDescending(e => key[e]).ThenBy(e => tree.ExtName(e), StringComparer.Ordinal)
            .Take(ExtStatLimit).ToArray();
        var stats = top.Select(e => new ExtStatDto(tree.ExtName(e), tree.ExtCategory(e), extSize[e], extAlloc[e], extFiles[e])).ToList();
        if (present.Length > top.Length)
        {
            // 排在 15 名之外的合成一条，面板上总数才对得上
            var rest = present.Except(top).ToArray();
            stats.Add(new ExtStatDto("*", FileCategories.Other, rest.Sum(e => extSize[e]), rest.Sum(e => extAlloc[e]), rest.Sum(e => extFiles[e])));
        }
        return new Composition(stats.ToArray(), categories, purposes);
    }

    static double Percent(long value, long total) => total > 0 ? Math.Clamp((double)value / total, 0, 1) : 0;

    internal static void RequireNode(FileTree tree, int node)
    { if (!tree.IsValid(node)) throw new ArgumentException(L.T("节点不存在或已删除", "The item doesn't exist or was deleted", "項目が存在しないか、削除されています")); }

    /// 子树里所有未删除的非目录节点；显式栈，目录嵌套几百层也不会炸调用栈。
    /// 子树占整棵树一成以上就改成顺着下标线性扫：父下标永远小于子下标，一趟就能推出谁在子树里；
    /// 两百万个节点的随机访问几乎全是缓存未命中，顺序扫要快一个数量级（根节点详情的 300 ms 靠它兜底）。
    struct LeafWalker
    {
        readonly FileTree _tree;
        readonly ulong[]? _inside; // 线性扫模式：下标位图，置位表示在子树里
        readonly int _root;
        int _next;
        int[] _stack;
        int _top;

        public LeafWalker(FileTree tree, int root)
        {
            _tree = tree;
            _root = root;
            _stack = [];
            if (((long)tree.Files(root) + tree.Dirs(root) + 1) * 16 >= tree.Count)
            {
                _inside = new ulong[(tree.Count + 63) >> 6];
                _next = root;
                return;
            }
            _stack = new int[64];
            _stack[0] = root;
            _top = 1;
        }

        public bool MoveNext(out int leaf)
        {
            if (_inside != null) return ScanNext(out leaf);
            while (_top > 0)
            {
                int n = _stack[--_top];
                if (!_tree.IsDir(n))
                {
                    leaf = n;
                    return true;
                }
                var kids = _tree.Children(n);
                if (_top + kids.Length > _stack.Length) Array.Resize(ref _stack, Math.Max(_stack.Length * 2, _top + kids.Length));
                foreach (int c in kids)
                    if (!_tree.IsDeleted(c)) _stack[_top++] = c;
            }
            leaf = FileTree.NoNode;
            return false;
        }

        bool ScanNext(out int leaf)
        {
            var inside = _inside!;
            for (int count = _tree.Count; _next < count;)
            {
                int i = _next++;
                if (i != _root)
                {
                    int p = _tree.Parent(i);
                    if ((inside[p >> 6] & (1UL << (p & 63))) == 0 || _tree.IsDeleted(i)) continue;
                }
                inside[i >> 6] |= 1UL << (i & 63);
                if (_tree.IsDir(i)) continue;
                leaf = i;
                return true;
            }
            leaf = FileTree.NoNode;
            return false;
        }
    }

    /// 固定容量的"前 K 名"：值大的更好，值相同 id 小的更好，所以结果是确定的。
    sealed class TopK(int capacity)
    {
        readonly PriorityQueue<int, (long Value, int NegId)> _heap = new(capacity + 1);
        long _worstValue;
        int _worstId;

        public void Add(int id, long value)
        {
            if (_heap.Count == capacity)
            {
                // 满员后绝大多数候选连堆都不用碰，两次比较就淘汰
                if (value < _worstValue || value == _worstValue && id > _worstId) return;
                _heap.Dequeue();
            }
            _heap.Enqueue(id, (value, -id));
            if (_heap.Count == capacity && _heap.TryPeek(out int worst, out var priority))
            {
                _worstValue = priority.Value;
                _worstId = worst;
            }
        }

        /// 最好的排前面。
        public int[] ToSortedArray()
        {
            var result = new int[_heap.Count];
            for (int k = result.Length - 1; k >= 0; k--) result[k] = _heap.Dequeue();
            return result;
        }
    }
}
