using System.ComponentModel;
using System.Diagnostics;

namespace DiskLens.Core;

/// <param name="summaries">成功扫描的摘要存到这里（dise §3.2）；null 就不存</param>
public sealed class ScanService(SummaryStore? summaries = null) : IDisposable
{
    // 虚拟节点的名字跟着界面语言；按名字找回它时三种语言都认（扫描之后换过语言也找得到）
    static readonly string[] UnaccountedNames = ["未统计（系统保留或无权访问）", "Not counted (reserved by the system or no access)", "未集計（システム予約またはアクセス不可）"];
    static string UnaccountedName => L.T(UnaccountedNames[0], UnaccountedNames[1], UnaccountedNames[2]);

    static bool IsUnaccounted(ReadOnlySpan<char> name)
    {
        foreach (var n in UnaccountedNames) if (name.SequenceEqual(n)) return true;
        return false;
    }

    readonly object _sync = new();
    RunState? _run;
    FileTree? _tree;
    ScanSummaryDto? _summary;
    ScanCounters? _report; // 当前结果那次扫描的计数器：异常清单从这里翻，和 _tree、_summary 一起换
    TreeWatcher? _watcher; // 盯着当前结果那棵树的扫描范围；新结果换上时才换掉（重扫被取消时旧树照样跟踪）
    int _nextId;
    bool _disposed;

    sealed class RunState(int id, string path, ScanMode mode, bool volumeRoot)
    {
        public readonly int Id = id;
        public readonly string Path = path;
        public readonly bool VolumeRoot = volumeRoot;
        public ScanMode Mode = mode;
        public ScanCounters Counters = new();
        public readonly CancellationTokenSource Cancel = new();
        public readonly Stopwatch Watch = Stopwatch.StartNew();
        // 进度一律在这把锁里发：阶段事件、定时采样、「取消中」不会乱序；终态在锁里落定，之后不会再冒出进度。先 Gate 后 _sync
        public readonly object Gate = new();
        public long Used;
        public bool Running = true;
        public volatile bool Cancelling;
    }

    public event Action<ScanProgressDto>? Progress;
    public event Action<ScanSummaryDto>? Completed;
    public event Action<ScanErrorDto>? Failed;
    /// 扫描之后磁盘上的变化（实时状态）：完成时先发一条初始状态，之后别的程序删了东西、新建了东西就发
    public event Action<TreeChangeDto>? TreeChanged;
    public FileTree? Tree { get { lock (_sync) return _tree; } }
    public ScanSummaryDto? Summary { get { lock (_sync) return _summary; } }
    public bool IsScanning { get { lock (_sync) return _run?.Running == true; } }

    /// 这棵树那次扫描的完成时刻（Unix 毫秒，即汇总里的 CompletedAtMs），最近文件按它开时间窗口。按树身份取：
    /// 拿到树之后又换过扫描就报错，不拿别的扫描的时刻充数
    public long CompletedAtMs(FileTree tree) => SummaryOf(tree).CompletedAtMs;

    /// 这棵树那次扫描的汇总；拿到树之后又换过扫描就报错（同上）
    public ScanSummaryDto SummaryOf(FileTree tree)
    {
        lock (_sync) return ReferenceEquals(_tree, tree) ? _summary! : throw new InvalidOperationException(L.T("扫描结果已更换，请重试", "The scan result was replaced; try again", "スキャン結果が置き換わりました。もう一度お試しください"));
    }

