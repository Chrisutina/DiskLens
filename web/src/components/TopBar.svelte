<script lang="ts">
  import { ChevronDown, Clock3, Grid2X2, HardDrive, RefreshCw, Shield } from '@lucide/svelte';
  import DisplayOptions from './DisplayOptions.svelte';
  import SearchBox from './SearchBox.svelte';
  import ScanIssues from './ScanIssues.svelte';
  import { app, backToStart, liveStatus, requestElevation, rescan, setPage } from '../lib/state.svelte.ts';
  import { formatBytes, formatCount, formatDate, formatDuration } from '../lib/format.ts';
  import { t } from '../lib/i18n.ts';

  const s = $derived(app.summary);
  const live = $derived(liveStatus());
  let reportOpen = $state(false);
  let report = $state<HTMLDetailsElement>();
  let reportToggle = $state<HTMLElement>();
</script>

<svelte:window
  onpointerdown={(e) => reportOpen && !report?.contains(e.target as Node) && (reportOpen = false)}
  onkeydowncapture={(e) => {
    if (reportOpen && e.key === 'Escape') { reportOpen = false; e.stopPropagation(); reportToggle?.focus(); }
  }}
/>

<header class="toolbar">
  <div class="bar">
    <button class="home" type="button" title={t('回到启动页')} aria-label={t('回到启动页')} onclick={backToStart}><span class="mark" aria-hidden="true"></span><span class="brand-name">DiskLens</span></button>
    {#if s}
      <button type="button" class="btn scope" title={t('{path} · 返回启动页换盘', { path: s.rootPath })} onclick={backToStart}>
        <HardDrive size={15} /><span class="path">{s.rootPath}</span><ChevronDown size={13} />
      </button>
    {/if}
    <div class="search-slot"><SearchBox /></div>
    <div class="actions">
      <DisplayOptions />
      {#if s}
        <details class="report" bind:this={report} bind:open={reportOpen}>
          <summary class="btn" bind:this={reportToggle}>{t('扫描详情')}<ChevronDown size={13} /></summary>
          <div class="float report-content">
            <h2>{t('本次扫描')}</h2>
            <p class="path">{s.rootPath}</p>
            <dl><dt>{t('完成时间')}</dt><dd>{formatDate(s.completedAtMs)}</dd><dt>{t('扫描方式')}</dt><dd>{s.mode === 'mft' ? t('MFT 深度扫描') : t('常规扫描')} · {s.fileSystem}</dd><dt>{t('耗时')}</dt><dd>{formatDuration(s.elapsedMs)}</dd><dt>{t('文件 / 目录')}</dt><dd>{formatCount(s.files)} / {formatCount(s.dirs)}</dd><dt>{t('拒绝访问入口')}</dt><dd>{formatCount(s.inaccessibleDirs)}</dd><dt>{t('跳过链接 / 挂载点')}</dt><dd>{formatCount(s.linksSkipped)} / {formatCount(s.mountPointsSkipped)}</dd><dt>{t('读取失败')}</dt><dd>{formatCount(s.errors)}</dd><dt>{t('未解释空间')}</dt><dd>{s.isVolumeRoot ? formatBytes(s.unaccountedBytes) : t('文件夹扫描不估计整卷差额')}</dd></dl>
            {#each s.gapParts as part}<p class="gap">{part.basis} · {formatBytes(part.bytes)}</p>{/each}
            <p class="note">{t('拒绝访问入口数不代表其内部全部未读目录。')}</p>
            {#if reportOpen}{#key s.scanId}<ScanIssues scanId={s.scanId} />{/key}{/if}
            {#if !app.info?.isAdmin && s.inaccessibleDirs > 0}
              <button type="button" class="btn" onclick={() => { reportOpen = false; requestElevation(s.rootPath, 'auto', s.inaccessibleDirs); }}><Shield size={14} />{t('以管理员身份重新扫描')}</button>
            {/if}
          </div>
        </details>
        <button type="button" class="btn btn-primary" title={t('重新扫描（F5）')} onclick={rescan}><RefreshCw size={14} />{t('重新扫描')}</button>
      {/if}
    </div>
  </div>
  <nav class="views" aria-label={t('页面')}>
    <button type="button" aria-current={app.page === 'browse' ? 'page' : undefined} onclick={() => setPage('browse')}><Grid2X2 size={15} />{t('资源地图')}</button>
    <button type="button" aria-current={app.page === 'recent' ? 'page' : undefined} onclick={() => setPage('recent')}><Clock3 size={15} />{t('最近文件')}</button>
    <span>{#if live}<b class:stale={live.stale} title={live.stale ? t('按 F5 重新扫描') : undefined}>{live.text}</b> · {/if}{t('仅在本机分析')}</span>
  </nav>
</header>

<style>
  .toolbar { min-width: 0; position: relative; z-index: 30; }
  .bar { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; min-height: 44px; }
  .home { display: flex; align-items: center; gap: 8px; padding: 5px 7px 5px 0; background: none; border: 0; font-size: 15px; font-weight: 600; cursor: pointer; }
  .mark { width: 17px; height: 17px; border: 2px solid var(--workspace-accent); border-radius: 50%; background: linear-gradient(90deg, var(--workspace-accent) 50%, transparent 50%); }
  .scope { max-width: 235px; min-width: 0; gap: 7px; }
  .scope .path { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .search-slot { flex: 1; min-width: 180px; }
  .actions { display: flex; align-items: center; gap: 7px; margin-left: auto; }
  .views { display: flex; align-items: center; gap: 18px; margin-top: 5px; min-height: 36px; border-bottom: 1px solid var(--line); }
  .views button { display: flex; align-items: center; gap: 7px; padding: 8px 2px; border: 0; border-bottom: 2px solid transparent; background: none; color: var(--muted); font-size: 13px; cursor: pointer; }
  .views button[aria-current="page"] { border-bottom-color: var(--workspace-accent); color: var(--workspace-accent); font-weight: 600; }
  .views > span { margin-left: auto; color: var(--muted); font-size: 11px; }
  .views b { font-weight: 400; }
  .views b.stale { color: var(--review-text); }
  .report { position: relative; }
  .report > summary { list-style: none; cursor: pointer; }
  .report > summary::-webkit-details-marker { display: none; }
  .report-content { position: absolute; right: 0; top: calc(100% + 9px); width: min(360px, calc(100vw - 32px)); padding: 18px; max-height: min(540px, calc(100vh - 160px)); overflow: auto; }
  h2 { margin: 0 0 6px; font-size: 15px; }
  .report-content .path { margin: 0 0 14px; overflow-wrap: anywhere; color: var(--muted); }
  dl { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 8px 14px; margin: 0; font-size: 12px; }
  dt { color: var(--muted); }
  dd { margin: 0; text-align: right; overflow-wrap: anywhere; }
  .note, .gap { font-size: 11px; color: var(--muted); margin: 12px 0 0; }
  .report-content .btn { margin-top: 14px; }
  @media(max-width:1100px) { .brand-name { display: none; } .scope { max-width: 195px; } }
  @media(max-width:800px) { .search-slot { order: 3; flex-basis: 100%; } .scope { max-width: calc(100% - 45px); } .actions { flex-wrap: wrap; } .views > span { display: none; } .report-content { position: fixed; top: 100px; left: 16px; right: 16px; } }
  @media(max-width:480px) { .actions { flex-basis: 100%; margin: 0; gap: 5px; } }
</style>
