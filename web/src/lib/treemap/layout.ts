// squarified treemap 布局与命中测试（DESIGN §8.5）。纯函数、不碰 DOM：测试直接 import，12k 节点要在几毫秒内算完，
// 所以全程用类型化数组，输出是「先序排列的结构数组」而不是一堆对象。
import { Flag } from '../types.ts';

export interface LayoutNode {
  /** 父节点下标，根为 -1；父一定在子前面 */
  readonly p: number;
  readonly v: number;
  readonly f: number;
}

export interface LayoutOptions {
  /** 视图根外留白（视图根本身不画框） */
  margin: number;
  /** 第一层块间隙 / 更深层块间隙 */
  gapTop: number;
  gapDeep: number;
  /** 目录标题行高，以及标题下方子块区的四周内边距 */
  header: number;
  inset: number;
  /** 目录宽或高小于它就不展开，按叶子画 */
  minDirW: number;
  minDirH: number;
  /** 收缩间隙后宽或高不足这个值的块直接丢掉 */
  minTile: number;
}

export const DEFAULT_LAYOUT: Readonly<LayoutOptions> = {
  margin: 8,
  gapTop: 4,
  gapDeep: 3,
  header: 28,
  inset: 6,
  minDirW: 90,
  minDirH: 64,
  minTile: 1,
};

/** 合并单子目录链的门槛：唯一铺得出的子块至少占兄弟总和的这么多；零头再多就照常嵌套，免得链尾的子块被放大 */
const CHAIN_SHARE = 0.99;

export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/** 块按先序排列（父在子前），第 k 个块就是下面各数组的第 k 项 */
export interface Layout {
  count: number;
  /** 块 → 视图节点下标；合并过单子目录链的块是链尾 */
  node: Int32Array;
  /** 块 → 单子目录链的链头；没合并时等于 node */
  head: Int32Array;
  /** 画出来的矩形（已经扣掉块间隙） */
  x: Float64Array;
  y: Float64Array;
  w: Float64Array;
  h: Float64Array;
  /** 0 = 视图根的直接子块 */
  depth: Uint16Array;
  /** 1 = 展开的目录（有标题行和子块区） */
  open: Uint8Array;
  /** 子树结束位置（不含），命中测试靠它跳过整棵子树 */
  end: Int32Array;
  /** 视图节点下标 → 块；没有布局的是 -1 */
  tileOf: Int32Array;
  options: Readonly<LayoutOptions>;
}

/**
 * 把按值降序的 m 个条目铺满 (x,y,w,h)，结果写进 out（每项 4 个数：x y w h）。
 * 面积严格正比于值，相邻块之间不留缝；缝由调用方事先把区域外扩、事后把块内缩来做。
 * 值必须是正数。
 */
export function squarifyInto(vals: ArrayLike<number>, m: number, x: number, y: number, w: number, h: number, out: Float64Array): void {
  let total = 0;
  for (let i = 0; i < m; i++) total += vals[i];
  if (!(total > 0) || !(w > 0) || !(h > 0)) {
    for (let i = 0; i < m; i++) {
      out[4 * i] = x;
      out[4 * i + 1] = y;
      out[4 * i + 2] = 0;
      out[4 * i + 3] = 0;
    }
    return;
  }
  const k = (w * h) / total;
  let fx = x;
  let fy = y;
  let fw = w;
  let fh = h;
  let i = 0;
  while (i < m) {
    const side = Math.min(fw, fh);
    if (!(side > 0)) {
      for (let q = i; q < m; q++) {
        out[4 * q] = fx;
        out[4 * q + 1] = fy;
        out[4 * q + 2] = 0;
        out[4 * q + 3] = 0;
      }
      return;
    }
    // 一排里不断加入下一项，直到最差长宽比不再变好
    const s2 = side * side;
    let sum = vals[i] * k;
    let hi = sum;
    let lo = sum;
    let worst = Math.max((s2 * hi) / (sum * sum), (sum * sum) / (s2 * lo));
    let j = i + 1;
    while (j < m) {
      const a = vals[j] * k;
      const ns = sum + a;
      const nhi = a > hi ? a : hi;
      const nlo = a < lo ? a : lo;
      const nw = Math.max((s2 * nhi) / (ns * ns), (ns * ns) / (s2 * nlo));
      if (nw > worst) break;
      sum = ns;
      hi = nhi;
      lo = nlo;
      worst = nw;
      j++;
    }
    // 宽矩形沿左边竖着摆一列，窄矩形沿上边横着摆一行；最后一排吃掉剩下的全部厚度，免得留浮点缝
    const wide = fw >= fh;
    const thick = j >= m ? (wide ? fw : fh) : sum / side;
    const stop = wide ? fy + fh : fx + fw;
    let cur = wide ? fy : fx;
    for (let q = i; q < j; q++) {
      const len = q === j - 1 ? Math.max(0, stop - cur) : (vals[q] * k) / thick;
      const o = 4 * q;
      if (wide) {
        out[o] = fx;
        out[o + 1] = cur;
        out[o + 2] = thick;
        out[o + 3] = len;
      } else {
        out[o] = cur;
        out[o + 1] = fy;
        out[o + 2] = len;
        out[o + 3] = thick;
      }
      cur += len;
    }
    if (wide) {
      fx += thick;
      fw = Math.max(0, fw - thick);
    } else {
      fy += thick;
      fh = Math.max(0, fh - thick);
    }
    i = j;
  }
}

