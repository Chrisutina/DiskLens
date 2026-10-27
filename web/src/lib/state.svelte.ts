import { api } from './api.ts';
import { formatBytes } from './format.ts';
import { locale, setLocale, t, type Locale } from './i18n.ts';
import { cleanupSpace } from './cleanup.ts';
import type * as T from './types.ts';
import type { CleanupTask, ConfirmedCleanupTask } from './cleanupTasks.svelte.ts';

// 全局状态 + 动作。组件只读 app、只调这里导出的函数，别自己去 api 里掏东西 —— 不然删除后谁来刷新就成了悬案。
// 大块数据用 $state.raw（整体替换才触发更新），否则几万个视图节点全被包成 Proxy，卡给你看。

export type Screen = 'start' | 'scanning' | 'main';
/** overview/cleanup 保留兼容旧调用；setPage 将它们映射到地图及清理右栏。 */
export type Page = 'overview' | 'browse' | 'recent' | 'cleanup';
/** 空间浏览页右侧面板的两种内容：选中项详情、当前位置的大文件 */
export type RightTab = 'details' | 'largest' | 'cleanup';
/** 资源地图着色：按用途（参考图 Category）或按清理状态（参考图 Cleanup） */
export type ExplorerMode = 'category' | 'cleanup';
/** 浏览维度（dise §5.3）：目录分布（地图）或应用占用（当前范围按所属对象汇总） */
export type BrowseDim = 'dirs' | 'apps';
/** 顶栏搜索的范围（dise §3.6）：默认本地文件；选了引擎时只在回车提交后打开浏览器 */
export type SearchScope = 'files' | 'google' | 'bing';

/** 一条建议的勾选状态；items 为 null 表示整条，否则是勾选的子集节点 */
export interface CleanupPick {
  on: boolean;
  items: number[] | null;
}

export interface CleanupStats {
  safe: { bytes: number; count: number };
  review: { bytes: number; count: number };
  selected: { bytes: number; count: number; immediateBytes: number; recycleBytes: number; manualBytes: number };
}

/** 资源地图用：哪些节点被建议覆盖（按组），哪些被勾选（画斜纹）。只含建议直接指向的节点，子孙由使用方沿视图父链继承。 */
export interface CleanupMarks {
  group: Map<number, T.SuggestionGroup>;
  selected: Set<number>;
}

export interface MenuState {
  x: number;
  y: number;
  nodeId: number;
  name: string;
  isDir: boolean;
  /** Flag 位（视图节点的 f 或详情的 f） */
  flags: number;
}

export interface BatchItem {
  suggestion: T.Suggestion;
  nodeIds: number[] | null;
  bytes: number;
  /** command / openSettings / info：不会自动执行，在确认框里给按钮让用户手动处理 */
  manual: boolean;
  check?: T.CleanupCheck;
}

export type ConfirmState = (
  | { kind: 'delete'; check: T.DeleteCheck; permanent: boolean; busy: boolean }
  | { kind: 'cleanup'; suggestion: T.Suggestion; nodeIds: number[] | null; permanent: boolean; busy: boolean; check?: T.CleanupCheck }
  | { kind: 'cleanupBatch'; items: BatchItem[]; permanent: boolean; busy: boolean }
  /** denied：扫描时有这么多目录没权限读取（扫完自动问的那种），文案按「重新扫描」说 */
  | { kind: 'elevate'; scanPath: string | null; mode: T.ScanMode; busy: boolean; denied?: number }
) & { preparing?: boolean; preflightError?: string };

export interface Toast {
  id: number;
  kind: 'info' | 'success' | 'warn' | 'error';
  text: string;
}

export interface Settings {
  engine: T.SearchEngine;
}

const SETTINGS_KEY = 'disklens.settings';
export const isMock = api.isMock;

class AppState {
  screen = $state<Screen>('start');
  page = $state<Page>('browse');
  info = $state.raw<T.AppInfo | null>(null);
  drives = $state.raw<T.DriveInfo[]>([]);
  drivesLoading = $state(false);

  /** 最近一次请求扫描的路径和模式（F5 重扫用） */
  scanPath = $state<string | null>(null);
  scanMode = $state<T.ScanMode>('auto');
  progress = $state.raw<T.ScanProgress | null>(null);
  scanError = $state<string | null>(null);
  scanCancelling = $state(false);
  summary = $state.raw<T.ScanSummary | null>(null);
  /** 扫描之后磁盘上的变化（tree.changed 最新一条）；扫描号对不上当前结果就不算数 */
  live = $state.raw<T.TreeChange | null>(null);

  metric = $state<T.Metric>('alloc');
  viewRootId = $state(0);
  view = $state.raw<T.ViewData | null>(null);
  viewLoading = $state(false);
  viewError = $state<string | null>(null);
  /** 当前视图根的详情（面包屑、构成标签页用） */
  rootDetails = $state.raw<T.NodeDetails | null>(null);
  selectedId = $state(0);
  details = $state.raw<T.NodeDetails | null>(null);
  detailsLoading = $state(false);
  detailsError = $state<string | null>(null);
  hoverId = $state(-1);
  largest = $state.raw<T.FileItem[]>([]);
  largestLoading = $state(false);
  largestError = $state<string | null>(null);
  recent = $state.raw<T.RecentFiles | null>(null);
  recentLoading = $state(false);
  recentError = $state<string | null>(null);
  recentDays = $state<1 | 7>(7);
  recentSort = $state<T.RecentSort>('size');
  recentInFolder = $state(false);
  suggestions = $state.raw<T.Suggestion[]>([]);
  suggestionsLoading = $state(false);
  suggestionsError = $state<string | null>(null);
  scriptLoading = $state(false);

  rightTab = $state<RightTab>('details');
  /** 右侧面板按需打开：选中对象、点「详情」「大文件」或「查看详情」时打开，× 或 Esc 关闭 */
  panelOpen = $state(false);
  explorerMode = $state<ExplorerMode>('category');
  dimension = $state<BrowseDim>('dirs');
  /** 图例：点住的筛选、悬停的临时高亮；值是用途 U（类别模式）或清理组（清理模式），null 表示没有 */
  legendFilter = $state<number | null>(null);
  legendHover = $state<number | null>(null);
  /** 和地图同步的目录列表（dise §5.2）：按需展开；分页累加，换目录、度量、排序重读 */
  listOpen = $state(false);
  listSort = $state<T.ChildSort>('size');
  list = $state.raw<T.Children | null>(null);
  listLoading = $state(false);
  listError = $state<string | null>(null);
  /** 应用占用维度：当前地图目录按所属对象汇总 */
  owners = $state.raw<T.OwnerUsage | null>(null);
  ownersLoading = $state(false);
  ownersError = $state<string | null>(null);
  /** 清理建议的勾选，键是 suggestion.id（DESIGN §9.5） */
  cleanupSel = $state<Record<string, CleanupPick>>({});

  cleanupStats: CleanupStats = $derived.by(() => computeStats(this.suggestions, this.cleanupSel));
  cleanupMarks: CleanupMarks = $derived.by(() => computeMarks(this.suggestions, this.cleanupSel));

  searchQuery = $state('');
  searchScope = $state<SearchScope>('files');
  /** 顶栏搜索默认按相关度排；切到「占用」就和地图一个口径 */
  searchSort = $state<T.SearchSort>('relevance');
  searchResult = $state.raw<T.SearchResult | null>(null);
  searchLoading = $state(false);
  searchOpen = $state(false);

