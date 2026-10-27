<script lang="ts">
  // 详情标签（DESIGN §9.3）：选中节点的全部信息，以及它最大的 12 个子项。
  import { Copy, Lock, TriangleAlert } from '@lucide/svelte';
  import { app, copyText, engineLabel, loadDetails, openInExplorer, requestDelete, searchWeb, select, zoomTo } from '../lib/state.svelte.ts';
  import { catName } from '../lib/categories.ts';
  import { formatBytes, formatCount, formatDate, formatPercent, splitBytes } from '../lib/format.ts';
  import { purpose } from '../lib/purposes.ts';
  import { EVIDENCE_NAMES, OWNER_KIND_NAMES, role } from '../lib/roles.ts';
  import { Flag, hasFlag, type ChildItem, type NodeDetails } from '../lib/types.ts';
  import CategoryIcon from './CategoryIcon.svelte';
  import RelatedSection from './RelatedSection.svelte';
  import { t } from '../lib/i18n.ts';

  const d = $derived(app.details);
  const alloc = $derived(app.metric === 'alloc');

  /** 占用和文件大小差得明显时说清楚为什么（dise §5.3）；差得不多不啰嗦 */
  function explainSize(n: NodeDetails): string | null {
    if (hasFlag(n.f, Flag.Cloud)) return t('云端文件：内容不在本机，占用接近 0。');
    if (hasFlag(n.f, Flag.Compressed)) return t('NTFS 压缩：占用小于文件大小。');
    if (hasFlag(n.f, Flag.Sparse)) return t('稀疏文件：没写入数据的部分不占空间。');
    if (n.size < 1024 * 1024 && n.alloc < 1024 * 1024) return null;
    if (n.alloc < n.size * 0.8) return n.isDir ? t('占用明显小于文件大小：里面有压缩、稀疏、云端文件，或只计一次的硬链接。') : t('占用明显小于文件大小：可能是压缩、稀疏或硬链接只计一次。');
    if (n.isDir && n.alloc > n.size * 1.25) return t('占用明显大于文件大小：小文件多，按簇分配后有零头。');
    return null;
  }
  const sizeNote = $derived(d ? explainSize(d) : null);
  // ChildItem 还没有用途字段 u，横条先统一用详情自己的用途色；有了再换成各自的
  const color = $derived(purpose(d?.u ?? 0).dot);
  const val = (c: ChildItem): number => (alloc ? c.alloc : c.size);
  const kids = $derived(d ? [...d.topChildren].sort((a, b) => val(b) - val(a)).slice(0, 12) : []);
  const kidMax = $derived(Math.max(1, ...kids.map(val)));

  // 单击后右栏立刻换成这个子项自己的内容，列表跟着重绘，双击的第二下可能落在别的行上，
  // 所以不用 dblclick 事件，记住第一下点的是谁，第二下（detail === 2）直接进它。
  let lastKid: ChildItem | null = null;
  function pickKid(e: MouseEvent, c: ChildItem): void {
    if (e.detail === 2 && lastKid?.isDir) {
      void zoomTo(lastKid.id);
      return;
    }
    lastKid = c;
    select(c.id);
  }
</script>

