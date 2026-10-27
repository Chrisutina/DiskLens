using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DiskLens.Core;

/// 运行列表的一段：VCN 起点、LCN 起点（-1 表示稀疏空洞）、簇数。
internal readonly record struct MftRun(long Vcn, long Lcn, long Clusters);

/// 一个 $FILE_NAME。解析时直接追加进 MftNames，不给每个名字单独分配对象——100 万文件就是 130 万次分配。
internal struct MftLink
{
    internal ulong Parent;       // 父引用：低 48 位记录号，高 16 位序列号
    internal int Record;         // 所属基记录号（解析器不填，调用方填；扩展记录合并后改成基记录号，-1 表示作废）
    internal int NameOffset;     // 在名字池里的偏移
    internal ushort NameLength;  // 字符数
    internal byte Namespace;     // 0 POSIX、1 Win32、2 DOS、3 Win32&DOS
    internal byte Dropped;       // 建树前规整名字时置位，见 MftTreeBuilder
}

/// 名字池：链接表加字符池，按追加顺序存放，所以失败时直接截断就能回滚。
internal sealed class MftNames
{
    internal MftLink[] Links = new MftLink[256];
    internal char[] Chars = new char[4096];
    internal int Count, CharCount;

    internal void Reset() { Count = 0; CharCount = 0; }

    internal ReadOnlySpan<char> Name(int i) => Chars.AsSpan(Links[i].NameOffset, Links[i].NameLength);

    internal void Truncate(int count)
    {
        if (count >= Count) return;
        CharCount = Links[count].NameOffset;
        Count = count;
    }

    /// 原样保存通过解析器校验的名字，避免替换字符后指向另一个真实文件。
    internal void Add(ulong parent, byte ns, ReadOnlySpan<byte> utf16)
    {
        int n = utf16.Length / 2;
        if (Count == Links.Length) Array.Resize(ref Links, Links.Length * 2);
        if (CharCount + n > Chars.Length) Array.Resize(ref Chars, Math.Max(Chars.Length * 2, CharCount + n));
        var dst = Chars.AsSpan(CharCount, n);
        utf16[..(n * 2)].CopyTo(MemoryMarshal.AsBytes(dst));
        Links[Count++] = new MftLink { Parent = parent, NameOffset = CharCount, NameLength = (ushort)n, Namespace = ns };
        CharCount += n;
    }
}

internal enum MftParseStatus : byte
{
    Unused,   // 全零或未在用：正常现象，不算错
    Ok,
    Corrupt,
}

/// 一条记录解析出来的标量。名字另外进 MftNames。
internal struct MftRecordInfo
{
    internal ushort Sequence;
    internal ulong BaseReference;     // 非 0 表示扩展记录，属性归基记录
    internal bool IsDirectory, HasData, HasAttributeList;
    internal NodeFlags Flags;
    internal long Mtime;
    internal long Size;               // 未命名 $DATA 的逻辑大小
    internal long Alloc;              // 所有 VCN 0 的非常驻属性占用之和
    internal uint ReparseTag;
    internal bool VolumeMount;        // 挂载点标签、替代名是 \??\Volume{…}：卷挂载点，不是联接点
}

/// 找 $MFT 自己的位置时才需要：未命名 $DATA 的运行列表和 $ATTRIBUTE_LIST。普通记录传 null，热路径不碰它。
internal sealed class MftBoot
{
    internal readonly List<MftRun> DataRuns = [];
    internal readonly List<MftRun> ListRuns = [];
    internal byte[]? ListBytes;   // 常驻列表的内容
    internal long ListSize;       // 非常驻列表的实际字节数
}

/// 只吃 Span，不碰任何 IO，所以不用管理员也能拿构造出来的记录测。
internal static class MftRecordParser
{
    internal const ulong ReferenceMask = 0x0000_FFFF_FFFF_FFFF;

    /// 稀疏文件可以声明天文数字的逻辑长度，几条损坏记录就能让全树求和溢出，所以封顶 1 PiB。
    internal const long MaxLogicalSize = 1L << 50;

