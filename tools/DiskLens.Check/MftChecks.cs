using System.Text;
using DiskLens.Core;

namespace DiskLens.Check;

// MFT 深度扫描自检（DESIGN §4.4、§4.5、§11）。
//   dotnet run --project tools/DiskLens.Check -c Release -- mft [--perf 记录数] [--volume X:] [--compare X:]
// 前面几组全部用构造的记录字节，不碰真实卷，不需要管理员；--volume / --compare 需要提权，没提权就打印跳过。
static partial class MftChecks
{
    public static void Run(CheckContext ctx)
    {
        RunLists(ctx);
        ParseRecords(ctx);
        CorruptRecords(ctx);
        VolumeData(ctx);
        DeviceReads(ctx);
        FileBacked(ctx);
        TreeScenario(ctx);
        FragmentedMft(ctx);
        BrokenVolumes(ctx);
        Pipeline(ctx);
        Synthetic(ctx);
        RealVolume(ctx);
        Compare(ctx);
    }

    const ulong Root = 5UL << 48 | 5;   // 根目录的引用：记录 5，序列号 5

    static ulong Ref(long number, ushort sequence = 1) => MftRec.Ref(number, sequence);

    sealed record Parsed(MftParseStatus Status, MftRecordInfo Info, MftNames Names, string? Error);

    static Parsed Parse(byte[] record, long number = 30, long maxBytes = long.MaxValue)
    {
        var names = new MftNames();
        var status = MftRecordParser.TryParse(record, number, maxBytes, names, out var info, out string? error);
        return new Parsed(status, info, names, error);
    }

    static string NameOf(Parsed p, int i) => p.Names.Name(i).ToString();

    /// 第 index 个属性在记录里的偏移（构造的记录都是 0x38 起头）。
    static int AttrAt(byte[] record, int index)
    {
        int at = MftRec.R16(record, 0x14);
        for (int i = 0; i < index; i++) at += (int)BitConverter.ToUInt32(record, at + 4);
        return at;
    }

    // ---------------- 运行列表 ----------------

