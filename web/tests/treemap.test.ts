import { test } from 'node:test';
import assert from 'node:assert/strict';
import { squarify, layoutTreemap, hitTest, emptyLayout, tileTitle, type Layout, type LayoutNode, type Rect } from '../src/lib/treemap/layout.ts';
import { inheritMarks, ancestorSeed, GROUP_NONE, GROUP_SAFE, GROUP_REVIEW } from '../src/lib/treemap/marks.ts';
import { createMeter } from '../src/lib/treemap/text.ts';
import { planZoom } from '../src/lib/treemap/zoom.ts';
import { Flag } from '../src/lib/types.ts';

// ---------- 工具 ----------

function rng(seed: number): () => number {
  let s = seed >>> 0;
  return () => {
    s = (Math.imul(s, 1664525) + 1013904223) >>> 0;
    return s / 4294967296;
  };
}

const DIR = Flag.Directory;
const node = (p: number, v: number, f = 0): LayoutNode => ({ p, v, f });

/** 随机树：目录值 = 子值之和，同级按值降序，先序展开——和后端 tree.view 的契约一致 */
function synth(total: number, seed = 1, tail = 6): LayoutNode[] {
  interface T { v: number; kids: T[]; dir: boolean }
  const rand = rng(seed);
  const root: T = { v: 0, kids: [], dir: true };
  const dirs = [root];
  for (let made = 1; made < total; made++) {
    const parent = dirs[Math.floor(rand() ** 2 * dirs.length)];
    const dir = rand() < 0.22;
    const t: T = { v: dir ? 0 : 1 + Math.floor(rand() ** tail * 4e9), kids: [], dir };
    parent.kids.push(t);
    if (dir) dirs.push(t);
  }
  const sum = (t: T): number => {
    for (const c of t.kids) t.v += sum(c);
    return t.v;
  };
  sum(root);
  const out: LayoutNode[] = [];
  const emit = (t: T, p: number): void => {
    const i = out.length;
    out.push(node(p, t.v, t.dir ? DIR : 0));
    t.kids.sort((a, b) => b.v - a.v);
    for (const c of t.kids) emit(c, i);
  };
  emit(root, -1);
  return out;
}

const NO_SPACE = { margin: 0, gapTop: 0, gapDeep: 0, header: 0, inset: 0, minDirW: 0, minDirH: 0, minTile: 0 };

const rectOf = (L: Layout, k: number): Rect => ({ x: L.x[k], y: L.y[k], w: L.w[k], h: L.h[k] });
const overlapArea = (a: Rect, b: Rect): number =>
  Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x)) * Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));
/** 两矩形在某个轴上的最小间距（负数表示重叠） */
const separation = (a: Rect, b: Rect): number => Math.max(a.x - (b.x + b.w), b.x - (a.x + a.w), a.y - (b.y + b.h), b.y - (a.y + a.h));
const parentTile = (L: Layout, k: number): number => {
  for (let j = k - 1; j >= 0; j--) if (L.depth[j] < L.depth[k] && L.end[j] > k) return j;
  return -1;
};

// ---------- squarify ----------

test('squarify：面积正比于值（误差 < 1%）、不重叠、不越界', () => {
  const rand = rng(7);
  for (const [w, h] of [[960, 640], [180, 900], [37, 41], [1920, 12], [5, 5]]) {
    const values = Array.from({ length: 60 }, () => 1 + Math.floor(rand() ** 5 * 1e6)).sort((a, b) => b - a);
    const total = values.reduce((s, v) => s + v, 0);
    const rects = squarify(values, 7, 9, w, h);
    let area = 0;
    rects.forEach((r, i) => {
      const want = (values[i] / total) * w * h;
      assert.ok(Math.abs(r.w * r.h - want) <= want * 0.01 + 1e-9, `第 ${i} 项面积 ${r.w * r.h} 应接近 ${want}`);
      assert.ok(r.x >= 7 - 1e-9 && r.y >= 9 - 1e-9 && r.x + r.w <= 7 + w + 1e-9 && r.y + r.h <= 9 + h + 1e-9, '越界');
      area += r.w * r.h;
    });
    assert.ok(Math.abs(area - w * h) < 1e-6 * w * h, '没有铺满');
    for (let a = 0; a < rects.length; a++) {
      for (let b = a + 1; b < rects.length; b++) assert.ok(overlapArea(rects[a], rects[b]) < 1e-6, `${a} 和 ${b} 重叠`);
    }
  }
});

