import type { ViewNode } from '../types.ts';
import type { Layout } from './layout.ts';

/** 只访问画出的真实条目；聚合块没有独立身份，进入对应父目录即可继续浏览。 */
export function keyboardTiles(nodes: readonly ViewNode[], layout: Layout): number[] {
  const tiles: number[] = [];
  for (let k = 0; k < layout.count; k++) if (nodes[layout.node[k]].id >= 0) tiles.push(k);
  return tiles;
}

/** 箭头按画布的先序顺序访问，避免嵌套矩形之间的空间导航漏掉小文件。 */
export function moveTile(tiles: readonly number[], current: number, key: string): number | null {
  const at = tiles.indexOf(current);
  switch (key) {
    case 'Home': return tiles[0] ?? -1;
    case 'End': return tiles.at(-1) ?? -1;
    case 'ArrowDown':
    case 'ArrowRight': return tiles[Math.min(at + 1, tiles.length - 1)] ?? -1;
    case 'ArrowUp':
    case 'ArrowLeft': return tiles[at < 0 ? tiles.length - 1 : Math.max(0, at - 1)] ?? -1;
    default: return null;
  }
}
