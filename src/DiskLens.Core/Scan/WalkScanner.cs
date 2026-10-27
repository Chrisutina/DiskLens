using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace DiskLens.Core;

public static class WalkScanner
{
    /// 返回未 Seal 的树，硬链接已去重。枚举只读元数据，不会把云端文件下载下来。
    public static FileTree Scan(string rootPath, ScanCounters counters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        rootPath = ScanNative.NormalizePath(rootPath);
        if (!Directory.Exists(rootPath)) throw new DirectoryNotFoundException(L.T($"扫描目录不存在或无法访问：{rootPath}", $"The folder to scan doesn't exist or can't be accessed: {rootPath}", $"スキャンするフォルダーが存在しないか、アクセスできません：{rootPath}"));
        if (Volumes.IsElevated() && !Volumes.EnableBackupPrivilege()) counters.Warn(L.T("未能开启备份特权，部分目录可能无法访问", "Couldn't enable the backup privilege; some folders may be inaccessible", "バックアップ特権を有効にできなかったため、一部のフォルダーにアクセスできない場合があります"));
        counters.Phase = "walk";
        string fs = Volumes.GetFileSystem(rootPath), type = Volumes.GetDriveType(rootPath);
        // 硬链接去重只在本地 NTFS / ReFS 上做：别的文件系统没有可信的 FileId
        bool dedup = type != "network" && (fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
            || fs.Equals("ReFS", StringComparison.OrdinalIgnoreCase));
        int workers = WorkerCount(type, type == "network" ? null : Volumes.IsSsd(rootPath), Environment.ProcessorCount);
        var tree = new FileTree(rootPath);
        var links = dedup ? new HardLinks() : null;
        using (var job = new Job(tree, counters, links, dedup, ct)) job.Run(rootPath, workers);
        counters.Phase = "links";
        if (links != null) WalkLinks.Resolve(tree, links, counters, workers, ct);
        ct.ThrowIfCancellationRequested();
        WarnInaccessible(rootPath, counters);
        return tree;
    }

    /// SSD 寻道几乎免费，线程开满；机械盘线程一多磁头来回跑反而更慢；网络盘看延迟，8 个比较平衡。
    internal static int WorkerCount(string driveType, bool? ssd, int processors) =>
        driveType == "network" ? 8 : ssd switch { true => Math.Clamp(processors, 4, 24), false => 2, null => 8 };

    // 前端只弹 warnings[0]，所以每个目录一条的刷屏不行；汇总成一句人话放在最后。
    static void WarnInaccessible(string rootPath, ScanCounters counters)
    {
        long denied = Interlocked.Read(ref counters.InaccessibleDirs);
        if (denied == 0) return;
        bool root = Volumes.IsVolumeRoot(rootPath);
        string effect = root
            ? L.T("已计入「未统计」", "they were counted under \"Not counted\"", "「未集計」に含めました")
            : L.T("里面的内容没有统计", "their contents weren't counted", "中身は集計していません");
        counters.Warn(Volumes.IsElevated()
            ? L.T($"{denied} 个目录即使以管理员身份也无法访问，{effect}。", $"{L.Plural(denied, "folder")} couldn't be accessed even as administrator; {effect}.",
                $"{denied} 個のフォルダーは管理者でもアクセスできませんでした。{effect}。")
            : L.T($"{denied} 个目录无权访问，{effect}。以管理员身份运行可以看到它们。", $"{L.Plural(denied, "folder")} couldn't be accessed; {effect}. Run as administrator to see them.",
                $"{denied} 個のフォルダーにアクセスできませんでした。{effect}。管理者として実行すると確認できます。"));
    }

    /// 一次扫描的全部状态：目录工作队列 + N 个专用线程。
    /// 用专用线程而不是线程池任务：这里每个线程都在阻塞式 IO 里泡着，线程池的慢速扩容会让 24 个工人
    /// 排着队一个个出场。
    sealed class Job : IDisposable
    {
        readonly record struct Work(int Id, string Path);

        // 每个线程自己的一套缓冲区，目录之间复用
        sealed class Buffers
        {
            public readonly byte[] Read = new byte[128 * 1024];
            public readonly WalkBatch Batch = new();
            public readonly long[] Categories = new long[FileCategories.Count];
            public readonly List<Work> Children = [];
            public int[] Ids = new int[256];
        }

