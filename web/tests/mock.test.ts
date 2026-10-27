import { test, mock } from 'node:test';
import assert from 'node:assert/strict';
import { createMockBackend } from '../src/lib/mock.ts';
import { PURPOSE, PURPOSE_COUNT } from '../src/lib/purposes.ts';
import { EVIDENCE_NAMES, OWNER_KIND_NAMES, ROLE, ROLE_COUNT } from '../src/lib/roles.ts';
import { CHILD_SORTS, Flag, RECENT_SORTS, RELATION_KINDS, hasFlag } from '../src/lib/types.ts';
import type * as T from '../src/lib/types.ts';
import { enumOf, keys, recordKeys } from './contracts.ts';

// 模拟后端的 view / details / search 要和 C# 后端同口径（QueryChecks、SearchChecks 验的那些不变量），
// 不然 pnpm dev 里调好的界面一上真机就对不上。

type Call = <R>(method: string, params?: unknown) => Promise<R>;

/** 模拟扫描 C:\（常规扫描、非管理员）；扫描进度靠 setInterval 推，用假时钟一口气走完 */
async function scannedC(onEvent?: (event: string, data: unknown) => void): Promise<Call> {
  mock.timers.enable({ apis: ['setInterval'] });
  try {
    let done = false;
    const backend = createMockBackend((event, data) => {
      if (event === 'scan.done') done = true;
      onEvent?.(event, data);
    });
    await backend('scan.start', { path: 'C:\\', mode: 'walk' });
    mock.timers.tick(10_000);
    assert.ok(done, '模拟扫描结束');
    return <R>(method: string, params: unknown = {}) => backend(method, params) as Promise<R>;
  } finally {
    mock.timers.reset();
  }
}

test('模拟扫描完成后同后端一样先发一条初始的 tree.changed，字段和 TreeChangeDto 一致', async () => {
  const changes: T.TreeChange[] = [];
  await scannedC((event, data) => { if (event === 'tree.changed') changes.push(data as T.TreeChange); });
  assert.equal(changes.length, 1);
  assert.deepEqual(keys(changes[0]), recordKeys('TreeChangeDto'), 'TreeChangeDto');
  assert.ok(changes[0].watching && changes[0].removedEntries === 0 && changes[0].otherChanges === 0 && !changes[0].overflow, '刚扫完：在跟踪、什么都没变');
});

test('模拟清理操作的 DTO 与事件契约一致，同 ID 重试幂等且异参数拒绝', async () => {
  const events: T.OperationStatus[] = [];
  const call = await scannedC((event, data) => { if (event === 'operation.progress') events.push(data as T.OperationStatus); });
  const suggestions = await call<T.Suggestion[]>('cleanup.list');
  const cache = suggestions.find((s) => s.action === 'deleteContents')!;
  const check = await call<T.CleanupCheck>('cleanup.check', { suggestionId: cache.id, nodeIds: null, permanent: null });
  const request = { suggestionId: cache.id, nodeIds: null, permanent: null, confirmationToken: check.confirmationToken, operationId: 'mock-cache' };
  const a = call<T.OpResult>('cleanup.run', request);
  const b = call<T.OpResult>('cleanup.run', request);
  const queued = await call<T.OperationStatus>('operation.status', { operationId: request.operationId });
  assert.equal(queued.status, 'queued', '执行前就能查询任务');
  assert.deepEqual(await a, await b, '同 ID 共享结果且令牌不重复消费');
  const status = await call<T.OperationStatus>('operation.status', { operationId: request.operationId });
  assert.deepEqual(keys(status), recordKeys('OperationStatusDto'));
  for (const event of events) assert.deepEqual(keys(event), recordKeys('OperationStatusDto'));
  assert.equal(status.status, 'completed');
  assert.deepEqual(keys(status.result!), recordKeys('OpResultDto'), 'OpResultDto');
  assert.equal(status.total, check.deleteCheck!.items.length);
  assert.equal(status.completed, status.total);
  assert.ok(status.processedEntries >= status.completed, '内部文件目录计数与确认目标分开');
  assert.ok(events.some((e) => e.status === 'running'));
  await assert.rejects(call('cleanup.run', { ...request, permanent: true }), /参数不一致/);
});

test('模拟操作在队列里可取消，取消后不删；外部命令只报告 external', async () => {
  const call = await scannedC();
  const list = await call<T.Suggestion[]>('cleanup.list');
  const cache = list.find((s) => s.action === 'deleteContents')!;
  const checked = await call<T.CleanupCheck>('cleanup.check', { suggestionId: cache.id, nodeIds: null, permanent: null });
  const pending = call<T.OpResult>('cleanup.run', { suggestionId: cache.id, nodeIds: null, permanent: null, confirmationToken: checked.confirmationToken, operationId: 'cancel-before-start' });
  await call('operation.cancel', { operationId: 'cancel-before-start' });
  await pending;
  const cancelled = await call<T.OperationStatus>('operation.status', { operationId: 'cancel-before-start' });
  assert.equal(cancelled.status, 'cancelled');
  assert.equal(cancelled.skipped, cancelled.total);
  assert.equal(cancelled.processedEntries, 0);
  assert.ok((await call<T.Suggestion[]>('cleanup.list')).some((s) => s.id === cache.id));
  const command = list.find((s) => s.action === 'command')!;
  const commandCheck = await call<T.CleanupCheck>('cleanup.check', { suggestionId: command.id, nodeIds: null, permanent: null });
  await call('cleanup.run', { suggestionId: command.id, nodeIds: null, permanent: null, confirmationToken: commandCheck.confirmationToken, operationId: 'external-command' });
  const external = await call<T.OperationStatus>('operation.status', { operationId: 'external-command' });
  assert.equal(external.status, 'external');
  assert.equal(external.completed, 0);
  assert.equal(external.freedBytes, null);
  assert.equal(await call('operation.status', { operationId: 'missing' }), null);
  await assert.rejects(call('operation.cancel', { operationId: 'missing' }), /找不到/);
});

