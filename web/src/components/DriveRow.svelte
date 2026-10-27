<script lang="ts">
  import { ArrowUpRight, HardDrive } from '@lucide/svelte';
  import { driveKind } from './DriveModel.ts';
  import { app, startScan } from '../lib/state.svelte.ts';
  import { formatBytes, formatPercent, splitBytes } from '../lib/format.ts';
  import { t } from '../lib/i18n.ts';
  import type { DriveInfo } from '../lib/types.ts';

  let { drive }: { drive: DriveInfo } = $props();
  const valid = $derived(drive.isReady && Number.isFinite(drive.totalBytes) && drive.totalBytes > 0
    && Number.isFinite(drive.freeBytes) && drive.freeBytes >= 0 && drive.freeBytes <= drive.totalBytes);
  const used = $derived(valid ? drive.totalBytes - drive.freeBytes : 0);
  const ratio = $derived(valid ? used / drive.totalBytes : 0);
  const free = $derived(splitBytes(drive.freeBytes));
  const kind = $derived(driveKind(drive));
</script>

<li class="drive card" aria-label={t('磁盘 {root}', { root: drive.root })} class:off={!drive.isReady}>
  <header>
    <span class="drive-icon"><HardDrive size={20} /></span>
    <div class="identity"><h3 title={drive.root + ' ' + drive.label}>{drive.root.replace(/\\$/, '')} <span>{drive.label || t('本地磁盘')}</span></h3>
      <p>{[drive.fileSystem, kind].filter(Boolean).join(' · ') || t('介质类型未识别')}</p></div>
    {#if valid}<span class="percentage">{t('{value} 已用', { value: formatPercent(ratio, 0) })}</span>{/if}
  </header>
  {#if valid}
    <div class="available"><strong>{free.value}</strong><span>{free.unit}</span><small>{t('当前可用')}</small></div>
    <div class="usage" role="img" aria-label={t('已用 {value}', { value: formatPercent(ratio, 0) })}><i style:width="{ratio * 100}%"></i></div>
    <div class="capacity"><span>{t('已用 {value}', { value: formatBytes(used) })}</span><span>{t('共 {value}', { value: formatBytes(drive.totalBytes) })}</span></div>
  {:else}
    <p class="unavailable">{drive.isReady ? t('容量暂不可用，可尝试扫描') : t('磁盘未就绪，请连接设备或插入介质')}</p>
  {/if}
  <button type="button" class="btn btn-primary scan" disabled={!drive.isReady || app.drivesLoading}
    onclick={() => void startScan(drive.root, 'auto')} aria-label={t('扫描 {drive}', { drive: drive.root })}>
    {t('扫描 {drive}', { drive: drive.root.replace(/\\$/, '') })}<ArrowUpRight size={16} />
  </button>
</li>

<style>
  .drive { padding: 20px; min-width: 0; display: flex; flex-direction: column; transition: box-shadow var(--dur), transform var(--dur); }
  .drive:hover { transform: translateY(-2px); box-shadow: 0 12px 28px var(--start-shadow); }
  .drive.off { box-shadow: none; }
  header { display: flex; align-items: center; gap: 10px; margin-bottom: 23px; }
  .drive-icon { flex: none; display: grid; place-items: center; width: 37px; height: 37px; border-radius: 11px; background: var(--hover); color: var(--start-accent); }
  .identity { min-width: 0; flex: 1; }
  h3 { font-size: 15px; line-height: 21px; margin: 0; overflow-wrap: anywhere; }
  h3 span { font-weight: 500; }
  .identity p { margin: 2px 0 0; font-size: 11px; color: var(--muted); }
  .percentage { flex: none; color: var(--muted); font-size: 11px; font-variant-numeric: tabular-nums; }
  .available { display: flex; align-items: baseline; flex-wrap: wrap; gap: 6px; margin-bottom: 15px; }
  .available strong { font-size: 36px; line-height: 1.15; font-weight: 500; letter-spacing: -1.4px; font-variant-numeric: tabular-nums; }
  .available span { color: var(--muted); font-size: 13px; }
  .available small { margin-left: auto; color: var(--muted); font-size: 11px; }
  .usage { height: 5px; border-radius: 4px; overflow: hidden; background: var(--start-track); }
  .usage i { display: block; height: 100%; background: var(--start-accent); border-radius: inherit; }
  .capacity { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 6px; margin: 8px 0 20px; color: var(--muted); font-size: 11px; font-variant-numeric: tabular-nums; }
  .scan { margin-top: auto; width: 100%; justify-content: space-between; height: 37px; border-radius: 9px; }
  .unavailable { min-height: 84px; color: var(--muted); margin: 0 0 20px; }
  @media (prefers-reduced-motion: reduce) { .drive { transition: none; } .drive:hover { transform: none; } }
</style>
