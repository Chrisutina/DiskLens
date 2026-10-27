import assert from 'node:assert/strict';
import { mock, test } from 'node:test';
import { MAX_ISSUES, createMockBackend } from '../src/lib/mock.ts';
import { PURPOSE_COUNT } from '../src/lib/purposes.ts';
import { GAP_KINDS, SCAN_ISSUE_KINDS } from '../src/lib/types.ts';
import type * as T from '../src/lib/types.ts';
import { enumOf, keys, readCore as read, recordKeys } from './contracts.ts';

// 扫描报告（dise §4.2 / §4.3）的前后端契约：字段名、枚举、清单上限直接读 C# 源文件比对，
// 模拟后端的计数、清单、取消流程和 ScanService 同口径——只改一边的话这里先翻脸。

type Call = <R>(method: string, params?: unknown) => Promise<R>;

/** 模拟扫描 C:\（常规扫描、非管理员），把事件也记下来 */
async function scannedC(): Promise<{ call: Call; events: [string, unknown][] }> {
  mock.timers.enable({ apis: ['setInterval'] });
  try {
    const events: [string, unknown][] = [];
    const backend = createMockBackend((event, data) => events.push([event, data]));
    await backend('scan.start', { path: 'C:\\', mode: 'walk' });
    mock.timers.tick(10_000);
    assert.deepEqual(events.slice(-2).map(([e]) => e), ['scan.done', 'tree.changed'], '模拟扫描结束，随后开始跟踪变化（同后端的顺序）');
    return { call: <R>(method: string, params: unknown = {}) => backend(method, params) as Promise<R>, events };
  } finally {
    mock.timers.reset();
  }
}

test('契约：异常种类、未解释容量的种类、清单上限和 C# 一致', () => {
  assert.deepEqual(enumOf('ScanIssueKind'), [...SCAN_ISSUE_KINDS]);
  assert.deepEqual(enumOf('GapKind'), [...GAP_KINDS]);
  const max = /public const int MaxIssues = ([\d_]+);/.exec(read('Scan/ScanCounters.cs'))?.[1];
  assert.equal(Number(max?.replaceAll('_', '')), MAX_ISSUES, '模拟后端的清单上限要和 ScanCounters.MaxIssues 一样');
});

test('契约：模拟后端的汇总、进度、异常清单字段和 C# 的 DTO 一一对应', async () => {
  const { call, events } = await scannedC();
  const summary = await call<T.ScanSummary>('scan.summary');
  assert.deepEqual(keys(summary), recordKeys('ScanSummaryDto'), 'ScanSummaryDto');
  const progress = events.find(([e]) => e === 'scan.progress')![1] as T.ScanProgress;
  assert.deepEqual(keys(progress), recordKeys('ScanProgressDto'), 'ScanProgressDto');
  const page = await call<T.ScanIssues>('scan.issues', { kind: 'denied' });
  assert.deepEqual(keys(page), recordKeys('ScanIssuesDto'), 'ScanIssuesDto');
  assert.deepEqual(keys(page.items[0]), recordKeys('ScanIssueDto'), 'ScanIssueDto');
  assert.deepEqual(keys(summary.gapParts[0]), recordKeys('GapPartDto'), 'GapPartDto');
});

