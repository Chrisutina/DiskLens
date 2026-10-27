import assert from 'node:assert/strict';
import { test } from 'node:test';
import { formatBytes, formatDelta, formatPercent, middleEllipsis, relativePath } from '../src/lib/format.ts';

test('formatBytes 用 1024 进制，三位有效数字', () => {
  assert.equal(formatBytes(0), '0 B');
  assert.equal(formatBytes(1023), '1023 B');
  assert.equal(formatBytes(1536), '1.5 KB');
  assert.equal(formatBytes(123 * 1024 ** 2), '123 MB');
  assert.equal(formatBytes(5.4 * 1024 ** 3), '5.4 GB');
  assert.equal(formatBytes(-2048), '-2.0 KB');
});

test('formatPercent、middleEllipsis、relativePath', () => {
  assert.equal(formatPercent(0.1234), '12.3%');
  assert.equal(formatPercent(0.0004), '<0.1%');
  const s = middleEllipsis('C:\\Users\\Alex\\AppData\\Local\\Google\\Chrome\\User Data\\Default\\Cache', 30);
  assert.equal(s.length, 30);
  assert.ok(s.startsWith('C:\\') && s.endsWith('Cache') && s.includes('…'));
  assert.equal(relativePath('C:\\Users\\Alex\\x.txt', 'C:\\Users'), 'Alex\\x.txt');
  assert.equal(relativePath('D:\\x', 'C:\\'), 'D:\\x');
});

test('formatDelta：增减带正负号（减号用数学负号），不变写持平', () => {
  assert.equal(formatDelta(0), '持平');
  assert.equal(formatDelta(1536), '+' + formatBytes(1536));
  assert.equal(formatDelta(-1536), '−' + formatBytes(1536));
});
