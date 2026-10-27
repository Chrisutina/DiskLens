import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';
import { compileModule } from 'svelte/compiler';
import type { api as Api } from '../src/lib/api.ts';
import type { CleanupTask, ConfirmedCleanupTask } from '../src/lib/cleanupTasks.svelte.ts';
import type { AppInfo, CleanupCheck, CleanupScript, DeleteCheck, NodeDetails, Ok, OperationStatus, OpResult, RecentFiles, ScanSummary, SearchResult, Suggestion, TreeChange, ViewData } from '../src/lib/types.ts';

type ApiType = typeof Api;
const settle = () => new Promise<void>((resolve) => setImmediate(resolve));
const details = (id: number, parent = 0) => ({ id, ancestors: id === 0 ? [] : [{ id: parent, name: 'parent' }] }) as NodeDetails;
const result = (query: string, id: number) => ({ query, items: [{ id }], total: 1, error: null }) as SearchResult;
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: Error) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

/** 任务区的假执行器：按确认清单直接调桥接，全部返回才结束；真执行器的状态机在 cleanupTasks.test.ts 里测 */
function fakeRunner(bridge: Partial<ApiType>) {
  const runner = {
    active: false,
    started: [] as ConfirmedCleanupTask[][],
    async start(tasks: ConfirmedCleanupTask[]): Promise<CleanupTask[]> {
      runner.started.push(tasks);
      const items: CleanupTask[] = [];
      for (const t of tasks) {
        const r = t.kind === 'delete'
          ? await bridge.deleteNodes!(t.nodeIds!, t.permanent === true, t.confirmationToken)
          : await bridge.runSuggestion!(t.suggestionId!, t.nodeIds, t.permanent, t.confirmationToken);
        items.push({ request: t, operationId: `op-${items.length}`, submitted: true, status: r.ok ? 'completed' : 'failed', progress: null, message: r.message, rejected: false });
      }
      return items;
    },
  };
  return runner;
}

// 编译实际的 Svelte 状态模块，只替换桥接 API；不在测试中复制状态机。
let instance = 0;
async function setup(overrides: Partial<ApiType> = {}) {
  const bridge = {
    isMock: false,
    details: async (id: number) => details(id),
    view: async (id: number) => ({ rootId: id, nodes: [] }) as unknown as ViewData,
    largest: async () => [],
    recent: async () => ({ asOfMs: 1, days: 7, total: 0, items: [] }),
    suggestions: async () => [],
    summary: async () => null,
    listDrives: async () => [],
    checkSuggestion: async () => ({ confirmationToken: 'fixture-token', deleteCheck: null }),
    ...overrides,
  };
  const globals = globalThis as typeof globalThis & { __diskLensTestApi?: typeof bridge };
  globals.__diskLensTestApi = bridge;
  const source = await readFile(new URL('../src/lib/state.svelte.ts', import.meta.url), 'utf8');
  const js = stripTypeScriptTypes(source).replace("import { api } from './api.ts';", 'const api = globalThis.__diskLensTestApi;');
  const compiled = compileModule(js, { filename: 'state.svelte.js', generate: 'client' }).js.code
    .replace(/from (['"])([^'"]+)\1/g, (_, quote: string, specifier: string) => {
      const url = specifier.startsWith('.') ? new URL(`../src/lib/${specifier}`, import.meta.url).href : import.meta.resolve(specifier);
      return `from ${quote}${url}${quote}`;
    });
  try {
    const state = await import(`data:text/javascript;base64,${Buffer.from(compiled + `\n// instance ${instance++}`).toString('base64')}`) as typeof import('../src/lib/state.svelte.ts');
    state.app.screen = 'main';
    const runner = fakeRunner(bridge as Partial<ApiType>);
    state.useTaskRunner(runner);
    return { ...state, runner };
  } finally {
    delete globals.__diskLensTestApi;
  }
}

test('定位 10 后选择 20：两种响应顺序都只保留 20，等待时不展示旧详情', async () => {
  for (const revealFirst of [true, false]) {
    const ten = deferred<NodeDetails>(), twenty = deferred<NodeDetails>();
    const s = await setup({ details: (id) => id === 10 ? ten.promise : twenty.promise });
    s.app.details = details(0);
    const pending = s.reveal(10);
    s.select(20);
    assert.equal(s.app.details, null);
    if (revealFirst) { ten.resolve(details(10, 1)); await pending; }
    twenty.resolve(details(20));
    await twenty.promise;
    await settle();
    if (!revealFirst) { ten.resolve(details(10, 1)); await pending; }
    assert.equal(s.app.selectedId, 20);
    assert.equal((s.app.details as NodeDetails | null)?.id, 20);
    assert.equal(s.app.viewRootId, 0);
  }
});

test('选择、导航、度量切换、重扫都会让在途定位失效', async () => {
  for (const action of ['selectSame', 'navigate', 'metric', 'scan'] as const) {
    const ten = deferred<NodeDetails>();
    const s = await setup({
      details: (id) => id === 10 ? ten.promise : Promise.resolve(details(id)),
      startScan: async () => ({ scanId: 2, mode: 'walk' }),
    });
    const pending = s.reveal(10);
    if (action === 'selectSame') s.select(0);
    if (action === 'navigate') await s.zoomTo(30);
    if (action === 'metric') await s.setMetric('size');
    if (action === 'scan') await s.startScan('C:\\fixture');
    ten.resolve(details(10, 1));
    await pending;
    assert.equal(s.app.selectedId, action === 'navigate' ? 30 : 0);
    assert.notEqual(s.app.details?.id, 10);
  }
});

test('连续定位丢弃旧响应和旧错误，后一次定位保留历史', async () => {
  for (const fails of [false, true]) {
    const ten = deferred<NodeDetails>();
    const s = await setup({ details: (id) => id === 10 ? ten.promise : Promise.resolve(details(id, 2)) });
    const old = s.reveal(10);
    await s.reveal(20);
    if (fails) ten.reject(new Error('stale error')); else ten.resolve(details(10, 1));
    await old;
    assert.equal(s.app.selectedId, 20);
    assert.equal(s.app.details?.id, 20);
    assert.equal(s.app.viewRootId, 2);
    assert.deepEqual([...s.app.backStack], [0]);
    assert.equal(s.app.toasts.length, 0);
  }
});

test('定位的视图加载尚未完成时，新的选择不会被覆盖', async () => {
  const view = deferred<ViewData>();
  const s = await setup({ view: () => view.promise, details: async (id) => details(id, 1) });
  const pending = s.reveal(10);
  await settle();
  s.select(20);
  await settle();
  view.resolve({ rootId: 1, nodes: [] } as unknown as ViewData);
  await pending;
  assert.equal(s.app.selectedId, 20);
  assert.equal(s.app.details?.id, 20);
});

test('搜索输入变化立即作废在途请求，防抖期间没有可选择旧结果', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const old = deferred<SearchResult>();
  const newer = deferred<SearchResult>();
  const calls: number[] = [];
  const queries: string[] = [];
  const s = await setup({
    search: (q) => { queries.push(q); return q === 'old' ? old.promise : newer.promise; },
    details: async (id) => { calls.push(id); return details(id); },
  });
  s.setSearchQuery('old');
  const pending = s.runSearch('old');
  s.setSearchQuery('new');
  old.resolve(result('old', 10));
  await pending;
  assert.equal(s.app.searchResult, null);
  assert.equal(s.app.searchLoading, true);
  await s.revealSearchResult(10);
  assert.deepEqual(calls, []);
  t.mock.timers.tick(119);
  assert.deepEqual(queries, ['old']);
  t.mock.timers.tick(1);
  newer.resolve(result('new', 20));
  await newer.promise;
  assert.equal((s.app.searchResult as SearchResult | null)?.query, 'new');
  assert.equal(s.app.searchLoading, false);
  await s.revealSearchResult(20);
  assert.deepEqual(calls, [20]);
});

test('已有结果在改词或清空时立即清掉，过期行事件也不能定位', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let calls = 0;
  const s = await setup({ details: async (id) => { calls++; return details(id); } });
  s.app.searchQuery = 'old';
  s.app.searchResult = result('old', 10);
  s.setSearchQuery('new');
  assert.equal(s.app.searchResult, null);
  await s.revealSearchResult(10);
  s.app.searchResult = result('old', 10);
  s.app.searchLoading = false;
  await s.revealSearchResult(10);
  assert.equal(calls, 0);
  s.setSearchQuery(' ');
  assert.equal(s.app.searchResult, null);
  assert.equal(s.app.searchLoading, false);
  t.mock.timers.tick(200);
});

