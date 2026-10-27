using System.Diagnostics;
using DiskLens.Core;

namespace DiskLens.Check;

// 用途分类（DESIGN §4.7）的自检：合成树 + 本机真实环境变量拼出来的路径。
// 期望值按设计文档独立再拼一遍，故意不复用被测代码里的解析——不然两边一起错就查不出来了，哼。
static class PurposeChecks
{
    const byte Unknown = Purposes.Unknown, Apps = Purposes.Apps, Games = Purposes.Games, AppData = Purposes.AppData, Dev = Purposes.Dev,
        Sdk = Purposes.Sdk, Cache = Purposes.Cache, Logs = Purposes.Logs, Ai = Purposes.Ai, Media = Purposes.Media, Data = Purposes.Data,
        Archives = Purposes.Archives, Sys = Purposes.System, Unexpl = Purposes.Unexplained;

    /// 断言信息里写名字，不写编号：「期望 游戏，实际 应用程序」一眼看懂。
    static readonly string[] Names = Purposes.Table.Select(t => t.Name).ToArray();
    static readonly string[] RoleNames = Roles.Table.Select(t => t.Name).ToArray();

    static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static readonly string Pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    static readonly string Pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    static readonly string Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    static readonly string SysDrive = Path.GetPathRoot(Environment.SystemDirectory)!;

    /// 所有野生场景都放在这个没有任何规则的目录下面。
    static readonly string Lab = Path.Combine(SysDrive, "DiskLensCheck");

    const long MB = 1L << 20;

    public static void Run(CheckContext ctx)
    {
        RealMachineRules(ctx);
        Priorities(ctx);
        RolesBeforeContainers(ctx);
        NameRules(ctx);
        ProjectMarkers(ctx);
        ExtensionFallback(ctx);
        Dominance(ctx);
        Intrinsic(ctx);
        DataRoles(ctx);
        Owners(ctx);
        Evidences(ctx);
        ScanRootIsFolder(ctx);
        FakeEnvironment(ctx);
        ShortTemp(ctx);
        Explanations(ctx);
        ConfigFiles(ctx);
        Contract(ctx);
        Scale(ctx);
        if (ctx.Flag("rules")) DumpRules(ctx);
        if (ctx.Option("scan") is { } path) ScanReport(ctx, path);
    }

    // ---------------- 真机体检（不参与断言）----------------
    // check purpose --rules：这台机器上的环境变量最后解析成了哪些路径规则，「为什么我的缓存没认出来」先看这个。
    // check purpose --scan <路径> [--depth 2] [--top 12]：只读枚举一个真实目录再分类，肉眼核对结果。