test('squarify：零值、负数、NaN 得到零面积，极小值不影响大块，退化区域不崩', () => {
  const r = squarify([5, 0, -1, NaN, 5], 0, 0, 100, 100);
  assert.equal(r[1].w * r[1].h, 0);
  assert.equal(r[2].w * r[2].h, 0);
  assert.equal(r[3].w * r[3].h, 0);
  assert.ok(Math.abs(r[0].w * r[0].h - 5000) < 1e-6 && Math.abs(r[4].w * r[4].h - 5000) < 1e-6);

  const tiny = squarify([1e12, 1], 0, 0, 1000, 1000);
  assert.ok(Math.abs(tiny[0].w * tiny[0].h - 1e6) < 1, '大块几乎吃满');
  assert.ok(tiny[1].w * tiny[1].h > 0 && tiny[1].w * tiny[1].h < 1e-5);

  assert.deepEqual(squarify([], 0, 0, 100, 100), []);
  assert.equal(squarify([1, 2], 0, 0, 0, 100).every((q) => q.w * q.h === 0), true);
  assert.equal(squarify([1, 2], 0, 0, 100, -5).every((q) => q.w * q.h === 0), true);
});

// ---------- 整棵树 ----------

test('layoutTreemap：去掉留白、间隙、标题后，每个块的面积都正比于值', () => {
  const nodes = synth(400, 5);
  const W = 1200;
  const H = 800;
  const L = layoutTreemap(nodes, W, H, NO_SPACE);
  const total = nodes[0].v;
  let checked = 0;
  for (let i = 1; i < nodes.length; i++) {
    if (!(nodes[i].v > 0)) {
      assert.equal(L.tileOf[i], -1, '零值不该有块');
      continue;
    }
    const k = L.tileOf[i];
    assert.ok(k >= 0, `节点 ${i} 缺块`);
    const want = (nodes[i].v / total) * W * H;
    assert.ok(Math.abs(L.w[k] * L.h[k] - want) <= want * 0.01 + 1e-9, `节点 ${i} 面积偏差过大`);
    checked++;
  }
  assert.ok(checked > 300);
});

test('layoutTreemap：默认规格——外留 8、间隙 4/3、标题行 28、内边距 6、不重叠不越界', () => {
  const nodes = synth(900, 11);
  const W = 1010;
  const H = 640;
  const L = layoutTreemap(nodes, W, H);
  assert.ok(L.count > 100);
  let minX = Infinity;
  let minY = Infinity;
  let maxR = 0;
  let maxB = 0;
  for (let k = 0; k < L.count; k++) {
    const r = rectOf(L, k);
    assert.ok(r.x >= 8 - 1e-9 && r.y >= 8 - 1e-9 && r.x + r.w <= W - 8 + 1e-9 && r.y + r.h <= H - 8 + 1e-9, '块伸进了外留白');
    if (L.depth[k] === 0) {
      minX = Math.min(minX, r.x);
      minY = Math.min(minY, r.y);
      maxR = Math.max(maxR, r.x + r.w);
      maxB = Math.max(maxB, r.y + r.h);
    }
    if (L.open[k] === 1) {
      assert.ok(r.w >= 90 && r.h >= 64, '太小的目录不该展开');
    } else {
      assert.ok(L.end[k] === k + 1, '没展开的块不该有子块');
    }
    // 先序：子树是连续区间，子块都在父块的内容区里
    assert.ok(L.end[k] > k && L.end[k] <= L.count);
    const pk = parentTile(L, k);
    if (pk >= 0) {
      const p = rectOf(L, pk);
      assert.equal(L.open[pk], 1);
      assert.ok(r.x >= p.x + 6 - 1e-9, '左内边距');
      assert.ok(r.y >= p.y + 28 + 6 - 1e-9, '标题行 + 上内边距');
      assert.ok(r.x + r.w <= p.x + p.w - 6 + 1e-9, '右内边距');
      assert.ok(r.y + r.h <= p.y + p.h - 6 + 1e-9, '下内边距');
    }
  }
  assert.ok(Math.abs(minX - 8) < 1e-9 && Math.abs(minY - 8) < 1e-9, '第一层应贴着 8px 留白');
  assert.ok(Math.abs(maxR - (W - 8)) < 1e-6 && Math.abs(maxB - (H - 8)) < 1e-6, '第一层应铺满留白框');

  // 同一父块下任意两块都不重叠，且间隙 ≥ 4（第一层）/ 3（更深）
  const bySibling = new Map<number, number[]>();
  for (let k = 0; k < L.count; k++) {
    const pk = parentTile(L, k);
    bySibling.set(pk, [...(bySibling.get(pk) ?? []), k]);
  }
  for (const [pk, group] of bySibling) {
    const gap = pk < 0 ? 4 : 3;
    for (let a = 0; a < group.length; a++) {
      for (let b = a + 1; b < group.length; b++) {
        assert.ok(separation(rectOf(L, group[a]), rectOf(L, group[b])) >= gap - 1e-6, `兄弟块 ${group[a]} 和 ${group[b]} 间隙不足 ${gap}`);
      }
    }
  }
});

