<script lang="ts">
  // 资源地图的画布（DESIGN §8.5）。铺满父容器；外框、图例、面包屑是 ExplorerCard 的事。
  // 两层画布：下层画整幅场景，上层只画悬停和选中，鼠标移动不会触发整幅重绘。
  import { onMount } from 'svelte';
  import { app, zoomTo, showDetails, back, forward, setHover, openMenu, closeMenu } from '../lib/state.svelte.ts';
  import { Flag } from '../lib/types.ts';
  import type { ViewData, ViewNode } from '../lib/types.ts';
  import { purpose } from '../lib/purposes.ts';
  import { formatBytes, formatPercent } from '../lib/format.ts';
  import { t } from '../lib/i18n.ts';
  import { layoutTreemap, hitTest, type Layout } from '../lib/treemap/layout.ts';
  import { inheritMarks, ancestorSeed, NO_SEED } from '../lib/treemap/marks.ts';
  import { createPainter, readTheme, type Painter } from '../lib/treemap/paint.ts';
  import { planZoom, playZoom } from '../lib/treemap/zoom.ts';
  import { keyboardTiles, moveTile } from '../lib/treemap/keyboard.ts';

  interface Tip {
    name: string;
    purpose: string;
    dot: string;
    path: string;
    sizeLabel: string;
    size: string;
    percent: string;
    kidsLabel: string;
    kids: string | null;
  }
  interface Painted {
    rootId: number;
    nodes: readonly ViewNode[];
    layout: Layout;
    w: number;
    h: number;
  }

  const HATCH_MS = 200;
  const mapId = $props.id();

  let host: HTMLDivElement;
  let sceneEl: HTMLCanvasElement;
  let overEl: HTMLCanvasElement;
  let tipEl = $state<HTMLDivElement>();

  let ready = $state(false);
  let size = $state({ w: 0, h: 0 });
  let fontsTick = $state(0);
  let hoverTile = $state(-1);
  let tip = $state.raw<Tip | null>(null);
  let keyboardFocus = $state(false);
  let reducedMotion = $state(false);

  let painter: Painter | null = null;
  let sceneCtx: CanvasRenderingContext2D | null = null;
  let overCtx: CanvasRenderingContext2D | null = null;

  // 布局只随视图数据和画布尺寸变化
  let layoutMs = 0;
  const layout = $derived.by((): Layout | null => {
    const v = app.view;
    if (!ready || !v || size.w <= 0 || size.h <= 0) return null;
    const t0 = performance.now();
    const l = layoutTreemap(v.nodes, size.w, size.h);
    layoutMs = performance.now() - t0;
    return l;
  });
  const empty = $derived(ready && app.view !== null && layout !== null && layout.count === 0);
  const metricLabel = $derived(app.metric === 'alloc' ? t('占用空间') : t('文件大小'));
  const accessibleTiles = $derived(app.view && layout ? keyboardTiles(app.view.nodes, layout) : []);
  const activePosition = $derived(app.view && layout ? accessibleTiles.findIndex((k) => app.view!.nodes[layout!.node[k]].id === app.selectedId) : -1);
  const activeTile = $derived(accessibleTiles[activePosition] ?? -1);
  const activeNode = $derived(app.view && layout && activeTile >= 0 ? app.view.nodes[layout.node[activeTile]] : null);

  // 事件处理和 rAF 里用的快照，由下面的 $effect 同步（读它们不需要响应式）
  let curView: ViewData | null = null;
  let curLayout: Layout | null = null;
  let curGroup: Uint8Array = new Uint8Array(0);
  let painted: Painted | null = null;
  let sceneDirty = false;
  let overlayDirty = false;
  let raf = 0;

  // 勾选斜纹的淡入淡出：每个节点一个 0..1 的不透明度，朝目标值走 200ms
  let hatchNodes: readonly ViewNode[] | null = null;
  let hatchA = new Float32Array(0);
  let hatchFrom = new Float32Array(0);
  let hatchTo: Uint8Array = new Uint8Array(0);
  let hatchT0 = 0;
  let hatchBusy = false;

  let idMapFor: readonly ViewNode[] | null = null;
  let idMap = new Map<number, number>();

  let snapEl: HTMLCanvasElement | null = null;
  let anims: Animation[] = [];

  let inside = false;
  let px = 0;
  let py = 0;
  let cx = 0;
  let cy = 0;

  function schedule(): void {
    if (!raf && ready) raf = requestAnimationFrame(frame);
  }

  $effect(() => {
    curView = app.view;
    curLayout = layout;
    void app.explorerMode;
    void app.cleanupMarks;
    void app.rootDetails;
    void fontsTick;
    void reducedMotion;
    sceneDirty = true;
    schedule();
  });

  $effect(() => {
    void hoverTile;
    void app.selectedId;
    void app.hoverId;
    void app.menu;
    void app.legendHover;
    void app.legendFilter;
    overlayDirty = true;
    schedule();
  });

  $effect(() => {
    if (tip && tipEl) placeTip();
  });

  $effect(() => {
    if (app.viewLoading) onLeave();
  });

  function frame(now: number): void {
    raf = 0;
    if (!painter || !sceneCtx || !overCtx || size.w <= 0 || size.h <= 0) return;
    const W = size.w;
    const H = size.h;
    const dpr = window.devicePixelRatio || 1;
    const pw = Math.round(W * dpr);
    const ph = Math.round(H * dpr);
    if (sceneEl.width !== pw || sceneEl.height !== ph) {
      sceneEl.width = pw;
      sceneEl.height = ph;
      overEl.width = pw;
      overEl.height = ph;
      sceneDirty = true;
    }
    if (sceneDirty) {
      sceneDirty = false;
      renderScene(now, W, H, dpr);
      overlayDirty = true;
      syncHover(true);
    }
    if (overlayDirty) {
      overlayDirty = false;
      renderOverlay(W, H, dpr);
    }
    if (hatchBusy) {
      sceneDirty = true;
      schedule();
    }
  }

  function renderScene(now: number, W: number, H: number, dpr: number): void {
    const view = curView;
    const lay = curLayout;
    if (!view || !lay || !painter || !sceneCtx) {
      sceneCtx?.setTransform(1, 0, 0, 1, 0, 0);
      sceneCtx?.clearRect(0, 0, sceneEl.width, sceneEl.height);
      painted = null;
      curGroup = new Uint8Array(0);
      return;
    }
    const t0 = performance.now();
    const nodes = view.nodes;
    if (painted && painted.rootId !== view.rootId) startZoom(painted, view, lay, W, H);

    const marks = app.cleanupMarks;
    const rd = app.rootDetails;
    const seed = rd && rd.id === view.rootId ? ancestorSeed(rd.ancestors.map((a) => a.id), marks.group, marks.selected) : NO_SEED;
    const inh = inheritMarks(nodes, marks.group, marks.selected, seed);
    stepHatch(nodes, inh.selected, now);
    curGroup = inh.group;
    painter.scene(sceneCtx, W, H, dpr, { nodes, layout: lay, cleanup: app.explorerMode === 'cleanup', group: inh.group, hatch: hatchA });
    painted = { rootId: view.rootId, nodes, layout: lay, w: W, h: H };
    // 诊断用：布局和绘制各花了多少毫秒（目标 12k 节点合计 < 16ms）
    host.dataset.layoutMs = layoutMs.toFixed(2);
    host.dataset.paintMs = (performance.now() - t0).toFixed(2);
    host.dataset.tiles = String(lay.count);
  }

  function stepHatch(nodes: readonly ViewNode[], sel: Uint8Array, now: number): void {
    if (reducedMotion || nodes !== hatchNodes || hatchA.length !== sel.length) {
      // 换视图或要求减少动效时直接到位，不继续调度动画帧。
      hatchNodes = nodes;
      hatchA = Float32Array.from(sel);
      hatchFrom = new Float32Array(sel.length);
      hatchTo = sel;
      hatchT0 = 0;
      hatchBusy = false;
      return;
    }
    let changed = false;
    for (let i = 0; i < sel.length; i++) {
      if (sel[i] !== hatchTo[i]) {
        changed = true;
        break;
      }
    }
    if (changed) {
      hatchFrom.set(hatchA);
      hatchTo = sel;
      hatchT0 = now || 1;
    }
    if (hatchT0 === 0) {
      hatchBusy = false;
      return;
    }
    const t = Math.min(1, (now - hatchT0) / HATCH_MS);
    const e = t * t * (3 - 2 * t);
    for (let i = 0; i < sel.length; i++) hatchA[i] = hatchFrom[i] + (hatchTo[i] - hatchFrom[i]) * e;
    if (t >= 1) hatchT0 = 0;
    hatchBusy = t < 1;
  }

  function renderOverlay(W: number, H: number, dpr: number): void {
    const view = curView;
    const lay = curLayout;
    if (!painter || !overCtx) return;
    if (!view || !lay) {
      overCtx.setTransform(1, 0, 0, 1, 0, 0);
      overCtx.clearRect(0, 0, overEl.width, overEl.height);
      return;
    }
    painter.overlay(overCtx, W, H, dpr, {
      nodes: view.nodes,
      layout: lay,
      cleanup: app.explorerMode === 'cleanup',
      group: curGroup,
      // 指针在块上用本地命中结果；否则跟随别处（右栏列表）设置的 hoverId
      hover: hoverTile >= 0 ? hoverTile : tileOfId(app.hoverId),
      selected: tileOfId(app.selectedId),
      target: app.menu ? tileOfId(app.menu.nodeId) : -1,
      focus: app.legendHover ?? app.legendFilter ?? -1,
    });
  }

  function tileOfId(id: number): number {
    const view = curView;
    const lay = curLayout;
    if (id < 0 || !view || !lay) return -1;
    if (idMapFor !== view.nodes) {
      idMap = new Map();
      view.nodes.forEach((n, i) => {
        if (n.id >= 0) idMap.set(n.id, i);
      });
      idMapFor = view.nodes;
    }
    const i = idMap.get(id);
    return i === undefined ? -1 : lay.tileOf[i];
  }

  // ---------- 进入 / 返回过渡 ----------

  function endZoom(): void {
    for (const a of anims) a.cancel();
    anims = [];
    snapEl?.remove();
    snapEl = null;
  }

  function startZoom(prev: Painted, view: ViewData, lay: Layout, W: number, H: number): void {
    endZoom();
    const plan = planZoom(prev, { rootId: view.rootId, nodes: view.nodes, layout: lay, w: W, h: H });
    const reduced = reducedMotion;
    let snap: HTMLCanvasElement | null = null;
    if (!reduced) {
      // 此刻下层画布还是旧画面：拷一份当快照
      snap = document.createElement('canvas');
      snap.width = sceneEl.width;
      snap.height = sceneEl.height;
      snap.getContext('2d')?.drawImage(sceneEl, 0, 0);
      Object.assign(snap.style, { position: 'absolute', inset: '0', width: '100%', height: '100%', pointerEvents: 'none', transformOrigin: '0 0' });
      host.insertBefore(snap, overEl);
      snapEl = snap;
    }
    const mine = playZoom(sceneEl, snap, plan.kind, plan.box, W, H, reduced);
    anims = mine;
    Promise.all(mine.map((a) => a.finished)).then(
      () => {
        if (anims !== mine) return;
        snapEl?.remove();
        snapEl = null;
        anims = [];
      },
      () => {}, // 被下一次导航取消
    );
  }

  // ---------- 悬停与提示卡 ----------

  /** refresh：重绘之后数据可能变了（度量、勾选），即使还是同一块也要重建提示卡 */
  function syncHover(refresh: boolean): void {
    const lay = curLayout;
    const view = curView;
    const t = !app.viewLoading && lay && view && inside ? hitTest(lay, px, py) : -1;
    if (t === hoverTile && !refresh) return;
    if (t !== hoverTile) {
      hoverTile = t;
      const nd = t >= 0 && lay && view ? view.nodes[lay.node[t]] : null;
      setHover(nd && nd.id >= 0 ? nd.id : -1);
      host.style.cursor = t >= 0 ? 'pointer' : 'default';
    }
    tip = t >= 0 ? buildTip(t) : null;
  }

  function buildTip(k: number): Tip | null {
    const view = curView;
    const lay = curLayout;
    if (!view || !lay) return null;
    const nodes = view.nodes;
    const i = lay.node[k];
    const nd = nodes[i];
    const agg = (nd.f & Flag.Aggregate) !== 0;
    const rd = app.rootDetails;
    const base = rd && rd.id === view.rootId ? rd.path : nodes[0].n;
    // 完整路径 = 视图根路径 + 视图里从根到它的名字链；聚合块指向它所在的目录
    const names: string[] = [];
    for (let j = agg ? nd.p : i; j > 0; j = nodes[j].p) names.push(nodes[j].n);
    const path = names.length ? `${base.endsWith('\\') ? base : base + '\\'}${names.reverse().join('\\')}` : base;
    const total = view.total > 0 ? view.total : nodes[0].v;
    const p = purpose(nd.u);
    return {
      name: nd.n || (agg ? t('其余 {n} 项', { n: nd.c }) : ''),
      purpose: p.name,
      dot: p.dot,
      path,
      sizeLabel: app.metric === 'alloc' ? t('占用空间') : t('文件大小'),
      size: formatBytes(nd.v),
      percent: formatPercent(total > 0 ? nd.v / total : 0),
      kidsLabel: agg ? t('合并条目') : t('子项'),
      kids: agg || (nd.f & Flag.Directory) !== 0 ? t('{n} 项', { n: nd.c }) : null,
    };
  }

  function placeTip(): void {
    if (!tipEl) return;
    const gap = 14;
    const w = tipEl.offsetWidth;
    const h = tipEl.offsetHeight;
    let x = cx + gap;
    let y = cy + gap + 4;
    if (x + w > window.innerWidth - 8) x = cx - gap - w;
    if (y + h > window.innerHeight - 8) y = cy - gap - h;
    tipEl.style.left = `${Math.max(8, x)}px`;
    tipEl.style.top = `${Math.max(8, y)}px`;
  }

  // ---------- 指针事件 ----------

  function local(e: MouseEvent): { x: number; y: number } {
    const r = host.getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top };
  }

  function hitAt(e: MouseEvent): number {
    const lay = curLayout;
    if (app.viewLoading || !lay) return -1;
    const p = local(e);
    return hitTest(lay, p.x, p.y);
  }

  function onMove(e: PointerEvent): void {
    if (app.viewLoading) return;
    const p = local(e);
    px = p.x;
    py = p.y;
    cx = e.clientX;
    cy = e.clientY;
    inside = true;
    syncHover(false);
    placeTip();
  }

  function onLeave(): void {
    inside = false;
    syncHover(false);
  }

  function onDown(e: PointerEvent): void {
    if (app.viewLoading) return;
    if (e.button === 0) {
      host.focus({ preventScroll: true });
      keyboardFocus = false;
    }
    if (e.button === 0 && app.menu) closeMenu();
  }

  function onClick(e: MouseEvent): void {
    activateTile(hitAt(e));
  }

  function activateTile(k: number): void {
    if (app.viewLoading) return;
    const view = curView;
    const lay = curLayout;
    if (k < 0 || !view || !lay) return;
    const nodes = view.nodes;
    const nd = nodes[lay.node[k]];
    if ((nd.f & Flag.Aggregate) !== 0) {
      // 「其余 N 项」属于它的父目录：进入父目录；父目录就是视图根时已经在里面了
      const parent = nodes[nd.p];
      if (nd.p > 0 && parent && parent.id >= 0) void zoomTo(parent.id);
      return;
    }
    if (nd.id < 0) return;
    if ((nd.f & Flag.Directory) !== 0) void zoomTo(nd.id);
    else showDetails(nd.id);
  }

  function onKeyDown(e: KeyboardEvent): void {
    if (app.viewLoading && ['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Home', 'End', 'Enter', ' ', 'Backspace', 'Delete'].includes(e.key)) {
      e.preventDefault();
      e.stopPropagation();
      return;
    }
    if (e.isComposing || e.altKey || e.ctrlKey || e.metaKey || app.confirm || app.menu || app.screen !== 'main') return;
    const next = moveTile(accessibleTiles, activeTile, e.key);
    if (next === null && e.key !== 'Enter' && e.key !== ' ' && e.key !== 'Backspace') return;
    e.preventDefault(); // 不让窗口快捷键再次进入目录或返回。
    e.stopPropagation();
    if (app.viewLoading) return;
    keyboardFocus = true;
    if (next !== null) {
      const nd = curView && curLayout && next >= 0 ? curView.nodes[curLayout.node[next]] : null;
      if (nd && nd.id !== app.selectedId) showDetails(nd.id);
    } else if (e.key === 'Backspace') void back();
    else activateTile(activeTile);
  }

  function onContext(e: MouseEvent): void {
    e.preventDefault();
    const k = hitAt(e);
    const view = curView;
    const lay = curLayout;
    if (k < 0 || !view || !lay) return;
    const nodes = view.nodes;
    let nd = nodes[lay.node[k]];
    const agg = (nd.f & Flag.Aggregate) !== 0;
    if (agg) nd = nodes[nd.p];
    if (!nd || nd.id < 0) return;
    // 聚合块弹的是父目录的菜单，但删除得锁住：用户点的是一小块「其余 N 项」，不是整个目录
    openMenu({ x: e.clientX, y: e.clientY, nodeId: nd.id, name: nd.n, isDir: agg || (nd.f & Flag.Directory) !== 0, flags: agg ? nd.f | Flag.Aggregate : nd.f });
  }

  // 鼠标侧键：在 mouseup 上触发；各阶段都拦住，免得外层的全局处理再触发一次。
  function onSideButton(e: MouseEvent): void {
    if (e.button !== 3 && e.button !== 4) return;
    e.stopPropagation();
    if (e.type === 'mouseup') {
      e.preventDefault();
      if (!app.viewLoading && !app.confirm && !app.menu) void (e.button === 3 ? back() : forward());
    }
  }

  onMount(() => {
    painter = createPainter(readTheme(host));
    sceneCtx = sceneEl.getContext('2d');
    overCtx = overEl.getContext('2d');

    const motionQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
    const onMotion = (): void => {
      reducedMotion = motionQuery.matches;
      if (reducedMotion) endZoom();
      sceneDirty = true;
      schedule();
    };
    onMotion();
    motionQuery.addEventListener('change', onMotion);

    const ro = new ResizeObserver((entries) => {
      const r = entries[entries.length - 1].contentRect;
      if (r.width !== size.w || r.height !== size.h) size = { w: r.width, h: r.height };
    });
    ro.observe(host);

    // 换显示器 / 改缩放比例时 CSS 尺寸不变、物理像素变了：监听分辨率媒体查询（每次触发后要换成新比例的查询）
    let dprQuery: MediaQueryList | null = null;
    const onDpr = (): void => {
      sceneDirty = true;
      schedule();
      watchDpr();
    };
    const watchDpr = (): void => {
      dprQuery?.removeEventListener('change', onDpr);
      dprQuery = window.matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`);
      dprQuery.addEventListener('change', onDpr);
    };
    watchDpr();

    const onFonts = (): void => {
      painter?.reset();
      fontsTick++;
    };
    void Promise.all(painter.fonts.map((f) => document.fonts.load(f))).then(onFonts, () => {});
    document.fonts.addEventListener('loadingdone', onFonts);

    const side = ['pointerdown', 'pointerup', 'mousedown', 'mouseup', 'auxclick'] as const;
    host.addEventListener('pointermove', onMove);
    host.addEventListener('pointerleave', onLeave);
    host.addEventListener('pointerdown', onDown);
    host.addEventListener('click', onClick);
    host.addEventListener('contextmenu', onContext);
    for (const t of side) host.addEventListener(t, onSideButton as EventListener);

    ready = true;
    return () => {
      ready = false;
      cancelAnimationFrame(raf);
      raf = 0;
      endZoom();
      ro.disconnect();
      motionQuery.removeEventListener('change', onMotion);
      dprQuery?.removeEventListener('change', onDpr);
      document.fonts.removeEventListener('loadingdone', onFonts);
      host.removeEventListener('pointermove', onMove);
      host.removeEventListener('pointerleave', onLeave);
      host.removeEventListener('pointerdown', onDown);
      host.removeEventListener('click', onClick);
      host.removeEventListener('contextmenu', onContext);
      for (const t of side) host.removeEventListener(t, onSideButton as EventListener);
      if (hoverTile >= 0) setHover(-1);
    };
  });
</script>

<div class="tm" bind:this={host} role="listbox" tabindex="0"
  aria-label={t('资源地图：方块面积表示{metric}', { metric: metricLabel })}
  aria-describedby={`${mapId}-help`}
  aria-activedescendant={!app.viewLoading && activeNode ? `${mapId}-active-${activeNode.id}` : undefined}
  aria-busy={app.viewLoading}
  aria-disabled={app.viewLoading}
  onkeydown={onKeyDown} onfocus={() => (keyboardFocus = true)} onblur={() => (keyboardFocus = false)}>
  <canvas class="layer" aria-hidden="true" bind:this={sceneEl}></canvas>
  <canvas class="layer" aria-hidden="true" bind:this={overEl}></canvas>
  <span class="sr-only" id={`${mapId}-help`}>{t('上或左方向键选择上一项，下或右方向键选择下一项，Home 和 End 跳到首尾。Enter 或空格进入文件夹、查看文件；Backspace 返回。')}</span>
  {#if activeNode && !app.viewLoading}
    <span class="sr-only" id={`${mapId}-active-${activeNode.id}`} role="option" aria-selected="true" aria-posinset={activePosition + 1} aria-setsize={accessibleTiles.length}>
      {t('{name}，{kind}，{metric} {size}', { name: activeNode.n, kind: (activeNode.f & Flag.Directory) !== 0 ? t('文件夹') : t('文件@@单个'), metric: metricLabel, size: formatBytes(activeNode.v) })}
    </span>
  {/if}
  {#if keyboardFocus && !app.viewLoading}
    <div class="keyboard-hint" aria-hidden="true">
      {#if activeNode}<strong>{activeNode.n}</strong><span>{activePosition + 1} / {accessibleTiles.length} · </span>{/if}
      {t('↑← 上一项 · ↓→ 下一项 · Enter 进入 / 查看 · Backspace 返回')}
    </div>
  {/if}
  {#if empty}
    <p class="empty">{app.view?.nodes[0]?.c === 0 ? t('这个文件夹是空的。') : t('这里没有占用空间的内容。')}</p>
  {/if}
  {#if tip && !app.menu && !app.viewLoading}
    <div class="tip float" bind:this={tipEl}>
      <div class="tip-head">
        <span class="dot" style:background={tip.dot}></span>
        <strong class="tip-name">{tip.name}</strong>
        <span class="tip-tag">{tip.purpose}</span>
      </div>
      <div class="tip-path mono">{tip.path}</div>
      <dl class="tip-rows">
        <dt>{tip.sizeLabel}</dt>
        <dd class="num">{tip.size}</dd>
        <dt>{t('占当前视图')}</dt>
        <dd class="num">{tip.percent}</dd>
        {#if tip.kids}
          <dt>{tip.kidsLabel}</dt>
          <dd class="num">{tip.kids}</dd>
        {/if}
      </dl>
    </div>
  {/if}
</div>

<style>
  .tm {
    position: absolute;
    inset: 0;
    overflow: hidden;
    background: var(--card);
  }
  .layer {
    position: absolute;
    inset: 0;
    width: 100%;
    height: 100%;
    pointer-events: none;
    transform-origin: 0 0;
  }
  .tm:focus-visible {
    outline: 3px solid var(--ink);
    outline-offset: -3px;
  }
  .sr-only {
    position: absolute;
    width: 1px;
    height: 1px;
    padding: 0;
    overflow: hidden;
    clip-path: inset(50%);
    white-space: nowrap;
  }
  .keyboard-hint {
    position: absolute;
    right: 12px;
    bottom: 12px;
    left: 12px;
    padding: 7px 10px;
    border: 1px solid var(--line);
    border-radius: var(--r-ctl);
    background: var(--card);
    font-size: 12px;
    pointer-events: none;
  }
  .keyboard-hint strong {
    display: block;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .empty {
    position: absolute;
    inset: 0;
    display: grid;
    place-items: center;
    margin: 0;
    color: var(--muted);
    pointer-events: none;
  }
  .tip {
    position: fixed;
    left: 0;
    top: 0;
    z-index: 20;
    width: max-content;
    max-width: 320px;
    padding: 10px 12px;
    font-size: 12px;
    line-height: 1.5;
    pointer-events: none;
  }
  .tip-head {
    display: flex;
    align-items: center;
    gap: 8px;
    min-width: 0;
  }
  .tip-name {
    min-width: 0;
    overflow: hidden;
    font-size: 13px;
    font-weight: 600;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .tip-tag {
    flex: none;
    margin-left: auto;
    padding-left: 8px;
    color: var(--muted);
    font-size: 11px;
  }
  .tip-path {
    display: -webkit-box;
    margin: 4px 0 8px;
    overflow: hidden;
    color: var(--muted);
    font-size: 11px;
    line-height: 1.45;
    overflow-wrap: anywhere;
    -webkit-box-orient: vertical;
    -webkit-line-clamp: 3;
    line-clamp: 3;
  }
  .tip-rows {
    display: grid;
    grid-template-columns: auto auto;
    justify-content: space-between;
    gap: 2px 20px;
    margin: 0;
  }
  .tip-rows dt {
    color: var(--muted);
  }
  .tip-rows dd {
    margin: 0;
    text-align: right;
  }
</style>
