using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;

namespace DiskLens.Core;

/// 调用方不持树锁；磁盘 IO 始终在锁外，只在读节点信息和改树时短暂加锁，且每次最多占二十来毫秒。
public static class DeleteService
{
    const int MaxSelection = 10_000, MaxErrors = 20;
    const long TenGB = 10L << 30;
    static readonly ConditionalWeakTable<FileTree, SemaphoreSlim> Gates = new();

    /// 同一棵树上的写操作（删除、对账、外部变化落树）共用一扇门，一次只进一个
    internal static SemaphoreSlim GateOf(FileTree tree) => Gates.GetValue(tree, _ => new(1, 1));

    /// 一个删除目标：计划阶段在锁内把要用的数字一次拍下来，后面的磁盘 IO 不再回头读树。
    internal sealed record Root(int Id, string Path, bool IsDir, bool IsLink, ProtectionResult Protection, long Entries,
        long Size, long Alloc, long Files, long CountedSize, long CountedAlloc);

    /// 一次删除的账本。Deleted 里只放最大的已删子树根：MarkDeleted 会连子孙一起标，逐个文件登记只会把树重排拖慢。
    internal sealed class Run(Action<DeleteProgressDto>? progress = null)
    {
        public readonly List<int> Deleted = [];
        public readonly List<int> Nuked = []; // 回收站模式下被系统直接删掉（没进回收站）的目标，空间是真的释放了
        /// 动手时已经不在磁盘上的（多半被别的程序先删了）：树里要标删除，但不是这次释放的，不算进 FreedBytes
        public readonly List<int> Gone = [];
        public readonly List<OpErrorDto> Errors = [];
        public int Failed, Removed, NukedEntries, GoneEntries;
        public bool Cancelled;
        public int Total, CompletedTargets, FailedTargets, SkippedTargets;
        public string? CurrentPath;
        long _lastReport;

        public void Report(bool force = false, long? freed = null)
        {
            if (progress == null || !force && Environment.TickCount64 - _lastReport < 125) return;
            _lastReport = Environment.TickCount64;
            try { progress(new(CurrentPath, Total, CompletedTargets, FailedTargets, SkippedTargets, (long)Removed + Failed, freed)); }
            catch (Exception ex) { Trace.TraceWarning("删除进度通知失败：{0}", ex.Message); }
        }

        public void Current(string path) { CurrentPath = path; Report(); }

        public void Fail(string path, string message)
        {
            Failed++;
            if (Errors.Count < MaxErrors) Errors.Add(new(path, message));
            Current(path);
        }
    }

    internal readonly record struct Kid(int Id, string Name, bool IsDir, bool IsLink);

    public static DeleteCheckDto Check(FileTree tree, IReadOnlyList<int> ids)
    {
        var roots = PlanRoots(tree, ids, MaxSelection);
        var items = roots.Select(r => new DeleteCheckItemDto(r.Id, r.Path, r.IsDir, r.Size, r.Alloc, r.Files,
            r.Protection.Blocked, r.Protection.Reason, r.Protection.Warning)).ToArray();
        long alloc = roots.Where(r => !r.Protection.Blocked).Sum(r => r.CountedAlloc);
        long size = roots.Where(r => !r.Protection.Blocked).Sum(r => r.CountedSize);
        long files = items.Where(i => !i.Blocked).Sum(i => i.Files);
        // 「单次删除」超过 10 GB / 10 万文件要警告，是指整批合计；前端只列带 Warning 的项，所以得挂到某一项上
        if ((alloc > TenGB || files > 100_000) && !items.Any(i => !i.Blocked && i.Warning?.Contains(Protection.BigDeletion) == true))
        {
            int biggestId = roots.Where(r => !r.Protection.Blocked).MaxBy(r => r.Alloc)!.Id;
            int at = Array.FindIndex(items, i => i.Id == biggestId);
            items[at] = items[at] with { Warning = items[at].Warning == null ? Protection.BigDeletion : items[at].Warning + L.T("；", "; ", "；") + Protection.BigDeletion };
        }
        return new(items, alloc, size, files, items.Any(i => i.Blocked), items.Any(i => i.Warning != null));
    }

    public static OpResultDto Delete(FileTree tree, IReadOnlyList<int> ids, bool permanent, CancellationToken ct) =>
        Execute(tree, ids, permanent, ct, MaxSelection);

