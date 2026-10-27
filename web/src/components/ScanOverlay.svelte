<script lang="ts">
  // 扫描中（DESIGN §9.2）：页面中央一张 640 宽的卡，用途构成条是进度的主角。
  import { fade } from 'svelte/transition';
  import { prefersReducedMotion } from 'svelte/motion';
  import { Check, TriangleAlert, Zap } from '@lucide/svelte';
  import ScanBar from './ScanBar.svelte';
  import ScanCounter from './ScanCounter.svelte';
  import { PHASE_TEXT, currentStep, filesPerSecond, isVolumeRoot, stepsFor } from './ScanModel.ts';
  import { app, cancelScan, leaveScan } from '../lib/state.svelte.ts';
  import { formatDuration, middleEllipsis } from '../lib/format.ts';
  import { t } from '../lib/i18n.ts';


  const p = $derived(app.progress);
  // 以后端报的为准；第一条进度到之前只有用户点的那个（可能是 auto，那就先不亮模式片）
  const mode = $derived(p && p.mode !== 'auto' ? p.mode : app.scanMode);
  const steps = $derived(stepsFor(mode));
  const now = $derived(currentStep(steps, p?.phase ?? 'prepare'));
  const where = $derived(app.scanPath ?? '');
  const phaseText = $derived(app.scanCancelling ? t('正在取消…') : PHASE_TEXT[p?.phase ?? 'prepare']);
</script>

