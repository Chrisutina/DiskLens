// 扫描中界面的纯计算（分段布局、阶段步骤）。和组件拆开，node --test 不用起浏览器就能测——
// 总不能指望人眼盯着一条会动的条，去判断它排没排对吧。
import { localized, type MsgId } from '../lib/i18n.ts';
import { PURPOSE_ORDER, purposeOfCategory } from '../lib/purposes.ts';
import { Cat } from '../lib/types.ts';
import type { Phase, ScanMode, ScanProgress } from '../lib/types.ts';

/** 进度条上的一段：按类别 K 切，颜色取 K 默认映射到的用途 U；from/to 是占整条的 0..1 */
export interface Seg {
  k: number;
  u: number;
  bytes: number;
  from: number;
  to: number;
  /** 左右邻居同用途就连成一块（去掉缝和描边）：聚拢以后同色的几段才看得出是一堆 */
  joinL: boolean;
  joinR: boolean;
}

/** 卷根才有「全部」可比；扫文件夹时总长只能是已统计，条永远是满的 */
export const isVolumeRoot = (path: string | null): boolean => /^[A-Za-z]:[\\/]?$/.test(path ?? '');

export function barTotal(p: Pick<ScanProgress, 'bytes' | 'usedBytes'>, volume: boolean): number {
  // 硬链接、压缩、稀疏文件会让已统计略超卷已用，取大的，条别溢出
  return Math.max(volume ? p.usedBytes : 0, p.bytes);
}

/**
 * clustered：读取阶段按类别顺序摆（同用途的段会被别的隔开）；读完按用途顺序聚拢，位置变化交给 CSS 过渡做动画。
 * done：汇总完成，分母换成各段之和，整条铺满、灰色斜纹消失。
 */
export function layout(cat: readonly number[], total: number, clustered: boolean, done: boolean): Seg[] {
  const parts: { k: number; u: number; bytes: number }[] = [];
  // 「未统计」(K=12) 不是读到的东西，由底下的斜纹表示
  for (let k = 0; k < Cat.Unaccounted; k++) if (cat[k] > 0) parts.push({ k, u: purposeOfCategory(k), bytes: cat[k] });
  // sort 是稳定的：同用途内部仍保持类别顺序
  if (clustered) parts.sort((a, b) => PURPOSE_ORDER.indexOf(a.u) - PURPOSE_ORDER.indexOf(b.u));
  const sum = parts.reduce((s, x) => s + x.bytes, 0);
  const den = done ? sum : Math.max(total, sum);
  let at = 0;
  return parts.map((s, i) => {
    const from = at / den;
    at += s.bytes;
    return { ...s, from, to: at / den, joinL: i > 0 && parts[i - 1].u === s.u, joinR: i < parts.length - 1 && parts[i + 1].u === s.u };
  });
}

// ---------------- 阶段 ----------------

export interface Step {
  phase: Phase;
  /** 原文 msgid：显示时 t(label)，单测照中文比 */
  label: MsgId;
}

const MFT_STEPS: Step[] = [
  { phase: 'prepare', label: '准备' },
  { phase: 'mft', label: '读取 MFT' },
  { phase: 'tree', label: '构建目录树' },
  { phase: 'links', label: '核对硬链接' },
  { phase: 'finalize', label: '汇总' },
];
// 常规扫描边走边建树，没有「构建目录树」这一步
const WALK_STEPS: Step[] = [
  { phase: 'prepare', label: '准备' },
  { phase: 'walk', label: '遍历目录' },
  { phase: 'links', label: '核对硬链接' },
  { phase: 'finalize', label: '汇总' },
];

export const stepsFor = (mode: ScanMode): Step[] => (mode === 'mft' ? MFT_STEPS : WALK_STEPS);

const RANK: Record<Phase, number> = { prepare: 0, mft: 1, walk: 1, tree: 2, links: 3, finalize: 4 };

/** 当前步骤的下标。扫描方式还没定（头几十毫秒）时阶段可能不在步骤表里，取不超过它的最后一步 */
export function currentStep(steps: readonly Step[], phase: Phase): number {
  let at = 0;
  steps.forEach((s, i) => {
    if (RANK[s.phase] <= RANK[phase]) at = i;
  });
  return at;
}

/** 后端没给当前路径时（MFT 是顺着记录读的，没有路径）用这句话顶上，免得那一行空着 */
export const PHASE_TEXT: Readonly<Record<Phase, string>> = localized({
  prepare: '正在准备…',
  mft: '正在读取主文件表…',
  walk: '正在遍历目录…',
  tree: '正在构建目录树…',
  links: '正在核对硬链接…',
  finalize: '正在汇总…',
});

/** 整段平均速度；刚开始的几百毫秒样本太少，给 0 比给个离谱的数强 */
export const filesPerSecond = (files: number, elapsedMs: number): number => (elapsedMs >= 300 ? Math.round((files * 1000) / elapsedMs) : 0);
