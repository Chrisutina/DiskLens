using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiskLens.Core;
using Microsoft.Web.WebView2.Core;

namespace DiskLens;

sealed class BridgeHost : IDisposable
{
    readonly Form _owner;
    readonly CoreWebView2 _web;
    readonly Uri _origin;
    readonly IntPtr _ownerHwnd;
    readonly ScanService _scan;
    readonly SummaryStore _summaries;
    // 只有会改状态的请求排这一把许可。只读查询读树时各自进 tree.Sync，删除和清理只在改树时分片短暂持锁，
    // 所以长时间的永久删除期间，视图、详情、搜索照样能及时返回。新加的操作默认排队，确认只读了再放进来。
    readonly SemaphoreSlim _requests = new(1, 1);
    // node.checkDelete 只读树、往自带锁的令牌表里记一笔，不改树，所以也不排队；scan.issues 只在服务锁里拿一下快照，扫描结束后清单不再变。
    // tree.recent / tree.owners / tree.related 和 tree.largest 一样，只在 tree.Sync 里读树；scan.savedSummaries 只读摘要文件（SummaryStore 自带锁），不碰树。
    // app.setLanguage 只改一个全局语言、写自带锁的设置文件，不碰树，也不排队：删除跑着的时候换语言不用干等
    static readonly HashSet<string> ReadOnly = ["tree.view", "tree.details", "tree.largest", "tree.children", "tree.recent", "tree.owners", "tree.related",
        "search.query", "node.checkDelete", "scan.issues", "scan.savedSummaries", "scan.cancel", "operation.status", "operation.cancel", "web.search",
        "app.setLanguage"];
    readonly ConfirmationStore _confirmations = new();
    readonly OperationStore _operations = new();
    readonly CancellationTokenSource _shutdown = new();
    readonly string? _autoScanPath;
    readonly ScanMode? _autoScanMode;
    SearchIndex? _index;
    FileTree? _suggestionTree;
    int _suggestionVersion;
    L.Lang _suggestionLanguage;
    List<SuggestionDto> _suggestions = [];
    readonly SettingsStore _settings;

    public BridgeHost(Form owner, CoreWebView2 web, Uri origin, string? scanPath, ScanMode? mode, SummaryStore summaries, SettingsStore settings)
    {
        _summaries = summaries;
        _settings = settings;
        _scan = new ScanService(summaries);
        _owner = owner;
        _web = web;
        _origin = origin;
        _ownerHwnd = owner.Handle;
        _autoScanPath = scanPath;
        _autoScanMode = mode;
        _scan.Progress += value => Send(new { @event = "scan.progress", data = value });
        _scan.Completed += value => Send(new { @event = "scan.done", data = value });
        _scan.Failed += value => Send(new { @event = "scan.error", data = value });
        _scan.TreeChanged += value => Send(new { @event = "tree.changed", data = value });
        _operations.Changed += value => Send(new { @event = "operation.progress", data = value });
        _web.WebMessageReceived += OnMessage;
    }

