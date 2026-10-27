import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';
import { compileModule } from 'svelte/compiler';
import type { api as Api } from '../src/lib/api.ts';
import type { ConfirmedCleanupTask } from '../src/lib/cleanupTasks.svelte.ts';
import type { OperationStatus, OpResult } from '../src/lib/types.ts';

const settle = () => new Promise<void>((resolve) => setImmediate(resolve));
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
const request = (title = '缓存'): ConfirmedCleanupTask => ({ title, kind: 'cleanup', suggestionId: title, nodeIds: [1], permanent: true, confirmationToken: `token-${title}`, targetCount: 1 });
const result: OpResult = { ok: true, alreadyGone: 0, freedBytes: 4, deleted: 1, failed: 0, errors: [], message: null, treeVersion: 2 };
function progress(operationId: string, values: Partial<OperationStatus> = {}): OperationStatus {
  return { operationId, kind: 'cleanup', status: 'completed', createdAtMs: 1, updatedAtMs: 2, currentPath: null,
    total: 1, completed: 1, failed: 0, skipped: 0, processedEntries: 3, freedBytes: 4, result, message: null, ...values };
}
let instance = 0;
async function setup(overrides: Partial<typeof Api> = {}) {
  let listener: (status: OperationStatus) => void = () => {};
  let subscriptions = 0;
  const calls: string[] = [];
  const bridge = {
    runSuggestion: (_id: string, _nodes: number[] | null, _permanent: boolean | null, _token?: string | null, operationId?: string) => { calls.push(operationId!); return new Promise<OpResult>(() => {}); },
    deleteNodes: (_nodes: number[], _permanent: boolean, _token?: string | null, operationId?: string) => { calls.push(operationId!); return new Promise<OpResult>(() => {}); },
    operationStatus: async () => null,
    cancelOperation: async (id: string) => progress(id, { status: 'cancelled', completed: 0, skipped: 1 }),
    on: (_event: string, cb: (status: OperationStatus) => void) => { listener = cb; subscriptions++; return () => {}; },
    ...overrides,
  };
  const globals = globalThis as typeof globalThis & { __cleanupTestApi?: typeof bridge };
  globals.__cleanupTestApi = bridge;
  const source = await readFile(new URL('../src/lib/cleanupTasks.svelte.ts', import.meta.url), 'utf8');
  const js = stripTypeScriptTypes(source).replace("import { api } from './api.ts';", 'const api = globalThis.__cleanupTestApi;');
  const compiled = compileModule(js, { filename: 'cleanupTasks.svelte.js', generate: 'client' }).js.code
    .replace(/from (['"])([^'"]+)\1/g, (_, q: string, path: string) => `from ${q}${path.startsWith('.') ? new URL(`../src/lib/${path}`, import.meta.url).href : import.meta.resolve(path)}${q}`);
  const state = await import(`data:text/javascript;base64,${Buffer.from(compiled + `\n// ${instance++}`).toString('base64')}`) as typeof import('../src/lib/cleanupTasks.svelte.ts');
  delete globals.__cleanupTestApi;
  return { ...state, calls, emit: (status: OperationStatus) => listener(status), subscriptions: () => subscriptions };
}

test('冻结确认清单，固定顺序执行；旧请求/未来项事件不推进，结果与历史保留', async () => {
  const s = await setup();
  const first = request('一');
  const done = s.startCleanupTasks([first, request('二')]);
  first.nodeIds!.push(999);
  assert.deepEqual([...s.cleanupTasks.items[0].request.nodeIds!], [1]);
  const [a, b] = s.cleanupTasks.items;
  s.emit(progress(b.operationId));
  assert.equal(s.calls.length, 1, '未提交项事件不提前推进');
  s.emit(progress(a.operationId));
  assert.equal(s.calls.length, 2);
  s.emit(progress(a.operationId, { status: 'running', updatedAtMs: 99 }));
  assert.equal(s.cleanupTasks.items[0].status, 'completed');
  s.emit(progress(b.operationId));
  assert.equal((await done).length, 2);
  assert.equal(s.cleanupTasks.active, false);
  const next = s.startCleanupTasks([request('三')]);
  assert.equal(s.cleanupTasks.history.length, 1);
  assert.equal(s.subscriptions(), 1);
  s.emit(progress(s.calls[2]));
  await next;
});

test('取消当前项后不提交后续；迟到 running 不撤销取消，取消结果如实保留', async () => {
  const s = await setup({ cancelOperation: async (id) => progress(id, { status: 'cancelling', completed: 0, updatedAtMs: 8 }) });
  const done = s.startCleanupTasks([request('一'), request('二')]);
  await s.cancelCleanupTasks();
  s.emit(progress(s.calls[0], { status: 'running', completed: 0, updatedAtMs: 3 }));
  assert.equal(s.cleanupTasks.items[0].status, 'cancelling');
  assert.equal(s.cleanupTasks.cancelRequested, true);
  s.emit(progress(s.calls[0], { status: 'cancelled', completed: 0, skipped: 1, updatedAtMs: 9 }));
  await done;
  assert.equal(s.calls.length, 1);
  assert.equal(s.cleanupTasks.items[1].status, 'cancelled');
  assert.equal(s.cleanupTasks.items[1].submitted, false);
});

test('请求超时只查询、不重发；查不到保留 unknown，后来查到终态才继续', async (t) => {
  let status: OperationStatus | null = null;
  let queries = 0;
  const s = await setup({ operationStatus: async () => { queries++; return status; } });
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const done = s.startCleanupTasks([request('一'), request('二')]);
  t.mock.timers.tick(30_000);
  await settle();
  assert.equal(s.cleanupTasks.items[0].status, 'unknown');
  assert.equal(s.calls.length, 1);
  assert.equal(queries, 1);
  assert.throws(() => s.startCleanupTasks([request('重复')]), /状态待确认/);
  t.mock.timers.tick(120_000);
  await settle();
  assert.equal(s.calls.length, 1);
  status = progress(s.calls[0]);
  await s.refreshCleanupTasks();
  assert.equal(s.calls.length, 2);
  s.emit(progress(s.calls[1]));
  await done;
  assert.equal(s.cleanupTasks.active, false);
  t.mock.timers.tick(120_000);
  await settle();
  assert.equal(queries, 2, '终态清理定时器');
});

test('传输失败后保留 unknown；取消查询失败也不提交下一项', async () => {
  const s = await setup({ runSuggestion: async () => { throw new Error('断联'); }, cancelOperation: async () => { throw new Error('取消响应丢失'); } });
  const done = s.startCleanupTasks([request('一'), request('二')]);
  await settle();
  assert.equal(s.cleanupTasks.items[0].status, 'unknown');
  await s.cancelCleanupTasks();
  assert.equal(s.cleanupTasks.items[1].submitted, false);
  s.emit(progress(s.cleanupTasks.items[0].operationId));
  await done;
  assert.equal(s.cleanupTasks.items[1].status, 'cancelled');
});

test('外部工具只报告 external；部分失败保留原因，await 仅在全部终态结束', async () => {
  const s = await setup();
  const done = s.startCleanupTasks([request('命令'), request('部分失败')]);
  let resolved = false;
  void done.then(() => { resolved = true; });
  s.emit(progress(s.calls[0], { status: 'external', completed: 0, processedEntries: 0, freedBytes: null, message: '已打开终端' }));
  await settle();
  assert.equal(resolved, false);
  assert.equal(s.cleanupTasks.items[0].status, 'external');
  assert.equal(s.cleanupTasks.items[0].progress!.freedBytes, null);
  const partial = { ...result, ok: false, failed: 1, errors: [{ path: 'C:\\fixture', message: '文件占用' }] };
  s.emit(progress(s.calls[1], { status: 'failed', total: 2, failed: 1, result: partial }));
  const items = await done;
  assert.equal(items[1].progress!.result!.errors[0].message, '文件占用');
});

test('旧状态查询未返回时，新项仍可查询；迟到响应与迟到执行错误不覆盖新项', async () => {
  const oldQuery = deferred<OperationStatus | null>();
  const oldRequest = deferred<OpResult>();
  let queries = 0;
  const s = await setup({
    runSuggestion: () => oldRequest.promise,
    operationStatus: (id) => ++queries === 1 ? oldQuery.promise : Promise.resolve(progress(id)),
  });
  const done = s.startCleanupTasks([request('一'), request('二')]);
  const a = s.cleanupTasks.items[0].operationId;
  const querying = s.refreshCleanupTasks();
  s.emit(progress(a));
  const b = s.cleanupTasks.items[1].operationId;
  await s.refreshCleanupTasks();
  await done;
  oldQuery.resolve(progress(a, { status: 'running', updatedAtMs: 999 }));
  oldRequest.reject(new Error('迟到的响应'));
  await querying;
  await settle();
  assert.equal(s.cleanupTasks.items[1].operationId, b);
  assert.equal(s.cleanupTasks.items[1].status, 'completed');
  assert.equal(s.cleanupTasks.querying, false);
});

test('查询自身超时仍保留未知，不把断联冒充失败', async (t) => {
  const s = await setup({ operationStatus: () => new Promise(() => {}) });
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const done = s.startCleanupTasks([request()]);
  const query = s.refreshCleanupTasks();
  t.mock.timers.tick(10_000);
  await query;
  assert.equal(s.cleanupTasks.items[0].status, 'unknown');
  assert.match(s.cleanupTasks.items[0].message!, /查询超时/);
  assert.equal(s.calls.length, 1);
  s.emit(progress(s.calls[0]));
  await done;
});

test('排队取消与执行前失败保留确认目标数，不把初始化零值冒充执行结果', async () => {
  const zero = { total: 0, completed: 0, failed: 0, skipped: 0, processedEntries: 0, freedBytes: null, result: null };
  const s = await setup({ cancelOperation: async (id) => progress(id, { ...zero, status: 'cancelled' }) });
  const done = s.startCleanupTasks([{ ...request('排队'), targetCount: 10 }, { ...request('未提交'), targetCount: 2 }]);
  s.emit(progress(s.calls[0], { ...zero, status: 'queued' }));
  assert.deepEqual(s.cleanupTargetCounts(s.cleanupTasks.items[0]), { total: 10, skipped: 0 });
  await s.cancelCleanupTasks(); await done;
  assert.deepEqual(s.cleanupTargetCounts(s.cleanupTasks.items[0]), { total: 10, skipped: 10 });
  assert.deepEqual(s.cleanupTargetCounts(s.cleanupTasks.items[1]), { total: 2, skipped: 2 });
  const failed = s.startCleanupTasks([{ ...request(), targetCount: 10 }]);
  s.emit(progress(s.calls[1], { ...zero, status: 'failed', message: '确认令牌失效' }));
  await failed;
  assert.deepEqual(s.cleanupTargetCounts(s.cleanupTasks.items[0]), { total: 10, skipped: 0 });
  for (const status of ['completed', 'external'] as const) {
    const next = s.startCleanupTasks([{ ...request(status), targetCount: 0 }]);
    s.emit(progress(s.calls.at(-1)!, { ...zero, status })); await next;
    assert.deepEqual(s.cleanupTargetCounts(s.cleanupTasks.items[0]), { total: 0, skipped: 0 });
  }
});

test('混合已知与未知释放量保留核实部分，取消未提交项不制造未知收益', async () => {
  const s = await setup();
  const done = s.startCleanupTasks([request('已完成'), request('未确认'), request('不提交')]);
  assert.deepEqual(s.cleanupReleasedSpace(s.cleanupTasks.items), { bytes: null, partial: true });
  s.emit(progress(s.calls[0], { freedBytes: 1024 }));
  assert.deepEqual(s.cleanupReleasedSpace(s.cleanupTasks.items), { bytes: 1024, partial: true });
  await s.cancelCleanupTasks(); await done;
  assert.deepEqual(s.cleanupReleasedSpace(s.cleanupTasks.items), { bytes: 1028, partial: false });
  const external = s.startCleanupTasks([request('外部终端')]);
  s.emit(progress(s.calls.at(-1)!, { status: 'external', freedBytes: null })); await external;
  assert.deepEqual(s.cleanupReleasedSpace(s.cleanupTasks.items), { bytes: null, partial: true });
  assert.deepEqual(s.cleanupReleasedSpace(s.cleanupTasks.history[0]), { bytes: 1028, partial: false });
});

test('后台明确拒绝且查不到任务记录：确认未执行，按失败收尾并接着提交后面的，这一项不重发', async () => {
  let first = true;
  const calls: string[] = [];
  const s = await setup({ runSuggestion: async (_id, _nodes, _permanent, _token, operationId) => {
    calls.push(operationId!);
    if (first) { first = false; throw Object.assign(new Error('任务编号无效'), { replied: true }); }
    return new Promise<OpResult>(() => {});
  } });
  const done = s.startCleanupTasks([request('一'), request('二')]);
  await settle(); await settle();
  assert.equal(s.cleanupTasks.items[0].status, 'failed');
  assert.match(s.cleanupTasks.items[0].message!, /后台拒绝执行：任务编号无效；没有任务记录，确认未执行/);
  assert.equal(calls.length, 2, '确认没执行的那项不重发，接着提交下一项');
  s.emit(progress(calls[1]));
  await done;
});
