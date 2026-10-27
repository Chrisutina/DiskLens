namespace DiskLens.Core;

/// DESIGN §4.7 里「按位置、名字、扩展名认」的那部分规则：环境 → 规范化完整路径字典，外加按名字、扩展名认的小判断。
/// 规则编号就是优先级，数字越小越优先；本类只管「谁命中哪条规则」，怎么继承、怎么覆盖归分类器。
/// 排序照 dise §7.3：有证据的具体数据角色（日志、缓存、模型、数据集）→ 所属容器（系统、SDK、游戏、应用、应用数据、项目）→ 扩展名兜底。
/// 两处例外是故意的：恢复数据（回收站、还原点、旧系统）整个是一个角色，里面的日志也算它的；
/// 用户文件夹只给「没有任何类型证据」的文件定用途，下载目录里的 ISO 是归档与镜像，不是文档。
internal sealed class PurposeRules
{
    public const byte
        RMeta = 1,       // 元文件、虚拟节点（分类器按标记认）
        RRecovery = 2,   // 回收站、还原点、恢复环境、旧系统
        RLogs = 3,       // 日志与诊断：目录名、诊断目录、日志和转储扩展名
        RCache = 4,      // 明确路径的缓存：包管理器、临时目录、Windows 更新缓存、Hugging Face 的 models--* / datasets--*（角色是模型数据 / 数据集）
        RSystem = 5,     // Windows 目录、卷根的系统文件
        RAi = 6,         // 模型目录（Ollama、LM Studio、ComfyUI 一类）、≥ 50 MB 的模型格式文件
        RDataset = 7,    // 各家库约定的数据集目录（Hugging Face hub 的 datasets--* 是下载缓存，走 RCache）
        RCacheName = 8,  // 按名字认的应用缓存目录（Cache、GPUCache……）
        RSdk = 9,
        RGames = 10,
        RApps = 11,
        RAppData = 12,   // AppData、ProgramData
        RDev = 13,       // 项目标记
        RType = 14,      // 扩展名兜底：文档与媒体、归档与镜像、数据库与数据集
        RUser = 15;      // 用户文件夹（桌面、文档、下载……）

    /// 一条规则都没命中。
    public const byte None = 16;

    /// 一条规则命中的结论：规则号（即优先级）、用途，以及规则本来就知道的数据角色（「这是 npm 缓存」→ 包管理器缓存）；
    /// 位置规则还可能知道整棵子树属于谁（Owner：npm、Windows、Google Chrome……），以及它的子目录各是什么（Kids）。
    /// Rule 为 0 表示没命中。
    public readonly record struct Hit(byte Rule, byte Purpose, byte Role, Kids Kids = Kids.None,
        OwnerKind Kind = OwnerKind.App, string? Owner = null, OwnerSource Source = OwnerSource.PathRule);

    static readonly string[] ShellNames = ["Desktop", "Documents", "Pictures", "Videos", "Music", "Downloads"];

    static readonly string[] RecoveryNames =
        ["$Recycle.Bin", "System Volume Information", "Recovery", "$WinREAgent", "Windows.old", "Config.Msi", "$Windows.~BT", "$Windows.~WS", "$GetCurrent", "$SysReset"];

    static readonly string[] SystemNames =
        ["Windows", "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp", "bootmgr", "BOOTNXT"];

    static readonly string[] AiHosts = ["ComfyUI", "stable-diffusion", "sd-webui", "Fooocus", "InvokeAI"];

    /// 规范化完整路径（不区分大小写，无尾部反斜杠）→ 命中；同一路径多条规则取更优先的。
    public readonly Dictionary<string, Hit> Paths = new(StringComparer.OrdinalIgnoreCase);

    /// Hugging Face 缓存的 hub 目录：直接子目录 models--* 是模型缓存、datasets--* 是数据集缓存（见 HubRepo）。
    public readonly HashSet<string> HfHubs = new(StringComparer.OrdinalIgnoreCase);

    /// 本身不能当项目根的目录：家目录里偶尔有个 package.json，别把整个家染成紫色。
    public readonly HashSet<string> NoProject = new(StringComparer.OrdinalIgnoreCase);

    /// 同上，连直接子目录也不行：AppData 下的 Local/Roaming、C:\Users 下每个人的家。
    public readonly HashSet<string> NoProjectKids = new(StringComparer.OrdinalIgnoreCase);

    void Add(Hit hit, string? path)
    {
        path = Full(path);
        if (path is null) return;
        if (!Paths.TryGetValue(path, out var cur) || hit.Rule < cur.Rule) Paths[path] = hit;
    }

