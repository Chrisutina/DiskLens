<script lang="ts">
  // 条目所在的文件夹：名字已经写在上一行，这里只写位置（完整路径放在悬停提示里）。
  // 从中间省略：开头（顶层目录）和结尾（上一级）都留着，窄了只缩中间；用户目录写成 ~。
  // 用两段 flex 实现而不是量宽度，字体回落到雅黑时也不会算错。
  import { app } from '../lib/state.svelte.ts';
  import { relativePath } from '../lib/format.ts';

  let { path, tail = 20 }: { path: string; tail?: number } = $props();

  const shown = $derived.by(() => {
    const i = path.lastIndexOf('\\');
    // 盘符根目录要留住结尾的反斜杠（C:\），其余去掉名字
    const dir = i < 0 ? path : i <= 2 ? path.slice(0, i + 1) : path.slice(0, i);
    const home = app.info?.homePath?.replace(/\\+$/, '');
    if (!home) return dir;
    if (dir.toLowerCase() === home.toLowerCase()) return '~';
    const rest = relativePath(dir, home);
    return rest === dir ? dir : `~\\${rest}`;
  });
  const cut = $derived(Math.max(0, shown.length - tail));
</script>

<span class="mid mono" title={path}>
  <span class="head">{shown.slice(0, cut)}</span><span class="tail">{shown.slice(cut)}</span>
</span>

<style>
  .mid {
    display: flex;
    width: 100%;
    min-width: 0;
    color: var(--muted);
    font-size: 12px;
    line-height: 16px;
    white-space: pre;
  }
  .head {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .tail {
    flex: none;
  }
</style>
