using Microsoft.Win32.SafeHandles;

namespace DiskLens.Core;

/// 读 offset 起的 buffer.Length 个字节；要么读满，要么抛。
internal delegate void MftDeviceRead(long offset, Span<byte> buffer);

/// FSCTL_GET_NTFS_VOLUME_DATA 里扫描要用的那几项。
internal readonly record struct MftGeometry(
    int BytesPerSector, int BytesPerCluster, int RecordSize, long MftValidLength, long MftStartLcn, long TotalClusters, long FreeClusters)
{
    internal long VolumeBytes => TotalClusters * BytesPerCluster;

    /// NTFS_VOLUME_DATA_BUFFER 之后紧跟 NTFS_EXTENDED_VOLUME_DATA（Win8+），版本号在后者里。
    internal static MftGeometry FromVolumeData(ReadOnlySpan<byte> info)
    {
        if (info.Length < 104) throw new InvalidDataException(L.T("NTFS 卷参数缓冲区太短，读不到版本号", "The NTFS volume data buffer is too short to read the version", "NTFS ボリュームのパラメーターのバッファーが短すぎて、バージョンを読み取れません"));
        if (ScanNative.U16(info, 100) != 3) throw new NotSupportedException(L.T("仅支持经过解析器验证的 NTFS 3.x 格式", "Only NTFS 3.x, which the parser was verified against, is supported", "パーサーで検証済みの NTFS 3.x 形式のみ対応しています"));
        int sector = checked((int)ScanNative.U32(info, 0x28)), cluster = checked((int)ScanNative.U32(info, 0x2C));
        int record = checked((int)ScanNative.U32(info, 0x30));
        long length = ScanNative.I64(info, 0x38), startLcn = ScanNative.I64(info, 0x40);
        long total = ScanNative.I64(info, 0x10), free = ScanNative.I64(info, 0x18);
        // 卷参数不合常理就别硬读：错的簇大小会让所有运行列表都读到别处去
        if (sector is < 512 or > 65536 || cluster < sector || cluster % sector != 0 || record < 512 || record > 65536
            || record % 512 != 0 || startLcn < 0 || length < record || length % record != 0 || total <= 0 || free < 0 || free > total)
            throw new InvalidDataException(L.T("NTFS 卷参数超出可验证范围", "The NTFS volume parameters are outside the range that can be verified", "NTFS ボリュームのパラメーターが検証できる範囲を超えています"));
        return new MftGeometry(sector, cluster, record, length, startLcn, total, free);
    }
}

/// 卷句柄只认扇区对齐的偏移和长度。常规路径本来就对齐（簇、记录都是扇区的整数倍），
/// 不对齐时绕道一块对齐的临时缓冲区，当作安全网，而不是热路径。
internal sealed class MftDevice(MftDeviceRead read, int sector)
{
    internal void Read(long offset, Span<byte> destination)
    {
        if (offset < 0) throw new InvalidDataException(L.T("负数物理偏移", "Negative physical offset", "物理オフセットが負の値です"));
        if (destination.IsEmpty) return;
        long end = checked(offset + destination.Length);
        if (offset % sector == 0 && destination.Length % sector == 0) { read(offset, destination); return; }
        long from = offset / sector * sector, to = (end + sector - 1) / sector * sector;
        byte[] aligned = new byte[checked((int)(to - from))];
        read(from, aligned);
        aligned.AsSpan((int)(offset - from), destination.Length).CopyTo(destination);
    }

    internal static MftDeviceRead FromHandle(SafeFileHandle handle) => (offset, buffer) =>
    {
        while (!buffer.IsEmpty)
        {
            int n = RandomAccess.Read(handle, buffer, offset);
            if (n <= 0) throw new EndOfStreamException(L.T("读取卷时提前到达末尾", "Reached the end of the volume early while reading", "ボリュームの読み取り中に予期せず末尾に達しました"));
            offset += n;
            buffer = buffer[n..];
        }
    };
}

/// 把 MFT 自己的逻辑偏移按运行列表映射到卷上的物理位置，读取可以跨区段。
internal sealed class MftStream
{
    readonly MftDevice _device;
    readonly MftRun[] _runs;
    readonly int _cluster;

    /// 运行列表覆盖的字节数。
    internal long Length { get; }

    /// <param name="requiredLength">运行列表至少要盖住这么多字节</param>
    internal MftStream(MftDevice device, IEnumerable<MftRun> runs, MftGeometry g, long requiredLength)
    {
        _device = device;
        _cluster = g.BytesPerCluster;
        _runs = runs.OrderBy(r => r.Vcn).ToArray();
        long next = 0;
        foreach (var run in _runs)
        {
            // MFT 不可能是稀疏的，也不可能有缺口或重叠；见到这些说明 0 号记录读岔了，直接放弃，由上层改走常规扫描
            if (run.Vcn != next || run.Lcn < 0 || run.Clusters <= 0 || run.Lcn > g.TotalClusters - run.Clusters)
                throw new InvalidDataException(L.T("MFT 运行列表有缺口、重叠、稀疏段或越过卷边界", "The MFT run list has gaps, overlaps or sparse runs, or crosses the volume boundary", "MFT のランリストに欠け・重なり・スパース区間があるか、ボリュームの境界を越えています"));
            next = checked(next + run.Clusters);
        }
        Length = checked(next * _cluster);
        if (Length < requiredLength) throw new InvalidDataException(L.T("MFT 运行列表未覆盖有效数据长度", "The MFT run list doesn't cover the valid data length", "MFT のランリストが有効なデータ長をカバーしていません"));
    }

