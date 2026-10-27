import { withName, type MsgId } from './i18n.ts';

// 主用途 U：资源地图「类别」模式的颜色来源，也是用途汇总的口径。
// 编号、key、名称与 src/DiskLens.Core/Purposes.cs 一致（tests/purposes.test.ts 直接读 C# 源文件比对）；颜色只从这里取，组件里别再冒出新色值。

export interface Purpose {
  u: number;
  key: string;
  /** 原文（和 C# 比对用）；显示读 name，跟着界面语言 */
  zh: MsgId;
  readonly name: string;
  /** 图例方块、圆点、详情标签：同色系里最深的一档 */
  dot: string;
  /** treemap 块填充：同色系的浅色 */
  fill: string;
  /** treemap 块描边：介于两者之间 */
  line: string;
}

export const PURPOSE = {
  Unknown: 0, Apps: 1, Games: 2, AppData: 3, Dev: 4, Sdk: 5, Cache: 6, Logs: 7, Ai: 8,
  Media: 9, Data: 10, Archives: 11, System: 12, Unexplained: 13,
} as const;

export const PURPOSE_COUNT = 14;

// 每个主用途一个色系；绿色留给「可放心清理」和勾选斜纹，用途色里不用。
// 未识别和未解释空间都是中性灰：未识别是实心浅灰，未解释空间在地图上另加灰斜纹、圆点更深。
const TABLE: readonly Purpose[] = [
  withName({ u: 0, key: 'unknown', zh: '未识别', dot: '#A1A1AA', fill: '#F2F2F3', line: '#D6D6DA' }),
  withName({ u: 1, key: 'apps', zh: '应用程序', dot: '#D9A21B', fill: '#FBF2D7', line: '#EED38E' }),
  withName({ u: 2, key: 'games', zh: '游戏', dot: '#CB4DC1', fill: '#F9E1F7', line: '#EDB6E8' }),
  withName({ u: 3, key: 'appdata', zh: '应用数据', dot: '#75A630', fill: '#ECF6DF', line: '#C7E0A3' }),
  withName({ u: 4, key: 'dev', zh: '开发项目', dot: '#7468F0', fill: '#E7E5FC', line: '#C2BCF6' }),
  withName({ u: 5, key: 'sdk', zh: 'SDK 与运行环境', dot: '#22A9C2', fill: '#DDF2F6', line: '#A9DCE6' }),
  withName({ u: 6, key: 'cache', zh: '缓存', dot: '#4F75F2', fill: '#E1E8FD', line: '#B6C7F8' }),
  withName({ u: 7, key: 'logs', zh: '日志与诊断', dot: '#149A86', fill: '#DAF4EE', line: '#9ED8CC' }),
  withName({ u: 8, key: 'ai', zh: 'AI 与模型', dot: '#E0558A', fill: '#FCE4EC', line: '#F2B6CB' }),
  withName({ u: 9, key: 'media', zh: '文档与媒体', dot: '#F07B3F', fill: '#FDEDE3', line: '#F5C9AE' }),
  withName({ u: 10, key: 'data', zh: '数据库与数据集', dot: '#7B3FB5', fill: '#EFE7F9', line: '#D0BAE8' }),
  withName({ u: 11, key: 'archives', zh: '归档与镜像', dot: '#946842', fill: '#EEE7DF', line: '#D3C2B1' }),
  withName({ u: 12, key: 'system', zh: '系统与恢复', dot: '#64748B', fill: '#E8EBF0', line: '#C6CDD8' }),
  withName({ u: 13, key: 'unexplained', zh: '未解释空间', dot: '#52525B', fill: '#F4F4F5', line: '#D4D4D8' }),
];

/** 图例顺序：按主用途编号，未识别和未解释空间垫底 */
export const PURPOSE_ORDER: readonly number[] = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 0, 13];

export const purpose = (u: number): Purpose => TABLE[u] ?? TABLE[0];

/** 「清理」着色模式与勾选斜纹的颜色 */
export const CLEANUP_COLORS = {
  safe: { fill: '#E7F6EC', line: '#4CAF6E', text: '#16A34A' },
  review: { fill: '#FDF0E3', line: '#F2B577', text: '#EA7A1A' },
  protected: { fill: '#F2F2F3', line: '#D6D6DA', text: '#71717A' },
  none: { fill: '#FAFAFA', line: '#E4E4E7', text: '#71717A' },
  hatch: '#86C99A',
  hatchLine: '#4CAF6E',
  unscannedHatch: '#D4D4D8',
} as const;

/**
 * 扫描进行中还没有树，只有按扩展名类别 K 累计的字节；用这个默认映射把它们画成用途色（DESIGN §4.7）。
 * 这只是按文件类型的估算，完成后的用途分类以树为准。「数据文件」里混着日志、缓存、数据库、游戏资源和模型，
 * 不猜，记成未识别——更不能整类算成 AI（dise §7.3）。
 * K：0 其他 1 文件夹 2 视频 3 图片 4 音频 5 文档 6 压缩包 7 代码 8 程序 9 系统 10 镜像 11 数据文件 12 未统计
 */
export function purposeOfCategory(k: number): number {
  switch (k) {
    case 2: case 3: case 4: case 5: return PURPOSE.Media;
    case 6: case 10: return PURPOSE.Archives;
    case 7: return PURPOSE.Dev;
    case 8: return PURPOSE.Apps;
    case 9: return PURPOSE.System;
    case 12: return PURPOSE.Unexplained;
    default: return PURPOSE.Unknown;
  }
}