test('layoutTreemap：单个子节点占满父块的内容区', () => {
  const nodes = [node(-1, 100, DIR), node(0, 100, DIR), node(1, 100)];
  const L = layoutTreemap(nodes, 400, 300);
  assert.equal(L.count, 2);
  assert.deepEqual(rectOf(L, 0), { x: 8, y: 8, w: 384, h: 284 });
  assert.equal(L.open[0], 1);
  assert.deepEqual(rectOf(L, 1), { x: 8 + 6, y: 8 + 28 + 6, w: 384 - 12, h: 284 - 28 - 12 });
  assert.equal(L.depth[1], 1);
});

test('layoutTreemap：零值、负数、NaN、已删除和小到看不见的块都没有块', () => {
  const nodes = [
    node(-1, 1e9 + 1, DIR),
    node(0, 1e9),
    node(0, 1), // 面积远小于 1px
    node(0, 0),
    node(0, -5),
    node(0, NaN),
    node(0, 5e8, Flag.Deleted),
  ];
  const L = layoutTreemap(nodes, 800, 600);
  assert.ok(L.tileOf[1] >= 0);
  for (const i of [2, 3, 4, 5, 6]) assert.equal(L.tileOf[i], -1, `节点 ${i} 不该有块`);
  assert.equal(L.count, 1);
  // 根没有子节点、空数组、尺寸为 0 都得到空布局而不是报错
  assert.equal(layoutTreemap([node(-1, 10, DIR)], 800, 600).count, 0);
  assert.equal(layoutTreemap([], 800, 600).count, 0);
  assert.equal(layoutTreemap(nodes, 0, 600).count, 0);
  assert.equal(layoutTreemap(nodes, 10, 10).count, 0);
});

test('layoutTreemap：宽 < 90 或高 < 64 的目录按叶子画，不展开', () => {
  const nodes = [node(-1, 100, DIR), node(0, 50, DIR), node(1, 50), node(0, 50, DIR), node(3, 50)];
  const narrow = layoutTreemap(nodes, 100, 400); // 内容区只有 84 宽
  assert.equal(narrow.count, 2);
  assert.ok(narrow.w[0] < 90 && narrow.open[0] === 0 && narrow.open[1] === 0);
  const flat = layoutTreemap(nodes, 500, 70); // 内容区只有 54 高
  assert.equal(flat.count, 2);
  assert.ok(flat.h[0] < 64 && flat.open[0] === 0 && flat.open[1] === 0);
  const roomy = layoutTreemap(nodes, 500, 400);
  assert.equal(roomy.count, 4);
  assert.equal(roomy.open[roomy.tileOf[1]], 1);
  assert.equal(roomy.open[roomy.tileOf[2]], 0, '文件不展开');
});

test('layoutTreemap：父子顺序乱了、同级没排序也不会崩，且仍然按值排布', () => {
  const nodes = [node(-1, 60, DIR), node(0, 10), node(0, 20), node(0, 30)];
  const L = layoutTreemap(nodes, 400, 300);
  assert.equal(L.count, 3);
  assert.deepEqual([...L.node.subarray(0, 3)], [3, 2, 1], '大的在前');
  const orphan = [node(-1, 10, DIR), node(5, 3), node(0, 7)];
  assert.equal(layoutTreemap(orphan, 400, 300).count, 1, '父下标非法的节点被忽略');
});

