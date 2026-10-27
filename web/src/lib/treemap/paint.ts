// Canvas 2D 绘制。整幅「场景」（块、斜纹、文字）画在下层画布；悬停阴影、选中环画在上层画布——
// 鼠标移动只重画上层，不会触发整幅重绘。颜色只来自 purposes.ts 和 theme.css 的变量（读一次缓存起来）。
import { t } from '../i18n.ts';
import { purpose, CLEANUP_COLORS, PURPOSE, PURPOSE_COUNT } from '../purposes.ts';
import { Flag } from '../types.ts';
import type { ViewNode } from '../types.ts';
import { formatBytes } from '../format.ts';
import { tileTitle, type Layout } from './layout.ts';
import { createMeter } from './text.ts';

export interface Theme {
  ink: string;
  ink2: string;
  muted: string;
  card: string;
  sans: string;
  mono: string;
}

export function readTheme(el: Element = document.documentElement): Theme {
  const cs = getComputedStyle(el);
  const v = (name: string, fallback: string): string => cs.getPropertyValue(name).trim() || fallback;
  return {
    ink: v('--ink', 'black'),
    ink2: v('--ink-2', 'black'),
    muted: v('--muted', 'gray'),
    card: v('--card', 'white'),
    sans: v('--font-sans', 'sans-serif'),
    mono: v('--font-mono', 'monospace'),
  };
}

export interface SceneData {
  nodes: readonly ViewNode[];
  layout: Layout;
  /** true = 按清理状态着色，false = 按用途着色 */
  cleanup: boolean;
  /** 每个视图节点继承后的清理组（marks.ts 的 GROUP_*） */
  group: Uint8Array;
  /** 勾选斜纹的不透明度 0..1；稳定时只有 0 和 1，动画中间是小数 */
  hatch: Float32Array;
}

export interface OverlayData {
  nodes: readonly ViewNode[];
  layout: Layout;
  cleanup: boolean;
  group: Uint8Array;
  /** 块序号，-1 表示没有 */
  hover: number;
  selected: number;
  /** 右键菜单正对着的块：和选中一样画外环 */
  target: number;
  /** 图例高亮或筛选的那一类（类别模式是用途 U，清理模式是清理组），-1 表示没有：其余区域蒙一层浅色 */
  focus: number;
}

export interface Painter {
  scene(ctx: CanvasRenderingContext2D, W: number, H: number, dpr: number, d: SceneData): void;
  overlay(ctx: CanvasRenderingContext2D, W: number, H: number, dpr: number, d: OverlayData): void;
  /** 字体加载完成后宽度缓存和斜纹图案都要重来 */
  reset(): void;
  /** 需要预加载的字体，加载完调用 reset 再重画 */
  fonts: readonly string[];
}

const R_BIG = 6;
const R_SMALL = 3;
const R_SWITCH = 24; // 短边小于它的块用小圆角
const PAD = 10; // 块内文字离边
const TINY = 8; // 短边小于它的块只画色块，不走圆角路径（一帧里成千上万个）
const HATCH_GAP = 7;
const HATCH_W = 1.5;
const AGG_ALPHA = 0.6;
/** 参考图里容器目录是用途底色，里面的子块比它深一档（实测约向描边色靠 22%），层次一眼分得清 */
const CHIP_MIX = 0.22;
const HOVER_W = 1.5;
const RING_W = 2;
const UNSCANNED = PURPOSE.Unexplained;
/** 调色板序号：0..13 是用途 U，14..17 是清理模式的 无 / 可放心 / 先确认 / 别删；样式键里占低 5 位 */
const CLEAN_BASE = PURPOSE_COUNT;
const BASE_BITS = 5;
const BASE_MASK = (1 << BASE_BITS) - 1;
const AGG = 1 << BASE_BITS;
const FULL_SHIFT = BASE_BITS + 1;
const CHIP = 1 << (BASE_BITS + 2);
/** 样式键总位数：base 5 位 + 聚合 + 勾选 + 嵌套叶子 */
const KEY_BITS = BASE_BITS + 3;
const KEY_MASK = (1 << KEY_BITS) - 1;

