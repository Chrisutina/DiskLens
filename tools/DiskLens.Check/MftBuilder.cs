using System.Buffers.Binary;
using System.Text;
using DiskLens.Core;

namespace DiskLens.Check;

/// 用代码拼 FILE 记录字节：布局照 NTFS 3.1 的真实记录（USA 在 0x30、属性从 0x38 起、带正确的 fixup）。
/// 手拼的好处是想让哪个字段坏就能让它坏，不用等真实卷上碰巧出现。
sealed class MftRec
{
    public const uint TypeStd = 0x10, TypeList = 0x20, TypeName = 0x30, TypeData = 0x80, TypeIndexAlloc = 0xA0, TypeBitmap = 0xB0, TypeReparse = 0xC0;

    readonly List<byte[]> _attrs = [];
    readonly int _size;
    ushort _nextId;

    public long Number { get; }
    public ushort Sequence { get; set; }
    public ushort HeaderFlags { get; set; }
    public ulong BaseRef { get; set; }

    public MftRec(long number, bool directory = false, ushort sequence = 1, int size = 1024)
    {
        Number = number;
        Sequence = sequence;
        _size = size;
        HeaderFlags = (ushort)(1 | (directory ? 2 : 0));
    }

    /// MFT 引用：低 48 位记录号，高 16 位序列号。
    public static ulong Ref(long number, ushort sequence = 1) => ((ulong)sequence << 48) | (ulong)number;

    public MftRec Std(long mtime = 0, uint attrs = 0, int length = 0x48)
    {
        var v = new byte[length];
        foreach (int at in new[] { 0, 8, 0x10, 0x18 }) W64(v, at, mtime);
        W32(v, 0x20, attrs);
        return Add(Resident(TypeStd, null, v));
    }

    public MftRec Name(ulong parent, string name, byte ns = 1)
    {
        var v = new byte[0x42 + name.Length * 2];
        W64(v, 0, parent);
        v[0x40] = (byte)name.Length;
        v[0x41] = ns;
        Encoding.Unicode.GetBytes(name, v.AsSpan(0x42));
        return Add(Resident(TypeName, null, v));
    }

    public MftRec DataResident(int length, string? stream = null) => Add(Resident(TypeData, stream, new byte[length]));

    /// 非常驻属性。highest VCN 由 runs 的簇数推出；lowestVcn 非 0 时模拟扩展记录里的后续段。
    public MftRec NonResident(uint type, string? stream, long size, long alloc, (long Lcn, long Len)[] runs,
        ushort flags = 0, long? total = null, long lowestVcn = 0)
    {
        long clusters = runs.Sum(r => r.Len);
        return Add(NonResidentAttr(type, stream, lowestVcn, lowestVcn + clusters - 1, alloc, size, size, flags, total, Runs(runs)));
    }

    public MftRec Data(long size, long alloc, params (long Lcn, long Len)[] runs) => NonResident(TypeData, null, size, alloc, runs);

    public MftRec IndexAlloc(long alloc) => NonResident(TypeIndexAlloc, "$I30", alloc, alloc, [(1000, alloc / 4096)]);

    public MftRec Reparse(uint tag)
    {
        var v = new byte[8];
        W32(v, 0, tag);
        return Add(Resident(TypeReparse, null, v));
    }

    public MftRec ResidentAttr(uint type, string? name, byte[] value, ushort flags = 0) => Add(Resident(type, name, value, flags));

    /// 挂载点标签的 REPARSE_DATA_BUFFER（联接点和卷挂载点同一个布局，差别只在替代名）。真实卷上和 MFT 记录里都是这几个字节。
    public static byte[] MountPoint(string substitute, string print, uint tag = 0xA0000003)
    {
        byte[] sub = Encoding.Unicode.GetBytes(substitute), shown = Encoding.Unicode.GetBytes(print);
        var data = new byte[16 + sub.Length + 2 + shown.Length + 2];
        W32(data, 0, tag);
        W16(data, 4, (ushort)(data.Length - 8));
        W16(data, 10, (ushort)sub.Length);
        W16(data, 12, (ushort)(sub.Length + 2));
        W16(data, 14, (ushort)shown.Length);
        sub.CopyTo(data, 16);
        shown.CopyTo(data, 18 + sub.Length);
        return data;
    }

