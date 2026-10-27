namespace DiskLens.Core;

/// 所属对象（dise §7.1、§8.1）：识别出的应用、游戏、项目、包管理器……同种同名的合并成一个对象（程序目录和 AppData 里的 Steam、
/// 游戏库里和「保存的游戏」里的同一个游戏）。Evidence 是这个对象本身认得多准（几处来源里最强的）；
/// 某个节点归它的依据看节点所在的那个根（OwnerRoot.Evidence）——按目录名并进来的那一处只是推测，不连累游戏库那一处。
public sealed record Owner(OwnerKind Kind, string Name, Evidence Evidence);

/// 所属对象在树里的一个根：哪个对象（Owners 的下标）、凭什么认出来的。
internal readonly record struct OwnerRoot(int Owner, OwnerSource Source)
{
    /// 这一处归属的证据：ProgramDir 之前的来源（位置规则、项目标记、安装信息、包名……）是已确认，之后的（按目录名猜）是推测。
    public Evidence Evidence => Source < OwnerSource.ProgramDir ? Evidence.Confirmed : Evidence.Inferred;
}

/// 一次用途分类的结果（DESIGN §4.7）：每个节点的主用途、数据角色和用途的证据等级，外加所属对象表和它们在树里的根。
/// FileTree.SetClassification 一次收下。
public sealed class Classification
{
    /// 角色占每个字节的低 6 位（Roles.Count ≤ 64），用途的证据等级占高 2 位——省一个按节点的数组。
    internal const int RoleMask = 63;

    internal Classification(byte[] purposes, byte[] roleBits, Dictionary<int, OwnerRoot> ownerRoots, Owner[] owners)
    {
        Purposes = purposes;
        RoleBits = roleBits;
        OwnerRoots = ownerRoots;
        Owners = owners;
    }

    /// 下标是节点 ID。
    public byte[] Purposes { get; }

    internal byte[] RoleBits { get; }

    /// 所属对象的根节点 → 对象；节点的所属对象是从它往上第一个根的对象。只有根进字典，不按节点开数组。
    internal Dictionary<int, OwnerRoot> OwnerRoots { get; }

    public Owner[] Owners { get; }

    public byte Role(int i) => (byte)(RoleBits[i] & RoleMask);

    public Evidence PurposeEvidence(int i) => (Evidence)(RoleBits[i] >> 6);
}

/// 分类过程中攒所属对象：同种同名（不区分大小写）合并，对象的证据取几处来源里最强的。
sealed class OwnerTable
{
    public readonly List<Owner> List = [];
    public readonly Dictionary<int, OwnerRoot> Roots = [];
    readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);

    public void Add(int node, OwnerKind kind, string name, OwnerSource source)
    {
        var root = new OwnerRoot(-1, source);
        string key = $"{(int)kind}:{name}";
        if (!_index.TryGetValue(key, out int k))
        {
            k = List.Count;
            List.Add(new Owner(kind, name, root.Evidence));
            _index[key] = k;
        }
        else if (root.Evidence > List[k].Evidence) List[k] = List[k] with { Evidence = root.Evidence };
        Roots[node] = root with { Owner = k };
    }
}

/// 用途分类（DESIGN §4.7，排序见 PurposeRules 顶上的说明）。
/// 规则编号即优先级，数字越小越优先。每条规则只给「自己命中的节点」打个种子，然后借「子下标 > 父下标」
/// 从前往后扫一遍：节点取继承来的规则，除非自己的种子或扩展名证据更优先。没碰上任何规则的目录最后从后往前汇总
/// 子树里各用途的大小，占 ≥ 60% 的说了算，否则未识别。全程只读树、不拼路径字符串。
/// 数据角色跟着同一趟走：规则命中时由规则给出，容器里再按目录名（node_modules、Mods……）和扩展名细分。
/// 所属对象也在这一趟里认：规则自带的（npm、Windows、浏览器）、项目标记、游戏安装信息，以及「容器的子目录各是一个对象」
/// （游戏库、Program Files、AppData、应用包、扩展目录、存档目录）。
/// 用途的证据等级按依据的种类定：位置规则、名称规则、安装信息、项目标记、元数据标记命中的整棵子树是已确认；
/// 扩展名、用户文件夹里没有类型证据的文件（位置只说明在哪）、按子树主要用途展示的目录是推测；未识别、未解释空间是未识别。
public static class PurposeClassifier
{
    const byte None = PurposeRules.None;
    const int W = Purposes.Count;

    /// AI 规则的扩展名兜底线：50 MB（1024 进制，和界面一致）。
    const long AiMinSize = 50L << 20;

    /// 详情中的分类依据；调用方持有 tree.Sync。只检查路径和祖先标记，不重跑整棵树分类、不读取文件内容。
    public static string Explain(FileTree tree, int id) => Explain(tree, id, PurposeEnv.Real);

