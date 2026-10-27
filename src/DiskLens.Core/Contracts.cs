using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskLens.Core;

// 前后端契约。改这里必须同步 web/src/lib/types.ts，不然前端只会安静地拿到 undefined。

public enum Metric { Alloc, Size }

public enum ScanMode { Auto, Mft, Walk }

public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }
}

// ---------- app / drives ----------

/// <param name="HomePath">用户配置文件目录（%USERPROFILE%），启动页「扫描主文件夹」和面包屑里的 ~ 用</param>
/// <param name="Language">现在用的界面语言（zh-CN / en / ja）：保存过的选择，没有就是按系统语言定的</param>
public sealed record AppInfoDto(string Version, bool IsAdmin, string OsVersion, int ProcessorCount,
    string? AutoScanPath, ScanMode? AutoScanMode, string? HomePath = null, string Language = "zh-CN");

/// app.setLanguage：zh-CN / en / ja；换掉后台发给界面的文字，并存进 settings.json，下次启动默认就是它
public sealed record LanguageRequest(string? Language);

/// <param name="DriveType">fixed | removable | network | cdrom | ram | unknown</param>
/// <param name="CanMft">现在就能深度扫描（已提权 + 本地 NTFS）</param>
/// <param name="SupportsMft">提权后能深度扫描（本地 NTFS，与是否已提权无关）——启动页决定要不要弹提权</param>
public sealed record DriveInfoDto(string Root, string Label, string FileSystem, string DriveType,
    long TotalBytes, long FreeBytes, bool IsReady, bool? IsSsd, bool CanMft, bool IsSystem, bool SupportsMft = false);

public sealed record OkDto(bool Ok, string? Message = null);
public sealed record PathDto(string? Path);
public sealed record RelaunchRequest(string? ScanPath = null, ScanMode? Mode = null);

// ---------- scan ----------

public sealed record ScanStartRequest(string Path, ScanMode Mode = ScanMode.Auto);
public sealed record ScanStartedDto(int ScanId, ScanMode Mode);

/// 开始时、每次阶段变化、取消被接受时各立即发一条，其余每 120 ms 采样一条；终态事件（scan.done / scan.error）之后不再有进度。
/// <param name="Phase">prepare | mft | walk | tree | links | finalize</param>
/// <param name="Percent">0..1，未知为 null</param>
/// <param name="CategoryBytes">按类别 K 累计的 alloc，长度 FileCategories.Count</param>
/// <param name="Cancelling">取消已被接受、后台还没停下（Phase 停在被打断的那一步）；真正停下后才发 scan.error（cancelled=true）。
/// 卡在系统 IO 里停不下来时就一直是 true，不提前说已停止</param>
public sealed record ScanProgressDto(int ScanId, ScanMode Mode, string Phase, long Files, long Dirs, long Bytes,
    long UsedBytes, double? Percent, string? CurrentPath, long ElapsedMs, long[] CategoryBytes, bool Cancelling = false);

/// 扫描报告里的异常种类（dise §4.3）：汇总里各有一个计数，scan.issues 按种类取清单。
public enum ScanIssueKind { Denied, Link, MountPoint, Error }

/// 未解释容量里的一份。NtfsMetadata：常规扫描读不到的 NTFS 元数据，按文件系统报告的 $MFT 大小算，是下限；
/// Unconfirmed：有依据的部分扣掉之后的余数，原因未确认——不归因于权限，也不归因于别的什么。
public enum GapKind { Unconfirmed, NtfsMetadata }

/// <param name="Basis">依据，给人看的一句话</param>
public sealed record GapPartDto(GapKind Kind, long Bytes, string Basis);