    internal void Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset > Length - destination.Length) throw new EndOfStreamException(L.T("MFT 逻辑偏移超出运行列表", "The MFT logical offset is beyond the run list", "MFT の論理オフセットがランリストの範囲外です"));
        int lo = 0, hi = _runs.Length - 1;
        long vcn = offset / _cluster;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_runs[mid].Vcn <= vcn) lo = mid; else hi = mid - 1;
        }
        for (int index = lo; !destination.IsEmpty; index++)
        {
            var run = _runs[index];
            long within = offset - run.Vcn * _cluster;
            int length = (int)Math.Min(destination.Length, run.Clusters * _cluster - within);
            _device.Read(run.Lcn * _cluster + within, destination[..length]);
            destination = destination[length..];
            offset += length;
        }
    }
}

internal static class MftVolume
{
    const int MaxListBytes = 64 * 1024 * 1024;

    /// 读 0 号记录，解析 $MFT 自己的 $DATA 运行列表。碎片化严重时一个记录装不下，
    /// 剩下的段在扩展记录里，靠 $ATTRIBUTE_LIST 找：扩展记录总在已映射的前段里，所以按 VCN 顺序一段段往后接。
    internal static MftStream Locate(MftDevice device, MftGeometry g)
    {
        var record = new byte[g.RecordSize];
        device.Read(checked(g.MftStartLcn * g.BytesPerCluster), record);
        var boot = new MftBoot();
        var status = MftRecordParser.TryParse(record, 0, g.VolumeBytes, new MftNames(), out var info, out string? error, boot);
        if (status != MftParseStatus.Ok || info.BaseReference != 0 || !info.HasData)
            throw new InvalidDataException(L.T("MFT 0 号记录无效：", "MFT record 0 is invalid: ", "MFT のレコード 0 が無効です：") + (error ?? L.T("不是在用的、带 DATA 的基记录", "it isn't an in-use base record with DATA", "DATA を持つ使用中のベース レコードではありません")));
        var runs = new List<MftRun>(boot.DataRuns);
        if (info.HasAttributeList) AppendListedSegments(device, g, boot, runs);
        return new MftStream(device, runs, g, g.MftValidLength);
    }

    static void AppendListedSegments(MftDevice device, MftGeometry g, MftBoot boot, List<MftRun> runs)
    {
        byte[] list = boot.ListBytes ?? ReadNonResidentList(device, g, boot);
        var entries = new List<(long StartVcn, long Record)>();
        for (int at = 0; at + 0x1A <= list.Length;)
        {
            int length = ScanNative.U16(list, at + 4);
            if (length < 0x1A || at + length > list.Length) break;   // 零填充或损坏的尾巴：到此为止
            if (ScanNative.U32(list, at) == 0x80 && list[at + 6] == 0)
                entries.Add((ScanNative.I64(list, at + 8), (long)(MftRecordParser.U64(list, at + 0x10) & MftRecordParser.ReferenceMask)));
            at += length;
        }
        entries.Sort((a, b) => a.StartVcn.CompareTo(b.StartVcn));
        var seen = new HashSet<long> { 0 };
        foreach (var (_, number) in entries)
        {
            if (!seen.Add(number)) continue;
            var mapped = new MftStream(device, runs, g, 0);
            var record = new byte[g.RecordSize];
            try { mapped.Read(checked(number * g.RecordSize), record); }
            catch (EndOfStreamException) { throw new InvalidDataException(L.T($"MFT 扩展记录 {number} 位于尚未映射的区域", $"MFT extension record {number} is in an area not mapped yet", $"MFT 拡張レコード {number} はまだマップされていない領域にあります")); }
            var extra = new MftBoot();
            var status = MftRecordParser.TryParse(record, number, g.VolumeBytes, new MftNames(), out var info, out string? error, extra);
            if (status != MftParseStatus.Ok || (info.BaseReference & MftRecordParser.ReferenceMask) != 0)
                throw new InvalidDataException(L.T($"MFT 扩展记录 {number} 无效：", $"MFT extension record {number} is invalid: ", $"MFT 拡張レコード {number} が無効です：") + (error ?? L.T("它的基记录不是 0 号记录", "its base record isn't record 0", "ベース レコードがレコード 0 ではありません")));
            runs.AddRange(extra.DataRuns);
        }
    }

    static byte[] ReadNonResidentList(MftDevice device, MftGeometry g, MftBoot boot)
    {
        if (boot.ListSize <= 0 || boot.ListSize > MaxListBytes) throw new InvalidDataException(L.T("$ATTRIBUTE_LIST 长度异常", "Unexpected $ATTRIBUTE_LIST length", "$ATTRIBUTE_LIST の長さが異常です"));
        var stream = new MftStream(device, boot.ListRuns, g, boot.ListSize);
        byte[] list = new byte[boot.ListSize];
        stream.Read(0, list);
        return list;
    }
}
