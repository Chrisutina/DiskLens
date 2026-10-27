<script lang="ts">
  // 应用占用维度（dise §5.3）：当前地图目录按所属对象汇总。只算这个范围里的部分，不是应用在整台机器上的总占用；
  // 跨对象的硬链接没法分给哪一方，单列「共享」，不重复计入；没有可靠归属的列「未归属」。
  import { LocateFixed } from '@lucide/svelte';
  import { app, loadOwners, reveal, setDimension } from '../lib/state.svelte.ts';
  import { formatBytes, formatPercent } from '../lib/format.ts';
  import { EVIDENCE_NAMES, OWNER_KIND_NAMES, role } from '../lib/roles.ts';
  import type { OwnerUsageItem } from '../lib/types.ts';
  import { t } from '../lib/i18n.ts';

  const u = $derived(app.owners);
  const alloc = $derived(app.metric === 'alloc');
  const pick = <T extends { sizeInScope: number; allocInScope: number }>(x: T): number => (alloc ? x.allocInScope : x.sizeInScope);
  const total = $derived(u ? (alloc ? u.totalAllocInScope : u.totalSizeInScope) : 0);
  const scope = $derived(app.rootDetails?.path ?? app.summary?.rootPath ?? '');

  /** 构成里最大的三种角色 */
  function topRoles(o: OwnerUsageItem): { name: string; bytes: number }[] {
    return o.roleBytesInScope.map((bytes, r) => ({ name: role(r).name, bytes })).filter((x) => x.bytes > 0)
      .sort((a, b) => b.bytes - a.bytes).slice(0, 3);
  }

  /** 定位一处根：切回目录分布，再走统一导航 */
  async function locate(id: number): Promise<void> {
    setDimension('dirs');
    await reveal(id);
  }
</script>

<section class="owners" aria-label={t('应用占用')} aria-busy={app.ownersLoading}>
  <p class="scope">{t('当前范围')} <span class="mono">{scope}</span> · {t('按所属对象汇总，只算这个范围里的部分，不是应用在整台机器上的总占用。')}</p>
  {#if app.ownersError}
    <div class="msg" role="alert"><p>{t('读取失败：{error}', { error: app.ownersError })}</p><button type="button" class="btn" onclick={() => void loadOwners()}>{t('重试')}</button></div>
  {:else if !u}
    <p class="msg" role="status">{t('正在按所属对象汇总…')}</p>
  {:else}
    <div class="totals">
      <span>{t('已归属')} <b class="num">{formatBytes(alloc ? u.ownedAllocInScope : u.ownedSizeInScope)}</b></span>
      <span title={t('跨对象的硬链接，没法可靠地分给哪一方，单列、不重复计入')}>{t('共享')} <b class="num">{formatBytes(alloc ? u.sharedAllocInScope : u.sharedSizeInScope)}</b></span>
      <span>{t('未归属')} <b class="num">{formatBytes(alloc ? u.unownedAllocInScope : u.unownedSizeInScope)}</b></span>
      <span>{t('合计')} <b class="num">{formatBytes(total)}</b>{t('（和地图上这个目录的大小一致）')}</span>
    </div>
    {#if u.items.length}
      <ol class="rows">
        {#each u.items as o (o.kind + ':' + o.name)}
          {@const v = pick(o)}
          <li>
            <div class="line">
              <b class="name" title={o.name}>{o.name}</b>
              <span class="meta">{OWNER_KIND_NAMES[o.kind]} · {EVIDENCE_NAMES[o.evidence]}</span>
              <span class="num val">{formatBytes(v)}</span>
              <span class="num pct">{formatPercent(total > 0 ? v / total : 0)}</span>
            </div>
            <span class="track"><i style:width="{total > 0 ? (v / total) * 100 : 0}%"></i></span>
            <p class="roles">{topRoles(o).map((r) => `${r.name} ${formatBytes(r.bytes)}`).join(' · ') || t('角色未识别')}</p>
            <details>
              <summary>{t('{n} 处位置', { n: o.roots.length })}</summary>
              <ul>
                {#each o.roots as r (r.id)}
                  <li>
                    <span class="mono path" title={r.path}>{r.path}</span>
                    <span class="num">{formatBytes(pick(r))}</span>
                    <span class="meta">{EVIDENCE_NAMES[r.evidence]}</span>
                    <button type="button" class="btn btn-ghost btn-icon" aria-label={t('在地图中定位 {name}', { name: r.path })} title={t('在地图中定位')} onclick={() => void locate(r.id)}><LocateFixed size={14} /></button>
                  </li>
                {/each}
              </ul>
            </details>
          </li>
        {/each}
      </ol>
      {#if u.ownerCount > u.items.length}<p class="msg">{t('另有 {n} 个对象没有列出（只列最大的 {shown} 个）。', { n: u.ownerCount - u.items.length, shown: u.items.length })}</p>{/if}
    {:else}
      <p class="msg">{t('这个范围里尚未识别出所属对象。')}</p>
    {/if}
    <p class="note">{t('归属只说明数据属于谁，不代表能不能删；同一个应用的数据不能整组删除。')}</p>
  {/if}
</section>

<style>
  .owners { display: flex; flex-direction: column; gap: 8px; min-width: 0; min-height: 0; padding: 10px 16px; overflow-y: auto; }
  .scope, .note, .msg { margin: 0; color: var(--muted); font-size: 12px; line-height: 18px; overflow-wrap: anywhere; }
  .msg p { margin: 0 0 8px; }
  .totals { display: flex; flex-wrap: wrap; gap: 4px 16px; font-size: 12px; color: var(--muted); }
  .totals b { color: var(--ink); font-weight: 600; }
  .rows { margin: 0; padding: 0; list-style: none; }
  .rows > li { padding: 8px 0; border-bottom: 1px solid var(--line-soft); }
  .line { display: grid; grid-template-columns: minmax(0, auto) minmax(0, 1fr) auto 56px; align-items: baseline; gap: 8px; }
  .name { overflow: hidden; font-size: 13px; text-overflow: ellipsis; white-space: nowrap; }
  .meta { color: var(--muted); font-size: 11px; }
  .val { font-size: 13px; }
  .pct { color: var(--muted); font-size: 12px; text-align: right; }
  .track { display: block; height: 5px; margin-top: 5px; overflow: hidden; border-radius: 3px; background: var(--line-soft); }
  .track i { display: block; height: 100%; border-radius: 3px; background: var(--workspace-accent, #3f6fb3); opacity: .7; }
  .roles { margin: 4px 0 0; color: var(--muted); font-size: 11px; }
  details { margin-top: 4px; font-size: 12px; }
  summary { color: var(--workspace-accent, #3f6fb3); cursor: pointer; }
  details ul { margin: 4px 0 0; padding: 0; list-style: none; }
  details li { display: grid; grid-template-columns: minmax(0, 1fr) auto auto 28px; align-items: center; gap: 8px; padding: 2px 0; }
  .path { overflow: hidden; font-size: 11px; text-overflow: ellipsis; white-space: nowrap; }
</style>
