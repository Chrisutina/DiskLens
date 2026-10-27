<script lang="ts">
  // 用途构成条：扫描进度的主角（DESIGN §9.2）。分段、聚拢、闪一下都是数据变了由 CSS 补间出来的，这里不跑定时器。
  import { CLEANUP_COLORS, PURPOSE, PURPOSE_ORDER, purpose } from '../lib/purposes.ts';
  import type { ScanProgress } from '../lib/types.ts';
  import { barTotal, layout } from './ScanModel.ts';
  import { t } from '../lib/i18n.ts';

  let { progress, volume }: { progress: ScanProgress | null; volume: boolean } = $props();

  const phase = $derived(progress?.phase ?? 'prepare');
  // 光标只在读取/遍历时往前推
  const reading = $derived(phase === 'mft' || phase === 'walk');
  // 读完才聚拢：MFT 在「构建目录树」，常规扫描没这一步，到「核对硬链接」才聚
  const clustered = $derived(phase === 'tree' || phase === 'links' || phase === 'finalize');
  const done = $derived(phase === 'finalize');
  // 去重那一小段闪一下：元素挂上就播一次，播完自己是透明的，不用定时器收尾
  const flash = $derived(phase === 'links' || phase === 'finalize');

  const segs = $derived(layout(progress?.categoryBytes ?? [], progress ? barTotal(progress, volume) : 0, clustered, done));
  const edge = $derived(segs.at(-1)?.to ?? 0);
  const present = $derived(new Set(segs.map((s) => s.u)));
  // 汇总完成后没有「尚未统计」可言
  const pending = $derived(volume && !done && edge < 0.999);

  // 未扫描块的斜纹：和资源地图里同一个色、同一个线宽（§8.2：1.5px，间距 7px）
  const unscanned = purpose(PURPOSE.Unexplained);
  const hatch = (gap: number): string => `repeating-linear-gradient(45deg, ${CLEANUP_COLORS.unscannedHatch} 0 1.5px, transparent 1.5px ${gap}px), ${unscanned.fill}`;
</script>

<div class="wrap">
  <p class="caption">{volume ? t('已统计空间构成') : t('已发现内容构成')}</p>
  {#if !volume}<p class="hint">{t('仅表示已发现内容的用途占比，不代表扫描完成度。')}</p>{/if}
  <div
    class="slot"
    role="img"
    aria-label={volume ? t('已统计空间按用途构成') : t('已发现内容按用途构成，不代表扫描完成度')}
    style:background={hatch(7)}
  >
    <div class="track" class:cluster={clustered}>
      <!-- 斜纹钉在槽上不动，已统计的部分用纯色盖住。不盖的话缝里透斜纹，整条像一圈虚线框，丑 -->
      <span class="under" style:width="calc({edge * 100}% + 2px)" style:background={unscanned.fill}></span>
      {#each segs as s (s.k)}
        <span
          class="seg"
          class:jl={s.joinL}
          class:jr={s.joinR}
          style:left="calc({s.from * 100}% + {s.joinL ? 0 : 1}px)"
          style:width="calc({(s.to - s.from) * 100}% - {(s.joinL ? 0 : 1) + (s.joinR ? 0 : 1)}px)"
          style:--seg-fill={purpose(s.u).fill}
          style:--seg-line={purpose(s.u).line}
        ></span>
      {/each}
      {#if flash}<span class="flash" style:left="{edge * 100}%"></span>{/if}
      <span class="cursor" class:on={reading} style:left="{edge * 100}%"></span>
    </div>
  </div>

  <ul class="legend" aria-label={t('图例')}>
    {#each PURPOSE_ORDER.filter((u) => present.has(u)) as u (u)}
      <li><span class="swatch" style:background={purpose(u).dot}></span>{purpose(u).name}</li>
    {/each}
    {#if pending}<li><span class="swatch" style:background={hatch(4)}></span>{t('尚未统计')}</li>{/if}
  </ul>
</div>

<style>
  /* 光标要探出条的上下沿，所以外面留白、槽本身不裁剪 */
  .wrap {
    padding-top: 6px;
  }
  .caption {
    margin: 0 0 4px;
    color: var(--ink-2);
    font-size: 13px;
    font-weight: 500;
  }
  .hint {
    margin: 0 0 8px;
    color: var(--muted);
    font-size: 12px;
  }
  .slot {
    position: relative;
    height: 40px;
    border: 1px solid var(--line);
    border-radius: 8px;
  }
  .track {
    position: absolute;
    inset: 1px;
  }
  /* 比轨道各探出 1px，把轨道和槽边框之间那一圈也盖住 */
  .under {
    position: absolute;
    top: -1px;
    bottom: -1px;
    left: -1px;
    border-radius: 7px;
    transition: width 300ms linear;
  }
  .seg {
    position: absolute;
    top: 0;
    bottom: 0;
    box-sizing: border-box;
    border: 1px solid var(--seg-line);
    border-radius: 6px;
    background: var(--seg-fill);
    transition:
      left 300ms linear,
      width 300ms linear;
  }
  .seg.jl {
    border-left-width: 0;
    border-top-left-radius: 0;
    border-bottom-left-radius: 0;
  }
  .seg.jr {
    border-right-width: 0;
    border-top-right-radius: 0;
    border-bottom-right-radius: 0;
  }
  /* 聚拢和铺满时放慢，让人看得清它们是怎么挪过去的 */
  .cluster .seg,
  .cluster .under {
    transition-duration: 700ms;
    transition-timing-function: var(--ease);
  }

  .cursor {
    position: absolute;
    top: -6px;
    bottom: -6px;
    width: 3px;
    margin-left: -1.5px;
    border-radius: 2px;
    background: var(--ink);
    box-shadow: 0 0 0 2px var(--card);
    opacity: 0;
    transition:
      left 300ms linear,
      opacity 200ms;
  }
  .cursor.on {
    opacity: 1;
  }

  /* 被去重的那一小段：在已统计的右端亮两下，缩没 */
  .flash {
    position: absolute;
    top: 0;
    bottom: 0;
    width: 0;
    margin-left: -9px;
    border-radius: 4px;
    background: var(--ink);
    opacity: 0;
    animation: flash 800ms var(--ease) forwards;
  }
  @keyframes flash {
    0% {
      width: 9px;
      opacity: 0;
    }
    20% {
      opacity: 0.7;
    }
    45% {
      opacity: 0.12;
    }
    70% {
      width: 9px;
      opacity: 0.7;
    }
    100% {
      width: 0;
      opacity: 0;
    }
  }

  /* 和资源地图图例一个写法；K→U 的默认映射最多出 6 种用途加「尚未统计」，一行装得下，先把这一行占住别让卡片跳 */
  .legend {
    display: flex;
    flex-wrap: wrap;
    gap: 4px 14px;
    min-height: 20px;
    margin: 12px 0 0;
    padding: 0;
    color: var(--muted);
    font-size: 12px;
    line-height: 20px;
    list-style: none;
  }
  .legend li {
    display: inline-flex;
    align-items: center;
    gap: 6px;
  }
  .legend .swatch {
    width: 8px;
    height: 8px;
    border-radius: 2px;
  }
</style>