/// 扫描报告的四项（dise §4.3）：InaccessibleDirs、LinksSkipped + MountPointsSkipped、Errors、UnaccountedBytes（拆成 GapParts）。
/// 前三项是扫描时的数字，删除后不重算，和 scan.issues 的 Total 一一相等；未解释容量跟着卷空间和删除实时刷新。
/// <param name="InaccessibleDirs">发现的拒绝访问入口：读不了的目录各算一个，里面还有多少东西不知道，所以它不是「没读到的目录总数」。MFT 扫描不看目录权限，恒为 0</param>
/// <param name="LinksSkipped">按规则没进入的链接：联接点（junction）、目录符号链接、文件系统没给出类型的重解析目录</param>
/// <param name="UnaccountedBytes">未解释容量 = 卷已用 − 已统计；只在扫卷根时有，扫文件夹恒为 0</param>
/// <param name="PurposeBytes">整棵树按用途 U 汇总的 alloc，长度 Purposes.Count</param>
/// <param name="MountPointsSkipped">按规则没进入的卷挂载点：这里挂着另一个卷，内容不属于本卷</param>
/// <param name="Errors">读取失败及其他错误，拒绝访问不算在内：共享冲突、路径过长、设备错误、扫描期间被删、硬链接无法核实、MFT 损坏记录和孤立记录</param>
/// <param name="GapParts">UnaccountedBytes 的拆分，各份之和等于它；没有差额时为空数组</param>
/// <param name="CompletedAtMs">扫描完成时刻（Unix 毫秒）：结果换上那一刻、和树在同一把锁里记下。最近文件的时间基准、
/// 已保存摘要的时间都是它；删除后刷新汇总不变</param>
public sealed record ScanSummaryDto(int ScanId, string RootPath, int RootId, ScanMode Mode, string FileSystem,
    bool IsVolumeRoot, bool IsAdmin, long VolumeTotal, long VolumeFree, long VolumeUsed,
    long ScannedAlloc, long ScannedSize, long Files, long Dirs, long InaccessibleDirs, long LinksSkipped,
    long HardLinkDups, long HardLinkSavedBytes, long UnaccountedBytes, long ElapsedMs,
    long[] CategoryBytes, string[] Warnings, int TreeVersion, long[]? PurposeBytes = null,
    long MountPointsSkipped = 0, long Errors = 0, GapPartDto[]? GapParts = null, long CompletedAtMs = 0);

/// 某个扫描范围最近一次成功完成的扫描摘要（dise §3.2）：SummaryStore 存在本机，重启后还在；scan.savedSummaries 按完成时刻新到旧给出。
/// 它只是「上次扫描结果」：不带节点 ID、扫描号、树版本这类只在那次运行里有效的东西，未重新扫描、拿到新的扫描结果之前，
/// 不能拿它导航、查详情或删除。各字段同 ScanSummaryDto 扫描完成那一刻的值，之后的删除不改它。
/// <param name="RootPath">扫描范围：规整后的根路径。范围按它认、不区分大小写，同一范围只留完成时刻最晚的一次</param>
/// <param name="CompletedAtMs">扫描完成时刻（Unix 毫秒），即 ScanSummaryDto.CompletedAtMs</param>
/// <param name="PurposeBytes">按用途 U 汇总的 alloc，长度 Purposes.Count</param>
/// 下面几项是第 2 版才有的「缓存对照」，旧版本文件里读出来是 null：
/// <param name="IsAdmin">扫描时是不是管理员：权限不同认出的应用不一样，只和同模式、同权限的上一次对照应用</param>
/// <param name="TopEntries">根下面占用最大的几项，带上一次扫描同一范围时的占用</param>
/// <param name="Apps">认出的应用（App/Game/Browser 类所属对象），占用大的在前，最多 SummaryStore.MaxApps 个</param>
/// <param name="RemovedApps">上一次还在、这次没认出的应用（卸载、搬走，或这次没读到）</param>
/// <param name="PreviousAtMs">同一范围上一次扫描的完成时刻；第一次扫描这个范围就是 null</param>
/// <param name="PreviousAlloc">同一范围上一次扫描的已统计占用</param>
/// <param name="AppsComparedAtMs">应用是和哪一次对照的（上一次的完成时刻）；没有可比的上一次（第一次、旧版本记录、扫描方式或权限不同）是 null，这时谁都不算新</param>
public sealed record SavedSummaryDto(string RootPath, ScanMode Mode, string FileSystem, bool IsVolumeRoot, long CompletedAtMs,
    long ElapsedMs, long Files, long Dirs, long ScannedAlloc, long ScannedSize, long VolumeTotal, long VolumeFree,
    long[] PurposeBytes, long InaccessibleDirs, long LinksSkipped, long MountPointsSkipped, long Errors,
    long UnaccountedBytes, GapPartDto[] GapParts, bool? IsAdmin = null, SavedEntryDto[]? TopEntries = null, SavedAppDto[]? Apps = null,
    SavedAppDto[]? RemovedApps = null, long? PreviousAtMs = null, long? PreviousAlloc = null, long? AppsComparedAtMs = null);

