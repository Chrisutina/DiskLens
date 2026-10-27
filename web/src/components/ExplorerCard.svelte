<script lang="ts">
  import { ChevronLeft, ChevronRight, List } from '@lucide/svelte';
  import Treemap from './Treemap.svelte';
  import RelatedTree from './RelatedTree.svelte';
  import DirList from './DirList.svelte';
  import OwnerView from './OwnerView.svelte';
  import { relatedTree } from '../lib/related.svelte.ts';
  import { breadcrumbs, collapseCrumbs } from './ExplorerCrumbs.ts';
  import { app, back, closePanel, loadView, openPanel, setDimension, setLegendFilter, setLegendHover, toggleList, viewTrail, zoomTo, type RightTab } from '../lib/state.svelte.ts';
  import { cleanupTasks } from '../lib/cleanupTasks.svelte.ts';
  import { formatBytes, formatPercent } from '../lib/format.ts';
  import { t } from '../lib/i18n.ts';
  import { CLEANUP_COLORS, PURPOSE_ORDER, purpose } from '../lib/purposes.ts';
  import { GROUP_NONE, GROUP_PROTECTED, GROUP_REVIEW, GROUP_SAFE } from '../lib/treemap/marks.ts';

  const crumbs = $derived(collapseCrumbs(breadcrumbs(viewTrail(), app.info?.homePath ?? null)));

  // 图例只列当前视图里真的出现过的用途；视图根本身不画，不算。键：类别模式是用途 U，清理模式是清理组
  const legend = $derived.by(() => {
    const seen = new Set<number>();
    for (const n of app.view?.nodes ?? []) if (n.p >= 0) seen.add(n.u);
    return PURPOSE_ORDER.filter((u) => seen.has(u)).map((u) => ({ key: u, name: purpose(u).name, fill: purpose(u).dot, line: purpose(u).dot }));
  });

  const cleanupLegend = $derived([
    { key: GROUP_SAFE, name: t('可放心清理'), ...CLEANUP_COLORS.safe },
    { key: GROUP_REVIEW, name: t('先确认'), ...CLEANUP_COLORS.review },
    { key: GROUP_PROTECTED, name: t('别删'), ...CLEANUP_COLORS.protected },
    { key: GROUP_NONE, name: t('不涉及清理'), ...CLEANUP_COLORS.none },
  ]);
  const entries = $derived(app.explorerMode === 'cleanup' ? cleanupLegend : legend);
  const filtered = $derived(app.legendFilter === null ? null : entries.find((e) => e.key === app.legendFilter) ?? null);

  // 筛选时说明统计范围和分母（dise §7.4）：只是当前目录里的这一类，不是整个磁盘
  const share = $derived.by(() => {
    if (!filtered || app.explorerMode === 'cleanup') return null;
    const bytes = app.rootDetails?.purposeBytes?.[filtered.key];
    const total = app.view?.total ?? 0;
    return bytes == null ? null : { bytes, total, frac: total > 0 ? bytes / total : 0 };
  });
  const here = $derived(viewTrail().at(-1)?.name ?? '');

  // 右侧面板的开关：按下的那个再点一次就关上
  const shown = (tab: RightTab) => app.panelOpen && app.rightTab === tab;
  const toggle = (tab: RightTab) => (shown(tab) ? closePanel() : openPanel(tab));

</script>

