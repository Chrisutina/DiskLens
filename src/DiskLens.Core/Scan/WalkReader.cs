using System.Runtime.InteropServices;

namespace DiskLens.Core;

/// 一个目录项。名字不单独分配字符串，放在 WalkBatch 的字符池里 —— 几百万个文件，每个一个 string 纯属给 GC 打工。
internal struct WalkEntry
{
    public int NameOffset, NameLength;
    public NodeFlags Flags;
    public uint Tag;
    public long Size, Alloc, Modified;
    public UInt128 FileId;
}

/// 每个工作线程一份，目录之间复用。
internal sealed class WalkBatch
{
    public readonly List<WalkEntry> Entries = new(256);
    char[] _names = new char[8192];
    int _length;

    public void Clear() { Entries.Clear(); _length = 0; }

    public ReadOnlySpan<char> Name(in WalkEntry e) => _names.AsSpan(e.NameOffset, e.NameLength);

    public void Add(ReadOnlySpan<char> name, NodeFlags flags, uint tag, long size, long alloc, long modified, UInt128 fileId)
    {
        if (_length + name.Length > _names.Length)
            Array.Resize(ref _names, Math.Max(_names.Length * 2, _length + name.Length));
        name.CopyTo(_names.AsSpan(_length));
        Entries.Add(new WalkEntry
        {
            NameOffset = _length, NameLength = name.Length, Flags = flags, Tag = tag,
            Size = size, Alloc = alloc, Modified = modified, FileId = fileId,
        });
        _length += name.Length;
    }
}

/// 对 GetFileInformationByHandleEx 的一次调用。做成委托是为了自检能塞假数据，测降级链。
internal delegate bool DirQuery(int infoClass, byte[] buffer, out int error);

/// 目录枚举：读缓冲、解析三种信息类、降级链，以及「进不进去 / 要不要重查占用」两条规则。
internal static class WalkReader
{
    /// 重解析标签的名称代理位。junction、符号链接、挂载点都带它：它们只是「指向别处的名字」，
    /// 进去既会重复统计，还可能绕成环。云文件、WCI、Dedup 之类不带，照常进。
    const uint NameSurrogate = 0x20000000;
    const uint TagWof = 0x80000017;
    const int ErrorNoMoreFiles = 18, ErrorInvalidData = 13;

    internal const int LevelCount = 3;
    // 降级等级：0 = FileIdExtdDirectoryInfo，1 = FileIdBothDirectoryInfo，2 = FileFullDirectoryInfo。
    // 每级有「从头读」和「接着读」两个信息类。
    static readonly int[] RestartClass = [20, 11, 15];
    static readonly int[] ContinueClass = [19, 10, 14];
    static readonly int[] NameAt = [88, 104, 68];

    internal static bool IsAccessDenied(int error) => error is 5 or 65; // ERROR_ACCESS_DENIED / ERROR_NETWORK_ACCESS_DENIED

    /// 文件系统不认这个信息类（FAT、exFAT、部分网络盘）时返回的几个错误码。
    internal static bool IsUnsupportedClass(int error) => error is 1 or 50 or 87 or 120 or 124;

    /// 是目录，并且不是「指向别处的名字」才进去。标签读出来是 0 的重解析目录（降级结构里有的文件系统不填）
    /// 当作未知，宁可不进也别在符号链接环里转圈。
    internal static bool ShouldEnter(NodeFlags flags, uint tag)
    {
        if ((flags & NodeFlags.Directory) == 0) return false;
        if ((flags & NodeFlags.Reparse) == 0) return true;
        return tag != 0 && (tag & NameSurrogate) == 0;
    }

    /// 目录项里的占用不可信的文件，要单独问一次 GetCompressedFileSizeW：
    /// 带压缩 / 稀疏属性，或重解析标签是 WOF；以及本地 NTFS / ReFS 上「有大小却占用为 0」的文件 ——
    /// CompactOS 的 WOF 压缩文件在目录项里既不带重解析属性、标签也是 0，数据又在命名流里，
    /// 目录项只剩逻辑大小和 0 占用，这是它唯一的破绽。常驻 MFT 的小文件占用是按 8 字节对齐的，不会是 0。
    /// 云文件占位符不查：占用就是本地实际有的部分，没必要为它多开十万次文件。
    /// 网络盘不做「占用为 0」这一条：有的服务器不报分配大小，会变成逐文件联网。
    internal static bool NeedsRealAlloc(NodeFlags flags, uint tag, long size, long alloc, bool localNtfs) =>
        (flags & (NodeFlags.Directory | NodeFlags.Cloud)) == 0
        && ((flags & (NodeFlags.Compressed | NodeFlags.Sparse)) != 0 || tag == TagWof
            || (localNtfs && size > 0 && alloc == 0));