test('模拟后端 scan.savedSummaries：字段和 C# 的 SavedSummaryDto 一致；只有扫完的才存，同一范围（不分大小写）只留最近一次，新的在前', async () => {
  mock.timers.enable({ apis: ['setInterval'] });
  try {
    const backend = createMockBackend(() => {});
    const call = <R>(method: string, params: unknown = {}) => backend(method, params) as Promise<R>;
    const before = await call<T.SavedSummary[]>('scan.savedSummaries');
    assert.ok(before.length >= 1, '一开始就有假的「上次扫描结果」，pnpm dev 里看得到');
    for (const s of before) {
      assert.deepEqual(keys(s), recordKeys('SavedSummaryDto'), 'SavedSummaryDto：不带扫描号、节点 ID、树版本');
      assert.equal(s.purposeBytes.length, PURPOSE_COUNT);
      assert.equal(s.purposeBytes.reduce((a, b) => a + b, 0), s.scannedAlloc + s.unaccountedBytes, `${s.rootPath}：用途字节之和 = 已统计 + 未解释`);
      assert.equal(s.gapParts.reduce((a, p) => a + p.bytes, 0), s.unaccountedBytes, `${s.rootPath}：拆分之和等于未解释容量`);
    }
    assert.deepEqual(keys(before.find((s) => s.gapParts.length)!.gapParts[0]), recordKeys('GapPartDto'), 'GapPartDto');

    await backend('scan.start', { path: 'C:\\', mode: 'walk' });
    await backend('scan.cancel', {});
    mock.timers.tick(10_000);
    assert.deepEqual(await call('scan.savedSummaries'), before, '取消的扫描不改已保存的摘要');

    await backend('scan.start', { path: 'd:\\', mode: 'walk' });
    mock.timers.tick(10_000);
    const summary = (await call<T.ScanSummary | null>('scan.summary'))!;
    const after = await call<T.SavedSummary[]>('scan.savedSummaries');
    assert.equal(after.length, before.length, 'd:\\ 和 D:\\ 是同一个范围，只留一条');
    assert.ok(after[0].rootPath === summary.rootPath && after[0].completedAtMs === summary.completedAtMs, '换成刚扫完的这一次，带完成时刻');
    assert.ok(after.every((s, i) => i === 0 || after[i - 1].completedAtMs >= s.completedAtMs), '新的在前');

    // 缓存对照（同 SummaryStore.Compare）：和 D 盘上一次的记录比
    const d = after[0], old = before.find((s) => s.rootPath === 'D:\\')!;
    assert.deepEqual(keys(d.topEntries![0]), recordKeys('SavedEntryDto'), 'SavedEntryDto');
    assert.deepEqual(keys(d.apps![0]), recordKeys('SavedAppDto'), 'SavedAppDto');
    assert.ok(d.previousAtMs === old.completedAtMs && d.previousAlloc === old.scannedAlloc && d.appsComparedAtMs === old.completedAtMs, '带上一次的时刻和总占用，应用对照过');
    assert.equal(d.topEntries!.find((e) => e.name === 'Media')!.previousAlloc, old.topEntries!.find((e) => e.name === 'Media')!.alloc, '大项按名字对上旧占用');
    const kept = d.apps!.find((a) => a.name === 'Starfield')!;
    assert.ok(!kept.isNew && kept.firstSeenMs === old.apps!.find((a) => a.name === 'Starfield')!.firstSeenMs, '上次就有的游戏沿用第一次出现的时刻，不算新');
    assert.ok(d.removedApps!.every((a) => !d.apps!.some((x) => x.name === a.name)), '消失的应用这次确实没有');
  } finally {
    mock.timers.reset();
  }
});

