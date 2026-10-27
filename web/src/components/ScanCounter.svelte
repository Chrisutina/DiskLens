<script lang="ts">
  import { Tween, prefersReducedMotion } from 'svelte/motion';
  import { linear } from 'svelte/easing';
  import { formatCount, splitBytes } from '../lib/format.ts';

  let { label, value, bytes = false }: { label: string; value: number; bytes?: boolean } = $props();

  // 进度事件大约 100ms 一次，补间比间隔略长，数字就是连续滚动而不是一格一格蹦；系统开了「减少动效」就直接跳
  const tween = Tween.of(() => value, { duration: () => prefersReducedMotion.current ? 0 : 260, easing: linear });
  $effect(() => { if (prefersReducedMotion.current) void tween.set(value, { duration: 0 }); });
  const shown = $derived(bytes ? splitBytes(tween.current) : { value: formatCount(Math.round(tween.current)), unit: '' });
</script>

<div class="cell">
  <div class="label">{label}</div>
  <div class="value num">{shown.value}{#if shown.unit}<span class="unit">{shown.unit}</span>{/if}</div>
</div>

<style>
  .cell {
    min-width: 0;
  }
  .label {
    color: var(--muted);
    font-size: 13px;
    line-height: 20px;
  }
  /* 和统计卡的大数字同一规格：mono 28/600 */
  .value {
    margin-top: 2px;
    overflow: hidden;
    font-size: 28px;
    font-weight: 600;
    line-height: 36px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .unit {
    margin-left: 0.22em;
  }
</style>
