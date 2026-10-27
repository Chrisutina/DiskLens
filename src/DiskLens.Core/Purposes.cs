namespace DiskLens.Core;

/// 主用途 U：treemap「类别」模式按它着色，汇总也按它分。
/// 和扩展名类别 K 不是一回事：K 回答「这是什么文件」，U 回答「这一坨是拿来干嘛的」。
/// 编号、key、名称与 web/src/lib/purposes.ts 一一对应；web/tests/purposes.test.ts 直接读这个文件比对，只改一边的话测试当场翻脸。
public static class Purposes
{
    public const byte Unknown = 0, Apps = 1, Games = 2, AppData = 3, Dev = 4, Sdk = 5, Cache = 6, Logs = 7, Ai = 8,
        Media = 9, Data = 10, Archives = 11, System = 12, Unexplained = 13;

    public const int Count = 14;

    /// 下标就是 U。13 是虚拟状态：卷已用减去已统计的差额，不冒充系统文件。
    public static readonly (string Key, string Name)[] Table =
    [
        ("unknown", "未识别"),
        ("apps", "应用程序"),
        ("games", "游戏"),
        ("appdata", "应用数据"),
        ("dev", "开发项目"),
        ("sdk", "SDK 与运行环境"),
        ("cache", "缓存"),
        ("logs", "日志与诊断"),
        ("ai", "AI 与模型"),
        ("media", "文档与媒体"),
        ("data", "数据库与数据集"),
        ("archives", "归档与镜像"),
        ("system", "系统与恢复"),
        ("unexplained", "未解释空间"),
    ];

    /// 名称的英文、日文，下标同 Table；和界面的目录（web/src/lib/locales）逐字一致，web/tests/purposes.test.ts 比对
    public static readonly string[] NamesEn = ["Unidentified", "Apps", "Games", "App data", "Dev project", "SDKs & runtimes", "Cache", "Logs & diagnostics", "AI & models", "Documents & media", "Databases & datasets", "Archives & images", "System & recovery", "Unexplained space"];
    public static readonly string[] NamesJa = ["未識別", "アプリケーション", "ゲーム", "アプリのデータ", "開発プロジェクト", "SDK とランタイム", "キャッシュ", "ログと診断", "AI とモデル", "ドキュメントとメディア", "データベースとデータセット", "アーカイブとイメージ", "システムと回復", "説明できない領域"];

    /// 当前界面语言的名称
    public static string Name(int u) => L.T(Table[u].Name, NamesEn[u], NamesJa[u]);
}