    /// 永久删除目录里的内容，保留目录本身；被占用的跳过。
    public static OpResultDto DeleteContents(FileTree tree, int dirNode, CancellationToken ct)
    {
        return Execute(tree, ContentIds(tree, dirNode), true, ct, int.MaxValue);
    }

    internal static int[] ContentIds(FileTree tree, int dirNode)
    {
        lock (tree.Sync)
        {
            TreeQueries.RequireNode(tree, dirNode);
            if (!tree.IsDir(dirNode) || IsLink(tree, dirNode)) throw new ArgumentException(L.T("需要一个普通目录", "A regular folder is required", "通常のフォルダーが必要です"));
            var protection = dirNode == FileTree.RootId ? Protection.EvaluatePath(tree.GetPath(dirNode)) : Protection.Evaluate(tree, dirNode);
            if (protection.Blocked) throw new IOException(protection.Reason ?? L.T("受保护的路径", "Protected path", "保護されたパス"));
            return tree.LiveChildren(dirNode).ToArray().Where(c => !tree.Has(c, NodeFlags.Virtual | NodeFlags.Metafile)).ToArray();
        }
    }

    /// 清空 binNode 所在卷的回收站，然后让树和磁盘对账。
    internal static OpResultDto EmptyRecycleBin(FileTree tree, int binNode, string volumeRoot, CancellationToken ct)
    {
        string root;
        lock (tree.Sync) root = ResolveRecycleBin(tree, binNode, volumeRoot).Root;
        ct.ThrowIfCancellationRequested();
        bool emptied = ShellOps.EmptyRecycleBin(root);
        var (freed, removed) = ReconcileCore(tree, binNode);
        lock (tree.Sync) return new(true, freed, (int)Math.Min(removed, int.MaxValue), 0, [], emptied ? L.T("已清空当前卷的回收站", "Emptied the Recycle Bin on this volume", "このボリュームのごみ箱を空にしました") : L.T("当前卷的回收站本来就是空的", "The Recycle Bin on this volume was already empty", "このボリュームのごみ箱はもともと空でした"), tree.Version);
    }

    /// 校验这个节点确实是 volumeRoot 这个卷的 $Recycle.Bin；调用方持有 tree.Sync。纯判定，不碰磁盘，所以能直接测。
    internal static (string Root, string Bin, long Files) ResolveRecycleBin(FileTree tree, int binNode, string volumeRoot)
    {
        string root = Protection.Normalize(volumeRoot);
        TreeQueries.RequireNode(tree, binNode);
        string bin = Protection.Normalize(tree.GetPath(binNode));
        if (!tree.IsDir(binNode) || tree.Has(binNode, NodeFlags.Reparse | NodeFlags.Virtual) || !Protection.Same(bin, Path.Combine(root, "$Recycle.Bin")))
            throw new ArgumentException(L.T("清空目标不是当前卷的回收站", "The target isn't the Recycle Bin of this volume", "対象はこのボリュームのごみ箱ではありません"));
        return (root, bin, tree.Files(binNode));
    }

    /// 把磁盘上已经不存在的子孙节点（含 node 自己）标记删除，返回净释放的 alloc。
    public static long Reconcile(FileTree tree, int node) => ReconcileCore(tree, node).Freed;

    // ---------------- 执行 ----------------

    internal static OpResultDto ExecuteConfirmed(FileTree tree, IReadOnlyList<int> ids, bool permanent, CancellationToken ct, DeletionSnapshot snapshot,
        Action<DeleteProgressDto>? progress = null) => Execute(tree, ids, permanent, ct, int.MaxValue, snapshot, progress);