interface Style {
  fill: string;
  line: string;
  lw: number;
  alpha: number;
  pattern: CanvasPattern | null;
  /** 勾选覆盖的完整样式：描边换成斜纹绿 */
  full: boolean;
}

function palette(base: number): { fill: string; line: string; dot: string } {
  if (base < CLEAN_BASE) {
    const p = purpose(base);
    return { fill: p.fill, line: p.line, dot: p.dot };
  }
  // 样式表按 5 位 base 全量生成，18..31 用不到，给个兜底免得建表时越界
  const c = [CLEANUP_COLORS.none, CLEANUP_COLORS.safe, CLEANUP_COLORS.review, CLEANUP_COLORS.protected][base - CLEAN_BASE] ?? CLEANUP_COLORS.none;
  return { fill: c.fill, line: c.line, dot: c.text };
}

/** #rrggbb 向另一个颜色靠 t */
function mixHex(a: string, b: string, t: number): string {
  const pa = parseInt(a.slice(1), 16);
  const pb = parseInt(b.slice(1), 16);
  const ch = (shift: number): number => Math.round(((pa >> shift) & 255) * (1 - t) + ((pb >> shift) & 255) * t);
  return `rgb(${ch(16)}, ${ch(8)}, ${ch(0)})`;
}

/** 未扫描在两种模式下都画灰斜纹：它不属于任何用途，也没有清理可言 */
const baseOf = (nd: ViewNode, group: number, cleanup: boolean): number =>
  nd.u === UNSCANNED ? UNSCANNED : cleanup ? CLEAN_BASE + group : nd.u > 0 && nd.u < UNSCANNED ? nd.u : 0;

/** 45° 斜纹图案。位图边长取整数设备像素，再用 pattern 变换把周期校准回 7 个 CSS 像素 */
function makeHatch(color: string, dpr: number, ctx: CanvasRenderingContext2D): CanvasPattern | null {
  const px = Math.max(4, Math.round(HATCH_GAP * dpr));
  const c = document.createElement('canvas');
  c.width = px;
  c.height = px;
  const g = c.getContext('2d');
  if (!g) return null;
  g.strokeStyle = color;
  g.lineWidth = HATCH_W * (px / HATCH_GAP);
  g.beginPath();
  // x + y = c 的三条线（c = 0、px、2px）拼起来才能首尾相接
  for (const off of [0, px, 2 * px]) {
    g.moveTo(off + px, -px);
    g.lineTo(off - px, px);
  }
  g.stroke();
  const p = ctx.createPattern(c, 'repeat');
  p?.setTransform(new DOMMatrix().scale(HATCH_GAP / px));
  return p;
}

