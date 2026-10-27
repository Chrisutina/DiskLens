using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskLens.Core;

/// Walk 和 MFT 两条扫描路径共用的 Win32 小工具。MftScanner / MftRecordParser 也在用这里的
/// U16/U32/I64/Flags/NormalizePath/CreateFileW/DeviceIoControl，签名别随手改。
internal static class ScanNative
{
    internal const uint TagMountPoint = 0xA0000003, TagSymlink = 0xA000000C;

    /// 联接点和卷挂载点用的是同一个标签，只能看重解析数据（REPARSE_DATA_BUFFER）里的替代名：\??\Volume{…} 是卷挂载点。
    internal static bool IsVolumeTarget(ReadOnlySpan<byte> reparse)
    {
        if (reparse.Length < 16) return false;
        int at = 16 + U16(reparse, 8), length = U16(reparse, 10) & ~1;
        return at + length <= reparse.Length
            && MemoryMarshal.Cast<byte, char>(reparse.Slice(at, length)).StartsWith(@"\??\Volume{", StringComparison.OrdinalIgnoreCase);
    }

    /// 读重解析点自己的数据（不跟随）看它是不是卷挂载点；打不开或读不到返回 null。一次扫描只有几十上百个联接点要问。
    internal static bool? IsVolumeMountPoint(string path)
    {
        using var handle = CreateFileW(LongPath(path), 0, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var data = new byte[16 * 1024]; // MAXIMUM_REPARSE_DATA_BUFFER_SIZE
        return DeviceIoControl(handle, 0x900A8, null, 0, data, data.Length, out int length, IntPtr.Zero)
            ? IsVolumeTarget(data.AsSpan(0, length)) : null;
    }

    /// 给 Win32 API 用的长路径形式。已带 \\?\ 或 \\.\ 的原样返回，UNC 走 \\?\UNC\。
    internal static string LongPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    /// 还原成普通路径：\\?\C:\x → C:\x，\\?\UNC\s\sh → \\s\sh。\\?\Volume{...} 这类还原不了的原样保留。
    internal static string StripLongPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.Length >= 7 && path.StartsWith(@"\\?\", StringComparison.Ordinal)
            && char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] == '\\') return path[4..];
        return path;
    }

    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // 先脱前缀再 GetFullPath：带 \\?\ 的路径 .NET 一概不规整，树根名字会带着前缀到处跑。
        string full = Path.GetFullPath(StripLongPrefix(path));
        string root = Path.GetPathRoot(full)!;
        return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
    }

    internal static NodeFlags Flags(uint attributes)
    {
        NodeFlags f = 0;
        if ((attributes & 0x10) != 0) f |= NodeFlags.Directory;
        if ((attributes & 0x2) != 0) f |= NodeFlags.Hidden;
        if ((attributes & 0x4) != 0) f |= NodeFlags.System;
        if ((attributes & 0x400) != 0) f |= NodeFlags.Reparse;
        if ((attributes & 0x800) != 0) f |= NodeFlags.Compressed;
        if ((attributes & 0x200) != 0) f |= NodeFlags.Sparse;
        if ((attributes & 0x4000) != 0) f |= NodeFlags.Encrypted;
        if ((attributes & (0x1000 | 0x40000 | 0x400000)) != 0) f |= NodeFlags.Cloud;
        return f;
    }

    /// 压缩 / 稀疏 / WOF 文件的真实占用。失败时给出 Win32 错误码，由调用方决定怎么记。
    internal static bool TryCompressedSize(string path, out long size, out int error)
    {
        uint low = GetCompressedFileSizeW(LongPath(path), out uint high);
        error = Marshal.GetLastWin32Error();
        // 返回 0xFFFFFFFF 且错误码为 0 时它就是一个合法的低 32 位，不能当失败。
        if (low == uint.MaxValue && error != 0) { size = 0; return false; }
        size = checked((long)(((ulong)high << 32) | low));
        return true;
    }

    /// Win32 错误码的系统说明（中文系统上就是中文），带上错误码方便排查。
    internal static string ErrorText(int error) => L.T($"{new System.ComponentModel.Win32Exception(error).Message}（错误 {error}）", $"{new System.ComponentModel.Win32Exception(error).Message} (error {error})", $"{new System.ComponentModel.Win32Exception(error).Message}（エラー {error}）");

    internal static long CompressedSize(string path, long fallback, ScanCounters? counters = null)
    {
        if (TryCompressedSize(path, out long size, out int error)) return size;
        counters?.Warn(L.T($"无法核实压缩或稀疏文件的实际占用（保留目录项值）：{path}，错误 {error}", $"Couldn't verify the actual size of a compressed or sparse file (keeping the directory entry's value): {path}, error {error}", $"圧縮ファイルまたはスパース ファイルの実際の使用容量を確認できません（ディレクトリ エントリの値を使用）：{path}、エラー {error}"));
        return fallback;
    }

    /// 本线程出错直接返回失败，不弹「请插入磁盘」之类的模态框 —— 扫描中途盘被拔或光驱没碟时，
    /// 默认行为是弹框把线程卡死。返回旧模式，用完交给 RestoreErrorMode。
    internal static uint SuppressErrorDialogs()
    {
        SetThreadErrorMode(0x1 /* SEM_FAILCRITICALERRORS */, out uint previous);
        return previous;
    }

    internal static void RestoreErrorMode(uint previous) => SetThreadErrorMode(previous, out _);

    internal static long I64(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt64LittleEndian(s[at..]);
    internal static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);
    internal static ushort U16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, byte[] buffer, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetCompressedFileSizeW(string path, out uint high);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputLength, byte[] output, int outputLength, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetThreadErrorMode(uint newMode, out uint oldMode);
}
