using DiskLens.Core;

namespace DiskLens.Check;

// 整条流水线（定位 MFT → 分块读 → 并行解析 → 合并扩展记录 → 建树 → 核对硬链接）跑在内存假卷上。
static partial class MftChecks
{
    const string VolRoot = @"X:\";   // 假卷的根路径，不对应任何真实盘符
    const long MB = 1024 * 1024;

    static FileTree ScanImage(MftImage image, ScanCounters counters, out MftStats stats, CancellationToken ct = default) =>
        MftScanner.ScanDevice(image.Read, image.Geometry, VolRoot, counters, ct, out stats);

    static MftRec Meta(long number, string name, bool directory = false) =>
        new MftRec(number, directory, (ushort)number).Std(0, 0x6).Name(Root, name, 3);

    // ---------------- 建树 ----------------

    static void TreeScenario(CheckContext ctx)
    {
        const long mtimeA = 133_811_111_111_111_111;
        var recs = new Dictionary<long, byte[]>();
        void Put(MftRec r) => recs[r.Number] = r.Build();

        // 根目录和 NTFS 元文件
        Put(new MftRec(5, true, 5).Std(0, 0x6).Name(Root, ".", 3).IndexAlloc(4096));
        Put(Meta(1, "$MFTMirr").Data(4096, 4096, (2, 1)));
        Put(Meta(2, "$LogFile").Data(64 * MB, 64 * MB, (1000, 16384)));
        Put(Meta(3, "$Volume").DataResident(0));
        Put(Meta(4, "$AttrDef").Data(2560, 4096, (3, 1)));
        Put(Meta(6, "$Bitmap").Data(8192, 8192, (4, 2)));
        Put(Meta(7, "$Boot").Data(8192, 8192, (0, 2)));
        // $BadClus:$Bad：逻辑和 0x28 都写着整个卷的大小，运行列表是一整段空洞，而且真机上它**没打稀疏标志**——
        // 只信字段就会凭空多出一个卷（C/D 盘实测都多了正好一个卷容量），必须按落盘的簇算
        Put(Meta(8, "$BadClus").DataResident(0).NonResident(MftRec.TypeData, "$Bad", 1L << 40, 1L << 40, [(-1, (1L << 40) / 4096)], 0, total: 0));
        Put(Meta(9, "$Secure").NonResident(MftRec.TypeData, "$SDS", 262144, 262144, [(5000, 64)]));
        Put(Meta(10, "$UpCase").Data(131072, 131072, (6000, 32)));
        Put(new MftRec(11, true, 11).Std(0, 0x6).Name(Root, "$Extend", 3));
        for (int n = 12; n <= 15; n++)   // 保留记录：在用、没有名字；14 号故意带一簇数据，看它是不是被安置进元数据目录而不是算孤立
            Put(n == 14 ? new MftRec(n).Std().Data(100, 4096, (9800, 1)) : new MftRec(n).Std().DataResident(0));
        const long usnLogical = 10L << 30;
        Put(new MftRec(24, false, 24).Std(0, 0x6).Name(Ref(11, 11), "$UsnJrnl", 3)
            .NonResident(MftRec.TypeData, "$J", usnLogical, usnLogical, [(-1, usnLogical / 4096 - 256), (7000, 256)], 0x8000, total: 1048576)
            .DataResident(16, "$Max"));
        Put(new MftRec(25, true, 25).Std(0, 0x6).Name(Ref(11, 11), "$RmMetadata", 3));
        Put(new MftRec(26, false, 26).Std(0, 0x6).Name(Ref(25, 25), "$Repair", 3).Data(4096, 4096, (7100, 1)));

        // 普通目录和文件
        Put(new MftRec(30, true).Std().Name(Root, "Windows", 3));
        Put(new MftRec(31, true).Std().Name(Ref(30), "System32", 3).IndexAlloc(8192));
        Put(new MftRec(32, true).Std().Name(Ref(30), "WinSxS", 3));
        // 同一个文件在 System32 和 WinSxS 各有一个硬链接，各自还带一个 DOS 短名
        Put(new MftRec(33).Std().Name(Ref(31), "kernel32.dll", 1).Name(Ref(31), "KERNEL~1.DLL", 2)
            .Name(Ref(32), "kernel32.dll", 1).Name(Ref(32), "KERNEL~2.DLL", 2).Data(100000, 102400, (5000, 25)));
        Put(new MftRec(34).Std(mtimeA).Name(Root, "a.txt", 3).DataResident(100));
        Put(new MftRec(35).Std().Name(Root, "long file name.txt", 1).Name(Root, "LONGFI~1.TXT", 2).DataResident(300));
        Put(new MftRec(36, true).Std().Name(Root, "Users", 3));
        Put(new MftRec(37).Std().Name(Ref(36), "notes.md", 3).Data(5000, 8192, (6000, 2)));
        Put(new MftRec(38, true).Std(0, 0x400).Name(Root, "link", 3).Reparse(0xA0000003));
        // 卷挂载点：同一个标签，替代名是 \??\Volume{…}
        Put(new MftRec(57, true).Std(0, 0x400).Name(Root, "mnt", 3)
            .ResidentAttr(MftRec.TypeReparse, null, MftRec.MountPoint(@"\??\Volume{6f1d0c5e-0000-0000-0000-100000000000}\", "")));
        Put(new MftRec(39).Std(0, 0x800).Name(Root, "packed.bin", 3).NonResident(MftRec.TypeData, null, MB, MB, [(8000, 256)], 0x0001, total: 65536));

        // 父链成环（A 的父是 B，B 的父是 A）、父序列号过期、父记录未使用、父记录是个文件、没有名字
        Put(new MftRec(40, true).Std().Name(Ref(41), "A", 3));
        Put(new MftRec(41, true).Std().Name(Ref(40), "B", 3));
        Put(new MftRec(42).Std().Name(Ref(40), "x.bin", 3).Data(5000, 8192, (9000, 2)));
        Put(new MftRec(43).Std().Name(Ref(30, 9), "stale.dat", 3).Data(1000, 4096, (9100, 1)));
        Put(new MftRec(44).Std().Name(Ref(50), "ghost.bin", 3).Data(10, 4096, (9200, 1)));
        Put(new MftRec(45).Std().Name(Ref(34), "underfile.txt", 3).DataResident(10));
        Put(new MftRec(46).Std().Data(100, 4096, (9300, 1)));
        Put(new MftRec(56, true).Std().Name(Ref(56), "selfref", 3));

        // 扩展记录：47 的 $DATA、命名流和第二个硬链接都在 48 里；49 指向序列号过期的基记录，必须整条丢掉
        Put(new MftRec(47).Std().Name(Root, "multi.bin", 3).ResidentAttr(MftRec.TypeList, null, new byte[0x30]));
        Put(new MftRec(48) { BaseRef = Ref(47) }.Name(Ref(31), "multi2.bin", 3).Data(70000, 73728, (9400, 18))
            .NonResident(MftRec.TypeData, "extra", 4000, 4096, [(9500, 1)]));
        Put(new MftRec(49) { BaseRef = Ref(34, 7) }.Data(999999, 4096, (9600, 1)));

        // 损坏的记录（fixup 不匹配）、空目录、只有 DOS 名的文件、重名、同目录两个硬链接
        var damaged = new MftRec(51).Std().Name(Root, "bad.bin", 3).DataResident(5).Build();
        damaged[510] ^= 1;
        recs[51] = damaged;
        Put(new MftRec(52, true).Std().Name(Root, "Empty", 3));
        Put(new MftRec(53).Std().Name(Root, "DOSONL~1.TXT", 2).DataResident(10));
        Put(new MftRec(54).Std().Name(Root, "dup.txt", 0).Name(Root, "dup.txt", 1).DataResident(10));
        Put(new MftRec(55).Std().Name(Root, "h1.txt", 3).Name(Root, "h2.txt", 3).Data(2000, 4096, (9700, 1)));

        var image = MftImage.Create(recs, 64);
        var counters = new ScanCounters();
        var tree = ScanImage(image, counters, out var stats);
        // 对账：不 Seal 之前每个节点的值都是自身值，非重复节点之和就是整卷占用
        long ownSum = 0;
        for (int i = 0; i < tree.Count; i++) if (!tree.Has(i, NodeFlags.HardLinkDup)) ownSum += tree.Alloc(i);
        tree.Seal();

        // 每条记录自己的占用，逐项列出：这是整张表的标准答案
        long expected = 4096                    // 根目录 $INDEX_ALLOCATION
            + 64 * 1024                         // $MFT：16 簇（运行列表盖住的整段）
            + 4096 + 64 * MB + 4096 + 8192 + 8192 + 0 /* $BadClus 稀疏 */ + 262144 + 131072
            + 1048576 /* $UsnJrnl:$J 总分配 */ + 4096 /* $Repair */
            + 8192 /* System32 的 $INDEX_ALLOCATION */ + 102400 /* kernel32.dll 只计一份 */
            + 8192 /* notes.md */ + 65536 /* packed.bin 压缩后 */
            + 4096 /* 记录-14 */ + 8192 /* x.bin */ + 4096 + 4096 + 4096 /* stale/ghost/记录-46 */
            + 77824 /* multi.bin = 扩展记录里 73728 + 命名流 4096 */ + 4096 /* h1/h2 只计一份 */;
        ctx.Equal(expected, tree.Alloc(0), "整棵树的占用 = 每条记录自身占用之和（硬链接只计一份）");
        ctx.Equal(expected, ownSum, "Seal 前自身值之和相同");
        ctx.Equal(expected, stats.Counted, "扫描器自己的对账数字");
        ctx.Equal(2, stats.Corrupt, "跳过的记录数：fixup 坏的 51 号 + 基记录过期的 49 号");

        int Find(string path)
        {
            int id = tree.FindByPath(VolRoot + path);
            ctx.True(id > 0, $"能按路径找到 {path}");
            return id;
        }
        bool Missing(string path) => tree.FindByPath(VolRoot + path) == FileTree.NoNode;

        // 硬链接：System32 排在 WinSxS 前面，计入 System32
        int k1 = Find(@"Windows\System32\kernel32.dll"), k2 = Find(@"Windows\WinSxS\kernel32.dll");
        if (k1 > 0 && k2 > 0)
        {
            ctx.True(!tree.Has(k1, NodeFlags.HardLinkDup) && tree.Has(k2, NodeFlags.HardLinkDup), "kernel32.dll 计入 System32，WinSxS 那份标重复");
            ctx.True(tree.Alloc(k1) == 102400 && tree.Alloc(k2) == 102400 && tree.Size(k2) == 100000, "重复链接保留自己的大小用于显示");
            ctx.Equal(2, tree.HardLinkGroup(k1)?.Length ?? 0, "kernel32.dll 的硬链接组只有两个成员（DOS 短名不是链接）");
            ctx.Equal(102400L + 8192, tree.Alloc(Find(@"Windows\System32")), "System32 = 自身索引 + kernel32.dll");
            ctx.Equal(0L, tree.Alloc(Find(@"Windows\WinSxS")), "WinSxS 里的重复链接对父目录贡献 0");
        }
        ctx.True(Missing(@"Windows\System32\KERNEL~1.DLL") && Missing(@"Windows\WinSxS\KERNEL~2.DLL"), "有长名时 DOS 短名被丢掉");
        int multi = Find("multi.bin"), multi2 = Find(@"Windows\System32\multi2.bin");
        if (multi > 0 && multi2 > 0)
        {
            ctx.True(tree.Size(multi) == 70000 && tree.Alloc(multi) == 77824, "扩展记录里的 $DATA 和命名流并入基记录");
            ctx.True(!tree.Has(multi, NodeFlags.HardLinkDup) && tree.Has(multi2, NodeFlags.HardLinkDup), "扩展记录里的第二个名字是硬链接，路径较大的那个标重复");
        }
        int h1 = Find("h1.txt"), h2 = Find("h2.txt");
        if (h1 > 0 && h2 > 0) ctx.True(!tree.Has(h1, NodeFlags.HardLinkDup) && tree.Has(h2, NodeFlags.HardLinkDup), "同一目录里的两个硬链接");
        ctx.Equal(3L, counters.HardLinkDups, "硬链接重复数：kernel32、multi、h1/h2 各一个");
        ctx.Equal(102400L + 77824 + 4096, counters.HardLinkSavedBytes, "硬链接省下的字节数");
        int a = Find("a.txt");
        if (a > 0) ctx.True(tree.Size(a) == 100 && tree.MTime(a) == mtimeA, "常驻小文件的大小和修改时间");
        ctx.True(Find("long file name.txt") > 0 && Missing("LONGFI~1.TXT"), "Win32 长名保留、DOS 短名丢弃");
        ctx.True(Find("DOSONL~1.TXT") > 0, "只有 DOS 名的记录保留这个名字");
        int dups = 0;
        foreach (int c in tree.Children(0)) if (tree.GetName(c) == "dup.txt") dups++;
        ctx.Equal(1, dups, "同父同名的两个 $FILE_NAME 只算一个链接");
        int packed = Find("packed.bin");
        if (packed > 0) ctx.True(tree.Alloc(packed) == 65536 && tree.Size(packed) == MB && tree.Has(packed, NodeFlags.Compressed), "压缩文件取总分配，保留压缩标志");
        int link = Find("link");
        if (link > 0) ctx.True(tree.IsDir(link) && tree.Has(link, NodeFlags.Reparse) && tree.Children(link).Length == 0, "junction 只是一个重解析目录节点");
        ctx.Equal(1L, counters.LinksSkipped, "联接点算跳过的链接");
        ctx.Equal(1L, counters.MountPointsSkipped, "替代名是卷的挂载点单独算卷挂载点");
        int mnt = Find("mnt");
        if (mnt > 0) ctx.True(tree.IsDir(mnt) && tree.Has(mnt, NodeFlags.Reparse) && tree.Children(mnt).Length == 0, "卷挂载点只是一个重解析目录节点");
        ctx.True(Find("Empty") > 0 && tree.IsDir(Find("Empty")), "空目录");

        // 元文件：0–15 号（除根目录）和 $Extend 整棵子树，统一在虚拟目录下
        int meta = Find("$NTFS 元数据");
        if (meta > 0)
        {
            ctx.True(tree.Flags(meta) == (NodeFlags.Directory | NodeFlags.Virtual | NodeFlags.Metafile) && tree.Parent(meta) == 0, "虚拟目录的标志和位置");
            foreach (string path in new[] { "$MFT", "$MFTMirr", "$LogFile", "$Volume", "$AttrDef", "$Bitmap", "$Boot", "$BadClus", "$Secure", "$UpCase", "$Extend", @"$Extend\$UsnJrnl", @"$Extend\$RmMetadata\$Repair" })
            {
                int id = Find(path);
                if (id > 0) ctx.True(tree.Has(id, NodeFlags.Metafile), $"{path} 带 Metafile 标志");
            }
            int mft = Find("$MFT"), usn = Find(@"$Extend\$UsnJrnl"), bad = Find("$BadClus");
            if (mft > 0) ctx.True(tree.Parent(mft) == meta && tree.Alloc(mft) == 64 * 1024, "$MFT 挂在虚拟目录下，占用 = 运行列表覆盖的簇");
            if (usn > 0) ctx.True(tree.Alloc(usn) == 1048576 && tree.Parent(tree.Parent(usn)) == meta, "$UsnJrnl 的稀疏流取总分配；$Extend 子树都在虚拟目录下");
            if (bad > 0) ctx.Equal(0L, tree.Alloc(bad), "$BadClus:$Bad 是稀疏流，真实占用 0");
        }

        // 孤立记录：挂到另一个虚拟目录，大小照常计入，带 Metafile 以便拦删除
        int orphan = Find("$孤立记录");
        if (orphan > 0)
        {
            ctx.True(tree.Flags(orphan) == (NodeFlags.Directory | NodeFlags.Virtual | NodeFlags.Metafile) && tree.Parent(orphan) == 0, "孤立记录目录的标志和位置");
            foreach (string path in new[] { "selfref", "A", @"A\B", @"A\x.bin", "stale.dat", "ghost.bin", "underfile.txt", "记录-46" })
            {
                int id = Find(@"$孤立记录\" + path);
                if (id > 0) ctx.True(tree.Has(id, NodeFlags.Metafile), $"孤立记录 {path} 带 Metafile 标志");
            }
            ctx.Equal(8192L + 4096 * 3, tree.Alloc(orphan), "孤立记录的大小照常计入（x.bin、stale、ghost、记录-46）");
        }
        ctx.True(Missing("记录-12") && Missing("记录-13") && Missing("记录-15"), "12、13、15 号保留记录（在用、没有名字、没有内容）不产生节点");
        int reserved = tree.FindByPath(VolRoot + "记录-14");
        ctx.True(reserved > 0 && tree.Parent(reserved) == meta && tree.Has(reserved, NodeFlags.Metafile) && tree.Alloc(reserved) == 4096, "14 号保留记录有内容，进「$NTFS 元数据」而不是孤立记录");
        ctx.True(Missing("bad.bin"), "fixup 坏掉的记录被跳过");
        ctx.True(tree.Size(Find("a.txt")) == 100 && tree.Alloc(Find("a.txt")) == 0, "扩展记录基记录过期时，它的内容没有并进别人的记录");

        ctx.Equal(30L, tree.Files(0), "文件条目数（硬链接每个链接算一条）");
        ctx.Equal(12L, tree.Dirs(0), "子孙目录数（虚拟目录不算）");
        ctx.Equal(6, stats.Orphans, "孤立子树数：selfref、环里的 A、stale、ghost、underfile、记录-46（14 号不算）");
        ctx.Equal(2L + 6, counters.Errors, "Errors = 跳过的损坏记录 2 条 + 孤立子树 6 个");
        var errors = counters.Page(ScanIssueKind.Error, 0, 100);
        ctx.True(errors.Total == 8 && errors.Recorded == 8, "错误清单：总数和记下的条数都是 8");
        ctx.Equal(2, errors.Items.Count(e => e.Path == VolRoot + "$MFT"), "损坏记录记在 $MFT 名下，两条");
        ctx.True(errors.Items.Any(e => e.Path == VolRoot + "$MFT" && e.Reason.Contains("51")) && errors.Items.Any(e => e.Reason.Contains("49")),
            "损坏记录的原因里写了记录号");
        foreach (string lost in new[] { "selfref", "A", "stale.dat", "ghost.bin", "underfile.txt", "记录-46" })
            ctx.True(errors.Items.Any(e => e.Path == VolRoot + @"$孤立记录\" + lost && e.Reason.Contains("$孤立记录")), $"孤立子树 {lost} 进了错误清单");
        ctx.True(counters.Page(ScanIssueKind.MountPoint, 0, 10) is (1, 1, [{ Path: var mp }]) && mp == VolRoot + "mnt", "卷挂载点清单");
        ctx.True(counters.Page(ScanIssueKind.Link, 0, 10) is (1, 1, [{ Path: var lp, Reason: var lr }]) && lp == VolRoot + "link" && lr.Contains("联接点"),
            "链接清单");
        ctx.Equal(0L, counters.Page(ScanIssueKind.Denied, 0, 10).Total, "MFT 扫描不看目录权限，没有拒绝访问");
        ctx.True(counters.Warnings.Any(w => w.Contains("51")) && counters.Warnings.Any(w => w.Contains("49")), "被跳过的记录写进了 Warn");
        ctx.True(counters.Warnings.Any(w => w.Contains("孤立记录")), "孤立记录有一句提醒");
        ctx.Equal(stats.InUse, (int)(counters.Files + counters.Dirs), "进度里的文件数+目录数 = 在用的基记录数");
        ctx.Equal(expected - 77824, counters.Bytes, "进度字节数按各记录自己的占用累加（扩展记录的占用不进进度）");
        ctx.Equal(counters.Bytes, counters.SnapshotCategories().Sum(), "各类别之和 = 总字节数");
        ctx.Equal("links", counters.Phase, "最后的阶段是 links");
        ctx.Equal(1.0, counters.Percent, "读完之后进度停在 1");

        // 树的结构不变量：子节点下标大于父节点下标
        bool ordered = true;
        for (int i = 1; i < tree.Count; i++) ordered &= tree.Parent(i) < i;
        ctx.True(ordered, "子节点下标 > 父节点下标");

        // 对账提示：卷已用和树里统计的差超过 0.5% 才警告，并写清两个数
        var quiet = new ScanCounters();
        var consistent = image.Geometry with { FreeClusters = image.Geometry.TotalClusters - (expected + 4095) / 4096 };
        MftScanner.ScanDevice(image.Read, consistent, VolRoot, quiet, default, out _);
        ctx.True(!quiet.Warnings.Any(w => w.Contains("对账")), "占用对得上时不发对账警告");
        var loud = new ScanCounters();
        var off = image.Geometry with { FreeClusters = image.Geometry.TotalClusters - (expected + 4095) / 4096 * 2 };
        MftScanner.ScanDevice(image.Read, off, VolRoot, loud, default, out _);
        ctx.True(loud.Warnings.Any(w => w.Contains("对账") && w.Contains("0.5%") && w.Contains(((off.TotalClusters - off.FreeClusters) * 4096).ToString("N0"))),
            "差额超过 0.5% 发对账警告，并写明卷已用字节数");
    }

    // ---------------- 设备读取与对齐 ----------------

    static void DeviceReads(CheckContext ctx)
    {
        // 4Kn 盘：扇区 4096。读取委托只认 4096 对齐，其余一律抛
        var disk = new byte[64 * 1024];
        new Random(7).NextBytes(disk);
        var calls = new List<(long Offset, int Length)>();
        MftDeviceRead strict = (offset, buffer) =>
        {
            if (offset % 4096 != 0 || buffer.Length % 4096 != 0) throw new InvalidOperationException($"没对齐：{offset}+{buffer.Length}");
            calls.Add((offset, buffer.Length));
            disk.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
        };
        var device = new MftDevice(strict, 4096);
        var aligned = new byte[8192];
        device.Read(4096, aligned);
        ctx.True(aligned.AsSpan().SequenceEqual(disk.AsSpan(4096, 8192)) && calls.Count == 1 && calls[0] == (4096, 8192), "对齐的读取原样透传");
        var odd = new byte[3000];
        device.Read(5000, odd);
        ctx.True(odd.AsSpan().SequenceEqual(disk.AsSpan(5000, 3000)), "不对齐的读取绕道对齐缓冲区，内容正确");
        ctx.True(calls.Skip(1).All(c => c.Offset % 4096 == 0 && c.Length % 4096 == 0), "绕道时实际下发的读取都是对齐的");
        var spanning = new byte[5000];
        device.Read(4000, spanning);
        ctx.True(spanning.AsSpan().SequenceEqual(disk.AsSpan(4000, 5000)), "跨扇区边界的不对齐读取");
        device.Read(0, []);
        try { device.Read(-1, new byte[4096]); ctx.Fail("负偏移应当抛 InvalidDataException"); }
        catch (InvalidDataException) { ctx.True(true, "负偏移被拒绝"); }
    }

    /// 把镜像写成沙箱里的文件，用和真实卷一样的方式（P/Invoke CreateFileW 拿句柄 + RandomAccess.Read）读它：
    /// 没有管理员也能验证句柄读取这一层（循环读满、偏移推进、外部创建的同步句柄），只差卷设备本身的脾气。
    static void FileBacked(CheckContext ctx)
    {
        const long slots = 4096 * 2 + 64;
        var recs = new Dictionary<long, byte[]>();
        recs[5] = new MftRec(5, true, 5).Std(0, 6).Name(Root, ".", 3).Build();
        recs[100] = new MftRec(100, true).Std().Name(Root, "d", 3).Build();
        foreach (long n in new long[] { 101, 4096, 8190, slots - 1 })
            recs[n] = new MftRec(n).Std().Name(Ref(100), $"f{n}.bin", 3).Data(10, 4096 * (n % 5 + 1), (500, 1)).Build();
        var image = MftImage.Create(recs, (int)slots);
        string path = Path.Combine(ctx.Sandbox(), "image.bin");
        image.WriteTo(path);
        using var handle = ScanNative.CreateFileW(path, 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        ctx.True(!handle.IsInvalid, "用 CreateFileW 打开镜像文件");
        if (handle.IsInvalid) return;
        var counters = new ScanCounters();
        var tree = MftScanner.ScanDevice(MftDevice.FromHandle(handle), image.Geometry, VolRoot, counters, default, out var stats);
        tree.Seal();
        var reference = ScanImage(image, new ScanCounters(), out _);
        reference.Seal();
        ctx.Equal(0, stats.Corrupt, "按句柄读取没有损坏记录");
        ctx.True(tree.Count == reference.Count && tree.Alloc(0) == reference.Alloc(0), "按句柄读取和按内存读取结果一致");
        foreach (long n in new long[] { 101, 4096, 8190, slots - 1 })
        {
            int id = tree.FindByPath($@"{VolRoot}d\f{n}.bin");
            ctx.True(id > 0 && tree.Alloc(id) == 4096 * (n % 5 + 1), $"{n} 号记录经句柄读到");
        }
        // 读过末尾要报错，不能无限循环
        var device = MftDevice.FromHandle(handle);
        try { device.Invoke(image.Geometry.VolumeBytes > 0 ? new FileInfo(path).Length - 1024 : 0, new byte[4096]); ctx.Fail("越过文件末尾的读取应当抛 EndOfStreamException"); }
        catch (EndOfStreamException) { ctx.True(true, "越过末尾的读取抛 EndOfStreamException"); }
    }

    // ---------------- 碎片化 MFT ----------------

    static void FragmentedMft(CheckContext ctx)
    {
        foreach (bool nonResident in new[] { false, true })
        {
            string mode = nonResident ? "非常驻列表" : "常驻列表";
            var recs = new Dictionary<long, byte[]>();
            void Put(MftRec r) => recs[r.Number] = r.Build();
            Put(new MftRec(5, true, 5).Std(0, 6).Name(Root, ".", 3));
            // 注意 0–15 号记录是元文件，不能拿来放普通文件；所以第 1 段放大一点（0–31）
            Put(new MftRec(17, true).Std().Name(Root, "dir", 3));
            Put(new MftRec(20).Std().Name(Ref(17), "in-first.bin", 3).Data(100, 4096, (900, 1)));      // MFT 第 1 段（0–31）
            Put(new MftRec(40).Std().Name(Ref(17), "in-second.bin", 3).Data(100, 8192, (901, 2)));     // 第 2 段（32–47）
            Put(new MftRec(55).Std().Name(Ref(17), "in-third.bin", 3).Data(100, 12288, (903, 3)));     // 第 3 段（48–63）
            Put(new MftRec(63).Std().Name(Root, "last-slot.bin", 3).Data(1, 4096, (910, 1)));          // 最后一个槽位
            var image = MftImage.Create(recs, 64, fragmentStarts: [0, 32, 48], extensionNumbers: [8, 9], nonResidentList: nonResident);
            var counters = new ScanCounters();
            var tree = ScanImage(image, counters, out var stats);
            tree.Seal();
            ctx.Equal(0, stats.Corrupt, $"{mode}：没有损坏记录（读岔了会读到 0xEE 垃圾簇）");
            ctx.Equal(2, stats.Extensions, $"{mode}：两条承载运行列表后续段的扩展记录");
            foreach (var (path, alloc) in new[] { (@"dir\in-first.bin", 4096L), (@"dir\in-second.bin", 8192), (@"dir\in-third.bin", 12288), ("last-slot.bin", 4096) })
            {
                int id = tree.FindByPath(VolRoot + path);
                ctx.True(id > 0 && tree.Alloc(id) == alloc, $"{mode}：{path} 位于不同的 MFT 段，照样读到");
            }
            int mft = tree.FindByPath(VolRoot + "$MFT");
            ctx.True(mft > 0 && tree.Alloc(mft) == 16 * 4096 + (nonResident ? 4096 : 0), $"{mode}：$MFT 自身占用 = 全部 16 簇（非常驻列表另加它自己的一簇）");
        }
    }

    // ---------------- 坏卷必须干净地失败，由 ScanService 降级到 Walk ----------------

    static void BrokenVolumes(CheckContext ctx)
    {
        static Dictionary<long, byte[]> Basic() => new()
        {
            [5] = new MftRec(5, true, 5).Std(0, 6).Name(Root, ".", 3).Build(),
            [10] = new MftRec(10).Std().Name(Root, "f.txt", 3).DataResident(1).Build(),
        };
        void Expect<T>(string what, MftImage image) where T : Exception
        {
            try { ScanImage(image, new ScanCounters(), out _); ctx.Fail($"{what}：应当抛 {typeof(T).Name}，实际成功了"); }
            catch (T) { ctx.True(true, what); }
            catch (Exception ex) { ctx.Fail($"{what}：应当抛 {typeof(T).Name}，实际 {ex.GetType().Name}：{ex.Message}"); }
        }
        MftRec Zero(params (long Lcn, long Len)[] runs) =>
            new MftRec(0).Std().Name(Ref(5, 5), "$MFT", 3).Data(64 * 1024, 64 * 1024, runs);

        var records = Basic();
        records[0] = new byte[1024];
        Expect<InvalidDataException>("0 号记录是全零", MftImage.Create(records, 64));
        records = Basic();
        records[0] = Zero((4, 2)).Build();
        Expect<InvalidDataException>("运行列表没盖住 MFT 有效长度", MftImage.Create(records, 64));
        records = Basic();
        records[0] = Zero((4, 8), (-1, 8)).Build();
        Expect<InvalidDataException>("MFT 运行列表里有稀疏段", MftImage.Create(records, 64));
        records = Basic();
        records[0] = Zero((1L << 31, 16)).Build();
        Expect<InvalidDataException>("MFT 运行列表越过卷边界", MftImage.Create(records, 64));
        records = Basic();
        records[0] = new MftRec(0).Std().Name(Ref(5, 5), "$MFT", 3).NonResident(MftRec.TypeData, null, 64 * 1024, 64 * 1024, [(4, 16)], lowestVcn: 4).Build();
        Expect<InvalidDataException>("0 号记录的 $DATA 不是从 VCN 0 开始", MftImage.Create(records, 64));
        records = Basic();
        var inconsistent = Zero((4, 16)).Build();
        MftRec.W64(inconsistent, AttrAt(inconsistent, 2) + 0x18, 99L);
        records[0] = inconsistent;
        Expect<InvalidDataException>("0 号记录的最后 VCN 和运行列表对不上", MftImage.Create(records, 64));
        records = Basic();
        var truncated = Zero((4, 16)).Build();
        int dataAt = AttrAt(truncated, 2);
        truncated[dataAt + MftRec.R16(truncated, dataAt + 0x20)] = 0x81;   // 声明 8 字节偏移，后面没那么多
        records[0] = truncated;
        Expect<InvalidDataException>("0 号记录的运行列表被截断", MftImage.Create(records, 64));
        records = Basic();
        records.Remove(5);
        Expect<InvalidDataException>("没有根目录记录", MftImage.Create(records, 64));
        records = Basic();
        records[5] = new MftRec(5, false, 5).Std().Name(Root, ".", 3).Build();
        Expect<InvalidDataException>("5 号记录不是目录", MftImage.Create(records, 64));
        // 属性列表指向还没映射的区域：扩展记录 40 在第 2 段里，而第 2 段正是要靠它才能映射的
        records = Basic();
        Expect<InvalidDataException>("扩展记录落在尚未映射的区域",
            MftImage.Create(records, 64, fragmentStarts: [0, 32, 48], extensionNumbers: [40, 9]));
    }

    // ---------------- 取消、进度、读失败、随机破坏 ----------------

    static void Pipeline(CheckContext ctx)
    {
        const int slots = 4096 * 5;   // 5 个 4 MB 的块
        var recs = new Dictionary<long, byte[]>();
        void Put(MftRec r) => recs[r.Number] = r.Build();
        Put(new MftRec(5, true, 5).Std(0, 6).Name(Root, ".", 3));
        Put(new MftRec(100, true).Std().Name(Root, "d", 3));
        long[] numbers = [101, 4095, 4096, 8191, 8192, 12288, 16384, 20000, slots - 1];
        foreach (long n in numbers) Put(new MftRec(n).Std().Name(Ref(100), $"f{n}.bin", 3).Data(10, 4096 * (n % 7 + 1), (500, 1)));

        // 正常跑：块边界两侧的记录都读到；读取按块进行；阶段和进度单调
        var image = MftImage.Create(recs, slots);
        var counters = new ScanCounters();
        var phases = new List<string>();
        var changes = new List<string>();
        counters.PhaseChanged = () => changes.Add(counters.Phase);
        double last = -1;
        bool monotonic = true;
        image.OnRead = (_, _) =>
        {
            phases.Add(counters.Phase);
            double p = counters.Percent;
            if (p < last - 1e-9) monotonic = false;
            last = Math.Max(last, p);
        };
        var tree = ScanImage(image, counters, out var stats);
        tree.Seal();
        foreach (long n in numbers)
        {
            int id = tree.FindByPath($@"{VolRoot}d\f{n}.bin");
            ctx.True(id > 0 && tree.Alloc(id) == 4096 * (n % 7 + 1), $"{n} 号记录（块边界两侧）读到了");
        }
        ctx.Equal(4L * 1024 * 1024, image.LargestRead, "按 4 MB 一块读取，不会一次读整个 MFT");
        ctx.Equal((long)slots * 1024, stats.ReadBytes, "读取字节数 = MFT 有效长度");
        ctx.True(phases.Count >= 6 && phases[0] == "prepare" && phases.Skip(1).All(p => p == "mft"), "先 prepare（读 0 号记录），之后读块期间全是 mft");
        ctx.True(monotonic, "进度单调不减");
        ctx.Equal("mft,tree,links", string.Join(',', changes), "阶段每变一次回调一次（ScanService 借它立即发进度），不重复、不漏");
        ctx.True(counters.CurrentPath?.Contains("$MFT") == true, "当前路径写了 $MFT 和记录进度");

        // 取消：开始前、读到一半
        try { ScanImage(image, new ScanCounters(), out _, new CancellationToken(true)); ctx.Fail("开始前已取消：应当抛 OperationCanceledException"); }
        catch (OperationCanceledException) { ctx.True(true, "开始前取消"); }
        using (var cts = new CancellationTokenSource())
        {
            var cancelling = MftImage.Create(recs, slots);
            int reads = 0;
            cancelling.OnRead = (_, _) => { if (++reads == 4) cts.Cancel(); };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { ScanImage(cancelling, new ScanCounters(), out _, cts.Token); ctx.Fail("读到一半取消：应当抛 OperationCanceledException"); }
            catch (OperationCanceledException) { ctx.True(watch.ElapsedMilliseconds < 10_000, "读到一半取消：及时抛出，没有卡在队列里"); }
        }

        // 读失败：原样抛出，不是被包成 AggregateException 或吞成取消
        var failing = MftImage.Create(recs, slots);
        int calls = 0;
        failing.OnRead = (_, _) => { if (++calls == 4) throw new IOException("磁盘炸了"); };
        try { ScanImage(failing, new ScanCounters(), out _); ctx.Fail("读失败：应当抛 IOException"); }
        catch (IOException ex) { ctx.Equal("磁盘炸了", ex.Message, "读失败原样抛出"); }

        // 取消和失败之后同一进程里还能正常再扫一次（没有遗留的卡死线程或脏状态）
        var again = ScanImage(MftImage.Create(recs, slots), new ScanCounters(), out var againStats);
        ctx.Equal(stats.Nodes, againStats.Nodes, "失败之后重扫结果相同");

        Fuzz(ctx);
    }

    /// 随机建一棵带硬链接、DOS 短名、扩展记录的树，再随机破坏一部分记录：扫描不许崩，树的结构不变量必须成立。
    static ulong ParentRef(long dir) => dir == 5 ? Root : Ref(dir);

    static void Fuzz(CheckContext ctx)
    {
        int survived = 0, totalCorrupt = 0;
        for (int seed = 1; seed <= 40; seed++)
        {
            var rng = new Random(seed);
            var recs = new Dictionary<long, byte[]>();
            var dirs = new List<long> { 5 };
            recs[5] = new MftRec(5, true, 5).Std(0, 6).Name(Root, ".", 3).Build();
            const int slots = 256;
            for (long n = 24; n < slots - 8; n++)
            {
                long parent = dirs[rng.Next(dirs.Count)];
                ulong parentRef = ParentRef(parent);
                var rec = new MftRec(n, rng.Next(5) == 0);
                rec.Std(rng.Next(), (uint)(rng.Next(4) == 0 ? 0x2 : 0)).Name(parentRef, $"n{n}", 1);
                if (rng.Next(4) == 0) rec.Name(parentRef, $"N{n}~1", 2);
                if (rng.Next(6) == 0) rec.Name(ParentRef(dirs[rng.Next(dirs.Count)]), $"link{n}", 1);
                if (rng.Next(3) == 0) rec.Data(rng.Next(100000), 4096 * rng.Next(1, 30), (rng.Next(1000, 9000), 1));
                else rec.DataResident(rng.Next(200));
                if (rng.Next(5) == 0) rec.IndexAlloc(4096 * rng.Next(1, 4));
                recs[n] = rec.Build();
                if (rec.HeaderFlags == 3) dirs.Add(n);
                if (rng.Next(20) == 0 && n + 1 < slots - 8)
                {
                    n++;
                    recs[n] = new MftRec(n) { BaseRef = Ref(n - 1) }.Name(parentRef, $"ext{n}", 1).NonResident(MftRec.TypeData, "s", 5, 4096, [(rng.Next(1000, 9000), 1)]).Build();
                }
            }
            foreach (long n in recs.Keys.Where(k => k > 5).ToArray())
                if (rng.Next(8) == 0)
                    for (int k = 1 + rng.Next(3); k > 0; k--) recs[n][rng.Next(0x30, 0x1F0)] = (byte)rng.Next(256);
            try
            {
                var image = MftImage.Create(recs, slots);
                var counters = new ScanCounters();
                var tree = ScanImage(image, counters, out var stats);
                long own = 0;
                for (int i = 0; i < tree.Count; i++)
                    if (!tree.Has(i, NodeFlags.HardLinkDup)) own += tree.Alloc(i);
                tree.Seal();
                bool ordered = true;
                for (int i = 1; i < tree.Count; i++) ordered &= tree.Parent(i) < i && tree.IsDir(tree.Parent(i));
                ctx.True(ordered && own == tree.Alloc(0), $"随机破坏的卷（种子 {seed}）：树结构成立、占用对账一致");
                totalCorrupt += stats.Corrupt;
                survived++;
            }
            catch (Exception ex) { ctx.Fail($"随机破坏的卷（种子 {seed}）：扫描抛出 {ex.GetType().Name}：{ex.Message}"); }
        }
        ctx.Equal(40, survived, "40 个随机破坏的卷全部扫完");
        ctx.True(totalCorrupt > 0, "随机破坏确实造出了损坏记录");
    }
}