/** 按完整路径找节点 ID（走 search.query） */
async function idOf(call: Call, path: string): Promise<number> {
  const name = path.slice(path.lastIndexOf('\\') + 1);
  const r = await call<T.SearchResult>('search.query', { query: name, scopeId: null, metric: 'alloc', limit: 100_000 });
  const hit = r.items.find((x) => x.path.toLowerCase() === path.toLowerCase());
  assert.ok(hit, `模拟数据里应该有 ${path}`);
  return hit.id;
}

test('模拟后端 tree.view：父在子前、子之和等于父、兄弟（含聚合节点）按值降序、不超预算', async () => {
  const call = await scannedC();
  let aggregates = 0;
  for (const maxNodes of [12000, 300, 50]) {
    const v = await call<T.ViewData>('tree.view', { nodeId: 0, metric: 'alloc', maxNodes });
    const budget = Math.min(50_000, Math.max(100, maxNodes));
    assert.ok(v.nodes.length <= budget, `maxNodes=${maxNodes}：${v.nodes.length} 个节点超了预算 ${budget}`);
    const sums = new Map<number, number>();
    const prev = new Map<number, number>();
    v.nodes.forEach((n, i) => {
      if (i === 0) return;
      assert.ok(n.p >= 0 && n.p < i, '父在子前');
      assert.ok(n.v <= (prev.get(n.p) ?? Infinity), `maxNodes=${maxNodes}：「${n.n}」比前一个兄弟大，聚合节点没按值排`);
      prev.set(n.p, n.v);
      sums.set(n.p, (sums.get(n.p) ?? 0) + n.v);
      if (n.id === -1) aggregates++;
    });
    for (const [p, s] of sums) {
      assert.equal(s, v.nodes[p].v, `maxNodes=${maxNodes}：「${v.nodes[p].n}」的子之和不等于它`);
      // 一块都放不下就不展开：只剩孤零零一块「其余」的目录不该出现（视图根例外）
      if (p > 0) assert.ok(v.nodes.some((n) => n.p === p && n.id >= 0), `「${v.nodes[p].n}」只展开出聚合节点`);
    }
  }
  assert.ok(aggregates > 0, '预算紧的时候应该有聚合节点');
});

test('模拟后端 tree.details：扩展名超过 15 种时第 16 行是「*」，各行合计对得上', async () => {
  const call = await scannedC();
  const summary = await call<T.ScanSummary>('scan.summary');
  const d = await call<T.NodeDetails>('tree.details', { nodeId: 0, metric: 'alloc' });
  assert.equal(d.extStats.length, 16);
  assert.equal(d.extStats[15].ext, '*');
  for (let i = 1; i < 15; i++) assert.ok(d.extStats[i].alloc <= d.extStats[i - 1].alloc, '前 15 行按占用降序');
  const sum = (key: 'alloc' | 'count'): number => d.extStats.reduce((s, x) => s + x[key], 0);
  assert.equal(sum('count'), d.files, '条目数合计等于文件数（硬链接的每个链接都算）');
  assert.equal(sum('alloc'), d.alloc - summary.unaccountedBytes, '字节合计等于已统计占用（虚拟的「未统计」不进扩展名统计）');
});

test('模拟后端确认令牌：口径同后端，一次性、清单必须一致、不带令牌不执行', async () => {
  const call = await scannedC();
  // 最大的几个是 pagefile.sys 这类受保护文件：挑第一个能删的
  let big: T.FileItem | undefined;
  let checked: T.DeleteCheck | undefined;
  for (const f of await call<T.FileItem[]>('tree.largest', { nodeId: 0, metric: 'alloc', limit: 50 })) {
    checked = await call<T.DeleteCheck>('node.checkDelete', { nodeIds: [f.id] });
    if (!checked.anyBlocked) { big = f; break; }
  }
  assert.ok(big && checked, '找到一个能删的大文件');
  assert.ok(checked.confirmationToken, '删除预检发令牌');
  await assert.rejects(call('node.delete', { nodeIds: [big!.id], permanent: true }), /确认已过期/, '不带令牌不删');
  const again = await call<T.DeleteCheck>('node.checkDelete', { nodeIds: [big.id] });
  const done = await call<T.OpResult>('node.delete', { nodeIds: [big.id], permanent: true, confirmationToken: again.confirmationToken });
  assert.ok(done.ok, '带着确认过的令牌照删');
  await assert.rejects(call('node.delete', { nodeIds: [big.id], permanent: true, confirmationToken: again.confirmationToken }), /确认已过期/, '令牌只能用一次');

  const list = await call<T.Suggestion[]>('cleanup.list');
  const temp = list.find((s) => s.action === 'deleteContents')!;
  const cc = await call<T.CleanupCheck>('cleanup.check', { suggestionId: temp.id, nodeIds: null, permanent: true });
  assert.ok(cc.confirmationToken && cc.deleteCheck && cc.deleteCheck.items.length > 0, '清空类建议预检列出要删的子项');
  await assert.rejects(call('cleanup.run', { suggestionId: temp.id, nodeIds: null, permanent: false, confirmationToken: cc.confirmationToken }),
    /对不上/, '永久删除开关和确认时不一样就拒绝');
  const cmd = list.find((s) => s.action === 'command')!;
  await assert.rejects(call('cleanup.run', { suggestionId: cmd.id, nodeIds: null, permanent: null }), /确认已过期/, '命令也要先确认');
  const settings = list.find((s) => s.action === 'openSettings');
  if (settings) assert.ok((await call<T.OpResult>('cleanup.run', { suggestionId: settings.id, nodeIds: null, permanent: null })).ok, '打开设置不用令牌');
});