// ---------- 命中测试 ----------

test('hitTest：最深的块优先；缝隙归邻居；标题行归目录；留白是 -1', () => {
  const nodes = [node(-1, 100, DIR), node(0, 100, DIR), node(1, 60), node(1, 40)];
  const L = layoutTreemap(nodes, 400, 300);
  const [dir, a, b] = [L.tileOf[1], L.tileOf[2], L.tileOf[3]];
  const A = rectOf(L, a);
  const B = rectOf(L, b);
  assert.equal(hitTest(L, A.x + A.w / 2, A.y + A.h / 2), a);
  assert.equal(hitTest(L, B.x + B.w / 2, B.y + B.h / 2), b);
  assert.equal(hitTest(L, L.x[dir] + 20, L.y[dir] + 10), dir, '标题行');
  assert.equal(hitTest(L, L.x[dir] + 2, L.y[dir] + 100), dir, '内边距');
  assert.equal(hitTest(L, 3, 3), -1, '外留白');
  assert.equal(hitTest(L, 399, 299), -1);
  // a、b 之间的缝：不能回落到父目录
  const sideBySide = B.x >= A.x + A.w - 1e-9;
  const gapHit = sideBySide ? hitTest(L, A.x + A.w + 1.5, A.y + A.h / 2) : hitTest(L, A.x + A.w / 2, A.y + A.h + 1.5);
  assert.ok(gapHit === a || gapHit === b, `缝隙命中了 ${gapHit}`);
  assert.equal(hitTest(emptyLayout(), 10, 10), -1);
});

test('hitTest：随机树里每次命中都落在包含指针的块上，且没有更深的块', () => {
  const nodes = synth(3000, 2);
  const L = layoutTreemap(nodes, 1000, 640);
  const rand = rng(99);
  for (let n = 0; n < 500; n++) {
    const x = rand() * 1000;
    const y = rand() * 640;
    const k = hitTest(L, x, y);
    if (k < 0) continue;
    const r = rectOf(L, k);
    assert.ok(x >= r.x - 2 && x < r.x + r.w + 2 && y >= r.y - 2 && y < r.y + r.h + 2, '命中的块不含指针');
    // 它的子块里不该有更深的命中
    for (let c = k + 1; c < L.end[k]; c++) {
      const q = rectOf(L, c);
      assert.ok(!(x >= q.x && x < q.x + q.w && y >= q.y && y < q.y + q.h), '漏了更深的块');
    }
  }
});

// ---------- 单子目录链 ----------

const named = (p: number, v: number, n: string, f = 0) => ({ p, v, f, n });
/** 某块所有子块的外包矩形 */
const kidsBox = (L: Layout, k: number): Rect => {
  let [x0, y0, x1, y1] = [Infinity, Infinity, -Infinity, -Infinity];
  for (let c = k + 1; c < L.end[k]; c++) {
    if (L.depth[c] !== L.depth[k] + 1) continue;
    x0 = Math.min(x0, L.x[c]);
    y0 = Math.min(y0, L.y[c]);
    x1 = Math.max(x1, L.x[c] + L.w[c]);
    y1 = Math.max(y1, L.y[c] + L.h[c]);
  }
  return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
};
const near = (a: Rect, b: Rect): boolean => Math.abs(a.x - b.x) + Math.abs(a.y - b.y) + Math.abs(a.w - b.w) + Math.abs(a.h - b.h) < 1e-6;

