using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace DiskLens.Core;

/// 外壳操作：资源管理器、浏览器、属性窗口、提权重启、白名单命令、回收站。
/// 会真的开窗口或启动进程的方法都配了一个「只拼参数、不启动」的 internal 函数，自检只测后者。
public static class ShellOps
{
    /// 外壳命名空间（SHParseDisplayName 等）装不下 260 字符以上的路径。
    const int ShellMaxPath = 259;

    // ---------------- 资源管理器 ----------------

    /// 目录：打开它本身；文件：打开父目录并选中。用 SHOpenFolderAndSelectItems，别拼 explorer 参数
    /// （逗号、空格、中文在命令行里都会出事）。
    public static void OpenInExplorer(string path, bool isDirectory)
    {
        var target = PlanExplorer(path, isDirectory);
        OnSta(() =>
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(target.Folder, IntPtr.Zero, out var folder, 0, out _));
            IntPtr item = IntPtr.Zero, list = IntPtr.Zero;
            try
            {
                if (target.Select != null)
                {
                    Marshal.ThrowExceptionForHR(SHParseDisplayName(target.Select, IntPtr.Zero, out item, 0, out _));
                    list = Marshal.AllocCoTaskMem(IntPtr.Size);
                    Marshal.WriteIntPtr(list, ILFindLastID(item)); // 要的是相对父目录的那一段 ID
                }
                Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(folder, target.Select == null ? 0u : 1u, list, 0));
            }
            finally
            {
                Marshal.FreeCoTaskMem(folder);
                if (item != IntPtr.Zero) Marshal.FreeCoTaskMem(item);
                if (list != IntPtr.Zero) Marshal.FreeCoTaskMem(list);
            }
            return true;
        });
    }

    internal readonly record struct ExplorerTarget(string Folder, string? Select);

    /// 决定打开哪个文件夹、选中谁。目标已经不在了就退到最近还在的上级；
    /// 路径超过外壳的上限（R2 的「超长」）就退到装得下的最深一层，并把通往目标的下一级选中。
    internal static ExplorerTarget PlanExplorer(string path, bool isDirectory, Func<string, bool>? exists = null)
    {
        exists ??= p => NativeFs.Probe(p) != PathState.Missing;
        path = Protection.Normalize(path);
        string folder = isDirectory ? path : Path.GetDirectoryName(path) ?? path;
        string? select = isDirectory ? null : path;
        if (!exists(path))
        {
            folder = path;
            while (!exists(folder) && Path.GetDirectoryName(folder) is { } up) folder = up;
            select = null;
        }
        if (folder.Length <= ShellMaxPath && (select == null || select.Length <= ShellMaxPath)) return new(folder, select);

        string target = select ?? folder;
        string deepest = folder;
        while (deepest.Length > ShellMaxPath && Path.GetDirectoryName(deepest) is { } up) deepest = up;
        string next = target[deepest.Length..].TrimStart('\\').Split('\\')[0];
        string candidate = Path.Combine(deepest, next);
        return new(deepest, next.Length > 0 && candidate.Length <= ShellMaxPath ? candidate : null);
    }

    // ---------------- 浏览器 ----------------

    /// 只接受 http/https。进程已提权时经 explorer.exe 转交，让浏览器以普通权限启动（R3）。
    public static void OpenUrl(string url)
    {
        if (!TryNormalizeWebUrl(url, out var uri)) throw new ArgumentException(L.T("只允许打开 HTTP 或 HTTPS 地址", "Only HTTP or HTTPS addresses can be opened", "開けるのは HTTP または HTTPS のアドレスだけです"));
        Process.Start(BuildBrowserStart(uri))?.Dispose();
    }

    internal static bool TryNormalizeWebUrl(string? url, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        // 反斜杠和控制字符会让 .NET 与浏览器对同一个字符串解析出不同的主机，一律不要
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || url.Any(char.IsControl) || url.Contains('\\')) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("http" or "https") || string.IsNullOrEmpty(parsed.Host)) return false;
        uri = parsed;
        return true;
    }

    internal static ProcessStartInfo BuildBrowserStart(Uri uri)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(uri.AbsoluteUri);
        return start;
    }

    /// 顶栏的外部搜索（dise §3.6）：用户选了引擎、提交了词才打开，只发用户自己输入的这几个字，不带扫描记录或路径。
    /// engine: google | bing；超过 200 个字截断（不劈开代理对），控制字符拒绝。
    public static string BuildQueryUrl(string? engine, string? query)
    {
        string prefix = engine?.Trim().ToLowerInvariant() switch
        {
            "bing" => "https://www.bing.com/search?q=",
            "google" => "https://www.google.com/search?q=",
            _ => throw new ArgumentException(L.T("不支持的搜索引擎", "Unsupported search engine", "サポートされていない検索エンジンです")),
        };
        string q = (query ?? "").Trim();
        if (q.Length == 0) throw new ArgumentException(L.T("请输入要搜索的内容", "Enter something to search for", "検索する内容を入力してください"));
        if (q.Any(char.IsControl)) throw new ArgumentException(L.T("搜索内容里有控制字符", "The search text contains control characters", "検索内容に制御文字が含まれています"));
        if (q.Length > 200) q = q[..(char.IsHighSurrogate(q[199]) ? 199 : 200)];
        return prefix + Uri.EscapeDataString(q);
    }

    /// engine: bing | baidu | google。只发送名字（名字太泛时带上父目录名），绝不带完整路径和用户名。
    /// 调用方持有 tree.Sync。
    public static string BuildWebSearchUrl(FileTree tree, int node, string engine)
    {
        string prefix = engine?.Trim().ToLowerInvariant() switch
        {
            "bing" => "https://www.bing.com/search?q=",
            "baidu" => "https://www.baidu.com/s?wd=",
            "google" => "https://www.google.com/search?q=",
            _ => throw new ArgumentException(L.T("不支持的搜索引擎", "Unsupported search engine", "サポートされていない検索エンジンです"))
        };
        return prefix + Uri.EscapeDataString(WebQuery(tree, node));
    }

    // 单独搜这些名字得到的全是噪音，加上父目录名（「Chrome Cache」比「Cache」有用得多）
    static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cache", "caches", "temp", "tmp", "data", "logs", "log", "bin", "obj", "lib", "libs", "src", "source", "sources",
        "config", "configs", "conf", "settings", "default", "defaults", "index", "main", "readme", "license", "changelog",
        "test", "tests", "build", "dist", "out", "output", "release", "debug", "resources", "assets", "images", "img",
        "files", "backup", "backups", "old", "new", "misc", "common", "shared", "plugins", "extensions", "packages",
        "download", "downloads", "documents", "pictures", "videos", "music", "desktop", "storage", "db", "database",
        "update", "updates", "install", "setup", "app", "apps", "core", "include", "public", "static", "templates",
        "scripts", "tools", "utils", "modules", "vendor", "docs", "help", "locale", "locales", "themes", "fonts", "icons",
        "media", "content", "local", "roaming", "user", "system", "session", "sessions", "profile", "profiles", "state",
        "history", "crash", "crashes", "dump", "dumps", "report", "reports", "thumbnails", "x64", "x86", "arm64", "win32", "win64",
    };

    internal static string WebQuery(FileTree tree, int node)
    {
        TreeQueries.RequireNode(tree, node);
        string name = node == FileTree.RootId
            ? RootLabel(tree.RootPath) ?? throw new ArgumentException(L.T("卷根目录没有可搜索的名字，请选一个文件或文件夹", "A volume root has no name to search for; pick a file or folder", "ボリュームのルートには検索できる名前がありません。ファイルかフォルダーを選んでください"))
            : tree.GetName(node);
        // 名字本身就是用户名（用户配置文件夹）：不能发出去，换成一句通用的
        if (IsAccountName(name)) return L.T("Windows 用户配置文件夹", "Windows user profile folder", "Windows のユーザー プロファイル フォルダー");
        string? parent = ParentLabel(tree, node);
        string query = IsGeneric(name) && parent != null && !MentionsAccount(parent) ? $"{parent} {name}" : name;
        return query.Length > 200 ? query[..200] : query;
    }

    static bool IsGeneric(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        return stem.Length <= 2 || stem.All(char.IsDigit) || GenericNames.Contains(stem) || GenericNames.Contains(name);
    }

    static bool IsAccountName(string s) =>
        s.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase) || s.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    // 父目录名只要「带着」用户名就不用（如 Alexander-backup），短于三个字符的账户名不做包含判断，免得误伤
    static bool MentionsAccount(string s) =>
        IsAccountName(s) || Environment.UserName.Length >= 3 && s.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase)
        || Environment.MachineName.Length >= 3 && s.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    static string? RootLabel(string rootPath)
    {
        string trimmed = rootPath.TrimEnd('\\', '/');
        return trimmed.Length == 0 || Path.GetPathRoot(rootPath)!.TrimEnd('\\', '/').Equals(trimmed, StringComparison.OrdinalIgnoreCase)
            ? null : Path.GetFileName(trimmed);
    }

    static string? ParentLabel(FileTree tree, int node)
    {
        int p = tree.Parent(node);
        while (p >= 0 && tree.Has(p, NodeFlags.Virtual)) p = tree.Parent(p); // 虚拟分组目录不在真实路径里
        if (p < 0) return null;
        return p == FileTree.RootId ? RootLabel(tree.RootPath) : tree.GetName(p);
    }

    // ---------------- 属性、提权、命令、设置 ----------------

    /// 必须在 UI（STA）线程调用，而且线程得保持活着（属性窗口是异步弹出的）。
    public static void ShowProperties(IntPtr ownerHwnd, string path)
    {
        path = Protection.Normalize(path);
        if (path.Length > ShellMaxPath) throw new PathTooLongException(L.T("路径超过 259 个字符，系统的属性窗口打不开", "The path is longer than 259 characters, which the Properties window can't open", "パスが 259 文字を超えているため、プロパティ ウィンドウを開けません"));
        // 0x0C = SEE_MASK_INVOKEIDLIST；0x400 = SEE_MASK_FLAG_NO_UI：出错由我们自己报，别让系统另弹一个框
        var info = new ShellExecuteInfo { Size = Marshal.SizeOf<ShellExecuteInfo>(), Mask = 0x0000000c | 0x00000400,
            Owner = ownerHwnd, Verb = "properties", File = path, Show = 1 };
        if (!ShellExecuteExW(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// 以管理员身份启动自己；用户在 UAC 里点了取消返回 false。
    public static bool RelaunchElevated(string arguments)
    {
        try
        {
            Process.Start(BuildRelaunchStart(arguments))?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; } // ERROR_CANCELLED
    }

    internal static ProcessStartInfo BuildRelaunchStart(string arguments, string? processPath = null, string? entryDll = null)
    {
        processPath ??= Environment.ProcessPath ?? throw new InvalidOperationException(L.T("无法定位当前程序", "Can't locate the running program", "実行中のプログラムの場所が分かりません"));
        // 开发时可能是 dotnet.exe 加载 DiskLens.dll：光传参数的话新进程只会得到一个裸的 dotnet
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            entryDll ??= Environment.GetCommandLineArgs().FirstOrDefault();
            if (entryDll != null && entryDll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) arguments = $"\"{entryDll}\" {arguments}";
        }
        return new ProcessStartInfo(processPath, arguments) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
    }

    /// 在新的控制台窗口里运行命令（cmd /k 或 powershell -NoExit），需要时请求提权。命令只能是白名单里的原文。
    public static void RunInConsole(string command, bool elevated, bool powershell) =>
        Process.Start(BuildConsoleStart(command, elevated, powershell))?.Dispose();

    internal static ProcessStartInfo BuildConsoleStart(string command, bool elevated, bool powershell)
    {
        if (!AllowedCommands.TryGetValue(command, out bool needsPowerShell) || needsPowerShell != powershell)
            throw new ArgumentException(L.T("该命令不在清理操作白名单内", "This command isn't on the cleanup allowlist", "このコマンドはクリーンアップの許可リストにありません"));
        // 解释器用绝对路径，工作目录放在 Windows 目录：npm 这类命令靠 PATH 解析，cmd 还会先找当前目录，当前目录不能是用户可写的
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var start = new ProcessStartInfo(powershell ? Path.Combine(windows, @"System32\WindowsPowerShell\v1.0\powershell.exe") : Path.Combine(windows, @"System32\cmd.exe"))
            { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal, WorkingDirectory = windows };
        if (elevated) start.Verb = "runas";
        if (powershell) { start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NoExit"); start.ArgumentList.Add("-Command"); }
        else start.ArgumentList.Add("/k");
        start.ArgumentList.Add(command);
        return start;
    }

    internal static readonly IReadOnlyDictionary<string, bool> AllowedCommands = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["Delete-DeliveryOptimizationCache -Force"] = true, ["npm cache clean --force"] = false,
        ["pip cache purge"] = false, ["dotnet nuget locals http-cache --clear"] = false,
        ["yarn cache clean"] = false, ["uv cache clean"] = false, ["go clean -cache"] = false,
        ["pnpm store prune"] = false, ["powercfg /h off"] = false,
        ["Dism.exe /Online /Cleanup-Image /StartComponentCleanup"] = false
    };

    /// 允许打开的系统设置入口：ms-settings: 这类 URI，或系统目录里的系统程序名。清理脚本生成器用同一份校验，规则表里的入口由 CleanupChecks 对拍。
    internal static readonly IReadOnlySet<string> AllowedSettings = new HashSet<string>(StringComparer.Ordinal)
    {
        "ms-settings:storagesense", "SystemPropertiesPerformance.exe", "SystemPropertiesProtection.exe"
    };

    public static void OpenSettings(string target) => Process.Start(BuildSettingsStart(target))?.Dispose();

    internal static ProcessStartInfo BuildSettingsStart(string target)
    {
        if (!AllowedSettings.Contains(target)) throw new ArgumentException(L.T("该设置入口不在白名单内", "This settings page isn't on the allowlist", "この設定ページは許可リストにありません"));
        string file = target.StartsWith("ms-settings:", StringComparison.Ordinal) ? target : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), target);
        return new ProcessStartInfo(file) { UseShellExecute = true };
    }

    // ---------------- 回收站 ----------------

    /// 清空 driveRoot 这个卷的回收站（静默，不弹确认和进度）。本来就是空的返回 false。
    public static bool EmptyRecycleBin(string driveRoot)
    {
        string root = Protection.Normalize(driveRoot);
        if (!Protection.Same(root, Protection.Normalize(Path.GetPathRoot(root)!))) throw new ArgumentException(L.T("清空回收站必须指定卷根目录", "Emptying the Recycle Bin needs a volume root", "ごみ箱を空にするにはボリュームのルートを指定する必要があります"));
        if (QueryRecycleBin(root).Items == 0) return false; // 空回收站上 SHEmptyRecycleBin 会返回 E_UNEXPECTED，那不是错误
        int hr = SHEmptyRecycleBinW(IntPtr.Zero, root, 1 | 2 | 4); // NOCONFIRMATION | NOPROGRESSUI | NOSOUND
        if (hr < 0 && hr != unchecked((int)0x8000FFFF)) Marshal.ThrowExceptionForHR(hr);
        return true;
    }

    /// 当前用户在这个卷上的回收站里有多少条目、多少字节。只读查询。
    internal static (long Items, long Bytes) QueryRecycleBin(string driveRoot)
    {
        var info = new RecycleBinInfo { Size = (uint)Marshal.SizeOf<RecycleBinInfo>() };
        Marshal.ThrowExceptionForHR(SHQueryRecycleBinW(driveRoot, ref info));
        return (info.Items, info.Bytes);
    }

    /// IFileOperation 一次提交多个路径，静默、不弹系统对话框。STA 线程上执行。
    public static RecycleResult MoveToRecycleBin(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? new(true, null) : OnSta(() => Recycler.Move(paths));

    static T OnSta<T>(Func<T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return action();
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() => { try { result = action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShellExecuteInfo
    {
        public int Size; public uint Mask; public IntPtr Owner;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show; public IntPtr Instance; public IntPtr IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr ClassKey; public uint HotKey; public IntPtr Icon; public IntPtr Process;
    }

    // shellapi.h 里这个结构是 pack(1) 声明的，总共 20 字节
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct RecycleBinInfo { public uint Size; public long Bytes; public long Items; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr idList, uint attributes, out uint found);
    [DllImport("shell32.dll")] static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr items, uint flags);
    [DllImport("shell32.dll")] static extern IntPtr ILFindLastID(IntPtr idList);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ShellExecuteExW(ref ShellExecuteInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBinW(IntPtr owner, string root, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBinW(string root, ref RecycleBinInfo info);
}
