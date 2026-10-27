import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { CLEANUP_COLORS, PURPOSE, PURPOSE_COUNT, PURPOSE_ORDER, purpose } from '../src/lib/purposes.ts';
import { EVIDENCE_NAMES, OWNER_KIND_NAMES, ROLE, ROLE_COUNT, role } from '../src/lib/roles.ts';
import { setLocale, type Locale } from '../src/lib/i18n.ts';

// 前后端用途契约：编号、key、名称以 C# 的 Purposes.cs / Roles.cs 为准，这里直接读源文件比对——
// 任何一边改了编号、key 或名字而另一边没跟，这个测试先翻脸，不用等界面上把「游戏」涂成「应用数据」才发现。

const read = (file: string): string => readFileSync(new URL(`../../src/DiskLens.Core/${file}`, import.meta.url), 'utf8');
const cs = read('Purposes.cs');
const rolesCs = read('Roles.cs');

/** 文件里第一处 public const byte 声明：名字 → 编号 */
function constantsOf(src: string): Map<string, number> {
  const m = /public const byte ([^;]+);/.exec(src);
  assert.ok(m, '找不到 public const byte 声明');
  return new Map(m[1].split(',').map((part) => {
    const [name, value] = part.split('=').map((s) => s.trim());
    return [name, Number(value)] as [string, number];
  }));
}

