import { withName, type MsgId } from './i18n.ts';

// 类别名字和颜色的唯一出处。CSS 变量 --cat-<k> 由 applyCategoryCssVars 在启动时注入。

export interface Category {
  k: number;
  key: string;
  /** 原文；显示读 name，跟着界面语言 */
  zh: MsgId;
  readonly name: string;
  color: string;
}

export const CATEGORIES: readonly Category[] = [
  withName({ k: 0, key: 'other', zh: '其他', color: '#7E8799' }),
  withName({ k: 1, key: 'folder', zh: '文件夹', color: '#2A3142' }),
  withName({ k: 2, key: 'video', zh: '视频', color: '#E5486B' }),
  withName({ k: 3, key: 'image', zh: '图片', color: '#F2B53A' }),
  withName({ k: 4, key: 'audio', zh: '音频', color: '#9C6CF0' }),
  withName({ k: 5, key: 'document', zh: '文档', color: '#3AA8E8' }),
  withName({ k: 6, key: 'archive', zh: '压缩包', color: '#EE7A3C' }),
  withName({ k: 7, key: 'code', zh: '代码与配置', color: '#6378F2' }),
  withName({ k: 8, key: 'executable', zh: '程序与库', color: '#47C27A' }),
  withName({ k: 9, key: 'system', zh: '系统', color: '#5E6E8C' }),
  withName({ k: 10, key: 'diskimage', zh: '磁盘镜像', color: '#D257C9' }),
  withName({ k: 11, key: 'data', zh: '数据文件', color: '#2EBFA5' }),
  withName({ k: 12, key: 'unaccounted', zh: '未统计', color: '#3B4252' }),
];

export const catColor = (k: number): string => (CATEGORIES[k] ?? CATEGORIES[0]).color;
export const catName = (k: number): string => (CATEGORIES[k] ?? CATEGORIES[0]).name;

export function applyCategoryCssVars(root: HTMLElement = document.documentElement): void {
  for (const c of CATEGORIES) root.style.setProperty(`--cat-${c.k}`, c.color);
}