    public ScanStartedDto Start(ScanStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Mode)) throw new ArgumentException(L.T("扫描模式无效", "Invalid scan mode", "スキャン モードが無効です"));
        string path;
        // 前端传空路径或带非法字符的路径时，NormalizePath 抛的是英文异常，这里换成人话。
        try { path = ScanNative.NormalizePath(request.Path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException(L.T("扫描路径无效：", "Invalid scan path: ", "スキャンのパスが無効です：") + request.Path); }
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(L.T($"扫描目录不存在或无法访问：{path}", $"The folder to scan doesn't exist or can't be accessed: {path}", $"スキャンするフォルダーが存在しないか、アクセスできません：{path}"));
        bool canMft = MftScanner.CanScan(path, out string reason);
        if (request.Mode == ScanMode.Mft && !canMft) throw new InvalidOperationException(reason);
        var mode = request.Mode == ScanMode.Auto ? canMft ? ScanMode.Mft : ScanMode.Walk : request.Mode;
        bool volumeRoot = Volumes.IsVolumeRoot(path);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run?.Running == true) _run.Cancel.Cancel();
            var run = new RunState(++_nextId, path, mode, volumeRoot);
            _run = run;
            _ = Task.Run(() => Execute(run));
            return new ScanStartedDto(run.Id, mode);
        }
    }

    /// 接受了就马上补发一条 Cancelling=true 的进度，不等下一次采样；扫描线程全部退出之后才发终态的 scan.error（cancelled=true）。
    /// 返回 false：没有正在进行的扫描（比如刚好扫完了）。
    public bool Cancel()
    {
        RunState? run;
        lock (_sync)
        {
            run = _run;
            if (run?.Running != true) return false;
            run.Cancelling = true;
            run.Cancel.Cancel();
        }
        PublishProgress(run);
        return true;
    }

    async Task Execute(RunState run)
    {
        using var reporting = new CancellationTokenSource();
        Task pump = Task.CompletedTask;
        TreeWatcher? watcher = null;
        try
        {
            // 扫描开始前就盯上：扫描读过的目录里随后被删的、扫完到换上新结果之间被删的，都攒着等树换上去再落（TreeWatcher）。
            // 扫完才开始盯的话，这段时间别的程序删掉的东西会一直留在树里
            watcher = new TreeWatcher(run.Id, run.Path);
            CancellationToken ct = run.Cancel.Token;
            try { var (total, free) = Volumes.GetSpace(run.Path); run.Used = Math.Max(0, total - free); }
            catch (Win32Exception ex) { run.Counters.Warn(ex.Message); }
            Watch(run, run.Counters);
            PublishProgress(run); // 一开始就报一次：哪怕整次扫描比采样间隔还短，前端也知道它开始了
            pump = ReportProgress(run, reporting.Token);
            FileTree tree;
            try
            {
                tree = run.Mode == ScanMode.Mft ? MftScanner.Scan(run.Path, run.Counters, ct)
                    : WalkScanner.Scan(run.Path, run.Counters, ct);
            }
            catch (Exception ex) when (run.Mode == ScanMode.Mft && ex is not OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
                var counters = new ScanCounters();
                counters.Warn(L.T("MFT 读取失败，已改用常规扫描：", "Couldn't read the MFT, so a regular scan was used instead: ", "MFT を読み取れなかったため、通常のスキャンに切り替えました：") + ex.Message);
                lock (run.Gate) { run.Mode = ScanMode.Walk; run.Counters = counters; }
                Watch(run, counters);
                PublishProgress(run); // 扫描方式换了，前端的步骤表也得换：马上说
                tree = WalkScanner.Scan(run.Path, counters, ct);
            }
            ct.ThrowIfCancellationRequested();
            run.Counters.Phase = "finalize";
            long totalBytes = 0, freeBytes = 0;
            try { (totalBytes, freeBytes) = Volumes.GetSpace(run.Path); }
            catch (Win32Exception ex) { run.Counters.Warn(ex.Message); }
            long used = Math.Max(0, totalBytes - freeBytes);
            bool volumeRoot = run.VolumeRoot;
            long scannedAlloc = 0, scannedSize = 0;
            for (int i = 0; i < tree.Count; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                if (tree.Has(i, NodeFlags.HardLinkDup)) continue;
                scannedAlloc = checked(scannedAlloc + tree.Alloc(i));
                scannedSize = checked(scannedSize + tree.Size(i));
            }
            long unaccounted = volumeRoot ? Math.Max(0, used - scannedAlloc) : 0;
            // 读不到卷空间时 used=0，别拿它去「超过」任何东西，不然白白吓用户一跳。
            if (volumeRoot && used > 0 && scannedAlloc > used)
                run.Counters.Warn(L.T($"已统计占用超过卷已用 {scannedAlloc - used:N0} 字节；扫描期间文件或卷空间可能发生变化", $"The counted size exceeds the volume's used space by {scannedAlloc - used:N0} bytes; files or volume space may have changed during the scan", $"集計した使用容量がボリュームの使用量を {scannedAlloc - used:N0} バイト上回っています。スキャン中にファイルやボリュームの容量が変わった可能性があります"));
            if (volumeRoot)
                tree.AddNode(0, UnaccountedName, NodeFlags.Virtual, unaccounted, unaccounted, 0);
            tree.Seal();
            ct.ThrowIfCancellationRequested();
            // 分类器要用路径找节点，所以必须在 Seal 之后。它是别人写的新代码：炸了就退回默认着色，
            // 不能让一个着色问题把整次扫描的结果陪葬。取消不算炸，照常往外抛。
            long[] purposeBytes;
            lock (tree.Sync)
            {
                try { tree.SetClassification(PurposeClassifier.Classify(tree, ct)); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { run.Counters.Warn(L.T("用途分类失败，已按默认用途着色：", "Purpose classification failed, so the default purpose colors are used: ", "用途の分類に失敗したため、既定の用途で色分けしました：") + ex.Message); }
                purposeBytes = tree.PurposeTotals(Metric.Alloc);
            }
            ct.ThrowIfCancellationRequested();
            run.Watch.Stop();
            // TreeVersion 必须在 SetClassification 之后读：它会让版本号加一。
            var c = run.Counters;
            c.SortIssues();
            string fileSystem = Volumes.GetFileSystem(run.Path);
            var summary = new ScanSummaryDto(run.Id, run.Path, 0, run.Mode, fileSystem,
                volumeRoot, Volumes.IsElevated(), totalBytes, freeBytes, used, scannedAlloc, scannedSize,
                tree.Files(0), tree.Dirs(0), c.InaccessibleDirs, c.LinksSkipped,
                c.HardLinkDups, c.HardLinkSavedBytes, unaccounted, run.Watch.ElapsedMilliseconds,
                tree.CategoryTotals(Metric.Alloc), c.Warnings, tree.Version, purposeBytes,
                c.MountPointsSkipped, c.Errors, SplitGap(run.Path, run.Mode, fileSystem, unaccounted));
            lock (run.Gate)
                lock (_sync)
                {
                    if (_disposed || !ReferenceEquals(_run, run)) return;
                    summary = summary with { CompletedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                    _tree = tree;
                    _summary = summary;
                    _report = c;
                    run.Running = false;
                }
            run.Counters.Percent = 1;
            // 先落盘再发完成事件：界面收到 scan.done 时已读得到这一次的摘要。只有走到这里的成功扫描才存，Save 不抛
            summaries?.Save(summary, tree);
            Publish(Completed, summary);
            StartWatching(tree, watcher);
            watcher = null; // 交出去了，归 _watcher 管
        }
        catch (Exception ex)
        {
            // 走到这里扫描线程都已经退出了（Walk 等齐工人线程、MFT 等齐解析任务才抛），终态事件不会早于后台真正停下
            bool current;
            lock (run.Gate)
                lock (_sync)
                {
                    current = !_disposed && ReferenceEquals(_run, run);
                    run.Running = false;
                }
            if (current) Publish(Failed, new ScanErrorDto(run.Id,
                ex is OperationCanceledException ? L.T("扫描已取消", "Scan cancelled", "スキャンはキャンセルされました") : ex.Message, ex is OperationCanceledException));
        }
        finally
        {
            watcher?.Dispose(); // 取消、失败、被新扫描顶掉：这次的跟踪作废，旧结果的跟踪照旧
            reporting.Cancel();
            await pump;
            lock (_sync) run.Cancel.Dispose();
        }
    }

    /// 新结果换上之后，扫描前就开始的跟踪接上这棵树，旧的跟踪随之停掉；期间又换了扫描就作废
    void StartWatching(FileTree tree, TreeWatcher watcher)
    {
        TreeWatcher? old;
        lock (_sync)
        {
            if (_disposed || !ReferenceEquals(_tree, tree)) { old = watcher; watcher = null!; }
            else { old = _watcher; _watcher = watcher; }
        }
        old?.Dispose();
        if (watcher == null) return;
        // 先订阅再接树：接上之后计时器随时会把攒下的删除落进树、发 Changed，不能发给没人听的空处
        watcher.Changed += change => Publish(TreeChanged, change);
        watcher.Attach(tree);
        Publish(TreeChanged, watcher.Snapshot());
    }

    // 阶段一变就补发一条进度，不靠 120 ms 采样碰巧抓到（dise §4.2）
    void Watch(RunState run, ScanCounters counters) => counters.PhaseChanged = () => PublishProgress(run);

    async Task ReportProgress(RunState run, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(120));
        try { while (await timer.WaitForNextTickAsync(ct)) PublishProgress(run); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// 发一条进度：定时采样、阶段变化、取消被接受都走这里。终态落定之后（Running=false）什么都不发。
    void PublishProgress(RunState run)
    {
        lock (run.Gate)
        {
            lock (_sync) { if (_disposed || !ReferenceEquals(_run, run) || !run.Running) return; }
            var c = run.Counters;
            double percent = c.Percent;
            if (run.Mode == ScanMode.Walk && run.Used > 0 && run.VolumeRoot)
                percent = Math.Min(0.99, Math.Max(0, Interlocked.Read(ref c.Bytes) / (double)run.Used));
            // 扫文件夹时卷已用空间不是进度条的分母，按约定报 0，前端改用已统计量当总长
            Publish(Progress, new ScanProgressDto(run.Id, run.Mode, c.Phase,
                Interlocked.Read(ref c.Files), Interlocked.Read(ref c.Dirs), Interlocked.Read(ref c.Bytes),
                run.VolumeRoot ? run.Used : 0, percent < 0 ? null : percent, c.CurrentPath, run.Watch.ElapsedMilliseconds,
                c.SnapshotCategories(), run.Cancelling));
        }
    }

    /// 当前结果那次扫描的异常清单（dise §4.3），按需翻页。指定的 ScanId 对不上就报错：换过扫描，旧清单作废，不拿别的扫描充数。
    public ScanIssuesDto Issues(ScanIssuesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Kind)) throw new ArgumentException(L.T("异常种类无效", "Invalid problem type", "問題の種類が無効です"));
        ScanSummaryDto? summary;
        ScanCounters? report;
        int? running;
        lock (_sync) { summary = _summary; report = _report; running = _run is { Running: true } r ? r.Id : null; }
        if (request.ScanId is { } wanted && wanted != summary?.ScanId)
            throw wanted == running ? new InvalidOperationException(L.T("扫描尚未完成，请稍候", "The scan isn't finished yet; please wait", "スキャンがまだ終わっていません。しばらくお待ちください"))
                : new ArgumentException(L.T("这份异常清单属于另一次扫描，已经失效，请重新打开扫描结果", "This problem list belongs to another scan and is no longer valid; reopen the scan result", "この問題一覧は別のスキャンのもので、無効になっています。スキャン結果を開き直してください"));
        if (summary == null || report == null) throw new InvalidOperationException(L.T("请先扫描磁盘或文件夹", "Scan a drive or folder first", "先にドライブまたはフォルダーをスキャンしてください"));
        int offset = Math.Max(0, request.Offset);
        var (total, recorded, items) = report.Page(request.Kind, offset, Math.Clamp(request.Limit, 1, 1000));
        return new ScanIssuesDto(summary.ScanId, request.Kind, total, recorded, offset, items, offset + items.Length < total);
    }

    /// 服务锁只用来拿快照和写回：等树锁、读卷空间都在服务锁外，长查询占着树锁时 Cancel/IsScanning 照样秒回。
    public ScanSummaryDto? RefreshSummary()
    {
        FileTree? tree;
        ScanSummaryDto? summary;
        lock (_sync) { tree = _tree; summary = _summary; }
        if (tree == null || summary == null) return null;
        long total = summary.VolumeTotal, free = summary.VolumeFree;
        try { (total, free) = Volumes.GetSpace(tree.RootPath); } catch (Win32Exception) { }
        ScanSummaryDto fresh;
        // 拒绝访问、跳过的链接、错误这几项是扫描报告，保持扫描时的数字（和 scan.issues 的总数对得上），不随删除重算
        lock (tree.Sync)
        {
            long virtualAlloc = 0, virtualSize = 0, dups = 0, saved = 0;
            int unaccountedNode = FileTree.NoNode;
            for (int i = 1; i < tree.Count; i++)
            {
                if (tree.IsDeleted(i)) continue;
                if (tree.Has(i, NodeFlags.Virtual) && !tree.IsDir(i))
                {
                    virtualAlloc += tree.Alloc(i); virtualSize += tree.Size(i);
                    if (IsUnaccounted(tree.NameSpan(i))) unaccountedNode = i;
                }
                if (tree.Has(i, NodeFlags.HardLinkDup)) { dups++; saved += tree.Alloc(i); }
            }
            long used = Math.Max(0, total - free), alloc = tree.Alloc(0) - virtualAlloc;
            long size = tree.Size(0) - virtualSize;
            var categories = tree.CategoryTotals(Metric.Alloc);
            long unaccounted = summary.IsVolumeRoot ? Math.Max(0, used - alloc) : 0;
            if (unaccountedNode != FileTree.NoNode) tree.UpdateVirtualSizes(unaccountedNode, unaccounted, unaccounted);
            categories[FileCategories.Unaccounted] = unaccounted;
            // 用途汇总放在虚拟节点改完之后算，「未扫描」那一格才会跟着动。
            fresh = summary with
            {
                VolumeTotal = total, VolumeFree = free, VolumeUsed = used,
                ScannedAlloc = alloc, ScannedSize = size,
                Files = tree.Files(0), Dirs = tree.Dirs(0), TreeVersion = tree.Version,
                HardLinkDups = dups, HardLinkSavedBytes = saved, UnaccountedBytes = unaccounted,
                CategoryBytes = categories, PurposeBytes = tree.PurposeTotals(Metric.Alloc),
            };
        }
        fresh = fresh with { GapParts = SplitGap(fresh.RootPath, fresh.Mode, fresh.FileSystem, fresh.UnaccountedBytes) };
        // 等树锁期间换了一次扫描：旧树的数字别覆盖新扫描的汇总
        lock (_sync) { if (ReferenceEquals(_tree, tree)) _summary = fresh; }
        return fresh;
    }

    /// 未解释容量拆成「有依据的部分 + 原因未确认的余数」（dise §4.3 第 4 项），各份之和等于 gap。
    internal static GapPartDto[] SplitGap(string root, ScanMode mode, string fileSystem, long gap)
    {
        if (gap <= 0) return [];
        // MFT 扫描已经把元数据算进了树（「$NTFS 元数据」），差额里没有它；常规扫描读不到元数据，$MFT 是唯一不用管理员就问得到大小的。
        // 网络盘不问：$MFT 在服务器上，问一次就是一次联网
        long mft = mode == ScanMode.Walk && fileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
            && Volumes.GetDriveType(root) != "network" ? Math.Min(Volumes.MftBytes(root), gap) : 0;
        var parts = new List<GapPartDto>(2);
        if (mft > 0)
            parts.Add(new(GapKind.NtfsMetadata, mft, L.T("常规扫描读不到 NTFS 元数据；这是文件系统报告的 $MFT（主文件表）大小，", "A regular scan can't read NTFS metadata; this is the $MFT (master file table) size reported by the file system. ", "通常のスキャンでは NTFS のメタデータを読めません。これはファイル システムが報告した $MFT（マスター ファイル テーブル）のサイズです。")
                + L.T("$LogFile、$Bitmap 等更小的元数据问不到，没有算进来", "Smaller metadata such as $LogFile and $Bitmap can't be queried and isn't counted", "$LogFile や $Bitmap などの小さなメタデータは取得できず、含めていません")));
        if (gap > mft)
            parts.Add(new(GapKind.Unconfirmed, gap - mft, mode == ScanMode.Mft
                ? L.T("原因未确认：MFT 扫描已经统计了全部记录（含元数据），扫描期间的写入、损坏或孤立的记录都可能造成剩下的差额", "Cause unconfirmed: the MFT scan counted every record (including metadata); writes during the scan, or damaged or orphaned records, could cause the remaining difference", "原因は未確認：MFT スキャンはすべてのレコード（メタデータを含む）を数えました。残りの差は、スキャン中の書き込みや、壊れたレコード・孤立したレコードによる可能性があります")
                : L.T("原因未确认：无权访问的目录、卷影副本、扫描期间的写入都可能占着这部分，扫描结果给不出依据，不归因于其中任何一项", "Cause unconfirmed: folders without access, shadow copies, or writes during the scan could all take this space. The scan gives no basis, so it isn't attributed to any of them", "原因は未確認：アクセスできないフォルダー、ボリューム シャドウ コピー、スキャン中の書き込みのいずれもこの容量を使っている可能性があります。スキャン結果に根拠がないため、どれにも帰属させません")));
        return [.. parts];
    }

    static void Publish<T>(Action<T>? handlers, T value)
    {
        if (handlers == null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
            try { handler(value); } catch (Exception ex) { Trace.TraceError("扫描事件处理失败：{0}", ex); }
    }

    public void Dispose()
    {
        TreeWatcher? watcher;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_run?.Running == true) _run.Cancel.Cancel();
            (watcher, _watcher) = (_watcher, null);
        }
        watcher?.Dispose();
    }
}
