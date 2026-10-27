<script lang="ts">
  import { untrack } from 'svelte';
  import { ScanIssueList, ISSUE_PAGE_SIZE } from '../lib/scanIssues.svelte.ts';
  import type { ScanIssueKind } from '../lib/types.ts';
  import { t, type MsgId } from '../lib/i18n.ts';

  let { scanId }: { scanId: number } = $props();
  const data = new ScanIssueList();
  const kinds: { value: ScanIssueKind; label: MsgId }[] = [
    { value: 'denied', label: '拒绝访问入口' },
    { value: 'link', label: '跳过的链接' },
    { value: 'mountPoint', label: '跳过的挂载点' },
    { value: 'error', label: '读取失败' },
  ];
  let open = $state(false);
  let kind = $state<ScanIssueKind>('denied');
  const label = $derived(t(kinds.find(k => k.value === kind)!.label));
  const page = $derived(data.page);

  $effect(() => {
    const id = scanId, selected = kind, expanded = open;
    untrack(() => { if (expanded) void data.show(id, selected); else data.close(); });
    return () => data.close();
  });
</script>

<details class="issues" bind:open>
  <summary>{t('查看异常位置与原因')}</summary>
  <div class="content" aria-label={t('扫描异常清单')} aria-busy={data.loading}>
    <label>{t('异常种类')}<select bind:value={kind}>{#each kinds as k}<option value={k.value}>{t(k.label)}</option>{/each}</select></label>
    {#if data.loading}
      <p class="message" role="status">{t('正在读取{label}…', { label })}</p>
    {:else if data.error}
      <div class="message" role="alert"><p>{t('清单读取失败：{error}', { error: data.error })}</p><button type="button" class="btn" onclick={() => void data.load()}>{t('重试清单')}</button></div>
    {:else if page}
      <p class="counts" role="status">{t('总计 {total} 条 · 已记录 {recorded} 条', { total: page.total, recorded: page.recorded })}<br />{t('本页返回 {n} 条', { n: page.items.length })}{page.items.length ? t('（第 {from}–{to} 条）', { from: page.offset + 1, to: page.offset + page.items.length }) : ''}</p>
      {#if page.recorded < page.total}<p class="message">{t('记录达到上限：另有 {n} 条仅计数，未保存位置与原因。', { n: page.total - page.recorded })}</p>{/if}
      {#if page.items.length}
        <ol start={page.offset + 1} aria-label={label}>
          {#each page.items as item}<li><code>{item.path}</code><p>{item.reason}</p></li>{/each}
        </ol>
      {:else}<p class="message">{page.total === 0 ? t('本次扫描没有这类记录。') : t('本页没有可展示的记录。')}</p>{/if}
      <p class="scope-note">{page.truncated || page.offset > 0 ? t('当前仅显示部分清单。') : t('当前显示全部已记录清单。')}{kind === 'denied' ? t('拒绝访问入口不代表其内部全部未读目录。') : ''}</p>
      {#if page.offset > 0 || page.offset + page.items.length < page.recorded}
        <div class="paging"><button type="button" class="btn" disabled={page.offset === 0} onclick={() => void data.load(Math.max(0, page.offset - ISSUE_PAGE_SIZE))}>{t('上一页')}</button><span>{t('第 {n} 页', { n: Math.floor(page.offset / ISSUE_PAGE_SIZE) + 1 })}</span><button type="button" class="btn" disabled={!page.items.length || page.offset + page.items.length >= page.recorded} onclick={() => void data.load(page.offset + page.items.length)}>{t('下一页')}</button></div>
      {/if}
    {/if}
  </div>
</details>

<style>
  .issues { margin-top: 14px; border-top: 1px solid var(--line); padding-top: 12px; min-width: 0; }
  .issues > summary { color: var(--workspace-accent); font-size: 12px; cursor: pointer; }
  .content { padding-top: 12px; min-width: 0; }
  label { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; font-size: 12px; color: var(--muted); }
  select { min-width: 0; max-width: 100%; padding: 5px 7px; border: 1px solid var(--line); border-radius: 6px; background: var(--card); }
  .counts, .message, .scope-note { margin: 10px 0; font-size: 11px; line-height: 1.7; color: var(--muted); overflow-wrap: anywhere; }
  .message p { margin: 0 0 8px; }
  ol { margin: 0; padding: 0 0 0 25px; max-height: 240px; overflow-y: auto; font-size: 11px; color: var(--muted); }
  li { padding: 8px 3px 8px 0; border-bottom: 1px solid var(--line); }
  code { display: block; overflow-wrap: anywhere; font: 11px/1.6 var(--font-mono); color: var(--ink-2); }
  li p { margin: 4px 0 0; overflow-wrap: anywhere; }
  .paging { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 6px; font-size: 11px; color: var(--muted); }
  .paging .btn { font-size: 12px; }
</style>
