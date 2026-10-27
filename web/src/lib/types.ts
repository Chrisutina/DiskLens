// 和 src/DiskLens.Core/Contracts.cs 一一对应（JSON 是 camelCase）。改一边忘了另一边，前端只会默默拿到 undefined。

export type Metric = 'alloc' | 'size';
export type ScanMode = 'auto' | 'mft' | 'walk';
export type Phase = 'prepare' | 'mft' | 'walk' | 'tree' | 'links' | 'finalize';
export type DriveType = 'fixed' | 'removable' | 'network' | 'cdrom' | 'ram' | 'unknown';
export type SearchEngine = 'bing' | 'baidu' | 'google';
export type SuggestionGroup = 'safe' | 'caution' | 'protected';
export type SuggestionAction = 'deleteContents' | 'deleteNodes' | 'emptyRecycleBin' | 'command' | 'openSettings' | 'info';
/** 证据等级（Roles.cs 的 Evidence 枚举）：已确认 / 推测 / 未识别，不出百分比 */
export type Evidence = 'unknown' | 'inferred' | 'confirmed';
/** 所属对象种类（Roles.cs 的 OwnerKind 枚举），中文名见 roles.ts */
export type OwnerKind = 'app' | 'game' | 'plugin' | 'project' | 'packageManager' | 'browser' | 'system' | 'model' | 'dataset' | 'runtime';

/** NodeFlags（树）+ ViewFlags（只在 tree.view 里出现） */
export const Flag = {
  Directory: 1,
  HardLinkDup: 2,
  Reparse: 4,
  Inaccessible: 8,
  Hidden: 16,
  System: 32,
  Compressed: 64,
  Sparse: 128,
  Cloud: 256,
  Metafile: 512,
  Deleted: 1024,
  Virtual: 2048,
  Encrypted: 4096,
  Aggregate: 8192,
  Unexpanded: 16384,
} as const;

export const hasFlag = (f: number, bit: number): boolean => (f & bit) !== 0;

/** 类别 K，名字和颜色见 categories.ts */
export const Cat = {
  Other: 0, Folder: 1, Video: 2, Image: 3, Audio: 4, Document: 5, Archive: 6,
  Code: 7, Executable: 8, System: 9, DiskImage: 10, Data: 11, Unaccounted: 12,
} as const;
export const CATEGORY_COUNT = 13;

export interface AppInfo {
  version: string;
  isAdmin: boolean;
  osVersion: string;
  processorCount: number;
  autoScanPath: string | null;
  autoScanMode: ScanMode | null;
  /** %USERPROFILE%；旧后端可能为 null */
  homePath: string | null;
  /** 后台现在用的界面语言（zh-CN / en / ja）：保存过的选择，没有就是系统语言 */
  language: string;
}

export interface DriveInfo {
  root: string;
  label: string;
  fileSystem: string;
  driveType: DriveType;
  totalBytes: number;
  freeBytes: number;
  isReady: boolean;
  isSsd: boolean | null;
  canMft: boolean;
  isSystem: boolean;
  /** 提权后能深度扫描（本地 NTFS），与是否已提权无关 */
  supportsMft: boolean;
}

export interface Ok { ok: boolean; message: string | null }
export interface PathResult { path: string | null }
export interface ScanStarted { scanId: number; mode: ScanMode }

/** 开始时、每次阶段变化、取消被接受时各立即来一条，其余每 120 ms 一条；scan.done / scan.error 之后不再有 */
export interface ScanProgress {
  scanId: number;
  mode: ScanMode;
  phase: Phase;
  files: number;
  dirs: number;
  bytes: number;
  usedBytes: number;
  /** 0..1，未知为 null */
  percent: number | null;
  currentPath: string | null;
  elapsedMs: number;
  /** 按类别 K 累计的 alloc，长度 CATEGORY_COUNT */
  categoryBytes: number[];
  /** 取消已被接受、后台还没停下（phase 停在被打断的那一步）；真正停下后才来 scan.error（cancelled=true） */
  cancelling: boolean;
}

/** 扫描报告里的异常种类（dise §4.3），和 Contracts.cs 的 ScanIssueKind 同序（契约测试直接比对） */
export const SCAN_ISSUE_KINDS = ['denied', 'link', 'mountPoint', 'error'] as const;
export type ScanIssueKind = (typeof SCAN_ISSUE_KINDS)[number];

/** 未解释容量里一份的种类：ntfsMetadata 是常规扫描读不到的 $MFT（下限），unconfirmed 是原因未确认的余数 */
export const GAP_KINDS = ['unconfirmed', 'ntfsMetadata'] as const;
export type GapKind = (typeof GAP_KINDS)[number];