    public MftRec Raw(byte[] attribute) => Add(attribute);

    MftRec Add(byte[] attribute)
    {
        _attrs.Add(attribute);
        return this;
    }

    public byte[] NonResidentAttr(uint type, string? name, long lowest, long highest, long alloc, long size, long init,
        ushort flags, long? total, byte[] runs)
    {
        int nameChars = name?.Length ?? 0, header = total != null ? 0x48 : 0x40;
        int runAt = Align(header + nameChars * 2), length = Align(runAt + runs.Length);
        var a = new byte[length];
        W32(a, 0, type); W32(a, 4, (uint)length);
        a[8] = 1; a[9] = (byte)nameChars;
        W16(a, 10, (ushort)header); W16(a, 12, flags); W16(a, 14, _nextId++);
        W64(a, 0x10, lowest); W64(a, 0x18, highest); W16(a, 0x20, (ushort)runAt);
        W16(a, 0x22, (ushort)((flags & 0x80FF) != 0 ? 4 : 0));
        W64(a, 0x28, alloc); W64(a, 0x30, size); W64(a, 0x38, init);
        if (total != null) W64(a, 0x40, total.Value);
        if (name != null) Encoding.Unicode.GetBytes(name, a.AsSpan(header));
        runs.CopyTo(a, runAt);
        return a;
    }

    byte[] Resident(uint type, string? name, byte[] value, ushort flags = 0)
    {
        int nameChars = name?.Length ?? 0, valueAt = Align(0x18 + nameChars * 2), length = Align(valueAt + value.Length);
        var a = new byte[length];
        W32(a, 0, type); W32(a, 4, (uint)length);
        a[9] = (byte)nameChars;
        W16(a, 10, 0x18); W16(a, 12, flags); W16(a, 14, _nextId++);
        W32(a, 0x10, (uint)value.Length); W16(a, 0x14, (ushort)valueAt);
        if (name != null) Encoding.Unicode.GetBytes(name, a.AsSpan(0x18));
        value.CopyTo(a, valueAt);
        return a;
    }

    /// 序列化整条记录，并做 fixup：每个扇区末尾两个字节换成更新序列号，原值存进 USA。
    public byte[] Build()
    {
        var r = new byte[_size];
        int count = _size / 512 + 1, usa = 0x30, at = Align(usa + count * 2);
        "FILE"u8.CopyTo(r);
        W16(r, 4, (ushort)usa); W16(r, 6, (ushort)count); W16(r, 0x10, Sequence); W16(r, 0x12, 1);
        W16(r, 0x14, (ushort)at); W16(r, 0x16, HeaderFlags);
        W32(r, 0x1C, (uint)_size); W64(r, 0x20, BaseRef); W16(r, 0x28, _nextId); W32(r, 0x2C, (uint)Number);
        foreach (var a in _attrs)
        {
            a.CopyTo(r, at);
            at += a.Length;
        }
        W32(r, at, 0xFFFFFFFF);
        at += 8;
        if (at > _size) throw new InvalidOperationException($"测试记录 {Number} 装不进 {_size} 字节");
        W32(r, 0x18, (uint)at);
        ushort usn = (ushort)(0x4B00 + Number % 251 + 1);
        W16(r, usa, usn);
        for (int i = 1; i < count; i++)
        {
            int tail = i * 512 - 2;
            W16(r, usa + i * 2, R16(r, tail));
            W16(r, tail, usn);
        }
        return r;
    }

