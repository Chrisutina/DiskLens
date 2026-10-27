// 目录共用的小工具。单独一个文件：en.ts / ja.ts 要用，又不能反过来 import i18n.ts（那会绕成圈）。

/** 参数：数字按千分位写，null / undefined 写成空（错误信息之类可能还没有） */
export type Params = Record<string, string | number | null | undefined>;
/** 一条译文：固定文字（{name} 占位），或者按参数拼的函数（英文单复数） */
export type Msg = string | ((p: Params) => string);

/** 数字按千分位写；三种语言都是 1,234 */
export const num = (v: string | number | null | undefined): string =>
  typeof v === 'number' ? (Number.isFinite(v) ? v.toLocaleString('en-US') : '—') : (v ?? '');

export const fill = (s: string, p?: Params): string =>
  p ? s.replace(/\{(\w+)\}/g, (m, k: string) => (k in p ? num(p[k]) : m)) : s;

/** 英文单复数：plural(2, 'file') → "2 files"；不规则的给出复数形式 */
export const plural = (n: string | number | null | undefined, one: string, many = `${one}s`): string =>
  `${num(n)} ${Number(n) === 1 ? one : many}`;
