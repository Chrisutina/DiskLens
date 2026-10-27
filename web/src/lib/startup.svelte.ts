import { api } from './api.ts';
import { app } from './state.svelte.ts';
import type { SavedApp, SavedSummary } from './types.ts';
import { t } from './i18n.ts';

/** 启动页的一行应用：扫描记录里认出的，带上它来自哪条记录 */
export interface StartupApplication extends SavedApp {
  /** 来自哪个扫描范围、那次扫描的完成时刻 */
  scope: string;
  scannedAtMs: number;
  /** 那次和哪一次对照过；null 表示没有可比的上一次，谁都不算新 */
  comparedAtMs: number | null;
}

export const WEEK_MS = 7 * 24 * 3600 * 1000;

/**
 * 启动页自己的读模型：全部来自本机保存的扫描记录（缓存对照），重启后、没有当前扫描结果也看得到；
 * 只看最近一周：一周内的扫描记录、一周内对照出来的新应用。离开页面或刷新后，旧请求不再回填。
 */
export class StartupData {
  records = $state.raw<SavedSummary[]>([]);
  recordsLoading = $state(false);
  recordsSkipped = $state(0);
  recordsError = $state<string | null>(null);
  openError = $state<string | null>(null);
  /** 「最近一周」从哪一刻往回数：每次刷新时取当前时间 */
  asOf = $state(Date.now());
  /** 最近一周的扫描记录，新的在前 */
  recent = $derived(this.records.filter(r => r.completedAtMs >= this.asOf - WEEK_MS));
  /** 最近一周对照出来的新应用：只算和上一次对照过、确实新出现的，第一次扫描就有的不算「新装」 */
  applications = $derived(mergeApps(this.records).filter(a => a.isNew && a.firstSeenMs >= this.asOf - WEEK_MS));
  /** 最近一周的对照里不见了的应用（别的记录里还在的不算） */
  removed = $derived(removedApps(this.records).filter(a => a.scannedAtMs >= this.asOf - WEEK_MS));
  private generation = 0;

  async refresh(): Promise<void> {
    const generation = ++this.generation;
    this.asOf = Date.now();
    this.records = [];
    this.recordsError = this.openError = null;
    this.recordsLoading = true;
    this.recordsSkipped = 0;
    const active = () => generation === this.generation && app.screen === 'start';
    try {
      const records = await api.savedSummaries();
      if (active()) {
        const valid = records.filter(r => r.completedAtMs > 0 && Number.isFinite(new Date(r.completedAtMs).getTime()));
        this.records = valid.sort((a, b) => b.completedAtMs - a.completedAtMs);
        this.recordsSkipped = records.length - valid.length;
      }
    } catch (e) {
      if (active()) this.recordsError = message(e);
    } finally {
      if (active()) this.recordsLoading = false;
    }
  }

  /** 在资源管理器里打开记录里的位置；已经不在了（卸载、搬走）就照实说 */
  async open(path: string): Promise<void> {
    const generation = this.generation;
    this.openError = null;
    try {
      const r = await api.openFolder(path);
      if (generation === this.generation && !r.ok) this.openError = r.message ?? t('打不开这个位置');
    } catch (e) {
      if (generation === this.generation) this.openError = message(e);
    }
  }

  dispose(): void { ++this.generation; }
}

const appKey = (a: SavedApp): string => `${a.kind}:${a.name}`.toLowerCase();

/** 同一个应用在好几条记录里（扫过 C 盘也扫过 Program Files）：同一处只留最近那条记录里的 */
export function mergeApps(records: SavedSummary[]): StartupApplication[] {
  const rows = new Map<string, StartupApplication>();
  for (const r of [...records].sort((a, b) => b.completedAtMs - a.completedAtMs))
    for (const a of r.apps ?? []) {
      const key = `${appKey(a)}|${a.path.toLowerCase()}`;
      if (!rows.has(key)) rows.set(key, { ...a, scope: r.rootPath, scannedAtMs: r.completedAtMs, comparedAtMs: r.appsComparedAtMs });
    }
  return [...rows.values()].sort((a, b) => Number(b.isNew) - Number(a.isNew) || b.firstSeenMs - a.firstSeenMs || b.alloc - a.alloc);
}

export function removedApps(records: SavedSummary[]): (SavedApp & { scope: string; scannedAtMs: number })[] {
  const present = new Set(records.flatMap(r => (r.apps ?? []).map(appKey)));
  return records.flatMap(r => (r.removedApps ?? []).filter(a => !present.has(appKey(a))).map(a => ({ ...a, scope: r.rootPath, scannedAtMs: r.completedAtMs })));
}

const message = (e: unknown): string => e instanceof Error ? e.message : String(e);