    /// 编码运行列表。Lcn 为 -1 表示稀疏空洞（偏移字段宽度 0）；其余是绝对 LCN，编码成相对上一个非稀疏段的有符号增量。
    public static byte[] Runs(params (long Lcn, long Len)[] runs)
    {
        var bytes = new List<byte>();
        long previous = 0;
        foreach (var (lcn, len) in runs)
        {
            var length = Minimal(len, signed: false);
            if (lcn < 0)
            {
                bytes.Add((byte)length.Length);
                bytes.AddRange(length);
                continue;
            }
            var offset = Minimal(lcn - previous, signed: true);
            previous = lcn;
            bytes.Add((byte)(offset.Length << 4 | length.Length));
            bytes.AddRange(length);
            bytes.AddRange(offset);
        }
        bytes.Add(0);
        return bytes.ToArray();
    }

    static byte[] Minimal(long value, bool signed)
    {
        for (int n = 1; n < 8; n++)
        {
            long limit = 1L << (n * 8 - (signed ? 1 : 0));
            if (signed ? value >= -limit && value < limit : value < limit)
                return BitConverter.GetBytes(value).AsSpan(0, n).ToArray();
        }
        return BitConverter.GetBytes(value);
    }

    public static int Align(int n) => (n + 7) & ~7;
    public static void W16(byte[] b, int at, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), v);
    public static void W32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);
    public static void W64(byte[] b, int at, long v) => BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(at), v);
    public static void W64(byte[] b, int at, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(at), v);
    public static ushort R16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
    public static void W32Span(Span<byte> b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b[at..], v);
    public static void W64Span(Span<byte> b, int at, long v) => BinaryPrimitives.WriteInt64LittleEndian(b[at..], v);
    public static void W64Span(Span<byte> b, int at, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b[at..], v);
}

/// 一块内存做的假卷：布局是 4 簇引导区 + MFT 的若干段（段之间留垃圾簇）。
/// 读取委托强制扇区对齐并限制在镜像范围内——真实卷句柄就是这个脾气，读岔了这里立刻抛出来。
sealed class MftImage
{
    public const int Sector = 512, Cluster = 4096, RecordSize = 1024, PerCluster = Cluster / RecordSize;

    readonly byte[] _bytes;
    public MftGeometry Geometry { get; set; }
    public int ReadCalls { get; private set; }
    public long LargestRead { get; private set; }
    public Action<long, int>? OnRead { get; set; }

    MftImage(byte[] bytes, MftGeometry geometry)
    {
        _bytes = bytes;
        Geometry = geometry;
    }

    public void WriteTo(string path) => File.WriteAllBytes(path, _bytes);

    public MftDeviceRead Read => (offset, buffer) =>
    {
        if (offset < 0 || offset % Sector != 0 || buffer.Length % Sector != 0)
            throw new InvalidOperationException($"读取没有按扇区对齐：偏移 {offset}，长度 {buffer.Length}");
        if (offset + buffer.Length > _bytes.Length) throw new EndOfStreamException($"读取越过镜像末尾：{offset}+{buffer.Length}");
        ReadCalls++;
        LargestRead = Math.Max(LargestRead, buffer.Length);
        OnRead?.Invoke(offset, buffer.Length);
        _bytes.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
    };

