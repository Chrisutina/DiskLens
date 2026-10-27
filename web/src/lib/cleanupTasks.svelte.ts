import { api } from './api.ts';
import type { OperationStatus } from './types.ts';
import { t } from './i18n.ts';

/** 只接收确认框已经预检并冻结的清单；这里不重新选择或扩大目标。 */
export interface ConfirmedCleanupTask {
  title: string;
  kind: 'cleanup' | 'delete';
  suggestionId?: string;
  nodeIds: number[] | null;
  permanent: boolean | null;
  confirmationToken: string;
  targetCount: number;
  /** 移到回收站：后台报的释放量只含放不进回收站、被直接删掉的部分，其余要清空回收站后才释放 */
  recycle?: boolean;
}

export interface CleanupTask {
  request: ConfirmedCleanupTask;
  operationId: string;
  submitted: boolean;
  status: OperationStatus['status'] | 'pending' | 'unknown';
  progress: OperationStatus | null;
  message: string | null;
  /** 执行请求被后台明确拒绝（不是超时或断联）；这时再查不到任务记录，就能确认没有执行 */
  rejected: boolean;
}

/** 排队/预检阶段的零计数尚不是执行清单；取消前未开工仍保留已确认的目标数。 */
export function cleanupTargetCounts(item: CleanupTask): { total: number; skipped: number } {
  const p = item.progress;
  const beforePlan = !p?.total && !p?.processedEntries && item.status !== 'completed' && item.status !== 'external';
  const total = beforePlan ? item.request.targetCount : p?.total ?? 0;
  return { total, skipped: beforePlan && item.status === 'cancelled' ? total : p?.skipped ?? 0 };
}

/** 一部分任务未知时也保留已经对账的释放量；未提交就取消的项目确定释放0。 */
export function cleanupReleasedSpace(items: CleanupTask[]): { bytes: number | null; partial: boolean } {
  let bytes = 0, known = 0;
  for (const item of items) {
    if (item.progress?.freedBytes != null) { bytes += item.progress.freedBytes; known++; }
    else if (!item.submitted && item.status === 'cancelled') known++;
  }
  return { bytes: known ? bytes : null, partial: known < items.length };
}

export const cleanupTasks = $state({
  items: [] as CleanupTask[],
  history: [] as CleanupTask[][],
  active: false,
  cancelRequested: false,
  querying: false,
});

const WAIT_MS = 30_000;
const final = (status: CleanupTask['status']) => ['completed', 'cancelled', 'failed', 'external'].includes(status);
let listening = false;
let watchdog: ReturnType<typeof setTimeout> | undefined;
let finish: ((items: CleanupTask[]) => void) | null = null;
let queryingItem: CleanupTask | null = null;
const current = () => cleanupTasks.items.find((item) => !final(item.status));
const message = (error: unknown) => error instanceof Error ? error.message : String(error);

function clearWatchdog(): void {
  clearTimeout(watchdog);
  watchdog = undefined;
}

function watch(item: CleanupTask): void {
  clearWatchdog();
  watchdog = setTimeout(() => {
    if (current() !== item || final(item.status)) return;
    item.status = 'unknown';
    item.message = t('请求等待超时，状态待确认；不会自动重新执行。');
    void refreshCleanupTasks();
  }, WAIT_MS);
}

function apply(status: OperationStatus): void {
  const item = current();
  if (!item || item.operationId !== status.operationId || final(item.status)) return;
  if (item.progress && item.progress.updatedAtMs > status.updatedAtMs) return;
  item.progress = status;
  item.status = cleanupTasks.cancelRequested && (status.status === 'running' || status.status === 'queued') ? 'cancelling' : status.status;
  item.message = cleanupTasks.cancelRequested && !final(status.status)
    ? t('已请求停止；后续项目不会提交，当前目录操作可能需要等待结束。') : status.message;
  if (status.status === 'cancelled') cleanupTasks.cancelRequested = true;
  if (final(status.status)) {
    clearWatchdog();
    advance();
  } else watch(item);
}

/** 状态请求也可能断联；只停止等待，不把它解释成清理失败。 */
async function bounded<T>(request: Promise<T>): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([request, new Promise<never>((_, reject) => {
      timer = setTimeout(() => reject(new Error(t('状态查询超时'))), 10_000);
    })]);
  } finally { clearTimeout(timer); }
}