    /// <param name="record">整条记录；fixup 会原地改写，调用方必须拥有这块内存</param>
    /// <param name="maxBytes">卷容量，任何单个属性的占用都不可能超过它</param>
    /// <param name="clusterSize">簇大小；给了就按运行列表里真正落盘的簇算占用（见 ParseBody 的说明），0 表示只信属性头的字段</param>
    internal static MftParseStatus TryParse(Span<byte> record, long number, long maxBytes, MftNames names,
        out MftRecordInfo info, out string? error, MftBoot? boot = null, long clusterSize = 0)
    {
        info = default;
        error = null;
        if (record.Length < 0x30) { error = L.T("记录头太短", "Record header too short", "レコード ヘッダーが短すぎます"); return MftParseStatus.Corrupt; }
        uint signature = U32(record, 0);
        if (signature == 0) return MftParseStatus.Unused;
        if (signature != 0x454C4946) { error = L.T("记录签名不是 FILE", "Record signature isn't FILE", "レコードのシグネチャが FILE ではありません"); return MftParseStatus.Corrupt; }
        ushort headerFlags = U16(record, 0x16);
        if ((headerFlags & 1) == 0) return MftParseStatus.Unused;
        int first = names.Count;
        var result = default(MftRecordInfo);
        try { error = ParseBody(record, headerFlags, maxBytes, names, ref result, boot, clusterSize); }
        // 兜底：偏移检查漏了一处也只是丢一条记录，不能让整个扫描陪葬
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        { error = L.T("解析越界：", "Parse out of bounds: ", "解析が範囲外です：") + ex.GetType().Name; }
        if (error == null) { info = result; return MftParseStatus.Ok; }
        names.Truncate(first);
        return MftParseStatus.Corrupt;
    }

