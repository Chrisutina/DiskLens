<script lang="ts">
  // 详情里的「关联」（dise §8.1）：它属于谁、承担什么角色、同一所属对象还有哪些模块、依据是什么。
  // 关联只用于浏览和定位，不进清理勾选；更深的探索在生长树里。
  import { untrack } from 'svelte';
  import { CornerUpLeft, GitBranch, LocateFixed } from '@lucide/svelte';
  import { app } from '../lib/state.svelte.ts';
  import { RelatedData, relatedReturn, relatedTree } from '../lib/related.svelte.ts';
  import { RELATION_LABELS } from '../lib/relatedModel.ts';
  import { EVIDENCE_NAMES, role } from '../lib/roles.ts';
  import { formatBytes } from '../lib/format.ts';
  import type { NodeDetails } from '../lib/types.ts';
  import { t } from '../lib/i18n.ts';

  let { node }: { node: NodeDetails } = $props();
  const data = new RelatedData();
  const LIST = 8;

  $effect(() => {
    const id = node.id, path = node.path, scanId = app.summary?.scanId;
    if (scanId == null) return;
    untrack(() => { data.reset(id, path, scanId); void data.load(); });
    return () => data.dispose();
  });

  const items = $derived(data.result?.items ?? []);
  const back = $derived(relatedReturn.point && relatedReturn.point.id !== node.id ? relatedReturn.point : null);
  const name = (p: string): string => p.split(/[\\/]/).filter(Boolean).at(-1) ?? p;
  const val = (x: { size: number; alloc: number }): number => (app.metric === 'alloc' ? x.alloc : x.size);
</script>

<section class="related" aria-label={t('关联')} aria-busy={data.loading}>
  <h4>{t('关联')} {#if data.result}<span class="num">{t('{n} 条', { n: data.result.total })}</span>{/if}</h4>
  {#if back}
    <button type="button" class="btn back" onclick={() => void data.locate(back.id, back.path, true)}><CornerUpLeft size={14} />{t('返回「{name}」', { name: name(back.path) })}</button>
  {/if}
  {#if data.loading && !data.result}
    <p class="muted" role="status">{t('正在读取关联对象…')}</p>
  {:else if data.error}
    <p class="muted" role="alert">{t('关联读取失败：{error}', { error: data.error })}</p>
    <button type="button" class="btn" onclick={() => void data.load()}>{t('重试')}</button>
  {:else if data.result}
    {#if items.length}
      <ul>
        {#each items.slice(0, LIST) as it (it.id)}
          <li>
            <div class="main">
              <span class="n" title={it.path}>{it.name}</span>
              <span class="k">{RELATION_LABELS[it.relation]} · {EVIDENCE_NAMES[it.evidence]} · {role(it.role).name}</span>
            </div>
            <span class="v num">{formatBytes(val(it))}</span>
            <button type="button" class="btn btn-ghost btn-icon" aria-label={t('在地图中定位 {name}', { name: it.name })} title={t('在地图中定位')} onclick={() => void data.locate(it.id, it.path)}><LocateFixed size={14} /></button>
          </li>
        {/each}
      </ul>
      {#if data.result.total > Math.min(LIST, items.length)}<p class="muted">{t('另有 {n} 条，在生长树里查看。', { n: data.result.total - Math.min(LIST, items.length) })}</p>{/if}
    {:else}
      <p class="muted">{data.result.note ?? t('尚未识别关联对象')}</p>
    {/if}
    <p class="muted">{t('只用于浏览和定位：属于同一个应用不等于可以整组删除，不会扩大清理范围。')}</p>
    <button type="button" class="btn" onclick={() => app.summary && relatedTree.show(node.id, node.path, app.summary.scanId)}><GitBranch size={14} />{t('查看生长树')}</button>
  {/if}
</section>

<style>
  .related { display: flex; flex-direction: column; gap: 8px; padding-top: 16px; border-top: 1px solid var(--line-soft); }
  h4 { display: flex; align-items: baseline; justify-content: space-between; margin: 0; font-size: 13px; font-weight: 600; }
  h4 .num { color: var(--muted); font-size: 12px; font-weight: 400; }
  ul { margin: 0; padding: 0; list-style: none; }
  li { display: grid; grid-template-columns: minmax(0, 1fr) auto auto; align-items: center; gap: 8px; padding: 6px 0; border-bottom: 1px solid var(--line-soft); }
  .main { display: flex; flex-direction: column; min-width: 0; }
  .n { overflow: hidden; font-size: 13px; text-overflow: ellipsis; white-space: nowrap; }
  .k { color: var(--muted); font-size: 11px; overflow-wrap: anywhere; }
  .v { color: var(--muted); font-size: 12px; }
  .muted { margin: 0; color: var(--muted); font-size: 12px; line-height: 18px; }
  .back { align-self: flex-start; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .related > .btn { align-self: flex-start; }
</style>
