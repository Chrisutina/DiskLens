import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';
import { compileModule } from 'svelte/compiler';
import type { api as Api } from '../src/lib/api.ts';
import type { Ok, SavedApp, SavedSummary } from '../src/lib/types.ts';

let instance = 0;
const record = (rootPath: string, completedAtMs: number, more: Partial<SavedSummary> = {}) =>
  ({ rootPath, completedAtMs, apps: null, removedApps: null, appsComparedAtMs: null, ...more }) as SavedSummary;
const app = (name: string, more: Partial<SavedApp> = {}): SavedApp => ({
  kind: 'app', name, path: `C:\\Apps\\${name}`, alloc: 100, role: 1, evidence: 'confirmed', installation: true, firstSeenMs: 1, isNew: false, ...more,
});
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(yes => { resolve = yes; });
  return { promise, resolve };
}
async function setup(overrides: Partial<typeof Api> = {}) {
  const state = { app: { screen: 'start', summary: null } };
  const bridge = { savedSummaries: async () => [], openFolder: async () => ({ ok: true, message: null }), ...overrides };
  const globals = globalThis as typeof globalThis & { __startupTest?: unknown };
  globals.__startupTest = { bridge, state };
  const source = await readFile(new URL('../src/lib/startup.svelte.ts', import.meta.url), 'utf8');
  const js = stripTypeScriptTypes(source)
    .replace("import { api } from './api.ts';", 'const api = globalThis.__startupTest.bridge;')
    .replace("import { app } from './state.svelte.ts';", 'const app = globalThis.__startupTest.state.app;');
  const compiled = compileModule(js, { filename: 'startup.svelte.js', generate: 'client' }).js.code
    .replace(/from (['"])([^'"]+)\1/g, (_, quote: string, specifier: string) =>
      'from ' + quote + (specifier.startsWith('.') ? new URL('../src/lib/' + specifier, import.meta.url).href : import.meta.resolve(specifier)) + quote);
  try {
    const mod = await import('data:text/javascript;base64,' + Buffer.from(compiled + '\n// ' + instance++).toString('base64')) as typeof import('../src/lib/startup.svelte.ts');
    return { data: new mod.StartupData(), mod, ...state };
  } finally { delete globals.__startupTest; }
}

const DAY = 24 * 3600 * 1000;

test('冷启动只读保存的扫描记录，按时间新到旧；没有当前扫描结果也有应用可看', async () => {
  const now = Date.now();
  const s = await setup({ savedSummaries: async () => [
    record('C:\\', now - 2 * DAY, { apps: [app('Steam', { isNew: true, firstSeenMs: now - 2 * DAY })] }),
    record('D:\\', now - DAY, { apps: [app('Game', { kind: 'game', path: 'D:\\Game', isNew: true, firstSeenMs: now - DAY })] }),
  ] });
  await s.data.refresh();
  assert.deepEqual(s.data.records.map(r => r.rootPath), ['D:\\', 'C:\\']);
  assert.deepEqual(s.data.applications.map(a => [a.name, a.scope]), [['Game', 'D:\\'], ['Steam', 'C:\\']]);
  assert.equal(s.data.recordsLoading, false);
});

test('只看最近一周：一周前的扫描记录不进最近列表；新应用只算一周内对照出来的，第一次扫描就有的不算新装', async () => {
  const now = Date.now();
  const s = await setup({ savedSummaries: async () => [
    record('C:\\', now - DAY, { appsComparedAtMs: now - 3 * DAY, apps: [
      app('Fresh', { isNew: true, firstSeenMs: now - DAY }),
      app('Seen', { firstSeenMs: now - DAY }),
      app('Stale', { isNew: true, firstSeenMs: now - 9 * DAY }),
      // 三天前那次对照出来的，之后又扫了一次：后台把「新应用」的结论沿用到了这条最新记录上
      app('Carried', { isNew: true, firstSeenMs: now - 3 * DAY, absentAtMs: now - 4 * DAY }),
    ], removedApps: [app('Gone')] }),
    record('D:\\', now - 8 * DAY, { removedApps: [app('LongGone', { path: 'D:\\LongGone' })] }),
  ] });
  await s.data.refresh();
  assert.deepEqual(s.data.recent.map(r => r.rootPath), ['C:\\'], '8 天前的 D 盘记录不在最近一周里');
  assert.deepEqual(s.data.records.length, 2, '记录本身还在，只是不列出来');
  assert.deepEqual(s.data.applications.map(a => a.name), ['Fresh', 'Carried'], '第一次就有的 Seen、一周前新出现的 Stale 都不算；重扫过的 Carried 照样在');
  assert.deepEqual(s.data.removed.map(a => a.name), ['Gone'], '一周前那次对照里消失的不提');
});

test('刷新和卸载使旧响应失效；加载时清除旧记录', async () => {
  const pending = deferred<SavedSummary[]>();
  let calls = 0;
  const s = await setup({ savedSummaries: () => ++calls === 1 ? pending.promise : Promise.resolve([record('new', 30)]) });
  const first = s.data.refresh();
  await s.data.refresh();
  pending.resolve([record('old', 10)]);
  await first;
  assert.equal(s.data.records[0].rootPath, 'new');
  const late = deferred<SavedSummary[]>();
  const t = await setup({ savedSummaries: () => late.promise });
  const request = t.data.refresh();
  t.data.dispose();
  late.resolve([record('late', 40)]);
  await request;
  assert.deepEqual(t.data.records, []);
});

test('摘要读取失败有错误、不留旧记录', async () => {
  const s = await setup({ savedSummaries: async () => { throw new Error('读取失败'); } });
  await s.data.refresh();
  assert.equal(s.data.recordsError, '读取失败');
  assert.deepEqual(s.data.records, []);
  assert.equal(s.data.recordsLoading, false);
});

test('无效的持久化完成时间不阻塞启动或污染有效记录', async () => {
  const s = await setup({ savedSummaries: async () => [record('valid', 1000), record('bad', Number.MAX_VALUE)] });
  await s.data.refresh();
  assert.deepEqual(s.data.records.map(r => r.rootPath), ['valid']);
  assert.equal(s.data.recordsSkipped, 1);
});

test('合并应用：新出现的在前，再按第一次出现新到旧；同一处在几条记录里只留最近那条；带对照时刻', async () => {
  const { mod } = await setup();
  const rows = mod.mergeApps([
    record('C:\\Program Files', 50, { apps: [app('Steam', { firstSeenMs: 40 })] }),
    record('C:\\', 30, { appsComparedAtMs: 20, apps: [app('steam', { firstSeenMs: 5 }), app('Fresh', { firstSeenMs: 30, isNew: true }), app('Old', { firstSeenMs: 2, alloc: 9 }), app('Older', { firstSeenMs: 2, alloc: 1 })] }),
  ]);
  assert.deepEqual(rows.map(r => [r.name, r.scope]), [['Fresh', 'C:\\'], ['Steam', 'C:\\Program Files'], ['Old', 'C:\\'], ['Older', 'C:\\']],
    '同一位置的 Steam 留最近那条记录的；同时刻按占用大的在前');
  assert.equal(rows[0].comparedAtMs, 20);
  assert.equal(rows[1].comparedAtMs, null);
  const twoPlaces = mod.mergeApps([record('D:\\', 9, { apps: [app('Steam', { path: 'D:\\Steam' })] }), record('C:\\', 8, { apps: [app('Steam')] })]);
  assert.equal(twoPlaces.length, 2, '同名但在不同位置（两块盘上各一个）分开列');
});

test('不见了的应用：别的记录里还在的不算', async () => {
  const { mod } = await setup();
  const gone = mod.removedApps([
    record('C:\\', 30, { removedApps: [app('Moved'), app('Gone')] }),
    record('D:\\', 20, { apps: [app('moved', { path: 'D:\\Moved' })] }),
  ]);
  assert.deepEqual(gone.map(a => [a.name, a.scope]), [['Gone', 'C:\\']]);
});

test('打开位置：后台说位置不在了就照实显示；新一轮刷新清掉旧错误，卸载后迟到的应答不回填', async () => {
  const pending = deferred<Ok>();
  let calls = 0;
  const s = await setup({ openFolder: () => ++calls === 1 ? Promise.resolve({ ok: false, message: '这个位置已经不在了' }) : pending.promise });
  await s.data.open('C:\\Apps\\Gone');
  assert.equal(s.data.openError, '这个位置已经不在了');
  await s.data.refresh();
  assert.equal(s.data.openError, null);
  const request = s.data.open('C:\\Apps\\Late');
  s.data.dispose();
  pending.resolve({ ok: false, message: '迟到' });
  await request;
  assert.equal(s.data.openError, null);
});