test('模拟后端 search.query：作用域不含自己、根不出现、size: 不带比较符是恰好等于', async () => {
  const call = await scannedC();
  const v = await call<T.ViewData>('tree.view', { nodeId: 0, metric: 'alloc' });
  const windows = v.nodes.find((n) => n.p === 0 && n.n === 'Windows')!;
  const scoped = await call<T.SearchResult>('search.query', { query: 'windows', scopeId: windows.id, metric: 'alloc', limit: 1000 });
  assert.ok(scoped.total > 0, '作用域里有名字带 windows 的');
  assert.ok(!scoped.items.some((i) => i.id === windows.id), '作用域目录自己不在结果里');
  assert.ok(scoped.items.every((i) => i.path.startsWith('C:\\Windows\\')), '结果都在作用域里');

  const all = await call<T.SearchResult>('search.query', { query: '*', scopeId: null, metric: 'alloc', limit: 1000 });
  assert.ok(all.total > 0 && !all.items.some((i) => i.id === 0), '根不出现在结果里');

  const [big] = await call<T.FileItem[]>('tree.largest', { nodeId: 0, metric: 'alloc', limit: 1 });
  const exact = await call<T.SearchResult>('search.query', { query: `size:${big.alloc}`, scopeId: null, metric: 'alloc', limit: 1000 });
  assert.ok(exact.items.some((i) => i.id === big.id), 'size:N 找得到恰好 N 字节的');
  assert.ok(exact.items.every((i) => i.alloc === big.alloc), 'size:N 不带比较符，只要恰好等于 N 的');

  await assert.rejects(call('search.query', { query: 'a', scopeId: 1e9, metric: 'alloc', limit: 10 }), /节点不存在/);
});

test('模拟后端 search.query 同 SearchIndex：字段一致；| 是或；相关度排序；智能匹配时没有精确结果才放宽成近似', async () => {
  const call = await scannedC();
  const q = (query: string, more: object = {}) => call<T.SearchResult>('search.query', { query, scopeId: null, metric: 'alloc', limit: 1000, ...more });
  assert.deepEqual(enumOf('SearchSort'), ['size', 'relevance'] satisfies T.SearchSort[]);
  const windows = await q('windows');
  assert.deepEqual(keys(windows), recordKeys('SearchResultDto'), 'SearchResultDto');
  const either = await q('windows | program');
  const program = await q('program');
  assert.equal(either.total, windows.total + program.total - (await q('windows program')).total, '两组的并集');
  const ranked = await q('windows', { sort: 'relevance' });
  assert.equal(ranked.items[0].name.toLowerCase(), 'windows', '名字整个就是 windows 的排第一');
  assert.ok(windows.items.every((x, i) => i === 0 || windows.items[i - 1].alloc >= x.alloc), '默认按占用');
  const near = await q('wndws', { smart: true });
  assert.ok(near.fuzzy && near.items.some((x) => x.name === 'Windows'), '漏了字母的 wndws：近似找到 Windows');
  assert.deepEqual([(await q('wndws')).total, (await q('wndws')).fuzzy], [0, false], '不开智能匹配就不放宽');
  assert.equal((await q('windows', { smart: true })).fuzzy, false, '有精确结果就不放宽');
});