  menu = $state<MenuState | null>(null);
  confirm = $state<ConfirmState | null>(null);
  toasts = $state<Toast[]>([]);
  settings = $state<Settings>(loadSettings());

  backStack = $state<number[]>([]);
  forwardStack = $state<number[]>([]);
}

export const app = new AppState();

// ---------------- 提示 ----------------

let toastSeq = 0;

export function toast(text: string, kind: Toast['kind'] = 'info'): void {
  const t: Toast = { id: ++toastSeq, kind, text };
  app.toasts = [...app.toasts, t];
  if (kind !== 'error') setTimeout(() => dismissToast(t.id), 4000);
}

export function dismissToast(id: number): void {
  app.toasts = app.toasts.filter((t) => t.id !== id);
}

const message = (e: unknown): string => (e instanceof Error ? e.message : String(e));

function fail(e: unknown, prefix?: string): void {
  toast(prefix ? t('{prefix}：{error}', { prefix, error: message(e) }) : message(e), 'error');
}

// ---------------- 界面语言 ----------------

/** 换界面语言（启动页左上角）：界面马上换；后台跟着换它发来的文字，并存进设置文件，下次启动默认就是它。
 *  已经读到的后台文字（清理建议、详情里的依据）按新语言安静重读一遍 */
export async function changeLanguage(code: Locale): Promise<void> {
  if (code === locale()) return;
  setLocale(code);
  try {
    const r = await api.setLanguage(code);
    if (r.message) toast(r.message, 'warn');
  } catch (e) {
    fail(e, t('语言没能保存'));
    return;
  }
  if (app.summary) void Promise.all([loadSuggestions(true), loadDetails(app.selectedId, true), loadRootDetails(true)]);
  // 演示数据的卷标跟着界面语言（真机上卷标是用户自己起的，重读一遍也一样）；安静地读，不转圈
  if (app.screen === 'start') void api.listDrives().then((d) => (app.drives = d), () => {});
}

// ---------------- 清理任务执行器 ----------------

/** 确认后的删除和清理交给任务区执行（cleanupTasks.svelte.ts，main.ts 注册）。这里只认接口：单测换成假的，也不让状态模块依赖任务区的实现。 */
export interface TaskRunner {
  /** 有任务在跑，或状态待确认 */
  readonly active: boolean;
  /** 只在全部任务都有确定结果时返回；状态待确认就一直不返回 */
  start(tasks: ConfirmedCleanupTask[]): Promise<CleanupTask[]>;
}

let taskRunner: TaskRunner | null = null;

export function useTaskRunner(runner: TaskRunner): void {
  taskRunner = runner;
}

/** 任务没结束或状态待确认时，挡住新的删除、清理、扫描和提权（同一卷上的写操作不能叠着来），并把任务区亮出来。 */
function tasksBusy(): boolean {
  if (!taskRunner?.active) return false;
  toast(t('清理任务还没结束或状态待确认：请在清理任务区查询状态，或停止后续项目'), 'warn');
  if (app.screen === 'main') openPanel('cleanup');
  return true;
}

// ---------------- 启动与扫描 ----------------

let currentScanId = -1;
let scanRequestSeq = 0;
let awaitingScanId = false;
// scan.start 的应答还没回来，扫描就已经结束了（小文件夹很常见）—— 先把事件攒着，别丢
const early = new Map<number, { done?: T.ScanSummary; error?: T.ScanError }>();

export async function init(): Promise<void> {
  api.on('scan.progress', (p) => {
    if (p.scanId === currentScanId) app.progress = p;
  });
  api.on('scan.done', (s) => {
    if (s.scanId === currentScanId) void onScanDone(s);
    else if (awaitingScanId && s.scanId > currentScanId) early.set(s.scanId, { ...early.get(s.scanId), done: s });
  });
  api.on('scan.error', (e) => {
    if (e.scanId === currentScanId) onScanError(e);
    else if (awaitingScanId && e.scanId > currentScanId) early.set(e.scanId, { ...early.get(e.scanId), error: e });
  });
  api.on('tree.changed', (c) => {
    if (c.scanId < (app.live?.scanId ?? -1)) return;
    app.live = c;
    if (treeMoved()) scheduleLive(1500);
  });
  // 自己的删除边删边落树：任务跑着就隔一会儿安静刷新一次，地图跟着一块块消失
  api.on('operation.progress', (s) => {
    if (s.status === 'running' || s.status === 'cancelling') scheduleLive(400);
  });
  try {
    app.info = await api.info();
    await refreshDrives();
    if (app.info.autoScanPath) await startScan(app.info.autoScanPath, app.info.autoScanMode ?? 'auto');
  } catch (e) {
    fail(e, t('初始化失败'));
  }
}

export async function refreshDrives(): Promise<void> {
  app.drivesLoading = true;
  try {
    app.drives = await api.listDrives();
  } catch (e) {
    fail(e, t('读取磁盘列表失败'));
  } finally {
    app.drivesLoading = false;
  }
}

export async function startPickedFolder(): Promise<void> {
  try {
    const { path } = await api.pickFolder();
    if (path) await startScan(path);
  } catch (e) {
    fail(e, t('无法选择文件夹'));
  }
}

/** 扫描期间旧结果保留在后面；取消就回到旧结果（没有就回启动页） */
export async function startScan(path: string, mode: T.ScanMode = 'auto'): Promise<void> {
  if (app.screen === 'scanning' && !app.scanError) return;
  if (tasksBusy()) return;
  scanRequestSeq++;
  invalidateRequests();
  currentScanId = -1;
  early.clear();
  app.scanPath = path;
  app.scanMode = mode;
  app.scanError = null;
  app.scanCancelling = false;
  app.progress = null;
  app.menu = null;
  app.confirm = null;
  app.searchOpen = false;
  app.screen = 'scanning';
  awaitingScanId = true;
  try {
    const r = await api.startScan(path, mode);
    currentScanId = r.scanId;
    const e = early.get(r.scanId);
    early.clear();
    if (e?.done) await onScanDone(e.done);
    else if (e?.error) onScanError(e.error);
  } catch (e) {
    app.scanError = message(e);
    app.scanCancelling = false;
  } finally {
    awaitingScanId = false;
  }
}

export async function cancelScan(): Promise<void> {
  if (app.screen !== 'scanning' || app.scanError || app.scanCancelling) return;
  const seq = scanRequestSeq;
  app.scanCancelling = true;
  try {
    const r = await api.cancelScan();
    if (!r.ok) throw new Error(r.message ?? t('后台未接受取消请求'));
  } catch (e) {
    if (seq === scanRequestSeq && app.screen === 'scanning' && app.scanCancelling) {
      app.scanCancelling = false;
      fail(e, t('取消失败'));
    }
  }
}

export function rescan(): void {
  // 用界面上正显示着的那次扫描：被取消的那次的路径留在 scanPath 里，F5 不能对着它重扫
  if (app.summary) void startScan(app.summary.rootPath, app.summary.mode);
  else if (app.scanPath) void startScan(app.scanPath, app.scanMode);
}