test('命令按钮走现有确认流程，取消不执行，确认后才执行一次', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let runs = 0;
  const s = await setup({ runSuggestion: async () => {
    runs++;
    return { ok: true, alreadyGone: 0, freedBytes: 0, deleted: 0, failed: 0, errors: [], treeVersion: 1, message: null };
  } });
  const row = await readFile(new URL('../src/components/RecommendRow.svelte', import.meta.url), 'utf8');
  assert.match(row, /onclick=\{\(\) => requestSuggestion\(s\)\}/);
  assert.doesNotMatch(row, /runManualSuggestion/);
  const suggestion = { id: 'hibernate', action: 'command', command: 'powercfg /h off', defaultPermanent: true, nodeIds: [] } as unknown as Suggestion;
  s.requestSuggestion(suggestion);
  assert.equal(s.app.confirm?.kind, 'cleanup');
  assert.equal(runs, 0);
  s.cancelConfirm();
  await s.confirmAction();
  assert.equal(runs, 0);
  s.requestSuggestion(suggestion);
  await settle();
  await s.confirmAction();
  assert.equal(runs, 1);
  assert.equal(s.app.confirm, null);
});

test('悬停仅更新高亮，不再读取详情', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let calls = 0;
  const s = await setup({ details: async (id) => { calls++; return details(id); } });
  s.setHover(10);
  t.mock.timers.tick(200);
  assert.equal(s.app.hoverId, 10);
  s.setHover(20);
  t.mock.timers.tick(200);
  s.setHover(-1);
  assert.equal(s.app.hoverId, -1);
  assert.equal(calls, 0);
});

test('地图加载保留旧画面，仅最新请求结束加载；失败清除旧对象并可重试', async () => {
  const first = deferred<ViewData>(), latest = deferred<ViewData>();
  let calls = 0;
  const oldView = { rootId: 0, nodes: [] } as unknown as ViewData;
  const s = await setup({ view: () => ++calls === 1 ? first.promise : calls === 2 ? latest.promise : Promise.resolve(oldView) });
  s.app.view = oldView;
  const a = s.loadView(), b = s.loadView();
  assert.equal(s.app.view, oldView);
  assert.equal(s.app.viewLoading, true);
  first.resolve(oldView);
  await a;
  assert.equal(s.app.viewLoading, true);
  latest.reject(new Error('slow disk failed'));
  await b;
  assert.equal(s.app.viewLoading, false);
  assert.equal(s.app.view, null);
  assert.equal(s.app.viewError, 'slow disk failed');
  await s.loadView();
  assert.equal(s.app.view, oldView);
  assert.equal(s.app.viewError, null);
});

test('详情与定位加载时隐藏旧详情，旧响应不能提前结束加载或允许删除', async () => {
  const first = deferred<NodeDetails>(), latest = deferred<NodeDetails>();
  let deletes = 0;
  const s = await setup({
    details: (id) => id === 10 ? first.promise : latest.promise,
    checkDelete: async () => { deletes++; throw new Error('不应调用'); },
  });
  s.app.details = details(0);
  const a = s.reveal(10);
  assert.equal(s.app.details, null);
  assert.equal(s.app.detailsLoading, true);
  s.select(20);
  await s.requestDelete([0], true);
  first.resolve(details(10));
  await a;
  assert.equal(s.app.detailsLoading, true);
  assert.equal(deletes, 0);
  latest.resolve(details(20));
  await latest.promise;
  await settle();
  assert.equal(s.app.detailsLoading, false);
  assert.equal((s.app.details as NodeDetails | null)?.id, 20);
});

test('取消立即禁用重复提交；请求应答不会提前退出，结束事件才清除状态', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  for (const ending of ['scan.error', 'scan.done']) {
    const events = new Map<string, (data: unknown) => void>();
    const accepted = deferred<Ok>();
    let calls = 0;
    const s = await setup({
      on: (event, cb) => { events.set(event, cb as (data: unknown) => void); return () => {}; },
      info: async () => ({ autoScanPath: null }) as AppInfo,
      startScan: async () => ({ scanId: 7, mode: 'walk' }),
      cancelScan: () => { calls++; return accepted.promise; },
    });
    await s.init();
    await s.startScan('C:\\fixture');
    const pending = s.cancelScan();
    assert.equal(s.app.scanCancelling, true);
    await s.cancelScan();
    assert.equal(calls, 1);
    accepted.resolve({ ok: true, message: null });
    await pending;
    assert.equal(s.app.scanCancelling, true);
    assert.equal(s.app.screen, 'scanning');
    events.get(ending)!({ scanId: 7, cancelled: true, rootId: 0, warnings: [] });
    assert.equal(s.app.scanCancelling, false);
    assert.notEqual(s.app.screen, 'scanning');
  }
});

