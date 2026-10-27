<script lang="ts">
  // 大文件标签（DESIGN §9.3）：当前视图根下最大的 100 个文件，单击定位，右键出菜单。
  import { app, loadLargest, openMenu, reveal } from '../lib/state.svelte.ts';
  import { formatBytes } from '../lib/format.ts';
  import { t } from '../lib/i18n.ts';
  import type { FileItem } from '../lib/types.ts';
  import CategoryIcon from './CategoryIcon.svelte';
  import RightPanelPath from './RightPanelPath.svelte';

  const val = (f: FileItem): number => (app.metric === 'alloc' ? f.alloc : f.size);

  function menu(e: MouseEvent, f: FileItem): void {
    e.preventDefault();
    openMenu({ x: e.clientX, y: e.clientY, nodeId: f.id, name: f.name, isDir: f.isDir, flags: f.f });
  }
</script>

<div class="scroll">
  {#if app.largestLoading}
    <div class="skeleton" role="status" aria-label={t('正在查找大文件')}>
      {#each [0, 1, 2, 3, 4, 5, 6, 7] as i (i)}
        <div class="sk-row">
          <i class="ic"></i>
          <span class="sk-text"><i class="l1"></i><i class="l2"></i></span>
          <i class="r"></i>
        </div>
      {/each}
    </div>
  {:else if app.largestError}
    <div class="empty" role="alert">
      <p>{t('大文件列表读取失败：{error}', { error: app.largestError })}</p>
      <button type="button" class="btn" onclick={() => void loadLargest()}>{t('重试')}</button>
    </div>
  {:else if app.largest.length === 0}
    <div class="empty">
      <p class="empty-title">{t('这里没有文件')}</p>
      <p>{t('当前位置下没有文件。回到上一级，或者在资源地图里换一个文件夹看看。')}</p>
    </div>
  {:else}
    <ul>
      {#each app.largest as f (f.id)}
        <li>
          <button type="button" class="lrow" title={f.path} onclick={() => reveal(f.id)} oncontextmenu={(e) => menu(e, f)}>
            <span class="icon"><CategoryIcon k={f.k} size={16} tinted={false} /></span>
            <span class="text">
              <span class="name">{f.name}</span>
              <RightPanelPath path={f.path} />
            </span>
            <span class="size num">{formatBytes(val(f))}</span>
          </button>
        </li>
      {/each}
    </ul>
  {/if}
</div>

<style>
  .scroll {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
  }
  ul {
    margin: 0;
    padding: 0;
    list-style: none;
  }
  .lrow {
    display: flex;
    align-items: center;
    gap: 12px;
    width: 100%;
    min-height: 56px;
    padding: 8px 16px;
    border: 0;
    border-bottom: 1px solid var(--line-soft);
    background: none;
    text-align: left;
    cursor: pointer;
    transition: background var(--dur-fast);
  }
  .lrow:hover {
    background: var(--hover);
  }
  .lrow:focus-visible {
    outline-offset: -2px;
  }
  .icon {
    display: grid;
    flex: none;
    place-items: center;
    width: 18px;
    color: var(--muted);
  }
  .text {
    display: flex;
    flex: 1;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
  }
  .name {
    overflow: hidden;
    font-size: 14px;
    font-weight: 500;
    line-height: 20px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .size {
    flex: none;
    font-size: 14px;
    font-weight: 600;
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

  /* 骨架屏不做循环动画（§8.7） */
  .sk-row {
    display: flex;
    align-items: center;
    gap: 12px;
    height: 56px;
    padding: 0 16px;
    border-bottom: 1px solid var(--line-soft);
  }
  .skeleton i {
    display: block;
    background: var(--line-soft);
    border-radius: 4px;
  }
  .ic {
    flex: none;
    width: 18px;
    height: 18px;
  }
  .sk-text {
    display: flex;
    flex: 1;
    flex-direction: column;
    gap: 8px;
  }
  .l1 {
    width: 50%;
    height: 12px;
  }
  .l2 {
    width: 70%;
    height: 10px;
  }
  .r {
    flex: none;
    width: 52px;
    height: 12px;
  }
</style>
