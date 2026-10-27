<script lang="ts">
  import { CircleAlert, CircleHelp, LoaderCircle, Search } from '@lucide/svelte';
  import { highlightParts, plainWords } from './SearchHighlight.ts';
  import { app, closeSearch, openMenu, revealSearchResult, setSearchQuery, setSearchScope, setSearchSort, submitWebSearch, type SearchScope } from '../lib/state.svelte.ts';
  import { formatBytes, middleEllipsis } from '../lib/format.ts';
  import { purpose } from '../lib/purposes.ts';
  import { Flag, hasFlag, type FileItem } from '../lib/types.ts';
  import { t } from '../lib/i18n.ts';

  let box: HTMLDivElement;
  let list = $state<HTMLElement>();
  let focused = $state(false);
  let active = $state(0);

  const query = $derived(app.searchQuery);
  const open = $derived(app.searchOpen && query.trim().length > 0);
  const result = $derived(app.searchResult?.query === query ? app.searchResult : null);
  const items = $derived(result?.items ?? []);
  const words = $derived(plainWords(query));
  const web = $derived(app.searchScope !== 'files');
  const engineName = $derived(app.searchScope === 'google' ? 'Google' : 'Bing');

  // 换了一批结果就回到第一行
  $effect(() => {
    void result;
    active = 0;
  });
  $effect(() => {
    list?.querySelector(`[data-i="${active}"]`)?.scrollIntoView({ block: 'nearest' });
  });

  // 点输入框、Tab 进来、按方向键，都把收起的下拉重新打开
  function reopen() {
    if (!open && query.trim()) setSearchQuery(query);
  }

  function onkeydown(e: KeyboardEvent) {
    if (e.isComposing) return; // 拼音输入法的回车是选字，不是定位
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (!open) return reopen();
      if (items.length) active = (active + (e.key === 'ArrowDown' ? 1 : items.length - 1)) % items.length;
    } else if (e.key === 'Enter' && web) {
      // 外部搜索：回车才提交，打开浏览器
      e.preventDefault();
      void submitWebSearch();
    } else if (e.key === 'Enter') {
      const it = items[active];
      if (open && it) {
        e.preventDefault();
        void revealSearchResult(it.id);
      }
    } else if (e.key === 'Escape') {
      e.stopPropagation(); // 不然全局的 Esc 会接着处理一遍
      if (open) closeSearch();
      else (e.currentTarget as HTMLElement).blur();
    }
  }

  function rowMenu(e: MouseEvent, it: FileItem) {
    e.preventDefault();
    openMenu({ x: e.clientX, y: e.clientY, nodeId: it.id, name: it.name, isDir: it.isDir || hasFlag(it.f, Flag.Directory), flags: it.f });
  }
</script>

<svelte:window onpointerdown={(e) => open && !box.contains(e.target as Node) && closeSearch()} />

