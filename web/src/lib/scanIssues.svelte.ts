import { t } from './i18n.ts';
import { api } from './api.ts';
import { app } from './state.svelte.ts';
import type { ScanIssues, ScanIssueKind } from './types.ts';

export const ISSUE_PAGE_SIZE = 20;

/** 清单只属于一次扫描；关闭、换类和翻页均令旧响应失效。 */
export class ScanIssueList {
  page = $state.raw<ScanIssues | null>(null);
  loading = $state(false);
  error = $state<string | null>(null);
  offset = $state(0);
  private generation = 0;
  private scanId: number | null = null;
  private kind: ScanIssueKind = 'denied';

  async show(scanId: number, kind: ScanIssueKind): Promise<void> {
    this.scanId = scanId;
    this.kind = kind;
    await this.load(0);
  }

  async load(offset = this.offset): Promise<void> {
    const generation = ++this.generation, scanId = this.scanId, kind = this.kind;
    this.offset = Math.max(0, offset);
    this.page = null;
    this.error = null;
    this.loading = true;
    const current = () => generation === this.generation && app.screen === 'main' && app.summary?.scanId === scanId;
    if (!current() || scanId === null) {
      this.loading = false;
      this.error = t('扫描结果已变化，请重新打开扫描详情');
      return;
    }
    try {
      const page = await api.issues(kind, this.offset, ISSUE_PAGE_SIZE, scanId);
      if (!current()) return;
      if (page.scanId !== scanId || page.kind !== kind || page.offset !== this.offset) {
        throw new Error(t('异常清单与当前扫描不一致，请重试'));
      }
      this.page = page;
    } catch (e) {
      if (current()) this.error = e instanceof Error ? e.message : String(e);
    } finally {
      if (generation === this.generation) this.loading = false;
    }
  }

  close(): void {
    ++this.generation;
    this.scanId = null;
    this.page = null;
    this.loading = false;
    this.error = null;
    this.offset = 0;
  }
}
