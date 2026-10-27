import type { Emit, MockHandler } from './api.ts';
import type * as T from './types.ts';
import { CATEGORY_COUNT, CHILD_SORTS, Cat, Flag, RECENT_SORTS, SCAN_ISSUE_KINDS, hasFlag } from './types.ts';
import { PURPOSE, PURPOSE_COUNT } from './purposes.ts';
import { EVIDENCE_NAMES, OWNER_KIND_NAMES, ROLE, ROLE_COUNT } from './roles.ts';
import { isLocale, locale, localized, t as tr, type MsgId } from './i18n.ts';

// 浏览器里调界面用的模拟后端（pnpm dev 时自动启用）。数据是确定性的假数据，
// 语义尽量贴着 C# 版：视图裁剪、硬链接只计一次、删除后聚合值回退。URL 带 ?admin=1 模拟管理员。

const KB = 1024;
const MB = KB * 1024;
const GB = MB * 1024;
const DAY = 86_400_000;
// 演示数据的「现在」跟着真实时钟（取整到小时）：启动页只列最近一周，拿固定日期算，过几天演示就空了
const NOW = Math.floor(Date.now() / 3_600_000) * 3_600_000;

const EXT_CAT: Record<string, number> = {};
for (const [k, list] of [
  [Cat.Video, 'mp4 mkv mov avi webm'],
  [Cat.Image, 'jpg jpeg png psd raw webp gif'],
  [Cat.Audio, 'mp3 flac wav m4a'],
  [Cat.Document, 'pdf docx xlsx pptx txt md'],
  [Cat.Archive, 'zip 7z rar cab tar gz'],
  [Cat.Code, 'js ts json py cs html css rs toml map'],
  [Cat.Executable, 'dll exe sys msi msp mui pdb node'],
  [Cat.System, 'cat mum manifest'],
  [Cat.DiskImage, 'iso vhdx wim esd vmdk'],
  [Cat.Data, 'db log dat tmp bin pak safetensors ckpt etl cache'],
] as const) {
  for (const e of list.split(' ')) EXT_CAT[e] = k;
}