/** 扫描失败页面的「返回」 */
export function leaveScan(): void {
  app.scanError = null;
  app.scanCancelling = false;
  app.screen = app.summary ? 'main' : 'start';
  // 回到的是旧结果：之后的刷新、实时变化都按它的扫描号认，不再认被取消或失败的那次
  currentScanId = app.summary?.scanId ?? -1;
  void loadRecent();
  if (treeMoved()) scheduleLive(0);
}

export function backToStart(): void {
  // 启动页看不到任务区，也不能在那里取消；任务有结果之前留在工作区
  if (tasksBusy()) return;
  app.screen = 'start';
  setPage('browse');
  void refreshDrives();
}

function onScanError(e: T.ScanError): void {
  app.scanCancelling = false;
  if (e.cancelled) {
    toast(t('已取消扫描'));
    leaveScan();
    return;
  }
  app.scanError = e.message;
}

async function onScanDone(s: T.ScanSummary): Promise<void> {
  app.scanCancelling = false;
  resetTreeState();
  app.summary = s;
  app.viewRootId = s.rootId;
  app.selectedId = s.rootId;
  app.screen = 'main';
  await Promise.all([loadView(), loadDetails(s.rootId), loadRootDetails(), loadLargest(), loadSuggestions(), loadList(), loadOwners()]);
  if (currentScanId !== s.scanId || app.screen !== 'main') return;
  if (s.warnings.length) toast(s.warnings[0], 'warn');
  if (isMock) applyDemoParams();
  // 有目录没读到：问一句要不要用管理员身份重扫。这个范围拒绝过一次就不再问，别每扫一次都弹
  if (s.inaccessibleDirs > 0 && !s.isAdmin && !app.confirm && !declinedElevation.has(s.rootPath.toLowerCase()))
    requestElevation(s.rootPath, 'auto', s.inaccessibleDirs);
}

/** 扫完自动问「用管理员身份重扫」时用户点了取消的范围（小写路径），这次运行里不再问 */
const declinedElevation = new Set<string>();

/** 演示模式专用：URL 参数直接摆出某个界面状态，方便无头截图（?page=browse&tab=details&explorer=cleanup&q=iso&zoom=Users） */
function applyDemoParams(): void {
  const q = new URLSearchParams(globalThis.location?.search ?? '');
  const tab = q.get('tab');
  if (tab === 'details' || tab === 'largest' || tab === 'cleanup') openPanel(tab);
  const mode = q.get('explorer');
  if (mode === 'category' || mode === 'cleanup') app.explorerMode = mode;
  const zoom = q.get('zoom');
  const child = zoom ? app.view?.nodes.find((n) => n.p === 0 && n.n.toLowerCase() === zoom.toLowerCase()) : undefined;
  if (child) void zoomTo(child.id);
  const search = q.get('q');
  if (search) setSearchQuery(search);
  // 放最后：zoomTo 会同步切回空间浏览
  const page = q.get('page');
  if (page === 'overview' || page === 'browse' || page === 'recent' || page === 'cleanup') setPage(page);
}

function resetTreeState(): void {
  invalidateRequests();
  app.page = 'browse';
  app.panelOpen = false;
  app.recent = null;
  app.recentError = null;
  app.view = null;
  app.viewError = null;
  app.details = null;
  app.rootDetails = null;
  app.largest = [];
  app.suggestions = [];
  knownSuggestions = [];
  app.searchQuery = '';
  app.searchResult = null;
  app.searchOpen = false;
  app.backStack = [];
  app.forwardStack = [];
  app.menu = null;
  app.confirm = null;
  app.hoverId = -1;
  app.cleanupSel = {};
  app.list = null;
  app.listError = null;
  app.owners = null;
  app.ownersError = null;
  app.legendFilter = null;
  app.legendHover = null;
}

// ---------------- 数据加载（每类请求自带序号，只认最后一次） ----------------

function invalidateRequests(): void {
  viewSeq++;
  detailsSeq++;
  rootSeq++;
  largestSeq++;
  recentSeq++;
  suggestionsSeq++;
  searchSeq++;
  scriptSeq++;
  confirmSeq++;
  listSeq++;
  ownersSeq++;
  app.listLoading = false;
  app.ownersLoading = false;
  detailsPending.clear();
  largestKey = '';
  clearTimeout(searchTimer);
  app.searchLoading = false;
  app.viewLoading = false;
  app.detailsLoading = false;
  app.detailsError = null;
  app.scriptLoading = false;
  app.largestLoading = false;
  app.recentLoading = false;
  app.suggestionsLoading = false;
  app.hoverId = -1;
}

let viewSeq = 0;
export async function loadView(quiet = false): Promise<void> {
  if (quiet && app.viewLoading) return; // 正经的读取还在路上，带回来的就是新的
  const seq = ++viewSeq;
  if (!quiet) {
    app.viewLoading = true;
    app.viewError = null;
    app.menu = null;
  }
  try {
    const v = await api.view(app.viewRootId, app.metric);
    if (seq === viewSeq) { app.view = v; app.viewError = null; }
  } catch (e) {
    if (seq === viewSeq && !quiet) {
      app.view = null;
      app.viewError = message(e);
    }
  } finally {
    if (seq === viewSeq) app.viewLoading = false;
  }
}

let detailsSeq = 0;
const detailsPending = new Map<string, Promise<T.NodeDetails>>();
// 只合并同一批中的重复请求，不缓存磁盘属性；根详情和选中根详情共用一次读取。
function readDetails(id: number): Promise<T.NodeDetails> {
  const key = `${scanRequestSeq}:${app.metric}:${id}`;
  let pending = detailsPending.get(key);
  if (!pending) {
    pending = api.details(id, app.metric).finally(() => {
      if (detailsPending.get(key) === pending) detailsPending.delete(key);
    });
    detailsPending.set(key, pending);
  }
  return pending;
}
export async function loadDetails(id: number = app.selectedId, quiet = false): Promise<void> {
  if (quiet && app.detailsLoading) return;
  const seq = ++detailsSeq;
  if (!quiet) {
    app.details = null;
    app.detailsLoading = true;
    app.detailsError = null;
    app.menu = null;
  }
  try {
    const d = await readDetails(id);
    if (seq === detailsSeq) app.details = d;
  } catch (e) {
    if (seq !== detailsSeq) return;
    // 选中的东西被别的程序删了：改选当前目录，别在详情栏挂一句报错
    if (quiet && id !== app.viewRootId) {
      app.selectedId = app.viewRootId;
      return loadDetails(app.viewRootId, true);
    }
    app.detailsError = message(e);
  } finally {
    if (seq === detailsSeq) app.detailsLoading = false;
  }
}

let rootSeq = 0;
export async function loadRootDetails(quiet = false): Promise<void> {
  const seq = ++rootSeq;
  if (!quiet) app.rootDetails = null;
  try {
    const d = await readDetails(app.viewRootId);
    if (seq === rootSeq) app.rootDetails = d;
  } catch (e) {
    if (seq === rootSeq && !quiet) fail(e, t('读取目录信息失败'));
  }
}

