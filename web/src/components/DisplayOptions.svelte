<script lang="ts">
  // 「显示选项」弹层（dise §5.3）：度量切换和地图着色都收在这里，不长期占顶栏主操作区。
  import { SlidersHorizontal } from '@lucide/svelte';
  import { app, setExplorerMode, setMetric } from '../lib/state.svelte.ts';
  import { t } from '../lib/i18n.ts';

  let open = $state(false);
  let root: HTMLDivElement;
  let toggle: HTMLButtonElement;
</script>

<!-- Esc 在捕获阶段就截住：只关弹层，别让全局的 Esc 顺手把详情面板也关了 -->
<svelte:window
  onpointerdown={(e) => open && !root.contains(e.target as Node) && (open = false)}
  onkeydowncapture={(e) => {
    if (!open || e.key !== 'Escape') return;
    open = false;
    e.stopPropagation();
    toggle.focus();
  }}
/>

<div class="wrap" bind:this={root}>
  <button type="button" class="btn" aria-haspopup="dialog" aria-expanded={open} bind:this={toggle} onclick={() => (open = !open)}>
    <SlidersHorizontal size={14} />{t('显示选项')}
  </button>
  {#if open}
    <div class="float pop" role="dialog" aria-label={t('显示选项')}>
      <p class="legend" id="opt-metric">{t('大小的算法')}</p>
      <div class="seg" role="group" aria-labelledby="opt-metric">
        <button type="button" aria-pressed={app.metric === 'alloc'} title={t('文件在磁盘上实际占的空间（按簇取整）')} onclick={() => void setMetric('alloc')}>{t('占用空间')}</button>
        <button type="button" aria-pressed={app.metric === 'size'} title={t('文件内容的字节数')} onclick={() => void setMetric('size')}>{t('文件大小')}</button>
      </div>
      <p class="legend" id="opt-color">{t('地图着色')}</p>
      <div class="seg" role="group" aria-labelledby="opt-color">
        <button type="button" aria-pressed={app.explorerMode === 'category'} title={t('按用途上色')} onclick={() => setExplorerMode('category')}>{t('类别')}</button>
        <button type="button" aria-pressed={app.explorerMode === 'cleanup'} title={t('按能不能清理上色，勾选的建议画斜纹')} onclick={() => setExplorerMode('cleanup')}>{t('清理')}</button>
      </div>
    </div>
  {/if}
</div>

<style>
  .wrap {
    position: relative;
  }
  .pop {
    position: absolute;
    top: calc(100% + 8px);
    right: 0;
    z-index: 30;
    display: grid;
    justify-items: start;
    width: 240px;
    padding: 12px 16px 16px;
  }
  .legend {
    margin: 4px 0 6px;
    color: var(--muted);
    font-size: 12px;
  }
  .legend + .seg ~ .legend {
    margin-top: 14px;
  }
  @media (max-width: 480px) {
    .pop { position: fixed; top: 100px; right: 16px; width: min(240px, calc(100vw - 32px)); }
  }
</style>
