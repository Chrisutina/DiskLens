namespace DiskLens.Core;

/// 调用方不持树锁；建议只来自扫描树和固定规则。
public static partial class CleanupAdvisor
{
    const string Safe = "safe", Caution = "caution", Protected = "protected";
    const string DeleteContents = "deleteContents", DeleteNodes = "deleteNodes", EmptyRecycleBin = "emptyRecycleBin",
        Command = "command", OpenSettings = "openSettings", Info = "info";

    /// 前端要靠 Items 算「只勾了一部分」的大小，所以可勾选的建议最多 100 条且 Items 全覆盖；不可勾选的只给前 20 条做预览。
    const int MaxSelectable = 100, MaxPreview = 20;

    public static List<SuggestionDto> Analyze(FileTree tree, CleanupContext ctx, CancellationToken ct) =>
        Analyze(tree, ctx, ct, PurposeEnv.Real);

    /// env 可替换，测试才不用赌这台机器刚好装了什么。
    internal static List<SuggestionDto> Analyze(FileTree tree, CleanupContext ctx, CancellationToken ct, PurposeEnv env)
    {
        var paths = new CleanupPaths(env, ctx.VolumeRoot); // 读文件和注册表的活儿留在锁外
        lock (tree.Sync) return new Analysis(tree, paths, ct).Run();
    }

    public static OpResultDto Run(FileTree tree, SuggestionDto suggestion, CleanupRunRequest req, CleanupContext ctx,
        CancellationToken ct, DeletionSnapshot? confirmed = null, Action<DeleteProgressDto>? progress = null) =>
        Run(tree, suggestion, req, ctx, ct, PurposeEnv.Real, confirmed, progress);

    /// 重新按规则表生成这条建议并核对所选节点；确认框（cleanup.check）和执行（cleanup.run）共用同一套校验。
    public static (SuggestionDto Current, int[] Ids) Prepare(FileTree tree, SuggestionDto suggestion, CleanupRunRequest req,
        CleanupContext ctx, CancellationToken ct) => Prepare(tree, suggestion, req, ctx, ct, PurposeEnv.Real);

    internal static (SuggestionDto Current, int[] Ids) Prepare(FileTree tree, SuggestionDto suggestion, CleanupRunRequest req,
        CleanupContext ctx, CancellationToken ct, PurposeEnv env)
    {
        if (req.SuggestionId != suggestion.Id) throw new ArgumentException(L.T("清理建议不匹配", "The cleanup suggestion doesn't match", "クリーンアップの提案が一致しません"));
        // 重新按规则表生成：传进来的 DTO 只用来认 ID，命令、目标、节点都不能成为执行依据。
        var current = Analyze(tree, ctx, ct, env).FirstOrDefault(s => s.Id == req.SuggestionId)
            ?? throw new ArgumentException(L.T("清理建议已过期，请重新扫描", "This cleanup suggestion is out of date; rescan", "このクリーンアップの提案は古くなっています。再スキャンしてください"));
        if (suggestion.Action != current.Action || suggestion.Command != current.Command || suggestion.Target != current.Target)
            throw new ArgumentException(L.T("清理建议与当前规则不一致，请刷新清理列表", "This cleanup suggestion doesn't match the current rules; refresh the cleanup list", "クリーンアップの提案が現在の規則と一致しません。クリーンアップの一覧を更新してください"));
        // 空数组当成「整条」太危险：前端哪天手滑传个 []，就变成全删了。
        if (req.NodeIds is { Length: 0 }) throw new ArgumentException(L.T("没有选中任何条目", "No items selected", "項目が選択されていません"));
        int[] ids = req.NodeIds?.Distinct().ToArray() ?? current.NodeIds;
        var allowed = current.NodeIds.ToHashSet();
        if (ids.Any(n => !allowed.Contains(n))) throw new ArgumentException(L.T("请求包含清理建议之外的条目", "The request includes items outside the cleanup suggestion", "要求にクリーンアップの提案に含まれない項目があります"));
        if (!current.Selectable && !allowed.SetEquals(ids)) throw new ArgumentException(L.T("这条建议只能整条清理，不能只选一部分", "This suggestion can only be cleaned up as a whole, not in part", "この提案はまとめてクリーンアップする必要があり、一部だけは選べません"));
        return (current, ids);
    }

