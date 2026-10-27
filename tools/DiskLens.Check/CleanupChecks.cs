using DiskLens.Core;

namespace DiskLens.Check;

// 清理建议与清理脚本（DESIGN §6）的自检。
// 树全是内存里合成的，路径用本机真实的环境变量和特殊文件夹拼——这台机器的缓存都搬去 D 盘了，正好拿来验「环境变量优先」。
// 会碰磁盘的只有 .sandbox/cleanup 里的两类测试：Run 的实删（只删沙箱）、PowerShell 辅助函数的实跑（只跑沙箱）。
// 期望值按设计文档独立再写一遍，不复用被测代码里的解析。
static partial class CleanupChecks
{
    const long KB = 1L << 10, MB = 1L << 20, GB = 1L << 30;
    const StringComparison Ic = StringComparison.OrdinalIgnoreCase;

    static long DaysAgo(double days) => DateTime.UtcNow.AddDays(-days).ToFileTimeUtc();

    /// 上一轮失败可能留下只读文件，CheckContext.Sandbox 的整棵删除清不掉它们（和脚本里的坑是同一个）；先把属性抹平，不跟随 junction。
    static void ClearReadOnly(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            if (attributes.HasFlag(FileAttributes.Directory)) ClearReadOnly(entry);
            else if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
        }
    }

    public static void Run(CheckContext ctx)
    {
        ClearReadOnly(CheckContext.SandboxDir("cleanup"));
        var sandbox = ctx.Sandbox();
        var full = new Full(new Layout(@"Q:\"));
        RuleTable(ctx, full);
        Ordering(ctx, full);
        DisplayPaths(ctx, full);
        ItemsCoverage(ctx, full);
        RealEnvironment(ctx);
        EnvironmentPriority(ctx);
        NearMisses(ctx);
        NodeModules(ctx);
        BuildOutputs(ctx);
        OldDownloads(ctx);
        NoDoubleCounting(ctx);
        SubtreeGuard(ctx);
        LargeFiles(ctx);
        Caps(ctx);
        Scale(ctx);
        CommandWhitelist(ctx, full);
        RunGuards(ctx, sandbox);
        RunExecutes(ctx, sandbox);
        ScriptContent(ctx, full);
        ScriptValidation(ctx, full);
        ScriptHostileNames(ctx);
        ScriptProtection(ctx);
        ScriptParses(ctx, full, sandbox);
        ScriptExecutes(ctx, sandbox);
        if (ctx.Option("scan") is { } path) ScanReport(ctx, path);
    }

    /// check cleanup --scan <路径>：只读枚举一个真实目录，用真实环境跑 Analyze，肉眼核对建议。不参与断言，也不会删任何东西。
    static void ScanReport(CheckContext ctx, string path)
    {
        var tree = ctx.Time($"扫描 {path}", () => WalkScanner.Scan(path, new ScanCounters(), CancellationToken.None));
        tree.Seal();
        var cc = new CleanupContext(false, Path.GetPathRoot(Path.GetFullPath(path))!, path, IntPtr.Zero);
        var list = ctx.Time("Analyze", () => CleanupAdvisor.Analyze(tree, cc, CancellationToken.None));
        foreach (var s in list)
            ctx.Info($"{s.Group,-9} {s.Id,-18} {Fmt(s.Bytes),10} {s.Count,9:N0} 个文件  {s.Action}{(s.Command is null ? "" : "：" + s.Command)}  {s.DisplayPath}");
    }

    // ---------------- 合成树的小工具 ----------------

    /// 一棵内存树，按完整路径造节点。目录的修改时间只在第一次创建时生效，所以要控制目录时间就先 Dir 再放文件。
    sealed class Sim
    {
        readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
        public string Root { get; }
        public FileTree Tree { get; }

        public Sim(string root)
        {
            Root = root;
            Tree = new FileTree(root, 1 << 10);
            _ids[root.TrimEnd('\\')] = FileTree.RootId;
        }

        public int Dir(string path, NodeFlags flags = 0, long mtime = 0)
        {
            path = path.TrimEnd('\\');
            if (_ids.TryGetValue(path, out int id)) return id;
            int parent = Dir(Path.GetDirectoryName(path) ?? throw new ArgumentException("没有父目录：" + path));
            return _ids[path] = Tree.AddNode(parent, Path.GetFileName(path), NodeFlags.Directory | flags, 0, 0, mtime);
        }

        public int File(string path, long size = MB, NodeFlags flags = 0, long mtime = 0)
        {
            int parent = Dir(Path.GetDirectoryName(path) ?? throw new ArgumentException("没有父目录：" + path));
            return _ids[path] = Tree.AddNode(parent, Path.GetFileName(path), flags, size, size, mtime);
        }

        public int Id(string path) => _ids[path.TrimEnd('\\')];
        public string PathOf(int id) => Tree.GetPath(id);
        public void Seal() => Tree.Seal();
    }

    /// 按盘符分树；fixedRoot 不为空时只有一棵树（沙箱里的假 C 盘不是真的盘根）。
    sealed class World(string? fixedRoot = null)
    {
        readonly Dictionary<string, Sim> _sims = new(StringComparer.OrdinalIgnoreCase);
        public IEnumerable<Sim> Sims => _sims.Values;

        public Sim For(string path)
        {
            string root = fixedRoot ?? Path.GetPathRoot(path)!;
            if (!_sims.TryGetValue(root, out var s)) _sims[root] = s = new Sim(root);
            return s;
        }

        public int Dir(string path, NodeFlags flags = 0, long mtime = 0) => For(path).Dir(path, flags, mtime);
        public int File(string path, long size = MB, NodeFlags flags = 0, long mtime = 0) => For(path).File(path, size, flags, mtime);

        public List<Hit> Analyze(PurposeEnv? env = null, bool admin = false)
        {
            var all = new List<Hit>();
            foreach (var s in _sims.Values)
            {
                s.Seal();
                var context = new CleanupContext(admin, s.Root, s.Root, IntPtr.Zero);
                foreach (var x in CleanupAdvisor.Analyze(s.Tree, context, CancellationToken.None, env ?? PurposeEnv.Real)) all.Add(new(s, x));
            }
            return all;
        }
    }

    sealed record Hit(Sim Sim, SuggestionDto S)
    {
        public string[] Paths => S.NodeIds.Select(Sim.PathOf).ToArray();
    }

    /// 一套假盘的标准位置。TreeRoot 可以是 Q:\，也可以是沙箱里的一个目录。
    sealed record Layout(string TreeRoot)
    {
        public string R => TreeRoot.TrimEnd('\\');
        public string Win => R + @"\Windows";
        public string Profile => R + @"\Users\u";
        public string Local => Profile + @"\AppData\Local";
        public string Roaming => Profile + @"\AppData\Roaming";
        public string ProgramData => R + @"\ProgramData";
        public string Temp => Local + @"\Temp";
        public string Downloads => Profile + @"\Downloads";
        /// 合成树没有真扫描：扫描开始时刻随便给一个，文件系统按 NTFS（脚本才会写目录类的删除）
        public CleanupContext Ctx(bool admin = false) => new(admin, TreeRoot, TreeRoot, IntPtr.Zero, 1_760_000_000_000, "NTFS");

        /// DisplayPath 的期望写法：用户目录前缀换成 ~。
        public string Disp(string path) => path.StartsWith(Profile + "\\", Ic) ? "~" + path[Profile.Length..] : path;
    }

    static PurposeEnv FakeEnv(Layout l, Dictionary<string, string>? vars = null, string? npmCache = null, string? pnpmStore = null, string? downloads = null)
    {
        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = l.Profile, ["LOCALAPPDATA"] = l.Local, ["APPDATA"] = l.Roaming,
            ["SystemRoot"] = l.Win, ["ProgramData"] = l.ProgramData, ["TEMP"] = l.Temp,
        };
        if (vars != null) foreach (var (k, x) in vars) v[k] = x;
        return new PurposeEnv
        {
            Var = n => v.GetValueOrDefault(n),
            Folder = f => f switch
            {
                Environment.SpecialFolder.UserProfile => l.Profile,
                Environment.SpecialFolder.LocalApplicationData => l.Local,
                Environment.SpecialFolder.ApplicationData => l.Roaming,
                Environment.SpecialFolder.Windows => l.Win,
                Environment.SpecialFolder.CommonApplicationData => l.ProgramData,
                _ => null,
            },
            Downloads = () => downloads,
            NpmRc = k => k == "cache" ? npmCache : null,
            PnpmConfig = k => k == "storeDir" ? pnpmStore : null,
        };
    }

    static string Set(IEnumerable<string> paths) => string.Join(" | ", paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

    static Hit? Get(CheckContext ctx, List<Hit> hits, string id, string why)
    {
        var m = hits.Where(h => h.S.Id == id).ToList();
        ctx.Equal(1, m.Count, $"{why}：{id} 应该恰好命中一次");
        return m.Count == 1 ? m[0] : null;
    }

    static void Absent(CheckContext ctx, List<Hit> hits, string id, string why) =>
        ctx.True(hits.All(h => h.S.Id != id), $"{why}：{id} 不该出现");

    /// 命中、节点集合、大小一起断言。
    static void Expect(CheckContext ctx, List<Hit> hits, string id, long bytes, string[] nodes, string why)
    {
        if (Get(ctx, hits, id, why) is not { } h) return;
        ctx.Equal(Set(nodes), Set(h.Paths), $"{why}：{id} 的节点");
        ctx.Equal(bytes, h.S.Bytes, $"{why}：{id} 的大小");
    }

    // ---------------- 一张什么都命中的假 C 盘 ----------------

    /// 每条规则各放一份命中、再放几份「长得像但不该命中」的干扰项。
    /// 大小全用整数 MB / GB，期望值直接是常量。
    sealed class Full
    {
        public Layout L { get; }
        public World W { get; }
        public PurposeEnv Env { get; }
        public List<Hit> Hits { get; }
        public Dictionary<string, (long Bytes, string[] Nodes)> Want { get; } = new();

        public Full(Layout l)
        {
            L = l;
            W = new World(l.TreeRoot);
            Env = FakeEnv(l);
            Build();
            Hits = W.Analyze(Env);
        }

        void Build()
        {
            var l = L;
            long Put(string path, long size, long mtime = 0, NodeFlags flags = 0) { W.File(path, size, flags, mtime); return size; }

            // 临时文件、回收站、更新缓存、传递优化
            Want["user-temp"] = (Put(l.Temp + @"\a.tmp", 3 * MB), [l.Temp]);
            Want["win-temp"] = (Put(l.Win + @"\Temp\b.tmp", 2 * MB), [l.Win + @"\Temp"]);
            Want["recycle-bin"] = (Put(l.R + @"\$Recycle.Bin\S-1-5-21-1\$R1.bin", 10 * MB), [l.R + @"\$Recycle.Bin"]);
            Want["wu-download"] = (Put(l.Win + @"\SoftwareDistribution\Download\x\pkg.cab", 5 * MB), [l.Win + @"\SoftwareDistribution\Download"]);
            string doCache = l.Win + @"\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache";
            Want["delivery-opt"] = (Put(doCache + @"\c1", 1 * MB), [doCache]);

            // 缩略图：只认资源管理器目录下直接放着的 thumbcache_*.db / iconcache_*.db（不分大小写），其余都是干扰项
            string explorer = l.Local + @"\Microsoft\Windows\Explorer";
            long thumbs = Put(explorer + @"\thumbcache_256.db", 1 * MB) + Put(explorer + @"\iconcache_idx.db", 512 * KB) + Put(explorer + @"\ThumbCache_idx.DB", 2 * MB);
            Put(explorer + @"\other.db", 9 * MB); Put(explorer + @"\thumbcache_256.txt", 7 * MB);
            Put(explorer + @"\sub\thumbcache_9.db", 6 * MB); Put(explorer + @"\iconcache_dir.db\inner.bin", 4 * MB);
            Want["thumb-cache"] = (thumbs, [explorer + @"\thumbcache_256.db", explorer + @"\iconcache_idx.db", explorer + @"\ThumbCache_idx.DB"]);

            // 浏览器：每个 Profile 下的几个缓存目录；登录数据、本地存储、Firefox 的 places 都不能碰
            string chrome = l.Local + @"\Google\Chrome\User Data", edge = l.Local + @"\Microsoft\Edge\User Data",
                brave = l.Local + @"\BraveSoftware\Brave-Browser\User Data", firefox = l.Local + @"\Mozilla\Firefox\Profiles\abc.default";
            long browsers = Put(chrome + @"\Default\Cache\f", 3 * MB) + Put(chrome + @"\Default\Code Cache\js\g", 1 * MB) + Put(chrome + @"\Default\GPUCache\h", 1 * MB)
                + Put(chrome + @"\Profile 1\DawnGraphiteCache\i", 1 * MB) + Put(chrome + @"\Profile 1\DawnWebGPUCache\j", 1 * MB)
                + Put(edge + @"\Default\Cache\k", 2 * MB) + Put(brave + @"\Default\Cache\l", 1 * MB) + Put(firefox + @"\cache2\entries\m", 4 * MB);
            Put(chrome + @"\Default\Login Data", 50 * MB); Put(chrome + @"\Default\Local Storage\leveldb\x", 40 * MB);
            Put(firefox + @"\places.sqlite", 30 * MB); Put(firefox + @"\startupCache\sc", 5 * MB);
            Want["browser-cache"] = (browsers, [chrome + @"\Default\Cache", chrome + @"\Default\Code Cache", chrome + @"\Default\GPUCache",
                chrome + @"\Profile 1\DawnGraphiteCache", chrome + @"\Profile 1\DawnWebGPUCache", edge + @"\Default\Cache", brave + @"\Default\Cache", firefox + @"\cache2"]);

            // 崩溃转储：每个位置的直接子项 + MEMORY.DMP 本身；WER 的兄弟目录不碰
            string wer = l.ProgramData + @"\Microsoft\Windows\WER", userWer = l.Local + @"\Microsoft\Windows\WER";
            long dumps = Put(l.Local + @"\CrashDumps\a.dmp", 2 * MB) + Put(l.Win + @"\Minidump\m.dmp", 1 * MB) + Put(l.Win + @"\MEMORY.DMP", 3 * MB)
                + Put(wer + @"\ReportArchive\Report1\r.wer", 1 * MB) + Put(wer + @"\ReportQueue\q1\q.wer", 1 * MB) + Put(userWer + @"\Temp\t.tmp", 1 * MB);
            Put(wer + @"\Other\z", 8 * MB);
            Want["crash-dumps"] = (dumps, [l.Local + @"\CrashDumps\a.dmp", l.Win + @"\Minidump\m.dmp", l.Win + @"\MEMORY.DMP", wer + @"\ReportArchive\Report1",
                wer + @"\ReportQueue\q1", userWer + @"\Temp"]);

            // 开发缓存：每种一条。.cargo\registry\src 和 gradle 的 wrapper 不是缓存目录
            Want["dev-caches-npm"] = (Put(l.Local + @"\npm-cache\_cacache\x", 12 * MB), [l.Local + @"\npm-cache"]);
            Want["dev-caches-pip"] = (Put(l.Local + @"\pip\Cache\p", 2 * MB), [l.Local + @"\pip\Cache"]);
            Want["dev-caches-nuget"] = (Put(l.Local + @"\NuGet\v3-cache\n", 3 * MB), [l.Local + @"\NuGet\v3-cache"]);
            Want["dev-caches-yarn"] = (Put(l.Local + @"\Yarn\Cache\y", 1 * MB), [l.Local + @"\Yarn\Cache"]);
            Want["dev-caches-gradle"] = (Put(l.Profile + @"\.gradle\caches\g", 6 * MB), [l.Profile + @"\.gradle\caches"]);
            Want["dev-caches-cargo"] = (Put(l.Profile + @"\.cargo\registry\cache\c", 7 * MB), [l.Profile + @"\.cargo\registry\cache"]);
            Want["dev-caches-go"] = (Put(l.Local + @"\go-build\gb", 9 * MB), [l.Local + @"\go-build"]);
            Want["dev-caches-uv"] = (Put(l.Local + @"\uv\cache\u", 11 * MB), [l.Local + @"\uv\cache"]);
            Put(l.Profile + @"\.cargo\registry\src\s", 99 * MB); Put(l.Profile + @"\.gradle\wrapper\dists\d", 80 * MB);
            Want["pnpm-store"] = (Put(l.Local + @"\pnpm\store\v3\files\p", 13 * MB), [l.Local + @"\pnpm\store"]);

            // 构建产物：三种标记，各带一份没有标记的干扰项
            string desk = l.Profile + @"\Desktop";
            Put(desk + @"\rust1\Cargo.toml", KB); long rust = Put(desk + @"\rust1\target\debug\x", 20 * MB);
            Put(desk + @"\dotnet1\app.csproj", KB); long obj = Put(desk + @"\dotnet1\obj\o", 4 * MB);
            Put(desk + @"\web1\package.json", KB); long next = Put(desk + @"\web1\.next\n", 6 * MB), turbo = Put(desk + @"\web1\.turbo\t", 1 * MB);
            Put(desk + @"\web1\dist\d", 30 * MB);
            Put(desk + @"\plain\target\x", 50 * MB); Put(desk + @"\plain\obj\y", 50 * MB); Put(desk + @"\plain\.next\z", 50 * MB);
            Want["build-outputs"] = (rust + obj + next + turbo, [desk + @"\rust1\target", desk + @"\web1\.next", desk + @"\dotnet1\obj", desk + @"\web1\.turbo"]); // 按大小降序

            // node_modules：三个项目，按「项目最近修改时间」应该是 old → mid → new，和大小顺序无关
            W.Dir(desk + @"\proj-old", mtime: DaysAgo(400));
            W.Dir(desk + @"\proj-old\node_modules", mtime: DaysAgo(0.1)); // npm install 刚跑过，不能算项目活跃
            W.Dir(desk + @"\proj-old\.git", mtime: DaysAgo(390));
            Put(desk + @"\proj-old\package.json", KB, DaysAgo(399));
            long mOld = Put(desk + @"\proj-old\node_modules\a\x", 10 * MB);
            W.Dir(desk + @"\proj-mid", mtime: DaysAgo(210));
            Put(desk + @"\proj-mid\package.json", KB, DaysAgo(200));
            long mMid = Put(desk + @"\proj-mid\node_modules\a\x", 45 * MB) + Put(desk + @"\proj-mid\node_modules\dep\node_modules\inner\x", 5 * MB);
            W.Dir(desk + @"\proj-new", mtime: DaysAgo(100));
            Put(desk + @"\proj-new\package.json", KB, DaysAgo(100));
            W.Dir(desk + @"\proj-new\src", mtime: DaysAgo(2));
            long mNew = Put(desk + @"\proj-new\node_modules\a\x", 30 * MB);
            Want["node-modules"] = (mOld + mMid + mNew, [desk + @"\proj-old\node_modules", desk + @"\proj-mid\node_modules", desk + @"\proj-new\node_modules"]);

            // AI 模型：环境变量没设时认默认位置；ComfyUI 的 models 按名字认，别家的 models 不认
            long ai = Put(l.Profile + @"\.ollama\models\blobs\b", 40 * MB) + Put(l.Profile + @"\.cache\huggingface\hub\m", 30 * MB)
                + Put(l.Profile + @"\.lmstudio\models\q", 20 * MB) + Put(l.R + @"\ai\ComfyUI\models\checkpoints\x.safetensors", 25 * MB);
            Put(l.R + @"\ai\other\models\z", 99 * MB); Put(l.R + @"\ai\ComfyUI\custom_nodes\n\models\q", 5 * MB);
            Want["ai-models"] = (ai, [l.Profile + @"\.ollama\models", l.Profile + @"\.cache\huggingface", l.Profile + @"\.lmstudio\models", l.R + @"\ai\ComfyUI\models"]);

            long android = Put(l.Profile + @"\.android\avd\Pixel.avd\userdata.img", 50 * MB) + Put(l.Local + @"\Android\Sdk\system-images\android-34\x", 70 * MB);
            Put(l.Profile + @"\.android\cache\c", 9 * MB); Put(l.Local + @"\Android\Sdk\platforms\p", 90 * MB);
            Want["android-sdk"] = (android, [l.Profile + @"\.android\avd", l.Local + @"\Android\Sdk\system-images"]);

            // 下载：91 天前算旧，89 天、时间未知（0）、时间在将来都不算
            string dl = l.Downloads;
            long oldDl = Put(dl + @"\old1.iso", 40 * MB, DaysAgo(100)) + Put(dl + @"\edge91.bin", 7 * MB, DaysAgo(91));
            W.Dir(dl + @"\olddir", mtime: DaysAgo(200)); oldDl += Put(dl + @"\olddir\f", 5 * MB);
            Put(dl + @"\new.zip", 90 * MB, DaysAgo(10)); Put(dl + @"\edge89.bin", 8 * MB, DaysAgo(89)); Put(dl + @"\unknown.bin", 20 * MB, 0);
            Put(dl + @"\future.bin", 15 * MB, DaysAgo(-365));
            Want["old-downloads"] = (oldDl, [dl + @"\old1.iso", dl + @"\edge91.bin", dl + @"\olddir"]);

            // 大文件：≥ 1 GB 的普通文件。系统文件、pagefile 这几个、被保护目录里的都排除
            long big = Put(l.Profile + @"\Videos\big.mkv", 2 * GB) + Put(l.R + @"\VMs\disk.vhdx", 3 * GB);
            Put(l.Profile + @"\notquite.bin", GB - 1); Put(l.Profile + @"\sys.dat", 2 * GB, 0, NodeFlags.System);
            Want["large-files"] = (big, [l.Profile + @"\Videos\big.mkv", l.R + @"\VMs\disk.vhdx"]);

            long wo = Put(l.R + @"\Windows.old\Windows\x.dll", 5 * MB) + Put(l.R + @"\Windows.old\big.wim", 8 * GB) + Put(l.R + @"\$Windows.~BT\s", 1 * MB);
            Want["windows-old"] = (wo, [l.R + @"\Windows.old", l.R + @"\$Windows.~BT"]);

            Want["hiberfil"] = (Put(l.R + @"\hiberfil.sys", 6 * GB), [l.R + @"\hiberfil.sys"]);
            Want["pagefile"] = (Put(l.R + @"\pagefile.sys", 4 * GB) + Put(l.R + @"\swapfile.sys", 256 * MB), [l.R + @"\pagefile.sys", l.R + @"\swapfile.sys"]);
            Want["winsxs"] = (Put(l.Win + @"\WinSxS\x\y.dll", 900 * MB), [l.Win + @"\WinSxS"]);
            Want["installer"] = (Put(l.Win + @"\Installer\i.msi", 100 * MB), [l.Win + @"\Installer"]);
            Want["restore-points"] = (Put(l.R + @"\System Volume Information\v.dat", 5 * GB), [l.R + @"\System Volume Information"]);
        }
    }

    // §6 表格：id、组、动作、需要管理员、默认永久、可勾选、命令、设置入口
    static readonly (string Id, string Group, string Action, bool Admin, bool Permanent, bool Selectable, string? Command, string? Target)[] Table =
    [
        ("user-temp", "safe", "deleteContents", false, true, false, null, null),
        ("win-temp", "safe", "deleteContents", true, true, false, null, null),
        ("recycle-bin", "safe", "emptyRecycleBin", false, true, false, null, null),
        ("wu-download", "safe", "deleteContents", true, true, false, null, null),
        ("delivery-opt", "safe", "command", true, false, false, "Delete-DeliveryOptimizationCache -Force", null),
        ("thumb-cache", "safe", "deleteNodes", false, true, false, null, null),
        ("browser-cache", "safe", "deleteContents", false, true, false, null, null),
        ("crash-dumps", "safe", "deleteNodes", false, true, false, null, null),
        ("dev-caches-npm", "safe", "command", false, false, false, "npm cache clean --force", null),
        ("dev-caches-pip", "safe", "command", false, false, false, "pip cache purge", null),
        ("dev-caches-nuget", "safe", "command", false, false, false, "dotnet nuget locals http-cache --clear", null),
        ("dev-caches-yarn", "safe", "command", false, false, false, "yarn cache clean", null),
        ("dev-caches-gradle", "safe", "deleteContents", false, true, false, null, null),
        ("dev-caches-cargo", "safe", "deleteContents", false, true, false, null, null),
        ("dev-caches-go", "safe", "command", false, false, false, "go clean -cache", null),
        ("dev-caches-uv", "safe", "command", false, false, false, "uv cache clean", null),
        ("pnpm-store", "safe", "command", false, false, false, "pnpm store prune", null),
        ("build-outputs", "safe", "deleteNodes", false, true, true, null, null),
        ("node-modules", "caution", "deleteNodes", false, false, true, null, null),
        ("ai-models", "caution", "deleteNodes", false, false, true, null, null),
        ("android-sdk", "caution", "deleteNodes", false, false, true, null, null),
        ("old-downloads", "caution", "deleteNodes", false, false, true, null, null),
        ("large-files", "caution", "deleteNodes", false, false, true, null, null),
        ("windows-old", "caution", "openSettings", false, false, false, null, "ms-settings:storagesense"),
        ("hiberfil", "protected", "command", true, false, false, "powercfg /h off", null),
        ("pagefile", "protected", "openSettings", false, false, false, null, "SystemPropertiesPerformance.exe"),
        ("winsxs", "protected", "command", true, false, false, "Dism.exe /Online /Cleanup-Image /StartComponentCleanup", null),
        ("installer", "protected", "info", false, false, false, null, null),
        ("restore-points", "protected", "openSettings", false, false, false, null, "SystemPropertiesProtection.exe"),
    ];

    static void RuleTable(CheckContext ctx, Full f)
    {
        ctx.Equal(Table.Length, f.Hits.Count, "假 C 盘上每条规则恰好各出一条建议");
        foreach (var (id, group, action, admin, permanent, selectable, command, target) in Table)
        {
            if (Get(ctx, f.Hits, id, "假 C 盘") is not { } h) continue;
            var s = h.S;
            ctx.Equal(group, s.Group, id + " 的组");
            ctx.Equal(action, s.Action, id + " 的动作");
            ctx.Equal(admin, s.RequiresAdmin, id + " 需要管理员");
            ctx.Equal(permanent, s.DefaultPermanent, id + " 默认永久删除");
            ctx.Equal(selectable, s.Selectable, id + " 可勾选");
            ctx.Equal(command, s.Command, id + " 的命令");
            ctx.Equal(target, s.Target, id + " 的设置入口");
            ctx.True(s.Title.Length > 0 && s.Description.Length > 0, id + " 有标题和说明");
            if (f.Want.TryGetValue(id, out var want)) Expect(ctx, f.Hits, id, want.Bytes, want.Nodes, "假 C 盘");
            else ctx.Fail($"测试自己漏写了 {id} 的期望值");
        }
        var node = Get(ctx, f.Hits, "node-modules", "标题");
        ctx.Equal("闲置项目的 node_modules", node?.S.Title, "node-modules 的标题");
        // node_modules 按项目最近修改时间从旧到新排；老项目的 node_modules 目录自己刚被 npm install 碰过，不能算活跃
        ctx.Equal(string.Join(" | ", f.Want["node-modules"].Nodes), string.Join(" | ", node?.Paths ?? []), "node_modules 的顺序（按项目修改时间旧→新）");
        var build = Get(ctx, f.Hits, "build-outputs", "构建产物顺序");
        ctx.Equal(string.Join(" | ", f.Want["build-outputs"].Nodes), string.Join(" | ", build?.Paths ?? []), "构建产物按大小降序");
        foreach (var h in f.Hits)
        {
            ctx.True(h.S.Bytes > 0, h.S.Id + " 大小为 0 的建议不该输出");
            ctx.True(h.S.NodeIds.Length > 0 && h.S.NodeIds.Distinct().Count() == h.S.NodeIds.Length, h.S.Id + " 的节点非空且不重复");
            ctx.True(!string.IsNullOrEmpty(h.S.DisplayPath), h.S.Id + " 每条建议都要有 DisplayPath");
        }
    }

    static void Ordering(CheckContext ctx, Full f)
    {
        var hits = f.Hits.Select(h => h.S).ToList();
        int Rank(string g) => g switch { "safe" => 0, "caution" => 1, _ => 2 };
        for (int i = 1; i < hits.Count; i++)
        {
            int a = Rank(hits[i - 1].Group), b = Rank(hits[i].Group);
            ctx.True(a <= b, $"组顺序 safe→caution→protected：{hits[i - 1].Id} 排在 {hits[i].Id} 前面");
            if (a == b) ctx.True(hits[i - 1].Bytes >= hits[i].Bytes, $"组内按 Bytes 降序：{hits[i - 1].Id} ≥ {hits[i].Id}");
        }
        string Ids(string group) => string.Join(",", hits.Where(h => h.Group == group).Select(h => h.Id));
        ctx.Equal("windows-old,large-files,android-sdk,ai-models,node-modules,old-downloads", Ids("caution"), "caution 组的完整顺序");
        ctx.Equal("hiberfil,restore-points,pagefile,winsxs,installer", Ids("protected"), "protected 组的完整顺序");
        ctx.Equal("build-outputs", hits[0].Id, "safe 组最大的在最前面");
    }

    static void DisplayPaths(CheckContext ctx, Full f)
    {
        var l = f.L;
        string Dp(string id) => f.Hits.First(h => h.S.Id == id).S.DisplayPath ?? "";
        ctx.Equal(@"~\AppData\Local\Temp", Dp("user-temp"), "用户目录前缀写成 ~");
        ctx.Equal(@"Q:\Windows\Temp", Dp("win-temp"), "不在用户目录下的写完整路径");
        ctx.Equal(@"~\AppData\Local\npm-cache", Dp("dev-caches-npm"), "npm 缓存的 DisplayPath");
        ctx.Equal(@"~\AppData\Local\Microsoft\Windows\Explorer", Dp("thumb-cache"), "缩略图缓存写所在目录，不写一长串文件");
        ctx.Equal(@"Q:\pagefile.sys, Q:\swapfile.sys", Dp("pagefile"), "两处用「, 」连接");
        ctx.Equal(@"Q:\Windows.old, Q:\$Windows.~BT", Dp("windows-old"), "两处不写「等」");
        // 太多：前两处 + 「等 N 处」；崩溃转储写容器目录，6 处
        ctx.Equal(@"~\AppData\Local\CrashDumps, Q:\Windows\Minidump 等 6 处", Dp("crash-dumps"), "六处写前两处加「等 6 处」");
        var browser = f.Hits.First(h => h.S.Id == "browser-cache");
        ctx.Equal($"{l.Disp(browser.Paths[0])}, {l.Disp(browser.Paths[1])} 等 {browser.Paths.Length} 处", browser.S.DisplayPath, "浏览器缓存的 DisplayPath");
        var modules = f.Hits.First(h => h.S.Id == "node-modules");
        ctx.Equal($"{l.Disp(modules.Paths[0])}, {l.Disp(modules.Paths[1])} 等 3 处", modules.S.DisplayPath, "node_modules 的 DisplayPath 跟着排好的顺序");
        ctx.True(f.Hits.All(h => !(h.S.DisplayPath ?? "").Contains(l.Profile, Ic)), "DisplayPath 里不该残留用户目录完整路径");
    }

    static void ItemsCoverage(CheckContext ctx, Full f)
    {
        foreach (var h in f.Hits)
        {
            var s = h.S;
            if (s.Selectable)
            {
                ctx.Equal(string.Join(",", s.NodeIds), string.Join(",", s.Items.Select(i => i.Id)), s.Id + "：可勾选建议的 Items 要覆盖全部 NodeIds，顺序一致");
                ctx.True(s.NodeIds.Length <= 100, s.Id + " 的 NodeIds 不超过 100");
            }
            else
            {
                ctx.Equal(Math.Min(20, s.NodeIds.Length), s.Items.Length, s.Id + "：不可勾选的取前 20");
                ctx.True(s.Items.All(i => s.NodeIds.Contains(i.Id)), s.Id + " 的 Items 都来自 NodeIds");
                ctx.True(s.Items.Zip(s.Items.Skip(1)).All(p => p.First.Alloc >= p.Second.Alloc), s.Id + " 的预览按大小降序");
            }
            ctx.True(s.Items.All(i => i.Path == h.Sim.PathOf(i.Id)), s.Id + " 的 Items 带真实路径");
        }
    }
}