export interface GapPart {
  kind: GapKind;
  bytes: number;
  /** 依据，给人看的一句话 */
  basis: string;
}

/**
 * inaccessibleDirs、linksSkipped / mountPointsSkipped、errors 是扫描时的数字，删除后不重算，和 scan.issues 的 total 一一相等；
 * unaccountedBytes（拆成 gapParts）跟着卷空间和删除实时刷新。
 */
export interface ScanSummary {
  scanId: number;
  rootPath: string;
  rootId: number;
  mode: ScanMode;
  fileSystem: string;
  isVolumeRoot: boolean;
  isAdmin: boolean;
  volumeTotal: number;
  volumeFree: number;
  volumeUsed: number;
  scannedAlloc: number;
  scannedSize: number;
  files: number;
  dirs: number;
  /** 发现的拒绝访问入口：读不了的目录各算一个，里面还有多少东西不知道——不是「没读到的目录总数」；MFT 扫描恒为 0 */
  inaccessibleDirs: number;
  /** 按规则没进入的链接：联接点、目录符号链接、类型读不出的重解析目录 */
  linksSkipped: number;
  hardLinkDups: number;
  hardLinkSavedBytes: number;
  /** 未解释容量 = 卷已用 − 已统计；只在扫卷根时有 */
  unaccountedBytes: number;
  elapsedMs: number;
  categoryBytes: number[];
  warnings: string[];
  treeVersion: number;
  /** 整棵树按用途 U 汇总的 alloc，长度 PURPOSE_COUNT；旧后端可能为 null */
  purposeBytes: number[] | null;
  /** 按规则没进入的卷挂载点（这里挂着另一个卷） */
  mountPointsSkipped: number;
  /** 读取失败及其他错误（拒绝访问不算）：共享冲突、路径过长、设备错误、扫描期间被删、MFT 损坏和孤立记录…… */
  errors: number;
  /** unaccountedBytes 的拆分，各份之和等于它；没有差额时为空 */
  gapParts: GapPart[];
  /** 扫描完成时刻（Unix 毫秒）：最近文件的时间基准、已保存摘要的时间都是它；删除后刷新汇总不变 */
  completedAtMs: number;
}

/**
 * 某个扫描范围最近一次成功扫描的摘要（scan.savedSummaries，dise §3.2），存在本机、重启后还在，按完成时刻新的在前。
 * 它只是「上次扫描结果」：不带节点 ID、扫描号、树版本——未重新扫描、拿到新的扫描结果之前，不能拿它导航、查详情或删除。
 * 各字段同 ScanSummary 扫描完成那一刻的值，之后的删除不改它。
 */
export interface SavedSummary {
  /** 扫描范围：规整后的根路径；范围按它认、不区分大小写，同一范围只留最近一次 */
  rootPath: string;
  mode: ScanMode;
  fileSystem: string;
  isVolumeRoot: boolean;
  /** 扫描完成时刻（Unix 毫秒），界面标「上次扫描结果 · 时间」用它 */
  completedAtMs: number;
  elapsedMs: number;
  files: number;
  dirs: number;
  scannedAlloc: number;
  scannedSize: number;
  volumeTotal: number;
  volumeFree: number;
  /** 按用途 U 汇总的 alloc，长度 PURPOSE_COUNT */
  purposeBytes: number[];
  inaccessibleDirs: number;
  linksSkipped: number;
  mountPointsSkipped: number;
  errors: number;
  unaccountedBytes: number;
  gapParts: GapPart[];
  // 以下是第 2 版记录才有的「缓存对照」，旧版本的记录里是 null
  /** 扫描时是不是管理员：只和同模式、同权限的上一次对照应用 */
  isAdmin: boolean | null;
  /** 根下面占用最大的几项，带上一次扫描同一范围时的占用 */
  topEntries: SavedEntry[] | null;
  /** 认出的应用（应用、游戏、浏览器），占用大的在前 */
  apps: SavedApp[] | null;
  /** 上一次还在、这次没认出的应用（卸载、搬走，或这次没读到） */
  removedApps: SavedApp[] | null;
  /** 同一范围上一次扫描的完成时刻和已统计占用；第一次扫描这个范围是 null */
  previousAtMs: number | null;
  previousAlloc: number | null;
  /** 应用和哪一次对照的；没有可比的上一次（第一次、旧记录、扫描方式或权限不同）是 null，这时谁都不算新 */
  appsComparedAtMs: number | null;
}

