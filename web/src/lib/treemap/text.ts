// 文字测量缓存。ctx.measureText 每次要几微秒，一帧里有几百个标签、每个还要二分找省略位置，吃不消；
// 所以按「字体 → 码点 → 宽度」缓存单个字符，整串宽度用求和估（忽略字距，只用来判断放不放得下）。

export interface Meter {
  width(font: string, text: string): number;
  /** 放得下原样返回；放不下就截断并补「…」；连一个字加「…」都放不下返回空串 */
  fit(font: string, text: string, max: number): string;
  /** 字体加载完成后宽度会变，旧缓存要清掉 */
  clear(): void;
}

export function createMeter(measureChar: (font: string, ch: string) => number): Meter {
  const fonts = new Map<string, Map<number, number>>();
  const table = (font: string): Map<number, number> => {
    let t = fonts.get(font);
    if (!t) {
      t = new Map();
      fonts.set(font, t);
    }
    return t;
  };
  const cw = (t: Map<number, number>, font: string, cp: number): number => {
    let w = t.get(cp);
    if (w === undefined) {
      w = measureChar(font, String.fromCodePoint(cp));
      t.set(cp, w);
    }
    return w;
  };
  return {
    width(font, text) {
      const t = table(font);
      let w = 0;
      for (let i = 0; i < text.length; i++) {
        const cp = text.codePointAt(i)!;
        if (cp > 0xffff) i++;
        w += cw(t, font, cp);
      }
      return w;
    },
    fit(font, text, max) {
      const t = table(font);
      const ell = cw(t, font, 0x2026);
      let w = 0;
      let cut = 0;
      for (let i = 0; i < text.length; ) {
        const cp = text.codePointAt(i)!;
        const len = cp > 0xffff ? 2 : 1;
        w += cw(t, font, cp);
        if (w + ell <= max) cut = i + len;
        i += len;
      }
      if (w <= max) return text;
      const head = text.slice(0, cut).trimEnd();
      return head ? head + '…' : '';
    },
    clear() {
      fonts.clear();
    },
  };
}
