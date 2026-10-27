<script lang="ts">
  import { onMount } from 'svelte';
  import { ArrowRight, ChevronDown, FolderOpen, History, Languages, Package, RefreshCw, Shield, ShieldCheck } from '@lucide/svelte';
  import DriveRow from './DriveRow.svelte';
  import StartupLogo from './StartupLogo.svelte';
  import { app, changeLanguage, isMock, refreshDrives, requestElevation, startScan } from '../lib/state.svelte.ts';
  import { LOCALES, isLocale, locale, t } from '../lib/i18n.ts';
  import { StartupData } from '../lib/startup.svelte.ts';
  import { EVIDENCE_NAMES, role } from '../lib/roles.ts';
  import { formatBytes, formatDate, formatDelta, formatDuration } from '../lib/format.ts';
  import '../styles/startup.css';

  const data = new StartupData();
  // 每栏最多三条，只看最近一周：多了反而看不过来
  const records = $derived(data.recent.slice(0, 3));
  const apps = $derived(data.applications.slice(0, 3));
  const olderRecords = $derived(data.records.length - records.length);
  const legacy = $derived(data.recent.length > 0 && data.recent.every((r) => r.apps === null));
  const trend = (n: number): string => (n > 0 ? 'up' : n < 0 ? 'down' : '');
  const current = $derived(LOCALES.find((l) => l.code === locale()) ?? LOCALES[0]);
  /** 上次对照后不见了的：前五个写名字，多的只写一共几个 */
  const removedNames = $derived(data.removed.slice(0, 5).map((a) => a.name).join(t('、'))
    + (data.removed.length > 5 ? t(' 等 {n} 个', { n: data.removed.length }) : ''));
  onMount(() => { void data.refresh(); return () => data.dispose(); });
</script>