let largestSeq = 0;
let largestKey = '';
let largestPendingKey = '';
export async function loadLargest(quiet = false): Promise<void> {
  const key = `${scanRequestSeq}:${app.viewRootId}:${app.metric}`;
  if (key === largestKey && !app.largestError) return;
  if (key === largestPendingKey && app.largestLoading) return;
  const seq = ++largestSeq;
  largestKey = '';
  if (!quiet) app.largest = [];
  app.largestError = null;
  app.largestLoading = false;
  if (app.page !== 'browse' || !app.panelOpen || app.rightTab !== 'largest') return;
  largestPendingKey = key;
  app.largestLoading = !quiet;
  try {
    const r = await api.largest(app.viewRootId, app.metric, 100);
    if (seq === largestSeq) { app.largest = r; largestKey = key; }
  } catch (e) {
    if (seq === largestSeq) app.largestError = message(e);
  } finally {
    if (seq === largestSeq) app.largestLoading = false;
  }
}

// ---------------- 目录列表、应用维度、图例 ----------------

let listSeq = 0;
/** 当前地图目录的直接子项；more = 接着读下一页。列表收着、不在地图页或在应用维度时不读 */
export async function loadList(more = false, quiet = false): Promise<void> {
  if (quiet && app.listLoading) return;
  const seq = ++listSeq;
  const prev = more ? app.list : null;
  // 安静刷新按已经展开的条数重读：一次最多 500，多的接着分页读，列表不缩回去
  const shown = quiet && app.list?.nodeId === app.viewRootId ? app.list.items.length : 0;
  if (!more && !quiet) app.list = null;
  app.listError = null;
  app.listLoading = false;
  if (!app.listOpen || app.screen !== 'main' || app.page !== 'browse' || app.dimension !== 'dirs') return;
  app.listLoading = !quiet;
  try {
    let r = await api.children(app.viewRootId, app.metric, app.listSort, prev?.items.length ?? 0, Math.min(500, Math.max(200, shown)));
    while (seq === listSeq && r.items.length < Math.min(shown, r.total)) {
      const page = await api.children(r.nodeId, r.metric, r.sort, r.items.length, Math.min(500, shown - r.items.length));
      if (!page.items.length) break;
      r = { ...r, total: page.total, items: [...r.items, ...page.items] };
    }
    if (seq !== listSeq) return;
    // 接页只在同一目录、同一度量和排序时拼；对不上就当第一页
    app.list = prev && prev.nodeId === r.nodeId && prev.sort === r.sort && prev.metric === r.metric
      ? { ...r, offset: 0, items: [...prev.items, ...r.items] } : r;
  } catch (e) {
    if (seq === listSeq) app.listError = message(e);
  } finally {
    if (seq === listSeq) app.listLoading = false;
  }
}

export function toggleList(open = !app.listOpen): void {
  app.listOpen = open;
  void loadList();
}

export function setListSort(sort: T.ChildSort): void {
  if (sort === app.listSort) return;
  app.listSort = sort;
  void loadList();
}

let ownersSeq = 0;
/** 应用占用：当前地图目录按所属对象汇总，只在应用维度下读 */
export async function loadOwners(quiet = false): Promise<void> {
  if (quiet && app.ownersLoading) return;
  const seq = ++ownersSeq;
  if (!quiet) app.owners = null;
  app.ownersError = null;
  app.ownersLoading = false;
  if (app.dimension !== 'apps' || app.screen !== 'main' || app.page !== 'browse') return;
  app.ownersLoading = !quiet;
  try {
    const r = await api.owners(app.viewRootId, app.metric, 100);
    if (seq === ownersSeq) app.owners = r;
  } catch (e) {
    if (seq === ownersSeq) app.ownersError = message(e);
  } finally {
    if (seq === ownersSeq) app.ownersLoading = false;
  }
}

export function setDimension(d: BrowseDim): void {
  if (d === app.dimension) return;
  app.dimension = d;
  app.legendFilter = null;
  app.legendHover = null;
  void loadOwners();
  void loadList();
}

/** 换着色方式：两种图例的键不是一回事（用途 / 清理组），筛选随之清掉 */
export function setExplorerMode(mode: ExplorerMode): void {
  if (mode === app.explorerMode) return;
  app.explorerMode = mode;
  app.legendFilter = null;
  app.legendHover = null;
}

/** 点图例：再点同一项就清掉 */
export function setLegendFilter(v: number | null): void {
  app.legendFilter = v === app.legendFilter ? null : v;
}

export function setLegendHover(v: number | null): void {
  app.legendHover = v;
}

/** 打开面板前焦点所在的元素：关面板时把焦点还给它 */
let panelTrigger: HTMLElement | null = null;

export function openPanel(tab: RightTab): void {
  if (!app.panelOpen) panelTrigger = (globalThis.document?.activeElement as HTMLElement | null) ?? null;
  app.panelOpen = true;
  app.rightTab = tab;
  if (tab === 'largest') void loadLargest();
}

/** × 或 Esc：焦点回到打开面板的地方；那里已经不在页面上了（换过页、菜单已关）就回到地图 */
export function closePanel(): void {
  app.panelOpen = false;
  const target = panelTrigger?.isConnected ? panelTrigger : globalThis.document?.querySelector<HTMLElement>('section.explorer [role="listbox"]');
  panelTrigger = null;
  target?.focus();
}

let recentSeq = 0;
export async function loadRecent(quiet = false): Promise<void> {
  if (quiet && app.recentLoading) return;
  const seq = ++recentSeq;
  if (!quiet) app.recent = null;
  app.recentError = null;
  app.recentLoading = false;
  if (app.page !== 'recent' || app.screen !== 'main') return;
  app.recentLoading = !quiet;
  try {
    const result = await api.recent(app.recentDays, app.recentInFolder ? app.viewRootId : null, app.recentSort, app.metric, 100);
    if (seq === recentSeq) app.recent = result;
  } catch (e) {
    if (seq === recentSeq) app.recentError = message(e);
  } finally {
    if (seq === recentSeq) app.recentLoading = false;
  }
}

export function setPage(page: Page): void {
  const next = page === 'recent' ? 'recent' : 'browse';
  const changed = next !== app.page;
  app.page = next;
  if (page === 'cleanup') openPanel('cleanup');
  app.menu = null;
  app.hoverId = -1;
  void loadRecent();
  void loadLargest(); // 离开空间浏览时换过目录或度量，回来要补读
  if (changed) {
    void loadList();
    void loadOwners();
  }
}

let suggestionsSeq = 0;
let knownSuggestions: T.Suggestion[] = [];
export async function loadSuggestions(quiet = false): Promise<void> {
  if (quiet && app.suggestionsLoading) return;
  const seq = ++suggestionsSeq;
  const previous = app.suggestions.length ? app.suggestions : knownSuggestions;
  knownSuggestions = previous;
  if (!quiet) app.suggestions = [];
  app.suggestionsError = null;
  app.suggestionsLoading = !quiet;
  try {
    const suggestions = await api.suggestions();
    if (seq === suggestionsSeq) {
      app.cleanupSel = reconcileSelection(suggestions, app.cleanupSel, previous);
      app.suggestions = suggestions;
      knownSuggestions = suggestions;
    }
  } catch (e) {
    if (seq === suggestionsSeq) app.suggestionsError = message(e);
  } finally {
    if (seq === suggestionsSeq) app.suggestionsLoading = false;
  }
}

// ---------------- 导航与选择 ----------------

export function select(id: number): void {
  if (id < 0) return;
  app.selectedId = id;
  void loadDetails(id);
}

/** 选中并打开详情面板（treemap 单击文件、右键「查看详情」用；搜索结果的右键菜单可能在别的页面） */
export function showDetails(id: number): void {
  if (id < 0) return;
  openPanel('details');
  if (app.page !== 'browse') setPage('browse');
  if (id === app.selectedId) void loadDetails(id);
  else select(id);
}

