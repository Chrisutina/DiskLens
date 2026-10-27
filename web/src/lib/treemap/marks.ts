// 清理建议只直接指向少数节点（cleanupMarks），资源地图要让它们的子孙也跟着变色、打斜纹：沿视图父链往下继承。
// 视图根的祖先在视图之外，但建议可能正好指向它们（放大进 .npm 之后里面全都该是「已勾选」），所以支持从祖先取种子。

/** 0 = 不涉及清理，其余与 CLEANUP_COLORS 的键一一对应 */
export const GROUP_NONE = 0;
export const GROUP_SAFE = 1;
export const GROUP_REVIEW = 2;
export const GROUP_PROTECTED = 3;

const CODE: Readonly<Record<string, number>> = { safe: GROUP_SAFE, caution: GROUP_REVIEW, protected: GROUP_PROTECTED };

export interface MarkNode {
  readonly p: number;
  /** 聚合节点是 -1，不会被任何建议直接指向 */
  readonly id: number;
}

export interface Seed {
  group: number;
  selected: boolean;
}

export const NO_SEED: Readonly<Seed> = { group: GROUP_NONE, selected: false };

export interface Inherited {
  /** 每个视图节点最终所属的清理组（GROUP_*） */
  group: Uint8Array;
  /** 1 = 被勾选的建议覆盖 */
  selected: Uint8Array;
}

export function inheritMarks(
  nodes: readonly MarkNode[],
  group: ReadonlyMap<number, string>,
  selected: ReadonlySet<number>,
  seed: Readonly<Seed> = NO_SEED,
): Inherited {
  const n = nodes.length;
  const g = new Uint8Array(n);
  const s = new Uint8Array(n);
  // 没有建议、没有祖先种子是最常见的情况，不用逐个节点查表
  if (group.size === 0 && selected.size === 0 && seed.group === GROUP_NONE && !seed.selected) return { group: g, selected: s };
  for (let i = 0; i < n; i++) {
    const nd = nodes[i];
    const inheritedGroup = nd.p >= 0 ? g[nd.p] : seed.group;
    const inheritedSel = nd.p >= 0 ? s[nd.p] : seed.selected ? 1 : 0;
    const own = nd.id >= 0 ? group.get(nd.id) : undefined;
    g[i] = own !== undefined ? (CODE[own] ?? GROUP_NONE) : inheritedGroup;
    s[i] = inheritedSel === 1 || (nd.id >= 0 && selected.has(nd.id)) ? 1 : 0;
  }
  return { group: g, selected: s };
}

/** ancestorIds 从树根到视图根的直接父节点；离视图根最近的有组的祖先说了算，任何一个祖先被勾选就整棵覆盖 */
export function ancestorSeed(ancestorIds: readonly number[], group: ReadonlyMap<number, string>, selected: ReadonlySet<number>): Seed {
  const seed: Seed = { group: GROUP_NONE, selected: false };
  for (let i = ancestorIds.length - 1; i >= 0; i--) {
    const id = ancestorIds[i];
    if (seed.group === GROUP_NONE) {
      const own = group.get(id);
      if (own !== undefined) seed.group = CODE[own] ?? GROUP_NONE;
    }
    if (selected.has(id)) seed.selected = true;
  }
  return seed;
}