export interface SavedEntry {
  name: string;
  isDir: boolean;
  alloc: number;
  files: number;
  /** 上一次扫描同一范围时的占用；上一次不在前列、或没有上一次是 null */
  previousAlloc: number | null;
}

export interface SavedApp {
  kind: OwnerKind;
  name: string;
  /** 显示用的那一处：优先已确认的安装位置，否则占用最大的一处 */
  path: string;
  /** 各处根的子树占用之和（嵌在里面的别的对象也算在内） */
  alloc: number;
  role: number;
  evidence: Evidence;
  /** 那一处是已确认的程序或游戏目录 */
  installation: boolean;
  /** 同一范围的扫描记录里第一次认出它的时刻：扫描发现的时刻，不是安装时刻 */
  firstSeenMs: number;
  /** 对照出来的新应用：某次和可比的上一次对照时还没有；之后的扫描一直带着，不会一重扫就丢 */
  isNew: boolean;
  /** 对照时它还没有的那次扫描的完成时刻；旧版本的记录没有这个字段 */
  absentAtMs?: number | null;
}

export interface ScanError { scanId: number; message: string; cancelled: boolean }

export interface ScanIssue { path: string; reason: string }

/** 一页异常清单（scan.issues）。扫描时每类最多记一万条，按路径排序 */
export interface ScanIssues {
  /** 清单属于哪次扫描（请求带了 scanId 却对不上会直接报错） */
  scanId: number;
  kind: ScanIssueKind;
  /** 这一类的总数，等于汇总里对应的计数 */
  total: number;
  /** 扫描时实际记下的条数；小于 total 说明超出上限的只计了数，没有路径可给 */
  recorded: number;
  offset: number;
  items: ScanIssue[];
  /** 本页之后还有没展示的（offset + items.length < total）：不能当完整清单展示 */
  truncated: boolean;
}

/** 视图节点：短字段名，一次几万个 */
export interface ViewNode {
  /** 树节点 ID；聚合节点（「其余 N 项」）为 -1 */
  id: number;
  /** 父节点在 nodes 数组里的下标；根为 -1。父节点一定排在子节点前面 */
  p: number;
  /** 名字 */
  n: string;
  /** 按请求度量的值；同一父节点的子节点之和等于父节点的值 */
  v: number;
  /** Flag 位 */
  f: number;
  /** 类别 */
  k: number;
  /** 未删除的直接子节点总数（聚合节点为被合并的条目数） */
  c: number;
  /** 用途类别（purposes.ts），资源地图「类别」模式按它着色；聚合节点取父节点的用途 */
  u: number;
}

export interface ViewData { rootId: number; metric: Metric; total: number; treeVersion: number; nodes: ViewNode[] }

export interface Ancestor { id: number; name: string }

export interface ChildItem { id: number; name: string; isDir: boolean; size: number; alloc: number; k: number; f: number; files: number; u: number }

export interface ExtStat { ext: string; k: number; size: number; alloc: number; count: number }

/** 所属对象（dise §8.1）：节点最近的那个 */
export interface Owner {
  kind: OwnerKind;
  name: string;
  /** 这个对象在本次扫描里包含该节点的那个根 */
  rootId: number;
  rootPath: string;
  /** 这一处归属的证据：按目录名推测的就是 inferred */
  evidence: Evidence;
}

export interface NodeDetails {
  /** 用途、角色、所属对象各自的依据（给人看的一段话） */
  purposeBasis?: string | null;
  id: number;
  name: string;
  path: string;
  isDir: boolean;
  k: number;
  f: number;
  /** 中文属性标签，如「隐藏」「硬链接」 */
  attributes: string[];
  size: number;
  alloc: number;
  files: number;
  dirs: number;
  modifiedMs: number | null;
  createdMs: number | null;
  accessedMs: number | null;
  percentOfParent: number;
  percentOfRoot: number;
  /** 从树根到直接父节点（不含自己），根在前 */
  ancestors: Ancestor[];
  topChildren: ChildItem[];
  childCount: number;
  extStats: ExtStat[];
  /** 子树按类别 K 的值（按请求度量），长度 CATEGORY_COUNT */
  categoryBytes: number[];
  isProtected: boolean;
  protectReason: string | null;
  warning: string | null;
  linkTarget: string | null;
  hardLinkCount: number;
  hardLinkCountedAt: string | null;
  treeVersion: number;
  /** 用途类别 */
  u: number;
  /** 子树按用途 U 的值（按请求度量），长度 PURPOSE_COUNT */
  purposeBytes: number[] | null;
  /** 数据角色（roles.ts） */
  role: number;
  /** 用途的证据等级 */
  evidence: Evidence;
  /** 最近的所属对象；没有可靠依据时为 null */
  owner: Owner | null;
}