test('取消失败可重试；上一轮迟到的失败不能干扰新扫描的取消', async () => {
  const late = deferred<Ok>();
  const accepted = deferred<Ok>();
  let calls = 0;
  const s = await setup({
    startScan: async () => ({ scanId: 7, mode: 'walk' }),
    cancelScan: () => ++calls === 1 ? Promise.reject(new Error('暂时失败')) : calls === 2 ? late.promise : accepted.promise,
  });
  await s.startScan('C:\\fixture');
  await s.cancelScan();
  assert.equal(s.app.scanCancelling, false);
  assert.match(s.app.toasts[0].text, /取消失败/);
  const pending = s.cancelScan();
  assert.equal(s.app.scanCancelling, true);
  s.leaveScan();
  await s.startScan('C:\\another-fixture');
  const newer = s.cancelScan();
  late.reject(new Error('旧失败'));
  await pending;
  assert.equal(s.app.scanCancelling, true);
  assert.equal(s.app.toasts.filter((item) => item.text.includes('旧失败')).length, 0);
  accepted.resolve({ ok: true, message: null });
  await newer;
  assert.equal(s.app.scanCancelling, true);
});

test('根详情合并在途读取，完成后重新读取真实属性而非长期缓存', async () => {
  let reads = 0;
  const pending = deferred<NodeDetails>();
  const s = await setup({ details: () => { reads++; return pending.promise; } });
  const selection = s.loadDetails(), root = s.loadRootDetails();
  assert.equal(reads, 1);
  pending.resolve(details(0));
  await Promise.all([selection, root]);
  assert.equal(s.app.details?.id, s.app.rootDetails?.id);
  await s.loadDetails();
  assert.equal(reads, 2);
});

test('大文件按需读取，换目录不展示旧列表，失败重试且回到原目录不误用空缓存', async () => {
  const calls: number[] = [];
  let fail = false;
  const s = await setup({ largest: async (id) => {
    calls.push(id);
    if (fail) throw new Error('磁盘暂不可用');
    return result('', id + 1).items;
  } });
  await s.zoomTo(10);
  assert.equal(calls.length, 0);
  s.openPanel('largest'); await settle();
  assert.deepEqual(calls, [10]);
  s.openPanel('details');
  await s.zoomTo(20);
  assert.equal(s.app.largest.length, 0);
  s.openPanel('largest'); s.closePanel(); await settle();
  await s.zoomTo(25);
  assert.deepEqual(calls, [10, 20], '面板关着时换目录不读大文件');
  await s.zoomTo(10);
  s.openPanel('largest'); await settle();
  assert.deepEqual(calls, [10, 20, 10]);
  fail = true;
  await s.zoomTo(30);
  assert.equal(s.app.largest.length, 0);
  assert.equal(s.app.largestError, '磁盘暂不可用');
  fail = false;
  await s.loadLargest();
  assert.equal(s.app.largestError, null);
  assert.equal(s.app.largest[0].id, 31);
});

test('脚本生成防重入、有忙碌状态，换扫描后不会复制过期脚本', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const oldNavigator = Object.getOwnPropertyDescriptor(globalThis, 'navigator');
  const copied: string[] = [];
  Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { clipboard: { writeText: async (v: string) => { copied.push(v); } } } });
  t.after(() => { if (oldNavigator) Object.defineProperty(globalThis, 'navigator', oldNavigator); else Reflect.deleteProperty(globalThis, 'navigator'); });
  const pending = deferred<CleanupScript>();
  let calls = 0;
  const s = await setup({ cleanupScript: () => { calls++; return pending.promise; }, startScan: async () => ({ scanId: 9, mode: 'walk' }) });
  s.app.suggestions = [{ id: 'temp', action: 'deleteContents', group: 'safe', nodeIds: [5], items: [], bytes: 1 }] as unknown as Suggestion[];
  s.app.cleanupSel = { temp: { on: true, items: null } };
  const one = s.copyCleanupScript();
  await s.copyCleanupScript();
  assert.equal(calls, 1);
  assert.equal(s.app.scriptLoading, true);
  await s.startScan('C:\\fixture');
  pending.resolve({ script: 'old-script', bytes: 1, items: 1, needsAdmin: false });
  await one;
  assert.deepEqual(copied, []);
  assert.equal(s.app.scriptLoading, false);
});

test('清理模式改变使旧预检作废，仅把最新确认token交给执行；手动命令也先进确认', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const old = deferred<CleanupCheck>(), current = deferred<CleanupCheck>();
  let checks = 0;
  const tokens: (string | null | undefined)[] = [];
  const s = await setup({
    checkSuggestion: () => ++checks === 1 ? old.promise : current.promise,
    runSuggestion: async (_id, _ids, _permanent, token) => { tokens.push(token); return { ok: true, alreadyGone: 0, freedBytes: 0, deleted: 0, failed: 0, errors: [], treeVersion: 1, message: null }; },
  });
  const suggestion = { id: 'file', action: 'deleteNodes', nodeIds: [10], defaultPermanent: false } as Suggestion;
  s.requestSuggestion(suggestion);
  await s.confirmAction();
  assert.deepEqual(tokens, []);
  s.setPermanent(true);
  old.resolve({ confirmationToken: 'old', deleteCheck: null }); await settle();
  assert.equal(s.app.confirm?.preparing, true);
  current.resolve({ confirmationToken: 'current', deleteCheck: null }); await settle();
  await s.confirmAction();
  assert.deepEqual(tokens, ['current']);
  await s.runManualSuggestion({ ...suggestion, action: 'command' });
  assert.equal(s.app.confirm?.kind, 'cleanup');
  assert.deepEqual(tokens, ['current']);
});

