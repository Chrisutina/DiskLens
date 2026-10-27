<script lang="ts">
  import { onMount } from 'svelte';
  import { app, back, closeMenu, closePanel, closeSearch, focusSearch, forward, init, rescan, requestDelete, up, zoomTo } from './lib/state.svelte.ts';
  import { relatedTree } from './lib/related.svelte.ts';
  import StartScreen from './components/StartScreen.svelte';
  import ScanOverlay from './components/ScanOverlay.svelte';
  import TopBar from './components/TopBar.svelte';
  import MapSummary from './components/MapSummary.svelte';
  import ExplorerCard from './components/ExplorerCard.svelte';
  import RecentFiles from './components/RecentFiles.svelte';
  import RightPanel from './components/RightPanel.svelte';
  import './styles/workspace.css';
  import ContextMenu, { copyNodePath } from './components/ContextMenu.svelte';
  import ConfirmDialog from './components/ConfirmDialog.svelte';
  import Toasts from './components/Toasts.svelte';

  onMount(() => {
    void init();
  });

  const ALT_NAV: Record<string, () => Promise<void>> = { ArrowLeft: back, ArrowRight: forward, ArrowUp: up };

  /** 正在往文本框里打字：这时 Backspace / Delete / Ctrl+C 是编辑文字，不是导航和删除 */
  function typing(t: EventTarget | null): boolean {
    if (!(t instanceof HTMLElement)) return false;
    if (t instanceof HTMLInputElement) return !['checkbox', 'radio', 'button'].includes(t.type);
    return t instanceof HTMLTextAreaElement || t.isContentEditable;
  }

  function onkeydown(e: KeyboardEvent) {
    if (e.defaultPrevented) return; // 对话框、菜单已经处理过的键，别再处理一遍
    // F5 要在所有界面都拦住：浏览器默认是刷新页面，状态全丢
    if (e.key === 'F5') {
      e.preventDefault();
      if (app.screen === 'main' && !app.confirm) rescan();
      return;
    }
    if (e.key === 'Escape') {
      if (app.confirm) return; // 对话框自己处理
      if (app.menu) {
        closeMenu();
        e.preventDefault();
      } else if (app.searchOpen) closeSearch();
      else if (app.screen === 'main' && app.page === 'browse' && app.panelOpen) {
        closePanel();
        e.preventDefault();
      }
      return;
    }
    // 扫描中、对话框和菜单打开时，其余快捷键全部停用（菜单自己吃方向键和回车）；
    // 生长树盖着地图时也停用：Backspace、Delete 作用在看不见的地图上会出人意料
    if (app.screen !== 'main' || app.confirm || app.menu || relatedTree.open) return;

    const mod = e.ctrlKey || e.metaKey;
    const key = e.key.toLowerCase();
    if (mod && key === 'f') {
      e.preventDefault();
      focusSearch();
      return;
    }
    if (mod && key === 'c') {
      if (app.page !== 'browse') return;
      // 用户选中了文字要复制，别抢
      if (typing(e.target) || window.getSelection()?.toString()) return;
      e.preventDefault();
      if (app.viewLoading || app.detailsLoading) return;
      void copyNodePath(app.selectedId);
      return;
    }
    if (e.altKey) {
      const go = ALT_NAV[e.key];
      if (go) {
        e.preventDefault();
        void go();
      }
      return;
    }
    if (typing(e.target)) return;
    if (e.key === 'Backspace') {
      e.preventDefault();
      void back();
    } else if (e.key === 'Delete') {
      if (app.page !== 'browse') return;
      e.preventDefault();
      if (app.viewLoading || app.detailsLoading) return;
      void requestDelete([app.selectedId], e.shiftKey);
    } else if (e.key === 'Enter') {
      if (app.page !== 'browse') return;
      if (app.viewLoading || app.detailsLoading) return;
      // 焦点在按钮、列表项上时，回车是它们自己的事
      if ((e.target as HTMLElement | null)?.closest?.('button, a, input, select, textarea, [role="option"], [role="menuitem"]')) return;
      if (app.details?.id === app.selectedId && app.details.isDir) void zoomTo(app.selectedId);
    }
  }

  /** 鼠标侧键：3 后退、4 前进。不拦的话 WebView2 会按浏览器历史处理 */
  function onmouse(e: MouseEvent) {
    if (e.button !== 3 && e.button !== 4) return;
    e.preventDefault();
    if (e.type === 'mouseup' && app.screen === 'main' && !app.confirm && !app.menu) void (e.button === 3 ? back() : forward());
  }
</script>

<!-- 屏蔽浏览器自带的右键菜单（输入框除外，要留着复制粘贴） -->
<svelte:window {onkeydown} onmousedown={onmouse} onmouseup={onmouse} onauxclick={onmouse} oncontextmenu={(e) => typing(e.target) || e.preventDefault()} />

{#if app.screen === 'start'}
  <StartScreen />
{:else if app.screen === 'scanning'}
  <ScanOverlay />
{:else}
  <div class="page workspace">
    <TopBar />
    <MapSummary />
    <main class:split={app.page === 'browse' && app.panelOpen}>
      {#if app.page === 'recent'}
        <RecentFiles />
      {:else}
        <ExplorerCard />
        {#if app.panelOpen}<RightPanel />{/if}
      {/if}
    </main>
  </div>
{/if}

<ContextMenu />
<ConfirmDialog />
<Toasts />

<style>
  /* 顶栏、紧凑统计与主工作区；地图用完剩余高度，右栏关闭后回收宽度。 */
  .page {
    display: grid;
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: auto auto minmax(0, 1fr);
    gap: 12px;
    height: 100%;
    padding: 12px 18px 16px;
  }
  main {
    display: grid;
    position: relative;
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: minmax(0, 1fr);
    gap: 12px;
    min-width: 0;
    min-height: 0;
  }
  /* 空间浏览页打开了右侧面板：地图让出 380px（窄窗口 340px） */
  main.split {
    grid-template-columns: minmax(0, 1fr) 360px;
  }
  @media (max-width: 1279px) {
    main.split {
      grid-template-columns: minmax(0, 1fr) 340px;
    }
  }
  @media (max-width: 800px) {
    .page { padding: 10px; gap: 10px; }
    main.split { grid-template-columns: minmax(0, 1fr); }
    main.split :global(aside.panel) { position: absolute; z-index: 10; right: 0; top: 0; width: min(360px, 100%); box-shadow: var(--shadow-float); }
  }
</style>
