import type * as T from './types.ts';
import { t } from './i18n.ts';

// 桥接客户端。在 WebView2 里走 chrome.webview；在普通浏览器里（pnpm dev）自动换成 mock.ts 的模拟后端。

export type MockHandler = (method: string, params: unknown) => Promise<unknown>;
export type Emit = (event: string, data: unknown) => void;

/** 后台明确回了错误：请求已经送达并处理过（区别于超时、断联）；任务区靠 replied 判断「确实没执行」 */
export class BridgeError extends Error { readonly replied = true; }
export class BridgeTimeoutError extends Error {}

interface WebView {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', cb: (e: MessageEvent) => void): void;
}

const webview = (globalThis as { chrome?: { webview?: WebView } }).chrome?.webview;
const listeners = new Map<string, Set<(data: unknown) => void>>();
const pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
let nextId = 1;

const emit: Emit = (event, data) => listeners.get(event)?.forEach((cb) => cb(data));

let mock: Promise<MockHandler> | null = null;

if (webview) {
  webview.addEventListener('message', (e) => {
    const m = e.data as { id?: number; ok?: boolean; result?: unknown; error?: { message?: string }; event?: string; data?: unknown };
    if (!m || typeof m !== 'object') return;
    if (typeof m.event === 'string') {
      emit(m.event, m.data);
      return;
    }
    if (typeof m.id !== 'number') return;
    const p = pending.get(m.id);
    if (!p) return;
    pending.delete(m.id);
    if (m.ok) p.resolve(m.result);
    else p.reject(new BridgeError(m.error?.message ?? t('未知错误')));
  });
} else {
  mock = import('./mock.ts').then((m) => m.createMockBackend(emit));
}

async function call<R>(method: string, params: unknown = {}, timeoutMs = 0): Promise<R> {
  if (mock) {
    try {
      return (await (await mock)(method, params)) as R;
    } catch (e) {
      throw e instanceof BridgeError ? e : new BridgeError(e instanceof Error ? e.message : String(e));
    }
  }
  const id = nextId++;
  return new Promise<R>((resolve, reject) => {
    const timer = timeoutMs ? setTimeout(() => {
      pending.delete(id);
      reject(new BridgeTimeoutError(t('请求超时，操作结果待确认')));
    }, timeoutMs) : undefined;
    pending.set(id, {
      resolve: (value) => { clearTimeout(timer); resolve(value as R); },
      reject: (error) => { clearTimeout(timer); reject(error); },
    });
    try { webview!.postMessage({ id, method, params }); }
    catch (error) { pending.delete(id); clearTimeout(timer); reject(error); }
  });
}

