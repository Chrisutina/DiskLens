<script lang="ts">
  // 关联生长树（dise §8.2–§8.4）：盖在资源地图上，右侧详情保持打开。横向主干从当前对象出发，先排有时间依据的记录，
  // 再接一段虚线的「当前关联」，关联对象以曲线分支上下展开。只读浏览：探索、返回、定位，不碰清理勾选。
  import { ArrowLeft, House, Maximize2, Minus, Plus, X } from '@lucide/svelte';
  import { app } from '../lib/state.svelte.ts';
  import { relatedTree } from '../lib/related.svelte.ts';
  import { clipText, layoutTree, RELATION_BASIS, RELATION_LABELS, relatedTimes } from '../lib/relatedModel.ts';
  import { EVIDENCE_NAMES, OWNER_KIND_NAMES, role } from '../lib/roles.ts';
  import { formatBytes, formatDate } from '../lib/format.ts';
  import { t } from '../lib/i18n.ts';

  const data = relatedTree.data;
  const STEP = 6;
  /** 画出来的分支数：默认只展开前 6 条（后台按 所在根 → 另几处根 → 各角色模块 排好），其余按需展开 */
  let shown = $state(STEP);
  /** 信息卡里的对象：-1 所属对象，0.. 分支下标，null 当前对象 */
  let picked = $state<number | null>(null);

  const node = $derived(data.node);
  const result = $derived(data.result);
  const items = $derived(result?.items ?? []);
  const times = $derived(node && app.summary ? relatedTimes(node, app.summary.completedAtMs) : []);
  const layout = $derived(layoutTree(times, !!result?.owner, items, shown));
  const pickedItem = $derived(picked !== null && picked >= 0 ? items[picked] ?? null : null);
  const val = (x: { size: number; alloc: number }): number => (app.metric === 'alloc' ? x.alloc : x.size);

  // 换了中心对象：分支回到前 6 条，信息卡回到中心
  let centerId = -1;
  $effect(() => {
    const id = node?.id ?? -1;
    if (id === centerId) return;
    centerId = id;
    shown = STEP;
    picked = null;
    if (id >= 0) queueMicrotask(fit); // 只在换中心时适应窗口；展开更多不重排，已有分支不跳
  });

  // 详情面板关了、换了选中项、换了页或扫描：浮层跟着关
  $effect(() => {
    if (app.screen !== 'main' || app.page !== 'browse' || !app.panelOpen || app.rightTab !== 'details' || app.selectedId !== data.originId) relatedTree.close();
  });

  // ---------------- 缩放与平移 ----------------
  let stage = $state<HTMLDivElement>();
  let k = $state(1), tx = $state(16), ty = $state(16);

  function fit(): void {
    if (!stage) return;
    const r = stage.getBoundingClientRect();
    k = Math.max(0.4, Math.min(1, (r.width - 32) / layout.width, (r.height - 32) / layout.height));
    tx = 16;
    ty = Math.max(8, (r.height - layout.height * k) / 2);
  }

  function zoomAt(factor: number, px: number, py: number): void {
    const next = Math.min(2.5, Math.max(0.4, k * factor));
    tx = px - ((px - tx) * next) / k;
    ty = py - ((py - ty) * next) / k;
    k = next;
  }

  function zoomCenter(factor: number): void {
    const r = stage?.getBoundingClientRect();
    if (r) zoomAt(factor, r.width / 2, r.height / 2);
  }

  function onwheel(e: WheelEvent): void {
    e.preventDefault();
    const r = stage!.getBoundingClientRect();
    zoomAt(e.deltaY < 0 ? 1.1 : 1 / 1.1, e.clientX - r.left, e.clientY - r.top);
  }

  let drag: { x: number; y: number; id: number } | null = null;
  function onpointerdown(e: PointerEvent): void {
    // 节点上按下不拖：抢了指针捕获，节点就收不到 click
    if (e.button !== 0 || (e.target as Element).closest('[data-node]')) return;
    drag = { x: e.clientX, y: e.clientY, id: e.pointerId };
    stage!.setPointerCapture(e.pointerId);
  }
  function onpointermove(e: PointerEvent): void {
    if (drag?.id !== e.pointerId) return;
    tx += e.clientX - drag.x;
    ty += e.clientY - drag.y;
    drag.x = e.clientX;
    drag.y = e.clientY;
  }
  function onpointerup(e: PointerEvent): void {
    if (drag?.id === e.pointerId) drag = null;
  }

  const PAN: Record<string, [number, number]> = { ArrowLeft: [40, 0], ArrowRight: [-40, 0], ArrowUp: [0, 40], ArrowDown: [0, -40] };
  function onkeydown(e: KeyboardEvent): void {
    if (e.target !== stage) return;
    const pan = PAN[e.key];
    if (pan) { tx += pan[0]; ty += pan[1]; }
    else if (e.key === '+' || e.key === '=') zoomCenter(1.2);
    else if (e.key === '-') zoomCenter(1 / 1.2);
    else if (e.key === '0') fit();
    else return;
    e.preventDefault();
  }

  /** 节点是按钮：回车、空格都算点 */
  function press(e: KeyboardEvent, act: () => void): void {
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); act(); }
  }

  function focusStage(el: HTMLElement) {
    queueMicrotask(() => el.focus());
  }