test('清理建议重复刷新不会忘记用户取消勾选；失败时不显示旧建议并允许重试', async () => {
  const a = deferred<Suggestion[]>(), b = deferred<Suggestion[]>();
  const entry = { id: 'cache', action: 'deleteContents', group: 'safe', nodeIds: [10], items: [], bytes: 5 } as unknown as Suggestion;
  let count = 0;
  const s = await setup({ suggestions: () => ++count === 1 ? a.promise : count === 2 ? b.promise : Promise.resolve([entry]) });
  s.app.suggestions = [entry];
  s.app.cleanupSel = { cache: { on: false, items: null } };
  const first = s.loadSuggestions(), second = s.loadSuggestions();
  a.resolve([entry]); await first;
  b.reject(new Error('拒绝访问')); await second;
  assert.deepEqual(s.app.suggestions, []);
  assert.equal(s.app.suggestionsError, '拒绝访问');
  await s.loadSuggestions();
  assert.equal(s.app.cleanupSel.cache.on, false);
  assert.equal(s.app.suggestionsError, null);
});

test('同一对象命中清理建议和保护建议时，地图保留更高风险', async () => {
  const s = await setup();
  const sample = { action: 'deleteNodes', nodeIds: [10], items: [], bytes: 10 };
  s.app.suggestions = [
    { ...sample, id: 'safe', group: 'safe' },
    { ...sample, id: 'protected', group: 'protected' },
  ] as unknown as Suggestion[];
  assert.equal(s.app.cleanupMarks.group.get(10), 'protected');
});

test('最近文件按需查询：天数、目录、排序、度量和上限传给真状态模块', async () => {
  const calls: unknown[][] = [];
  const s = await setup({ recent: async (...args) => {
    calls.push(args);
    return { asOfMs: 123, days: args[0], total: 0, items: [] };
  } });
  await s.loadRecent();
  assert.equal(calls.length, 0);
  s.setPage('recent'); await settle();
  assert.deepEqual(calls, [[7, null, 'size', 'alloc', 100]]);
  s.app.recentDays = 1;
  s.app.recentSort = 'modified';
  s.app.recentInFolder = true;
  s.app.viewRootId = 15;
  await s.setMetric('size');
  assert.deepEqual(calls.at(-1), [1, 15, 'modified', 'size', 100]);
  assert.equal(s.app.recent?.asOfMs, 123);
  s.setPage('browse');
  assert.equal(s.app.recent, null);
  assert.equal(calls.length, 2);
});

test('最近文件快速换筛选：旧响应和旧错误不能覆盖新结果或结束加载', async () => {
  for (const fails of [false, true]) {
    const old = deferred<RecentFiles>(), current = deferred<RecentFiles>();
    let calls = 0;
    const s = await setup({ recent: () => ++calls === 1 ? old.promise : current.promise });
    s.app.page = 'recent';
    const first = s.loadRecent();
    s.app.recentDays = 1;
    const second = s.loadRecent();
    assert.equal(s.app.recent, null);
    if (fails) old.reject(new Error('旧请求失败'));
    else old.resolve({ asOfMs: 1, days: 7, total: 20, items: [] });
    await first;
    assert.equal(s.app.recentLoading, true);
    assert.equal(s.app.recent, null);
    assert.equal(s.app.recentError, null);
    current.resolve({ asOfMs: 2, days: 1, total: 0, items: [] });
    await second;
    assert.equal((s.app.recent as RecentFiles | null)?.days, 1);
    assert.equal(s.app.recentLoading, false);
  }
});

test('最近文件失败隐藏旧列表并可重试；离开页面后丢弃在途响应', async () => {
  const pending = deferred<RecentFiles>();
  let count = 0;
  const s = await setup({ recent: async () => {
    if (++count === 1) throw new Error('暂时读不到');
    if (count === 2) return { asOfMs: 1, days: 7, total: 0, items: [] };
    return pending.promise;
  } });
  s.app.page = 'recent';
  s.app.recent = { asOfMs: 0, days: 7, total: 10, items: [] };
  await s.loadRecent();
  assert.equal(s.app.recent, null);
  assert.equal(s.app.recentError, '暂时读不到');
  await s.loadRecent();
  assert.equal(s.app.recentError, null);
  assert.equal((s.app.recent as RecentFiles | null)?.total, 0);
  const request = s.loadRecent();
  s.setPage('browse');
  pending.resolve({ asOfMs: 2, days: 7, total: 10, items: [] });
  await request;
  assert.equal(s.app.recent, null);
  assert.equal(s.app.recentLoading, false);
});

test('重扫使最近文件在途请求失效；取消后重新读取旧扫描结果', async () => {
  const pending = deferred<RecentFiles>();
  let count = 0;
  const s = await setup({
    recent: () => ++count === 1 ? pending.promise : Promise.resolve({ asOfMs: 2, days: 7, total: 0, items: [] }),
    startScan: async () => ({ scanId: 2, mode: 'walk' }),
  });
  s.app.summary = { scanId: 1 } as ScanSummary;
  s.app.page = 'recent';
  const request = s.loadRecent();
  await s.startScan('C:\\fixture');
  pending.resolve({ asOfMs: 1, days: 7, total: 10, items: [] });
  await request;
  assert.equal(s.app.recent, null);
  assert.equal(s.app.recentLoading, false);
  s.leaveScan(); await settle();
  assert.equal(s.app.page, 'recent');
  assert.equal((s.app.recent as RecentFiles | null)?.asOfMs, 2);
});

test('最近文件定位回地图并选中文件；导航和返回启动页关闭最近文件视图', async () => {
  const s = await setup({ details: async (id) => details(id, 5) });
  s.setPage('recent'); await settle();
  await s.reveal(10);
  assert.equal(s.app.page, 'browse');
  assert.equal(s.app.viewRootId, 5);
  assert.equal(s.app.selectedId, 10);
  assert.equal(s.app.details?.id, 10);
  assert.equal(s.app.rightTab, 'details');
  assert.equal(s.app.panelOpen, true);
  s.setPage('recent'); await settle();
  await s.back();
  assert.equal(s.app.page, 'browse');
  assert.equal(s.app.viewRootId, 0);
  s.setPage('recent'); await settle();
  s.backToStart();
  assert.equal(s.app.page, 'browse');
  assert.equal(s.app.recent, null);
});