<section class="card explorer" aria-label={t('资源地图')}>
  <header class="head">
    <div class="titles">
      <h2 class="card-title">{t('资源地图')}</h2>
      <p class="card-sub">{t('按{metric}排列，点击方块进入。', { metric: app.metric === 'alloc' ? t('占用空间') : t('文件大小') })}</p>
    </div>
    <div class="seg" role="group" aria-label={t('右侧面板')}>
      <button type="button" aria-pressed={shown('details')} title={t('选中项的详情')} onclick={() => toggle('details')}>{t('详情')}</button>
      <button type="button" aria-pressed={shown('largest')} title={t('当前位置下最大的 100 个文件')} onclick={() => toggle('largest')}>{t('大文件')}</button>
      <button type="button" aria-pressed={shown('cleanup')} title={cleanupTasks.active ? t('清理建议与任务（任务未结束）') : t('当前扫描范围的清理建议')} onclick={() => toggle('cleanup')}>{t('清理')}{#if cleanupTasks.active}<i class="busy" aria-hidden="true"></i>{/if}</button>
    </div>
  </header>

  <nav class="crumbs" aria-label={t('当前位置')}>
    <button type="button" class="btn btn-ghost btn-icon back" aria-label={t('后退')} title={t('后退（Backspace）')} disabled={!app.backStack.length} onclick={() => void back()}>
      <ChevronLeft size={16} />
    </button>
    <ol>
      {#each crumbs as c, i (i)}
        {#if i > 0}<li class="sep" aria-hidden="true"><ChevronRight size={12} /></li>{/if}
        <li>
          {#if c.id < 0}
            <span class="faint">{c.label}</span>
          {:else if i < crumbs.length - 1}
            <button type="button" class="crumb" onclick={() => void zoomTo(c.id)}>{c.label}</button>
          {:else}
            <span class="here" aria-current="location">{c.label}</span>
          {/if}
        </li>
      {/each}
    </ol>
    {#if app.view}<span class="total num">{formatBytes(app.view.total)}</span>{/if}
    <div class="viewctl">
      <div class="seg dims" role="group" aria-label={t('浏览维度')}>
        <button type="button" aria-pressed={app.dimension === 'dirs'} title={t('按目录看空间分布')} onclick={() => setDimension('dirs')}>{t('目录分布')}</button>
        <button type="button" aria-pressed={app.dimension === 'apps'} title={t('当前目录按所属应用、游戏、项目汇总')} onclick={() => setDimension('apps')}>{t('应用占用')}</button>
      </div>
      {#if app.dimension === 'dirs'}
        <button type="button" class="btn btn-ghost listbtn" aria-pressed={app.listOpen} title={t('和地图同步的目录列表：全部子项，可按名称、大小、修改时间排序')} onclick={() => toggleList()}><List size={14} />{t('列表')}</button>
      {/if}
    </div>
  </nav>

  <div class="body" class:with-list={app.dimension === 'dirs' && app.listOpen}>
    {#if app.dimension === 'apps'}
      <OwnerView />
    {:else}
      {#if app.listOpen}<DirList />{/if}
      <!-- Treemap 是 position:absolute 铺满父级；左右各留 12px，加上它自己的 8px 外边距，方块刚好和标题文字对齐（参考图） -->
      <div class="canvas" aria-busy={app.viewLoading}>
        <Treemap />
        {#if app.viewLoading || app.viewError}
          <div class="loading">
            {#if app.viewLoading}
              <p class="float" role="status">{t('正在加载地图…')}</p>
            {:else}
              <div class="float load-error" role="alert">
                <p>{t('地图加载失败：{error}', { error: app.viewError })}</p>
                <button type="button" class="btn" onclick={() => void loadView()}>{t('重试')}</button>
              </div>
            {/if}
          </div>
        {/if}
      </div>
    {/if}
  </div>

  {#if app.dimension === 'dirs'}
    <footer class="foot">
      <!-- 图例：悬停或聚焦高亮这一类，点击只看这一类（再点或「清除筛选」恢复）；名称和颜色同时给，不只靠颜色 -->
      <ul class="legend" aria-label={t('图例：悬停高亮对应区域，点击只看这一类')}>
        {#each entries as l (l.key)}
          <li>
            <button type="button" class="leg" aria-pressed={app.legendFilter === l.key}
              onmouseenter={() => setLegendHover(l.key)} onmouseleave={() => setLegendHover(null)}
              onfocus={() => setLegendHover(l.key)} onblur={() => setLegendHover(null)}
              onclick={() => setLegendFilter(l.key)}><i class="swatch" class:edged={app.explorerMode === 'cleanup'} style:background={l.fill} style:border-color={l.line}></i>{l.name}</button>
          </li>
        {/each}
        {#if app.explorerMode === 'cleanup'}<li><i class="swatch hatch"></i>{t('已勾选')}</li>{/if}
      </ul>
      {#if filtered}
        <p class="filter" role="status">
          {#if share}{t('只看「{name}」：在「{here}」里 {bytes} / {total}（{pct}，按{metric}；分母是当前目录，不是整个磁盘）', { name: filtered.name, here, bytes: formatBytes(share.bytes), total: formatBytes(share.total), pct: formatPercent(share.frac), metric: app.metric === 'alloc' ? t('占用空间') : t('文件大小') })}{:else}{t('只看「{name}」（按清理建议着色，当前目录内）', { name: filtered.name })}{/if}
          <button type="button" class="btn btn-ghost clear" onclick={() => setLegendFilter(null)}>{t('清除筛选')}</button>
        </p>
      {/if}
    </footer>
  {/if}
  <!-- 生长树盖在地图上，右侧详情保持打开；Esc 或 × 回到地图 -->
  {#if relatedTree.open}<RelatedTree />{/if}
</section>

<style>
  .explorer {
    position: relative;
    display: flex;
    flex-direction: column;
    min-width: 0;
    min-height: 0;
    overflow: hidden;
  }

  /* 照参考图量的：分段控件的垂直中心和标题对齐（距卡片顶 29px），分隔线紧贴副标题下方（60px） */
  .head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
    padding: 10px 16px;
    border-bottom: 1px solid var(--line);
  }
  .titles {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 4px 12px;
  }
  .titles .card-sub {
    margin-top: 0;
  }
  /* 「清理」按钮上的小圆点：任务在跑或状态待确认 */
  .busy {
    display: inline-block;
    width: 6px;
    height: 6px;
    margin-left: 5px;
    vertical-align: 2px;
    border-radius: 50%;
    background: var(--workspace-accent);
  }

  .crumbs {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 5px 16px 0 10px;
  }
  .back {
    width: 28px;
    height: 28px;
    flex: none;
  }
  ol {
    display: flex;
    flex: 1;
    align-items: center;
    gap: 4px;
    min-width: 0;
    margin: 0;
    padding: 0;
    overflow: hidden;
    list-style: none;
    white-space: nowrap;
  }
  li {
    display: inline-flex;
    align-items: center;
    min-width: 0;
  }
  .sep {
    color: var(--faint);
  }
  .faint {
    color: var(--faint);
  }
  .crumb {
    max-width: 220px;
    padding: 2px 6px;
    overflow: hidden;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--muted);
    cursor: pointer;
    text-overflow: ellipsis;
    transition: background var(--dur-fast);
  }
  .crumb:hover {
    background: var(--hover);
    color: var(--ink);
  }
  .here {
    padding: 2px 6px;
    overflow: hidden;
    color: var(--ink);
    font-weight: 600;
    text-overflow: ellipsis;
  }
  .total {
    flex: none;
    color: var(--muted);
  }

  .viewctl {
    display: flex;
    flex: none;
    align-items: center;
    gap: 6px;
    margin-left: 6px;
  }
  .dims button {
    padding: 0 8px;
    font-size: 12px;
  }
  .listbtn {
    height: 28px;
    gap: 4px;
    padding: 0 8px;
    font-size: 12px;
  }
  .listbtn[aria-pressed='true'] {
    background: var(--hover);
    color: var(--workspace-accent, var(--ink));
  }
  .body {
    display: grid;
    flex: 1;
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: minmax(0, 1fr);
    min-height: 0;
  }
  .body.with-list {
    grid-template-columns: 300px minmax(0, 1fr);
  }
  .canvas {
    position: relative;
    min-height: 0;
    margin: 0 4px;
  }
  .leg {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 2px 6px;
    border: 1px solid transparent;
    border-radius: 6px;
    background: none;
    color: inherit;
    font-size: 12px;
    cursor: pointer;
  }
  .leg:hover,
  .leg:focus-visible {
    background: var(--hover);
  }
  .leg[aria-pressed='true'] {
    border-color: var(--line);
    background: var(--hover);
    color: var(--ink);
    font-weight: 600;
  }
  .filter {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 4px 8px;
    margin: 0;
    color: var(--ink-2);
    font-size: 12px;
  }
  .clear {
    height: 24px;
    padding: 0 8px;
    font-size: 12px;
  }
  @media (max-width: 800px) {
    .body.with-list {
      grid-template-columns: minmax(0, 1fr);
      grid-template-rows: minmax(0, 42%) minmax(0, 1fr);
    }
    .body.with-list :global(.dirlist) {
      border-right: 0;
      border-bottom: 1px solid var(--line);
    }
    .dims button {
      padding: 0 6px;
    }
  }
  .loading {
    position: absolute;
    inset: 0;
    z-index: 2;
    display: grid;
    place-items: center;
    background: rgb(255 255 255 / 45%);
  }
  .loading .float {
    max-width: calc(100% - 32px);
    padding: 10px 14px;
    color: var(--ink-2);
  }
  .load-error p {
    margin: 0 0 8px;
    overflow-wrap: anywhere;
  }

  .foot {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 4px 16px;
    padding: 6px 16px 10px;
    color: var(--muted);
    font-size: 12px;
  }
  .legend {
    display: flex;
    flex-wrap: wrap;
    gap: 4px 12px;
    margin: 0;
    padding: 0;
    list-style: none;
  }
  .legend li {
    gap: 6px;
  }
  .legend .swatch {
    width: 8px;
    height: 8px;
    border-radius: 2px;
  }
  /* 「清理」模式的色块有描边，和图里的块一致 */
  .edged {
    border: 1px solid;
  }
  .swatch.hatch {
    border: 1px solid var(--safe-line);
    background: repeating-linear-gradient(45deg, var(--safe-hatch) 0 1.5px, transparent 1.5px 4px);
  }
  @media(max-width:600px) { .head { flex-wrap: wrap; gap: 8px; } .titles .card-sub { display: none; } .total { font-size: 11px; } .foot { max-height: 76px; overflow-y: auto; } }
</style>
