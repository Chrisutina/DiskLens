import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { createContext, runInContext } from 'node:vm';
import { keyboardTiles, moveTile } from '../src/lib/treemap/keyboard.ts';
import { layoutTreemap } from '../src/lib/treemap/layout.ts';
import { Flag, type ViewNode } from '../src/lib/types.ts';

test('键盘入口只访问实际画出的条目，不把聚合块当文件', () => {
  const nodes = [
    { id: 0, p: -1, v: 100, f: Flag.Directory },
    { id: 1, p: 0, v: 60, f: Flag.Directory },
    { id: 2, p: 1, v: 60, f: 0 },
    { id: -1, p: 0, v: 40, f: Flag.Aggregate },
    { id: 3, p: 0, v: 0, f: 0 },
  ] as ViewNode[];
  const layout = layoutTreemap(nodes, 900, 600);
  const tiles = keyboardTiles(nodes, layout);
  assert.ok(tiles.length > 0);
  assert.ok(tiles.every((k) => layout.tileOf[layout.node[k]] === k));
  assert.ok(tiles.every((k) => nodes[layout.node[k]].id > 0));
  assert.ok(!tiles.some((k) => nodes[layout.node[k]].id === 3));
});

test('方向键、首尾与空布局均有确定结果；边界不越界', () => {
  const tiles = [2, 4, 7];
  for (const key of ['ArrowDown', 'ArrowRight']) {
    assert.equal(moveTile(tiles, -1, key), 2);
    assert.equal(moveTile(tiles, 2, key), 4);
    assert.equal(moveTile(tiles, 7, key), 7);
  }
  for (const key of ['ArrowUp', 'ArrowLeft']) {
    assert.equal(moveTile(tiles, -1, key), 7);
    assert.equal(moveTile(tiles, 7, key), 4);
    assert.equal(moveTile(tiles, 2, key), 2);
  }
  assert.equal(moveTile(tiles, 4, 'Home'), 2);
  assert.equal(moveTile(tiles, 4, 'End'), 7);
  assert.equal(moveTile([], -1, 'ArrowDown'), -1);
  assert.equal(moveTile([], -1, 'ArrowUp'), -1);
  assert.equal(moveTile(tiles, 4, 'Enter'), null);
});

test('真实斜纹更新：200ms 补间；减少动效立即到最终状态，帧循环不再续帧', async () => {
  const source = await readFile(new URL('../src/components/Treemap.svelte', import.meta.url), 'utf8');
  const functionSource = (name: string) => {
    const match = source.match(new RegExp(`  function ${name}\\([^]*?\\r?\\n  }`));
    assert.ok(match, `缺少实际组件函数 ${name}`);
    return stripTypeScriptTypes(match[0]);
  };
  const duration = Number(source.match(/const HATCH_MS = (\d+);/)?.[1]);
  assert.equal(duration, 200);
  let scheduled = 0;
  const nodes: ViewNode[] = [];
  const ctx = createContext({
    HATCH_MS: duration, reducedMotion: false, hatchNodes: null,
    hatchA: new Float32Array(0), hatchFrom: new Float32Array(0), hatchTo: new Uint8Array(0), hatchT0: 0, hatchBusy: false,
    painter: {}, sceneCtx: {}, overCtx: {}, size: { w: 1, h: 1 }, window: { devicePixelRatio: 1 },
    sceneEl: { width: 1, height: 1 }, overEl: { width: 1, height: 1 },
    sceneDirty: false, overlayDirty: false, raf: 1,
    renderScene: () => {}, renderOverlay: () => {}, syncHover: () => {}, schedule: () => scheduled++,
  });
  const step = runInContext(`${functionSource('stepHatch')}; stepHatch`, ctx) as (nodes: ViewNode[], selected: Uint8Array, now: number) => void;
  const frame = runInContext(`${functionSource('frame')}; frame`, ctx) as (now: number) => void;
  const alpha = () => Array.from(ctx.hatchA as Float32Array);
  step(nodes, new Uint8Array([0, 1]), 100);
  step(nodes, new Uint8Array([1, 0]), 200);
  step(nodes, new Uint8Array([1, 0]), 300);
  assert.deepEqual(alpha(), [0.5, 0.5]);
  frame(300);
  assert.equal(scheduled, 1, '普通补间中仍请求下一帧');
  step(nodes, new Uint8Array([1, 0]), 400);
  assert.deepEqual(alpha(), [1, 0]);
  assert.equal(ctx.hatchBusy, false);

  step(nodes, new Uint8Array([0, 1]), 500);
  assert.equal(ctx.hatchBusy, true);
  ctx.reducedMotion = true;
  step(nodes, new Uint8Array([0, 1]), 550);
  assert.deepEqual(alpha(), [0, 1], '运行中的动画也立刻落定');
  step(nodes, new Uint8Array([1, 0]), 551);
  assert.deepEqual(alpha(), [1, 0], '后续勾选变化同样没有中间态');
  assert.equal(ctx.hatchBusy, false);
  assert.equal(ctx.hatchT0, 0);
  scheduled = 0;
  frame(551);
  assert.equal(scheduled, 0, '减少动效下不续帧');
});