export function createPainter(theme: Theme): Painter {
  const scratch = document.createElement('canvas').getContext('2d')!;
  const meter = createMeter((font, ch) => {
    scratch.font = font;
    return scratch.measureText(ch).width;
  });
  const fDirName = `600 13px ${theme.sans}`;
  const fDirSize = `400 12px ${theme.mono}`;
  const fName = `500 12px ${theme.sans}`;
  const fSize = (px: number): string => `600 ${px}px ${theme.mono}`;

  let styles: Style[] | null = null;
  let hatchSafe: CanvasPattern | null = null;
  let stylesDpr = 0;

  const buildStyles = (ctx: CanvasRenderingContext2D, dpr: number): void => {
    hatchSafe = makeHatch(CLEANUP_COLORS.hatch, dpr, ctx);
    const gray = makeHatch(CLEANUP_COLORS.unscannedHatch, dpr, ctx);
    styles = [];
    for (let key = 0; key <= KEY_MASK; key++) {
      const base = key & BASE_MASK;
      const agg = key & AGG ? 1 : 0;
      const full = (key >> FULL_SHIFT) & 1;
      const chip = key & CHIP ? 1 : 0;
      const pal = palette(base);
      styles.push({
        fill: chip && base !== UNSCANNED ? mixHex(pal.fill, pal.line, CHIP_MIX) : pal.fill,
        line: full && base !== UNSCANNED ? CLEANUP_COLORS.hatchLine : pal.line,
        lw: full && base !== UNSCANNED ? HATCH_W : 1,
        alpha: agg ? AGG_ALPHA : 1,
        pattern: base === UNSCANNED ? gray : full ? hatchSafe : null,
        full: full === 1 && base !== UNSCANNED,
      });
    }
    stylesDpr = dpr;
  };

  // 每帧复用的缓冲，只增不减
  let cap = 0;
  let X0 = new Float64Array(0);
  let Y0 = new Float64Array(0);
  let X1 = new Float64Array(0);
  let Y1 = new Float64Array(0);
  let BUCKET = new Int32Array(0);
  let ORDER = new Int32Array(0);
  const grow = (n: number): void => {
    if (n <= cap) return;
    cap = Math.max(n, cap * 2, 256);
    X0 = new Float64Array(cap);
    Y0 = new Float64Array(cap);
    X1 = new Float64Array(cap);
    Y1 = new Float64Array(cap);
    BUCKET = new Int32Array(cap);
    ORDER = new Int32Array(cap);
  };

  const drawNormal = (ctx: CanvasRenderingContext2D, st: Style, s: number, e: number): void => {
    const half = st.lw / 2;
    const faded = st.alpha < 1;
    ctx.lineWidth = st.lw;
    ctx.strokeStyle = st.line;
    ctx.fillStyle = st.fill;
    const batch = !faded && !st.pattern;
    if (batch) ctx.beginPath();
    for (let t = s; t < e; t++) {
      const k = ORDER[t];
      const w = X1[k] - X0[k];
      const h = Y1[k] - Y0[k];
      const m = w < h ? w : h;
      const r = Math.max(0, Math.min(m < R_SWITCH ? R_SMALL : R_BIG, m / 2) - half);
      if (!batch) ctx.beginPath();
      ctx.roundRect(X0[k] + half, Y0[k] + half, w - st.lw, h - st.lw, r);
      if (batch) continue;
      if (faded) {
        // 聚合块：先垫一层卡片底色，60% 的填充才真的比兄弟块淡
        ctx.globalAlpha = 1;
        ctx.fillStyle = theme.card;
        ctx.fill();
        ctx.globalAlpha = st.alpha;
        ctx.fillStyle = st.fill;
      }
      ctx.fill();
      if (st.pattern) {
        ctx.fillStyle = st.pattern;
        ctx.fill();
        ctx.fillStyle = st.fill;
      }
      ctx.stroke();
    }
    if (batch) {
      // 同层同样式的块彼此不相交，并成一条路径只需一次填充和描边
      ctx.fill();
      ctx.stroke();
    }
    ctx.globalAlpha = 1;
  };

  const drawTiny = (ctx: CanvasRenderingContext2D, st: Style, s: number, e: number): void => {
    // 同一样式的小块并成一条路径、一次填充：一帧里上万个块，逐个 fillRect 光录指令就要好几毫秒
    ctx.globalAlpha = st.alpha;
    ctx.fillStyle = st.line;
    ctx.beginPath();
    for (let t = s; t < e; t++) {
      const k = ORDER[t];
      ctx.rect(X0[k], Y0[k], X1[k] - X0[k], Y1[k] - Y0[k]);
    }
    ctx.fill();
    if (!st.full) {
      ctx.fillStyle = st.fill;
      ctx.beginPath();
      for (let t = s; t < e; t++) {
        const k = ORDER[t];
        const w = X1[k] - X0[k];
        const h = Y1[k] - Y0[k];
        if (w >= 3 && h >= 3) ctx.rect(X0[k] + 1, Y0[k] + 1, w - 2, h - 2);
      }
      ctx.fill();
    }
    ctx.globalAlpha = 1;
  };

  const sizeFont = (text: string, w: number, h: number, avail: number): number => {
    const want = Math.min(22, Math.max(13, Math.floor(13 + (Math.sqrt(w * h) - 70) * 0.045)));
    const fit = Math.floor((avail * 13) / meter.width(fSize(13), text));
    const px = Math.min(want, fit);
    return px >= 13 ? px : 0;
  };

  const drawLabels = (ctx: CanvasRenderingContext2D, d: SceneData, dpr: number): void => {
    const { nodes, layout: L } = d;
    const snap = (v: number): number => Math.round(v * dpr) / dpr;
    let font = '';
    let color = '';
    let align: CanvasTextAlign = 'left';
    ctx.textBaseline = 'alphabetic';
    ctx.textAlign = 'left';
    ctx.globalAlpha = 1;
    const set = (f: string, c: string, a: CanvasTextAlign): void => {
      if (f !== font) {
        ctx.font = f;
        font = f;
      }
      if (c !== color) {
        ctx.fillStyle = c;
        color = c;
      }
      if (a !== align) {
        ctx.textAlign = a;
        align = a;
      }
    };
    for (let k = 0; k < L.count; k++) {
      const x0 = X0[k];
      const y0 = Y0[k];
      const w = X1[k] - x0;
      const h = Y1[k] - y0;
      if (w < 30 || h < 18) continue;
      const nd = nodes[L.node[k]];
      // 合并过单子目录链的块，面积是链头的，大小也写链头的
      const sizeText = formatBytes(nodes[L.head[k]].v);

      if (L.open[k] === 1) {
        // 目录标题行：左边名字（单子目录链写成「Users › Alex」），右上大小；名字完整放不下时宁可丢大小，也不把名字截短去让位
        const avail = w - 2 * PAD;
        const sizeW = meter.width(fDirSize, sizeText);
        const title = tileTitle(nodes, L, k);
        const showSize = meter.width(fDirName, title) + 10 + sizeW <= avail;
        const name = meter.fit(fDirName, title, avail);
        const base = snap(y0 + 19);
        if (name) {
          set(fDirName, theme.ink, 'left');
          ctx.fillText(name, x0 + PAD, base);
        }
        if (showSize) {
          set(fDirSize, theme.muted, 'right');
          ctx.fillText(sizeText, x0 + w - PAD, base);
        }
        continue;
      }

      // 叶子（文件、没展开的目录、聚合块）：左上名字，左下大小；放不下名字只写大小，再小留白
      const avail = w - 2 * PAD;
      const canName = avail >= 28 && h >= 24;
      let sizeEnd = 0;
      if (canName) {
        const name = meter.fit(fName, nd.n || (nd.f & Flag.Aggregate ? t('其余 {n} 项', { n: nd.c }) : ''), avail);
        if (name) {
          set(fName, theme.ink2, 'left');
          ctx.fillText(name, x0 + PAD, snap(y0 + 19.5));
        }
        if (w >= 96 && h >= 64) {
          const px = sizeFont(sizeText, w, h, avail);
          if (px) {
            set(fSize(px), theme.ink, 'left');
            ctx.fillText(sizeText, x0 + PAD, snap(y0 + h - 11));
            sizeEnd = PAD + meter.width(fSize(px), sizeText);
          }
        }
      } else if (h >= 20 && meter.width(fSize(13), sizeText) <= w - 12) {
        set(fSize(13), theme.ink, 'left');
        ctx.fillText(sizeText, x0 + 6, snap(y0 + h / 2 + 4.7));
      }

      // 还能点进去的目录：右下角 ↘（不和左下的大小撞车才画）
      if ((nd.f & Flag.Directory) !== 0 && nd.c > 0 && w >= 48 && h >= 44 && sizeEnd + 8 < w - 18) {
        const ax = x0 + w - 18;
        const ay = y0 + h - 18;
        ctx.globalAlpha = 0.75;
        ctx.strokeStyle = theme.muted;
        ctx.lineWidth = 1.25;
        ctx.lineCap = 'round';
        ctx.lineJoin = 'round';
        ctx.beginPath();
        ctx.moveTo(ax + 2, ay + 2);
        ctx.lineTo(ax + 10, ay + 10);
        ctx.moveTo(ax + 10, ay + 3.5);
        ctx.lineTo(ax + 10, ay + 10);
        ctx.lineTo(ax + 3.5, ay + 10);
        ctx.stroke();
        ctx.globalAlpha = 1;
      }
    }
  };

  const painter: Painter = {
    fonts: [fDirName, fDirSize, fName, fSize(16)],

    reset() {
      meter.clear();
      styles = null;
    },

    scene(ctx, W, H, dpr, d) {
      const { nodes, layout: L } = d;
      const n = L.count;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, W, H);
      if (n === 0) return;
      if (!styles || stylesDpr !== dpr) buildStyles(ctx, dpr);
      grow(n);
      const snap = (v: number): number => Math.round(v * dpr) / dpr;

      // 先把每块的设备像素对齐矩形和样式算出来：边缘对齐像素，1px 描边才清晰
      const partial: number[] = [];
      let maxDepth = 0;
      for (let k = 0; k < n; k++) {
        const i = L.node[k];
        const nd = nodes[i];
        const x0 = snap(L.x[k]);
        const y0 = snap(L.y[k]);
        const x1 = Math.max(snap(L.x[k] + L.w[k]), x0 + 1 / dpr);
        const y1 = Math.max(snap(L.y[k] + L.h[k]), y0 + 1 / dpr);
        X0[k] = x0;
        Y0[k] = y0;
        X1[k] = x1;
        Y1[k] = y1;
        const a = d.hatch[i];
        const full = a >= 0.999 ? 1 : 0;
        if (!full && a > 0.001) partial.push(k);
        const depth = L.depth[k];
        // 嵌在容器目录里的叶子块（chip）比容器深一档，参考图的「类别」模式就是这样分层的；
        // 「清理」模式的填充色规范里写死了，不加这一档
        const chip = !d.cleanup && depth > 0 && L.open[k] === 0 ? CHIP : 0;
        const key = baseOf(nd, d.group[i], d.cleanup) | ((nd.f & Flag.Aggregate) !== 0 ? AGG : 0) | (full << FULL_SHIFT) | chip;
        const tiny = Math.min(x1 - x0, y1 - y0) < TINY ? 1 : 0;
        if (depth > maxDepth) maxDepth = depth;
        BUCKET[k] = (((depth << 1) | tiny) << KEY_BITS) | key;
      }

      // 按「层 → 大小 → 样式」计数排序：父先于子，同样式的块连着画，少换几万次 fillStyle
      const nb = (maxDepth + 1) << (KEY_BITS + 1);
      const start = new Int32Array(nb + 1);
      for (let k = 0; k < n; k++) start[BUCKET[k] + 1]++;
      for (let b = 0; b < nb; b++) start[b + 1] += start[b];
      const fillAt = start.slice(0, nb);
      for (let k = 0; k < n; k++) ORDER[fillAt[BUCKET[k]]++] = k;
      for (let b = 0; b < nb; b++) {
        const s = start[b];
        const e = start[b + 1];
        if (s === e) continue;
        const st = styles![b & KEY_MASK];
        if ((b >> KEY_BITS) & 1) drawTiny(ctx, st, s, e);
        else drawNormal(ctx, st, s, e);
      }

      // 斜纹淡入淡出中的块：盖在最上面，反正勾选覆盖的是整棵子树，盖在子块上和最终样子一致
      for (const k of partial) {
        const a = d.hatch[L.node[k]];
        const w = X1[k] - X0[k];
        const h = Y1[k] - Y0[k];
        const m = Math.min(w, h);
        ctx.globalAlpha = a;
        ctx.beginPath();
        if (m < TINY) {
          ctx.rect(X0[k], Y0[k], w, h);
        } else {
          ctx.roundRect(X0[k] + HATCH_W / 2, Y0[k] + HATCH_W / 2, w - HATCH_W, h - HATCH_W, Math.max(0, Math.min(m < R_SWITCH ? R_SMALL : R_BIG, m / 2) - HATCH_W / 2));
        }
        if (hatchSafe && m >= TINY) {
          ctx.fillStyle = hatchSafe;
          ctx.fill();
        }
        if (m >= TINY) {
          ctx.strokeStyle = CLEANUP_COLORS.hatchLine;
          ctx.lineWidth = HATCH_W;
          ctx.stroke();
        } else {
          ctx.fillStyle = CLEANUP_COLORS.hatchLine;
          ctx.fill();
        }
      }
      ctx.globalAlpha = 1;

      drawLabels(ctx, d, dpr);
    },

    overlay(ctx, W, H, dpr, d) {
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, W, H);
      const L = d.layout;
      const snap = (v: number): number => Math.round(v * dpr) / dpr;
      // 把块的轮廓（向外扩 off 个像素，负数是向内缩）加进当前路径
      const outline = (k: number, off: number): void => {
        const x0 = snap(L.x[k]);
        const y0 = snap(L.y[k]);
        const w = Math.max(snap(L.x[k] + L.w[k]) - x0, 1 / dpr);
        const h = Math.max(snap(L.y[k] + L.h[k]) - y0, 1 / dpr);
        const m = Math.min(w, h);
        const r = Math.max(0, Math.min(m < R_SWITCH ? R_SMALL : R_BIG, m / 2) + off);
        ctx.roundRect(x0 - off, y0 - off, Math.max(0, w + 2 * off), Math.max(0, h + 2 * off), r);
      };

      const ring = (k: number): void => {
        ctx.beginPath();
        outline(k, RING_W / 2);
        ctx.strokeStyle = theme.ink;
        ctx.lineWidth = RING_W;
        ctx.stroke();
      };

      // 图例高亮 / 筛选：整幅蒙浅色，只把这一类的叶子块抠出来（evenodd）。叶子互不重叠，展开的目录本身不抠——
      // 目录里混着别的用途时只亮匹配的那几块；颜色不变，只是别的变淡，场景层不用重画
      if (d.focus >= 0) {
        ctx.beginPath();
        ctx.rect(0, 0, W, H);
        for (let k = 0; k < L.count; k++) {
          if (L.open[k]) continue;
          const i = L.node[k];
          if ((d.cleanup ? d.group[i] : d.nodes[i].u) !== d.focus) continue;
          const x0 = snap(L.x[k]);
          const y0 = snap(L.y[k]);
          ctx.rect(x0, y0, Math.max(snap(L.x[k] + L.w[k]) - x0, 1 / dpr), Math.max(snap(L.y[k] + L.h[k]) - y0, 1 / dpr));
        }
        ctx.fillStyle = 'rgba(255, 255, 255, 0.74)';
        ctx.fill('evenodd');
      }

      if (d.hover >= 0 && d.hover < L.count) {
        const k = d.hover;
        const nd = d.nodes[L.node[k]];
        // 阴影只留块外面的部分：先把块自己裁掉，免得盖住下层的文字和斜纹
        ctx.save();
        ctx.beginPath();
        ctx.rect(0, 0, W, H);
        outline(k, 0);
        ctx.clip('evenodd');
        ctx.shadowColor = 'rgba(0, 0, 0, 0.08)';
        ctx.shadowBlur = 8 * dpr;
        ctx.shadowOffsetY = 2 * dpr;
        ctx.fillStyle = theme.card;
        ctx.beginPath();
        outline(k, 0);
        ctx.fill();
        ctx.restore();
        ctx.beginPath();
        outline(k, -HOVER_W / 2);
        ctx.strokeStyle = palette(baseOf(nd, d.group[L.node[k]], d.cleanup)).dot;
        ctx.lineWidth = HOVER_W;
        ctx.stroke();
      }
      if (d.selected >= 0 && d.selected < L.count) ring(d.selected);
      if (d.target >= 0 && d.target < L.count && d.target !== d.selected) ring(d.target);
    },
  };
  return painter;
}
