using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskLens.Core;

namespace DiskLens.Check;

// 删除、保护与外壳操作（DESIGN §7、R2、R3）的自检。
// 磁盘上的事全在 .sandbox/delete 里发生；保护规则用合成树（不碰真实文件）。
// 会弹窗、开浏览器、提权或清空回收站的外壳方法一概不调，只测它们背后的纯判定函数。
static partial class DeleteChecks
{
    const long Cluster = 4096;
    static long Alloc(long size) => size == 0 ? 0 : (size + Cluster - 1) / Cluster * Cluster;

    public static void Run(CheckContext ctx)
    {
        string box = OpenSandbox(ctx);
        Case(ctx, "回收站可用性", () =>
        {
            // 外壳在这些情况下会不吭声地永久删除，必须在提交前拦下
            ctx.True(RecycleBinPolicy.Refusal(@"\\server\share\a.bin", 1) is { } n && n.Contains("网络"), "UNC 路径没有回收站");
            ctx.True(RecycleBinPolicy.Refusal(Path.Combine(box, "x.bin"), 1) == null, "沙箱所在的本地固定盘、1 字节：可以进回收站");
            ctx.True(RecycleBinPolicy.Refusal(Path.Combine(box, "x.bin"), long.MaxValue / 2) is { } big && big.Contains("容量上限")
                || RecycleBinPolicy.Refusal(Path.Combine(box, "x.bin"), long.MaxValue / 2) == null, "超大条目：有容量上限时拒绝（没设上限的机器放行）");
        });
        Case(ctx, "永久删除", () => PermanentBasics(ctx, Sub(box, "permanent")));
        Case(ctx, "被占用", () => LockedFiles(ctx, Sub(box, "locked")));
        Case(ctx, "确认快照与令牌", () => Snapshots(ctx, Sub(box, "snapshot")));
        Case(ctx, "删除任务进度", () => Progress(ctx, Sub(box, "progress")));
        Case(ctx, "目录守卫", () => Guards(ctx, Sub(box, "guard")));
        Case(ctx, "junction", () => Junctions(ctx, Sub(box, "junction")));
        Case(ctx, "DeleteContents", () => Contents(ctx, Sub(box, "contents")));
        Case(ctx, "硬链接", () => HardLinks(ctx, Sub(box, "hardlink")));
        Case(ctx, "Reconcile", () => Reconciling(ctx, Sub(box, "reconcile")));
        Case(ctx, "长路径", () => LongPaths(ctx, Sub(box, "longpath")));
        Case(ctx, "沙箱里的受保护目录", () => BlockedInSandbox(ctx, Sub(box, "blocked")));
        Case(ctx, "回收站", () => Recycling(ctx, Sub(box, "recycle")));
        Case(ctx, "并发与取消", () => Concurrency(ctx, Sub(box, "concurrency")));
        Case(ctx, "大目录", () => Scale(ctx, Sub(box, "scale")));
        Case(ctx, "保护规则", () => ProtectionRules(ctx));
        Case(ctx, "Check", () => CheckApi(ctx));
        Case(ctx, "外壳纯判定", () => ShellPure(ctx, Sub(box, "shell")));
        Case(ctx, "沙箱路径是联接", () => SandboxLinks(ctx, Sub(box, "sandbox-link")));
        RemoveLinks();
    }

    /// 测试基础设施自己：假仓库的 .sandbox、或者某一区的沙箱目录是指向外面的 junction 时，清场之前就拒绝，外面的东西原封不动
    static void SandboxLinks(CheckContext ctx, string root)
    {
        string outside = Dir(Path.Combine(root, "outside")), repo = Dir(Path.Combine(root, "repo"));
        string ro = Put(Path.Combine(outside, "area", "ro.txt"), 1);
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        try
        {
            ctx.True(CheckContext.SandboxDir(repo, "area") == Path.Combine(repo, ".sandbox", "area"), "沙箱还不存在：放行");
            if (!Junction(Path.Combine(repo, ".sandbox"), outside)) { ctx.Info("mklink /J 失败，跳过沙箱联接用例"); return; }
            Throws<InvalidOperationException>(ctx, () => CheckContext.SandboxDir(repo, "area"), "沙箱根 .sandbox 是 junction：拒绝");
            string real = Dir(Path.Combine(root, "repo2", ".sandbox"));
            if (Junction(Path.Combine(real, "area"), Path.Combine(outside, "area")))
                Throws<InvalidOperationException>(ctx, () => CheckContext.SandboxDir(Path.Combine(root, "repo2"), "area"), "本区沙箱目录是 junction：拒绝");
            ctx.True(File.Exists(ro) && File.GetAttributes(ro).HasFlag(FileAttributes.ReadOnly), "链接指向的外面那个只读文件和属性原样");
        }
        finally { File.SetAttributes(ro, FileAttributes.Normal); }
    }

