<script lang="ts">
  import { Archive, CircleDashed, Code, Cpu, Database, Disc, File, FileText, Film, Folder, Image, Music, Settings } from '@lucide/svelte';
  import { catColor } from '../lib/categories.ts';

  let { k, size = 16, tinted = true }: { k: number; size?: number; tinted?: boolean } = $props();

  const ICONS = [File, Folder, Film, Image, Music, FileText, Archive, Code, Cpu, Settings, Disc, Database, CircleDashed];
  const Icon = $derived(ICONS[k] ?? File);
  // 文件夹和未统计的底色太暗，当图标颜色看不见，用次要文字色
  const color = $derived(!tinted ? 'currentColor' : k === 1 || k === 12 ? 'var(--muted)' : catColor(k));
</script>

<Icon {size} {color} strokeWidth={1.75} aria-hidden="true" />