    /// 读完一个目录。返回 0 或 Win32 错误码；出错时 batch 里留着出错之前已读到的条目，别白扔。
    /// 降级只发生在第一次调用就报「不支持」的时候，读到一半不换信息类；level 带回最终用的等级。
    internal static int ReadEntries(DirQuery query, ref int level, byte[] buffer, WalkBatch batch, CancellationToken ct)
    {
        bool restart = true;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int infoClass = (restart ? RestartClass : ContinueClass)[level];
            if (!query(infoClass, buffer, out int error))
            {
                if (error == ErrorNoMoreFiles) return 0;
                if (restart && level < LevelCount - 1 && IsUnsupportedClass(error)) { level++; continue; }
                return error;
            }
            restart = false;
            int bad = Parse(buffer, level, batch);
            if (bad != 0) return bad;
        }
    }

    /// 偏移量见 Win32 文档里 FILE_ID_EXTD_DIR_INFO / FILE_ID_BOTH_DIR_INFO / FILE_FULL_DIR_INFO。
    /// 三者前 60 字节布局相同；差别在 EaSize 之后，重解析标签：Extd 有专用字段（68），
    /// 另外两种把它塞在 EaSize（64）里 —— 只在带重解析属性时才是标签。
    internal static int Parse(ReadOnlySpan<byte> buffer, int level, WalkBatch batch)
    {
        int nameAt = NameAt[level];
        int at = 0;
        while (true)
        {
            if (buffer.Length - at < nameAt) return ErrorInvalidData;
            var row = buffer[at..];
            uint next = ScanNative.U32(row, 0), nameBytes = ScanNative.U32(row, 60);
            // 缓冲区来自文件系统，坏盘 / 坏驱动给出的长度不能信：越界就放弃这个目录，不能崩
            if ((nameBytes & 1) != 0 || nameBytes > (uint)(row.Length - nameAt)
                || (next != 0 && (next < nameAt + nameBytes || next > (uint)row.Length)))
                return ErrorInvalidData;
            // 直接按 UTF-16 原样拷：NTFS 允许孤立代理项，转 string 会被换成 U+FFFD，之后就按名字打不开文件了
            var name = MemoryMarshal.Cast<byte, char>(row.Slice(nameAt, (int)nameBytes));
            if (!IsDot(name)) AddEntry(row, name, level, batch);
            if (next == 0) return 0;
            at += (int)next;
        }
    }

    static void AddEntry(ReadOnlySpan<byte> row, ReadOnlySpan<char> name, int level, WalkBatch batch)
    {
        var flags = ScanNative.Flags(ScanNative.U32(row, 56));
        bool dir = (flags & NodeFlags.Directory) != 0;
        // 目录自己的大小不算：Walk 只统计文件，目录的数字由 Seal 聚合出来
        long size = dir ? 0 : Math.Max(0, ScanNative.I64(row, 40));
        long alloc = dir ? 0 : Math.Max(0, ScanNative.I64(row, 48));
        uint tag = (flags & NodeFlags.Reparse) != 0 ? ScanNative.U32(row, level == 0 ? 68 : 64) : 0;
        UInt128 id = level switch
        {
            0 => ((UInt128)(ulong)ScanNative.I64(row, 80) << 64) | (ulong)ScanNative.I64(row, 72),
            1 => (ulong)ScanNative.I64(row, 96),
            _ => 0, // FileFullDirectoryInfo 没有 FileId，硬链接去重会因此失效
        };
        batch.Add(name, flags, tag, size, alloc, ScanNative.I64(row, 24), id);
    }

    static bool IsDot(ReadOnlySpan<char> name) =>
        name is [ '.' ] or [ '.', '.' ];
}