export const api = {
  isMock: !webview,

  info: () => call<T.AppInfo>('app.info'),
  /** 换后台发给界面的文字，并存下来：下次启动默认就是这个语言 */
  setLanguage: (language: string) => call<T.Ok>('app.setLanguage', { language }),
  relaunchAdmin: (scanPath: string | null, mode: T.ScanMode | null) =>
    call<T.Ok>('app.relaunchAdmin', { scanPath, mode }),
  listDrives: () => call<T.DriveInfo[]>('drives.list'),
  pickFolder: () => call<T.PathResult>('dialog.pickFolder'),

  startScan: (path: string, mode: T.ScanMode) => call<T.ScanStarted>('scan.start', { path, mode }),
  cancelScan: () => call<T.Ok>('scan.cancel'),
  summary: () => call<T.ScanSummary | null>('scan.summary'),
  /** 每个范围最近一次成功扫描的摘要（新的在前）；只是「上次扫描结果」，不能拿来导航或删除 */
  savedSummaries: () => call<T.SavedSummary[]>('scan.savedSummaries'),
  /** 异常清单按需翻页；scanId 传正在展示的那次扫描，换过扫描会报错而不是给别的扫描的清单 */
  issues: (kind: T.ScanIssueKind, offset = 0, limit = 200, scanId: number | null = null) =>
    call<T.ScanIssues>('scan.issues', { kind, offset, limit, scanId }),

  view: (nodeId: number, metric: T.Metric, maxNodes = 12000) =>
    call<T.ViewData>('tree.view', { nodeId, metric, maxNodes }),
  details: (nodeId: number, metric: T.Metric) => call<T.NodeDetails>('tree.details', { nodeId, metric }),
  largest: (nodeId: number, metric: T.Metric, limit = 100) =>
    call<T.FileItem[]>('tree.largest', { nodeId, metric, limit }),
  /** 目录的全部直接子项，分页；地图并掉的小块在这里都能看到 */
  children: (nodeId: number, metric: T.Metric, sort: T.ChildSort, offset = 0, limit = 200) =>
    call<T.Children>('tree.children', { nodeId, metric, sort, offset, limit }),
  /** 最近 days 天改过的文件；窗口按扫描完成时刻开，scopeId 为 null 是整棵树 */
  recent: (days: number, scopeId: number | null, sort: T.RecentSort, metric: T.Metric, limit = 100) =>
    call<T.RecentFiles>('tree.recent', { days, scopeId, sort, metric, limit }),
  /** 当前范围按所属对象聚合；scopeId 为 null 是整棵树 */
  owners: (scopeId: number | null, metric: T.Metric, limit = 100) =>
    call<T.OwnerUsage>('tree.owners', { scopeId, metric, limit }),
  /** 节点的所属对象和同一对象的其他模块；只用于浏览定位，不扩大清理范围 */
  related: (nodeId: number, metric: T.Metric, limit = 50) =>
    call<T.Related>('tree.related', { nodeId, metric, limit }),

  /** smart：拼音首字母 + 没有精确结果时的近似匹配（顶栏搜索开着） */
  search: (query: string, scopeId: number | null, metric: T.Metric, limit = 300, smart = false, sort: T.SearchSort = 'size') =>
    call<T.SearchResult>('search.query', { query, scopeId, metric, limit, smart, sort }),

  suggestions: () => call<T.Suggestion[]>('cleanup.list'),
  checkSuggestion: (suggestionId: string, nodeIds: number[] | null, permanent: boolean | null) =>
    call<T.CleanupCheck>('cleanup.check', { suggestionId, nodeIds, permanent }),
  runSuggestion: (suggestionId: string, nodeIds: number[] | null, permanent: boolean | null, confirmationToken?: string | null, operationId?: string) =>
    call<T.OpResult>('cleanup.run', { suggestionId, nodeIds, permanent, confirmationToken, operationId }, operationId ? 30_000 : 0),
  cleanupScript: (selections: T.CleanupSelection[]) => call<T.CleanupScript>('cleanup.script', { selections }),

  openInExplorer: (nodeId: number) => call<T.Ok>('node.openInExplorer', { nodeId }),
  /** 启动页按路径打开扫描记录里的位置（记录的范围、认出的应用）；后台只认记录里有的路径 */
  openFolder: (path: string) => call<T.Ok>('shell.openFolder', { path }),
  searchWeb: (nodeId: number, engine: T.SearchEngine) => call<T.Ok>('node.searchWeb', { nodeId, engine }),
  /** 顶栏外部搜索：只发用户输入并提交的词 */
  webSearch: (engine: 'google' | 'bing', query: string) => call<T.Ok>('web.search', { engine, query }),
  properties: (nodeId: number) => call<T.Ok>('node.properties', { nodeId }),
  checkDelete: (nodeIds: number[]) => call<T.DeleteCheck>('node.checkDelete', { nodeIds }),
  deleteNodes: (nodeIds: number[], permanent: boolean, confirmationToken?: string | null, operationId?: string) => call<T.OpResult>('node.delete', { nodeIds, permanent, confirmationToken, operationId }, operationId ? 30_000 : 0),
  operationStatus: (operationId: string) => call<T.OperationStatus | null>('operation.status', { operationId }, 10_000),
  cancelOperation: (operationId: string) => call<T.OperationStatus>('operation.cancel', { operationId }, 10_000),

  on<E extends keyof T.EventMap>(event: E, cb: (data: T.EventMap[E]) => void): () => void {
    let set = listeners.get(event);
    if (!set) listeners.set(event, (set = new Set()));
    const fn = cb as (data: unknown) => void;
    set.add(fn);
    return () => set.delete(fn);
  },
};
