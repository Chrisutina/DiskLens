import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// 前后端契约测试共用：直接读 C# 源文件，拿 record 的参数名、枚举成员和模拟后端的输出比对。

export const readCore = (file: string): string => readFileSync(new URL(`../../src/DiskLens.Core/${file}`, import.meta.url), 'utf8');
const contracts = readCore('Contracts.cs');
const camel = (s: string): string => s[0].toLowerCase() + s.slice(1);

/** C# record 的参数名，按 JSON 的 camelCase 写法 */
export function recordKeys(name: string): string[] {
  const m = new RegExp(`public sealed record ${name}\\(([^;]*)\\);`).exec(contracts);
  assert.ok(m, `Contracts.cs 里找不到 record ${name}`);
  return m[1].split(',').map((p) => camel(p.replace(/=.*$/s, '').trim().split(/\s+/).at(-1)!)).sort();
}

export function enumOf(name: string): string[] {
  const m = new RegExp(`public enum ${name} \\{([^}]*)\\}`).exec(contracts);
  assert.ok(m, `Contracts.cs 里找不到 enum ${name}`);
  return m[1].split(',').map((s) => camel(s.trim())).filter(Boolean);
}

export const keys = (o: object): string[] => Object.keys(o).sort();
