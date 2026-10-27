using System.Diagnostics;
using System.Text.Json;

namespace DiskLens.Core;

/// 每个扫描范围最近一次成功扫描的摘要（dise §3.2），存成一个带版本号的 JSON 文件，位置由宿主定（DESIGN §2.1）。
/// - 范围按规整后的根路径认，不区分大小写；同一范围只留完成时刻最晚的一次。
/// - 第 2 版起顺手记下根下面的大项和认出的应用，并在覆盖前和同一范围的上一次对照（启动页的「缓存对照」）；第 1 版的文件照读，对照字段为 null。
/// - 最多 MaxScopes 个范围、文件不超过 MaxBytes，超了丢最旧的。
/// - 写：同目录的临时文件写完刷盘，再改名覆盖正式文件（原子替换），谁读都只会看到完整的旧文件或完整的新文件；
///   残留的临时文件（上次写到一半、或替换没成）不读，下次保存直接覆盖它。
/// - 读：损坏、版本不符、超限的文件当没有，改名成 .bad 留底（只留最近一个）。
/// 公开方法都不抛：存不下只记警告，不影响扫描结果，更不能让程序启动失败。
public sealed class SummaryStore(string path)
{
    public const int Version = 2, MaxScopes = 32, MaxBytes = 1024 * 1024, MaxTop = 12, MaxApps = 400, MaxRemoved = 50;

    // ponytail: 进程内一把全局锁，指向同一文件的几个实例也互斥。跨进程只靠原子替换保证文件完好：
    // 另一个进程正开着正式文件时替换会失败（只记警告，这次没存上），同时保存时后写的赢；要不丢得加跨进程锁
    static readonly object Gate = new();

    sealed record FileDto(int Version, SavedSummaryDto[]? Summaries);

    public string FilePath { get; } = path;

    /// 已保存的摘要，完成时刻新的在前；没有文件、读不了、文件坏了都是空数组
    public SavedSummaryDto[] List()
    {
        try { lock (Gate) return Load() ?? []; }
        catch (Exception ex)
        {
            Trace.TraceWarning("读取扫描摘要失败（{0}）：{1}", FilePath, ex);
            return [];
        }
    }