</script>

<svelte:window onkeydowncapture={(e) => {
  if (e.key === 'Escape' && relatedTree.open && !app.confirm && !app.menu) { e.preventDefault(); e.stopPropagation(); relatedTree.close(); }
}} />

<section class="tree" aria-label={t('关联生长树')}>
  <header>
    <div class="titles">
      <h2 class="card-title">{t('关联生长树')}</h2>
      <p class="card-sub">{t('主干只有文件系统时间和本次扫描时间，暂无历史记录；虚线那段是没有时间依据的当前关联。只用于浏览，不会扩大清理范围。')}</p>
    </div>
    <div class="tools">
      <button type="button" class="btn" disabled={!data.history.length || data.loading} onclick={() => void data.back()}><ArrowLeft size={14} />{t('返回上一个')}</button>
      <button type="button" class="btn" disabled={data.loading || !node || node.id === data.originId} onclick={() => void data.home()}><House size={14} />{t('回到起点')}</button>
      <span class="zoom" role="group" aria-label={t('缩放')}>
        <button type="button" class="btn btn-icon" aria-label={t('缩小')} title={t('缩小（-）')} onclick={() => zoomCenter(1 / 1.2)}><Minus size={14} /></button>
        <span class="num" aria-live="polite">{Math.round(k * 100)}%</span>
        <button type="button" class="btn btn-icon" aria-label={t('放大')} title={t('放大（+）')} onclick={() => zoomCenter(1.2)}><Plus size={14} /></button>
        <button type="button" class="btn btn-icon" aria-label={t('适应窗口')} title={t('适应窗口（0）')} onclick={fit}><Maximize2 size={14} /></button>
      </span>
      <button type="button" class="btn btn-ghost btn-icon" aria-label={t('关闭生长树')} title={t('关闭（Esc）')} onclick={() => relatedTree.close()}><X size={16} /></button>
    </div>
  </header>

  <!-- 画布本身可聚焦：方向键平移、加减号缩放；节点各自是按钮 -->
  <!-- svelte-ignore a11y_no_noninteractive_tabindex, a11y_no_noninteractive_element_interactions -->
  <div
    class="stage"
    bind:this={stage}
    tabindex="0"
    role="group"
    aria-label={t('树图：方向键平移，加减号缩放，0 适应窗口；Tab 逐个经过节点')}
    aria-busy={data.loading}
    {@attach focusStage}
    {onwheel}
    {onpointerdown}
    {onpointermove}
    {onpointerup}
    onpointercancel={onpointerup}
    {onkeydown}
  >
    {#if node}
      <svg width="100%" height="100%" aria-hidden="false">
        <g transform="translate({tx} {ty}) scale({k})">
          <line class="trunk" x1={layout.object.x + layout.object.w} y1={layout.trunkY} x2={layout.relationStart} y2={layout.trunkY} />
          <line class="trunk now" x1={layout.relationStart} y1={layout.trunkY} x2={layout.trunkEnd} y2={layout.trunkY} />
          {#each layout.events as ev (ev.kind)}
            <g class="event">
              <circle cx={ev.x} cy={layout.trunkY} r="5" />
              <text x={ev.x} y={layout.trunkY + 24} text-anchor="middle">{ev.label}</text>
              <text class="sub" x={ev.x} y={layout.trunkY + 40} text-anchor="middle">{formatDate(ev.time)}</text>
            </g>
          {/each}
          <text class="seg" x={layout.relationStart + 8} y={layout.trunkY - 10}>{t('当前关联（无时间依据）')}</text>
          {#if !items.length}
            <text class="sub" x={layout.relationStart + 8} y={layout.trunkY + 24}>{result?.note ?? t('尚未识别关联对象')}</text>
          {/if}

          {#if result?.owner && layout.owner}
            {@const o = result.owner}
            <path class="link {o.evidence}" d={layout.owner.path} />
            <g data-node class="node owner" class:picked={picked === -1} role="button" tabindex="0"
              aria-label={t('所属对象 {name}，{kind}，{evidence}', { name: o.name, kind: OWNER_KIND_NAMES[o.kind], evidence: EVIDENCE_NAMES[o.evidence] })}
              onclick={() => (picked = -1)} onkeydown={(e) => press(e, () => (picked = -1))}>
              <title>{o.rootPath}</title>
              <rect x={layout.owner.x} y={layout.owner.y} width={layout.owner.w} height={layout.owner.h} rx="25" />
              <text x={layout.owner.x + 18} y={layout.owner.y + 21}>{clipText(o.name, 24)}</text>
              <text class="sub" x={layout.owner.x + 18} y={layout.owner.y + 38}>{t('所属对象')} · {OWNER_KIND_NAMES[o.kind]} · {EVIDENCE_NAMES[o.evidence]}</text>
            </g>
          {/if}

          {#each layout.branches as b (b.item.id)}
            {@const it = b.item}
            <path class="link {it.evidence}" d={b.path} />
            <g data-node class="node" class:file={!it.isDir} class:picked={picked === b.index} role="button" tabindex="0"
              aria-label={t('{name}，{relation}，{evidence}，{size}', { name: it.name, relation: RELATION_LABELS[it.relation], evidence: EVIDENCE_NAMES[it.evidence], size: formatBytes(val(it)) })}
              onclick={() => (picked = b.index)} onkeydown={(e) => press(e, () => (picked = b.index))}>
              <title>{it.path}</title>
              <rect x={b.box.x} y={b.box.y} width={b.box.w} height={b.box.h} rx={it.isDir ? 8 : 3} />
              {#if it.isDir}
                <path class="glyph" d="M{b.box.x + 12} {b.box.y + 14}h6l2 2h8v9h-16z" />
              {:else}
                <path class="glyph" d="M{b.box.x + 13} {b.box.y + 12}h8l4 4v10h-12zM{b.box.x + 21} {b.box.y + 12}v4h4" />
              {/if}
              <text x={b.box.x + 34} y={b.box.y + 24}>{clipText(it.name, 21)}</text>
              <text class="sub" x={b.box.x + 12} y={b.box.y + 43}>{RELATION_LABELS[it.relation]} · {EVIDENCE_NAMES[it.evidence]}</text>
              <text class="sub" x={b.box.x + 12} y={b.box.y + 58}>{formatBytes(val(it))} · {clipText(role(it.role).name, 14)}</text>
            </g>
          {/each}

          <g data-node class="node current" class:picked={picked === null} role="button" tabindex="0"
            aria-label={t('当前对象 {name}，{size}', { name: node.name, size: formatBytes(val(node)) })}
            onclick={() => (picked = null)} onkeydown={(e) => press(e, () => (picked = null))}>
            <title>{node.path}</title>
            <rect x={layout.object.x} y={layout.object.y} width={layout.object.w} height={layout.object.h} rx="10" />
            <text x={layout.object.x + 14} y={layout.object.y + 25}>{clipText(node.name, 24)}</text>
            <text class="sub" x={layout.object.x + 14} y={layout.object.y + 45}>{node.id === data.originId ? t('起点') : t('当前探索')} · {node.isDir ? t('目录@@单个') : t('文件@@单个')} · {formatBytes(val(node))}</text>
          </g>
        </g>
      </svg>
      {#if data.loading}<p class="float flag" role="status">{t('正在读取关联…')}</p>{/if}
      {#if data.error}<p class="float flag err" role="alert">{data.error}</p>{/if}
    {:else if data.error}
      <div class="empty" role="alert"><p>{t('关联读取失败：{error}', { error: data.error })}</p><button type="button" class="btn" onclick={() => void data.load()}>{t('重试')}</button></div>
    {:else}
      <p class="empty" role="status">{t('正在读取关联…')}</p>
    {/if}

  </div>

  <!-- 信息卡单独占画布下面一条，不压在树上 -->
  {#if node}
      <aside class="info" aria-label={t('节点信息')}>
        {#if picked === -1 && result?.owner}
          {@const o = result.owner}
          <h3>{o.name}</h3>
          <p class="meta">{t('所属对象')} · {OWNER_KIND_NAMES[o.kind]} · {EVIDENCE_NAMES[o.evidence]}</p>
          <p class="path mono">{o.rootPath}</p>
          <p class="meta">{o.evidence === 'confirmed' ? t('这一处归属有安装信息、路径规则或项目标记为证。属于同一个对象不等于可以整组删除。') : t('这一处归属按目录名推测。属于同一个对象不等于可以整组删除。')}</p>
          <div class="acts"><button type="button" class="btn" onclick={() => void data.locate(o.rootId, o.rootPath)}>{t('在地图中定位它的根')}</button></div>
        {:else if pickedItem}
          {@const it = pickedItem}
          <h3>{it.name}</h3>
          <p class="meta">{RELATION_LABELS[it.relation]} · {EVIDENCE_NAMES[it.evidence]} · {role(it.role).name}</p>
          <p class="path mono">{it.path}</p>
          <p class="meta">{t('占用 {alloc} · 文件大小 {size}', { alloc: formatBytes(it.alloc), size: formatBytes(it.size) })}</p>
          <p class="meta">{RELATION_BASIS[it.relation]}{it.evidence === 'inferred' ? t('这一条是推测。') : ''}</p>
          <div class="acts">
            <button type="button" class="btn" disabled={data.loading} onclick={() => void data.load(it.id, it.path, true)}>{t('以它为中心继续探索')}</button>
            <button type="button" class="btn" onclick={() => void data.locate(it.id, it.path)}>{t('在地图中定位')}</button>
          </div>
        {:else}
          <h3>{node.name}</h3>
          <p class="meta">{node.id === data.originId ? t('起点') : t('当前探索')} · {node.isDir ? t('目录@@单个') : t('文件@@单个')} · {role(node.role).name}</p>
          <p class="path mono">{node.path}</p>
          <p class="meta">{t('占用 {alloc} · 文件大小 {size}', { alloc: formatBytes(node.alloc), size: formatBytes(node.size) })}</p>
          <p class="meta">{t('修改 {modified} · 创建 {created}（扫描时读取）', { modified: formatDate(node.modifiedMs), created: formatDate(node.createdMs) })}</p>
          {#if node.owner}<p class="meta">{t('所属对象：{name}（{kind}，{evidence}）', { name: node.owner.name, kind: OWNER_KIND_NAMES[node.owner.kind], evidence: EVIDENCE_NAMES[node.owner.evidence] })}</p>{/if}
          {#if node.id !== data.originId}
            <div class="acts"><button type="button" class="btn" onclick={() => void data.locate(node.id, node.path)}>{t('在地图中定位')}</button></div>
          {/if}
        {/if}
      </aside>
  {/if}

  <footer>
    {#if items.length > shown}<button type="button" class="btn" onclick={() => (shown += STEP)}>{t('展开更多分支（还有 {n} 条）', { n: items.length - shown })}</button>{/if}
    {#if shown > STEP}<button type="button" class="btn" onclick={() => { shown = STEP; if (picked !== null && picked >= STEP) picked = null; }}>{t('收起')}</button>{/if}
    {#if result && node && result.total > items.length}<button type="button" class="btn" disabled={data.loading} onclick={() => void data.load(node.id, node.path, false, 200)}>{t('读取更多关联（共 {n} 条）', { n: result.total })}</button>{/if}
    <span class="legend"><i class="solid"></i>{t('已确认')} <i class="dashed"></i>{t('推测')} <b class="shape dir"></b>{t('目录@@单个')} <b class="shape file"></b>{t('文件@@单个')} <b class="shape owner"></b>{t('所属对象')}</span>
  </footer>
</section>

<style>
  .tree { position: absolute; inset: 0; z-index: 6; display: flex; flex-direction: column; min-width: 0; background: var(--card); }
  header { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px 16px; padding: 10px 16px; border-bottom: 1px solid var(--line); }
  .titles { min-width: 0; flex: 1 1 320px; }
  .titles .card-sub { margin-top: 2px; }
  .tools { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
  .zoom { display: inline-flex; align-items: center; gap: 2px; }
  .zoom .num { min-width: 44px; text-align: center; font-size: 12px; color: var(--muted); }
  .stage { position: relative; flex: 1; min-height: 0; overflow: hidden; cursor: grab; background: var(--page); outline: none; }
  .stage:focus-visible { box-shadow: inset 0 0 0 2px var(--workspace-accent); }
  .stage:active { cursor: grabbing; }
  svg { display: block; touch-action: none; user-select: none; }
  .trunk { stroke: var(--ink-2); stroke-width: 3; stroke-linecap: round; }
  .trunk.now { stroke: var(--muted); stroke-dasharray: 8 6; }
  .event circle { fill: var(--card); stroke: var(--ink-2); stroke-width: 2; }
  text { font-size: 13px; fill: var(--ink); }
  text.sub, .seg { font-size: 11px; fill: var(--muted); }
  .link { fill: none; stroke: var(--workspace-accent); stroke-width: 2; opacity: .55; }
  .link.inferred, .link.unknown { stroke-dasharray: 6 5; }
  .node { cursor: pointer; outline: none; }
  .node rect { fill: var(--card); stroke: var(--line); stroke-width: 1.5; }
  .node.file rect { stroke-dasharray: none; }
  .node.owner rect { fill: color-mix(in srgb, var(--workspace-accent) 10%, var(--card)); stroke: var(--workspace-accent); }
  .node.current rect { stroke: var(--workspace-accent); stroke-width: 2.5; }
  .node:hover rect { stroke: var(--workspace-accent); }
  .node.picked rect, .node:focus-visible rect { stroke: var(--workspace-accent); stroke-width: 3; }
  .glyph { fill: none; stroke: var(--muted); stroke-width: 1.4; stroke-linejoin: round; }
  .flag { position: absolute; top: 10px; left: 50%; transform: translateX(-50%); margin: 0; padding: 6px 12px; font-size: 12px; }
  .flag.err { color: var(--review-text); }
  .empty { padding: 40px; text-align: center; color: var(--muted); }
  .info { flex: none; max-height: 132px; overflow: auto; padding: 10px 16px; border-top: 1px solid var(--line); background: var(--card); }
  .info h3 { margin: 0; font-size: 14px; overflow-wrap: anywhere; }
  .info .meta { margin: 4px 0 0; font-size: 12px; color: var(--muted); overflow-wrap: anywhere; }
  .info .path { margin: 6px 0 0; font-size: 12px; overflow-wrap: anywhere; color: var(--ink-2); }
  .acts { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 10px; }
  footer { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; padding: 8px 16px; border-top: 1px solid var(--line); font-size: 12px; color: var(--muted); }
  .legend { display: inline-flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; margin-left: auto; }
  .legend i { display: inline-block; width: 22px; height: 0; border-top: 2px solid var(--workspace-accent); opacity: .7; }
  .legend i.dashed { border-top-style: dashed; }
  .legend b { display: inline-block; width: 14px; height: 10px; border: 1.5px solid var(--line); background: var(--card); }
  .legend b.dir { border-radius: 3px; }
  .legend b.file { border-radius: 1px; }
  .legend b.owner { border-radius: 6px; border-color: var(--workspace-accent); }
  @media (max-width: 600px) { .titles .card-sub { display: none; } .info { max-height: 38%; } }
</style>
