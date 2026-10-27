// 搜索结果的名字高亮。语法见 DESIGN §5：只有「普通子串词」对应名字里的一段，
// 排除词、过滤词、通配符、路径词都不是，画高亮只会误导人。

const FILTER = /^(ext|size|type|file|dir):/i;

/** 查询里的普通子串词（小写、去重）；双引号括起来的带空格词算一个词，引号外的 | 是「或」的分隔 */
export function plainWords(query: string): string[] {
  const out = new Set<string>();
  for (const [, neg, quoted, bare] of query.matchAll(/(-?)"([^"]*)"|([^\s|]+)/g)) {
    const w = quoted ?? bare;
    if (!w || neg || (bare !== undefined && w.startsWith('-')) || FILTER.test(w) || /[*?\\]/.test(w)) continue;
    out.add(w.toLowerCase());
  }
  return [...out];
}

export interface Part {
  text: string;
  hit: boolean;
}

/** 按匹配区间把名字切成片段；重叠或相邻的区间合并 */
export function highlightParts(name: string, words: string[]): Part[] {
  const lower = name.toLowerCase();
  // 个别字符小写后长度会变（İ），下标就对不上了；宁可不高亮也别高亮错位
  if (!words.length || lower.length !== name.length) return [{ text: name, hit: false }];
  const spans: [number, number][] = [];
  for (const w of words) {
    for (let i = lower.indexOf(w); i >= 0; i = lower.indexOf(w, i + w.length)) spans.push([i, i + w.length]);
  }
  spans.sort((a, b) => a[0] - b[0]);
  const parts: Part[] = [];
  let at = 0;
  for (let i = 0; i < spans.length; ) {
    const [start] = spans[i];
    let end = spans[i][1];
    for (i++; i < spans.length && spans[i][0] <= end; i++) end = Math.max(end, spans[i][1]);
    if (start > at) parts.push({ text: name.slice(at, start), hit: false });
    parts.push({ text: name.slice(start, end), hit: true });
    at = end;
  }
  if (at < name.length) parts.push({ text: name.slice(at), hit: false });
  return parts.length ? parts : [{ text: name, hit: false }];
}