    /// <param name="records">记录号 → 记录字节；0 号记录（$MFT）自动生成，除非调用方自己给了</param>
    /// <param name="slots">MFT 总槽位数，必须是 4 的倍数（一簇四条）</param>
    /// <param name="fragmentStarts">每段 MFT 的首条记录号，第一个必须是 0；多段时 0 号记录自动带上 $ATTRIBUTE_LIST</param>
    /// <param name="extensionNumbers">第 2 段起，各段运行列表所在的扩展记录号（必须落在第 1 段里）</param>
    /// <param name="nonResidentList">$ATTRIBUTE_LIST 放在 MFT 之外的一个簇里</param>
    public static MftImage Create(IReadOnlyDictionary<long, byte[]> records, int slots, int[]? fragmentStarts = null,
        long[]? extensionNumbers = null, bool nonResidentList = false)
    {
        fragmentStarts ??= [0];
        extensionNumbers ??= [];
        if (slots % PerCluster != 0 || fragmentStarts[0] != 0) throw new ArgumentException("槽位数或段起点不合规");
        int fragments = fragmentStarts.Length;
        var ends = new int[fragments];
        for (int i = 0; i < fragments; i++) ends[i] = i + 1 < fragments ? fragmentStarts[i + 1] : slots;
        var clusters = new long[fragments];
        var lcn = new long[fragments];
        long next = 4;
        for (int i = 0; i < fragments; i++)
        {
            if ((ends[i] - fragmentStarts[i]) % PerCluster != 0) throw new ArgumentException("段边界必须按簇对齐");
            clusters[i] = (ends[i] - fragmentStarts[i]) / PerCluster;
            lcn[i] = next;
            next += clusters[i] + 3;   // 段间留 3 个垃圾簇：读岔了立刻读到 0xEE
        }
        long listLcn = next++;
        long imageClusters = next + 1;
        var bytes = new byte[imageClusters * Cluster];
        Array.Fill(bytes, (byte)0xEE);
        for (int i = 0; i < fragments; i++) bytes.AsSpan((int)(lcn[i] * Cluster), (int)(clusters[i] * Cluster)).Clear();

        long totalClusters = clusters.Sum(), mftSize = (long)slots * RecordSize;
        var extra = new Dictionary<long, byte[]>(records);
        if (!extra.ContainsKey(0))
        {
            var zero = new MftRec(0).Std(0, 0x6).Name(MftRec.Ref(5, 5), "$MFT", 3);
            zero.Raw(zero.NonResidentAttr(MftRec.TypeData, null, 0, clusters[0] - 1, totalClusters * Cluster, mftSize, mftSize, 0, null,
                MftRec.Runs((lcn[0], clusters[0]))));
            if (fragments > 1)
            {
                var list = new List<byte>();
                long vcn = 0;
                for (int i = 0; i < fragments; i++)
                {
                    var entry = new byte[0x20];
                    MftRec.W32(entry, 0, MftRec.TypeData); MftRec.W16(entry, 4, 0x20);
                    MftRec.W64(entry, 8, vcn); MftRec.W64(entry, 0x10, MftRec.Ref(i == 0 ? 0 : extensionNumbers[i - 1]));
                    list.AddRange(entry);
                    vcn += clusters[i];
                }
                if (nonResidentList)
                {
                    zero.Raw(zero.NonResidentAttr(MftRec.TypeList, null, 0, 0, Cluster, list.Count, list.Count, 0, null,
                        MftRec.Runs((listLcn, 1))));
                    list.CopyTo(bytes, (int)(listLcn * Cluster));
                }
                else zero.ResidentAttr(MftRec.TypeList, null, list.ToArray());
                long startVcn = clusters[0];
                for (int i = 1; i < fragments; i++)
                {
                    var ext = new MftRec(extensionNumbers[i - 1]) { BaseRef = MftRec.Ref(0) };
                    ext.Raw(ext.NonResidentAttr(MftRec.TypeData, null, startVcn, startVcn + clusters[i] - 1, 0, 0, 0, 0, null,
                        MftRec.Runs((lcn[i], clusters[i]))));
                    extra[extensionNumbers[i - 1]] = ext.Build();
                    startVcn += clusters[i];
                }
            }
            extra[0] = zero.Build();
        }
        foreach (var (number, record) in extra)
        {
            int f = Array.FindLastIndex(fragmentStarts, s => s <= number);
            if (number >= slots || record.Length != RecordSize) throw new ArgumentException($"记录 {number} 超出槽位或长度不对");
            record.CopyTo(bytes, (int)(lcn[f] * Cluster + (number - fragmentStarts[f]) * RecordSize));
        }
        // 总簇数给一个很大的值：解析器用它给单个属性的占用封顶，测试里的大文件（几个 GB）不能被误杀
        var geometry = new MftGeometry(Sector, Cluster, RecordSize, mftSize, lcn[0], 1L << 30, 1L << 30);
        return new MftImage(bytes, geometry);
    }
}