    internal static string Explain(FileTree tree, int id, PurposeEnv env)
    {
        byte u = tree.Purpose(id);
        bool isDir = tree.IsDir(id);
        string ext = isDir ? "" : tree.ExtName(tree.ExtId(id));
        string kind = isDir ? L.T("文件夹", "Folder", "フォルダー") : FileCategories.Describe(ext);
        string caution = L.T("用途分类不代表可安全清理。", "A purpose doesn't mean it's safe to clean up.", "用途の分類は、安全にクリーンアップできることを意味しません。");
        if (tree.Has(id, NodeFlags.Metafile)) return L.T("扫描器识别为文件系统元数据。", "Identified by the scanner as file system metadata. ", "スキャナーがファイル システムのメタデータとして識別しました。") + caution;
        if (tree.Has(id, NodeFlags.Virtual))
            return isDir ? L.T("扫描器生成的虚拟分组，不是普通文件夹。", "A virtual group created by the scanner, not a regular folder. ", "スキャナーが作った仮想のグループで、通常のフォルダーではありません。") + caution
                : L.T("未解释空间：卷的已用空间里没有被扫描统计到的部分，不是某个具体文件，也不当作系统文件。", "Unexplained space: the part of the volume's used space the scan didn't count; it's not any particular file and isn't treated as system files. ", "説明できない領域：ボリュームの使用量のうちスキャンで集計されなかった部分です。特定のファイルではなく、システム ファイルとしても扱いません。") + caution;
        if (u == Purposes.Unknown) return L.T($"文件类型：{kind}；未识别：没有命中任何用途规则，现有依据不足以判断用途。{OwnerText(tree, id)}{caution}", $"File type: {kind}; unidentified: no purpose rule matched, and there isn't enough basis to judge the purpose. {OwnerText(tree, id)}{caution}", $"ファイルの種類：{kind}。未識別：どの用途の規則にも一致せず、用途を判断する根拠が足りません。{OwnerText(tree, id)}{caution}");

        string path = NormRoot(tree.GetPath(id));
        var rules = PurposeRules.Build(env, tree.RootPath);
        byte best = None, bestU = Purposes.Unknown;
        string? basis = null;
        void Pick(byte priority, byte pu, string text) { if (priority < best) { best = priority; bestU = pu; basis = text; } }
        bool ownPath = false;
        foreach (var (rulePath, hit) in rules.Paths)
        {
            if (path.Equals(rulePath, StringComparison.OrdinalIgnoreCase)) { ownPath = true; Pick(hit.Rule, hit.Purpose, L.T("位置规则：", "location rule: ", "場所の規則：") + rulePath); }
            else if (IsInside(path, rulePath)) Pick(hit.Rule, hit.Purpose, L.T("继承上级目录的位置规则：", "location rule inherited from a parent folder: ", "親フォルダーから継承した場所の規則：") + rulePath);
        }
        // DumpStack.log* 带通配，字典里没有；和分类器一样按卷根的直接子项认
        if (!isDir && IsVolumeRoot(Path.GetDirectoryName(path) ?? "") && tree.NameSpan(id).StartsWith("DumpStack.log", StringComparison.OrdinalIgnoreCase))
        {
            ownPath = true;
            Pick(PurposeRules.RSystem, Purposes.System, L.T("位置规则：卷根的 " + tree.GetName(id), $"location rule: {tree.GetName(id)} on the volume root", $"場所の規則：ボリュームのルートの {tree.GetName(id)}"));
        }
        foreach (string hub in rules.HfHubs)
        {
            if (!IsInside(path, hub)) continue;
            var h = PurposeRules.HubRepo(path.AsSpan(hub.Length + 1));
            if (h.Rule != 0) Pick(h.Rule, h.Purpose, (h.Kind == OwnerKind.Model ? L.T("Hugging Face 模型缓存仓库：", "Hugging Face model cache repository: ", "Hugging Face のモデル キャッシュ リポジトリ：") : L.T("Hugging Face 数据集缓存仓库：", "Hugging Face dataset cache repository: ", "Hugging Face のデータセット キャッシュ リポジトリ：")) + hub + "\\" + path[(hub.Length + 1)..].Split('\\')[0]);
        }

        for (string? p = isDir ? path : Path.GetDirectoryName(path); p != null; p = Path.GetDirectoryName(p))
        {
            var h = PurposeRules.DirName(Path.GetFileName(p), Path.GetFileName(Path.GetDirectoryName(p)));
            if (h.Rule != 0) Pick(h.Rule, h.Purpose, (p.Length == path.Length ? L.T("目录名称规则：", "folder name rule: ", "フォルダー名の規則：") : L.T("继承上级目录的名称规则：", "name rule inherited from a parent folder: ", "親フォルダーから継承した名前の規則：")) + p);
        }
        // 和分类器一样：文件自己有位置规则（卷根的 DumpStack.log）时扩展名不参与
        if (!isDir && !ownPath)
        {
            byte type = PurposeRules.TypePurpose(tree.Category(id), ext);
            if (PurposeRules.IsLogExtension(ext)) Pick(PurposeRules.RLogs, Purposes.Logs, L.T($"日志与诊断类扩展名 .{ext}（文件本身说明了角色，比所在目录的用途优先）", $"log or diagnostics extension .{ext} (the file itself shows its role, which takes priority over its folder's purpose)", $"ログ・診断系の拡張子 .{ext}（ファイル自体が役割を示すため、フォルダーの用途より優先します）"));
            else if (PurposeRules.IsModelExtension(ext) && tree.Size(id) >= AiMinSize)
                Pick(PurposeRules.RAi, Purposes.Ai, L.T($"模型格式扩展名 .{ext}，文件大小至少 50 MB（未读取内容验证）", $"model format extension .{ext}, file size at least 50 MB (content not checked)", $"モデル形式の拡張子 .{ext}、ファイル サイズ 50 MB 以上（内容は未検証）"));
            else if (type != Purposes.Unknown)
                Pick(PurposeRules.RType, type, L.T($"按扩展名 .{ext} 兜底归为「{Purposes.Name(type)}」（所在位置没有更具体的规则，未读取内容验证）",
                    $"classified as \"{Purposes.Name(type)}\" by the .{ext} extension as a fallback (no more specific rule for its location; content not checked)",
                    $"拡張子 .{ext} から既定で「{Purposes.Name(type)}」に分類（場所に応じたより具体的な規則がなく、内容は未検証）"));
        }

        if (best > PurposeRules.RGames)
            for (int p = tree.IsDir(id) ? id : tree.Parent(id); p >= 0; p = tree.Parent(p))
            {
                string dirPath = NormRoot(tree.GetPath(p));
                bool exempt = IsVolumeRoot(dirPath) || rules.NoProject.Contains(dirPath) || rules.NoProjectKids.Contains(dirPath)
                    || rules.NoProjectKids.Contains(Path.GetDirectoryName(dirPath) ?? "");
                foreach (int c in tree.LiveChildren(p))
                {
                    if (PurposeRules.IsGameMarker(tree.NameSpan(c))) Pick(PurposeRules.RGames, Purposes.Games, L.T("游戏安装信息：", "game install info: ", "ゲームのインストール情報：") + tree.GetPath(c));
                    else if (!exempt && (PurposeRules.IsMarker(tree.NameSpan(c)) || tree.ExtName(tree.ExtId(c)) is "sln" or "slnx" or "csproj"))
                        Pick(PurposeRules.RDev, Purposes.Dev, L.T("项目标记：", "project marker: ", "プロジェクトの目印：") + tree.GetPath(c));
                }
            }

        if (basis is null) basis = isDir
            ? L.T("子树中该用途占已计入空间的至少 60%（不是目录内每个文件的类型）", "this purpose takes at least 60% of the counted space in the subtree (not the type of every file in the folder)", "サブツリーでこの用途が集計済み容量の 60% 以上を占めます（フォルダー内のすべてのファイルの種類ではありません）")
            : L.T("按文件扩展名推断所属用途，未读取内容验证", "purpose inferred from the file extension; content not checked", "ファイルの拡張子から用途を推定（内容は未検証）");
        // 继承来的目录换成了子树的主要用途（分类器的 Dominate）：先说展示色从哪来，再说目录本身沿用的规则
        else if (isDir && bestU != u)
            basis = L.T($"子树中「{Purposes.Name(u)}」占已计入空间的至少 60%，目录按它展示（子项各自的分类不变）；目录本身沿用「{Purposes.Name(bestU)}」，{basis}",
                $"\"{Purposes.Name(u)}\" takes at least 60% of the counted space in the subtree, so the folder is shown as it (items inside keep their own purposes); the folder itself keeps \"{Purposes.Name(bestU)}\", {basis}",
                $"サブツリーで「{Purposes.Name(u)}」が集計済み容量の 60% 以上を占めるため、フォルダーはそれで表示します（中の項目の分類はそのまま）。フォルダー自体は「{Purposes.Name(bestU)}」のまま：{basis}");
        return L.T($"文件类型：{kind}；{basis}。{RoleText(tree, id, ext, ownPath)}{OwnerText(tree, id)}{caution}", $"File type: {kind}; {basis}. {RoleText(tree, id, ext, ownPath)}{OwnerText(tree, id)}{caution}", $"ファイルの種類：{kind}。{basis}。{RoleText(tree, id, ext, ownPath)}{OwnerText(tree, id)}{caution}");
    }

