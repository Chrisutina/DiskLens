import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';
import { compileModule } from 'svelte/compiler';
import { clipText, layoutTree, relatedTimes, TREE, type Box } from '../src/lib/relatedModel.ts';
import type { NodeDetails, Related, RelatedItem } from '../src/lib/types.ts';

const item = (id: number, name = `模块-${id}`): RelatedItem =>
  ({ id, name, path: `C:/App/${name}`, isDir: id % 3 !== 0, size: id, alloc: id, role: 1, relation: id === 1 ? 'root' : 'roleModule', evidence: 'inferred' });

test('主干只放有时间依据的记录：读不到的不补，按先后排，扫描时间总在', () => {
  const t = relatedTimes({ createdMs: 2000, modifiedMs: 1000 } as NodeDetails, 3000);
  assert.deepEqual(t.map((e) => e.kind), ['modified', 'created', 'scanned'], '创建晚于修改（复制保留了修改时间）也照实排，不强行补成顺序');
  assert.deepEqual(relatedTimes({ createdMs: null, modifiedMs: 0 } as NodeDetails, 3000).map((e) => e.kind), ['scanned']);
});

test('生长树布局：分支上下交替、位置只由下标决定，展开更多不挪动已有分支，长名字和多分支不重叠', () => {
  const items = Array.from({ length: 40 }, (_, i) => item(i + 1));
  const times = relatedTimes({ createdMs: 1, modifiedMs: 2 } as NodeDetails, 3);
  const six = layoutTree(times, true, items, 6), all = layoutTree(times, true, items, 40);
  assert.equal(six.branches.length, 6);
  assert.deepEqual(all.branches.slice(0, 6).map((b) => b.box), six.branches.map((b) => b.box), '展开后前 6 条原地不动');
  assert.deepEqual(all.branches.slice(0, 4).map((b) => b.above), [true, false, true, false]);
  const boxes: Box[] = [all.object, all.owner!, ...all.branches.map((b) => b.box)];
  const overlap = (a: Box, b: Box) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
  for (let i = 0; i < boxes.length; i++) for (let j = i + 1; j < boxes.length; j++) assert.ok(!overlap(boxes[i], boxes[j]), `节点 ${i} 和 ${j} 不重叠`);
  assert.ok(all.events.every((e) => e.x > all.object.x + all.object.w && e.x < all.relationStart), '时间记录排在当前对象和「当前关联」段之间');
  assert.ok(all.branches.every((b) => b.anchorX > all.relationStart), '没有时间依据的关联只挂在虚线段上');
  assert.ok(all.width >= Math.max(...boxes.map((b) => b.x + b.w)) && all.height === TREE.height, '画布装得下所有节点');
  assert.equal(layoutTree(times, false, items, 0).owner, null);
});

test('SVG 文字按字宽截断：汉字算两个字宽，短的原样', () => {
  assert.equal(clipText('short', 10), 'short');
  const long = clipText('非常非常长的应用程序数据目录名字', 12);
  assert.ok(long.endsWith('…') && long.length <= 7, long);
});

// ---------------- RelatedData：编译真实的 runes 模块，只换掉桥接和状态 ----------------