    /// 记下一次成功扫描的摘要（取消、失败的扫描不要调）；同一范围已有更晚完成的就不动。存不下记警告、返回 false。
    /// 给了树就顺手记下根下面的大项和认出的应用，和同一范围的上一次对照
    public bool Save(ScanSummaryDto summary, FileTree? tree = null)
    {
        try
        {
            var (top, apps) = tree == null ? default : Capture(tree);
            var entry = new SavedSummaryDto(summary.RootPath, summary.Mode, summary.FileSystem, summary.IsVolumeRoot,
                summary.CompletedAtMs, summary.ElapsedMs, summary.Files, summary.Dirs, summary.ScannedAlloc, summary.ScannedSize,
                summary.VolumeTotal, summary.VolumeFree, summary.PurposeBytes ?? new long[Purposes.Count], summary.InaccessibleDirs,
                summary.LinksSkipped, summary.MountPointsSkipped, summary.Errors, summary.UnaccountedBytes, summary.GapParts ?? [],
                summary.IsAdmin, top, apps);
            lock (Gate)
            {
                // 文件在、只是这会儿读不了（被占用、没权限）：别拿空列表把别的范围的摘要盖掉
                var list = Load()?.ToList() ?? throw new IOException(L.T("现有的摘要文件读不了", "The existing summary file can't be read", "既存の概要ファイルを読み込めません"));
                int old = list.FindIndex(s => s.RootPath.Equals(entry.RootPath, StringComparison.OrdinalIgnoreCase));
                SavedSummaryDto? previous = null;
                if (old >= 0)
                {
                    // 先完成的扫描晚落盘：别把这个范围更新的一次盖掉
                    if (list[old].CompletedAtMs > entry.CompletedAtMs) return true;
                    previous = list[old];
                    list.RemoveAt(old);
                }
                list.Add(Compare(entry, previous));
                list.Sort((a, b) => b.CompletedAtMs.CompareTo(a.CompletedAtMs));
                if (list.Count > MaxScopes) list.RemoveRange(MaxScopes, list.Count - MaxScopes);
                byte[] bytes;
                while ((bytes = JsonSerializer.SerializeToUtf8Bytes(new FileDto(Version, [.. list]), Json.Options)).Length > MaxBytes)
                    list.RemoveAt(list.Count - 1); // 丢最旧的
                Write(bytes);
                return true;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("扫描摘要没存上（{0}）：{1}", FilePath, ex.Message);
            return false;
        }
    }

    /// 这个路径在不在扫描记录里（记录的范围、认出的应用的位置）：外壳只许打开这些地方
    public bool Knows(string path) => List().Any(s => Same(s.RootPath, path) || (s.Apps ?? []).Any(a => Same(a.Path, path)));

    static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    /// 根下面占用最大的几项、认出的应用（App/Game/Browser 类所属对象）。自己进 tree.Sync
    internal static (SavedEntryDto[] Top, SavedAppDto[] Apps) Capture(FileTree tree)
    {
        lock (tree.Sync)
        {
            var top = tree.LiveChildren(FileTree.RootId).ToArray()
                .Select(n => (Id: n, Alloc: tree.CountedValue(n, Metric.Alloc)))
                .OrderByDescending(x => x.Alloc).ThenBy(x => x.Id).Take(MaxTop)
                .Select(x => new SavedEntryDto(tree.GetName(x.Id), tree.IsDir(x.Id), x.Alloc, tree.IsDir(x.Id) ? tree.Files(x.Id) : 1, null))
                .ToArray();
            var apps = new List<SavedAppDto>();
            foreach (var group in tree.OwnerRoots.Where(r => !tree.IsDeleted(r.Key)).GroupBy(r => r.Value.Owner))
            {
                var owner = tree.Owners[group.Key];
                if (owner.Kind is not (OwnerKind.App or OwnerKind.Game or OwnerKind.Browser)) continue;
                // 同一对象的根互相嵌套时只算外层，不然占用算两遍
                var roots = group.Select(r => r.Key).Where(n => !InsideSameOwner(tree, n, group.Key)).ToList();
                if (roots.Count == 0) continue;
                var installed = roots.Where(n => tree.OwnerEvidenceAt(n) == Evidence.Confirmed && tree.PurposeEvidence(n) == Evidence.Confirmed
                    && tree.Role(n) is Roles.Program or Roles.Game).ToList();
                int shown = (installed.Count > 0 ? installed : roots).MaxBy(n => tree.CountedValue(n, Metric.Alloc));
                apps.Add(new(owner.Kind, owner.Name, tree.GetPath(shown), roots.Sum(n => tree.CountedValue(n, Metric.Alloc)),
                    tree.Role(shown), tree.OwnerEvidenceAt(shown), installed.Count > 0, 0, false));
            }
            // ponytail: 只留占用最大的 MaxApps 个；上一次被挤出名单的应用这次会被当成「新出现」。真有人一个范围认出几百个应用再说
            return (top, [.. apps.OrderByDescending(a => a.Alloc).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Take(MaxApps)]);
        }
    }

    static bool InsideSameOwner(FileTree tree, int root, int owner)
    {
        for (int x = tree.Parent(root); x != FileTree.NoNode; x = tree.Parent(x))
            if (tree.OwnerRoots.TryGetValue(x, out var r) && r.Owner == owner) return true;
        return false;
    }

    /// 和同一范围的上一次对照：大项的占用变化、应用第一次出现的时刻、新出现和消失的应用
    internal static SavedSummaryDto Compare(SavedSummaryDto e, SavedSummaryDto? prev)
    {
        // 应用只和同模式、同权限、也记了应用的上一次比：MFT 和常规扫描、管理员和标准权限认得出的不一样，硬比会冒出一堆假的「新应用」
        bool comparable = prev?.Apps != null && e.Apps != null && prev.Mode == e.Mode && prev.IsAdmin == e.IsAdmin;
        var seen = prev?.Apps?.GroupBy(AppKey, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // 上一次就有的：沿用第一次出现的时刻和「对照出来的新应用」这个结论，不然启动页「最近一周的新应用」一重扫就没了。
        // 旧版本只在发现那条记录上标新、没记 AbsentAtMs：那条记录的对照时刻就是它还没有的那次
        var apps = e.Apps?.Select(a => seen != null && seen.TryGetValue(AppKey(a), out var p)
                ? a with { FirstSeenMs = p.FirstSeenMs, IsNew = p.IsNew, AbsentAtMs = p.IsNew ? p.AbsentAtMs ?? prev!.AppsComparedAtMs : null }
                : a with { FirstSeenMs = e.CompletedAtMs, IsNew = comparable, AbsentAtMs = comparable ? prev!.CompletedAtMs : null }).ToArray();
        if (prev == null) return e with { Apps = apps };
        var before = prev.TopEntries?.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Alloc, StringComparer.OrdinalIgnoreCase);
        var now = apps?.Select(AppKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return e with
        {
            TopEntries = e.TopEntries?.Select(t => t with { PreviousAlloc = before != null && before.TryGetValue(t.Name, out long b) ? b : null }).ToArray(),
            Apps = apps,
            RemovedApps = comparable ? [.. prev.Apps!.Where(p => !now!.Contains(AppKey(p))).Take(MaxRemoved).Select(p => p with { IsNew = false, AbsentAtMs = null })] : null,
            PreviousAtMs = prev.CompletedAtMs,
            PreviousAlloc = prev.ScannedAlloc,
            AppsComparedAtMs = comparable ? prev.CompletedAtMs : null,
        };
    }

    static string AppKey(SavedAppDto a) => $"{a.Kind}:{a.Name}";

    void Write(byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true); // 先落盘再改名：断电后不会换上一个空文件
        }
        File.Move(temp, FilePath, overwrite: true);
    }