test('清理完成后重新读取最近文件，不保留已移除的条目', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let calls = 0;
  const s = await setup({
    recent: async () => { calls++; return { asOfMs: 1, days: 7, total: 0, items: [] }; },
    runSuggestion: async () => ({ ok: true, alreadyGone: 0, freedBytes: 0, deleted: 1, failed: 0, errors: [], treeVersion: 2, message: null }),
  });
  s.app.page = 'recent';
  s.app.recent = { asOfMs: 1, days: 7, total: 1, items: [] };
  s.requestSuggestion({ id: 'fixture', action: 'deleteContents', nodeIds: [10], defaultPermanent: false } as Suggestion);
  await settle();
  await s.confirmAction();
  assert.equal(calls, 1);
  assert.equal(s.app.recent?.total, 0);
});

test('地图与最近文件切换，旧总览/清理入口映射到地图及右栏，定位保留原有行为', async () => {
  let recentCalls = 0;
  const s = await setup({ details: async (id) => details(id, 5), recent: async () => { recentCalls++; return { asOfMs: 1, days: 7, total: 0, items: [] }; } });
  assert.equal(s.app.page, 'browse');
  assert.equal(s.app.panelOpen, false);
  for (const page of ['overview', 'cleanup', 'browse'] as const) {
    s.setPage(page); await settle();
    assert.equal(s.app.page, 'browse');
    if (page === 'cleanup') assert.equal(s.app.rightTab, 'cleanup');
  }
  assert.equal(recentCalls, 0);
  s.setPage('recent'); await settle();
  assert.equal(recentCalls, 1);

  s.setPage('cleanup');
  await s.reveal(10);
  assert.equal(s.app.page, 'browse');
  assert.equal(s.app.viewRootId, 5);
  assert.equal(s.app.selectedId, 10);
  assert.equal(s.app.panelOpen, true);
  assert.equal(s.app.rightTab, 'details');

  s.closePanel();
  s.setPage('overview');
  s.showDetails(12); await settle();
  assert.equal(s.app.page, 'browse');
  assert.equal(s.app.panelOpen, true);
  assert.equal(s.app.details?.id, 12);

  s.openPanel('largest');
  assert.equal(s.app.rightTab, 'largest');
  s.closePanel();
  assert.equal(s.app.panelOpen, false);
  s.backToStart();
  assert.equal(s.app.page, 'browse');
});

test('详情、大文件、清理右栏切换不重置目录、选中对象或清理勾选', async () => {
  const s = await setup();
  s.app.selectedId = 42;
  s.app.viewRootId = 10;
  s.app.backStack = [0, 5];
  s.app.cleanupSel = { fixture: { on: true, items: [42] } };
  for (const tab of ['details', 'largest', 'cleanup'] as const) {
    s.openPanel(tab); await settle();
    assert.equal(s.app.rightTab, tab);
    assert.equal(s.app.panelOpen, true);
    assert.equal(s.app.selectedId, 42);
    assert.equal(s.app.viewRootId, 10);
    assert.deepEqual(s.app.backStack, [0, 5]);
    assert.deepEqual(s.app.cleanupSel.fixture, { on: true, items: [42] });
  }
  s.closePanel();
  s.setPage('recent'); await settle();
  s.setPage('browse'); await settle();
  assert.equal(s.app.viewRootId, 10);
  assert.equal(s.app.selectedId, 42);
});

test('大文件面板开着时离开空间浏览不读；回来补读当前目录和度量', async () => {
  const calls: [number, string][] = [];
  const s = await setup({ largest: async (id, metric) => { calls.push([id, metric]); return []; } });
  s.openPanel('largest'); await settle();
  assert.deepEqual(calls, [[0, 'alloc']]);
  s.setPage('recent');
  await s.setMetric('size');
  assert.equal(calls.length, 1);
  s.setPage('browse'); await settle();
  assert.deepEqual(calls, [[0, 'alloc'], [0, 'size']]);
});

test('确认后把冻结的清单和令牌交给任务区：确认框立刻关闭、清理右栏打开，任务全部结束才刷新树', async () => {
  const run = deferred<OpResult>();
  let views = 0;
  const check: DeleteCheck = { confirmationToken: 'tok-1', items: [
    { id: 10, path: 'C:/a/x.bin', isDir: false, size: 1, alloc: 1, files: 1, blocked: false, reason: null, warning: null },
    { id: 11, path: 'C:/Windows', isDir: true, size: 1, alloc: 1, files: 1, blocked: true, reason: '系统目录', warning: null },
  ], totalAlloc: 2, totalSize: 2, totalFiles: 2, anyBlocked: true, anyWarning: false };
  const s = await setup({
    checkDelete: async () => check,
    deleteNodes: () => run.promise,
    view: async (id) => { views++; return { rootId: id, nodes: [] } as unknown as ViewData; },
  });
  await s.requestDelete([10, 11], false);
  assert.equal(s.app.confirm?.kind, 'delete');
  const confirming = s.confirmAction();
  await settle();
  assert.equal(s.app.confirm, null, '交给任务区后确认框立即关闭');
  assert.equal(s.app.panelOpen && s.app.rightTab, 'cleanup', '清理右栏打开，任务区可见');
  assert.deepEqual(s.runner.started[0].map((t) => [t.kind, t.nodeIds, t.permanent, t.confirmationToken, t.targetCount, t.recycle]),
    [['delete', [10], false, 'tok-1', 1, true]], '只交出没被拦下的对象和预检令牌');
  assert.equal(views, 0, '任务没结束不刷新地图');
  run.resolve({ ok: true, alreadyGone: 0, freedBytes: 0, deleted: 1, failed: 0, errors: [], message: null, treeVersion: 2 });
  await confirming;
  assert.equal(views, 1, '任务结束后刷新地图');
  assert.match(s.app.toasts.at(-1)!.text, /清理任务结束：完成 1 项.*清空回收站后才释放.*结果保留在清理任务区/);
});

test('任务没结束或状态待确认时，新的删除、清理、扫描、提权和回启动页都被挡住', async () => {
  let checks = 0, scans = 0;
  const s = await setup({
    checkDelete: async () => { checks++; throw new Error('不应调用'); },
    startScan: async () => { scans++; return { scanId: 9, mode: 'walk' }; },
  });
  s.runner.active = true;
  s.app.summary = { rootPath: 'D:/', mode: 'walk' } as ScanSummary;
  s.app.suggestions = [{ id: 'cache', action: 'deleteContents', group: 'safe', nodeIds: [10], items: [], bytes: 5 } as unknown as Suggestion];
  s.app.cleanupSel = { cache: { on: true, items: null } };
  await s.requestDelete([10], false);
  s.requestSuggestion(s.app.suggestions[0]);
  s.requestCleanupSelected();
  s.rescan();
  s.requestElevation('D:/');
  s.backToStart();
  await settle();
  assert.equal(checks, 0);
  assert.equal(scans, 0);
  assert.equal(s.app.confirm, null);
  assert.equal(s.app.screen, 'main', '启动页看不到任务区，留在工作区');
  assert.equal(s.app.panelOpen && s.app.rightTab, 'cleanup', '亮出任务区');
  assert.match(s.app.toasts.at(-1)!.text, /清理任务还没结束或状态待确认/);
  s.runner.active = false;
  s.requestCleanupSelected();
  // 上面断言过 confirm 是 null，TS 会一直按 null 收窄；重新按原类型读
  assert.equal((s.app.confirm as { kind: string } | null)?.kind, 'cleanupBatch', '任务有结果后恢复');
});

