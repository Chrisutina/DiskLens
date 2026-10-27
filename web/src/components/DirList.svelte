<script lang="ts">
  // 和地图同步的目录列表（dise §5.2）：当前地图目录的全部直接子项（地图并成「其余 N 项」的也在），
  // 共享选中项和悬停；按名称、大小、修改时间排序；键盘上下移动、回车进入目录或打开文件详情——地图的等价入口。
  import { File, Folder } from '@lucide/svelte';
  import { app, loadList, select, setHover, setListSort, showDetails, zoomTo } from '../lib/state.svelte.ts';
  import { formatBytes, formatDate } from '../lib/format.ts';
  import { purpose } from '../lib/purposes.ts';
  import type { ChildEntry, ChildSort } from '../lib/types.ts';
  import { t, type MsgId } from '../lib/i18n.ts';

  const COLS: { sort: ChildSort; label: MsgId }[] = [
    { sort: 'name', label: '名称' },
    { sort: 'size', label: '大小' },
    { sort: 'modified', label: '修改时间' },
  ];
  const list = $derived(app.list);
  const items = $derived(list?.items ?? []);
  const max = $derived(Math.max(1, ...items.map((i) => i.value)));
  let rows = $state<HTMLDivElement>();

  // 选中项在列表里就滚到看得见（选中来自地图、搜索定位时）
  $effect(() => {
    const id = app.selectedId;
    void items;
    queueMicrotask(() => rows?.querySelector<HTMLElement>(`[data-id="${id}"]`)?.scrollIntoView({ block: 'nearest' }));
  });

  // 单击选中；第二下（detail === 2）对目录是进入——不用 dblclick，单击后列表会重绘，第二下可能落在别的行上
  let last: ChildEntry | null = null;
  function pick(e: MouseEvent, it: ChildEntry): void {
    if (e.detail === 2 && last?.id === it.id && it.isDir) {
      void zoomTo(it.id);
      return;
    }
    last = it;
    select(it.id);
  }

  function open(it: ChildEntry): void {
    if (it.isDir) void zoomTo(it.id);
    else showDetails(it.id);
  }

  /** 单焦点：上下、Home/End 移动，回车进入；只有当前行在 Tab 顺序里 */
  function onkeydown(e: KeyboardEvent): void {
    const buttons = [...(rows?.querySelectorAll<HTMLButtonElement>('button.row') ?? [])];
    const at = buttons.indexOf(document.activeElement as HTMLButtonElement);
    if (at < 0) return;
    const to = e.key === 'ArrowDown' ? at + 1 : e.key === 'ArrowUp' ? at - 1 : e.key === 'Home' ? 0 : e.key === 'End' ? buttons.length - 1 : -1;
    if (to >= 0 && to < buttons.length) {
      e.preventDefault();
      buttons[to].focus();
      select(items[to].id);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      open(items[at]);
    }
  }

  const sortName = (s: ChildSort): string => t(COLS.find((c) => c.sort === s)!.label);
  const focusIndex = $derived(Math.max(0, items.findIndex((i) => i.id === app.selectedId)));
</script>