<main class="start" aria-label={t('DiskLens 启动页')}>
  <div class="col">
    <div class="top">
    <!-- 左上角换界面语言：原生 select 叠在胶囊上，键盘、读屏都照原生的走；选过就存进设置，下次启动默认就是它 -->
    <div class="lang" title={t('界面语言')}>
      <Languages size={14} aria-hidden="true" /><span>{current.name}</span><ChevronDown size={12} aria-hidden="true" />
      <select aria-label={t('界面语言')} value={current.code} onchange={(e) => { const v = e.currentTarget.value; if (isLocale(v)) void changeLanguage(v); }}>
        {#each LOCALES as l (l.code)}<option value={l.code} lang={l.code}>{l.name}</option>{/each}
      </select>
    </div>
    <div class="perm" role="group" aria-label={t('运行权限')}>
      {#if app.info?.isAdmin}
        <span class="perm-state admin"><ShieldCheck size={14} />{t('管理员权限 · 本地分析')}</span>
      {:else}
        <span class="perm-state"><Shield size={14} />{t('标准权限 · 本地分析')}</span>
        <button type="button" class="perm-action" title={t('重启后能读取受保护的目录，本地 NTFS 卷根可用更快的深度扫描')} onclick={() => requestElevation(null, 'auto')}>{t('以管理员身份重启')}</button>
      {/if}
    </div>
    </div>
    <header class="hero"><StartupLogo /><p>{t('选择磁盘，查看空间分布')}</p></header>

    {#if app.summary}
      <button type="button" class="resume" onclick={() => (app.screen = 'main')}>
        <span>{t('继续查看当前结果')} <span class="path">{app.summary.rootPath}</span></span><ArrowRight size={16} />
      </button>
    {/if}

    <section aria-label={t('本机磁盘')} aria-busy={app.drivesLoading}>
      <header class="section-head"><h2>{t('本机磁盘')}</h2><div class="section-actions"><span>{t('扫描方式自动选择')}</span>
        <button type="button" class="btn btn-icon" aria-label={t('刷新磁盘列表')} title={t('刷新磁盘列表')} disabled={app.drivesLoading} onclick={() => void refreshDrives()}><RefreshCw size={14} /></button>
      </div></header>
      {#if app.drives.length}
        {#if app.drivesLoading}<p class="status" role="status">{t('正在刷新磁盘，容量以刷新完成后的结果为准…')}</p>{/if}
        <ul class="drives">{#each app.drives as d (d.root)}<DriveRow drive={d} />{/each}</ul>
      {:else}<p class="empty" role="status">{app.drivesLoading ? t('正在读取磁盘…') : t('没有读到磁盘，请连接设备后刷新。')}</p>{/if}
    </section>

    <div class="bottom">
      <section aria-label={t('最近扫描记录')} aria-busy={data.recordsLoading}>
        <header class="section-head"><h2>{t('最近扫描')}</h2><span>{data.recordsLoading ? t('读取中') : t('最近一周 {n} 条', { n: data.recent.length })}</span></header>
        {#if data.recordsLoading}<p class="empty" role="status">{t('正在读取扫描记录…')}</p>
        {:else if data.recordsError}<div class="empty" role="status"><p>{t('扫描记录读取失败：{error}', { error: data.recordsError })}</p><button class="btn" onclick={() => void data.refresh()}>{t('重试')}</button></div>
        {:else if !data.records.length}<p class="empty">{t('还没有扫描记录。完成一次扫描后，摘要会保存在这里，下次扫描同一范围时自动对照变化。')}</p>
        {:else if !records.length}<p class="empty">{t('最近一周没有扫描过。')}</p>
        {:else}
          {#each records as record (record.rootPath)}
            <details class="history">
              <summary><span class="item-icon"><History size={16} /></span><span class="record-copy"><strong class="path">{record.rootPath}</strong><time datetime={new Date(record.completedAtMs).toISOString()}>{formatDate(record.completedAtMs)}</time></span><span class="record-size">{formatBytes(record.scannedAlloc)}<ChevronDown size={14} /></span></summary>
              <div class="record-body">
                <p>{t('{mode} · {files} 个文件 · {dirs} 个文件夹 · 用时 {time}', { mode: record.mode === 'mft' ? t('MFT 深度扫描') : t('常规扫描'), files: record.files, dirs: record.dirs, time: formatDuration(record.elapsedMs) })}</p>
                {#if record.previousAtMs !== null && record.previousAlloc !== null}
                  <p>{t('比 {date} 那次扫描', { date: formatDate(record.previousAtMs) })} <b class="delta {trend(record.scannedAlloc - record.previousAlloc)}">{formatDelta(record.scannedAlloc - record.previousAlloc)}</b></p>
                {/if}
                {#if record.topEntries?.length}
                  <table class="entries" aria-label={t('{root} 下占用最大的几项', { root: record.rootPath })}>
                    <tbody>{#each record.topEntries.slice(0, 6) as e (e.name)}
                      <tr><th scope="row" class="path">{e.name}</th><td>{formatBytes(e.alloc)}</td>
                        <td class="delta {e.previousAlloc === null ? '' : trend(e.alloc - e.previousAlloc)}">{e.previousAlloc !== null ? formatDelta(e.alloc - e.previousAlloc) : record.previousAtMs !== null ? t('上次不在前列') : ''}</td></tr>
                    {/each}</tbody>
                  </table>
                {:else}
                  <p>{t('这条记录是旧版本存的，没有目录明细；重新扫描一次，以后就能对照。')}</p>
                {/if}
                <p class="hint">{t('这是保存的摘要，查看可操作的地图需要重新扫描。')}</p>
                <div class="record-actions">
                  <button type="button" class="text-button" onclick={() => void startScan(record.rootPath, 'auto')}>{t('重新扫描此范围')} <ArrowRight size={14} /></button>
                  <button type="button" class="text-button" onclick={() => void data.open(record.rootPath)}><FolderOpen size={14} />{t('打开位置')}</button>
                </div>
              </div>
            </details>
          {/each}
        {/if}
        {#if !data.recordsLoading && olderRecords > 0}<p class="app-note">{t('另有 {n} 条更早或排在后面的记录没有列出。', { n: olderRecords })}</p>{/if}
        {#if data.recordsSkipped}<p class="app-note">{t('已略过 {n} 条完成时间无效的记录。', { n: data.recordsSkipped })}</p>{/if}
      </section>

      <section aria-label={t('新应用与安装位置')} aria-busy={data.recordsLoading}>
        <header class="section-head"><h2>{t('新应用与安装位置')}</h2><span>{data.applications.length ? t('最近一周新出现 {n} 个', { n: data.applications.length }) : t('最近一周')}</span></header>
        {#if data.recordsLoading}<p class="empty" role="status">{t('正在读取扫描记录…')}</p>
        {:else if data.recordsError}<div class="empty" role="status"><p>{t('扫描记录读取失败：{error}', { error: data.recordsError })}</p><button class="btn" onclick={() => void data.refresh()}>{t('重试')}</button></div>
        {:else if !data.records.length}<p class="empty">{t('扫描后，这里会对照前后两次扫描，列出新出现的应用和它们的位置。')}</p>
        {:else if !data.applications.length}<p class="empty">{legacy ? t('最近的记录是旧版本存的，不含应用信息；重新扫描一次就有了。') : t('最近一周没有新出现的应用。')}</p>
        {:else}
          <ul class="applications">{#each apps as item (item.kind + '|' + item.name + '|' + item.path)}
            <li><span class="item-icon"><Package size={16} /></span>
              <div class="application-copy">
                <strong>{item.name}</strong>{#if item.isNew}<span class="badge">{t('新出现')}</span>{/if}
                <code>{item.path}</code>
                <p class="meta">{item.installation ? t('安装位置') : t('识别位置')} · {role(item.role).name} · {EVIDENCE_NAMES[item.evidence]} · {t('约 {size}', { size: formatBytes(item.alloc) })}</p>
                <p class="meta">{item.isNew ? t('{absent} 那次扫描还没有，{found} 扫描时发现', { absent: formatDate(item.absentAtMs ?? item.comparedAtMs), found: formatDate(item.firstSeenMs) }) : t('首次记录于 {date}', { date: formatDate(item.firstSeenMs) })}</p>
              </div>
              <button type="button" class="locate btn btn-icon" aria-label={t('在资源管理器中打开 {name} 的位置', { name: item.name })} title={t('在资源管理器中打开')} onclick={() => void data.open(item.path)}><FolderOpen size={16} /></button></li>
          {/each}</ul>
          {#if data.applications.length > apps.length}<p class="app-note">{t('另有 {n} 个新应用没有列出。', { n: data.applications.length - apps.length })}</p>{/if}
        {/if}
        {#if data.removed.length}<p class="app-note">{t('上次对照后不见了：{names}（可能已卸载、搬走，或这次没读到）。', { names: removedNames })}</p>{/if}
        {#if data.records.length}<p class="app-note">{t('「新出现」只在同一范围、同一扫描方式和权限的前后两次扫描之间对照；时间是扫描发现它的时刻，不是安装时刻。')}</p>{/if}
      </section>
    </div>
    {#if data.openError}<p class="open-error" role="alert">{data.openError}</p>{/if}

    {#if isMock}<footer><span class="demo">{t('演示模式 · 模拟数据')}</span></footer>{/if}
  </div>
</main>

<style>
  .start { height: 100%; overflow-y: auto; padding: 28px 24px 20px; }
  .col { max-width: 1080px; min-height: 100%; margin: 0 auto; display: flex; flex-direction: column; }
  .top { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: 8px; margin: -8px 0 4px; }
  .perm { display: flex; flex-wrap: wrap; justify-content: flex-end; align-items: center; gap: 8px; margin-left: auto; }
  .lang {
    position: relative; display: inline-flex; align-items: center; gap: 6px; padding: 6px 12px; border: 1px solid var(--line); border-radius: 999px;
    background: var(--card); color: var(--ink-2); font-size: 12px;
    transition: background var(--dur-fast) var(--ease), border-color var(--dur-fast) var(--ease), color var(--dur-fast) var(--ease);
  }
  .lang:hover { background: var(--hover); border-color: var(--start-accent); color: var(--start-accent); }
  .lang:focus-within { outline: 2px solid var(--start-accent); outline-offset: 2px; }
  .lang select { position: absolute; inset: 0; width: 100%; height: 100%; opacity: 0; cursor: pointer; font: inherit; }
  .lang select:focus-visible { outline: none; }
  .perm-state { display: inline-flex; align-items: center; gap: 6px; color: var(--muted); font-size: 12px; }
  .perm-state.admin { color: var(--safe-text); }
  .perm-action {
    display: inline-flex; align-items: center; gap: 6px; padding: 6px 12px; border: 1px solid var(--line); border-radius: 999px;
    background: var(--card); color: var(--ink-2); font: inherit; font-size: 12px; cursor: pointer;
    transition: background var(--dur-fast) var(--ease), border-color var(--dur-fast) var(--ease), transform var(--dur-fast) var(--ease);
  }
  .perm-action:hover { background: var(--hover); border-color: var(--start-accent); color: var(--start-accent); }
  .perm-action:active { transform: translateY(1px); }
  .perm-action:focus-visible { outline: 2px solid var(--start-accent); outline-offset: 2px; }
  .hero { text-align: center; padding: clamp(4px, 3vh, 30px) 0 30px; }
  .hero p { margin: 8px 0 0; color: var(--muted); font-size: 13px; letter-spacing: .7px; }
  .section-head { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 13px; }
  h2 { margin: 0; font-size: 15px; font-weight: 600; }
  .section-head span { font-size: 12px; color: var(--muted); }
  .section-actions { display: flex; align-items: center; gap: 10px; }
  .section-actions .btn { background: transparent; border: 0; }
  .drives { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 16px; list-style: none; padding: 0; margin: 0; }
  .bottom { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 34px; margin-top: 32px; }
  .bottom section { min-width: 0; }
  .empty { color: var(--muted); font-size: 13px; padding: 20px 0; margin: 0; line-height: 1.8; overflow-wrap: anywhere; }
  .empty p { margin: 0 0 10px; }
  .status { color: var(--muted); margin: 0 0 12px; }
  .history { border-bottom: 1px solid var(--line); }
  .history:last-child { border-bottom: 0; }
  .history > summary { display: flex; align-items: center; gap: 10px; list-style: none; padding: 14px 0; cursor: pointer; }
  .history > summary::-webkit-details-marker { display: none; }
  .item-icon { display: grid; place-items: center; width: 32px; height: 32px; flex: none; background: var(--hover); color: var(--start-accent); border-radius: 10px; }
  .record-copy { flex: 1; min-width: 0; }
  .record-copy strong { display: block; font-size: 12px; font-weight: 500; }
  time { display: block; color: var(--muted); font-size: 11px; margin-top: 3px; }
  .record-size { display: flex; align-items: center; gap: 7px; white-space: nowrap; color: var(--muted); font-size: 12px; font-variant-numeric: tabular-nums; }
  .record-body { padding: 0 0 16px 42px; color: var(--muted); font-size: 12px; }
  .record-body p { margin: 0 0 6px; }
  .record-body .hint { font-size: 11px; margin-top: 8px; }
  .entries { width: 100%; border-collapse: collapse; margin: 4px 0 6px; font-variant-numeric: tabular-nums; }
  .entries th, .entries td { padding: 4px 0; border-bottom: 1px solid var(--line-soft); font-weight: 400; text-align: right; white-space: nowrap; }
  .entries th { text-align: left; color: var(--ink-2); white-space: normal; padding-right: 12px; }
  .entries td + td { padding-left: 12px; width: 7.5em; }
  .delta { font-weight: 400; }
  .delta.up { color: var(--review-text); }
  .delta.down { color: var(--safe-text); }
  .record-actions { display: flex; flex-wrap: wrap; gap: 4px 18px; }
  .text-button { display: inline-flex; align-items: center; gap: 7px; background: none; border: 0; color: var(--start-accent); cursor: pointer; padding: 5px 0; font-size: 12px; text-align: left; }
  .more { margin-top: 10px; }
  .applications { list-style: none; padding: 0; margin: 0; }
  .applications > li { display: flex; align-items: flex-start; gap: 10px; padding: 13px 0; border-bottom: 1px solid var(--line); }
  .applications > li:last-child { border-bottom: 0; }
  .application-copy { flex: 1; min-width: 0; }
  .application-copy strong { font-size: 12px; font-weight: 500; }
  .badge { margin-left: 8px; padding: 1px 6px; border-radius: 999px; background: var(--review-bg); color: var(--review-text); font-size: 11px; }
  code { display: block; font: 11px/1.6 var(--font-mono); margin-top: 3px; color: var(--muted); overflow-wrap: anywhere; }
  .meta { margin: 3px 0 0; color: var(--muted); font-size: 11px; overflow-wrap: anywhere; }
  .locate { color: var(--muted); border: 0; background: transparent; flex: none; }
  .app-note { font-size: 11px; color: var(--muted); margin: 12px 0 0; line-height: 1.7; }
  .open-error { margin: 14px 0 0; font-size: 12px; color: var(--danger); }
  .path { overflow-wrap: anywhere; }
  .resume { display: flex; gap: 12px; justify-content: space-between; align-items: center; padding: 11px 14px; background: var(--hover); color: var(--ink-2); border: 1px solid var(--line); border-radius: 10px; margin-bottom: 20px; text-align: left; cursor: pointer; }
  .resume > span { min-width: 0; }
  footer { display: flex; justify-content: flex-end; padding: 17px 0 0; margin-top: auto; color: var(--muted); font-size: 11px; }
  .bottom + footer, .open-error + footer { margin-top: max(30px, 4vh); }
  @media (prefers-reduced-motion: reduce) { .perm-action, .lang { transition: none; } .perm-action:active { transform: none; } }
  @media (max-width: 850px) { .drives { grid-template-columns: repeat(2, minmax(0, 1fr)); } .bottom { gap: 24px; } }
  @media (max-width: 600px) { .start { padding: 18px 16px; } .drives, .bottom { grid-template-columns: minmax(0, 1fr); } .hero { padding-bottom: 24px; } .bottom { gap: 26px; } .record-body { padding-left: 0; } .top { margin-top: 0; } }
</style>
