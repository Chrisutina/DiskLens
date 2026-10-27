using System.Diagnostics;
using DiskLens.Core;

namespace DiskLens.Check;

// 合成大卷：读到哪一块就现场造哪一块记录，不占内存，用来量流水线本身的耗时和峰值内存。
// 默认 20 万文件当冒烟；验收目标（DESIGN §4.4.8）用 --perf 1000000 跑：< 5 秒、峰值 < 600 MB。
static partial class MftChecks
{
    static void Synthetic(CheckContext ctx)
    {
        long files = long.TryParse(ctx.Option("perf"), out long n) ? n : 200_000;
        bool target = files >= 1_000_000;   // 明确要求百万级才把验收目标当断言
        int blocks = (int)((files + 62) / 63);
        var volume = new SyntheticVolume(blocks);
        var counters = new ScanCounters();
        GC.Collect();
        var watch = Stopwatch.StartNew();
        var tree = MftScanner.ScanDevice(volume.Read, volume.Geometry, VolRoot, counters, default, out var stats);
        double scanMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        tree.Seal();
        double sealMs = watch.Elapsed.TotalMilliseconds;
        var process = Process.GetCurrentProcess();
        double peakMb = process.PeakWorkingSet64 / 1048576.0;
        ctx.Info($"合成卷：{volume.Slots:N0} 个记录槽、{volume.FileCount:N0} 个文件、{blocks:N0} 个目录 → 树 {tree.Count:N0} 个节点");
        ctx.Info($"耗时：读取+解析 {stats.ReadMs:F0} ms，合并 {stats.AssembleMs:F0} ms，建树+硬链接 {stats.BuildMs:F0} ms，扫描合计 {scanMs:F0} ms，Seal {sealMs:F0} ms");
        ctx.Info($"内存：进程峰值工作集 {peakMb:F0} MB，托管堆 {GC.GetTotalMemory(false) / 1048576.0:F0} MB（合成卷自己只占几 MB）");

        ctx.Equal(volume.ExpectedNodes, tree.Count, "合成卷的节点数");
        ctx.Equal(volume.ExpectedAlloc, tree.Alloc(0), "合成卷的占用总和（硬链接只计一份）");
        ctx.Equal(volume.ExpectedLinkDups, counters.HardLinkDups, "合成卷的硬链接重复数");
        ctx.Equal(0, stats.Corrupt, "合成卷没有损坏记录");
        ctx.True(scanMs < (target ? 5000 : 20000), target ? $"{files:N0} 文件扫描 {scanMs:F0} ms，目标 < 5000 ms" : $"冒烟规模扫描 {scanMs:F0} ms < 20000 ms（没有二次方行为）");
        if (target) ctx.True(peakMb < 600, $"峰值工作集 {peakMb:F0} MB，目标 < 600 MB");
    }

    /// 布局：0 号 $MFT、5 号根目录，32 号起每 64 条为一组——第 0 条是目录 d，其余 63 条是它里面的文件。
    /// 目录按 8 叉树挂（组 0 挂根），文件里每 4 个有一个 DOS 短名，每组有两个文件在另一个目录里还有一个硬链接。
    sealed class SyntheticVolume
    {
        const int First = 32, Group = 64, RecordSize = 1024;
        readonly byte[][] _templates;      // 0 普通文件、1 带 DOS 短名、2 带硬链接、3 目录
        readonly int[][] _offsets;         // 每个模板：[name1 值偏移, name2 值偏移, $DATA 属性偏移]
        readonly int _groups;

        public int Slots { get; }
        public MftGeometry Geometry { get; }
        public long FileCount => (long)_groups * (Group - 1);
        public int ExpectedNodes => 3 + _groups * (Group + 2);   // 根、虚拟目录、$MFT，每组 64 个节点加 2 个额外硬链接
        public long ExpectedLinkDups => 2L * _groups;
        public long ExpectedAlloc { get; }

