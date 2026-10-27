<script lang="ts">
  // 清理建议的一行（参考图右栏）：复选框 + 标题/位置 + 大小 + ›；展开后是说明、条目、命令。
  import { ChevronRight, Copy, Lock, ShieldAlert } from '@lucide/svelte';
  import {
    app,
    copyText,
    isSelectable,
    reveal,
    requestSuggestion,
    toggleSuggestion,
    toggleSuggestionItem,
  } from '../lib/state.svelte.ts';
  import { formatBytes } from '../lib/format.ts';
  import { cleanupSpaceLabel } from '../lib/cleanup.ts';
  import type { Suggestion } from '../lib/types.ts';
  import { t } from '../lib/i18n.ts';
  import RightPanelPath from './RightPanelPath.svelte';

  let { s }: { s: Suggestion } = $props();

  let open = $state(false);

  const selectable = $derived(isSelectable(s));
  const pick = $derived(app.cleanupSel[s.id]);
  const on = $derived(pick?.on === true);
  // 只勾了一部分条目时复选框显示「半选」
  const partial = $derived(pick?.on === true && pick.items !== null && pick.items.length < s.nodeIds.length);
  const nodeSet = $derived(new Set(s.nodeIds));
  const chosen = $derived(new Set<number>(on ? (pick?.items ?? s.nodeIds) : []));
  const path = $derived(s.displayPath ?? s.items[0]?.path ?? '');
  const needAdmin = $derived(s.requiresAdmin && app.info?.isAdmin !== true);
  const canRun = $derived(s.action === 'command' || s.action === 'openSettings');

  // 勾上/取消整条。点半选框 = 全部取消，和浏览器自己的切换一致；
  // 之前留着一部分勾选再点，要补全，不然 toggleSuggestion 会带着旧的子集重新勾上。
  function toggleMain(): void {
    if (on) toggleSuggestion(s.id, false);
    else if (pick?.items) for (const id of s.nodeIds) toggleSuggestionItem(s.id, id, true);
    else toggleSuggestion(s.id, true);
  }
</script>