    /// <param name="confirmed">确认框拍下的删除快照：给了就只删确认过的那一份，并在动手前逐个核对身份</param>
    internal static OpResultDto Run(FileTree tree, SuggestionDto suggestion, CleanupRunRequest req, CleanupContext ctx,
        CancellationToken ct, PurposeEnv env, DeletionSnapshot? confirmed = null, Action<DeleteProgressDto>? progress = null)
    {
        var (current, ids) = Prepare(tree, suggestion, req, ctx, ct, env);
        switch (current.Action)
        {
            case DeleteNodes:
                return confirmed?.Delete(req.Permanent ?? current.DefaultPermanent, ct, progress)
                    ?? DeleteService.Delete(tree, ids, req.Permanent ?? current.DefaultPermanent, ct);
            case DeleteContents when confirmed != null:
                return confirmed.Delete(true, ct, progress);
            case DeleteContents:
                var results = new List<OpResultDto>();
                foreach (int n in ids)
                { if (ct.IsCancellationRequested) break; results.Add(DeleteService.DeleteContents(tree, n, ct)); }
                lock (tree.Sync) return new(!ct.IsCancellationRequested && results.All(r => r.Ok), results.Sum(r => r.FreedBytes),
                    results.Sum(r => r.Deleted), results.Sum(r => r.Failed), results.SelectMany(r => r.Errors).Take(20).ToArray(),
                    ct.IsCancellationRequested ? L.T("操作已取消；已经完成的条目已更新", "Operation cancelled; finished items were updated", "操作をキャンセルしました。完了した項目は更新済みです") : L.T("已清理目录内容；保留了目录和被占用的文件", "Cleaned up the folder contents; kept the folders and files in use", "フォルダーの中身をクリーンアップしました。フォルダーと使用中のファイルは残しています"), tree.Version,
                    results.Sum(r => r.AlreadyGone));
            case EmptyRecycleBin:
                progress?.Invoke(new(ctx.VolumeRoot + "$Recycle.Bin", 1, 0, 0, 0, 0));
                var emptied = DeleteService.EmptyRecycleBin(tree, ids.Single(), ctx.VolumeRoot, ct);
                progress?.Invoke(new(null, 1, emptied.Ok ? 1 : 0, emptied.Ok ? 0 : 1, 0, emptied.Deleted, emptied.FreedBytes));
                return emptied;
            case Command:
                ct.ThrowIfCancellationRequested();
                if (current.Command == null || !ShellOps.AllowedCommands.TryGetValue(current.Command, out bool powershell)) throw new InvalidOperationException(L.T("清理命令未获白名单授权", "The cleanup command isn't on the allowlist", "クリーンアップのコマンドが許可リストにありません"));
                ShellOps.RunInConsole(current.Command, current.RequiresAdmin && !ctx.IsAdmin, powershell);
                return Done(L.T("已在新终端打开清理命令；完成后可按 F5 重新扫描", "Opened the cleanup command in a new terminal; press F5 to rescan when it finishes", "新しいターミナルでクリーンアップのコマンドを開きました。終わったら F5 で再スキャンできます"));
            case OpenSettings:
                ct.ThrowIfCancellationRequested();
                ShellOps.OpenSettings(current.Target ?? throw new InvalidOperationException(L.T("缺少设置入口", "Missing settings page", "設定ページがありません")));
                return Done(L.T("已打开系统设置；完成后可按 F5 重新扫描", "Opened Windows Settings; press F5 to rescan when you're done", "Windows の設定を開きました。終わったら F5 で再スキャンできます"));
            default: return Done(current.Description);
        }

        OpResultDto Done(string message) { lock (tree.Sync) return new(true, 0, 0, 0, [], message, tree.Version); }
    }

    /// 规则表。调用方持有 tree.Sync，所以全程只读树、不碰磁盘。
    sealed class Analysis(FileTree tree, CleanupPaths p, CancellationToken ct)
    {
        const StringComparison C = StringComparison.OrdinalIgnoreCase;
        const long LargeFile = 1L << 30;
        const long MinOldDownloads = 1L << 20;
        readonly List<SuggestionDto> _out = [];
        // 应用、编辑器扩展、系统目录的节点：里面的 node_modules 是人家程序的一部分，不是闲置项目
        readonly HashSet<int> toolZones = [];
        // 已经被前面的建议认领的节点和它们的祖先：同一批字节只能算进一条建议，否则「已选」「清理后可用」全部虚高
        readonly HashSet<int> claimed = [], claimedAncestors = [];

        sealed record Rule(string Id, string Group, string Title, string Description, string Action, string? Command = null,
            string? Target = null, bool Admin = false, bool Permanent = false, bool Selectable = false);