/// 扫描记录里根下面的一项。
/// <param name="PreviousAlloc">上一次扫描同一范围时它的占用；上一次不在前列、或者没有上一次，就是 null</param>
public sealed record SavedEntryDto(string Name, bool IsDir, long Alloc, long Files, long? PreviousAlloc);

/// 扫描记录里认出的一个应用。
/// <param name="Path">显示用的那一处根：优先已确认的安装位置，否则占用最大的一处</param>
/// <param name="Alloc">这个对象各处根（同一对象嵌套的只算外层）的子树占用之和；嵌在里面的别的对象（Steam 里的游戏）也算在内</param>
/// <param name="Role">显示那一处根的数据角色（Roles）</param>
/// <param name="Evidence">显示那一处根归到这个对象名下的证据</param>
/// <param name="Installation">显示那一处是已确认的程序或游戏目录（安装位置）</param>
/// <param name="FirstSeenMs">同一范围的扫描记录里第一次认出它的完成时刻；不同范围各算各的</param>
/// <param name="IsNew">对照出来的新应用：某次和可比的上一次对照时它还没有。之后的扫描一直带着这个结论（启动页再按 FirstSeenMs 只列最近一周的），
/// 不会因为又扫了一次就丢</param>
/// <param name="AbsentAtMs">对照时它还没有的那次扫描的完成时刻；IsNew 为 false 时是 null</param>
public sealed record SavedAppDto(OwnerKind Kind, string Name, string Path, long Alloc, byte Role, Evidence Evidence, bool Installation,
    long FirstSeenMs, bool IsNew, long? AbsentAtMs = null);

/// 在资源管理器里打开扫描记录里的一个位置（记录的范围、认出的应用）；不在记录里的路径一律拒绝。
public sealed record OpenFolderRequest(string Path);

public sealed record ScanErrorDto(int ScanId, string Message, bool Cancelled);

/// <param name="ScanId">要哪次扫描的清单；null 表示当前结果。和当前结果对不上（换过扫描）就报错，不拿别的扫描充数</param>
/// <param name="Limit">夹在 1..1000</param>
public sealed record ScanIssuesRequest(ScanIssueKind Kind, int Offset = 0, int Limit = 200, int? ScanId = null);

public sealed record ScanIssueDto(string Path, string Reason);

/// 一页异常清单（dise §4.3）。扫描时每类最多记 ScanCounters.MaxIssues 条，按路径排序。
/// <param name="Total">这一类的总数，等于汇总里对应的计数，不受记录上限影响</param>
/// <param name="Recorded">扫描时实际记下的条数；小于 Total 说明超出上限的那些只计了数，没有路径可给</param>
/// <param name="Items">本页：已记下的第 Offset 条起，最多 Limit 条</param>
/// <param name="Truncated">本页之后还有没展示的（Offset + Items.Length &lt; Total）：要么还能往后翻，要么根本没记下——不能当完整清单展示</param>
public sealed record ScanIssuesDto(int ScanId, ScanIssueKind Kind, long Total, int Recorded, int Offset, ScanIssueDto[] Items,
    bool Truncated);

// ---------- tree ----------

public sealed record NodeRequest(int NodeId);
public sealed record NodeIdsRequest(int[] NodeIds);
public sealed record ViewRequest(int NodeId, Metric Metric = Metric.Alloc, int MaxNodes = 12000);
public sealed record DetailsRequest(int NodeId, Metric Metric = Metric.Alloc);
public sealed record LargestRequest(int NodeId, Metric Metric = Metric.Alloc, int Limit = 100);

// ---------- children ----------