/** 便于测试和小场景的包装：非正数、NaN 的条目得到零面积矩形，其余按值降序铺 */
export function squarify(values: readonly number[], x: number, y: number, w: number, h: number): Rect[] {
  const order: number[] = [];
  values.forEach((v, i) => {
    if (v > 0 && Number.isFinite(v)) order.push(i);
  });
  order.sort((a, b) => values[b] - values[a]);
  const vals = Float64Array.from(order, (i) => values[i]);
  const out = new Float64Array(order.length * 4);
  squarifyInto(vals, order.length, x, y, w, h, out);
  const rects: Rect[] = values.map(() => ({ x, y, w: 0, h: 0 }));
  order.forEach((i, j) => {
    rects[i] = { x: out[4 * j], y: out[4 * j + 1], w: out[4 * j + 2], h: out[4 * j + 3] };
  });
  return rects;
}

export function emptyLayout(opt: Readonly<LayoutOptions> = DEFAULT_LAYOUT): Layout {
  return {
    count: 0,
    node: new Int32Array(0),
    head: new Int32Array(0),
    x: new Float64Array(0),
    y: new Float64Array(0),
    w: new Float64Array(0),
    h: new Float64Array(0),
    depth: new Uint16Array(0),
    open: new Uint8Array(0),
    end: new Int32Array(0),
    tileOf: new Int32Array(0),
    options: opt,
  };
}

/**
 * nodes[0] 是视图根，不画框，只画它的子孙。块间隙的做法：把区域外扩 gap/2 再切分，每块再内缩 gap/2，
 * 这样最外层的块正好贴着留白线，相邻块之间正好是 gap。
 */
