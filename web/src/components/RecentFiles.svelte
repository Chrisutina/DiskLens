<script lang="ts">
  import { app, loadRecent, reveal, rescan } from '../lib/state.svelte.ts';
  import { formatBytes, formatDate } from '../lib/format.ts';
  import { purpose } from '../lib/purposes.ts';
  import { EVIDENCE_NAMES, role } from '../lib/roles.ts';
  import { t } from '../lib/i18n.ts';

  const result = $derived(app.recent);
  const folder = $derived(app.rootDetails?.path ?? app.summary?.rootPath ?? '');
</script>

<section class="card recent" aria-label={t('最近文件')}>
  <header>
    <h2 class="card-title">{t('最近文件')}</h2>
    <p class="card-sub">{t('找出最近修改的大文件，再定位到它所在的目录。')}</p>
    <div class="filters">
      <label>{t('时间范围')}
        <select bind:value={app.recentDays} onchange={() => void loadRecent()}>
          <option value={1}>{t('最近一天')}</option>
          <option value={7}>{t('最近一周')}</option>
        </select>
      </label>
      <label>{t('排序')}
        <select bind:value={app.recentSort} onchange={() => void loadRecent()}>
          <option value="size">{t('按{metric}', { metric: app.metric === 'alloc' ? t('占用空间') : t('文件大小') })}</option>
          <option value="modified">{t('按修改时间')}</option>
        </select>
      </label>
      <label class="scope">
        <input type="checkbox" bind:checked={app.recentInFolder} onchange={() => void loadRecent()} />
        {t('仅当前目录')}
      </label>
    </div>
    <p class="scope-path" title={app.recentInFolder ? folder : app.summary?.rootPath}>
      {t('范围：{path}', { path: app.recentInFolder ? folder : app.summary?.rootPath })}
    </p>
  </header>

  <div class="results" aria-busy={app.recentLoading}>
    {#if app.recentLoading}
      <p class="empty" role="status">{t('正在读取最近文件…')}</p>
    {:else if app.recentError}
      <div class="empty" role="alert">
        <p>{t('最近文件读取失败：{error}', { error: app.recentError })}</p>
        <button type="button" class="btn" onclick={() => void loadRecent()}>{t('重试')}</button>
      </div>
    {:else if result}
      <div class="summary" role="status">
        <strong class="num">{t('共 {n} 个文件', { n: result.total })}</strong>
        {#if result.total > result.items.length}
          <span>{app.recentSort === 'modified' ? t('仅显示最近修改的 {n} 个，结果已截断。', { n: result.items.length }) : t('仅显示最大的 {n} 个，结果已截断。', { n: result.items.length })}</span>
        {/if}
      </div>
      {#if result.items.length === 0}
        <div class="empty">
          <p>{t('这个范围内没有符合条件的文件。')}</p>
          <p>{t('可改选最近一周、取消目录限制，或重新扫描获取新结果。')}</p>
        </div>
      {:else}
        <table>
          <caption class="sr-only">{t('最近修改的文件，按{order}降序排列', { order: app.recentSort === 'modified' ? t('修改时间') : app.metric === 'alloc' ? t('占用空间') : t('文件大小') })}</caption>
          <thead>
            <tr>
              <th scope="col">{t('文件 / 所属目录')}</th>
              <th scope="col" class="bytes">{app.metric === 'alloc' ? t('占用空间') : t('文件大小')}</th>
              <th scope="col" class="modified">{t('修改时间')}</th>
              <th scope="col" class="action"><span class="sr-only">{t('操作')}</span></th>
            </tr>
          </thead>
          <tbody>
            {#each result.items as file (file.id)}
              <tr>
                <td>
                  <div class="name" title={file.path}>{file.name}</div>
                  <div class="path mono" title={file.dirPath}>{file.dirPath}</div>
                  <div class="tags">
                    <span>{purpose(file.u).name} · {role(file.role).name}</span>
                    <span>{file.owner ? t('{name}（{evidence}）', { name: file.owner.name, evidence: EVIDENCE_NAMES[file.owner.evidence] }) : t('所属对象未识别')}</span>
                  </div>
                </td>
                <td class="bytes num" title={t('文件大小：{size}；占用空间：{alloc}', { size: formatBytes(file.size), alloc: formatBytes(file.alloc) })}>
                  {formatBytes(app.metric === 'alloc' ? file.alloc : file.size)}
                </td>
                <td class="modified num">{formatDate(file.modifiedMs)}</td>
                <td class="action">
                  <button type="button" class="btn" aria-label={t('定位 {name}', { name: file.name })} disabled={app.detailsLoading} onclick={() => void reveal(file.id)}>{t('定位')}</button>
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      {/if}
    {/if}
  </div>

  <footer>
    {#if result}
      <p>{t('按扫描时间 {date} 往前 {days} 天计算。结果不会随当前时间自动更新。', { date: formatDate(result.asOfMs), days: result.days })}</p>
    {/if}
    <p>{t('修改时间不等于写入量，也不代表新增或下载。')}<button type="button" class="link" onclick={rescan}>{t('重新扫描')}</button>{t('可获取新结果。')}</p>
  </footer>
</section>

<style>
  .recent { display: flex; flex-direction: column; min-width: 0; min-height: 0; overflow: hidden; }
  header { padding: 16px 20px 12px; border-bottom: 1px solid var(--line); }
  .filters { display: flex; flex-wrap: wrap; align-items: center; gap: 10px 20px; margin-top: 16px; }
  label { display: flex; align-items: center; gap: 8px; color: var(--ink-2); }
  select { min-height: 32px; padding: 4px 8px; border: 1px solid var(--line); border-radius: var(--r-ctl); background: var(--card); }
  input { accent-color: var(--ink); }
  .scope-path { overflow: hidden; margin: 10px 0 0; color: var(--muted); text-overflow: ellipsis; white-space: nowrap; }
  .results { flex: 1; min-height: 0; overflow: auto; }
  .summary { display: flex; flex-wrap: wrap; gap: 4px 16px; padding: 12px 20px; color: var(--muted); }
  .summary strong { color: var(--ink); }
  table { width: 100%; border-collapse: collapse; table-layout: fixed; }
  th { background: var(--hover); color: var(--muted); font-weight: 500; text-align: left; }
  th, td { padding: 10px 12px; border-bottom: 1px solid var(--line-soft); }
  th:first-child, td:first-child { padding-left: 20px; }
  tbody tr:hover { background: var(--hover); }
  .name { overflow: hidden; color: var(--ink); font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
  .path { overflow: hidden; margin-top: 3px; color: var(--muted); font-size: 11px; text-overflow: ellipsis; white-space: nowrap; }
  .tags { display: flex; flex-wrap: wrap; gap: 2px 12px; margin-top: 5px; color: var(--muted); font-size: 12px; overflow-wrap: anywhere; }
  .bytes { width: 100px; text-align: right; white-space: nowrap; }
  .modified { width: 140px; font-size: 12px; }
  .action { width: 76px; }
  .empty { padding: 32px 20px; color: var(--muted); text-align: center; overflow-wrap: anywhere; }
  .empty p { margin: 0 0 10px; }
  footer { padding: 12px 20px; border-top: 1px solid var(--line); color: var(--muted); font-size: 12px; }
  footer p { margin: 0; }
  .link { padding: 0; border: 0; background: none; color: var(--ink-2); text-decoration: underline; cursor: pointer; }
  .sr-only { position: absolute; width: 1px; height: 1px; padding: 0; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
  @media (max-width: 1100px) {
    th, td { padding: 10px 8px; }
    .bytes { width: 85px; }
    .modified { width: 105px; }
    .action { width: 66px; }
  }
</style>
