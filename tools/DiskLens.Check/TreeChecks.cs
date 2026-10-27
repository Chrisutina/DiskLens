using DiskLens.Core;

namespace DiskLens.Check;

static class TreeChecks
{
    public static void Run(CheckContext ctx)
    {
        Aggregation(ctx);
        HardLinks(ctx);
        PathsAndVirtual(ctx);
        Categories(ctx);
        VirtualRefresh(ctx);
        Scale(ctx);
    }

    static void Aggregation(CheckContext ctx)
    {
        var t = new FileTree(@"X:\");
        int a = t.AddNode(0, "A", NodeFlags.Directory, 0, 0, 0);
        int b = t.AddNode(0, "B", NodeFlags.Directory, 0, 4096, 0); // 目录自身占用（MFT 模式的索引分配）
        t.AddNode(a, "small.txt", 0, 10, 4096, 0);
        t.AddNode(a, "big.mp4", 0, 10_000, 12_288, 0);
        int sub = t.AddNode(b, "sub", NodeFlags.Directory, 0, 0, 0);
        t.AddNode(sub, "deep.iso", 0, 100_000, 102_400, 0);
        t.AddNode(0, "未统计", NodeFlags.Virtual, 50_000, 50_000, 0);
        t.Seal();

        ctx.Equal(10_010L, t.Size(a), "A 的逻辑大小");
        ctx.Equal(16_384L, t.Alloc(a), "A 的占用");
        ctx.Equal(106_496L, t.Alloc(b), "B 的占用含自身");
        ctx.Equal(16_384L + 106_496 + 50_000, t.Alloc(0), "根的占用");
        ctx.Equal(3L, t.Files(0), "文件数不含虚拟节点");
        ctx.Equal(3L, t.Dirs(0), "目录数");
        var kids = t.Children(0).ToArray();
        ctx.Equal(b, kids[0], "子节点按占用降序：B 第一");
        ctx.True(t.Alloc(kids[0]) >= t.Alloc(kids[1]) && t.Alloc(kids[1]) >= t.Alloc(kids[2]), "根的子节点降序");

        long freed = t.MarkDeleted(sub);
        ctx.Equal(102_400L, freed, "删 sub 的净释放");
        ctx.Equal(4096L, t.Alloc(b), "删 sub 后 B 只剩自身");
        ctx.Equal(2L, t.Files(0), "删 sub 后文件数");
        ctx.Equal(2L, t.Dirs(0), "删 sub 后目录数");
        ctx.True(t.IsDeleted(t.Children(sub)[0]), "子孙一并标删除");
        ctx.Equal(a, t.Children(0)[1], "删除后重排：A 排到 B 前面（第一是 50000 的虚拟节点）");
        ctx.Equal(0, t.LiveChildren(b).Length, "B 没有活着的子节点");
        ctx.True(t.Version >= 2, "版本号递增");
    }

    static void HardLinks(CheckContext ctx)
    {
        var t = new FileTree(@"X:\");
        int w = t.AddNode(0, "Windows", NodeFlags.Directory, 0, 0, 0);
        int sxs = t.AddNode(w, "WinSxS", NodeFlags.Directory, 0, 0, 0);
        int s32 = t.AddNode(w, "System32", NodeFlags.Directory, 0, 0, 0);
        int l1 = t.AddNode(sxs, "kernel.dll", 0, 1000, 4096, 0);
        int l2 = t.AddNode(s32, "kernel.dll", 0, 1000, 4096, 0);
        int canonical = t.ResolveHardLinkGroup([l1, l2]);
        t.Seal();

        ctx.Equal(l2, canonical, "System32 排在 WinSxS 前面，计入 System32");
        ctx.Equal(4096L, t.Alloc(s32), "System32 计入");
        ctx.Equal(0L, t.Alloc(sxs), "WinSxS 不重复计入");
        ctx.Equal(4096L, t.Alloc(0), "根只算一次");
        ctx.Equal(2L, t.Files(0), "两个链接都算文件条目");
        ctx.Equal(4096L, t.Alloc(l1), "副本节点保留自己的大小用于显示");
        ctx.Equal(l2, t.CountedLink(l1), "CountedLink 指向计入的链接");

        long freed = t.MarkDeleted(l2);
        ctx.Equal(0L, freed, "删掉还有别的链接的文件，净释放为 0");
        ctx.Equal(4096L, t.Alloc(sxs), "计入身份转给 WinSxS");
        ctx.Equal(0L, t.Alloc(s32), "System32 扣掉");
        ctx.Equal(4096L, t.Alloc(0), "根不变");
        ctx.Equal(l1, t.CountedLink(l1), "l1 现在是计入的链接");

        freed = t.MarkDeleted(sxs);
        ctx.Equal(4096L, freed, "最后一个链接删掉才真正释放");
        ctx.Equal(0L, t.Alloc(0), "根清零");
    }

    static void PathsAndVirtual(CheckContext ctx)
    {
        var t = new FileTree(@"C:\");
        int meta = t.AddNode(0, "$NTFS 元数据", NodeFlags.Directory | NodeFlags.Virtual | NodeFlags.Metafile, 0, 0, 0);
        int mft = t.AddNode(meta, "$MFT", NodeFlags.Metafile, 1 << 20, 1 << 20, 0);
        int users = t.AddNode(0, "Users", NodeFlags.Directory, 0, 0, 0);
        int me = t.AddNode(users, "Alice", NodeFlags.Directory, 0, 0, 0);
        int f = t.AddNode(me, "Notes, v2 (final).TXT", 0, 5, 4096, 0);
        t.Seal();

        ctx.Equal(@"C:\$MFT", t.GetPath(mft), "虚拟分组不进真实路径");
        ctx.Equal(@"C:\Users\Alice\Notes, v2 (final).TXT", t.GetPath(f), "普通路径");
        ctx.Equal(f, t.FindByPath(@"c:\users\ALICE\notes, v2 (final).txt"), "FindByPath 不区分大小写");
        ctx.Equal(f, t.FindByPath(@"\\?\C:\Users\Alice\Notes, v2 (final).TXT"), "FindByPath 认 \\?\\ 前缀");
        ctx.Equal(mft, t.FindByPath(@"C:\$MFT"), "FindByPath 穿过虚拟分组");
        ctx.Equal(0, t.FindByPath(@"C:\"), "根");
        ctx.Equal(FileTree.NoNode, t.FindByPath(@"D:\Users"), "别的卷");
        ctx.Equal(FileTree.NoNode, t.FindByPath(@"C:\Users\Bob"), "不存在");
        ctx.Equal(FileCategories.System, t.Category(mft), "元文件归系统类");
        ctx.Equal(2L, t.Files(0), "元文件和普通文件都计文件数");
        ctx.Equal(2L, t.Dirs(0), "目录数不含虚拟目录");

        var sub = new FileTree(@"D:\data");
        int x = sub.AddNode(0, "x.bin", 0, 1, 1, 0);
        sub.Seal();
        ctx.Equal(@"D:\data\x.bin", sub.GetPath(x), "文件夹根的路径拼接");
        ctx.Equal(FileTree.NoNode, sub.FindByPath(@"D:\database"), "前缀相同但不是子路径");
        ctx.Equal(x, sub.FindByPath(@"D:\data\X.BIN"), "文件夹根下查找");
    }

    static void Categories(CheckContext ctx)
    {
        var t = new FileTree(@"C:\");
        int v = t.AddNode(0, "movie.MKV", 0, 1, 1, 0);
        int p = t.AddNode(0, "pagefile.sys", NodeFlags.Hidden | NodeFlags.System, 1, 1, 0);
        int n = t.AddNode(0, "README", 0, 1, 1, 0);
        int d = t.AddNode(0, "archive.tar.gz", 0, 1, 1, 0);
        int dot = t.AddNode(0, "weird.", 0, 1, 1, 0);
        int m = t.AddNode(0, "model.safetensors", 0, 1, 1, 0);
        t.Seal();
        ctx.Equal(FileCategories.Video, t.Category(v), "mkv → 视频（大小写无关）");
        ctx.Equal("mkv", t.Ext(v), "扩展名转小写");
        ctx.Equal(FileCategories.System, t.Category(p), "pagefile.sys → 系统");
        ctx.Equal("", t.Ext(n), "无扩展名");
        ctx.Equal("gz", t.Ext(d), "取最后一个点");
        ctx.Equal("", t.Ext(dot), "点在末尾不算扩展名");
        ctx.Equal(FileCategories.Data, t.Category(m), "safetensors → 数据与模型");
    }

    static void Scale(CheckContext ctx)
    {
        // 50 万节点：Seal 必须够快，否则 MFT 扫完卡在汇总上就丢人了
        var t = new FileTree(@"S:\");
        var rnd = new Random(7);
        var dirs = new List<int> { 0 };
        for (int i = 0; i < 500_000; i++)
        {
            int parent = dirs[rnd.Next(dirs.Count)];
            if (i % 20 == 0) dirs.Add(t.AddNode(parent, "d" + i, NodeFlags.Directory, 0, 0, 0));
            else t.AddNode(parent, "f" + i + ".dat", 0, rnd.Next(1, 1 << 20), 4096, 0);
        }
        ctx.Time("Seal 50 万节点", () => { t.Seal(); return 0; });
        long sum = 0;
        for (int i = 1; i < t.Count; i++) if (!t.IsDir(i)) sum += t.Alloc(i);
        ctx.Equal(sum, t.Alloc(0), "根的占用等于所有文件之和");
    }

    static void VirtualRefresh(CheckContext ctx)
    {
        var t = new FileTree(@"X:\");
        int file = t.AddNode(0, "file.bin", 0, 4096, 4096, 0);
        int unknown = t.AddNode(0, "未统计", NodeFlags.Virtual, 0, 0, 0);
        t.Seal();
        t.UpdateVirtualSizes(unknown, 8192, 8192);
        ctx.Equal(12_288L, t.Alloc(0), "对账变化更新根聚合");
        ctx.Equal(unknown, t.Children(0)[0], "对账变化重新排序");
        ctx.Equal(8192L, t.CategoryTotals(Metric.Alloc)[FileCategories.Unaccounted], "未统计类别同步");
        ctx.Equal(1L, t.Files(0), "虚拟叶子不增加文件数");
        int version = t.Version;
        t.UpdateVirtualSizes(unknown, 8192, 8192);
        ctx.Equal(version, t.Version, "数值未变不作废缓存");
        bool refused = false;
        try { t.UpdateVirtualSizes(file, 1, 1); } catch (ArgumentException) { refused = true; }
        ctx.True(refused, "不能修改真实文件的扫描大小");
    }
}