export function layoutTreemap(nodes: readonly LayoutNode[], width: number, height: number, opt: Partial<LayoutOptions> = {}): Layout {
  const o: LayoutOptions = { ...DEFAULT_LAYOUT, ...opt };
  const n = nodes.length;
  if (n === 0 || nodes[0].p >= 0 || !(width > 0) || !(height > 0)) return emptyLayout(o);

  const valid = (i: number): boolean => {
    const nd = nodes[i];
    return nd.v > 0 && Number.isFinite(nd.v) && (nd.f & Flag.Deleted) === 0;
  };

  // 按父节点把子节点分桶（CSR）：start[p]..start[p+1] 是 p 的有效子节点
  const start = new Int32Array(n + 1);
  for (let i = 1; i < n; i++) {
    const p = nodes[i].p;
    if (p >= 0 && p < i && valid(i)) start[p + 1]++;
  }
  let maxKids = 0;
  for (let i = 0; i < n; i++) {
    if (start[i + 1] > maxKids) maxKids = start[i + 1];
    start[i + 1] += start[i];
  }
  const kids = new Int32Array(start[n]);
  const fill = start.slice(0, n);
  for (let i = 1; i < n; i++) {
    const p = nodes[i].p;
    if (p >= 0 && p < i && valid(i)) kids[fill[p]++] = i;
  }

  const rx = new Float64Array(n);
  const ry = new Float64Array(n);
  const rw = new Float64Array(n);
  const rh = new Float64Array(n);
  const dp = new Uint16Array(n);
  const stack = new Int32Array(n);
  const vals = new Float64Array(maxKids);
  const cells = new Float64Array(maxKids * 4);
  let sp = 0;

  const L = emptyLayout(o);
  L.node = new Int32Array(n);
  L.head = new Int32Array(n);
  L.x = new Float64Array(n);
  L.y = new Float64Array(n);
  L.w = new Float64Array(n);
  L.h = new Float64Array(n);
  L.depth = new Uint16Array(n);
  L.open = new Uint8Array(n);
  L.end = new Int32Array(n);
  L.tileOf = new Int32Array(n).fill(-1);

  // 把 parent 的子块铺进内容区 (cx,cy,cw,ch)，写进 rx..rh，并按「第一个子块最先弹出」的顺序压栈
  const place = (parent: number, cx: number, cy: number, cw: number, ch: number, gap: number, depth: number): void => {
    const s = start[parent];
    const m = start[parent + 1] - s;
    if (m === 0 || !(cw > 0) || !(ch > 0)) return;
    for (let j = 1; j < m; j++) {
      // 契约保证同一父节点的子节点按值降序；不信的话排一次，不影响常规路径
      if (nodes[kids[s + j]].v > nodes[kids[s + j - 1]].v) {
        kids.subarray(s, s + m).sort((a, b) => nodes[b].v - nodes[a].v);
        break;
      }
    }
    for (let j = 0; j < m; j++) vals[j] = nodes[kids[s + j]].v;
    const half = gap / 2;
    squarifyInto(vals, m, cx - half, cy - half, cw + gap, ch + gap, cells);
    for (let j = m - 1; j >= 0; j--) {
      const w = cells[4 * j + 2] - gap;
      const h = cells[4 * j + 3] - gap;
      if (w < o.minTile || h < o.minTile) continue;
      const c = kids[s + j];
      rx[c] = cells[4 * j] + half;
      ry[c] = cells[4 * j + 1] + half;
      rw[c] = w;
      rh[c] = h;
      dp[c] = depth;
      stack[sp++] = c;
    }
  };

  const kidSum = (p: number): number => {
    let s = 0;
    for (let j = start[p]; j < start[p + 1]; j++) s += nodes[kids[j]].v;
    return s;
  };

  place(0, o.margin, o.margin, width - 2 * o.margin, height - 2 * o.margin, o.gapTop, 0);
  let count = 0;
  while (sp > 0) {
    const i = stack[--sp];
    const k = count++;
    L.node[k] = i;
    L.head[k] = i;
    L.x[k] = rx[i];
    L.y[k] = ry[i];
    L.w[k] = rw[i];
    L.h[k] = rh[i];
    L.depth[k] = dp[i];
    L.tileOf[i] = k;
    if (start[i + 1] > start[i] && rw[i] >= o.minDirW && rh[i] >= o.minDirH) {
      L.open[k] = 1;
      const cx = rx[i] + o.inset;
      const cy = ry[i] + o.header + o.inset;
      const cw = rw[i] - 2 * o.inset;
      const ch = rh[i] - o.header - 2 * o.inset;
      // 单子目录链（「Users › Alex」）：内容区里只铺出一块、它独占九成九以上（剩下的零头小到画不出来）、
      // 而且还能展开，就收回这块，把它的子块直接铺进同一个内容区，标题合成一行，省掉一层嵌套。
      // 合并块从此代表链尾：点进去、悬停、右键都对着它；链上每个目录的 tileOf 都指向它。
      for (let c = i; ; ) {
        const top = sp;
        place(c, cx, cy, cw, ch, o.gapDeep, dp[i] + 1);
        const only = sp === top + 1 ? stack[top] : -1;
        if (only < 0 || start[only + 1] === start[only] || nodes[only].v < CHAIN_SHARE * kidSum(c)) break;
        sp = top;
        c = only;
        L.node[k] = c;
        L.tileOf[c] = k;
      }
    }
  }
  L.count = count;

  // 子树 = [k, end[k])：往后第一个深度不大于自己的块就是边界
  const chain: number[] = [];
  for (let k = 0; k < count; k++) {
    while (chain.length > 0 && L.depth[chain[chain.length - 1]] >= L.depth[k]) L.end[chain.pop()!] = k;
    chain.push(k);
  }
  while (chain.length > 0) L.end[chain.pop()!] = count;
  return L;
}

/** 块的标题：合并过的单子目录链从链头写到链尾（「Users › Alex」），没合并就是自己的名字 */
export function tileTitle(nodes: readonly { p: number; n: string }[], L: Layout, k: number): string {
  let i = L.node[k];
  let title = nodes[i].n;
  while (i !== L.head[k]) {
    i = nodes[i].p;
    title = `${nodes[i].n} › ${title}`;
  }
  return title;
}

/**
 * 指针下最深的块，没有返回 -1。用「含间隙的格子」而不是画出来的矩形判断，
 * 指针在两块之间的缝里时落到邻居身上，悬停不会在缝上闪一下父目录。
 */
export function hitTest(L: Layout, px: number, py: number): number {
  const { x, y, w, h, end, depth, options } = L;
  let best = -1;
  let i = 0;
  let stop = L.count;
  while (i < stop) {
    const g = (depth[i] === 0 ? options.gapTop : options.gapDeep) / 2;
    if (px >= x[i] - g && px < x[i] + w[i] + g && py >= y[i] - g && py < y[i] + h[i] + g) {
      best = i;
      stop = end[i];
      i++;
    } else {
      i = end[i];
    }
  }
  return best;
}