/// 目录列表（dise §5.2）的排序。Name：目录在前、名字不区分大小写；Size：按 Metric 的计入值大的在前；
/// Modified：修改时间新的在前，未知的排最后。并列一律按 Id 升序。
public enum ChildSort { Name, Size, Modified }

/// 一个目录的全部直接子项，分页给：地图把小块并成「其余 N 项」，列表里每一项都在。
/// <param name="NodeId">不存在或已删除抛 ArgumentException</param>
/// <param name="Offset">夹在 0..Total</param>
/// <param name="Limit">夹在 1..500</param>
public sealed record ChildrenRequest(int NodeId, Metric Metric = Metric.Alloc, ChildSort Sort = ChildSort.Size, int Offset = 0, int Limit = 200);

/// <param name="Value">按请求度量的计入值，和地图同口径（硬链接副本计 0）</param>
/// <param name="ModifiedMs">扫描时读到的修改时间，Unix 毫秒；未知为 null</param>
/// <param name="U">主用途（Purposes），列表的圆点和地图同色</param>
/// <param name="ChildCount">目录的未删除直接子项数；文件为 0</param>
public sealed record ChildEntryDto(int Id, string Name, bool IsDir, long Value, long Size, long Alloc, long? ModifiedMs,
    int K, int F, int U, int ChildCount);

/// <param name="Total">未删除的直接子项总数，不受分页影响</param>
public sealed record ChildrenDto(int NodeId, Metric Metric, ChildSort Sort, int Total, int Offset, ChildEntryDto[] Items);

/// 视图节点用短字段名：一次几万个，省一半 JSON。
/// <param name="Id">树节点 ID；聚合节点为 -1</param>
/// <param name="P">父节点在 Nodes 数组里的下标，根为 -1</param>
/// <param name="N">名字</param>
/// <param name="V">按请求度量的值</param>
/// <param name="F">NodeFlags | ViewFlags</param>
/// <param name="K">类别</param>
/// <param name="C">未删除的直接子节点总数（聚合节点为被合并的条目数）</param>
/// <param name="U">用途类别（Purposes），treemap 的「类别」模式按它着色</param>
public sealed record ViewNodeDto(int Id, int P, string N, long V, int F, int K, int C, int U = 0);

public sealed record ViewDto(int RootId, Metric Metric, long Total, int TreeVersion, ViewNodeDto[] Nodes);

public sealed record AncestorDto(int Id, string Name);

public sealed record ChildItemDto(int Id, string Name, bool IsDir, long Size, long Alloc, int K, int F, long Files,
    int U = 0);

/// <param name="Ext">扩展名；"*" 表示前 15 名之外合并成的一行（K=0，排在最后）</param>
/// <param name="Count">文件条目数（硬链接的每个链接都算一条，和 Files 同口径）</param>
public sealed record ExtStatDto(string Ext, int K, long Size, long Alloc, long Count);

/// 所属对象（dise §8.1）：节点最近的那个。RootId / RootPath 是这个对象在本次扫描里包含该节点的那个根；
/// Evidence 是这一处归属的证据——按目录名推测的就是 inferred，哪怕同名对象在别处是确认过的。
public sealed record OwnerDto(OwnerKind Kind, string Name, int RootId, string RootPath, Evidence Evidence);

/// <param name="ModifiedMs">Unix 毫秒；未知为 null（CreatedMs、AccessedMs 同理，详情接口实时读取）</param>
/// <param name="Ancestors">从树根到直接父节点（不含自己），顺序是根在前</param>
/// <param name="Attributes">中文属性标签，如「隐藏」「硬链接」</param>
/// <param name="CategoryBytes">子树按类别 K 的值（按请求度量），长度 FileCategories.Count</param>
/// <param name="PurposeBasis">用途、角色、所属对象各自的依据（给人看的一段话）</param>
/// <param name="Role">数据角色（Roles）</param>
/// <param name="Evidence">用途的证据等级</param>
/// <param name="Owner">最近的所属对象；没有可靠依据时为 null</param>
public sealed record NodeDetailsDto(int Id, string Name, string Path, bool IsDir, int K, int F, string[] Attributes,
    long Size, long Alloc, long Files, long Dirs, long? ModifiedMs, long? CreatedMs, long? AccessedMs,
    double PercentOfParent, double PercentOfRoot, AncestorDto[] Ancestors, ChildItemDto[] TopChildren, int ChildCount,
    ExtStatDto[] ExtStats, long[] CategoryBytes, bool IsProtected, string? ProtectReason, string? Warning,
    string? LinkTarget, int HardLinkCount, string? HardLinkCountedAt, int TreeVersion,
    int U = 0, long[]? PurposeBytes = null, string? PurposeBasis = null,
    int Role = 0, Evidence Evidence = Evidence.Unknown, OwnerDto? Owner = null);