const countOf = (src: string): number => Number(/public const int Count = (\d+);/.exec(src)?.[1]);
const tableOf = (src: string): { key: string; name: string }[] =>
  [...src.matchAll(/\("([a-zA-Z]+)", "([^"]+)"\)/g)].map((m) => ({ key: m[1], name: m[2] }));

/** C# 枚举成员按 JsonStringEnumConverter(CamelCase) 的写法：首字母小写 */
function enumOf(name: string): string[] {
  const m = new RegExp(`enum ${name} : byte \\{([^}]*)\\}`).exec(rolesCs);
  assert.ok(m, `Roles.cs 里找不到 enum ${name}`);
  return m[1].split(',').map((s) => s.trim()).filter(Boolean).map((s) => s[0].toLowerCase() + s.slice(1));
}

function stringsOf(name: string): string[] {
  const m = new RegExp(`${name} = \\[([^\\]]*)\\]`).exec(rolesCs);
  assert.ok(m, `Roles.cs 里找不到 ${name}`);
  return [...m[1].matchAll(/"([^"]+)"/g)].map((x) => x[1]);
}

const csConstants = (): Map<string, number> => constantsOf(cs);
const csCount = countOf(cs);
const csTable = tableOf(cs);

test('契约：编号常量和 C# Purposes 一一对应', () => {
  const c = csConstants();
  assert.equal(csCount, PURPOSE_COUNT, '用途数量');
  assert.deepEqual(Object.fromEntries(c), { ...PURPOSE }, '常量名和编号');
  assert.deepEqual([...c.values()].sort((a, b) => a - b), [...Array(PURPOSE_COUNT).keys()], '编号从 0 连续排到 Count - 1');
});

test('契约：每个编号的 key、名称和 C# 的名称表一致', () => {
  assert.equal(csTable.length, PURPOSE_COUNT, 'C# 名称表的条数等于用途数');
  for (let u = 0; u < PURPOSE_COUNT; u++) {
    const p = purpose(u);
    assert.equal(p.u, u, `第 ${u} 项的 u`);
    assert.deepEqual({ key: p.key, name: p.name }, csTable[u], `U${u} 的 key 和名称`);
  }
  assert.equal(new Set(csTable.map((t) => t.key)).size, PURPOSE_COUNT, 'key 不重复');
  assert.equal(new Set(csTable.map((t) => t.name)).size, PURPOSE_COUNT, '名称不重复');
  assert.equal(purpose(99), purpose(PURPOSE.Unknown), '越界编号回落到未识别');
});

test('契约：角色编号、key、名称和 C# 的 Roles 一一对应', () => {
  const c = constantsOf(rolesCs);
  assert.equal(countOf(rolesCs), ROLE_COUNT, '角色数量');
  assert.deepEqual(Object.fromEntries(c), { ...ROLE }, '常量名和编号');
  assert.deepEqual([...c.values()].sort((a, b) => a - b), [...Array(ROLE_COUNT).keys()], '编号从 0 连续排到 Count - 1');
  const t = tableOf(rolesCs);
  assert.equal(t.length, ROLE_COUNT, 'C# 角色表的条数等于角色数');
  for (let r = 0; r < ROLE_COUNT; r++) {
    assert.equal(role(r).r, r, `第 ${r} 项的编号`);
    assert.deepEqual({ key: role(r).key, name: role(r).name }, t[r], `角色 ${r} 的 key 和名称`);
  }
  assert.equal(new Set(t.map((x) => x.key)).size, ROLE_COUNT, 'key 不重复');
  assert.equal(new Set(t.map((x) => x.name)).size, ROLE_COUNT, '名称不重复');
  assert.ok(ROLE_COUNT <= 64, 'C# 把角色塞在一个字节的低 6 位');
  assert.equal(role(99), role(ROLE.Unknown), '越界编号回落到未知');
});

test('契约：证据等级、所属对象种类和 C# 的枚举逐项对应（JSON 里是 camelCase 字符串）', () => {
  assert.deepEqual(Object.keys(EVIDENCE_NAMES), enumOf('Evidence'), '证据等级的取值和顺序');
  assert.deepEqual(Object.values(EVIDENCE_NAMES), stringsOf('EvidenceNames'), '证据等级的名称');
  assert.deepEqual(Object.keys(OWNER_KIND_NAMES), enumOf('OwnerKind'), '所属对象种类的取值和顺序');
  assert.deepEqual(Object.values(OWNER_KIND_NAMES), stringsOf('OwnerKindNames'), '所属对象种类的名称');
});

test('契约：后台名字表的英文、日文和界面目录逐字一致（后台的说明文字里也会提到这些名字）', () => {
  const csStrings = (src: string, name: string): string[] => {
    const m = new RegExp(`${name} = \\[([^\\]]*)\\]`).exec(src);
    assert.ok(m, `找不到 ${name}`);
    return [...m[1].matchAll(/"([^"]+)"/g)].map((x) => x[1]);
  };
  const names = () => ({
    purposes: Array.from({ length: PURPOSE_COUNT }, (_, u) => purpose(u).name),
    roles: Array.from({ length: ROLE_COUNT }, (_, r) => role(r).name),
    evidence: Object.values(EVIDENCE_NAMES),
    ownerKinds: Object.values(OWNER_KIND_NAMES),
  });
  const cases: [Locale, string][] = [['en', 'En'], ['ja', 'Ja']];
  try {
    for (const [code, suffix] of cases) {
      setLocale(code);
      const ui = names();
      assert.deepEqual(csStrings(cs, `Names${suffix}`), ui.purposes, `用途名（${code}）`);
      assert.deepEqual(csStrings(rolesCs, `Names${suffix}`), ui.roles, `角色名（${code}）`);
      assert.deepEqual(csStrings(rolesCs, `EvidenceNames${suffix}`), ui.evidence, `证据等级（${code}）`);
      assert.deepEqual(csStrings(rolesCs, `OwnerKindNames${suffix}`), ui.ownerKinds, `所属对象种类（${code}）`);
    }
  } finally {
    setLocale('zh-CN');
  }
});

test('图例顺序：每个用途正好出现一次，未识别和未解释空间垫底', () => {
  assert.deepEqual([...PURPOSE_ORDER].sort((a, b) => a - b), [...Array(PURPOSE_COUNT).keys()]);
  assert.deepEqual(PURPOSE_ORDER.slice(-2), [PURPOSE.Unknown, PURPOSE.Unexplained]);
});

// ---------------- 配色 ----------------

const rgb = (hex: string): number[] => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));

function hsl(hex: string): { h: number; s: number; l: number } {
  const [r, g, b] = rgb(hex).map((v) => v / 255);
  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  const l = (max + min) / 2;
  const d = max - min;
  const s = d === 0 ? 0 : d / (1 - Math.abs(2 * l - 1));
  const h = d === 0 ? 0 : max === r ? (((g - b) / d) * 60 + 360) % 360 : max === g ? ((b - r) / d) * 60 + 120 : ((r - g) / d) * 60 + 240;
  return { h, s, l };
}