test('单子目录链：独生子目录并进同一个标题行，子块直接铺满父块的内容区', () => {
  // 根 ─ Users ─ Alex ─ Videos(60)、Music(40)
  const nodes = [named(-1, 100, '~', DIR), named(0, 100, 'Users', DIR), named(1, 100, 'Alex', DIR), named(2, 60, 'Videos'), named(2, 40, 'Music')];
  const L = layoutTreemap(nodes, 400, 300);
  assert.equal(L.count, 3, '合并块 + 两个文件');
  assert.equal(L.open[0], 1);
  assert.equal(L.node[0], 2, '合并块代表链尾');
  assert.equal(L.head[0], 1);
  assert.equal(L.tileOf[1], 0);
  assert.equal(L.tileOf[2], 0, '链上每个目录都指向合并块');
  assert.equal(tileTitle(nodes, L, 0), 'Users › Alex');
  assert.deepEqual(rectOf(L, 0), { x: 8, y: 8, w: 384, h: 284 });
  for (const i of [3, 4]) {
    assert.equal(L.depth[L.tileOf[i]], 1);
    assert.equal(parentTile(L, L.tileOf[i]), 0);
  }
  // 只扣一层标题和内边距：子块正好铺满合并块的内容区
  assert.ok(near(kidsBox(L, 0), { x: 8 + 6, y: 8 + 28 + 6, w: 384 - 12, h: 284 - 28 - 12 }), '子块没有铺满内容区');
  assert.equal(hitTest(L, 8 + 20, 8 + 10), 0, '标题行命中合并块');
  // 没合并的块：链头就是自己
  const flat = layoutTreemap([named(-1, 10, 'r', DIR), named(0, 6, 'a'), named(0, 4, 'b')], 400, 300);
  assert.deepEqual([...flat.head.subarray(0, flat.count)], [...flat.node.subarray(0, flat.count)]);
});

test('单子目录链：长链合成一行；唯一的子节点是文件、没展开的目录、或有看得见的兄弟时照常嵌套', () => {
  const chain = [named(-1, 10, 'r', DIR), named(0, 10, 'src', DIR), named(1, 10, 'main', DIR), named(2, 10, 'java', DIR), named(3, 6, 'A.java'), named(3, 4, 'B.java')];
  const L = layoutTreemap(chain, 500, 400);
  assert.equal(L.count, 3);
  assert.equal(tileTitle(chain, L, 0), 'src › main › java');
  assert.deepEqual([L.tileOf[1], L.tileOf[2], L.tileOf[3]], [0, 0, 0]);

  const file = [named(-1, 10, 'r', DIR), named(0, 10, 'logs', DIR), named(1, 10, 'big.log')];
  const F = layoutTreemap(file, 400, 300);
  assert.equal(F.count, 2);
  assert.equal(tileTitle(file, F, 0), 'logs');

  const leafDir = [named(-1, 10, 'r', DIR), named(0, 10, 'a', DIR), named(1, 10, 'b', DIR | Flag.Unexpanded)];
  const D = layoutTreemap(leafDir, 400, 300);
  assert.equal(D.count, 2, '没展开的独生子目录照常画成叶子');
  assert.equal(tileTitle(leafDir, D, 0), 'a');

  // Users 里除了 Alex 还有个看得见的 Public：不能为了合并把它藏起来
  const two = [named(-1, 100, 'r', DIR), named(0, 100, 'Users', DIR), named(1, 90, 'Alex', DIR), named(2, 90, 'f'), named(1, 10, 'Public', DIR), named(4, 10, 'g')];
  const T = layoutTreemap(two, 400, 300);
  assert.equal(tileTitle(two, T, 0), 'Users');
  assert.ok(T.tileOf[2] > 0 && T.tileOf[4] > 0 && T.tileOf[2] !== T.tileOf[4], 'Alex 和 Public 各有自己的块');
});

test('单子目录链：画不出来的零头（≤ 1%）不挡合并，成片的零头会挡', () => {
  // desktop.ini 这种零头：小到铺不出块，Users 照样和 Alex 合并
  const crumbs = [named(-1, 1e9 + 4096, 'C:\\', DIR), named(0, 1e9 + 4096, 'Users', DIR), named(1, 1e9, 'Alex', DIR), named(2, 6e8, 'Videos'), named(2, 4e8, 'Music'), named(1, 4096, 'desktop.ini')];
  const L = layoutTreemap(crumbs, 400, 300);
  assert.equal(tileTitle(crumbs, L, 0), 'Users › Alex');
  assert.equal(L.tileOf[5], -1, '零头没有块');
  assert.ok(near(kidsBox(L, 0), { x: 14, y: 42, w: 372, h: 244 }));

  // 四千个各自画不出来的小文件加起来占四成：照常嵌套，不然 Alex 的子块会被放大四成
  const dust = [named(-1, 100, 'r', DIR), named(0, 100, 'Pictures', DIR), named(1, 60, '2024', DIR), named(2, 60, 'a.jpg')];
  for (let i = 0; i < 4000; i++) dust.push(named(1, 0.01, `IMG_${i}.jpg`));
  const P = layoutTreemap(dust, 400, 300);
  assert.equal(tileTitle(dust, P, 0), 'Pictures');
  const inner = P.tileOf[2];
  assert.ok(inner > 0 && P.open[inner] === 1 && tileTitle(dust, P, inner) === '2024', '2024 照常是自己的嵌套块');
  assert.equal(P.end[0] - 1, 2, '零头都没有块，只剩 2024 和它的文件');
});