/// <param name="U">用途类别，搜索结果和大文件列表的圆点要和 treemap 同色</param>
public sealed record FileItemDto(int Id, string Name, string Path, bool IsDir, long Size, long Alloc, long? ModifiedMs,
    int K, int F, int U = 0);

// ---------- recent ----------

/// Size：按 Metric 的值大的在前；Modified：修改时间新的在前。并列一律按 Id 升序。
public enum RecentSort { Size, Modified }

/// 最近文件（dise §6）：时间窗口内修改过的文件。修改时间只说明文件最后一次被改是什么时候，不等于写入量
/// （20 GB 的文件最近改过不代表最近写了 20 GB），也不代表新增、下载或增长——复制、恢复会保留原修改时间。
/// 排除目录、虚拟节点、硬链接副本（一组硬链接只出计入的那一份）。
/// <param name="Days">窗口天数，夹在 1..3650</param>
/// <param name="ScopeId">只看它的子树（含自己）；null 表示整棵树；不存在或已删除抛 ArgumentException</param>
/// <param name="Limit">夹在 1..1000</param>
public sealed record RecentRequest(int Days = 7, int? ScopeId = null, RecentSort Sort = RecentSort.Size,
    Metric Metric = Metric.Alloc, int Limit = 100);

/// <param name="DirPath">所在目录的路径</param>
/// <param name="ModifiedMs">扫描时读到的修改时间，Unix 毫秒</param>
/// <param name="U">主用途（Purposes）</param>
/// <param name="Role">数据角色（Roles）</param>
/// <param name="Owner">最近的所属对象（名字、种类、这一处归属的证据）；没有可靠依据时为 null</param>
public sealed record RecentFileDto(int Id, string Name, string Path, string DirPath, long Size, long Alloc, long ModifiedMs,
    int K, int F, int U, int Role, OwnerDto? Owner);

/// <param name="AsOfMs">时间基准：扫描完成时刻（Unix 毫秒），由桥接层给，不是查询当下——同一份扫描结果什么时候查都一样。
/// 窗口按毫秒比：ModifiedMs 落在闭区间 [AsOfMs − Days × 86 400 000, AsOfMs] 里的算，恰好 Days 天前改的也算；
/// 晚于基准的（时钟不准留下的未来时间）和修改时间未知的不算</param>
/// <param name="Days">实际用的天数（夹过之后的）</param>
/// <param name="Total">窗口内符合条件的文件总数，不受 Limit 影响；大于 Items.Length 说明被截断了</param>
public sealed record RecentFilesDto(long AsOfMs, int Days, long Total, RecentFileDto[] Items);

// ---------- owner usage ----------

/// 按所属对象聚合（dise §5.3 的「应用占用」浏览维度）。只覆盖当前扫描范围（或作用域子树）——字段名里的 InScope 就是这个意思：
/// 这不是对象在整台机器上的总占用，范围之外的部分根本没扫到，界面上也别这么叫。
/// <param name="ScopeId">只看它的子树（含自己）；null 表示整棵树；不存在或已删除抛 ArgumentException</param>
/// <param name="Metric">排序和 RoleBytesInScope 用的度量；内容大小、实际占用两种字节数都会给</param>
/// <param name="Limit">最多返回几个对象，夹在 1..1000</param>
public sealed record OwnerUsageRequest(int? ScopeId = null, Metric Metric = Metric.Alloc, int Limit = 100);