test('模拟后端扫描报告：四类计数等于清单总数，清单有路径和原因、按路径排好，未解释容量拆成 $MFT 和原因未确认', async () => {
  const { call } = await scannedC();
  const s = await call<T.ScanSummary>('scan.summary');
  const want: Record<T.ScanIssueKind, number> = { denied: s.inaccessibleDirs, link: s.linksSkipped, mountPoint: s.mountPointsSkipped, error: s.errors };
  assert.deepEqual(want, { denied: 2, link: 3, mountPoint: 1, error: 2 }, '模拟数据里有少量无权限目录、链接、挂载点和读取失败');
  for (const kind of SCAN_ISSUE_KINDS) {
    const page = await call<T.ScanIssues>('scan.issues', { kind, scanId: s.scanId });
    assert.equal(page.total, want[kind], `${kind}：清单总数等于汇总里的计数`);
    assert.equal(page.recorded, want[kind], `${kind}：没超上限，全都记下了`);
    assert.equal(page.items.length, want[kind]);
    assert.equal(page.truncated, false, `${kind}：一页放得下，不截断`);
    assert.equal(page.scanId, s.scanId);
    for (const x of page.items) assert.ok(x.path.startsWith('C:\\') && x.reason.length > 0, `${kind}：${x.path} 有路径和原因`);
    const paths = page.items.map((x) => x.path.toUpperCase());
    assert.deepEqual(paths, [...paths].sort(), `${kind}：按路径排好`);
  }
  const mount = await call<T.ScanIssues>('scan.issues', { kind: 'mountPoint' });
  assert.ok(mount.items[0].reason.startsWith('卷挂载点'), '挂载点和链接分开');

  assert.deepEqual(s.gapParts.map((p) => p.kind), ['ntfsMetadata', 'unconfirmed'], '常规扫描：先扣 $MFT，余数原因未确认');
  assert.equal(s.gapParts.reduce((a, p) => a + p.bytes, 0), s.unaccountedBytes, '各份之和等于未解释容量');
  assert.ok(s.gapParts[1].basis.startsWith('原因未确认'), '余数写明原因未确认，不归因于权限');
});

test('模拟后端 scan.issues：翻页的截断标志说实话，换过扫描的编号和未知种类报错', async () => {
  const { call } = await scannedC();
  const first = await call<T.ScanIssues>('scan.issues', { kind: 'link', limit: 1 });
  assert.equal(first.items.length, 1);
  assert.equal(first.truncated, true, '只给了 3 条里的 1 条：截断');
  const last = await call<T.ScanIssues>('scan.issues', { kind: 'link', offset: 2, limit: 1 });
  assert.equal(last.truncated, false, '最后一页不截断');
  const beyond = await call<T.ScanIssues>('scan.issues', { kind: 'link', offset: 99 });
  assert.deepEqual([beyond.items.length, beyond.total, beyond.truncated], [0, 3, false], '翻过末尾：空页，总数照旧');
  const clamped = await call<T.ScanIssues>('scan.issues', { kind: 'link', limit: 0 });
  assert.equal(clamped.items.length, 1, 'limit 夹到至少 1');
  await assert.rejects(call('scan.issues', { kind: 'link', scanId: first.scanId + 100 }), /失效/, '别的扫描的编号：报错，不拿当前清单充数');
  await assert.rejects(call('scan.issues', { kind: 'bogus' }), /异常种类无效/);
});

test('模拟后端取消：接受时马上报「取消中」，下一拍才发终态，之后不再有进度', async () => {
  mock.timers.enable({ apis: ['setInterval'] });
  try {
    const events: [string, unknown][] = [];
    const backend = createMockBackend((event, data) => events.push([event, data]));
    const idle = (await backend('scan.cancel', {})) as T.Ok;
    assert.ok(idle.ok && idle.message, '没有扫描在跑：不算失败，只说一句');
    await backend('scan.start', { path: 'C:\\', mode: 'walk' });
    assert.equal(events[0]?.[0], 'scan.progress', '开始就有一条进度');
    mock.timers.tick(1000);
    const before = events.length;
    assert.ok(((await backend('scan.cancel', {})) as T.Ok).ok);
    const accepted = events.slice(before);
    assert.equal(accepted.length, 1, '接受取消时只多一条事件');
    const [event, data] = accepted[0];
    const p = data as T.ScanProgress;
    assert.ok(event === 'scan.progress' && p.cancelling && p.phase === 'walk', '那条是「取消中」的进度，阶段停在被打断的那一步');
    mock.timers.tick(100);
    assert.deepEqual(events.at(-1), ['scan.error', { scanId: p.scanId, message: '扫描已取消', cancelled: true }], '下一拍才发终态');
    const ended = events.length;
    mock.timers.tick(2000);
    assert.equal(events.length, ended, '终态之后不再有进度');
    assert.ok(events.slice(0, before).every(([, d]) => !(d as T.ScanProgress).cancelling), '取消之前的进度不带取消中');
  } finally {
    mock.timers.reset();
  }
});
