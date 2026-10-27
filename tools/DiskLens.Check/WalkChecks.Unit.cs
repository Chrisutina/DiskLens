using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskLens.Core;

namespace DiskLens.Check;

// 不碰真实目录的用例：路径与卷根解析、三种目录信息类的解析、降级链、进入 / 占用规则。
static partial class WalkChecks
{
    static void PathUnits(CheckContext ctx)
    {
        // LongPath：普通 → \\?\，UNC → \\?\UNC\，已带前缀 / 设备路径原样
        ctx.Equal(@"\\?\C:\a", ScanNative.LongPath(@"C:\a"), "LongPath 普通路径");
        ctx.Equal(@"\\?\UNC\srv\sh\x", ScanNative.LongPath(@"\\srv\sh\x"), "LongPath UNC");
        ctx.Equal(@"\\?\C:\a", ScanNative.LongPath(@"\\?\C:\a"), "LongPath 已带前缀");
        ctx.Equal(@"\\?\UNC\srv\sh", ScanNative.LongPath(@"\\?\UNC\srv\sh"), "LongPath 已带 UNC 前缀");
        ctx.Equal(@"\\.\C:", ScanNative.LongPath(@"\\.\C:"), "LongPath 设备路径不被当成 UNC");

        ctx.Equal(@"C:\a\b", ScanNative.StripLongPrefix(@"\\?\C:\a\b"), "脱前缀 盘符");
        ctx.Equal(@"\\srv\sh\x", ScanNative.StripLongPrefix(@"\\?\UNC\srv\sh\x"), "脱前缀 UNC");
        ctx.Equal(@"\\?\Volume{1234}\x", ScanNative.StripLongPrefix(@"\\?\Volume{1234}\x"), "卷 GUID 路径还原不了，原样保留");
        ctx.Equal(@"C:\x", ScanNative.StripLongPrefix(@"C:\x"), "普通路径不动");

        ctx.Equal(@"C:\b", ScanNative.NormalizePath(@"\\?\C:\a\..\b\"), "NormalizePath 脱前缀、规整 .. 与尾部斜杠");
        ctx.Equal(@"\\srv\sh\x", ScanNative.NormalizePath(@"\\?\UNC\srv\sh\x\"), "NormalizePath UNC");
        ctx.Equal(@"C:\", ScanNative.NormalizePath(@"C:\"), "NormalizePath 卷根保留斜杠");
        ctx.Equal(@"C:\foo\bar", ScanNative.NormalizePath("C:/foo/bar/"), "NormalizePath 正斜杠");

        // 卷根：UNC 纯字符串，不能为它联网；长路径前缀要认
        ctx.Equal(@"\\srv\share\", Volumes.GetVolumeRoot(@"\\srv\share\dir\file"), "UNC 卷根");
        ctx.Equal(@"\\srv\share\", Volumes.GetVolumeRoot(@"\\?\UNC\srv\share\dir"), "\\?\\UNC 卷根");
        ctx.Equal(@"\\srv\share\", Volumes.GetVolumeRoot(@"\\srv\share"), "UNC 只有共享名");
        ctx.Equal(@"\\srv\", Volumes.GetVolumeRoot(@"\\srv\"), "UNC 只有服务器名");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string drive = Path.GetPathRoot(windows)!;
        ctx.Equal(drive, Volumes.GetVolumeRoot(windows), "盘符卷根");
        ctx.Equal(drive, Volumes.GetVolumeRoot(@"\\?\" + windows), @"\\?\ 前缀的盘符卷根");
        ctx.Equal(drive, Volumes.GetVolumeRoot(windows.ToLowerInvariant())?.ToUpperInvariant(), "小写盘符");
        string deep = windows;
        while (deep.Length < 300) deep = Path.Combine(deep, "long-path-segment-" + new string('x', 30));
        ctx.Equal(drive, Volumes.GetVolumeRoot(deep), "超过 MAX_PATH 的路径也能求卷根");

        ctx.True(Volumes.IsVolumeRoot(drive), "IsVolumeRoot 盘符根");
        ctx.True(Volumes.IsVolumeRoot(@"\\?\" + drive), @"IsVolumeRoot \\?\ 前缀盘符根");
        ctx.True(!Volumes.IsVolumeRoot(windows), "IsVolumeRoot 子目录不是卷根");
        ctx.True(Volumes.IsVolumeRoot(@"\\srv\share"), "IsVolumeRoot UNC 共享根");
        ctx.True(Volumes.IsVolumeRoot(@"\\srv\share\"), "IsVolumeRoot UNC 共享根带斜杠");
        ctx.True(!Volumes.IsVolumeRoot(@"\\srv\share\x"), "IsVolumeRoot UNC 子目录");
        ctx.Equal("network", Volumes.GetDriveType(@"\\srv\share\x"), "UNC 一律按网络盘，且不联网");
        ctx.Equal("fixed", Volumes.GetDriveType(windows), "系统盘是固定盘");
        ctx.True(Volumes.GetVolumeRoot("\0bad") == null, "非法路径返回 null 而不是抛");
    }

    static void PolicyUnits(CheckContext ctx)
    {
        // 线程数：SSD clamp(核数, 4, 24)，机械盘 2，网络盘 8，未知 8
        ctx.Equal(4, WalkScanner.WorkerCount("fixed", true, 2), "SSD 少核取下限 4");
        ctx.Equal(12, WalkScanner.WorkerCount("fixed", true, 12), "SSD 按核数");
        ctx.Equal(24, WalkScanner.WorkerCount("fixed", true, 64), "SSD 多核取上限 24");
        ctx.Equal(2, WalkScanner.WorkerCount("fixed", false, 16), "机械盘 2 线程");
        ctx.Equal(8, WalkScanner.WorkerCount("fixed", null, 16), "查不出类型按 8");
        ctx.Equal(8, WalkScanner.WorkerCount("network", true, 16), "网络盘固定 8，不看 SSD");
        ctx.Equal(8, WalkScanner.WorkerCount("network", null, 2), "网络盘 8");

        // 要不要进子目录：只看名称代理位，云标签照进，标签读不出来（0）的重解析目录不进
        const NodeFlags d = NodeFlags.Directory, r = NodeFlags.Reparse;
        ctx.True(WalkReader.ShouldEnter(d, 0), "普通目录进入");
        ctx.True(!WalkReader.ShouldEnter(0, 0), "文件不进入");
        ctx.True(!WalkReader.ShouldEnter(d | r, 0xA0000003), "junction 不进");
        ctx.True(!WalkReader.ShouldEnter(d | r, 0xA000000C), "符号链接不进");
        ctx.True(!WalkReader.ShouldEnter(d | r, 0xA0000003 & ~0x80000000u), "只要有名称代理位就不进（不看别的位）");
        ctx.True(WalkReader.ShouldEnter(d | r, 0x9000001A), "OneDrive 云标签进入");
        ctx.True(WalkReader.ShouldEnter(d | r, 0x80000018), "WCI 进入");
        ctx.True(WalkReader.ShouldEnter(d | r, 0x8000001B), "AppExecLink 标签（无名称代理位）按规则进入");
        ctx.True(!WalkReader.ShouldEnter(d | r, 0), "重解析目录标签为 0 = 文件系统没给，当未知不进");

        // 要不要单独查真实占用（默认：1000 字节、占 4096、本地 NTFS）
        const NodeFlags f = NodeFlags.None;
        static bool Needs(NodeFlags flags, uint tag = 0, long size = 1000, long alloc = 4096, bool local = true) =>
            WalkReader.NeedsRealAlloc(flags, tag, size, alloc, local);
        ctx.True(!Needs(f), "普通文件用目录项占用");
        ctx.True(Needs(NodeFlags.Compressed), "压缩文件要查");
        ctx.True(Needs(NodeFlags.Sparse), "稀疏文件要查");
        ctx.True(Needs(r, 0x80000017), "WOF 标签要查");
        ctx.True(!Needs(r, 0x8000001B), "AppExecLink 不查");
        ctx.True(!Needs(NodeFlags.Cloud | NodeFlags.Sparse), "云占位符不查（不为它多开十万次文件）");
        ctx.True(!Needs(d | NodeFlags.Compressed), "目录不查");
        ctx.True(Needs(f, 0, 262144, 0), "本地 NTFS 上有大小却占用为 0：CompactOS 的 WOF 文件在目录项里既无重解析属性也无标签，这是唯一破绽");
        ctx.True(!Needs(f, 0, 262144, 0, local: false), "网络盘不做「占用为 0」这条：有的服务器不报分配大小，会变成逐文件联网");
        ctx.True(!Needs(f, 0, 0, 0), "空文件占用 0 是正常的");
        ctx.True(!Needs(f, 0, 5, 8), "常驻 MFT 的小文件占用按 8 字节对齐，不是 0");
        ctx.True(!Needs(NodeFlags.Cloud, 0, 262144, 0), "云占位符本来就占用 0，不查");

        ctx.True(WalkReader.IsAccessDenied(5) && WalkReader.IsAccessDenied(65) && !WalkReader.IsAccessDenied(32), "拒绝访问错误码");
        foreach (int code in new[] { 1, 50, 87, 120, 124 }) ctx.True(WalkReader.IsUnsupportedClass(code), $"错误 {code} 视为信息类不支持");
        foreach (int code in new[] { 5, 18, 21, 32, 3 }) ctx.True(!WalkReader.IsUnsupportedClass(code), $"错误 {code} 不触发降级");
    }

    // ---- 目录项解析：按文档里的结构体布局手工拼缓冲区 ----

    sealed record Spec(string Name, uint Attr, long Size = 0, long Alloc = 0, long Modified = 0, uint Tag = 0, uint Ea = 0,
        ulong IdLow = 0, ulong IdHigh = 0);

    static byte[] Rows(int level, params Spec[] specs)
    {
        int nameAt = level switch { 0 => 88, 1 => 104, _ => 68 };
        var rows = new List<byte[]>();
        for (int i = 0; i < specs.Length; i++)
        {
            var s = specs[i];
            ReadOnlySpan<byte> name = MemoryMarshal.AsBytes(s.Name.AsSpan()); // 原样 UTF-16，保住孤立代理项
            var row = new byte[(nameAt + name.Length + 7) & ~7]; // 条目 8 字节对齐
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(0), i == specs.Length - 1 ? 0 : (uint)row.Length);
            BinaryPrimitives.WriteInt64LittleEndian(row.AsSpan(24), s.Modified);
            BinaryPrimitives.WriteInt64LittleEndian(row.AsSpan(40), s.Size);
            BinaryPrimitives.WriteInt64LittleEndian(row.AsSpan(48), s.Alloc);
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(56), s.Attr);
            BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(60), (uint)name.Length);
            bool reparse = (s.Attr & 0x400) != 0;
            switch (level)
            {
                case 0: // FILE_ID_EXTD_DIR_INFO：EaSize(64) ReparsePointTag(68) FileId(72, 16 字节)
                    BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(64), s.Ea);
                    BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(68), s.Tag);
                    BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(72), s.IdLow);
                    BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(80), s.IdHigh);
                    break;
                case 1: // FILE_ID_BOTH_DIR_INFO：EaSize(64，带重解析属性时装标签) ShortName(68..) FileId(96, 8 字节)
                    BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(64), reparse ? s.Tag : s.Ea);
                    BinaryPrimitives.WriteUInt64LittleEndian(row.AsSpan(96), s.IdLow);
                    break;
                default: // FILE_FULL_DIR_INFO：只有 EaSize(64)
                    BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(64), reparse ? s.Tag : s.Ea);
                    break;
            }
            name.CopyTo(row.AsSpan(nameAt));
            rows.Add(row);
        }
        return rows.SelectMany(x => x).ToArray();
    }

    static Spec[] SampleSpecs() =>
    [
        new(".", 0x10), new("..", 0x10),
        new("plain.txt", 0x20, 12345, 16384, 132_000_000_000_000_001, Ea: 0, IdLow: 0x0102030405060708, IdHigh: 0x1112131415161718),
        new("dir 目录", 0x10, 4096, 8192, 132_000_000_000_000_002, IdLow: 7),
        new("link-junction", 0x10 | 0x400, 0, 0, 3, Tag: 0xA0000003, IdLow: 8),
        new("cloud-dir", 0x10 | 0x400 | 0x400000, 0, 0, 4, Tag: 0x9000001A, IdLow: 9),
        new("wof.dll", 0x20 | 0x400, 1_000_000, 0, 5, Tag: 0x80000017, IdLow: 10),
        new("ea.txt", 0x20, 10, 4096, 6, Ea: 77, IdLow: 11), // 没有重解析属性：EaSize 里的 77 绝不能当标签
        new("sparse.bin", 0x20 | 0x200, 1L << 30, 8192, 7, IdLow: 12),
        new("comp.bin", 0x20 | 0x800, 262144, 4096, 8, IdLow: 13),
        new("hidden-sys", 0x20 | 0x2 | 0x4, 1, 0, 9, IdLow: 14),
        new("enc.bin", 0x20 | 0x4000, 100, 4096, 10, IdLow: 15),
        new("lone\ud800surrogate.txt", 0x20, 1, 0, 11, IdLow: 16),
        new("last-entry", 0x20, 5, 0, 12, IdLow: 17),
    ];

    static void ParserUnits(CheckContext ctx)
    {
        var expected = new (string Name, NodeFlags Flags, long Size, long Alloc, uint Tag)[]
        {
            ("plain.txt", 0, 12345, 16384, 0),
            ("dir 目录", NodeFlags.Directory, 0, 0, 0),
            ("link-junction", NodeFlags.Directory | NodeFlags.Reparse, 0, 0, 0xA0000003),
            ("cloud-dir", NodeFlags.Directory | NodeFlags.Reparse | NodeFlags.Cloud, 0, 0, 0x9000001A),
            ("wof.dll", NodeFlags.Reparse, 1_000_000, 0, 0x80000017),
            ("ea.txt", 0, 10, 4096, 0),
            ("sparse.bin", NodeFlags.Sparse, 1L << 30, 8192, 0),
            ("comp.bin", NodeFlags.Compressed, 262144, 4096, 0),
            ("hidden-sys", NodeFlags.Hidden | NodeFlags.System, 1, 0, 0),
            ("enc.bin", NodeFlags.Encrypted, 100, 4096, 0),
            ("lone\ud800surrogate.txt", 0, 1, 0, 0),
            ("last-entry", 0, 5, 0, 0),
        };
        var specs = SampleSpecs().Skip(2).ToArray(); // 去掉 . 和 ..
        for (int level = 0; level < WalkReader.LevelCount; level++)
        {
            string tag = $"信息类等级 {level}";
            var batch = new WalkBatch();
            ctx.Equal(0, WalkReader.Parse(Rows(level, SampleSpecs()), level, batch), tag + " 解析成功");
            ctx.Equal(expected.Length, batch.Entries.Count, tag + " 跳过 . 和 .. 后的条目数");
            for (int i = 0; i < Math.Min(expected.Length, batch.Entries.Count); i++)
            {
                var e = batch.Entries[i];
                var x = expected[i];
                var s = specs[i];
                ctx.True(batch.Name(e).SequenceEqual(x.Name), $"{tag} 名字 {x.Name}（含孤立代理项原样保留）");
                ctx.Equal(x.Flags, e.Flags, $"{tag} {x.Name} 标志");
                ctx.Equal(x.Size, e.Size, $"{tag} {x.Name} 大小（目录恒为 0）");
                ctx.Equal(x.Alloc, e.Alloc, $"{tag} {x.Name} 占用");
                ctx.Equal(x.Tag, e.Tag, $"{tag} {x.Name} 重解析标签");
                ctx.Equal(s.Modified, e.Modified, $"{tag} {x.Name} 修改时间取 LastWriteTime");
                // FileId：Extd 是 128 位，Both 只有低 64 位，Full 没有
                UInt128 wantId = level switch { 0 => ((UInt128)s.IdHigh << 64) | s.IdLow, 1 => s.IdLow, _ => 0 };
                ctx.True(e.FileId == wantId, $"{tag} {x.Name} FileId 宽度");
            }
        }
    }

    // ---- 降级链：用假查询喂数据，验证调用顺序、等级、部分结果和错误传递 ----

    static void ChainUnits(CheckContext ctx)
    {
        var a = new Spec("a.txt", 0x20, 100, 4096, IdLow: 1);
        var b = new Spec("b.txt", 0x20, 200, 4096, IdLow: 2);
        var c = new Spec("c.txt", 0x20, 300, 4096, IdLow: 3);
        var buffer = new byte[128 * 1024];

        // A：Extd 和 Both 都报 87，Full 分两块读完，最后 ERROR_NO_MORE_FILES
        {
            var calls = new List<int>();
            byte[] chunk1 = Rows(2, a, b), chunk2 = Rows(2, c);
            var batch = new WalkBatch();
            int level = 0;
            int result = WalkReader.ReadEntries((int cls, byte[] buf, out int err) =>
            {
                calls.Add(cls);
                if (cls is 20 or 11) { err = 87; return false; }
                if (cls == 15) { chunk1.CopyTo(buf, 0); err = 0; return true; }
                if (cls == 14 && calls.Count(x => x == 14) == 1) { chunk2.CopyTo(buf, 0); err = 0; return true; }
                err = 18;
                return false;
            }, ref level, buffer, batch, CancellationToken.None);
            ctx.Equal(0, result, "降级链 A 读完返回 0");
            ctx.Equal(2, level, "降级链 A 落到 FileFullDirectoryInfo");
            ctx.Equal("20,11,15,14,14", string.Join(',', calls), "降级链 A 调用顺序：先从头读各级，再接着读");
            ctx.Equal(3, batch.Entries.Count, "降级链 A 两块缓冲的条目都在");
        }
        // B：只有 Extd 不支持（124），Both 可用
        {
            var calls = new List<int>();
            byte[] chunk = Rows(1, a);
            var batch = new WalkBatch();
            int level = 0;
            int result = WalkReader.ReadEntries((int cls, byte[] buf, out int err) =>
            {
                calls.Add(cls);
                if (cls == 20) { err = 124; return false; }
                if (cls == 11) { chunk.CopyTo(buf, 0); err = 0; return true; }
                err = 18;
                return false;
            }, ref level, buffer, batch, CancellationToken.None);
            ctx.Equal(0, result, "降级链 B 返回 0");
            ctx.Equal(1, level, "降级链 B 落到 FileIdBothDirectoryInfo");
            ctx.Equal("20,11,10", string.Join(',', calls), "降级链 B 调用顺序");
            ctx.True(batch.Entries.Count == 1 && batch.Entries[0].FileId == 1, "降级链 B 按 Both 布局解析出 FileId");
        }
        // C：第一次就拒绝访问：不降级，原样返回，什么都没读到
        {
            var calls = new List<int>();
            var batch = new WalkBatch();
            int level = 0;
            int result = WalkReader.ReadEntries((int cls, byte[] buf, out int err) => { calls.Add(cls); err = 5; return false; },
                ref level, buffer, batch, CancellationToken.None);
            ctx.Equal(5, result, "降级链 C 拒绝访问原样返回");
            ctx.Equal(0, level, "降级链 C 拒绝访问不降级");
            ctx.Equal("20", string.Join(',', calls), "降级链 C 只调用一次");
            ctx.Equal(0, batch.Entries.Count, "降级链 C 没有条目");
        }
        // D：读到一半出错：返回错误码，已读到的条目保留
        {
            var calls = new List<int>();
            byte[] chunk = Rows(0, a, b);
            var batch = new WalkBatch();
            int level = 0;
            int result = WalkReader.ReadEntries((int cls, byte[] buf, out int err) =>
            {
                calls.Add(cls);
                if (cls == 20) { chunk.CopyTo(buf, 0); err = 0; return true; }
                err = 1117; // ERROR_IO_DEVICE
                return false;
            }, ref level, buffer, batch, CancellationToken.None);
            ctx.Equal(1117, result, "降级链 D 返回读到一半的错误码");
            ctx.Equal(2, batch.Entries.Count, "降级链 D 出错前读到的条目不丢");
            ctx.Equal("20,19", string.Join(',', calls), "降级链 D 第二次用「接着读」信息类");
        }
        // E：接着读的时候报「不支持」不能换信息类（换了就是把同一目录读两遍）
        {
            byte[] chunk = Rows(0, a);
            var batch = new WalkBatch();
            int level = 0, calls = 0;
            int result = WalkReader.ReadEntries((int cls, byte[] buf, out int err) =>
            {
                calls++;
                if (cls == 20) { chunk.CopyTo(buf, 0); err = 0; return true; }
                err = 87;
                return false;
            }, ref level, buffer, batch, CancellationToken.None);
            ctx.Equal(87, result, "降级链 E 读到一半的不支持错误原样返回");
            ctx.Equal(0, level, "降级链 E 读到一半不降级");
            ctx.Equal(2, calls, "降级链 E 不重试");
        }
        // F：空目录
        {
            var batch = new WalkBatch();
            int level = 0;
            ctx.Equal(0, WalkReader.ReadEntries((int cls, byte[] buf, out int err) => { err = 18; return false; },
                ref level, buffer, batch, CancellationToken.None), "空目录返回 0");
            ctx.Equal(0, batch.Entries.Count, "空目录没有条目");
        }
        // G：缓冲区内容被破坏（来自坏盘 / 坏驱动）：返回 ERROR_INVALID_DATA，不能崩、不能死循环
        foreach (var (what, patch) in new (string, Action<byte[]>)[]
        {
            ("名字长度是奇数", row => BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(60), 7)),
            ("名字长度超出缓冲区", row => BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(60), 0x7FFFFFF0)),
            ("下一条偏移超出缓冲区", row => BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(0), 0x7FFFFFF0)),
            ("下一条偏移小于本条自己的长度", row => BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(0), 8)),
        })
        {
            byte[] chunk = Rows(0, a, b);
            patch(chunk);
            var batch = new WalkBatch();
            int level = 0;
            int result = WalkReader.ReadEntries((int cls, byte[] buf, out int err) =>
            {
                Array.Clear(buf);
                chunk.CopyTo(buf, 0);
                err = 0;
                return true;
            }, ref level, buffer, batch, CancellationToken.None);
            ctx.Equal(13, result, $"损坏缓冲区（{what}）返回 ERROR_INVALID_DATA");
        }
        // H：取消
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            int level = 0;
            try
            {
                WalkReader.ReadEntries((int cls, byte[] buf, out int err) => { err = 18; return false; }, ref level, buffer,
                    new WalkBatch(), cancelled.Token);
                ctx.Fail("降级链 H 忽略了取消");
            }
            catch (OperationCanceledException) { ctx.True(true, "读取目录时响应取消"); }
        }
    }

    // ---- 卷信息 ----

    static void VolumeUnits(CheckContext ctx)
    {
        bool elevated = Volumes.IsElevated();
        bool granted = Volumes.EnableBackupPrivilege();
        // 非管理员令牌里没有这项特权：AdjustTokenPrivileges 会「成功」但 LastError 是 1300，必须判成 false
        if (!elevated) ctx.True(!granted, "非管理员进程不可能得到备份特权（ERROR_NOT_ALL_ASSIGNED 必须判失败）");
        else ctx.True(granted, "管理员进程能开启备份特权");

        var timer = Stopwatch.StartNew();
        var drives = Volumes.List();
        ctx.True(timer.Elapsed < TimeSpan.FromSeconds(4), $"Volumes.List 不卡（{timer.ElapsedMilliseconds} ms）");
        ctx.True(drives.Any(d => d.IsReady && d.TotalBytes > 0), "真实卷列表可读");
        var system = drives.FirstOrDefault(d => d.IsSystem);
        ctx.True(system is { IsReady: true, FileSystem: "NTFS" }, "系统盘就绪且是 NTFS");
        ctx.True(drives.All(d => d.Root.EndsWith('\\')), "卷根都以反斜杠结尾");
        ctx.True(drives.All(d => d.CanMft == (elevated && d.SupportsMft)), "CanMft = 已提权 && SupportsMft");
        ctx.True(drives.All(d => d.IsReady || (d.TotalBytes == 0 && !d.CanMft && !d.SupportsMft)), "未就绪的盘不声称能扫");
        ctx.Info("IsSsd：" + string.Join("，", drives.Where(d => d.IsReady).Select(d => $"{d.Root}={(d.IsSsd?.ToString() ?? "未知")}")));

        // 用假探测模拟卡死的网络盘：期限一到就放弃，且同一个盘上一次探测没返回时不再开新的
        int hung = 0;
        Volumes.DriveProbe? Hang(string root, string type) { Interlocked.Increment(ref hung); Thread.Sleep(6000); return null; }
        timer.Restart();
        var one = Volumes.List(["Y:\\"], _ => "network", Hang, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(400));
        ctx.True(timer.ElapsedMilliseconds < 2500, $"卡死的网络盘在期限内放弃（{timer.ElapsedMilliseconds} ms）");
        ctx.True(one is [{ IsReady: false, DriveType: "network", TotalBytes: 0 }], "卡死的盘显示为未就绪");
        timer.Restart();
        Volumes.List(["Y:\\"], _ => "network", Hang, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(400));
        ctx.Equal(1, Volatile.Read(ref hung), "上一次探测没返回时复用，不开第二个线程");
        ctx.True(timer.ElapsedMilliseconds < 2500, "第二次调用同样不卡");

        // 多个盘并行：整体耗时是最慢期限，不是相加；其余盘不受拖累
        timer.Restart();
        var mixed = Volumes.List(["T1:\\", "T2:\\", "T3:\\"], root => root == "T3:\\" ? "removable" : "fixed",
            (root, type) => root switch
            {
                "T1:\\" => new Volumes.DriveProbe("数据", "NTFS", 1000, 400, true),
                "T2:\\" => new Volumes.DriveProbe("", "exFAT", 500, 100, null),
                _ => new Volumes.DriveProbe("U盘", "NTFS", 64, 32, false),
            }, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(400));
        ctx.True(mixed[0] is { IsReady: true, Label: "数据", SupportsMft: true, IsSsd: true } && mixed[0].CanMft == elevated, "固定 NTFS：能深度扫描");
        ctx.True(mixed[1] is { IsReady: true, SupportsMft: false, CanMft: false }, "exFAT 不支持 MFT");
        ctx.True(mixed[2] is { IsReady: true, SupportsMft: true, IsSsd: false }, "可移动 NTFS 支持 MFT");
        var network = Volumes.List(["T4:\\"], _ => "network", (_, _) => new Volumes.DriveProbe("共享", "NTFS", 1, 1, null),
            TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(400));
        ctx.True(network[0] is { IsReady: true, SupportsMft: false, CanMft: false }, "网络盘即使是 NTFS 也不支持 MFT");
    }
}