test('单子目录链：从上一层放大进链头或链尾，锚点都是合并块', () => {
  const W = 800;
  const H = 600;
  const home = [{ id: 10, p: -1, v: 100, f: DIR }, { id: 11, p: 0, v: 100, f: DIR }, { id: 12, p: 1, v: 100, f: DIR }, { id: 13, p: 2, v: 60, f: 0 }, { id: 14, p: 2, v: 40, f: 0 }];
  const alex = [{ id: 12, p: -1, v: 100, f: DIR }, { id: 13, p: 0, v: 60, f: 0 }, { id: 14, p: 0, v: 40, f: 0 }];
  const users = [{ id: 11, p: -1, v: 100, f: DIR }, { id: 12, p: 0, v: 100, f: DIR }, { id: 13, p: 1, v: 60, f: 0 }, { id: 14, p: 1, v: 40, f: 0 }];
  const view = (rootId: number, nodes: typeof home) => ({ rootId, nodes, layout: layoutTreemap(nodes, W, H), w: W, h: H });
  const A = view(10, home);
  const merged = rectOf(A.layout, 0);
  assert.equal(A.layout.node[0], 2);
  assert.deepEqual(planZoom(A, view(12, alex)), { kind: 'in', box: merged }, '点合并块进入链尾');
  assert.deepEqual(planZoom(A, view(11, users)), { kind: 'in', box: merged }, '面包屑跳到链头');
  assert.deepEqual(planZoom(view(12, alex), A), { kind: 'out', box: merged }, '从链尾返回');
});

// ---------- 性能 ----------

function timeLayout(name: string, nodes: LayoutNode[]): void {
  const runs: number[] = [];
  let count = 0;
  for (let i = 0; i < 15; i++) {
    const t0 = performance.now();
    count = layoutTreemap(nodes, 1010, 640).count;
    runs.push(performance.now() - t0);
  }
  const cold = runs[0];
  runs.sort((a, b) => a - b);
  const median = runs[Math.floor(runs.length / 2)];
  console.log(`# 12k 节点布局（${name}）：首次 ${cold.toFixed(2)} ms，中位 ${median.toFixed(2)} ms，最慢 ${runs[runs.length - 1].toFixed(2)} ms，排出 ${count} 块`);
  assert.ok(median < 30, `布局太慢：${median} ms`);
}

test('12k 节点布局耗时（打印）', () => {
  const nodes = synth(12000, 3, 1.5); // 值分布平一点，排出的块更多
  assert.equal(nodes.length, 12000);
  timeLayout('随机树', nodes);
  // 最坏情况：一层 12k 个值差不多的文件，每个都有块
  const flat: LayoutNode[] = [node(-1, 0, DIR)];
  for (let i = 0; i < 12000; i++) flat.push(node(0, 2000 - Math.floor(i / 8)));
  flat[0] = node(-1, flat.reduce((s, n, i) => (i ? s + n.v : s), 0), DIR);
  timeLayout('一层 12k 个文件', flat);
});

// ---------- 进入 / 返回过渡 ----------