export interface FileItem {
  id: number;
  name: string;
  path: string;
  isDir: boolean;
  size: number;
  alloc: number;
  modifiedMs: number | null;
  k: number;
  f: number;
  /** 用途类别，和 treemap 同色 */
  u: number;
}

/** 目录列表的排序（Contracts.cs 的 ChildSort，同序）：name 目录在前、名字不分大小写；size 按度量的计入值大的在前；modified 新的在前、未知最后；并列按 id */
export const CHILD_SORTS = ['name', 'size', 'modified'] as const;
export type ChildSort = (typeof CHILD_SORTS)[number];

export interface ChildEntry {
  id: number;
  name: string;
  isDir: boolean;
  /** 按请求度量的计入值，和地图同口径（硬链接副本计 0） */
  value: number;
  size: number;
  alloc: number;
  /** 扫描时读到的修改时间（Unix 毫秒），未知为 null */
  modifiedMs: number | null;
  k: number;
  f: number;
  /** 主用途，圆点和地图同色 */
  u: number;
  /** 目录的未删除直接子项数；文件为 0 */
  childCount: number;
}

/** 目录列表（tree.children）：一个目录的全部直接子项，分页给；地图并成「其余 N 项」的小块在这里都有 */
export interface Children {
  nodeId: number;
  metric: Metric;
  sort: ChildSort;
  /** 未删除的直接子项总数，不受分页影响 */
  total: number;
  offset: number;
  items: ChildEntry[];
}

/** 最近文件的排序（Contracts.cs 的 RecentSort，同序）：size 按度量大的在前，modified 按修改时间新的在前；并列按 id */
export const RECENT_SORTS = ['size', 'modified'] as const;
export type RecentSort = (typeof RECENT_SORTS)[number];

export interface RecentFile {
  id: number;
  name: string;
  path: string;
  /** 所在目录的路径 */
  dirPath: string;
  size: number;
  alloc: number;
  /** 扫描时读到的修改时间（Unix 毫秒） */
  modifiedMs: number;
  k: number;
  f: number;
  /** 主用途 */
  u: number;
  /** 数据角色（roles.ts） */
  role: number;
  /** 最近的所属对象；没有可靠依据时为 null */
  owner: Owner | null;
}

/** 最近文件（tree.recent）：修改时间落在 [asOfMs − days 天, asOfMs] 里的文件。
 *  修改时间不等于写入量，也不代表新增或下载——界面别这么说 */
export interface RecentFiles {
  /** 时间基准：扫描完成时刻，不是查询当下 */
  asOfMs: number;
  /** 实际用的天数（夹在 1..3650） */
  days: number;
  /** 窗口内的总数，不受 limit 影响；大于 items.length 说明截断了 */
  total: number;
  items: RecentFile[];
}

/** 对象在树里的一处根；字节只算当前范围里、归这一处的（扣掉嵌在里面的别的根，不含共享） */
export interface OwnerUsageRoot { id: number; path: string; sizeInScope: number; allocInScope: number; evidence: Evidence }

export interface OwnerUsageItem {
  kind: OwnerKind;
  name: string;
  /** 对象本身的证据（几处来源里最强的） */
  evidence: Evidence;
  sizeInScope: number;
  allocInScope: number;
  /** 按角色分的构成（按请求度量），长度 ROLE_COUNT，之和等于这个度量下的计入大小 */
  roleBytesInScope: number[];
  /** 按请求度量降序，并列按 id */
  roots: OwnerUsageRoot[];
}

/** 按所属对象聚合（tree.owners）：只算当前范围，不是对象在整台机器上的总占用。
 *  两种度量各自满足 owned + shared + unowned = total（total 和地图上作用域的大小一致） */
export interface OwnerUsage {
  scopeId: number;
  totalSizeInScope: number;
  totalAllocInScope: number;
  ownedSizeInScope: number;
  ownedAllocInScope: number;
  /** 跨对象的硬链接组，没法可靠地分给哪一方 */
  sharedSizeInScope: number;
  sharedAllocInScope: number;
  unownedSizeInScope: number;
  unownedAllocInScope: number;
  /** 碰到范围的对象个数，不受 limit 影响；大于 items.length 说明截断了 */
  ownerCount: number;
  items: OwnerUsageItem[];
}

/** 关联关系（Contracts.cs 的 RelationKind，同序）：所在的根、同一对象的另一处根、同一处根下换了角色的模块 */
export const RELATION_KINDS = ['root', 'otherRoot', 'roleModule'] as const;
export type RelationKind = (typeof RELATION_KINDS)[number];

