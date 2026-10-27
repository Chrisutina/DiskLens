<script lang="ts">
  import { X } from '@lucide/svelte';
  import { fly } from 'svelte/transition';
  import { prefersReducedMotion } from 'svelte/motion';
  import { app, dismissToast } from '../lib/state.svelte.ts';
  import { t } from '../lib/i18n.ts';

  // 左边条的颜色：success / warn / error 对应 safe / review / danger（DESIGN §9.3）
  const BAR = { info: 'var(--ink-2)', success: 'var(--safe)', warn: 'var(--review)', error: 'var(--danger)' } as const;
</script>

<div class="stack" role="region" aria-label={t('通知')}>
  {#each app.toasts as n (n.id)}
    <div class="float toast" role={n.kind === 'error' ? 'alert' : 'status'} style:--bar={BAR[n.kind]} transition:fly={{ y: 8, duration: prefersReducedMotion.current ? 0 : 160 }}>
      <p>{n.text}</p>
      <button type="button" class="btn btn-ghost btn-icon" aria-label={t('关闭通知')} onclick={() => dismissToast(n.id)}><X size={14} /></button>
    </div>
  {/each}
</div>

<style>
  .stack {
    position: fixed;
    top: 64px;
    left: 50%;
    transform: translateX(-50%);
    z-index: 60;
    display: flex;
    flex-direction: column;
    gap: 8px;
    width: 360px;
    max-width: calc(100vw - 32px);
    max-height: min(320px, calc(100vh - 144px));
    overflow-y: auto;
    pointer-events: none;
  }
  .toast {
    position: relative;
    display: flex;
    flex: none;
    align-items: flex-start;
    gap: 8px;
    padding: 10px 12px 10px 16px;
    overflow: hidden;
    pointer-events: auto;
  }
  /* 3px 色条贴着左边，被圆角裁掉一截，所以不用 border-left */
  .toast::before {
    content: '';
    position: absolute;
    inset: 0 auto 0 0;
    width: 3px;
    background: var(--bar);
  }
  p {
    flex: 1;
    min-width: 0;
    margin: 0;
    padding: 2px 0;
    overflow-wrap: anywhere;
  }
  .btn-icon {
    flex: none;
    width: 24px;
    height: 24px;
    margin: -1px 0;
  }
</style>