    internal static bool IsSameOrigin(string source, Uri origin) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == origin.Scheme &&
        uri.Host == origin.Host && uri.Port == origin.Port && uri.UserInfo.Length == 0;

    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsSameOrigin(e.Source, _origin) || _shutdown.IsCancellationRequested) return;
        int id = -1;
        try
        {
            if (e.WebMessageAsJson.Length > 128 * 1024) throw new ArgumentException(L.T("请求内容过长", "The request is too large", "要求の内容が長すぎます"));
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var number) || !number.TryGetInt32(out id) || id < 0) return;
            // 用 TryGetProperty：GetProperty 缺字段会抛 KeyNotFoundException，前端就只能看到一句英文。
            var method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (string.IsNullOrEmpty(method)) throw new ArgumentException(L.T("缺少操作名称", "Missing operation name", "操作名がありません"));
            var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            if (method is "cleanup.run" or "node.delete")
            {
                string? operationId = method == "cleanup.run" ? Read<CleanupRunRequest>(parameters).OperationId : Read<DeleteRequest>(parameters).OperationId;
                string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(method + ":" + parameters.GetRawText())));
                var (operation, created) = _operations.Register(operationId, method == "cleanup.run" ? "cleanup" : "delete", fingerprint);
                if (created) await ExecuteOperation(method, parameters, operation);
                var result = await operation.ResultAsync();
                Send(new { id, ok = true, result });
                return;
            }
            bool exclusive = !ReadOnly.Contains(method);
            if (exclusive) await _requests.WaitAsync(_shutdown.Token);
            try
            {
                var result = await Task.Run(() => Dispatch(method, parameters), _shutdown.Token);
                Send(new { id, ok = true, result });
            }
            finally { if (exclusive) _requests.Release(); }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Trace.TraceWarning("桥接请求失败：{0}", ex);
            if (id >= 0) Send(new { id, ok = false, error = new { message = ErrorMessage(ex) } });
        }
    }

    async Task ExecuteOperation(string method, JsonElement parameters, OperationStore.Entry operation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, operation.Token);
        bool entered = false;
        try
        {
            await _requests.WaitAsync(cancellation.Token);
            entered = true;
            operation.Start();
            cancellation.Token.ThrowIfCancellationRequested();
            var result = (OpResultDto)(await Task.Run(() => Dispatch(method, parameters, operation, cancellation.Token), cancellation.Token))!;
            operation.Complete(result);
        }
        catch (Exception ex) { operation.Fail(ex, ErrorMessage(ex)); }
        finally { if (entered) _requests.Release(); }
    }

    // 前端只会把这句话原样显示给用户，.NET 的英文异常原文别直接甩出去。
    internal static string ErrorMessage(Exception ex) => ex switch
    {
        JsonException => L.T("请求参数无效", "Invalid request parameters", "要求のパラメーターが無効です"),
        OperationCanceledException => L.T("操作已取消", "Operation cancelled", "操作はキャンセルされました"),
        NotImplementedException => L.T("该功能还没装好", "This feature isn't available yet", "この機能はまだ利用できません"),
        // 这几类是程序自己的毛病，不是用户操作不当：保留原文方便排查，加个中文开头。
        NullReferenceException or IndexOutOfRangeException or InvalidCastException or KeyNotFoundException
            or ArgumentNullException => L.T("内部错误：", "Internal error: ", "内部エラー：") + ex.Message,
        _ => ex.Message,
    };

    async Task<object?> Dispatch(string method, JsonElement parameters, OperationStore.Entry? operation = null, CancellationToken? cancellation = null)
    {
        var ct = cancellation ?? _shutdown.Token;
        switch (method)
        {
            case "operation.status": return _operations.Status(Read<OperationRequest>(parameters).OperationId);
            case "operation.cancel": return _operations.Cancel(Read<OperationRequest>(parameters).OperationId);
            case "app.info":
                return new AppInfoDto(Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0",
                    Volumes.IsElevated(), Environment.OSVersion.VersionString, Environment.ProcessorCount,
                    _autoScanPath, _autoScanMode, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), L.Code);
            case "app.setLanguage":
            {
                string? code = Read<LanguageRequest>(parameters).Language;
                L.Current = L.Parse(code) ?? throw new ArgumentException(L.T($"不支持的界面语言：{code}", $"Unsupported language: {code}", $"対応していない言語です：{code}"));
                // 存不下也照样换：这次运行用新语言，下次启动退回系统语言，照实说一声
                return new OkDto(true, _settings.SaveLanguage(L.Code) ? null : L.T("语言已切换，但没能保存，下次启动会按系统语言显示",
                    "Language changed, but it couldn't be saved; next time DiskLens will follow the system language",
                    "言語を切り替えましたが保存できませんでした。次回はシステムの言語で表示されます"));
            }
            case "drives.list": return Volumes.List();
            case "dialog.pickFolder":
                return await OnUi(() =>
                {
                    using var dialog = new FolderBrowserDialog { Description = L.T("选择要扫描的文件夹", "Choose a folder to scan", "スキャンするフォルダーを選択"), UseDescriptionForTitle = true };
                    return new PathDto(dialog.ShowDialog(_owner) == DialogResult.OK ? dialog.SelectedPath : null);
                });
            case "app.relaunchAdmin":
            {
                var request = Read<RelaunchRequest>(parameters);
                var args = request.ScanPath is null ? "" : "--scan " + QuoteArgument(Path.GetFullPath(request.ScanPath));
                if (request.Mode is { } mode)
                {
                    if (!Enum.IsDefined(mode)) throw new ArgumentException(L.T("未知扫描模式", "Unknown scan mode", "不明なスキャン モードです"));
                    args += " --mode " + mode.ToString().ToLowerInvariant();
                }
                bool ok = ShellOps.RelaunchElevated(args);
                if (ok) _ = CloseAfterReply();
                return new OkDto(ok, ok ? L.T("正在以管理员身份重启", "Restarting as administrator", "管理者として再起動しています") : L.T("已取消，仍以标准权限运行", "Cancelled; still running as a standard user", "キャンセルしました。標準権限のまま実行します"));
            }
            case "scan.start":
            {
                var started = _scan.Start(Read<ScanStartRequest>(parameters));
                // 等 Start 真的接受了再丢缓存：路径敲错被拒绝时，已经建好的索引和建议还得留着用。
                // 扫描期间 NeedTree 一直拒绝访问；此前已经在跑的旧树搜索可能把旧索引写回来，下次搜索按树身份会换掉它。
                _index = null;
                _suggestionTree = null;
                _suggestions = [];
                _confirmations.Clear(); // 旧树上确认过的清单不能拿到新树上执行
                return started;
            }
            // 「取消中」和终态都走 scan.progress / scan.error 事件；这里只说有没有扫描可取消（刚好扫完也不算失败）
            case "scan.cancel": return new OkDto(true, _scan.Cancel() ? null : L.T("没有正在进行的扫描", "No scan in progress", "実行中のスキャンはありません"));
            case "scan.summary": return _scan.RefreshSummary();
            case "scan.issues": return _scan.Issues(Read<ScanIssuesRequest>(parameters));
            // 上次扫描结果（dise §3.2）：只是摘要，不带节点 ID；拿到新的扫描结果之前不能拿它导航或删除
            case "scan.savedSummaries": return _summaries.List();
            // 请求参数先在锁外解析：参数有问题就别白占树锁，更别先花几百毫秒建一遍索引。
            case "tree.view":
            {
                var request = Read<ViewRequest>(parameters);
                var tree = NeedTree();
                lock (tree.Sync) return TreeQueries.BuildView(tree, request);
            }
            case "tree.details": return TreeQueries.GetDetails(NeedTree(), Read<DetailsRequest>(parameters));
            case "tree.largest":
            {
                var request = Read<LargestRequest>(parameters);
                var tree = NeedTree();
                lock (tree.Sync) return TreeQueries.LargestFiles(tree, request);
            }
            case "tree.children":
            {
                var request = Read<ChildrenRequest>(parameters);
                var tree = NeedTree();
                lock (tree.Sync) return TreeQueries.Children(tree, request);
            }
            case "tree.recent":
            {
                var request = Read<RecentRequest>(parameters);
                var tree = NeedTree();
                // 窗口按这棵树的扫描完成时刻开，不按「现在」：程序开着过几天，扫描时算最近的文件也不会被挤出窗口
                long asOfMs = _scan.CompletedAtMs(tree);
                lock (tree.Sync) return TreeQueries.RecentFiles(tree, request, asOfMs);
            }
            case "tree.owners":
            {
                var request = Read<OwnerUsageRequest>(parameters);
                var tree = NeedTree();
                lock (tree.Sync) return TreeQueries.OwnerUsage(tree, request);
            }
            case "tree.related":
            {
                var request = Read<RelatedRequest>(parameters);
                var tree = NeedTree();
                lock (tree.Sync) return TreeQueries.Related(tree, request);
            }
            case "search.query":
            {
                var request = Read<SearchRequest>(parameters);
                var tree = NeedTree();
                lock (tree.Sync)
                {
                    // ponytail: 首次搜索才建索引（200 万节点约 0.5 秒）；嫌慢就在 scan.done 之后、前端首批请求结束后的空闲时预建。
                    // 用局部变量：搜索不排队了，换树后另一棵树上的搜索随时可能改写 _index。
                    var index = _index;
                    if (index?.Tree != tree) _index = index = SearchIndex.Build(tree, ct);
                    return index.Query(request, ct);
                }
            }
            case "cleanup.list": return Suggestions(NeedTree());
            // 确认框打开时核对一遍、拍下要删的对象并发令牌；执行时只认这张令牌对应的清单（DESIGN §9.2）
            case "cleanup.check":
            {
                var tree = NeedTree();
                var request = Read<CleanupRunRequest>(parameters);
                var suggestion = Suggestions(tree).SingleOrDefault(s => s.Id == request.SuggestionId)
                    ?? throw new ArgumentException(L.T("该建议已经失效，请刷新清理列表", "This suggestion is no longer valid; refresh the cleanup list", "この提案は無効になりました。クリーンアップの一覧を更新してください"));
                var (current, ids) = CleanupAdvisor.Prepare(tree, suggestion, request, CleanupContext(tree), ct);
                var snapshot = current.Action switch
                {
                    "deleteNodes" => DeletionSnapshot.Capture(tree, ids, contents: false, ct),
                    "deleteContents" => DeletionSnapshot.Capture(tree, ids, contents: true, ct),
                    _ => null,
                };
                string token = _confirmations.Issue(new(tree, suggestion.Id, request.NodeIds, request.Permanent, snapshot));
                return new CleanupCheckDto(token, snapshot?.Check);
            }
            case "cleanup.run":
            {
                var tree = NeedTree();
                var request = Read<CleanupRunRequest>(parameters);
                var suggestion = Suggestions(tree).SingleOrDefault(s => s.Id == request.SuggestionId)
                    ?? throw new ArgumentException(L.T("该建议已经失效，请刷新清理列表", "This suggestion is no longer valid; refresh the cleanup list", "この提案は無効になりました。クリーンアップの一覧を更新してください"));
                // 打开设置、只看说明不改任何东西，不用确认；其余（删除、清空回收站、运行命令）必须带着确认框的令牌来
                DeletionSnapshot? confirmed = null;
                if (suggestion.Action is not ("openSettings" or "info"))
                {
                    var entry = _confirmations.Take(request.ConfirmationToken);
                    if (!entry.Matches(tree, suggestion.Id, request.NodeIds, request.Permanent))
                        throw new ArgumentException(L.T("确认框里核对过的对象和这次请求对不上，请重新确认", "The targets checked in the confirmation don't match this request; confirm again", "確認画面で確認した対象とこの要求が一致しません。もう一度確認してください"));
                    confirmed = entry.Snapshot;
                }
                if (operation != null) operation.External = suggestion.Action is "command" or "openSettings";
                return CleanupAdvisor.Run(tree, suggestion, request, CleanupContext(tree), ct, confirmed, operation is null ? null : operation.Report);
            }
            case "cleanup.script":
            {
                var tree = NeedTree();
                var request = Read<CleanupScriptRequest>(parameters);
                if (request.Selections is not { Length: > 0 }) throw new ArgumentException(L.T("请先勾选要清理的项目", "Select the items to clean up first", "先にクリーンアップする項目を選んでください"));
                // 脚本只按当前缓存的建议列表生成，前端传来的 ID 对不上就由 BuildScript 拒绝。
                // 扫描开始时刻 = 完成时刻 − 用时，再往前让两秒：文件时间按时钟滴答取整，宁可多留几个也不能把扫描之后的算进来
                var summary = _scan.SummaryOf(tree);
                var context = CleanupContext(tree) with
                {
                    ScanStartedAtMs = summary.CompletedAtMs - summary.ElapsedMs - 2000,
                    FileSystem = summary.FileSystem,
                };
                return CleanupAdvisor.BuildScript(tree, Suggestions(tree), request, context);
            }
            case "node.checkDelete":
            {
                var tree = NeedTree();
                var snapshot = DeletionSnapshot.Capture(tree, Read<NodeIdsRequest>(parameters).NodeIds ?? [], contents: false, ct);
                string token = _confirmations.Issue(new(tree, null, [.. snapshot.Roots], null, snapshot));
                return snapshot.Check with { ConfirmationToken = token };
            }
            case "node.delete":
            {
                var request = Read<DeleteRequest>(parameters);
                if (request.NodeIds is null || request.NodeIds.Length > 1000) throw new ArgumentException(L.T("一次最多处理 1000 个条目", "Up to 1,000 items can be handled at once", "一度に処理できるのは 1,000 項目までです"));
                var tree = NeedTree();
                var entry = _confirmations.Take(request.ConfirmationToken);
                if (entry.Snapshot is not { } snapshot || !entry.Matches(tree, null, request.NodeIds, null))
                    throw new ArgumentException(L.T("确认框里核对过的对象和这次请求对不上，请重新确认", "The targets checked in the confirmation don't match this request; confirm again", "確認画面で確認した対象とこの要求が一致しません。もう一度確認してください"));
                return snapshot.Delete(request.Permanent, ct, operation is null ? null : operation.Report);
            }
            case "node.openInExplorer":
            {
                var node = NodePath(Read<NodeRequest>(parameters).NodeId);
                ShellOps.OpenInExplorer(node.Path, node.IsDir);
                return new OkDto(true);
            }
            // 启动页的扫描记录没有节点 ID，只能按路径开；只认记录里出现过的位置，页面不能随便指一个路径
            case "shell.openFolder":
            {
                string path = Read<OpenFolderRequest>(parameters).Path ?? "";
                if (!Path.IsPathFullyQualified(path) || !_summaries.Knows(path)) throw new ArgumentException(L.T("这个位置不在扫描记录里", "This location is not in the scan history", "この場所はスキャン履歴にありません"));
                if (!Directory.Exists(path)) return new OkDto(false, L.T("这个位置已经不在了（可能已经卸载或移走），重新扫描后再看", "This location no longer exists (maybe uninstalled or moved); rescan to check again", "この場所はもうありません（アンインストールまたは移動された可能性があります）。再スキャンしてから確認してください"));
                ShellOps.OpenInExplorer(path, isDirectory: true);
                return new OkDto(true);
            }
            case "node.searchWeb":
            {
                var tree = NeedTree();
                var request = Read<SearchWebRequest>(parameters);
                string url;
                lock (tree.Sync) url = ShellOps.BuildWebSearchUrl(tree, request.NodeId, request.Engine);
                ShellOps.OpenUrl(url);
                return new OkDto(true);
            }
            case "web.search":
            {
                var request = Read<WebQueryRequest>(parameters);
                ShellOps.OpenUrl(ShellOps.BuildQueryUrl(request.Engine, request.Query));
                return new OkDto(true);
            }
            case "node.properties":
            {
                var node = NodePath(Read<NodeRequest>(parameters).NodeId);
                await OnUi(() => { ShellOps.ShowProperties(_ownerHwnd, node.Path); return true; });
                return new OkDto(true);
            }
            default: throw new ArgumentException(L.T("不支持的操作：", "Unsupported operation: ", "サポートされていない操作です：") + method);
        }
    }

    FileTree NeedTree()
    {
        if (_scan.IsScanning) throw new InvalidOperationException(L.T("扫描尚未完成，请稍候", "The scan isn't finished yet; please wait", "スキャンがまだ終わっていません。しばらくお待ちください"));
        return _scan.Tree ?? throw new InvalidOperationException(L.T("请先扫描磁盘或文件夹", "Scan a drive or folder first", "先にドライブまたはフォルダーをスキャンしてください"));
    }

    (string Path, bool IsDir) NodePath(int id)
    {
        var tree = NeedTree();
        lock (tree.Sync)
        {
            if (!tree.IsValid(id)) throw new ArgumentException(L.T("节点不存在或已删除", "The item doesn't exist or was deleted", "項目が存在しないか、削除されています"));
            if (tree.Has(id, NodeFlags.Virtual | NodeFlags.Metafile)) throw new ArgumentException(L.T("该项没有可操作的文件路径", "This item has no file path to act on", "この項目には操作できるファイル パスがありません"));
            return (tree.GetPath(id), tree.IsDir(id));
        }
    }

    CleanupContext CleanupContext(FileTree tree) => new(Volumes.IsElevated(),
        Volumes.GetVolumeRoot(tree.RootPath) ?? tree.RootPath, tree.RootPath, _ownerHwnd);

    List<SuggestionDto> Suggestions(FileTree tree)
    {
        // 建议的标题和说明是按当时的界面语言写的：换了语言也要重算
        if (_suggestionTree == tree && _suggestionVersion == tree.Version && _suggestionLanguage == L.Current) return _suggestions;
        _suggestions = CleanupAdvisor.Analyze(tree, CleanupContext(tree), _shutdown.Token);
        _suggestionTree = tree;
        _suggestionVersion = tree.Version;
        _suggestionLanguage = L.Current;
        return _suggestions;
    }

    static T Read<T>(JsonElement parameters) =>
        JsonSerializer.Deserialize<T>(parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : parameters.GetRawText(), Json.Options)
        ?? throw new ArgumentException(L.T("缺少请求参数", "Missing request parameters", "要求のパラメーターがありません"));

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    Task<T> OnUi<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_shutdown.IsCancellationRequested) return Task.FromCanceled<T>(_shutdown.Token);
        try
        {
            _owner.BeginInvoke(() =>
            {
                try { completion.SetResult(action()); }
                catch (Exception ex) { completion.SetException(ex); }
            });
        }
        catch (InvalidOperationException ex) { completion.SetException(ex); }
        return completion.Task.WaitAsync(_shutdown.Token);
    }

    void Send(object message)
    {
        if (_shutdown.IsCancellationRequested || _owner.IsDisposed) return;
        string json = JsonSerializer.Serialize(message, Json.Options);
        try
        {
            _owner.BeginInvoke(() =>
            {
                if (!_shutdown.IsCancellationRequested && !_owner.IsDisposed)
                {
                    // 渲染进程挂了或正在关闭时 Post 会抛各种异常；这里逃出去就是 WinForms 的未处理异常弹窗，
                    // 而进度事件一秒八次，能弹到天荒地老。
                    try { _web.PostWebMessageAsJson(json); }
                    catch (Exception ex) { Trace.TraceWarning("向界面发送消息失败：{0}", ex.Message); }
                }
            });
        }
        catch (InvalidOperationException) { }
    }

    async Task CloseAfterReply()
    {
        await Task.Delay(350);
        if (!_shutdown.IsCancellationRequested) await OnUi(() => { _owner.Close(); return true; });
    }

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested) return;
        _web.WebMessageReceived -= OnMessage;
        _shutdown.Cancel();
        _scan.Dispose();
    }
}
