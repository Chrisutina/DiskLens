import { t } from './i18n.ts';
import { api } from './api.ts';
import { app, reveal } from './state.svelte.ts';
import type { NodeDetails, Related } from './types.ts';

interface Point { id: number; path: string; scanId: number }

/** 从关联浏览「定位」离开时所在的对象：之后的详情里给一个「返回」，只在同一次扫描内有效 */
class ReturnPoint { point = $state.raw<Point | null>(null); }
export const relatedReturn = new ReturnPoint();

const message = (e: unknown): string => e instanceof Error ? e.message : String(e);

/**
 * 关联浏览只读：探索不碰清理勾选，定位才交给地图的统一导航。每个实例自己丢弃旧响应。
 * 不调 scan.summary 核对：它在后台要排写许可，删除进行中会被卡住；同一次扫描里节点 ID 不会被别的对象复用，
 * 换了扫描前端自己知道（app.summary.scanId），删掉的节点读详情会失败——核对 ID + 路径就够了。
 */
export class RelatedData {
  node = $state.raw<NodeDetails | null>(null);
  result = $state.raw<Related | null>(null);
  loading = $state(false);
  error = $state<string | null>(null);
  history = $state.raw<Point[]>([]);
  limit = $state(50);
  private generation = 0;
  private origin: Point | null = null;
  private target: Point | null = null;

  get originId(): number | null { return this.origin?.id ?? null; }

  reset(id: number, path: string, scanId: number): void {
    ++this.generation;
    this.origin = this.target = { id, path, scanId };
    this.node = this.result = null;
    this.loading = false;
    this.error = null;
    this.history = [];
    this.limit = 50;
    if (relatedReturn.point?.scanId !== scanId) relatedReturn.point = null;
  }

  /** 只在详情面板开着、选中的还是起点、扫描没换时有效；否则旧响应一律丢掉 */
  private active(generation: number): boolean {
    return generation === this.generation && app.screen === 'main' && app.page === 'browse'
      && app.panelOpen && app.rightTab === 'details' && app.summary?.scanId === this.origin?.scanId
      && app.selectedId === this.origin?.id;
  }

  private async verified(id: number, path: string): Promise<NodeDetails> {
    const node = await api.details(id, app.metric);
    if (node.id !== id || node.path.toLowerCase() !== path.toLowerCase()) throw new Error(t('对象已变化或已删除，请刷新地图后查看'));
    return node;
  }

  /** 读一个对象的关联；explore 表示从当前对象走过去（记进历史，能「返回上一个」）。加载期间保留旧画面并标记。 */
  async load(id = this.target?.id, path = this.target?.path, explore = false, limit = this.limit): Promise<void> {
    if (id == null || path == null || !this.origin) return;
    const previous = this.target;
    const generation = ++this.generation;
    this.target = { id, path, scanId: this.origin.scanId };
    this.limit = limit;
    this.loading = true;
    this.error = null;
    try {
      const [node, result] = await Promise.all([this.verified(id, path), api.related(id, app.metric, limit)]);
      if (!this.active(generation)) return;
      if (result.nodeId !== id) throw new Error(t('关联结果和对象对不上，请重试'));
      if (explore && previous && previous.id !== id) this.history = [...this.history, previous];
      this.node = node;
      this.result = result;
    } catch (e) {
      if (!this.active(generation)) return;
      this.error = message(e);
      if (explore) this.target = previous; // 没走过去：还停在原来的对象上
      else this.node = this.result = null;
    } finally {
      if (generation === this.generation) this.loading = false;
    }
  }

  async back(): Promise<void> {
    const previous = this.history.at(-1);
    if (!previous) return;
    this.history = this.history.slice(0, -1);
    await this.load(previous.id, previous.path, false, 50);
  }

  async home(): Promise<void> {
    if (!this.origin) return;
    this.history = [];
    await this.load(this.origin.id, this.origin.path, false, 50);
  }

  /** 到地图上定位：先核对对象还在，再交给统一导航；离开起点时记下返回点，returning 是走「返回」回来 */
  async locate(id: number, path: string, returning = false): Promise<boolean> {
    if (!this.origin) return false;
    const generation = this.generation, origin = this.origin;
    this.error = null;
    try {
      await this.verified(id, path);
      if (generation !== this.generation || app.screen !== 'main' || app.summary?.scanId !== origin.scanId) return false;
      relatedReturn.point = returning ? null : (relatedReturn.point ?? origin);
      await reveal(id);
      return true;
    } catch (e) {
      if (generation === this.generation) this.error = message(e);
      return false;
    }
  }

  dispose(): void { ++this.generation; }
}

/** 生长树浮层：盖在资源地图上，右侧详情保持打开；它自己的一份关联数据，探索不影响详情里的列表 */
class TreeView {
  open = $state(false);
  readonly data = new RelatedData();
  private trigger: HTMLElement | null = null;

  show(id: number, path: string, scanId: number): void {
    this.trigger = (globalThis.document?.activeElement as HTMLElement | null) ?? null;
    this.data.reset(id, path, scanId);
    this.open = true;
    void this.data.load();
  }

  /** 关闭并把焦点还给打开它的按钮（已经不在页面上就算了） */
  close(): void {
    if (!this.open) return;
    this.open = false;
    this.data.dispose();
    const trigger = this.trigger;
    this.trigger = null;
    if (trigger?.isConnected) trigger.focus();
  }
}
export const relatedTree = new TreeView();
