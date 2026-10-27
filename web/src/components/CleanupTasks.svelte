<script lang="ts">
  // 清理右栏顶上的任务区（dise §9.2）：确认后的删除和清理在这里逐项执行，可查询、可停止后续项目，结果保留到关闭应用。
  import { ChevronDown } from '@lucide/svelte';
  import { cleanupTasks, cleanupTargetCounts, cleanupReleasedSpace, cancelCleanupTasks, refreshCleanupTasks, type CleanupTask } from '../lib/cleanupTasks.svelte.ts';
  import { formatBytes } from '../lib/format.ts';
  import { t, type MsgId } from '../lib/i18n.ts';

  const labels: Record<CleanupTask['status'], MsgId> = {
    pending: '等待提交', queued: '等待执行', running: '正在处理', cancelling: '正在取消',
    completed: '已完成', cancelled: '已取消', failed: '执行失败', external: '已打开外部工具，完成情况未确认', unknown: '状态待确认',
  };
  const batches = $derived(cleanupTasks.items.length ? [cleanupTasks.items, ...cleanupTasks.history] : []);
  const released = $derived(cleanupReleasedSpace(cleanupTasks.items));
  const recycle = $derived(cleanupTasks.items.some((i) => i.request.recycle));
  const totals = $derived.by(() => {
    const items = cleanupTasks.items;
    return {
      total: items.reduce((n, i) => n + cleanupTargetCounts(i).total, 0),
      completed: items.reduce((n, i) => n + (i.progress?.completed ?? 0), 0),
      failed: items.reduce((n, i) => n + (i.progress?.failed ?? 0), 0),
      skipped: items.reduce((n, i) => n + cleanupTargetCounts(i).skipped, 0),
      entries: items.reduce((n, i) => n + (i.progress?.processedEntries ?? 0), 0),
    };
  });
  const unknown = $derived(cleanupTasks.items.some((i) => i.status === 'unknown'));
  const phase = $derived(cleanupTasks.active ? (unknown ? t('状态待确认') : t('进行中')) : t('已结束'));

  // 有任务在跑时总是展开；结束后可以收起，腾地方给建议列表
  let collapsed = $state(false);
  $effect(() => { if (cleanupTasks.active) collapsed = false; });
</script>

{#if batches.length}
  <section class="tasks" aria-label={t('清理任务')}>
    <header>
      <button type="button" class="toggle" aria-expanded={!collapsed} aria-controls="cleanup-task-body" onclick={() => (collapsed = !collapsed)}>
        <ChevronDown size={14} />
        <span>{t('清理任务')}</span>
        <span class="state" class:uncertain={unknown}>{phase}</span>
      </button>
      {#if cleanupTasks.active}
        <div class="actions">
          <button class="btn" type="button" disabled={cleanupTasks.querying} onclick={() => void refreshCleanupTasks()}>{cleanupTasks.querying ? t('正在查询…') : t('查询状态')}</button>
          <button class="btn" type="button" disabled={cleanupTasks.cancelRequested} onclick={() => void cancelCleanupTasks()}>{cleanupTasks.cancelRequested ? t('已请求停止') : t('停止后续项目')}</button>
        </div>
      {/if}
    </header>
    <p class="summary" role="status">{t('确认目标 {total} · 完成 {completed} · 失败 {failed} · 跳过 {skipped}', totals)}<br />
      {t('已处理文件和目录 {n}', { n: totals.entries })} · {released.bytes === null ? t('实际释放量未确认') : t('已核实释放 {size}', { size: formatBytes(released.bytes) }) + (released.partial ? t('（其余项目未确认）') : '')}
    </p>
    {#if !collapsed}
      <div id="cleanup-task-body">
        <p class="hint">{t('停止只影响后续项目，不撤销已完成的操作；当前目录可能要等它处理完。')}{#if recycle}{t('移入回收站的文件要清空回收站后才释放空间。')}{/if}{t('结果保留到关闭应用。')}</p>
        <div class="results">
          {#each batches as batch, index}
            {#if index > 0}<h3>{t('此前任务')}</h3>{/if}
            <ul>
              {#each batch as item (item.operationId)}
                <li>
                  <div class="line"><b>{item.request.title}</b><span class:uncertain={item.status === 'unknown'}>{t(labels[item.status])}</span></div>
                  {#if item.progress?.currentPath}<p class="path mono">{item.progress.currentPath}</p>{/if}
                  {#if item.message}<p>{item.message}</p>{/if}
                  {#if item.progress}
                    <p>{t('已处理文件和目录 {n}', { n: item.progress.processedEntries })} · {item.progress.freedBytes === null ? t('释放量未确认') : t('已核实释放 {size}', { size: formatBytes(item.progress.freedBytes) })}{item.request.recycle ? t('（移入回收站的不算）') : ''}</p>
                  {/if}
                  {#if item.progress?.result?.errors.length}
                    <details><summary>{t('查看失败原因（{n}）', { n: item.progress.result.errors.length })}</summary>
                      {#each item.progress.result.errors as error}<p class="path">{error.path}{t('：')}{error.message}</p>{/each}
                    </details>
                  {/if}
                </li>
              {/each}
            </ul>
          {/each}
        </div>
      </div>
    {/if}
  </section>
{/if}

<style>
  .tasks { flex: none; min-width: 0; border-bottom: 1px solid var(--line); background: var(--page); }
  header, .line, .actions { display: flex; align-items: center; justify-content: space-between; gap: 8px; flex-wrap: wrap; }
  header { padding: 8px 16px 0; }
  .toggle { display: flex; align-items: center; gap: 6px; padding: 4px 0; border: 0; background: none; font-size: 13px; font-weight: 600; color: var(--ink); cursor: pointer; }
  .toggle :global(svg) { transition: transform var(--dur-fast); }
  .toggle[aria-expanded='false'] :global(svg) { transform: rotate(-90deg); }
  .state { font-weight: 400; font-size: 12px; color: var(--muted); }
  .actions .btn { height: 26px; padding: 0 10px; font-size: 12px; }
  .summary, .hint { margin: 6px 16px; line-height: 1.6; font-size: 12px; }
  .hint, li p, .line span { font-size: 12px; color: var(--muted); }
  .results { max-height: 200px; overflow: auto; }
  ul { margin: 0; padding: 0; list-style: none; }
  li { padding: 8px 16px; border-top: 1px solid var(--line-soft); }
  li p { margin: 4px 0 0; }
  .path { overflow-wrap: anywhere; }
  .line b { font-size: 13px; font-weight: 500; overflow-wrap: anywhere; }
  .uncertain { color: var(--review-text) !important; }
  h3 { margin: 10px 16px 4px; font-size: 12px; color: var(--muted); }
  details { margin-top: 4px; font-size: 12px; }
  @media (prefers-reduced-motion: reduce) { .toggle :global(svg) { transition: none; } }
</style>