    static void DumpRules(CheckContext ctx)
    {
        var rules = PurposeRules.Build(PurposeEnv.Real, SysDrive);
        foreach (var (path, hit) in rules.Paths.OrderBy(p => p.Value.Rule).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            ctx.Info($"规则 {hit.Rule} {Names[hit.Purpose],-8} {RoleNames[hit.Role],-8} {path}");
        ctx.Info($"不能当项目根：{rules.NoProject.Count} 个目录，{rules.NoProjectKids.Count} 个连子目录一起（{string.Join("；", rules.NoProjectKids)}）");
    }

    static void ScanReport(CheckContext ctx, string path)
    {
        var tree = ctx.Time($"扫描 {path}", () => WalkScanner.Scan(path, new ScanCounters(), CancellationToken.None));
        tree.Seal();
        var sw = Stopwatch.StartNew();
        var result = PurposeClassifier.Classify(tree, CancellationToken.None);
        ctx.Info($"{tree.Count:N0} 个节点分类耗时 {sw.Elapsed.TotalMilliseconds:F0} ms");
        tree.SetClassification(result);
        var u = result.Purposes;
        int depth = int.TryParse(ctx.Option("depth"), out int d) ? d : 1, top = int.TryParse(ctx.Option("top"), out int t) ? t : 12;
        ctx.Info("用途汇总：" + string.Join("，", tree.PurposeTotals(Metric.Alloc).Select((b, i) => $"{Names[i]} {b / (double)(1L << 30):F2} GB")));
        void Show(int node, int level)
        {
            int shown = 0;
            foreach (int c in tree.LiveChildren(node))
            {
                if (shown++ >= top) break;
                var owner = tree.OwnerOf(c, out int root);
                string who = owner is null ? "" : $"  ← {Roles.OwnerKindNames[(int)owner.Kind]}「{owner.Name}」{Roles.EvidenceNames[(int)tree.OwnerEvidenceAt(root)]}";
                ctx.Info($"{new string(' ', level * 2)}{Names[u[c]],-8}{RoleNames[tree.Role(c)],-8}{tree.Alloc(c) / (double)(1L << 30),9:F2} GB  {tree.GetName(c)}{(tree.IsDir(c) ? "\\" : "")}{who}");
                if (level + 1 < depth && tree.IsDir(c)) Show(c, level + 1);
            }
        }
        Show(FileTree.RootId, 0);
    }

    // ---------------- 每条规则命中（本机真实环境） ----------------

    static void RealMachineRules(CheckContext ctx)
    {
        var s = new Scenario();
        var sys = SysDrive;

        s.Tree(Sys, "恢复数据", J(sys, "Windows.old"), J(sys, "$Recycle.Bin"), J(sys, "System Volume Information"), J(sys, "Recovery"),
            J(sys, "$WinREAgent"), J(sys, "Config.Msi"), J(sys, "$Windows.~BT"), J(sys, "$Windows.~WS"), J(sys, "$GetCurrent"), J(sys, "$SysReset"));
        s.Tree(Sys, "系统目录", J(sys, "Windows"));
        // DumpStack.log 自己就是位置规则的目标：扩展名 .log 不能把它改成日志
        foreach (var f in new[] { "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp", "DumpStack.log.old", "bootmgr", "BOOTNXT" })
            s.File(J(sys, f)!, Sys, "卷根系统文件", 8L << 30);
        s.File(J(sys, "Windows2", "x.dat")!, Unknown, "名字相近的不是系统目录");

        s.Tree(Logs, "日志与诊断目录", J(sys, "PerfLogs"), J(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Diagnosis"),
            J(Local, "CrashDumps"), J(Local, "Microsoft", "Windows", "WER"), J(sys, "Windows", "Logs"), J(sys, "Windows", "Minidump"),
            J(sys, "Windows", "LiveKernelReports"), J(sys, "Windows", "System32", "LogFiles"));

        // 临时目录、Windows 更新和传递优化、安装包缓存、缩略图缓存：明确位置的缓存，Windows 目录里的也算缓存
        s.Tree(Cache, "临时与更新缓存", J(Local, "Temp"), J(sys, "Windows", "Temp"), J(sys, "Windows", "SoftwareDistribution", "Download"),
            J(sys, "Windows", "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
            J(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache"), J(Local, "Microsoft", "Windows", "Explorer"));

        // 环境变量指向的位置优先，默认位置也要认——搬家前留在老地方的那份照样是缓存
        s.Tree(Cache, "包管理器缓存",
            Real("npm_config_cache"), J(Local, "npm-cache"), J(Roaming, "npm-cache"),
            J(Real("PNPM_HOME"), "store"), J(Local, "pnpm", "store"), J(Local, "pnpm-cache"),
            Real("YARN_CACHE_FOLDER"), J(Local, "Yarn", "Cache"),
            Real("PIP_CACHE_DIR"), J(Local, "pip", "Cache"),
            Real("UV_CACHE_DIR"), J(Local, "uv", "cache"),
            Real("NUGET_PACKAGES"), J(Profile, ".nuget", "packages"),
            Real("NUGET_HTTP_CACHE_PATH"), J(Local, "NuGet", "v3-cache"),
            J(Real("GRADLE_USER_HOME"), "caches"), J(Profile, ".gradle", "caches"),
            J(Profile, ".m2", "repository"),
            J(Real("CARGO_HOME"), "registry"), J(Profile, ".cargo", "registry"),
            J(Local, "go-build"), J(RealFirst("GOPATH"), "pkg", "mod"), J(Profile, "go", "pkg", "mod"),
            Real("PUPPETEER_CACHE_DIR"), J(Profile, ".cache", "puppeteer"),
            Real("PLAYWRIGHT_BROWSERS_PATH"), J(Local, "ms-playwright"),
            Real("ELECTRON_CACHE"), J(Local, "electron", "Cache"),
            J(Local, "node-gyp", "Cache"),
            J(Real("BUN_INSTALL"), "install", "cache"), J(Profile, ".bun", "install", "cache"),
            J(Local, "Composer"), J(Local, "Pub", "Cache"),
            RealFirst("CONDA_PKGS_DIRS"));

        s.Tree(Ai, "AI 模型目录",
            Real("OLLAMA_MODELS"), J(Profile, ".ollama", "models"),
            J(Profile, ".lmstudio", "models"), J(Profile, ".cache", "lm-studio", "models"));
        // Hugging Face 缓存里 hub 下的 models--* 仓库是模型缓存（主用途缓存、角色模型数据）、datasets--* 是数据集缓存（主用途缓存、角色数据集），根目录其他东西都不算
        string? hfHub = J(Profile, ".cache", "huggingface", "hub");
        s.File(J(hfHub, "models--org--name", "blobs", "x.bin")!, Cache, "Hugging Face 的 models--* 仓库是模型缓存");
        s.File(J(hfHub, "datasets--org--name", "blobs", "x.bin")!, Cache, "Hugging Face 的 datasets--* 仓库是数据集缓存");
        s.File(J(hfHub, "spaces--org--name", "x.bin")!, Unknown, "Hugging Face 的 spaces--* 不认");
        s.File(J(Profile, ".cache", "huggingface", "token.bin")!, Unknown, "Hugging Face 根下的杂项不算模型");
        s.File(J(Profile, ".lmstudio", "bin", "lms.exe")!, Unknown, "LM Studio 只有 models 目录算模型");

        s.Tree(Data, "数据集目录", J(Profile, ".cache", "huggingface", "datasets"), J(Profile, "tensorflow_datasets"), J(Profile, ".keras", "datasets"),
            J(Profile, "scikit_learn_data"), J(Profile, ".cache", "kagglehub", "datasets"), Real("HF_DATASETS_CACHE"), J(Real("HF_HOME"), "datasets"));

        s.Tree(Sdk, "SDK 与运行环境",
            J(Local, "Android", "Sdk"), J(Profile, ".android"), J(Profile, ".rustup"), Real("RUSTUP_HOME"), J(Profile, ".dotnet"),
            J(Pf, "dotnet"), J(Pf86, "Windows Kits"), J(Pf, "Microsoft Visual Studio"), J(Pf86, "Microsoft Visual Studio"),
            J(Profile, ".jdks"), J(Pf, "Java"), J(Pf, "Eclipse Adoptium"), J(Pf, "Unity", "Hub", "Editor"),
            J(Profile, "flutter"), J(Pf, "nodejs"), J(Local, "Programs", "Python"), J(Profile, "miniconda3"), J(Profile, "anaconda3"));

        s.Tree(Apps, "应用程序", Pf, Pf86, J(Local, "Programs"));

        // AppData 和 ProgramData：里面没被更具体的规则认领的部分是应用数据
        s.Tree(AppData, "应用数据", J(Profile, "AppData"), Local, Roaming, J(Profile, "AppData", "LocalLow"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), J(Roaming, "Mozilla", "Firefox", "Profiles"),
            J(Local, "Google", "Chrome", "User Data", "Default"));

        var shell = new List<string?>
        {
            Desktop, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            J(Profile, "Downloads"), J(Profile, "Documents"),
        };
        foreach (var od in new[] { Real("OneDrive"), Real("OneDriveConsumer"), Real("OneDriveCommercial") })
            foreach (var name in new[] { "Desktop", "Documents", "Pictures" }) shell.Add(J(od, name));
        s.Tree(Media, "用户文件夹", [.. shell]);

        s.File(J(Lab, "elsewhere", "x.dat")!, Unknown, "没命中任何规则的文件是未识别");
        s.Verify(ctx);
    }

    // ---------------- 优先级 ----------------

    static void Priorities(CheckContext ctx)
    {
        var s = new Scenario();
        var npm = J(Local, "npm-cache")!;
        s.File(J(npm, "_npx", "abc", "package.json")!, Cache, "npm 缓存里的 package.json 不变成代码");
        s.Dir(J(npm, "_npx", "abc")!, Cache, "npm 缓存里带 package.json 的目录");

        var app = J(Pf, "SomeElectronApp", "resources", "app")!;
        s.File(J(app, "package.json")!, Apps, "Program Files 里 Electron 应用的 package.json 不变成代码");
        s.Dir(app, Apps, "Electron 的 resources\\app 目录");
        s.File(J(Pf, "SomeElectronApp", "resources", "app", "node_modules", "x", "y.dat")!, Apps, "应用里的 node_modules");

        s.File(J(Desktop, "proj", "package.json")!, Dev, "桌面上的项目是代码");
        s.Dir(J(Desktop, "proj")!, Dev, "桌面上的项目目录");
        s.File(J(Desktop, "proj", "node_modules", "left-pad", "index.dat")!, Dev, "桌面项目的 node_modules 也是代码");
        s.File(J(Desktop, "loose.dat")!, Media, "桌面散文件是媒体");
        s.File(J(Desktop, "clip.mp4")!, Media, "桌面上的视频");
        s.File(J(Desktop, "package.json")!, Media, "桌面本身不当项目根（它的散文件仍是媒体）");
        s.Dir(Desktop, Media, "桌面目录自己");

        s.File(J(Pf, "Microsoft Visual Studio", "2022", "Community", "Common7", "IDE", "devenv.exe")!, Sdk, "Program Files\\Microsoft Visual Studio 是 SDK");
        s.File(J(Pf86, "Microsoft Visual Studio", "Installer", "setup.exe")!, Sdk, "Program Files (x86)\\Microsoft Visual Studio 是 SDK");
        s.File(J(Pf, "Microsoft Visual Studio", "2022", "Community", "proj", "package.json")!, Sdk, "SDK 里的项目标记不翻盘");
        s.File(J(Local, "Programs", "Python", "Python312", "Lib", "site.dat")!, Sdk, "Programs\\Python 是 SDK，不是应用");
        s.File(J(Local, "Programs", "Cool", "cool.exe")!, Apps, "Programs 下别的是应用");

        // 「应用与游戏」拆开：平台客户端是应用，它的游戏库是游戏；Program Files 里带 .build.info 的是 Battle.net 游戏
        var steam = J(Pf86, "Steam")!;
        s.File(J(steam, "steam.exe")!, Apps, "Steam 客户端本体是应用");
        s.Dir(J(steam, "steamapps")!, Games, "Steam 客户端里的 steamapps 是游戏库（游戏比程序目录优先）");
        s.File(J(steam, "steamapps", "common", "Portal 2", "portal2.exe")!, Games, "游戏库里的游戏本体");
        s.File(J(steam, "steamapps", "common", "Portal 2", "portal2", "pak01_dir.vpk")!, Games, "已识别游戏的资源仍是游戏");
        s.File(J(steam, "steamapps", "common", "Portal 2", "portal2", "media", "intro.mp4")!, Games, "游戏里的视频不按扩展名改成媒体");
        s.File(J(steam, "steamapps", "common", "Portal 2", "package.json")!, Games, "游戏里的项目标记不翻盘", 1);
        s.File(J(Pf86, "World of Warcraft", ".build.info")!, Games, "Program Files 里的 Battle.net 游戏", 1);
        s.File(J(Pf86, "World of Warcraft", "_retail_", "Wow.exe")!, Games, "Battle.net 游戏本体");
        s.File(J(Pf86, "Battle.net", "Battle.net.exe")!, Apps, "Battle.net 启动器本身是应用");

        s.File(J(SysDrive, "Windows", "Foo", "package.json")!, Sys, "系统目录里的项目标记不翻盘");
        s.File(J(SysDrive, "Windows", "System32", "big.safetensors")!, Sys, "系统目录里的大模型文件仍是系统", 200 * MB);

        s.File(J(Pf, "SomeApp", "models", "big.onnx")!, Ai, "应用目录里的大模型文件是 AI（AI 比应用优先）", 80 * MB);
        s.File(J(Pf, "SomeApp", "models", "tiny.onnx")!, Apps, "应用目录里的小 onnx 还是应用", 2 * MB);
        s.File(J(Desktop, "proj", "weights", "w.safetensors")!, Ai, "项目里的大模型文件是 AI（AI 比开发项目优先）", 300 * MB);
        s.File(J(Desktop, "dl.gguf")!, Ai, "桌面上的大模型文件是 AI", 4000 * MB);
        s.File(J(npm, "blob", "huge.pt")!, Cache, "缓存里的大模型文件仍是缓存（缓存比 AI 优先）", 200 * MB);
        s.Verify(ctx);
    }

    // ---------------- dise §7.3：具体数据角色 → 所属容器 → 扩展名兜底 ----------------

    static void RolesBeforeContainers(CheckContext ctx)
    {
        var s = new Scenario();
        var game = J(Pf86, "Steam", "steamapps", "common", "Elden Ring")!;
        s.File(J(game, "Game", "eldenring.exe")!, Games, "游戏本体");
        s.File(J(game, "Game", "Data0.bdt")!, Games, "游戏资源");
        s.File(J(game, "Game", "output.log")!, Logs, "游戏目录里的 .log 是日志与诊断（角色比所属游戏优先）");
        s.File(J(game, "Game", "crash.dmp")!, Logs, "游戏的崩溃转储");
        s.File(J(game, "Logs", "session.txt")!, Logs, "游戏的 Logs 目录里的文本也是日志");
        s.Dir(J(game, "Logs")!, Logs, "游戏的 Logs 目录自己");
        s.File(J(game, "Game", "movie", "intro.webm")!, Games, "游戏里的视频（类型兜底不翻盘）");
        s.File(J(game, "Game", "dlc.zip")!, Games, "游戏里的压缩包仍是游戏");
        s.File(J(Pf86, "Steam", "steamapps", "shadercache", "1245620", "fozpipelinesv6", "steamapp_pipeline_cache.foz")!, Cache, "游戏库里的着色器缓存是缓存");
        s.File(J(Pf86, "Steam", "logs", "content_log.txt")!, Logs, "Steam 客户端的 logs 目录");

        // 应用数据：配置、用户数据、浏览器资料；里面的缓存、日志、程序、SDK 各归各的
        var chrome = J(Local, "Google", "Chrome", "User Data", "Default")!;
        s.File(J(chrome, "History")!, AppData, "浏览器资料");
        s.File(J(chrome, "Favicons.db")!, AppData, "应用数据里的数据库仍是应用数据（类型兜底不翻盘）");
        s.File(J(chrome, "Cache", "Cache_Data", "f_000001")!, Cache, "浏览器缓存（Cache 目录比应用数据优先）");
        s.File(J(chrome, "Code Cache", "js", "index")!, Cache, "Code Cache");
        s.File(J(chrome, "Service Worker", "CacheStorage", "x")!, Cache, "Service Worker 的 CacheStorage");
        s.File(J(Roaming, "Code", "GPUCache", "data_1")!, Cache, "Electron 应用的 GPUCache");
        s.File(J(Roaming, "Code", "User", "settings.json")!, AppData, "应用配置");
        s.File(J(Roaming, "Code", "logs", "20261019", "main.log")!, Logs, "应用日志");
        s.File(J(Local, "CrashDumps", "app.exe.1234.dmp")!, Logs, "CrashDumps");
        s.File(J(Local, "Microsoft", "Windows", "WER", "ReportArchive", "r", "Report.wer")!, Logs, "Windows 错误报告");
        s.File(J(Local, "Temp", "setup.tmp")!, Cache, "临时目录（不是哪个应用的数据）");
        s.File(J(Local, "Temp", "install.log")!, Logs, "临时目录里的 .log 是日志");
        s.File(J(Local, "Microsoft", "Windows", "Explorer", "thumbcache_256.db")!, Cache, "缩略图缓存");
        s.File(J(Local, "SomeTool", "package.json")!, AppData, "应用数据比项目标记优先：应用自带的 package.json 不翻成开发项目", 1);
        s.File(J(Local, "SomeTool", "app.js")!, AppData, "…它的其他文件");
        s.File(J(Local, "Docker", "wsl", "disk", "ext4.vhdx")!, AppData, "应用数据里的虚拟磁盘仍是应用数据", 40L << 30);
        s.File(J(Local, "SomeTool", "model.onnx")!, Ai, "应用数据里的大模型文件是 AI（具体角色）", 80 * MB);
        s.File(J(Local, "npm-cache", "_logs", "debug-0.log")!, Logs, "缓存目录里的日志是日志");

        // Windows 目录：日志、转储、明确位置的缓存比「系统」优先；名字叫 Cache 的目录不行
        string win = J(SysDrive, "Windows")!;
        s.File(J(win, "Logs", "CBS", "CbsPersist_1.cab")!, Logs, "Windows\\Logs 里的压缩包也是日志");
        s.File(J(win, "System32", "winevt", "Logs", "System.evtx")!, Logs, "事件日志");
        s.File(J(win, "MEMORY.DMP")!, Logs, "内核转储", 2L << 30);
        s.File(J(win, "Panther", "setupact.log")!, Logs, "安装日志");
        s.File(J(win, "Temp", "x.tmp")!, Cache, "Windows\\Temp 是缓存");
        s.File(J(win, "SoftwareDistribution", "Download", "kb.cab")!, Cache, "Windows 更新缓存");
        s.File(J(win, "System32", "Cache", "x.dat")!, Sys, "Windows 里只是名字叫 Cache 的目录仍是系统");
        s.File(J(win, "System32", "kernel32.dll")!, Sys, "系统组件");

        // 恢复数据里什么都算恢复数据，日志也不例外
        s.File(J(SysDrive, "$Recycle.Bin", "S-1-5-21-1", "$R0A1B2C.log")!, Sys, "回收站里的日志仍是系统与恢复");
        s.File(J(SysDrive, "Windows.old", "Windows", "Logs", "x.log")!, Sys, "旧系统里的日志仍是系统与恢复");
        s.File(J(SysDrive, "$Recycle.Bin", "S-1-5-21-1", "$R1", "Cache", "x")!, Sys, "回收站里的 Cache 目录仍是系统与恢复");

        // 扩展名兜底只在没有容器规则时生效；用户文件夹比它还弱，只给没有类型证据的文件定用途
        string dl = J(Profile, "Downloads")!;
        s.File(J(dl, "Win11_24H2.iso")!, Archives, "下载目录里的安装镜像是归档与镜像", 5L << 30);
        s.File(J(dl, "photos.zip")!, Archives, "下载目录里的压缩包");
        s.File(J(dl, "notes.sqlite")!, Data, "下载目录里的数据库");
        s.File(J(dl, "report.pdf")!, Media, "下载目录里的文档");
        s.File(J(dl, "setup.exe")!, Media, "下载目录里没有类型证据的文件跟着用户文件夹走");
        s.File(J(dl, "debug.log")!, Logs, "下载目录里的日志");
        s.Dir(dl, Media, "下载目录自己");
        s.File(J(Pf, "SomeApp", "data.zip")!, Apps, "程序目录里的压缩包仍是应用程序");
        s.File(J(Desktop, "proj", "package.json")!, Dev, "桌面上的项目", 1);
        s.File(J(Desktop, "proj", "fixtures", "seed.sqlite")!, Dev, "项目里的数据库仍是开发项目");
        s.Verify(ctx);
    }

    // ---------------- 按名字认的规则（任意位置） ----------------

    static void NameRules(CheckContext ctx)
    {
        var s = new Scenario();
        string n = J(Lab, "names")!;
        s.File(J(n, "SteamLibrary", "steamapps", "common", "Game", "g.dat")!, Games, "SteamLibrary");
        s.File(J(n, "Games", "Steam", "steamapps", "common", "Game", "g.dat")!, Games, "路径里有 \\steamapps\\");
        s.Dir(J(n, "Games", "Steam", "steamapps")!, Games, "steamapps 目录自己");
        s.File(J(n, "Epic Games", "Fortnite", "f.dat")!, Games, "Epic Games");
        s.File(J(n, "XboxGames", "Halo", "h.dat")!, Games, "XboxGames");
        foreach (var lib in new[] { "GOG Games", "Riot Games", "EA Games", "Origin Games", "Rockstar Games", "Amazon Games", "ModifiableWindowsApps", "gog games" })
            s.File(J(n, lib, "Some Game", "x.dat")!, Games, $"游戏库 {lib}");
        s.File(J(n, "GOG Galaxy", "Games", "Witcher", "w.dat")!, Games, "GOG Galaxy 下的 Games");
        s.File(J(n, "Ubisoft", "Ubisoft Game Launcher", "games", "AC", "a.dat")!, Games, "Ubisoft Game Launcher 下的 games");
        s.File(J(n, "Ubisoft", "Ubisoft Game Launcher", "data", "c.dat")!, Unknown, "Ubisoft 启动器的其他目录不是游戏");
        s.File(J(n, "Misc", "Games", "x.dat")!, Unknown, "随便哪里的 Games 不算游戏库");
        s.File(J(n, "Misc", "WindowsApps", "Pkg", "p.dat")!, Apps, "任意位置的 WindowsApps 是应用");
        s.File(J(n, "steamapps2", "x.dat")!, Unknown, "名字只是相近的不算");
        s.File(J(n, "Blizzard", "Overwatch", ".build.info")!, Games, "Battle.net 游戏根下的 .build.info", 1);
        s.File(J(n, "Blizzard", "Overwatch", "Data", "casc", "d.dat")!, Games, "…整个游戏目录都是游戏");
        s.File(J(n, "Blizzard", "Overwatch", "package.json")!, Games, "游戏根里的 package.json 不翻成开发项目", 1);
        s.File(J(n, "Blizzard", "loose.dat")!, Unknown, "游戏根的上一层不受影响");

        s.File(J(n, "ComfyUI_windows_portable", "ComfyUI", "models", "checkpoints", "a.dat")!, Ai, "父目录名含 ComfyUI 的 models");
        s.Dir(J(n, "ComfyUI_windows_portable", "ComfyUI", "models")!, Ai, "models 目录自己");
        s.File(J(n, "stable-diffusion-webui", "models", "Lora", "l.dat")!, Ai, "stable-diffusion");
        s.File(J(n, "sd-webui-forge", "models", "v.dat")!, Ai, "sd-webui");
        s.File(J(n, "Fooocus_win64", "models", "f.dat")!, Ai, "Fooocus");
        s.File(J(n, "InvokeAI", "models", "i.dat")!, Ai, "InvokeAI");
        s.File(J(n, "comfyui-lite", "MODELS", "c.dat")!, Ai, "大小写不敏感");
        s.File(J(n, "Whatever", "models", "x.dat")!, Unknown, "父目录不带关键字的 models 不是 AI");
        s.File(J(n, "ComfyUI", "custom_nodes", "x.dat")!, Unknown, "ComfyUI 的其他子目录不是 AI");

        foreach (var name in new[] { "Logs", "logs", "LogFiles", "PerfLogs", "CrashDumps", "Crashpad", "CrashReports", "Crash Reports", "Minidump", "LiveKernelReports", "WER" })
        {
            s.Dir(J(n, "App-" + name, name)!, Logs, $"日志目录名 {name}");
            s.File(J(n, "App-" + name, name, "x.dat")!, Logs, $"日志目录 {name} 里的文件");
        }
        foreach (var name in new[] { "Cache", "cache", "Caches", "cache2", "Code Cache", "GPUCache", "ShaderCache", "shadercache", "GrShaderCache", "DawnCache",
                     "DawnGraphiteCache", "DawnWebGPUCache", "CacheStorage", "ScriptCache", "INetCache", "D3DSCache", "DXCache", "GLCache", "__pycache__" })
            s.File(J(n, "App-" + name, name, "x.dat")!, Cache, $"缓存目录名 {name}");
        // 只收专名：单数 log、点开头的 .cache（Hugging Face、LM Studio 都住在里面）、名字相近的都不算
        foreach (var name in new[] { "log", ".cache", "Cached", "cache_old", "LogsBackup", "Temp", "tmp" })
            s.File(J(n, "Near-" + name, name, "x.dat")!, Unknown, $"名字相近的 {name} 不按名字认");
        s.File(J(n, "caches", "pip", "x.dat")!, Cache, "叫 caches 的目录里的东西是缓存");
        s.File(J(n, "caches", "huggingface", "hub", "models--a--b", "w.dat")!, Cache, "没登记的 hub 里的 models-- 不算模型，跟着 caches 走");
        s.Verify(ctx);
    }

    // ---------------- 项目根检测 ----------------

    static void ProjectMarkers(CheckContext ctx)
    {
        var s = new Scenario();
        string p = J(Lab, "projects")!;
        foreach (var m in new[]
                 {
                     "package.json", "Cargo.toml", "go.mod", "pyproject.toml", "requirements.txt", "setup.py", "pom.xml", "build.gradle",
                     "build.gradle.kts", "CMakeLists.txt", "composer.json", "Gemfile", "deno.json", "pubspec.yaml", "App.sln", "App.slnx",
                     "App.csproj", "PACKAGE.JSON", "cargo.TOML",
                 })
        {
            string dir = J(p, "p-" + m)!;
            s.File(J(dir, m)!, Dev, $"标记 {m}", 1);
            s.Dir(dir, Dev, $"标记 {m} 的项目根");
            s.File(J(dir, "node_modules", "x", "y.dat")!, Dev, $"项目根 {m} 下的 node_modules");
            s.File(J(dir, "target", "debug", "z.png")!, Dev, $"项目根 {m} 下的 target 和图片");
        }
        s.File(J(p, "git-dir", ".git", "HEAD")!, Dev, ".git 目录", 1);
        s.File(J(p, "git-dir", "src", "a.dat")!, Dev, ".git 目录所在的项目");
        s.File(J(p, "git-file", ".git")!, Dev, ".git 是文件（worktree）", 1);
        s.File(J(p, "git-file", "src", "a.dat")!, Dev, ".git 文件所在的项目");
        s.File(J(p, "nested", "inner", "package.json")!, Dev, "嵌套的项目", 1);
        s.File(J(p, "nested", "inner", "deep", "d.dat")!, Dev, "嵌套项目的子孙");
        s.File(J(p, "no-marker", "package.json.bak")!, Unknown, "不完全等于标记的名字");
        s.File(J(p, "no-marker", "readme.dat")!, Unknown, "没有标记的目录不是项目");
        s.File(J(p, "marker-in-child", "sub", "package.json")!, Dev, "标记只管直接父目录", 1);
        s.File(J(p, "marker-in-child", "sibling.dat")!, Unknown, "兄弟目录里的标记不会让上一层变成项目");
        s.Verify(ctx);
    }

    // ---------------- 扩展名回退 ----------------

    static void ExtensionFallback(CheckContext ctx)
    {
        var s = new Scenario();
        string d = J(Lab, "ext")!;
        s.File(J(d, "clip.mp4")!, Media, "视频 → 媒体");
        s.File(J(d, "photo.JPG")!, Media, "图片 → 媒体");
        s.File(J(d, "song.flac")!, Media, "音频 → 媒体");
        s.File(J(d, "paper.pdf")!, Media, "文档 → 媒体");
        s.File(J(d, "raw.bin")!, Unknown, "数据文件不是媒体");
        s.File(J(d, "setup.exe")!, Unknown, "程序不是媒体");
        s.File(J(d, "noext")!, Unknown, "没有扩展名");

        // 日志与诊断（具体角色）、数据库与数据集、归档与镜像（类型兜底）
        foreach (var e in new[] { "log", "LOG", "etl", "evtx", "evt", "blg", "dmp", "mdmp", "hdmp", "wer" }) s.File(J(d, "x." + e)!, Logs, $".{e} → 日志与诊断");
        foreach (var e in new[] { "db", "sqlite", "sqlite3", "db3", "mdb", "accdb", "mdf", "ndf", "ldf", "ibd", "parquet", "arrow", "feather", "npy", "npz", "h5", "hdf5", "tfrecord" })
            s.File(J(d, "x." + e)!, Data, $".{e} → 数据库与数据集");
        foreach (var e in new[] { "zip", "7z", "rar", "tar", "gz", "cab", "iso", "img", "vhd", "vhdx", "vmdk", "wim", "esd", "dmg" })
            s.File(J(d, "x." + e)!, Archives, $".{e} → 归档与镜像");
        // 通用数据和不认识的格式不猜：不把数据文件统一归成 AI 或数据库
        foreach (var e in new[] { "dat", "bak", "tmp", "cache", "pak", "bundle", "idx", "pack" }) s.File(J(d, "x." + e)!, Unknown, $".{e} 不猜用途");

        s.File(J(d, "big.safetensors")!, Ai, "≥ 50 MB 的 safetensors → AI", 60 * MB);
        s.File(J(d, "small.safetensors")!, Unknown, "小 safetensors 不是 AI", 1 * MB);
        s.File(J(d, "edge.gguf")!, Ai, "恰好 50 MB 算", 50 * MB);
        s.File(J(d, "edge-1.gguf")!, Unknown, "差 1 字节不算", 50 * MB - 1);
        foreach (var e in new[] { "gguf", "onnx", "SAFETENSORS" }) s.File(J(d, "m." + e)!, Ai, $"大 .{e} 是 AI", 100 * MB);
        // .ckpt / .pt / .pth 也是普通检查点、数据甚至游戏存档的扩展名：不凭扩展名认成模型
        foreach (var e in new[] { "ckpt", "pt", "pth" }) s.File(J(d, "m." + e)!, Unknown, $"大 .{e} 不凭扩展名算 AI", 100 * MB);
        s.File(J(d, "huge.bin")!, Unknown, "再大的 bin 也不是 AI（不在扩展名名单里）", 2000 * MB);
        s.File(J(d, "huge.mp4")!, Media, "再大的视频还是媒体", 2000 * MB);
        s.Verify(ctx);
    }

    // ---------------- 主导用途 ----------------

    static void Dominance(CheckContext ctx)
    {
        var s = new Scenario();
        string d = J(Lab, "dom")!;
        s.File(J(d, "m60", "a.mp4")!, Media, "60% 媒体", 60 * MB);
        s.File(J(d, "m60", "b.dat")!, Unknown, "40% 其他", 40 * MB);
        s.Dir(J(d, "m60")!, Media, "恰好 60% 算主导");
        s.File(J(d, "m59", "a.mp4")!, Media, "59% 媒体", 59 * MB);
        s.File(J(d, "m59", "b.dat")!, Unknown, "41% 其他", 41 * MB);
        s.Dir(J(d, "m59")!, Unknown, "不足 60% 回到其他");
        s.File(J(d, "all", "a.dat")!, Unknown, "全是其他", 5 * MB);
        s.Dir(J(d, "all")!, Unknown, "全是其他的目录");
        s.Dir(J(d, "empty", "nothing")!, Unknown, "空目录没有主导用途");

        // 主导用途看的是叶子文件各自的用途，不管它们挂在哪层规则下面
        s.File(J(d, "mixed", "proj", "package.json")!, Dev, "项目标记", 1);
        s.File(J(d, "mixed", "proj", "big.dat")!, Dev, "项目里的大文件", 70 * MB);
        s.File(J(d, "mixed", "other.dat")!, Unknown, "项目外的小文件", 30 * MB);
        s.Dir(J(d, "mixed")!, Dev, "项目占 70% → 父目录取代码");

        s.File(J(d, "nested", "inner", "deeper", "a.mp4")!, Media, "很深的媒体", 90 * MB);
        s.File(J(d, "nested", "x.dat")!, Unknown, "10% 其他", 10 * MB);
        s.Dir(J(d, "nested")!, Media, "逐层汇总：中间层目录也取媒体");
        s.Dir(J(d, "nested", "inner")!, Media, "中间层只有媒体");

        s.File(J(d, "mixed-purposes", "a.dat")!, Unknown, "其他 35", 35 * MB);
        s.File(J(d, "mixed-purposes", "b.mp4")!, Media, "媒体 35", 35 * MB);
        s.File(J(d, "mixed-purposes", "c.gguf")!, Ai, "AI 80（扩展名回退至少要 50 MB）", 80 * MB);
        s.Dir(J(d, "mixed-purposes")!, Unknown, "谁都不到 60%");

        // 没有规则的目录里装满镜像和压缩包：按扩展名兜底的用途照样参与主导
        s.File(J(d, "Backups", "pc-2025.vhdx")!, Archives, "备份磁盘", 80 * MB);
        s.File(J(d, "Backups", "photos.7z")!, Archives, "备份压缩包", 15 * MB);
        s.File(J(d, "Backups", "notes.txt")!, Media, "说明文字", 5 * MB);
        s.Dir(J(d, "Backups")!, Archives, "归档占 95% → 目录取归档与镜像");
        s.Verify(ctx);

        // 用途只是继承来的目录：子树里另一种用途占 ≥ 60% 就按它展示，子项分类不变；目录自己有证据的不换
        var inh = new Scenario();
        string local = @"X:\Users\Alex\AppData\Local";
        inh.File(local + @"\NVIDIA\DXCache\a.bin", Cache, "显卡着色器缓存", 90 * MB);
        inh.File(local + @"\NVIDIA\settings.json", AppData, "旁边的配置仍是应用数据", 10 * MB);
        inh.Dir(local + @"\NVIDIA", Cache, "继承应用数据的目录九成是缓存 → 按缓存展示");
        inh.File(local + @"\Half\Cache\a.bin", Cache, "一半缓存", 50 * MB);
        inh.File(local + @"\Half\b.json", AppData, "一半配置", 50 * MB);
        inh.Dir(local + @"\Half", AppData, "谁都没过 60% → 沿用继承的应用数据（不会变成未识别）");
        inh.Dir(local + @"\Empty", AppData, "空的继承目录沿用继承的用途");
        inh.Dir(local, AppData, "AppData\\Local 自己有位置规则：子树七成是缓存也不换");
        inh.File(@"X:\Users\Alex\Downloads\isos\a.iso", Archives, "下载目录里的镜像", 100 * MB);
        inh.Dir(@"X:\Users\Alex\Downloads\isos", Archives, "下载目录里全是镜像的子目录按归档与镜像展示");
        inh.Dir(@"X:\Users\Alex\Downloads", Media, "下载目录自己仍是用户文件夹");
        inh.Verify(ctx, FakeEnv());
        string why = inh.Explain(local + @"\NVIDIA", FakeEnv());
        ctx.True(why.Contains("子树中「缓存」占已计入空间的至少 60%") && why.Contains("目录本身沿用「应用数据」"), $"换了展示色的目录要说明两层来历，实际「{why}」");

        // 已删除节点和硬链接副本不计入
        var t = new Scenario();
        string m = J(Lab, "del", "Mix")!;
        t.File(J(m, "a.mp4")!, Media, "将被删除的大视频", 70 * MB);
        t.File(J(m, "b.dat")!, Unknown, "留下的其他", 30 * MB);
        t.File(J(m, "c.mp4")!, Media, "一个很小的视频", 1 * MB);
        t.Dir(m, Media, "删除前 71/101 是媒体");
        t.Verify(ctx);
        var tree = t.TreeFor(m);
        tree.MarkDeleted(tree.FindByPath(J(m, "a.mp4")!));
        var after = PurposeClassifier.Classify(tree, CancellationToken.None).Purposes;
        ctx.Equal(Names[Unknown], Names[after[tree.FindByPath(m)]], "删掉大视频后 Mix 只剩 30/31 其他，不能把已删除的算进去");

        // 项目标记被删掉以后，那个目录就不再是项目根
        var u = new Scenario();
        string proj = J(Lab, "del", "Proj")!;
        u.File(J(proj, "package.json")!, Dev, "删除前它是项目", 1);
        u.File(J(proj, "a.dat")!, Dev, "删除前项目里的文件");
        u.Verify(ctx);
        var ut = u.TreeFor(proj);
        ut.MarkDeleted(ut.FindByPath(J(proj, "package.json")!));
        var gone = PurposeClassifier.Classify(ut, CancellationToken.None).Purposes;
        ctx.Equal(Names[Unknown], Names[gone[ut.FindByPath(J(proj, "a.dat")!)]], "package.json 删掉后项目里的文件不再是代码");
    }

    // ---------------- 虚拟叶子 / 元文件 ----------------

    static void Intrinsic(CheckContext ctx)
    {
        var s = new Scenario();
        string x = @"X:\";
        s.Dir(J(x, "$NTFS 元数据")!, Sys, "$NTFS 元数据（带元文件标记）", NodeFlags.Virtual | NodeFlags.Metafile);
        s.File(J(x, "$NTFS 元数据", "$MFT")!, Sys, "$MFT 元文件", 500 * MB, NodeFlags.Metafile);
        s.File(J(x, "$NTFS 元数据", "$Extend", "$UsnJrnl")!, Sys, "元数据目录里的普通条目继承系统", 10 * MB);
        s.Dir(J(x, "$孤立记录（路径无法核实）")!, Sys, "$孤立记录", NodeFlags.Virtual | NodeFlags.Metafile);
        s.File(J(x, "$孤立记录（路径无法核实）", "lost.mp4")!, Sys, "孤立记录里的视频也是系统（元数据最优先）", 5 * MB);
        s.File(J(x, "$孤立记录（路径无法核实）", "lost.log")!, Sys, "孤立记录里的日志也是系统（元数据比日志优先）", 5 * MB);
        s.File(J(x, "未统计（系统保留或无权访问）")!, Unexpl, "虚拟叶子是未解释空间，不冒充系统文件", 2000 * MB, NodeFlags.Virtual);
        s.File(J(x, "Users", "a", "stray-meta.dat")!, Sys, "任何位置的元文件都是系统", 1 * MB, NodeFlags.Metafile);
        s.File(J(x, "Users", "a", "ordinary.mp4")!, Media, "旁边的普通视频不受影响", 1 * MB);
        s.Verify(ctx, FakeEnv());

        // 只有名字、没有元文件标记的虚拟分组目录也按名字认；先建目录才能带上 Virtual 标记
        var t = new Scenario();
        t.Dir(J(x, "$NTFS 元数据")!, Sys, "只靠名字认出来的 $NTFS 元数据", NodeFlags.Virtual);
        t.File(J(x, "$NTFS 元数据", "f.dat")!, Sys, "它里面的文件", 1 * MB);
        t.Dir(J(x, "$孤立记录（路径无法核实）")!, Sys, "只靠名字认出来的 $孤立记录", NodeFlags.Virtual);
        t.File(J(x, "$孤立记录（路径无法核实）", "g.dat")!, Sys, "它里面的文件", 1 * MB);
        t.Verify(ctx, FakeEnv());
    }

    // ---------------- 数据角色（dise §7.2）：规则给出 → 容器里按目录名、扩展名细分 → 扩展名兜底 → 未知 ----------------

    static void DataRoles(CheckContext ctx)
    {
        var s = new Scenario();
        string me = @"X:\Users\Alex", local = me + @"\AppData\Local", roaming = me + @"\AppData\Roaming";

        // 缓存：规则本来就知道是谁的缓存
        s.File(local + @"\npm-cache\_cacache\content-v2\sha512\ab\cd", Cache, Roles.PackageCache, "npm 缓存");
        s.File(@"X:\Rc\pnpm\v3\files\00\abc", Cache, Roles.PackageCache, "pnpm 配置里的 store");
        s.File(local + @"\Temp\setup.tmp", Cache, Roles.Temp, "用户临时目录");
        s.File(@"X:\Windows\Temp\x.tmp", Cache, Roles.Temp, "系统临时目录");
        s.File(@"X:\Windows\SoftwareDistribution\Download\kb.cab", Cache, Roles.UpdateCache, "Windows 更新下载");
        s.File(@"X:\ProgramData\Package Cache\{guid}\setup.msi", Cache, Roles.UpdateCache, "安装包缓存");
        s.File(local + @"\Microsoft\Windows\Explorer\thumbcache_256.db", Cache, Roles.AppCache, "缩略图缓存");
        s.File(roaming + @"\Code\GPUCache\data_1", Cache, Roles.AppCache, "按名字认的应用缓存");

        // 日志与诊断：目录名和扩展名各自说明是日志、崩溃转储还是错误报告
        s.File(@"X:\Windows\Logs\CBS\CbsPersist_1.cab", Logs, Roles.Log, "Windows\\Logs 里的压缩包是日志");
        s.File(local + @"\CrashDumps\app.exe.1234.dmp", Logs, Roles.CrashDump, "CrashDumps");
        s.File(local + @"\CrashDumps\readme.txt", Logs, Roles.CrashDump, "CrashDumps 里的其他文件跟着目录");
        s.File(local + @"\Microsoft\Windows\WER\ReportArchive\r\Report.wer", Logs, Roles.ErrorReport, "Windows 错误报告");
        s.File(roaming + @"\Code\Crashpad\reports\a.txt", Logs, Roles.CrashDump, "Crashpad");
        s.File(@"X:\Lab\loose\debug.log", Logs, Roles.Log, ".log 扩展名");
        s.File(@"X:\Lab\loose\crash.mdmp", Logs, Roles.CrashDump, ".mdmp 扩展名");

        // 游戏：本体、资源、配置、模组、存档；游戏里的日志主用途是日志与诊断
        string game = @"X:\Games\SteamLibrary\steamapps\common\Elden Ring";
        s.File(game + @"\Game\eldenring.exe", Games, Roles.Game, "游戏的程序是游戏本体");
        s.File(game + @"\Game\Data0.bdt", Games, Roles.GameAsset, "游戏的其他文件是游戏资源");
        s.File(game + @"\Game\movie\intro.webm", Games, Roles.GameAsset, "游戏里的视频也是游戏资源");
        s.File(game + @"\Game\settings.ini", Games, Roles.Config, "游戏的配置文件");
        s.File(game + @"\Game\output.log", Logs, Roles.Log, "游戏日志：主用途日志与诊断");
        s.File(game + @"\Game\mods\coop\coop.dll", Games, Roles.Mod, "游戏的 mods 目录");
        s.File(@"X:\Games\SteamLibrary\steamapps\workshop\content\1245620\123\a.pak", Games, Roles.Mod, "Steam 创意工坊");
        s.File(me + @"\Saved Games\Elden Ring\ER0000.sl2", Games, Roles.Save, "「保存的游戏」");
        s.File(me + @"\OneDrive\文档\My Games\Skyrim\Saves\a.ess", Games, Roles.Save, "文档里的 My Games");
        s.File(me + @"\Documents\My Games\Skyrim\SkyrimPrefs.ini", Games, Roles.Save, "My Games 里的东西都按存档算（各游戏的存档和设置混放）");

        // 应用程序：本体、组件、扩展与插件、配置
        s.File(@"X:\Program Files\App\app.exe", Apps, Roles.Program, "程序目录");
        s.File(@"X:\Program Files\App\data\strings.json", Apps, Roles.Program, "程序目录里的其他文件");
        s.File(@"X:\Program Files\App\app.ini", Apps, Roles.Config, "程序目录里的配置文件");
        s.File(@"X:\Program Files\App\plugins\p.dll", Apps, Roles.Extension, "程序的 plugins 目录");
        s.File(@"X:\Program Files\Common Files\System\x.dll", Apps, Roles.Component, "Common Files 是共用组件");
        s.File(me + @"\.vscode\extensions\ms-python.python-2024.2.1\package.json", Apps, Roles.Extension, "VS Code 扩展目录（里面的 package.json 不翻成项目）", 1);
        s.File(me + @"\.vscode\extensions\ms-python.python-2024.2.1\out\x.js", Apps, Roles.Extension, "扩展里的 out 不是构建产物");

        // 应用数据：用户数据、配置、浏览器资料（里面的扩展、缓存各归各的）
        s.File(roaming + @"\Code\User\settings.json", AppData, Roles.UserData, "应用数据里的 .json 不当配置猜");
        s.File(roaming + @"\Tool\tool.ini", AppData, Roles.Config, "应用数据里的配置文件");
        s.File(me + @"\AppData\LocalLow\Unity\x.dat", AppData, Roles.UserData, "LocalLow");
        string chrome = local + @"\Google\Chrome\User Data";
        s.File(chrome + @"\Default\History", AppData, Roles.BrowserProfile, "Chrome 的用户资料");
        s.File(chrome + @"\Default\Extensions\abc\1.0_0\m.js", AppData, Roles.Extension, "浏览器扩展");
        s.File(chrome + @"\Default\Cache\Cache_Data\f_000001", Cache, Roles.AppCache, "浏览器缓存");
        s.File(roaming + @"\Mozilla\Firefox\Profiles\p.default\places.sqlite", AppData, Roles.BrowserProfile, "Firefox 的用户资料");

        // 开发项目：源代码、项目依赖、构建产物；依赖里的 package.json 不翻回源代码
        string proj = me + @"\code\web";
        s.File(proj + @"\package.json", Dev, Roles.Source, "项目标记", 1);
        s.File(proj + @"\src\a.ts", Dev, Roles.Source, "源代码");
        s.File(proj + @"\fixtures\seed.sqlite", Dev, Roles.Source, "项目里的数据库也算项目文件");
        s.File(proj + @"\node_modules\left-pad\package.json", Dev, Roles.Dependency, "node_modules 里的包", 1);
        s.File(proj + @"\node_modules\left-pad\lib\index.js", Dev, Roles.Dependency, "node_modules 里的包的文件");
        s.Role(proj + @"\node_modules\left-pad", Roles.Dependency, "带 package.json 的依赖目录");
        s.File(proj + @"\.venv\Lib\site.py", Dev, Roles.Dependency, ".venv");
        s.File(proj + @"\dist\app.js", Dev, Roles.BuildOutput, "dist");
        s.File(proj + @"\packages\ui\package.json", Dev, Roles.Source, "monorepo 里的子包", 1);
        s.File(proj + @"\packages\ui\src\b.ts", Dev, Roles.Source, "子包的源代码");
        string rs = me + @"\code\tauri";
        s.File(rs + @"\Cargo.toml", Dev, Roles.Source, "Rust 项目", 1);
        s.File(rs + @"\target\debug\app.exe", Dev, Roles.BuildOutput, "target");
        s.File(rs + @"\cmake-build-debug\x.o", Dev, Roles.BuildOutput, "cmake-build-*");
        s.File(rs + @"\logs\x.txt", Logs, Roles.Log, "项目里的 logs 目录是日志");

        // SDK、AI、数据集、系统、恢复
        s.File(me + @"\.rustup\toolchains\stable\bin\rustc.exe", Sdk, Roles.Runtime, "运行环境");
        s.File(me + @"\.ollama\models\blobs\sha256-1", Ai, Roles.Weights, "Ollama 的模型");
        s.File(@"X:\Lab\ComfyUI\models\checkpoints\a.safetensors", Ai, Roles.Weights, "ComfyUI 的 models", 200 * MB);
        s.File(@"X:\Lab\loose\m.gguf", Ai, Roles.Weights, "大模型格式文件", 60 * MB);
        // dise §7.3：明确的模型缓存主归缓存、标注模型数据；里面再大的模型文件也不被扩展名拉回 AI。Ollama、LM Studio 的仍是 AI 与模型
        // 数据集仓库同理（2026-10-20 用户定）：主归缓存、标数据集
        s.File(me + @"\.cache\huggingface\hub\datasets--org--set\blobs\x", Cache, Roles.Dataset, "Hugging Face 数据集仓库：缓存·数据集");
        s.File(me + @"\.cache\huggingface\hub\datasets--org--set\snapshots\abc\train.safetensors", Cache, Roles.Dataset, "数据集仓库里的大模型格式文件仍是缓存·数据集", 4000 * MB);
        s.Role(me + @"\.cache\huggingface\hub\datasets--org--set", Roles.Dataset, "数据集仓库目录");
        s.File(me + @"\.cache\huggingface\hub\models--org--name\blobs\sha", Cache, Roles.ModelData, "Hugging Face 模型仓库：缓存·模型数据");
        s.File(me + @"\.cache\huggingface\hub\models--org--name\snapshots\abc\model.safetensors", Cache, Roles.ModelData, "模型仓库里的大权重文件仍是缓存·模型数据", 4000 * MB);
        s.Role(me + @"\.cache\huggingface\hub\models--org--name", Roles.ModelData, "模型仓库目录");
        s.File(me + @"\.cache\huggingface\hub\models--org--name\.no_exist\abc\debug.log", Logs, Roles.Log, "模型仓库里的日志仍按日志算");
        s.File(me + @"\.lmstudio\models\org\repo\q4.gguf", Ai, Roles.Weights, "LM Studio 的模型仍是 AI 与模型·模型权重", 4000 * MB);
        s.File(me + @"\tensorflow_datasets\mnist\x.tfrecord", Data, Roles.Dataset, "数据集目录");
        s.File(@"X:\Windows\System32\kernel32.dll", Sys, Roles.System, "系统组件");
        s.File(@"X:\pagefile.sys", Sys, Roles.System, "卷根系统文件", 8L << 30);
        s.File(@"X:\DumpStack.log", Sys, Roles.System, "卷根 DumpStack.log 是系统组件，不因扩展名变成日志");
        s.File(@"X:\$Recycle.Bin\S-1\$R1.mp4", Sys, Roles.Recovery, "回收站里的东西是恢复文件");

        // 扩展名兜底：没有任何容器时文件形式就是角色
        string lab = @"X:\Lab\loose";
        s.File(lab + @"\a.mp4", Media, Roles.Video, "视频");
        s.File(lab + @"\a.jpg", Media, Roles.Image, "图片");
        s.File(lab + @"\a.flac", Media, Roles.Audio, "音频");
        s.File(lab + @"\a.pdf", Media, Roles.Document, "文档");
        s.File(lab + @"\a.zip", Archives, Roles.Archive, "压缩包");
        s.File(lab + @"\a.iso", Archives, Roles.InstallImage, "安装镜像");
        s.File(lab + @"\a.vhdx", Archives, Roles.VirtualDisk, "虚拟磁盘");
        s.File(lab + @"\a.sqlite", Data, Roles.Database, "数据库");
        s.File(lab + @"\a.parquet", Data, Roles.Dataset, "列式数据是数据集");
        s.File(@"X:\Data\Dl\photo.png", Media, Roles.Image, "下载目录里的图片");
        s.File(@"X:\Data\Dl\setup.exe", Media, Roles.Unknown, "下载目录里没有类型证据的文件：位置不说明角色");
        s.File(lab + @"\x.dat", Unknown, Roles.Unknown, "通用数据：未知");
        s.File(lab + @"\x.ini", Unknown, Roles.Unknown, "容器外的 .ini 不凭扩展名猜成谁的配置");

        // 元数据、虚拟叶子
        s.Dir(@"X:\$NTFS 元数据", Sys, "元数据分组", NodeFlags.Virtual | NodeFlags.Metafile);
        s.Role(@"X:\$NTFS 元数据", Roles.System, "元数据分组");
        s.File(@"X:\$NTFS 元数据\$MFT", Sys, "元文件", 500 * MB, NodeFlags.Metafile);
        s.Role(@"X:\$NTFS 元数据\$MFT", Roles.System, "元文件");
        s.File(@"X:\未统计（系统保留或无权访问）", Unexpl, "未解释空间", 1 * MB, NodeFlags.Virtual);
        s.Role(@"X:\未统计（系统保留或无权访问）", Roles.Unknown, "未解释空间没有角色");
        s.Verify(ctx, FakeEnv());
    }

    // ---------------- 所属对象（dise §8.1）：有依据才认，猜不准就不认；同一对象的不同位置能并就并 ----------------

    static void Owners(CheckContext ctx)
    {
        const Evidence Yes = Evidence.Confirmed, Guess = Evidence.Inferred;
        var s = new Scenario();
        string me = @"X:\Users\Alex", local = me + @"\AppData\Local", roaming = me + @"\AppData\Roaming";
        void F(string path, long size = MB) => s.Node(path, size);

        // 位置规则自带：包管理器、Windows、回收站、浏览器、运行环境、Ollama
        F(local + @"\npm-cache\_cacache\content-v2\x");
        s.Owner(local + @"\npm-cache\_cacache\content-v2\x", OwnerKind.PackageManager, "npm", Yes, "npm 缓存", local + @"\npm-cache");
        F(@"X:\Rc\pnpm\v3\x");
        s.Owner(@"X:\Rc\pnpm\v3\x", OwnerKind.PackageManager, "pnpm", Yes, "pnpm 的 store");
        F(local + @"\Temp\setup.tmp");
        s.NoOwner(local + @"\Temp\setup.tmp", "临时目录谁的都不是");
        F(@"X:\Windows\System32\kernel32.dll");
        s.Owner(@"X:\Windows\System32\kernel32.dll", OwnerKind.System, "Windows", Yes, "Windows 目录", @"X:\Windows");
        F(@"X:\pagefile.sys", 8L << 30);
        s.Owner(@"X:\pagefile.sys", OwnerKind.System, "Windows", Yes, "卷根的系统文件", @"X:\pagefile.sys");
        F(@"X:\$Recycle.Bin\S-1\$R1.mp4");
        s.Owner(@"X:\$Recycle.Bin\S-1\$R1.mp4", OwnerKind.System, "回收站", Yes, "回收站里的东西归回收站");
        F(@"X:\ProgramData\Package Cache\{guid}\setup.msi");
        s.NoOwner(@"X:\ProgramData\Package Cache\{guid}\setup.msi", "安装包缓存不硬塞给谁");
        F(@"X:\ProgramData\Microsoft\Diagnosis\ETLLogs\a.etl");
        s.Owner(@"X:\ProgramData\Microsoft\Diagnosis\ETLLogs\a.etl", OwnerKind.System, "Windows", Yes, "诊断数据归 Windows");
        F(local + @"\Google\Chrome\User Data\Default\History");
        s.Owner(local + @"\Google\Chrome\User Data\Default\History", OwnerKind.Browser, "Google Chrome", Yes, "Chrome 的用户资料", local + @"\Google\Chrome\User Data");
        F(local + @"\Google\Chrome\User Data\Default\Cache\Cache_Data\f_1");
        s.Owner(local + @"\Google\Chrome\User Data\Default\Cache\Cache_Data\f_1", OwnerKind.Browser, "Google Chrome", Yes, "浏览器缓存仍归浏览器");
        F(roaming + @"\Mozilla\Firefox\Profiles\p\places.sqlite");
        s.Owner(roaming + @"\Mozilla\Firefox\Profiles\p\places.sqlite", OwnerKind.Browser, "Firefox", Yes, "Firefox 的用户资料（Roaming）");
        F(local + @"\Mozilla\Firefox\Profiles\p\cache2\entries\x");
        s.Owner(local + @"\Mozilla\Firefox\Profiles\p\cache2\entries\x", OwnerKind.Browser, "Firefox", Yes, "Firefox 的缓存（Local）并成同一个对象");
        F(me + @"\.rustup\toolchains\stable\bin\rustc.exe");
        s.Owner(me + @"\.rustup\toolchains\stable\bin\rustc.exe", OwnerKind.Runtime, "Rust", Yes, "运行环境");
        F(@"X:\Program Files\dotnet\dotnet.exe");
        s.Owner(@"X:\Program Files\dotnet\dotnet.exe", OwnerKind.Runtime, ".NET", Yes, "Program Files 里的 SDK 按规则认，不当普通应用");
        F(me + @"\.ollama\models\blobs\sha256-1");
        s.Owner(me + @"\.ollama\models\blobs\sha256-1", OwnerKind.App, "Ollama", Yes, "Ollama 的模型目录");

        // 游戏：游戏库里的游戏文件夹、Battle.net 的安装信息；游戏日志的所属对象仍是游戏
        string game = @"X:\Games\SteamLibrary\steamapps\common\Elden Ring";
        F(game + @"\Game\output.log");
        s.Owner(game + @"\Game\output.log", OwnerKind.Game, "Elden Ring", Yes, "游戏日志归游戏", game);
        F(@"X:\Games\SteamLibrary\steamapps\shadercache\1245620\x.foz");
        s.NoOwner(@"X:\Games\SteamLibrary\steamapps\shadercache\1245620\x.foz", "只有编号的着色器缓存猜不出是哪个游戏");
        F(@"X:\Games\Epic Games\Fortnite\FortniteGame\a.pak");
        s.Owner(@"X:\Games\Epic Games\Fortnite\FortniteGame\a.pak", OwnerKind.Game, "Fortnite", Yes, "Epic 游戏库");
        F(@"X:\Games\Epic Games\Launcher\Engine\x.dll");
        s.NoOwner(@"X:\Games\Epic Games\Launcher\Engine\x.dll", "游戏库里的启动器不是游戏");
        F(@"X:\Program Files (x86)\World of Warcraft\.build.info", 1);
        F(@"X:\Program Files (x86)\World of Warcraft\_retail_\Wow.exe");
        s.Owner(@"X:\Program Files (x86)\World of Warcraft\_retail_\Wow.exe", OwnerKind.Game, "World of Warcraft", Yes, "Battle.net 游戏的安装信息比程序目录优先");
        F(me + @"\Saved Games\Elden Ring\ER0000.sl2");
        s.Owner(me + @"\Saved Games\Elden Ring\ER0000.sl2", OwnerKind.Game, "Elden Ring", Guess, "「保存的游戏」按目录名推测，并到同名游戏上");

        // 程序目录：Program Files\应用、Program Files\厂商\应用、WindowsApps 的包；Steam 客户端和它的游戏
        F(@"X:\Program Files\App\app.exe");
        s.Owner(@"X:\Program Files\App\app.exe", OwnerKind.App, "App", Guess, "Program Files 下的目录名", @"X:\Program Files\App");
        F(@"X:\Program Files\Adobe\Adobe Photoshop 2026\Photoshop.exe");
        F(@"X:\Program Files\Adobe\Adobe Photoshop 2026\Required\x.dll");
        s.Owner(@"X:\Program Files\Adobe\Adobe Photoshop 2026\Required\x.dll", OwnerKind.App, "Adobe Photoshop 2026", Guess, "厂商目录下直接带 .exe 的是应用");
        F(@"X:\Program Files\Adobe\Common\x.dll");
        s.Owner(@"X:\Program Files\Adobe\Common\x.dll", OwnerKind.App, "Adobe", Guess, "厂商目录里的其他东西归厂商");
        F(@"X:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE");
        s.Owner(@"X:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", OwnerKind.App, "Microsoft Office", Guess, ".exe 在更深处的不算厂商目录");
        F(@"X:\Program Files\Common Files\System\x.dll");
        s.NoOwner(@"X:\Program Files\Common Files\System\x.dll", "Common Files 是共用组件，不当成一个应用");
        F(@"X:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21.2361.0_x64__8wekyb3d8bbwe\wt.exe");
        s.Owner(@"X:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21.2361.0_x64__8wekyb3d8bbwe\wt.exe", OwnerKind.App, "Microsoft.WindowsTerminal", Yes, "Windows 应用包名");
        F(local + @"\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json");
        s.Owner(local + @"\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json", OwnerKind.App, "Microsoft.WindowsTerminal", Yes, "应用包的数据按包名并到同一个应用");
        F(@"X:\Program Files (x86)\Steam\steam.exe");
        s.Owner(@"X:\Program Files (x86)\Steam\steam.exe", OwnerKind.App, "Steam", Guess, "Steam 客户端");
        F(@"X:\Program Files (x86)\Steam\steamapps\common\Portal 2\portal2.exe");
        s.Owner(@"X:\Program Files (x86)\Steam\steamapps\common\Portal 2\portal2.exe", OwnerKind.Game, "Portal 2", Yes, "Steam 客户端里的游戏库");
        F(@"X:\Program Files (x86)\Steam\steamapps\shadercache\620\x.foz");
        s.Owner(@"X:\Program Files (x86)\Steam\steamapps\shadercache\620\x.foz", OwnerKind.App, "Steam", Guess, "认不出游戏的着色器缓存落到最近的 Steam");
        F(local + @"\Programs\Microsoft VS Code\Code.exe");
        s.Owner(local + @"\Programs\Microsoft VS Code\Code.exe", OwnerKind.App, "Microsoft VS Code", Guess, "用户级 Programs");
        F(me + @"\.vscode\extensions\ms-python.python-2024.2.1\package.json", 1);
        s.Owner(me + @"\.vscode\extensions\ms-python.python-2024.2.1\package.json", OwnerKind.Plugin, "ms-python.python", Yes, "VS Code 扩展去掉版本号，里面的 package.json 不另立项目");

        // 应用数据：AppData、ProgramData 下的目录名（推测）；按名字认的日志、缓存目录不当应用
        F(roaming + @"\Code\User\settings.json");
        s.Owner(roaming + @"\Code\User\settings.json", OwnerKind.App, "Code", Guess, "AppData 下的目录名", roaming + @"\Code");
        F(local + @"\SomeTool\package.json", 1);
        F(local + @"\SomeTool\lib\a.js");
        s.Owner(local + @"\SomeTool\lib\a.js", OwnerKind.App, "SomeTool", Guess, "AppData 里带 package.json 的也是应用，不是项目");
        F(local + @"\CrashDumps\app.dmp");
        s.NoOwner(local + @"\CrashDumps\app.dmp", "CrashDumps 是各家共用的转储目录");
        F(@"X:\ProgramData\Vendor\settings.ini");
        s.Owner(@"X:\ProgramData\Vendor\settings.ini", OwnerKind.App, "Vendor", Guess, "ProgramData 下的目录名");
        F(local + @"\Steam\htmlcache\x");
        s.Owner(local + @"\Steam\htmlcache\x", OwnerKind.App, "Steam", Guess, "AppData 里的 Steam 和程序目录的 Steam 并成一个");

        // 开发项目：项目标记所在目录；依赖里的包不另立项目，monorepo 的子包是自己的项目
        string web = me + @"\code\web";
        F(web + @"\package.json", 1);
        F(web + @"\src\a.ts");
        s.Owner(web + @"\src\a.ts", OwnerKind.Project, "web", Yes, "项目标记", web);
        F(web + @"\node_modules\left-pad\package.json", 1);
        F(web + @"\node_modules\left-pad\index.js");
        s.Owner(web + @"\node_modules\left-pad\index.js", OwnerKind.Project, "web", Yes, "依赖里的包仍归项目");
        F(web + @"\packages\ui\package.json", 1);
        F(web + @"\packages\ui\src\b.ts");
        s.Owner(web + @"\packages\ui\src\b.ts", OwnerKind.Project, "ui", Yes, "monorepo 子包");
        F(me + @"\Desktop\proj\Cargo.toml", 1);
        F(me + @"\Desktop\proj\src\main.rs");
        s.Owner(me + @"\Desktop\proj\src\main.rs", OwnerKind.Project, "proj", Yes, "桌面上的项目");
        F(me + @"\Desktop\loose.txt");
        s.NoOwner(me + @"\Desktop\loose.txt", "桌面上的散文件谁的都不是");

        // 模型、数据集：Hugging Face 仓库名
        string hub = me + @"\.cache\huggingface\hub";
        F(hub + @"\models--org--name\blobs\x");
        s.Owner(hub + @"\models--org--name\blobs\x", OwnerKind.Model, "org/name", Yes, "Hugging Face 模型仓库", hub + @"\models--org--name");
        F(hub + @"\models--gpt2\blobs\x");
        s.Owner(hub + @"\models--gpt2\blobs\x", OwnerKind.Model, "gpt2", Yes, "没有组织名的仓库");
        F(hub + @"\datasets--org--set\blobs\x");
        s.Owner(hub + @"\datasets--org--set\blobs\x", OwnerKind.Dataset, "org/set", Yes, "Hugging Face 数据集仓库");

        F(@"X:\Lab\loose\x.dat");
        s.NoOwner(@"X:\Lab\loose\x.dat", "没有依据的文件没有所属对象");
        s.Verify(ctx, FakeEnv());

        // 合并：同种同名的只有一个对象；对象本身取最强的证据，按名字并进来的那一处仍是推测
        var t = s.TreeOf(@"X:\Program Files\App\app.exe");
        ctx.Equal(1, t.Owners.Count(o => o.Name == "Microsoft.WindowsTerminal"), "应用包和它的数据是同一个对象");
        ctx.Equal(1, t.Owners.Count(o => o.Name == "Steam"), "程序目录和 AppData 里的 Steam 是同一个对象");
        ctx.Equal(1, t.Owners.Count(o => o.Name == "Firefox"), "Firefox 的 Roaming 和 Local 是同一个对象");
        ctx.True(t.Owners.Single(o => o.Name == "Elden Ring").Evidence == Yes, "游戏库认出来的游戏，对象本身是已确认（存档那一处推测不连累它）");
        ctx.True(t.Owners.Single(o => o.Name == "Steam").Evidence == Guess, "只按目录名认出来的应用，对象本身也只是推测");
        ctx.Equal(0, t.Owners.Count(o => o.Kind == OwnerKind.Project && o.Name == "left-pad"), "依赖里的包没有另立项目");
        ctx.Equal(0, t.Owners.Count(o => o.Name is "CrashDumps" or "Temp" or "Packages" or "Programs" or "Common Files" or "WindowsApps"), "容器目录本身不当所属对象");

        // 扫描根在容器里面：根上方不在树里，顺着根路径认
        void Root(string root, string file, OwnerKind kind, string name, Evidence evidence, string why)
        {
            var r = new Scenario(root);
            r.Node(file);
            if (kind == OwnerKind.Project) r.Node(Path.Combine(root, "package.json"), 1);
            r.Owner(file, kind, name, evidence, why, root);
            r.Verify(ctx, FakeEnv());
        }
        Root(game, game + @"\Game\eldenring.exe", OwnerKind.Game, "Elden Ring", Yes, "直接扫一个游戏");
        Root(@"X:\Program Files\App", @"X:\Program Files\App\bin\x.dll", OwnerKind.App, "App", Guess, "直接扫 Program Files 里的一个应用");
        Root(roaming + @"\Code", roaming + @"\Code\User\settings.json", OwnerKind.App, "Code", Guess, "直接扫 AppData 里的一个应用");
        Root(web, web + @"\src\a.ts", OwnerKind.Project, "web", Yes, "直接扫一个项目");
        Root(local + @"\npm-cache\_cacache", local + @"\npm-cache\_cacache\index-v5\x", OwnerKind.PackageManager, "npm", Yes, "直接扫 npm 缓存里的子目录");
        Root(hub + @"\models--org--name\snapshots", hub + @"\models--org--name\snapshots\abc\config.json", OwnerKind.Model, "org/name", Yes, "直接扫模型仓库里面");
        var vendor = new Scenario(@"X:\Program Files\Adobe");
        vendor.Node(@"X:\Program Files\Adobe\Adobe Photoshop 2026\Photoshop.exe");
        vendor.Owner(@"X:\Program Files\Adobe\Adobe Photoshop 2026\Photoshop.exe", OwnerKind.App, "Adobe Photoshop 2026", Guess, "直接扫厂商目录：下面带 .exe 的是应用");
        vendor.Node(@"X:\Program Files\Adobe\readme.txt");
        vendor.Owner(@"X:\Program Files\Adobe\readme.txt", OwnerKind.App, "Adobe", Guess, "厂商目录根上的文件归厂商");
        vendor.Verify(ctx, FakeEnv());
    }

    // ---------------- 用途的证据等级：按依据的种类，不按「是不是规则命中的那一层」 ----------------

    static void Evidences(CheckContext ctx)
    {
        var s = new Scenario();
        string me = @"X:\Users\Alex", local = me + @"\AppData\Local";
        void E(string path, byte purpose, Evidence want, string why, long size = MB, bool dir = false)
        {
            if (dir) s.Dir(path, purpose, why);
            else s.File(path, purpose, why, size);
            s.PurposeEvidence(path, want, why);
        }
        E(local + @"\npm-cache", Cache, Evidence.Confirmed, "位置规则命中的目录", dir: true);
        E(local + @"\npm-cache\_cacache\content-v2\x", Cache, Evidence.Confirmed, "位置规则下面的文件也是已确认（规则说的就是整棵子树）");
        E(@"X:\Windows\System32\kernel32.dll", Sys, Evidence.Confirmed, "系统目录里的文件");
        E(@"X:\Games\SteamLibrary\steamapps\common\Elden Ring\Logs\x.txt", Logs, Evidence.Confirmed, "日志目录名");
        E(@"X:\Games\SteamLibrary\steamapps\common\Elden Ring\Game\output.log", Logs, Evidence.Inferred, "游戏日志靠扩展名，推测");
        E(@"X:\Games\SteamLibrary\steamapps\common\Elden Ring\Game\Data0.bdt", Games, Evidence.Confirmed, "游戏库里的游戏文件");
        E(me + @"\code\web\package.json", Dev, Evidence.Confirmed, "项目标记", 1);
        E(me + @"\code\web\src\a.ts", Dev, Evidence.Confirmed, "项目里的文件");
        E(@"X:\Lab\loose\a.mp4", Media, Evidence.Inferred, "扩展名兜底");
        E(@"X:\Lab\loose\x.dat", Unknown, Evidence.Unknown, "未识别");
        E(@"X:\Data\Dl", Media, Evidence.Confirmed, "下载目录本身", dir: true);
        E(@"X:\Data\Dl\setup.exe", Media, Evidence.Inferred, "下载目录里没有类型证据的文件：位置只说明在哪");
        E(@"X:\Data\Dl\a.iso", Archives, Evidence.Inferred, "下载目录里按扩展名认的镜像");
        E(@"X:\Lab\dom\Movies\a.mp4", Media, Evidence.Inferred, "电影", 90 * MB);
        E(@"X:\Lab\dom\Movies", Media, Evidence.Inferred, "按子树主要用途展示的目录", dir: true);
        E(@"X:\Lab\dom\Mixed\a.mp4", Media, Evidence.Inferred, "一半视频", 50 * MB);
        E(@"X:\Lab\dom\Mixed\b.dat", Unknown, Evidence.Unknown, "一半杂项", 50 * MB);
        E(@"X:\Lab\dom\Mixed", Unknown, Evidence.Unknown, "谁都不过线的目录", dir: true);
        E(local + @"\NVIDIA\DXCache\a.bin", Cache, Evidence.Confirmed, "着色器缓存", 90 * MB);
        E(local + @"\NVIDIA\settings.json", AppData, Evidence.Confirmed, "应用数据", 10 * MB);
        E(local + @"\NVIDIA", Cache, Evidence.Inferred, "继承应用数据、按子树换成缓存展示的目录", dir: true);
        E(local, AppData, Evidence.Confirmed, "AppData\\Local 自己", dir: true);
        s.Dir(@"X:\$NTFS 元数据", Sys, "元数据分组", NodeFlags.Virtual | NodeFlags.Metafile);
        s.File(@"X:\$NTFS 元数据\$MFT", Sys, "元文件", 1 * MB, NodeFlags.Metafile);
        s.PurposeEvidence(@"X:\$NTFS 元数据\$MFT", Evidence.Confirmed, "元文件：文件系统自己的标记");
        s.File(@"X:\未统计（系统保留或无权访问）", Unexpl, "未解释空间", 1 * MB, NodeFlags.Virtual);
        s.PurposeEvidence(@"X:\未统计（系统保留或无权访问）", Evidence.Unknown, "未解释空间说不清是什么");
        s.Verify(ctx, FakeEnv());

        // 扫描根在用户文件夹里面是推测、就是用户文件夹本身是已确认；根落在位置规则里面是已确认
        var r1 = new Scenario(@"X:\Data\Dl\stuff");
        r1.File(@"X:\Data\Dl\stuff\a.dat", Media, "下载目录里的子目录");
        r1.PurposeEvidence(@"X:\Data\Dl\stuff", Evidence.Inferred, "扫描根在下载目录里面");
        r1.Verify(ctx, FakeEnv());
        var r2 = new Scenario(@"X:\Data\Dl");
        r2.File(@"X:\Data\Dl\a.dat", Media, "下载目录");
        r2.PurposeEvidence(@"X:\Data\Dl", Evidence.Confirmed, "扫描根就是下载目录");
        r2.Verify(ctx, FakeEnv());
        var r3 = new Scenario(local + @"\npm-cache\_cacache");
        r3.File(local + @"\npm-cache\_cacache\x", Cache, "npm 缓存里的子目录");
        r3.PurposeEvidence(local + @"\npm-cache\_cacache", Evidence.Confirmed, "扫描根在 npm 缓存里面");
        r3.Verify(ctx, FakeEnv());
    }

    // ---------------- 扫描根是普通文件夹 ----------------

    static void ScanRootIsFolder(CheckContext ctx)
    {
        // 直接扫桌面：根自己命中用户文件夹规则，项目照样覆盖
        var s = new Scenario(Desktop);
        s.File(J(Desktop, "loose.dat")!, Media, "桌面根下的散文件");
        s.File(J(Desktop, "proj", "package.json")!, Dev, "桌面根下的项目", 1);
        s.File(J(Desktop, "proj", "src", "a.dat")!, Dev, "项目子孙");
        s.Dir(Desktop, Media, "扫描根（桌面）自己");
        s.Verify(ctx);

        // 直接扫 npm 缓存，以及缓存里面的子目录——根在规则目录内部
        var npm = J(Local, "npm-cache")!;
        var c1 = new Scenario(npm);
        c1.File(J(npm, "a.dat")!, Cache, "扫的就是缓存目录");
        c1.Dir(npm, Cache, "缓存根");
        c1.Verify(ctx);
        var inner = J(npm, "_cacache")!;
        var c2 = new Scenario(inner);
        c2.File(J(inner, "content-v2", "x.dat")!, Cache, "扫的是缓存里的子目录");
        c2.File(J(inner, "package.json")!, Cache, "子目录根里的 package.json 不翻盘", 1);
        c2.Dir(inner, Cache, "缓存里的子目录根");
        c2.Verify(ctx);

        // Program Files 里面的某个应用
        var app = J(Pf, "SomeApp")!;
        var a = new Scenario(app);
        a.File(J(app, "package.json")!, Apps, "应用目录根里的 package.json", 1);
        a.File(J(app, "bin", "x.dll")!, Apps, "应用子孙");
        a.Dir(app, Apps, "应用根");
        a.Verify(ctx);

        // 根本身就是规则目录；根在系统目录里面
        var win = new Scenario(J(SysDrive, "Windows")!);
        win.File(J(SysDrive, "Windows", "System32", "k.dll")!, Sys, "扫的是 Windows");
        win.Dir(J(SysDrive, "Windows")!, Sys, "Windows 根");
        win.Verify(ctx);
        var sys32 = new Scenario(J(SysDrive, "Windows", "System32")!);
        sys32.File(J(SysDrive, "Windows", "System32", "k.dll")!, Sys, "扫的是 Windows 里面的目录");
        sys32.Verify(ctx);

        // 名字规则在根的上方（直接扫 steamapps\common）
        var steam = J(Lab, "scanroot", "SteamLibrary", "steamapps", "common")!;
        var st = new Scenario(steam);
        st.File(J(steam, "Game", "g.dat")!, Games, "扫的是 SteamLibrary 里面");
        st.Dir(steam, Games, "扫描根在游戏库里面，根自己也是游戏");
        st.Verify(ctx);

        // 普通文件夹：没有规则，靠主导用途
        string media = J(Lab, "scanroot", "Media")!;
        var m = new Scenario(media);
        m.File(J(media, "a.mp4")!, Media, "普通文件夹里的视频", 70 * MB);
        m.File(J(media, "b.dat")!, Unknown, "普通文件夹里的杂项", 30 * MB);
        m.Dir(media, Media, "普通文件夹的主导用途");
        m.Verify(ctx);
        var m2 = new Scenario(media);
        m2.File(J(media, "a.mp4")!, Media, "视频", 55 * MB);
        m2.File(J(media, "b.dat")!, Unknown, "杂项", 45 * MB);
        m2.Dir(media, Unknown, "55% 不够");
        m2.Verify(ctx);

        // 扫描根带 \\?\ 前缀（长路径写法）：规则照样对得上
        var pre = new Scenario(@"\\?\X:\");
        pre.File(@"\\?\X:\Windows\k.dll", Sys, "带前缀的根下的 Windows");
        pre.File(@"\\?\X:\Users\Alex\Desktop\a.dat", Media, "带前缀的根下的桌面");
        pre.File(@"\\?\X:\Users\Alex\Desktop\proj\package.json", Dev, "带前缀的根下的项目", 1);
        pre.File(@"\\?\X:\Users\Alex\AppData\Local\npm-cache\a.dat", Cache, "带前缀的根下的缓存");
        pre.Verify(ctx, FakeEnv());

        // 扫的就是卷根：卷根自己不能当项目根
        var v = new Scenario(@"X:\");
        v.File(@"X:\package.json", Unknown, "卷根下的 package.json", 1);
        v.File(@"X:\pad.dat", Unknown, "卷根下的普通文件", 10 * MB);
        v.File(@"X:\proj\package.json", Dev, "卷根下的项目", 1);
        v.File(@"X:\Windows\k.dll", Sys, "卷根下的 Windows");
        v.File(@"X:\DumpStack.log.tmp", Sys, "卷根下的 DumpStack");
        v.Dir(@"X:\", Unknown, "卷根自己不是代码");
        v.Verify(ctx, FakeEnv());

        // 根在按名字认的日志目录里、在应用数据里
        var logs = new Scenario(@"X:\Users\Alex\AppData\Local\SomeApp\logs");
        logs.File(@"X:\Users\Alex\AppData\Local\SomeApp\logs\today.txt", Logs, "扫的是应用的 logs 目录");
        logs.Dir(@"X:\Users\Alex\AppData\Local\SomeApp\logs", Logs, "logs 根自己");
        logs.Verify(ctx, FakeEnv());
        var ad = new Scenario(@"X:\Users\Alex\AppData\Roaming\Code");
        ad.File(@"X:\Users\Alex\AppData\Roaming\Code\User\settings.json", AppData, "扫的是应用数据里的一个应用");
        ad.File(@"X:\Users\Alex\AppData\Roaming\Code\Cache\data_0", Cache, "它的缓存目录");
        ad.Dir(@"X:\Users\Alex\AppData\Roaming\Code", AppData, "根自己");
        ad.Verify(ctx, FakeEnv());
    }

    // ---------------- 假环境：环境变量优先、写歪的值、豁免 ----------------

    internal static PurposeEnv FakeEnv(Dictionary<string, string>? extra = null)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = @"X:\Users\Alex",
            ["LOCALAPPDATA"] = @"X:\Users\Alex\AppData\Local",
            ["APPDATA"] = @"X:\Users\Alex\AppData\Roaming",
            ["ProgramFiles"] = @"X:\Program Files",
            ["ProgramFiles(x86)"] = @"X:\Program Files (x86)",
            ["SystemRoot"] = @"X:\Windows",
            ["ProgramData"] = @"X:\ProgramData",
        };
        if (extra != null) foreach (var (k, v) in extra) vars[k] = v;
        return new PurposeEnv
        {
            Var = k => vars.GetValueOrDefault(k),
            Folder = f => f == Environment.SpecialFolder.MyDocuments ? @"X:\Users\Alex\OneDrive\文档" : null,
            Downloads = () => @"X:\Data\Dl",
            NpmRc = k => k == "cache" ? @"X:\Rc\npm" : null,
            PnpmConfig = k => k == "storeDir" ? @"X:\Rc\pnpm" : null,
        };
    }

    static void FakeEnvironment(CheckContext ctx)
    {
        var env = FakeEnv(new()
        {
            ["npm_config_cache"] = @"X:\Moved\npm", ["UV_CACHE_DIR"] = @"X:\Moved\uv", ["CARGO_HOME"] = @"X:\Moved\cargo",
            ["RUSTUP_HOME"] = @"X:\Moved\rustup", ["GOPATH"] = @"X:\Moved\go1;X:\Moved\go2",
            ["CONDA_PKGS_DIRS"] = @"X:\Moved\pkgsA,X:\Moved\pkgsB", ["CONDA_PREFIX"] = @"X:\Dev\conda\envs\dev",
            ["PLAYWRIGHT_BROWSERS_PATH"] = "0", ["PUB_CACHE"] = @"X:\", ["OLLAMA_MODELS"] = @"X:\Moved\ollama", ["HF_HOME"] = @"X:\Moved\hf",
            ["XDG_CACHE_HOME"] = @"X:\Moved\xdg", ["BUN_INSTALL"] = @"X:\Moved\bun", ["NUGET_PACKAGES"] = @"X:\Moved\nuget",
            ["ANDROID_SDK_ROOT"] = @"X:\Moved\android", ["OneDrive"] = @"X:\Users\Alex\OneDrive",
            ["TEMP"] = @"X:\Tmp", ["TFDS_DATA_DIR"] = @"X:\Moved\tfds", ["SCIKIT_LEARN_DATA"] = @"X:\Moved\sk",
        });
        var s = new Scenario();
        s.File(@"X:\Moved\npm\a.dat", Cache, "npm_config_cache 指向的位置");
        s.File(@"X:\Users\Alex\AppData\Local\npm-cache\a.dat", Cache, "搬家后留在默认位置的 npm 缓存也认");
        s.File(@"X:\Rc\npm\a.dat", Cache, "~/.npmrc 里的 cache");
        s.File(@"X:\Rc\pnpm\a.dat", Cache, "pnpm config.yaml 里的 storeDir");
        s.File(@"X:\Moved\uv\a.dat", Cache, "UV_CACHE_DIR");
        s.File(@"X:\Moved\cargo\registry\a.dat", Cache, "CARGO_HOME\\registry");
        s.File(@"X:\Moved\cargo\bin\cargo.exe", Unknown, "CARGO_HOME 下只有 registry 是缓存");
        s.File(@"X:\Moved\rustup\toolchains\a.dat", Sdk, "RUSTUP_HOME");
        s.File(@"X:\Moved\go1\pkg\mod\a.dat", Cache, "GOPATH 第一项的 pkg\\mod");
        s.File(@"X:\Moved\go2\pkg\mod\a.dat", Cache, "GOPATH 第二项的 pkg\\mod");
        s.File(@"X:\Moved\pkgsA\a.dat", Cache, "CONDA_PKGS_DIRS 第一项");
        s.File(@"X:\Moved\pkgsB\a.dat", Cache, "CONDA_PKGS_DIRS 第二项");
        s.File(@"X:\Dev\conda\Lib\a.dat", Sdk, "CONDA_PREFIX 推出的 conda 安装目录");
        s.File(@"X:\Dev\conda\pkgs\a.dat", Cache, "conda 安装目录下的 pkgs");
        s.File(@"X:\Moved\ollama\a.dat", Ai, "OLLAMA_MODELS");
        s.File(@"X:\Moved\hf\hub\models--a--b\a.dat", Cache, "HF_HOME\\hub 下的模型仓库是模型缓存");
        s.File(@"X:\Moved\hf\a.dat", Unknown, "HF_HOME 根下不是模型");
        s.File(@"X:\Moved\hf\hub\datasets--a--b\a.dat", Cache, "HF_HOME\\hub 下的数据集仓库是数据集缓存");
        s.File(@"X:\Moved\hf\datasets\a.dat", Data, "HF_HOME\\datasets（datasets 库的缓存）");
        s.File(@"X:\Moved\tfds\mnist\a.dat", Data, "TFDS_DATA_DIR");
        s.File(@"X:\Moved\sk\a.dat", Data, "SCIKIT_LEARN_DATA");
        s.File(@"X:\Tmp\a.dat", Cache, "搬走的 TEMP");
        s.File(@"X:\Users\Alex\AppData\Local\Temp\a.dat", Cache, "默认位置的临时目录也认");
        s.File(@"X:\Windows\Temp\a.dat", Cache, "Windows\\Temp");
        s.File(@"X:\Windows\SoftwareDistribution\Download\a.cab", Cache, "Windows 更新缓存");
        s.File(@"X:\ProgramData\Package Cache\{guid}\setup.msi", Cache, "安装包缓存");
        s.File(@"X:\ProgramData\Microsoft\Diagnosis\ETLLogs\a.dat", Logs, "诊断数据目录");
        s.File(@"X:\ProgramData\Vendor\settings.ini", AppData, "ProgramData 里的应用数据");
        s.File(@"X:\Moved\xdg\huggingface\hub\models--a--b\a.dat", Cache, "XDG_CACHE_HOME\\huggingface\\hub 下的模型仓库是模型缓存");
        s.File(@"X:\Moved\xdg\puppeteer\a.dat", Cache, "XDG_CACHE_HOME\\puppeteer");
        s.File(@"X:\Moved\xdg\pnpm\a.dat", Cache, "XDG_CACHE_HOME\\pnpm");
        s.File(@"X:\Moved\bun\install\cache\a.dat", Cache, "BUN_INSTALL\\install\\cache");
        s.File(@"X:\Moved\bun\bin\bun.exe", Unknown, "BUN_INSTALL 下只有 install\\cache 是缓存");
        s.File(@"X:\Moved\nuget\a.dat", Cache, "NUGET_PACKAGES");
        s.File(@"X:\Moved\android\a.dat", Sdk, "ANDROID_SDK_ROOT");
        s.File(@"X:\Data\Dl\a.dat", Media, "注册表里搬走的 Downloads");
        s.File(@"X:\Users\Alex\OneDrive\文档\a.dat", Media, "GetFolderPath 重定向后的 Documents");
        s.File(@"X:\Users\Alex\OneDrive\Desktop\a.dat", Media, "OneDrive 下的同名文件夹");
        s.File(@"X:\random\a.dat", Unknown, "写歪的环境变量（PUB_CACHE=盘符根、相对路径）不能把整块盘染色");
        s.File(@"X:\Windows\k.dll", Sys, "SystemRoot");
        s.File(@"X:\Program Files\App\a.exe", Apps, "ProgramFiles");
        s.File(@"X:\Program Files (x86)\App\a.exe", Apps, "ProgramFiles(x86)");
        s.File(@"X:\pagefile.sys", Sys, "卷根 pagefile", 8L << 30);
        s.File(@"X:\DumpStack.log.old", Sys, "DumpStack.log 通配");
        s.File(@"X:\$Recycle.Bin\S-1\a.dat", Sys, "回收站");
        s.Verify(ctx, env);

        // 项目根豁免：卷根、家、AppData 及其直接子目录、shell 文件夹、别人的家
        var e = new Scenario();
        e.File(@"X:\package.json", Unknown, "卷根里的标记", 1);
        e.File(@"X:\Users\package.json", Unknown, "Users 目录里的标记", 1);
        e.File(@"X:\Users\Alex\package.json", Unknown, "家目录里的标记", 1);
        e.File(@"X:\Users\Bob\package.json", Unknown, "别人家里的标记", 1);
        e.File(@"X:\Users\Alex\AppData\package.json", AppData, "AppData 里的标记", 1);
        e.File(@"X:\Users\Alex\AppData\Local\package.json", AppData, "AppData\\Local 里的标记", 1);
        e.File(@"X:\Users\Alex\AppData\Roaming\.git", AppData, "AppData\\Roaming 里的标记", 1);
        e.File(@"X:\Users\Alex\AppData\Local\Foo\package.json", AppData, "AppData\\Local 子目录里的标记：应用数据比项目标记优先", 1);
        e.File(@"X:\Users\Alex\AppData\Local\Foo\a.dat", AppData, "…它的子孙", 1 * MB);
        e.File(@"X:\Users\Alex\Desktop\package.json", Media, "桌面里的标记", 1);
        e.File(@"X:\Users\Alex\Desktop\proj\package.json", Dev, "桌面上的项目", 1);
        e.File(@"X:\Users\Alex\Desktop\proj\b.dat", Dev, "桌面项目的子孙");
        e.File(@"X:\Users\Alex\OneDrive\文档\package.json", Media, "重定向的文档里的标记", 1);
        e.File(@"X:\Users\Alex\OneDrive\文档\proj\pom.xml", Dev, "重定向的文档里的项目", 1);
        e.File(@"X:\Data\Dl\package.json", Media, "下载目录里的标记", 1);
        e.File(@"X:\Users\Alex\code\package.json", Dev, "家里的子目录可以是项目", 1);
        e.File(@"X:\Users\Alex\pad.dat", Unknown, "垫一个大文件，免得家目录被小标记文件的占比带偏", 50 * MB);
        e.File(@"X:\Users\Bob\pad.dat", Unknown, "同上", 50 * MB);
        e.File(@"X:\Users\pad.dat", Unknown, "同上", 50 * MB);
        e.File(@"X:\pad.dat", Unknown, "同上", 50 * MB);
        e.File(@"X:\Users\Alex\AppData\Local\pad.dat", AppData, "同上（AppData 里的是应用数据）", 50 * MB);
        e.File(@"X:\Users\Alex\AppData\pad.dat", AppData, "同上", 50 * MB);
        e.File(@"X:\Program Files\App\package.json", Apps, "Program Files 里的标记", 1);
        e.Verify(ctx, env);
        foreach (var dir in new[] { @"X:\", @"X:\Users", @"X:\Users\Alex", @"X:\Users\Bob", @"X:\Users\Alex\AppData", @"X:\Users\Alex\AppData\Local", @"X:\Users\Alex\AppData\Roaming" })
            ctx.True(e.Of(dir) != Dev, $"{dir} 本身不能被染成代码项目（实际 {Names[e.Of(dir)]}）");
        ctx.Equal(Names[Media], Names[e.Of(@"X:\Users\Alex\Desktop")], "桌面目录自己仍是媒体");
    }

    // ---------------- %TEMP% 是 8.3 短名（用户名超过 8 个字符的机器上默认就是） ----------------

    static void ShortTemp(CheckContext ctx)
    {
        // 长名反过来求：默认临时目录的短名正好等于 %TEMP%，就知道它的长名是谁——不借被测代码那条展开路径（Path.GetFullPath）
        string? temp = Environment.GetEnvironmentVariable("TEMP")?.TrimEnd('\\');
        string longTemp = J(Local, "Temp")!;
        var sb = new System.Text.StringBuilder(520);
        bool shortOfDefault = temp is not null && temp.Contains('~') && CleanupChecks.GetShortPathNameW(longTemp, sb, (uint)sb.Capacity) > 0
            && sb.ToString().TrimEnd('\\').Equals(temp, StringComparison.OrdinalIgnoreCase);
        if (!shortOfDefault)
        {
            ctx.Info("本机 TEMP 不是默认临时目录的 8.3 短名，跳过短名换长名这一项");
            return;
        }
        // LOCALAPPDATA 指到别处：默认位置那条规则帮不上忙，只剩 TEMP 这一条
        var env = FakeEnv(new() { ["TEMP"] = temp!, ["LOCALAPPDATA"] = @"X:\Elsewhere" });
        var s = new Scenario();
        s.File(Path.Combine(longTemp, "a.tmp"), Cache, $"TEMP 是短名 {temp}，树里的长名也要认成缓存");
        s.Verify(ctx, env);
    }

    // ---------------- 详情里的分类依据（PurposeBasis） ----------------

    static void Explanations(CheckContext ctx)
    {
        var env = FakeEnv();
        var s = new Scenario();
        string game = @"X:\Games\SteamLibrary\steamapps\common\Elden Ring";
        s.File(game + @"\Game\output.log", Logs, "游戏日志");
        s.File(game + @"\Game\Data0.bdt", Games, "游戏资源");
        s.File(@"X:\Users\Alex\Downloads\win.iso", Archives, "下载目录里的镜像", 5L << 30);
        s.File(@"X:\Users\Alex\Downloads\setup.exe", Media, "下载目录里没有类型证据的文件");
        s.File(@"X:\DumpStack.log", Sys, "卷根的 DumpStack.log");
        s.File(@"X:\Lab\x.dat", Unknown, "没有依据的文件");
        s.File(@"X:\未统计（系统保留或无权访问）", Unexpl, "未解释空间", 10 * MB, NodeFlags.Virtual);
        s.File(@"X:\Users\Alex\AppData\Local\SomeTool\settings.json", AppData, "应用数据");
        s.File(@"X:\DumpStack.log.old", Sys, "卷根的 DumpStack.log.old（通配）");
        s.File(@"X:\Users\Alex\AppData\Local\npm-cache\_cacache\x", Cache, "npm 缓存");
        s.File(@"X:\Users\Alex\code\web\package.json", Dev, "项目标记", 1);
        s.File(@"X:\Users\Alex\code\web\node_modules\left-pad\index.js", Dev, "依赖");
        s.File(@"X:\Program Files\App\app.exe", Apps, "应用");
        s.File(@"X:\Users\Alex\.ollama\models\manifests\registry.ollama.ai\library\llama3\latest", Ai, "Ollama 的清单文件");
        s.File(@"X:\Users\Alex\.cache\huggingface\hub\models--org--name\snapshots\abc\model.safetensors", Cache, "Hugging Face 模型仓库里的权重", 4000 * MB);
        s.Verify(ctx, env);

        void Has(string path, string why, params string[] want)
        {
            string text = s.Explain(path, env);
            foreach (var w in want) ctx.True(text.Contains(w), $"{why}：依据里应写「{w}」，实际「{text}」");
        }
        Has(game + @"\Game\output.log", "游戏日志写明是按日志扩展名、而且比所在目录优先", "日志与诊断类扩展名 .log", "比所在目录的用途优先");
        Has(game + @"\Game\Data0.bdt", "游戏资源写明继承了游戏库", @"继承上级目录的名称规则：X:\Games\SteamLibrary\steamapps");
        Has(@"X:\Users\Alex\Downloads\win.iso", "下载目录里的镜像写明是扩展名兜底", "按扩展名 .iso 兜底归为「归档与镜像」");
        Has(@"X:\Users\Alex\Downloads\setup.exe", "没有类型证据的文件写明继承用户文件夹", @"继承上级目录的位置规则：X:\Users\Alex\Downloads");
        Has(@"X:\DumpStack.log", "卷根 DumpStack.log 写明是它自己的位置规则", @"位置规则：X:\DumpStack.log");
        ctx.True(!s.Explain(@"X:\DumpStack.log", env).Contains("扩展名"), "卷根 DumpStack.log 的依据不提扩展名");
        Has(@"X:\Lab\x.dat", "未识别写明依据不足", "未识别", "依据不足");
        Has(@"X:\未统计（系统保留或无权访问）", "虚拟叶子写明是未解释空间", "未解释空间", "不当作系统文件");
        Has(@"X:\Users\Alex\AppData\Local\SomeTool\settings.json", "应用数据写明继承 AppData", @"继承上级目录的位置规则：X:\Users\Alex\AppData");
        foreach (var path in new[] { game + @"\Game\output.log", @"X:\Lab\x.dat", @"X:\Users\Alex\Downloads\win.iso" })
            Has(path, "每条依据都提醒用途不等于能删", "用途分类不代表可安全清理");

        // 角色和所属对象的依据（dise 的例子：游戏日志主用途是日志与诊断，所属对象仍是游戏）
        Has(game + @"\Game\output.log", "游戏日志的角色和所属对象", "角色「日志」：按扩展名 .log", "所属对象「Elden Ring」（游戏，已确认）：游戏库里的游戏文件夹");
        Has(game + @"\Game\Data0.bdt", "游戏资源按扩展名细分", "角色「游戏资源」：按扩展名 .bdt");
        Has(@"X:\Users\Alex\AppData\Local\npm-cache\_cacache\x", "规则自带的角色和所属对象", "角色「包管理器缓存」：和用途出自同一条依据", "所属对象「npm」（包管理器，已确认）：位置规则");
        Has(@"X:\Users\Alex\code\web\node_modules\left-pad\index.js", "依赖的角色来自目录名，所属对象是项目",
            "角色「项目依赖」：来自目录名「node_modules」", "所属对象「web」（开发项目，已确认）：项目标记");
        Has(@"X:\Program Files\App\app.exe", "按目录名猜的所属对象写明是推测", "所属对象「App」（应用，推测）：按程序目录下的文件夹名推测");
        Has(@"X:\Users\Alex\.ollama\models\manifests\registry.ollama.ai\library\llama3\latest", "模型目录里的小文件不硬说是权重", "按所在的模型目录推断");
        Has(@"X:\Users\Alex\.cache\huggingface\hub\models--org--name\snapshots\abc\model.safetensors", "模型缓存：主归缓存、标模型数据、所属对象是那个模型",
            @"Hugging Face 模型缓存仓库：X:\Users\Alex\.cache\huggingface\hub\models--org--name", "角色「模型数据」：和用途出自同一条依据",
            "所属对象「org/name」（模型，已确认）：Hugging Face 缓存仓库名");
        Has(@"X:\Lab\x.dat", "没有依据就说尚未识别", "所属对象：尚未识别");
        Has(@"X:\Users\Alex\Downloads\setup.exe", "用户文件夹不说明角色", "角色：未知");
        Has(@"X:\DumpStack.log", "卷根 DumpStack.log 归 Windows", "所属对象「Windows」（系统组件，已确认）：位置规则");
        Has(@"X:\DumpStack.log.old", "DumpStack.log 的通配也是位置规则，角色不按扩展名", @"位置规则：卷根的 DumpStack.log.old", "角色「系统组件」：和用途出自同一条依据");

        // 详情接口把同样的结论原样带出去（PurposeBasis 走真实环境，这里只核对字段）
        var tree = s.TreeOf(@"X:\Lab\x.dat");
        NodeDetailsDto D(string path) => TreeQueries.GetDetails(tree, new DetailsRequest(s.Id(path)));
        var log = D(game + @"\Game\output.log");
        int gameRoot = tree.FindByPath(game);
        ctx.True(log.Role == Roles.Log && log.Evidence == Evidence.Inferred, $"游戏日志的详情：角色日志、用途证据推测（实际 {log.Role}/{log.Evidence}）");
        ctx.Equal(new OwnerDto(OwnerKind.Game, "Elden Ring", gameRoot, game, Evidence.Confirmed), log.Owner, "游戏日志的详情：所属对象是游戏，带根节点和根路径");
        ctx.True(log.PurposeBasis!.Contains("所属对象「Elden Ring」"), "游戏日志的详情：依据里写了所属对象");
        var npm = D(@"X:\Users\Alex\AppData\Local\npm-cache\_cacache\x");
        ctx.True(npm.Role == Roles.PackageCache && npm.Evidence == Evidence.Confirmed && npm.Owner is { Kind: OwnerKind.PackageManager, Name: "npm", Evidence: Evidence.Confirmed },
            "npm 缓存的详情：包管理器缓存、已确认、所属 npm");
        var app = D(@"X:\Program Files\App\app.exe");
        ctx.True(app.Owner is { Kind: OwnerKind.App, Name: "App", Evidence: Evidence.Inferred } && app.Owner.RootPath == @"X:\Program Files\App", "应用的详情：按目录名推测");
        var none = D(@"X:\Lab\x.dat");
        ctx.True(none.Role == Roles.Unknown && none.Evidence == Evidence.Unknown && none.Owner is null, "没依据的文件：角色未知、证据未识别、没有所属对象");
    }

    // ---------------- 配置文件里的缓存位置：~/.npmrc、pnpm config.yaml ----------------

    static void ConfigFiles(CheckContext ctx)
    {
        const string rc = "; 注释\n//registry.npmjs.org/:_authToken=secret\nprefix = D:\\a\\b\n  cache = \"D:\\x\\npm\"  \r\n# cache=ignored\nCache=D:\\later\\npm\n";
        ctx.Equal(@"D:\later\npm", PurposeEnv.NpmRcValue(rc, "cache"), ".npmrc 同键多次出现后面的赢，键名不区分大小写");
        ctx.Equal(@"D:\a\b", PurposeEnv.NpmRcValue(rc, "prefix"), ".npmrc 带空格的键值");
        ctx.Equal(null, PurposeEnv.NpmRcValue(rc, "store-dir"), ".npmrc 没有这个键");
        ctx.Equal(@"D:\x\npm", PurposeEnv.NpmRcValue("cache=\"D:\\x\\npm\"", "cache")?.Trim('"'), ".npmrc 引号由调用方的路径规范化去掉");
        ctx.Equal(null, PurposeEnv.NpmRcValue("", "cache"), "空文件");

        const string yaml = "# pnpm\nstoreDir: D:\\Tools\\x\\pnpm-store\nother: 1\ncacheDir: \"D:\\\\q\\\\c\"\nquoted: 'D:\\z'\nempty:\n";
        ctx.Equal(@"D:\Tools\x\pnpm-store", PurposeEnv.YamlValue(yaml, "storeDir"), "YAML 裸值按字面，盘符的冒号不是分隔符");
        ctx.Equal(@"D:\q\c", PurposeEnv.YamlValue(yaml, "cacheDir"), "YAML 双引号里的 \\\\ 是一个反斜杠");
        ctx.Equal(@"D:\z", PurposeEnv.YamlValue(yaml, "quoted"), "YAML 单引号按字面");
        ctx.Equal(null, PurposeEnv.YamlValue(yaml, "empty"), "YAML 空值");
        ctx.Equal(null, PurposeEnv.YamlValue(yaml, "missing"), "YAML 没有这个键");
    }

    // ---------------- 契约：长度、只读、取消、SetPurposes ----------------

    static void Contract(CheckContext ctx)
    {
        // 名称表本身（和前端逐项比对的是 web/tests/purposes.test.ts）
        ctx.Equal(Purposes.Count, Purposes.Table.Length, "名称表条数等于用途数");
        ctx.Equal(Purposes.Count, Purposes.Table.Select(t => t.Key).Distinct().Count(), "名称表的 key 不重复");
        ctx.Equal(Purposes.Count, Purposes.Table.Select(t => t.Name).Distinct().Count(), "名称表的名称不重复");
        ctx.Equal(Purposes.Count - 1, (int)Purposes.Unexplained, "未解释空间是最后一个编号");
        ctx.Equal("未识别", Purposes.Table[Purposes.Unknown].Name, "0 号是未识别");
        ctx.Equal(Roles.Count, Roles.Table.Length, "角色表条数等于角色数");
        ctx.Equal(Roles.Count, Roles.Table.Select(t => t.Key).Distinct().Count(), "角色 key 不重复");
        ctx.Equal(Roles.Count, Roles.Table.Select(t => t.Name).Distinct().Count(), "角色名称不重复");
        ctx.True(Roles.Count <= Classification.RoleMask + 1, "角色编号塞得进一个字节的低 6 位");
        ctx.Equal("未知", Roles.Table[Roles.Unknown].Name, "0 号角色是未知");

        var s = new Scenario();
        s.File(@"X:\Windows\k.dll", Sys, "系统", 100);
        s.File(@"X:\Users\Alex\Desktop\v.mp4", Media, "媒体", 50);
        s.File(@"X:\Users\Alex\code\package.json", Dev, "代码", 1);
        s.File(@"X:\Users\Alex\code\a.cs", Dev, "代码", 20);
        s.File(@"X:\x.dat", Unknown, "其他", 7);
        s.File(@"X:\未统计", Unexpl, "未扫描", 1000, NodeFlags.Virtual);
        var tree = s.TreeFor(@"X:\");
        var env = FakeEnv();
        tree.Seal();
        int version = tree.Version;
        var c = PurposeClassifier.Classify(tree, env, CancellationToken.None);
        var u = c.Purposes;
        ctx.Equal(tree.Count, u.Length, "返回数组长度等于节点数");
        ctx.Equal(tree.Count, c.RoleBits.Length, "角色数组长度等于节点数");
        ctx.Equal(version, tree.Version, "分类不修改树（版本号不变）");
        ctx.True(u.All(x => x < Purposes.Count), "用途编号都在范围内");
        ctx.True(Enumerable.Range(0, tree.Count).All(i => c.Role(i) < Roles.Count), "角色编号都在范围内");
        var again = PurposeClassifier.Classify(tree, env, CancellationToken.None);
        ctx.True(u.AsSpan().SequenceEqual(again.Purposes) && c.RoleBits.AsSpan().SequenceEqual(again.RoleBits), "重复分类结果一致");
        tree.SetClassification(c);
        ctx.Equal(version + 1, tree.Version, "SetClassification 只让版本号加一");
        var totals = tree.PurposeTotals(Metric.Alloc);
        ctx.Equal(100L, totals[Sys], "PurposeTotals 系统");
        ctx.Equal(50L, totals[Media], "PurposeTotals 媒体");
        ctx.Equal(21L, totals[Dev], "PurposeTotals 代码");
        ctx.Equal(7L, totals[Unknown], "PurposeTotals 其他");
        ctx.Equal(1000L, totals[Unexpl], "PurposeTotals 未扫描");
        ctx.Equal(Sys, tree.Purpose(tree.FindByPath(@"X:\Windows\k.dll")), "SetClassification 后 Purpose(i) 读到分类结果");
        ctx.Equal(RoleNames[Roles.Source], RoleNames[tree.Role(tree.FindByPath(@"X:\Users\Alex\code\a.cs"))], "SetClassification 后 Role(i) 读到分类结果");

        var raw = new FileTree(@"X:\");
        raw.AddNode(0, "a", 0, 1, 1, 0);
        ctx.True(Throws<InvalidOperationException>(() => PurposeClassifier.Classify(raw, CancellationToken.None)), "没 Seal 的树应该被拒绝");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        ctx.True(Throws<OperationCanceledException>(() => PurposeClassifier.Classify(tree, env, cts.Token)), "已取消的令牌应该抛 OperationCanceledException");

        var lone = new FileTree(@"X:\");
        lone.Seal();
        ctx.Equal(1, PurposeClassifier.Classify(lone, env, CancellationToken.None).Purposes.Length, "只有根节点的树");
    }

    // ---------------- 200 万节点性能 ----------------

    static void Scale(CheckContext ctx)
    {
        const int Total = 2_000_000;
        var s = new Scenario(SysDrive, capacity: Total + 4096);
        var tree = s.TreeFor(SysDrive);
        var gen = new Gen(tree, 20261016);
        (string Path, int Budget)[] regions =
        [
            (J(SysDrive, "Windows")!, 350_000), (Pf, 200_000), (J(Pf, "Microsoft Visual Studio")!, 80_000), (Local, 450_000),
            (Roaming, 40_000), (J(Profile, "Documents")!, 60_000), (J(Profile, "Downloads")!, 100_000), (J(Profile, ".nuget", "packages")!, 120_000),
            (J(Profile, ".cache", "huggingface")!, 5_000), (J(Lab, "Media")!, 80_000), (J(Lab, "SteamLibrary", "steamapps", "common")!, 100_000),
        ];
        // 个别机器的桌面/家目录可能不在系统盘上，这里只取在同一个卷里的
        string desk = Desktop.StartsWith(SysDrive, StringComparison.OrdinalIgnoreCase) ? Desktop : J(Lab, "desk")!;
        ctx.Time("合成 200 万节点树", () =>
        {
            foreach (var (path, budget) in regions)
                if (path.StartsWith(SysDrive, StringComparison.OrdinalIgnoreCase)) gen.Fill(s.DirId(path), budget, 0);
            for (int k = 0; k < 20; k++)
            {
                int proj = s.DirId(J(desk, "proj" + k)!);
                tree.AddNode(proj, "package.json", 0, 400, 4096, 0);
                gen.Fill(proj, 20_000, 3);
            }
            gen.Fill(s.DirId(J(Lab, "rest")!), Total - tree.Count, 0);
            tree.Seal();
            return 0;
        });
        ctx.Info($"节点数 {tree.Count:N0}");
        ctx.True(tree.Count >= Total - 10, "合成树够大");

        GC.Collect();
        var times = new double[7];
        Classification classified = null!;
        for (int k = 0; k < times.Length; k++)
        {
            var sw = Stopwatch.StartNew();
            classified = PurposeClassifier.Classify(tree, CancellationToken.None);
            times[k] = sw.Elapsed.TotalMilliseconds;
        }
        var result = classified.Purposes;
        var sorted = times.Order().ToArray();
        double median = sorted[sorted.Length / 2];
        ctx.Info($"200 万节点分类：中位数 {median:F0} ms，最快 {sorted[0]:F0} ms，最慢 {sorted[^1]:F0} ms（逐次 {string.Join(", ", times.Select(t => t.ToString("F0")))}）");
#if DEBUG
        ctx.Info("Debug 构建不断言耗时（要看数字请加 -c Release）");
#else
        ctx.True(median < 300, $"200 万节点分类中位数应小于 300 ms，实际 {median:F0} ms");
#endif

        // 分类到一半被取消：专用线程在开跑后约 5 ms 取消，整趟要 30 ms 以上，所以必然落在循环中间
        using var cts = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var killer = new Thread(() =>
        {
            started.Set();
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < 5) Thread.SpinWait(50);
            cts.Cancel();
        });
        killer.Start();
        started.Wait();
        ctx.True(Throws<OperationCanceledException>(() => PurposeClassifier.Classify(tree, cts.Token)), "分类到一半取消，应该及时抛 OperationCanceledException");
        killer.Join();

        tree.SetClassification(classified);
        var totals = tree.PurposeTotals(Metric.Alloc);
        ctx.Info("用途汇总（GB）："+ string.Join("，", totals.Select((t, i) => $"{Names[i]} {t / (double)(1L << 30):F1}")));
        foreach (byte k in new[] { Sys, Apps, Games, AppData, Dev, Sdk, Cache, Logs, Ai, Media, Archives })
            ctx.True(totals[k] > 0, $"合成树里「{Names[k]}」有字节");
        var roleCount = new int[Roles.Count];
        for (int i = 0; i < tree.Count; i++) roleCount[tree.Role(i)]++;
        ctx.Info("角色节点数：" + string.Join("，", roleCount.Select((n, r) => (n, r)).Where(x => x.n > 0).Select(x => $"{RoleNames[x.r]} {x.n:N0}")));
        foreach (byte r in new[] { Roles.System, Roles.Program, Roles.Runtime, Roles.UserData, Roles.Source, Roles.Dependency, Roles.PackageCache, Roles.Log, Roles.Game, Roles.GameAsset })
            ctx.True(roleCount[r] > 0, $"合成树里有角色「{RoleNames[r]}」的节点");
        var evidenceCount = new int[3];
        for (int i = 0; i < tree.Count; i++) evidenceCount[(int)tree.PurposeEvidence(i)]++;
        ctx.Info($"用途证据：{string.Join("，", evidenceCount.Select((n, e) => $"{Roles.EvidenceNames[e]} {n:N0}"))}；所属对象 {tree.Owners.Count:N0} 个（"
            + string.Join("，", tree.Owners.GroupBy(o => o.Kind).Select(g => $"{Roles.OwnerKindNames[(int)g.Key]} {g.Count():N0}")) + "）");
        ctx.True(evidenceCount.All(n => n > 0), "合成树里三档证据都有");
        ctx.Equal("开发项目「proj7」", tree.OwnerOf(tree.FindByPath(J(desk, "proj7", "package.json")!), out _) is { } po ? $"{Roles.OwnerKindNames[(int)po.Kind]}「{po.Name}」" : "无", "桌面项目里的文件归这个项目");
        ctx.Equal("系统组件「Windows」", tree.OwnerOf(tree.FindByPath(J(SysDrive, "Windows")!), out _) is { } wo ? $"{Roles.OwnerKindNames[(int)wo.Kind]}「{wo.Name}」" : "无", "Windows 目录归 Windows");
        ctx.Equal(Names[Sys], Names[result[tree.FindByPath(J(SysDrive, "Windows")!)]], "Windows 目录是系统");
        ctx.Equal(Names[Apps], Names[result[tree.FindByPath(Pf)]], "Program Files 是应用");
        ctx.Equal(Names[Dev], Names[result[tree.FindByPath(J(desk, "proj7")!)]], "桌面项目是代码");
        ctx.Equal(Names[Games], Names[result[tree.FindByPath(J(Lab, "SteamLibrary")!)]], "SteamLibrary 是游戏");
        ctx.Equal(Names[AppData], Names[result[tree.FindByPath(Local)]], "AppData\\Local 是应用数据");
    }

    // ---------------- 小工具 ----------------

    static bool Throws<T>(Action a) where T : Exception
    {
        try { a(); }
        catch (T) { return true; }
        return false;
    }

    static string? J(string? dir, params string[] parts)
    {
        if (dir is null) return null;
        foreach (var p in parts) dir = Path.Combine(dir, p);
        return dir;
    }

    /// 环境变量里的路径（写歪的、相对的、盘符根一律当没设置）。
    static string? Real(string? name) => name is null ? null : Usable(Environment.GetEnvironmentVariable(name));

    static string? Usable(string? v)
    {
        if (string.IsNullOrWhiteSpace(v) || !Path.IsPathFullyQualified(v)) return null;
        v = Path.GetFullPath(v).TrimEnd('\\');
        return Path.GetPathRoot(v + "\\")!.TrimEnd('\\').Length == v.Length ? null : v;
    }

    /// GOPATH、CONDA_PKGS_DIRS 这类列表变量只取第一项当样本。
    static string? RealFirst(string name) => Usable(Environment.GetEnvironmentVariable(name)?.Split(';', ',')[0]);

    /// 合成世界：按盘符根分树（真机的缓存目录散落在 C: 和 D:），记下「路径 → 期望用途」，最后一起核对。
    sealed class Scenario
    {
        readonly Dictionary<string, Synth> _byRoot = new(StringComparer.OrdinalIgnoreCase);
        readonly List<(string Path, byte Want, string Why)> _want = [];
        readonly List<(string Path, byte Want, string Why)> _roles = [];
        readonly List<(string Path, string Want, string Why)> _owners = [];
        readonly List<(string Path, Evidence Want, string Why)> _evidence = [];
        readonly string? _fixedRoot;
        readonly int _capacity;

        public Scenario(string? fixedRoot = null, int capacity = 1 << 12)
        {
            _fixedRoot = fixedRoot;
            _capacity = capacity;
        }

        Synth For(string path)
        {
            string root = _fixedRoot ?? Path.GetPathRoot(path)!;
            if (!_byRoot.TryGetValue(root, out var s)) _byRoot[root] = s = new Synth(root, _capacity);
            return s;
        }

        public FileTree TreeFor(string path) => For(path).Tree;

        public int DirId(string path) => For(path).Dir(path);

        public void Dir(string path, byte want, string why, NodeFlags flags = 0)
        {
            For(path).Dir(path, flags);
            _want.Add((path, want, why));
        }

        public void File(string path, byte want, string why, long size = MB, NodeFlags flags = 0)
        {
            For(path).File(path, size, flags);
            _want.Add((path, want, why));
        }

        /// 每个目录下放一个深一层的 x.dat：既验目录自己，也验子孙继承。
        public void Tree(byte want, string why, params string?[] dirs)
        {
            foreach (var d in dirs)
            {
                if (Usable(d) is not { } dir) continue;
                Dir(dir, want, why);
                File(Path.Combine(dir, "sub", "x.dat"), want, why + "·子孙继承");
            }
        }

        /// 已经登记过的节点，再要求它的数据角色。
        public void Role(string path, byte role, string why) => _roles.Add((path, role, why));

        /// 已经登记过的节点归哪个所属对象、这一处归属的证据；给了 root 就连所属对象的根路径一起核对。
        public void Owner(string path, OwnerKind kind, string name, Evidence evidence, string why, string? root = null) =>
            _owners.Add((path, Describe(kind, name, evidence, root), why));

        public void NoOwner(string path, string why) => _owners.Add((path, "无", why));

        /// 已经登记过的节点，用途的证据等级。
        public void PurposeEvidence(string path, Evidence want, string why) => _evidence.Add((path, want, why));

        static string Describe(OwnerKind kind, string name, Evidence evidence, string? root) =>
            $"{Roles.OwnerKindNames[(int)kind]}「{name}」{Roles.EvidenceNames[(int)evidence]}{(root is null ? "" : " @ " + root)}";

        string OwnerOf(string path, bool withRoot)
        {
            var t = TreeOf(path);
            var o = t.OwnerOf(Id(path), out int root);
            return o is null ? "无" : Describe(o.Kind, o.Name, t.OwnerEvidenceAt(root), withRoot ? t.GetPath(root) : null);
        }

        /// 只建节点、不要求用途（核对所属对象、证据时用）。
        public void Node(string path, long size = MB) => For(path).File(path, size, 0);

        /// 文件连同用途和角色一起登记。
        public void File(string path, byte want, byte role, string why, long size = MB)
        {
            File(path, want, why, size);
            Role(path, role, why);
        }

        public byte Of(string path) => For(path).Of(path);

        public FileTree TreeOf(string path) => For(path).Tree;

        public int Id(string path) => For(path).Id(path);

        /// 详情里的分类依据：Explain 读的是树上的用途，Verify 已经把分类结果写回树了，之后调用。
        public string Explain(string path, PurposeEnv env) => For(path).Explain(path, env);

        public void Verify(CheckContext ctx, PurposeEnv? env = null)
        {
            foreach (var s in _byRoot.Values) s.Run(env);
            foreach (var (path, want, why) in _want) ctx.Equal(Names[want], Names[Of(path)], $"{why}（{path}）");
            foreach (var (path, want, why) in _roles) ctx.Equal(RoleNames[want], RoleNames[TreeOf(path).Role(Id(path))], $"{why}·角色（{path}）");
            foreach (var (path, want, why) in _owners) ctx.Equal(want, OwnerOf(path, want.Contains(" @ ")), $"{why}·所属对象（{path}）");
            foreach (var (path, want, why) in _evidence)
                ctx.Equal(Roles.EvidenceNames[(int)want], Roles.EvidenceNames[(int)TreeOf(path).PurposeEvidence(Id(path))], $"{why}·用途证据（{path}）");
        }
    }

    sealed class Synth
    {
        readonly string _root;
        readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
        Classification? _result;

        public FileTree Tree { get; }

        public Synth(string root, int capacity)
        {
            _root = root.TrimEnd('\\');
            Tree = new FileTree(root, capacity);
            _ids[_root] = FileTree.RootId;
        }

        public int Dir(string path, NodeFlags extra = 0)
        {
            path = path.TrimEnd('\\');
            if (_ids.TryGetValue(path, out int id)) return id;
            if (!path.StartsWith(_root + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"{path} 不在 {_root} 树里");
            int parent = Dir(Path.GetDirectoryName(path)!);
            id = Tree.AddNode(parent, Path.GetFileName(path), NodeFlags.Directory | extra, 0, 0, 0);
            _ids[path] = id;
            return id;
        }

        public int File(string path, long size, NodeFlags flags)
        {
            path = path.TrimEnd('\\');
            int parent = Dir(Path.GetDirectoryName(path)!);
            int id = Tree.AddNode(parent, Path.GetFileName(path), flags, size, size, 0);
            _ids[path] = id;
            return id;
        }

        public void Run(PurposeEnv? env)
        {
            Tree.Seal();
            _result = env is null
                ? PurposeClassifier.Classify(Tree, CancellationToken.None)
                : PurposeClassifier.Classify(Tree, env, CancellationToken.None);
            Tree.SetClassification(_result);
        }

        public int Id(string path) => _ids[path.TrimEnd('\\')];

        public byte Of(string path) => _result!.Purposes[Id(path)];

        public string Explain(string path, PurposeEnv env) => PurposeClassifier.Explain(Tree, Id(path), env);
    }

    /// 确定性的随机树生成器：目录和文件的名字、扩展名、大小都有点样子，偶尔撒几个项目标记和大模型文件。
    sealed class Gen(FileTree tree, int seed)
    {
        static readonly string[] Exts =
            ["dll", "exe", "txt", "json", "js", "png", "jpg", "mp4", "dat", "bin", "log", "md", "cs", "mui", "pak", "xml", "pdf", "zip"];

        readonly Random _rng = new(seed);
        readonly char[] _buf = new char[48];
        int _n;

        public void Fill(int dir, int budget, int depth)
        {
            if (budget <= 0) return;
            // 太深就别再分叉了，剩下的当平铺文件塞完，免得递归失控
            int files = depth >= 14 ? budget : Math.Min(budget, _rng.Next(4, 40));
            for (int i = 0; i < files; i++) AddFile(dir);
            budget -= files;
            if (budget <= 0) return;
            if (_rng.Next(120) == 0)
            {
                tree.AddNode(dir, "package.json", 0, 300, 4096, 0);
                budget--;
            }
            int subs = _rng.Next(2, 8);
            for (int k = 0; k < subs && budget > 0; k++)
            {
                int left = subs - k;
                int share = left == 1 ? budget : Math.Min(budget, Math.Max(1, (int)(budget / (double)left * (0.5 + _rng.NextDouble()))));
                budget -= share;
                int d = tree.AddNode(dir, DirName(), NodeFlags.Directory, 0, 0, 0);
                Fill(d, share - 1, depth + 1);
            }
        }

        string DirName() => _rng.Next(60) switch
        {
            0 => "node_modules",
            1 => "models",
            2 => ".git",
            _ => "d" + _n++,
        };

        void AddFile(int dir)
        {
            string ext = _rng.Next(1500) == 0 ? "safetensors" : Exts[_rng.Next(Exts.Length)];
            long size = ext == "safetensors" ? 60L << 20 : 1L << _rng.Next(8, 24);
            _buf[0] = 'f';
            _n.TryFormat(_buf.AsSpan(1), out int w);
            _n++;
            int len = 1 + w;
            _buf[len++] = '.';
            ext.CopyTo(0, _buf, len, ext.Length);
            len += ext.Length;
            tree.AddNode(dir, _buf.AsSpan(0, len), 0, size, (size + 4095) & ~4095L, 0);
        }
    }
}
