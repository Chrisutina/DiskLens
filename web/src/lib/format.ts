import { t } from './i18n.ts';

// 1024 进制、显示成 KB/MB/GB —— 跟资源管理器一个口径，不然用户一对数就以为我们算错了。

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];

export function splitBytes(n: number): { value: string; unit: string } {
  if (!Number.isFinite(n)) return { value: '—', unit: '' };
  const neg = n < 0;
  let v = Math.abs(n);
  let u = 0;
  while (v >= 1024 && u < UNITS.length - 1) {
    v /= 1024;
    u++;
  }
  // 参考图的口径：两位整数以内一位小数（6.6 GB、41.7 GB），三位整数不带小数（135 GB）
  const s = u === 0 ? String(Math.round(v)) : v >= 99.95 ? v.toFixed(0) : v.toFixed(1);
  return { value: (neg ? '-' : '') + s, unit: UNITS[u] };
}

export function formatBytes(n: number): string {
  const { value, unit } = splitBytes(n);
  return unit ? `${value} ${unit}` : value;
}

export const formatCount = (n: number): string => (Number.isFinite(n) ? n.toLocaleString('zh-CN') : '—');

/** 两次扫描之间的增减：+1.2 GB / −300 MB / 持平 */
export const formatDelta = (n: number): string => (n === 0 ? t('持平') : `${n > 0 ? '+' : '−'}${formatBytes(Math.abs(n))}`);

/** x 是 0..1 的比例 */
export function formatPercent(x: number, digits = 1): string {
  if (!Number.isFinite(x)) return '—';
  const p = x * 100;
  if (p > 0 && p < 0.1) return '<0.1%';
  return `${p.toFixed(digits)}%`;
}

export function formatDate(ms: number | null | undefined): string {
  if (ms == null) return '—';
  const d = new Date(ms);
  const pad = (x: number) => String(x).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export function formatDuration(ms: number): string {
  if (!Number.isFinite(ms)) return '—';
  if (ms < 1000) return t('{n} 毫秒', { n: Math.round(ms) });
  const s = ms / 1000;
  if (s < 60) return t('{n} 秒', { n: s.toFixed(1) });
  const m = Math.floor(s / 60);
  return t('{m} 分 {s} 秒', { m, s: Math.round(s - m * 60) });
}

/** 路径太长时从中间省略，保住开头的盘符和结尾的文件名 */
export function middleEllipsis(s: string, max: number): string {
  if (s.length <= max) return s;
  const keep = Math.max(max - 1, 2);
  const head = Math.ceil(keep * 0.4);
  return `${s.slice(0, head)}…${s.slice(s.length - (keep - head))}`;
}

/** base 下的相对路径；不在 base 下就原样返回 */
export function relativePath(path: string, base: string): string {
  const b = base.endsWith('\\') ? base : `${base}\\`;
  return path.toLowerCase().startsWith(b.toLowerCase()) ? path.slice(b.length) : path;
}