        // Warn 总共只留 50 条，错误类的最多占 10 条，别把别的警告都挤掉
        const int MaxErrorWarnings = 10;
        const uint ListDirectory = 0x1, Synchronize = 0x100000, BackupSemantics = 0x02000000;

        readonly FileTree _tree;
        readonly ScanCounters _counters;
        readonly HardLinks? _links;
        readonly bool _localNtfs;
        readonly CancellationToken _ct;
        readonly CancellationTokenSource _stop; // 外部取消 + 内部出了意外要叫停，共用一个
        readonly BlockingCollection<Work> _queue = new();
        int _pending, _baseLevel, _warned;
        Exception? _fatal;

        public Job(FileTree tree, ScanCounters counters, HardLinks? links, bool localNtfs, CancellationToken ct)
        {
            _tree = tree;
            _counters = counters;
            _links = links;
            _localNtfs = localNtfs;
            _ct = ct;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        public void Run(string rootPath, int workers)
        {
            _pending = 1; // 「未完成」计数：队列里的加正在处理的，归零就是遍历结束
            _queue.Add(new Work(FileTree.RootId, rootPath));
            var threads = new Thread[workers];
            for (int i = 0; i < workers; i++)
            {
                threads[i] = new Thread(Worker) { IsBackground = true, Name = "DiskLens.Walk-" + i };
                threads[i].Start();
            }
            foreach (var t in threads) t.Join();
            _ct.ThrowIfCancellationRequested();
            if (_fatal != null) ExceptionDispatchInfo.Capture(_fatal).Throw();
        }

        public void Dispose()
        {
            _queue.Dispose();
            _stop.Dispose();
        }

        void Worker()
        {
            ScanNative.SuppressErrorDialogs(); // 线程随扫描结束，不用还原
            var buffers = new Buffers();
            try
            {
                foreach (var work in _queue.GetConsumingEnumerable(_stop.Token))
                {
                    try { Process(work, buffers); }
                    finally { if (Interlocked.Decrement(ref _pending) == 0) _queue.CompleteAdding(); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // 走到这里是代码有 bug，不是磁盘的问题：让其它线程停手，整次扫描如实失败
                Interlocked.CompareExchange(ref _fatal, ex, null);
                _stop.Cancel();
            }
        }

        void Process(Work work, Buffers b)
        {
            _stop.Token.ThrowIfCancellationRequested();
            _counters.CurrentPath = work.Path;
            b.Batch.Clear();
            try
            {
                int level = Volatile.Read(ref _baseLevel);
                int error = ReadDirectory(work.Path, ref level, b);
                // 根目录第一个被读，而且读它的时候别的线程都在等活：它读通的信息类就是这个卷能用的最好一级，
                // 其余目录直接从这一级起步，免得 FAT 之类的盘每个目录都白试两次失败。
                if (work.Id == FileTree.RootId && error == 0) Volatile.Write(ref _baseLevel, level);
                FixAllocations(work.Path, b);
                Commit(work, error, b);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单个目录出岔子不能拖垮整次扫描：记一笔，接着扫别的
                Report(work.Path, L.T($"无法完整读取目录：{ex.Message}", $"Couldn't read the whole folder: {ex.Message}", $"フォルダー全体を読み取れませんでした：{ex.Message}"), 0);
            }
        }

        int ReadDirectory(string path, ref int level, Buffers b)
        {
            using var handle = ScanNative.CreateFileW(ScanNative.LongPath(path), ListDirectory | Synchronize, 7, IntPtr.Zero, 3,
                BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) return Marshal.GetLastWin32Error();
            return WalkReader.ReadEntries((int infoClass, byte[] buffer, out int error) =>
            {
                if (ScanNative.GetFileInformationByHandleEx(handle, infoClass, buffer, buffer.Length)) { error = 0; return true; }
                error = Marshal.GetLastWin32Error();
                return false;
            }, ref level, b.Read, b.Batch, _stop.Token);
        }

        void FixAllocations(string dir, Buffers b)
        {
            var entries = CollectionsMarshal.AsSpan(b.Batch.Entries);
            for (int i = 0; i < entries.Length; i++)
            {
                ref var e = ref entries[i];
                if (!WalkReader.NeedsRealAlloc(e.Flags, e.Tag, e.Size, e.Alloc, _localNtfs)) continue;
                string path = Path.Join(dir, b.Batch.Name(e));
                if (ScanNative.TryCompressedSize(path, out long real, out int error)) e.Alloc = real;
                // 被服务独占 / 没有读属性的权限，在活的系统卷上是常态：沿用目录项的值，不值得惊动用户
                else if (!WalkReader.IsAccessDenied(error) && error is not (32 or 1920))
                    Report(path, L.T("无法核实压缩或稀疏文件的实际占用，沿用目录项的值：", "Couldn't verify the actual size of a compressed or sparse file; using the directory entry's value: ", "圧縮ファイルまたはスパース ファイルの実際の使用容量を確認できないため、ディレクトリ エントリの値を使います：") + ScanNative.ErrorText(error), error);
            }
        }

        /// 把一个目录读到的条目作为一批加进树，再把子目录推进队列。父节点一定先于子节点 AddNode。
        void Commit(Work work, int error, Buffers b)
        {
            var entries = CollectionsMarshal.AsSpan(b.Batch.Entries);
            if (b.Ids.Length < entries.Length) b.Ids = new int[Math.Max(entries.Length, b.Ids.Length * 2)];
            Array.Clear(b.Categories);
            long files = 0, bytes = 0;
            lock (_tree.Sync) // 每个目录只拿一次锁；文件系统调用、路径拼接都挪到锁外
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    ref readonly var e = ref entries[i];
                    int id = _tree.AddNode(work.Id, b.Batch.Name(e), e.Flags, e.Size, e.Alloc, e.Modified);
                    b.Ids[i] = id;
                    if ((e.Flags & NodeFlags.Directory) != 0) continue;
                    files++;
                    bytes += e.Alloc;
                    b.Categories[_tree.Category(id)] += e.Alloc;
                }
                if (WalkReader.IsAccessDenied(error)) _tree.AddFlags(work.Id, NodeFlags.Inaccessible);
            }

            long dirs = 0;
            b.Children.Clear();
            for (int i = 0; i < entries.Length; i++)
            {
                ref readonly var e = ref entries[i];
                if ((e.Flags & NodeFlags.Directory) != 0)
                {
                    dirs++;
                    string path = Path.Join(work.Path, b.Batch.Name(e));
                    if (WalkReader.ShouldEnter(e.Flags, e.Tag)) b.Children.Add(new Work(b.Ids[i], path));
                    // 联接点要多问一次才分得清是不是卷挂载点；一个卷上就几十上百个，不在热路径上
                    else _counters.RecordLink(path, e.Tag, e.Tag == ScanNative.TagMountPoint ? ScanNative.IsVolumeMountPoint(path) : false);
                }
                else if (_links != null && e.FileId != 0) _links.Register(e.FileId, b.Ids[i]); // FAT 上 FileId 是 0，跳过
            }
            _counters.AddBatch(files, dirs, bytes, b.Categories);
            // 拒绝访问是常态（回收站、别人的用户目录），单独一类，不算错误，也不写 Warn
            if (WalkReader.IsAccessDenied(error))
                _counters.Record(ScanIssueKind.Denied, work.Path, L.T($"拒绝访问（错误 {error}）：里面有什么、有多大都不知道", $"Access denied (error {error}): what's inside and how big it is are unknown", $"アクセス拒否（エラー {error}）：中身もサイズも分かりません"));
            else if (error != 0) Report(work.Path, L.T("无法完整读取目录：", "Couldn't read the whole folder: ", "フォルダー全体を読み取れませんでした：") + ScanNative.ErrorText(error), error);

            if (b.Children.Count == 0) return;
            Interlocked.Add(ref _pending, b.Children.Count); // 必须在本目录的 -1 之前，计数才不会提前归零
            foreach (var child in b.Children) _queue.Add(child, _stop.Token);
        }

        void Report(string path, string reason, int error)
        {
            _counters.Record(ScanIssueKind.Error, path, reason);
            if (error is 2 or 3) return; // 扫描期间文件 / 目录被删掉是常态（临时文件），清单里有，别再弹警告吓用户
            if (Interlocked.Increment(ref _warned) <= MaxErrorWarnings) _counters.Warn(L.T($"{reason}，位置：{path}", $"{reason}, at: {path}", $"{reason}（場所：{path}）"));
        }
    }
}