export interface RelatedItem {
  id: number;
  name: string;
  path: string;
  isDir: boolean;
  /** 节点在地图上的大小（整棵子树） */
  size: number;
  alloc: number;
  role: number;
  relation: RelationKind;
  evidence: Evidence;
}

/** 关联详情（tree.related）：只用于浏览和定位，不扩大清理范围——属于同一个应用不等于可以整组删除 */
export interface Related {
  nodeId: number;
  owner: Owner | null;
  /** 找到的关联总数，不受 limit 和每种角色 3 个的影响 */
  total: number;
  /** 所在的根 → 另几处根 → 各角色的模块 */
  items: RelatedItem[];
  /** items 为空时是「尚未识别关联对象」，否则为 null */
  note: string | null;
}

/** 搜索结果的排法：按占用（同地图的口径），或按相关度（名字整个就是 > 开头 > 词首 > 中间，同档再按占用） */
export type SearchSort = 'size' | 'relevance';

export interface SearchResult {
  query: string;
  total: number;
  tookMs: number;
  truncated: boolean;
  items: FileItem[];
  /** 语法错误时的中文说明 */
  error: string | null;
  /** 精确匹配一条都没有，这是放宽后的近似结果（只在智能匹配时出现），按相似度排 */
  fuzzy: boolean;
}

export interface Suggestion {
  id: string;
  group: SuggestionGroup;
  title: string;
  description: string;
  bytes: number;
  count: number;
  action: SuggestionAction;
  command: string | null;
  target: string | null;
  requiresAdmin: boolean;
  defaultPermanent: boolean;
  selectable: boolean;
  nodeIds: number[];
  items: FileItem[];
  /** 给人看的位置（用户目录写成 ~），参考图右栏那行灰色小字 */
  displayPath: string | null;
}

/** 勾选的一条建议；nodeIds 为空表示整条，不为空必须是建议 nodeIds 的子集 */
export interface CleanupSelection {
  suggestionId: string;
  nodeIds: number[] | null;
}

export interface CleanupScript {
  /** PowerShell 脚本全文 */
  script: string;
  bytes: number;
  items: number;
  needsAdmin: boolean;
}

export interface DeleteCheckItem {
  id: number;
  path: string;
  isDir: boolean;
  size: number;
  alloc: number;
  files: number;
  blocked: boolean;
  reason: string | null;
  warning: string | null;
}

export interface DeleteCheck {
  confirmationToken?: string | null;
  items: DeleteCheckItem[];
  totalAlloc: number;
  totalSize: number;
  totalFiles: number;
  anyBlocked: boolean;
  anyWarning: boolean;
}

export interface CleanupCheck { confirmationToken: string; deleteCheck: DeleteCheck | null }

export interface OpError { path: string; message: string }

export interface OpResult {
  ok: boolean;
  freedBytes: number;
  deleted: number;
  failed: number;
  errors: OpError[];
  message: string | null;
  treeVersion: number;
  /** 动手前已经不在磁盘上的条目数（多半被别的程序先删了）：树里照样标掉，不算 deleted，也不算进 freedBytes */
  alreadyGone: number;
}

export interface OperationStatus {
  operationId: string;
  kind: 'cleanup' | 'delete';
  status: 'queued' | 'running' | 'cancelling' | 'completed' | 'cancelled' | 'failed' | 'external';
  createdAtMs: number;
  updatedAtMs: number;
  currentPath: string | null;
  /** 去嵌套后的确认目标数，不是其内部文件总数。 */
  total: number;
  completed: number;
  failed: number;
  skipped: number;
  processedEntries: number;
  /** 无法核实时为 null，不能用扫描估算代替。 */
  freedBytes: number | null;
  result: OpResult | null;
  message: string | null;
}

/** 扫描之后磁盘上的变化（tree.changed）：数字都是这次扫描以来累计的 */
export interface TreeChange {
  scanId: number;
  /** 系统不给这个位置发变化通知时为 false：地图不会自己变 */
  watching: boolean;
  /** 别的程序删掉、已经从树里标掉的条目数 */
  removedEntries: number;
  /** 新建或改名出来的条目数：不重扫加不进树，只提示统计可能过期 */
  otherChanges: number;
  /** 跟丢过变化（太多、通知中断），建议重扫 */
  overflow: boolean;
  treeVersion: number;
}

export interface EventMap {
  'operation.progress': OperationStatus;
  'tree.changed': TreeChange;
  'scan.progress': ScanProgress;
  'scan.done': ScanSummary;
  'scan.error': ScanError;
}