test('清空回收站要单独确认：新出现不默认勾、选中安全项不带它、批量里列为单独处理', async () => {
  const bin = { id: 'recycle-bin', action: 'emptyRecycleBin', group: 'safe', nodeIds: [5], items: [], bytes: 9 } as unknown as Suggestion;
  const cache = { id: 'cache', action: 'deleteContents', group: 'safe', nodeIds: [10], items: [], bytes: 5 } as unknown as Suggestion;
  const s = await setup({ suggestions: async () => [bin, cache] });
  await s.loadSuggestions();
  assert.equal(s.app.cleanupSel['recycle-bin']?.on, false);
  assert.equal(s.app.cleanupSel.cache?.on, true);
  s.toggleSuggestion('cache', false);
  s.selectSafe();
  assert.equal(s.app.cleanupSel['recycle-bin']?.on, false, '选中安全项不带回收站');
  assert.equal(s.app.cleanupSel.cache?.on, true);
  s.toggleSuggestion('recycle-bin', true);
  s.requestCleanupSelected();
  const c = s.app.confirm;
  assert.equal(c?.kind, 'cleanupBatch');
  if (c?.kind !== 'cleanupBatch') return;
  assert.deepEqual(c.items.map((i) => [i.suggestion.id, i.manual]), [['recycle-bin', true], ['cache', false]]);
});

test('目录列表：收着不读；展开读当前目录，接页只在同目录同排序时拼，换排序、换目录重读，迟到的旧页丢掉', async () => {
  const calls: [number, string, number][] = [];
  const pages = new Map<string, ReturnType<typeof deferred<import('../src/lib/types.ts').Children>>>();
  const s = await setup({
    children: (nodeId, _metric, sort, offset = 0) => {
      calls.push([nodeId, sort, offset]);
      const d = deferred<import('../src/lib/types.ts').Children>();
      pages.set(`${nodeId}:${sort}:${offset}`, d);
      return d.promise;
    },
  });
  const page = (nodeId: number, sort: 'name' | 'size' | 'modified', offset: number, ids: number[], total = 3) =>
    ({ nodeId, metric: 'alloc', sort, total, offset, items: ids.map((id) => ({ id, name: `n${id}`, isDir: false, value: 1, size: 1, alloc: 1, modifiedMs: null, k: 0, f: 0, u: 0, childCount: 0 })) }) as import('../src/lib/types.ts').Children;
  await s.loadList();
  assert.equal(calls.length, 0, '列表收着不读');
  s.toggleList(true);
  pages.get('0:size:0')!.resolve(page(0, 'size', 0, [1, 2]));
  await settle();
  assert.deepEqual(s.app.list?.items.map((i) => i.id), [1, 2]);
  const more = s.loadList(true);
  assert.deepEqual(calls.at(-1), [0, 'size', 2], '接着第 2 项往后读');
  pages.get('0:size:2')!.resolve(page(0, 'size', 2, [3]));
  await more;
  assert.deepEqual(s.app.list?.items.map((i) => i.id), [1, 2, 3], '同目录同排序的下一页拼在后面');
  s.setListSort('name');
  const late = pages.get('0:name:0')!;
  s.app.viewRootId = 5;
  const moved = s.loadList();
  late.resolve(page(0, 'name', 0, [9]));
  pages.get('5:name:0')!.resolve(page(5, 'name', 0, [7], 1));
  await moved;
  await settle();
  assert.deepEqual(s.app.list?.items.map((i) => i.id), [7], '换目录后，旧目录迟到的那页不覆盖');
});

test('目录列表展开到 600 条后安静刷新：一次最多 500，分页接着读回 600 条，不缩回去', async () => {
  const calls: [number, number][] = [];
  const total = 700;
  const s = await setup({
    children: async (nodeId, metric, sort, offset = 0, limit = 200) => {
      calls.push([offset, limit]);
      const ids = Array.from({ length: Math.max(0, Math.min(limit, 500, total - offset)) }, (_, i) => offset + i);
      return { nodeId, metric, sort, total, offset, items: ids.map((id) => ({ id, name: `n${id}`, isDir: false, value: 1, size: 1, alloc: 1, modifiedMs: null, k: 0, f: 0, u: 0, childCount: 0 })) } as import('../src/lib/types.ts').Children;
    },
  });
  s.toggleList(true);
  await settle();
  await s.loadList(true);
  await s.loadList(true);
  assert.equal(s.app.list?.items.length, 600, '先展开到 600 条');
  calls.length = 0;
  await s.loadList(false, true);
  assert.deepEqual(calls, [[0, 500], [500, 100]], '安静刷新：先读 500，再接着读剩下的 100');
  assert.deepEqual(s.app.list?.items.map((i) => i.id), Array.from({ length: 600 }, (_, i) => i), '还是 600 条，顺序接得上');
});

test('应用占用维度：只在应用维度读当前目录；回到目录分布清掉；图例筛选点两次取消，换着色方式清掉', async () => {
  const scopes: (number | null)[] = [];
  const s = await setup({ owners: async (scopeId) => { scopes.push(scopeId); return { scopeId: scopeId ?? 0, items: [] } as unknown as import('../src/lib/types.ts').OwnerUsage; } });
  s.app.viewRootId = 42;
  await s.loadOwners();
  assert.deepEqual(scopes, [], '目录分布下不读');
  s.setDimension('apps');
  await settle();
  assert.deepEqual(scopes, [42]);
  assert.equal(s.app.owners?.scopeId, 42);
  s.setDimension('dirs');
  await settle();
  assert.equal(s.app.owners, null);
  s.setLegendFilter(3);
  assert.equal(s.app.legendFilter, 3);
  s.setLegendFilter(3);
  assert.equal(s.app.legendFilter, null, '再点同一项取消');
  s.setLegendFilter(2);
  s.setExplorerMode('cleanup');
  assert.equal(s.app.legendFilter, null, '两种图例的键不是一回事，换着色方式清掉');
});

