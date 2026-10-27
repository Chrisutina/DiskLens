using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DiskLens.Core;

namespace DiskLens.Check;

// 规则本身的自检：真实环境、环境变量优先、近似但不该命中的干扰项、各条规则的细节。
static partial class CleanupChecks
{
    static string? EnvVar(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// 只认写得像样的绝对路径：和被测代码对「写歪的环境变量」的态度一致，相对路径当没设。
    static string? Abs(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = Environment.ExpandEnvironmentVariables(v.Trim().Trim('"'));
        return Path.IsPathFullyQualified(v) ? Path.GetFullPath(v).TrimEnd('\\') : null;
    }

    /// 独立于被测代码的 8.3 展开：逐段枚举父目录，让文件系统自己说长名叫什么。
    static string LongForm(string path)
    {
        path = path.TrimEnd('\\');
        if (path.Length <= Path.GetPathRoot(path)!.TrimEnd('\\').Length) return path;
        string parent = LongForm(Path.GetDirectoryName(path)!), segment = Path.GetFileName(path);
        if (segment.Contains('~') && Directory.Exists(parent))
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent, segment)) return Path.Combine(parent, Path.GetFileName(entry));
        return Path.Combine(parent, segment);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);

    /// 几个盘上的树合起来看：同一个 id 可能因为搬到 D 盘而出现在两棵树里。
    static void ExpectAll(CheckContext ctx, List<Hit> hits, string id, long bytes, string[] nodes, string why)
    {
        var m = hits.Where(h => h.S.Id == id).ToList();
        ctx.Equal(Set(nodes), Set(m.SelectMany(h => h.Paths)), $"{why}：{id} 的节点");
        ctx.Equal(bytes, m.Sum(h => h.S.Bytes), $"{why}：{id} 的大小");
    }

    // ---------------- 本机真实环境 ----------------

    /// 用这台机器真实的环境变量、.npmrc、pnpm 配置、注册表拼出路径，再让 Analyze 用真实环境去找。
    /// 本机的缓存都搬去了 D 盘：老位置放一份更大的残留，命令类建议必须只认生效的那一处。
    static void RealEnvironment(CheckContext ctx)
    {
        var real = PurposeEnv.Real;
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows), local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            sys = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        var w = new World();
        var want = new Dictionary<string, (long Bytes, string[] Nodes)>();
        long Put(string path, long size, long mtime = 0) { w.File(path, size, 0, mtime); return size; }

        string temp = LongForm(Abs(EnvVar("TEMP")) ?? Abs(EnvVar("TMP")) ?? Path.GetTempPath());
        want["user-temp"] = (Put(temp + @"\a.tmp", 3 * MB), [temp]);
        if (!temp.Equals(local + @"\Temp", Ic)) Put(local + @"\Temp\decoy.tmp", 99 * MB); // 老位置的残留不算
        want["win-temp"] = (Put(win + @"\Temp\a.tmp", 2 * MB), [win + @"\Temp"]);
        want["recycle-bin"] = (Put(sys + @"\$Recycle.Bin\S-1\$R1", 4 * MB), [sys + @"\$Recycle.Bin"]);
        want["wu-download"] = (Put(win + @"\SoftwareDistribution\Download\p", 5 * MB), [win + @"\SoftwareDistribution\Download"]);
        string explorer = local + @"\Microsoft\Windows\Explorer", chrome = local + @"\Google\Chrome\User Data\Default";
        want["thumb-cache"] = (Put(explorer + @"\thumbcache_256.db", MB), [explorer + @"\thumbcache_256.db"]);
        want["browser-cache"] = (Put(chrome + @"\Cache\f", MB), [chrome + @"\Cache"]);
        want["crash-dumps"] = (Put(local + @"\CrashDumps\a.dmp", MB) + Put(programData + @"\Microsoft\Windows\WER\ReportQueue\q\r", MB),
            [local + @"\CrashDumps\a.dmp", programData + @"\Microsoft\Windows\WER\ReportQueue\q"]);

        // 命令类：生效位置 = 环境变量 / 配置文件 / 默认位置，依次取第一个；老位置的残留不能算进来
        void Tool(string id, string? effective, string fallback)
        {
            string at = effective ?? fallback;
            want[id] = (Put(at + @"\x.bin", 12 * MB), [at]);
            if (!at.Equals(fallback, Ic)) Put(fallback + @"\decoy.bin", 99 * MB);
        }
        Tool("dev-caches-npm", Abs(EnvVar("npm_config_cache")) ?? Abs(real.NpmRc("cache")), local + @"\npm-cache");
        Tool("dev-caches-pip", Abs(EnvVar("PIP_CACHE_DIR")), local + @"\pip\Cache");
        Tool("dev-caches-nuget", Abs(EnvVar("NUGET_HTTP_CACHE_PATH")), local + @"\NuGet\v3-cache");
        Tool("dev-caches-yarn", Abs(EnvVar("YARN_CACHE_FOLDER")), local + @"\Yarn\Cache");
        Tool("dev-caches-go", Abs(EnvVar("GOCACHE")), local + @"\go-build");
        Tool("dev-caches-uv", Abs(EnvVar("UV_CACHE_DIR")), local + @"\uv\cache");
        string? pnpmHome = Abs(EnvVar("PNPM_HOME"));
        Tool("pnpm-store", Abs(EnvVar("npm_config_store_dir")) ?? Abs(real.NpmRc("store-dir")) ?? Abs(real.PnpmConfig("storeDir")) ?? (pnpmHome is null ? null : pnpmHome + @"\store"),
            local + @"\pnpm\store");

        // 删目录内容类：生效位置 + 老位置都算；这里只放生效的那一处
        string gradle = (Abs(EnvVar("GRADLE_USER_HOME")) ?? profile + @"\.gradle") + @"\caches";
        want["dev-caches-gradle"] = (Put(gradle + @"\g.bin", 6 * MB), [gradle]);
        string cargo = (Abs(EnvVar("CARGO_HOME")) ?? profile + @"\.cargo") + @"\registry\cache";
        want["dev-caches-cargo"] = (Put(cargo + @"\c.bin", 7 * MB), [cargo]);

        string ollama = Abs(EnvVar("OLLAMA_MODELS")) ?? profile + @"\.ollama\models", hf = Abs(EnvVar("HF_HOME")) ?? profile + @"\.cache\huggingface";
        want["ai-models"] = (Put(ollama + @"\blob", 30 * MB) + Put(hf + @"\hub\m", 20 * MB), [ollama, hf]);
        string avd = Abs(EnvVar("ANDROID_AVD_HOME")) ?? (Abs(EnvVar("ANDROID_USER_HOME")) ?? profile + @"\.android") + @"\avd";
        string images = (Abs(EnvVar("ANDROID_HOME")) ?? Abs(EnvVar("ANDROID_SDK_ROOT")) ?? local + @"\Android\Sdk") + @"\system-images";
        want["android-sdk"] = (Put(avd + @"\Pixel.avd\u", 8 * MB) + Put(images + @"\android-34\i", 9 * MB), [avd, images]);

        // 注册表里搬走的 Downloads 优先；91 天前的文件算旧
        string downloads = Abs(real.Downloads()) ?? profile + @"\Downloads";
        want["old-downloads"] = (Put(downloads + @"\old.iso", 12 * MB, DaysAgo(91)), [downloads + @"\old.iso"]);
        Put(downloads + @"\new.zip", 50 * MB, DaysAgo(1));

        var hits = w.Analyze();
        ctx.Equal(Set(want.Keys), Set(hits.Select(h => h.S.Id).Distinct()), "真实环境：命中的建议集合");
        foreach (var (id, (bytes, nodes)) in want) ExpectAll(ctx, hits, id, bytes, nodes, "真实环境");
        ctx.Info($"真实环境：{w.Sims.Count()} 棵树、{hits.Count} 条建议；TEMP={Environment.GetEnvironmentVariable("TEMP")} → {temp}");
    }

    // ---------------- 环境变量优先（用假环境覆盖本机没有的情况） ----------------

    static void EnvironmentPriority(CheckContext ctx)
    {
        var l = new Layout(@"Q:\");
        string moved = l.R + @"\Moved";
        var vars = new Dictionary<string, string>
        {
            ["PIP_CACHE_DIR"] = moved + @"\pip", ["NUGET_HTTP_CACHE_PATH"] = moved + @"\nuget-http", ["YARN_CACHE_FOLDER"] = moved + @"\yarn",
            ["GOCACHE"] = moved + @"\gocache", ["UV_CACHE_DIR"] = moved + @"\uv", ["GRADLE_USER_HOME"] = moved + @"\gradle", ["CARGO_HOME"] = moved + @"\cargo",
            ["OLLAMA_MODELS"] = moved + @"\ollama", ["HF_HOME"] = moved + @"\hf", ["ANDROID_AVD_HOME"] = moved + @"\avd", ["ANDROID_HOME"] = moved + @"\asdk",
            ["TEMP"] = moved + @"\temp",
        };
        PurposeEnv Env(Dictionary<string, string>? extra, string? npmrc)
        {
            var merged = new Dictionary<string, string>(vars);
            if (extra != null) foreach (var (k, v) in extra) merged[k] = v;
            return FakeEnv(l, merged, npmrc, moved + @"\pnpm-store", moved + @"\Downloads");
        }

        World Build()
        {
            var w = new World(l.TreeRoot);
            // 搬家后的位置各 12 MB，老位置各留 99 MB 残留：谁被选中一目了然
            foreach (var at in new[] { @"\npm", @"\pip", @"\nuget-http", @"\yarn", @"\gocache", @"\uv", @"\pnpm-store", @"\gradle\caches", @"\cargo\registry\cache",
                @"\ollama", @"\hf", @"\avd", @"\asdk\system-images", @"\temp", @"\npmenv" }) w.File(moved + at + @"\x", 12 * MB);
            w.File(moved + @"\Downloads\old.bin", 12 * MB, 0, DaysAgo(100));
            foreach (var at in new[] { l.Local + @"\npm-cache", l.Local + @"\pip\Cache", l.Local + @"\NuGet\v3-cache", l.Local + @"\Yarn\Cache", l.Local + @"\go-build",
                l.Local + @"\uv\cache", l.Local + @"\pnpm\store", l.Profile + @"\.gradle\caches", l.Profile + @"\.cargo\registry\cache", l.Profile + @"\.ollama\models",
                l.Profile + @"\.cache\huggingface", l.Profile + @"\.android\avd", l.Local + @"\Android\Sdk\system-images", l.Temp }) w.File(at + @"\x", 99 * MB);
            w.File(l.Downloads + @"\old2.bin", 99 * MB, 0, DaysAgo(100));
            return w;
        }

        var hits = Build().Analyze(Env(null, moved + @"\npm"));
        // 命令类：只认生效的位置，命令清不到老位置，把残留算进来用户会觉得我们在吹牛
        foreach (var (id, at) in new[] { ("dev-caches-npm", @"\npm"), ("dev-caches-pip", @"\pip"), ("dev-caches-nuget", @"\nuget-http"), ("dev-caches-yarn", @"\yarn"),
            ("dev-caches-go", @"\gocache"), ("dev-caches-uv", @"\uv"), ("pnpm-store", @"\pnpm-store") })
            Expect(ctx, hits, id, 12 * MB, [moved + at], "环境变量/配置优先");
        Expect(ctx, hits, "user-temp", 12 * MB, [moved + @"\temp"], "TEMP 指到哪就清哪");
        // 删内容类：生效位置和老位置都收
        Expect(ctx, hits, "dev-caches-gradle", 111 * MB, [moved + @"\gradle\caches", l.Profile + @"\.gradle\caches"], "GRADLE_USER_HOME + 默认");
        Expect(ctx, hits, "dev-caches-cargo", 111 * MB, [moved + @"\cargo\registry\cache", l.Profile + @"\.cargo\registry\cache"], "CARGO_HOME + 默认");
        Expect(ctx, hits, "ai-models", 222 * MB, [moved + @"\ollama", l.Profile + @"\.ollama\models", moved + @"\hf", l.Profile + @"\.cache\huggingface"], "OLLAMA_MODELS / HF_HOME + 默认");
        Expect(ctx, hits, "android-sdk", 222 * MB, [moved + @"\avd", l.Profile + @"\.android\avd", moved + @"\asdk\system-images", l.Local + @"\Android\Sdk\system-images"], "ANDROID_* + 默认");
        Expect(ctx, hits, "old-downloads", 111 * MB, [moved + @"\Downloads\old.bin", l.Downloads + @"\old2.bin"], "注册表里的 Downloads + 默认");

        // npm_config_cache 比 .npmrc 优先
        hits = Build().Analyze(Env(new() { ["npm_config_cache"] = moved + @"\npmenv" }, moved + @"\npm"));
        Expect(ctx, hits, "dev-caches-npm", 12 * MB, [moved + @"\npmenv"], "npm_config_cache 比 .npmrc 优先");
        // 写歪的（相对路径、~ 开头）当没设，回落到默认位置
        foreach (var bad in new[] { @"..\x", "~/x", "cache" })
        {
            hits = Build().Analyze(Env(new() { ["UV_CACHE_DIR"] = bad }, bad));
            Expect(ctx, hits, "dev-caches-npm", 99 * MB, [l.Local + @"\npm-cache"], $"写歪的 .npmrc cache「{bad}」回落默认");
            Expect(ctx, hits, "dev-caches-uv", 99 * MB, [l.Local + @"\uv\cache"], $"写歪的 UV_CACHE_DIR「{bad}」回落默认");
        }

        // %TEMP% 是 8.3 短名时（用户名超过 8 个字符的机器上默认就是）要能认出树里的长名
        string longDir = Path.Combine(ctx.Sandbox(clean: false), "Short Name Probe Directory", "Temp");
        Directory.CreateDirectory(longDir);
        var sb = new StringBuilder(260);
        string shortDir = GetShortPathNameW(longDir, sb, (uint)sb.Capacity) > 0 ? sb.ToString() : longDir;
        if (!shortDir.Contains('~')) ctx.Info("沙箱所在卷没有 8.3 短名，跳过短名展开的单测（真实环境那一项在本机 TEMP 是短名时已经覆盖）");
        else
        {
            var w = new World(Path.GetPathRoot(longDir)!);
            w.File(longDir + @"\a.tmp", 3 * MB);
            var shortEnv = FakeEnv(new Layout(Path.GetPathRoot(longDir)!), new() { ["TEMP"] = shortDir });
            Expect(ctx, w.Analyze(shortEnv), "user-temp", 3 * MB, [longDir], "TEMP 是 8.3 短名也要命中长名的树");
        }
    }

    // ---------------- 近似但不该命中 ----------------

    static void NearMisses(CheckContext ctx)
    {
        var l = new Layout(@"Q:\");
        var w = new World(l.TreeRoot);
        string desk = l.Profile + @"\Desktop", explorer = l.Local + @"\Microsoft\Windows\Explorer";
        w.Dir(l.Temp); w.Dir(l.Win + @"\Temp"); w.Dir(l.R + @"\$Recycle.Bin"); w.Dir(l.R + @"\Windows.old"); // 空目录：大小为 0 的建议不输出
        w.File(l.Local + @"\Temp2\x", 5 * MB); w.File(l.R + @"\Temp\x", 5 * MB);                              // 名字相近、位置不对
        w.File(explorer + @"\other.db", 5 * MB); w.File(explorer + @"\thumbcache_256.txt", 5 * MB); w.File(explorer + @"\sub\iconcache_9.db", 5 * MB);
        w.File(l.Local + @"\Google\Chrome\User Data\Default\Login Data", 5 * MB);
        w.File(desk + @"\p1\target\x", 5 * MB);                                           // target 旁边没有 Cargo.toml
        w.File(desk + @"\p2\obj\x", 5 * MB); w.File(desk + @"\p2\app.cs", KB);            // obj 旁边只有 .cs
        w.File(desk + @"\p3\.next\x", 5 * MB); w.File(desk + @"\p4\.turbo\x", 5 * MB);   // 没有 package.json
        w.File(desk + @"\p5\target\x", 5 * MB); w.Dir(desk + @"\p5\Cargo.toml");         // Cargo.toml 是个目录
        w.File(desk + @"\p6\obj\x", 5 * MB); w.Dir(desk + @"\p6\app.csproj");            // .csproj 是个目录
        w.Dir(desk + @"\p7\node_modules");                                               // 空的 node_modules
        w.File(l.Downloads + @"\new.zip", 90 * MB, 0, DaysAgo(10)); w.File(l.Downloads + @"\unknown.bin", 20 * MB, 0, 0);
        w.File(l.R + @"\big\almost.bin", GB - 1); w.File(l.R + @"\big\sys.bin", 2 * GB, NodeFlags.System);
        w.File(l.R + @"\ai\other\models\z", 99 * MB);
        var hits = w.Analyze(FakeEnv(l));
        ctx.Equal("", string.Join(",", hits.Select(h => h.S.Id)), "只有干扰项时一条建议都不该出");
        ctx.Equal(0, new World(l.TreeRoot).Analyze(FakeEnv(l)).Count, "没有任何节点的世界也不该出建议");
    }

    static void NodeModules(CheckContext ctx)
    {
        var l = new Layout(@"Q:\");
        var w = new World(l.TreeRoot);
        string d = l.Profile + @"\Desktop";
        // 52 个项目：越大的项目越老，所以「按大小取前 50」和「按修改时间排」的结果是反着的
        for (int i = 1; i <= 52; i++)
        {
            string proj = d + $@"\p{i:00}";
            w.Dir(proj, mtime: DaysAgo(i));
            w.File(proj + @"\package.json", KB, 0, DaysAgo(i));
            w.File(proj + @"\node_modules\a\x", i * MB);
        }
        w.File(d + @"\p10\node_modules\a\node_modules\b\x", 3 * MB); // 嵌套的不另算
        w.File(d + @"\p10\packages\sub\node_modules\y", 2 * MB);     // 项目里另一处 node_modules 是独立的一项，但排不进前 50
        // 这些都比 52 个项目大，没排除掉的话会挤进前 50（真机上就把 VS Code 扩展的依赖当成了闲置项目）
        w.File(l.Profile + @"\.vscode\extensions\ext.a\package.json", KB);
        w.File(l.Profile + @"\.vscode\extensions\ext.a\node_modules\z", 500 * MB);
        w.File(l.Profile + @"\AppData\Local\Programs\app\resources\app\package.json", KB);
        w.File(l.Profile + @"\AppData\Local\Programs\app\resources\app\node_modules\z", 400 * MB);
        w.File(d + @"\loose\node_modules\z", 300 * MB); // 旁边没有 package.json，不认是项目
        w.File(l.Profile + @"\.dsh\profiles\web\package.json", KB); // 工具的点目录（真机上 .dsh 的 web 配置就这样被当成过闲置项目）
        w.File(l.Profile + @"\.dsh\profiles\web\node_modules\z", 350 * MB);
        var hits = w.Analyze(FakeEnv(l));
        if (Get(ctx, hits, "node-modules", "52 个项目") is not { } h) return;
        ctx.True(h.Paths.All(p => !p.Contains(@"\.vscode\") && !p.Contains(@"\AppData\") && !p.Contains(@"\loose\")),
            "编辑器扩展、应用自带的、旁边没有 package.json 的 node_modules 都不算闲置项目");
        ctx.Equal(50, h.S.NodeIds.Length, "node_modules 按大小取前 50");
        var expected = Enumerable.Range(3, 50).Reverse().Select(i => d + $@"\p{i:00}\node_modules");
        // p10 因为多了嵌套和 packages 里那处，p10 自己的 node_modules 是 10+3 MB，不影响前 50 的名单
        ctx.Equal(string.Join(" | ", expected), string.Join(" | ", h.Paths), "前 50 名，按项目修改时间从旧到新");
        ctx.True(h.Paths.All(p => !p.Contains(@"\node_modules\a\node_modules")), "嵌套的 node_modules 不单独出现");
        ctx.Equal(Enumerable.Range(3, 50).Sum(i => (long)i) * MB + 3 * MB, h.S.Bytes, "嵌套的那 3 MB 只算在外层里，没有重复计入");
    }

    static void BuildOutputs(CheckContext ctx)
    {
        var l = new Layout(@"Q:\");
        var w = new World(l.TreeRoot);
        string d = l.Profile + @"\Desktop";
        w.File(d + @"\r\cargo.toml", KB);                                                            // 标记文件名不分大小写
        w.File(d + @"\r\TARGET\x", 8 * MB);
        w.File(d + @"\r\TARGET\sub\Cargo.toml", KB); w.File(d + @"\r\TARGET\sub\target\y", 3 * MB);  // 嵌套的 target 不重复计入
        w.File(d + @"\c\App.CSPROJ", KB); w.File(d + @"\c\Obj\z", 2 * MB);
        var hits = w.Analyze(FakeEnv(l));
        Expect(ctx, hits, "build-outputs", 13 * MB + KB, [d + @"\r\TARGET", d + @"\c\Obj"], "标记不分大小写、嵌套只取外层"); // 多出的 1 KB 是嵌套的那份 Cargo.toml
    }

    static void OldDownloads(CheckContext ctx)
    {
        // 假 C 盘里已经覆盖了 91 天算旧、89 天 / 时间未知 / 时间在将来不算；这里补「下载目录不存在」和「条目是目录」
        var l = new Layout(@"Q:\");
        var w = new World(l.TreeRoot);
        w.File(l.Profile + @"\Desktop\old.bin", 5 * MB, 0, DaysAgo(500)); // 不在下载目录里的旧文件不算
        ctx.True(w.Analyze(FakeEnv(l)).All(h => h.S.Id != "old-downloads"), "没有下载目录就没有 old-downloads");
        w = new World(l.TreeRoot);
        w.Dir(l.Downloads + @"\folder", mtime: DaysAgo(120)); w.File(l.Downloads + @"\folder\a", 6 * MB, 0, DaysAgo(1)); // 目录自己的修改时间说了算
        w.File(l.Downloads + @"\sub\deep.bin", 9 * MB, 0, DaysAgo(300));                                                 // 只看下载目录的直接子项
        Expect(ctx, w.Analyze(FakeEnv(l)), "old-downloads", 6 * MB, [l.Downloads + @"\folder"], "只看直接子项，目录按自己的修改时间");
        w = new World(l.TreeRoot);
        w.File(l.Downloads + @"\desktop.ini", 288, NodeFlags.Hidden | NodeFlags.System, DaysAgo(400)); // 资源管理器的配置，不是下载
        w.File(l.Downloads + @"\tiny.txt", 300 * KB, 0, DaysAgo(400));
        Absent(ctx, w.Analyze(FakeEnv(l)), "old-downloads", "隐藏/系统文件不算，旧下载合计不到 1 MB 就不提");
    }

    /// 审查抓到的：同一批字节被两条建议各算一次，「已选」「清理后可用」全部虚高。
    static void NoDoubleCounting(CheckContext ctx)
    {
        var l = new Layout(@"Q:\");
        var w = new World(l.TreeRoot);
        string hf = l.Profile + @"\.cache\huggingface";
        w.File(hf + @"\hub\model.safetensors", 2 * GB);
        w.File(l.Downloads + @"\ubuntu.iso", 3 * GB, 0, DaysAgo(200)); // 既是 90 天前的下载，又是 1 GB 以上的大文件
        var hits = w.Analyze(FakeEnv(l));
        ctx.True(hits.Any(h => h.S.Id == "ai-models"), "HF 缓存算 AI 模型");
        ctx.True(hits.Any(h => h.S.Id == "old-downloads"), "旧 ISO 算旧下载");
        ctx.True(hits.Where(h => h.S.Id == "large-files").All(h => h.Paths.All(p =>
            !p.StartsWith(hf, StringComparison.OrdinalIgnoreCase) && !p.EndsWith("ubuntu.iso", StringComparison.OrdinalIgnoreCase))),
            "已经被前面建议认领的文件不再出现在大文件里");
        long sum = hits.Where(h => h.S.Group != "protected").Sum(h => h.S.Bytes);
        ctx.True(sum <= 5 * GB + 16 * MB, "各条建议合计不超过实际字节（不重复计入）");
    }

    /// 审查抓到的：目录自己不受保护、里面藏着 .ssh，删除计划会拦，清理建议和脚本却照样摆出来。
    static void SubtreeGuard(CheckContext ctx)
    {
        var l = new Layout(@"Q:\");
        var w = new World(l.TreeRoot);
        string d = l.Downloads + @"\old-laptop-backup";
        w.Dir(d, mtime: DaysAgo(400));
        w.File(d + @"\.ssh\id_ed25519", KB, 0, DaysAgo(400));
        w.File(d + @"\photos.zip", 6 * MB, 0, DaysAgo(400));
        Absent(ctx, w.Analyze(FakeEnv(l)), "old-downloads", "目录里藏着 .ssh 的旧下载不摆出来");
    }

    static void LargeFiles(CheckContext ctx)
    {
        var l = new Layout(@"Q:\");
        World Build(bool withOk)
        {
            var w = new World(l.TreeRoot);
            w.File(l.R + @"\Windows.old\big.wim", 8 * GB); w.File(l.R + @"\System Volume Information\v.dat", 5 * GB); w.File(l.R + @"\$Recycle.Bin\S-1\$Rbig.iso", 2 * GB);
            w.File(l.R + @"\hiberfil.sys", 6 * GB); w.File(l.R + @"\pagefile.sys", 4 * GB); w.File(l.R + @"\swapfile.sys", 3 * GB); w.File(l.R + @"\DumpStack.log.tmp", 2 * GB);
            w.File(l.R + @"\x\sys.bin", 2 * GB, NodeFlags.System); w.File(l.R + @"\x\meta.bin", 2 * GB, NodeFlags.Metafile);
            w.File(l.R + @"\x\virtual.bin", 2 * GB, NodeFlags.Virtual); w.File(l.R + @"\x\reparse.bin", 2 * GB, NodeFlags.Reparse);
            w.File(l.R + @"\x\dup.bin", 2 * GB, NodeFlags.HardLinkDup); w.File(l.R + @"\x\almost.bin", GB - 1);
            if (withOk) w.File(l.R + @"\x\ok.bin", GB); // 恰好 1 GB 算（≥）
            return w;
        }
        Absent(ctx, Build(false).Analyze(FakeEnv(l)), "large-files", "系统文件、pagefile、受保护目录、虚拟/元文件/链接/硬链接副本、不足 1 GB 都不是大文件");
        Expect(ctx, Build(true).Analyze(FakeEnv(l)), "large-files", GB, [l.R + @"\x\ok.bin"], "恰好 1 GB 的普通文件");
    }

    /// 可勾选的建议最多 100 条（node_modules、大文件固定 50 条），Items 要全覆盖；不可勾选的预览只取 20。
    static void Caps(CheckContext ctx)
    {
        var l = new Layout(@"Q:\");
        var w = new World(l.TreeRoot);
        string d = l.Profile + @"\Desktop", chrome = l.Local + @"\Google\Chrome\User Data";
        for (int i = 1; i <= 130; i++) { w.File(d + $@"\r{i}\Cargo.toml", KB); w.File(d + $@"\r{i}\target\x", i * MB); }
        for (int i = 1; i <= 60; i++) w.File(l.R + $@"\big\f{i}.bin", GB + i * MB);
        for (int i = 1; i <= 120; i++) w.File(l.Downloads + $@"\o{i}.bin", i * MB, 0, DaysAgo(100));
        for (int i = 1; i <= 25; i++) w.File(chrome + $@"\Profile {i}\Cache\x", i * MB);
        var hits = w.Analyze(FakeEnv(l));

        if (Get(ctx, hits, "build-outputs", "130 个构建产物") is { } b)
        {
            ctx.Equal(Set(Enumerable.Range(31, 100).Select(i => d + $@"\r{i}\target")), Set(b.Paths), "构建产物取最大的 100 个");
            ctx.Equal(100, b.S.Items.Length, "构建产物的 Items 覆盖全部 100 个 NodeIds");
            ctx.Equal(Enumerable.Range(31, 100).Sum(i => (long)i) * MB, b.S.Bytes, "大小只算这 100 个");
        }
        if (Get(ctx, hits, "large-files", "60 个大文件") is { } f)
        {
            ctx.Equal(Set(Enumerable.Range(11, 50).Select(i => l.R + $@"\big\f{i}.bin")), Set(f.Paths), "大文件取前 50");
            ctx.Equal(50, f.S.Items.Length, "大文件的 Items 覆盖全部 50 个");
        }
        if (Get(ctx, hits, "old-downloads", "120 个旧下载") is { } o)
        {
            ctx.Equal(Set(Enumerable.Range(21, 100).Select(i => l.Downloads + $@"\o{i}.bin")), Set(o.Paths), "旧下载取最大的 100 个");
            ctx.Equal(100, o.S.Items.Length, "旧下载的 Items 覆盖全部 100 个");
        }
        if (Get(ctx, hits, "browser-cache", "25 个缓存目录") is { } c)
        {
            ctx.Equal(25, c.S.NodeIds.Length, "不可勾选的建议 NodeIds 不截断");
            ctx.Equal(20, c.S.Items.Length, "不可勾选的预览取前 20");
            ctx.Equal(Set(Enumerable.Range(6, 20).Select(i => chrome + $@"\Profile {i}\Cache")), Set(c.S.Items.Select(i => i.Path)), "预览是最大的 20 个");
        }
    }

    /// 200 万节点的树：Analyze 是持着树锁跑的，慢了会卡住界面。
    static void Scale(CheckContext ctx)
    {
        var l = new Layout(@"Q:\");
        var tree = new FileTree(l.TreeRoot, 2_100_000);
        int Add(int parent, string name, NodeFlags flags = 0, long size = 0) => tree.AddNode(parent, name, flags, size, size, 0);
        int desk = Add(Add(Add(0, "Users", NodeFlags.Directory), "u", NodeFlags.Directory), "Desktop", NodeFlags.Directory);
        for (int i = 0; i < 2000; i++)
        {
            int proj = Add(desk, "proj" + i, NodeFlags.Directory);
            Add(proj, "package.json", 0, 1024);
            int modules = Add(proj, "node_modules", NodeFlags.Directory);
            for (int j = 0; j < 300; j++) Add(modules, "m" + j + ".js", 0, 4096);
            int src = Add(proj, "src", NodeFlags.Directory);
            for (int j = 0; j < 697; j++) Add(src, "s" + j + ".ts", 0, 2048);
        }
        tree.Seal();
        var clock = Stopwatch.StartNew();
        var list = CleanupAdvisor.Analyze(tree, l.Ctx(), CancellationToken.None, FakeEnv(l));
        ctx.Info($"{tree.Count:N0} 个节点 Analyze：{clock.ElapsedMilliseconds} ms");
        ctx.True(clock.ElapsedMilliseconds < 3000, $"200 万节点的 Analyze 应该在 3 秒内，实际 {clock.ElapsedMilliseconds} ms");
        ctx.Equal(50, list.FirstOrDefault(s => s.Id == "node-modules")?.NodeIds.Length ?? 0, "200 万节点里的 node_modules 取前 50");
    }

    static void CommandWhitelist(CheckContext ctx, Full f)
    {
        var commands = f.Hits.Where(h => h.S.Action == "command").Select(h => h.S.Command!).ToList();
        ctx.Equal(Set(ShellOps.AllowedCommands.Keys), Set(commands), "规则表里的命令和 ShellOps 白名单一一对应");
        foreach (var c in commands) ctx.Equal(c.StartsWith("Delete-DeliveryOptimizationCache"), ShellOps.AllowedCommands[c], $"「{c}」的 PowerShell 标记");
        string[] settings = ["ms-settings:storagesense", "SystemPropertiesPerformance.exe", "SystemPropertiesProtection.exe"];
        var targets = f.Hits.Where(h => h.S.Action == "openSettings").Select(h => h.S.Target!).ToList();
        ctx.Equal(Set(settings), Set(targets), "openSettings 的入口只有这三个");
        ctx.Equal(Set(settings), Set(ShellOps.AllowedSettings), "ShellOps 的设置入口白名单（脚本生成器共用）和规则表一致");
        ctx.True(f.Hits.Where(h => h.S.Action != "command").All(h => h.S.Command == null), "只有 command 带命令");
        ctx.True(f.Hits.Where(h => h.S.Action != "openSettings").All(h => h.S.Target == null), "只有 openSettings 带入口");
    }
}
