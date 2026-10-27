<script lang="ts">
  import { LoaderCircle, TriangleAlert } from '@lucide/svelte';
  import { app, cancelConfirm, confirmAction, runManualSuggestion, setPermanent, type BatchItem } from '../lib/state.svelte.ts';
  import { formatBytes, middleEllipsis } from '../lib/format.ts';
  import { cleanupSpace } from '../lib/cleanup.ts';
  import type { DeleteCheck, Suggestion } from '../lib/types.ts';
  import { t } from '../lib/i18n.ts';

  interface View {
    title: string;
    sub: string;
    /** 主按钮上的动作，后面会接「 · 大小」 */
    action: string;
    size: number | null;
    /** 不可撤销：主按钮用红色 */
    danger: boolean;
    ready: boolean;
  }

  const c = $derived(app.confirm);
  // 「我知道这些在系统或程序目录里」；每次弹出新的确认框都要重新勾
  let ack = $state(false);
  $effect(() => {
    void c;
    ack = false;
  });

  const name = (p: string) => p.split(/[\\/]/).filter(Boolean).at(-1) ?? p;
  const sumOf = <T,>(xs: T[], f: (x: T) => number) => xs.reduce((a, x) => a + f(x), 0);

  // ---- delete ----
  const doable = $derived(c?.kind === 'delete' ? c.check.items.filter((i) => !i.blocked) : []);
  const blocked = $derived(c?.kind === 'delete' ? c.check.items.filter((i) => i.blocked) : []);
  const warned = $derived(doable.filter((i) => i.warning));
  const needAck = $derived(c?.kind === 'delete' && c.check.anyWarning && doable.length > 0);
  // 所选的全是受保护项：没有可以确认的动作，只留一个「关闭」
  const nothingToDo = $derived(c?.kind === 'delete' && doable.length === 0);

  // ---- cleanup ----
  function cleanupBytes(s: Suggestion, ids: number[] | null): number {
    return ids ? sumOf(s.items.filter((i) => ids.includes(i.id)), (i) => i.alloc) : s.bytes;
  }
  function cleanupVerb(s: Suggestion, permanent: boolean): string {
    switch (s.action) {
      case 'deleteNodes': return permanent ? t('永久删除') : t('移到回收站');
      case 'emptyRecycleBin': return t('清空回收站');
      case 'command': return t('运行命令');
      default: return t('立即清理');
    }
  }

  // ---- cleanupBatch ----
  const auto = $derived(c?.kind === 'cleanupBatch' ? c.items.filter((i) => !i.manual) : []);
  const manual = $derived(c?.kind === 'cleanupBatch' ? c.items.filter((i) => i.manual) : []);
  const checkedBytes = (it: BatchItem) => it.check?.deleteCheck?.totalAlloc ?? it.bytes;
  const autoBytes = $derived(sumOf(auto, checkedBytes));
  const batchSpace = $derived.by(() => {
    const total = { immediate: 0, recycle: 0 };
    for (const it of auto) {
      const part = cleanupSpace(it.suggestion, checkedBytes(it), c?.kind === 'cleanupBatch' && c.permanent);
      total.immediate += part.immediate;
      total.recycle += part.recycle;
    }
    return total;
  });
  const hasDeleteNodes = $derived(auto.some((i) => i.suggestion.action === 'deleteNodes'));
  const needAdmin = $derived(
    !app.info?.isAdmin &&
      (c?.kind === 'cleanupBatch' ? auto.some((i) => i.suggestion.requiresAdmin) : c?.kind === 'cleanup' && c.suggestion.requiresAdmin),
  );
  function how(it: BatchItem, permanent: boolean): string {
    switch (it.suggestion.action) {
      case 'deleteNodes': return permanent ? t('永久删除，不进回收站') : t('移到回收站');
      case 'emptyRecycleBin': return t('清空回收站，无法恢复');
      default: return t('永久删除，不进回收站');
    }
  }

  const view = $derived.by((): View | null => {
    if (!c) return null;
    switch (c.kind) {
      case 'delete': {
        const n = doable.length;
        const verb = c.permanent ? t('永久删除') : t('移到回收站');
        const one = n === 1 ? { name: name(doable[0].path) } : null;
        return {
          title: !n ? t('无法删除所选项目')
            : one ? (c.permanent ? t('永久删除「{name}」？', one) : t('把「{name}」移到回收站？', one))
              : c.permanent ? t('永久删除{n} 项？', { n }) : t('把{n} 项移到回收站？', { n }),
          sub: !n ? t('所选项目都受保护，不会被删除。') : c.permanent ? t('删除后无法恢复，也不会经过回收站。') : t('文件会先放进回收站，清空回收站后才会真正释放空间。'),
          action: verb,
          size: sumOf(doable, (i) => i.alloc),
          danger: c.permanent,
          ready: n > 0 && (!needAck || ack),
        };
      }
      case 'cleanup': {
        const s = c.suggestion;
        const verb = cleanupVerb(s, c.permanent);
        const irreversible = s.action === 'emptyRecycleBin' || s.action === 'deleteContents' || (s.action === 'deleteNodes' && c.permanent);
        return {
          title: s.action === 'command' ? t('运行「{title}」的命令？', { title: s.title }) : t('清理「{title}」？', { title: s.title }),
          sub: s.description,
          action: verb,
          size: s.action === 'command' ? null : c.check?.deleteCheck?.totalAlloc ?? cleanupBytes(s, c.nodeIds),
          danger: irreversible,
          ready: true,
        };
      }
      case 'cleanupBatch':
        return {
          title: t('立即清理 {n} 项？', { n: auto.length }),
          sub: manual.length ? t('另有 {n} 项需要单独处理，不会随这一批执行。', { n: manual.length }) : t('确认后在清理任务区按顺序处理，可随时停止后续项目。移入回收站的文件，清空回收站后才释放空间。'),
          action: t('立即清理'),
          size: autoBytes,
          // 清缓存、清空回收站、勾了永久删除都回不了头：主按钮标红，默认焦点放在取消上，手一滑按回车也不会整批执行
          danger: c.permanent || auto.some((i) => i.suggestion.action !== 'deleteNodes'),
          ready: auto.length > 0,
        };
      case 'elevate':
        return c.denied
          ? {
            title: t('有 {n} 个目录没有权限读取，用管理员身份重新扫描？', { n: c.denied }),
            sub: t('这些目录里的文件没算进这次结果。以管理员身份重启后会自动重新扫描这个范围；本地 NTFS 卷根还会改用更快的深度扫描。'),
            action: t('以管理员身份重新扫描'),
            size: null,
            danger: false,
            ready: true,
          }
          : {
            title: t('以管理员身份重新启动？'),
            sub: t('深度扫描要读取 NTFS 主文件表，需要管理员权限。'),
            action: t('以管理员身份重启'),
            size: null,
            danger: false,
            ready: true,
          };
    }
  });

  const primaryText = $derived(c?.preparing ? t('正在核对目标…') : view ? (view.size === null ? view.action : `${view.action} · ${formatBytes(view.size)}`) : '');
  // 永久删除时把焦点放在「取消」上：手一滑按了回车也不会删东西
  const irreversible = $derived(!!view?.danger);

  /** 对话框一出现就接管焦点，Tab 不会跑到后面的页面上；关闭后把焦点还给原来的元素 */
  function takeFocus(node: HTMLElement) {
    const prev = document.activeElement as HTMLElement | null;
    queueMicrotask(() => (node.querySelector<HTMLElement>('[data-autofocus]') ?? node).focus());
    return () => prev?.focus?.();
  }

  function onkeydown(e: KeyboardEvent) {
    if (!c || !view || e.isComposing) return;
    if (e.key === 'Escape') {
      e.preventDefault();
      cancelConfirm();
    } else if (e.key === 'Enter') {
      // 焦点在任何按钮上时，回车是按钮自己的事（取消就是取消）；其余位置回车 = 确认
      if ((e.target as HTMLElement | null)?.closest?.('button')) return;
      e.preventDefault();
      if (view.ready && !c.busy && !c.preparing && !c.preflightError) void confirmAction();
    } else if (e.key === 'Tab') {
      const dlg = document.getElementById('confirm-dialog');
      const f = [...(dlg?.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), a[href]') ?? [])];
      if (!f.length) return e.preventDefault();
      const at = f.indexOf(document.activeElement as HTMLElement);
      if (e.shiftKey && at <= 0) {
        e.preventDefault();
        f[f.length - 1].focus();
      } else if (!e.shiftKey && at === f.length - 1) {
        e.preventDefault();
        f[0].focus();
      } else if (at < 0) {
        e.preventDefault();
        f[0].focus();
      }
    }
  }
</script>

{#snippet targets(check: DeleteCheck)}
  <section class="targets">
    <h3>{t('已核对的操作对象')} · {t('{n} 项', { n: check.items.length })}</h3>
    <ul class="items">
      {#each check.items as item (item.id)}
        <li class="stack">
          <span class="target-path mono">{item.path}</span>
          {#if item.blocked}
            <span class="why">{t('不会处理：{reason}', { reason: item.reason ?? t('受保护的位置') })}</span>
          {:else}
            <span class="how">{formatBytes(item.alloc)}{#if item.warning} · {item.warning}{/if}</span>
          {/if}
        </li>
      {/each}
    </ul>
  </section>
{/snippet}

<svelte:window {onkeydown} />

{#if c && view}
  <!-- 点遮罩 = 取消；键盘用户有 Esc -->
  <div class="scrim" role="presentation" onpointerdown={(e) => e.target === e.currentTarget && cancelConfirm()}>
    <div
      id="confirm-dialog"
      class="float dlg"
      class:wide={c.kind === 'cleanupBatch'}
      role="alertdialog"
      aria-modal="true"
      aria-labelledby="confirm-title"
      aria-describedby="confirm-sub"
      tabindex="-1"
      {@attach takeFocus}
    >
      <header>
        <h2 id="confirm-title" class="card-title">{view.title}</h2>
        <p id="confirm-sub" class="card-sub">{view.sub}</p>
      </header>

      <div class="body">
        {#if c.preparing}<p class="note" role="status">{t('正在核对目标…完成前不会执行。')}</p>{/if}
        {#if c.preflightError}<p class="why" role="alert">{t('无法核对目标：{error}。请取消后重试。', { error: c.preflightError })}</p>{/if}
        {#if c.kind === 'delete'}
          {#if doable.length}
            <ul class="items">
              {#each doable as it (it.id)}
                <li>
                  <span class="path mono" title={it.path}>{middleEllipsis(it.path, 64)}</span>
                  <span class="size num">{formatBytes(it.alloc)}</span>
                </li>
              {/each}
            </ul>
            <p class="total">{t('合计')} <b class="num">{formatBytes(view.size ?? 0)}</b> · <span class="num">{t('{n} 个文件', { n: sumOf(doable, (i) => i.files) })}</span></p>
          {/if}

          {#if blocked.length}
            <section class="block">
              <h3>{t('无法删除')} · {t('{n} 项', { n: blocked.length })}</h3>
              <ul class="items">
                {#each blocked as it (it.id)}
                  <li class="stack">
                    <span class="path mono" title={it.path}>{middleEllipsis(it.path, 64)}</span>
                    <span class="why">{it.reason ?? t('受保护的位置')}</span>
                  </li>
                {/each}
              </ul>
            </section>
          {/if}

          {#if needAck}
            <section class="warn">
              <h3><TriangleAlert size={14} />{t('这些项目在系统或程序目录里')}</h3>
              {#if warned.length}
                <ul>
                  {#each warned as it (it.id)}<li><span class="mono">{name(it.path)}</span>{t('：')}{it.warning}</li>{/each}
                </ul>
              {/if}
              <label class="opt"><input type="checkbox" class="check" bind:checked={ack} data-autofocus={irreversible ? undefined : ''} />{t('我知道这些在系统或程序目录里')}</label>
            </section>
          {/if}

          {#if doable.length}
            <label class="opt">
              <input type="checkbox" class="check" checked={c.permanent} disabled={c.busy} onchange={(e) => setPermanent(e.currentTarget.checked)} />
              {t('永久删除，不放进回收站（无法恢复）')}
            </label>
          {/if}
        {:else if c.kind === 'cleanup'}
          <ul class="items rich">
            <li>
              <div class="main">
                <div class="t">{c.suggestion.title}</div>
                {#if c.suggestion.displayPath}<div class="p mono">{c.suggestion.displayPath}</div>{/if}
              </div>
              {#if c.suggestion.action !== 'command'}<span class="size num">{formatBytes(view.size ?? 0)}</span>{/if}
            </li>
          </ul>
          {#if !c.preparing && !c.preflightError && c.check?.deleteCheck}
            {@render targets(c.check.deleteCheck)}
          {/if}
          {#if c.suggestion.action === 'command' && c.suggestion.command}
            <p class="note">{t('会在新窗口运行下面的命令：')}</p>
            <pre class="cmd mono">{c.suggestion.command}</pre>
          {:else if c.suggestion.action === 'deleteNodes'}
            <p class="note">{c.permanent ? t('预计立即释放 {size}；实际释放以完成后为准。', { size: formatBytes(view.size ?? 0) }) : t('清空回收站后才释放 {size}；实际释放以完成后为准。', { size: formatBytes(view.size ?? 0) })}</p>
            <label class="opt">
              <input type="checkbox" class="check" checked={c.permanent} disabled={c.busy} onchange={(e) => setPermanent(e.currentTarget.checked)} />
              {t('永久删除，不放进回收站（无法恢复）')}
            </label>
          {:else}
            <p class="note">{c.suggestion.action === 'emptyRecycleBin' ? t('回收站里的文件会被永久删除，无法恢复。') : t('永久删除，不进回收站。')}</p>
          {/if}
          {#if needAdmin}<p class="note warn-text">{t('这一项需要管理员权限。当前是标准权限，可能清理不全。')}</p>{/if}
        {:else if c.kind === 'cleanupBatch'}
          {#if auto.length}
            <ul class="items rich">
              {#each auto as it (it.suggestion.id)}
                <li>
                  <div class="main">
                    <div class="t">{it.suggestion.title}</div>
                    {#if it.suggestion.displayPath}<div class="p mono">{it.suggestion.displayPath}</div>{/if}
                    <div class="how">{how(it, c.permanent)}</div>
                    {#if !c.preparing && !c.preflightError && it.check?.deleteCheck}
                      {@render targets(it.check.deleteCheck)}
                    {/if}
                  </div>
                  <span class="size num">{formatBytes(checkedBytes(it))}</span>
                </li>
              {/each}
            </ul>
            <p class="total">{t('合计')} <b class="num">{formatBytes(autoBytes)}</b> · <span class="num">{t('{n} 项', { n: auto.length })}</span></p>
            <p class="note">{t('预计立即释放 {size}', { size: formatBytes(batchSpace.immediate) })}{#if batchSpace.recycle > 0} · {t('清空回收站后释放 {size}', { size: formatBytes(batchSpace.recycle) })}{/if}{t('。估算可能重叠，实际释放以完成后为准。')}</p>
          {/if}

          {#if manual.length}
            <section class="block">
              <h3>{t('需要单独处理')} · {t('{n} 项', { n: manual.length })}</h3>
              <ul class="items rich">
                {#each manual as it (it.suggestion.id)}
                  <li>
                    <div class="main">
                      <div class="t">{it.suggestion.title}</div>
                      {#if it.suggestion.command}<div class="p mono">{it.suggestion.command}</div>{:else}<div class="p">{it.suggestion.description}</div>{/if}
                    </div>
                    {#if it.suggestion.action === 'command' || it.suggestion.action === 'openSettings' || it.suggestion.action === 'emptyRecycleBin'}
                      <!-- 换成这一项自己的确认框；这一批不会执行 -->
                      <button type="button" class="btn" disabled={c.busy || c.preparing} onclick={() => void runManualSuggestion(it.suggestion)}>
                        {it.suggestion.action === 'command' ? t('运行命令…') : it.suggestion.action === 'openSettings' ? t('打开设置') : t('单独确认清空…')}
                      </button>
                    {/if}
                  </li>
                {/each}
              </ul>
            </section>
          {/if}

          {#if hasDeleteNodes}
            <label class="opt">
              <input type="checkbox" class="check" checked={c.permanent} disabled={c.busy} onchange={(e) => setPermanent(e.currentTarget.checked)} />
              {t('删除所选文件时永久删除，不放进回收站')}
            </label>
          {/if}
          {#if needAdmin}<p class="note warn-text">{t('其中有需要管理员权限的项目。当前是标准权限，可能清理不全。')}</p>{/if}
        {:else}
          <p class="plain">
            {t('确认后，DiskLens 会以管理员身份重新启动')}{#if c.scanPath}{t('，并自动扫描')} <code class="mono">{c.scanPath}</code>{/if}{t('。')}
          </p>
          <p class="plain muted">{t('Windows 会弹出权限确认窗口，请选择「是」。选择「否」则保持现在的标准权限。')}</p>
        {/if}
      </div>

      <footer>
        <button type="button" class="btn" data-autofocus={irreversible || nothingToDo ? '' : undefined} disabled={c.busy} onclick={cancelConfirm}>
          {nothingToDo ? t('关闭') : t('取消')}
        </button>
        {#if !nothingToDo}
          <button
            type="button"
            class="btn {view.danger ? 'btn-danger' : 'btn-primary'}"
            data-autofocus={!irreversible && !needAck ? '' : undefined}
            disabled={!view.ready || c.busy || c.preparing || !!c.preflightError}
            onclick={() => void confirmAction()}
          >
            {#if c.busy || c.preparing}<LoaderCircle size={14} class="spin" />{/if}{primaryText}
          </button>
        {/if}
      </footer>
    </div>
  </div>
{/if}

<style>
  .scrim {
    position: fixed;
    inset: 0;
    z-index: 50;
    display: grid;
    place-items: center;
    background: rgba(0, 0, 0, 0.25);
    animation: fade var(--dur) ease-out;
  }
  .dlg {
    display: flex;
    flex-direction: column;
    width: 520px;
    max-width: calc(100vw - 32px);
    max-height: calc(100vh - 48px);
    outline: 0;
    animation: rise var(--dur) var(--ease);
  }
  .dlg.wide {
    width: 620px;
  }
  header {
    padding: 20px 20px 12px;
  }
  .body {
    display: grid;
    flex: 1;
    gap: 14px;
    min-height: 0;
    padding: 0 20px 16px;
    overflow-y: auto;
  }
  footer {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    padding: 14px 20px;
    border-top: 1px solid var(--line);
  }

  .items {
    max-height: 240px;
    margin: 0;
    padding: 0;
    overflow-y: auto;
    border: 1px solid var(--line);
    border-radius: var(--r-ctl);
    list-style: none;
  }
  .items li {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    padding: 8px 12px;
    border-top: 1px solid var(--line-soft);
  }
  .items li:first-child {
    border-top: 0;
  }
  .items li.stack {
    flex-direction: column;
    align-items: flex-start;
    gap: 2px;
  }
  .path {
    min-width: 0;
    overflow: hidden;
    font-size: 12px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .size {
    flex: none;
    font-size: 13px;
    font-weight: 600;
  }
  .rich .main {
    min-width: 0;
    flex: 1;
  }
  .rich .t {
    font-size: 14px;
    font-weight: 500;
  }
  .rich .p {
    overflow: hidden;
    color: var(--muted);
    font-size: 12px;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .rich .how {
    margin-top: 2px;
    color: var(--muted);
    font-size: 12px;
  }
  .total {
    margin: 0;
    color: var(--muted);
  }
  .targets {
    min-width: 0;
    margin-top: 8px;
  }
  .target-path {
    overflow-wrap: anywhere;
    font-size: 12px;
  }
  .total b {
    color: var(--ink);
    font-weight: 600;
  }

  h3 {
    display: flex;
    align-items: center;
    gap: 6px;
    margin: 0 0 8px;
    font-size: 13px;
    font-weight: 600;
  }
  .block h3 {
    color: var(--muted);
  }
  .why {
    color: var(--danger);
    font-size: 12px;
  }
  .warn {
    padding: 12px;
    border: 1px solid var(--review-line);
    border-radius: var(--r-ctl);
    background: var(--review-bg);
  }
  .warn h3 {
    color: var(--review-text);
  }
  .warn ul {
    margin: 0 0 10px;
    padding-left: 18px;
    font-size: 12px;
  }
  .opt {
    display: flex;
    align-items: center;
    gap: 8px;
    cursor: pointer;
  }
  .note,
  .plain {
    margin: 0;
  }
  .note {
    color: var(--muted);
  }
  .warn-text {
    color: var(--review-text);
  }
  .muted {
    color: var(--muted);
  }
  .cmd {
    margin: 0;
    padding: 10px 12px;
    overflow-x: auto;
    border-radius: var(--r-ctl);
    background: var(--page);
    font-size: 12px;
    white-space: pre-wrap;
    word-break: break-all;
  }
  code {
    padding: 1px 5px;
    border-radius: 4px;
    background: var(--page);
    font-size: 12px;
    word-break: break-all;
  }

  footer :global(.spin) {
    animation: turn 1s linear infinite;
  }
  @keyframes turn {
    to {
      transform: rotate(360deg);
    }
  }
  @keyframes fade {
    from {
      opacity: 0;
    }
  }
  @keyframes rise {
    from {
      opacity: 0;
      transform: translateY(6px);
    }
  }
</style>