export function setHover(id: number): void {
  app.hoverId = id;
}

async function goTo(id: number): Promise<void> {
  setPage('browse');
  setHover(-1);
  app.viewRootId = id;
  app.selectedId = id;
  await Promise.all([loadView(), loadDetails(id), loadRootDetails(), loadLargest(), loadList(), loadOwners()]);
}

export async function zoomTo(id: number): Promise<void> {
  if (id < 0 || id === app.viewRootId) return;
  app.backStack = [...app.backStack, app.viewRootId];
  app.forwardStack = [];
  await goTo(id);
}

export async function back(): Promise<void> {
  const prev = app.backStack.at(-1);
  if (prev === undefined) return;
  app.backStack = app.backStack.slice(0, -1);
  app.forwardStack = [...app.forwardStack, app.viewRootId];
  await goTo(prev);
}

export async function forward(): Promise<void> {
  const next = app.forwardStack.at(-1);
  if (next === undefined) return;
  app.forwardStack = app.forwardStack.slice(0, -1);
  app.backStack = [...app.backStack, app.viewRootId];
  await goTo(next);
}

export async function up(): Promise<void> {
  const parent = app.rootDetails?.ancestors.at(-1);
  if (parent) await zoomTo(parent.id);
}

/** 面包屑：从树根到当前视图根 */
export function viewTrail(): T.Ancestor[] {
  const d = app.rootDetails;
  return d ? [...d.ancestors, { id: d.id, name: d.name }] : [];
}

/** 搜索结果、大文件、最近文件、清理条目的「定位」：切到空间浏览，放大到它的父目录，选中它并打开详情 */
export async function reveal(id: number): Promise<void> {
  if (id < 0) return;
  const seq = ++detailsSeq;
  app.details = null;
  app.detailsLoading = true;
  app.detailsError = null;
  app.menu = null;
  openPanel('details');
  try {
    const d = await readDetails(id);
    if (seq !== detailsSeq || app.screen !== 'main') return;
    const parent = d.ancestors.at(-1)?.id ?? d.id;
    const navigate = parent !== app.viewRootId;
    if (navigate) {
      app.backStack = [...app.backStack, app.viewRootId];
      app.forwardStack = [];
      setHover(-1);
      app.viewRootId = parent;
    }
    // 定位和普通选择共用详情序号；一次写入选中项与详情，后续导航加载不再覆盖它们。
    app.selectedId = id;
    app.details = d;
    app.detailsLoading = false;
    openPanel('details');
    app.searchOpen = false;
    setPage('browse');
    if (navigate) await Promise.all([loadView(), loadRootDetails(), loadLargest(), loadList(), loadOwners()]);
  } catch (e) {
    if (seq === detailsSeq) app.detailsError = message(e);
  } finally {
    if (seq === detailsSeq) app.detailsLoading = false;
  }
}

export async function setMetric(m: T.Metric): Promise<void> {
  if (m === app.metric) return;
  app.metric = m;
  setHover(-1);
  await Promise.all([
    loadView(),
    loadDetails(),
    loadRootDetails(),
    loadLargest(),
    loadRecent(),
    loadList(),
    loadOwners(),
    app.searchQuery.trim() ? runSearch(app.searchQuery) : Promise.resolve(),
  ]);
}

// ---------------- 搜索 ----------------

let searchTimer: ReturnType<typeof setTimeout> | undefined;
let searchSeq = 0;

export function setSearchQuery(q: string): void {
  app.searchQuery = q;
  // 外部搜索不随打字查本地，也不把输入发出去；回车提交才打开浏览器（submitWebSearch）
  if (app.searchScope !== 'files') {
    app.searchOpen = false;
    return;
  }
  app.searchOpen = q.trim().length > 0;
  clearTimeout(searchTimer);
  searchSeq++;
  app.searchResult = null;
  app.searchLoading = q.trim().length > 0;
  if (!q.trim()) return;
  searchTimer = setTimeout(() => void runSearch(q), 120);
}

export async function runSearch(q: string, quiet = false): Promise<void> {
  if (q !== app.searchQuery || !q.trim() || app.searchScope !== 'files') return;
  if (quiet && app.searchLoading) return;
  clearTimeout(searchTimer);
  const seq = ++searchSeq;
  if (!quiet) {
    app.searchResult = null;
    app.searchLoading = true;
  }
  try {
    const r = await api.search(q, null, app.metric, 300, true, app.searchSort);
    if (seq === searchSeq) app.searchResult = r;
  } catch (e) {
    if (seq === searchSeq && !quiet) fail(e, t('搜索失败'));
  } finally {
    if (seq === searchSeq) app.searchLoading = false;
  }
}

export function setSearchSort(sort: T.SearchSort): void {
  if (sort === app.searchSort) return;
  app.searchSort = sort;
  void runSearch(app.searchQuery);
}

/** 输入或度量变化后，旧行的点击和回车都不能再定位。 */
export async function revealSearchResult(id: number): Promise<void> {
  const r = app.searchResult;
  if (app.searchLoading || !r || r.query !== app.searchQuery || !r.items.some((item) => item.id === id)) return;
  await reveal(id);
}

export function closeSearch(): void {
  app.searchOpen = false;
}

export function setSearchScope(scope: SearchScope): void {
  if (scope === app.searchScope) return;
  app.searchScope = scope;
  clearTimeout(searchTimer);
  searchSeq++;
  app.searchResult = null;
  app.searchLoading = false;
  if (scope === 'files') setSearchQuery(app.searchQuery);
  else {
    app.searchOpen = false;
    setEngine(scope); // 选过的引擎也给右键「在网上搜索」用
  }
}

/** 顶栏外部搜索：用户选了引擎并按回车才打开浏览器，只发输入框里的这几个字，不带扫描记录或路径 */
export async function submitWebSearch(): Promise<void> {
  const engine = app.searchScope, query = app.searchQuery.trim();
  if (engine === 'files' || !query) return;
  try {
    const r = await api.webSearch(engine, query);
    if (!r.ok) throw new Error(r.message ?? t('打开失败'));
    toast(isMock ? t('演示模式：不会打开浏览器') : t('已在浏览器里用 {engine} 搜索「{query}」', { engine: engine === 'google' ? 'Google' : 'Bing', query }));
  } catch (e) {
    fail(e, t('无法打开浏览器'));
  }
}

/** 搜索框的 id 约定只放这一处：Ctrl+F、空状态里的「去搜索」都调它 */
export const SEARCH_INPUT_ID = 'search-input';

export function focusSearch(): void {
  const input = document.getElementById(SEARCH_INPUT_ID) as HTMLInputElement | null;
  input?.focus();
  input?.select();
}

// ---------------- 右键菜单与外壳操作 ----------------

export function openMenu(m: MenuState): void {
  app.menu = m;
}

export function closeMenu(): void {
  app.menu = null;
}

export async function openInExplorer(id: number): Promise<void> {
  try {
    const r = await api.openInExplorer(id);
    if (!r.ok) throw new Error(r.message ?? t('打开失败'));
    if (isMock) toast(t('演示模式：不会打开真实文件'));
  } catch (e) {
    fail(e, t('无法在资源管理器中显示'));
  }
}