    static string? ParseBody(Span<byte> rec, ushort headerFlags, long maxBytes, MftNames names, ref MftRecordInfo r, MftBoot? boot,
        long clusterSize)
    {
        if (U32(rec, 0x1C) != (uint)rec.Length) return L.T("记录的分配长度与记录大小不符", "The record's allocated length doesn't match the record size", "レコードの割り当て長がレコード サイズと一致しません");
        if (Fixup(rec) is { } fixupError) return fixupError;
        uint usedU = U32(rec, 0x18);
        if (usedU < 0x30 || usedU > (uint)rec.Length) return L.T("记录已用长度无效", "Invalid record used length", "レコードの使用長が無効です");
        int used = (int)usedU, at = U16(rec, 0x14), usaEnd = U16(rec, 4) + U16(rec, 6) * 2;
        if (at < usaEnd || (at & 7) != 0 || at > used - 4) return L.T("属性起点无效", "Invalid attribute start", "属性の開始位置が無効です");
        r.Sequence = U16(rec, 0x10);
        r.BaseReference = U64(rec, 0x20);
        r.IsDirectory = (headerFlags & 2) != 0;
        NodeFlags flags = 0;
        long alloc = 0;
        while (true)
        {
            if (at > used - 4) return L.T("缺失属性结束标记", "Missing attribute end marker", "属性の終了マーカーがありません");
            uint type = U32(rec, at);
            if (type == uint.MaxValue) break;
            if (at > used - 16) return L.T("截断的属性头", "Truncated attribute header", "属性ヘッダーが途中で切れています");
            uint lengthU = U32(rec, at + 4);
            if (lengthU < 24 || (lengthU & 7) != 0 || lengthU > (uint)(used - at)) return L.T("属性长度越界", "Attribute length out of bounds", "属性の長さが範囲外です");
            int length = (int)lengthU;
            var a = rec.Slice(at, length);
            if (a[8] > 1) return L.T("未知属性存储形式", "Unknown attribute storage form", "不明な属性の格納形式です");
            bool nonResident = a[8] == 1;
            int nameChars = a[9], nameAt = U16(a, 10);
            if (nameChars != 0 && (nameAt < (nonResident ? 0x40 : 0x18) || nameAt + nameChars * 2 > length)) return L.T("属性名字越界", "Attribute name out of bounds", "属性名が範囲外です");
            ushort attrFlags = U16(a, 12);
            bool unnamedData = type == 0x80 && nameChars == 0;
            if (!nonResident)
            {
                uint valueLength = U32(a, 0x10);
                int valueAt = U16(a, 0x14);
                if (valueAt < 0x18 || valueAt > length || valueLength > (uint)(length - valueAt)) return L.T("常驻属性的值越界", "Resident attribute value out of bounds", "常駐属性の値が範囲外です");
                var value = a.Slice(valueAt, (int)valueLength);
                switch (type)
                {
                    case 0x10:
                        if (value.Length < 0x24) return L.T("STANDARD_INFORMATION 太短", "STANDARD_INFORMATION too short", "STANDARD_INFORMATION が短すぎます");
                        r.Mtime = I64(value, 8);
                        flags |= ScanNative.Flags(U32(value, 0x20));
                        break;
                    case 0x20:
                        r.HasAttributeList = true;
                        if (boot != null) boot.ListBytes = value.ToArray();
                        break;
                    case 0x30:
                        if (value.Length < 0x42) return L.T("FILE_NAME 太短", "FILE_NAME too short", "FILE_NAME が短すぎます");
                        int nameLength = value[0x40];
                        if (nameLength == 0 || value[0x41] > 3 || 0x42 + nameLength * 2 > value.Length) return L.T("FILE_NAME 名字越界或为空", "FILE_NAME name out of bounds or empty", "FILE_NAME の名前が範囲外か空です");
                        var name = value.Slice(0x42, nameLength * 2);
                        if (MemoryMarshal.Cast<byte, char>(name).IndexOfAny('\\', '/', '\0') >= 0)
                            return L.T("FILE_NAME 包含路径分隔符或 NUL", "FILE_NAME contains a path separator or NUL", "FILE_NAME にパス区切り文字か NUL が含まれています");
                        names.Add(U64(value, 0), value[0x41], name);
                        break;
                    case 0x80 when unnamedData:
                        if (r.HasData) return L.T("重复的未命名 DATA 属性", "Duplicate unnamed DATA attribute", "名前のない DATA 属性が重複しています");
                        r.Size = value.Length;
                        r.HasData = true;
                        break;
                    case 0xC0:
                        if (value.Length < 4) return L.T("REPARSE_POINT 太短", "REPARSE_POINT too short", "REPARSE_POINT が短すぎます");
                        r.ReparseTag = U32(value, 0);
                        r.VolumeMount = r.ReparseTag == ScanNative.TagMountPoint && ScanNative.IsVolumeTarget(value);
                        flags |= NodeFlags.Reparse;
                        break;
                }
            }
            else
            {
                if (length < 0x40) return L.T("非常驻属性头太短", "Non-resident attribute header too short", "非常駐属性のヘッダーが短すぎます");
                // 云盘占位文件的重解析数据最长 4 KB 左右，一条记录装不下，会变成非常驻；标签读不到，但占用要算、标志要打
                if (type == 0xC0) flags |= NodeFlags.Reparse;
                long lowest = I64(a, 0x10), highest = I64(a, 0x18);
                int runAt = U16(a, 0x20);
                if (lowest < 0 || highest < lowest - 1 || runAt < 0x40 || runAt >= length) return L.T("无效的 VCN 或运行列表偏移", "Invalid VCN or run list offset", "VCN またはランリストのオフセットが無効です");
                if (type == 0x20) r.HasAttributeList = true;
                if (lowest == 0)
                {
                    // 总分配字段（0x40）只在压缩或稀疏属性、且运行列表偏移 ≥ 0x48 时存在
                    bool packed = (attrFlags & 0x80FF) != 0 && runAt >= 0x48;
                    long attrAlloc = I64(a, packed ? 0x40 : 0x28), attrSize = I64(a, 0x30);
                    // $BadClus:$Bad 没打稀疏标志，0x28 却写着整个卷的大小、运行列表是一整段空洞——真机 C/D 盘都因此
                    // 凭空多出一个卷。没打标志、这一段又覆盖了整个属性、里面还有空洞时，按真正落盘的簇算才对
                    if (!packed && clusterSize > 0 && highest < long.MaxValue / clusterSize && attrAlloc == (highest + 1) * clusterSize
                        && TryCountClusters(a[runAt..], lowest, highest, out long real, out bool holes) && holes)
                        attrAlloc = real * clusterSize;
                    if (attrAlloc < 0 || attrSize < 0) return L.T("属性大小不能为负", "Attribute size can't be negative", "属性のサイズが負の値です");
                    if (attrAlloc > maxBytes - alloc) return L.T("属性占用超过卷容量", "Attribute allocation exceeds the volume size", "属性の使用容量がボリューム容量を超えています");
                    alloc += attrAlloc;
                    if (unnamedData)
                    {
                        if (r.HasData) return L.T("重复的未命名 DATA 属性", "Duplicate unnamed DATA attribute", "名前のない DATA 属性が重複しています");
                        r.Size = Math.Min(attrSize, MaxLogicalSize);
                        r.HasData = true;
                    }
                    if (boot != null && type == 0x20) boot.ListSize = attrSize;
                }
                if (boot != null && (unnamedData || type == 0x20)
                    && !TryParseRuns(a[runAt..], lowest, highest, type == 0x20 ? boot.ListRuns : boot.DataRuns, out string? runError))
                    return runError;
            }
            at += length;
        }
        // 目录位来自记录头：$STANDARD_INFORMATION 里通常没有 0x10，信它反而会把普通文件认成目录
        flags &= ~NodeFlags.Directory;
        if (r.IsDirectory) flags |= NodeFlags.Directory;
        r.Flags = flags;
        r.Alloc = alloc;
        return null;
    }

