// 启动页磁盘行的判定逻辑。抽成纯函数，是因为「要不要弹提权」这种分支靠手点是点不全的，别逞强。
import type { DriveInfo } from '../lib/types.ts';
import { t } from '../lib/i18n.ts';

/** SSD / HDD / 网络 / 可移动 片；认不出介质的固定盘就不画，别瞎编 */
export function driveKind(d: DriveInfo): string | null {
  switch (d.driveType) {
    case 'network': return t('网络');
    case 'removable': return t('可移动');
    case 'cdrom': return t('光驱');
    case 'ram': return t('内存盘');
    case 'fixed': return d.isSsd === true ? 'SSD' : d.isSsd === false ? 'HDD' : null;
    default: return null;
  }
}

/**
 * 「深度扫描」能不能点、悬停说什么。
 * 后端的 canMft 还要求「已是管理员」，非管理员时所有卷都是 false，没法据此判断提权有没有用；
 * 所以非管理员时按同一口径（本地 NTFS）自己判断，免得 U 盘、网络盘点了「深度扫描」白白弹一次提权。
 */
export function deepScanState(d: DriveInfo, admin: boolean): { off: boolean; title: string } {
  if (!d.isReady) return { off: true, title: t('磁盘未就绪') };
  const localNtfs = d.fileSystem.toUpperCase() === 'NTFS' && (d.driveType === 'fixed' || d.driveType === 'removable');
  if (admin ? !d.canMft : !localNtfs) return { off: true, title: t('只有本地 NTFS 卷支持深度扫描') };
  return { off: false, title: admin ? t('直接读取 NTFS 主文件表，更快也更完整') : t('需要管理员权限：点击后会提示以管理员身份重启') };
}