    /// null：文件在，但这会儿读不了（被占用、没权限）
    SavedSummaryDto[]? Load()
    {
        FileDto? file = null;
        string? bad = null;
        try
        {
            // 共享给足：读的时候不挡别的进程读、改名留底。注意改名覆盖仍会被任何开着的句柄挡住（见类注释）
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes) bad = $"超过 {MaxBytes / 1024} KB";
            else file = JsonSerializer.Deserialize<FileDto>(stream, Json.Options);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return []; }
        catch (JsonException ex) { bad = "不是有效的 JSON：" + ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("扫描摘要文件这会儿读不了（{0}）：{1}", FilePath, ex.Message);
            return null;
        }
        bad ??= file?.Version is not (1 or Version) ? $"版本 {file?.Version} 不是 1 或 {Version}"
            : !Valid(file.Summaries) ? "内容不完整、范围重复或超过上限" : null;
        return bad != null ? Quarantine(bad) : [.. file!.Summaries!.OrderByDescending(s => s.CompletedAtMs)];
    }

    static bool Valid(SavedSummaryDto[]? items) =>
        items is { Length: <= MaxScopes }
        && items.All(s => s is { RootPath.Length: > 0, FileSystem: not null, PurposeBytes.Length: Purposes.Count, GapParts: not null }
            && Enum.IsDefined(s.Mode) && s.GapParts.All(p => p is { Basis: not null } && Enum.IsDefined(p.Kind))
            && (s.TopEntries ?? []).All(t => t is { Name: not null })
            && (s.Apps ?? []).Concat(s.RemovedApps ?? []).All(a => a is { Name: not null, Path: not null } && Enum.IsDefined(a.Kind) && Enum.IsDefined(a.Evidence)))
        && items.DistinctBy(s => s.RootPath, StringComparer.OrdinalIgnoreCase).Count() == items.Length;

    /// 坏文件改名成 .bad 留底（只留最近一个），当作没有
    SavedSummaryDto[] Quarantine(string why)
    {
        Trace.TraceWarning("扫描摘要文件不可用（{0}），改名为 .bad 留底：{1}", why, FilePath);
        try { File.Move(FilePath, FilePath + ".bad", overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("坏的扫描摘要文件没能改名：{0}", ex.Message); }
        return [];
    }
}