const settle = () => new Promise<void>((resolve) => setImmediate(resolve));
let instance = 0;
async function setup(details: (id: number) => Promise<NodeDetails>, related: (id: number) => Promise<Related> = async (id) => ({ nodeId: id, owner: null, total: 0, items: [], note: '尚未识别关联对象' })) {
  const app = { screen: 'main', page: 'browse', panelOpen: true, rightTab: 'details', selectedId: 10, metric: 'alloc', summary: { scanId: 1 } };
  const revealed: number[] = [];
  const globals = globalThis as typeof globalThis & { __relatedTestApi?: unknown; __relatedTestState?: unknown };
  globals.__relatedTestApi = { details, related };
  globals.__relatedTestState = { app, reveal: async (id: number) => { revealed.push(id); app.selectedId = id; } };
  const source = await readFile(new URL('../src/lib/related.svelte.ts', import.meta.url), 'utf8');
  const js = stripTypeScriptTypes(source)
    .replace("import { api } from './api.ts';", 'const api = globalThis.__relatedTestApi;')
    .replace("import { app, reveal } from './state.svelte.ts';", 'const { app, reveal } = globalThis.__relatedTestState;');
  const compiled = compileModule(js, { filename: 'related.svelte.js', generate: 'client' }).js.code
    .replace(/from (['"])([^'"]+)\1/g, (_, q: string, path: string) => `from ${q}${path.startsWith('.') ? new URL(`../src/lib/${path}`, import.meta.url).href : import.meta.resolve(path)}${q}`);
  const mod = await import(`data:text/javascript;base64,${Buffer.from(compiled + `\n// ${instance++}`).toString('base64')}`) as typeof import('../src/lib/related.svelte.ts');
  delete globals.__relatedTestApi;
  delete globals.__relatedTestState;
  return { ...mod, app, revealed };
}
const node = (id: number, path = `C:/App/n${id}`) => ({ id, path, name: `n${id}`, treeVersion: 1 }) as NodeDetails;

test('关联：选中项换了，迟到的响应丢掉；路径对不上就报对象已变化', async () => {
  let release!: () => void;
  const gate = new Promise<void>((r) => { release = r; });
  const s = await setup(async (id) => { await gate; return node(id); });
  const data = new s.RelatedData();
  data.reset(10, 'C:/App/n10', 1);
  const pending = data.load();
  s.app.selectedId = 20;
  release();
  await pending;
  assert.equal(data.node, null);
  assert.equal(data.error, null, '不属于当前选中项的结果不显示，也不报错');

  const t = await setup(async (id) => node(id, 'C:/Other/path'));
  const moved = new t.RelatedData();
  moved.reset(10, 'C:/App/n10', 1);
  await moved.load();
  assert.match(moved.error!, /对象已变化或已删除/);
});

test('关联：探索记历史、返回上一个、回到起点；探索失败停在原处', async () => {
  const s = await setup(async (id) => { if (id === 99) throw new Error('读不到'); return node(id); });
  const data = new s.RelatedData();
  data.reset(10, 'C:/App/n10', 1);
  await data.load();
  await data.load(11, 'C:/App/n11', true);
  await data.load(12, 'C:/App/n12', true);
  assert.deepEqual(data.history.map((p) => p.id), [10, 11]);
  await data.back();
  assert.equal(data.node?.id, 11);
  assert.deepEqual(data.history.map((p) => p.id), [10]);
  await data.load(99, 'C:/App/n99', true);
  assert.match(data.error!, /读不到/);
  assert.equal(data.node?.id, 11, '失败时保留原来的画面');
  await data.load();
  assert.equal(data.node?.id, 11, '重试读的还是原来的对象');
  await data.home();
  assert.equal(data.node?.id, 10);
  assert.deepEqual(data.history, []);
});

test('关联：定位记下返回点，走「返回」回来后清掉；换扫描清掉旧返回点', async () => {
  const s = await setup(async (id) => node(id));
  const data = new s.RelatedData();
  data.reset(10, 'C:/App/n10', 1);
  assert.equal(await data.locate(11, 'C:/App/n11'), true);
  assert.deepEqual(s.revealed, [11]);
  assert.equal(s.relatedReturn.point?.id, 10);
  const next = new s.RelatedData();
  next.reset(11, 'C:/App/n11', 1);
  assert.equal(s.relatedReturn.point?.id, 10, '同一次扫描里返回点还在');
  assert.equal(await next.locate(10, 'C:/App/n10', true), true);
  assert.equal(s.relatedReturn.point, null);
  await data.locate(11, 'C:/App/n11');
  new s.RelatedData().reset(5, 'C:/x', 2);
  assert.equal(s.relatedReturn.point, null, '换了扫描，旧 ID 不再用来返回');
  await settle();
});