test('模拟后端用途分类和 C# 同口径：具体角色先于所属容器，扩展名兜底最后，没证据就是未识别', async () => {
  const call = await scannedC();
  async function uOf(path: string): Promise<number> {
    const name = path.slice(path.lastIndexOf('\\') + 1);
    const r = await call<T.SearchResult>('search.query', { query: name, scopeId: null, metric: 'alloc', limit: 100_000 });
    const hit = r.items.find((x) => x.path.toLowerCase() === path.toLowerCase());
    assert.ok(hit, `模拟数据里应该有 ${path}`);
    return hit.u;
  }
  const want: [string, number, string][] = [
    ['C:\\Windows\\System32', PURPOSE.System, 'Windows 里的系统组件'],
    ['C:\\Windows\\Logs\\CBS', PURPOSE.Logs, 'Windows 的日志目录比系统容器优先'],
    ['C:\\Windows\\MEMORY.DMP', PURPOSE.Logs, '内核转储'],
    ['C:\\Windows\\Temp', PURPOSE.Cache, 'Windows 临时目录'],
    ['C:\\Windows\\SoftwareDistribution\\Download', PURPOSE.Cache, 'Windows 更新缓存'],
    ['C:\\Program Files\\Microsoft Office', PURPOSE.Apps, '程序目录'],
    ['C:\\Program Files\\dotnet', PURPOSE.Sdk, 'SDK 比程序目录优先'],
    ['C:\\Program Files (x86)\\Steam\\steamapps\\common\\Cyberpunk 2077', PURPOSE.Games, '游戏库里的游戏'],
    ['C:\\Program Files (x86)\\Steam\\steamapps\\shadercache', PURPOSE.Cache, '游戏库里的着色器缓存'],
    ['C:\\ProgramData\\Microsoft\\Windows\\WER', PURPOSE.Logs, 'Windows 错误报告'],
    ['C:\\ProgramData\\Package Cache', PURPOSE.Cache, '安装包缓存'],
    ['C:\\Users\\Alex\\AppData\\Local\\Packages', PURPOSE.AppData, '应用数据'],
    ['C:\\Users\\Alex\\AppData\\Local', PURPOSE.AppData, 'AppData\\Local 自己有位置规则，子树大半是缓存也不换展示色'],
    ['C:\\Users\\Alex\\AppData\\Local\\Microsoft', PURPOSE.Cache, '只是继承应用数据的目录，子树大半是缓存就按缓存展示'],
    ['C:\\Users\\Alex\\AppData\\Local\\Google\\Chrome\\User Data\\Default\\Cache', PURPOSE.Cache, '浏览器缓存比应用数据优先'],
    ['C:\\Users\\Alex\\AppData\\Local\\CrashDumps', PURPOSE.Logs, '崩溃转储'],
    ['C:\\Users\\Alex\\AppData\\Local\\Temp', PURPOSE.Cache, '用户临时目录'],
    ['C:\\Users\\Alex\\AppData\\Local\\npm-cache', PURPOSE.Cache, '包管理器缓存'],
    ['C:\\Users\\Alex\\Downloads', PURPOSE.Media, '下载目录自己是用户文件夹'],
    ['C:\\Users\\Alex\\Downloads\\Win11_24H2_Chinese_Simplified_x64.iso', PURPOSE.Archives, '下载目录里的镜像按扩展名归档'],
    ['C:\\Users\\Alex\\Videos', PURPOSE.Media, '视频文件夹'],
    ['C:\\Users\\Alex\\source\\repos\\web-app', PURPOSE.Dev, '开发项目'],
    ['C:\\Users\\Alex\\stable-diffusion\\models', PURPOSE.Ai, '模型目录'],
    ['C:\\$Recycle.Bin', PURPOSE.System, '回收站是系统与恢复'],
    ['C:\\Windows.old', PURPOSE.System, '旧系统是系统与恢复'],
    ['C:\\pagefile.sys', PURPOSE.System, '卷根系统文件'],
  ];
  for (const [path, u, why] of want) assert.equal(await uOf(path), u, `${why}（${path}）`);

  // 数据文件不统一归 AI：模型目录以外的 .bin/.dat/.ckpt/.pak 一个都不能是 AI
  const data = await call<T.SearchResult>('search.query', { query: 'ext:bin,dat,ckpt,pak', scopeId: null, metric: 'alloc', limit: 100_000 });
  assert.ok(data.items.length > 100, '模拟数据里有足够多的通用数据文件');
  for (const x of data.items) if (!x.path.toLowerCase().includes('\\models\\')) assert.notEqual(x.u, PURPOSE.Ai, `${x.path} 不能凭扩展名算成 AI`);

  const s = await call<T.ScanSummary>('scan.summary');
  assert.equal(s.purposeBytes?.length, PURPOSE_COUNT);
  for (const u of [PURPOSE.Apps, PURPOSE.Games, PURPOSE.AppData, PURPOSE.Dev, PURPOSE.Sdk, PURPOSE.Cache, PURPOSE.Logs, PURPOSE.Ai, PURPOSE.Media, PURPOSE.Archives, PURPOSE.System, PURPOSE.Unexplained])
    assert.ok(s.purposeBytes![u] > 0, `汇总里 U${u} 有字节`);
});

