using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskLens.Core;

internal enum PathState { Missing, Present, Unknown }

/// 删除路径上的底层调用。全部走 \\?\ 前缀：宿主声明了 longPathAware，自检控制台没有，两边的行为必须一样。
internal static class NativeFs
{
    internal const int ErrFileNotFound = 2, ErrPathNotFound = 3, ErrAccessDenied = 5, ErrSharing = 32, ErrLock = 33, ErrDirNotEmpty = 145,
        ErrDeletePending = 303;

    /// 删除时「它已经不在了」：找不到、路径没了，或者别的程序已经发起删除、正等最后一个句柄关掉
    internal static bool IsGone(int error) => error is ErrFileNotFound or ErrPathNotFound or ErrDeletePending;
    const int ErrInvalidFunction = 1, ErrNotSupported = 50, ErrInvalidParameter = 87, ErrDirectory = 267;
    const uint ListDirectory = 0x1, ReadAttributes = 0x80, WriteAttributes = 0x100, DeleteAccess = 0x10000;
    const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4, OpenExisting = 3;
    const int FileDispositionInfo = 4, FileDispositionInfoEx = 21;
    // FILE_DISPOSITION_FLAG_DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE：和新版 DeleteFile 一样名字立刻消失，只读也照删
    const uint DispositionFlags = 0x1 | 0x2 | 0x10;
    const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    const uint AttrReadOnly = 1, AttrDirectory = 0x10, AttrReparse = 0x400, AttrNormal = 0x80;
    // 重解析标签里的「名字代理」位：junction、符号链接、挂载点都带它，云占位、去重、WOF 压缩不带。
    // 扫描器也是按这一位决定要不要钻进去，删除必须同一个标准。
    const uint NameSurrogate = 0x20000000;