<section class="dirlist" aria-label={t('目录列表')} aria-busy={app.listLoading}>
  <div class="cols" role="group" aria-label={t('排序')}>
    {#each COLS as c (c.sort)}
      <button type="button" class="col {c.sort}" aria-pressed={app.listSort === c.sort} title={t('按{label}排序', { label: t(c.label) })} onclick={() => setListSort(c.sort)}>
        {t(c.label)}{#if app.listSort === c.sort}<span aria-hidden="true"> {c.sort === 'name' ? '↑' : '↓'}</span>{/if}
      </button>
    {/each}
  </div>
  <!-- svelte-ignore a11y_interactive_supports_focus -->
  <div class="rows" bind:this={rows} role="listbox" aria-label={t('当前目录的子项：上下键移动，回车进入目录或查看文件')} {onkeydown}>
    {#if app.listError}
      <div class="msg" role="alert"><p>{t('列表读取失败：{error}', { error: app.listError })}</p><button type="button" class="btn" onclick={() => void loadList()}>{t('重试')}</button></div>
    {:else if !list && app.listLoading}
      <p class="msg" role="status">{t('正在读取目录列表…')}</p>
    {:else if list && !items.length}
      <p class="msg">{t('这个目录是空的。')}</p>
    {/if}
    {#each items as it, i (it.id)}
      <button
        type="button"
        class="row"
        role="option"
        data-id={it.id}
        aria-selected={it.id === app.selectedId}
        tabindex={i === focusIndex ? 0 : -1}
        title={it.name}
        onclick={(e) => pick(e, it)}
        onmouseenter={() => setHover(it.id)}
        onmouseleave={() => setHover(-1)}
        onfocus={() => setHover(it.id)}
        onblur={() => setHover(-1)}
      >
        <span class="dot" style:background={purpose(it.u).dot} aria-hidden="true"></span>
        <span class="icon" aria-hidden="true">{#if it.isDir}<Folder size={13} />{:else}<File size={13} />{/if}</span>
        <span class="name">{it.name}</span>
        <span class="val num">{formatBytes(it.value)}</span>
        <span class="mod num">{it.modifiedMs ? formatDate(it.modifiedMs).slice(0, 10) : '—'}</span>
        <i class="bar" style:width="{(it.value / max) * 100}%" style:background={purpose(it.u).dot}></i>
      </button>
    {/each}
    {#if list && items.length < list.total}
      <button type="button" class="btn more" disabled={app.listLoading} onclick={() => void loadList(true)}>{app.listLoading ? t('正在读取…') : t('再读 {n} 项（还有 {left} 项）', { n: Math.min(200, list.total - items.length), left: list.total - items.length })}</button>
    {/if}
  </div>
  {#if list}<p class="foot">{t('{n} 项 · 按{sort}排 · 大小按{metric}，和地图同口径', { n: list.total, sort: sortName(list.sort), metric: app.metric === 'alloc' ? t('占用空间') : t('文件大小') })}</p>{/if}
</section>

<style>
  .dirlist { display: flex; flex-direction: column; min-width: 0; min-height: 0; border-right: 1px solid var(--line); }
  .cols { display: grid; grid-template-columns: minmax(0, 1fr) 76px 84px; gap: 4px; padding: 6px 10px 4px 28px; border-bottom: 1px solid var(--line-soft); }
  .col { padding: 2px 4px; border: 0; border-radius: 4px; background: none; color: var(--muted); font-size: 11px; text-align: left; cursor: pointer; }
  .col.size, .col.modified { text-align: right; }
  .col[aria-pressed='true'] { color: var(--ink); font-weight: 600; }
  .col:hover { background: var(--hover); }
  .rows { flex: 1; min-height: 0; overflow-y: auto; padding: 2px 0 8px; }
  .row { position: relative; display: grid; grid-template-columns: 8px 16px minmax(0, 1fr) 72px 80px; align-items: center; gap: 4px; width: 100%; padding: 5px 10px 7px; border: 0; background: none; text-align: left; font-size: 12px; cursor: pointer; }
  .row:hover { background: var(--hover); }
  .row[aria-selected='true'] { background: color-mix(in srgb, var(--workspace-accent, #3f6fb3) 12%, transparent); }
  .row:focus-visible { outline: 2px solid var(--workspace-accent, #3f6fb3); outline-offset: -2px; }
  .dot { width: 7px; height: 7px; border-radius: 2px; }
  .icon { display: inline-flex; color: var(--muted); }
  .name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .val, .mod { color: var(--muted); text-align: right; white-space: nowrap; }
  .bar { position: absolute; left: 28px; bottom: 2px; height: 2px; max-width: calc(100% - 38px); border-radius: 1px; opacity: .55; }
  .msg { margin: 0; padding: 14px; color: var(--muted); font-size: 12px; }
  .msg p { margin: 0 0 8px; }
  .more { margin: 8px 10px 0; width: calc(100% - 20px); font-size: 12px; }
  .foot { margin: 0; padding: 6px 10px; border-top: 1px solid var(--line-soft); color: var(--muted); font-size: 11px; }
</style>
