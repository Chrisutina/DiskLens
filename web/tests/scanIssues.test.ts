import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';
import { compileModule } from 'svelte/compiler';
import type { ScanIssues, ScanIssueKind } from '../src/lib/types.ts';

type ReadIssues = (kind: ScanIssueKind, offset?: number, limit?: number, scanId?: number | null) => Promise<ScanIssues>;
let instance = 0;
const response = (kind: ScanIssueKind = 'denied', offset = 0, total = 1, recorded = total): ScanIssues => ({
  scanId: 7, kind, offset, total, recorded,
  items: Array.from({ length: Math.min(20, Math.max(0, recorded - offset)) }, (_, i) => ({ path: 'D:\\fixture\\' + (offset + i), reason: '拒绝访问' })),
  truncated: offset + Math.min(20, Math.max(0, recorded - offset)) < total,
});
function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (reason: Error) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
async function setup(read: ReadIssues = async (kind, offset) => response(kind, offset)) {
  const app = { screen: 'main', summary: { scanId: 7 } };
  const bridge = { issues: read };
  const globals = globalThis as typeof globalThis & { __scanIssuesTest?: unknown };
  globals.__scanIssuesTest = { app, bridge };
  const source = await readFile(new URL('../src/lib/scanIssues.svelte.ts', import.meta.url), 'utf8');
  const js = stripTypeScriptTypes(source)
    .replace("import { api } from './api.ts';", 'const api = globalThis.__scanIssuesTest.bridge;')
    .replace("import { app } from './state.svelte.ts';", 'const app = globalThis.__scanIssuesTest.app;');
  const compiled = compileModule(js, { filename: 'scanIssues.svelte.js', generate: 'client' }).js.code
    .replace(/from (['"])([^'"]+)\1/g, (_, quote: string, specifier: string) =>
      'from ' + quote + (specifier.startsWith('.') ? new URL('../src/lib/' + specifier, import.meta.url).href : import.meta.resolve(specifier)) + quote);
  try {
    const mod = await import('data:text/javascript;base64,' + Buffer.from(compiled + '\n// ' + instance++).toString('base64')) as typeof import('../src/lib/scanIssues.svelte.ts');
    return { app, data: new mod.ScanIssueList() };
  } finally { delete globals.__scanIssuesTest; }
}

test('异常清单按需请求并绑定扫描号，翻页保留 total/recorded/truncated 语义', async () => {
  const calls: unknown[][] = [];
  const s = await setup(async (...args) => { calls.push(args); return response(args[0], args[1], 65, 25); });
  assert.equal(calls.length, 0);
  await s.data.show(7, 'denied');
  assert.deepEqual(calls[0], ['denied', 0, 20, 7]);
  assert.equal(s.data.page?.items.length, 20);
  assert.equal(s.data.page?.total, 65);
  assert.equal(s.data.page?.recorded, 25);
  await s.data.load(20);
  assert.deepEqual(calls[1], ['denied', 20, 20, 7]);
  assert.equal(s.data.page?.items.length, 5);
  assert.equal(s.data.page?.truncated, true, '记录上限之外仍有仅计数条目，不能称完整清单');
});

test('快速换异常种类时旧响应不能替换当前页', async () => {
  const old = deferred<ScanIssues>();
  const s = await setup((kind, offset) => kind === 'denied' ? old.promise : Promise.resolve(response(kind, offset)));
  const first = s.data.show(7, 'denied');
  await s.data.show(7, 'link');
  old.resolve(response('denied'));
  await first;
  assert.equal(s.data.page?.kind, 'link');
  assert.equal(s.data.loading, false);
});

test('翻页读取中清除旧页；失败可重试同一页，不偷偷回到第一页', async () => {
  let failures = 1;
  const s = await setup(async (kind, offset) => {
    if (offset === 20 && failures--) throw new Error('读取中断');
    return response(kind, offset, 30);
  });
  await s.data.show(7, 'error');
  const request = s.data.load(20);
  assert.equal(s.data.page, null);
  await request;
  assert.equal(s.data.error, '读取中断');
  assert.equal(s.data.offset, 20);
  await s.data.load();
  assert.equal(s.data.error, null);
  assert.deepEqual(s.data.page, response('error', 20, 30));
});

test('关闭或离开扫描页后请求成功/失败都不能回填', async () => {
  for (const action of ['close', 'leave', 'scan'] as const) {
    for (const failure of [false, true]) {
      const pending = deferred<ScanIssues>();
      const s = await setup(() => pending.promise);
      const request = s.data.show(7, 'denied');
      if (action === 'close') s.data.close();
      if (action === 'leave') s.app.screen = 'start';
      if (action === 'scan') s.app.summary.scanId = 8;
      if (failure) pending.reject(new Error('过期错误')); else pending.resolve(response());
      await request;
      assert.equal(s.data.page, null);
      assert.equal(s.data.error, null);
      assert.equal(s.data.loading, false);
    }
  }
});

test('后端返回不同扫描/类别/偏移时拒绝展示；旧扫描号不发请求', async () => {
  for (const change of [{ scanId: 8 }, { kind: 'link' as const }, { offset: 20 }]) {
    const s = await setup(async () => ({ ...response(), ...change }));
    await s.data.show(7, 'denied');
    assert.equal(s.data.page, null);
    assert.match(s.data.error!, /不一致/);
  }
  let calls = 0;
  const s = await setup(async () => { calls++; return response(); });
  await s.data.show(6, 'denied');
  assert.equal(calls, 0);
  assert.match(s.data.error!, /变化/);
});

test('空清单与只有计数没有位置的清单保持区别', async () => {
  const empty = await setup(async () => response('mountPoint', 0, 0));
  await empty.data.show(7, 'mountPoint');
  assert.equal(empty.data.page?.total, 0);
  assert.equal(empty.data.page?.truncated, false);
  const capped = await setup(async () => response('error', 0, 3, 0));
  await capped.data.show(7, 'error');
  assert.equal(capped.data.page?.total, 3);
  assert.equal(capped.data.page?.recorded, 0);
  assert.equal(capped.data.page?.items.length, 0);
  assert.equal(capped.data.page?.truncated, true);
});
