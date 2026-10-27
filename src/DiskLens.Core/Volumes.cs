using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskLens.Core;

public static class Volumes
{
    /// 一个盘「现在能不能读」探测出来的东西。读不出来就是 null，界面上显示成未就绪。
    internal sealed record DriveProbe(string Label, string FileSystem, long Total, long Free, bool? Ssd);

    // 同一个盘上一次探测还没返回时别再开新的：掉线的网络盘会把线程卡到 SMB 超时，
    // 用户狂点刷新不能把线程池一个个点干。
    static readonly Dictionary<string, Task<DriveProbe?>> InFlight = new(StringComparer.OrdinalIgnoreCase);

    static readonly Lazy<bool> Elevated = new(() =>
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    });

    // 盘符本来就是卷根，直接问 GetDriveTypeW；走 GetDriveType(path) 会先 GetVolumePathNameW，
    // 对掉线的映射网络盘那一步可能就联网了，探测期限也就形同虚设。
    public static IReadOnlyList<DriveInfoDto> List() => List(
        Environment.GetLogicalDrives(), root => DriveTypeName(GetDriveTypeW(root)), ProbeDrive,
        TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(1500));

    /// 探测函数和期限可注入，自检拿它模拟「卡死的网络盘」。
    /// 所有盘并行探测、共用一个时钟：整体耗时是最慢那个盘的期限，而不是各盘期限相加。
    internal static IReadOnlyList<DriveInfoDto> List(IEnumerable<string> roots, Func<string, string> typeOf,
        Func<string, string, DriveProbe?> probe, TimeSpan fixedDeadline, TimeSpan otherDeadline)
    {
        string? system = GetVolumeRoot(Environment.SystemDirectory);
        bool elevated = IsElevated();
        var clock = Stopwatch.StartNew();
        var drives = roots.Select(root =>
        {
            string type = typeOf(root);
            return (root, type, task: StartProbe(root, type, probe));
        }).ToArray();
        return drives.Select(d =>
        {
            bool isSystem = string.Equals(d.root, system, StringComparison.OrdinalIgnoreCase);
            var left = (d.type == "fixed" ? fixedDeadline : otherDeadline) - clock.Elapsed;
            DriveProbe? p = null;
            try { if (d.task.Wait(left > TimeSpan.Zero ? left : TimeSpan.Zero)) p = d.task.Result; }
            catch (AggregateException) { } // 探测自己炸了就当没就绪，别带崩整个列表
            if (p == null) return new DriveInfoDto(d.root, "", "", d.type, 0, 0, false, null, false, isSystem);
            // 与 MftScanner.CanScan 同口径：本地 NTFS。SupportsMft 与是否已提权无关，CanMft 才要求已提权。
            bool supportsMft = p.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase) && (d.type is "fixed" or "removable");
            return new DriveInfoDto(d.root, p.Label, p.FileSystem, d.type, p.Total, p.Free, true, p.Ssd,
                elevated && supportsMft, isSystem, supportsMft);
        }).ToArray();
    }

    static Task<DriveProbe?> StartProbe(string root, string type, Func<string, string, DriveProbe?> probe)
    {
        lock (InFlight)
        {
            if (InFlight.TryGetValue(root, out var running)) return running;
            var task = Task.Run(() => probe(root, type));
            InFlight[root] = task;
            task.ContinueWith(_ => { lock (InFlight) InFlight.Remove(root); }, TaskScheduler.Default);
            return task;
        }
    }

    static DriveProbe? ProbeDrive(string root, string type)
    {
        uint previous = ScanNative.SuppressErrorDialogs(); // 没盘的读卡器 / 光驱默认会弹框
        try
        {
            var label = new StringBuilder(262);
            var fs = new StringBuilder(64);
            if (!GetVolumeInformationW(root, label, label.Capacity, out _, out _, out _, fs, fs.Capacity)) return null;
            if (!GetDiskFreeSpaceExW(root, out _, out long total, out long free)) return null;
            return new DriveProbe(label.ToString(), fs.ToString(), total, free,
                type is "fixed" or "removable" ? IsSsd(root) : null);
        }
        finally { ScanNative.RestoreErrorMode(previous); }
    }

    /// 进程是否以管理员身份运行。运行期间不会变，算一次就够。
    public static bool IsElevated() => Elevated.Value;

    public static bool EnableBackupPrivilege()
    {
        const uint TokenAdjustPrivileges = 0x20, TokenQuery = 0x8, SePrivilegeEnabled = 2;
        const int ErrorNotAllAssigned = 1300;
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) return false;
        using (token)
        {
            if (!LookupPrivilegeValueW(null, "SeBackupPrivilege", out var luid)) return false;
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            // 令牌里压根没有这项特权时，AdjustTokenPrivileges 照样返回 TRUE，只在 LastError 留一个 1300。
            // 不查它，非管理员进程也会得到一个假的「已开启」。
            return AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)
                && Marshal.GetLastWin32Error() != ErrorNotAllAssigned;
        }
    }

    public static (long Total, long Free) GetSpace(string path)
    {
        // 第三个参数（TotalNumberOfFreeBytes）不受配额影响，用它而不是「可用空间」。
        if (!GetDiskFreeSpaceExW(GetVolumeRoot(path) ?? path, out _, out long total, out long free))
            throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("无法读取卷空间", "Couldn't read the volume space", "ボリュームの容量を読み取れません"));
        return (total, free);
    }

    /// 文件系统报告的 $MFT 大小（字节）。不用管理员也问得到（$LogFile、$Bitmap 就不行），但只是文件大小、不含簇对齐，算下限；
    /// 不是 NTFS 或问不到返回 0。
    public static long MftBytes(string volumeRoot)
    {
        var data = new byte[36]; // WIN32_FILE_ATTRIBUTE_DATA：nFileSizeHigh 在 28，nFileSizeLow 在 32
        if (!GetFileAttributesExW(Path.Join(volumeRoot, "$MFT"), 0, data)) return 0;
        return (long)(((ulong)ScanNative.U32(data, 28) << 32) | ScanNative.U32(data, 32));
    }

    public static string GetFileSystem(string path)
    {
        var fs = new StringBuilder(64);
        return GetVolumeInformationW(GetVolumeRoot(path) ?? path, null, 0, out _, out _, out _, fs, fs.Capacity)
            ? fs.ToString() : "";
    }

    /// 有「寻道惩罚」的才算机械盘。查不出来（网络盘、虚拟盘、没权限）返回 null，调用方按未知处理。
    public static bool? IsSsd(string path)
    {
        string? root = GetVolumeRoot(path);
        if (root == null || IsUnc(root)) return null;
        // 先换成 \\?\Volume{guid}：挂在文件夹上的卷（D:\mnt\x）也能查，不局限于盘符。
        var name = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(root, name, name.Capacity)) return null;
        using var handle = ScanNative.CreateFileW(name.ToString().TrimEnd('\\'), 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var query = new byte[12];
        query[0] = 7; // StorageDeviceSeekPenaltyProperty，QueryType 0 = PropertyStandardQuery
        var result = new byte[12];
        return ScanNative.DeviceIoControl(handle, 0x2d1400, query, query.Length, result, result.Length, out int count, IntPtr.Zero)
            && count >= 9 ? result[8] == 0 : null;
    }

    public static string GetDriveType(string path)
    {
        string root = GetVolumeRoot(path) ?? path;
        if (IsUnc(root)) return "network"; // UNC 一眼就是网络，别让 GetDriveTypeW 为它去碰网络
        return DriveTypeName(GetDriveTypeW(root));
    }

    static string DriveTypeName(uint type) => type switch
    {
        2 => "removable", 3 => "fixed", 4 => "network", 5 => "cdrom", 6 => "ram", _ => "unknown",
    };

    /// 卷根，形如 C:\、\\server\share\、\\?\Volume{guid}\。认 \\?\ 前缀；UNC 纯字符串解析，绝不联网。
    public static string? GetVolumeRoot(string path)
    {
        try
        {
            string full = Path.GetFullPath(ScanNative.StripLongPrefix(path));
            if (IsUnc(full)) return UncRoot(full);
            // GetVolumePathNameW 对超过 MAX_PATH 的普通路径会失败，长的补上 \\?\ 前缀再问。
            var buffer = new StringBuilder(1024);
            if (GetVolumePathNameW(full.Length >= 248 ? ScanNative.LongPath(full) : full, buffer, buffer.Capacity))
                return ScanNative.StripLongPrefix(buffer.ToString());
            return Path.GetPathRoot(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static bool IsVolumeRoot(string path)
    {
        var root = GetVolumeRoot(path);
        return root != null && string.Equals(Path.GetFullPath(ScanNative.StripLongPrefix(path)).TrimEnd('\\', '/'),
            root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal)
        && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal);

    /// \\server\share\rest → \\server\share\。只有服务器名、没有共享名时原样返回。
    static string UncRoot(string full)
    {
        int server = full.IndexOf('\\', 2);
        if (server < 0 || server == full.Length - 1) return full;
        int share = full.IndexOf('\\', server + 1);
        return share < 0 ? full + '\\' : full[..(share + 1)];
    }

    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetDiskFreeSpaceExW(string path, out long available, out long total, out long free);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetDriveTypeW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetVolumePathNameW(string path, StringBuilder root, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetFileAttributesExW(string path, int level, byte[] data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool GetVolumeInformationW(string root, StringBuilder? label, int labelLength, out uint serial, out uint maxName, out uint flags, StringBuilder fs, int fsLength);
}
