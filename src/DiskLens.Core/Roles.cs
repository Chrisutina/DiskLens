namespace DiskLens.Core;

/// 数据角色（dise §7.1、§7.2）：这块数据在它的用途里扮演什么——程序本体、配置、包管理器缓存、存档……
/// 和主用途 U 一样一个节点一个，规则命中时由规则给出，没有更具体的证据时按扩展名推，再没有就是未知。
/// 编号、key、名称与 web/src/lib/roles.ts 一一对应；web/tests/purposes.test.ts 直接读这个文件比对，只改一边当场失败。
/// 编号必须小于 64：FileTree 把角色和用途证据塞在同一个字节里（低 6 位角色、高 2 位证据）。
public static class Roles
{
    public const byte Unknown = 0, Program = 1, Component = 2, Extension = 3, Game = 4, GameAsset = 5, Mod = 6, Save = 7,
        Config = 8, UserData = 9, BrowserProfile = 10, Source = 11, Dependency = 12, BuildOutput = 13, Runtime = 14,
        AppCache = 15, PackageCache = 16, UpdateCache = 17, Temp = 18, Log = 19, CrashDump = 20, ErrorReport = 21,
        Weights = 22, ModelData = 23, Document = 24, Image = 25, Video = 26, Audio = 27, Database = 28, Dataset = 29,
        Archive = 30, InstallImage = 31, VirtualDisk = 32, System = 33, Recovery = 34;

    public const int Count = 35;

    /// 下标就是角色编号。
    public static readonly (string Key, string Name)[] Table =
    [
        ("unknown", "未知"),
        ("program", "程序本体"),
        ("component", "组件"),
        ("extension", "扩展与插件"),
        ("game", "游戏本体"),
        ("gameAsset", "游戏资源"),
        ("mod", "模组"),
        ("save", "存档"),
        ("config", "配置"),
        ("userData", "用户数据"),
        ("browserProfile", "浏览器资料"),
        ("source", "源代码"),
        ("dependency", "项目依赖"),
        ("buildOutput", "构建产物"),
        ("runtime", "运行环境"),
        ("appCache", "应用缓存"),
        ("packageCache", "包管理器缓存"),
        ("updateCache", "更新缓存"),
        ("temp", "临时文件"),
        ("log", "日志"),
        ("crashDump", "崩溃转储"),
        ("errorReport", "错误报告"),
        ("weights", "模型权重"),
        ("modelData", "模型数据"),
        ("document", "文档"),
        ("image", "图片"),
        ("video", "视频"),
        ("audio", "音频"),
        ("database", "数据库"),
        ("dataset", "数据集"),
        ("archive", "压缩包"),
        ("installImage", "安装镜像"),
        ("virtualDisk", "虚拟磁盘"),
        ("system", "系统组件"),
        ("recovery", "恢复文件"),
    ];

    /// 下标就是 (int)Evidence。
    public static readonly string[] EvidenceNames = ["未识别", "推测", "已确认"];

    /// 下标就是 (int)OwnerKind。
    public static readonly string[] OwnerKindNames = ["应用", "游戏", "插件", "开发项目", "包管理器", "浏览器", "系统组件", "模型", "数据集", "运行环境"];

    // 上面三张表名字的英文、日文，下标一样；和界面的目录（web/src/lib/locales）逐字一致，web/tests/purposes.test.ts 比对
    public static readonly string[] NamesEn = ["Unknown", "Program", "Component", "Extensions & plug-ins", "Game", "Game assets", "Mods", "Saves", "Settings", "User data", "Browser profile", "Source code", "Project dependencies", "Build output", "Runtime", "App cache", "Package cache", "Update cache", "Temporary files", "Logs", "Crash dumps", "Error reports", "Model weights", "Model data", "Document", "Image", "Video", "Audio", "Database", "Dataset", "Archive", "Installer image", "Virtual disk", "System component", "Recovery files"];
    public static readonly string[] NamesJa = ["不明", "プログラム本体", "コンポーネント", "拡張機能・プラグイン", "ゲーム本体", "ゲームデータ", "MOD", "セーブデータ", "設定", "ユーザーデータ", "ブラウザーのプロファイル", "ソースコード", "プロジェクトの依存関係", "ビルド成果物", "ランタイム", "アプリのキャッシュ", "パッケージマネージャーのキャッシュ", "更新キャッシュ", "一時ファイル", "ログ", "クラッシュダンプ", "エラーレポート", "モデルの重み", "モデルデータ", "ドキュメント", "画像", "動画", "オーディオ", "データベース", "データセット", "圧縮ファイル", "インストールイメージ", "仮想ディスク", "システムコンポーネント", "回復ファイル"];
    public static readonly string[] EvidenceNamesEn = ["Unidentified", "Inferred", "Confirmed"];
    public static readonly string[] EvidenceNamesJa = ["未識別", "推定", "確認済み"];
    public static readonly string[] OwnerKindNamesEn = ["App", "Game", "Plug-in", "Dev project", "Package manager", "Browser", "System component", "Model", "Dataset", "Runtime"];
    public static readonly string[] OwnerKindNamesJa = ["アプリ", "ゲーム", "プラグイン", "開発プロジェクト", "パッケージマネージャー", "ブラウザー", "システムコンポーネント", "モデル", "データセット", "ランタイム"];

    /// 当前界面语言的名称
    public static string Name(int role) => L.T(Table[role].Name, NamesEn[role], NamesJa[role]);
    public static string EvidenceName(int e) => L.T(EvidenceNames[e], EvidenceNamesEn[e], EvidenceNamesJa[e]);
    public static string OwnerKindName(int k) => L.T(OwnerKindNames[k], OwnerKindNamesEn[k], OwnerKindNamesJa[k]);
}

/// 证据等级（dise §7.3）：只分三档，不出没有校准依据的百分比。JSON 里是 "unknown" / "inferred" / "confirmed"。
public enum Evidence : byte { Unknown, Inferred, Confirmed }

/// 所属对象的种类（dise §8.1「它属于哪个应用、插件、项目或系统组件」）。JSON 里是 camelCase 字符串。
public enum OwnerKind : byte { App, Game, Plugin, Project, PackageManager, Browser, System, Model, Dataset, Runtime }
