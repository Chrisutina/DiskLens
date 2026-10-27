<script module lang="ts">
  import { api } from '../lib/api.ts';
  import {
    app,
    closeMenu,
    copyText,
    openInExplorer,
    requestDelete,
    engineLabel,
    searchWeb,
    showDetails,
    showProperties,
    toast,
    zoomTo,
  } from '../lib/state.svelte.ts';
  import { Flag, hasFlag } from '../lib/types.ts';
  import { t } from '../lib/i18n.ts';

  /** 复制完整路径。视图节点只带名字：选中节点的详情已经在手就直接用，否则现取 */
  export async function copyNodePath(nodeId: number): Promise<void> {
    try {
      const d = app.details?.id === nodeId ? app.details : await api.details(nodeId, app.metric);
      await copyText(d.path, t('路径'));
    } catch (e) {
      toast(t('无法读取路径：{error}', { error: e instanceof Error ? e.message : String(e) }), 'error');
    }
  }
</script>

<script lang="ts">
  interface Item {
    label: string;
    danger?: boolean;
    disabled?: boolean;
    run: () => void;
  }

  const m = $derived(app.menu);

  // 删除项为什么不可用：这三类没有对应的磁盘文件，后端也会拦，但不如一开始就说清楚
  const blockReason = $derived.by(() => {
    if (!m) return null;
    if (m.nodeId < 0 || hasFlag(m.flags, Flag.Aggregate)) return t('这是合并显示的小条目，没有对应的单个文件');
    if (hasFlag(m.flags, Flag.Virtual)) return t('虚拟条目，磁盘上没有对应的文件');
    if (hasFlag(m.flags, Flag.Metafile)) return t('NTFS 元数据，不能删除');
    return null;
  });

  // 分组：组与组之间画分隔线
  const groups = $derived.by((): Item[][] => {
    if (!m) return [];
    const id = m.nodeId;
    const locked = blockReason !== null;
    const act = (fn: () => unknown) => () => {
      closeMenu();
      void fn();
    };
    return [
      [
        ...(m.isDir ? [{ label: t('打开'), run: act(() => zoomTo(id)) }] : []),
        { label: t('在资源管理器中显示'), run: act(() => openInExplorer(id)) },
        { label: t('在网上搜索（{engine}）', { engine: engineLabel() }), run: act(() => searchWeb(id)) },
        { label: t('复制路径'), run: act(() => copyNodePath(id)) },
        { label: t('复制名称'), run: act(() => copyText(m.name, t('名称@@复制'))) },
        { label: t('属性'), run: act(() => showProperties(id)) },
        { label: t('查看详情'), run: act(() => showDetails(id)) },
      ],
      [
        { label: t('移到回收站'), disabled: locked, run: act(() => requestDelete([id], false)) },
        { label: t('永久删除…'), danger: true, disabled: locked, run: act(() => requestDelete([id], true)) },
      ],
    ];
  });

  const flat = $derived(groups.flat());
  let active = $state(-1);
  let menuW = $state(0);
  let menuH = $state(0);
  let el = $state<HTMLElement>();

  // 菜单换了目标就重新来过，并把焦点交给菜单，方向键才有处
  $effect(() => {
    if (m) {
      active = -1;
      el?.focus();
    }
  });

  // 贴着鼠标，但不能伸出窗口
  const left = $derived(m ? Math.max(8, Math.min(m.x, window.innerWidth - menuW - 8)) : 0);
  const top = $derived(m ? Math.max(8, Math.min(m.y, window.innerHeight - menuH - 8)) : 0);

  function step(dir: 1 | -1) {
    for (let i = 1; i <= flat.length; i++) {
      const next = (active + dir * i + flat.length * 2) % flat.length;
      if (!flat[next].disabled) return (active = next);
    }
  }

  function onkeydown(e: KeyboardEvent) {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      if (active < 0 && e.key === 'ArrowUp') active = 0;
      step(e.key === 'ArrowDown' ? 1 : -1);
    } else if (e.key === 'Enter' || e.key === ' ') {
      if (active >= 0) flat[active].run();
    } else if (e.key === 'Tab') {
      closeMenu();
    } else return; // Esc 交给全局处理
    // 菜单吃掉的键不能再冒泡：run() 已经把菜单关了，全局的回车处理看到「没有菜单」会再「进入选中的目录」
    e.preventDefault();
    e.stopPropagation();
  }

</script>

<svelte:window
  onpointerdown={(e) => m && !el?.contains(e.target as Node) && closeMenu()}
  onblur={closeMenu}
  onresize={closeMenu}
/>

{#if m}
  <div
    class="float menu"
    role="menu"
    tabindex="-1"
    aria-label={m.name}
    bind:this={el}
    bind:clientWidth={menuW}
    bind:clientHeight={menuH}
    style:left="{left}px"
    style:top="{top}px"
    {onkeydown}
    oncontextmenu={(e) => e.preventDefault()}
  >
    {#each groups as group, g (g)}
      {#if g > 0}<div class="sep" role="separator"></div>{/if}
      {#each group as it (it.label)}
        {@const i = flat.indexOf(it)}
        <button
          type="button"
          role="menuitem"
          tabindex="-1"
          class="item"
          class:danger={it.danger}
          class:active={i === active}
          disabled={it.disabled}
          onpointermove={() => !it.disabled && (active = i)}
          onclick={it.run}
        >
          {it.label}
        </button>
      {/each}
    {/each}
    {#if blockReason}<p class="why">{blockReason}</p>{/if}
  </div>
{/if}

<style>
  .menu {
    position: fixed;
    z-index: 40;
    width: 232px;
    padding: 6px;
    outline: 0;
  }
  .item {
    display: block;
    width: 100%;
    height: 32px;
    padding: 0 10px;
    border: 0;
    border-radius: 6px;
    background: transparent;
    font-size: 13px;
    text-align: left;
    cursor: pointer;
  }
  .item.active {
    background: var(--hover);
  }
  .item.danger {
    color: var(--danger);
  }
  .item.danger.active {
    background: var(--danger-bg);
  }
  .item:disabled {
    color: var(--faint);
    cursor: not-allowed;
  }
  .sep {
    height: 1px;
    margin: 6px 4px;
    background: var(--line-soft);
  }
  .why {
    margin: 4px 10px 4px;
    color: var(--muted);
    font-size: 12px;
    line-height: 1.4;
  }
</style>