<!-- 固定铺满：扫描结束时它在原地淡出，露出已经画好的主界面，而不是先白一下再跳（淡出要 global，否则父级切走时不播） -->
<div class="stage" out:fade|global={{ duration: prefersReducedMotion.current ? 0 : 240 }}>
  <section class="card scan" aria-label={t('扫描进度')}>
    {#if app.scanError}
      <div class="fail">
        <span class="ico"><TriangleAlert size={18} /></span>
        <div class="why">
          <h2 class="card-title">{t('扫描失败')}</h2>
          <p class="mono where" title={where}>{middleEllipsis(where, 56)}</p>
          <p class="msg">{app.scanError}</p>
        </div>
      </div>
      <footer class="foot">
        <span></span>
        <button type="button" class="btn" onclick={leaveScan}>{t('返回')}</button>
      </footer>
    {:else}
      <header class="head">
        <h2 class="title" title={where}>{t('正在扫描')} <span class="mono">{middleEllipsis(where, 40)}</span></h2>
        {#if mode === 'mft'}
          <span class="chip"><Zap size={12} />{t('MFT 深度扫描')}</span>
        {:else if mode === 'walk'}
          <span class="chip">{t('常规扫描')}</span>
        {/if}
      </header>

      <div class="nums">
        <ScanCounter label={t('文件')} value={p?.files ?? 0} />
        <ScanCounter label={t('目录')} value={p?.dirs ?? 0} />
        <ScanCounter label={t('已统计')} value={p?.bytes ?? 0} bytes />
      </div>

      <ScanBar progress={p} volume={isVolumeRoot(app.scanPath)} />

      <ol class="steps" aria-label={t('扫描阶段')}>
        {#each steps as s, i (s.phase)}
          <li class:done={i < now} class:now={i === now} aria-current={i === now ? 'step' : undefined}>
            <span class="node">{#if i < now}<Check size={11} strokeWidth={3.5} />{/if}</span>
            {t(s.label)}
          </li>
        {/each}
      </ol>

      <div class="activity" role="progressbar" aria-label={t('扫描进度')} aria-valuetext={phaseText}><span></span></div>
      <p class="phase" role="status">{phaseText}</p>
      {#if p?.currentPath}<p class="path mono" title={p.currentPath}>{middleEllipsis(p.currentPath, 78)}</p>{/if}

      <footer class="foot">
        <span class="stats">
          {t('耗时')} <span class="num">{formatDuration(p?.elapsedMs ?? 0)}</span>
          <i aria-hidden="true"></i>
          {t('速度')} <span class="num">{t('{n} 文件/秒', { n: filesPerSecond(p?.files ?? 0, p?.elapsedMs ?? 0) })}</span>
        </span>
        <button type="button" class="btn" disabled={app.scanCancelling} onclick={() => void cancelScan()}>{app.scanCancelling ? t('正在取消…') : t('取消')}</button>
      </footer>
    {/if}
  </section>
</div>

<style>
  /* 层级：高于主界面顶栏（30）、低于菜单/对话框/提示（40+） */
  .stage {
    position: fixed;
    inset: 0;
    z-index: 35;
    display: grid;
    padding: 16px;
    overflow-y: auto;
    background: var(--page);
  }
  /* 居中用 margin:auto 而不是 place-items：窗口矮到放不下时 place-items 会把顶部切掉还滚不上去 */
  .scan {
    width: 640px;
    max-width: 100%;
    margin: auto;
    padding: 20px 24px 0;
  }

  .head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    min-height: 26px;
  }
  .title {
    min-width: 0;
    margin: 0;
    overflow: hidden;
    font-size: 16px;
    font-weight: 600;
    line-height: 26px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .head .chip {
    flex: none;
    height: 26px;
  }

  /* 三个大数字：竖线分隔，和顶部统计卡同一个套路 */
  .nums {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    margin: 18px 0 14px;
  }
  .nums > :global(*:not(:first-child)) {
    padding-left: 20px;
    border-left: 1px solid var(--line);
  }

  /* 阶段步骤：当前步骤 ink，完成的打勾，没到的发灰；连接线只是个意思 */
  .steps {
    display: flex;
    align-items: center;
    margin: 16px 0 0;
    padding: 0;
    list-style: none;
  }
  .steps li {
    display: flex;
    flex: none;
    align-items: center;
    gap: 6px;
    color: var(--faint);
    font-size: 13px;
    line-height: 20px;
    white-space: nowrap;
  }
  .steps li:not(:last-child) {
    flex: 1 1 auto;
  }
  .steps li:not(:last-child)::after {
    content: '';
    flex: 1;
    height: 1px;
    margin: 0 10px 0 6px;
    background: var(--line);
  }
  .steps li.done {
    color: var(--muted);
  }
  .steps li.done::after {
    background: var(--ink);
  }
  .steps li.now {
    color: var(--ink);
    font-weight: 600;
  }
  .node {
    display: grid;
    flex: none;
    place-items: center;
    width: 16px;
    height: 16px;
    border: 2px solid var(--line);
    border-radius: 50%;
    color: var(--card);
  }
  .done .node {
    border-color: var(--ink);
    background: var(--ink);
  }
  .now .node {
    border: 2px solid var(--ink);
  }
  .now .node::after {
    content: '';
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: var(--ink);
  }

  .activity {
    height: 3px;
    margin: 16px 0 0;
    overflow: hidden;
    border-radius: 2px;
    background: var(--line-soft);
  }
  .activity span {
    display: block;
    width: 30%;
    height: 100%;
    border-radius: inherit;
    background: var(--ink-2);
    animation: scanning 1.4s ease-in-out infinite alternate;
  }
  @keyframes scanning {
    from { transform: translateX(-100%); }
    to { transform: translateX(334%); }
  }
  @media (prefers-reduced-motion: reduce) {
    .activity span { animation: none; transform: translateX(116%); }
  }
  .phase {
    margin: 8px 0 0;
    color: var(--ink-2);
    font-size: 13px;
  }
  .path {
    margin: 4px 0 0;
    overflow: hidden;
    color: var(--muted);
    font-size: 12px;
    line-height: 20px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .foot {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    margin: 14px -24px 0;
    padding: 12px 24px;
    border-top: 1px solid var(--line);
  }
  .stats {
    color: var(--muted);
    font-size: 13px;
  }
  .stats .num {
    color: var(--ink-2);
  }
  .stats i {
    display: inline-block;
    width: 1px;
    height: 12px;
    margin: 0 10px;
    vertical-align: -1px;
    background: var(--line);
  }

  .fail {
    display: flex;
    gap: 14px;
    padding-bottom: 18px;
  }
  .ico {
    display: grid;
    flex: none;
    place-items: center;
    width: 36px;
    height: 36px;
    border-radius: var(--r-ctl);
    background: var(--danger-bg);
    color: var(--danger);
  }
  .why {
    min-width: 0;
  }
  .where {
    margin: 2px 0 0;
    color: var(--muted);
    font-size: 12px;
  }
  .msg {
    margin: 10px 0 0;
    color: var(--ink-2);
    font-size: 13px;
    overflow-wrap: anywhere;
    white-space: pre-wrap;
  }
</style>