/// 对象在树里的一处根。
/// <param name="Id">根节点。作用域整个落在这个对象里时，它在作用域上方（不在作用域里），字节数仍只算作用域里的那部分</param>
/// <param name="SizeInScope">这一处在当前范围内计入的字节：根的子树减去嵌套在里面的别的根（哪个对象的都算），不含共享容量</param>
/// <param name="Evidence">这一处归属的证据（按目录名并进来的是推测）</param>
public sealed record OwnerUsageRootDto(int Id, string Path, long SizeInScope, long AllocInScope, Evidence Evidence);

/// <param name="Evidence">对象本身认得多准（几处来源里最强的）；每一处根自己的证据看 Roots</param>
/// <param name="SizeInScope">当前范围内的计入大小：各处根的子树之和，减去嵌套在里面的别的对象的根；硬链接只算计入的那一份，
/// 跨对象的硬链接组单列在 OwnerUsageDto.SharedSizeInScope，不在这里</param>
/// <param name="RoleBytesInScope">按数据角色（Roles）分的构成，按请求度量，长度 Roles.Count；之和等于这个度量下的计入大小</param>
/// <param name="Roots">碰到当前范围的各处根，按请求度量大的在前，并列按 Id 升序</param>
public sealed record OwnerUsageItemDto(OwnerKind Kind, string Name, Evidence Evidence, long SizeInScope, long AllocInScope,
    long[] RoleBytesInScope, OwnerUsageRootDto[] Roots);

/// 当前范围内按所属对象的汇总。每个字节只记一次，两种度量各自满足
/// OwnedInScope + SharedInScope + UnownedInScope = TotalInScope：
/// 节点归从它往上最近的那个根所属的对象；目录自身的占用（大目录的索引）也算在它所属的对象上，和地图上目录的大小同口径；
/// 硬链接副本不算。计入的那一份所在的硬链接组里，活着的链接（范围外的也看）不全归同一个对象时（一边归某个对象、
/// 另一边不归任何对象也算），这份字节没法可靠地分给哪一方，记进共享，不硬塞给计入那一份所在的对象。
/// <param name="TotalSizeInScope">范围总量：作用域节点的计入值，和地图上它的大小一致（作用域本身是硬链接副本时为 0）</param>
/// <param name="OwnedSizeInScope">所有对象（不受 Limit 影响）的计入大小之和</param>
/// <param name="SharedSizeInScope">无法可靠分摊的共享容量；没有就是 0</param>
/// <param name="UnownedSizeInScope">不属于任何所属对象的字节，含「未统计」虚拟叶子</param>
/// <param name="OwnerCount">碰到当前范围的对象个数（计入为 0 的也算），不受 Limit 影响；大于 Items.Length 说明被截断了</param>
/// <param name="Items">按请求度量的计入大小降序，并列按名字（不区分大小写）、再按种类</param>
public sealed record OwnerUsageDto(int ScopeId, long TotalSizeInScope, long TotalAllocInScope, long OwnedSizeInScope, long OwnedAllocInScope,
    long SharedSizeInScope, long SharedAllocInScope, long UnownedSizeInScope, long UnownedAllocInScope, int OwnerCount, OwnerUsageItemDto[] Items);

// ---------- related ----------

/// 关联关系（dise §8.1）。只出有依据的三种，不按名字像、时间近去猜：
/// Root：节点所在项目或应用（所属对象）的那处根，节点就在它的子树里；
/// OtherRoot：同一所属对象的另一处根（Program Files 里的程序目录和 AppData 里的那份、游戏库和存档目录……）；
/// RoleModule：同一处根下角色不同的模块——目录的角色和上级不同，即角色从这一层开始换了（插件、配置、缓存、日志……）。
public enum RelationKind { Root, OtherRoot, RoleModule }

/// 关联详情（dise §8.1）：节点的所属对象，以及同一对象在当前扫描范围里的其他模块。
/// 关联只用于浏览和定位，不扩大清理范围：属于同一个应用不等于可以整组删除，清理建议和删除保护都不读这里。
/// <param name="NodeId">不存在或已删除抛 ArgumentException</param>
/// <param name="Metric">排序用的度量；内容大小、实际占用两种字节数都会给</param>
/// <param name="Limit">最多返回几条，夹在 1..1000</param>
public sealed record RelatedRequest(int NodeId, Metric Metric = Metric.Alloc, int Limit = 50);

