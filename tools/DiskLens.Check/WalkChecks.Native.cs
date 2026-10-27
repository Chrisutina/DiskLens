using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DiskLens.Core;
using Microsoft.Win32.SafeHandles;

namespace DiskLens.Check;

// 自检用的「标准答案」和系统调用。这里的实现刻意与被测代码走不同的 API（.NET 枚举 + 句柄查询），
// 两边对得上才说明不是一起错。
static partial class WalkChecks
{
    const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000, FileReadAttributes = 0x80;

    /// 一个文件真正占的字节：压缩 / 稀疏 / 重解析（WOF）走 GetCompressedFileSizeW，普通文件问 FileStandardInfo。
    static long Allocation(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.ReparsePoint)) == 0)
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[24];
            if (!ScanNative.GetFileInformationByHandleEx(handle, 1, buffer, buffer.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return ScanNative.I64(buffer, 0);
        }
        return CompressedApi(path);
    }

    static long CompressedApi(string path)
    {
        uint low = GetCompressedFileSizeW(ScanNative.LongPath(path), out uint high);
        int error = Marshal.GetLastWin32Error();
        if (low == uint.MaxValue && error != 0) throw new Win32Exception(error);
        return checked((long)(((ulong)high << 32) | low));
    }

    static long ClusterSize(string path)
    {
        string root = Volumes.GetVolumeRoot(path) ?? path;
        if (!GetDiskFreeSpaceW(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "读不到簇大小");
        return (long)sectorsPerCluster * bytesPerSector;
    }

    static long RoundUp(long value, long unit) => (value + unit - 1) / unit * unit;

    /// 一个文件的真实大小、占用和身份，用独立的句柄查询得到；打不开返回 null。
    /// 占用规则与产品代码同一份规格：压缩 / 稀疏 / WOF 标签，以及本地 NTFS 上「有大小却占用为 0」都问 GetCompressedFileSizeW。
    static (long Size, long Alloc, (ulong, ulong, ulong) Key, uint Attributes)? Truth(string path, ref int unique)
    {
        using var handle = ScanNative.CreateFileW(ScanNative.LongPath(path), FileReadAttributes, 7, IntPtr.Zero, 3,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var standard = new byte[24];
        if (!ScanNative.GetFileInformationByHandleEx(handle, 1, standard, standard.Length)) return null;
        var id = new byte[24]; // FILE_ID_INFO：卷序列号 + 128 位 FileId
        var key = ScanNative.GetFileInformationByHandleEx(handle, 18, id, id.Length)
            ? ((ulong)ScanNative.I64(id, 0), (ulong)ScanNative.I64(id, 8), (ulong)ScanNative.I64(id, 16))
            : (0UL, 0UL, (ulong)++unique);
        long alloc = Math.Max(0, ScanNative.I64(standard, 0)), size = Math.Max(0, ScanNative.I64(standard, 8));
        uint attributes = 0;
        var tag = new byte[8];
        if (ScanNative.GetFileInformationByHandleEx(handle, 9, tag, tag.Length))
        {
            attributes = ScanNative.U32(tag, 0);
            uint reparse = ScanNative.U32(tag, 4);
            bool cloud = (attributes & (0x1000 | 0x40000 | 0x400000)) != 0;
            if (!cloud && ((attributes & (0x800 | 0x200)) != 0 || reparse == 0x80000017 || (size > 0 && alloc == 0))
                && ScanNative.TryCompressedSize(path, out long real, out _))
                alloc = real;
        }
        return (size, alloc, key, attributes);
    }

    /// 遍历一棵树（不进重解析目录），每个文件条目回调一次。
    static void EachFile(string root, Action<FileSystemInfo> onFile, Action? onDir = null)
    {
        var pending = new Stack<string>();
        // 带 \\?\ 前缀走，.NET 就不会替我们「规整」路径：名字以空格开头、以点结尾的目录在 NTFS 上合法，
        // 普通路径却打不开（D:\ RESOURCE433 就是真实例子）
        pending.Push(ScanNative.LongPath(ScanNative.NormalizePath(root)));
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true, ReturnSpecialDirectories = false };
        while (pending.Count > 0)
        {
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos("*", options).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; } // 目录在列出之后被删了，活的卷上是常态
            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo dir)
                {
                    onDir?.Invoke();
                    if ((dir.Attributes & FileAttributes.ReparsePoint) == 0) pending.Push(dir.FullName);
                }
                else onFile(entry);
            }
        }
    }

    /// 暴力标准答案：.NET 枚举整棵树，不进重解析目录，按 FileId 去重，逐个文件开句柄问真实大小。
    static (long Files, long Dirs, long Size, long Alloc) Oracle(string root)
    {
        long files = 0, dirs = 0, size = 0, alloc = 0;
        int unique = 0;
        var seen = new HashSet<(ulong, ulong, ulong)>();
        EachFile(root, entry =>
        {
            files++;
            if (Truth(entry.FullName, ref unique) is not { } t || !seen.Add(t.Key)) return; // 硬链接的后续出场不再计
            size += t.Size;
            alloc += t.Alloc;
        }, () => dirs++);
        return (files, dirs, size, alloc);
    }

    static void WriteBytes(string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }

    internal static void MakeJunction(string path, string target) => MakeMountPoint(path, @"\??\" + target, target);

    /// 非管理员也能设挂载点标签（和 mklink /J 一样）；替代名写成 \??\Volume{…} 就是卷挂载点，卷不存在也设得上、删得掉。
    static bool MakeMountPoint(string path, string substitute, string print)
    {
        Directory.CreateDirectory(path);
        using var handle = ScanNative.CreateFileW(path, 0x40000000, 7, IntPtr.Zero, 3, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (Control(handle, 0x900A4, MftRec.MountPoint(substitute, print))) return true;
        if (!substitute.StartsWith(@"\??\Volume{", StringComparison.Ordinal))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "构造沙箱 junction 失败");
        return false;
    }

    /// 只删链接本身，不碰目标；不存在（2 / 3）或根本不是链接而是个有内容的目录（145）都算「没东西可删」。
    internal static void RemoveLink(string path)
    {
        if (!RemoveDirectoryW(ScanNative.LongPath(path)) && Marshal.GetLastWin32Error() is not (2 or 3 or 145))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "移除沙箱 junction 失败：" + path);
    }

    static bool Control(SafeFileHandle handle, uint code, byte[] input) =>
        DeviceIoControl(handle, code, input, input.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);

    // ---- ACL：必须按 SID。这台机器的计算机名和用户名同名，按名字授权会授给计算机账户。 ----
    // 取的是当前令牌里的用户 SID，与 whoami /user 打印的是同一个值，省得解析本地化的控制台输出。
    static string UserSid => WindowsIdentity.GetCurrent().User!.Value;

    static bool DenyListing(string dir) => RunTool("icacls.exe", dir, "/deny", "*" + UserSid + ":(OI)(CI)(RD)") == 0;

    /// 先摘掉那条拒绝 ACE（目录才读得进去），再把里面所有东西的 ACL 重置回继承。目录不存在时什么都不做。
    static void RestoreAcl(string dir)
    {
        if (!Directory.Exists(dir)) return;
        RunTool("icacls.exe", dir, "/remove:d", "*" + UserSid);
        RunTool("icacls.exe", dir, "/reset", "/t", "/c", "/q");
    }

    /// 跑 System32 里的命令行工具，不开窗口；输出丢弃，只要退出码。
    static int RunTool(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("启动失败：" + exe);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(true); throw new TimeoutException(exe + " 超过 30 秒没返回"); }
        Task.WaitAll(output, error);
        return process.ExitCode;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateHardLinkW(string path, string target, IntPtr security);
    // 返回的是 1 字节 BOOLEAN，不是 4 字节 BOOL：按默认封送，失败时高位的垃圾会被读成 true
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool CreateSymbolicLinkW(string link, string target, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetCompressedFileSizeW(string path, out uint high);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool RemoveDirectoryW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);
}