{#snippet meter(label: string, frac: number)}
  <div class="meter">
    <span class="mlabel">{label}</span>
    <span class="track"><i style:width="{Math.min(1, Math.max(0, frac)) * 100}%" style:min-width={frac > 0 ? '2px' : null} style:background={color}></i></span>
    <span class="mval num">{formatPercent(frac)}</span>
  </div>
{/snippet}

{#if app.detailsLoading}
  <div class="empty" role="status" aria-busy="true">
    <p class="empty-title">{t('正在加载详情…')}</p>
    <p>{t('读取完成后显示所选项的信息和操作。')}</p>
  </div>
{:else if app.detailsError}
  <div class="empty" role="alert">
    <p>{t('详情读取失败：{error}', { error: app.detailsError })}</p>
    <button type="button" class="btn" onclick={() => void loadDetails()}>{t('重试')}</button>
  </div>
{:else if d}
  {@const big = splitBytes(alloc ? d.alloc : d.size)}
  {@const p = purpose(d.u)}
  <div class="scroll">
    <header>
      <h3 class="name">{d.name}</h3>
      <p class="meta">
        <CategoryIcon k={d.k} size={14} tinted={false} />{t('文件类型：{type}', { type: catName(d.k) })}
      </p>
    </header>
    <!-- 用途、角色、所属对象分开说，各带证据等级；它们都不代表清理风险（dise §7.1、§8.1） -->
    <dl class="identity" aria-label={t('用途与归属')}>
      <dt>{t('主用途')}</dt><dd><span class="dot" style:background={p.dot}></span>{p.name}<span class="ev ev-{d.evidence}">{EVIDENCE_NAMES[d.evidence]}</span></dd>
      <dt>{t('数据角色')}</dt><dd>{d.role ? role(d.role).name : t('未识别')}</dd>
      <dt>{t('所属对象')}</dt>
      <dd>{#if d.owner}{d.owner.name}<span class="faint">（{OWNER_KIND_NAMES[d.owner.kind]}）</span><span class="ev ev-{d.owner.evidence}">{EVIDENCE_NAMES[d.owner.evidence]}</span>{:else}<span class="faint">{t('尚未识别')}</span>{/if}</dd>
    </dl>
    <p class="note">{t('依据：{basis}用途和归属只说明它是什么，不代表能不能删。', { basis: d.purposeBasis ?? t('尚未提供详细依据。') })}</p>

    <div class="path">
      <!-- 反斜杠后允许断行，路径换行时不会把文件名从中间劈开 -->
      <span class="ptext mono">{#each d.path.split('\\') as part, i}{#if i > 0}\<wbr />{/if}{part}{/each}</span>
      <button type="button" class="btn btn-ghost btn-icon" title={t('复制路径')} aria-label={t('复制路径')} onclick={() => copyText(d.path, t('路径'))}>
        <Copy size={14} aria-hidden="true" />
      </button>
    </div>

    <div class="sizes">
      <span class="slabel">{alloc ? t('占用空间') : t('文件大小')}</span>
      <span class="big num">{big.value} {big.unit}</span>
      <span class="salt num">{alloc ? t('文件大小') : t('占用空间')} {formatBytes(alloc ? d.size : d.alloc)}</span>
    </div>
    {#if sizeNote}<p class="note">{sizeNote}</p>{/if}

    <div class="meters">
      {@render meter(t('占父目录'), d.percentOfParent)}
      {@render meter(t('占根'), d.percentOfRoot)}
    </div>

    <dl class="facts" aria-label={t('扫描时读取的属性')}>
      <dt class="caption">{t('扫描时读取')}</dt><dd class="caption">{app.summary ? t('{date} 完成的扫描', { date: formatDate(app.summary.completedAtMs) }) : ''}</dd>
      {#if d.isDir}
        <dt>{t('文件数')}</dt><dd class="num">{formatCount(d.files)}</dd>
        <dt>{t('目录数')}</dt><dd class="num">{formatCount(d.dirs)}</dd>
      {/if}
      <dt>{t('修改时间')}</dt><dd class="num">{formatDate(d.modifiedMs)}</dd>
      <dt>{t('创建时间')}</dt><dd class="num">{formatDate(d.createdMs)}</dd>
      <dt>{t('访问时间')}</dt><dd class="num">{formatDate(d.accessedMs)}</dd>
    </dl>

    {#if d.attributes.length}
      <div class="chips">
        {#each d.attributes as a}<span class="chip">{a}</span>{/each}
      </div>
    {/if}

    {#if d.hardLinkCount > 1}
      <p class="note">{t('硬链接 ×{n}', { n: d.hardLinkCount })}{#if d.hardLinkCountedAt}{t('，计入位置：')}<span class="mono">{d.hardLinkCountedAt}</span>{/if}</p>
    {/if}
    {#if d.isProtected}
      <p class="note icon lock"><Lock size={14} aria-hidden="true" /><span>{d.protectReason ?? t('这是受保护的位置，不能删除。')}</span></p>
    {:else if d.warning}
      <p class="note icon warn"><TriangleAlert size={14} aria-hidden="true" /><span>{d.warning}</span></p>
    {/if}

    <div class="acts">
      <button type="button" class="btn" onclick={() => openInExplorer(d.id)}>{t('在资源管理器中显示')}</button>
      <button type="button" class="btn" onclick={() => searchWeb(d.id)}>{t('在网上搜索（{engine}）', { engine: engineLabel() })}</button>
      <button
        type="button"
        class="btn btn-danger"
        disabled={d.isProtected}
        title={d.isProtected ? (d.protectReason ?? t('这是受保护的位置，不能删除。')) : undefined}
        onclick={() => requestDelete([d.id], false)}>{t('删除…')}</button>
    </div>

    <RelatedSection node={d} />

    {#if kids.length}
      <section class="kids">
        <h4>{t('最大的子项')} <span class="num">{kids.length} / {formatCount(d.childCount)}</span></h4>
        <ul>
          {#each kids as c (c.id)}
            <li>
              <button
                type="button"
                class="kid"
                title={c.isDir ? t('单击选中，双击进入') : t('单击选中')}
                onclick={(e) => pickKid(e, c)}>
                <span class="kname">{c.name}</span>
                <span class="kval num">{formatBytes(val(c))}</span>
                <span class="kbar"><i style:width="{(val(c) / kidMax) * 100}%" style:background={color}></i></span>
              </button>
            </li>
          {/each}
        </ul>
      </section>
    {/if}
  </div>
{:else}
  <div class="empty">
    <p class="empty-title">{t('还没有选中项')}</p>
    <p>{t('在资源地图里点一个文件，这里会显示它的详细信息。')}</p>
  </div>
{/if}

<style>
  .scroll {
    display: flex;
    flex: 1;
    flex-direction: column;
    gap: 16px;
    min-height: 0;
    padding: 16px 16px 20px;
    overflow-y: auto;
  }
  .name {
    margin: 0;
    font-size: 16px;
    font-weight: 600;
    line-height: 24px;
    overflow-wrap: anywhere;
  }
  .meta {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px;
    margin: 2px 0 0;
    color: var(--muted);
    font-size: 13px;
  }
  .sep {
    color: var(--faint);
  }

  .path {
    display: flex;
    align-items: flex-start;
    gap: 4px;
    padding: 6px 4px 6px 10px;
    border-radius: var(--r-ctl);
    background: var(--page);
  }
  .ptext {
    flex: 1;
    min-width: 0;
    padding: 4px 0;
    font-size: 12px;
    line-height: 18px;
    overflow-wrap: anywhere;
  }
  .path .btn-icon {
    flex: none;
    width: 28px;
    height: 28px;
  }

  .sizes {
    display: flex;
    flex-direction: column;
  }
  .slabel {
    color: var(--muted);
    font-size: 13px;
  }
  .big {
    font-size: 28px;
    font-weight: 600;
    line-height: 36px;
  }
  .salt {
    color: var(--muted);
    font-size: 13px;
  }

  .meters {
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .meter {
    display: grid;
    grid-template-columns: 64px 1fr 52px;
    align-items: center;
    gap: 12px;
  }
  .mlabel {
    color: var(--muted);
    font-size: 13px;
  }
  .track {
    display: block;
    height: 6px;
    overflow: hidden;
    border-radius: 3px;
    background: var(--line-soft);
  }
  .track i,
  .kbar i {
    display: block;
    height: 100%;
    border-radius: 3px;
  }
  .mval {
    font-size: 12px;
    text-align: right;
  }

  .facts {
    display: grid;
    grid-template-columns: auto 1fr;
    gap: 6px 16px;
    margin: 0;
    font-size: 13px;
  }
  .facts dt {
    color: var(--muted);
  }
  .facts dd {
    margin: 0;
  }
  .facts .caption {
    color: var(--muted);
    font-size: 11px;
  }
  .identity {
    display: grid;
    grid-template-columns: auto minmax(0, 1fr);
    gap: 6px 16px;
    margin: 0;
    font-size: 13px;
  }
  .identity dt {
    color: var(--muted);
  }
  .identity dd {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 4px 6px;
    margin: 0;
    overflow-wrap: anywhere;
  }
  .identity .dot {
    width: 8px;
    height: 8px;
    border-radius: 2px;
  }
  .faint {
    color: var(--muted);
  }
  /* 证据等级用字说明，不只靠颜色 */
  .ev {
    padding: 0 6px;
    border: 1px solid var(--line);
    border-radius: 9px;
    color: var(--muted);
    font-size: 11px;
    line-height: 16px;
  }
  .ev-confirmed {
    border-color: var(--workspace-accent, var(--line));
    color: var(--workspace-accent, var(--ink-2));
  }

  .chips {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }
  .note {
    margin: 0;
    padding: 8px 10px;
    border-radius: var(--r-ctl);
    background: var(--page);
    color: var(--ink-2);
    font-size: 13px;
    line-height: 20px;
    overflow-wrap: anywhere;
  }
  /* 带图标的提示：图标一列、文字一列；没图标的（硬链接）保持一段话 */
  .note.icon {
    display: flex;
    align-items: flex-start;
    gap: 8px;
  }
  .note :global(svg) {
    flex: none;
    margin-top: 3px;
  }
  .note.lock {
    color: var(--muted);
  }
  .note.warn {
    background: var(--review-bg);
    color: var(--review-text);
  }
  .note .mono {
    font-size: 12px;
  }

  .acts {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }

  .kids {
    padding-top: 16px;
    border-top: 1px solid var(--line-soft);
  }
  .kids h4 {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    margin: 0 0 4px;
    font-size: 13px;
    font-weight: 600;
  }
  .kids h4 .num {
    color: var(--muted);
    font-size: 12px;
    font-weight: 400;
  }
  .kids ul {
    margin: 0;
    padding: 0;
    list-style: none;
  }
  /* 悬停底色向两边多出 8px，条本身仍和上面的占比条对齐 */
  .kid {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    gap: 2px 12px;
    width: calc(100% + 16px);
    padding: 6px 8px;
    margin: 0 -8px;
    border: 0;
    border-radius: var(--r-ctl);
    background: none;
    text-align: left;
    cursor: pointer;
    transition: background var(--dur-fast);
  }
  .kid:hover {
    background: var(--hover);
  }
  .kname {
    overflow: hidden;
    font-size: 13px;
    line-height: 20px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .kval {
    color: var(--muted);
    font-size: 12px;
    line-height: 20px;
  }
  .kbar {
    grid-column: 1 / -1;
    height: 4px;
    border-radius: 2px;
    background: var(--line-soft);
  }
  .kbar i {
    border-radius: 2px;
  }

  .empty {
    padding: 40px 32px;
    color: var(--muted);
    text-align: center;
  }
  .empty p {
    margin: 0 0 8px;
  }
  .empty-title {
    color: var(--ink);
    font-size: 14px;
    font-weight: 500;
  }
</style>