    /// 每个 512 字节扇区的最后两个字节被 NTFS 换成了更新序列号，原值存在 USA 里；先全部校验再还原，失败的记录不留半截改动。
    static string? Fixup(Span<byte> rec)
    {
        if (rec.Length < 512 || rec.Length % 512 != 0) return L.T("记录长度不是 512 的整数倍", "Record length isn't a multiple of 512", "レコード長が 512 の倍数ではありません");
        int usa = U16(rec, 4), count = U16(rec, 6);
        if (count != rec.Length / 512 + 1) return L.T("更新序列数组个数不对", "Wrong update sequence array count", "更新シーケンス配列の個数が正しくありません");
        if (usa < 0x2A || usa > rec.Length - count * 2) return L.T("更新序列数组越界", "Update sequence array out of bounds", "更新シーケンス配列が範囲外です");
        ushort usn = U16(rec, usa);
        for (int i = 1; i < count; i++)
            if (U16(rec, i * 512 - 2) != usn) return L.T("扇区 fixup 校验失败", "Sector fixup check failed", "セクターの fixup 検証に失敗しました");
        for (int i = 1; i < count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(rec[(i * 512 - 2)..], U16(rec, usa + i * 2));
        return null;
    }

    /// 解析运行列表，追加到 runs。头字节低 4 位是簇数字段宽度，高 4 位是 LCN 增量字段宽度（0 = 稀疏空洞）；
    /// 增量是有符号的，相对上一个非稀疏段的 LCN（稀疏段不改变这个基准）。
    internal static bool TryParseRuns(ReadOnlySpan<byte> bytes, long lowestVcn, long highestVcn, List<MftRun> runs, out string? error)
    {
        error = null;
        long vcn = lowestVcn, lcn = 0;
        int at = 0;
        while (at < bytes.Length)
        {
            byte header = bytes[at++];
            if (header == 0) break;
            int lengthBytes = header & 0xF, offsetBytes = header >> 4;
            if (lengthBytes is < 1 or > 8 || offsetBytes > 8) { error = L.T("无效的运行列表字段宽度", "Invalid run list field width", "ランリストのフィールド幅が無効です"); return false; }
            if (bytes.Length - at < lengthBytes + offsetBytes) { error = L.T("运行列表被截断", "Run list truncated", "ランリストが途中で切れています"); return false; }
            ulong clusters = 0, delta = 0;
            for (int i = 0; i < lengthBytes; i++) clusters |= (ulong)bytes[at++] << (i * 8);
            for (int i = 0; i < offsetBytes; i++) delta |= (ulong)bytes[at++] << (i * 8);
            if (clusters == 0 || clusters > long.MaxValue || (long)clusters > long.MaxValue - vcn) { error = L.T("无效的运行长度", "Invalid run length", "ラン長が無効です"); return false; }
            long runLcn = -1;
            if (offsetBytes > 0)
            {
                if (offsetBytes < 8 && (delta & (1UL << (offsetBytes * 8 - 1))) != 0) delta |= ulong.MaxValue << (offsetBytes * 8);
                long step = (long)delta;
                if (step > 0 ? lcn > long.MaxValue - step : lcn < long.MinValue - step) { error = L.T("运行 LCN 溢出", "Run LCN overflow", "ランの LCN がオーバーフローしました"); return false; }
                lcn += step;
                if (lcn < 0) { error = L.T("运行指向负数 LCN", "Run points to a negative LCN", "ランが負の LCN を指しています"); return false; }
                runLcn = lcn;
            }
            runs.Add(new MftRun(vcn, runLcn, (long)clusters));
            vcn += (long)clusters;
        }
        if (vcn != highestVcn + 1) { error = L.T("运行列表与 VCN 范围不一致", "Run list doesn't match the VCN range", "ランリストが VCN の範囲と一致しません"); return false; }
        return true;
    }

    /// 和 TryParseRuns 同样的解码，只数非稀疏段的簇，不分配内存；读不懂就返回 false，让调用方退回字段。
    static bool TryCountClusters(ReadOnlySpan<byte> bytes, long lowestVcn, long highestVcn, out long real, out bool holes)
    {
        real = 0;
        holes = false;
        long vcn = lowestVcn;
        int at = 0;
        while (at < bytes.Length)
        {
            byte header = bytes[at++];
            if (header == 0) break;
            int lengthBytes = header & 0xF, offsetBytes = header >> 4;
            if (lengthBytes is < 1 or > 8 || offsetBytes > 8 || bytes.Length - at < lengthBytes + offsetBytes) return false;
            ulong clusters = 0;
            for (int i = 0; i < lengthBytes; i++) clusters |= (ulong)bytes[at + i] << (i * 8);
            at += lengthBytes + offsetBytes;
            if (clusters == 0 || clusters > long.MaxValue || (long)clusters > long.MaxValue - vcn) return false;
            if (offsetBytes > 0) real += (long)clusters;
            else holes = true;
            vcn += (long)clusters;
        }
        return vcn == highestVcn + 1;
    }

    internal static ulong U64(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt64LittleEndian(s[at..]);
    static long I64(ReadOnlySpan<byte> s, int at) => ScanNative.I64(s, at);
    static uint U32(ReadOnlySpan<byte> s, int at) => ScanNative.U32(s, at);
    static ushort U16(ReadOnlySpan<byte> s, int at) => ScanNative.U16(s, at);
}