export async function refreshCleanupTasks(): Promise<void> {
  const item = current();
  if (!item?.submitted || queryingItem === item) return;
  queryingItem = item;
  cleanupTasks.querying = true;
  try {
    const status = await bounded(api.operationStatus(item.operationId));
    if (current() !== item) return;
    if (status) apply(status);
    else if (item.rejected) {
      // 后台回了错误，而且没有登记这个任务：登记早于执行，所以确认没执行。按失败收尾、接着处理后面的，这一项不重发。
      item.status = 'failed';
      item.message = t('{message}；没有任务记录，确认未执行，不会自动重发。', { message: item.message });
      clearWatchdog();
      advance();
    } else {
      item.status = 'unknown';
      item.message = t('暂未查到任务记录，状态待确认；不会重新提交。');
      clearWatchdog();
    }
  } catch (error) {
    if (current() !== item) return;
    item.status = 'unknown';
    item.message = t('状态待确认：{error}。不会重新提交。', { error: message(error) });
    clearWatchdog();
  } finally {
    if (queryingItem === item) { queryingItem = null; cleanupTasks.querying = false; }
  }
}

function advance(): void {
  if (cleanupTasks.cancelRequested) {
    for (const item of cleanupTasks.items) {
      if (!item.submitted && item.status === 'pending') {
        item.status = 'cancelled';
        item.message = t('已取消，未提交。');
      }
    }
  }
  const item = current();
  if (!item) {
    cleanupTasks.active = false;
    clearWatchdog();
    const resolve = finish;
    finish = null;
    resolve?.(cleanupTasks.items);
    return;
  }
  if (item.submitted) return;
  item.submitted = true;
  item.status = 'queued';
  watch(item);
  const r = item.request;
  const request = r.kind === 'delete'
    ? api.deleteNodes(r.nodeIds!, r.permanent === true, r.confirmationToken, item.operationId)
    : api.runSuggestion(r.suggestionId!, r.nodeIds, r.permanent, r.confirmationToken, item.operationId);
  // 结果和异常都先核对后端状态；传输失败不代表写操作没有发生。
  void request.then(() => {
    if (current() === item) void refreshCleanupTasks();
  }, (error) => {
    if (current() !== item) return;
    item.rejected = (error as { replied?: boolean } | null)?.replied === true;
    item.status = 'unknown';
    item.message = item.rejected ? t('后台拒绝执行：{error}', { error: message(error) }) : t('执行响应未确认：{error}', { error: message(error) });
    clearWatchdog();
    void refreshCleanupTasks();
  });
}

/** Promise 只在全部操作已知结束时返回，unknown 会保留队列、禁止再次启动。 */
export function startCleanupTasks(requests: ConfirmedCleanupTask[]): Promise<CleanupTask[]> {
  if (cleanupTasks.active) throw new Error(t('已有清理任务执行中或状态待确认，请先查询原任务。'));
  if (!requests.length || requests.some((r) => !r.confirmationToken || (r.kind === 'delete' ? !r.nodeIds?.length : !r.suggestionId))) {
    throw new Error(t('清理任务缺少已确认的目标或令牌。'));
  }
  if (!listening) {
    api.on('operation.progress', apply);
    listening = true;
  }
  if (cleanupTasks.items.length) cleanupTasks.history = [cleanupTasks.items, ...cleanupTasks.history];
  cleanupTasks.items = requests.map((request) => ({
    request: { ...request, nodeIds: request.nodeIds ? [...request.nodeIds] : null },
    operationId: crypto.randomUUID(), submitted: false, status: 'pending', progress: null, message: null, rejected: false,
  }));
  cleanupTasks.cancelRequested = false;
  cleanupTasks.active = true;
  const result = new Promise<CleanupTask[]>((resolve) => { finish = resolve; });
  advance();
  return result;
}

export async function cancelCleanupTasks(): Promise<void> {
  if (!cleanupTasks.active || cleanupTasks.cancelRequested) return;
  cleanupTasks.cancelRequested = true;
  const item = current();
  advance();
  if (!item?.submitted || final(item.status)) return;
  item.message = t('正在停止后续项目；已完成的操作不会回滚，当前目录操作可能需要等待结束。');
  try {
    const status = await bounded(api.cancelOperation(item.operationId));
    if (current() === item) apply(status);
  } catch (error) {
    if (current() !== item) return;
    item.status = 'unknown';
    item.message = t('取消结果待确认：{error}。后续项目不会提交。', { error: message(error) });
    clearWatchdog();
  }
}
