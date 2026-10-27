import type { Suggestion } from './types.ts';
import { t } from './i18n.ts';

/** 批量清理默认移入回收站；单项确认传入自己的 permanent 值。 */
export function cleanupSpace(s: Suggestion, bytes: number, permanent = false): { immediate: number; recycle: number; manual: number } {
  switch (s.action) {
    case 'deleteContents':
    case 'emptyRecycleBin': return { immediate: bytes, recycle: 0, manual: 0 };
    case 'deleteNodes': return { immediate: permanent ? bytes : 0, recycle: permanent ? 0 : bytes, manual: 0 };
    default: return { immediate: 0, recycle: 0, manual: bytes };
  }
}

/** 固定 10% 容量档，同档优先较少文件；不能用两项差值判定「接近」，否则排序不传递。 */
export function compareSuggestions(a: Suggestion, b: Suggestion, isAdmin: boolean): number {
  const risk = { safe: 0, caution: 1, protected: 2 };
  const execution = (s: Suggestion) => s.action === 'info' ? 3
    : s.requiresAdmin && !isAdmin ? 2
      : s.action === 'command' || s.action === 'openSettings' ? 1 : 0;
  const spaceBand = (bytes: number) => bytes > 0 ? Math.floor(Math.log(bytes) / Math.log(1.1)) : -1;
  return risk[a.group] - risk[b.group] || execution(a) - execution(b)
    || spaceBand(b.bytes) - spaceBand(a.bytes) || (a.count ?? 0) - (b.count ?? 0)
    || b.bytes - a.bytes || a.id.localeCompare(b.id);
}

export function cleanupSpaceLabel(s: Suggestion): string {
  switch (s.action) {
    case 'deleteNodes': return t('默认移入回收站，清空后才释放空间');
    case 'deleteContents': return t('永久清理内容，预计立即释放');
    case 'emptyRecycleBin': return t('永久清空回收站，预计立即释放');
    case 'command': return t('需确认运行命令，释放量以完成后为准');
    case 'openSettings': return t('需在系统设置中处理，不计入立即释放');
    default: return t('仅供查看，不计入可清理空间');
  }
}