    static OpResultDto Execute(FileTree tree, IReadOnlyList<int> ids, bool permanent, CancellationToken ct, int limit, DeletionSnapshot? snapshot = null,
        Action<DeleteProgressDto>? progress = null)
    {
        var gate = GateOf(tree);
        gate.Wait(ct);
        try
        {
            var roots = PlanRoots(tree, ids, limit);
            var run = new Run(progress) { Total = roots.Count };
            foreach (var r in roots.Where(r => r.Protection.Blocked))
            {
                run.Fail(r.Path, r.Protection.Reason ?? L.T("受保护的路径", "Protected path", "保護されたパス"));
                run.SkippedTargets++;
            }
            var todo = roots.Where(r => !r.Protection.Blocked).ToList();
            run.Report(force: true);
            // 每做完一个目标就把它在树里标掉（最多 10 次/秒）：地图和列表跟着删除进度一块一块消失，不用等整批结束
            int appliedGone = 0, appliedDeleted = 0, appliedNuked = 0;
            long freed = 0, nukedFreed = 0, lastApply = 0;
            void Apply()
            {
                // 先标「已经不在的」：它们可能是后面要标的目录的子项，先扣掉，目录的释放量里就不含别人删的那部分
                if (run.Gone.Count > appliedGone) ApplyDeleted(tree, run.Gone.GetRange(appliedGone, run.Gone.Count - appliedGone));
                if (run.Deleted.Count > appliedDeleted) freed += ApplyDeleted(tree, run.Deleted.GetRange(appliedDeleted, run.Deleted.Count - appliedDeleted)).Freed;
                if (run.Nuked.Count > appliedNuked) nukedFreed += ApplyDeleted(tree, run.Nuked.GetRange(appliedNuked, run.Nuked.Count - appliedNuked)).Freed;
                (appliedGone, appliedDeleted, appliedNuked) = (run.Gone.Count, run.Deleted.Count, run.Nuked.Count);
                lastApply = Environment.TickCount64;
            }
            try
            {
                using var walk = permanent ? new PermanentWalk(tree, run, ct, snapshot) : null;
                foreach (var r in todo)
                {
                    ct.ThrowIfCancellationRequested();
                    int failures = run.Failed;
                    run.CurrentPath = r.Path;
                    run.Report(force: true);
                    ct.ThrowIfCancellationRequested();
                    if (walk != null) walk.Root(r);
                    else RecycleAll(run, [r], ct, snapshot);
                    if (run.Failed > failures) run.FailedTargets++;
                    else run.CompletedTargets++;
                    if (Environment.TickCount64 - lastApply >= 100) Apply();
                    run.Report(force: true); // 释放量照旧只在收尾报一次（对账前未知），树是边删边更新的
                }
            }
            catch (OperationCanceledException) { run.Cancelled = true; }

            Apply();
            int version;
            lock (tree.Sync) version = tree.Version;
            run.SkippedTargets = run.Total - run.CompletedTargets - run.FailedTargets;
            run.CurrentPath = null;
            run.Report(force: true, freed: permanent ? freed : nukedFreed);
            // 移到回收站不释放空间（DESIGN §7）；只有系统自作主张直接删掉的那几项才算
            return new(!run.Cancelled && run.Failed == 0, permanent ? freed : nukedFreed, run.Removed, run.Failed, run.Errors.ToArray(),
                Describe(run, permanent), version, run.GoneEntries);
        }
        finally { gate.Release(); }
    }