    /// 角色从哪来：文件和所在目录角色不同只可能是扩展名给的（自己就是位置规则目标的除外，卷根的 DumpStack.log）；
    /// 否则往上找角色从哪一层开始，那一层的目录名能细分出这个角色（node_modules、mods……）就说目录名，不然就是和用途同一条依据。
    static string RoleText(FileTree tree, int id, string ext, bool ownPath)
    {
        byte role = tree.Role(id);
        if (role == Roles.Unknown) return L.T("角色：未知（没有能说明数据角色的依据）。", "Role: unknown (no basis for the data role). ", "役割：不明（データの役割を示す根拠がありません）。");
        string name = Roles.Name(role);
        bool isDir = tree.IsDir(id);
        int parent = tree.Parent(id);
        if (!isDir && !ownPath && (parent < 0 || tree.Role(parent) != role))
            return L.T($"角色「{name}」：按扩展名 .{ext} 推断（未读取内容验证）。", $"Role \"{name}\": inferred from the .{ext} extension (content not checked). ", $"役割「{name}」：拡張子 .{ext} から推定（内容は未検証）。");
        if (role == Roles.Weights && !isDir && !PurposeRules.IsModelExtension(ext))
            return L.T($"角色「{name}」：按所在的模型目录推断，目录里也可能有清单、配置这类小文件（未读取内容验证）。", $"Role \"{name}\": inferred from the model folder it's in; the folder may also hold small files such as manifests and configs (content not checked). ", $"役割「{name}」：モデルのフォルダーにあることから推定。フォルダーにはマニフェストや設定などの小さなファイルもある場合があります（内容は未検証）。");
        int top = isDir ? id : parent;
        while (top > FileTree.RootId && tree.Role(tree.Parent(top)) == role) top = tree.Parent(top);
        if (top > FileTree.RootId && PurposeRules.Refine(tree.Role(tree.Parent(top)), tree.NameSpan(top)) == role)
            return L.T($"角色「{name}」：来自目录名「{tree.GetName(top)}」（{tree.GetPath(top)}）。", $"Role \"{name}\": from the folder name \"{tree.GetName(top)}\" ({tree.GetPath(top)}). ", $"役割「{name}」：フォルダー名「{tree.GetName(top)}」から（{tree.GetPath(top)}）。");
        return L.T($"角色「{name}」：和用途出自同一条依据。", $"Role \"{name}\": from the same basis as the purpose. ", $"役割「{name}」：用途と同じ根拠によります。");
    }