    /// 环境变量值 → 规范化完整路径；写歪的、相对的、盘符根一律当没设置。
    internal static string? Full(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = Environment.ExpandEnvironmentVariables(v.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(v)) return null;
        try { v = Path.GetFullPath(v); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
        v = v.TrimEnd('\\');
        // 盘符根或共享根当规则目标会把整块盘染色，一条写歪的环境变量不值得这个代价
        return Path.GetPathRoot(v + "\\")?.TrimEnd('\\').Length == v.Length ? null : v;
    }

    static string? J(string? dir, params string[] parts)
    {
        if (dir is null) return null;
        foreach (var p in parts) dir = Path.Combine(dir, p);
        return dir;
    }

    public static PurposeRules Build(PurposeEnv env, string rootPath)
    {
        var r = new PurposeRules();
        string? V(string name) => Full(env.Var(name));
        string? F(Environment.SpecialFolder f) => Full(env.Folder(f));
        IEnumerable<string?> List(string name) =>
            (env.Var(name) ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Full);
        void Add(Hit hit, params string?[] paths)
        {
            foreach (var p in paths) r.Add(hit, p);
        }
        // 规则本身就说明了是谁的：包管理器、运行环境、Windows、浏览器
        static Hit Pm(string name) => new(RCache, Purposes.Cache, Roles.PackageCache, Kind: OwnerKind.PackageManager, Owner: name);
        static Hit Sdk(string name) => new(RSdk, Purposes.Sdk, Roles.Runtime, Kind: OwnerKind.Runtime, Owner: name);
        static Hit Win(byte rule, byte purpose, byte role, string name = "Windows") => new(rule, purpose, role, Kind: OwnerKind.System, Owner: name);
        static Hit Browser(string name) => new(RAppData, Purposes.AppData, Roles.BrowserProfile, Kind: OwnerKind.Browser, Owner: name);

        // 环境变量优先于默认位置；两边都收，因为搬家之前留在老地方的那份也还是缓存
        var profile = V("USERPROFILE") ?? F(Environment.SpecialFolder.UserProfile);
        var local = V("LOCALAPPDATA") ?? F(Environment.SpecialFolder.LocalApplicationData);
        var roaming = V("APPDATA") ?? F(Environment.SpecialFolder.ApplicationData);
        var progData = V("ProgramData") ?? F(Environment.SpecialFolder.CommonApplicationData);
        var pf = V("ProgramFiles") ?? F(Environment.SpecialFolder.ProgramFiles);
        var pf86 = V("ProgramFiles(x86)") ?? F(Environment.SpecialFolder.ProgramFilesX86);
        var pf64 = V("ProgramW6432");
        var win = V("SystemRoot") ?? F(Environment.SpecialFolder.Windows);
        var xdg = V("XDG_CACHE_HOME");
        var dotCache = J(profile, ".cache");
        var vol = Path.GetPathRoot(rootPath);

        // 2 恢复数据：回收站、还原点、恢复环境、旧系统、升级和重置的暂存目录，里面是什么都算它们的
        foreach (var name in RecoveryNames) Add(Win(RRecovery, Purposes.System, Roles.Recovery, name == "$Recycle.Bin" ? L.T("回收站", "Recycle Bin", "ごみ箱") : "Windows"), J(vol, name));

        // 3 日志与诊断（Logs、CrashDumps、WER 这类按名字认，在 DirName 里）
        Add(Win(RLogs, Purposes.Logs, Roles.Log), J(progData, "Microsoft", "Diagnosis"));

        // 4 缓存：包管理器（各自是所属对象）、临时目录（谁的都不是）、Windows 更新与传递优化、安装包缓存、缩略图缓存
        Add(Pm("npm"), V("npm_config_cache"), Full(env.NpmRc("cache")), J(local, "npm-cache"), J(roaming, "npm-cache"));
        Add(Pm("pnpm"), J(V("PNPM_HOME"), "store"), V("npm_config_store_dir"), Full(env.NpmRc("store-dir")), Full(env.PnpmConfig("storeDir")),
            J(local, "pnpm", "store"), J(local, "pnpm-cache"), J(xdg, "pnpm"), Full(env.NpmRc("cache-dir")), Full(env.PnpmConfig("cacheDir")));
        Add(Pm("Yarn"), V("YARN_CACHE_FOLDER"), J(local, "Yarn", "Cache"));
        Add(Pm("pip"), V("PIP_CACHE_DIR"), J(local, "pip", "Cache"));
        Add(Pm("uv"), V("UV_CACHE_DIR"), J(local, "uv", "cache"));
        Add(Pm("NuGet"), V("NUGET_PACKAGES"), J(profile, ".nuget", "packages"), V("NUGET_HTTP_CACHE_PATH"), J(local, "NuGet", "v3-cache"));
        Add(Pm("Gradle"), J(V("GRADLE_USER_HOME"), "caches"), J(profile, ".gradle", "caches"));
        Add(Pm("Maven"), J(profile, ".m2", "repository"));
        Add(Pm("Cargo"), J(V("CARGO_HOME"), "registry"), J(profile, ".cargo", "registry"));
        Add(Pm("Go"), V("GOCACHE"), J(local, "go-build"), V("GOMODCACHE"), J(profile, "go", "pkg", "mod"));
        Add(Pm("Puppeteer"), V("PUPPETEER_CACHE_DIR"), J(dotCache, "puppeteer"), J(xdg, "puppeteer"));
        Add(Pm("Playwright"), V("PLAYWRIGHT_BROWSERS_PATH"), J(local, "ms-playwright"));
        Add(Pm("Electron"), V("ELECTRON_CACHE"), J(local, "electron", "Cache"));
        Add(Pm("node-gyp"), J(local, "node-gyp", "Cache"));
        Add(Pm("Bun"), J(V("BUN_INSTALL"), "install", "cache"), J(profile, ".bun", "install", "cache"));
        Add(Pm("Composer"), V("COMPOSER_CACHE_DIR"), J(local, "Composer"));
        Add(Pm("Pub"), V("PUB_CACHE"), J(local, "Pub", "Cache"));
        foreach (var gopath in List("GOPATH")) Add(Pm("Go"), J(gopath, "pkg", "mod"));
        foreach (var pkgs in List("CONDA_PKGS_DIRS")) Add(Pm("conda"), pkgs);
        // %TEMP% 在用户名长的机器上是 C:\Users\ALEXAN~1\… 这种短名；Full 里的 Path.GetFullPath 会把它展开成树里的长名
        Add(new(RCache, Purposes.Cache, Roles.Temp), V("TEMP"), V("TMP"), J(local, "Temp"), J(win, "Temp"));
        // 安装包缓存（Package Cache）是装好的程序留着修复和卸载用的安装包，和更新下载归一类；它属于装它的那些程序，不归 Windows
        Add(Win(RCache, Purposes.Cache, Roles.UpdateCache), J(win, "SoftwareDistribution", "Download"),
            J(win, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"));
        Add(new(RCache, Purposes.Cache, Roles.UpdateCache), J(progData, "Package Cache"));
        Add(Win(RCache, Purposes.Cache, Roles.AppCache), J(local, "Microsoft", "Windows", "Explorer"));

        // 5 系统：Windows 目录和卷根的系统文件；DumpStack.log* 的通配由分类器扫卷根子节点补
        foreach (var name in SystemNames) Add(Win(RSystem, Purposes.System, Roles.System), J(vol, name));
        Add(Win(RSystem, Purposes.System, Roles.System), win);

        // 6 AI 与模型（Ollama 的 blobs 是共享的，只能算到 Ollama 头上）。Hugging Face hub 的仓库是下载缓存，归缓存（见 HubRepo）
        Add(new(RAi, Purposes.Ai, Roles.Weights, Owner: "Ollama"), V("OLLAMA_MODELS"), J(profile, ".ollama", "models"));
        Add(new(RAi, Purposes.Ai, Roles.Weights, Owner: "LM Studio"),
            J(profile, ".lmstudio", "models"), J(dotCache, "lm-studio", "models"), J(xdg, "lm-studio", "models"));
        foreach (var hub in new[] { V("HF_HUB_CACHE"), V("HUGGINGFACE_HUB_CACHE"), J(V("HF_HOME"), "hub"), J(dotCache, "huggingface", "hub"), J(xdg, "huggingface", "hub") })
            if (Full(hub) is { } full) r.HfHubs.Add(full);

        // 7 数据集：各家库约定的数据集下载目录（Hugging Face hub 里的 datasets--* 是下载缓存，归缓存，见 HubRepo）
        Add(new(RDataset, Purposes.Data, Roles.Dataset),
            V("HF_DATASETS_CACHE"), J(V("HF_HOME"), "datasets"), J(dotCache, "huggingface", "datasets"), J(xdg, "huggingface", "datasets"),
            V("TFDS_DATA_DIR"), J(profile, "tensorflow_datasets"), J(profile, ".keras", "datasets"),
            V("SCIKIT_LEARN_DATA"), J(profile, "scikit_learn_data"), J(dotCache, "kagglehub", "datasets"), J(xdg, "kagglehub", "datasets"));

        // 9 SDK 与运行环境。conda 的安装目录靠环境变量或默认位置猜，猜到了它的 pkgs 另算缓存
        // ponytail: 装在自定义位置又没设环境变量的 conda（比如 D:\Tools\conda）认不出来；要认再把 conda-meta 目录当标记
        Add(Sdk("Android SDK"), J(local, "Android", "Sdk"), V("ANDROID_SDK_ROOT"), V("ANDROID_HOME"), J(profile, ".android"), V("ANDROID_USER_HOME"));
        Add(Sdk("Rust"), J(profile, ".rustup"), V("RUSTUP_HOME"));
        Add(Sdk(".NET"), J(profile, ".dotnet"), J(pf, "dotnet"), V("DOTNET_ROOT"));
        Add(Sdk("Windows SDK"), J(pf86, "Windows Kits"));
        Add(Sdk("Visual Studio"), J(pf, "Microsoft Visual Studio"), J(pf86, "Microsoft Visual Studio"));
        Add(Sdk("Java"), J(profile, ".jdks"), J(pf, "Java"), J(pf, "Eclipse Adoptium"));
        Add(Sdk("Unity"), J(pf, "Unity", "Hub", "Editor"));
        Add(Sdk("Flutter"), J(profile, "flutter"));
        Add(Sdk("Node.js"), J(pf, "nodejs"));
        Add(Sdk("Python"), J(local, "Programs", "Python"));
        var conda = new List<string?> { V("CONDA_ROOT"), V("MAMBA_ROOT_PREFIX") };
        if (V("CONDA_PREFIX") is { } prefix)
        {
            int envs = prefix.IndexOf(@"\envs\", StringComparison.OrdinalIgnoreCase);
            conda.Add(envs > 0 ? prefix[..envs] : prefix);
        }
        foreach (var home in new[] { profile, local, progData })
            foreach (var name in new[] { "miniconda3", "anaconda3", "miniforge3", "mambaforge" })
                conda.Add(J(home, name));
        foreach (var dir in conda)
        {
            Add(Sdk("Conda"), dir);
            Add(Pm("conda"), J(dir, "pkgs"));
        }

        // 11 应用程序（游戏库和 WindowsApps 按名字认，在 DirName 里；游戏库比程序目录优先，Steam 客户端是应用、steamapps 是游戏）。
        // 下面每个目录按名字推测是一个应用；自己不带 .exe、子目录带的是厂商目录（Adobe\Photoshop），见分类器
        Add(new(RApps, Purposes.Apps, Roles.Program, Kids.Apps), pf, pf86, pf64, J(local, "Programs"));
        // 各家程序共用的组件；VS Code 系编辑器的扩展目录（用途算应用程序，dise §7.2「应用程序 → 扩展与插件」）
        Add(new(RApps, Purposes.Apps, Roles.Component), J(pf, "Common Files"), J(pf86, "Common Files"), J(pf64, "Common Files"));
        Add(new(RApps, Purposes.Apps, Roles.Extension, Kids.Plugins),
            J(profile, ".vscode", "extensions"), J(profile, ".vscode-insiders", "extensions"), J(profile, ".cursor", "extensions"));

        // 12 应用数据：AppData（Local、LocalLow、Roaming）和 ProgramData。里面的缓存、日志、SDK、安装的程序各有更优先的规则，
        // 剩下的配置、用户数据、浏览器资料才落到这里；应用自带的 package.json 也不再把它们翻成开发项目。
        // 下面每个目录按名字推测是一个应用（或厂商）；Packages 下是 Windows 应用包，目录名就是包名
        Add(new(RAppData, Purposes.AppData, Roles.UserData), J(profile, "AppData"));
        Add(new(RAppData, Purposes.AppData, Roles.UserData, Kids.AppData), local, roaming, J(profile, "AppData", "LocalLow"), progData);
        Add(new(RAppData, Purposes.AppData, Roles.UserData, Kids.Packages), J(local, "Packages"));
        // 浏览器的用户资料目录：书签、历史、扩展、登录状态（里面的 Cache 等目录按名字另算缓存）
        Add(Browser("Google Chrome"), J(local, "Google", "Chrome", "User Data"));
        Add(Browser("Microsoft Edge"), J(local, "Microsoft", "Edge", "User Data"));
        Add(Browser("Brave"), J(local, "BraveSoftware", "Brave-Browser", "User Data"));
        Add(Browser("Vivaldi"), J(local, "Vivaldi", "User Data"));
        Add(Browser("Chromium"), J(local, "Chromium", "User Data"));
        Add(Browser("Firefox"), J(roaming, "Mozilla", "Firefox"), J(local, "Mozilla", "Firefox"));
        Add(Browser("Opera"), J(roaming, "Opera Software", "Opera Stable"), J(local, "Opera Software", "Opera Stable"));

        // 10 游戏存档：Windows 的「保存的游戏」和文档里的 My Games（各游戏的存档和设置，比用户文件夹优先）；下面每个目录按名字推测是一个游戏
        var docs = new List<string?> { F(Environment.SpecialFolder.MyDocuments), J(profile, "Documents") };
        foreach (var oneDrive in new[] { V("OneDrive"), V("OneDriveConsumer"), V("OneDriveCommercial") }) docs.Add(J(oneDrive, "Documents"));
        Add(new(RGames, Purposes.Games, Roles.Save, Kids.Saves), J(profile, "Saved Games"));
        foreach (var d in docs) Add(new(RGames, Purposes.Games, Roles.Save, Kids.Saves), J(d, "My Games"));

        // 15 用户文件夹：shell 文件夹，含 OneDrive 下的同名文件夹。位置只说明在哪，不说明是什么，所以不给角色
        var shell = new List<string?>
        {
            F(Environment.SpecialFolder.DesktopDirectory), F(Environment.SpecialFolder.MyDocuments),
            F(Environment.SpecialFolder.MyPictures), F(Environment.SpecialFolder.MyVideos),
            F(Environment.SpecialFolder.MyMusic), Full(env.Downloads()),
        };
        foreach (var name in ShellNames) shell.Add(J(profile, name));
        foreach (var oneDrive in new[] { V("OneDrive"), V("OneDriveConsumer"), V("OneDriveCommercial") })
            foreach (var name in ShellNames) shell.Add(J(oneDrive, name));
        foreach (var dir in shell)
        {
            r.Add(new(RUser, Purposes.Media, Roles.Unknown), dir);
            if (dir is not null) r.NoProject.Add(dir);
        }

        // 项目根豁免：家、系统目录、Program Files、AppData 及其子目录、C:\Users 下的所有家
        foreach (var dir in new[] { profile, win, pf, pf86, pf64 })
            if (dir is not null) r.NoProject.Add(dir);
        foreach (var dir in new[] { Parent(profile), Parent(local), Parent(roaming), J(profile, "AppData") })
            if (dir is not null) r.NoProjectKids.Add(dir);
        return r;

        static string? Parent(string? p) => p is null ? null : Full(Path.GetDirectoryName(p));
    }

    // ---------- 按名字认（任意位置，不区分大小写） ----------

    static Hit Log(byte role) => new(RLogs, Purposes.Logs, role);
    static Hit Cache() => new(RCacheName, Purposes.Cache, Roles.AppCache);
    static Hit Games(Kids kids = Kids.Games) => new(RGames, Purposes.Games, Roles.Game, kids);

    /// 日志、缓存这种具体角色比所属容器优先：游戏里的 Logs 是日志，应用数据里的 GPUCache 是缓存。
    /// 只收专名，不收 log、tmp、data 这种谁都可能叫的名字（node_modules 里就有个包叫 log）。
    static readonly Dictionary<string, Hit> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Logs"] = Log(Roles.Log), ["LogFiles"] = Log(Roles.Log), ["PerfLogs"] = Log(Roles.Log),
        ["CrashDumps"] = Log(Roles.CrashDump), ["Crashpad"] = Log(Roles.CrashDump), ["Minidump"] = Log(Roles.CrashDump),
        ["LiveKernelReports"] = Log(Roles.CrashDump),
        ["CrashReports"] = Log(Roles.ErrorReport), ["Crash Reports"] = Log(Roles.ErrorReport), ["WER"] = Log(Roles.ErrorReport),

        // Chromium / Electron 的缓存目录、Firefox 的 cache2、显卡驱动的着色器缓存、IE 的 INetCache、Python 字节码
        ["Cache"] = Cache(), ["Caches"] = Cache(), ["cache2"] = Cache(), ["Code Cache"] = Cache(), ["GPUCache"] = Cache(),
        ["ShaderCache"] = Cache(), ["GrShaderCache"] = Cache(), ["DawnCache"] = Cache(), ["DawnGraphiteCache"] = Cache(),
        ["DawnWebGPUCache"] = Cache(), ["CacheStorage"] = Cache(), ["ScriptCache"] = Cache(), ["INetCache"] = Cache(),
        ["D3DSCache"] = Cache(), ["DXCache"] = Cache(), ["GLCache"] = Cache(), ["__pycache__"] = Cache(),

        // 各家游戏平台的库目录：子目录各是一个游戏（Steam 的在 steamapps\common 下面）
        ["steamapps"] = Games(Kids.SteamApps), ["SteamLibrary"] = Games(Kids.None), ["Epic Games"] = Games(), ["XboxGames"] = Games(),
        ["ModifiableWindowsApps"] = Games(), ["GOG Games"] = Games(), ["Riot Games"] = Games(), ["EA Games"] = Games(),
        ["Origin Games"] = Games(), ["Rockstar Games"] = Games(), ["Amazon Games"] = Games(),

        // Windows 应用包的安装目录，子目录名是「包名_版本_架构__发布者」
        ["WindowsApps"] = new(RApps, Purposes.Apps, Roles.Program, Kids.Packages),
    };

    static readonly Dictionary<string, Hit>.AlternateLookup<ReadOnlySpan<char>> NamedLookup = Named.GetAlternateLookup<ReadOnlySpan<char>>();

    /// 目录名规则：专名（日志、缓存、游戏库、WindowsApps），外加两种看父目录的：GOG Galaxy、Ubisoft 启动器下的 Games 是游戏，
    /// 父目录名带 ComfyUI 等字样的 models 是 AI 模型。Rule 为 0 表示没命中。
    public static Hit DirName(ReadOnlySpan<char> name, ReadOnlySpan<char> parent)
    {
        const StringComparison C = StringComparison.OrdinalIgnoreCase;
        if (NamedLookup.TryGetValue(name, out var hit)) return hit;
        if (name.Equals("Games", C) && (parent.Equals("GOG Galaxy", C) || parent.Equals("Ubisoft Game Launcher", C))) return Games();
        if (name.Equals("models", C))
            foreach (var host in AiHosts)
                if (parent.StartsWith(host, C) && (parent.Length == host.Length || parent[host.Length] is '-' or '_'))
                    return new(RAi, Purposes.Ai, Roles.Weights);
        return default;
    }

    /// Hugging Face hub 下的仓库（传入 hub 之后的相对路径）：models--* 是模型缓存，datasets--* 是数据集缓存，spaces--* 和杂项不认。
    /// 两种仓库都是 huggingface_hub 按需下载、能重新拉取的缓存（dise §7.3「明确的模型缓存主归缓存、标注模型数据」），
    /// 所以主用途都是缓存，角色分别是模型数据、数据集；规则号和其他明确位置的缓存一样，里面的大模型文件不会被扩展名拉回 AI。
    /// 仓库名「models--组织--名字」「datasets--组织--名字」就是包信息，所属对象是「组织/名字」这个模型或数据集。
    public static Hit HubRepo(ReadOnlySpan<char> repo)
    {
        const StringComparison C = StringComparison.OrdinalIgnoreCase;
        int slash = repo.IndexOf('\\');
        if (slash >= 0) repo = repo[..slash];
        if (repo.StartsWith("models--", C))
            return new(RCache, Purposes.Cache, Roles.ModelData, Kind: OwnerKind.Model, Owner: RepoName(repo[8..]), Source: OwnerSource.HubRepo);
        if (repo.StartsWith("datasets--", C))
            return new(RCache, Purposes.Cache, Roles.Dataset, Kind: OwnerKind.Dataset, Owner: RepoName(repo[10..]), Source: OwnerSource.HubRepo);
        return default;

        static string? RepoName(ReadOnlySpan<char> s) => s.IsEmpty ? null : s.ToString().Replace("--", "/");
    }

    // ---------- 容器的子目录各是谁（所属对象） ----------

    /// 父目录说「子目录各是什么」时，这个子目录算哪个所属对象（种类、来源、名字）；不算就是 null。
    /// 厂商目录要看子目录里有没有 .exe，这里不管，调用方自己判断。
    public static (OwnerKind Kind, OwnerSource Source, string Name)? KidOwner(Kids up, ReadOnlySpan<char> name) => up switch
    {
        Kids.Games when !IsLauncher(name) => (OwnerKind.Game, OwnerSource.GameLibrary, name.ToString()),
        Kids.Packages when name.IndexOf('_') > 0 => (OwnerKind.App, OwnerSource.AppPackage, name[..name.IndexOf('_')].ToString()),
        Kids.Plugins => (OwnerKind.Plugin, OwnerSource.Plugin, PluginName(name)),
        Kids.Apps => (OwnerKind.App, OwnerSource.ProgramDir, name.ToString()),
        Kids.Vendor => (OwnerKind.App, OwnerSource.VendorApp, name.ToString()),
        Kids.AppData => (OwnerKind.App, OwnerSource.AppDataDir, name.ToString()),
        Kids.Saves => (OwnerKind.Game, OwnerSource.SaveDir, name.ToString()),
        _ => null,
    };

    /// 游戏库里不是游戏的东西：平台启动器本体和运行库（DirectXRedist、GameInputRedist……）。
    static bool IsLauncher(ReadOnlySpan<char> name)
    {
        const StringComparison C = StringComparison.OrdinalIgnoreCase;
        return name.Equals("Launcher", C) || name.Equals("Riot Client", C) || name.Equals("Steamworks Shared", C) || name.Contains("Redist", C);
    }

    /// VS Code 系扩展的目录名是「发布者.扩展名-版本[-平台]」：从第一个「-数字」起去掉。
    static string PluginName(ReadOnlySpan<char> dir)
    {
        for (int i = 1; i + 1 < dir.Length; i++)
            if (dir[i] == '-' && char.IsAsciiDigit(dir[i + 1])) return dir[..i].ToString();
        return dir.ToString();
    }

    // ---------- 角色细分（不改用途和规则号，只改角色） ----------

    enum Part : byte { None, Deps, Build, Mods, Saves, Plugins }

    static readonly Dictionary<string, Part> Parts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["node_modules"] = Part.Deps, ["bower_components"] = Part.Deps, ["jspm_packages"] = Part.Deps, ["vendor"] = Part.Deps,
        [".venv"] = Part.Deps, ["venv"] = Part.Deps, ["Pods"] = Part.Deps,
        ["target"] = Part.Build, ["obj"] = Part.Build, ["bin"] = Part.Build, ["dist"] = Part.Build, ["build"] = Part.Build,
        ["out"] = Part.Build, [".next"] = Part.Build, [".nuxt"] = Part.Build, [".svelte-kit"] = Part.Build, [".turbo"] = Part.Build,
        [".parcel-cache"] = Part.Build, [".gradle"] = Part.Build, [".angular"] = Part.Build,
        ["mods"] = Part.Mods, ["workshop"] = Part.Mods,
        ["SaveGames"] = Part.Saves, ["Saves"] = Part.Saves, ["SaveData"] = Part.Saves,
        ["plugins"] = Part.Plugins, ["extensions"] = Part.Plugins, ["addons"] = Part.Plugins, ["add-ons"] = Part.Plugins, ["AddIns"] = Part.Plugins,
    };

    static readonly Dictionary<string, Part>.AlternateLookup<ReadOnlySpan<char>> PartsLookup = Parts.GetAlternateLookup<ReadOnlySpan<char>>();

    /// 容器里按目录名细分角色：项目里的 node_modules 是项目依赖、target 是构建产物（cmake-build-* 也是），
    /// 游戏里的 Mods 是模组、SaveGames 是存档，程序、应用数据、浏览器资料里的 plugins / extensions 是扩展与插件。
    /// 只在对应的容器里认：别处叫 bin、build、extensions 的目录什么都说明不了。
    public static byte Refine(byte role, ReadOnlySpan<char> name)
    {
        if (role is not (Roles.Source or Roles.Game or Roles.Program or Roles.UserData or Roles.BrowserProfile)) return role;
        var part = PartsLookup.TryGetValue(name, out var p) ? p : Part.None;
        return role switch
        {
            Roles.Source => part == Part.Deps ? Roles.Dependency
                : part == Part.Build || name.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase) ? Roles.BuildOutput : role,
            Roles.Game => part switch { Part.Mods => Roles.Mod, Part.Saves => Roles.Save, Part.Plugins => Roles.Extension, _ => role },
            _ => part == Part.Plugins ? Roles.Extension : role,
        };
    }

    /// 容器的默认角色遇到文件时按扩展名细分：游戏里的程序是游戏本体、配置文件是配置、其余是游戏资源；
    /// 程序目录和应用数据里的配置文件是配置。别的角色（缓存、日志、源代码……）文件和目录一样。
    public static byte FileRole(byte role, bool exec, bool config) => role switch
    {
        Roles.Game => exec ? Roles.Game : config ? Roles.Config : Roles.GameAsset,
        Roles.Program or Roles.UserData => config ? Roles.Config : role,
        _ => role,
    };

    // ---------- 按扩展名认 ----------

    /// 日志、转储、事件追踪：文件自己就说明了角色，比所在容器优先（游戏目录里的 .log 是日志与诊断）。
    public static bool IsLogExtension(string ext) => ext is "log" or "etl" or "evtx" or "evt" or "blg" or "dmp" or "mdmp" or "hdmp" or "wer";

    /// 日志类扩展名各自的角色：转储是崩溃转储，.wer 是错误报告，其余是日志。
    public static byte LogRole(string ext) => ext is "dmp" or "mdmp" or "hdmp" ? Roles.CrashDump : ext == "wer" ? Roles.ErrorReport : Roles.Log;

    /// 模型格式。只认这三种，而且要 ≥ 50 MB；.ckpt/.pt/.pth、.bin 也是普通检查点、数据和存档的扩展名，不凭扩展名算 AI。
    public static bool IsModelExtension(string ext) => ext is "safetensors" or "gguf" or "onnx";

    /// 数据库和数据集格式。和 .bin、.dat 不同，这些格式本身就说明了是结构化数据。
    public static bool IsDataExtension(string ext) =>
        ext is "db" or "sqlite" or "sqlite3" or "db3" or "mdb" or "accdb" or "mdf" or "ndf" or "ldf" or "ibd"
            or "parquet" or "arrow" or "feather" or "npy" or "npz" or "h5" or "hdf5" or "tfrecord";

    /// 列式、数组格式是数据集；其余数据格式是数据库。
    static bool IsDatasetExtension(string ext) => ext is "parquet" or "arrow" or "feather" or "npy" or "npz" or "h5" or "hdf5" or "tfrecord";

    /// 磁盘镜像里的虚拟机磁盘；其余（iso、wim、esd……）是安装镜像。
    static bool IsVirtualDiskExtension(string ext) => ext is "vhd" or "vhdx" or "avhdx" or "vmdk" or "vdi" or "qcow2" or "ova" or "gho";

    /// 一眼就是配置的扩展名。.json、.xml、.yaml 什么都装，不算。
    public static bool IsConfigExtension(string ext) => ext is "ini" or "cfg" or "conf" or "config" or "cnf" or "prefs" or "properties";

    /// 扩展名兜底（最后一级证据，只在没有任何容器规则时生效）：数据库与数据集、文档与媒体、归档与镜像；都不是就是未识别。
    public static byte TypePurpose(byte k, string ext) =>
        IsDataExtension(ext) ? Purposes.Data
        : k is FileCategories.Video or FileCategories.Image or FileCategories.Audio or FileCategories.Document ? Purposes.Media
        : k is FileCategories.Archive or FileCategories.DiskImage ? Purposes.Archives
        : Purposes.Unknown;

    /// 扩展名兜底时顺带的角色：文档、图片、视频、音频，数据库、数据集，压缩包、安装镜像、虚拟磁盘。
    public static byte TypeRole(byte k, string ext) =>
        IsDataExtension(ext) ? (IsDatasetExtension(ext) ? Roles.Dataset : Roles.Database)
        : k switch
        {
            FileCategories.Video => Roles.Video,
            FileCategories.Image => Roles.Image,
            FileCategories.Audio => Roles.Audio,
            FileCategories.Document => Roles.Document,
            FileCategories.Archive => Roles.Archive,
            FileCategories.DiskImage => IsVirtualDiskExtension(ext) ? Roles.VirtualDisk : Roles.InstallImage,
            _ => Roles.Unknown,
        };

    // ---------- 标记 ----------

    /// 游戏安装信息：Battle.net 的游戏没有统一的库目录，各自装在 Program Files 里，根下都有一份 CASC 的 .build.info。
    public static bool IsGameMarker(ReadOnlySpan<char> n) => n.Length == 11 && n.Equals(".build.info", StringComparison.OrdinalIgnoreCase);

    /// 虚拟分组目录，按名字认一遍，防止哪天扫描器忘了给它们打元文件标记。
    /// 名字跟着界面语言，三种语言的名字都认。
    public static bool IsMetaGroup(ReadOnlySpan<char> name)
    {
        foreach (var n in MftTreeBuilder.MetaNames) if (name.StartsWith(n, StringComparison.Ordinal)) return true;
        foreach (var n in MftTreeBuilder.OrphanNames) if (name.StartsWith(n, StringComparison.Ordinal)) return true;
        return false;
    }

    /// 项目标记（不含 *.sln/*.slnx/*.csproj，那三个按扩展名 ID 认，省得再比一遍字符串）。
    /// 先按长度分流：绝大多数名字连一次比较都用不上。
    public static bool IsMarker(ReadOnlySpan<char> n)
    {
        const StringComparison C = StringComparison.OrdinalIgnoreCase;
        return n.Length switch
        {
            4 => n.Equals(".git", C),
            6 => n.Equals("go.mod", C),
            7 => n.Equals("pom.xml", C) || n.Equals("Gemfile", C),
            8 => n.Equals("setup.py", C),
            9 => n.Equals("deno.json", C),
            10 => n.Equals("Cargo.toml", C),
            12 => n.Equals("package.json", C) || n.Equals("build.gradle", C) || n.Equals("pubspec.yaml", C),
            13 => n.Equals("composer.json", C),
            14 => n.Equals("pyproject.toml", C) || n.Equals("CMakeLists.txt", C),
            16 => n.Equals("requirements.txt", C) || n.Equals("build.gradle.kts", C),
            _ => false,
        };
    }
}

/// 目录的直接子目录各是什么——所属对象靠它认（PurposeRules.KidOwner）。
internal enum Kids : byte
{
    None,
    Apps,       // Program Files 系：每个子目录按名字推测是一个应用（自己不带 .exe、子目录带的是厂商目录）
    Vendor,     // 厂商目录：直接带 .exe 的子目录各是一个应用
    AppData,    // AppData 的 Local / Roaming / LocalLow、ProgramData：每个子目录按名字推测是一个应用或厂商
    Packages,   // WindowsApps、AppData\Local\Packages：子目录名里第一个下划线前面是 Windows 应用包名
    Plugins,    // VS Code 系的扩展目录：子目录名是「发布者.扩展名-版本」
    Saves,      // Saved Games、My Games：每个子目录按名字推测是一个游戏
    Games,      // 游戏库：每个子目录是一个游戏
    SteamApps,  // steamapps：里面的 common 才是游戏库
}

/// 所属对象是凭什么认出来的：决定证据等级（ProgramDir 之前的是已确认，之后的是按目录名推测）和详情里的说法。
internal enum OwnerSource : byte
{
    PathRule,     // 位置规则本身说明了是谁的：npm 缓存、Windows、Google Chrome 的用户资料
    Project,      // 项目标记
    GameInstall,  // 游戏安装信息 .build.info
    GameLibrary,  // 游戏库里的游戏文件夹
    HubRepo,      // Hugging Face 缓存仓库名
    AppPackage,   // Windows 应用包名
    Plugin,       // 扩展目录名
    ProgramDir,   // Program Files 下的目录名
    VendorApp,    // 厂商目录下直接带 .exe 的目录名
    AppDataDir,   // AppData、ProgramData 下的目录名
    SaveDir,      // 存档目录下的目录名
}