        public List<SuggestionDto> Run()
        {
            string? J(string? root, params string[] parts) => CleanupPaths.Join(root, parts);
            string? win = p.Windows, local = p.Local, user = p.User, vol = p.Volume;

            // ---- safe ----
            Emit(new("user-temp", Safe, L.T("临时文件", "Temporary files", "一時ファイル"), L.T("程序运行时留下的临时文件；正在使用的会被跳过。", "Temporary files left by programs; files in use are skipped.", "プログラムが残した一時ファイルです。使用中のものはスキップします。"), DeleteContents), Existing(p.Temp));
            Emit(new("win-temp", Safe, L.T("系统临时文件", "System temporary files", "システムの一時ファイル"), L.T("Windows 自己的临时文件；需要管理员权限，正在使用的会被跳过。", "Windows' own temporary files; needs administrator rights, and files in use are skipped.", "Windows 自身の一時ファイルです。管理者権限が必要で、使用中のものはスキップします。"), DeleteContents, Admin: true), Existing(J(win, "Temp")));
            Emit(new("recycle-bin", Safe, L.T("回收站", "Recycle Bin", "ごみ箱"), L.T("清空当前卷的回收站；清空后无法恢复。", "Empties the Recycle Bin on this volume; this can't be undone.", "このボリュームのごみ箱を空にします。空にすると元に戻せません。"), EmptyRecycleBin), Existing(J(vol, "$Recycle.Bin")));
            Emit(new("wu-download", Safe, L.T("Windows 更新缓存", "Windows Update cache", "Windows Update のキャッシュ"), L.T("已安装更新的下载缓存，需要时 Windows 会重新下载；需要管理员权限。", "Download cache of installed updates; Windows downloads them again if needed. Needs administrator rights.", "インストール済みの更新プログラムのダウンロード キャッシュです。必要なら Windows が再ダウンロードします。管理者権限が必要です。"), DeleteContents, Admin: true),
                Existing(J(win, "SoftwareDistribution", "Download")));
            Emit(new("delivery-opt", Safe, L.T("传递优化缓存", "Delivery Optimization cache", "配信の最適化のキャッシュ"), L.T("用于共享更新的缓存，用 Windows 自带的命令清理；需要管理员权限。", "Cache for sharing updates, cleaned up with a built-in Windows command; needs administrator rights.", "更新プログラムを共有するためのキャッシュです。Windows 標準のコマンドでクリーンアップします。管理者権限が必要です。"), Command,
                    Command: "Delete-DeliveryOptimizationCache -Force", Admin: true),
                Existing(J(win, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache")));
            ThumbCache();
            BrowserCache();
            CrashDumps();
            DevCaches();
            Emit(new("pnpm-store", Safe, L.T("pnpm 内容存储", "pnpm content store", "pnpm のコンテンツ ストア"), L.T("用 pnpm 自带的 prune 命令清理没有被引用的包；不要直接删除 store。", "Cleans up unreferenced packages with pnpm's own prune command; don't delete the store directly.", "pnpm 標準の prune コマンドで参照されていないパッケージをクリーンアップします。store を直接削除しないでください。"), Command, Command: "pnpm store prune"),
                Existing(First(p.Var("npm_config_store_dir"), p.NpmStore, p.PnpmStore, J(p.Var("PNPM_HOME"), "store"), J(local, "pnpm", "store"))));

            // ---- 全树扫一遍：node_modules、构建产物、AI 模型目录、大文件 ----
            foreach (int z in Existing(J(user, "AppData"), J(user, ".vscode"), J(user, ".vscode-insiders"), J(user, ".cursor"),
                J(user, ".windsurf"), J(user, ".trae"), J(user, ".kiro"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)))
                toolZones.Add(z);
            // 家目录下的点目录（.vscode、.codex、.dsh……）基本都是工具自己的数据，真项目很少放这儿
            foreach (int home in Existing(user))
                foreach (int c in tree.LiveChildren(home))
                    if (tree.IsDir(c) && tree.NameSpan(c).StartsWith('.')) toolZones.Add(c);
            Scan(out var modules, out var builds, out var models, out var large);
            Emit(new("build-outputs", Safe, L.T("构建产物", "Build output", "ビルド成果物"), L.T("Cargo 的 target、.NET 的 obj、Next.js 的 .next 和 Turbo 的 .turbo；下次构建会重新生成。", "Cargo's target, .NET's obj, Next.js's .next and Turbo's .turbo; they're regenerated on the next build.", "Cargo の target、.NET の obj、Next.js の .next、Turbo の .turbo です。次のビルドで再生成されます。"), DeleteNodes, Permanent: true, Selectable: true),
                Pick(builds, MaxSelectable));

            // ---- caution ----
            Emit(new("node-modules", Caution, L.T("闲置项目的 node_modules", "node_modules of idle projects", "使っていないプロジェクトの node_modules"), L.T("项目依赖，删除后需要重新安装；最久没动过的项目排在前面。", "Project dependencies; reinstall them after deleting. Projects untouched the longest come first.", "プロジェクトの依存関係です。削除後は再インストールが必要です。長く触っていないプロジェクトほど上に並びます。"), DeleteNodes, Selectable: true),
                Pick(modules, 50).OrderBy(ProjectMTime).ThenByDescending(tree.Alloc).ToArray());
            Emit(new("ai-models", Caution, L.T("AI 模型", "AI models", "AI モデル"), L.T("本地模型文件，删除后要重新下载，体积很大。", "Local model files; they are large and must be downloaded again after deleting.", "ローカルのモデル ファイルです。サイズが大きく、削除後は再ダウンロードが必要です。"), DeleteNodes, Selectable: true),
                Pick(Existing(p.Var("OLLAMA_MODELS"), J(user, ".ollama", "models"), p.Var("HF_HOME"), J(user, ".cache", "huggingface"),
                    J(user, ".lmstudio", "models")).Concat(models), MaxSelectable));
            Emit(new("android-sdk", Caution, L.T("Android 模拟器与系统镜像", "Android emulators & system images", "Android エミュレーターとシステム イメージ"), L.T("不用的模拟器和系统镜像可以删，也可以在 Android Studio 的 SDK Manager 里管理。", "Unused emulators and system images can be deleted, or managed in Android Studio's SDK Manager.", "使わないエミュレーターやシステム イメージは削除できます。Android Studio の SDK Manager でも管理できます。"), DeleteNodes, Selectable: true),
                Pick(Existing(p.Var("ANDROID_AVD_HOME"), J(p.Var("ANDROID_USER_HOME"), "avd"), J(user, ".android", "avd"),
                    J(p.Var("ANDROID_HOME"), "system-images"), J(p.Var("ANDROID_SDK_ROOT"), "system-images"), J(local, "Android", "Sdk", "system-images")), MaxSelectable));
            OldDownloads();
            Emit(new("large-files", Caution, L.T("大文件", "Large files", "大きなファイル"), L.T("1 GB 以上的文件，视频、镜像、虚拟机磁盘之类；请确认用途后再删。", "Files over 1 GB, such as videos, disk images and virtual machine disks; check what they're for before deleting.", "動画、ディスク イメージ、仮想マシンのディスクなど 1 GB 以上のファイルです。用途を確認してから削除してください。"), DeleteNodes, Selectable: true),
                Pick(large, 50));
            Emit(new("windows-old", Caution, L.T("旧版 Windows", "Previous Windows installation", "以前の Windows"), L.T("旧系统和升级残留，10 天内可以回退；请用系统的存储设置清理。", "The old system and upgrade leftovers; you can roll back within 10 days. Clean them up in Windows storage settings.", "以前のシステムとアップグレードの残りです。10 日以内なら元に戻せます。Windows のストレージ設定でクリーンアップしてください。"), OpenSettings, Target: "ms-settings:storagesense"),
                Existing(new[] { "Windows.old", "$Windows.~BT", "$Windows.~WS", "$WINDOWS.~Q", "$GetCurrent", "ESD" }.Select(s => J(vol, s)).ToArray()));

            // ---- protected：别删，只给正确的处理方法 ----
            Emit(new("hiberfil", Protected, L.T("休眠文件", "Hibernation file", "休止状態ファイル"), L.T("运行命令会同时关闭休眠和快速启动；不要直接删文件。", "The command turns off both hibernation and Fast Startup; don't delete the file directly.", "コマンドを実行すると休止状態と高速スタートアップの両方がオフになります。ファイルを直接削除しないでください。"), Command, Command: "powercfg /h off", Admin: true),
                Existing(J(vol, "hiberfil.sys")));
            Emit(new("pagefile", Protected, L.T("虚拟内存", "Virtual memory", "仮想メモリ"), L.T("请在系统设置里调整虚拟内存，不要直接删文件。", "Adjust virtual memory in system settings; don't delete the file directly.", "システムの設定で仮想メモリを調整してください。ファイルを直接削除しないでください。"), OpenSettings, Target: "SystemPropertiesPerformance.exe"),
                Existing(J(vol, "pagefile.sys"), J(vol, "swapfile.sys")));
            Emit(new("winsxs", Protected, L.T("Windows 组件存储", "Windows component store", "Windows コンポーネント ストア"), L.T("手动删除会损坏系统，请用 DISM 清理旧组件；大小已扣除硬链接。", "Deleting by hand breaks Windows; use DISM to clean up old components. The size already excludes hard links.", "手動で削除するとシステムが壊れます。DISM で古いコンポーネントをクリーンアップしてください。サイズはハードリンクを差し引き済みです。"), Command,
                Command: "Dism.exe /Online /Cleanup-Image /StartComponentCleanup", Admin: true), Existing(J(win, "WinSxS")));
            Emit(new("installer", Protected, L.T("Windows 安装缓存", "Windows Installer cache", "Windows インストーラーのキャッシュ"), L.T("MSI 安装缓存，删除后程序可能无法卸载或修复，请保留。", "The MSI installer cache; without it, programs may not uninstall or repair. Keep it.", "MSI のインストール キャッシュです。削除するとプログラムをアンインストールや修復できなくなる場合があるため、残してください。"), Info), Existing(J(win, "Installer")));
            Emit(new("restore-points", Protected, L.T("还原点与卷影副本", "Restore points & shadow copies", "復元ポイントとボリューム シャドウ コピー"), L.T("请在系统保护里调整占用上限，不要直接删目录。", "Adjust the space limit in System Protection; don't delete the folder directly.", "システムの保護で使用量の上限を調整してください。フォルダーを直接削除しないでください。"), OpenSettings, Target: "SystemPropertiesProtection.exe"),
                Existing(J(vol, "System Volume Information")));

            return _out.OrderBy(s => s.Group switch { Safe => 0, Caution => 1, _ => 2 }).ThenByDescending(s => s.Bytes)
                .ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        }

        void ThumbCache()
        {
            var explorer = Existing(CleanupPaths.Join(p.Local, "Microsoft", "Windows", "Explorer"));
            var files = new List<int>();
            foreach (int dir in explorer)
                foreach (int n in tree.LiveChildren(dir))
                {
                    var name = tree.NameSpan(n);
                    if (!tree.IsDir(n) && name.EndsWith(".db", C) && (name.StartsWith("thumbcache_", C) || name.StartsWith("iconcache_", C))) files.Add(n);
                }
            Emit(new("thumb-cache", Safe, L.T("缩略图缓存", "Thumbnail cache", "縮小表示のキャッシュ"), L.T("缩略图和图标缓存会自动重建；资源管理器占用的文件会被跳过。", "Thumbnail and icon caches rebuild themselves; files File Explorer is using are skipped.", "縮小表示とアイコンのキャッシュは自動で再作成されます。エクスプローラーが使用中のファイルはスキップします。"), DeleteNodes, Permanent: true), files, explorer);
        }

        void BrowserCache()
        {
            var dirs = new List<int>();
            foreach (int data in Existing(CleanupPaths.Join(p.Local, "Google", "Chrome", "User Data"), CleanupPaths.Join(p.Local, "Microsoft", "Edge", "User Data"),
                CleanupPaths.Join(p.Local, "BraveSoftware", "Brave-Browser", "User Data")))
                foreach (int profile in tree.LiveChildren(data))
                    if (tree.IsDir(profile))
                        foreach (var name in new[] { "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache" }) AddDir(dirs, profile, name);
            foreach (int profiles in Existing(CleanupPaths.Join(p.Local, "Mozilla", "Firefox", "Profiles")))
                foreach (int profile in tree.LiveChildren(profiles)) AddDir(dirs, profile, "cache2");
            Emit(new("browser-cache", Safe, L.T("浏览器缓存", "Browser cache", "ブラウザーのキャッシュ"), L.T("先关闭浏览器；只清缓存，不会删除登录状态、书签和历史。", "Close the browser first; only the cache is cleared, not sign-ins, bookmarks or history.", "先にブラウザーを閉じてください。消すのはキャッシュだけで、サインイン状態・ブックマーク・履歴は削除しません。"), DeleteContents), dirs);

            void AddDir(List<int> list, int parent, string name)
            {
                int n = tree.FindChild(parent, name);
                if (n >= 0 && tree.IsDir(n)) list.Add(n);
            }
        }

        void CrashDumps()
        {
            // 目录取内容、MEMORY.DMP 是单个文件，一条建议只能有一种动作，所以统一落成 deleteNodes；
            // 展示用的位置写容器目录，不写一长串转储文件。
            string? J(string? root, params string[] parts) => CleanupPaths.Join(root, parts);
            var where = Existing(J(p.Local, "CrashDumps"), J(p.Windows, "Minidump"), J(p.Windows, "MEMORY.DMP"),
                J(p.ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"), J(p.ProgramData, "Microsoft", "Windows", "WER", "ReportQueue"),
                J(p.Local, "Microsoft", "Windows", "WER"));
            var nodes = new List<int>();
            foreach (int n in where)
                if (tree.IsDir(n)) nodes.AddRange(tree.LiveChildren(n).ToArray()); else nodes.Add(n);
            Emit(new("crash-dumps", Safe, L.T("崩溃转储和错误报告", "Crash dumps & error reports", "クラッシュダンプとエラー報告"), L.T("程序崩溃时留下的转储和错误报告；删除后无法再用它们排查过去的崩溃。", "Dumps and error reports left by program crashes; once deleted, past crashes can't be investigated with them.", "プログラムのクラッシュ時に残ったダンプとエラー報告です。削除すると過去のクラッシュの調査には使えなくなります。"), DeleteNodes, Permanent: true), nodes, where);
        }

        void DevCaches()
        {
            string? J(string? root, params string[] parts) => CleanupPaths.Join(root, parts);
            string local = p.Local, user = p.User;
            // 候选位置：环境变量/配置在前，默认位置在后。有官方命令的只认生效的第一个——命令清不到老位置，
            // 把老位置的大小算进来，用户跑完命令发现没腾出那么多空间就要骂人了；删目录内容的则两处都收。
            var tools = new (string Id, string Title, string?[] Where, string? Command)[]
            {
                ("npm", L.T("npm 缓存", "npm cache", "npm のキャッシュ"), [p.Var("npm_config_cache"), p.NpmCache, J(local, "npm-cache")], "npm cache clean --force"),
                ("pip", L.T("pip 缓存", "pip cache", "pip のキャッシュ"), [p.Var("PIP_CACHE_DIR"), J(local, "pip", "Cache")], "pip cache purge"),
                ("nuget", L.T("NuGet HTTP 缓存", "NuGet HTTP cache", "NuGet の HTTP キャッシュ"), [p.Var("NUGET_HTTP_CACHE_PATH"), J(local, "NuGet", "v3-cache")], "dotnet nuget locals http-cache --clear"),
                ("yarn", L.T("Yarn 缓存", "Yarn cache", "Yarn のキャッシュ"), [p.Var("YARN_CACHE_FOLDER"), J(local, "Yarn", "Cache")], "yarn cache clean"),
                ("gradle", L.T("Gradle 缓存", "Gradle cache", "Gradle のキャッシュ"), [J(p.Var("GRADLE_USER_HOME"), "caches"), J(user, ".gradle", "caches")], null),
                ("cargo", L.T("Cargo 注册表缓存", "Cargo registry cache", "Cargo のレジストリ キャッシュ"), [J(p.Var("CARGO_HOME"), "registry", "cache"), J(user, ".cargo", "registry", "cache")], null),
                ("go", L.T("Go 构建缓存", "Go build cache", "Go のビルド キャッシュ"), [p.Var("GOCACHE"), J(local, "go-build")], "go clean -cache"),
                ("uv", L.T("uv 缓存", "uv cache", "uv のキャッシュ"), [p.Var("UV_CACHE_DIR"), J(local, "uv", "cache")], "uv cache clean"),
            };
            foreach (var (id, title, where, command) in tools)
            {
                string description = command is null ? L.T("删除后下次构建会重新下载；请先关闭相关开发工具。", "Downloaded again on the next build after deleting; close related dev tools first.", "削除すると次のビルドで再ダウンロードされます。先に関連する開発ツールを閉じてください。") : L.T("用官方命令清理；删除后下次安装会重新下载。", "Cleaned up with the official command; downloaded again on the next install.", "公式のコマンドでクリーンアップします。削除すると次のインストール時に再ダウンロードされます。");
                Emit(new("dev-caches-" + id, Safe, title, description, command is null ? DeleteContents : Command, Command: command),
                    command is null ? Existing(where) : Existing(First(where)));
            }
        }

        void OldDownloads()
        {
            long cutoff = DateTime.UtcNow.AddDays(-90).ToFileTimeUtc(); // 树里的修改时间是 FILETIME
            var old = new List<int>();
            foreach (int dir in Existing(p.DownloadsDir, CleanupPaths.Join(p.User, "Downloads")))
                foreach (int n in tree.LiveChildren(dir))
                    // desktop.ini 这类隐藏/系统文件是资源管理器的配置，不是「下载」，摆出来只是噪音
                    if (tree.MTime(n) > 0 && tree.MTime(n) < cutoff && !tree.Has(n, NodeFlags.Hidden | NodeFlags.System)) old.Add(n);
            if (old.Sum(tree.Alloc) < MinOldDownloads) return; // 几百字节也值得占一行建议？不值得
            Emit(new("old-downloads", Caution, L.T("90 天前的下载", "Downloads older than 90 days", "90 日以上前のダウンロード"), L.T("下载文件夹里 90 天没改动过的条目，按大小排序，请自己挑。", "Items in Downloads unchanged for 90 days, sorted by size; pick them yourself.", "ダウンロード フォルダー内で 90 日間変更されていない項目をサイズ順に表示します。自分で選んでください。"), DeleteNodes, Selectable: true), Pick(old, MaxSelectable));
        }

        /// 一趟扫完整棵树：节点下标本身就是父子有序的，不用递归。
        void Scan(out List<int> modules, out List<int> builds, out List<int> models, out List<int> large)
        {
            modules = []; builds = []; models = []; large = [];
            for (int n = 1; n < tree.Count; n++)
            {
                if ((n & 4095) == 0) ct.ThrowIfCancellationRequested();
                if (!tree.IsValid(n) || tree.Has(n, NodeFlags.Virtual | NodeFlags.Metafile | NodeFlags.Reparse)) continue;
                if (!tree.IsDir(n))
                {
                    // 硬链接副本删了也腾不出空间；pagefile 这几个在 Protection 里拦得住，但名字先筛掉更稳。
                    if (tree.Alloc(n) >= LargeFile && !tree.Has(n, NodeFlags.System | NodeFlags.HardLinkDup)
                        && !FileCategories.IsSystemFileName(tree.NameSpan(n)) && !Protection.Evaluate(tree, n).Blocked) large.Add(n);
                    continue;
                }
                var name = tree.NameSpan(n);
                int parent = tree.Parent(n);
                // 只认「项目里的」node_modules：旁边得有 package.json，而且不在应用和编辑器扩展的地盘里——
                // VS Code 扩展、Electron 应用、npm 全局包也都带 node_modules，删了就是把人家的程序拆了
                if (name.Equals("node_modules", C)) { if (!InsideNodeModules(n) && HasFile(parent, "package.json") && !InToolZone(n)) modules.Add(n); }
                else if (name.Equals("target", C)) { if (HasFile(parent, "Cargo.toml")) builds.Add(n); }
                else if (name.Equals("obj", C)) { if (HasCsproj(parent)) builds.Add(n); }
                else if (name.Equals(".next", C) || name.Equals(".turbo", C)) { if (HasFile(parent, "package.json")) builds.Add(n); }
                else if (name.Equals("models", C) && PurposeRules.DirName(name, tree.NameSpan(parent)).Rule == PurposeRules.RAi) models.Add(n);
            }
        }

        bool Covered(int n)
        {
            for (int a = n; a >= 0; a = tree.Parent(a))
                if (claimed.Contains(a)) return true;
            return false;
        }

        bool InToolZone(int n)
        {
            for (int a = tree.Parent(n); a >= 0; a = tree.Parent(a))
                if (toolZones.Contains(a)) return true;
            return false;
        }

        bool InsideNodeModules(int n)
        {
            for (int a = tree.Parent(n); a >= 0; a = tree.Parent(a))
                if (tree.NameSpan(a).Equals("node_modules", C)) return true;
            return false;
        }

        bool HasFile(int dir, string name)
        {
            int n = tree.FindChild(dir, name);
            return n >= 0 && !tree.IsDir(n);
        }

        bool HasCsproj(int dir)
        {
            foreach (int n in tree.LiveChildren(dir))
                if (!tree.IsDir(n) && tree.NameSpan(n).EndsWith(".csproj", C)) return true;
            return false;
        }

        /// 「项目最近修改时间」：项目目录和它直接子项（不含 node_modules 自己）里最新的那个。
        /// 目录的修改时间只在直接增删条目时才变，所以光看项目目录不够；.git 每次提交都会动，正好当活跃信号。
        /// 不递归是为了快，代价是只改了深层文件的项目会被当成更闲置——排序只是提示，勾不勾还是用户说了算。
        long ProjectMTime(int modules)
        {
            int project = tree.Parent(modules);
            long newest = tree.MTime(project);
            foreach (int n in tree.LiveChildren(project))
                if (n != modules) newest = Math.Max(newest, tree.MTime(n));
            return newest == 0 ? long.MaxValue : newest; // 时间未知的排最后，别冤枉人家「闲置」
        }

        static string? First(params string?[] candidates) => candidates.FirstOrDefault(c => c is not null);

        int Find(string? path) => string.IsNullOrEmpty(path) ? FileTree.NoNode : tree.FindByPath(path);

        int[] Existing(params string?[] paths) => paths.Select(Find).Where(n => n >= 0).Distinct().ToArray();

        /// 去掉已删除、虚拟、重解析点节点，以及被别的候选包含在内的嵌套节点（删了外层里层自然没了，大小也不能算两遍）。
        int[] Outermost(IEnumerable<int> nodes)
        {
            var list = nodes.Distinct().Where(n => tree.IsValid(n) && !tree.Has(n, NodeFlags.Virtual | NodeFlags.Reparse)).ToArray();
            var set = list.ToHashSet();
            return list.Where(n =>
            {
                for (int a = tree.Parent(n); a >= 0; a = tree.Parent(a)) if (set.Contains(a)) return false;
                return true;
            }).ToArray();
        }

        /// 去嵌套后按大小取前 N 个。
        int[] Pick(IEnumerable<int> nodes, int take) =>
            Outermost(nodes).OrderByDescending(tree.Alloc).ThenBy(n => n).Take(take).ToArray();

        /// shown：给人看的位置（DisplayPath 用），默认就是 NodeIds 自己。
        void Emit(Rule r, IEnumerable<int> found, IEnumerable<int>? shown = null)
        {
            ct.ThrowIfCancellationRequested();
            var nodes = Outermost(found);
            if (r.Action is DeleteNodes or DeleteContents)
            {
                // §7 的保护规则对清理一视同仁：会被拦的条目摆出来只会让用户点了才看到失败，也会虚报能腾出的空间。
                // 清空回收站和那几条只给命令/入口的 protected 建议本来就不走删除，不筛。
                // 删整个节点要看整棵子树（目录里藏着 .ssh 也得拦，和 DeleteService 的删除计划同一个判定）；
                // 清空目录的内容在执行和脚本里逐个子项再判，这里只看目录自己
                nodes = nodes.Where(n => !(n == FileTree.RootId ? Protection.EvaluatePath(tree.GetPath(n))
                    : r.Action == DeleteNodes ? Protection.EvaluateTree(tree, n) : Protection.Evaluate(tree, n)).Blocked).ToArray();
                // 扫描根本身删不掉（只有 deleteContents 允许把它当目录清空），别把一条点不动的建议摆给用户。
                if (r.Action == DeleteNodes) nodes = nodes.Where(n => n != FileTree.RootId).ToArray();
                // 「先确认」里的删除类建议不碰系统/程序数据和漫游配置：批量清理没有逐项的警告确认框
                if (r.Group == Caution && r.Action == DeleteNodes)
                    nodes = nodes.Where(n => Protection.EvaluatePath(tree.GetPath(n)).Warning == null).ToArray();
            }
            if (r.Group != Protected)
            {
                nodes = nodes.Where(n => !Covered(n) && !claimedAncestors.Contains(n)).ToArray();
                if (r.Selectable && nodes.Length > MaxSelectable) nodes = nodes[..MaxSelectable];
                foreach (int n in nodes)
                {
                    claimed.Add(n);
                    for (int a = tree.Parent(n); a >= 0 && claimedAncestors.Add(a); a = tree.Parent(a)) { }
                }
            }
            if (r.Selectable && nodes.Length > MaxSelectable) nodes = nodes[..MaxSelectable];
            long bytes = nodes.Sum(n => tree.CountedValue(n, Metric.Alloc));
            if (bytes <= 0) return;
            var preview = r.Selectable ? nodes : nodes.OrderByDescending(tree.Alloc).ThenBy(n => n).Take(MaxPreview).ToArray();
            var places = (shown ?? nodes).Distinct().Select(tree.GetPath).ToArray();
            _out.Add(new(r.Id, r.Group, r.Title, r.Description, bytes, nodes.Sum(n => tree.IsDir(n) ? tree.Files(n) : 1L), r.Action,
                r.Command, r.Target, r.Admin, r.Permanent || r.Action is DeleteContents or EmptyRecycleBin, r.Selectable,
                nodes, preview.Select(n => DtoFactory.FileItem(tree, n)).ToArray(), p.DisplayList(places)));
        }
    }
}