    static string Describe(Run run, bool permanent)
    {
        // 别的程序抢先删掉的：目标达成了，但不是这次释放的，单独说
        string gone = run.GoneEntries == 0 ? "" : L.T(
            $"；另有 {run.GoneEntries:N0} 项动手前已经不在（可能被别的程序先删了），没算进释放量",
            $"; {L.Plural(run.GoneEntries, "more item")} were already gone before deleting (maybe removed by another program) and aren't counted as freed",
            $"。ほかに {run.GoneEntries:N0} 項目は削除前になくなっていたため（別のプログラムが先に削除した可能性があります）、空き容量に含めていません");
        if (run.Cancelled) return L.T("操作已取消；已经完成的条目已更新", "Operation cancelled; finished items were updated", "操作をキャンセルしました。完了した項目は更新済みです") + gone;
        string skipped = run.Failed == 0 ? "" : L.T(
            $"；{run.Failed:N0} 项被占用、没有权限或受保护，已跳过",
            $"; skipped {L.Plural(run.Failed, "item")} that were in use, lacked permission or were protected",
            $"。{run.Failed:N0} 項目は使用中・権限なし・保護のいずれかのためスキップしました");
        if (permanent)
            return L.T($"已永久删除 {run.Removed:N0} 项{skipped}{gone}", $"Permanently deleted {L.Plural(run.Removed, "item")}{skipped}{gone}", $"{run.Removed:N0} 項目を完全に削除しました{skipped}{gone}");
        // 回收站里的空间要清空之后才释放（DESIGN §7），这句必须写明
        string tail = L.T("这些文件仍占用磁盘空间，清空回收站后才会释放", "These files still take disk space until you empty the Recycle Bin", "これらのファイルはごみ箱を空にするまでディスク容量を使い続けます");
        string nuked = run.Nuked.Count == 0 ? "" : L.T(
            $"；注意：其中 {run.Nuked.Count:N0} 个目标系统没有放进回收站而是直接删除了，无法恢复",
            $"; note: Windows deleted {L.Plural(run.Nuked.Count, "target")} directly instead of moving them to the Recycle Bin, and they can't be recovered",
            $"。注意：そのうち {run.Nuked.Count:N0} 個の対象は、Windows がごみ箱に入れずに直接削除したため元に戻せません");
        int moved = run.Removed - run.NukedEntries;
        return run.Removed == 0
            ? L.T($"没有条目被移到回收站{skipped}{gone}", $"Nothing was moved to the Recycle Bin{skipped}{gone}", $"ごみ箱に移動した項目はありません{skipped}{gone}")
            : L.T($"已移到回收站（{moved:N0} 项）{skipped}{nuked}{gone}；{tail}", $"Moved {L.Plural(moved, "item")} to the Recycle Bin{skipped}{nuked}{gone}; {tail}",
                $"ごみ箱に移動しました（{moved:N0} 項目）{skipped}{nuked}{gone}。{tail}");
    }

    /// 回收站：所有目标一次提交给 IFileOperation。批次被第一个错误中止时，剩下的逐个重试，错误才能落到具体条目上。
    static void RecycleAll(Run run, List<Root> roots, CancellationToken ct, DeletionSnapshot? snapshot = null)
    {
        if (roots.Count == 0) return;
        using var guards = new NativeFs.GuardSet();
        var live = new List<Root>();
        foreach (var r in roots)
        {
            ct.ThrowIfCancellationRequested();
            try { guards.AcquireChain(Path.GetDirectoryName(r.Path)!); } // 提交期间钉住父链，并确认没有穿过链接
            catch (Exception ex) when (NativeFs.IsEntryError(ex))
            {
                if (ex is System.ComponentModel.Win32Exception w && NativeFs.IsGone(w.NativeErrorCode)) AlreadyGone(run, r);
                else run.Fail(r.Path, NativeFs.Describe(ex));
                continue;
            }
            if (NativeFs.Probe(r.Path) == PathState.Missing) { AlreadyGone(run, r); continue; }
            // 进不了回收站的别交给外壳：它会不吭声地直接永久删除。让用户自己决定要不要改用永久删除
            if (RecycleBinPolicy.Refusal(r.Path, r.CountedAlloc) is { } refusal)
            {
                run.Fail(r.Path, L.T($"放不进回收站（{refusal}），没有删除；确实要删请改用永久删除", $"Can't go to the Recycle Bin ({refusal}), so it wasn't deleted; use permanent delete if you really want it gone", $"ごみ箱に入れられないため（{refusal}）、削除していません。どうしても削除する場合は完全に削除を使ってください"));
                continue;
            }
            // 确认过的清单逐个目标核对：父链已经钉住，这一项对不上就单独停掉，别的照常进回收站
            try { snapshot?.ValidateRoot(r.Id, recycling: true, ct); }
            catch (Exception ex) when (NativeFs.IsEntryError(ex)) { run.Fail(r.Path, NativeFs.Describe(ex)); continue; }
            live.Add(r);
        }
        if (live.Count == 0) return;

        var batch = ShellOps.MoveToRecycleBin(live.Select(r => r.Path).ToList());
        var batchInfo = Outcomes(batch);
        var pending = new List<Root>();
        foreach (var r in live)
            if (NativeFs.Probe(r.Path) == PathState.Missing) Settled(run, r, batchInfo);
            else pending.Add(r);
        foreach (var r in pending)
        {
            ct.ThrowIfCancellationRequested();
            string? error = batchInfo.GetValueOrDefault(r.Path)?.Error ?? batch.Error;
            if (live.Count > 1)
            {
                if (snapshot != null) { run.Fail(r.Path, L.T("回收站操作部分完成，请重新确认后重试", "The Recycle Bin operation only partly finished; confirm again and retry", "ごみ箱の操作が一部だけ完了しました。もう一度確認してから再試行してください")); continue; }
                var single = ShellOps.MoveToRecycleBin([r.Path]);
                var singleInfo = Outcomes(single);
                if (NativeFs.Probe(r.Path) == PathState.Missing) { Settled(run, r, singleInfo); continue; }
                error = singleInfo.GetValueOrDefault(r.Path)?.Error ?? single.Error ?? error;
            }
            run.Fail(r.Path, error ?? L.T("未能移到回收站，请检查占用、权限或回收站是否可用", "Couldn't move it to the Recycle Bin; check whether it's in use, the permissions, and whether the Recycle Bin is available", "ごみ箱に移動できませんでした。使用中でないか、権限、ごみ箱が使えるかを確認してください"));
        }
    }

