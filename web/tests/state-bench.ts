// 同一模拟扫描结果上的请求数与等待时间；mock 固定 40ms 延迟，不代表真实磁盘吞吐。
import { readFile, writeFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { compileModule } from 'svelte/compiler';
import { createMockBackend } from '../src/lib/mock.ts';
import type { api as Api } from '../src/lib/api.ts';

const events = new Map<string, (value: unknown) => void>();
let doneAt = 0;
let calls: { method: string; ms: number }[] = [];
const backend = createMockBackend((event, data) => {
  if (event === 'scan.done') doneAt = performance.now();
  events.get(event)?.(data);
});
async function call(method: string, params = {}) {
  const start = performance.now();
  const result = await backend(method, params);
  calls.push({ method, ms: performance.now() - start });
  return result;
}
const bridge = {
  isMock: false,
  on: (event: string, callback: (value: unknown) => void) => { events.set(event, callback); return () => {}; },
  info: () => call('app.info'), listDrives: () => call('drives.list'),
  startScan: (path: string, mode: string) => call('scan.start', { path, mode }),
  view: (nodeId: number, metric: string) => call('tree.view', { nodeId, metric }),
  details: (nodeId: number, metric: string) => call('tree.details', { nodeId, metric }),
  largest: (nodeId: number, metric: string, limit: number) => call('tree.largest', { nodeId, metric, limit }),
  suggestions: () => call('cleanup.list'),
};
const globals = globalThis as typeof globalThis & { __diskLensBench?: unknown };
globals.__diskLensBench = bridge;
const source = await readFile(new URL('../src/lib/state.svelte.ts', import.meta.url), 'utf8');
const code = compileModule(stripTypeScriptTypes(source).replace("import { api } from './api.ts';", 'const api = globalThis.__diskLensBench;'),
  { filename: 'state.svelte.js', generate: 'client' }).js.code.replace(/from (['"])([^'"]+)\1/g, (_, quote, specifier: string) =>
    `from ${quote}${specifier.startsWith('.') ? new URL(`../src/lib/${specifier}`, import.meta.url).href : import.meta.resolve(specifier)}${quote}`);
const state = await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`) as typeof import('../src/lib/state.svelte.ts');
delete globals.__diskLensBench;
await state.init();
const results = [];
for (let run = 0; run < 4; run++) {
  calls = []; doneAt = 0;
  await state.startScan('C:\\', 'walk');
  const deadline = performance.now() + 15000;
  while (!doneAt || state.app.viewLoading || state.app.detailsLoading || state.app.largestLoading || state.app.suggestionsLoading) {
    if (performance.now() > deadline) throw new Error('状态加载超时');
    await new Promise((r) => setTimeout(r, 5));
  }
  results.push({ run: run === 0 ? 'first' : `repeat-${run}`, files: state.app.summary?.files,
    postScanMs: +(performance.now() - doneAt).toFixed(2),
    requests: Object.fromEntries(['tree.view', 'tree.details', 'tree.largest', 'cleanup.list'].map(m => [m, calls.filter(c => c.method === m).length])),
    calls: calls.filter(c => c.method.startsWith('tree.') || c.method === 'cleanup.list').map(c => ({ ...c, ms: +c.ms.toFixed(2) })) });
}
console.log(JSON.stringify(results, null, 2));
if (process.argv[2]) await writeFile(process.argv[2], JSON.stringify(results, null, 2));