export async function searchWeb(id: number): Promise<void> {
  try {
    const r = await api.searchWeb(id, app.settings.engine);
    if (!r.ok) throw new Error(r.message ?? t('打开失败'));
    if (isMock) toast(t('演示模式：不会打开浏览器'));
  } catch (e) {
    fail(e, t('无法打开浏览器'));
  }
}

export async function showProperties(id: number): Promise<void> {
  try {
    const r = await api.properties(id);
    if (!r.ok) throw new Error(r.message ?? t('打开失败'));
    if (isMock) toast(t('演示模式：不会打开系统属性'));
  } catch (e) {
    fail(e, t('无法打开属性'));
  }
}

export async function copyText(text: string, label = t('路径')): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    toast(t('已复制{label}', { label }), 'success');
  } catch {
    toast(t('复制失败'), 'error');
  }
}

// ---------------- 删除、清理、提权（都先进确认框） ----------------

let confirmSeq = 0;

export async function requestDelete(ids: number[], permanent: boolean): Promise<void> {
  if (app.viewLoading || app.detailsLoading) return;
  const valid = ids.filter((id) => id >= 0);
  if (!valid.length || tasksBusy()) return;
  const scanId = currentScanId;
  const seq = ++confirmSeq;
  try {
    const check = await api.checkDelete(valid);
    if (seq !== confirmSeq || scanId !== currentScanId || app.screen !== 'main') return;
    app.confirm = { kind: 'delete', check, permanent, busy: false };
  } catch (e) {
    if (seq === confirmSeq) fail(e, t('无法删除'));
  }
}

export function requestSuggestion(s: T.Suggestion, nodeIds: number[] | null = null): void {
  if (s.action === 'info' || tasksBusy()) return;
  if (s.action === 'openSettings') {
    void runSuggestionDirect(s);
    return;
  }
  app.confirm = { kind: 'cleanup', suggestion: s, nodeIds: explicitIds(s, nodeIds), permanent: s.defaultPermanent, busy: false };
  void prepareCleanup(app.confirm);
}

async function prepareCleanup(c: ConfirmState): Promise<void> {
  const seq = ++confirmSeq;
  c.preparing = true;
  c.preflightError = undefined;
  try {
    if (c.kind === 'cleanup') {
      const check = await api.checkSuggestion(c.suggestion.id, c.nodeIds, c.permanent);
      if (seq === confirmSeq && app.confirm === c) c.check = check;
    } else if (c.kind === 'cleanupBatch') {
      for (const it of c.items) {
        if (it.manual) continue;
        const check = await api.checkSuggestion(it.suggestion.id, explicitIds(it.suggestion, it.nodeIds),
          it.suggestion.action === 'deleteNodes' ? c.permanent : null);
        if (seq !== confirmSeq || app.confirm !== c) return;
        it.check = check;
      }
    }
  } catch (e) {
    if (seq === confirmSeq && app.confirm === c) c.preflightError = message(e);
  } finally {
    if (seq === confirmSeq && app.confirm === c) c.preparing = false;
  }
}

async function runSuggestionDirect(s: T.Suggestion): Promise<void> {
  try {
    const r = await api.runSuggestion(s.id, null, null);
    toast(r.message ?? (r.ok ? t('已打开') : t('操作失败')), r.ok ? 'info' : 'error');
  } catch (e) {
    fail(e);
  }
}

/** 深度扫描、读受保护的目录需要管理员：弹确认框，确认后以管理员身份重启并自动开始扫描。denied：这次扫描没读到的目录数 */
export function requestElevation(scanPath: string | null, mode: T.ScanMode = 'mft', denied?: number): void {
  if (tasksBusy()) return; // 重启会打断正在执行的删除
  confirmSeq++;
  app.confirm = { kind: 'elevate', scanPath, mode, busy: false, denied };
}

export function setPermanent(v: boolean): void {
  const c = app.confirm;
  if (!c || c.kind === 'elevate' || c.busy || c.permanent === v) return;
  c.permanent = v;
  if (c.kind === 'cleanup' || c.kind === 'cleanupBatch') void prepareCleanup(c);
}

export function cancelConfirm(): void {
  const c = app.confirm;
  if (c?.busy) return;
  if (c?.kind === 'elevate' && c.denied && c.scanPath) declinedElevation.add(c.scanPath.toLowerCase());
  confirmSeq++;
  app.confirm = null;
}

export async function confirmAction(): Promise<void> {
  const c = app.confirm;
  if (!c || c.busy || c.preparing || c.preflightError) return;
  c.busy = true;
  try {
    if (c.kind === 'elevate') {
      const r = await api.relaunchAdmin(c.scanPath, c.mode);
      app.confirm = null;
      if (!r.ok) toast(r.message ?? t('已取消，仍以标准权限运行'), 'warn');
      else if (isMock) {
        toast(r.message ?? t('演示模式：已模拟切换为管理员'));
        app.info = await api.info();
        await refreshDrives();
        if (c.scanPath && app.info?.isAdmin) await startScan(c.scanPath, c.mode);
      }
      return;
    }
    // 单项删除、单条清理、批量清理走同一条路：冻结确认框核对过的清单和令牌，交给任务区逐项执行。
    // 确认框随即关掉，进度、取消和结果都在清理右栏的任务区；全部有了确定结果才刷新树。
    if (!taskRunner) throw new Error(t('清理任务区尚未就绪，没有执行任何操作'));
    const tasks = confirmedTasks(c);
    if (!tasks.length) throw new Error(t('没有可执行的项目'));
    const scanId = currentScanId;
    const run = taskRunner.start(tasks);
    app.confirm = null;
    // 不换页：在最近文件页删的，回到地图就看到任务区；概览里的清理格也会显示进度
    openPanel('cleanup');
    const items = await run;
    if (scanId !== currentScanId) return;
    reportTasks(items);
    await afterMutation(items.flatMap((i) => i.request.nodeIds ?? []));
  } catch (e) {
    c.busy = false;
    fail(e);
  }
}

const baseName = (p: string): string => p.split(/[\\/]/).filter(Boolean).at(-1) ?? p;

/** 把确认框当时核对并冻结的清单变成任务：不重新挑选、不扩大目标；令牌和参数与预检时一模一样，后台才认 */
function confirmedTasks(c: Exclude<ConfirmState, { kind: 'elevate' }>): ConfirmedCleanupTask[] {
  if (c.kind === 'delete') {
    const items = c.check.items.filter((i) => !i.blocked);
    if (!items.length) return [];
    const one = { name: baseName(items[0].path) }, n = { n: items.length };
    return [{
      // 标题在确认这一刻定下来，之后换语言也不改：它是这批任务的记录
      title: items.length === 1 ? (c.permanent ? t('永久删除「{name}」', one) : t('移到回收站「{name}」', one))
        : c.permanent ? t('永久删除 {n} 项', n) : t('移到回收站 {n} 项', n),
      kind: 'delete', nodeIds: items.map((i) => i.id), permanent: c.permanent,
      confirmationToken: c.check.confirmationToken ?? '', targetCount: items.length, recycle: !c.permanent,
    }];
  }
  if (c.kind === 'cleanup') return [cleanupTask(c.suggestion, c.nodeIds, c.permanent, c.check)];
  // 只有「删除所选」类建议听确认框的永久删除开关；清缓存类一律按建议自己的默认值
  return c.items.filter((it) => !it.manual)
    .map((it) => cleanupTask(it.suggestion, it.nodeIds, it.suggestion.action === 'deleteNodes' ? c.permanent : null, it.check));
}