test('planZoom：新根在旧布局里是进入，旧根在新布局里是返回，其余只淡入', () => {
  const W = 800;
  const H = 600;
  // 视图 A：根 1 → 目录 2（含文件 3、4）、文件 5；视图 B：以目录 2 为根
  const a = [{ id: 1, p: -1, v: 100, f: DIR }, { id: 2, p: 0, v: 70, f: DIR }, { id: 3, p: 1, v: 40, f: 0 }, { id: 4, p: 1, v: 30, f: 0 }, { id: 5, p: 0, v: 30, f: 0 }];
  const b = [{ id: 2, p: -1, v: 70, f: DIR }, { id: 3, p: 0, v: 40, f: 0 }, { id: 4, p: 0, v: 30, f: 0 }];
  const A = { rootId: 1, nodes: a, layout: layoutTreemap(a, W, H), w: W, h: H };
  const B = { rootId: 2, nodes: b, layout: layoutTreemap(b, W, H), w: W, h: H };
  const dirTile = A.layout.tileOf[1];

  const into = planZoom(A, B);
  assert.equal(into.kind, 'in');
  assert.deepEqual(into.box, rectOf(A.layout, dirTile), '进入：锚点是目录在旧布局里的块');

  const out = planZoom(B, A);
  assert.equal(out.kind, 'out');
  assert.deepEqual(out.box, rectOf(A.layout, dirTile), '返回：锚点是旧根在新布局里的块');

  const other = [{ id: 9, p: -1, v: 10, f: DIR }, { id: 10, p: 0, v: 10, f: 0 }];
  const C = { rootId: 9, nodes: other, layout: layoutTreemap(other, W, H), w: W, h: H };
  assert.equal(planZoom(A, C).kind, 'fade');
  assert.equal(planZoom(A, { ...B, w: W + 1 }).kind, 'fade', '画布尺寸变了就不做定位动画');
  assert.equal(planZoom(A, { ...A, rootId: 1 }).kind, 'fade');
});

// ---------- 清理标记继承 ----------

test('inheritMarks：子孙继承组和勾选，自己的组优先，聚合块继承父节点', () => {
  // 0 根(100) ─ 1 A ─ 2 A1、3 A2；0 ─ 4 B ─ 5 B1、6 聚合
  const nodes = [
    { p: -1, id: 100 },
    { p: 0, id: 1 },
    { p: 1, id: 2 },
    { p: 1, id: 3 },
    { p: 0, id: 4 },
    { p: 4, id: 5 },
    { p: 4, id: -1 },
  ];
  const r = inheritMarks(nodes, new Map([[1, 'safe'], [5, 'caution']]), new Set([1]));
  assert.deepEqual([...r.group], [GROUP_NONE, GROUP_SAFE, GROUP_SAFE, GROUP_SAFE, GROUP_NONE, GROUP_REVIEW, GROUP_NONE]);
  assert.deepEqual([...r.selected], [0, 1, 1, 1, 0, 0, 0]);

  const none = inheritMarks(nodes, new Map(), new Set());
  assert.ok(none.group.every((g) => g === 0) && none.selected.every((s) => s === 0));
});

test('ancestorSeed：视图根的祖先被建议指向时，整个视图都继承', () => {
  const group = new Map([[11, 'safe'], [12, 'caution']]);
  assert.deepEqual(ancestorSeed([10, 11, 12], group, new Set([10])), { group: GROUP_REVIEW, selected: true });
  assert.deepEqual(ancestorSeed([10, 11], group, new Set()), { group: GROUP_SAFE, selected: false });
  assert.deepEqual(ancestorSeed([], group, new Set([1])), { group: GROUP_NONE, selected: false });
  const nodes = [{ p: -1, id: 100 }, { p: 0, id: 1 }, { p: 0, id: 2 }];
  const r = inheritMarks(nodes, new Map([[2, 'protected']]), new Set(), { group: GROUP_SAFE, selected: true });
  assert.deepEqual([...r.group], [GROUP_SAFE, GROUP_SAFE, 3]);
  assert.deepEqual([...r.selected], [1, 1, 1]);
});

// ---------- 文字测量 ----------

test('文字测量：整串宽度求和、超宽补省略号、字符宽度只量一次', () => {
  let calls = 0;
  const m = createMeter((_font, ch) => {
    calls++;
    return ch === '…' ? 12 : ch.length > 1 ? 24 : 10;
  });
  assert.equal(m.width('f', 'hello'), 50);
  assert.equal(m.fit('f', 'hello', 60), 'hello');
  assert.equal(m.fit('f', 'hello world', 60), 'hell…');
  assert.equal(m.fit('f', 'ab cd', 45), 'ab…', '省略号前不留空格');
  assert.equal(m.fit('f', 'hello', 5), '');
  assert.equal(m.fit('f', '', 5), '');
  assert.equal(m.width('f', '😀😀'), 48, '代理对按一个字符算');
  const before = calls;
  for (let i = 0; i < 100; i++) m.fit('f', 'hello world', 60);
  assert.equal(calls, before, '缓存之后不再测量');
  m.clear();
  m.width('f', 'hello');
  assert.ok(calls > before, '清缓存后重新测量');
});