test('外部搜索：选了引擎打字不查本地、不发出去；回车只发输入的词；切回文件重新查本地', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const local: string[] = [], web: [string, string][] = [];
  const s = await setup({
    search: async (q) => { local.push(q); return { query: q, items: [], total: 0, tookMs: 1, truncated: false, error: null, fuzzy: false }; },
    webSearch: async (engine, query) => { web.push([engine, query]); return { ok: true, message: null }; },
  });
  s.setSearchScope('bing');
  s.setSearchQuery('  pagefile.sys ');
  t.mock.timers.tick(500);
  await settle();
  assert.deepEqual(local, [], '不随打字查本地');
  assert.equal(s.app.searchOpen, false);
  assert.deepEqual(web, [], '打字不发出去');
  await s.submitWebSearch();
  assert.deepEqual(web, [['bing', 'pagefile.sys']], '回车只发去掉首尾空白的词');
  s.setSearchScope('files');
  t.mock.timers.tick(500);
  await settle();
  assert.deepEqual(local, ['  pagefile.sys '], '切回文件，对当前输入查本地');
  await s.submitWebSearch();
  assert.equal(web.length, 1, '文件范围下回车不会打开浏览器');
});

// ---------------- 实时状态 ----------------

type Listener = (data: unknown) => void;
const listen = (events: Map<string, Listener>) => (event: string, cb: unknown) => { events.set(event, cb as Listener); return () => {}; };
const changed = (c: Partial<TreeChange>): TreeChange => ({ scanId: 3, watching: true, removedEntries: 0, otherChanges: 0, overflow: false, treeVersion: 4, ...c });

test('别的程序删了东西：攒一下再安静刷新，地图不清空、前进后退留着，选中的没了退回当前目录；只多了新建不刷新', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const events = new Map<string, Listener>();
  const pending = deferred<ViewData>();
  let views = 0, gone = false;
  const s = await setup({
    on: listen(events) as ApiType['on'],
    info: async () => ({ autoScanPath: null }) as AppInfo,
    startScan: async () => ({ scanId: 3, mode: 'walk' }),
    view: async (id) => { views++; return views === 2 ? pending.promise : ({ rootId: id, nodes: [] } as unknown as ViewData); },
    details: async (id) => { if (gone && id === 42) throw new Error('节点已删除'); return details(id); },
    summary: async () => ({ scanId: 3, treeVersion: 5 }) as ScanSummary,
  });
  await s.init();
  await s.startScan('C:\fixture');
  events.get('scan.done')!({ scanId: 3, treeVersion: 4, rootId: 0, warnings: [] });
  await settle();
  const old = s.app.view;
  Object.assign(s.app, { selectedId: 42, backStack: [7] });

  events.get('tree.changed')!(changed({ otherChanges: 2 }));
  t.mock.timers.tick(5000); await settle();
  assert.equal(views, 1, '只多了新建：树没变，不刷新');
  assert.deepEqual(s.liveStatus(), { text: '实时跟踪中 · 另有 2 处新建或改名，统计可能过期', stale: true });

  gone = true;
  events.get('tree.changed')!(changed({ removedEntries: 3, otherChanges: 2, treeVersion: 5 }));
  events.get('tree.changed')!(changed({ removedEntries: 4, otherChanges: 2, treeVersion: 6 }));
  assert.equal(views, 1, '先攒一下，不是来一条刷一次');
  t.mock.timers.tick(1500); await settle();
  assert.equal(views, 2, '一阵变化只刷一次');
  assert.equal(s.app.view, old, '刷新期间旧地图留着，不闪白');
  assert.equal(s.app.viewLoading, false, '安静刷新不转圈');
  const fresh = { rootId: 0, nodes: [] } as unknown as ViewData;
  pending.resolve(fresh); await settle();
  assert.equal(s.app.view, fresh);
  assert.deepEqual([s.app.selectedId, s.app.details?.id, s.app.detailsError], [0, 0, null], '选中的被删了：改选当前目录，不挂报错');
  assert.deepEqual(s.app.backStack, [7], '前进后退记录留着');
  assert.equal(s.app.summary?.treeVersion, 5, '汇总跟着刷新');
  assert.equal(s.app.toasts.length, 0, '安静刷新不弹提示');
  assert.match(s.liveStatus()!.text, /^已同步别的程序删除的 4 项 · 另有 2 处新建或改名/);

  events.get('tree.changed')!(changed({ scanId: 2, removedEntries: 9, treeVersion: 99 }));
  t.mock.timers.tick(5000); await settle();
  assert.equal(views, 2, '旧扫描迟到的变化不理');
  events.get('tree.changed')!(changed({ watching: false, treeVersion: 5 }));
  assert.equal(s.liveStatus()?.text, '这个位置收不到变化通知，有变化要重新扫描');
  events.get('tree.changed')!(changed({ overflow: true, treeVersion: 5 }));
  assert.deepEqual(s.liveStatus(), { text: '变化太多，跟丢了一部分，建议重新扫描', stale: true });
});

test('自己的删除还在跑：隔一会儿安静刷新一次地图，任务结束后进度事件不再触发', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const events = new Map<string, Listener>();
  let views = 0;
  const s = await setup({
    on: listen(events) as ApiType['on'],
    info: async () => ({ autoScanPath: null }) as AppInfo,
    view: async (id) => { views++; return { rootId: id, nodes: [] } as unknown as ViewData; },
  });
  await s.init();
  s.app.summary = { scanId: 1, treeVersion: 1 } as ScanSummary;
  const progress = (status: OperationStatus['status']) => events.get('operation.progress')!({ status } as OperationStatus);
  progress('running'); progress('running');
  t.mock.timers.tick(400); await settle();
  assert.equal(views, 1, '一阵进度只刷一次');
  progress('completed');
  t.mock.timers.tick(5000); await settle();
  assert.equal(views, 1, '收尾刷新归确认流程，进度事件不再刷');
});