    static Dictionary<string, RecycleItem> Outcomes(RecycleResult result) =>
        (result.Items ?? []).ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);

    /// 磁盘上已经没了：看外壳怎么说的——真进回收站了，还是被系统直接删了。
    static void Settled(Run run, Root r, Dictionary<string, RecycleItem> info)
    {
        if (info.TryGetValue(r.Path, out var item) && item.Done && !item.Recycled)
        {
            run.Removed += (int)Math.Min(r.Entries, int.MaxValue);
            run.NukedEntries += (int)Math.Min(r.Entries, int.MaxValue);
            run.Nuked.Add(r.Id);
        }
        else Recycled(run, r);
    }

    static void Recycled(Run run, Root r)
    {
        run.Removed += (int)Math.Min(r.Entries, int.MaxValue);
        run.Deleted.Add(r.Id);
    }

    /// 还没交给外壳就已经不在了：别的程序先删了。目标算达成，树里照样标删除，但不算这次的释放
    static void AlreadyGone(Run run, Root r)
    {
        run.GoneEntries += (int)Math.Min(r.Entries, int.MaxValue);
        run.Gone.Add(r.Id);
    }

    /// 选择规整 + 保护判定。节点 ID 无效直接抛；分片持锁，几万个目标也不会把界面查询堵住。
    static List<Root> PlanRoots(FileTree tree, IReadOnlyList<int> ids, int limit)
    {
        if (ids.Count > limit) throw new ArgumentException(L.T($"一次最多选择 {limit:N0} 个条目", $"You can select up to {limit:N0} items at once", $"一度に選べるのは {limit:N0} 項目までです"));
        int[] candidates;
        lock (tree.Sync)
        {
            var distinct = ids.Distinct().ToArray();
            foreach (int n in distinct) TreeQueries.RequireNode(tree, n);
            var selected = distinct.ToHashSet();
            candidates = distinct.Where(n => !Covered(tree, n, selected)).ToArray();
        }
        var roots = new List<Root>(candidates.Length);
        var clock = new Stopwatch();
        for (int i = 0; i < candidates.Length;)
            lock (tree.Sync)
            {
                clock.Restart();
                while (i < candidates.Length && clock.ElapsedMilliseconds < 20)
                {
                    int n = candidates[i++];
                    TreeQueries.RequireNode(tree, n); // 分片之间可能被别的操作删了
                    roots.Add(Plan(tree, n));
                }
            }
        return roots;
    }

    /// 选中了某个目录又选中它里面的东西：只认外层的。
    static bool Covered(FileTree tree, int node, HashSet<int> selected)
    {
        for (int p = tree.Parent(node); p >= 0; p = tree.Parent(p))
            if (selected.Contains(p)) return true;
        return false;
    }

    static Root Plan(FileTree tree, int n)
    {
        var protection = Protection.EvaluateTree(tree, n);
        string path = tree.GetPath(n);
        try { path = Protection.Normalize(path); }
        catch (ArgumentException) { protection = new(true, L.T("路径无效，拒绝删除", "Invalid path; not deleted", "パスが無効なため削除しません"), null); }
        bool dir = tree.IsDir(n);
        return new(n, path, dir, IsLink(tree, n), protection, dir ? 1 + tree.Files(n) + tree.Dirs(n) : 1,
            tree.Size(n), tree.Alloc(n), tree.Files(n), tree.CountedValue(n, Metric.Size), tree.CountedValue(n, Metric.Alloc));
    }

    /// 目录型重解析点没有子节点：junction、符号链接这类只当链接删，不进去。
    /// 有子节点的（云占位目录之类）说明扫描钻进去过，是真目录。
    internal static bool IsLink(FileTree tree, int node) =>
        tree.Has(node, NodeFlags.Reparse) && tree.IsDir(node) && tree.LiveChildren(node).Length == 0;

    /// 锁内拍下一个目录的直接子项。
    internal static Kid[] Snapshot(FileTree tree, int dir)
    {
        lock (tree.Sync)
        {
            if (!tree.IsValid(dir)) return [];
            var live = tree.LiveChildren(dir);
            var kids = new List<Kid>(live.Length);
            foreach (int c in live)
            {
                if (tree.Has(c, NodeFlags.Virtual | NodeFlags.Metafile)) continue; // 不是磁盘上的东西
                kids.Add(new(c, tree.GetName(c), tree.IsDir(c), IsLink(tree, c)));
            }
            return kids.ToArray();
        }
    }

    /// 把删掉的子树根在树里标掉。MarkDeleted 每次都要重排所有祖先的子节点数组，所以分片持锁，别一口气占着。
    internal static (long Freed, long Entries) ApplyDeleted(FileTree tree, List<int> ids)
    {
        long freed = 0, entries = 0;
        ids.Sort(); // 父节点的 ID 一定比子孙小：先标父，子孙随之作废
        var clock = new Stopwatch();
        for (int i = 0; i < ids.Count;)
            lock (tree.Sync)
            {
                clock.Restart();
                while (i < ids.Count && clock.ElapsedMilliseconds < 20)
                {
                    int n = ids[i++];
                    if (n == FileTree.RootId || !tree.IsValid(n)) continue;
                    entries += tree.IsDir(n) ? 1 + tree.Files(n) + tree.Dirs(n) : 1;
                    freed += tree.MarkDeleted(n);
                }
            }
        return (freed, entries);
    }

    // ---------------- 对账 ----------------

    static (long Freed, long Entries) ReconcileCore(FileTree tree, int node)
    {
        var gate = GateOf(tree);
        gate.Wait();
        try
        {
            string path;
            bool dir;
            lock (tree.Sync)
            {
                TreeQueries.RequireNode(tree, node);
                path = Protection.Normalize(tree.GetPath(node));
                dir = tree.IsDir(node) && !IsLink(tree, node);
            }
            var missing = new List<int>();
            if (!dir)
            {
                if (node != FileTree.RootId && NativeFs.Probe(path) == PathState.Missing) missing.Add(node);
            }
            else
            {
                // 每个目录只枚举一次磁盘，拿名字集合去比树里的子项，比逐个文件问一遍快得多，也不会被长路径绊倒
                var stack = new Stack<(int Id, string Path)>();
                stack.Push((node, path));
                while (stack.TryPop(out var frame))
                {
                    var names = ListNames(frame.Path, out bool gone);
                    var kids = Snapshot(tree, frame.Id);
                    if (gone)
                    {
                        if (frame.Id != FileTree.RootId) missing.Add(frame.Id);
                        else missing.AddRange(kids.Select(k => k.Id));
                        continue;
                    }
                    if (names == null) continue; // 读不了就是不知道，宁可不动
                    foreach (var kid in kids)
                        if (!names.Contains(kid.Name)) missing.Add(kid.Id);
                        else if (kid.IsDir && !kid.IsLink) stack.Push((kid.Id, frame.Path + "\\" + kid.Name));
                }
            }
            return ApplyDeleted(tree, missing);
        }
        finally { gate.Release(); }
    }

    /// 目录里现有的名字。目录不存在时 gone=true；读不了（权限、类型变了）返回 null。
    static HashSet<string>? ListNames(string path, out bool gone)
    {
        gone = false;
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, ReturnSpecialDirectories = false };
            foreach (string name in new FileSystemEnumerable<string>(NativeFs.Native(path), (ref FileSystemEntry e) => e.FileName.ToString(), options))
                names.Add(name);
            return names;
        }
        catch (DirectoryNotFoundException) { gone = true; return null; }
        catch (Exception ex) when (NativeFs.IsEntryError(ex)) { return null; }
    }
}