test('模拟后端详情带角色、用途证据、所属对象：字段和取值范围和真后端一致，代表性例子同口径', async () => {
  const call = await scannedC();
  const details = (id: number): Promise<T.NodeDetails> => call<T.NodeDetails>('tree.details', { nodeId: id, metric: 'alloc' });

  // 取值范围：地图上的一批节点并发取详情（模拟后端每次请求都有人为延迟，串着走太慢）
  const v = await call<T.ViewData>('tree.view', { nodeId: 0, metric: 'alloc', maxNodes: 400 });
  const all = await Promise.all(v.nodes.filter((x) => x.id >= 0).map((x) => details(x.id)));
  assert.ok(all.length > 100, '抽到的节点够多');
  for (const d of all) {
    assert.ok(Number.isInteger(d.role) && d.role >= 0 && d.role < ROLE_COUNT, `${d.path} 的角色 ${d.role} 越界`);
    assert.ok(d.evidence in EVIDENCE_NAMES, `${d.path} 的用途证据 ${d.evidence}`);
    if (d.u === PURPOSE.Unknown || d.u === PURPOSE.Unexplained) assert.equal(d.evidence, 'unknown', `${d.path}：未识别的用途没有证据`);
    if (!d.owner) continue;
    assert.ok(d.owner.kind in OWNER_KIND_NAMES && d.owner.name.length > 0 && d.owner.evidence in EVIDENCE_NAMES, `${d.path} 的所属对象字段`);
    const inside = d.path.toLowerCase() === d.owner.rootPath.toLowerCase() || d.path.toLowerCase().startsWith(`${d.owner.rootPath.toLowerCase()}\\`);
    assert.ok(inside, `${d.path} 应该在所属对象的根 ${d.owner.rootPath} 里面`);
  }
  assert.ok(all.some((d) => d.owner) && all.some((d) => !d.owner), '有的有所属对象，有的没有');
  const root = all.find((d) => d.owner?.kind === 'game')!;
  assert.equal((await details(root.owner!.rootId)).path, root.owner!.rootPath, '所属对象的根节点 ID 和根路径对得上');

  // 代表性例子：dise 的游戏日志、项目依赖、包管理器缓存、应用数据、扩展名兜底、没有依据的
  const at = async (path: string): Promise<T.NodeDetails> => details(await idOf(call, path));
  const owner = (d: T.NodeDetails): string => (d.owner ? `${d.owner.kind}:${d.owner.name}:${d.owner.evidence}` : '无');
  const want: [string, number, T.Evidence, string, string][] = [
    ['C:\\Users\\Alex\\AppData\\Local\\npm-cache', ROLE.PackageCache, 'confirmed', 'packageManager:npm:confirmed', '包管理器缓存'],
    ['C:\\Users\\Alex\\source\\repos\\web-app', ROLE.Source, 'confirmed', 'project:web-app:confirmed', '项目根'],
    ['C:\\Users\\Alex\\source\\repos\\web-app\\node_modules', ROLE.Dependency, 'confirmed', 'project:web-app:confirmed', '项目依赖仍归项目'],
    ['C:\\Users\\Alex\\source\\repos\\tauri-thing\\target', ROLE.BuildOutput, 'confirmed', 'project:tauri-thing:confirmed', '构建产物'],
    ['C:\\Program Files (x86)\\Steam\\steamapps\\common\\Cyberpunk 2077', ROLE.Game, 'confirmed', 'game:Cyberpunk 2077:confirmed', '游戏库里的游戏'],
    ['C:\\Program Files (x86)\\Steam', ROLE.Program, 'inferred', 'app:Steam:inferred', 'Steam 客户端按目录名推测；目录大半是游戏库，按子树展示成游戏也是推测'],
    ['C:\\Program Files\\Microsoft Office', ROLE.Program, 'confirmed', 'app:Microsoft Office:inferred', '程序目录'],
    ['C:\\Program Files\\dotnet', ROLE.Runtime, 'confirmed', 'runtime:.NET:confirmed', '运行环境'],
    ['C:\\Windows\\Logs\\CBS', ROLE.Log, 'confirmed', 'system:Windows:confirmed', 'Windows 的日志归 Windows'],
    ['C:\\Windows\\MEMORY.DMP', ROLE.CrashDump, 'inferred', 'system:Windows:confirmed', '内核转储靠扩展名'],
    ['C:\\Users\\Alex\\AppData\\Local\\Google\\Chrome\\User Data\\Default\\Cache', ROLE.AppCache, 'confirmed', 'browser:Google Chrome:confirmed', '浏览器缓存归浏览器'],
    ['C:\\Users\\Alex\\AppData\\Roaming\\Code', ROLE.UserData, 'confirmed', 'app:Code:inferred', 'AppData 下的目录名'],
    ['C:\\Users\\Alex\\AppData\\Local\\Packages', ROLE.UserData, 'confirmed', '无', '应用包的数据目录本身不是应用'],
    ['C:\\Users\\Alex\\AppData\\Local\\Temp', ROLE.Temp, 'confirmed', '无', '临时目录谁的都不是'],
    ['C:\\Users\\Alex\\Downloads', ROLE.Unknown, 'confirmed', '无', '下载目录本身'],
    ['C:\\Users\\Alex\\Downloads\\Win11_24H2_Chinese_Simplified_x64.iso', ROLE.InstallImage, 'inferred', '无', '下载目录里的镜像按扩展名'],
    ['C:\\Users\\Alex\\stable-diffusion\\models', ROLE.Weights, 'confirmed', '无', '模型目录'],
    ['C:\\$Recycle.Bin', ROLE.Recovery, 'confirmed', 'system:回收站:confirmed', '回收站'],
  ];
  for (const [path, r, ev, who, why] of want) {
    const d = await at(path);
    assert.deepEqual({ role: d.role, evidence: d.evidence, owner: owner(d) }, { role: r, evidence: ev, owner: who }, `${why}（${path}）`);
  }
});

// ---------- M1d：最近文件、按所属对象聚合、关联详情 ----------

const DAY = 86_400_000;
const inScope = (m: T.Metric, x: { sizeInScope: number; allocInScope: number }): number => (m === 'alloc' ? x.allocInScope : x.sizeInScope);

