using System.ComponentModel;

namespace DiskLens.Core;

/// 永久删除：按树的节点后序遍历，先删文件后删目录，绝不跟随链接。
///
/// 防「路径中途被换成 junction」的办法：往下走之前，先对每一层目录开一个不跟随的守卫句柄（NativeFs.OpenGuard），
/// 句柄一直握到这个目录处理完。有守卫在手，这条路径上的目录既不能被改名也不能被替换，而且开的时候就核实过不是链接。
/// 叶子（文件、链接）用不跟随链接的句柄标记删除（NativeFs.Delete），有确认清单时在同一个句柄上先核对身份。
/// 每个目录只开一次守卫，整棵树的开销是 O(目录数 + 文件数)，不是 O(文件数 × 深度)。
sealed class PermanentWalk(FileTree tree, DeleteService.Run run, CancellationToken ct, DeletionSnapshot? snapshot = null) : IDisposable
{
    readonly NativeFs.GuardSet _guards = new();
    string? _baseDir;
    List<string>? _baseChain;

    sealed class Frame(int id, string path, DeleteService.Kid[] kids)
    {
        public readonly int Id = id;
        public readonly string Path = path;
        public readonly DeleteService.Kid[] Kids = kids;
        public int Next;
        public bool Clean = true;                // 子项是不是都删掉了；有一个没删掉，目录自己就别试了
        public readonly List<int> Done = [];     // 这一层已经删掉的子项（子树根）
    }

    public void Root(DeleteService.Root root)
    {
        ct.ThrowIfCancellationRequested();
        string parent = Path.GetDirectoryName(root.Path)!;
        if (!EnterBase(parent, root.Path)) return;
        if (!root.IsDir || root.IsLink) { Leaf(root.Id, root.Path, root.IsDir, run.Deleted); return; }
        Tree(root);
    }

    public void Dispose() => _guards.Dispose();

    // 同一批目标通常共用一个父目录（DeleteContents 全都是），父链守卫只开一次，换了父目录再重开
    bool EnterBase(string parent, string failPath)
    {
        if (_baseDir != null && Protection.Same(_baseDir, parent)) return true;
        if (_baseChain != null) _guards.ReleaseChain(_baseChain);
        _baseChain = null;
        _baseDir = null;
        try
        {
            _baseChain = _guards.AcquireChain(parent);
            _baseDir = parent;
            return true;
        }
        catch (Exception ex) when (NativeFs.IsEntryError(ex)) { run.Fail(failPath, NativeFs.Describe(ex)); return false; }
    }

    void Tree(DeleteService.Root root)
    {
        var frames = new Stack<Frame>();
        var top = new List<int>();
        try
        {
            if (!Enter(root.Id, root.Path, frames, top)) return;
            while (frames.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var f = frames.Peek();
                if (f.Next < f.Kids.Length)
                {
                    var kid = f.Kids[f.Next++];
                    string path = f.Path + "\\" + kid.Name;
                    bool ok = kid.IsDir && !kid.IsLink ? Enter(kid.Id, path, frames, f.Done) : Leaf(kid.Id, path, kid.IsDir, f.Done);
                    if (!ok) f.Clean = false;
                    continue;
                }

                frames.Pop();
                _guards.Release(f.Path); // 先放手，才删得掉这个目录自己（守卫的共享模式不含 DELETE）
                var sink = frames.Count > 0 ? frames.Peek().Done : top;
                if (f.Clean && Leaf(f.Id, f.Path, true, sink, notEmptyHint: true))
                    continue; // 目录删掉了，里面的子项被它覆盖，不必单独登记
                sink.AddRange(f.Done);
                if (frames.Count > 0) frames.Peek().Clean = false;
            }
        }
        finally
        {
            // 取消时栈里还压着没走完的目录：它们已经删掉的子项照样要登记，否则树里还留着磁盘上早已不存在的节点
            foreach (var f in frames) top.AddRange(f.Done);
            run.Deleted.AddRange(top);
        }
    }

    /// 进入一个真目录：开守卫、拍下子项、压栈。返回 false 表示这个目录没进成（已经记过错误）。
    bool Enter(int id, string path, Stack<Frame> frames, List<int> parentDone)
    {
        run.Current(path);
        try { _guards.Acquire(path); snapshot?.Require(id, path); }
        catch (Exception ex) when (NativeFs.IsEntryError(ex))
        {
            if (ex is Win32Exception w && NativeFs.IsGone(w.NativeErrorCode))
            {
                Gone(id); // 早就不在了（多半别的程序先删了）：目标已经达成，但不是这次删的
                return true;
            }
            run.Fail(path, NativeFs.Describe(ex));
            return false;
        }
        frames.Push(new Frame(id, path, DeleteService.Snapshot(tree, id)));
        return true;
    }

    /// 删一个叶子（文件、链接，或者子项都清空了的目录）。已经不存在也算成功（目标达成），但记进 Gone，不算这次释放的。
    /// 有确认清单时，身份核对和删除在同一个句柄上做：先核对再按路径删的话，中间被换成同名的新文件就会删错。
    bool Leaf(int id, string path, bool directory, List<int> done, bool notEmptyHint = false)
    {
        run.Current(path);
        int error;
        try { error = snapshot is null ? NativeFs.Delete(path, directory) : snapshot.DeleteVerified(id, path, directory); }
        catch (Exception ex) when (NativeFs.IsEntryError(ex))
        {
            run.Fail(path, NativeFs.Describe(ex));
            return false;
        }
        if (error == 0)
        {
            run.Removed++;
            done.Add(id);
            run.Report();
            return true;
        }
        if (NativeFs.IsGone(error))
        {
            Gone(id);
            return true;
        }
        run.Fail(path, notEmptyHint && error == NativeFs.ErrDirNotEmpty
            ? L.T("目录不为空：扫描之后里面出现了新文件，这个目录先保留", "Folder not empty: new files appeared after the scan, so the folder is kept", "フォルダーが空ではありません：スキャン後に新しいファイルができたため、このフォルダーは残します")
            : NativeFs.Describe(error));
        return false;
    }

    /// 动手时已经不在了：树里照样要标删除（Gone 先于目录本身标，目录的释放量就不含它），条目计进 GoneEntries
    void Gone(int id)
    {
        run.Gone.Add(id);
        run.GoneEntries++;
        run.Report();
    }
}
