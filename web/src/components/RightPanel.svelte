<script lang="ts">
  // 空间浏览页的右侧面板（DESIGN §9.3）：按需打开，内容是选中项详情或当前位置的大文件；× 或 Esc 关闭，焦点回到打开它的地方。
  import { X } from '@lucide/svelte';
  import { app, closePanel, type RightTab } from '../lib/state.svelte.ts';
  import DetailsPanel from './DetailsPanel.svelte';
  import LargestList from './LargestList.svelte';
  import RecommendList from './RecommendList.svelte';
  import CleanupTasks from './CleanupTasks.svelte';
  import { t, type MsgId } from '../lib/i18n.ts';

  const TABS: Record<RightTab, { title: MsgId; sub: MsgId }> = {
    details: { title: '详情', sub: '选中项的信息。' },
    largest: { title: '大文件', sub: '当前位置下最大的 100 个文件。' },
    cleanup: { title: '清理建议', sub: '当前扫描范围；先确认项目，再执行清理。进度和结果在任务区。' },
  };

  const tab = $derived(TABS[app.rightTab]);
</script>

<aside class="card panel" aria-label={t(tab.title)}>
  <header>
    <div class="top">
      <h2 class="card-title">{t(tab.title)}</h2>
      <button type="button" class="btn btn-ghost btn-icon close" aria-label={t('关闭面板')} title={t('关闭（Esc）')} onclick={closePanel}><X size={16} /></button>
    </div>
    <p class="card-sub">{t(tab.sub)}</p>
  </header>

  <!-- 换内容时整块重建并淡入（160ms，§8.7） -->
  {#key app.rightTab}
    <div class="body">
      {#if app.rightTab === 'details'}
        <DetailsPanel />
      {:else if app.rightTab === 'largest'}
        <LargestList />
      {:else}
        <CleanupTasks />
        <RecommendList />
      {/if}
    </div>
  {/key}
</aside>

<style>
  .panel {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-height: 0;
    overflow: hidden;
  }
  /* 左右 16px：参考图里标题和复选框左缘对齐（实测），规范写的 20px 会错开 4px */
  header {
    flex: none;
    padding: 12px 16px 10px;
    border-bottom: 1px solid var(--line);
  }
  /* 关闭按钮和标题同一行，副标题在下面铺满 */
  .top {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    min-height: 32px;
  }
  .close {
    margin-right: -8px;
  }
  .body {
    display: flex;
    flex: 1;
    flex-direction: column;
    min-height: 0;
    animation: fade var(--dur) var(--ease);
  }
  @keyframes fade {
    from {
      opacity: 0;
    }
  }
  @media (prefers-reduced-motion: reduce) { .body { animation: none; } }
</style>
