namespace DiskLens.Core;

/// DESIGN §7 的保护规则。拦截 = 删了会弄坏系统或者不可挽回；警告 = 允许，但要用户多确认一次。
/// 原则：宁可多问一次，也不要把用户自己的文件当系统文件拦掉（拦错了用户只能去资源管理器里删，工具就白做了）。
public static class Protection
{
    internal static string BigDeletion => L.T("本次删除超过 10 GB 或 10 万个文件", "This deletion is over 10 GB or 100,000 files", "今回の削除は 10 GB または 10 万ファイルを超えています");

    // %WINDIR% 下面整棵都不许动的子目录
    static readonly string[] WindowsParts = ["System32", "SysWOW64", "WinSxS", "Installer", "servicing", "Boot", "SystemApps", "SystemResources", "Fonts", "assembly", "Microsoft.NET"];
    // 下面几组都只认「卷根下的第一级」：别处同名的是用户自己的东西（备份里的 pagefile.sys、叫 Boot 的项目目录……）
    static readonly HashSet<string> RootDirs = new(["System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "EFI", "Windows.old", "$Windows.~BT", "$Windows.~WS", "$WINDOWS.~Q", "$GetCurrent", "ESD"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> RootFiles = new(["bootmgr", "BOOTNXT", "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> RootSpecials = new(["Program Files", "Program Files (x86)", "ProgramData", "Users"], StringComparer.OrdinalIgnoreCase);
    // 私钥：丢了找不回来，且体积小到删它没有任何腾空间的意义，所以直接拦；名字出现在路径的任何一层都算
    static readonly HashSet<string> KeyFolders = new([".ssh", ".gnupg"], StringComparer.OrdinalIgnoreCase);
    // 凭据和访问配置：重新登录能补回来，只警告
    static readonly HashSet<string> CredentialFolders = new([".aws", ".azure", ".kube"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> KeyLookup = KeyFolders.GetAlternateLookup<ReadOnlySpan<char>>();

    /// 环境里的特殊目录，进程内不会变，查一次就够（Environment.GetFolderPath 每次都要问 Shell）。
    sealed class Known
    {
        public static readonly Known Current = new();

        public readonly string? Windows, Roaming, Own;
        public readonly string[] WindowsParts, Roots;

        Known()
        {
            Windows = Folder(Environment.SpecialFolder.Windows);
            Roaming = Folder(Environment.SpecialFolder.ApplicationData);
            WindowsParts = Windows == null ? [] : Protection.WindowsParts.Select(p => Path.Combine(Windows, p)).ToArray();
            // 搬家过的 ProgramData、用户目录不一定还在卷根下，按环境里的真实位置再认一遍
            Roots = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData }
                .Select(Folder).OfType<string>().ToArray();
            ProfileRoot = Folder(Environment.SpecialFolder.UserProfile);
            string? own = Try(AppContext.BaseDirectory);
            // 装在卷根上的话整个盘都成了「自己的目录」，那就谁也别拦
            Own = own != null && !Same(own, Path.GetPathRoot(own)!) ? own : null;
        }

        public readonly string? ProfileRoot;

        static string? Folder(Environment.SpecialFolder folder)
        {
            string path = Environment.GetFolderPath(folder);
            return path.Length == 0 ? null : Try(path);
        }

        static string? Try(string path)
        {
            try { return Normalize(path); }
            catch (ArgumentException) { return null; }
        }
    }

    /// 节点本身的判定，调用方持有 tree.Sync。
    public static ProtectionResult Evaluate(FileTree tree, int node)
    {
        TreeQueries.RequireNode(tree, node);
        if (node == FileTree.RootId) return Block(L.T("扫描根目录不能删除", "The scan root can't be deleted", "スキャンのルートは削除できません"));
        if (tree.Has(node, NodeFlags.Virtual | NodeFlags.Metafile)) return Block(L.T("虚拟节点和 NTFS 元数据不能删除", "Virtual items and NTFS metadata can't be deleted", "仮想の項目と NTFS のメタデータは削除できません"));
        for (int p = tree.Parent(node); p >= 0; p = tree.Parent(p))
            if (tree.Has(p, NodeFlags.Virtual | NodeFlags.Metafile)) return Block(L.T("该条目位于受保护的虚拟或元数据目录内", "This item is inside a protected virtual or metadata folder", "この項目は保護された仮想フォルダーまたはメタデータのフォルダー内にあります"));
        // 规整（GetFullPath）会吃掉名字末尾的点和空格、把「...」折叠成上一级：判定和删除就不是同一个对象了，
        // 实测能把扫描根整个送进回收站。路径上任何一段是这种名字都不碰
        for (int x = node; x > FileTree.RootId; x = tree.Parent(x))
            if (OddName(tree.NameSpan(x))) return Block(OddReason);
        string path, root;
        try { path = Normalize(tree.GetPath(node)); root = Normalize(tree.RootPath); }
        catch (ArgumentException) { return Block(L.T("路径无效，拒绝删除", "Invalid path; not deleted", "パスが無効なため削除しません")); }
        if (!Within(path, root)) return Block(L.T("路径超出扫描目录，拒绝删除", "The path is outside the scanned folder; not deleted", "パスがスキャン対象のフォルダーの外にあるため削除しません"));
        var result = EvaluatePath(path);
        if (result.Blocked) return result;

        var warnings = new List<string>();
        if (result.Warning != null) warnings.Add(result.Warning);
        if (tree.Has(node, NodeFlags.System)) warnings.Add(L.T("条目具有系统属性，请确认用途", "The item has the System attribute; check what it's for", "この項目にはシステム属性があります。用途を確認してください"));
        if (tree.Has(node, NodeFlags.Inaccessible)) warnings.Add(L.T("扫描时没能读取该目录，实际内容和大小未知", "This folder couldn't be read during the scan; its actual contents and size are unknown", "スキャン時にこのフォルダーを読み取れなかったため、実際の内容とサイズは不明です"));
        if (tree.Alloc(node) > 10L << 30 || tree.Files(node) > 100_000) warnings.Add(BigDeletion);
        // 目录型重解析点没有子节点就是链接（junction、符号链接）；有子节点说明扫描钻进去过，是云占位之类的真目录
        if (tree.Has(node, NodeFlags.Reparse) && tree.IsDir(node) && tree.LiveChildren(node).Length == 0)
            warnings.Add(L.T("这是链接或联接点：只删除链接本身，不会删除它指向的内容", "This is a link or junction: only the link itself is deleted, not what it points to", "これはリンクまたはジャンクションです。削除されるのはリンク自体で、リンク先の内容は削除されません"));
        if (tree.Has(node, NodeFlags.Cloud)) warnings.Add(L.T("云同步或脱机文件：删除后可能同步到云端", "Cloud-synced or offline file: deleting it may sync to the cloud", "クラウド同期またはオフラインのファイルです。削除がクラウドに同期される場合があります"));
        if (tree.HardLinkGroup(node) is { Length: > 1 }) warnings.Add(L.T("这是硬链接；其他链接仍存在时不会释放文件占用", "This is a hard link; space isn't freed while other links remain", "これはハードリンクです。ほかのリンクが残っている間は容量が空きません"));
        return new(false, null, warnings.Count > 0 ? string.Join(L.T("；", "; ", "；"), warnings) : null);
    }

    /// 删除计划用：节点本身的判定，加上整棵子树里「会让这次删除作废」的拦截。调用方持有 tree.Sync。
    /// 子树里只对目录做完整的路径判定（一个目录一次，文件只查名字），三十万个节点也是毫秒级。
    internal static ProtectionResult EvaluateTree(FileTree tree, int root)
    {
        var own = Evaluate(tree, root);
        if (own.Blocked || !tree.IsDir(root)) return own;
        string? inner = null;
        var stack = new Stack<(int Node, string Path)>();
        try { stack.Push((root, Normalize(tree.GetPath(root)))); }
        catch (ArgumentException) { return Block(L.T("路径无效，拒绝删除", "Invalid path; not deleted", "パスが無効なため削除しません")); }
        while (stack.TryPop(out var frame))
            foreach (int child in tree.LiveChildren(frame.Node))
            {
                if (tree.Has(child, NodeFlags.Virtual | NodeFlags.Metafile)) return Inside(tree, child, L.T("虚拟节点和 NTFS 元数据不能删除", "Virtual items and NTFS metadata can't be deleted", "仮想の項目と NTFS のメタデータは削除できません"));
                if (KeyLookup.Contains(tree.NameSpan(child))) return Inside(tree, child, L.T("密钥、凭据或访问配置目录不能直接删除", "Key, credential or access configuration folders can't be deleted directly", "鍵・資格情報・アクセス設定のフォルダーは直接削除できません"));
                if (OddName(tree.NameSpan(child))) return Inside(tree, child, OddReason);
                if (!tree.IsDir(child)) continue;
                string path = $"{frame.Path}\\{tree.NameSpan(child)}";
                var r = EvaluatePath(path);
                if (r.Blocked) return Inside(tree, child, r.Reason!);
                if (r.Warning != null && inner == null && own.Warning == null) inner = L.T($"目录内含「{tree.GetName(child)}」：{r.Warning}", $"Contains \"{tree.GetName(child)}\": {r.Warning}", $"フォルダー内に「{tree.GetName(child)}」があります：{r.Warning}");
                if (tree.LiveChildren(child).Length > 0) stack.Push((child, path));
            }
        return inner == null ? own : new(false, null, inner);
    }

    internal static ProtectionResult EvaluatePath(string path)
    {
        path = Normalize(path);
        var known = Known.Current;
        string volume = Path.GetPathRoot(path)!;
        if (Same(path.TrimEnd('\\'), volume.TrimEnd('\\'))) return Block(L.T("卷根目录不能删除", "A volume root can't be deleted", "ボリュームのルートは削除できません"));
        string[] parts = path[volume.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string first = parts.Length > 0 ? parts[0] : "";

        if (RootDirs.Contains(first))
            return Block(first.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ? L.T("请使用清空回收站操作", "Use Empty Recycle Bin instead", "ごみ箱を空にする操作を使ってください") : L.T("系统或恢复目录，请使用 Windows 存储设置管理", "System or recovery folder; manage it in Windows storage settings", "システムまたは回復用のフォルダーです。Windows のストレージ設定で管理してください"));
        if (parts.Length == 1 && RootFiles.Contains(first)) return Block(L.T("系统启动或虚拟内存文件，请使用系统设置管理", "System boot or virtual memory file; manage it in system settings", "システムの起動ファイルまたは仮想メモリのファイルです。システムの設定で管理してください"));
        if (first.Length > 1 && first[0] == '$') return Block(L.T("系统元数据目录或文件不能直接删除", "System metadata folders or files can't be deleted directly", "システムのメタデータのフォルダーやファイルは直接削除できません"));

        // 按名字也认 Windows：经管理共享（\host\C$）或 subst 盘符扫进来时，环境里的绝对路径对不上，名字对得上
        if (first.Equals("Windows", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length == 1) return Block(L.T("Windows 目录不能删除", "The Windows folder can't be deleted", "Windows フォルダーは削除できません"));
            if (Protection.WindowsParts.Any(w => w.Equals(parts[1], StringComparison.OrdinalIgnoreCase))) return Block(L.T("Windows 核心组件不能直接删除", "Core Windows components can't be deleted directly", "Windows の中核コンポーネントは直接削除できません"));
        }
        if (known.Windows != null)
        {
            if (Same(path, known.Windows)) return Block(L.T("Windows 目录不能删除", "The Windows folder can't be deleted", "Windows フォルダーは削除できません"));
            foreach (string part in known.WindowsParts)
                if (Within(path, part)) return Block(L.T("Windows 核心组件不能直接删除", "Core Windows components can't be deleted directly", "Windows の中核コンポーネントは直接削除できません"));
        }
        if (parts.Length == 1 && RootSpecials.Contains(first)) return Block(L.T("系统、程序或用户配置根目录不能删除", "System, program or user profile root folders can't be deleted", "システム・プログラム・ユーザー プロファイルのルート フォルダーは削除できません"));
        foreach (string special in known.Roots)
            if (Same(path, special)) return Block(L.T("系统、程序或用户配置根目录不能删除", "System, program or user profile root folders can't be deleted", "システム・プログラム・ユーザー プロファイルのルート フォルダーは削除できません"));
        // Users\<任何名字> 都是用户配置文件根（含 Public、Default），环境里的当前用户那份可能搬过家，也认
        if (parts.Length == 2 && first.Equals("Users", StringComparison.OrdinalIgnoreCase) || known.ProfileRoot != null && Same(path, known.ProfileRoot))
            return Block(L.T("用户配置文件根目录不能删除", "A user profile root folder can't be deleted", "ユーザー プロファイルのルート フォルダーは削除できません"));
        if (known.Own != null && (Within(path, known.Own) || Within(known.Own, path))) return Block(L.T("DiskLens 正在使用该目录，不能删除", "DiskLens is using this folder, so it can't be deleted", "DiskLens が使用中のフォルダーのため削除できません"));
        foreach (string part in parts)
            if (KeyFolders.Contains(part)) return Block(L.T("密钥、凭据或访问配置目录不能直接删除", "Key, credential or access configuration folders can't be deleted directly", "鍵・資格情報・アクセス設定のフォルダーは直接削除できません"));

        var warnings = new List<string>();
        if (known.Roaming != null && Within(path, known.Roaming)) warnings.Add(L.T("漫游配置可能包含登录状态和个人设置", "Roaming data may include sign-ins and personal settings", "移動プロファイルのデータには、サインイン状態や個人設定が含まれる場合があります"));
        if (known.Windows != null && Within(path, known.Windows) || first.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase) || first.Equals("ProgramData", StringComparison.OrdinalIgnoreCase))
            warnings.Add(L.T("系统或程序数据，删除可能影响程序运行", "System or program data; deleting it may break programs", "システムまたはプログラムのデータです。削除するとプログラムの動作に影響する場合があります"));
        if (parts.Any(CredentialFolders.Contains)) warnings.Add(L.T("含登录凭据或访问配置，删除后需要重新登录或配置", "Contains sign-in credentials or access settings; you'll need to sign in or set up again after deleting", "サインインの資格情報やアクセス設定を含みます。削除後はサインインや設定をやり直す必要があります"));
        return warnings.Count == 0 ? ProtectionResult.Allowed : new(false, null, string.Join(L.T("；", "; ", "；"), warnings));
    }

    static string OddReason => L.T("名字以点或空格结尾（或只由点组成），系统会把它当成别的路径；请在资源管理器里处理",
        "The name ends with a dot or space (or is only dots), so Windows treats it as a different path; handle it in File Explorer",
        "名前の末尾がピリオドかスペース（またはピリオドだけ）のため、Windows は別のパスとして扱います。エクスプローラーで操作してください");

    static bool OddName(ReadOnlySpan<char> name) => name.Length > 0 && (name[^1] == '.' || name[^1] == ' ');

    /// 绝对路径规整：正斜杠换反斜杠、去掉 \\?\ 前缀、解开 ..、去掉结尾分隔符；设备路径、相对路径和备用数据流一律拒绝。
    internal static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = path.Replace('/', '\\');
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path) || path.Contains('\0')) throw new ArgumentException("路径无效");
        string full = Path.GetFullPath(path);
        if (full.AsSpan(full.StartsWith(@"\\", StringComparison.Ordinal) ? 0 : 2).Contains(':')) throw new ArgumentException(L.T("不允许设备路径或备用数据流", "Device paths and alternate data streams aren't allowed", "デバイス パスや代替データ ストリームは使えません"));
        return Path.TrimEndingDirectorySeparator(full);
    }

    internal static bool Within(string path, string parent) => Same(path, parent) || path.StartsWith(parent.EndsWith('\\') ? parent : parent + "\\", StringComparison.OrdinalIgnoreCase);
    internal static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    static ProtectionResult Block(string reason) => new(true, reason, null);
    static ProtectionResult Inside(FileTree tree, int child, string reason) => Block(L.T($"目录包含受保护条目「{tree.GetName(child)}」：{reason}", $"Contains the protected item \"{tree.GetName(child)}\": {reason}", $"保護された項目「{tree.GetName(child)}」を含みます：{reason}"));
}