/** CIE76 色差：sRGB → Lab 后取欧氏距离；2 左右是刚能看出来，这里只用来拦「两个用途几乎同色」 */
function deltaE(a: string, b: string): number {
  const lab = (hex: string): number[] => {
    const [r, g, b2] = rgb(hex).map((v) => v / 255).map((v) => (v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
    const f = (t: number): number => (t > 216 / 24389 ? Math.cbrt(t) : (24389 / 27 * t + 16) / 116);
    const x = f((0.4124 * r + 0.3576 * g + 0.1805 * b2) / 0.95047);
    const y = f(0.2126 * r + 0.7152 * g + 0.0722 * b2);
    const z = f((0.0193 * r + 0.1192 * g + 0.9505 * b2) / 1.08883);
    return [116 * y - 16, 500 * (x - y), 200 * (y - z)];
  };
  const p = lab(a);
  const q = lab(b);
  return Math.hypot(p[0] - q[0], p[1] - q[1], p[2] - q[2]);
}

const all = [...Array(PURPOSE_COUNT).keys()].map(purpose);
const neutral = new Set<number>([PURPOSE.Unknown, PURPOSE.Unexplained]);

test('配色：每个用途一个色系——填充最浅、描边居中、圆点最深，三者同一色相', () => {
  for (const p of all) {
    for (const c of [p.dot, p.fill, p.line]) assert.match(c, /^#[0-9A-F]{6}$/, `${p.name} 的颜色写成 #RRGGBB`);
    const [dot, fill, line] = [hsl(p.dot), hsl(p.fill), hsl(p.line)];
    assert.ok(fill.l > line.l && line.l > dot.l, `${p.name}：填充 > 描边 > 圆点（亮度 ${fill.l.toFixed(2)} / ${line.l.toFixed(2)} / ${dot.l.toFixed(2)}）`);
    if (neutral.has(p.u)) continue;
    const gap = (x: number, y: number): number => Math.min(Math.abs(x - y), 360 - Math.abs(x - y));
    assert.ok(dot.s >= 0.12, `${p.name} 的圆点有颜色（饱和度 ${dot.s.toFixed(2)}）`);
    assert.ok(gap(dot.h, fill.h) <= 10 && gap(dot.h, line.h) <= 10, `${p.name} 的三档同一色相（${dot.h.toFixed(0)}° / ${fill.h.toFixed(0)}° / ${line.h.toFixed(0)}°）`);
  }
});

test('配色：未识别和未解释空间是中性灰，而且彼此分得开', () => {
  for (const u of neutral) for (const c of Object.values(purpose(u)).filter((v) => typeof v === 'string' && v.startsWith('#'))) {
    assert.ok(hsl(c as string).s < 0.08, `${purpose(u).name} 的 ${c} 是中性灰`);
  }
  // 填充几乎一样：未解释空间在地图和扫描条上另画灰斜纹（paint.ts / ScanBar），图例靠更深的圆点
  assert.ok(deltaE(purpose(PURPOSE.Unknown).dot, purpose(PURPOSE.Unexplained).dot) >= 15, '两种灰的圆点一眼分得开');
});

test('配色：用途之间不撞色，也不借用清理风险色和勾选的绿色', () => {
  for (let i = 0; i < all.length; i++) {
    for (let j = i + 1; j < all.length; j++) {
      const [a, b] = [all[i], all[j]];
      assert.ok(deltaE(a.dot, b.dot) >= 12, `${a.name} 和 ${b.name} 的圆点太像（ΔE ${deltaE(a.dot, b.dot).toFixed(1)}）`);
      if (neutral.has(a.u) && neutral.has(b.u)) continue;
      assert.ok(deltaE(a.fill, b.fill) >= 3, `${a.name} 和 ${b.name} 的填充太像（ΔE ${deltaE(a.fill, b.fill).toFixed(1)}）`);
      assert.ok(deltaE(a.line, b.line) >= 5, `${a.name} 和 ${b.name} 的描边太像（ΔE ${deltaE(a.line, b.line).toFixed(1)}）`);
    }
  }
  const risk = new Set(Object.values(CLEANUP_COLORS).flatMap((v) => (typeof v === 'string' ? [v] : Object.values(v))).map((c) => c.toUpperCase()));
  for (const p of all) {
    for (const c of [p.dot, p.fill, p.line]) assert.ok(!risk.has(c.toUpperCase()) || neutral.has(p.u), `${p.name} 的 ${c} 和清理颜色重了`);
    const h = hsl(p.dot);
    assert.ok(neutral.has(p.u) || h.h < 95 || h.h > 160, `${p.name} 的圆点落在绿色区间（${h.h.toFixed(0)}°），绿色留给「可放心清理」和勾选斜纹`);
  }
});