/// <param name="Size">节点在地图上的大小（整棵子树，嵌在里面的别的对象也算在内）；Alloc 同理</param>
/// <param name="Role">数据角色（Roles）</param>
/// <param name="Evidence">这条关联的证据，取两处归属里弱的那个：按目录名并进来的那处根只是推测，和它的关联也只能算推测；
/// 所在的根和它下面的模块就是节点所在那处根的证据</param>
public sealed record RelatedItemDto(int Id, string Name, string Path, bool IsDir, long Size, long Alloc, int Role,
    RelationKind Relation, Evidence Evidence);

/// <param name="Owner">节点最近的所属对象；没有为 null，这时 Items 一定是空的</param>
/// <param name="Total">找到的关联总数，不受 Limit 和每种角色的代表个数影响；大于 Items.Length 说明截断了</param>
/// <param name="Items">依次是：所在的根（节点自己就是根时没有）；另几处根，按度量降序；各角色的模块——只看所在那处根自己的地盘
/// （嵌在里面的别的根整棵不算，同一对象的那处已经作为另一处根列过），不含节点自己和包含它的目录，角色未知的不算；
/// 每种角色只列最大的 3 个，组按组里最大的那个降序。并列一律按 Id（角色组按角色编号）升序</param>
/// <param name="Note">Items 为空时是「尚未识别关联对象」，否则为 null：没有可靠关联就直说，不编造</param>
public sealed record RelatedDto(int NodeId, OwnerDto? Owner, int Total, RelatedItemDto[] Items, string? Note);

// ---------- search ----------

/// 搜索结果的排法：按占用（同地图的口径）或按相关度（名字整个就是它 > 开头 > 词首 > 中间，同档再按占用）。
public enum SearchSort { Size, Relevance }

/// <param name="ScopeId">只搜它的严格子孙（不含自己）；不存在或已删除抛 ArgumentException</param>
/// <param name="Limit">最多返回几条，上限 1000；≤ 0 表示只数个数</param>
/// <param name="Smart">智能匹配：名字里的汉字也按拼音首字母对（wd 找得到「文档」）；一条精确结果都没有时，
/// 把名字词放宽成「字符按顺序出现、挨得够近」再找一遍（结果标 Fuzzy，按相似度排）</param>
/// 空白或只有空引号的 Query 返回空结果且没有 Error；根节点（Id 0）是扫描目标本身，不参与匹配。
public sealed record SearchRequest(string Query, int? ScopeId = null, Metric Metric = Metric.Alloc, int Limit = 300,
    bool Smart = false, SearchSort Sort = SearchSort.Size);

/// <param name="Fuzzy">精确匹配一条都没有，这是放宽后的近似结果（只在 Smart 时出现）</param>
public sealed record SearchResultDto(string Query, long Total, int TookMs, bool Truncated, FileItemDto[] Items,
    string? Error, bool Fuzzy = false);

// ---------- cleanup ----------

/// <param name="Group">safe | caution | protected</param>
/// <param name="Action">deleteContents | deleteNodes | emptyRecycleBin | command | openSettings | info</param>
/// <param name="Command">action=command 时显示给用户的命令</param>
/// <param name="Target">action=openSettings 时的 URI 或程序名</param>
/// <param name="Selectable">条目可以勾选（只删一部分）</param>
/// <param name="DisplayPath">给人看的位置，用户目录前缀写成 ~，多个位置用「, 」连接（参考图里那行灰色小字）</param>
public sealed record SuggestionDto(string Id, string Group, string Title, string Description, long Bytes, long Count,
    string Action, string? Command, string? Target, bool RequiresAdmin, bool DefaultPermanent, bool Selectable,
    int[] NodeIds, FileItemDto[] Items, string? DisplayPath = null);

public sealed record CleanupRunRequest(string SuggestionId, int[]? NodeIds = null, bool? Permanent = null,
    string? ConfirmationToken = null, string? OperationId = null);

public sealed record CleanupCheckDto(string ConfirmationToken, DeleteCheckDto? DeleteCheck = null);