    /// 所属对象和这一处归属的依据；没有可靠依据就直说（dise §8.1「尚未识别关联对象」）。
    static string OwnerText(FileTree tree, int id)
    {
        if (tree.OwnerOf(id, out int root) is not { } owner) return L.T("所属对象：尚未识别（没有可靠的归属依据）。", "Owner: not identified yet (no reliable basis). ", "所属：まだ識別されていません（確かな根拠がありません）。");
        string why = tree.OwnerSourceAt(root) switch
        {
            OwnerSource.PathRule => L.T("位置规则", "location rule", "場所の規則"),
            OwnerSource.Project => L.T("项目标记", "project marker", "プロジェクトの目印"),
            OwnerSource.GameInstall => L.T("游戏安装信息 .build.info", "game install info .build.info", "ゲームのインストール情報 .build.info"),
            OwnerSource.GameLibrary => L.T("游戏库里的游戏文件夹", "game folder in a game library", "ゲーム ライブラリ内のゲーム フォルダー"),
            OwnerSource.HubRepo => L.T("Hugging Face 缓存仓库名", "Hugging Face cache repository name", "Hugging Face のキャッシュ リポジトリ名"),
            OwnerSource.AppPackage => L.T("Windows 应用包名", "Windows app package name", "Windows アプリのパッケージ名"),
            OwnerSource.Plugin => L.T("扩展目录名（去掉版本号）", "extension folder name (without the version)", "拡張機能のフォルダー名（バージョンを除く）"),
            OwnerSource.ProgramDir => L.T("按程序目录下的文件夹名推测", "guessed from the folder name under the program folder", "プログラム フォルダー下のフォルダー名から推定"),
            OwnerSource.VendorApp => L.T("按「厂商\\应用」的目录结构推测（目录里直接有 .exe）", "guessed from a \"Vendor\\App\" folder layout (the folder has an .exe directly)", "「ベンダー\\アプリ」のフォルダー構成から推定（フォルダー直下に .exe があります）"),
            OwnerSource.AppDataDir => L.T("按 AppData、ProgramData 下的文件夹名推测", "guessed from the folder name under AppData or ProgramData", "AppData・ProgramData 下のフォルダー名から推定"),
            _ => L.T("按存档目录下的文件夹名推测", "guessed from the folder name under a saves folder", "セーブ フォルダー下のフォルダー名から推定"),
        };
        string kind = Roles.OwnerKindName((int)owner.Kind), evidence = Roles.EvidenceName((int)tree.OwnerEvidenceAt(root));
        return L.T($"所属对象「{owner.Name}」（{kind}，{evidence}）：{why}，{tree.GetPath(root)}。", $"Owner \"{owner.Name}\" ({kind}, {evidence}): {why}, {tree.GetPath(root)}. ",
            $"所属「{owner.Name}」（{kind}、{evidence}）：{why}、{tree.GetPath(root)}。");
    }

    /// 树必须已 Seal；调用方持有 tree.Sync，随后 tree.SetClassification。
    public static Classification Classify(FileTree tree, CancellationToken ct) => Classify(tree, PurposeEnv.Real, ct);

    internal static Classification Classify(FileTree tree, PurposeEnv env, CancellationToken ct)
    {
        if (!tree.IsSealed) throw new InvalidOperationException("树还没 Seal，不能分类");
        ct.ThrowIfCancellationRequested();
        int n = tree.Count;
        var purpose = new byte[n];
        var rule = new byte[n];
        var role = new byte[n];
        Array.Fill(rule, None);

        // 先放项目、游戏标记的种子，再按豁免清掉不该当项目根的，最后才放路径规则——顺序反了会误伤路径规则的种子
        string root = NormRoot(tree.RootPath);
        MarkProjects(tree, rule, purpose, role, ct);
        var rules = PurposeRules.Build(env, root);
        Exempt(tree, rules, root, rule, purpose, role);
        byte rootMarker = rule[FileTree.RootId];
        var hits = new Dictionary<int, PurposeRules.Hit>();
        Seed(tree, rules, root, rule, purpose, role, hits);
        var slot = new int[n];
        var own = new bool[n];
        own[FileTree.RootId] = rule[FileTree.RootId] != None;
        int exe = -1;
        for (int e = 1; e < tree.ExtCount; e++)
            if (tree.ExtName(e) == "exe") exe = e;
        var owners = new OwnerTable();
        var rootKids = RootContext(tree, rules, root, rule[FileTree.RootId] == rootMarker ? rootMarker : None, owners, exe);
        role[FileTree.RootId] |= (byte)((int)RootEvidence(rules, root, rule[FileTree.RootId]) << 6);
        int dirs = Propagate(tree, rule, purpose, role, hits, owners, rootKids, exe, slot, own, ct);
        Dominate(tree, rule, purpose, role, slot, own, dirs, ct);
        return new Classification(purpose, role, owners.Roots, owners.List.ToArray());
    }