test('契约：最近文件、所属对象聚合、关联详情的字段和枚举和 C# 的 DTO 一一对应', async () => {
  assert.deepEqual(enumOf('RecentSort'), [...RECENT_SORTS]);
  assert.deepEqual(enumOf('RelationKind'), [...RELATION_KINDS]);
  const call = await scannedC();
  const recent = await call<T.RecentFiles>('tree.recent', { days: 30 });
  assert.deepEqual(keys(recent), recordKeys('RecentFilesDto'), 'RecentFilesDto');
  for (const x of recent.items) assert.deepEqual(keys(x), recordKeys('RecentFileDto'), 'RecentFileDto');
  assert.deepEqual(keys(recent.items.find((x) => x.owner)!.owner!), recordKeys('OwnerDto'), 'OwnerDto');
  const usage = await call<T.OwnerUsage>('tree.owners');
  assert.deepEqual(keys(usage), recordKeys('OwnerUsageDto'), 'OwnerUsageDto');
  assert.deepEqual(keys(usage.items[0]), recordKeys('OwnerUsageItemDto'), 'OwnerUsageItemDto');
  assert.deepEqual(keys(usage.items[0].roots[0]), recordKeys('OwnerUsageRootDto'), 'OwnerUsageRootDto');
  const related = await call<T.Related>('tree.related', { nodeId: await idOf(call, 'C:\\Windows\\Logs\\CBS') });
  assert.deepEqual(keys(related), recordKeys('RelatedDto'), 'RelatedDto');
  assert.deepEqual(keys(related.items[0]), recordKeys('RelatedItemDto'), 'RelatedItemDto');
});

test('模拟后端 tree.recent：窗口按扫描完成时刻开，只出真文件，两种排序并列按 id，days 和 limit 夹住', async () => {
  const call = await scannedC();
  const bySize = await call<T.RecentFiles>('tree.recent', { days: 7, sort: 'size', metric: 'alloc', limit: 1000 });
  const byTime = await call<T.RecentFiles>('tree.recent', { days: 7, sort: 'modified', limit: 1000 });
  assert.ok(bySize.total > 10 && bySize.total === bySize.items.length && bySize.total <= 1000, `7 天里有一批文件且不截断（${bySize.total}）`);
  assert.equal(byTime.asOfMs, bySize.asOfMs, '基准是扫描完成时刻，什么时候查都一样');
  assert.equal(bySize.asOfMs, (await call<T.ScanSummary>('scan.summary')).completedAtMs, '基准就是汇总里的完成时刻');
  assert.deepEqual(byTime.items.map((x) => x.id).sort((a, b) => a - b), bySize.items.map((x) => x.id).sort((a, b) => a - b), '换排序不换集合');
  for (const x of bySize.items) {
    assert.ok(x.modifiedMs >= bySize.asOfMs - 7 * DAY && x.modifiedMs <= bySize.asOfMs, `${x.path} 在窗口里`);
    assert.ok(!hasFlag(x.f, Flag.Directory) && !hasFlag(x.f, Flag.Virtual) && !hasFlag(x.f, Flag.HardLinkDup), `${x.path} 是计入的真文件`);
    assert.equal(`${x.dirPath.replace(/\\$/, '')}\\${x.name}`, x.path, '所在目录 + 名字 = 路径');
    assert.ok(x.role >= 0 && x.role < ROLE_COUNT && x.u >= 0 && x.u < PURPOSE_COUNT);
    if (x.owner) {
      assert.ok(x.owner.kind in OWNER_KIND_NAMES && x.owner.evidence in EVIDENCE_NAMES, `${x.path} 的所属对象字段`);
      assert.ok(x.path === x.owner.rootPath || x.path.startsWith(`${x.owner.rootPath}\\`), `${x.path} 在所属对象的根里（文件自己可以就是根）`);
    }
  }
  bySize.items.slice(1).forEach((x, i) => {
    const prev = bySize.items[i];
    assert.ok(prev.alloc > x.alloc || (prev.alloc === x.alloc && prev.id < x.id), '按占用降序，并列按 id');
  });
  byTime.items.slice(1).forEach((x, i) => {
    const prev = byTime.items[i];
    assert.ok(prev.modifiedMs > x.modifiedMs || (prev.modifiedMs === x.modifiedMs && prev.id < x.id), '按修改时间新的在前，并列按 id');
  });
  const day = await call<T.RecentFiles>('tree.recent', { days: 0, limit: 0 });
  assert.equal(day.days, 1, 'days 夹到至少 1');
  assert.equal(day.items.length, 1, 'limit 夹到至少 1');
  assert.ok(day.total > 1, '截断时 total 照样是窗口内的总数');
  assert.equal((await call<T.RecentFiles>('tree.recent', { days: 1e6 })).days, 3650, 'days 夹到最多 3650');
  const pagefile = await idOf(call, 'C:\\pagefile.sys');
  assert.deepEqual((await call<T.RecentFiles>('tree.recent', { days: 1, scopeId: pagefile })).items.map((x) => x.id), [pagefile], '作用域是文件时含它自己');
  const windows = await call<T.RecentFiles>('tree.recent', { days: 3650, scopeId: await idOf(call, 'C:\\Windows'), limit: 1000 });
  assert.ok(windows.total > 0 && windows.items.every((x) => x.path.startsWith('C:\\Windows\\')), '作用域只看它的子树');
  await assert.rejects(call('tree.recent', { sort: 'bogus' }), /请求参数无效/);
  await assert.rejects(call('tree.recent', { scopeId: 1e9 }), /节点不存在或已删除/);
});