    static void RunLists(CheckContext ctx)
    {
        static List<MftRun>? Try(byte[] bytes, long lowest, long highest, out string? error)
        {
            var runs = new List<MftRun>();
            return MftRecordParser.TryParseRuns(bytes, lowest, highest, runs, out error) ? runs : null;
        }

        var one = Try(MftRec.Runs((100, 5)), 0, 4, out _);
        ctx.Equal(1, one?.Count ?? -1, "单段运行列表");
        if (one is { Count: 1 }) ctx.Equal(new MftRun(0, 100, 5), one[0], "单段的 VCN/LCN/簇数");

        // 负的 LCN 增量：第二段回到更靠前的位置
        var back = Try(MftRec.Runs((1000, 10), (500, 5)), 0, 14, out _);
        ctx.Equal(2, back?.Count ?? -1, "负增量运行列表段数");
        if (back is { Count: 2 })
        {
            ctx.Equal(new MftRun(0, 1000, 10), back[0], "负增量：第一段");
            ctx.Equal(new MftRun(10, 500, 5), back[1], "负增量：第二段按有符号增量回退到 LCN 500");
        }

        // 稀疏段（偏移字节数为 0）：不改变上一个 LCN 基准，下一段的增量仍然相对稀疏段之前的 LCN
        var sparse = Try(MftRec.Runs((100, 4), (-1, 8), (200, 4)), 0, 15, out _);
        ctx.Equal(3, sparse?.Count ?? -1, "稀疏运行列表段数");
        if (sparse is { Count: 3 })
        {
            ctx.Equal(new MftRun(4, -1, 8), sparse[1], "稀疏段 LCN 记为 -1");
            ctx.Equal(new MftRun(12, 200, 4), sparse[2], "稀疏段之后的 LCN 相对稀疏段之前的那段");
        }
        var trailing = Try(MftRec.Runs((7, 2), (-1, 1000)), 0, 1001, out _);
        ctx.True(trailing is { Count: 2 } && trailing[1].Lcn == -1 && trailing[1].Clusters == 1000, "结尾的稀疏空洞");

        var multi = Try(MftRec.Runs((100, 3), (300, 2), (250, 7), (9000, 1)), 0, 12, out _);
        ctx.Equal(4, multi?.Count ?? -1, "多段运行列表");
        if (multi is { Count: 4 })
            ctx.True(multi.Select(r => r.Lcn).SequenceEqual([100L, 300, 250, 9000]), "多段 LCN 逐段累加增量");

        // 非 0 的起始 VCN（扩展记录里的后续段）
        var later = Try(MftRec.Runs((50, 4)), 16, 19, out _);
        ctx.True(later is { Count: 1 } && later[0].Vcn == 16, "起始 VCN 非 0 的段");

        // 8 字节宽的有符号增量和 8 字节宽的长度
        var wide = new List<byte> { 0x88 };
        wide.AddRange(BitConverter.GetBytes(3L));
        wide.AddRange(BitConverter.GetBytes(1L << 40));
        wide.Add(0x88);
        wide.AddRange(BitConverter.GetBytes(2L));
        wide.AddRange(BitConverter.GetBytes(-(1L << 39)));
        wide.Add(0);
        var wideRuns = Try([.. wide], 0, 4, out _);
        ctx.True(wideRuns is { Count: 2 } && wideRuns[0].Lcn == 1L << 40 && wideRuns[1].Lcn == 1L << 39, "8 字节宽的字段");

        ctx.True(Try([0], 0, -1, out _) is { Count: 0 }, "空属性的运行列表只有结束符");
        ctx.True(Try([0x11, 0x05, 0x64], 0, 4, out _) is { Count: 1 }, "缺结束符但 VCN 范围吻合时照收");

        // 坏列表：每一种都必须报错，不能崩
        void Bad(string what, byte[] bytes, long lowest, long highest)
        {
            var runs = new List<MftRun>();
            ctx.True(!MftRecordParser.TryParseRuns(bytes, lowest, highest, runs, out string? e) && !string.IsNullOrEmpty(e), what);
        }
        Bad("截断：声明了偏移字节却没有", [0x21, 0x05], 0, 4);
        Bad("截断：长度字段不全", [0x31, 0x05, 0x00], 0, 4);
        Bad("簇数为 0", [0x11, 0x00, 0x64, 0], 0, -1);
        Bad("长度字段宽度为 0", [0x10, 0x64, 0], 0, 4);
        Bad("长度字段宽度超过 8", [0x19, 1, 2, 3, 4, 5, 6, 7, 8, 9, 0x64, 0], 0, 4);
        Bad("偏移字段宽度超过 8", [0x91, 0x05, 1, 2, 3, 4, 5, 6, 7, 8, 9, 0], 0, 4);
        Bad("增量把 LCN 减成负数", [0x11, 0x01, 0x0A, 0x11, 0x01, 0xF0, 0], 0, 1);
        Bad("VCN 范围对不上（少）", MftRec.Runs((100, 3)), 0, 9);
        Bad("VCN 范围对不上（多）", MftRec.Runs((100, 3)), 0, 1);
        Bad("簇数溢出 long", [0x18, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x11, 0x64, 0], 0, 4);
    }

    // ---------------- 单条记录 ----------------

