using System.Diagnostics;

namespace DiskLens.Check;

public sealed class CheckContext(string area, string[] args)
{
    public string Area { get; } = area;
    public string[] Args { get; } = args;
    public int Failures { get; private set; }
    public int Assertions { get; private set; }

    public static string RepoRoot { get; } = FindRepoRoot();

    /// 本 area 专用的沙箱目录 <仓库>/.sandbox/<area>。测试只许在这里面造文件、删文件。
    public string Sandbox(bool clean = true)
    {
        var dir = SandboxDir(Area);
        if (clean && Directory.Exists(dir))
        {
            if (!dir.Contains(Path.DirectorySeparatorChar + ".sandbox" + Path.DirectorySeparatorChar))
                throw new InvalidOperationException("沙箱路径不对劲，拒绝清理：" + dir);
            ClearReadOnly(new DirectoryInfo(dir)); // 只读文件和留下的 junction 都会让 Directory.Delete 整个炸掉，连带下一轮测试
            Directory.Delete(dir, recursive: true);
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string SandboxDir(string area) => SandboxDir(RepoRoot, area);

    /// 沙箱根 .sandbox 和本区目录必须是真目录：哪一层是联接或符号链接，造文件、抹只读、清理就全落到仓库外面去了。
    /// 动手之前先查，查到就拒绝（还不存在的层不算）。各区清场前的预处理也要先过这一道
    internal static string SandboxDir(string repo, string area)
    {
        string root = Path.Combine(repo, ".sandbox"), dir = Path.Combine(root, area);
        foreach (string p in new[] { root, dir })
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(p); } // 不跟随链接：拿到的是链接自己的属性，目标不在也照样认得出
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("沙箱路径是联接或符号链接，拒绝在里面造文件或清理：" + p);
        }
        return dir;
    }

    /// --name value
    public string? Option(string name)
    {
        int i = Array.IndexOf(Args, "--" + name);
        return i >= 0 && i + 1 < Args.Length ? Args[i + 1] : null;
    }

    public bool Flag(string name) => Array.IndexOf(Args, "--" + name) >= 0;

    public void True(bool condition, string what)
    {
        Assertions++;
        if (!condition) Fail(what);
    }

    public void Equal<T>(T expected, T actual, string what)
    {
        Assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) Fail($"{what}：期望 {expected}，实际 {actual}");
    }

    public void Fail(string message)
    {
        Failures++;
        Console.WriteLine($"  ✗ {message}");
    }

    public void Info(string message) => Console.WriteLine($"  · {message}");

    public T Time<T>(string label, Func<T> f)
    {
        var sw = Stopwatch.StartNew();
        var r = f();
        Info($"{label}：{sw.Elapsed.TotalMilliseconds:F1} ms");
        return r;
    }

    /// 手工递归：遇到重解析点（junction、符号链接）只处理它本身，绝不进去改目标的属性。
    static void ClearReadOnly(DirectoryInfo dir)
    {
        foreach (var e in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true }))
        {
            if (e.Attributes.HasFlag(FileAttributes.ReadOnly)) e.Attributes &= ~FileAttributes.ReadOnly;
            if (e is not DirectoryInfo d) continue;
            // 链接先单独删掉（非递归，只删链接本身）：留给递归删除的话，.NET 会先对 junction 调 DeleteVolumeMountPoint，非管理员被拒
            if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) d.Delete();
            else ClearReadOnly(d);
        }
    }

    static string FindRepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "DiskLens.slnx"))) return d.FullName;
        throw new InvalidOperationException("找不到仓库根目录（DiskLens.slnx）");
    }
}