test('模拟后端 tree.owners：两种度量各自 owned + shared + unowned = total（等于地图上的大小），构成、根、排序、截断', async () => {
  const call = await scannedC();
  const windowsId = await idOf(call, 'C:\\Windows');
  for (const metric of ['alloc', 'size'] as const) {
    for (const path of [null, 'C:\\Windows', 'C:\\Windows\\Logs', 'C:\\Users\\Alex']) {
      const scopeId = path === null ? null : await idOf(call, path);
      const u = await call<T.OwnerUsage>('tree.owners', { scopeId, metric, limit: 1000 });
      const at = `${path ?? '整棵树'}/${metric}`;
      assert.equal(metric === 'alloc' ? u.totalAllocInScope : u.totalSizeInScope, (await call<T.ViewData>('tree.view', { nodeId: scopeId ?? 0, metric, maxNodes: 100 })).total, `${at}：总量和地图一致`);
      assert.equal(u.ownedAllocInScope + u.sharedAllocInScope + u.unownedAllocInScope, u.totalAllocInScope, `${at}：占用守恒`);
      assert.equal(u.ownedSizeInScope + u.sharedSizeInScope + u.unownedSizeInScope, u.totalSizeInScope, `${at}：大小守恒`);
      assert.equal(u.ownerCount, u.items.length, `${at}：limit 1000 不截断`);
      assert.equal(u.items.reduce((s, x) => s + x.allocInScope, 0), u.ownedAllocInScope, `${at}：owned 是各对象之和`);
      assert.equal(u.items.reduce((s, x) => s + x.sizeInScope, 0), u.ownedSizeInScope, `${at}：owned 是各对象之和`);
      u.items.forEach((x, i) => {
        assert.ok(x.kind in OWNER_KIND_NAMES && x.evidence in EVIDENCE_NAMES && x.name.length > 0, `${at}：${x.name} 的字段`);
        assert.equal(x.roleBytesInScope.length, ROLE_COUNT);
        assert.equal(x.roleBytesInScope.reduce((a, b) => a + b, 0), inScope(metric, x), `${at}：${x.name} 的角色构成之和等于计入大小`);
        assert.equal(x.roots.reduce((s, r) => s + r.allocInScope, 0), x.allocInScope, `${at}：${x.name} 各处根之和等于计入占用`);
        assert.equal(x.roots.reduce((s, r) => s + r.sizeInScope, 0), x.sizeInScope, `${at}：${x.name} 各处根之和等于计入大小`);
        x.roots.slice(1).forEach((r, j) => assert.ok(inScope(metric, x.roots[j]) > inScope(metric, r) || (inScope(metric, x.roots[j]) === inScope(metric, r) && x.roots[j].id < r.id), `${at}：根按度量降序`));
        if (i > 0) assert.ok(inScope(metric, u.items[i - 1]) >= inScope(metric, x), `${at}：对象按度量降序`);
      });
    }
  }
  const logs = await call<T.OwnerUsage>('tree.owners', { scopeId: await idOf(call, 'C:\\Windows\\Logs') });
  assert.deepEqual(logs.items.map((x) => [x.name, x.roots.map((r) => r.id)]), [['Windows', [windowsId]]], '作用域整个落在 Windows 里：那处根在作用域上方');
  assert.equal(logs.ownedAllocInScope, logs.totalAllocInScope);
  const full = await call<T.OwnerUsage>('tree.owners');
  const one = await call<T.OwnerUsage>('tree.owners', { limit: 0 });
  assert.ok(full.unownedAllocInScope > 0 && full.ownerCount > 5, '整盘有不归任何对象的，也有好几个对象');
  assert.deepEqual([one.items.length, one.ownerCount, one.ownedAllocInScope, one.items[0]], [1, full.ownerCount, full.ownedAllocInScope, full.items[0]], 'limit 夹到 1：只截 items，总数照旧');
  await assert.rejects(call('tree.owners', { scopeId: 1e9 }), /节点不存在或已删除/);
});