/// 选中的一条建议；NodeIds 为空表示整条建议，不为空必须是该建议 NodeIds 的子集。
public sealed record CleanupSelection(string SuggestionId, int[]? NodeIds = null);

public sealed record CleanupScriptRequest(CleanupSelection[] Selections);

/// <param name="Script">PowerShell 脚本全文（含注释头）；保留扫描时的绝对路径</param>
/// <param name="NeedsAdmin">任一选中项需要管理员 PowerShell</param>
public sealed record CleanupScriptDto(string Script, long Bytes, int Items, bool NeedsAdmin);

/// <param name="ScanStartedAtMs">那次扫描开始的时刻（Unix 毫秒，宿主已留出余量）；清理脚本靠它认出扫描之后新建、改过、挪进来的东西，0 表示不知道</param>
/// <param name="FileSystem">扫描范围所在卷的文件系统（NTFS、ReFS、exFAT…）；只有记 ChangeTime 的卷，脚本才敢清目录</param>
public sealed record CleanupContext(bool IsAdmin, string VolumeRoot, string ScanRoot, IntPtr OwnerHwnd, long ScanStartedAtMs = 0, string? FileSystem = null);

// ---------- node ops ----------

/// <param name="Engine">bing | baidu | google</param>
public sealed record SearchWebRequest(int NodeId, string Engine = "bing");

/// 顶栏的外部搜索：Engine 是 google | bing，Query 是用户自己输入并提交的词
public sealed record WebQueryRequest(string Engine, string Query);

public sealed record DeleteRequest(int[] NodeIds, bool Permanent, string? ConfirmationToken = null, string? OperationId = null);

public sealed record OperationRequest(string OperationId);

/// Total/Completed/Failed/Skipped 以去嵌套后的确认目标为单位；ProcessedEntries 是实际处理的文件和目录数。
/// FreedBytes 在删除对账前为 null；外部终端或设置入口只能报告 external，不表示清理完成。
public sealed record OperationStatusDto(string OperationId, string Kind, string Status, long CreatedAtMs,
    long UpdatedAtMs, string? CurrentPath, int Total, int Completed, int Failed, int Skipped,
    long ProcessedEntries, long? FreedBytes, OpResultDto? Result = null, string? Message = null);

public sealed record DeleteProgressDto(string? CurrentPath, int Total, int Completed, int Failed, int Skipped,
    long ProcessedEntries, long? FreedBytes = null);

public sealed record DeleteCheckItemDto(int Id, string Path, bool IsDir, long Size, long Alloc, long Files,
    bool Blocked, string? Reason, string? Warning);

public sealed record DeleteCheckDto(DeleteCheckItemDto[] Items, long TotalAlloc, long TotalSize, long TotalFiles,
    bool AnyBlocked, bool AnyWarning, string? ConfirmationToken = null);

public sealed record OpErrorDto(string Path, string Message);

/// 扫描之后磁盘上的变化（tree.changed 事件）。数字都是这次扫描以来累计的；ScanId 对不上的旧事件要丢掉。
/// <param name="Watching">在不在跟踪：系统不给这个位置发变化通知时为 false，这时地图不会自己变</param>
/// <param name="RemovedEntries">别的程序删掉、已经从树里标删除的条目数</param>
/// <param name="OtherChanges">新建或改名出来的条目数：不重扫没法加进树，只计数提示「统计可能过期」</param>
/// <param name="Overflow">跟丢过（变化太多、通知中断）：有些删除可能没跟上，建议重扫</param>
public sealed record TreeChangeDto(int ScanId, bool Watching, long RemovedEntries, long OtherChanges, bool Overflow, int TreeVersion);

/// <param name="AlreadyGone">动手前已经不在磁盘上的条目数（多半被别的程序先删了）：树里照样标删除，但不算进 FreedBytes，也不算 Deleted 失败</param>
public sealed record OpResultDto(bool Ok, long FreedBytes, int Deleted, int Failed, OpErrorDto[] Errors,
    string? Message, int TreeVersion, int AlreadyGone = 0);

public readonly record struct ProtectionResult(bool Blocked, string? Reason, string? Warning)
{
    public static readonly ProtectionResult Allowed = new(false, null, null);
}
