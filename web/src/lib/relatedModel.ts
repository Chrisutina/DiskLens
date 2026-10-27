import type { NodeDetails, RelatedItem, RelationKind } from './types.ts';
import { localized, t } from './i18n.ts';

// 关联生长树的纯布局（dise §8.2/§8.3）：横向主干从当前对象出发，先排有时间依据的记录，后接一段虚线的「当前关联」，
// 关联对象以曲线分支上下交替展开。位置只由下标决定：展开更多分支只往右加列，已有的不动。

export const RELATION_LABELS: Readonly<Record<RelationKind, string>> = localized({
  root: '所属对象的根', otherRoot: '同一对象的另一处', roleModule: '同一对象的其他模块',
});

/** 关联依据，给人看的一句话 */
export const RELATION_BASIS: Readonly<Record<RelationKind, string>> = localized({
  root: '它所在的这一处就是所属对象的根，归属按路径、安装信息或目录名认出。',
  otherRoot: '同一个所属对象在本次扫描范围里的另一处位置。',
  roleModule: '同一处根下承担不同数据角色的模块（程序、配置、数据、缓存、日志……）。',
});

export interface TrunkEvent { kind: 'created' | 'modified' | 'scanned'; label: string; time: number }

/** 主干只放有时间依据的记录：文件系统的创建、修改时间和本次扫描时间，按先后排；读不到的不补，也不推导别的事件 */
export function relatedTimes(node: Pick<NodeDetails, 'createdMs' | 'modifiedMs'>, scannedAt: number): TrunkEvent[] {
  const events: TrunkEvent[] = [
    { kind: 'created', label: t('文件系统创建'), time: node.createdMs ?? NaN },
    { kind: 'modified', label: t('文件系统修改'), time: node.modifiedMs ?? NaN },
    { kind: 'scanned', label: t('本次扫描'), time: scannedAt },
  ];
  return events.filter((e) => Number.isFinite(e.time) && e.time > 0).sort((a, b) => a.time - b.time);
}

export const TREE = {
  height: 460,
  trunkY: 230,
  pad: 24,
  object: { w: 210, h: 64 },
  owner: { w: 210, h: 50, x: 54, y: 36 },
  eventGap: 150,
  node: { w: 200, h: 66 },
  column: 230,
  rowAbove: 40,
  rowBelow: 354,
  /** 「当前关联」段开头留给段名的长度 */
  relationLead: 160,
} as const;

export interface Box { x: number; y: number; w: number; h: number }
export interface PlacedEvent extends TrunkEvent { x: number }
export interface PlacedBranch { item: RelatedItem; index: number; above: boolean; box: Box; anchorX: number; path: string }

export interface TreeLayout {
  width: number;
  height: number;
  trunkY: number;
  object: Box;
  owner: (Box & { path: string }) | null;
  events: PlacedEvent[];
  /** 时间段到这里结束，后面是没有时间依据的「当前关联」段（画虚线） */
  relationStart: number;
  trunkEnd: number;
  branches: PlacedBranch[];
}

export function layoutTree(times: readonly TrunkEvent[], hasOwner: boolean, items: readonly RelatedItem[], shown: number): TreeLayout {
  const { trunkY, pad } = TREE;
  const object: Box = { x: pad, y: trunkY - TREE.object.h / 2, w: TREE.object.w, h: TREE.object.h };
  let owner: TreeLayout['owner'] = null;
  if (hasOwner) {
    const o = TREE.owner, fromX = object.x + object.w / 2, toX = o.x + o.w / 2, toY = o.y + o.h;
    owner = { x: o.x, y: o.y, w: o.w, h: o.h, path: `M${fromX} ${object.y} C${fromX} ${object.y - 50} ${toX} ${toY + 40} ${toX} ${toY}` };
  }
  const firstEvent = object.x + object.w + 70;
  const events = times.map((t, i) => ({ ...t, x: firstEvent + i * TREE.eventGap }));
  const relationStart = (events.at(-1)?.x ?? firstEvent - TREE.eventGap) + 80;
  const count = Math.max(0, Math.min(Math.floor(shown), items.length));
  const branches = items.slice(0, count).map((item, index): PlacedBranch => {
    const above = index % 2 === 0;
    const anchorX = relationStart + TREE.relationLead + Math.floor(index / 2) * TREE.column;
    const box = { x: anchorX + 18, y: above ? TREE.rowAbove : TREE.rowBelow, w: TREE.node.w, h: TREE.node.h };
    const endX = box.x + box.w / 2, endY = above ? box.y + box.h : box.y, dir = above ? -1 : 1;
    return { item, index, above, box, anchorX, path: `M${anchorX} ${trunkY} C${anchorX} ${trunkY + dir * 70} ${endX} ${endY - dir * 50} ${endX} ${endY}` };
  });
  const trunkEnd = (branches.at(-1)?.anchorX ?? relationStart + TREE.relationLead) + 40;
  const right = Math.max(trunkEnd, ...branches.map((b) => b.box.x + b.box.w));
  return { width: right + pad, height: TREE.height, trunkY, object, owner, events, relationStart, trunkEnd, branches };
}

/** SVG 文字不会自动省略：按字宽估着截断（汉字约两个拉丁字宽），完整内容放在 <title> 和信息卡里 */
export function clipText(text: string, maxUnits: number): string {
  let units = 0;
  for (let i = 0; i < text.length; i++) {
    units += text.charCodeAt(i) > 0x2e80 ? 2 : 1;
    if (units > maxUnits) return text.slice(0, Math.max(1, i - 1)) + '…';
  }
  return text;
}
