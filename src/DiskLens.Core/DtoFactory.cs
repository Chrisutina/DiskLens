namespace DiskLens.Core;

/// 好几个模块都要的 DTO 构造放这儿，免得各写一份还写得不一样。调用方持有 tree.Sync。
public static class DtoFactory
{
    internal const long UnixEpochFileTime = 116444736000000000L;

    /// FILETIME（UTC，100ns）→ Unix 毫秒；0 或负数表示未知。
    public static long? UnixMs(long fileTime) =>
        fileTime <= UnixEpochFileTime ? null : (fileTime - UnixEpochFileTime) / 10_000;

    public static FileItemDto FileItem(FileTree t, int i) =>
        new(i, t.GetName(i), t.GetPath(i), t.IsDir(i), t.Size(i), t.Alloc(i), UnixMs(t.MTime(i)),
            t.Category(i), (int)t.Flags(i), t.Purpose(i));

    /// 节点最近的所属对象；没有为 null。
    public static OwnerDto? Owner(FileTree t, int i) =>
        t.OwnerOf(i, out int root) is { } o ? new(o.Kind, o.Name, root, t.GetPath(root), t.OwnerEvidenceAt(root)) : null;

    public static ChildItemDto ChildItem(FileTree t, int i) =>
        new(i, t.GetName(i), t.IsDir(i), t.Size(i), t.Alloc(i), t.Category(i), (int)t.Flags(i), t.Files(i), t.Purpose(i));
}
