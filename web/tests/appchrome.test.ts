import assert from 'node:assert/strict';
import { test } from 'node:test';
import { highlightParts, plainWords } from '../src/components/SearchHighlight.ts';
import { breadcrumbs, collapseCrumbs } from '../src/components/ExplorerCrumbs.ts';

test('plainWords 只留普通子串词', () => {
  assert.deepEqual(plainWords('ext:iso size:>1gb'), []);
  assert.deepEqual(plainWords('Foo -bar *.log img_??.png a\\b type:dir dir: FILE:'), ['foo']);
  assert.deepEqual(plainWords('"My File" foo FOO -"skip me" ext:mp4,mkv'), ['my file', 'foo']);
  assert.deepEqual(plainWords('   '), []);
  assert.deepEqual(plainWords('foo|bar -x | "a b" |'), ['foo', 'bar', 'a b'], '引号外的 | 是「或」的分隔，不是词的一部分');
});

test('highlightParts 按子串切片，重叠相邻合并，不分大小写', () => {
  const t = (name: string, words: string[]) => highlightParts(name, words).map((p) => (p.hit ? `[${p.text}]` : p.text)).join('');
  assert.equal(t('Foo_foo.txt', ['foo']), '[Foo]_[foo].txt');
  assert.equal(t('abcd', ['ab', 'bc']), '[abc]d');
  assert.equal(t('abcd', ['ab', 'cd']), '[abcd]');
  assert.equal(t('hello', []), 'hello');
  assert.equal(t('hello', ['zzz']), 'hello');
  assert.equal(t('İstanbul', ['stan']), 'İstanbul'); // 小写后长度变了，不高亮
});

test('breadcrumbs 把用户目录折成 ~', () => {
  const trail = [
    { id: 0, name: 'C:\\' },
    { id: 1, name: 'Users' },
    { id: 2, name: 'Alex' },
    { id: 3, name: 'Desktop' },
  ];
  assert.deepEqual(breadcrumbs(trail, 'C:\\Users\\Alex'), [
    { id: 2, label: '~' },
    { id: 3, label: 'Desktop' },
  ]);
  assert.deepEqual(breadcrumbs(trail.slice(0, 3), 'c:\\users\\alex\\'), [{ id: 2, label: '~' }]);
  assert.deepEqual(breadcrumbs(trail.slice(0, 2), 'C:\\Users\\Alex').map((c) => c.label), ['C:\\', 'Users']);
  assert.deepEqual(breadcrumbs(trail, null).map((c) => c.label), ['C:\\', 'Users', 'Alex', 'Desktop']);
  // 扫描根在用户目录里
  const sub = [{ id: 0, name: 'C:\\Users\\Alex\\Projects' }, { id: 5, name: 'atlas' }];
  assert.deepEqual(breadcrumbs(sub, 'C:\\Users\\Alex').map((c) => c.label), ['~\\Projects', 'atlas']);
});

test('collapseCrumbs 太深时留头和尾', () => {
  const c = (n: number) => Array.from({ length: n }, (_, i) => ({ id: i, label: `d${i}` }));
  assert.equal(collapseCrumbs(c(5)).length, 5);
  assert.deepEqual(collapseCrumbs(c(8)).map((x) => x.label), ['d0', '…', 'd5', 'd6', 'd7']);
});
