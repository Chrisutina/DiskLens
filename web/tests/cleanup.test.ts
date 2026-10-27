import assert from 'node:assert/strict';
import { test } from 'node:test';
import { cleanupSpace, compareSuggestions } from '../src/lib/cleanup.ts';
import type { Suggestion } from '../src/lib/types.ts';

const suggestion = (id: string, values: Partial<Suggestion> = {}) => ({
  id, group: 'safe', action: 'deleteContents', bytes: 100, requiresAdmin: false, ...values,
}) as Suggestion;

test('同一批缓存、下载、命令只把永久清理计为立即释放，回收站和手动量分开', () => {
  const selection = [suggestion('cache'), suggestion('download', { action: 'deleteNodes', bytes: 200, defaultPermanent: true }),
    suggestion('command', { action: 'command', bytes: 300 })];
  const space = selection.reduce((sum, s) => {
    const part = cleanupSpace(s, s.bytes);
    return { immediate: sum.immediate + part.immediate, recycle: sum.recycle + part.recycle, manual: sum.manual + part.manual };
  }, { immediate: 0, recycle: 0, manual: 0 });
  assert.deepEqual(space, { immediate: 100, recycle: 200, manual: 300 });
  assert.deepEqual(cleanupSpace(selection[1], 50, true), { immediate: 50, recycle: 0, manual: 0 });
  assert.deepEqual(cleanupSpace(suggestion('bin', { action: 'emptyRecycleBin' }), 90), { immediate: 90, recycle: 0, manual: 0 });
});

test('风险高的大项不挤掉安全项；当前权限可执行项优先，权限改变会重新排序', () => {
  const list = [suggestion('caution', { group: 'caution', bytes: 9999 }),
    suggestion('admin', { requiresAdmin: true, bytes: 2000 }), suggestion('small'), suggestion('large', { bytes: 500 }),
    suggestion('info', { action: 'info', bytes: 9000 }), suggestion('command', { action: 'command', bytes: 1000 })];
  assert.deepEqual(list.toSorted((a, b) => compareSuggestions(a, b, false)).map(s => s.id), ['large', 'small', 'command', 'admin', 'info', 'caution']);
  assert.deepEqual(list.toSorted((a, b) => compareSuggestions(a, b, true)).map(s => s.id), ['admin', 'large', 'small', 'command', 'info', 'caution']);
});

test('相近容量固定分档后优先少文件，比较器在边界仍满足传递性', () => {
  const small = suggestion('small', { bytes: 1000, count: 2 });
  const big = suggestion('big', { bytes: 1010, count: 20 });
  assert.ok(compareSuggestions(small, big, false) < 0);
  const items = [small, big, ...Array.from({ length: 15 }, (_, i) => suggestion(String(i), { bytes: 850 + i * 20, count: 20 - i }))];
  for (const a of items) for (const b of items) for (const c of items) {
    if (compareSuggestions(a, b, false) <= 0 && compareSuggestions(b, c, false) <= 0) {
      assert.ok(compareSuggestions(a, c, false) <= 0, `${a.id} <= ${b.id} <= ${c.id}`);
    }
  }
});