<div class="row" class:open>
  {#if selectable}
    <input class="check" type="checkbox" checked={on} indeterminate={partial} aria-label={t('选中 {name}', { name: s.title })} onchange={toggleMain} />
  {:else}
    <span class="lead" title={t('只能用正确的方法处理，不会进清理脚本')}><Lock size={14} aria-hidden="true" /></span>
  {/if}
  <button type="button" class="main" aria-expanded={open} onclick={() => (open = !open)}>
    <span class="text">
      <span class="title">{s.title}</span>
      <span class="path mono" title={path}>{path}</span>
    </span>
    <span class="size num">{formatBytes(s.bytes)}</span>
    <span class="chev"><ChevronRight size={16} strokeWidth={1.75} aria-hidden="true" /></span>
  </button>
</div>

{#if open}
  <div class="more">
    <p class="desc">{s.description}</p>
    <p class="space">{cleanupSpaceLabel(s)}</p>
    {#if needAdmin}
      <p class="admin"><ShieldAlert size={14} aria-hidden="true" />{t('需要管理员（当前是标准权限）')}</p>
    {/if}
    {#if s.items.length}
      <ul class="items">
        {#each s.items as it}
          <li class="item">
            {#if selectable && s.selectable && nodeSet.has(it.id)}
              <input class="check" type="checkbox" checked={chosen.has(it.id)} aria-label={t('选中 {name}', { name: it.name })} onchange={() => toggleSuggestionItem(s.id, it.id)} />
            {/if}
            <button type="button" class="iname" title={t('定位到资源地图并查看详情')} onclick={() => reveal(it.id)}>
              <span class="in">{it.name}</span>
              <span class="isize num">{formatBytes(it.alloc)}</span>
              <span class="ip"><RightPanelPath path={it.path} /></span>
            </button>
          </li>
        {/each}
      </ul>
    {/if}
    {#if s.command}
      <code class="cmd mono">{s.command}</code>
    {/if}
    {#if s.command || canRun}
      <div class="acts">
        {#if s.command}
          <button type="button" class="btn" onclick={() => copyText(s.command ?? '', t('命令'))}><Copy size={14} aria-hidden="true" />{t('复制命令')}</button>
        {/if}
        {#if canRun}
          <button type="button" class="btn" onclick={() => requestSuggestion(s)}>{s.action === 'command' ? t('在终端运行') : t('打开设置')}</button>
        {/if}
      </div>
    {/if}
  </div>
{/if}

<style>
  .row {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 0 16px;
    border-bottom: 1px solid var(--line-soft);
    transition: background var(--dur-fast);
  }
  .row:hover {
    background: var(--hover);
  }
  /* 展开时和下面的内容连成一块，不要中间一条线 */
  .row.open {
    border-bottom-color: transparent;
  }
  .row > .check {
    flex: none;
  }
  .lead {
    display: grid;
    flex: none;
    place-items: center;
    width: 18px;
    height: 18px;
    color: var(--faint);
  }
  /* 59px + 1px 分隔线 = 参考图的 60px 行距 */
  .main {
    display: flex;
    flex: 1;
    align-items: center;
    gap: 12px;
    min-width: 0;
    height: 59px;
    padding: 0;
    border: 0;
    background: none;
    text-align: left;
    cursor: pointer;
  }
  .main:focus-visible {
    outline-offset: -2px;
  }
  .text {
    display: flex;
    flex: 1;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
  }
  .title {
    overflow: hidden;
    font-size: 14px;
    font-weight: 500;
    line-height: 20px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .path {
    overflow: hidden;
    color: var(--muted);
    font-size: 12px;
    line-height: 16px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .size {
    flex: none;
    font-size: 14px;
    font-weight: 600;
  }
  .chev {
    display: grid;
    flex: none;
    color: var(--faint);
    transition:
      transform var(--dur),
      color var(--dur-fast);
  }
  .main:hover .chev {
    color: var(--ink-2);
  }
  .open .chev {
    transform: rotate(90deg);
  }

  .more {
    display: flex;
    flex-direction: column;
    gap: 10px;
    padding: 0 16px 14px 46px;
    border-bottom: 1px solid var(--line-soft);
  }
  .desc {
    margin: 0;
    color: var(--ink-2);
    font-size: 13px;
    line-height: 20px;
  }
  .admin {
    display: flex;
    align-items: center;
    gap: 6px;
    margin: 0;
    color: var(--review-text);
    font-size: 12px;
    font-weight: 500;
  }
  .space {
    margin: 0;
    color: var(--muted);
    font-size: 12px;
    line-height: 18px;
  }
  .items {
    max-height: 216px;
    margin: 0;
    padding: 0;
    overflow-y: auto;
    border: 1px solid var(--line);
    border-radius: var(--r-ctl);
    list-style: none;
  }
  .item {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 6px 10px;
    border-bottom: 1px solid var(--line-soft);
  }
  .item:last-child {
    border-bottom: 0;
  }
  .item > .check {
    flex: none;
  }
  /* 第一行名字 + 大小，第二行位置占满整行宽度 */
  .iname {
    display: grid;
    flex: 1;
    grid-template-columns: minmax(0, 1fr) auto;
    column-gap: 10px;
    row-gap: 1px;
    min-width: 0;
    padding: 0;
    border: 0;
    background: none;
    text-align: left;
    cursor: pointer;
  }
  .in {
    overflow: hidden;
    font-size: 13px;
    font-weight: 500;
    line-height: 18px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .iname:hover .in {
    text-decoration: underline;
  }
  .isize {
    color: var(--muted);
    font-size: 12px;
    line-height: 18px;
  }
  .ip {
    display: flex;
    grid-column: 1 / -1;
    min-width: 0;
  }
  .cmd {
    display: block;
    padding: 8px 10px;
    border-radius: var(--r-ctl);
    background: var(--page);
    font-size: 12px;
    line-height: 18px;
    overflow-wrap: anywhere;
  }
  .acts {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }
</style>
