<script lang="ts">
  import { app, openPanel, setPage } from '../lib/state.svelte.ts';
  import { cleanupTasks } from '../lib/cleanupTasks.svelte.ts';
  import { formatBytes } from '../lib/format.ts';
  import { t } from '../lib/i18n.ts';
  const s = $derived(app.summary);
  const outside = $derived(!s ? 0 : s.isVolumeRoot ? s.unaccountedBytes : Math.max(0, s.volumeUsed - s.scannedAlloc));
  // 清理格同时是任务区的入口：任务在跑或待确认时显示进度，结束后提示结果还能再看
  const task = $derived.by(() => {
    const items = cleanupTasks.items;
    if (!items.length) return null;
    const ended = items.filter((i) => ['completed', 'cancelled', 'failed', 'external'].includes(i.status)).length;
    if (!cleanupTasks.active) return { busy: false, title: '', note: t('上次清理任务已结束，结果在清理面板') };
    return { busy: true, title: items.some((i) => i.status === 'unknown') ? t('状态待确认') : t('任务进行中'), note: t('{ended} / {total} 项已结束 · 打开查看或停止', { ended, total: items.length }) };
  });
</script>

<section class="summary" aria-label={t('空间概览')}>
  <div class="cell"><span>{t('已统计占用')}</span><strong>{s ? formatBytes(s.scannedAlloc) : '—'}</strong><small>{s ? t('{files} 个文件 · {dirs} 个目录', { files: s.files, dirs: s.dirs }) : t('等待扫描结果')}</small></div>
  <div class="cell"><span>{t('磁盘可用')}</span><strong>{s ? formatBytes(s.volumeFree) : '—'}</strong><small>{s ? t('所在卷共 {size}', { size: formatBytes(s.volumeTotal) }) : '—'}</small></div>
  <button type="button" class="cell cleanup" onclick={() => { setPage('browse'); openPanel('cleanup'); }}>
    <span>{t('清理建议')} <span aria-hidden="true">↗</span></span>
    {#if task?.busy}
      <strong class="busy">{task.title}</strong><small>{task.note}</small>
    {:else}
      <strong>{app.suggestionsLoading ? t('分析中…') : app.suggestionsError ? t('读取失败@@加载') : t('{n} 项', { n: app.cleanupStats.safe.count + app.cleanupStats.review.count })}</strong><small>{task?.note ?? (app.suggestionsLoading ? t('等待本次分析') : app.suggestionsError ? t('打开面板重试') : t('先确认项目，释放量以执行结果为准'))}</small>
    {/if}
  </button>
  <div class="cell"><span>{s?.isVolumeRoot ? t('未解释空间') : t('扫描范围外')}</span><strong>{s ? formatBytes(outside) : '—'}</strong><small>{s?.isVolumeRoot ? t('卷已用与统计量差额，原因见扫描详情') : t('所在卷已用空间减去本次统计量')}</small></div>
</section>

<style>
  .summary { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); border: 1px solid var(--line); border-radius: 12px; background: var(--card); }
  .cell { min-width: 0; display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: baseline; gap: 3px 12px; padding: 13px 17px; border: 0; border-right: 1px solid var(--line); background: transparent; text-align: left; }
  .cell:last-child { border-right: 0; }
  .cell > span { color: var(--muted); font-size: 12px; }
  strong { font-size: 20px; font-weight: 600; letter-spacing: -.5px; font-variant-numeric: tabular-nums; white-space: nowrap; }
  small { grid-column: 1/-1; font-size: 11px; color: var(--muted); overflow-wrap: anywhere; }
  .cleanup { cursor: pointer; }
  .cleanup:hover { background: var(--hover); }
  .cleanup > span { color: var(--workspace-accent); }
  .busy { font-size: 16px; color: var(--workspace-accent); }
  @media (max-width: 1100px) { .cell { padding: 11px 12px; gap: 4px; } strong { font-size: 18px; } }
  @media (max-width: 800px) { .summary { grid-template-columns: repeat(2, minmax(0, 1fr)); } .cell:nth-child(2) { border-right: 0; } .cell:nth-child(-n+2) { border-bottom: 1px solid var(--line); } }
  @media (max-width: 420px) { .cell { grid-template-columns: minmax(0, 1fr); } }
</style>