    static void Put(byte[] rule, byte[] purpose, byte[] role, int id, PurposeRules.Hit h)
    {
        if (h.Rule >= rule[id]) return;
        rule[id] = h.Rule;
        purpose[id] = h.Purpose;
        role[id] = h.Role;
    }

    static void SetEvidence(byte[] role, int i, Evidence e) => role[i] = (byte)((role[i] & Classification.RoleMask) | ((int)e << 6));

    /// 扫描根自己的证据等级：只有落在用户文件夹「里面」（而不是用户文件夹本身）才是推测，没有规则的等 Dominate 定。
    static Evidence RootEvidence(PurposeRules rules, string root, byte r) =>
        r == None ? Evidence.Unknown
        : r == PurposeRules.RUser && !rules.Paths.ContainsKey(root.TrimEnd('\\')) ? Evidence.Inferred
        : Evidence.Confirmed;

    /// 扫描根上方那几层不在树里：顺着根路径把「子目录是什么」和最近的所属对象走一遍，给根认个所属对象，返回根的子目录是什么。
    /// 直接扫 steamapps\common\某游戏、Program Files\某应用、AppData\Roaming\某应用、某个项目的时候用得上。
    /// ponytail: 根上方只看位置和名字、不比较规则优先级（回收站里的 Program Files\X 也会被认成应用）；真碰上再按 Propagate 的顺序补。
    static Kids RootContext(FileTree tree, PurposeRules rules, string root, byte marker, OwnerTable owners, int exe)
    {
        string vol = Path.GetPathRoot(root) ?? "";
        var segs = root[vol.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string prefix = vol.TrimEnd('\\'), parent = "";
        var kids = Kids.None;
        (OwnerKind Kind, string Name, OwnerSource Source)? found = null;
        for (int k = 0; k < segs.Length; k++)
        {
            string seg = segs[k];
            var up = kids;
            bool inHub = rules.HfHubs.Contains(prefix);
            prefix += "\\" + seg;
            var hit = rules.Paths.TryGetValue(prefix, out var pathHit) ? pathHit : PurposeRules.DirName(seg, parent);
            if (hit.Rule == 0 && inHub) hit = PurposeRules.HubRepo(seg);
            kids = hit.Kids;
            if (hit.Owner != null) found = (hit.Kind, hit.Owner, hit.Source);
            else if (hit.Rule == 0 && PurposeRules.KidOwner(up, seg) is { } kid)
            {
                // 厂商目录只能对根本身判断（上方的目录不在树里，看不到它们的子项）
                bool last = k == segs.Length - 1;
                if (up != Kids.Vendor || (last && HasExe(tree, FileTree.RootId, exe))) found = (kid.Kind, kid.Name, kid.Source);
                if (up == Kids.Apps && last && IsVendor(tree, FileTree.RootId, exe)) kids = Kids.Vendor;
            }
            else if (up == Kids.SteamApps && seg.Equals("common", StringComparison.OrdinalIgnoreCase)) kids = Kids.Games;
            parent = seg;
        }
        // 根自己的项目标记、游戏安装信息比上方推出来的更直接
        string name = segs.Length > 0 ? segs[^1] : root;
        if (marker == PurposeRules.RDev) owners.Add(FileTree.RootId, OwnerKind.Project, name, OwnerSource.Project);
        else if (marker == PurposeRules.RGames) owners.Add(FileTree.RootId, OwnerKind.Game, name, OwnerSource.GameInstall);
        else if (found is { } f) owners.Add(FileTree.RootId, f.Kind, f.Name, f.Source);
        return kids;
    }

    static bool HasExe(FileTree tree, int dir, int exe)
    {
        if (exe < 0) return false;
        foreach (int c in tree.LiveChildren(dir))
            if (tree.ExtId(c) == exe) return true;
        return false;
    }

    /// 厂商目录：自己不带 .exe，某个子目录直接带（Program Files\Adobe\Adobe Photoshop 2026\Photoshop.exe）。
    static bool IsVendor(FileTree tree, int dir, int exe)
    {
        if (HasExe(tree, dir, exe)) return false;
        foreach (int c in tree.LiveChildren(dir))
            if (tree.IsDir(c) && HasExe(tree, c, exe)) return true;
        return false;
    }

    /// 只看每个节点的名字，把它的父目录标成项目根或游戏根——按下标顺序扫，名字池和数组都是顺序访问，比逐目录枚举子节点快得多。
    static void MarkProjects(FileTree tree, byte[] rule, byte[] purpose, byte[] role, CancellationToken ct)
    {
        var projectExt = new bool[tree.ExtCount];
        for (int e = 1; e < projectExt.Length; e++) projectExt[e] = tree.ExtName(e) is "sln" or "slnx" or "csproj";
        var project = new PurposeRules.Hit(PurposeRules.RDev, Purposes.Dev, Roles.Source);
        var game = new PurposeRules.Hit(PurposeRules.RGames, Purposes.Games, Roles.Game);
        for (int i = 1; i < tree.Count; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            var name = tree.NameSpan(i);
            bool isProject = projectExt[tree.ExtId(i)] || PurposeRules.IsMarker(name);
            if (!isProject && !PurposeRules.IsGameMarker(name)) continue;
            if (tree.Has(i, NodeFlags.Deleted)) continue;
            Put(rule, purpose, role, tree.Parent(i), isProject ? project : game);
        }
    }

    /// 豁免的目录自己不能当项目根；此刻 rule 里只有项目和游戏标记的种子，只清项目的（游戏安装信息不是「家里偶尔有个 package.json」那种误会）。
    static void Exempt(FileTree tree, PurposeRules rules, string root, byte[] rule, byte[] purpose, byte[] role)
    {
        void Clear(int id)
        {
            if (id == FileTree.NoNode || rule[id] != PurposeRules.RDev) return;
            rule[id] = None;
            purpose[id] = Purposes.Unknown;
            role[id] = Roles.Unknown;
        }
        if (IsVolumeRoot(root)) Clear(FileTree.RootId);
        foreach (var path in rules.NoProject) Clear(tree.FindByPath(path));
        foreach (var path in rules.NoProjectKids)
        {
            int dir = tree.FindByPath(path);
            if (dir == FileTree.NoNode) continue;
            Clear(dir);
            foreach (int c in tree.LiveChildren(dir))
                if (tree.IsDir(c)) Clear(c);
        }
    }

    /// 路径规则：FindByPath 定位节点打种子；扫描根本身落在规则目录里面时（扫的是某个子文件夹），
    /// 根也算命中，否则只扫 npm 缓存的一个子目录就全变成「未识别」了。
    /// hits 记下种子来自哪条位置规则（项目、游戏标记不进），Propagate 要从里面拿所属对象和「子目录是什么」。
    static void Seed(FileTree tree, PurposeRules rules, string root, byte[] rule, byte[] purpose, byte[] role, Dictionary<int, PurposeRules.Hit> hits)
    {
        void Put(int id, PurposeRules.Hit h)
        {
            if (h.Rule >= rule[id]) return;
            PurposeClassifier.Put(rule, purpose, role, id, h);
            hits[id] = h;
        }
        // 根可能同时落在好几条规则里（AppData → 浏览器资料）：同号时取更深的那条，它说的角色更具体
        int rootDepth = -1;
        void PutRoot(PurposeRules.Hit h, int depth)
        {
            if (h.Rule == 0 || h.Rule > rule[FileTree.RootId] || (h.Rule == rule[FileTree.RootId] && depth <= rootDepth)) return;
            rule[FileTree.RootId] = h.Rule;
            purpose[FileTree.RootId] = h.Purpose;
            role[FileTree.RootId] = h.Role;
            rootDepth = depth;
        }

        foreach (string hub in rules.HfHubs)
        {
            if (IsInside(root, hub)) PutRoot(PurposeRules.HubRepo(root.AsSpan(hub.Length + 1)), hub.Length + 1);
            int id = tree.FindByPath(hub);
            if (id == FileTree.NoNode) continue;
            foreach (int c in tree.LiveChildren(id))
            {
                if (!tree.IsDir(c)) continue;
                var h = PurposeRules.HubRepo(tree.NameSpan(c));
                if (h.Rule != 0) Put(c, h);
            }
        }
        // ponytail: 每条规则一次 FindByPath，沿途每层线性扫子节点；除非规则路径恰好穿过百万子节点的目录，否则几毫秒的事，到那时再改成一次 trie 遍历
        foreach (var (path, hit) in rules.Paths)
        {
            int id = tree.FindByPath(path);
            if (id == FileTree.RootId || (id == FileTree.NoNode && IsInside(root, path))) PutRoot(hit, path.Length);
            else if (id != FileTree.NoNode) Put(id, hit);
        }

        // 按名字认的规则对扫描根上方的目录同样适用（比如直接扫 D:\SteamLibrary\steamapps\common）
        var segs = root.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        int depth = segs.Length > 0 ? segs[0].Length : 0;
        for (int k = 1; k < segs.Length; k++)
        {
            depth += segs[k].Length + 1;
            PutRoot(PurposeRules.DirName(segs[k], segs[k - 1]), depth);
        }

        // DumpStack.log* 带通配，字典写不了，扫一眼卷根的直接子节点
        int vol = tree.FindByPath(Path.GetPathRoot(root) ?? "");
        if (vol == FileTree.NoNode) return;
        var dump = new PurposeRules.Hit(PurposeRules.RSystem, Purposes.System, Roles.System, Kind: OwnerKind.System, Owner: "Windows");
        foreach (int c in tree.LiveChildren(vol))
            if (tree.NameSpan(c).StartsWith("DumpStack.log", StringComparison.OrdinalIgnoreCase))
                Put(c, dump);
    }

    /// FindByPath 认斜杠和 \\?\ 前缀，这里自己比字符串也得认，不然前缀一不同就一条规则都对不上。
    /// 卷根保留尾部反斜杠（"C:" 拼 Path.Combine 会变成「C 盘当前目录」）。
    static string NormRoot(string path)
    {
        path = path.Replace('/', '\\');
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return PurposeRules.Full(path) ?? path.TrimEnd('\\') + "\\";
    }

    static bool IsInside(string root, string dir) =>
        root.Length > dir.Length && root[dir.Length] == '\\' && root.StartsWith(dir, StringComparison.OrdinalIgnoreCase);

    static bool IsVolumeRoot(string path) =>
        Path.GetPathRoot(path)?.TrimEnd('\\').Length == path.TrimEnd('\\').Length;

    /// 自顶向下：每个节点从「自己的种子 / 节点自带属性 / 名字扩展名规则」里挑最优先的，
    /// 比父节点继承下来的更优先才覆盖。顺便给每个目录编号，供后面的汇总数组用；
    /// own 记下目录的用途是不是它自己的证据给的（继承来的目录展示色可以换成子树的主要用途，见 Dominate）。
    /// 角色：自己的证据赢了就用它的角色（同号时更深的规则更具体，也算赢——AppData 里的浏览器资料目录；
    /// 项目标记例外，node_modules 里每个包都带 package.json，不能因此翻回源代码），否则继承父目录的，再按目录名、扩展名细分。
    /// 所属对象：赢了的位置规则自带的 → 项目标记（不在依赖、构建产物里）、游戏安装信息 → 父目录说「子目录各是一个对象」的。
    /// kids 记每个目录的子目录是什么（只给这一趟用）。
    static int Propagate(FileTree tree, byte[] rule, byte[] purpose, byte[] role, Dictionary<int, PurposeRules.Hit> hits, OwnerTable owners,
        Kids rootKids, int exe, int[] slot, bool[] own, CancellationToken ct)
    {
        // 每种扩展名的证据只算一次：日志和转储、模型格式（循环里再看大小）、类型兜底；外加两个只影响角色的特征
        int extCount = tree.ExtCount;
        var extRule = new byte[extCount];
        var extU = new byte[extCount];
        var extRole = new byte[extCount];
        var exec = new bool[extCount];
        var config = new bool[extCount];
        Array.Fill(extRule, None);
        for (int e = 1; e < extCount; e++)
        {
            string name = tree.ExtName(e);
            byte k = FileCategories.FromExtension(name);
            byte type = PurposeRules.TypePurpose(k, name);
            if (PurposeRules.IsLogExtension(name)) (extRule[e], extU[e], extRole[e]) = (PurposeRules.RLogs, Purposes.Logs, PurposeRules.LogRole(name));
            else if (PurposeRules.IsModelExtension(name)) (extRule[e], extU[e], extRole[e]) = (PurposeRules.RAi, Purposes.Ai, Roles.Weights);
            else if (type != Purposes.Unknown) (extRule[e], extU[e], extRole[e]) = (PurposeRules.RType, type, PurposeRules.TypeRole(k, name));
            exec[e] = k == FileCategories.Executable;
            config[e] = PurposeRules.IsConfigExtension(name);
        }

        var kids = new Kids[tree.Count];
        kids[FileTree.RootId] = rootKids;
        int dirs = 1; // 根占第 0 行
        for (int i = 1; i < tree.Count; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            var f = tree.Flags(i);
            int p = tree.Parent(i);
            byte r = rule[i], u = purpose[i], ro = role[i];
            bool dir = (f & NodeFlags.Directory) != 0, byExt = false;
            // 种子来自位置规则（Hugging Face 仓库、DumpStack 也算）就记下那条命中；项目、游戏标记的种子不在 hits 里
            PurposeRules.Hit hit = default;
            bool marker = r != None && !hits.TryGetValue(i, out hit);

            if (dir)
            {
                slot[i] = dirs++;
                if ((f & NodeFlags.Metafile) != 0 || ((f & NodeFlags.Virtual) != 0 && PurposeRules.IsMetaGroup(tree.NameSpan(i))))
                    (r, u, ro, hit, marker) = (PurposeRules.RMeta, Purposes.System, Roles.System, default, false);
                else if ((f & NodeFlags.Virtual) == 0)
                {
                    var h = PurposeRules.DirName(tree.NameSpan(i), tree.NameSpan(p));
                    if (h.Rule != 0 && h.Rule < r) (r, u, ro, hit, marker) = (h.Rule, h.Purpose, h.Role, h, false);
                }
            }
            else if ((f & NodeFlags.Metafile) != 0) (r, u, ro) = (PurposeRules.RMeta, Purposes.System, Roles.System);
            else if ((f & NodeFlags.Virtual) != 0) (r, u, ro) = (PurposeRules.RMeta, Purposes.Unexplained, Roles.Unknown);
            else if (r == None)
            {
                // 自己没有位置规则时才看扩展名：卷根的 DumpStack.log 是系统文件，不因为扩展名是 .log 就改成日志
                int e = tree.ExtId(i);
                byte x = extRule[e];
                if (x == PurposeRules.RAi && tree.Size(i) < AiMinSize) x = None;
                if (x != None) (r, u, ro, byExt) = (x, extU[e], extRole[e], true);
            }

            byte inherited = rule[p];
            own[i] = r != None && r <= inherited;
            if (r < inherited)
            {
                rule[i] = r;
                purpose[i] = u;
            }
            else
            {
                rule[i] = inherited;
                purpose[i] = purpose[p];
            }

            bool win = r != None && (r < inherited || (r == inherited && r != PurposeRules.RDev));
            byte myRole = win ? ro : (byte)(role[p] & Classification.RoleMask);
            if (dir)
            {
                if (!win) myRole = PurposeRules.Refine(myRole, tree.NameSpan(i));
            }
            else if (!(byExt && win))
            {
                int e = tree.ExtId(i);
                myRole = PurposeRules.FileRole(myRole, exec[e], config[e]);
            }

            var evidence = purpose[i] is Purposes.Unknown or Purposes.Unexplained ? Evidence.Unknown
                : byExt && r < inherited ? Evidence.Inferred
                : rule[i] == PurposeRules.RUser && !(win && hit.Rule == PurposeRules.RUser) ? Evidence.Inferred
                : Evidence.Confirmed;
            role[i] = (byte)(myRole | ((int)evidence << 6));

            // 所属对象：先看赢了的规则自己说没说，再看项目、游戏标记，最后看父目录说「子目录各是一个对象」
            var k = Kids.None;
            bool rooted = false;
            if (win && hit.Rule != 0)
            {
                k = hit.Kids;
                if (hit.Owner != null)
                {
                    owners.Add(i, hit.Kind, hit.Owner, hit.Source);
                    rooted = true;
                }
            }
            if (dir && !rooted)
            {
                // 依赖、构建产物里的 package.json 不另立项目（myRole 已经被 node_modules 之类改掉了）
                if (marker && rule[i] == PurposeRules.RDev && myRole == Roles.Source)
                    owners.Add(i, OwnerKind.Project, tree.GetName(i), OwnerSource.Project);
                else if (marker && win && r == PurposeRules.RGames)
                    owners.Add(i, OwnerKind.Game, tree.GetName(i), OwnerSource.GameInstall);
                else if (!win)
                {
                    var up = kids[p];
                    if (PurposeRules.KidOwner(up, tree.NameSpan(i)) is { } kid && (up != Kids.Vendor || HasExe(tree, i, exe)))
                    {
                        owners.Add(i, kid.Kind, kid.Name, kid.Source);
                        if (up == Kids.Apps && IsVendor(tree, i, exe)) k = Kids.Vendor;
                    }
                    else if (up == Kids.SteamApps && tree.NameSpan(i).Equals("common", StringComparison.OrdinalIgnoreCase)) k = Kids.Games;
                }
            }
            kids[i] = k;
        }
        return dirs;
    }

    /// 自底向上：每个目录一行 Purposes.Count 个用途的累计大小，只给目录分配。
    /// 没命中规则的目录取占 ≥ 60% 的用途，否则未识别；用途只是继承来的目录，子树里另一种用途占 ≥ 60% 时展示它
    /// （AppData 里九成是 DXCache 的 NVIDIA 目录显示成缓存），没人过线就沿用继承的。只改目录自己，子项分类和汇总口径不动（dise §7.3）。
    /// 权重是计入的 alloc（和 PurposeTotals 同口径：目录自身、硬链接副本、已删除节点都不算）。
    /// 按子树构成定下来的展示用途是推测。
    static void Dominate(FileTree tree, byte[] rule, byte[] purpose, byte[] role, int[] slot, bool[] own, int dirs, CancellationToken ct)
    {
        var hist = new long[dirs * W];
        for (int i = tree.Count - 1; i > 0; i--)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            var f = tree.Flags(i);
            int up = slot[tree.Parent(i)] * W;
            if ((f & NodeFlags.Directory) != 0)
            {
                int row = slot[i] * W;
                if (rule[i] == None) Dominated(i, Pick(hist, row));
                else if (!own[i] && Pick(hist, row) is var main and not Purposes.Unknown && main != purpose[i]) Dominated(i, main);
                for (int k = 0; k < W; k++) hist[up + k] += hist[row + k];
            }
            else if ((f & (NodeFlags.Deleted | NodeFlags.HardLinkDup)) == 0)
            {
                hist[up + purpose[i]] += tree.Alloc(i);
            }
        }
        if (rule[0] == None) Dominated(0, Pick(hist, 0));

        void Dominated(int i, byte main)
        {
            purpose[i] = main;
            SetEvidence(role, i, main == Purposes.Unknown ? Evidence.Unknown : Evidence.Inferred);
        }
    }

    /// 占比整数比较：best / total ≥ 6/10，不碰浮点。≥ 60% 的用途至多一个，所以不用管平局。
    static byte Pick(long[] hist, int row)
    {
        long total = 0, best = 0;
        int which = 0;
        for (int k = 0; k < W; k++)
        {
            long v = hist[row + k];
            total += v;
            if (v > best)
            {
                best = v;
                which = k;
            }
        }
        return total > 0 && best * 10 >= total * 6 ? (byte)which : Purposes.Unknown;
    }
}