<div class="search" bind:this={box}>
  <div class="field" class:focused>
    <Search size={16} class="lead" aria-hidden="true" />
    <!-- 搜索范围：默认本地文件；选 Google / Bing 时只在回车后打开浏览器，不随打字发出任何内容 -->
    <select class="scope" aria-label={t('搜索范围')} value={app.searchScope} onchange={(e) => setSearchScope(e.currentTarget.value as SearchScope)}>
      <option value="files">{t('文件')}</option>
      <option value="google">Google</option>
      <option value="bing">Bing</option>
    </select>
    <input
      id="search-input"
      type="text"
      role="combobox"
      placeholder={web ? t('用 {engine} 搜索，回车在浏览器打开…', { engine: engineName }) : t('搜索文件和文件夹…')}
      autocomplete="off"
      spellcheck="false"
      aria-label={web ? t('用 {engine} 搜索，回车在浏览器打开', { engine: engineName }) : t('搜索文件和文件夹')}
      aria-expanded={open}
      aria-controls="search-list"
      aria-activedescendant={open && items.length ? `sr-${active}` : undefined}
      value={query}
      oninput={(e) => setSearchQuery(e.currentTarget.value)}
      onfocus={() => {
        focused = true;
        reopen();
      }}
      onblur={() => (focused = false)}
      onclick={reopen}
      {onkeydown}
    />
    {#if !query && !focused}<span class="kbd">Ctrl F</span>{/if}
    <span class="help" hidden={web}>
      <button type="button" class="qm" aria-label={t('搜索语法')} aria-describedby="search-syntax"><CircleHelp size={16} /></button>
      <div class="float syntax" id="search-syntax" role="tooltip">
        <dl>
          <dt><code>foo bar</code></dt>
          <dd>{t('空格分隔的词要同时满足；带空格的词用双引号括起来')} <code>"…"</code></dd>
          <dt><code>foo | bar</code></dt>
          <dd>{t('满足任一组就算；每组里照旧要同时满足')}</dd>
          <dt><code>-foo</code></dt>
          <dd>{t('名字不包含 foo')}</dd>
          <dt><code>wd</code></dt>
          <dd>{t('拼音首字母，找得到「文档」')}</dd>
          <dt><code>*.log</code> <code>img_??.png</code></dt>
          <dd>{t('通配符，匹配整个名字')}</dd>
          <dt><code>a\b</code></dt>
          <dd>{t('含反斜杠的词匹配完整路径')}</dd>
          <dt><code>ext:mp4,mkv</code></dt>
          <dd>{t('按扩展名')}</dd>
          <dt><code>size:&gt;1gb</code> <code>size:100mb-2gb</code></dt>
          <dd>{t('按当前度量过滤大小，单位 b、k、m、g、t')}</dd>
          <dt><code>type:file</code> <code>type:dir</code></dt>
          <dd>{t('只看文件或只看文件夹')}</dd>
        </dl>
        <p>{t('只有过滤词也可以，比如')} <code>ext:iso size:&gt;1gb</code>{t('。一条都对不上时，会退一步给出近似结果（dwnld 找到 download）。')}</p>
      </div>
    </span>
  </div>

  {#if open}
    <div class="float drop">
      {#if !result}
        <p class="note"><LoaderCircle size={14} class="spin" />{t('正在搜索…')}</p>
      {:else if result.error}
        <p class="note bad"><CircleAlert size={14} />{result.error}</p>
      {:else if result.total === 0}
        <p class="note">{t('没有找到匹配项。试试')} <code>ext:mp4</code> / <code>size:&gt;1gb</code></p>
      {:else}
        <div class="head num">
          <span>{t('{n} 项', { n: result.total })} · {result.tookMs} ms{result.truncated ? t(' · 只显示前 {n} 项', { n: items.length }) : ''}</span>
          {#if app.searchLoading}<LoaderCircle size={13} class="spin" />{/if}
          {#if !result.fuzzy}
            <span class="sort" role="group" aria-label={t('排序')}>
              <button type="button" aria-pressed={app.searchSort === 'relevance'} onclick={() => setSearchSort('relevance')}>{t('相关度')}</button>
              <button type="button" aria-pressed={app.searchSort === 'size'} onclick={() => setSearchSort('size')}>{t('占用')}</button>
            </span>
          {/if}
        </div>
        {#if result.fuzzy}<p class="note fuzzy">{t('没有完全对上的，下面是近似结果（字符按顺序出现），按相似度排。')}</p>{/if}
        <div class="list" id="search-list" role="listbox" aria-label={t('搜索结果')} bind:this={list}>
          {#each items as it, i (it.id)}
            <button
              type="button"
              role="option"
              class="row"
              class:active={i === active}
              id="sr-{i}"
              data-i={i}
              tabindex="-1"
              aria-selected={i === active}
              onclick={() => void revealSearchResult(it.id)}
              onpointermove={() => (active = i)}
              oncontextmenu={(e) => rowMenu(e, it)}
            >
              <span class="dot" style:background={purpose(it.u).dot}></span>
              <span class="name">
                {#each highlightParts(it.name, words) as p}{#if p.hit}<mark>{p.text}</mark>{:else}{p.text}{/if}{/each}
              </span>
              <span class="size num">{formatBytes(app.metric === 'alloc' ? it.alloc : it.size)}</span>
              <span class="path mono" title={it.path}>{middleEllipsis(it.path, 72)}</span>
            </button>
          {/each}
        </div>
      {/if}
    </div>
  {/if}
</div>

<style>
  .search {
    position: relative;
    flex: 0 1 420px;
    min-width: 0;
    width: 100%;
  }
  .field {
    display: flex;
    align-items: center;
    gap: 8px;
    height: 36px;
    padding: 0 8px 0 10px;
    border: 1px solid var(--line);
    border-radius: var(--r-ctl);
    background: var(--card);
    transition: border-color var(--dur-fast);
  }
  .field:hover {
    border-color: #d4d4d8;
  }
  /* 搜索范围：紧贴放大镜的一个原生下拉，右边一道分隔线 */
  .scope {
    flex: none;
    height: 24px;
    padding: 0 4px 0 2px;
    border: 0;
    border-right: 1px solid var(--line);
    border-radius: 0;
    background: transparent;
    color: var(--ink-2);
    font-size: 12px;
    cursor: pointer;
  }
  .scope:focus-visible {
    outline: 2px solid var(--workspace-accent, var(--ink));
    outline-offset: 1px;
  }
  /* 焦点环画在整个输入框上，里面的 input 自己不画 */
  .field.focused {
    border-color: var(--ink);
    outline: 2px solid var(--ink);
    outline-offset: 2px;
  }
  .field :global(.lead) {
    flex: none;
    color: var(--faint);
  }
  input {
    flex: 1;
    min-width: 0;
    height: 100%;
    padding: 0;
    border: 0;
    outline: 0;
    background: transparent;
    font-size: 13px;
  }
  input::placeholder {
    color: var(--faint);
  }
  .qm {
    display: grid;
    place-items: center;
    width: 24px;
    height: 24px;
    padding: 0;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--faint);
    cursor: help;
  }
  .qm:hover {
    color: var(--ink-2);
  }

  .help {
    position: relative;
    display: inline-flex;
  }
  .syntax {
    position: absolute;
    top: calc(100% + 14px);
    right: -8px;
    z-index: 31;
    display: none;
    width: min(400px, calc(100vw - 40px));
    padding: 14px 16px;
    font-size: 12px;
  }
  .help:hover .syntax,
  .help:focus-within .syntax {
    display: block;
  }
  dl {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 8px 14px;
    margin: 0;
  }
  dt {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
  }
  dd {
    margin: 0;
    color: var(--muted);
  }
  .syntax p {
    margin: 12px 0 0;
    color: var(--muted);
  }
  code {
    padding: 1px 5px;
    border-radius: 4px;
    background: var(--page);
    color: var(--ink);
    font: 12px var(--font-mono);
    white-space: nowrap;
  }

  .drop {
    position: absolute;
    top: calc(100% + 8px);
    left: 0;
    z-index: 30;
    display: flex;
    flex-direction: column;
    width: 100%;
    max-width: calc(100vw - 32px);
    max-height: min(520px, calc(100vh - 96px));
    overflow: hidden;
  }
  .note {
    display: flex;
    align-items: center;
    gap: 8px;
    margin: 0;
    padding: 16px;
    color: var(--muted);
  }
  .note.bad {
    background: var(--danger-bg);
    color: var(--danger);
  }
  .note :global(svg) {
    flex: none;
  }
  .head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 10px 16px;
    border-bottom: 1px solid var(--line-soft);
    color: var(--muted);
    font-size: 12px;
  }
  .sort {
    display: inline-flex;
    gap: 2px;
    margin-left: auto;
  }
  .sort button {
    padding: 2px 8px;
    border: 0;
    border-radius: var(--r-ctl);
    background: none;
    color: var(--muted);
    font-size: 12px;
    cursor: pointer;
  }
  .sort button[aria-pressed='true'] {
    background: var(--hover);
    color: var(--ink);
    font-weight: 600;
  }
  .note.fuzzy {
    padding: 8px 16px;
    border-bottom: 1px solid var(--line-soft);
    font-size: 12px;
  }
  .list {
    flex: 1;
    min-height: 0;
    padding: 4px;
    overflow-y: auto;
  }
  .row {
    display: grid;
    grid-template-columns: 8px minmax(0, 1fr) auto;
    column-gap: 10px;
    align-items: center;
    width: 100%;
    padding: 7px 12px;
    border: 0;
    border-radius: 8px;
    background: transparent;
    text-align: left;
    cursor: pointer;
  }
  .row.active {
    background: var(--hover);
  }
  .name {
    overflow: hidden;
    font-size: 13px;
    font-weight: 500;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  mark {
    border-radius: 3px;
    background: var(--review-bg);
    box-shadow: inset 0 -2px 0 var(--review-line);
    color: inherit;
  }
  .size {
    grid-row: span 2;
    grid-column: 3;
    font-size: 13px;
    font-weight: 600;
  }
  .path {
    grid-column: 2;
    overflow: hidden;
    color: var(--muted);
    font-size: 12px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .row :global(.dot) {
    align-self: start;
    margin-top: 6px;
  }

  .note :global(.spin),
  .head :global(.spin) {
    animation: spin 1s linear infinite;
  }
  @keyframes spin {
    to {
      transform: rotate(360deg);
    }
  }
</style>