function cleanupTask(s: T.Suggestion, nodeIds: number[] | null, permanent: boolean | null, check?: T.CleanupCheck): ConfirmedCleanupTask {
  return {
    title: s.title, kind: 'cleanup', suggestionId: s.id, nodeIds: explicitIds(s, nodeIds), permanent,
    confirmationToken: check?.confirmationToken ?? '',
    targetCount: check?.deleteCheck?.items.filter((i) => !i.blocked).length ?? 1,
    recycle: s.action === 'deleteNodes' && !(permanent ?? s.defaultPermanent),
  };
}

/** 一句话收尾通知；明细（失败原因、每项释放量）留在任务区，Toast 不代替记录 */
function reportTasks(items: CleanupTask[]): void {
  const count = (status: CleanupTask['status']) => items.filter((i) => i.status === status).length;
  const parts = [t('完成 {n} 项', { n: count('completed') })];
  if (count('failed')) parts.push(t('失败 {n} 项', { n: count('failed') }));
  if (count('cancelled')) parts.push(t('取消 {n} 项', { n: count('cancelled') }));
  if (count('external')) parts.push(t('已打开外部工具 {n} 项', { n: count('external') }));
  let freed = 0, unknown = false;
  for (const i of items) {
    if (i.progress?.freedBytes != null) freed += i.progress.freedBytes;
    else if (i.submitted && i.status !== 'external') unknown = true;
  }
  const space = unknown ? t('部分释放量未确认') : t('已核实释放 {size}@@通知', { size: formatBytes(freed) });
  const recycle = items.some((i) => i.request.recycle && i.status !== 'cancelled') ? t('，移入回收站的要清空回收站后才释放') : '';
  toast(t('{demo}清理任务结束：{parts}；{space}{recycle}。结果保留在清理任务区。', { demo: isMock ? t('演示模式：') : '', parts: parts.join(' · '), space, recycle }),
    count('failed') ? 'warn' : 'success');
}

/** 删除后：视图根或它的祖先被删了，就退到最近的活祖先；然后全部刷新。
 *  live：别的程序删了东西、或自己的删除还在进行 —— 安静刷新，不清空不转圈不弹错，前进后退记录留着 */
async function afterMutation(deleted: number[], live = false): Promise<void> {
  detailsPending.clear();
  largestKey = '';
  if (!live) setHover(-1);
  const scanId = currentScanId;
  const selectionSeq = detailsSeq;
  const root = app.viewRootId;
  const gone = new Set(deleted);
  for (const ancestor of viewTrail().reverse()) {
    try {
      await api.details(ancestor.id, app.metric);
      if (scanId !== currentScanId) return;
      if (selectionSeq !== detailsSeq || root !== app.viewRootId) break;
      if (ancestor.id !== app.viewRootId) app.selectedId = ancestor.id;
      app.viewRootId = ancestor.id;
      break;
    } catch {
      // 清理建议可能删除当前视图的祖先，从近到远寻找仍存在的目录。
    }
  }
  if (scanId !== currentScanId) return;
  if (selectionSeq === detailsSeq && !live) {
    app.backStack = [];
    app.forwardStack = [];
    if (gone.has(app.selectedId)) app.selectedId = app.viewRootId;
  }
  const summaryP = api.summary().catch(() => null);
  await Promise.all([loadView(live), selectionSeq === detailsSeq ? loadDetails(app.selectedId, live) : Promise.resolve(), loadRootDetails(live),
    loadLargest(live), loadRecent(live), loadSuggestions(live), loadList(false, live), loadOwners(live)]);
  const fresh = await summaryP;
  // 等的这一阵可能已经换了扫描：旧摘要写回去，D 盘的地图就配上了 C 盘的汇总，按 F5 还会去重扫 C 盘。
  // 同一次扫描里，先发出、后回来的旧版本也不能盖掉别的刷新已经写上的新版本
  if (scanId !== currentScanId) return;
  if (fresh && fresh.scanId === scanId && (app.summary?.scanId !== scanId || fresh.treeVersion >= app.summary.treeVersion)) app.summary = fresh;
  if (app.searchQuery.trim()) void runSearch(app.searchQuery, live);
}

// ---------------- 实时状态（扫描之后磁盘上的变化） ----------------

/** 树比界面上的汇总新：别的程序删了东西，已经落进树里 */
const treeMoved = (): boolean => !!app.live && app.live.scanId === app.summary?.scanId && app.live.treeVersion > app.summary.treeVersion;

let liveTimer: ReturnType<typeof setTimeout> | undefined;
let liveBusy = false;
let liveAgain = false;

/** 攒一下再刷新：一阵删除只刷一次，刷新期间又来的等这次完了再补一次 */
function scheduleLive(delay: number): void {
  if (liveBusy) liveAgain = true;
  else if (liveTimer === undefined) liveTimer = setTimeout(() => void runLive(), delay);
}

async function runLive(): Promise<void> {
  liveTimer = undefined;
  if (app.screen !== 'main' || !app.summary) return;
  liveBusy = true;
  try {
    await afterMutation([], true);
  } finally {
    liveBusy = false;
  }
  if (liveAgain) {
    liveAgain = false;
    scheduleLive(400);
  }
}

/** 页签栏右侧那一句：在不在跟踪、同步了多少、要不要重扫 */
export function liveStatus(): { text: string; stale: boolean } | null {
  const c = app.live;
  if (!c || c.scanId !== app.summary?.scanId) return null;
  if (!c.watching) return { text: t('这个位置收不到变化通知，有变化要重新扫描'), stale: false };
  if (c.overflow) return { text: t('变化太多，跟丢了一部分，建议重新扫描'), stale: true };
  const parts = [c.removedEntries ? t('已同步别的程序删除的 {n} 项', { n: c.removedEntries }) : t('实时跟踪中')];
  if (c.otherChanges) parts.push(t('另有 {n} 处新建或改名，统计可能过期', { n: c.otherChanges }));
  return { text: parts.join(' · '), stale: c.otherChanges > 0 };
}

// ---------------- 清理勾选与脚本（DESIGN §9.5） ----------------

/** 建议刷新后：还在的保留原勾选（子集里已经不存在的节点剔掉），新出现的 safe 默认勾上 */
function reconcileSelection(
  next: T.Suggestion[],
  prev: Record<string, CleanupPick>,
  prevList: T.Suggestion[],
): Record<string, CleanupPick> {
  const known = new Set(prevList.map((s) => s.id));
  const out: Record<string, CleanupPick> = {};
  for (const s of next) {
    if (!isSelectable(s)) continue;
    const old = prev[s.id];
    if (old && known.has(s.id)) {
      const alive = new Set(s.nodeIds);
      const items = s.selectable ? (old.items?.filter((id) => alive.has(id)) ?? null) : null;
      out[s.id] = { on: old.on && (items === null || items.length > 0), items };
    } else {
      // 清空回收站无法恢复，要单独确认：新出现时不默认勾上
      out[s.id] = { on: s.group === 'safe' && s.action !== 'emptyRecycleBin', items: null };
    }
  }
  return out;
}