    /// 杀软偶尔会在刚建好的 junction 上停一下，沙箱清理就撞上拒绝访问；重试几次，别让测试基础设施把整轮拖垮。
    static string OpenSandbox(CheckContext ctx)
    {
        for (int attempt = 0; ; attempt++)
            try { return ctx.Sandbox(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < 8) { Thread.Sleep(300); }
    }

    static readonly List<string> Links = [];

    /// 收尾先把自己建的 junction 摘掉（只删链接本身），下一轮清沙箱就不会在链接上绊倒。
    static void RemoveLinks()
    {
        foreach (string link in Links)
            for (int attempt = 0; attempt < 5 && Exists(link); attempt++)
            {
                try { Directory.Delete(link, false); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Thread.Sleep(200); }
            }
        Links.Clear();
    }

    // ---------------- 磁盘小工具 ----------------

    static string Sub(string box, string name) => Dir(Path.Combine(box, name));

    static string Dir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    /// 写一个指定大小的文件，缺的父目录一并建好。
    static string Put(string path, int size = 100)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    /// 不跟随链接地看路径在不在：File.Exists 对 junction 会去看目标，这里只看链接自己。
    static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    static string Cmd(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c");
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? "" : output.Trim();
    }

    /// mklink /J：不需要管理员。成功返回 true。
    static bool Junction(string link, string target)
    {
        bool ok = Cmd("mklink", "/J", link, target) == "";
        if (ok) Links.Add(link);
        return ok;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLinkW(string link, string existing, IntPtr security);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    // ---------------- 沙箱镜像 ----------------

    /// 沙箱目录的内存镜像：节点与磁盘一一对应，大小和时间取自真实文件。
    /// 删除逻辑不会替我们对账，所以每个用例收尾都拿 Consistent 逐点核对树和磁盘。
    sealed class Disk
    {
        public FileTree Tree { get; }
        readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);

        Disk(string root)
        {
            Tree = new FileTree(root, 1 << 8);
            _ids[root] = FileTree.RootId;
        }

        public int this[string path] => _ids[path.TrimEnd('\\')];

        public static Disk Scan(string root, Action<Disk>? beforeSeal = null)
        {
            root = root.TrimEnd('\\');
            var disk = new Disk(root);
            var queue = new Queue<string>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                string dir = queue.Dequeue();
                int parent = disk._ids[dir];
                foreach (string path in Directory.EnumerateFileSystemEntries(dir))
                {
                    var attr = File.GetAttributes(path);
                    var flags = NodeFlags.None;
                    if (attr.HasFlag(FileAttributes.Directory)) flags |= NodeFlags.Directory;
                    if (attr.HasFlag(FileAttributes.ReparsePoint)) flags |= NodeFlags.Reparse;
                    if (attr.HasFlag(FileAttributes.Hidden)) flags |= NodeFlags.Hidden;
                    if (attr.HasFlag(FileAttributes.System)) flags |= NodeFlags.System;
                    long size = 0, mtime = 0;
                    if ((flags & (NodeFlags.Directory | NodeFlags.Reparse)) == 0)
                    {
                        var info = new FileInfo(path);
                        size = info.Length;
                        mtime = info.LastWriteTimeUtc.ToFileTimeUtc();
                    }
                    int id = disk.Tree.AddNode(parent, Path.GetFileName(path), flags, size, Alloc(size), mtime);
                    disk._ids[path] = id;
                    if ((flags & NodeFlags.Directory) != 0 && (flags & NodeFlags.Reparse) == 0) queue.Enqueue(path);
                }
            }
            beforeSeal?.Invoke(disk);
            disk.Tree.Seal();
            return disk;
        }
    }

    /// 树与磁盘逐点一致：没删的必须还在，删了的必须不在；根的汇总等于活节点重新数一遍。
    static void Consistent(CheckContext ctx, FileTree tree, string what)
    {
        int bad = 0;
        long alloc = 0, files = 0;
        lock (tree.Sync)
            for (int i = 1; i < tree.Count; i++)
            {
                if (tree.Has(i, NodeFlags.Virtual)) continue;
                bool onDisk = Exists(tree.GetPath(i));
                if (tree.IsDeleted(i) == onDisk && bad++ < 5)
                    ctx.Fail($"{what}：{tree.GetPath(i)} 树里{(tree.IsDeleted(i) ? "已删除" : "还在")}，磁盘上{(onDisk ? "还在" : "不存在")}");
                if (tree.IsDeleted(i) || tree.IsDir(i)) continue;
                files++;
                if (!tree.Has(i, NodeFlags.HardLinkDup)) alloc += tree.Alloc(i);
            }
        ctx.Equal(0, bad, $"{what}：树与磁盘不一致的节点数");
        lock (tree.Sync)
        {
            ctx.Equal(alloc, tree.Alloc(FileTree.RootId), $"{what}：根的占用等于活文件之和");
            ctx.Equal(files, tree.Files(FileTree.RootId), $"{what}：根的文件数等于活文件数");
        }
    }

    static OpResultDto Delete(FileTree tree, bool permanent, params int[] ids) =>
        DeleteService.Delete(tree, ids, permanent, CancellationToken.None);

    // ---------------- 合成树 ----------------

    /// 按完整路径造节点的内存树，只给保护规则和 Check 用，不碰磁盘。
    sealed class Sim
    {
        readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
        public FileTree Tree { get; }

        public Sim(string root)
        {
            Tree = new FileTree(root, 1 << 10);
            _ids[root.TrimEnd('\\')] = FileTree.RootId;
        }

        public int Dir(string path, NodeFlags flags = 0) => Node(path, NodeFlags.Directory | flags, 0);
        public int File(string path, long size = 1000, NodeFlags flags = 0) => Node(path, flags, size);

        int Node(string path, NodeFlags flags, long size)
        {
            path = path.TrimEnd('\\');
            if (_ids.TryGetValue(path, out int id)) return id;
            string parentPath = Path.GetDirectoryName(path) ?? throw new ArgumentException("没有父目录：" + path);
            int parent = Dir(parentPath);
            id = Tree.AddNode(parent, Path.GetFileName(path), flags, size, Alloc(size), 0);
            _ids[path] = id;
            return id;
        }

        public FileTree Done()
        {
            Tree.Seal();
            return Tree;
        }
    }
}