test('模拟后端 tree.related：所在的根 → 另几处根 → 换了角色的模块；证据取弱的，每种角色最多 3 个，没有所属对象就直说', async () => {
  const call = await scannedC();
  const details = (id: number): Promise<T.NodeDetails> => call<T.NodeDetails>('tree.details', { nodeId: id, metric: 'alloc' });
  const CBS = 'C:\\Windows\\Logs\\CBS';
  const cbs = await idOf(call, CBS);
  const r = await call<T.Related>('tree.related', { nodeId: cbs, limit: 1000 });
  const root = r.items[0];
  assert.ok(r.owner?.name === 'Windows' && root.relation === 'root' && root.id === r.owner.rootId && root.evidence === r.owner.evidence, '第一条是所在的根');
  const order = r.items.map((x) => RELATION_KINDS.indexOf(x.relation));
  assert.deepEqual(order, [...order].sort((a, b) => a - b), '先根，再另几处根，最后模块');
  assert.ok(r.total >= r.items.length && r.note === null);

  const others = r.items.filter((x) => x.relation === 'otherRoot');
  assert.ok(others.length >= 3, 'Windows 有好几处根（页面文件、休眠文件……）');
  others.slice(1).forEach((x, i) => assert.ok(others[i].alloc > x.alloc || (others[i].alloc === x.alloc && others[i].id < x.id), '另几处根按占用降序'));
  const ev = Object.keys(EVIDENCE_NAMES);
  for (const x of others) {
    const own = (await details(x.id)).owner!;
    assert.ok(own.name === 'Windows' && own.rootId === x.id, `${x.path} 是 Windows 的另一处根`);
    assert.equal(x.evidence, ev[Math.min(ev.indexOf(own.evidence), ev.indexOf(root.evidence))], `${x.path}：证据取两处里弱的`);
  }

  const modules = r.items.filter((x) => x.relation === 'roleModule');
  assert.ok(modules.length > 0, 'C:\\Windows 下有换了角色的目录');
  const perRole = new Map<number, number>();
  for (const x of modules) {
    perRole.set(x.role, (perRole.get(x.role) ?? 0) + 1);
    const d = await details(x.id);
    assert.ok(x.isDir && x.role !== ROLE.Unknown && x.evidence === root.evidence, `${x.path} 是目录、角色已知、证据同所在的根`);
    assert.equal(d.owner?.rootId, root.id, `${x.path} 在所在那处根自己的地盘里`);
    assert.notEqual((await details(d.ancestors.at(-1)!.id)).role, x.role, `${x.path} 的角色和上级不同`);
    assert.ok(!`${CBS}\\`.toLowerCase().startsWith(`${x.path}\\`.toLowerCase()), `${x.path} 不是包含节点的目录`);
  }
  assert.ok([...perRole.values()].every((n) => n <= 3), '每种角色最多 3 个');

  const limited = await call<T.Related>('tree.related', { nodeId: cbs, limit: 0 });
  assert.deepEqual([limited.items, limited.total], [[root], r.total], 'limit 夹到 1：总数照旧');
  const self = await call<T.Related>('tree.related', { nodeId: root.id });
  assert.ok(self.items.length > 0 && self.items.every((x) => x.relation !== 'root'), '节点自己就是根：没有「所在的根」这一条');
  const none = await call<T.Related>('tree.related', { nodeId: await idOf(call, 'C:\\Users\\Alex\\Downloads') });
  assert.deepEqual([none.owner, none.items, none.total, none.note], [null, [], 0, '尚未识别关联对象'], '没有所属对象：不编造关联');
  await assert.rejects(call('tree.related', { nodeId: 1e9 }), /节点不存在或已删除/);
});

// ---------- M3：目录列表 ----------

test('契约与口径：tree.children 的字段和枚举对上 C#；分页拼起来就是全部直接子项，地图并掉的小块在列表里都有', async () => {
  assert.deepEqual(enumOf('ChildSort'), [...CHILD_SORTS]);
  const call = await scannedC();
  const first = await call<T.Children>('tree.children', { nodeId: 0, metric: 'alloc', sort: 'size', limit: 5 });
  assert.deepEqual(keys(first), recordKeys('ChildrenDto'), 'ChildrenDto');
  assert.deepEqual(keys(first.items[0]), recordKeys('ChildEntryDto'), 'ChildEntryDto');
  // 找一个子项多到地图会聚合的目录
  const view = await call<T.ViewData>('tree.view', { nodeId: 0, metric: 'alloc', maxNodes: 400 });
  const agg = view.nodes.find((n) => hasFlag(n.f, Flag.Aggregate) && n.p >= 0);
  assert.ok(agg, '假数据里有被聚合成「其余 N 项」的小块');
  const parent = view.nodes[agg!.p].id;
  for (const sort of CHILD_SORTS) {
    const all: T.ChildEntry[] = [];
    let total = 0;
    for (let offset = 0; ; offset += 7) {
      const page = await call<T.Children>('tree.children', { nodeId: parent, metric: 'size', sort, offset, limit: 7 });
      total = page.total;
      all.push(...page.items);
      if (page.items.length < 7) break;
    }
    assert.equal(all.length, total, `${sort}：分页拼起来等于总数`);
    assert.equal(new Set(all.map((x) => x.id)).size, total, `${sort}：没有重复`);
    const cmp = sort === 'size' ? (a: T.ChildEntry, b: T.ChildEntry) => b.value - a.value
      : sort === 'modified' ? (a: T.ChildEntry, b: T.ChildEntry) => (b.modifiedMs ?? 0) - (a.modifiedMs ?? 0)
        : (a: T.ChildEntry, b: T.ChildEntry) => Number(b.isDir) - Number(a.isDir);
    assert.ok(all.every((x, i) => i === 0 || cmp(all[i - 1], x) <= 0), `${sort}：顺序对`);
  }
  const shown = new Set(view.nodes.filter((n) => n.p === agg!.p && n.id >= 0).map((n) => n.id));
  const listed = await call<T.Children>('tree.children', { nodeId: parent, limit: 500 });
  assert.ok(listed.items.some((x) => !shown.has(x.id)), '地图上看不到的小项在列表里有');
  assert.equal((await call<T.Children>('tree.children', { nodeId: parent, limit: 0 })).items.length, 1, 'limit 夹到 1');
  await assert.rejects(() => call('tree.children', { nodeId: parent, sort: 'bogus' }), /请求参数无效/);
});
