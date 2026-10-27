import assert from 'node:assert/strict';
import { test } from 'node:test';
import { en } from '../src/lib/locales/en.ts';
import { ja } from '../src/lib/locales/ja.ts';
import { plural, type Msg } from '../src/lib/locales/util.ts';
import { fromSystem, isLocale, locale, localized, setLocale, t, withName, type MsgId } from '../src/lib/i18n.ts';

// 界面语言（DESIGN §8.8）：目录完整、占位对得上、换语言即时生效并记住，启动时按 ?lang= → 本地存储 → 系统语言取。

const holes = (s: string): string[] => [...new Set([...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]))].sort();
const zhOf = (id: string): string => (id.includes('@@') ? id.slice(0, id.indexOf('@@')) : id);

test('系统语言：中文（简繁都算）用中文，日文用日文，其余英文，拿不到算中文', () => {
  assert.equal(fromSystem('zh-CN'), 'zh-CN');
  assert.equal(fromSystem('zh-TW'), 'zh-CN');
  assert.equal(fromSystem('ja-JP'), 'ja');
  assert.equal(fromSystem('en-GB'), 'en');
  assert.equal(fromSystem('de'), 'en');
  assert.equal(fromSystem(undefined), 'zh-CN');
  assert.ok(isLocale('ja') && !isLocale('jp') && !isLocale(null));
});

test('目录：英文和日文的 msgid 一模一样，没有空译文', () => {
  assert.deepEqual(Object.keys(ja).sort(), Object.keys(en).sort());
  for (const [id, msg] of [...Object.entries(en), ...Object.entries(ja)] as [string, Msg][])
    assert.ok(typeof msg === 'function' || msg.trim().length > 0, `空译文：${id}`);
});

test('目录：译文的占位和中文原文一致；写成函数的，给齐参数后不剩占位', () => {
  for (const [name, catalog] of [['en', en], ['ja', ja]] as const) {
    for (const [id, msg] of Object.entries(catalog) as [string, Msg][]) {
      const want = holes(zhOf(id));
      if (typeof msg === 'string') assert.deepEqual(holes(msg), want, `${name}：${id}`);
      else {
        const out = msg(Object.fromEntries(want.map((h) => [h, 2])));
        assert.ok(!/\{\w+\}/.test(out) && !out.includes('undefined'), `${name}：${id} → ${out}`);
      }
    }
  }
});

test('t()：中文就是 msgid，@@ 后面的上下文不显示；数字按千分位，null 写成空', () => {
  assert.equal(locale(), 'zh-CN');
  assert.equal(t('文件@@单个'), '文件');
  assert.equal(t('{n} 项', { n: 12345 }), '12,345 项');
  assert.equal(t('详情读取失败：{error}', { error: null }), '详情读取失败：');
  assert.equal(plural(1, 'file'), '1 file');
  assert.equal(plural(1500, 'file'), '1,500 files');
});

test('换语言：t() 和名字表即时跟着变，写进本地存储；同一个中文按上下文译成不同的词', () => {
  const store = new Map<string, string>();
  const g = globalThis as { localStorage?: Pick<Storage, 'getItem' | 'setItem'> };
  g.localStorage = { getItem: (k) => store.get(k) ?? null, setItem: (k, v) => void store.set(k, v) };
  const names = localized({ a: '文件', b: '文件@@单个' } satisfies Record<string, MsgId>);
  const row = withName({ id: 1, zh: '视频' as MsgId });
  try {
    setLocale('en');
    assert.equal(locale(), 'en');
    assert.equal(store.get('disklens.language'), 'en');
    assert.equal(t('{n} 项', { n: 1 }), '1 item');
    assert.equal(t('{n} 项', { n: 3 }), '3 items');
    assert.deepEqual({ ...names }, { a: 'Files', b: 'File' });
    assert.equal(row.name, 'Video');
    assert.equal(row.zh, '视频', '原文留着给契约测试');
    setLocale('ja');
    assert.equal(t('取消'), ja['取消']);
    assert.notEqual(ja['取消'], en['取消']);
    assert.equal(store.get('disklens.language'), 'ja');
  } finally {
    setLocale('zh-CN');
    delete g.localStorage;
  }
  assert.equal(t('取消'), '取消');
});

test('启动语言：地址里的 ?lang= 优先，其次本地存储，都没有按系统语言（Node 里拿不到，就是中文）', async () => {
  const g = globalThis as { location?: unknown; localStorage?: unknown };
  const load = async (search: string, saved: string | null): Promise<string> => {
    g.location = { search, pathname: '/', hash: '' };
    g.localStorage = { getItem: () => saved, setItem: () => {} };
    // 每次换个查询串拿一份新的模块实例：启动语言只在加载时定一次
    const m = await import(new URL(`../src/lib/i18n.ts?case=${encodeURIComponent(search + '|' + saved)}`, import.meta.url).href) as typeof import('../src/lib/i18n.ts');
    return m.locale();
  };
  try {
    assert.equal(await load('?lang=ja', 'en'), 'ja', '宿主带的 ?lang= 是保存过的选择，优先');
    assert.equal(await load('', 'en'), 'en', '浏览器演示：本地存储');
    assert.equal(await load('?lang=xx', 'oops'), 'zh-CN', '都不认识：按系统语言');
  } finally {
    delete g.location;
    delete g.localStorage;
  }
});