test('取消重扫回到旧结果：刷新和实时变化都按旧结果的扫描号认', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const events = new Map<string, Listener>();
  const s = await setup({
    on: listen(events) as ApiType['on'],
    info: async () => ({ autoScanPath: null }) as AppInfo,
    startScan: async () => ({ scanId: 8, mode: 'walk' }),
    summary: async () => ({ scanId: 3, treeVersion: 9 }) as ScanSummary,
  });
  await s.init();
  s.app.summary = { scanId: 3, treeVersion: 4 } as ScanSummary;
  await s.startScan('C:\fixture');
  events.get('scan.error')!({ scanId: 8, message: '扫描已取消', cancelled: true });
  assert.equal(s.app.screen, 'main');
  events.get('tree.changed')!(changed({ removedEntries: 1, treeVersion: 9 }));
  t.mock.timers.tick(1500); await settle();
  assert.equal(s.app.summary?.treeVersion, 9, '旧结果的汇总照样刷新（以前会因为扫描号对不上被丢掉）');
});

test('顶栏搜索开智能匹配、默认按相关度；切排序立刻按新排序重查，同一种不重复查', async () => {
  const calls: unknown[][] = [];
  const s = await setup({ search: async (...args) => { calls.push(args.slice(3)); return { ...result(args[0], 1), fuzzy: false }; } });
  s.setSearchQuery('report');
  await s.runSearch('report');
  assert.deepEqual(calls, [[300, true, 'relevance']]);
  s.setSearchSort('size');
  await settle();
  assert.deepEqual(calls.at(-1), [300, true, 'size']);
  s.setSearchSort('size');
  await settle();
  assert.equal(calls.length, 2);
});

// ---------------- 扫完自动问要不要提权 ----------------

test('扫描发现有目录没权限读：扫完自动问要不要用管理员身份重扫；同一范围拒绝过就不再问，管理员扫的不问', async () => {
  const events = new Map<string, Listener>();
  let id = 0;
  const s = await setup({
    on: listen(events) as ApiType['on'],
    info: async () => ({ autoScanPath: null, isAdmin: false }) as AppInfo,
    startScan: async () => ({ scanId: ++id, mode: 'walk' }),
  });
  await s.init();
  const scan = async (root: string, more: Partial<ScanSummary>) => {
    await s.startScan(root);
    events.get('scan.done')!({ scanId: id, rootId: 0, rootPath: root, warnings: [], treeVersion: 1, inaccessibleDirs: 0, isAdmin: false, ...more });
    await settle();
  };
  await scan('C:\\', { inaccessibleDirs: 112 });
  const c = s.app.confirm as { kind: string; scanPath: string; mode: string; denied?: number } | null;
  assert.deepEqual([c?.kind, c?.scanPath, c?.mode, c?.denied], ['elevate', 'C:\\', 'auto', 112], '带上没读到的目录数，扫完就问');
  s.cancelConfirm();
  await scan('c:\\', { inaccessibleDirs: 112 });
  assert.equal(s.app.confirm, null, '同一范围（不分大小写）拒绝过：这次运行里不再问');
  await scan('D:\\', { inaccessibleDirs: 0 });
  assert.equal(s.app.confirm, null, '都读到了：不问');
  await scan('E:\\', { inaccessibleDirs: 5, isAdmin: true });
  assert.equal(s.app.confirm, null, '已经是管理员扫的：不问');
  await scan('F:\\', { inaccessibleDirs: 5 });
  assert.equal((s.app.confirm as { denied?: number } | null)?.denied, 5, '别的范围照样问');
});

// ---------------- 迟到的汇总 ----------------

test('安静刷新等汇总时换了扫描：迟到的旧汇总不写回，F5 重扫的还是当前范围；同一次扫描里旧版本不盖新版本', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const events = new Map<string, Listener>();
  const summaries: { promise: Promise<ScanSummary>; resolve: (v: ScanSummary) => void }[] = [];
  const started: string[] = [];
  let id = 0;
  const check: DeleteCheck = { confirmationToken: 'tok', items: [{ id: 5, path: 'D:/x.bin', isDir: false, size: 1, alloc: 1, files: 1, blocked: false, reason: null, warning: null }],
    totalAlloc: 1, totalSize: 1, totalFiles: 1, anyBlocked: false, anyWarning: false };
  const s = await setup({
    on: listen(events) as ApiType['on'],
    info: async () => ({ autoScanPath: null }) as AppInfo,
    startScan: async (path) => { started.push(path); return { scanId: ++id, mode: 'walk' }; },
    summary: () => { const d = deferred<ScanSummary>(); summaries.push(d); return d.promise; },
    checkDelete: async () => check,
    deleteNodes: async () => ({ ok: true, alreadyGone: 0, freedBytes: 1, deleted: 1, failed: 0, errors: [], message: null, treeVersion: 3 }),
  });
  await s.init();
  const done = (scanId: number, rootPath: string) =>
    events.get('scan.done')!({ scanId, rootId: 0, rootPath, mode: 'walk', warnings: [], treeVersion: 1, inaccessibleDirs: 0, isAdmin: false });
  const summary = (scanId: number, rootPath: string, treeVersion: number) => ({ scanId, rootPath, mode: 'walk', treeVersion }) as ScanSummary;

  await s.startScan('C:'); done(1, 'C:'); await settle();
  events.get('tree.changed')!(changed({ scanId: 1, removedEntries: 1, treeVersion: 2 }));
  t.mock.timers.tick(1500); await settle();
  assert.equal(summaries.length, 1, 'C 盘的安静刷新发出去了，汇总还没回来');
  await s.startScan('D:'); done(2, 'D:'); await settle();
  summaries[0].resolve(summary(1, 'C:', 2)); await settle();
  assert.equal(s.app.summary?.rootPath, 'D:', 'D 盘的地图配的还是 D 盘的汇总');

  events.get('tree.changed')!(changed({ scanId: 2, removedEntries: 1, treeVersion: 2 }));
  t.mock.timers.tick(1500); await settle();
  await s.requestDelete([5], true);
  const confirming = s.confirmAction();
  await settle();
  assert.equal(summaries.length, 3, '安静刷新和删除之后的刷新都在等汇总');
  summaries[2].resolve(summary(2, 'D:', 3)); await confirming;
  summaries[1].resolve(summary(2, 'D:', 2)); await settle();
  assert.equal(s.app.summary?.treeVersion, 3, '先发出、后回来的旧版本不盖掉删除之后的新汇总');

  s.rescan();
  assert.deepEqual(started, ['C:', 'D:', 'D:'], 'F5 重扫的是当前这次的范围');
});
