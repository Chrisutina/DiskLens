import { createSubscriber } from 'svelte/reactivity';
import { en } from './locales/en.ts';
import { ja } from './locales/ja.ts';
import { fill, type Msg, type Params } from './locales/util.ts';

// 界面语言（DESIGN §8.8）。msgid 就是简体中文原文：中文直接用它，英文、日文目录按同一个 msgid 给译文。
// MsgId 从英文目录推出来，日文目录必须一条不差地对上——少翻一条、多一条、引用了目录里没有的 msgid，svelte-check 当场报错。
// 不用 runes：format.ts 这类纯模块和 Node 里的单测也要能直接 import；界面靠 createSubscriber 跟着语言重画。

export const LOCALES = [
  { code: 'zh-CN', name: '简体中文' },
  { code: 'en', name: 'English' },
  { code: 'ja', name: '日本語' },
] as const;

export type Locale = (typeof LOCALES)[number]['code'];
export type MsgId = keyof typeof en;

const catalogs: Record<Exclude<Locale, 'zh-CN'>, Record<MsgId, Msg>> = { en, ja };
const STORAGE_KEY = 'disklens.language';

/** 系统语言标签 → 界面语言：中文（简繁都算）用中文，日文用日文，其余一律英文；拿不到就是中文（Node 单测里就是这样） */
export function fromSystem(tag: string | null | undefined): Locale {
  const t = (tag ?? '').toLowerCase();
  if (!t || t.startsWith('zh')) return 'zh-CN';
  return t.startsWith('ja') ? 'ja' : 'en';
}

export const isLocale = (v: unknown): v is Locale => LOCALES.some((l) => l.code === v);

/** 启动时的语言：宿主在地址里带的 ?lang=（保存过的选择，没有就是系统语言）→ 浏览器演示模式记在 localStorage 的 → 系统语言 */
function initial(): Locale {
  const fromUrl = new URLSearchParams(globalThis.location?.search ?? '').get('lang');
  if (isLocale(fromUrl)) return fromUrl;
  try {
    const saved = globalThis.localStorage?.getItem(STORAGE_KEY);
    if (isLocale(saved)) return saved;
  } catch { /* 存储不可用：当没有 */ }
  return fromSystem(globalThis.window ? globalThis.navigator?.language : undefined);
}

let current: Locale = initial();
let notify: (() => void) | undefined;
const track = createSubscriber((update) => {
  notify = update;
  return () => (notify = undefined);
});

/** 当前语言；在模板、$derived 里读会跟着变 */
export function locale(): Locale {
  track();
  return current;
}

/** 换语言：界面马上重画，<html lang>、标题、地址里的 ?lang= 和本地存储一起改（刷新页面还是这个语言）。保存到后台的事由调用方做 */
export function setLocale(next: Locale): void {
  if (next === current) return;
  current = next;
  applyToDocument(next);
  try { globalThis.localStorage?.setItem(STORAGE_KEY, next); } catch { /* 存不下就算了，后台那份才是准的 */ }
  notify?.();
}

function applyToDocument(code: Locale): void {
  const doc = globalThis.document;
  if (!doc) return;
  doc.documentElement.lang = code;
  doc.title = t('DiskLens 磁盘透镜');
  const loc = globalThis.location;
  if (loc && globalThis.history) {
    const q = new URLSearchParams(loc.search);
    q.set('lang', code);
    globalThis.history.replaceState(globalThis.history.state, '', `${loc.pathname}?${q}${loc.hash}`);
  }
}

/** 同一个中文在不同地方要译成不同的词（「文件」是计数时的 Files，也是一项的类型 File）：msgid 写成「文件@@单个」，中文显示 @@ 前面那段 */
const CONTEXT = '@@';

/** 译文；{name} 换成参数（数字按千分位），英文的单复数之类写成函数 */
export function t(id: MsgId, params?: Params): string {
  const lang = locale();
  const msg: Msg = lang === 'zh-CN' ? (id.includes(CONTEXT) ? id.slice(0, id.indexOf(CONTEXT)) : id) : catalogs[lang][id];
  return typeof msg === 'function' ? msg(params ?? {}) : fill(msg, params);
}

/**
 * 名字表按当前语言读：表本身和 key 照旧（前后端契约比的是中文原文），取值的时候才翻。
 * 在模板里读也会跟着语言变；Object.values 之类照常能用（属性是可枚举的 getter）
 */
export function localized<K extends string>(names: Readonly<Record<K, MsgId>>): Readonly<Record<K, string>> {
  const out = {} as Record<K, string>;
  for (const key of Object.keys(names) as K[]) Object.defineProperty(out, key, { enumerable: true, get: () => t(names[key]) });
  return out;
}

/** 表里的一行：name 读出来是当前语言，zh 留着原文给契约测试和排序用 */
export function withName<T extends object>(row: T & { zh: MsgId }): T & { zh: MsgId; readonly name: string } {
  return Object.defineProperty(row, 'name', { enumerable: true, get: () => t(row.zh) }) as T & { zh: MsgId; readonly name: string };
}

applyToDocument(current);