/** 能被勾进脚本的建议：别删组和纯说明不行 */
export function isSelectable(s: T.Suggestion): boolean {
  return s.group !== 'protected' && s.action !== 'info';
}

/** 删除类建议一律把确认框当时的列表发给后端：传 null 的话后端会按「现在」重算候选，可能删到确认框里没列出的东西 */
function explicitIds(s: T.Suggestion, picked: number[] | null): number[] | null {
  if (s.action !== 'deleteNodes' && s.action !== 'deleteContents') return picked;
  return picked ?? s.nodeIds;
}

function pickBytes(s: T.Suggestion, pick: CleanupPick | undefined): number {
  if (!pick?.on) return 0;
  if (!pick.items) return s.bytes;
  const chosen = new Set(pick.items);
  return s.items.filter((i) => chosen.has(i.id)).reduce((sum, i) => sum + i.alloc, 0);
}

function computeStats(list: T.Suggestion[], sel: Record<string, CleanupPick>): CleanupStats {
  const st: CleanupStats = { safe: { bytes: 0, count: 0 }, review: { bytes: 0, count: 0 }, selected: { bytes: 0, count: 0, immediateBytes: 0, recycleBytes: 0, manualBytes: 0 } };
  for (const s of list) {
    if (s.group === 'safe') {
      st.safe.bytes += s.bytes;
      st.safe.count++;
    } else if (s.group === 'caution') {
      st.review.bytes += s.bytes;
      st.review.count++;
    }
    const b = pickBytes(s, sel[s.id]);
    if (sel[s.id]?.on) {
      st.selected.bytes += b;
      st.selected.count++;
      const space = cleanupSpace(s, b, false);
      st.selected.immediateBytes += space.immediate;
      st.selected.recycleBytes += space.recycle;
      st.selected.manualBytes += space.manual;
    }
  }
  return st;
}

function computeMarks(list: T.Suggestion[], sel: Record<string, CleanupPick>): CleanupMarks {
  const group = new Map<number, T.SuggestionGroup>();
  const selected = new Set<number>();
  // 同一节点命中多条建议时显示较高风险，不能让缓存建议遮住保护提示。
  const rank = { protected: 2, caution: 1, safe: 0 } as const;
  for (const s of list) {
    for (const id of s.nodeIds) {
      const g = group.get(id);
      if (!g || rank[s.group] > rank[g]) group.set(id, s.group);
    }
    const pick = sel[s.id];
    if (pick?.on) for (const id of pick.items ?? s.nodeIds) selected.add(id);
  }
  return { group, selected };
}

export function toggleSuggestion(id: string, on?: boolean): void {
  const s = app.suggestions.find((x) => x.id === id);
  if (!s || !isSelectable(s)) return;
  const cur = app.cleanupSel[id] ?? { on: false, items: null };
  const next = on ?? !cur.on;
  // 勾上整条就是整条：清掉旧子集，免得用户以为全选了其实只选了一半
  app.cleanupSel = { ...app.cleanupSel, [id]: { on: next, items: next ? null : cur.items } };
}

/** 勾选建议里的单个条目（selectable 的建议） */
export function toggleSuggestionItem(id: string, nodeId: number, on?: boolean): void {
  const s = app.suggestions.find((x) => x.id === id);
  if (!s || !s.selectable || !isSelectable(s) || !s.nodeIds.includes(nodeId)) return;
  const cur = app.cleanupSel[id] ?? { on: false, items: null };
  const set = new Set(cur.on ? (cur.items ?? s.nodeIds) : []);
  const next = on ?? !set.has(nodeId);
  if (next) set.add(nodeId);
  else set.delete(nodeId);
  const items = set.size === s.nodeIds.length ? null : [...set];
  app.cleanupSel = { ...app.cleanupSel, [id]: { on: set.size > 0, items } };
}

/** 参考图的「Select safe」：safe 组全勾上（清空回收站除外，它要单独确认），其余不动 */
export function selectSafe(): void {
  const next = { ...app.cleanupSel };
  for (const s of app.suggestions) if (s.group === 'safe' && isSelectable(s) && s.action !== 'emptyRecycleBin') next[s.id] = { on: true, items: null };
  app.cleanupSel = next;
}

export function selectedCleanup(): T.CleanupSelection[] {
  return app.suggestions
    .filter((s) => app.cleanupSel[s.id]?.on)
    .map((s) => ({ suggestionId: s.id, nodeIds: explicitIds(s, app.cleanupSel[s.id].items) }));
}

/** 参考图的「Copy script」 */
let scriptSeq = 0;
export async function copyCleanupScript(): Promise<void> {
  if (app.scriptLoading) return;
  const selections = selectedCleanup();
  if (!selections.length) {
    toast(t('先勾选要清理的项'), 'warn');
    return;
  }
  const seq = ++scriptSeq;
  app.scriptLoading = true;
  try {
    const r = await api.cleanupScript(selections);
    if (seq !== scriptSeq || app.screen !== 'main') return;
    await navigator.clipboard.writeText(r.script);
    toast(t('已复制清理脚本（{n} 项 · {size}）', { n: r.items, size: formatBytes(r.bytes) }) + (r.needsAdmin ? t('，其中有需要管理员 PowerShell 的段落') : ''), 'success');
  } catch (e) {
    if (seq === scriptSeq) fail(e, t('生成或复制清理脚本失败'));
  } finally {
    if (seq === scriptSeq) app.scriptLoading = false;
  }
}

/** 「立即清理…」：把勾选的建议放进确认框，确认后逐条执行 */
export function requestCleanupSelected(): void {
  if (tasksBusy()) return;
  const items: BatchItem[] = [];
  for (const s of app.suggestions) {
    const pick = app.cleanupSel[s.id];
    if (!pick?.on) continue;
    // 命令、设置要用户自己处理；清空回收站无法恢复，不随批量执行，在确认框里单独确认
    const manual = s.action === 'command' || s.action === 'openSettings' || s.action === 'info' || s.action === 'emptyRecycleBin';
    items.push({ suggestion: s, nodeIds: explicitIds(s, pick.items), bytes: pickBytes(s, pick), manual });
  }
  if (!items.length) {
    toast(t('先勾选要清理的项'), 'warn');
    return;
  }
  app.confirm = { kind: 'cleanupBatch', items, permanent: false, busy: false };
  void prepareCleanup(app.confirm);
}

/** 确认框里「需要手动处理」那几条的按钮：运行命令或打开设置 */
export async function runManualSuggestion(s: T.Suggestion): Promise<void> {
  requestSuggestion(s);
}

// ---------------- 设置 ----------------

function loadSettings(): Settings {
  try {
    const s = JSON.parse(localStorage.getItem(SETTINGS_KEY) ?? '{}') as Partial<Settings>;
    return { engine: s.engine === 'google' ? 'google' : 'bing' };
  } catch {
    return { engine: 'bing' };
  }
}

/** 右键和详情里「在网上搜索」用的引擎：就是顶栏最后选过的 Google / Bing，没选过是 Bing。不再单设一个设置项 */
export const engineLabel = (): string => (app.settings.engine === 'google' ? 'Google' : 'Bing');

function setEngine(engine: T.SearchEngine): void {
  app.settings.engine = engine;
  try {
    localStorage.setItem(SETTINGS_KEY, JSON.stringify(app.settings));
  } catch {
    // 存不下就算了，下次启动回到默认
  }
}
