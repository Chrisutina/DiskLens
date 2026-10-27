<script lang="ts">
  // 清理建议（清理页主体）：三组药丸 + 行列表，页脚固定「已选 · 选中安全项 · 立即清理 · 复制脚本」。
  import { app, copyCleanupScript, requestCleanupSelected, selectSafe, loadSuggestions, openPanel, setPage } from '../lib/state.svelte.ts';
  import { compareSuggestions } from '../lib/cleanup.ts';
  import { cleanupTasks } from '../lib/cleanupTasks.svelte.ts';
  import { formatBytes } from '../lib/format.ts';
  import type { SuggestionGroup } from '../lib/types.ts';
  import { t, type MsgId } from '../lib/i18n.ts';
  import RecommendRow from './RecommendRow.svelte';

  const GROUPS: { group: SuggestionGroup; label: MsgId; pill: string }[] = [
    { group: 'safe', label: '可放心清理', pill: 'pill-safe' },
    { group: 'caution', label: '先确认', pill: 'pill-review' },
    { group: 'protected', label: '别删 · 用正确的方法', pill: 'pill-muted' },
  ];

  // 组表示风险；同组优先当前可执行的项目，再看空间。
  const sections = $derived(
    GROUPS.map((g) => {
      const items = app.suggestions.filter((s) => s.group === g.group).sort((a, b) => compareSuggestions(a, b, app.info?.isAdmin === true));
      return { ...g, items, bytes: items.reduce((n, s) => n + s.bytes, 0) };
    }).filter((g) => g.items.length > 0),
  );
  const sel = $derived(app.cleanupStats.selected);
</script>

<div class="list">
  {#if app.suggestionsLoading && app.suggestions.length === 0}
    <div class="skeleton" role="status" aria-label={t('正在分析清理建议')}>
      {#each [0, 1, 2, 3, 4] as i (i)}
        <div class="sk-row">
          <i class="box"></i>
          <span class="sk-text"><i class="l1"></i><i class="l2"></i></span>
          <i class="r"></i>
        </div>
      {/each}
    </div>
  {:else if app.suggestionsError}
    <div class="empty" role="alert">
      <p class="empty-title">{t('清理建议加载失败')}</p>
      <p>{app.suggestionsError}</p>
      <button type="button" class="btn" onclick={() => void loadSuggestions()}>{t('重试')}</button>
    </div>
  {:else if sections.length === 0}
    <div class="empty">
      <p class="empty-title">{t('没有找到可清理的项')}</p>
      <p>{t('这个位置下没有常见的缓存、临时文件和构建产物。去「大文件」看看哪些文件占得最多。')}</p>
      <button type="button" class="btn" onclick={() => { setPage('browse'); openPanel('largest'); }}>{t('查看大文件')}</button>
    </div>
  {:else}
    {#each sections as g (g.group)}
      <div class="ghead">
        <span class="pill {g.pill}">{t(g.label)}</span>
        <span class="gsum num" title={t('扫描占用合计，范围可能重叠，不等于净释放量')}>{t('占用合计上限 {size}', { size: formatBytes(g.bytes) })}</span>
      </div>
      {#each g.items as s (s.id)}
        <RecommendRow {s} />
      {/each}
    {/each}
  {/if}
</div>

<footer class="foot">
  <p class="stat"><b class="num">{formatBytes(sel.bytes)}</b> {t('已选')} · <span class="num">{t('{n} 项', { n: sel.count })}</span></p>
  {#if sel.count > 0}
    <p class="estimate" title={t('扫描估算；范围重叠、硬链接和共享数据尚未全部扣除，实际释放以完成后为准。')}>{t('预计立即释放上限 {size}', { size: formatBytes(sel.immediateBytes) })}{#if sel.recycleBytes > 0} · {t('回收站待释放上限 {size}', { size: formatBytes(sel.recycleBytes) })}{/if}{#if sel.manualBytes > 0} · {t('手动处理占用 {size}', { size: formatBytes(sel.manualBytes) })}{/if}{t('（未确认净释放量）')}</p>
  {/if}
  <div class="btns">
    <button type="button" class="btn" onclick={selectSafe}>{t('选中安全项')}</button>
    <button type="button" class="btn" disabled={sel.count === 0 || app.suggestionsLoading || cleanupTasks.active} title={cleanupTasks.active ? t('上一批清理任务还没结束或状态待确认') : undefined} onclick={requestCleanupSelected}>{t('立即清理…')}</button>
    <button type="button" class="btn btn-primary" disabled={sel.count === 0 || app.scriptLoading || app.suggestionsLoading} aria-busy={app.scriptLoading} onclick={copyCleanupScript}>{app.scriptLoading ? t('正在生成脚本…') : t('复制脚本')}</button>
  </div>
</footer>

<style>
  .list {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
  }
  /* 参考图实测：组头 36px、药丸 18px 高（共用 .pill 是 22px，这里局部收小到参考图的尺寸） */
  .ghead {
    display: flex;
    align-items: center;
    justify-content: space-between;
    height: 36px;
    padding: 0 16px;
    border-bottom: 1px solid var(--line-soft);
  }
  .ghead .pill {
    height: 18px;
  }
  .gsum {
    color: var(--muted);
    font-size: 12px;
  }

  .foot {
    flex: none;
    padding: 10px 16px 14px;
    border-top: 1px solid var(--line);
    background: var(--card);
  }
  .stat {
    margin: 0 0 8px;
    color: var(--muted);
    font-size: 13px;
    line-height: 20px;
  }
  .stat b {
    color: var(--ink);
    font-weight: 600;
  }
  .estimate {
    margin: -2px 0 8px;
    color: var(--muted);
    font-size: 12px;
    line-height: 18px;
  }
  .btns {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }
  /* 参考图页脚按钮实测 28px 高 */
  .btns .btn {
    height: 28px;
    padding: 0 12px;
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
  .empty .btn {
    margin-top: 8px;
  }

  /* 骨架屏不做循环动画（§8.7），静态灰块就够了 */
  .sk-row {
    display: flex;
    align-items: center;
    gap: 12px;
    height: 60px;
    padding: 0 16px;
    border-bottom: 1px solid var(--line-soft);
  }
  .skeleton i {
    display: block;
    background: var(--line-soft);
    border-radius: 4px;
  }
  .box {
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
    width: 58%;
    height: 12px;
  }
  .l2 {
    width: 36%;
    height: 10px;
  }
  .r {
    flex: none;
    width: 52px;
    height: 12px;
  }
</style>