    static void ParseRecords(CheckContext ctx)
    {
        // 常驻 $DATA：大小是值长度，不占簇
        const long mtime = 133_800_000_000_000_000;
        var p = Parse(new MftRec(30).Std(mtime, 0x2 | 0x20).Name(Ref(5, 5), "a.txt", 3).DataResident(100).Build());
        ctx.Equal(MftParseStatus.Ok, p.Status, "常驻记录解析成功");
        ctx.Equal(100L, p.Info.Size, "常驻 $DATA 的逻辑大小 = 值长度");
        ctx.Equal(0L, p.Info.Alloc, "常驻 $DATA 不占簇");
        ctx.True(p.Info.HasData, "常驻 $DATA 标记 HasData");
        ctx.Equal(mtime, p.Info.Mtime, "修改时间取 $STANDARD_INFORMATION 的 0x08");
        ctx.Equal(NodeFlags.Hidden, p.Info.Flags, "文件属性映射（隐藏）");
        ctx.True(!p.Info.IsDirectory && p.Info.BaseReference == 0 && p.Info.Sequence == 1, "基记录、非目录、序列号 1");
        ctx.Equal(1, p.Names.Count, "一个 $FILE_NAME");
        if (p.Names.Count == 1)
        {
            ctx.Equal("a.txt", NameOf(p, 0), "名字");
            ctx.Equal(Ref(5, 5), p.Names.Links[0].Parent, "父引用保留序列号");
            ctx.Equal((byte)3, p.Names.Links[0].Namespace, "命名空间");
        }

        // 非常驻 $DATA：大小取实际大小，占用取分配大小
        p = Parse(new MftRec(30).Std().Name(Ref(5, 5), "b.bin").Data(10000, 12288, (100, 3)).Build());
        ctx.True(p.Status == MftParseStatus.Ok && p.Info.Size == 10000 && p.Info.Alloc == 12288, "非常驻：size=实际大小，alloc=分配大小");

        // 压缩/稀疏：总分配字段（0x40）才是真实占用，0x28 是含空洞的虚拟分配
        p = Parse(new MftRec(30).Std().Name(Ref(5, 5), "c.bin").NonResident(MftRec.TypeData, null, 100000, 65536, [(100, 16)], 0x0001, total: 8192).Build());
        ctx.Equal(8192L, p.Info.Alloc, "压缩文件取总分配字段");
        ctx.Equal(100000L, p.Info.Size, "压缩文件逻辑大小是未压缩长度");
        p = Parse(new MftRec(30).Std().Name(Ref(5, 5), "s.bin").NonResident(MftRec.TypeData, null, 1L << 40, 1L << 40, [(100, 1), (-1, 255)], 0x8000, total: 4096).Build());
        ctx.Equal(4096L, p.Info.Alloc, "稀疏文件取总分配字段，不把空洞算成占用");
        ctx.Equal(1L << 40, p.Info.Size, "稀疏文件逻辑大小保留");
        p = Parse(new MftRec(30).Std().Name(Ref(5, 5), "n.bin").NonResident(MftRec.TypeData, null, 100000, 65536, [(100, 16)], 0x0001).Build());
        ctx.Equal(65536L, p.Info.Alloc, "带压缩标志但没有总分配字段（运行列表偏移 0x40）时退回分配大小");
        p = Parse(new MftRec(30).Std().Name(Ref(5, 5), "u.bin").NonResident(MftRec.TypeData, null, 100000, 65536, [(100, 16)], 0, total: 777).Build());
        ctx.Equal(65536L, p.Info.Alloc, "没有压缩/稀疏标志时不看 0x40 处的值");
        p = Parse(new MftRec(30).Std().Name(Ref(5, 5), "e.bin").NonResident(MftRec.TypeData, null, 100000, 65536, [(100, 16)], 0x4000, total: 777).Build());
        ctx.Equal(65536L, p.Info.Alloc, "只有加密标志不算压缩/稀疏");
        p = Parse(new MftRec(30).Std().Name(Ref(5, 5), "z.bin").Data(0, 0).Build());
        ctx.True(p.Status == MftParseStatus.Ok && p.Info.Size == 0 && p.Info.Alloc == 0, "空的非常驻 $DATA（零簇）");

        // 命名流（ADS）：占用计入，逻辑大小不动
        p = Parse(new MftRec(30).Std().Name(Ref(5, 5), "d.exe").DataResident(10)
            .DataResident(26, "Zone.Identifier")
            .NonResident(MftRec.TypeData, "big", 5000, 8192, [(200, 2)]).Build());
        ctx.Equal(10L, p.Info.Size, "命名流不改变未命名流的逻辑大小");
        ctx.Equal(8192L, p.Info.Alloc, "非常驻命名流计入占用，常驻命名流不占簇");
        p = Parse(new MftRec(30).Std().Name(Ref(5, 5), "only-ads").DataResident(5, "x").Build());
        ctx.True(!p.Info.HasData && p.Info.Size == 0, "只有命名流的文件没有 $DATA 大小");
        p = Parse(new MftRec(30).Std().Name(Ref(5, 5), "wof.bin").Data(0, 0)
            .NonResident(MftRec.TypeData, "WofCompressedData", 40000, 40960, [(300, 10)]).Build());
        ctx.Equal(40960L, p.Info.Alloc, "WOF 压缩数据在命名流里，必须计入");

        // 多个 $FILE_NAME：Win32+DOS、两个硬链接
        p = Parse(new MftRec(30).Std().Name(Ref(31), "kernel32.dll").Name(Ref(31), "KERNEL~1.DLL", 2).Name(Ref(32), "k32.dll", 3).DataResident(1).Build());
        ctx.Equal(3, p.Names.Count, "解析器如实交出所有名字，去重留给建树");
        ctx.True(p.Names.Links.Take(3).Select(l => l.Namespace).SequenceEqual(new byte[] { 1, 2, 3 }), "各名字的命名空间");
        ctx.True(p.Names.Links.Take(3).Select(l => l.Parent).SequenceEqual([Ref(31), Ref(31), Ref(32)]), "各名字的父引用");

        // 目录：$INDEX_ALLOCATION 和非常驻 $BITMAP 都算目录自己的占用
        p = Parse(new MftRec(31, directory: true).Std(0, 0x10).Name(Ref(5, 5), "dir").IndexAlloc(8192)
            .NonResident(MftRec.TypeBitmap, "$I30", 8, 4096, [(300, 1)]).Build());
        ctx.True(p.Info.IsDirectory && (p.Info.Flags & NodeFlags.Directory) != 0, "目录位来自记录头");
        ctx.Equal(12288L, p.Info.Alloc, "目录自身占用 = $INDEX_ALLOCATION + 非常驻 $BITMAP");
        ctx.Equal(0L, p.Info.Size, "目录没有 $DATA");
        p = Parse(new MftRec(32).Std(0, 0x10).Name(Ref(5, 5), "notdir").DataResident(1).Build());
        ctx.True(!p.Info.IsDirectory && (p.Info.Flags & NodeFlags.Directory) == 0, "SI 里的目录位不能把普通文件变成目录");

        // 属性映射、重解析点、属性列表
        p = Parse(new MftRec(33).Std(0, 0x2 | 0x4 | 0x800 | 0x200 | 0x4000 | 0x1000 | 0x400).Name(Ref(5, 5), "f").Reparse(0xA000000C).Build());
        ctx.Equal(NodeFlags.Hidden | NodeFlags.System | NodeFlags.Compressed | NodeFlags.Sparse | NodeFlags.Encrypted | NodeFlags.Cloud | NodeFlags.Reparse,
            p.Info.Flags, "文件属性全映射");
        ctx.Equal(0xA000000Cu, p.Info.ReparseTag, "重解析标签");
        p = Parse(new MftRec(34, true).Std().Name(Ref(5, 5), "junction").Reparse(0xA0000003).Build());
        ctx.True((p.Info.ReparseTag & 0x20000000) != 0 && (p.Info.Flags & NodeFlags.Reparse) != 0, "名称代理标签（挂载点）");
        ctx.True(!p.Info.VolumeMount, "只有标签、没有替代名的挂载点不当成卷挂载点");
        // 联接点和卷挂载点是同一个标签，只看替代名
        MftParseStatus Mount(byte[] data, out bool volume)
        {
            var parsed = Parse(new MftRec(34, true).Std().Name(Ref(5, 5), "m").ResidentAttr(MftRec.TypeReparse, null, data).Build());
            volume = parsed.Info.VolumeMount;
            return parsed.Status;
        }
        ctx.True(Mount(MftRec.MountPoint(@"\??\Volume{5a1e0000-0000-0000-0000-000000000001}\", ""), out bool vol) == MftParseStatus.Ok && vol,
            @"替代名是 \??\Volume{…} 的是卷挂载点");
        ctx.True(Mount(MftRec.MountPoint(@"\??\C:\Users", @"C:\Users"), out vol) == MftParseStatus.Ok && !vol, "替代名是普通路径的是联接点");
        ctx.True(Mount(MftRec.MountPoint(@"\??\Volume{5a1e0000-0000-0000-0000-000000000001}\", "", tag: 0xA000000C), out vol) == MftParseStatus.Ok && !vol,
            "符号链接标签不看替代名");
        var lying = MftRec.MountPoint(@"\??\Volume{5a1e0000-0000-0000-0000-000000000001}\", "");
        MftRec.W16(lying, 10, 0x7FF0);
        ctx.True(Mount(lying, out vol) == MftParseStatus.Ok && !vol, "替代名长度越界：不当成卷挂载点，记录照常解析");
        // 云盘占位文件的重解析数据可以长到一条记录装不下，变成非常驻：不能当损坏扔掉，占用要算
        p = Parse(new MftRec(34).Std(0, 0x400 | 0x400000).Name(Ref(5, 5), "placeholder.docx")
            .NonResident(MftRec.TypeReparse, null, 5000, 8192, [(77, 2)]).Data(1000000, 0, (-1, 245)).Build());
        ctx.Equal(MftParseStatus.Ok, p.Status, "非常驻的 $REPARSE_POINT 不算损坏");
        ctx.True((p.Info.Flags & (NodeFlags.Reparse | NodeFlags.Cloud)) == (NodeFlags.Reparse | NodeFlags.Cloud) && p.Info.Alloc == 8192 && p.Info.Size == 1000000,
            "非常驻重解析数据：打 Reparse/Cloud 标志、占用计入、逻辑大小来自 $DATA");
        p = Parse(new MftRec(35).Std().Name(Ref(5, 5), "g").ResidentAttr(MftRec.TypeList, null, new byte[32]).Build());
        ctx.True(p.Info.HasAttributeList, "识别 $ATTRIBUTE_LIST");

        // 扩展记录：基引用非 0；它自己的属性照常解析
        p = Parse(new MftRec(48) { BaseRef = Ref(47, 3) }.Name(Ref(31), "second", 1).Data(70000, 73728, (7000, 18)).Build(), 48);
        ctx.True(p.Status == MftParseStatus.Ok && p.Info.BaseReference == Ref(47, 3), "扩展记录报告基引用（含序列号）");
        ctx.True(p.Info.Alloc == 73728 && p.Names.Count == 1, "扩展记录的占用和名字照常解析");

        // 未在用 / 全零
        ctx.Equal(MftParseStatus.Unused, Parse(new byte[1024]).Status, "全零记录是未使用，不算错");
        ctx.Equal(MftParseStatus.Unused, Parse(new MftRec(36) { HeaderFlags = 0 }.Std().Name(Ref(5, 5), "freed").Build()).Status, "未在用记录");
        ctx.Equal(MftParseStatus.Unused, Parse(new MftRec(36) { HeaderFlags = 2 }.Std().Build()).Status, "只有目录位、没有在用位也是未使用");
        var freed = Parse(new MftRec(36) { HeaderFlags = 0 }.Std().Name(Ref(5, 5), "freed").Build());
        ctx.Equal(0, freed.Names.Count, "未在用的记录不产出名字");

        // fixup：扇区末尾的更新序列号要还原成 USA 里存的原值
        var rec = new MftRec(37).Std().Name(Ref(5, 5), "fix").DataResident(5).Build();
        MftRec.W16(rec, 0x32, 0xBEEF);
        MftRec.W16(rec, 0x34, 0xCAFE);
        p = Parse(rec);
        ctx.Equal(MftParseStatus.Ok, p.Status, "fixup 正常的记录");
        ctx.True(MftRec.R16(rec, 510) == 0xBEEF && MftRec.R16(rec, 1022) == 0xCAFE, "fixup 把两个扇区末尾还原成 USA 里的值");
        // 4 KB 记录（4Kn 盘）：USA 有 9 项
        var big = new MftRec(38, size: 4096).Std().Name(Ref(5, 5), "big-record").DataResident(2000).Build();
        ctx.Equal(MftParseStatus.Ok, Parse(big).Status, "4096 字节的记录");

        p = Parse(new MftRec(39).Std().Name(Ref(5, 5), "a_b_中文.txt", 0).DataResident(1).Build());
        ctx.True(p.Status == MftParseStatus.Ok && p.Names.Count == 1 && NameOf(p, 0) == "a_b_中文.txt", "合法 POSIX 名字原样保留");

        // 逻辑大小封顶；占用不封顶（超出卷容量是损坏）
        p = Parse(new MftRec(40).Std().Name(Ref(5, 5), "huge").NonResident(MftRec.TypeData, null, long.MaxValue / 2, 4096, [(1, 1)]).Build());
        ctx.Equal(MftRecordParser.MaxLogicalSize, p.Info.Size, "稀疏文件声明的天文数字逻辑大小被封顶");
    }

    static void CorruptRecords(CheckContext ctx)
    {
        void Corrupt(string what, byte[] record, long maxBytes = long.MaxValue)
        {
            // 名字池里先放一个别人的名字：失败的记录只能回滚自己追加的，不能动别人的
            var names = new MftNames();
            names.Add(Ref(5), 1, Encoding.Unicode.GetBytes("keep"));
            var status = MftRecordParser.TryParse(record, 30, maxBytes, names, out _, out string? error);
            ctx.True(status == MftParseStatus.Corrupt && !string.IsNullOrEmpty(error), $"{what} → 损坏");
            ctx.True(names.Count == 1 && names.CharCount == 4 && names.Name(0).SequenceEqual("keep"), $"{what}：名字池回滚到原样");
        }
        byte[] Good() => new MftRec(30).Std().Name(Ref(5, 5), "ok.txt", 3).Name(Ref(5, 5), "second", 1).Data(5000, 8192, (100, 2)).Build();

        var r = Good(); r[0] = (byte)'B'; r[1] = (byte)'A'; r[2] = (byte)'A'; r[3] = (byte)'D';
        Corrupt("签名 BAAD", r);
        r = Good(); r[510] ^= 1;
        Corrupt("fixup 不匹配（第一个扇区）", r);
        r = Good(); r[1022] ^= 0x40;
        Corrupt("fixup 不匹配（第二个扇区）", r);
        r = Good(); MftRec.W16(r, 6, 2);
        Corrupt("USA 个数不对", r);
        r = Good(); MftRec.W16(r, 4, 0x3F0);
        Corrupt("USA 偏移越界", r);
        r = Good(); MftRec.W16(r, 4, 8);
        Corrupt("USA 压在记录头上", r);
        r = Good(); MftRec.W32(r, 0x1C, 4096);
        Corrupt("记录自称的分配长度和卷参数不符", r);
        r = Good(); MftRec.W32(r, 0x18, 5000);
        Corrupt("已用长度超过记录", r);
        r = Good(); MftRec.W32(r, 0x18, 0x10);
        Corrupt("已用长度小于记录头", r);
        r = Good(); MftRec.W16(r, 0x14, 0x3A);
        Corrupt("属性起点未按 8 对齐", r);
        r = Good(); MftRec.W16(r, 0x14, 0x20);
        Corrupt("属性起点压在 USA 上", r);

        int a1 = AttrAt(Good(), 1);   // 第一个 $FILE_NAME
        r = Good(); MftRec.W32(r, a1 + 4, 0x7FFFFFF8);
        Corrupt("属性长度越界（巨大）", r);
        r = Good(); MftRec.W32(r, a1 + 4, 0xFFFFFFF8);
        Corrupt("属性长度高位置位", r);
        r = Good(); MftRec.W32(r, a1 + 4, 20);
        Corrupt("属性长度未按 8 对齐", r);
        r = Good(); MftRec.W32(r, a1 + 4, 16);
        Corrupt("属性长度小于最小头", r);
        r = Good(); MftRec.W32(r, a1 + 4, 0);
        Corrupt("属性长度为 0（会死循环的那种）", r);
        r = Good(); MftRec.W32(r, a1 + 4, (uint)(MftRec.R16(r, 0x18) | 0x400));
        Corrupt("属性长度压过记录已用长度", r);
        r = Good(); r[a1 + 8] = 7;
        Corrupt("未知的属性存储形式", r);
        r = Good(); MftRec.W32(r, a1 + 0x10, 0x10000);
        Corrupt("常驻值长度越界", r);
        r = Good(); MftRec.W16(r, a1 + 0x14, 0x7000);
        Corrupt("常驻值偏移越界", r);
        r = Good(); MftRec.W16(r, a1 + 0x14, 0x10);
        Corrupt("常驻值偏移落在属性头里", r);
        r = Good(); r[a1 + 9] = 200;
        Corrupt("属性名字长度越界", r);
        int nameValue = a1 + MftRec.R16(r, a1 + 0x14);
        r = Good(); r[nameValue + 0x40] = 250;
        Corrupt("FILE_NAME 声明的名字长度超过值", r);
        r = Good(); r[nameValue + 0x40] = 0;
        Corrupt("FILE_NAME 名字为空", r);
        r = Good(); r[nameValue + 0x41] = 7;
        Corrupt("FILE_NAME 命名空间非法", r);
        r = Good(); MftRec.W32(r, a1 + 0x10, 0x20);
        Corrupt("FILE_NAME 值太短", r);

        foreach (byte ns in new byte[] { 0, 1, 2, 3 })
            foreach (char invalid in new[] { '\\', '/', '\0' })
                Corrupt($"FILE_NAME 命名空间 {ns} 含 U+{(int)invalid:X4}，拒收且不生成 a_b.txt",
                    new MftRec(30).Std().Name(Root, "already-added.txt").Name(Root, $"a{invalid}b.txt", ns).DataResident(1).Build());

        int d = AttrAt(Good(), 3);   // 非常驻 $DATA
        r = Good(); MftRec.W16(r, d + 0x20, 0x4000);
        Corrupt("运行列表偏移越界", r);
        r = Good(); MftRec.W16(r, d + 0x20, 0x20);
        Corrupt("运行列表偏移落在属性头里", r);
        r = Good(); MftRec.W64(r, d + 0x10, -5L);
        Corrupt("起始 VCN 为负", r);
        r = Good(); MftRec.W64(r, d + 0x18, -9L);
        Corrupt("最后 VCN 比起始 VCN 还小两格", r);
        // 普通记录的运行列表用不上，解析器不展开它（省时间）；$MFT 自己的运行列表由 Locate 展开，见 BrokenVolumes
        r = Good(); MftRec.W64(r, d + 0x28, -1L);
        Corrupt("分配大小为负", r);
        r = Good(); MftRec.W64(r, d + 0x30, -1L);
        Corrupt("实际大小为负", r);
        Corrupt("单个属性的占用超过卷容量", Good(), maxBytes: 4096);
        Corrupt("重复的未命名 $DATA", new MftRec(30).Std().Name(Ref(5, 5), "x").DataResident(1).Data(10, 4096, (1, 1)).Build());
        Corrupt("重复的常驻未命名 $DATA", new MftRec(30).Std().Name(Ref(5, 5), "x").DataResident(1).DataResident(2).Build());
        Corrupt("STANDARD_INFORMATION 太短", new MftRec(30).ResidentAttr(MftRec.TypeStd, null, new byte[0x10]).Name(Ref(5, 5), "x").Build());
        Corrupt("REPARSE_POINT 太短", new MftRec(30).Std().Name(Ref(5, 5), "x").ResidentAttr(MftRec.TypeReparse, null, new byte[2]).Build());
        r = Good(); int end = MftRec.R16(r, 0x18) - 8; MftRec.W32(r, end, 0);
        Corrupt("缺失属性结束标记", r);
        r = Good(); MftRec.W32(r, 0x18, (uint)(MftRec.R16(r, 0x18) - 5));
        Corrupt("已用长度卡在结束标记中间", r);
        var odd = new byte[1000];
        "FILE"u8.CopyTo(odd);
        odd[0x16] = 1;
        MftRec.W32(odd, 0x1C, 1000);
        Corrupt("记录长度不是 512 的倍数", odd);

        // 不管坏成什么样都不许抛异常、不许死循环：拿一条好记录逐字节翻转，全部喂给解析器
        var seed = new MftRec(30).Std(1, 6).Name(Ref(5, 5), "fuzz.txt").Data(5000, 8192, (100, 2)).DataResident(7, "ads").Build();
        int outcomes = 0, leaks = 0;
        var rng = new Random(12345);
        for (int round = 0; round < 3000; round++)
        {
            var copy = (byte[])seed.Clone();
            int flips = 1 + rng.Next(4);
            for (int k = 0; k < flips; k++) copy[rng.Next(0x38, 0x140)] = (byte)rng.Next(256);
            try
            {
                var status = MftRecordParser.TryParse(copy, 30, 1L << 40, new MftNames(), out _, out string? error);
                // 解析器外层有兜底 catch：只要走到了它，就说明哪处偏移检查漏了，必须当失败报
                if (status == MftParseStatus.Corrupt && error!.StartsWith("解析越界", StringComparison.Ordinal)) leaks++;
                outcomes++;
            }
            catch (Exception ex) { ctx.Fail($"随机破坏的记录让解析器抛出了 {ex.GetType().Name}：{ex.Message}"); break; }
        }
        ctx.Equal(3000, outcomes, "3000 条随机破坏的记录全部平稳返回");
        ctx.Equal(0, leaks, "随机破坏的记录没有一条漏过偏移检查、靠兜底 catch 收场");
    }

    // ---------------- 卷参数 ----------------

    static void VolumeData(CheckContext ctx)
    {
        // 按 NTFS_VOLUME_DATA_BUFFER + NTFS_EXTENDED_VOLUME_DATA 的布局手拼
        var info = new byte[128];
        MftRec.W64(info, 0x10, 1000000L); MftRec.W64(info, 0x18, 250000L);
        MftRec.W32(info, 0x28, 512); MftRec.W32(info, 0x2C, 4096); MftRec.W32(info, 0x30, 1024);
        MftRec.W64(info, 0x38, 1024L * 5000); MftRec.W64(info, 0x40, 786432L);
        MftRec.W32(info, 0x60, 32); MftRec.W16(info, 0x64, 3); MftRec.W16(info, 0x66, 1);
        var g = MftGeometry.FromVolumeData(info);
        ctx.Equal(new MftGeometry(512, 4096, 1024, 1024L * 5000, 786432L, 1000000L, 250000L), g, "卷参数各字段偏移");
        ctx.Equal(1000000L * 4096, g.VolumeBytes, "卷容量 = 总簇数 × 簇大小");

        void Throws<T>(string what, Action<byte[]> damage, int length = 128) where T : Exception
        {
            var copy = (byte[])info.Clone();
            damage(copy);
            try { MftGeometry.FromVolumeData(copy.AsSpan(0, length)); ctx.Fail($"{what}：应当抛 {typeof(T).Name}，实际没抛"); }
            catch (T) { ctx.True(true, what); }
            catch (Exception ex) { ctx.Fail($"{what}：应当抛 {typeof(T).Name}，实际 {ex.GetType().Name}"); }
        }
        Throws<NotSupportedException>("NTFS 版本不是 3.x", b => MftRec.W16(b, 0x64, 1));
        Throws<InvalidDataException>("缓冲区太短读不到版本", _ => { }, length: 96);
        Throws<InvalidDataException>("扇区大小过小", b => MftRec.W32(b, 0x28, 100));
        Throws<InvalidDataException>("簇不是扇区的整数倍", b => MftRec.W32(b, 0x2C, 700));
        Throws<InvalidDataException>("记录大小不是 512 的倍数", b => MftRec.W32(b, 0x30, 1000));
        Throws<InvalidDataException>("MFT 有效长度不是记录的整数倍", b => MftRec.W64(b, 0x38, 1024L * 5000 + 5));
        Throws<InvalidDataException>("MFT 起始簇为负", b => MftRec.W64(b, 0x40, -1L));
        Throws<InvalidDataException>("空闲簇超过总簇", b => MftRec.W64(b, 0x18, 2000000L));
    }
}
