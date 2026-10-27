import type { Ancestor } from '../lib/types.ts';

export interface Crumb {
  id: number;
  label: string;
}

const trimEnd = (p: string): string => p.replace(/[\\/]+$/, '');

/**
 * 面包屑：用户目录前缀折成「~」，前面的段（C:\ › Users › Alex）不再显示。
 * trail 是从树根到当前视图根的名字链；树根的名字是它的完整路径（卷根 C:\ 或被扫描的文件夹）。
 */
export function breadcrumbs(trail: Ancestor[], home: string | null): Crumb[] {
  const plain = trail.map((a) => ({ id: a.id, label: a.name }));
  if (!home || !trail.length) return plain;
  const h = trimEnd(home).toLowerCase();
  let path = '';
  for (let i = 0; i < trail.length; i++) {
    path = i === 0 ? trail[i].name : `${trimEnd(path)}\\${trail[i].name}`;
    if (trimEnd(path).toLowerCase() === h) return [{ id: trail[i].id, label: '~' }, ...plain.slice(i + 1)];
  }
  // 扫描的就是用户目录里的某个文件夹：根节点名字是完整路径，把前缀换成 ~ 即可
  const root = trimEnd(trail[0].name);
  if (root.toLowerCase().startsWith(`${h}\\`)) plain[0].label = `~${root.slice(h.length)}`;
  return plain;
}

/** 路径太深时只留开头和最后几段，当前位置不能被挤掉；id 为 -1 的是「…」占位 */
export function collapseCrumbs(crumbs: Crumb[], keepTail = 3): Crumb[] {
  if (crumbs.length <= keepTail + 2) return crumbs;
  return [crumbs[0], { id: -1, label: '…' }, ...crumbs.slice(-keepTail)];
}