        public SyntheticVolume(int groups)
        {
            _groups = groups;
            Slots = First + groups * Group;
            Geometry = new MftGeometry(512, 4096, RecordSize, (long)Slots * RecordSize, 4, 1L << 32, 1L << 32);
            _templates = new byte[4][];
            _offsets = new int[4][];
            for (int t = 0; t < 4; t++)
            {
                var rec = new MftRec(100, directory: t == 3).Std(1, 0);
                rec.Name(Root, t == 3 ? "d00000000" : "f00000000.dat", 1);
                if (t == 1) rec.Name(Root, "FILE~0000000", 2);
                if (t == 2) rec.Name(Root, "h00000000.lnk", 1);
                if (t != 3) rec.Data(5000, 8192, (0x123456, 2));
                _templates[t] = rec.Build();
                int name1 = AttrAt(_templates[t], 1) + 0x18, name2 = t is 1 or 2 ? AttrAt(_templates[t], 2) + 0x18 : -1;
                _offsets[t] = [name1, name2, t == 3 ? -1 : AttrAt(_templates[t], t is 1 or 2 ? 3 : 2)];
            }
            long alloc = (long)Slots * RecordSize;   // $MFT 自己
            for (int g = 0; g < groups; g++)
                for (int s = 1; s < Group; s++) alloc += Clusters(First + (long)g * Group + s) * 4096;
            ExpectedAlloc = alloc;
        }

        static long Size(long r) => 1000 + r * 2654435761L % 1_000_000;
        static long Clusters(long r) => (Size(r) + 4095) / 4096;

        public MftDeviceRead Read => (offset, buffer) =>
        {
            const long start = 4L * 4096;
            if (offset < start || offset + buffer.Length > start + (long)Slots * RecordSize || (offset - start) % RecordSize != 0 || buffer.Length % RecordSize != 0)
                throw new IOException($"合成卷只提供 MFT 区域：偏移 {offset}，长度 {buffer.Length}");
            long number = (offset - start) / RecordSize;
            for (int i = 0; i < buffer.Length / RecordSize; i++) Fill(number + i, buffer.Slice(i * RecordSize, RecordSize));
        };

        void Fill(long r, Span<byte> dst)
        {
            if (r == 0)
            {
                new MftRec(0).Std(0, 6).Name(Ref(5, 5), "$MFT", 3).Data((long)Slots * RecordSize, (long)Slots * RecordSize, (4, Slots / 4)).Build().CopyTo(dst);
                return;
            }
            if (r == 5)
            {
                new MftRec(5, true, 5).Std(0, 6).Name(Root, ".", 3).Build().CopyTo(dst);
                return;
            }
            if (r < First) { dst.Clear(); return; }
            long g = (r - First) / Group, s = (r - First) % Group;
            int kind = s == 0 ? 3 : s % 50 == 7 ? 2 : s % 4 == 0 ? 1 : 0;
            _templates[kind].CopyTo(dst);
            int[] at = _offsets[kind];
            MftRec.W32Span(dst, 0x2C, (uint)r);
            MftRec.W64Span(dst, at[0], g == 0 ? Root : Ref(First + (g - 1) / 8 * Group));
            WriteDigits(dst, at[0] + 0x42 + 2, r, 8);
            if (kind == 1) WriteDigits(dst, at[1] + 0x42 + 2 * 5, r, 7);
            if (kind == 2)
            {
                MftRec.W64Span(dst, at[1], Ref(First + (g * 7 + 3) % _groups * Group));
                WriteDigits(dst, at[1] + 0x42 + 2, r, 8);
            }
            if (kind == 3) return;
            long clusters = Clusters(r), data = at[2];
            MftRec.W64Span(dst, (int)data + 0x18, clusters - 1);
            MftRec.W64Span(dst, (int)data + 0x28, clusters * 4096);
            MftRec.W64Span(dst, (int)data + 0x30, Size(r));
            MftRec.W64Span(dst, (int)data + 0x38, Size(r));
            dst[(int)data + 0x40 + 1] = (byte)clusters;
            long lcn = 1000 + r % 8_000_000;
            dst[(int)data + 0x40 + 2] = (byte)lcn;
            dst[(int)data + 0x40 + 3] = (byte)(lcn >> 8);
            dst[(int)data + 0x40 + 4] = (byte)(lcn >> 16);
        }

        static void WriteDigits(Span<byte> dst, int at, long value, int digits)
        {
            for (int i = digits - 1; i >= 0; i--, value /= 10) dst[at + i * 2] = (byte)('0' + value % 10);
        }
    }
}
