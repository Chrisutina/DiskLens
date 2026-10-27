using System.ComponentModel;

namespace DiskLens.Core;

/// 确认框拍下的删除清单（DESIGN §9.2）：只记路径和文件身份（卷号 + 文件 ID + 创建时间 + 类型），不持磁盘句柄。
/// 执行时逐项核对：身份变了（被换成别的文件）、回收站模式下目录里多出东西，就停掉这一项并写明原因，别的照做（dise §9.3）。
/// 确认时已经不在的条目记成「缺席」：执行时还不在就当已删除，冒出一个新的同名对象则拒绝——
/// 临时目录里文件来来去去，不能因此整单确认不了。
public sealed class DeletionSnapshot
{
    readonly FileTree _tree;
    readonly Dictionary<int, (string Path, NativeFs.Identity? Identity)> _entries = [];
    readonly Dictionary<int, string[]> _children = [];
    readonly Dictionary<int, List<int>> _byRoot = [];
    readonly int[] _roots;
    public DeleteCheckDto Check { get; }

    DeletionSnapshot(FileTree tree, int[] roots, DeleteCheckDto check) { _tree = tree; _roots = roots; Check = check; }

    public static DeletionSnapshot Capture(FileTree tree, IReadOnlyList<int> ids, bool contents, CancellationToken ct)
    {
        int[] roots = contents ? ids.SelectMany(id => DeleteService.ContentIds(tree, id)).Distinct().ToArray() : ids.ToArray();
        var check = DeleteService.Check(tree, roots);
        var snapshot = new DeletionSnapshot(tree, check.Items.Where(i => !i.Blocked).Select(i => i.Id).ToArray(), check);
        foreach (int root in snapshot._roots)
        {
            var members = snapshot._byRoot[root] = [];
            var pending = new Stack<int>();
            pending.Push(root);
            while (pending.TryPop(out int id))
            {
                ct.ThrowIfCancellationRequested();
                string path;
                int[] children;
                lock (tree.Sync)
                {
                    TreeQueries.RequireNode(tree, id);
                    path = tree.GetPath(id);
                    children = tree.IsDir(id) && !DeleteService.IsLink(tree, id) ? tree.LiveChildren(id).ToArray() : [];
                    if (tree.IsDir(id) && !DeleteService.IsLink(tree, id))
                        snapshot._children[id] = children.Select(tree.GetName).ToArray();
                }
                var identity = TryIdentity(path);
                snapshot._entries[id] = (path, identity);
                members.Add(id);
                if (identity is null) continue; // 已经不在了：子项更不用看
                foreach (int child in children) pending.Push(child);
            }
        }
        return snapshot;
    }

    /// 确认框里列出的可执行目标（被保护规则拦下的不在内）。
    public IReadOnlyList<int> Roots => _roots;

    public bool Matches(FileTree tree, IReadOnlyList<int> roots) => ReferenceEquals(tree, _tree) && _roots.ToHashSet().SetEquals(roots);

    /// 核对一个目标的整棵确认清单；不对就抛 IOException，调用方把这一项记成失败。
    /// 回收站是整个目录搬走：确认之后目录里多出来的东西也会被一起搬，所以内容变了就要重新确认。
    public void ValidateRoot(int root, bool recycling, CancellationToken ct)
    {
        if (!_byRoot.TryGetValue(root, out var members)) throw new IOException(L.T("该条目不在确认过的清单里，请重新确认", "This item isn't in the confirmed list; confirm again", "この項目は確認済みの一覧にありません。もう一度確認してください"));
        foreach (int id in members)
        {
            ct.ThrowIfCancellationRequested();
            var entry = _entries[id];
            Require(id, entry.Path);
            if (recycling && entry.Identity is not null && _children.TryGetValue(id, out var expected) && NativeFs.Probe(entry.Path) != PathState.Missing)
            {
                var actual = Directory.EnumerateFileSystemEntries(NativeFs.Native(entry.Path)).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!actual.SetEquals(expected)) throw new IOException(L.T("确认之后目录里的内容变了，没有删除；请重新扫描并确认：", "The folder's contents changed after confirming, so nothing was deleted; rescan and confirm: ", "確認後にフォルダーの内容が変わったため、削除していません。再スキャンして確認してください：") + entry.Path);
            }
        }
    }

    internal void Require(int id, string path)
    {
        var expected = Expected(id, path);
        var actual = TryIdentity(path);
        // 确认时在、现在没了：没东西可删，交给删除流程按「已不存在」处理
        if (actual is null) return;
        if (actual != expected) throw Replaced(path);
    }

    /// 永久删除最后那一下（PermanentWalk）：在删除用的同一个句柄上核对身份，核对完到删掉之间名字被换了也删不到新来的。
    /// 确认时缺席的条目冒出同名对象同样算替换；已经不在就返回「找不到」，交给删除流程按「已不存在」处理
    internal int DeleteVerified(int id, string path, bool directory)
    {
        var expected = Expected(id, path);
        return NativeFs.Delete(path, directory, actual => { if (actual != expected) throw Replaced(path); });
    }

    NativeFs.Identity? Expected(int id, string path) =>
        _entries.TryGetValue(id, out var entry) && Protection.Same(path, entry.Path) ? entry.Identity
        : throw new IOException(L.T("这一项不在确认过的清单里，没有删除；请重新确认：", "This item isn't in the confirmed list, so it wasn't deleted; confirm again: ", "この項目は確認済みの一覧にないため、削除していません。もう一度確認してください：") + path);

    static IOException Replaced(string path) => new(L.T("确认之后这个文件被替换了，没有删除；请重新扫描并确认：", "This file was replaced after confirming, so it wasn't deleted; rescan and confirm: ", "確認後にこのファイルが置き換えられたため、削除していません。再スキャンして確認してください：") + path);

    /// 永久删除在遍历时逐项核对（PermanentWalk）；回收站在提交前逐个目标核对（RecycleAll）。
    public OpResultDto Delete(bool permanent, CancellationToken ct, Action<DeleteProgressDto>? progress = null) =>
        DeleteService.ExecuteConfirmed(_tree, _roots, permanent, ct, this, progress);

    /// null：已经不在了（找不到、路径没了、别人已经发起删除）。清理脚本生成时也用它
    internal static NativeFs.Identity? TryIdentity(string path)
    {
        try { return NativeFs.ReadIdentity(path); }
        catch (Win32Exception ex) when (NativeFs.IsGone(ex.NativeErrorCode)) { return null; }
    }
}