    /// 入参必须是 Protection.Normalize 过的绝对路径。
    internal static string Native(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    /// 返回 Win32 错误码，0 表示删掉了。不跟随链接地开一个带 DELETE 的句柄，在这个句柄上标记删除：
    /// 叶子是 junction 或符号链接时删的只是链接本身；共享模式和 DeleteFile 一样，别人独占着就报「被占用」。
    /// verify 不为 null 时先在同一个句柄上核对身份（对不上它抛 IOException）：核对和删除绑在一个句柄上，
    /// 中间名字被换掉也删不到新来的那个——删的始终是核对过的那一个。
    /// 类型和 directory 对不上时照旧报错（文件当目录删是 267，目录当文件删是 5），不顺手删掉别的东西。
    internal static int Delete(string path, bool directory, Action<Identity>? verify = null)
    {
        string native = Native(path);
        // 写属性只给去掉只读的兜底用：拿不到就不要，删除照样试
        var handle = OpenForDelete(native, WriteAttributes);
        bool writable = !handle.IsInvalid;
        if (!writable && Marshal.GetLastWin32Error() == ErrAccessDenied) { handle.Dispose(); handle = OpenForDelete(native, 0); }
        using (handle)
        {
            if (handle.IsInvalid) return Marshal.GetLastWin32Error();
            if (!GetFileInformationByHandle(handle, out HandleInfo info)) return Marshal.GetLastWin32Error();
            verify?.Invoke(ToIdentity(info));
            if ((info.Attributes & AttrDirectory) != 0 != directory) return directory ? ErrDirectory : ErrAccessDenied;
            AfterVerify?.Invoke(path);
            if (MarkDeleted(handle)) return 0;
            int error = Marshal.GetLastWin32Error();
            // 老系统、FAT 不认带标志的删除时退回普通删除；只读要先在同一个句柄上去掉，其余的拒绝访问就是真的没权限
            if (error != ErrAccessDenied || !writable || (info.Attributes & AttrReadOnly) == 0 || !ClearReadOnly(handle, info.Attributes)) return error;
            return MarkDeleted(handle) ? 0 : Marshal.GetLastWin32Error();
        }
    }

    /// 自检专用：身份核对完、标记删除之前插一脚（比如把同名文件换掉），证明删掉的是核对过的那一个。平时是 null
    internal static Action<string>? AfterVerify;

    static SafeFileHandle OpenForDelete(string native, uint extra) => CreateFileW(native, DeleteAccess | ReadAttributes | extra,
        ShareRead | ShareWrite | ShareDelete, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);

    static bool MarkDeleted(SafeFileHandle handle)
    {
        uint flags = DispositionFlags;
        if (SetFileInformationByHandle(handle, FileDispositionInfoEx, ref flags, sizeof(uint))) return true;
        if (Marshal.GetLastWin32Error() is not (ErrInvalidParameter or ErrNotSupported or ErrInvalidFunction)) return false;
        byte delete = 1;
        return SetFileInformationByHandle(handle, FileDispositionInfo, ref delete, 1);
    }

    // 在删除用的句柄上改属性：句柄是 OPEN_REPARSE_POINT 打开的，改的一定是条目自己，不会顺着链接改到目标。
    static bool ClearReadOnly(SafeFileHandle handle, uint attributes)
    {
        var change = new BasicInfo { Attributes = attributes & ~AttrReadOnly }; // 时间字段为 0 表示不改
        if (change.Attributes == 0) change.Attributes = AttrNormal;
        return SetFileInformationByHandle(handle, 0, ref change, BasicSize);
    }

    /// 不跟随链接地看路径在不在。查不准（权限、非法名字）返回 Unknown，调用方一律当作「还在」。
    internal static PathState Probe(string path)
    {
        if (GetFileAttributesW(Native(path)) != uint.MaxValue) return PathState.Present;
        return Marshal.GetLastWin32Error() is ErrFileNotFound or ErrPathNotFound ? PathState.Missing : PathState.Unknown;
    }

    internal readonly record struct Identity(uint Volume, ulong FileId, long Created, uint Kind);

    /// 只要 FILE_READ_ATTRIBUTES：不参与共享检查，别的程序独占打开的文件（临时文件常见）照样读得到身份。
    /// 带上读数据/列目录权限的话，一个被占用的文件就能让整个确认失败。
    internal static Identity ReadIdentity(string path)
    {
        using var handle = CreateFileW(Native(path), ReadAttributes, ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out HandleInfo info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return ToIdentity(info);
    }

    static Identity ToIdentity(in HandleInfo info) => new(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow,
        ((long)info.CreatedHigh << 32) | info.CreatedLow, info.Attributes & (AttrDirectory | AttrReparse));

    /// 目录守卫：不跟随地打开目录，拒绝名字代理型的重解析点；
    /// 带着列目录访问、共享里不给 DELETE，别人就没法在我们动手期间改这个目录的名字。
    /// （只有 FILE_READ_ATTRIBUTES 的句柄不参与共享检查，拦不住任何人，所以访问里必须带 LIST_DIRECTORY。）
    internal static SafeFileHandle OpenGuard(string directory)
    {
        string native = Native(directory);
        var handle = CreateFileW(native, ListDirectory | ReadAttributes, ShareRead | ShareWrite, IntPtr.Zero,
            OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid && Marshal.GetLastWin32Error() == ErrAccessDenied)
        {
            // 没有列目录的权限：退一步只核实不是链接，钉不住名字也比一刀切拒绝强
            handle.Dispose();
            handle = CreateFileW(native, ReadAttributes, ShareRead | ShareWrite | ShareDelete, IntPtr.Zero,
                OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        }
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag tag, 8))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        if ((tag.Attributes & AttrReparse) != 0 && (tag.Tag & NameSurrogate) != 0)
        {
            handle.Dispose();
            throw new IOException(L.T("该目录是链接（联接点或符号链接），拒绝穿过链接删除；请直接扫描它指向的真实目录", "This folder is a link (junction or symbolic link), and deleting through links is refused; scan the real folder it points to instead", "このフォルダーはリンク（ジャンクションまたはシンボリック リンク）のため、リンク越しの削除は行いません。リンク先の実際のフォルダーを直接スキャンしてください"));
        }
        if ((tag.Attributes & AttrDirectory) == 0)
        {
            handle.Dispose();
            throw new IOException(L.T("该路径现在不是目录，请重新扫描", "This path is no longer a folder; rescan", "このパスは現在フォルダーではありません。再スキャンしてください"));
        }
        return handle;
    }

    internal static string Describe(int error) => error switch
    {
        ErrAccessDenied => L.T("没有权限，或受系统保护", "No permission, or protected by the system", "権限がないか、システムで保護されています"),
        ErrSharing or ErrLock => L.T("被其他程序占用", "In use by another program", "別のプログラムが使用中です"),
        ErrDirNotEmpty => L.T("目录不为空（扫描之后里面出现了新文件，或有文件被占用没能删掉）", "Folder not empty (new files appeared after the scan, or some files were in use and couldn't be deleted)", "フォルダーが空ではありません（スキャン後に新しいファイルができたか、使用中で削除できないファイルがあります）"),
        _ => new Win32Exception(error).Message
    };

    internal static string Describe(Exception ex) => ex is Win32Exception w ? Describe(w.NativeErrorCode) : ex.Message;

    /// 这类异常只代表单个条目出了问题，记进错误列表继续做别的。
    internal static bool IsEntryError(Exception ex) => ex is IOException or UnauthorizedAccessException or Win32Exception or COMException;

    /// 一串目录守卫，按路径引用计数：一批条目共用祖先时只开一次句柄。
    internal sealed class GuardSet : IDisposable
    {
        readonly Dictionary<string, (SafeFileHandle Handle, int Refs)> _held = new(StringComparer.OrdinalIgnoreCase);

        public void Acquire(string directory)
        {
            if (_held.TryGetValue(directory, out var entry)) _held[directory] = (entry.Handle, entry.Refs + 1);
            else _held[directory] = (OpenGuard(directory), 1);
        }

        public void Release(string directory)
        {
            if (!_held.TryGetValue(directory, out var entry)) return;
            if (entry.Refs > 1) { _held[directory] = (entry.Handle, entry.Refs - 1); return; }
            _held.Remove(directory);
            entry.Handle.Dispose();
        }

        /// 从卷根一路守到 directory（含）；中途失败就把已经拿到的放掉再抛。
        public List<string> AcquireChain(string directory)
        {
            var chain = new List<string>();
            for (string? p = directory; p != null; p = Path.GetDirectoryName(p)) chain.Add(p);
            chain.Reverse();
            int taken = 0;
            try { foreach (string p in chain) { Acquire(p); taken++; } }
            catch { for (int i = taken - 1; i >= 0; i--) Release(chain[i]); throw; }
            return chain;
        }

        public void ReleaseChain(List<string> chain)
        {
            for (int i = chain.Count - 1; i >= 0; i--) Release(chain[i]);
        }

        public void Dispose()
        {
            foreach (var (handle, _) in _held.Values) handle.Dispose();
            _held.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AttributeTag { public uint Attributes; public uint Tag; }

    [StructLayout(LayoutKind.Sequential)]
    struct HandleInfo
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);

    [StructLayout(LayoutKind.Sequential)]
    struct BasicInfo { public long Creation, Access, Write, Change; public uint Attributes; }

    static readonly uint BasicSize = (uint)Marshal.SizeOf<BasicInfo>();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFileAttributesW(string path);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out BasicInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref BasicInfo info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref uint flags, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte delete, uint size);
}