function rng(seed: number): () => number {
  return () => {
    seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const roundUp = (n: number) => (n <= 0 ? 0 : Math.ceil(n / 4096) * 4096);
const clamp = (v: number, lo: number, hi: number): number => Math.min(hi, Math.max(lo, v));
/** C# 枚举的顺序：OwnerKind（并列时按它排）、Evidence（弱在前） */
const KINDS = Object.keys(OWNER_KIND_NAMES);
const EVIDENCES = Object.keys(EVIDENCE_NAMES);

class MockTree {
  rootPath: string;
  parent: number[] = [];
  name: string[] = [];
  flags: number[] = [];
  size: number[] = [];
  alloc: number[] = [];
  mtime: number[] = [];
  k: number[] = [];
  files: number[] = [];
  dirs: number[] = [];
  children: number[][] = [];
  groups = new Map<number, number[]>();
  /** 用途类别、数据角色、用途证据，classifyPurposes 填 */
  u: number[] = [];
  role: number[] = [];
  evidence: T.Evidence[] = [];
  /** 所属对象表和它们的根（节点 → 对象下标、这一处的证据） */
  owners: { kind: T.OwnerKind; name: string; evidence: T.Evidence }[] = [];
  ownerRoots = new Map<number, { owner: number; evidence: T.Evidence }>();
  version = 1;

  constructor(rootPath: string) {
    this.rootPath = rootPath;
    this.add(-1, rootPath, Flag.Directory, 0, 0, NOW);
  }

  add(p: number, name: string, flags: number, size: number, alloc: number, mtime: number): number {
    const id = this.parent.length;
    const isDir = hasFlag(flags, Flag.Directory);
    const virtual = hasFlag(flags, Flag.Virtual);
    const ext = name.includes('.') ? name.slice(name.lastIndexOf('.') + 1).toLowerCase() : '';
    this.parent.push(p);
    this.name.push(name);
    this.flags.push(flags);
    this.size.push(size);
    this.alloc.push(alloc);
    this.mtime.push(mtime);
    this.k.push(
      isDir ? Cat.Folder
        : virtual ? Cat.Unaccounted
          : hasFlag(flags, Flag.Metafile) || /^(pagefile|hiberfil|swapfile)\.sys$/i.test(name) ? Cat.System
            : (EXT_CAT[ext] ?? Cat.Other),
    );
    this.files.push(isDir || virtual ? 0 : 1);
    this.dirs.push(0);
    this.children.push([]);
    if (p >= 0) this.children[p].push(id);
    return id;
  }

  link(nodes: number[]): void {
    const sorted = [...nodes].sort((a, b) => this.path(a).toLowerCase().localeCompare(this.path(b).toLowerCase()));
    for (const n of nodes) {
      this.groups.set(n, nodes);
      if (n === sorted[0]) this.flags[n] &= ~Flag.HardLinkDup;
      else this.flags[n] |= Flag.HardLinkDup;
    }
  }

  seal(): void {
    for (let i = this.parent.length - 1; i > 0; i--) {
      const p = this.parent[i];
      if (!hasFlag(this.flags[i], Flag.HardLinkDup)) {
        this.size[p] += this.size[i];
        this.alloc[p] += this.alloc[i];
      }
      this.files[p] += this.files[i];
      this.dirs[p] += this.dirs[i] + (this.realDir(i) ? 1 : 0);
    }
    for (let i = 0; i < this.parent.length; i++) this.sortKids(i);
  }

  realDir(i: number): boolean {
    return hasFlag(this.flags[i], Flag.Directory) && !hasFlag(this.flags[i], Flag.Virtual);
  }

  counted(i: number, m: T.Metric): number {
    return hasFlag(this.flags[i], Flag.HardLinkDup) ? 0 : this.val(i, m);
  }

  val(i: number, m: T.Metric): number {
    return m === 'alloc' ? this.alloc[i] : this.size[i];
  }

  sortKids(i: number): void {
    this.children[i].sort((a, b) => {
      const da = hasFlag(this.flags[a], Flag.Deleted) ? 1 : 0;
      const db = hasFlag(this.flags[b], Flag.Deleted) ? 1 : 0;
      return da - db || this.counted(b, 'alloc') - this.counted(a, 'alloc');
    });
  }

  live(i: number): number[] {
    return this.children[i].filter((c) => !hasFlag(this.flags[c], Flag.Deleted));
  }

  path(i: number): string {
    const parts: string[] = [];
    for (let x = i; x > 0; x = this.parent[x]) {
      if (x === i || !hasFlag(this.flags[x], Flag.Virtual)) parts.unshift(this.name[x]);
    }
    const root = this.rootPath.endsWith('\\') ? this.rootPath : `${this.rootPath}\\`;
    return i === 0 ? this.rootPath : root + parts.join('\\');
  }

  find(path: string): number {
    const root = this.rootPath.replace(/\\$/, '').toLowerCase();
    const p = path.replace(/\\$/, '').toLowerCase();
    if (p === root) return 0;
    if (!p.startsWith(`${root}\\`)) return -1;
    let cur = 0;
    for (const seg of p.slice(root.length + 1).split('\\')) {
      const next = this.live(cur).find((c) => this.name[c].toLowerCase() === seg);
      if (next === undefined) return -1;
      cur = next;
    }
    return cur;
  }

  ancestors(i: number): T.Ancestor[] {
    const a: T.Ancestor[] = [];
    for (let p = this.parent[i]; p >= 0; p = this.parent[p]) a.unshift({ id: p, name: this.name[p] });
    return a;
  }

  isUnder(i: number, anc: number): boolean {
    for (let x = i; x >= 0; x = this.parent[x]) if (x === anc) return true;
    return false;
  }

  /** 返回净释放的 alloc；硬链接还有活链接时把计入身份转过去 */
  markDeleted(i: number): number {
    if (i <= 0 || hasFlag(this.flags[i], Flag.Deleted)) return 0;
    const dup = hasFlag(this.flags[i], Flag.HardLinkDup);
    const s = dup ? 0 : this.size[i];
    const a = dup ? 0 : this.alloc[i];
    const f = this.files[i];
    const d = this.dirs[i] + (this.realDir(i) ? 1 : 0);
    for (let p = this.parent[i]; p >= 0; p = this.parent[p]) {
      this.size[p] -= s;
      this.alloc[p] -= a;
      this.files[p] -= f;
      this.dirs[p] -= d;
    }
    const counted: number[] = [];
    const stack = [i];
    while (stack.length) {
      const n = stack.pop()!;
      this.flags[n] |= Flag.Deleted;
      if (this.groups.has(n) && !hasFlag(this.flags[n], Flag.HardLinkDup)) counted.push(n);
      stack.push(...this.live(n));
    }
    let promoted = 0;
    for (const n of counted) {
      const heir = this.groups.get(n)!.find((x) => !hasFlag(this.flags[x], Flag.Deleted));
      if (heir === undefined) continue;
      this.flags[n] |= Flag.HardLinkDup;
      this.flags[heir] &= ~Flag.HardLinkDup;
      for (let p = this.parent[heir]; p >= 0; p = this.parent[p]) {
        this.size[p] += this.size[heir];
        this.alloc[p] += this.alloc[heir];
        this.sortKids(p);
      }
      promoted += this.alloc[heir];
    }
    for (let p = this.parent[i]; p >= 0; p = this.parent[p]) this.sortKids(p);
    this.version++;
    return a - promoted;
  }
}

// ---------------- 假数据 ----------------

interface Gen {
  t: MockTree;
  r: () => number;
}

function when(g: Gen, maxAgeDays: number): number {
  return NOW - Math.floor(g.r() * maxAgeDays) * DAY - Math.floor(g.r() * DAY);
}

function dir(g: Gen, p: number, name: string, flags = 0): number {
  return g.t.add(p, name, Flag.Directory | flags, 0, 0, when(g, 400));
}

function file(g: Gen, p: number, name: string, bytes: number, flags = 0, ageDays = 400): number {
  const b = Math.round(bytes);
  return g.t.add(p, name, flags, b, roundUp(b), when(g, ageDays));
}

/** count 个文件，总大小约 total，大小呈长尾分布 */
function files(g: Gen, p: number, count: number, total: number, exts: string[], stem = 'file', ageDays = 400): number[] {
  const w = Array.from({ length: count }, () => Math.min(Math.pow(g.r() + 0.02, -1.6), 400));
  const sum = w.reduce((a, b) => a + b, 0);
  return w.map((x, i) => {
    const ext = exts[Math.floor(g.r() * exts.length)];
    const name = `${stem}_${(i * 7919 + 13).toString(36)}${ext ? `.${ext}` : ''}`;
    return file(g, p, name, (x / sum) * total, 0, ageDays);
  });
}

function path(g: Gen, p: number, ...names: string[]): number {
  let cur = p;
  for (const n of names) cur = g.t.live(cur).find((c) => g.t.name[c] === n) ?? dir(g, cur, n);
  return cur;
}

function buildSystemDrive(root: string, admin: boolean, mft: boolean): MockTree {
  const g: Gen = { t: new MockTree(root), r: rng(20261015) };
  const win = dir(g, 0, 'Windows');
  const s32 = dir(g, win, 'System32');
  const s32Files = files(g, s32, 900, 6.8 * GB, ['dll', 'dll', 'dll', 'exe', 'mui', 'dat'], 'sys');
  files(g, dir(g, s32, 'drivers'), 300, 0.45 * GB, ['sys'], 'drv');
  const repo = path(g, s32, 'DriverStore', 'FileRepository');
  for (let i = 0; i < 40; i++) files(g, dir(g, repo, `nv_dispsig.inf_amd64_${(i * 2654435761 >>> 0).toString(16)}`), 12, (0.05 + g.r() * 0.2) * GB, ['dll', 'sys', 'cat', 'bin'], 'drv');
  files(g, dir(g, win, 'SysWOW64'), 600, 2.1 * GB, ['dll', 'exe'], 'wow');
  const sxs = dir(g, win, 'WinSxS');
  for (let i = 0; i < 120; i++) {
    const c = dir(g, sxs, `amd64_microsoft-windows-component${i}_31bf3856ad364e35_10.0.26100.${4000 + i}_none_${(i * 40503 >>> 0).toString(16)}`);
    const own = files(g, c, 4, (0.01 + g.r() * 0.05) * GB, ['dll', 'mum', 'manifest'], 'cmp');
    // 一部分和 System32 是硬链接：同一个文件，只能算一次
    const twin = s32Files[(i * 7) % s32Files.length];
    const l = g.t.add(c, g.t.name[twin], 0, g.t.size[twin], g.t.alloc[twin], g.t.mtime[twin]);
    g.t.link([twin, l]);
    void own;
  }
  files(g, dir(g, win, 'Installer'), 80, 3.2 * GB, ['msi', 'msp'], 'inst');
  files(g, path(g, win, 'SoftwareDistribution', 'Download'), 40, 1.8 * GB, ['cab', 'esd'], 'kb');
  files(g, dir(g, win, 'Temp'), 200, 0.6 * GB, ['tmp', 'log', 'etl'], 'tmp', 30);
  files(g, path(g, win, 'Logs', 'CBS'), 12, 0.35 * GB, ['log', 'cab'], 'CbsPersist');
  files(g, dir(g, win, 'Fonts'), 400, 0.9 * GB, ['ttf', 'ttc', 'otf'], 'font');
  file(g, win, 'MEMORY.DMP', 2.4 * GB, Flag.System, 60);

  const pf = dir(g, 0, 'Program Files');
  files(g, dir(g, pf, 'Microsoft Office'), 700, 4.2 * GB, ['dll', 'exe', 'msi', 'dat'], 'office');
  files(g, path(g, pf, 'Adobe', 'Adobe Photoshop 2026'), 900, 5.1 * GB, ['dll', 'exe', 'pak', 'dat'], 'ps');
  files(g, dir(g, pf, 'NVIDIA Corporation'), 300, 1.8 * GB, ['dll', 'exe', 'bin'], 'nv');
  const apps = dir(g, pf, 'WindowsApps', admin ? Flag.Hidden : Flag.Hidden | Flag.Inaccessible);
  if (admin) files(g, apps, 400, 9 * GB, ['dll', 'exe', 'pak'], 'app');
  files(g, dir(g, pf, 'dotnet'), 500, 1.1 * GB, ['dll', 'exe', 'json'], 'dotnet');

  const common = path(g, 0, 'Program Files (x86)', 'Steam', 'steamapps', 'common');
  files(g, dir(g, common, 'Cyberpunk 2077'), 260, 72 * GB, ['pak', 'pak', 'bin', 'dll', 'exe'], 'archive');
  files(g, dir(g, common, 'ELDEN RING'), 120, 48 * GB, ['bin', 'pak', 'dll'], 'data');
  files(g, dir(g, g.t.parent[common], 'shadercache'), 300, 3 * GB, ['bin'], 'shader');

  const pd = dir(g, 0, 'ProgramData', Flag.Hidden);
  files(g, path(g, pd, 'Microsoft', 'Windows', 'WER', 'ReportArchive'), 60, 0.8 * GB, ['dmp', 'txt'], 'wer', 120);
  files(g, dir(g, pd, 'Package Cache'), 200, 2.6 * GB, ['msi', 'cab', 'exe'], 'pkg');

  const me = path(g, 0, 'Users', 'Alex');
  const local = path(g, me, 'AppData', 'Local');
  g.t.flags[g.t.parent[local]] |= Flag.Hidden;
  files(g, dir(g, local, 'Temp'), 1500, 4.3 * GB, ['tmp', 'log', 'zip', 'exe', ''], 'tmp', 90);
  const chrome = path(g, local, 'Google', 'Chrome', 'User Data', 'Default');
  files(g, path(g, chrome, 'Cache', 'Cache_Data'), 2500, 1.6 * GB, [''], 'f');
  files(g, dir(g, chrome, 'Code Cache'), 600, 0.4 * GB, [''], 'js');
  files(g, path(g, local, 'Microsoft', 'Edge', 'User Data', 'Default', 'Cache'), 1200, 0.9 * GB, [''], 'f');
  files(g, dir(g, local, 'npm-cache'), 4000, 2.2 * GB, ['', 'json'], 'content');
  files(g, path(g, local, 'pip', 'Cache'), 800, 1.3 * GB, ['', 'whl'], 'http');
  files(g, path(g, local, 'NuGet', 'v3-cache'), 600, 0.8 * GB, ['dat', 'json'], 'nupkg');
  files(g, dir(g, local, 'CrashDumps'), 9, 1.1 * GB, ['dmp'], 'crash', 200);
  const explorer = path(g, local, 'Microsoft', 'Windows', 'Explorer');
  for (const s of ['32', '96', '256', '1024', 'idx']) file(g, explorer, `thumbcache_${s}.db`, (0.03 + g.r() * 0.2) * GB);
  files(g, dir(g, local, 'Packages'), 900, 3 * GB, ['dat', 'db', 'json'], 'pkg');
  files(g, path(g, me, 'AppData', 'Roaming', 'Code'), 700, 0.6 * GB, ['json', 'js', 'db'], 'code');

  const dl = dir(g, me, 'Downloads');
  file(g, dl, 'Win11_24H2_Chinese_Simplified_x64.iso', 5.4 * GB, 0, 300);
  file(g, dl, 'ubuntu-26.04-desktop-amd64.iso', 6.1 * GB, 0, 120);
  files(g, dl, 160, 14 * GB, ['zip', 'exe', 'msi', 'mp4', 'pdf', '7z'], 'download', 500);
  files(g, dir(g, me, 'Videos'), 120, 38 * GB, ['mkv', 'mp4', 'mov'], 'clip');
  const pics = dir(g, me, 'Pictures');
  for (const y of ['2023', '2024', '2025', '2026']) files(g, dir(g, pics, y), 750, 2.2 * GB, ['jpg', 'jpg', 'png', 'raw', 'psd'], 'IMG');
  files(g, dir(g, me, 'Music'), 800, 6 * GB, ['flac', 'mp3'], 'track');
  files(g, dir(g, me, 'Documents'), 600, 4 * GB, ['pdf', 'docx', 'xlsx', 'pptx', 'md'], 'doc');
  const repos = path(g, me, 'source', 'repos');
  const web = dir(g, repos, 'web-app');
  file(g, web, 'package.json', 2 * KB);
  const nm = dir(g, web, 'node_modules');
  for (let i = 0; i < 300; i++) files(g, dir(g, nm, `pkg-${i.toString(36)}`), 20, (0.5 + g.r() * 8) * MB, ['js', 'js', 'json', 'map', 'ts', 'node'], 'index');
  const rs = dir(g, repos, 'tauri-thing');
  file(g, rs, 'Cargo.toml', 1 * KB);
  files(g, path(g, rs, 'target', 'debug'), 2000, 3.6 * GB, ['rlib', 'exe', 'pdb', 'd', 'bin'], 'dep');
  files(g, path(g, me, 'stable-diffusion', 'models'), 14, 21 * GB, ['safetensors', 'ckpt'], 'model');

  const rb = dir(g, 0, '$Recycle.Bin', Flag.Hidden | Flag.System);
  files(g, dir(g, rb, 'S-1-5-21-1004336348-1177238915-682003330-1001'), 30, 2.3 * GB, ['mp4', 'zip', 'psd'], '$R', 60);
  const svi = dir(g, 0, 'System Volume Information', Flag.Hidden | Flag.System | (admin ? 0 : Flag.Inaccessible));
  if (admin) files(g, svi, 6, 7.5 * GB, [''], '{3808876b-c176-4e48-b7ae-04046e6cc752}');
  files(g, dir(g, 0, 'Windows.old'), 600, 14 * GB, ['dll', 'exe', 'dat'], 'old', 20);
  file(g, 0, 'pagefile.sys', 16 * GB, Flag.Hidden | Flag.System, 1);
  file(g, 0, 'hiberfil.sys', 12.7 * GB, Flag.Hidden | Flag.System, 1);
  file(g, 0, 'swapfile.sys', 256 * MB, Flag.Hidden | Flag.System, 1);
  if (mft) {
    const meta = g.t.add(0, tr('$NTFS 元数据'), Flag.Directory | Flag.Virtual | Flag.Metafile, 0, 0, NOW);
    file(g, meta, '$MFT', MFT_BYTES, Flag.Metafile | Flag.Hidden | Flag.System);
    file(g, meta, '$LogFile', 64 * MB, Flag.Metafile | Flag.Hidden | Flag.System);
    file(g, meta, '$Bitmap', 15 * MB, Flag.Metafile | Flag.Hidden | Flag.System);
    file(g, path(g, meta, '$Extend'), '$UsnJrnl', 32 * MB, Flag.Metafile | Flag.Hidden | Flag.System);
  }
  // 链接和挂载点放在最后造：前面的随机数序列一个都不动，别的模拟数据保持原样
  for (const [p] of LINKS) {
    const names = p.slice(3).split('\\');
    dir(g, path(g, 0, ...names.slice(0, -1)), names.at(-1)!, Flag.Reparse | (names.length === 1 ? Flag.Hidden | Flag.System : 0));
  }
  return g.t;
}

/** 模拟的 $MFT 大小：MFT 扫描里它在「$NTFS 元数据」下，常规扫描时它是未解释容量里有依据的那一份 */
const MFT_BYTES = Math.round(1.9 * GB);

// 扫描报告的原因文字，和 C# 那边（ScanCounters.RecordLink、WalkScanner、ScanService.SplitGap）一字不差
const REASONS = localized({
  junction: '联接点（junction）：指向别处的目录，按规则不进入，免得重复统计或绕成环',
  denied: '拒绝访问（错误 5）：里面有什么、有多大都不知道',
  symlink: '目录符号链接：按规则不进入，免得重复统计或绕成环',
  mount: '卷挂载点：这里挂着另一个卷，按规则不进入，它的内容不属于本次扫描',
  metadata: '常规扫描读不到 NTFS 元数据；这是文件系统报告的 $MFT（主文件表）大小，$LogFile、$Bitmap 等更小的元数据问不到，没有算进来',
  unconfirmedWalk: '原因未确认：无权访问的目录、卷影副本、扫描期间的写入都可能占着这部分，扫描结果给不出依据，不归因于其中任何一项',
  unconfirmedMft: '原因未确认：MFT 扫描已经统计了全部记录（含元数据），扫描期间的写入、损坏或孤立的记录都可能造成剩下的差额',
});

/** 系统盘上按规则跳过的链接和挂载点：buildSystemDrive 照着它造节点，异常清单照着它出条目，两边不会对不上 */
const LINKS: [string, 'link' | 'mountPoint', 'junction' | 'symlink' | 'mount'][] = [
  ['C:\\Documents and Settings', 'link', 'junction'],
  ['C:\\Users\\All Users', 'link', 'symlink'],
  ['C:\\Users\\Alex\\AppData\\Local\\Application Data', 'link', 'junction'],
  ['C:\\Mount\\Backup', 'mountPoint', 'mount'],
];

/** 常规扫描在系统盘上碰到的读取失败（模拟数据里两条，挂在真实存在的目录上） */
const READ_ERRORS: { path: string; reason: MsgId }[] = [
  { path: 'C:\\ProgramData\\Package Cache', reason: '无法完整读取目录：由于 I/O 设备错误，无法运行此项请求。（错误 1117）' },
  { path: 'C:\\Windows\\Logs\\CBS', reason: '无法完整读取目录：另一个程序正在使用此文件，进程无法访问。（错误 32）' },
];

/** 和 ScanCounters.MaxIssues 同一个数（契约测试读 C# 源文件比对） */
export const MAX_ISSUES = 10_000;

type Issues = Record<T.ScanIssueKind, T.ScanIssue[]>;

/** 一次模拟扫描的异常清单，按路径排好（同 ScanCounters.SortIssues） */
function scanIssues(t: MockTree, mode: T.ScanMode): Issues {
  const issues: Issues = { denied: [], link: [], mountPoint: [], error: [] };
  for (let i = 1; i < t.parent.length; i++)
    if (hasFlag(t.flags[i], Flag.Inaccessible)) issues.denied.push({ path: t.path(i), reason: REASONS.denied });
  for (const [p, kind, reason] of LINKS) if (t.find(p) > 0) issues[kind].push({ path: p, reason: REASONS[reason] });
  if (mode === 'walk') for (const e of READ_ERRORS) if (t.find(e.path) > 0) issues.error.push({ path: e.path, reason: tr(e.reason) });
  const key = (s: string): string => s.toUpperCase();
  for (const list of Object.values(issues)) list.sort((a, b) => (key(a.path) < key(b.path) ? -1 : key(a.path) > key(b.path) ? 1 : 0));
  return issues;
}

/** 同 ScanService.SplitGap：常规扫描先扣 $MFT（下限），余数原因未确认；MFT 扫描的元数据已经在树里 */
function splitGap(mode: T.ScanMode, fileSystem: string, gap: number): T.GapPart[] {
  if (gap <= 0) return [];
  const mft = mode === 'walk' && fileSystem === 'NTFS' ? Math.min(MFT_BYTES, gap) : 0;
  const parts: T.GapPart[] = [];
  if (mft > 0) parts.push({ kind: 'ntfsMetadata', bytes: mft, basis: REASONS.metadata });
  if (gap > mft) parts.push({ kind: 'unconfirmed', bytes: gap - mft, basis: mode === 'mft' ? REASONS.unconfirmedMft : REASONS.unconfirmedWalk });
  return parts;
}

/** 汇总里能留到下次启动的部分（同 SummaryStore.Save）：不带扫描号、节点 ID、树版本 */
function toSaved(s: T.ScanSummary): T.SavedSummary {
  return {
    rootPath: s.rootPath, mode: s.mode, fileSystem: s.fileSystem, isVolumeRoot: s.isVolumeRoot, completedAtMs: s.completedAtMs,
    elapsedMs: s.elapsedMs, files: s.files, dirs: s.dirs, scannedAlloc: s.scannedAlloc, scannedSize: s.scannedSize,
    volumeTotal: s.volumeTotal, volumeFree: s.volumeFree, purposeBytes: s.purposeBytes ?? new Array<number>(PURPOSE_COUNT).fill(0),
    inaccessibleDirs: s.inaccessibleDirs, linksSkipped: s.linksSkipped, mountPointsSkipped: s.mountPointsSkipped, errors: s.errors,
    unaccountedBytes: s.unaccountedBytes, gapParts: s.gapParts, isAdmin: s.isAdmin, topEntries: null, apps: null, removedApps: null,
    previousAtMs: null, previousAlloc: null, appsComparedAtMs: null,
  };
}

/** 同 SearchIndex.Tier：名字整个就是（或去掉扩展名就是）10 > 开头 7 > 在词首 5 > 在中间 3 */
function nameTier(name: string, term: string): number {
  let at = name.indexOf(term);
  if (at < 0) return 0;
  if (name.length === term.length || (at === 0 && name.lastIndexOf('.') === term.length)) return 10;
  if (at === 0) return 7;
  for (; at > 0; at = name.indexOf(term, at + 1)) {
    const before = name[at - 1];
    if (!/[\p{L}\p{N}]/u.test(before) || (before > '\x7f') !== (term[0] > '\x7f')) return 5;
  }
  return 3;
}

/** 同 SearchIndex.Subsequence：字符按顺序出现，挨得越紧、越靠前分越高（最多 300）；跨度超过词长两倍不算 */
function subsequence(name: string, term: string): number {
  let span = Infinity, from = 0;
  for (let s = name.indexOf(term[0]); s >= 0; s = name.indexOf(term[0], s + 1)) {
    let i = s, k = 0;
    for (; i < name.length && k < term.length; i++) if (name[i] === term[k]) k++;
    if (k < term.length) break;
    if (i - s < span) { span = i - s; from = s; }
    if (span === term.length) break;
  }
  return span > term.length * 2 ? 0 : Math.max(1, 300 - (span - term.length) * 20 - from);
}

/** 同 SummaryStore.Compare：和同一范围的上一次对照——大项的旧占用、应用第一次出现的时刻、新出现和消失的应用 */
function compareSaved(e: T.SavedSummary, prev: T.SavedSummary | null): T.SavedSummary {
  const key = (a: T.SavedApp): string => `${a.kind}:${a.name}`.toLowerCase();
  // 只和同模式、同权限、也记了应用的上一次比
  const comparable = !!prev?.apps && !!e.apps && prev.mode === e.mode && prev.isAdmin === e.isAdmin;
  const seen = new Map((prev?.apps ?? []).map((a) => [key(a), a] as const));
  const apps = e.apps?.map((a) => {
    const p = seen.get(key(a));
    // 上一次就有的沿用「对照出来的新应用」这个结论；旧记录没有 absentAtMs 就拿那条记录的对照时刻补上
    return p ? { ...a, firstSeenMs: p.firstSeenMs, isNew: p.isNew, absentAtMs: p.isNew ? p.absentAtMs ?? prev!.appsComparedAtMs : null }
      : { ...a, firstSeenMs: e.completedAtMs, isNew: comparable, absentAtMs: comparable ? prev!.completedAtMs : null };
  }) ?? null;
  if (!prev) return { ...e, apps };
  const before = new Map((prev.topEntries ?? []).map((t) => [t.name.toLowerCase(), t.alloc] as const));
  const now = new Set((apps ?? []).map(key));
  return {
    ...e, apps,
    topEntries: e.topEntries?.map((t) => ({ ...t, previousAlloc: before.get(t.name.toLowerCase()) ?? null })) ?? null,
    removedApps: comparable ? prev.apps!.filter((p) => !now.has(key(p))).slice(0, 50).map((p) => ({ ...p, isNew: false, absentAtMs: null })) : null,
    previousAtMs: prev.completedAtMs, previousAlloc: prev.scannedAlloc,
    appsComparedAtMs: comparable ? prev.completedAtMs : null,
  };
}

/** 假的「上次扫描结果」：D 盘 3 天前、下载文件夹 9 天前各扫过一次。和模拟树无关，但自洽：用途字节之和 = 已统计 + 未解释 */
function fakeSaved(): T.SavedSummary[] {
  const make = (rootPath: string, drive: T.DriveInfo, used: number, daysAgo: number, elapsedMs: number, files: number, dirs: number,
    gb: [number, number][], gap: number, inaccessibleDirs: number): T.SavedSummary => {
    const purposeBytes = new Array<number>(PURPOSE_COUNT).fill(0);
    for (const [u, v] of gb) purposeBytes[u] = Math.round(v * GB);
    purposeBytes[PURPOSE.Unexplained] += gap;
    const scannedAlloc = purposeBytes.reduce((a, b) => a + b, 0) - gap;
    return {
      rootPath, mode: 'walk', fileSystem: drive.fileSystem, isVolumeRoot: rootPath === drive.root, completedAtMs: NOW - daysAgo * DAY,
      elapsedMs, files, dirs, scannedAlloc, scannedSize: Math.round(scannedAlloc * 0.98), volumeTotal: drive.totalBytes,
      volumeFree: drive.totalBytes - used, purposeBytes, inaccessibleDirs, linksSkipped: 0, mountPointsSkipped: 0, errors: 0,
      unaccountedBytes: gap, gapParts: splitGap('walk', drive.fileSystem, gap), isAdmin: false, topEntries: null, apps: null, removedApps: null,
      previousAtMs: null, previousAlloc: null, appsComparedAtMs: null,
    };
  };
  const [c, d] = DRIVES;
  const dGap = Math.round(1.6 * GB);
  const dRecord = make('D:\\', d, d.totalBytes - d.freeBytes, 3, 41_200, 412_806, 38_115,
    [[PURPOSE.Games, 395], [PURPOSE.Media, 312], [PURPOSE.Archives, 148], [PURPOSE.Data, 96], [PURPOSE.AppData, 41], [PURPOSE.Dev, 37.4], [PURPOSE.Unknown, 20]],
    dGap, 2);
  // D 盘 17 天前也扫过：这次对照出一个新装的游戏、一个不见了的游戏，几个大目录的增减
  const entry = (name: string, gb: number, files: number, prevGb: number | null): T.SavedEntry =>
    ({ name, isDir: true, alloc: Math.round(gb * GB), files, previousAlloc: prevGb === null ? null : Math.round(prevGb * GB) });
  // absentDaysAgo：对照出来的新应用，那次扫描还没有它；null 就不是新的
  const game = (name: string, gb: number, firstDaysAgo: number, absentDaysAgo: number | null = null): T.SavedApp => ({
    kind: 'game', name, path: `D:\\SteamLibrary\\steamapps\\common\\${name}`, alloc: Math.round(gb * GB), role: ROLE.Game,
    evidence: 'confirmed', installation: true, firstSeenMs: NOW - firstDaysAgo * DAY, isNew: absentDaysAgo !== null,
    absentAtMs: absentDaysAgo === null ? null : NOW - absentDaysAgo * DAY,
  });
  const previousAtMs = NOW - 17 * DAY;
  Object.assign(dRecord, {
    topEntries: [entry('Media', 515, 4_180, 498), entry('SteamLibrary', 395, 602, 288), entry('Backups', 210, 8, 210), entry('Projects', 37.4, 3_612, 35.1)],
    apps: [game('Baldurs Gate 3', 150, 3, 17), game('Starfield', 125, 60), game('Red Dead Redemption 2', 120, 60)],
    removedApps: [game('Cyberpunk 2077', 70, 120)],
    previousAtMs, previousAlloc: dRecord.scannedAlloc - Math.round(124 * GB), appsComparedAtMs: previousAtMs,
  } satisfies Partial<T.SavedSummary>);
  return [
    dRecord,
    make(`${HOME}\\Downloads`, c, 341 * GB, 9, 860, 1_284, 97, [[PURPOSE.Media, 21.7], [PURPOSE.Archives, 9.4], [PURPOSE.Apps, 6.2], [PURPOSE.Unknown, 0.9]], 0, 0),
  ];
}

function buildDataDrive(root: string, seed: number): MockTree {
  const g: Gen = { t: new MockTree(root), r: rng(seed) };
  const lib = path(g, 0, 'SteamLibrary', 'steamapps', 'common');
  for (const [n, s] of [['Baldurs Gate 3', 150], ['Starfield', 125], ['Red Dead Redemption 2', 120]] as const) files(g, dir(g, lib, n), 200, s * GB, ['pak', 'bin', 'dll'], 'data');
  const media = dir(g, 0, 'Media');
  files(g, dir(g, media, 'Movies'), 180, 420 * GB, ['mkv', 'mp4'], 'movie');
  files(g, dir(g, media, 'Photos'), 4000, 95 * GB, ['jpg', 'raw', 'png'], 'IMG');
  files(g, dir(g, 0, 'Backups'), 8, 210 * GB, ['vhdx', 'zip'], 'backup', 700);
  const proj = dir(g, 0, 'Projects');
  for (let i = 0; i < 12; i++) files(g, dir(g, proj, `project-${i}`), 300, (0.3 + g.r() * 4) * GB, ['ts', 'js', 'json', 'png', 'bin', 'cs', 'rs'], 'src');
  return g.t;
}

// ---------------- 用途分类（DESIGN §4.7 的简化版，规则号和优先级照抄 C# 的 PurposeRules） ----------------

const HOME = 'C:\\Users\\Alex';

/** 规则号即优先级，越小越优先：具体角色 → 所属容器 → 扩展名兜底 → 用户文件夹 */
const R = { Meta: 1, Recovery: 2, Logs: 3, Cache: 4, System: 5, Ai: 6, CacheName: 8, Sdk: 9, Games: 10, Apps: 11, AppData: 12, Dev: 13, Type: 14, User: 15 } as const;

/** 子目录各是什么（所属对象靠它认），同 C# 的 Kids；模拟数据用得上的几种 */
type Kids = 'apps' | 'appdata' | 'games' | 'steamapps';

interface Rule {
  u: number;
  prio: number;
  /** 规则本来就知道的数据角色 */
  role: number;
  /** 规则自带的所属对象（npm、Windows、Google Chrome……） */
  owner?: { kind: T.OwnerKind; name: string };
  kids?: Kids;
  /** 证据来自扩展名 */
  ext?: boolean;
}

const sys = (name = 'Windows'): Rule['owner'] => ({ kind: 'system', name });
const pm = (name: string): Rule => ({ u: PURPOSE.Cache, prio: R.Cache, role: ROLE.PackageCache, owner: { kind: 'packageManager', name } });
const sdk = (name: string): Rule => ({ u: PURPOSE.Sdk, prio: R.Sdk, role: ROLE.Runtime, owner: { kind: 'runtime', name } });
const browser = (name: string): Rule => ({ u: PURPOSE.AppData, prio: R.AppData, role: ROLE.BrowserProfile, owner: { kind: 'browser', name } });

/** 相对卷根、小写的路径 → 位置规则（自带角色、所属对象、子目录是什么） */
const PATHS: [RegExp, Rule][] = [
  [/^\$recycle\.bin$/, { u: PURPOSE.System, prio: R.Recovery, role: ROLE.Recovery, owner: sys(tr('回收站')) }],
  [/^(system volume information|recovery|\$winreagent|windows\.old|config\.msi|\$windows\.~bt|\$windows\.~ws|\$getcurrent|\$sysreset)$/,
    { u: PURPOSE.System, prio: R.Recovery, role: ROLE.Recovery, owner: sys() }],
  [/^programdata\\microsoft\\diagnosis$/, { u: PURPOSE.Logs, prio: R.Logs, role: ROLE.Log, owner: sys() }],
  [/^users\\[^\\]+\\appdata\\local\\npm-cache$/, pm('npm')],
  [/^users\\[^\\]+\\appdata\\local\\pip\\cache$/, pm('pip')],
  [/^users\\[^\\]+\\appdata\\local\\nuget\\v3-cache$/, pm('NuGet')],
  [/^users\\[^\\]+\\appdata\\local\\yarn\\cache$/, pm('Yarn')],
  [/^users\\[^\\]+\\appdata\\local\\pnpm\\store$/, pm('pnpm')],
  [/^(users\\[^\\]+\\appdata\\local|windows)\\temp$/, { u: PURPOSE.Cache, prio: R.Cache, role: ROLE.Temp }],
  [/^users\\[^\\]+\\appdata\\local\\microsoft\\windows\\explorer$/, { u: PURPOSE.Cache, prio: R.Cache, role: ROLE.AppCache, owner: sys() }],
  [/^windows\\softwaredistribution\\download$/, { u: PURPOSE.Cache, prio: R.Cache, role: ROLE.UpdateCache, owner: sys() }],
  [/^programdata\\package cache$/, { u: PURPOSE.Cache, prio: R.Cache, role: ROLE.UpdateCache }],
  [/^(windows|pagefile\.sys|hiberfil\.sys|swapfile\.sys|dumpstack\.log(\.tmp)?|bootmgr|bootnxt)$/, { u: PURPOSE.System, prio: R.System, role: ROLE.System, owner: sys() }],
  [/^users\\[^\\]+\\\.ollama\\models$/, { u: PURPOSE.Ai, prio: R.Ai, role: ROLE.Weights, owner: { kind: 'app', name: 'Ollama' } }],
  [/^users\\[^\\]+\\appdata\\local\\android\\sdk$/, sdk('Android SDK')],
  [/^users\\[^\\]+\\\.android$/, sdk('Android SDK')],
  [/^program files\\dotnet$/, sdk('.NET')],
  [/^(program files|program files \(x86\))$/, { u: PURPOSE.Apps, prio: R.Apps, role: ROLE.Program, kids: 'apps' }],
  [/^program files( \(x86\))?\\common files$/, { u: PURPOSE.Apps, prio: R.Apps, role: ROLE.Component }],
  [/^users\\[^\\]+\\appdata$/, { u: PURPOSE.AppData, prio: R.AppData, role: ROLE.UserData }],
  [/^(users\\[^\\]+\\appdata\\(local|locallow|roaming)|programdata)$/, { u: PURPOSE.AppData, prio: R.AppData, role: ROLE.UserData, kids: 'appdata' }],
  // 应用包的数据目录本身不是一个应用（C# 那边按包名认里面的子目录，模拟数据用不上）
  [/^users\\[^\\]+\\appdata\\local\\packages$/, { u: PURPOSE.AppData, prio: R.AppData, role: ROLE.UserData }],
  [/^users\\[^\\]+\\appdata\\local\\google\\chrome\\user data$/, browser('Google Chrome')],
  [/^users\\[^\\]+\\appdata\\local\\microsoft\\edge\\user data$/, browser('Microsoft Edge')],
  // 用户文件夹只说明在哪、不说明是什么：没有角色
  [/^users\\[^\\]+\\(desktop|documents|downloads|pictures|videos|music)$/, { u: PURPOSE.Media, prio: R.User, role: ROLE.Unknown }],
];

const LOG_DIRS: Record<string, number> = {
  logs: ROLE.Log, logfiles: ROLE.Log, perflogs: ROLE.Log, crashdumps: ROLE.CrashDump, crashpad: ROLE.CrashDump, minidump: ROLE.CrashDump,
  livekernelreports: ROLE.CrashDump, crashreports: ROLE.ErrorReport, 'crash reports': ROLE.ErrorReport, wer: ROLE.ErrorReport,
};
const CACHE_DIRS = new Set(['cache', 'caches', 'cache2', 'code cache', 'gpucache', 'shadercache', 'grshadercache', 'dawncache', 'dawngraphitecache',
  'dawnwebgpucache', 'cachestorage', 'scriptcache', 'inetcache', 'd3dscache', 'dxcache', 'glcache', '__pycache__']);
const GAME_DIRS = new Set(['steamlibrary', 'epic games', 'xboxgames', 'modifiablewindowsapps', 'gog games', 'riot games', 'ea games', 'origin games', 'rockstar games', 'amazon games']);
const LOG_EXT = new Set(['log', 'etl', 'evtx', 'evt', 'blg', 'dmp', 'mdmp', 'hdmp', 'wer']);
const DATA_EXT = new Set(['db', 'sqlite', 'sqlite3', 'db3', 'mdb', 'accdb', 'mdf', 'ndf', 'ldf', 'ibd', 'parquet', 'arrow', 'feather', 'npy', 'npz', 'h5', 'hdf5', 'tfrecord']);
const DATASET_EXT = new Set(['parquet', 'arrow', 'feather', 'npy', 'npz', 'h5', 'hdf5', 'tfrecord']);
const VIRTUAL_DISK_EXT = new Set(['vhd', 'vhdx', 'avhdx', 'vmdk', 'vdi', 'qcow2', 'ova', 'gho']);
const CONFIG_EXT = new Set(['ini', 'cfg', 'conf', 'config', 'cnf', 'prefs', 'properties']);
const MARKERS = ['package.json', 'cargo.toml', '.git', 'go.mod'];
const DEPS = new Set(['node_modules', 'bower_components', 'jspm_packages', 'vendor', '.venv', 'venv', 'pods']);
const BUILDS = new Set(['target', 'obj', 'bin', 'dist', 'build', 'out', '.next', '.nuxt', '.svelte-kit', '.turbo', '.parcel-cache', '.gradle', '.angular']);
const PLUGINS = new Set(['plugins', 'extensions', 'addons', 'add-ons', 'addins']);

const extOf = (name: string): string => (name.includes('.') ? name.slice(name.lastIndexOf('.') + 1).toLowerCase() : '');

/** 节点自己的证据：明确位置 → 目录名和标记 → 文件扩展名（文件自己就是位置规则的目标时不看扩展名） */
function ownPurpose(t: MockTree, i: number): Rule | null {
  const f = t.flags[i];
  if (hasFlag(f, Flag.Virtual) && !hasFlag(f, Flag.Directory)) return { u: PURPOSE.Unexplained, prio: R.Meta, role: ROLE.Unknown };
  if (hasFlag(f, Flag.Metafile)) return { u: PURPOSE.System, prio: R.Meta, role: ROLE.System };
  const rel = t.path(i).toLowerCase().replace(/^[a-z]:\\/, '');
  for (const [re, rule] of PATHS) if (re.test(rel)) return rule;

  const name = t.name[i].toLowerCase();
  if (hasFlag(f, Flag.Directory)) {
    if (name in LOG_DIRS) return { u: PURPOSE.Logs, prio: R.Logs, role: LOG_DIRS[name] };
    if (CACHE_DIRS.has(name)) return { u: PURPOSE.Cache, prio: R.CacheName, role: ROLE.AppCache };
    if (/^(stable-diffusion|comfyui)/.test(t.name[t.parent[i]]?.toLowerCase() ?? '') && name === 'models') return { u: PURPOSE.Ai, prio: R.Ai, role: ROLE.Weights };
    if (name === 'steamapps') return { u: PURPOSE.Games, prio: R.Games, role: ROLE.Game, kids: 'steamapps' };
    if (GAME_DIRS.has(name)) return { u: PURPOSE.Games, prio: R.Games, role: ROLE.Game, kids: name === 'steamlibrary' ? undefined : 'games' };
    if (name === 'windowsapps') return { u: PURPOSE.Apps, prio: R.Apps, role: ROLE.Program };
    if (i > 0 && t.live(i).some((c) => MARKERS.includes(t.name[c].toLowerCase()))) return { u: PURPOSE.Dev, prio: R.Dev, role: ROLE.Source };
    return null;
  }
  const ext = extOf(name);
  const k = t.k[i];
  if (LOG_EXT.has(ext)) return { u: PURPOSE.Logs, prio: R.Logs, role: /^[mh]?dmp$/.test(ext) ? ROLE.CrashDump : ext === 'wer' ? ROLE.ErrorReport : ROLE.Log, ext: true };
  if (['safetensors', 'gguf', 'onnx'].includes(ext) && t.size[i] >= 50 * MB) return { u: PURPOSE.Ai, prio: R.Ai, role: ROLE.Weights, ext: true };
  if (DATA_EXT.has(ext)) return { u: PURPOSE.Data, prio: R.Type, role: DATASET_EXT.has(ext) ? ROLE.Dataset : ROLE.Database, ext: true };
  const media: Record<number, number> = { [Cat.Video]: ROLE.Video, [Cat.Image]: ROLE.Image, [Cat.Audio]: ROLE.Audio, [Cat.Document]: ROLE.Document };
  if (k in media) return { u: PURPOSE.Media, prio: R.Type, role: media[k], ext: true };
  if (k === Cat.Archive) return { u: PURPOSE.Archives, prio: R.Type, role: ROLE.Archive, ext: true };
  if (k === Cat.DiskImage) return { u: PURPOSE.Archives, prio: R.Type, role: VIRTUAL_DISK_EXT.has(ext) ? ROLE.VirtualDisk : ROLE.InstallImage, ext: true };
  return null;
}

/** 容器里按目录名细分角色（同 C# 的 PurposeRules.Refine） */
function refine(role: number, name: string): number {
  const n = name.toLowerCase();
  if (role === ROLE.Source) return DEPS.has(n) ? ROLE.Dependency : BUILDS.has(n) || n.startsWith('cmake-build-') ? ROLE.BuildOutput : role;
  if (role === ROLE.Game) return n === 'mods' || n === 'workshop' ? ROLE.Mod : ['savegames', 'saves', 'savedata'].includes(n) ? ROLE.Save : PLUGINS.has(n) ? ROLE.Extension : role;
  if (role === ROLE.Program || role === ROLE.UserData || role === ROLE.BrowserProfile) return PLUGINS.has(n) ? ROLE.Extension : role;
  return role;
}

/** 容器的默认角色遇到文件时按扩展名细分（同 C# 的 PurposeRules.FileRole） */
function fileRole(role: number, k: number, ext: string): number {
  if (role === ROLE.Game) return k === Cat.Executable ? ROLE.Game : CONFIG_EXT.has(ext) ? ROLE.Config : ROLE.GameAsset;
  if (role === ROLE.Program || role === ROLE.UserData) return CONFIG_EXT.has(ext) ? ROLE.Config : role;
  return role;
}

function classifyPurposes(t: MockTree): void {
  const n = t.parent.length;
  const rule: (Rule | null)[] = new Array(n).fill(null);
  const kids: (Kids | undefined)[] = new Array(n).fill(undefined);
  const own = new Array<boolean>(n).fill(false);
  const u = new Array<number>(n).fill(-1);
  const role = new Array<number>(n).fill(ROLE.Unknown);
  const evidence = new Array<T.Evidence>(n).fill('unknown');
  const index = new Map<string, number>();
  t.owners = [];
  t.ownerRoots = new Map();
  /** 同种同名合并；对象本身取最强的证据，每个根记自己这一处的证据 */
  const addOwner = (i: number, kind: T.OwnerKind, name: string, ev: T.Evidence): void => {
    const key = `${kind}:${name.toLowerCase()}`;
    let k = index.get(key);
    if (k === undefined) {
      k = t.owners.length;
      t.owners.push({ kind, name, evidence: ev });
      index.set(key, k);
    } else if (ev === 'confirmed') t.owners[k].evidence = ev;
    t.ownerRoots.set(i, { owner: k, evidence: ev });
  };

  for (let i = 0; i < n; i++) {
    const p = t.parent[i];
    const isDir = hasFlag(t.flags[i], Flag.Directory);
    const inherited = i > 0 ? rule[p] : null;
    const mine = ownPurpose(t, i);
    // 同号时更深的规则更具体，也算赢（AppData 里的浏览器资料）；项目标记例外，依赖里的 package.json 不翻回源代码
    const win = !!mine && (!inherited || mine.prio < inherited.prio || (mine.prio === inherited.prio && mine.prio !== R.Dev));
    const eff = win ? mine : inherited;
    own[i] = !!mine && (!inherited || mine.prio <= inherited.prio);
    rule[i] = eff;
    if (eff) u[i] = eff.u;
    else if (!isDir) u[i] = PURPOSE.Unknown;

    let r = win ? mine!.role : i > 0 ? role[p] : ROLE.Unknown;
    if (isDir && !win) r = refine(r, t.name[i]);
    else if (!isDir && !(win && mine!.ext)) r = fileRole(r, t.k[i], extOf(t.name[i]));
    role[i] = r;
    evidence[i] = u[i] < 0 || u[i] === PURPOSE.Unknown || u[i] === PURPOSE.Unexplained ? 'unknown'
      : win && mine!.ext && mine!.prio < (inherited?.prio ?? 99) ? 'inferred'
        : eff?.prio === R.User && !(win && mine!.prio === R.User) ? 'inferred'
          : 'confirmed';

    // 所属对象：规则自带 → 项目标记 → 父目录说「子目录各是一个对象」
    if (win && mine!.owner) addOwner(i, mine!.owner.kind, mine!.owner.name, 'confirmed');
    else if (isDir && mine?.prio === R.Dev && eff?.prio === R.Dev && r === ROLE.Source) addOwner(i, 'project', t.name[i], 'confirmed');
    else if (isDir && !win && i > 0) {
      const up = kids[p];
      const name = t.name[i];
      if (up === 'apps' && eff?.prio === R.Apps) addOwner(i, 'app', name, 'inferred');
      else if (up === 'appdata' && eff?.prio === R.AppData) addOwner(i, 'app', name, 'inferred');
      else if (up === 'games' && !/^(launcher|riot client|steamworks shared)$|redist/i.test(name)) addOwner(i, 'game', name, 'confirmed');
      else if (up === 'steamapps' && name.toLowerCase() === 'common') kids[i] = 'games';
    }
    if (win && mine!.kids) kids[i] = mine!.kids;
  }
  // 没命中任何规则的目录：子树里某个用途占 60% 以上就用它，否则未识别；
  // 用途只是继承来的目录：另一种用途占 60% 以上时按它展示，没人过线就沿用继承的。按子树构成定的展示用途是推测
  const sums = new Map<number, number[]>();
  for (let i = n - 1; i >= 0; i--) {
    const isDir = hasFlag(t.flags[i], Flag.Directory);
    if (isDir && (u[i] < 0 || !own[i])) {
      const s = sums.get(i) ?? new Array<number>(PURPOSE_COUNT).fill(0);
      const total = s.reduce((a, b) => a + b, 0);
      const best = s.indexOf(Math.max(...s));
      const main = total > 0 && s[best] / total >= 0.6 ? best : PURPOSE.Unknown;
      if (u[i] < 0 || (main !== PURPOSE.Unknown && main !== u[i])) {
        u[i] = main;
        evidence[i] = main === PURPOSE.Unknown ? 'unknown' : 'inferred';
      }
    }
    const p = t.parent[i];
    if (p < 0 || hasFlag(t.flags[i], Flag.HardLinkDup) || hasFlag(t.flags[i], Flag.Deleted)) continue;
    const ps = sums.get(p) ?? new Array<number>(PURPOSE_COUNT).fill(0);
    if (isDir) {
      const s = sums.get(i);
      if (s) for (let k = 0; k < PURPOSE_COUNT; k++) ps[k] += s[k];
    } else ps[u[i]] += t.alloc[i];
    sums.set(p, ps);
  }
  t.u = u;
  t.role = role;
  t.evidence = evidence;
}

/** 最近的所属对象：从自己往上找第一个根 */
function ownerOf(t: MockTree, i: number): T.Owner | null {
  for (let x = i; x >= 0; x = t.parent[x]) {
    const r = t.ownerRoots.get(x);
    if (!r) continue;
    const o = t.owners[r.owner];
    return { kind: o.kind, name: o.name, rootId: x, rootPath: t.path(x), evidence: r.evidence };
  }
  return null;
}

function displayPath(path: string): string {
  return path.toLowerCase().startsWith(HOME.toLowerCase()) ? `~${path.slice(HOME.length)}` : path;
}

/** cleanup.script 的模拟版：格式照 DESIGN §6.2 */
function mockScript(items: { s: T.Suggestion; paths: string[]; bytes: number }[]): string {
  const head = [
    tr('# DiskLens 清理脚本 · {date} · {n} 项 · 约 {gb} GB', { date: new Date().toLocaleString(locale()), n: items.length, gb: (items.reduce((a, b) => a + b.bytes, 0) / GB).toFixed(1) }),
    tr('# 删除不进回收站，无法恢复。运行前请关闭浏览器、IDE 和相关程序。'),
    tr('# 用法：在 PowerShell 中粘贴运行；标了「需要管理员」的段落请在管理员 PowerShell 中运行。'),
    "$ErrorActionPreference = 'Continue'",
    tr('# （演示模式生成，路径来自模拟数据）'),
  ];
  const lit = (p: string) => (p.toLowerCase().startsWith(HOME.toLowerCase()) ? `(Join-Path $env:USERPROFILE '${p.slice(HOME.length + 1).replace(/'/g, "''")}')` : `'${p.replace(/'/g, "''")}'`);
  const body = items.flatMap(({ s, paths, bytes }) => {
    const title = `# ── ${s.title}${s.requiresAdmin ? tr('（需要管理员）') : ''} · ${(bytes / GB).toFixed(1)} GB · ${s.displayPath ?? ''}`;
    const lines =
      s.action === 'command' ? [s.command ?? '']
        : s.action === 'deleteContents' ? paths.map((p) => `Clear-DiskLensFolder ${lit(p)}`)
          : s.action === 'deleteNodes' ? paths.map((p) => `Remove-DiskLensItem ${lit(p)}`)
            : s.action === 'emptyRecycleBin' ? ['Clear-RecycleBin -DriveLetter C -Force']
              : s.action === 'openSettings' ? [`Start-Process '${s.target ?? ''}'`] : [`# ${s.description}`];
    return ['', title, ...lines];
  });
  return [...head, ...body, ''].join('\r\n');
}

// ---------------- 后端 ----------------

// 卷标是用户自己起的名字；模拟数据里按界面语言显示
const DRIVES: (Omit<T.DriveInfo, 'label'> & { label: MsgId | 'NAS' })[] = [
  { root: 'C:\\', label: '系统', fileSystem: 'NTFS', driveType: 'fixed', totalBytes: 476.9 * GB, freeBytes: 0, isReady: true, isSsd: true, canMft: false, isSystem: true, supportsMft: false },
  { root: 'D:\\', label: '数据', fileSystem: 'NTFS', driveType: 'fixed', totalBytes: 1863 * GB, freeBytes: 812 * GB, isReady: true, isSsd: false, canMft: false, isSystem: false, supportsMft: false },
  { root: 'E:\\', label: 'U 盘', fileSystem: 'exFAT', driveType: 'removable', totalBytes: 119 * GB, freeBytes: 77 * GB, isReady: true, isSsd: null, canMft: false, isSystem: false, supportsMft: false },
  { root: 'Z:\\', label: 'NAS', fileSystem: 'NTFS', driveType: 'network', totalBytes: 7450 * GB, freeBytes: 2100 * GB, isReady: true, isSsd: null, canMft: false, isSystem: false, supportsMft: false },
];

const PROTECTED = ['windows', 'windows\\system32', 'windows\\syswow64', 'windows\\winsxs', 'windows\\installer', 'program files', 'program files (x86)', 'programdata', 'users', 'users\\alex', 'system volume information', '$recycle.bin', 'pagefile.sys', 'hiberfil.sys', 'swapfile.sys'];

export function createMockBackend(emit: Emit): MockHandler {
  const query = new URLSearchParams(globalThis.location?.search ?? '');
  const admin = query.get('admin') === '1';
  let tree: MockTree | null = null;
  let summary: T.ScanSummary | null = null;
  let scanId = 0;
  let timer: ReturnType<typeof setInterval> | undefined;
  let running = -1;
  /** 当前结果那次扫描的异常清单，和 summary 一起换 */
  let issues: Issues | null = null;
  /** 正在跑的那次扫描：取消是否已被接受、最近一条进度（报「取消中」时照抄它的阶段和数字） */
  let cancelling = false;
  let last: T.ScanProgress | null = null;
  /** 已保存的「上次扫描结果」，新的在前（同 SummaryStore；32 个范围的上限模拟里碰不到，不做） */
  let saved = fakeSaved();

  /** 同 SummaryStore.Save：只有扫完的才存；范围按根路径认、不区分大小写，已有更晚完成的就不动 */
  function remember(s: T.ScanSummary, t: MockTree): void {
    const old = saved.find((x) => x.rootPath.toLowerCase() === s.rootPath.toLowerCase());
    if (old && old.completedAtMs > s.completedAtMs) return;
    saved = [compareSaved({ ...toSaved(s), ...capture(t) }, old ?? null), ...saved.filter((x) => x !== old)].sort((a, b) => b.completedAtMs - a.completedAtMs);
  }

  /** 同 SummaryStore.Capture（简化）：根下面的大项、认出的应用（同一对象嵌套的根只算外层，优先显示已确认的安装位置） */
  function capture(t: MockTree): Pick<T.SavedSummary, 'topEntries' | 'apps'> {
    const topEntries = t.live(0)
      .map((n): T.SavedEntry => ({ name: t.name[n], isDir: hasFlag(t.flags[n], Flag.Directory), alloc: t.counted(n, 'alloc'), files: t.files[n], previousAlloc: null }))
      .sort((a, b) => b.alloc - a.alloc).slice(0, 12);
    const byOwner = new Map<number, number[]>();
    for (const [n, r] of t.ownerRoots) if (!hasFlag(t.flags[n], Flag.Deleted)) byOwner.set(r.owner, [...(byOwner.get(r.owner) ?? []), n]);
    const apps: T.SavedApp[] = [];
    for (const [k, nodes] of byOwner) {
      const o = t.owners[k];
      if (o.kind !== 'app' && o.kind !== 'game' && o.kind !== 'browser') continue;
      const roots = nodes.filter((n) => { for (let x = t.parent[n]; x >= 0; x = t.parent[x]) if (t.ownerRoots.get(x)?.owner === k) return false; return true; });
      const installed = roots.filter((n) => t.ownerRoots.get(n)!.evidence === 'confirmed' && t.evidence[n] === 'confirmed'
        && (t.role[n] === ROLE.Program || t.role[n] === ROLE.Game));
      const shown = (installed.length ? installed : roots).reduce((a, b) => (t.counted(b, 'alloc') > t.counted(a, 'alloc') ? b : a));
      apps.push({ kind: o.kind, name: o.name, path: t.path(shown), alloc: roots.reduce((sum, n) => sum + t.counted(n, 'alloc'), 0),
        role: t.role[shown] ?? ROLE.Unknown, evidence: t.ownerRoots.get(shown)!.evidence, installation: installed.length > 0, firstSeenMs: 0, isNew: false, absentAtMs: null });
    }
    return { topEntries, apps: apps.sort((a, b) => b.alloc - a.alloc || a.name.localeCompare(b.name)).slice(0, 400) };
  }

  // 确认框令牌，口径同后端 ConfirmationStore：一次性、最多 32 个、换扫描清空，执行时清单必须和确认时一致
  interface Confirmation { suggestionId: string | null; nodeIds: number[] | null; permanent: boolean | null }
  const confirmations = new Map<string, Confirmation>();
  let tokenSeq = 0;
  function issue(c: Confirmation): string {
    const token = `mock-${++tokenSeq}`;
    confirmations.set(token, c);
    while (confirmations.size > 32) confirmations.delete(confirmations.keys().next().value!);
    return token;
  }
  function take(token: string | null | undefined, c: Confirmation): void {
    const e = token ? confirmations.get(token) : undefined;
    if (!e) throw new Error(tr('确认已过期或已经用过，请重新打开确认框'));
    confirmations.delete(token!);
    const sameIds = e.nodeIds === null ? c.nodeIds === null : c.nodeIds !== null && e.nodeIds.length === new Set(c.nodeIds).size && c.nodeIds.every((id) => e.nodeIds!.includes(id));
    if (e.suggestionId !== c.suggestionId || e.permanent !== c.permanent || !sameIds) throw new Error(tr('确认框里核对过的对象和这次请求对不上，请重新确认'));
  }

  const drives = (): T.DriveInfo[] =>
    DRIVES.map((d) => ({
      ...d,
      label: d.label === 'NAS' ? d.label : tr(d.label),
      canMft: admin && d.fileSystem === 'NTFS' && d.driveType !== 'network',
      supportsMft: d.fileSystem === 'NTFS' && d.driveType !== 'network',
      freeBytes: d.root === 'C:\\' ? d.totalBytes - (summaryFor('C:\\')?.volumeUsed ?? 341 * GB) : d.freeBytes,
    }));

  function summaryFor(root: string): T.ScanSummary | null {
    return summary && summary.rootPath === root ? summary : null;
  }

  function isVolumeRoot(p: string): boolean {
    return /^[A-Za-z]:\\?$/.test(p);
  }

  /** 扫描报告的四类计数取扫描时的清单（删除后不重算，同后端）；未解释容量按当前差额拆 */
  function buildSummary(t: MockTree, id: number, mode: T.ScanMode, elapsed: number, unaccounted: number, found: Issues): T.ScanSummary {
    const root = t.rootPath;
    const drive = DRIVES.find((d) => root.toUpperCase().startsWith(d.root)) ?? DRIVES[0];
    const vr = isVolumeRoot(root);
    const used = vr ? t.alloc[0] : drive.totalBytes - drive.freeBytes;
    const cats = new Array<number>(CATEGORY_COUNT).fill(0);
    const purposes = new Array<number>(PURPOSE_COUNT).fill(0);
    const inacc = found.denied.length;
    let dups = 0;
    let saved = 0;
    for (let i = 1; i < t.parent.length; i++) {
      const f = t.flags[i];
      if (hasFlag(f, Flag.Deleted)) continue;
      if (hasFlag(f, Flag.HardLinkDup)) {
        dups++;
        saved += t.alloc[i];
        continue;
      }
      if (!hasFlag(f, Flag.Directory)) {
        cats[t.k[i]] += t.alloc[i];
        purposes[t.u[i] ?? 0] += t.alloc[i];
      }
    }
    return {
      scanId: id, rootPath: root, rootId: 0, mode, fileSystem: drive.fileSystem, isVolumeRoot: vr, isAdmin: admin,
      volumeTotal: drive.totalBytes, volumeFree: drive.totalBytes - used, volumeUsed: used,
      scannedAlloc: t.alloc[0] - unaccounted, scannedSize: t.size[0] - unaccounted, files: t.files[0], dirs: t.dirs[0],
      inaccessibleDirs: inacc, linksSkipped: found.link.length, hardLinkDups: dups, hardLinkSavedBytes: saved,
      unaccountedBytes: unaccounted, elapsedMs: elapsed, categoryBytes: cats,
      warnings: inacc && !admin ? [tr('{n} 个目录无权访问，已计入「未统计」。以管理员身份运行可以看到它们。', { n: inacc })] : [],
      treeVersion: t.version,
      purposeBytes: purposes,
      mountPointsSkipped: found.mountPoint.length,
      errors: found.error.length,
      gapParts: splitGap(mode, drive.fileSystem, unaccounted),
      completedAtMs: NOW, // 同 tree.recent：假数据的时间都按 NOW 生成，NOW 就当完成时刻
    };
  }

  function startScan(p: { path: string; mode: T.ScanMode }): T.ScanStarted {
    const root = p.path;
    const vr = isVolumeRoot(root);
    const drive = DRIVES.find((d) => root.toUpperCase().startsWith(d.root));
    const mftOk = admin && vr && drive?.fileSystem === 'NTFS' && drive.driveType !== 'network';
    if (p.mode === 'mft' && !mftOk) throw new Error(admin ? tr('MFT 扫描只支持本地 NTFS 卷的根目录') : tr('MFT 扫描需要管理员权限'));
    const mode: T.ScanMode = p.mode === 'walk' ? 'walk' : mftOk ? 'mft' : 'walk';
    const id = ++scanId;
    confirmations.clear();
    if (timer) clearInterval(timer);
    running = id;
    cancelling = false;

    const t = root.toUpperCase().startsWith('C:') && vr ? buildSystemDrive('C:\\', admin, mode === 'mft') : buildDataDrive(root, root.length * 977);
    const unaccounted = vr && root.toUpperCase().startsWith('C:') ? (mode === 'mft' ? 0 : Math.round((admin ? 2.1 : 11.4) * GB)) : 0;
    if (unaccounted > 0) t.add(0, tr('未统计（系统保留或无权访问）'), Flag.Virtual, unaccounted, unaccounted, 0);
    t.seal();
    classifyPurposes(t);
    const found = scanIssues(t, mode);
    const final = buildSummary(t, id, mode, mode === 'mft' ? 2840 : 6120, unaccounted, found);
    const used = vr ? final.volumeUsed : 0; // 约定：扫文件夹时进度里的 usedBytes 是 0

    const plan: [T.Phase, number][] = mode === 'mft'
      ? [['prepare', 300], ['mft', 1600], ['tree', 450], ['links', 350], ['finalize', 300]]
      : [['prepare', 300], ['walk', 2200], ['links', 350], ['finalize', 300]];
    const totalMs = plan.reduce((a, [, ms]) => a + ms, 0);
    const scanEnd = plan.slice(0, 2).reduce((a, [, ms]) => a + ms, 0);
    let elapsed = 0;
    const progress = (): T.ScanProgress => {
      let acc = 0;
      let phase: T.Phase = 'finalize';
      for (const [ph, ms] of plan) {
        if (elapsed <= acc + ms) {
          phase = ph;
          break;
        }
        acc += ms;
      }
      const frac = Math.min(1, Math.max(0, (elapsed - plan[0][1]) / (scanEnd - plan[0][1])));
      const ease = 1 - Math.pow(1 - frac, 1.6);
      return {
        scanId: id, mode, phase,
        files: Math.round(final.files * ease), dirs: Math.round(final.dirs * ease), bytes: Math.round(final.scannedAlloc * ease),
        usedBytes: used, percent: phase === 'prepare' ? null : Math.min(ease, mode === 'walk' ? 0.99 : 1),
        currentPath: phase === 'mft' || phase === 'walk' ? t.path(Math.min(t.parent.length - 1, Math.floor(ease * (t.parent.length - 1)))) : null,
        elapsedMs: elapsed,
        categoryBytes: final.categoryBytes.map((b, k) => Math.round(b * Math.min(1, ease * (1 + ((k * 37) % 11) / 20)))),
        cancelling: false,
      };
    };
    // 同后端：一开始就报一条；取消被接受后下一拍（后台真正停下）才发终态，之后不再有进度
    emit('scan.progress', (last = progress()));
    timer = setInterval(() => {
      if (running !== id) return;
      if (cancelling) {
        clearInterval(timer);
        running = -1;
        emit('scan.error', { scanId: id, message: tr('扫描已取消'), cancelled: true } satisfies T.ScanError);
        return;
      }
      elapsed += 100;
      emit('scan.progress', (last = progress()));
      if (elapsed >= totalMs) {
        clearInterval(timer);
        running = -1;
        tree = t;
        issues = found;
        summary = { ...final, elapsedMs: elapsed };
        remember(summary, t); // 同后端：先存摘要再发完成事件
        emit('scan.done', summary);
        // 同后端：完成后开始跟踪，先发一条初始状态（模拟后端不会有别的程序删东西）
        emit('tree.changed', { scanId: id, watching: true, removedEntries: 0, otherChanges: 0, overflow: false, treeVersion: t.version } satisfies T.TreeChange);
      }
    }, 100);
    return { scanId: id, mode };
  }

  function need(): MockTree {
    if (!tree) throw new Error(tr('还没有扫描结果'));
    return tree;
  }

  function node(t: MockTree, id: number): number {
    if (id < 0 || id >= t.parent.length || hasFlag(t.flags[id], Flag.Deleted)) throw new Error(tr('节点不存在或已删除'));
    return id;
  }

  function fileItem(t: MockTree, i: number): T.FileItem {
    return { id: i, name: t.name[i], path: t.path(i), isDir: hasFlag(t.flags[i], Flag.Directory), size: t.size[i], alloc: t.alloc[i], modifiedMs: t.mtime[i] || null, k: t.k[i], f: t.flags[i], u: t.u[i] ?? PURPOSE.Unknown };
  }

  // 和 TreeQueries.BuildView 同口径：预算夹在 100..50000；值最大的目录先展开（同值先进先出）；
  // 留够「其余 N 项」的位置才收子节点，一个都放不下就不展开（视图根例外）；聚合节点按值排进兄弟里
  function view(p: { nodeId: number; metric: T.Metric; maxNodes?: number }): T.ViewData {
    const t = need();
    const root = node(t, p.nodeId);
    const m = p.metric;
    const budget = Math.min(50_000, Math.max(100, p.maxNodes ?? 12000));
    const total = t.val(root, m);
    const nodes: T.ViewNode[] = [{ id: root, p: -1, n: t.name[root], v: total, f: t.flags[root], k: t.k[root], c: t.live(root).length, u: t.u[root] }];
    const threshold = total / (budget * 4);
    const heap = [0]; // 待展开的视图下标
    const expanded = new Set<number>();
    while (nodes.length < budget && heap.length) {
      let at = 0;
      for (let j = 1; j < heap.length; j++) {
        const a = nodes[heap[j]].v;
        const b = nodes[heap[at]].v;
        if (a > b || (a === b && heap[j] < heap[at])) at = j;
      }
      const vi = heap.splice(at, 1)[0];
      const slots = budget - nodes.length;
      const dv = nodes[vi].v;
      const kids = t.live(nodes[vi].id).map((c) => ({ c, v: t.counted(c, m) })).filter((x) => x.v > 0).sort((a, b) => b.v - a.v || a.c - b.c);
      let kept = 0;
      let keptSum = 0;
      for (const x of kids) {
        if (kept >= 1500 || x.v < threshold) break;
        if (kept + 1 + (dv - keptSum - x.v > 0 ? 1 : 0) > slots) break; // 没留够聚合节点的位置就不收
        kept++;
        keptSum += x.v;
      }
      const rest = dv - keptSum;
      if (kept === 0 && (vi !== 0 || rest <= 0)) {
        if (slots <= 1) break;
        continue;
      }
      expanded.add(vi);
      const merged = kids.length - kept;
      const aggregate = (): T.ViewNode => ({ id: -1, p: vi, n: merged ? tr('其余 {n} 项', { n: merged }) : tr('目录自身'), v: rest, f: Flag.Aggregate, k: Cat.Other, c: merged, u: nodes[vi].u });
      let pending = rest > 0;
      for (const x of kids.slice(0, kept)) {
        if (pending && rest > x.v) {
          nodes.push(aggregate());
          pending = false;
        }
        nodes.push({ id: x.c, p: vi, n: t.name[x.c], v: x.v, f: t.flags[x.c], k: t.k[x.c], c: t.live(x.c).length, u: t.u[x.c] });
        if (hasFlag(t.flags[x.c], Flag.Directory) && t.live(x.c).length) heap.push(nodes.length - 1);
      }
      if (pending) nodes.push(aggregate());
    }
    for (let vi = 0; vi < nodes.length; vi++) {
      const n = nodes[vi];
      if (n.id >= 0 && hasFlag(n.f, Flag.Directory) && n.c > 0 && !expanded.has(vi)) n.f |= Flag.Unexpanded;
    }
    return { rootId: root, metric: m, total, treeVersion: t.version, nodes };
  }

  function details(p: { nodeId: number; metric: T.Metric }): T.NodeDetails {
    const t = need();
    const id = node(t, p.nodeId);
    const m = p.metric;
    const parent = t.parent[id];
    const ext = new Map<string, T.ExtStat>();
    const cats = new Array<number>(CATEGORY_COUNT).fill(0);
    const purposes = new Array<number>(PURPOSE_COUNT).fill(0);
    const stack = [id];
    while (stack.length) {
      const n = stack.pop()!;
      if (hasFlag(t.flags[n], Flag.Directory)) {
        stack.push(...t.live(n));
        continue;
      }
      // 和后端 Compose 同口径：硬链接副本只算条目数不算字节（查的就是它自己时照算），虚拟叶子不进扩展名统计
      const counted = n === id || !hasFlag(t.flags[n], Flag.HardLinkDup);
      if (counted) {
        cats[t.k[n]] += t.val(n, m);
        purposes[t.u[n] ?? 0] += t.val(n, m);
      }
      if (hasFlag(t.flags[n], Flag.Virtual)) continue;
      const e = t.name[n].includes('.') ? t.name[n].slice(t.name[n].lastIndexOf('.') + 1).toLowerCase() : '';
      const s = ext.get(e) ?? { ext: e, k: t.k[n], size: 0, alloc: 0, count: 0 };
      s.count++;
      if (counted) {
        s.size += t.size[n];
        s.alloc += t.alloc[n];
      }
      ext.set(e, s);
    }
    const byValue = [...ext.values()].sort((a, b) => (m === 'alloc' ? b.alloc - a.alloc : b.size - a.size) || (a.ext < b.ext ? -1 : a.ext > b.ext ? 1 : 0));
    const extStats = byValue.slice(0, 15);
    // 排在 15 名之外的合成一条「*」，面板上总数才对得上
    if (byValue.length > 15)
      extStats.push(byValue.slice(15).reduce((s, x) => ({ ...s, size: s.size + x.size, alloc: s.alloc + x.alloc, count: s.count + x.count }), { ext: '*', k: Cat.Other, size: 0, alloc: 0, count: 0 }));
    const f = t.flags[id];
    const labels: [number, MsgId][] = [[Flag.Hidden, '隐藏'], [Flag.System, '系统'], [Flag.Compressed, '压缩'], [Flag.Sparse, '稀疏'], [Flag.Reparse, '重解析点'], [Flag.HardLinkDup, '硬链接'], [Flag.Cloud, '仅云端'], [Flag.Encrypted, '加密'], [Flag.Inaccessible, '无法访问'], [Flag.Metafile, 'NTFS 元数据']];
    const lowerPath = t.path(id).toLowerCase().replace(/^[a-z]:\\/, '');
    const isProt = id === 0 || hasFlag(f, Flag.Virtual) || hasFlag(f, Flag.Metafile) || PROTECTED.includes(lowerPath) || lowerPath.startsWith('windows\\system32\\') || lowerPath.startsWith('windows\\winsxs\\');
    const group = t.groups.get(id);
    return {
      id, name: t.name[id], path: t.path(id), isDir: hasFlag(f, Flag.Directory), k: t.k[id], f,
      attributes: labels.filter(([b]) => hasFlag(f, b)).map(([, s]) => tr(s)).concat(group && !hasFlag(f, Flag.HardLinkDup) ? [tr('硬链接')] : []),
      size: t.size[id], alloc: t.alloc[id], files: t.files[id], dirs: t.dirs[id],
      modifiedMs: t.mtime[id] || null, createdMs: t.mtime[id] ? t.mtime[id] - 30 * DAY : null, accessedMs: t.mtime[id] ? NOW - DAY : null,
      percentOfParent: parent >= 0 && t.val(parent, m) ? t.counted(id, m) / t.val(parent, m) : 1,
      percentOfRoot: t.val(0, m) ? t.counted(id, m) / t.val(0, m) : 1,
      ancestors: t.ancestors(id),
      topChildren: t.live(id).slice(0, 12).map((c) => ({ id: c, name: t.name[c], isDir: hasFlag(t.flags[c], Flag.Directory), size: t.size[c], alloc: t.alloc[c], k: t.k[c], f: t.flags[c], files: t.files[c], u: t.u[c] ?? PURPOSE.Unknown })),
      childCount: t.live(id).length,
      extStats,
      categoryBytes: cats,
      isProtected: isProt, protectReason: isProt ? tr('系统关键位置，DiskLens 不会删除它') : null,
      warning: !isProt && /^(windows|program files|programdata)\\/.test(lowerPath) ? tr('这是系统或程序目录里的内容，删除前请确认用途') : null,
      linkTarget: hasFlag(f, Flag.Reparse) ? 'D:\\Somewhere\\Else' : null,
      hardLinkCount: group?.length ?? 1,
      hardLinkCountedAt: group ? t.path(group.find((x) => !hasFlag(t.flags[x], Flag.HardLinkDup) && !hasFlag(t.flags[x], Flag.Deleted)) ?? id) : null,
      treeVersion: t.version,
      u: t.u[id] ?? PURPOSE.Unknown,
      purposeBytes: purposes,
      role: t.role[id] ?? ROLE.Unknown,
      evidence: t.evidence[id] ?? 'unknown',
      owner: ownerOf(t, id),
    };
  }

  function largest(p: { nodeId: number; metric: T.Metric; limit?: number }): T.FileItem[] {
    const t = need();
    const out: number[] = [];
    const stack = [node(t, p.nodeId)];
    while (stack.length) {
      const n = stack.pop()!;
      if (hasFlag(t.flags[n], Flag.Directory)) stack.push(...t.live(n));
      else if (!hasFlag(t.flags[n], Flag.Virtual)) out.push(n);
    }
    return out.sort((a, b) => t.val(b, p.metric) - t.val(a, p.metric)).slice(0, p.limit ?? 100).map((i) => fileItem(t, i));
  }

  // 同 TreeQueries.Children：全部直接子项按三种口径排好再分页。名字比较同 C# 的 OrdinalIgnoreCase（转大写后按 UTF-16 码点比）
  function children(p: { nodeId: number; metric?: T.Metric; sort?: T.ChildSort; offset?: number; limit?: number }): T.Children {
    const t = need();
    const id = node(t, p.nodeId);
    const m = p.metric ?? 'alloc', sort = p.sort ?? 'size';
    if (!CHILD_SORTS.includes(sort)) throw new Error(tr('请求参数无效'));
    const dir = (i: number) => (hasFlag(t.flags[i], Flag.Directory) ? 1 : 0);
    const upper = (i: number) => t.name[i].toUpperCase();
    const byName = (a: number, b: number) => (upper(a) < upper(b) ? -1 : upper(a) > upper(b) ? 1 : 0);
    const kids = [...t.live(id)].sort(
      sort === 'name' ? (a, b) => dir(b) - dir(a) || byName(a, b) || a - b
        : sort === 'modified' ? (a, b) => t.mtime[b] - t.mtime[a] || a - b
          : (a, b) => t.counted(b, m) - t.counted(a, m) || a - b);
    const offset = clamp(p.offset ?? 0, 0, kids.length), limit = clamp(p.limit ?? 200, 1, 500);
    return {
      nodeId: id, metric: m, sort, total: kids.length, offset,
      items: kids.slice(offset, offset + limit).map((c) => ({
        id: c, name: t.name[c], isDir: dir(c) === 1, value: t.counted(c, m), size: t.size[c], alloc: t.alloc[c],
        modifiedMs: t.mtime[c] || null, k: t.k[c], f: t.flags[c], u: t.u[c] ?? PURPOSE.Unknown, childCount: dir(c) ? t.live(c).length : 0,
      })),
    };
  }

  // 同 TreeQueries.RecentFiles：窗口是闭区间 [基准 − days 天, 基准]。基准是扫描完成时刻——假数据的时间都按 NOW 生成，NOW 就当完成时刻。
  // 只出真文件：目录、虚拟节点、硬链接副本、修改时间未知的不算；作用域是文件时含它自己
  function recent(p: { days?: number; scopeId?: number | null; sort?: T.RecentSort; metric?: T.Metric; limit?: number }): T.RecentFiles {
    const t = need();
    if (p.sort != null && !RECENT_SORTS.includes(p.sort)) throw new Error(tr('请求参数无效'));
    const days = clamp(p.days ?? 7, 1, 3650);
    const m = p.metric ?? 'alloc';
    const from = NOW - days * DAY;
    const hits: number[] = [];
    const stack = [p.scopeId == null ? 0 : node(t, p.scopeId)];
    while (stack.length) {
      const n = stack.pop()!;
      const f = t.flags[n];
      if (hasFlag(f, Flag.Directory)) stack.push(...t.live(n));
      else if (!hasFlag(f, Flag.Virtual) && !hasFlag(f, Flag.HardLinkDup) && t.mtime[n] > 0 && t.mtime[n] >= from && t.mtime[n] <= NOW) hits.push(n);
    }
    hits.sort(p.sort === 'modified' ? (a, b) => t.mtime[b] - t.mtime[a] || a - b : (a, b) => t.val(b, m) - t.val(a, m) || a - b);
    const items = hits.slice(0, clamp(p.limit ?? 100, 1, 1000)).map((i): T.RecentFile => ({
      id: i, name: t.name[i], path: t.path(i), dirPath: t.path(t.parent[i]), size: t.size[i], alloc: t.alloc[i], modifiedMs: t.mtime[i],
      k: t.k[i], f: t.flags[i], u: t.u[i] ?? PURPOSE.Unknown, role: t.role[i] ?? ROLE.Unknown, owner: ownerOf(t, i),
    }));
    return { asOfMs: NOW, days, total: hits.length, items };
  }

  /** 往上最近的所属对象根（含自己）；没有为 -1 */
  function rootOf(t: MockTree, i: number): number {
    for (let x = i; x >= 0; x = t.parent[x]) if (t.ownerRoots.has(x)) return x;
    return -1;
  }

  // 同 TreeQueries.OwnerUsage（简化）：每个节点自己那份（计入值减去活着的子节点的计入值）归往上最近的根；
  // 硬链接组的活链接不全归同一个对象（含一边不归任何对象）时记进共享。两种度量各自 owned + shared + unowned = total
  function ownerUsage(p: { scopeId?: number | null; metric?: T.Metric; limit?: number }): T.OwnerUsage {
    const t = need();
    const scope = p.scopeId == null ? 0 : node(t, p.scopeId);
    const m = p.metric ?? 'alloc';
    const pick = (size: number, alloc: number): number => (m === 'alloc' ? alloc : size);
    interface Bytes { size: number; alloc: number }
    const acc = new Map<number, Bytes & { roles: number[]; roots: Map<number, Bytes> }>();
    const touch = (root: number): [Bytes & { roles: number[] }, Bytes] => {
      const k = t.ownerRoots.get(root)!.owner;
      let o = acc.get(k);
      if (!o) acc.set(k, (o = { size: 0, alloc: 0, roles: new Array<number>(ROLE_COUNT).fill(0), roots: new Map() }));
      let r = o.roots.get(root);
      if (!r) o.roots.set(root, (r = { size: 0, alloc: 0 }));
      return [o, r];
    };
    const ownerAt = (i: number): number => { const r = rootOf(t, i); return r < 0 ? -1 : t.ownerRoots.get(r)!.owner; };
    const above = rootOf(t, scope);
    if (above >= 0) touch(above); // 作用域整个落在某个对象里：那处根在作用域上方
    const shared = { size: 0, alloc: 0 };
    const unowned = { size: 0, alloc: 0 };
    const stack = [scope];
    while (stack.length) {
      const n = stack.pop()!;
      const kids = t.live(n);
      stack.push(...kids);
      if (t.ownerRoots.has(n)) touch(n);
      const size = t.counted(n, 'size') - kids.reduce((s, c) => s + t.counted(c, 'size'), 0);
      const alloc = t.counted(n, 'alloc') - kids.reduce((s, c) => s + t.counted(c, 'alloc'), 0);
      const root = rootOf(t, n);
      const group = hasFlag(t.flags[n], Flag.HardLinkDup) ? undefined : t.groups.get(n);
      const target = group?.some((x) => !hasFlag(t.flags[x], Flag.Deleted) && ownerAt(x) !== ownerAt(n)) ? shared : root < 0 ? unowned : null;
      if (target) { target.size += size; target.alloc += alloc; continue; }
      const [o, r] = touch(root);
      o.size += size; o.alloc += alloc; r.size += size; r.alloc += alloc;
      o.roles[t.role[n] ?? ROLE.Unknown] += pick(size, alloc);
    }
    const upper = (s: string): string => s.toUpperCase();
    const all = [...acc].map(([k, o]): T.OwnerUsageItem => ({
      kind: t.owners[k].kind, name: t.owners[k].name, evidence: t.owners[k].evidence, sizeInScope: o.size, allocInScope: o.alloc, roleBytesInScope: o.roles,
      roots: [...o.roots].map(([id, r]) => ({ id, path: t.path(id), sizeInScope: r.size, allocInScope: r.alloc, evidence: t.ownerRoots.get(id)!.evidence }))
        .sort((a, b) => pick(b.sizeInScope, b.allocInScope) - pick(a.sizeInScope, a.allocInScope) || a.id - b.id),
    })).sort((a, b) => pick(b.sizeInScope, b.allocInScope) - pick(a.sizeInScope, a.allocInScope)
      || (upper(a.name) < upper(b.name) ? -1 : upper(a.name) > upper(b.name) ? 1 : 0) || KINDS.indexOf(a.kind) - KINDS.indexOf(b.kind));
    return {
      scopeId: scope, totalSizeInScope: t.counted(scope, 'size'), totalAllocInScope: t.counted(scope, 'alloc'),
      ownedSizeInScope: all.reduce((s, x) => s + x.sizeInScope, 0), ownedAllocInScope: all.reduce((s, x) => s + x.allocInScope, 0),
      sharedSizeInScope: shared.size, sharedAllocInScope: shared.alloc, unownedSizeInScope: unowned.size, unownedAllocInScope: unowned.alloc,
      ownerCount: all.length, items: all.slice(0, clamp(p.limit ?? 100, 1, 1000)),
    };
  }

  // 同 TreeQueries.Related（简化）：所在的根 → 同一对象的另几处根 → 所在那处根自己地盘里（嵌在里面的别的根整棵跳过）
  // 角色和上级不同的目录，不含节点自己和包含它的目录，角色未知的不算，每种角色最多 3 个
  function related(p: { nodeId: number; metric?: T.Metric; limit?: number }): T.Related {
    const t = need();
    const id = node(t, p.nodeId);
    const m = p.metric ?? 'alloc';
    const owner = ownerOf(t, id);
    const found: T.RelatedItem[] = [];
    let total = 0;
    if (owner) {
      const mine = t.ownerRoots.get(owner.rootId)!;
      const byValue = (a: number, b: number): number => t.val(b, m) - t.val(a, m) || a - b;
      const item = (i: number, relation: T.RelationKind, evidence: T.Evidence): T.RelatedItem => ({
        id: i, name: t.name[i], path: t.path(i), isDir: hasFlag(t.flags[i], Flag.Directory), size: t.size[i], alloc: t.alloc[i],
        role: t.role[i] ?? ROLE.Unknown, relation, evidence,
      });
      if (owner.rootId !== id) found.push(item(owner.rootId, 'root', mine.evidence));
      const others = [...t.ownerRoots].filter(([r, x]) => x.owner === mine.owner && r !== owner.rootId && !hasFlag(t.flags[r], Flag.Deleted)).map(([r]) => r);
      for (const r of others.sort(byValue)) {
        const ev = t.ownerRoots.get(r)!.evidence;
        found.push(item(r, 'otherRoot', EVIDENCES.indexOf(ev) < EVIDENCES.indexOf(mine.evidence) ? ev : mine.evidence));
      }
      const groups = new Map<number, number[]>();
      const stack = [owner.rootId];
      while (stack.length) {
        const n = stack.pop()!;
        for (const c of t.live(n)) if (hasFlag(t.flags[c], Flag.Directory) && !t.ownerRoots.has(c)) stack.push(c);
        const r = t.role[n] ?? ROLE.Unknown;
        if (n === owner.rootId || r === ROLE.Unknown || r === t.role[t.parent[n]] || t.isUnder(id, n)) continue;
        const g = groups.get(r);
        if (g) g.push(n);
        else groups.set(r, [n]);
      }
      const modules = [...groups].map(([r, g]) => ({ r, g: g.sort(byValue) }))
        .sort((a, b) => t.val(b.g[0], m) - t.val(a.g[0], m) || a.r - b.r);
      total = found.length + modules.reduce((s, x) => s + x.g.length, 0);
      for (const { g } of modules) for (const n of g.slice(0, 3)) found.push(item(n, 'roleModule', mine.evidence));
    }
    const items = found.slice(0, clamp(p.limit ?? 50, 1, 1000));
    return { nodeId: id, owner, total, items, note: items.length ? null : tr('尚未识别关联对象') };
  }

  // 同 SearchIndex（简化）：| 分组、组内同时满足；相关度排序；智能匹配时精确一条都没有就把名字词放宽成近似匹配。模拟版没有引号和拼音首字母
  function search(p: { query: string; scopeId: number | null; metric: T.Metric; limit?: number; smart?: boolean; sort?: T.SearchSort }): T.SearchResult {
    const t0 = performance.now();
    const t = need();
    const unit: Record<string, number> = { '': 1, b: 1, k: KB, kb: KB, m: MB, mb: MB, g: GB, gb: GB, t: GB * 1024, tb: GB * 1024 };
    interface Term { test: (i: number) => boolean; name?: string }
    const groups: Term[][] = [];
    for (const words of p.query.split('|').map((g) => g.trim().split(/\s+/).filter(Boolean)).filter((g) => g.length)) {
      const terms: Term[] = [];
      for (const raw of words) {
        const term = raw.toLowerCase();
        if (term.startsWith('ext:')) {
          const set = new Set(term.slice(4).split(/[,;]/).filter(Boolean));
          terms.push({ test: (i) => set.has(t.name[i].includes('.') ? t.name[i].slice(t.name[i].lastIndexOf('.') + 1).toLowerCase() : '') });
        } else if (term.startsWith('size:')) {
          const mm = /^size:(>=|<=|>|<)?(\d+(?:\.\d+)?)([a-z]*)$/.exec(term);
          if (!mm || !(mm[3] in unit)) return { query: p.query, total: 0, tookMs: 0, truncated: false, items: [], error: tr('看不懂「{raw}」，试试 size:>1gb', { raw }), fuzzy: false };
          const n = Number(mm[2]) * unit[mm[3]];
          const op = mm[1] ?? '='; // 不带比较符是「恰好等于」（DESIGN §5）
          terms.push({ test: (i) => { const v = t.val(i, p.metric); return op === '=' ? v === n : op === '>' ? v > n : op === '<' ? v < n : op === '<=' ? v <= n : v >= n; } });
        } else if (term === 'type:dir' || term === 'dir:') {
          terms.push({ test: (i) => hasFlag(t.flags[i], Flag.Directory) });
        } else if (term === 'type:file' || term === 'file:') {
          terms.push({ test: (i) => !hasFlag(t.flags[i], Flag.Directory) });
        } else if (term.startsWith('-') && term.length > 1) {
          terms.push({ test: (i) => !t.name[i].toLowerCase().includes(term.slice(1)) });
        } else if (/[*?]/.test(term)) {
          const re = new RegExp(`^${term.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.')}$`, 'i');
          terms.push({ test: (i) => re.test(t.name[i]) });
        } else if (term.includes('\\')) {
          terms.push({ test: (i) => t.path(i).toLowerCase().includes(term) });
        } else {
          terms.push({ test: (i) => t.name[i].toLowerCase().includes(term), name: term });
        }
      }
      groups.push(terms);
    }
    if (p.scopeId != null) node(t, p.scopeId);
    const run = (fuzzy: boolean): { i: number; score: number }[] => {
      const loose = (x: Term): x is Term & { name: string } => fuzzy && x.name !== undefined && x.name.length >= 3;
      const hits: { i: number; score: number }[] = [];
      // 从 1 起：根是扫描目标本身，不参与搜索；作用域只搜严格子孙，不含它自己
      for (let i = 1; i < t.parent.length; i++) {
        const f = t.flags[i];
        if (hasFlag(f, Flag.Deleted) || hasFlag(f, Flag.Virtual)) continue;
        if (p.scopeId != null && (i === p.scopeId || !t.isUnder(i, p.scopeId))) continue;
        const name = t.name[i].toLowerCase();
        const g = groups.find((terms) => terms.every((x) => (loose(x) ? subsequence(name, x.name) > 0 : x.test(i))));
        if (g) hits.push({ i, score: g.reduce((sum, x) => sum + (x.name === undefined ? 0 : loose(x) ? subsequence(name, x.name) : nameTier(name, x.name) * 100), 0) });
      }
      return hits;
    };
    let hits = run(false);
    let fuzzy = false;
    if (!hits.length && p.smart && groups.length === 1 && groups[0].some((x) => x.name !== undefined && x.name.length >= 3)) {
      hits = run(true);
      fuzzy = hits.length > 0;
    }
    const byValue = (a: { i: number }, b: { i: number }): number => t.val(b.i, p.metric) - t.val(a.i, p.metric);
    hits.sort(fuzzy || p.sort === 'relevance' ? (a, b) => b.score - a.score || byValue(a, b) : byValue);
    const limit = p.limit ?? 300;
    return { query: p.query, total: hits.length, tookMs: Math.round(performance.now() - t0), truncated: hits.length > limit, items: hits.slice(0, limit).map((h) => fileItem(t, h.i)), error: null, fuzzy };
  }

  function suggestions(): T.Suggestion[] {
    const t = need();
    const out: T.Suggestion[] = [];
    const add = (id: string, group: T.SuggestionGroup, titleId: MsgId, descriptionId: MsgId, paths: string[], action: T.SuggestionAction, extra: Partial<T.Suggestion> = {}) => {
      const ids = paths.map((x) => t.find(x)).filter((x) => x > 0);
      const bytes = ids.reduce((s, i) => s + t.alloc[i], 0);
      if (!ids.length || bytes <= 0) return;
      const shown = ids.map((i) => displayPath(t.path(i)));
      const dp = shown.length > 2 ? tr('{first} 等 {n} 处', { first: shown.slice(0, 2).join(', '), n: shown.length }) : shown.join(', ');
      const selectable = extra.selectable ?? false;
      out.push({ id, group, title: tr(titleId), description: tr(descriptionId), bytes, count: ids.reduce((s, i) => s + Math.max(1, t.files[i]), 0), action, command: null, target: null, requiresAdmin: false, defaultPermanent: action === 'deleteContents', selectable, nodeIds: ids, items: ids.slice(0, selectable ? 100 : 20).map((i) => fileItem(t, i)), displayPath: dp, ...extra });
    };
    const u = HOME;
    add('user-temp', 'safe', '临时文件', '程序运行时留下的临时文件。正在使用的文件会被跳过。', [`${u}\\AppData\\Local\\Temp`], 'deleteContents');
    add('win-temp', 'safe', '系统临时文件', 'Windows 自己的临时目录。', ['C:\\Windows\\Temp'], 'deleteContents', { requiresAdmin: true });
    add('recycle-bin', 'safe', '回收站', '清空这个卷上的回收站。', ['C:\\$Recycle.Bin'], 'emptyRecycleBin', { defaultPermanent: true });
    add('wu-download', 'safe', 'Windows 更新缓存', '已经安装完的更新留下的安装包。', ['C:\\Windows\\SoftwareDistribution\\Download'], 'deleteContents', { requiresAdmin: true });
    add('browser-cache', 'safe', '浏览器缓存', '网页缓存会自动重建，不影响登录状态、书签和历史。请先关闭浏览器。', [`${u}\\AppData\\Local\\Google\\Chrome\\User Data\\Default\\Cache`, `${u}\\AppData\\Local\\Google\\Chrome\\User Data\\Default\\Code Cache`, `${u}\\AppData\\Local\\Microsoft\\Edge\\User Data\\Default\\Cache`], 'deleteContents');
    add('crash-dumps', 'safe', '崩溃转储和错误报告', '程序崩溃时生成的内存转储，排查完问题就没用了。', [`${u}\\AppData\\Local\\CrashDumps`, 'C:\\Windows\\MEMORY.DMP', 'C:\\ProgramData\\Microsoft\\Windows\\WER\\ReportArchive'], 'deleteContents');
    add('dev-caches-npm', 'safe', 'npm 缓存', '删除后下次安装依赖会重新下载。', [`${u}\\AppData\\Local\\npm-cache`], 'command', { command: 'npm cache clean --force' });
    add('dev-caches-pip', 'safe', 'pip 缓存', '删除后下次安装会重新下载。', [`${u}\\AppData\\Local\\pip\\Cache`], 'command', { command: 'pip cache purge' });
    add('dev-caches-nuget', 'safe', 'NuGet HTTP 缓存', '删除后下次还原包会重新下载。', [`${u}\\AppData\\Local\\NuGet\\v3-cache`], 'command', { command: 'dotnet nuget locals http-cache --clear' });
    add('build-outputs', 'safe', '编译产物', 'Rust 的 target 目录，可以重新编译出来。', [`${u}\\source\\repos\\tauri-thing\\target`], 'deleteNodes', { selectable: true, defaultPermanent: true });
    add('node-modules', 'caution', '闲置项目的 node_modules', '项目依赖，删除后需要重新安装。', [`${u}\\source\\repos\\web-app\\node_modules`], 'deleteNodes', { selectable: true, defaultPermanent: false });
    add('ai-models', 'caution', 'AI 模型', '本地模型文件，删除后要重新下载，体积很大。', [`${u}\\stable-diffusion\\models`], 'deleteNodes', { selectable: true, defaultPermanent: false });
    const big = largest({ nodeId: 0, metric: 'alloc', limit: 50 }).filter((x) => x.alloc >= GB && !/\.sys$/i.test(x.name) && !hasFlag(x.f, Flag.Metafile) && !x.path.toLowerCase().startsWith('c:\\windows'));
    add('large-files', 'caution', '大文件', '1 GB 以上的文件，按大小排序，自己挑。', big.map((x) => x.path), 'deleteNodes', { selectable: true, defaultPermanent: false });
    add('windows-old', 'caution', '旧版 Windows', '升级前的系统备份，10 天内可以回退。需要用系统的存储设置清理。', ['C:\\Windows.old'], 'openSettings', { target: 'ms-settings:storagesense' });
    add('hiberfil', 'protected', '休眠文件', '关闭休眠可以释放它，但快速启动也会一起关闭。不要直接删除文件。', ['C:\\hiberfil.sys'], 'command', { command: 'powercfg /h off', requiresAdmin: true });
    add('pagefile', 'protected', '虚拟内存', '在系统设置里调整大小，不要直接删除。', ['C:\\pagefile.sys', 'C:\\swapfile.sys'], 'openSettings', { target: 'SystemPropertiesPerformance.exe' });
    add('winsxs', 'protected', 'WinSxS 组件存储', '手动删除会损坏系统。用 DISM 清理旧组件。大小已经扣除了硬链接。', ['C:\\Windows\\WinSxS'], 'command', { command: 'Dism.exe /Online /Cleanup-Image /StartComponentCleanup', requiresAdmin: true });
    add('installer', 'protected', 'Windows Installer 缓存', '删除后很多程序将无法卸载或修复。', ['C:\\Windows\\Installer'], 'info');
    return out;
  }

  function check(ids: number[]): T.DeleteCheck {
    const t = need();
    const unique = [...new Set(ids)].filter((id) => !ids.some((parent) => parent !== id && t.isUnder(id, parent)));
    const items = unique.map((i) => {
      node(t, i);
      const d = details({ nodeId: i, metric: 'alloc' });
      return { id: i, path: d.path, isDir: d.isDir, size: d.size, alloc: d.alloc, files: d.files, blocked: d.isProtected, reason: d.protectReason, warning: d.warning };
    });
    const ok = items.filter((x) => !x.blocked);
    return {
      items,
      totalAlloc: ok.reduce((s, x) => s + x.alloc, 0), totalSize: ok.reduce((s, x) => s + x.size, 0), totalFiles: ok.reduce((s, x) => s + Math.max(1, x.files), 0),
      anyBlocked: items.some((x) => x.blocked), anyWarning: items.some((x) => !!x.warning),
    };
  }

  function remove(ids: number[], contentsOnly = false): T.OpResult {
    const t = need();
    let freed = 0;
    let deleted = 0;
    const unique = [...new Set(ids)].filter((id) => !ids.some((parent) => parent !== id && t.isUnder(id, parent)));
    for (const i of unique) {
      if (contentsOnly) for (const c of t.live(i)) { freed += t.markDeleted(c); deleted++; }
      else { freed += t.markDeleted(i); deleted++; }
    }
    if (summary && issues) summary = buildSummary(t, summary.scanId, summary.mode, summary.elapsedMs, summary.unaccountedBytes, issues);
    return { ok: true, alreadyGone: 0, freedBytes: freed, deleted, failed: 0, errors: [], message: null, treeVersion: t.version };
  }

  const ok = (message: string | null = null): T.Ok => ({ ok: true, message });
  const operations = new Map<string, { fingerprint: string; status: T.OperationStatus; promise: Promise<T.OpResult> }>();
  let writes: Promise<unknown> = Promise.resolve();
  function publish(status: T.OperationStatus, patch: Partial<T.OperationStatus>): void {
    Object.assign(status, patch, { updatedAtMs: Math.max(Date.now(), status.updatedAtMs + 1) });
    emit('operation.progress', { ...status });
  }

  const routes: Record<string, (p: any) => unknown> = {
    'operation.status': (p: { operationId: string }) => {
      const entry = operations.get(p.operationId);
      return entry ? { ...entry.status } : null;
    },
    'operation.cancel': (p: { operationId: string }) => {
      const entry = operations.get(p.operationId);
      if (!entry) throw new Error(tr('找不到这个清理任务'));
      if (entry.status.status === 'queued' || entry.status.status === 'running') publish(entry.status, { status: 'cancelling', message: tr('正在停止后续项目') });
      return { ...entry.status };
    },
    // ?scan=C:\ 让页面一打开就自动扫描（无头截图对照参考图时用），?mode=mft|walk 指定模式
    'app.info': () => ({ version: '0.1.0-mock', isAdmin: admin, osVersion: 'Windows 11 (mock)', processorCount: 24, autoScanPath: query.get('scan'), autoScanMode: (query.get('mode') as T.ScanMode | null) ?? null, homePath: HOME, language: locale() }) satisfies T.AppInfo,
    // 浏览器演示模式没有设置文件：语言记在 localStorage（i18n.setLocale 记的），这里只核对一下
    'app.setLanguage': (p: { language: string }) => {
      if (!isLocale(p.language)) throw new Error(tr('不支持的界面语言：{language}', { language: p.language }));
      return ok(null);
    },
    'app.relaunchAdmin': () => ok(tr('模拟环境：请在地址后加 ?admin=1 重新打开')),
    'drives.list': drives,
    'dialog.pickFolder': () => ({ path: 'D:\\Projects' }) satisfies T.PathResult,
    'scan.start': startScan,
    // 同后端：接受就马上报一条「取消中」，下一拍才发终态；没有扫描在跑也不算失败
    'scan.cancel': () => {
      if (running < 0) return ok(tr('没有正在进行的扫描'));
      cancelling = true;
      if (last) emit('scan.progress', { ...last, cancelling: true } satisfies T.ScanProgress);
      return ok();
    },
    'scan.summary': () => summary,
    'scan.savedSummaries': () => [...saved],
    // 同 ScanService.Issues：先认扫描编号，再翻页；limit 夹在 1..1000，越界给空页
    'scan.issues': (p: { kind: T.ScanIssueKind; offset?: number; limit?: number; scanId?: number | null }) => {
      if (!SCAN_ISSUE_KINDS.includes(p.kind)) throw new Error(tr('异常种类无效'));
      if (p.scanId != null && p.scanId !== summary?.scanId)
        throw new Error(p.scanId === running ? tr('扫描尚未完成，请稍候') : tr('这份异常清单属于另一次扫描，已经失效，请重新打开扫描结果'));
      if (!summary || !issues) throw new Error(tr('请先扫描磁盘或文件夹'));
      const all = issues[p.kind];
      const recorded = Math.min(all.length, MAX_ISSUES);
      const offset = Math.max(0, p.offset ?? 0);
      const items = all.slice(Math.min(offset, recorded), Math.min(recorded, offset + Math.min(1000, Math.max(1, p.limit ?? 200))));
      return { scanId: summary.scanId, kind: p.kind, total: all.length, recorded, offset, items, truncated: offset + items.length < all.length } satisfies T.ScanIssues;
    },
    'tree.view': view,
    'tree.details': details,
    'tree.largest': largest,
    'tree.children': children,
    'tree.recent': recent,
    'tree.owners': ownerUsage,
    'tree.related': related,
    'search.query': search,
    'cleanup.list': suggestions,
    'cleanup.check': (p: { suggestionId: string; nodeIds: number[] | null; permanent: boolean | null }) => {
      const t = need();
      const s = suggestions().find((x) => x.id === p.suggestionId);
      if (!s) throw new Error(tr('这条建议已经过期，请刷新'));
      if (p.nodeIds && (p.nodeIds.length === 0 || p.nodeIds.some((id) => !s.nodeIds.includes(id)))) throw new Error(tr('请求包含清理建议之外的条目'));
      const ids = p.nodeIds ?? s.nodeIds;
      const deleteCheck = s.action === 'deleteNodes' ? check(ids) : s.action === 'deleteContents' ? check(ids.flatMap((i) => t.live(i))) : null;
      return { confirmationToken: issue({ suggestionId: s.id, nodeIds: p.nodeIds, permanent: p.permanent }), deleteCheck } satisfies T.CleanupCheck;
    },
    'cleanup.run': (p: { suggestionId: string; nodeIds: number[] | null; permanent: boolean | null; confirmationToken?: string | null }) => {
      const s = suggestions().find((x) => x.id === p.suggestionId);
      if (!s) throw new Error(tr('这条建议已经过期，请刷新'));
      if (s.action !== 'openSettings' && s.action !== 'info') take(p.confirmationToken, { suggestionId: s.id, nodeIds: p.nodeIds, permanent: p.permanent });
      if (s.action === 'command') return { ok: true, alreadyGone: 0, freedBytes: 0, deleted: 0, failed: 0, errors: [], message: tr('已在新窗口运行：{command}。完成后按 F5 重新扫描', { command: s.command }), treeVersion: need().version };
      if (s.action === 'openSettings') return { ok: true, alreadyGone: 0, freedBytes: 0, deleted: 0, failed: 0, errors: [], message: tr('已打开系统设置'), treeVersion: need().version };
      const ids = p.nodeIds ?? s.nodeIds;
      return remove(ids, s.action === 'deleteContents' || s.action === 'emptyRecycleBin');
    },
    'cleanup.script': (p: { selections: T.CleanupSelection[] }) => {
      const t = need();
      const all = suggestions();
      const items = p.selections.map((sel) => {
        const s = all.find((x) => x.id === sel.suggestionId);
        if (!s) throw new Error(tr('这条建议已经过期，请刷新'));
        if (sel.nodeIds && sel.nodeIds.some((id) => !s.nodeIds.includes(id))) throw new Error(tr('勾选的条目不属于这条建议'));
        const ids = sel.nodeIds ?? s.nodeIds;
        return { s, paths: ids.map((i) => t.path(i)), bytes: ids.reduce((a, i) => a + t.alloc[i], 0) };
      });
      return { script: mockScript(items), bytes: items.reduce((a, b) => a + b.bytes, 0), items: items.length, needsAdmin: items.some((x) => x.s.requiresAdmin) } satisfies T.CleanupScript;
    },
    'node.openInExplorer': () => ok(),
    // 同后端：只认扫描记录里的位置（范围、认出的应用）；演示模式不真的打开资源管理器
    'shell.openFolder': (p: { path: string }) => {
      const same = (x: string): boolean => x.replace(/\\+$/, '').toLowerCase() === p.path.replace(/\\+$/, '').toLowerCase();
      if (!saved.some((s) => same(s.rootPath) || (s.apps ?? []).some((a) => same(a.path)))) throw new Error(tr('这个位置不在扫描记录里'));
      return ok();
    },
    'node.searchWeb': () => ok(),
    // 同 ShellOps.BuildQueryUrl 的校验；演示模式不开浏览器
    'web.search': (p: { engine?: string; query?: string }) => {
      if (!['google', 'bing'].includes((p.engine ?? '').trim().toLowerCase())) throw new Error(tr('不支持的搜索引擎'));
      if (!(p.query ?? '').trim()) throw new Error(tr('请输入要搜索的内容'));
      return ok();
    },
    'node.properties': () => ok(),
    'node.checkDelete': (p: { nodeIds: number[] }) => {
      const c = check(p.nodeIds);
      return { ...c, confirmationToken: issue({ suggestionId: null, nodeIds: c.items.filter((x) => !x.blocked).map((x) => x.id), permanent: null }) };
    },
    'node.delete': (p: { nodeIds: number[]; permanent: boolean; confirmationToken?: string | null }) => {
      take(p.confirmationToken, { suggestionId: null, nodeIds: p.nodeIds, permanent: null });
      return remove(check(p.nodeIds).items.filter((x) => !x.blocked).map((x) => x.id));
    },
  };

  return async (method, params) => {
    const fn = routes[method];
    if (!fn) throw new Error(tr('模拟后端没有实现 {method}', { method }));
    if (method === 'operation.status' || method === 'operation.cancel') return fn(params ?? {});
    if (method === 'cleanup.run' || method === 'node.delete') {
      const p = params as { operationId?: string; suggestionId?: string; nodeIds: number[] | null; permanent: boolean | null };
      if (p.operationId) {
        const fingerprint = JSON.stringify({ method, ...p });
        const old = operations.get(p.operationId);
        if (old) {
          if (old.fingerprint !== fingerprint) throw new Error(tr('同一 operationId 的请求参数不一致'));
          return old.promise;
        }
        const status: T.OperationStatus = {
          operationId: p.operationId, kind: method === 'node.delete' ? 'delete' : 'cleanup', status: 'queued',
          createdAtMs: Date.now(), updatedAtMs: Date.now(), currentPath: null, total: 0, completed: 0,
          failed: 0, skipped: 0, processedEntries: 0, freedBytes: null, result: null, message: null,
        };
        const execute = async (): Promise<T.OpResult> => {
          await new Promise((r) => setTimeout(r, 40));
          try {
            const t = need();
            const suggestion = method === 'cleanup.run' ? suggestions().find((s) => s.id === p.suggestionId) : null;
            const external = suggestion?.action === 'command' || suggestion?.action === 'openSettings';
            const raw = p.nodeIds ?? suggestion?.nodeIds ?? [];
            const targets = suggestion?.action === 'deleteContents' ? raw.flatMap((id) => t.live(id)) : raw;
            const ids = [...new Set(targets)].filter((id) => !targets.some((parent) => parent !== id && t.isUnder(id, parent)));
            status.total = external ? 0 : ids.length;
            if (status.status === 'cancelling') {
              const result: T.OpResult = { ok: false, alreadyGone: 0, freedBytes: 0, deleted: 0, failed: 0, errors: [], message: tr('已取消，未执行'), treeVersion: t.version };
              publish(status, { status: 'cancelled', skipped: status.total, result, freedBytes: 0, message: result.message });
              return result;
            }
            const entries = ids.reduce((n, id) => n + t.files[id] + t.dirs[id] + (t.realDir(id) ? 1 : 0), 0);
            publish(status, { status: 'running', currentPath: ids.length ? t.path(ids[0]) : null });
            const result = fn(params) as T.OpResult;
            const recycled = method === 'node.delete' ? !p.permanent : suggestion?.action === 'deleteNodes' && !p.permanent;
            publish(status, { status: external ? 'external' : result.ok ? 'completed' : 'failed', completed: external ? 0 : status.total,
              processedEntries: external ? 0 : entries, freedBytes: external ? null : recycled ? 0 : result.freedBytes,
              result, message: result.message, currentPath: null });
            return result;
          } catch (error) {
            const result: T.OpResult = { ok: false, alreadyGone: 0, freedBytes: 0, deleted: 0, failed: 1, errors: [{ path: '', message: String(error) }], message: String(error), treeVersion: tree?.version ?? 0 };
            publish(status, { status: 'failed', failed: status.total, result, message: String(error), currentPath: null });
            throw error;
          }
        };
        const promise = writes.then(execute, execute);
        operations.set(p.operationId, { fingerprint, status, promise });
        writes = promise.catch(() => {});
        publish(status, {});
        return promise;
      }
    }
    await new Promise((r) => setTimeout(r, method === 'search.query' ? 15 : 40));
    return fn(params ?? {});
  };
}
