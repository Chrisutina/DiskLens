import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { test } from 'node:test';

// Windows PowerShell 5.1 按系统代码页读没有 BOM 的脚本：中文系统上 UTF-8 的中文会读成乱码，引号对不上，整个脚本解析失败；
// cmd 读 .cmd 用 OEM 代码页，BOM 也会把第一行弄坏。build.ps1 就这样在新拉下来的目录里一闪而过、Start-DiskLens.ps1 起不来过。
test('仓库根目录的启动脚本只有 ASCII（.ps1 带 UTF-8 BOM 也行）', () => {
  const root = new URL('../../', import.meta.url);
  const scripts = readdirSync(root).filter((n) => /\.(ps1|cmd)$/i.test(n));
  assert.ok(scripts.includes('build.ps1') && scripts.includes('Start-DiskLens.ps1') && scripts.includes('Start-DiskLens.cmd'));
  for (const name of scripts) {
    const bytes = readFileSync(new URL(name, root));
    const bom = bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf;
    assert.ok(bytes.every((b) => b < 0x80) || (bom && name.endsWith('.ps1')), `${name} 含非 ASCII 字符`);
  }
});
