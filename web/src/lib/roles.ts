// 数据角色、证据等级、所属对象种类（dise §7.1–7.3）。
// 编号、key、名称与 src/DiskLens.Core/Roles.cs 一致（tests/purposes.test.ts 直接读 C# 源文件比对）；角色只是文字，不配颜色。

import type { Evidence, OwnerKind } from './types.ts';
import { localized, withName, type MsgId } from './i18n.ts';

export interface Role {
  r: number;
  key: string;
  /** 原文（和 C# 比对用）；显示读 name，跟着界面语言 */
  zh: MsgId;
  readonly name: string;
}

export const ROLE = {
  Unknown: 0, Program: 1, Component: 2, Extension: 3, Game: 4, GameAsset: 5, Mod: 6, Save: 7,
  Config: 8, UserData: 9, BrowserProfile: 10, Source: 11, Dependency: 12, BuildOutput: 13, Runtime: 14,
  AppCache: 15, PackageCache: 16, UpdateCache: 17, Temp: 18, Log: 19, CrashDump: 20, ErrorReport: 21,
  Weights: 22, ModelData: 23, Document: 24, Image: 25, Video: 26, Audio: 27, Database: 28, Dataset: 29,
  Archive: 30, InstallImage: 31, VirtualDisk: 32, System: 33, Recovery: 34,
} as const;

export const ROLE_COUNT = 35;

const TABLE: readonly Role[] = [
  withName({ r: 0, key: 'unknown', zh: '未知' }),
  withName({ r: 1, key: 'program', zh: '程序本体' }),
  withName({ r: 2, key: 'component', zh: '组件' }),
  withName({ r: 3, key: 'extension', zh: '扩展与插件' }),
  withName({ r: 4, key: 'game', zh: '游戏本体' }),
  withName({ r: 5, key: 'gameAsset', zh: '游戏资源' }),
  withName({ r: 6, key: 'mod', zh: '模组' }),
  withName({ r: 7, key: 'save', zh: '存档' }),
  withName({ r: 8, key: 'config', zh: '配置' }),
  withName({ r: 9, key: 'userData', zh: '用户数据' }),
  withName({ r: 10, key: 'browserProfile', zh: '浏览器资料' }),
  withName({ r: 11, key: 'source', zh: '源代码' }),
  withName({ r: 12, key: 'dependency', zh: '项目依赖' }),
  withName({ r: 13, key: 'buildOutput', zh: '构建产物' }),
  withName({ r: 14, key: 'runtime', zh: '运行环境' }),
  withName({ r: 15, key: 'appCache', zh: '应用缓存' }),
  withName({ r: 16, key: 'packageCache', zh: '包管理器缓存' }),
  withName({ r: 17, key: 'updateCache', zh: '更新缓存' }),
  withName({ r: 18, key: 'temp', zh: '临时文件' }),
  withName({ r: 19, key: 'log', zh: '日志' }),
  withName({ r: 20, key: 'crashDump', zh: '崩溃转储' }),
  withName({ r: 21, key: 'errorReport', zh: '错误报告' }),
  withName({ r: 22, key: 'weights', zh: '模型权重' }),
  withName({ r: 23, key: 'modelData', zh: '模型数据' }),
  withName({ r: 24, key: 'document', zh: '文档' }),
  withName({ r: 25, key: 'image', zh: '图片' }),
  withName({ r: 26, key: 'video', zh: '视频' }),
  withName({ r: 27, key: 'audio', zh: '音频' }),
  withName({ r: 28, key: 'database', zh: '数据库' }),
  withName({ r: 29, key: 'dataset', zh: '数据集' }),
  withName({ r: 30, key: 'archive', zh: '压缩包' }),
  withName({ r: 31, key: 'installImage', zh: '安装镜像' }),
  withName({ r: 32, key: 'virtualDisk', zh: '虚拟磁盘' }),
  withName({ r: 33, key: 'system', zh: '系统组件' }),
  withName({ r: 34, key: 'recovery', zh: '恢复文件' }),
];

export const role = (r: number): Role => TABLE[r] ?? TABLE[0];

/** 顺序同 C# 的 Evidence 枚举 */
export const EVIDENCE_NAMES: Readonly<Record<Evidence, string>> = localized({ unknown: '未识别', inferred: '推测', confirmed: '已确认' });

/** 顺序同 C# 的 OwnerKind 枚举 */
export const OWNER_KIND_NAMES: Readonly<Record<OwnerKind, string>> = localized({
  app: '应用', game: '游戏@@所属', plugin: '插件', project: '开发项目', packageManager: '包管理器',
  browser: '浏览器', system: '系统组件', model: '模型', dataset: '数据集', runtime: '运行环境',
});
