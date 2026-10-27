using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DiskLens;
using DiskLens.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DiskLens.UiCheck;

static class Program
{
    static int _assertions;
    static int _requestId = 1_000_000;

    [STAThread]
    static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        // 不用 ApplicationConfiguration.Initialize()：宿主程序集里有一份同名内部类，会撞出 CS0436 警告。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "DiskLens.slnx")))
            root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("找不到仓库根目录");
        string sandbox = Path.Combine(root, ".sandbox", "ui");
        // 沙箱根和 ui 目录必须是真目录：是联接或符号链接的话，下面造夹具、清旧夹具都会落到仓库外面（同 DiskLens.Check 的 SandboxDir）
        foreach (string p in new[] { Path.GetDirectoryName(sandbox)!, sandbox })
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(p); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("沙箱路径是联接或符号链接，拒绝在里面造文件或清理：" + p);
        }
        Directory.CreateDirectory(sandbox);
        // 以前的版本跑完不清夹具，先把遗留的收拾掉。
        foreach (string stale in Directory.GetDirectories(sandbox).Where(d =>
                     Path.GetFileName(d).StartsWith("fixture-") || Path.GetFileName(d).StartsWith("service-")))
            DeleteInSandbox(sandbox, stale);

        // 名字取短：夹具路径会原样显示在顶栏里，太长会把前端顶栏挤变形，截图就没法看了。
        string id = Guid.NewGuid().ToString("N")[..6];
        string folder = Path.Combine(sandbox, "fixture-" + id), serviceFolder = Path.Combine(sandbox, "service-" + id);
        Environment.SetEnvironmentVariable("DISKLENS_DEV_URL", null);
        int result = 1;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "项目代码"));
            File.WriteAllBytes(Path.Combine(folder, "影片, 测试.mp4"), new byte[512 * 1024]);
            File.WriteAllBytes(Path.Combine(folder, "备份.zip"), new byte[256 * 1024]);
            // 很久没改过的文件：最近文件（tree.recent）的 7 天窗口里不该有它；其余夹具文件都是刚写的
            File.SetLastWriteTimeUtc(Path.Combine(folder, "备份.zip"), DateTime.UtcNow.AddDays(-400));
            File.WriteAllText(Path.Combine(folder, "项目代码", "readme.md"), "DiskLens UI fixture");
            // 项目标记：详情里要能认出「项目代码」这个所属对象
            File.WriteAllText(Path.Combine(folder, "项目代码", "package.json"), "{}");
            for (int i = 0; i < 24; i++)
                File.WriteAllBytes(Path.Combine(folder, "项目代码", $"module-{i}.ts"), new byte[(i + 1) * 1024]);
            File.SetLastWriteTimeUtc(Path.Combine(folder, "项目代码", "module-0.ts"), DateTime.UtcNow.AddDays(-3));

            CheckScanService(serviceFolder);

            // 界面固定成中文开局：断言按中文写，也不能被谁在这个 data 目录里存过的语言带偏；切换语言另有一段专门验
            using var form = new MainForm(folder, ScanMode.Walk, "zh-CN");
            form.Shown += async (_, _) =>
            {
                try
                {
                    // 看门狗：WebView2 被遮挡或界面卡住时，脚本和截图的 await 可能永远不返回，检查不能陪着一起挂死。
                    await CheckWindow(form, folder, Path.Combine(sandbox, "desktop.png")).WaitAsync(TimeSpan.FromMinutes(4));
                    Console.WriteLine($"[ui] 通过，共 {_assertions} 条断言（ScanService 直测 + 真实 WebView2 + 真实扫描 + JS/C# 桥接）");
                    result = 0;
                }
                catch (Exception ex) { Console.Error.WriteLine("[ui] 失败：" + ex); }
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        catch (Exception ex) { Console.Error.WriteLine("[ui] 失败：" + ex); }
        finally
        {
            // 窗口已经关了才删：夹具只在沙箱里，只删自己造的这两个。
            foreach (string dir in new[] { folder, serviceFolder })
                try { DeleteInSandbox(sandbox, dir); }
                catch (IOException ex) { Console.Error.WriteLine("[ui] 夹具没清干净：" + ex.Message); }
        }
        return result;
    }

    /// 不开窗口，直接验 ScanService：入口校验、用途汇总、删除后的汇总刷新。
    static void CheckScanService(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.mp4"), new byte[300 * 1024]);
        File.WriteAllBytes(Path.Combine(dir, "b.bin"), new byte[100 * 1024]);
        File.WriteAllBytes(Path.Combine(dir, "c.txt"), new byte[10 * 1024]);

        using var service = new ScanService();
        ScanSummaryDto? done = null;
        ScanErrorDto? failed = null;
        using var finished = new ManualResetEventSlim();
        service.Completed += s => { done = s; finished.Set(); };
        service.Failed += e => { failed = e; finished.Set(); };

        Assert(Throws<ArgumentException>(() => service.Start(new ScanStartRequest("  "))).Message.Contains("扫描路径无效"), "空路径给出中文提示");
        Throws<DirectoryNotFoundException>(() => service.Start(new ScanStartRequest(Path.Combine(dir, "不存在"))));
        Throws<InvalidOperationException>(() => service.Start(new ScanStartRequest(dir, ScanMode.Mft)));
        Throws<ArgumentException>(() => service.Start(new ScanStartRequest(dir, (ScanMode)99)));
        Assert(!service.IsScanning && service.Tree == null, "被拒绝的请求不留下扫描任务");

        var started = service.Start(new ScanStartRequest(dir, ScanMode.Walk));
        Assert(started.Mode == ScanMode.Walk, "文件夹扫描走常规模式");
        Assert(finished.Wait(TimeSpan.FromSeconds(30)), "扫描在 30 秒内结束");
        Assert(failed == null && done != null, "扫描成功完成：" + failed?.Message);
        var summary = done!;
        var tree = service.Tree ?? throw new InvalidOperationException("扫描完成后应有树");
        Assert(!service.IsScanning, "完成后不再处于扫描中");
        Assert(summary.Files == 3 && !summary.IsVolumeRoot && summary.UnaccountedBytes == 0, "文件夹扫描：3 个文件、没有未统计");
        Assert(summary.PurposeBytes is { Length: Purposes.Count }, "汇总带每个用途的字节数");
        long[] purposesBefore = summary.PurposeBytes!;
        Assert(purposesBefore.Sum() == summary.ScannedAlloc, "各用途字节数加起来等于已统计占用");
        Assert(summary.TreeVersion == tree.Version, "汇总里的树版本取自写入用途之后");

        // 永久删掉沙箱里的一个文件，RefreshSummary 的用途汇总要跟着少。
        int victim;
        lock (tree.Sync) victim = tree.FindByPath(Path.Combine(dir, "b.bin"));
        Assert(victim >= 0, "能按路径找到夹具文件");
        var op = DeleteService.Delete(tree, [victim], permanent: true, CancellationToken.None);
        Assert(op.Ok && op.Deleted == 1 && !File.Exists(Path.Combine(dir, "b.bin")), "沙箱里永久删除一个文件");
        var refreshed = service.RefreshSummary() ?? throw new InvalidOperationException("刷新应返回汇总");
        Assert(refreshed.Files == summary.Files - 1, "删除后文件数少一个");
        long[] purposesAfter = refreshed.PurposeBytes ?? throw new InvalidOperationException("刷新后的汇总缺少用途字节数");
        Assert(purposesAfter.Sum() == purposesBefore.Sum() - op.FreedBytes, "删除后用途字节数同步减少");
        Assert(purposesAfter.Sum() == refreshed.ScannedAlloc, "刷新后用途汇总仍等于已统计占用");
    }

    static async Task CheckWindow(MainForm form, string folder, string screenshot)
    {
        Assert(BridgeHost.IsSameOrigin("https://disklens.local/index.html", new Uri("https://disklens.local")), "允许本地界面来源");
        Assert(!BridgeHost.IsSameOrigin("https://disklens.local.evil.test", new Uri("https://disklens.local")), "拒绝相似域名");
        Assert(!BridgeHost.IsSameOrigin("http://disklens.local", new Uri("https://disklens.local")), "拒绝不同协议");
        Assert(!BridgeHost.IsSameOrigin("https://disklens.local:9000", new Uri("https://disklens.local")), "拒绝不同端口");
        Assert(!BridgeHost.IsSameOrigin("https://user@disklens.local/", new Uri("https://disklens.local")), "拒绝带用户信息的来源");
        Assert(BridgeHost.QuoteArgument("D:\\") == "\"D:\\\\\"", "卷根参数尾部反斜线正确转义");
        Assert(BridgeHost.QuoteArgument("D:\\中文 目录") == "\"D:\\中文 目录\"", "带空格和中文参数保留");
        Assert(typeof(MainForm).Assembly.GetManifestResourceNames().Contains("app.ico"), "窗口图标已嵌入程序集");
        // 没构建前端就跑这个检查，只会在「界面初始化」上干等 30 秒；直接说清楚缺什么。
        Assert(File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html")),
            "找不到前端产物 wwwroot\\index.html：先运行 pnpm --dir web build，再重新编译 UiCheck");

        var web = form.Controls.OfType<WebView2>().Single();
        await Wait(async () => web.CoreWebView2 is not null &&
            await web.ExecuteScriptAsync("document.readyState === 'complete' && !!document.querySelector('#app')") == "true", "界面初始化");
        string data = Path.Combine(AppContext.BaseDirectory, "data", Volumes.IsElevated() ? "webview2-admin" : "webview2-user");
        Assert(Directory.Exists(data), "WebView2 数据目录在程序目录的 data 下（DESIGN §2.1）");
        await Wait(async () =>
        {
            using var reply = await Call(web, "scan.summary");
            return reply.RootElement.GetProperty("ok").GetBoolean() &&
                   reply.RootElement.GetProperty("result").ValueKind == JsonValueKind.Object;
        }, "实际文件夹扫描完成");
        await Wait(async () => await web.ExecuteScriptAsync("!!document.querySelector('section[aria-label=\"资源地图\"]')") == "true", "前端收到扫描完成事件");

        var metrics = JsonDocument.Parse(await web.ExecuteScriptAsync(
            "({ w: innerWidth, h: innerHeight, dpr: devicePixelRatio, clipped: [...document.body.querySelectorAll('*')]" +
            ".filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.right > innerWidth + 1; })" +
            ".slice(0, 5).map(e => e.tagName.toLowerCase() + '.' + String(e.className?.baseVal ?? e.className).slice(0, 40) + ' 右边缘 ' + Math.round(e.getBoundingClientRect().right)) })")).RootElement;
        int cssWidth = metrics.GetProperty("w").GetInt32();
        Console.WriteLine($"窗口 {cssWidth}×{metrics.GetProperty("h").GetInt32()} CSS 像素，缩放 {metrics.GetProperty("dpr").GetDouble():0.##}");
        // 仅提示不断言：元素被挤出视口是前端排版的事（夹具路径很深时顶栏会被撑宽），不该让宿主检查替它背锅。
        foreach (var clipped in metrics.GetProperty("clipped").EnumerateArray())
            Console.WriteLine($"提示：超出窗口右边缘的元素：{clipped.GetString()}（窗口宽 {cssWidth}）");
        Assert(form.WindowState == FormWindowState.Maximized || cssWidth >= 1100, "窗口按 DPI 放大后不小于版式最小宽度 1100");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using (var reply = await Call(web, "app.info"))
        {
            var info = Result(reply, "app.info");
            Assert(info.GetProperty("version").GetString() == "0.1.0", "桌面桥接返回真实版本");
            Assert(info.GetProperty("homePath").GetString() == home, "app.info 带用户主目录");
            Assert(info.GetProperty("autoScanPath").GetString() == folder, "app.info 带自动扫描路径");
        }

        long purposeTotal, completedAt, files;
        int scanId;
        using (var reply = await Call(web, "scan.summary"))
        {
            var summary = Result(reply, "scan.summary");
            // ExecuteScriptAsync 把超出 int 的整数写成「1792477228326.0」，毫秒时间戳一律按 double 读
            completedAt = (long)summary.GetProperty("completedAtMs").GetDouble();
            Assert(completedAt > 0 && completedAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "汇总带扫描完成时刻，不晚于现在");
            var purposes = summary.GetProperty("purposeBytes");
            Assert(purposes.ValueKind == JsonValueKind.Array && purposes.GetArrayLength() == Purposes.Count, "summary.purposeBytes 长度等于用途数");
            purposeTotal = purposes.EnumerateArray().Sum(x => x.GetInt64());
            Assert(purposeTotal == summary.GetProperty("scannedAlloc").GetInt64(), "summary.purposeBytes 之和等于已统计占用");
            Assert(summary.GetProperty("unaccountedBytes").GetInt64() == 0, "文件夹扫描没有未统计");
            Assert(summary.GetProperty("inaccessibleDirs").GetInt64() == 0 && summary.GetProperty("linksSkipped").GetInt64() == 0
                && summary.GetProperty("mountPointsSkipped").GetInt64() == 0 && summary.GetProperty("errors").GetInt64() == 0
                && summary.GetProperty("gapParts").GetArrayLength() == 0, "汇总带扫描报告的四类计数和未解释容量的拆分（夹具里都是 0）");
            scanId = summary.GetProperty("scanId").GetInt32();
            files = summary.GetProperty("files").GetInt64();
        }
        // 上次扫描结果（dise §3.2）：夹具扫完（前端已收到完成事件），桥接上读得到这个范围的摘要，带完成时刻，不带扫描号和节点 ID
        using (var reply = await Call(web, "scan.savedSummaries"))
        {
            var saved = Result(reply, "scan.savedSummaries").EnumerateArray().ToArray();
            var mine = saved.Where(s => s.GetProperty("rootPath").GetString() == folder).ToArray();
            Assert(mine is [{ } entry] && (long)entry.GetProperty("completedAtMs").GetDouble() == completedAt
                && entry.GetProperty("files").GetInt64() == files && !entry.TryGetProperty("scanId", out _) && !entry.TryGetProperty("rootId", out _),
                "桥接上 scan.savedSummaries：这个夹具范围有一条摘要，完成时刻和汇总一致，不带扫描号和节点 ID");
            var times = saved.Select(s => s.GetProperty("completedAtMs").GetDouble()).ToArray();
            Assert(times.Zip(times.Skip(1)).All(p => p.First >= p.Second), "已保存的摘要新的在前");
        }
        Assert(File.Exists(Path.Combine(AppContext.BaseDirectory, "data", $"scan-summaries-{(Volumes.IsElevated() ? "admin" : "user")}.json")),
            "摘要文件和 WebView2 目录在同一个 data 下，按权限分开（DESIGN §2.1）");
        // 异常清单（dise §4.3）：种类按 camelCase 字符串传，清单属于哪次扫描写在应答里，旧编号要不到别的扫描的清单
        using (var reply = await Call(web, "scan.issues", new { kind = "mountPoint", offset = 0, limit = 50, scanId }))
        {
            var issues = Result(reply, "scan.issues");
            Assert(issues.GetProperty("scanId").GetInt32() == scanId && issues.GetProperty("kind").GetString() == "mountPoint"
                && issues.GetProperty("total").GetInt64() == 0 && issues.GetProperty("recorded").GetInt32() == 0
                && issues.GetProperty("items").GetArrayLength() == 0 && !issues.GetProperty("truncated").GetBoolean(),
                "桥接上 scan.issues：属于当前扫描、没有异常时是空清单且不截断");
        }
        using (var reply = await Call(web, "scan.issues", new { kind = "link", scanId = scanId + 1000 }))
            Assert(IsFriendlyError(reply, "失效"), "scan.issues 拿别的扫描编号：给中文错误，不拿当前清单充数");
        using (var reply = await Call(web, "scan.issues", new { kind = "bogus" }))
            Assert(IsFriendlyError(reply, "请求参数无效"), "scan.issues 未知种类：请求参数无效");

        using (var reply = await Call(web, "tree.details", new { nodeId = 0, metric = "alloc" }))
        {
            var details = Result(reply, "tree.details");
            Assert(details.GetProperty("path").GetString() == folder, "详情对应真实沙箱路径");
            Assert(details.GetProperty("files").GetInt64() == 28, "真实扫描文件总数");
            Assert(details.GetProperty("isProtected").GetBoolean(), "禁止删除扫描根目录");
            Assert(details.GetProperty("u").GetInt32() is >= 0 and < Purposes.Count, "详情带用途 u");
            Assert(details.GetProperty("role").GetInt32() is >= 0 and < Roles.Count, "详情带角色");
            Assert(details.GetProperty("evidence").GetString() is "unknown" or "inferred" or "confirmed", "详情带用途证据（camelCase 字符串）");
            Assert(details.GetProperty("owner").ValueKind == JsonValueKind.Null, "扫描根没有可靠的所属对象就给 null");
            var purposes = details.GetProperty("purposeBytes");
            Assert(purposes.GetArrayLength() == Purposes.Count, "详情 purposeBytes 长度等于用途数");
            Assert(purposes.EnumerateArray().Sum(x => x.GetInt64()) == purposeTotal, "根节点详情的用途构成对得上汇总");
        }

        using (var reply = await Call(web, "tree.view", new { nodeId = 0, metric = "alloc", maxNodes = 12000 }))
        {
            var nodes = Result(reply, "tree.view").GetProperty("nodes").EnumerateArray().ToArray();
            Assert(nodes.Length > 3, "地图取得真实节点");
            Assert(nodes.All(n => n.GetProperty("u").GetInt32() is >= 0 and < Purposes.Count), "每个视图节点都带合法的用途 u");
            foreach (var aggregate in nodes.Where(n => n.GetProperty("id").GetInt32() == -1))
                Assert(aggregate.GetProperty("u").GetInt32() == nodes[aggregate.GetProperty("p").GetInt32()].GetProperty("u").GetInt32(),
                    "聚合节点的用途取父节点");
            foreach (var node in nodes.Where(n => n.GetProperty("id").GetInt32() >= 0).Take(5))
            {
                using var detail = await Call(web, "tree.details", new { nodeId = node.GetProperty("id").GetInt32() });
                Assert(Result(detail, "tree.details").GetProperty("u").GetInt32() == node.GetProperty("u").GetInt32(), "视图节点和详情的用途一致");
            }
        }

        using (var reply = await Call(web, "search.query", new { query = "module-", metric = "alloc", limit = 300 }))
        {
            var found = Result(reply, "search.query");
            Assert(found.GetProperty("total").GetInt64() == 24, "桌面搜索返回真实结果");
            using var detail = await Call(web, "tree.details", new { nodeId = found.GetProperty("items")[0].GetProperty("id").GetInt32() });
            var module = Result(detail, "tree.details");
            Assert(module.GetProperty("role").GetInt32() == Roles.Source && module.GetProperty("evidence").GetString() == "confirmed", "项目里的文件：角色源代码、用途证据已确认");
            var owner = module.GetProperty("owner");
            Assert(owner.GetProperty("kind").GetString() == "project" && owner.GetProperty("name").GetString() == "项目代码"
                && owner.GetProperty("evidence").GetString() == "confirmed" && owner.GetProperty("rootPath").GetString() == Path.Combine(folder, "项目代码"),
                "项目里的文件：所属对象是项目标记认出的「项目代码」，种类和证据是 camelCase 字符串");
            Assert(module.GetProperty("purposeBasis").GetString()!.Contains("所属对象「项目代码」"), "依据里写了所属对象");

            using var related = await Call(web, "tree.related", new { nodeId = module.GetProperty("id").GetInt32(), metric = "alloc" });
            var rel = Result(related, "tree.related");
            var first = rel.GetProperty("items")[0];
            Assert(rel.GetProperty("owner").GetProperty("name").GetString() == "项目代码" && first.GetProperty("relation").GetString() == "root"
                && first.GetProperty("path").GetString() == Path.Combine(folder, "项目代码") && rel.GetProperty("note").ValueKind == JsonValueKind.Null,
                "桥接上 tree.related：项目里的文件，第一条关联是所在的项目根（关系是 camelCase 字符串）");
        }
        using (var reply = await Call(web, "tree.related", new { nodeId = 0 }))
        {
            var rel = Result(reply, "tree.related");
            Assert(rel.GetProperty("owner").ValueKind == JsonValueKind.Null && rel.GetProperty("items").GetArrayLength() == 0
                && rel.GetProperty("total").GetInt32() == 0 && rel.GetProperty("note").GetString() == "尚未识别关联对象",
                "扫描根没有所属对象：不编造关联，直说尚未识别");
        }
        using (var reply = await Call(web, "tree.related", new { nodeId = int.MaxValue }))
            Assert(IsFriendlyError(reply, "节点不存在或已删除"), "tree.related 无效节点返回错误应答");

        // 最近文件（dise §6）：窗口按扫描完成时刻开；夹具里只有备份.zip 是 400 天前改的
        using (var reply = await Call(web, "tree.recent", new { days = 7, sort = "modified", metric = "alloc", limit = 100 }))
        {
            var recent = Result(reply, "tree.recent");
            var items = recent.GetProperty("items").EnumerateArray().ToArray();
            // ExecuteScriptAsync 把超出 int 的整数序列化成「1792477228326.0」，GetInt64 不认，按 double 读（2^53 以内是精确的）
            var modified = items.Select(x => (long)x.GetProperty("modifiedMs").GetDouble()).ToArray();
            long asOf = (long)recent.GetProperty("asOfMs").GetDouble();
            Assert(recent.GetProperty("days").GetInt32() == 7 && recent.GetProperty("total").GetInt64() == 27 && items.Length == 27
                && items.All(x => x.GetProperty("name").GetString() != "备份.zip"), "桥接上 tree.recent：7 天窗口里是备份.zip 以外的 27 个文件");
            Assert(modified.Max() <= asOf && asOf == completedAt,
                "时间基准就是汇总里的扫描完成时刻：不早于夹具文件的修改时间");
            Assert(modified.Zip(modified.Skip(1)).All(p => p.First >= p.Second), "按修改时间新的在前");
            var file = items.First(x => x.GetProperty("name").GetString() == "module-0.ts");
            Assert(file.GetProperty("dirPath").GetString() == Path.Combine(folder, "项目代码") && file.GetProperty("role").GetInt32() == Roles.Source
                && file.GetProperty("owner").GetProperty("kind").GetString() == "project", "最近文件带所在目录、角色和所属对象");
        }
        using (var reply = await Call(web, "tree.recent", new { days = 3650 }))
            Assert(Result(reply, "tree.recent").GetProperty("total").GetInt64() == 28, "窗口放宽到 3650 天，旧文件也在");
        using (var reply = await Call(web, "tree.recent", new { sort = "bogus" }))
            Assert(IsFriendlyError(reply, "请求参数无效"), "tree.recent 未知排序：请求参数无效");

        // 按所属对象聚合：夹具里只有「项目代码」一个对象；两种度量各自 owned + shared + unowned = total
        using (var reply = await Call(web, "tree.owners", new { metric = "alloc" }))
        {
            var usage = Result(reply, "tree.owners");
            Func<string, long> get = name => usage.GetProperty(name).GetInt64();
            Assert(get("totalAllocInScope") == purposeTotal
                && get("ownedAllocInScope") + get("sharedAllocInScope") + get("unownedAllocInScope") == get("totalAllocInScope")
                && get("ownedSizeInScope") + get("sharedSizeInScope") + get("unownedSizeInScope") == get("totalSizeInScope"),
                "桥接上 tree.owners：两种度量各自守恒，总量等于已统计占用");
            var items = usage.GetProperty("items");
            Assert(usage.GetProperty("ownerCount").GetInt32() == 1 && items.GetArrayLength() == 1, "夹具里只有一个所属对象");
            var project = items[0];
            var roots = project.GetProperty("roots");
            var roles = project.GetProperty("roleBytesInScope");
            Assert(project.GetProperty("kind").GetString() == "project" && project.GetProperty("name").GetString() == "项目代码"
                && roots.GetArrayLength() == 1 && roots[0].GetProperty("path").GetString() == Path.Combine(folder, "项目代码")
                && project.GetProperty("allocInScope").GetInt64() == get("ownedAllocInScope"), "唯一的对象是「项目代码」，一处根");
            Assert(roles.GetArrayLength() == Roles.Count && roles.EnumerateArray().Sum(x => x.GetInt64()) == project.GetProperty("allocInScope").GetInt64(),
                "角色构成长度等于角色数，之和等于计入大小");
        }
        using (var reply = await Call(web, "node.checkDelete", new { nodeIds = new[] { 0 } }))
            Assert(Result(reply, "node.checkDelete").GetProperty("anyBlocked").GetBoolean(), "删除预检拦截根目录");

        // 错误应答：一律是 ok=false 加一句中文，不能把 .NET 的英文异常原文漏给用户。
        using (var reply = await Call(web, "tree.details", new { nodeId = int.MaxValue }))
            Assert(IsFriendlyError(reply, "节点不存在或已删除"), "无效节点返回错误应答");
        using (var reply = await Call(web, "tree.details", new { nodeId = "abc" }))
            Assert(IsFriendlyError(reply, "请求参数无效"), "参数类型不对时返回中文提示");
        using (var reply = await Send(web, NextId(), id => new { id, @params = new { } }))
            Assert(IsFriendlyError(reply, "缺少操作名称"), "缺少操作名称时返回中文提示");
        using (var reply = await Call(web, "arbitrary.command", new { command = "invalid" }))
            Assert(IsFriendlyError(reply, "不支持的操作"), "未知桥接操作不可执行");
        using (var reply = await Call(web, "scan.start", new { path = Path.Combine(folder, "不存在"), mode = "walk" }))
            Assert(IsFriendlyError(reply, "不存在"), "无效扫描路径返回错误");
        using (var reply = await Call(web, "scan.start", new { path = "  ", mode = "walk" }))
            Assert(IsFriendlyError(reply, "扫描路径无效"), "空扫描路径返回中文提示");
        // 清理脚本生成器可能还是桩：不管桩还是真实现，对不存在的建议都必须是 ok=false 加人话，不是崩溃。
        using (var reply = await Call(web, "cleanup.script", new { selections = new[] { new { suggestionId = "no-such-suggestion" } } }))
        {
            Assert(IsFriendlyError(reply, null), "cleanup.script 对无效建议返回错误应答");
            Console.WriteLine($"cleanup.script 对无效建议的应答：ok=false，{ErrorText(reply)}");
        }
        using (var reply = await Call(web, "cleanup.script", new { selections = Array.Empty<object>() }))
            Assert(IsFriendlyError(reply, "请先勾选"), "cleanup.script 没勾选时给出提示");
        // 前端确认框先走 cleanup.check 拿令牌：桥接上必须有这个操作（codex 版只接了前端，确认按钮永远点不动）
        using (var reply = await Call(web, "cleanup.check", new { suggestionId = "no-such-suggestion" }))
            Assert(IsFriendlyError(reply, "失效") && !ErrorText(reply).Contains("不支持"), "cleanup.check 已接上桥接：无效建议给中文提示");
        using (var reply = await Call(web, "scan.summary"))
            Assert(Result(reply, "scan.summary").GetProperty("rootPath").GetString() == folder, "失败请求保留已完成扫描");

        // 被拦截的外部导航（外链、拖进窗口的文件）不能把界面换成「加载失败」。
        var blocked = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnBlocked(object? sender, CoreWebView2NavigationCompletedEventArgs e) => blocked.TrySetResult(e);
        web.CoreWebView2.NavigationCompleted += OnBlocked; // 晚于宿主自己的处理器注册，所以宿主先跑完。
        web.CoreWebView2.Navigate("https://example.invalid/");
        var blockedArgs = await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        web.CoreWebView2.NavigationCompleted -= OnBlocked;
        Assert(!blockedArgs.IsSuccess && blockedArgs.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled, "被拦下的导航以 OperationCanceled 收场");
        await Task.Delay(300);
        Assert(web.Visible, "拦截外部导航后界面仍然可见");
        Assert(await web.ExecuteScriptAsync("location.hostname") == "\"disklens.local\"", "拦截外部导航后页面仍停在本地界面");
        using (var reply = await Call(web, "app.info"))
            Assert(reply.RootElement.GetProperty("ok").GetBoolean(), "拦截外部导航后桥接照常工作");

        await using (var stream = File.Create(screenshot))
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
                .WaitAsync(TimeSpan.FromSeconds(20));
        Console.WriteLine($"桌面界面截图：{screenshot}");

        await CheckShell(form, web, Path.GetDirectoryName(screenshot)!);
        await CheckRecentPage(form, web, Path.GetDirectoryName(screenshot)!);
        await CheckRelatedUi(web, Path.GetDirectoryName(screenshot)!);
        await CheckBrowseUi(web, Path.GetDirectoryName(screenshot)!);
        await CheckStartup(form, web, folder, Path.GetDirectoryName(screenshot)!);
        await CheckLiveTracking(web, folder);
        await CheckSmartSearch(web);
        await CheckCleanupTasksUi(web, folder); // 从界面重扫、删除夹具；之后的检查直接走桥接
        await CheckReadsDuringDelete(web, folder); // 会重扫并删掉一批文件，放在截图之后
        await CheckOperationCancellation(web, folder);
    }

    /// 界面上的完整清理链路（dise §11.3）：搜索定位 → Delete → 确认框（勾永久删除，只动沙箱夹具，不进真实回收站）→ 任务区执行与结果；
    /// 第二批运行中按「查询状态」「停止后续项目」→ 已取消、已删的不回滚、计数和磁盘对得上，上一批结果仍在任务区。
    static async Task CheckCleanupTasksUi(WebView2 web, string folder)
    {
        string first = Path.Combine(folder, "任务区夹具"), second = Path.Combine(folder, "任务区取消");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        for (int i = 0; i < 200; i++) File.WriteAllText(Path.Combine(first, $"a-{i}.txt"), "fixture");
        for (int i = 0; i < 8000; i++) File.WriteAllText(Path.Combine(second, $"b-{i}.txt"), "fixture");

        // 从界面重扫：直接调 scan.start 的话界面不认那次扫描，界面和后台就不是同一棵树了
        int before;
        using (var reply = await Call(web, "scan.summary")) before = Result(reply, "scan.summary").GetProperty("scanId").GetInt32();
        await web.ExecuteScriptAsync("document.querySelector('button[title=\"重新扫描（F5）\"]').click()");
        await Wait(async () =>
        {
            using var reply = await Call(web, "scan.summary");
            return reply.RootElement.GetProperty("result") is { ValueKind: JsonValueKind.Object } s && s.GetProperty("scanId").GetInt32() > before
                && await web.ExecuteScriptAsync(MapIdle) == "true";
        }, "界面重新扫描完成");

        const string tasks = "document.querySelector('aside[aria-label=\"清理建议\"] section[aria-label=\"清理任务\"]')";
        await RevealBySearch(web, "任务区夹具");
        await DeleteSelectedPermanently(web);
        await Wait(async () => await web.ExecuteScriptAsync($"!document.getElementById('confirm-dialog') && !!{tasks}") == "true",
            "确认后确认框关闭，清理右栏的任务区打开");
        await Wait(async () => await web.ExecuteScriptAsync($"{tasks}.querySelector('li').innerText.includes('已完成')") == "true", "任务区显示第一批已完成");
        Assert(!Directory.Exists(first), "任务区执行的永久删除真的删掉了夹具目录");
        Assert(await web.ExecuteScriptAsync($$"""
            (() => { const t = {{tasks}}.innerText;
              return t.includes('永久删除「任务区夹具」') && t.includes('确认目标 1 · 完成 1 · 失败 0 · 跳过 0') && t.includes('已处理文件和目录 201'); })()
            """) == "true", "任务区如实记下确认目标、完成数和处理过的 200 个文件加 1 个目录");
        Assert(await web.ExecuteScriptAsync("document.querySelector('section[aria-label=\"空间概览\"]').innerText.includes('上次清理任务已结束')") == "true",
            "概览里的清理格提示上次任务的结果还能再看");

        // 第二批：第一条带进度的运行事件一到，就像用户那样按「查询状态」和「停止后续项目」
        await RevealBySearch(web, "任务区取消");
        await web.ExecuteScriptAsync($$"""
            (() => {
              const seen = window.__taskUi = { clicked: false };
              window.chrome.webview.addEventListener('message', function handler(e) {
                const m = e.data;
                if (m.event !== 'operation.progress' || m.data.status !== 'running' || !(m.data.processedEntries > 0)) return;
                window.chrome.webview.removeEventListener('message', handler);
                const button = (text) => [...{{tasks}}.querySelectorAll('button')].find(b => b.textContent.trim() === text);
                button('查询状态')?.click();
                button('停止后续项目').click();
                seen.clicked = true;
              });
            })()
            """);
        await DeleteSelectedPermanently(web);
        await Wait(async () => await web.ExecuteScriptAsync($"window.__taskUi.clicked && {tasks}.querySelector('li').innerText.includes('已取消')") == "true",
            "运行中按「停止后续项目」后任务区显示已取消");
        int remaining = Directory.GetFiles(second).Length;
        Assert(remaining > 0 && remaining < 8000, $"停止后不回滚已删的、也不再删后面的（剩 {remaining} 个）");
        Assert(await web.ExecuteScriptAsync($$"""
            (() => { const li = {{tasks}}.querySelector('li'), m = li.innerText.match(/已处理文件和目录 ([\d,]+)/);
              return !!m && Number(m[1].replace(/,/g, '')) === {{8000 - remaining}}; })()
            """) == "true", "任务区的处理计数和磁盘上实际删掉的文件数一致");
        Assert(await web.ExecuteScriptAsync($$"""
            (() => { const t = {{tasks}}.innerText;
              return t.includes('确认目标 1 · 完成 0 · 失败 0 · 跳过 1') && t.includes('此前任务') && t.includes('永久删除「任务区夹具」')
                && ![...{{tasks}}.querySelectorAll('button')].some(b => b.textContent.trim() === '停止后续项目'); })()
            """) == "true", "没做完的目录目标计为跳过；上一批结果保留在「此前任务」；结束后不再显示停止按钮");
        Console.WriteLine($"界面任务区：第一批删 201 项完成；第二批运行中停止，剩 {remaining} 个文件");
        await using (var image = File.Create(Path.Combine(Path.GetDirectoryName(folder)!, "tasks-air-1440.png")))
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image).WaitAsync(TimeSpan.FromSeconds(20));
    }

    /// 关联与生长树（dise §8.4）：搜索 → 地图 → 详情的用途与归属 → 关联模块 → 生长树（缩放、Esc、焦点归还）→ 定位关联对象 → 返回原对象。
    static async Task CheckRelatedUi(WebView2 web, string screenshots)
    {
        const string related = "document.querySelector('aside[aria-label=\"详情\"] section[aria-label=\"关联\"]')";
        const string tree = "document.querySelector('section[aria-label=\"关联生长树\"]')";
        string openTree = $"(() => {{ const b = [...{related}.querySelectorAll('button')].find(b => b.textContent.includes('查看生长树')); b.focus(); b.click(); }})()";
        string treeReady = $"!!{tree} && {tree}.querySelector('.stage').getAttribute('aria-busy') === 'false' && !!{tree}.querySelector('.node.current')";

        await RevealBySearch(web, "module-23.ts");
        await Wait(async () => await web.ExecuteScriptAsync($"!!{related} && {related}.getAttribute('aria-busy') === 'false' && {related}.innerText.includes('项目代码')") == "true",
            "详情里列出同一所属对象的关联模块");
        Assert(await web.ExecuteScriptAsync("""
            (() => { const t = document.querySelector('aside[aria-label="详情"] dl[aria-label="用途与归属"]').innerText;
              return t.includes('主用途') && t.includes('数据角色') && t.includes('所属对象') && t.includes('项目代码') && t.includes('已确认'); })()
            """) == "true", "详情分开列出用途、角色和所属对象，各带证据等级");
        Assert(await web.ExecuteScriptAsync($"{related}.innerText.includes('所属对象的根') && {related}.innerText.includes('不会扩大清理范围')") == "true",
            "关联条目写明关系类型，并说明不扩大清理范围");

        await web.ExecuteScriptAsync(openTree);
        await Wait(async () => await web.ExecuteScriptAsync(treeReady) == "true", "生长树打开并画出当前对象");
        Assert(await web.ExecuteScriptAsync($$"""
            (() => { const t = {{tree}};
              return !!t.querySelector('.node.owner') && t.querySelectorAll('.event').length >= 2 && !!t.querySelector('.trunk.now')
                && t.innerText.includes('暂无历史记录') && !!document.querySelector('aside[aria-label="详情"]'); })()
            """) == "true", "主干有时间记录和虚线的当前关联段，所属对象单独成节点，详情面板保持打开");
        string zoom = await web.ExecuteScriptAsync($"{tree}.querySelector('.zoom .num').textContent");
        await web.ExecuteScriptAsync($"(() => {{ const s = {tree}.querySelector('.stage'); s.focus(); s.dispatchEvent(new KeyboardEvent('keydown', {{ key: '+', bubbles: true }})); }})()");
        await Wait(async () => await web.ExecuteScriptAsync($"{tree}.querySelector('.zoom .num').textContent") != zoom, "画布上按加号放大");
        await web.ExecuteScriptAsync($"{tree}.querySelector('button[aria-label=\"适应窗口\"]').click()");
        await Task.Delay(150);
        await using (var image = File.Create(Path.Combine(screenshots, "related-tree-1440.png")))
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image).WaitAsync(TimeSpan.FromSeconds(20));
        await web.ExecuteScriptAsync(PressEscape);
        await Wait(async () => await web.ExecuteScriptAsync($"!{tree} && document.activeElement?.textContent.includes('查看生长树') === true") == "true",
            "Esc 关掉生长树，焦点回到打开它的按钮");

        await web.ExecuteScriptAsync(openTree);
        await Wait(async () => await web.ExecuteScriptAsync(treeReady) == "true", "再次打开生长树");
        await web.ExecuteScriptAsync($"[...{tree}.querySelectorAll('.node:not(.current):not(.owner)')].find(n => n.textContent.includes('项目代码')).dispatchEvent(new MouseEvent('click', {{ bubbles: true }}))");
        const string info = "document.querySelector('section[aria-label=\"关联生长树\"] aside[aria-label=\"节点信息\"]')";
        await Wait(async () => await web.ExecuteScriptAsync($"{info}?.querySelector('h3')?.textContent === '项目代码'") == "true", "点关联分支，信息卡换成它");
        await web.ExecuteScriptAsync($"[...{info}.querySelectorAll('button')].find(b => b.textContent === '在地图中定位').click()");
        await Wait(async () => await web.ExecuteScriptAsync($"!{tree} && document.querySelector('aside[aria-label=\"详情\"] .name')?.textContent === '项目代码' && ({MapIdle})") == "true",
            "在地图中定位关联对象：生长树关掉，地图和详情换成它");
        string back = $"[...{related}.querySelectorAll('button')].find(b => b.textContent.includes('返回「module-23.ts」'))";
        await Wait(async () => await web.ExecuteScriptAsync($"!!{related} && !!{back}") == "true", "详情里给出返回原对象的入口");
        await web.ExecuteScriptAsync($"{back}.click()");
        await Wait(async () => await web.ExecuteScriptAsync($"document.querySelector('aside[aria-label=\"详情\"] .name')?.textContent === 'module-23.ts' && !{back}") == "true",
            "返回原对象，返回入口用过即清");
    }

    /// 空间浏览（dise §5、§7.4、§3.6）：同步目录列表（全部子项、排序、选中同步、键盘）、图例筛选与分母说明、应用占用维度与定位、顶栏外部搜索。
    /// 当前地图停在「项目代码」（上一步返回 module-23.ts 留下的）。
    static async Task CheckBrowseUi(WebView2 web, string screenshots)
    {
        const string list = "document.querySelector('section[aria-label=\"目录列表\"]')";
        string rowNames = $"[...{list}.querySelectorAll('button.row .name')].map(e => e.textContent)";
        int dir;
        using (var reply = await Call(web, "scan.summary")) _ = Result(reply, "scan.summary");
        using (var reply = await Call(web, "search.query", new { query = "项目代码", metric = "alloc", limit = 5 }))
            dir = Result(reply, "search.query").GetProperty("items").EnumerateArray().First(i => i.GetProperty("isDir").GetBoolean()).GetProperty("id").GetInt32();

        await web.ExecuteScriptAsync("document.querySelector('section[aria-label=\"资源地图\"] .listbtn').click()");
        await Wait(async () => await web.ExecuteScriptAsync($"!!{list} && {list}.getAttribute('aria-busy') === 'false' && {list}.querySelectorAll('button.row').length > 0") == "true", "打开目录列表");
        using (var reply = await Call(web, "tree.children", new { nodeId = dir, metric = "alloc", sort = "name", limit = 500 }))
        {
            var expected = Result(reply, "tree.children");
            int total = expected.GetProperty("total").GetInt32();
            Assert(await web.ExecuteScriptAsync($"{list}.querySelectorAll('button.row').length === {total} && {list}.querySelector('.foot').textContent.startsWith('{total} 项')") == "true",
                $"列表列出当前目录全部 {total} 个子项（地图上的小块也在）");
            await web.ExecuteScriptAsync($"[...{list}.querySelectorAll('.cols button')].find(b => b.textContent.startsWith('名称')).click()");
            string names = JsonSerializer.Serialize(expected.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToArray());
            await Wait(async () => await web.ExecuteScriptAsync($"JSON.stringify({rowNames}) === JSON.stringify({names})") == "true", "按名称排序后和后台给的顺序一致");
        }
        await web.ExecuteScriptAsync($"[...{list}.querySelectorAll('button.row')].find(b => b.querySelector('.name').textContent === 'module-5.ts').click()");
        await Wait(async () => await web.ExecuteScriptAsync($"document.querySelector('aside[aria-label=\"详情\"] .name')?.textContent === 'module-5.ts' && {list}.querySelector('button.row[aria-selected=\"true\"] .name').textContent === 'module-5.ts'") == "true",
            "点列表行：选中项和详情同步换成它");
        await web.ExecuteScriptAsync($"(() => {{ const r = {list}.querySelector('button.row[aria-selected=\"true\"]'); r.focus(); r.dispatchEvent(new KeyboardEvent('keydown', {{ key: 'ArrowDown', bubbles: true }})); }})()");
        await Wait(async () => await web.ExecuteScriptAsync($"document.activeElement?.closest('section[aria-label=\"目录列表\"]') !== null && document.activeElement.getAttribute('aria-selected') === 'true' && document.activeElement.querySelector('.name').textContent !== 'module-5.ts'") == "true",
            "列表里按下方向键：焦点和选中一起移到下一行");
        await using (var image = File.Create(Path.Combine(screenshots, "browse-list-1440.png")))
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image).WaitAsync(TimeSpan.FromSeconds(20));

        // 图例：点一项只看这一类，写明分母是当前目录；清除筛选恢复
        await web.ExecuteScriptAsync("document.querySelector('.legend .leg').click()");
        await Wait(async () => await web.ExecuteScriptAsync("document.querySelector('.legend .leg').getAttribute('aria-pressed') === 'true' && document.querySelector('.filter')?.textContent.includes('分母是当前目录') === true") == "true",
            "点图例只看这一类，并写明比例的分母是当前目录");
        await web.ExecuteScriptAsync("document.querySelector('.filter .clear').click()");
        await Wait(async () => await web.ExecuteScriptAsync("!document.querySelector('.filter') && document.querySelector('.legend .leg').getAttribute('aria-pressed') === 'false'") == "true", "清除筛选");

        // 应用占用维度：范围写明，合计和地图同口径；定位一处根回到目录分布
        await web.ExecuteScriptAsync("[...document.querySelectorAll('.dims button')].find(b => b.textContent === '应用占用').click()");
        const string owners = "document.querySelector('section[aria-label=\"应用占用\"]')";
        await Wait(async () => await web.ExecuteScriptAsync($"!!{owners} && {owners}.getAttribute('aria-busy') === 'false' && {owners}.innerText.includes('项目代码')") == "true", "应用占用列出所属对象");
        Assert(await web.ExecuteScriptAsync($"{owners}.innerText.includes('不是应用在整台机器上的总占用') && {owners}.innerText.includes('和地图上这个目录的大小一致') && !document.querySelector('section[aria-label=\"资源地图\"] .canvas') && !{list}") == "true",
            "应用占用写明只算当前范围，合计和地图同口径；地图和目录列表让出位置");
        await web.ExecuteScriptAsync($"(() => {{ const d = {owners}.querySelector('details'); d.open = true; d.querySelector('button').click(); }})()");
        await Wait(async () => await web.ExecuteScriptAsync($"!{owners} && !!document.querySelector('section[aria-label=\"资源地图\"] .canvas') && document.querySelector('aside[aria-label=\"详情\"] .name')?.textContent === '项目代码' && ({MapIdle})") == "true",
            "在应用占用里定位一处根：回到目录分布，地图和详情换成它");

        // 外部搜索：选了引擎，打字不查本地；后台拒绝不在白名单的引擎和空词（不发合法请求，免得真的打开浏览器）
        await web.ExecuteScriptAsync("(() => { const s = document.querySelector('select.scope'); s.value = 'google'; s.dispatchEvent(new Event('change', { bubbles: true })); const i = document.getElementById('search-input'); i.value = 'pagefile'; i.dispatchEvent(new Event('input', { bubbles: true })); })()");
        await Task.Delay(400);
        Assert(await web.ExecuteScriptAsync("!document.getElementById('search-list') && document.getElementById('search-input').placeholder.includes('Google') && document.querySelector('.search .help').hidden") == "true",
            "选 Google 后打字不出本地结果，提示回车才在浏览器打开");
        await web.ExecuteScriptAsync("(() => { const s = document.querySelector('select.scope'); s.value = 'files'; s.dispatchEvent(new Event('change', { bubbles: true })); const i = document.getElementById('search-input'); i.value = ''; i.dispatchEvent(new Event('input', { bubbles: true })); })()");
        using (var reply = await Call(web, "web.search", new { engine = "baidu", query = "x" }))
            Assert(IsFriendlyError(reply, "不支持的搜索引擎"), "web.search 拒绝白名单外的引擎");
        using (var reply = await Call(web, "web.search", new { engine = "bing", query = "   " }))
            Assert(IsFriendlyError(reply, "请输入要搜索的内容"), "web.search 拒绝空搜索词");
        await web.ExecuteScriptAsync("document.querySelector('section[aria-label=\"资源地图\"] .listbtn').click()");
        await Wait(async () => await web.ExecuteScriptAsync($"!{list}") == "true", "收起目录列表，地图收回宽度");
    }

    /// 地图已经出来、没有在加载（JS 表达式）
    const string MapIdle = "!!document.querySelector('section[aria-label=\"资源地图\"]') && document.querySelector('section[aria-label=\"资源地图\"] .canvas')?.getAttribute('aria-busy') === 'false'";

    /// 在顶栏搜索框里搜名字，点开搜索结果：定位到地图并打开它的详情
    static async Task RevealBySearch(WebView2 web, string name)
    {
        string q = JsonSerializer.Serialize(name);
        await web.ExecuteScriptAsync($$"""
            (() => { const i = document.getElementById('search-input'); i.focus(); i.value = {{q}}; i.dispatchEvent(new Event('input', { bubbles: true })); })()
            """);
        string row = $"[...document.querySelectorAll('#search-list [role=\"option\"]')].find(b => b.innerText.includes({q}))";
        await Wait(async () => await web.ExecuteScriptAsync($"!!{row}") == "true", $"搜索结果里出现「{name}」");
        await web.ExecuteScriptAsync($"{row}.click()");
        await Wait(async () => await web.ExecuteScriptAsync($"document.querySelector('aside[aria-label=\"详情\"]')?.innerText.includes({q}) === true && ({MapIdle})") == "true",
            $"定位到「{name}」并打开详情");
    }

    /// 按 Delete，在确认框里勾「永久删除」（沙箱夹具不进真实回收站），再按红色主按钮
    static async Task DeleteSelectedPermanently(WebView2 web)
    {
        await web.ExecuteScriptAsync("document.activeElement?.blur(); document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true }))");
        await Wait(async () => await web.ExecuteScriptAsync("!!document.getElementById('confirm-dialog')") == "true", "Delete 弹出确认框");
        await web.ExecuteScriptAsync("[...document.querySelectorAll('#confirm-dialog label.opt')].find(l => l.innerText.includes('永久删除')).querySelector('input').click()");
        await Wait(async () => await web.ExecuteScriptAsync("!!document.querySelector('#confirm-dialog footer .btn-danger:not(:disabled)')") == "true", "勾上永久删除后主按钮变红且可用");
        await web.ExecuteScriptAsync("document.querySelector('#confirm-dialog footer .btn-danger').click()");
    }

    static async Task CheckStartup(MainForm form, WebView2 web, string folder, string screenshots)
    {
        await web.ExecuteScriptAsync("document.querySelector('button[title=\"回到启动页\"]').click()");
        const string page = "document.querySelector('main[aria-label=\"DiskLens 启动页\"]')";
        await Wait(async () => await web.ExecuteScriptAsync($"!!{page} && !{page}.querySelector('[aria-busy=\"true\"]')") == "true", "启动页数据加载完成");
        Assert(await web.ExecuteScriptAsync($"!!{page}.querySelector('svg[aria-label=\"DiskLens 磁盘透镜\"]')") == "true", "启动页显示可访问的终端 Logo");
        using (var reply = await Call(web, "drives.list"))
        {
            int count = Result(reply, "drives.list").GetArrayLength();
            Assert(await web.ExecuteScriptAsync($"{page}.querySelectorAll('.drive').length") == count.ToString(), "每个真实磁盘各有一张独立卡片");
        }
        Assert(await web.ExecuteScriptAsync($"!{page}.innerText.includes('扫描主文件夹') && !{page}.innerText.includes('扫描文件夹…') && !{page}.innerText.includes('深度扫描')") == "true", "启动页移除文件夹快捷入口和双扫描按钮");
        string scope = JsonSerializer.Serialize(folder);
        // 缓存对照（第 2 版记录）：夹具这条记下了根下面的大项和认出的应用（只有开发项目，不算应用）
        double savedAt;
        using (var reply = await Call(web, "scan.savedSummaries"))
        {
            var mine = Result(reply, "scan.savedSummaries").EnumerateArray().Single(e => e.GetProperty("rootPath").GetString() == folder);
            savedAt = mine.GetProperty("completedAtMs").GetDouble();
            Assert(mine.GetProperty("apps").GetArrayLength() == 0 && mine.GetProperty("topEntries").EnumerateArray().Any(e => e.GetProperty("name").GetString() == "项目代码"),
                "夹具的扫描记录带大项（项目代码）和应用清单（只有开发项目，应用是空的）");
        }
        Assert(await web.ExecuteScriptAsync($"![...{page}.querySelectorAll('section[aria-label=\"新应用与安装位置\"] code')].some(c => c.textContent.startsWith({scope}))") == "true",
            "只有开发项目的真实夹具不冒充应用");
        string entry = $"[...{page}.querySelectorAll('details.history')].find(e => e.querySelector('strong').textContent === {scope})";
        await web.ExecuteScriptAsync($"{entry}.querySelector('summary').click()");
        Assert(await web.ExecuteScriptAsync($"{entry}.open && {entry}.innerText.includes('这是保存的摘要') && {entry}.querySelector('table.entries').innerText.includes('项目代码') && !document.querySelector('section[aria-label=\"资源地图\"]')") == "true",
            "真实持久化摘要先展开，带根下面的大项；不把旧摘要当成文件树");
        using (var reply = await Call(web, "shell.openFolder", new { path = Path.Combine(folder, "不在记录里") }))
            Assert(IsFriendlyError(reply, "不在扫描记录里"), "按路径打开只认扫描记录里的位置，别的路径拒绝");
        using (var reply = await Call(web, "shell.openFolder", new { path = "相对路径" }))
            Assert(IsFriendlyError(reply, "不在扫描记录里"), "相对路径拒绝");
        var minimum = form.MinimumSize;
        form.MinimumSize = Size.Empty;
        foreach (int width in new[] { 1440, 1100, 600, 360 })
        {
            form.WindowState = FormWindowState.Normal;
            form.ClientSize = new Size(form.LogicalToDeviceUnits(width), form.LogicalToDeviceUnits(860));
            await Task.Delay(200);
            Assert(await web.ExecuteScriptAsync($$"""
                (() => { const p = {{page}}; return p.scrollWidth <= p.clientWidth + 1 &&
                  [...p.querySelectorAll('button, summary, .drive, code')].every(e => {
                    const b = e.getBoundingClientRect(); return b.width === 0 || (b.left >= 0 && b.right <= innerWidth + 1);
                  }); })()
                """) == "true", $"澄净启动页在 {width} 宽度下没有横向溢出");
            await using var image = File.Create(Path.Combine(screenshots, $"startup-air-{width}.png"));
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image).WaitAsync(TimeSpan.FromSeconds(20));
        }
        form.MinimumSize = minimum;
        form.ClientSize = new Size(form.LogicalToDeviceUnits(1440), form.LogicalToDeviceUnits(860));
        await CheckLanguage(web, screenshots);
        await web.ExecuteScriptAsync($"{entry}.querySelector('button').click()");
        await Wait(async () => await web.ExecuteScriptAsync("!!document.querySelector('section[aria-label=\"资源地图\"]')") == "true", "历史摘要的重新扫描返回真实资源地图");
        using var current = await Call(web, "scan.summary");
        Assert(Result(current, "scan.summary").GetProperty("rootPath").GetString() == folder, "历史重新扫描只扫描所选夹具范围");
        using (var reply = await Call(web, "scan.savedSummaries"))
        {
            var mine = Result(reply, "scan.savedSummaries").EnumerateArray().Single(e => e.GetProperty("rootPath").GetString() == folder);
            var project = mine.GetProperty("topEntries").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "项目代码");
            Assert(mine.GetProperty("previousAtMs").GetDouble() == savedAt && project.GetProperty("previousAlloc").ValueKind == JsonValueKind.Number,
                "重扫之后的记录和上一次对照：带上一次的完成时刻，大项带上一次的占用");
        }
    }

    /// 界面语言（DESIGN §8.8）：启动页左上角换成英文、日文——界面马上换，后台的文字跟着换，settings.json 记下来；最后换回中文
    static async Task CheckLanguage(WebView2 web, string screenshots)
    {
        string settings = Path.Combine(AppContext.BaseDirectory, "data", "settings.json");
        const string start = "document.querySelector('main.start')";
        foreach (var (code, label, text, cancel) in new[]
        {
            ("en", "English", "Free now", "No scan in progress"),
            ("ja", "日本語", "空き容量", "実行中のスキャンはありません"),
            ("zh-CN", "简体中文", "当前可用", "没有正在进行的扫描"),
        })
        {
            await web.ExecuteScriptAsync($$"""
                (() => { const s = {{start}}.querySelector('.lang select'); s.value = '{{code}}'; s.dispatchEvent(new Event('change', { bubbles: true })); })()
                """);
            await Wait(async () => await web.ExecuteScriptAsync($"document.documentElement.lang === '{code}' && {start}.querySelector('.lang span').textContent === '{label}'") == "true",
                $"启动页左上角换成 {label}：界面马上换");
            Assert(await web.ExecuteScriptAsync($"{start}.innerText.includes('{text}') && new URLSearchParams(location.search).get('lang') === '{code}'") == "true",
                $"{label}：启动页的文字跟着换，地址里的 ?lang= 也改了（刷新还是这个语言）");
            await Wait(() => Task.FromResult(TryRead(settings)?.Contains($"\"language\":\"{code}\"") == true), $"{label}：settings.json 记下了选择，下次启动就是它");
            using (var reply = await Call(web, "app.info"))
                Assert(Result(reply, "app.info").GetProperty("language").GetString() == code, $"{label}：后台的界面语言跟着换");
            using (var reply = await Call(web, "scan.cancel"))
                Assert(Result(reply, "scan.cancel").GetProperty("message").GetString() == cancel, $"{label}：后台发来的文字是这个语言");
            await using var image = File.Create(Path.Combine(screenshots, $"startup-{code}.png"));
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image).WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    /// 宿主正在改名覆盖时读会撞上共享冲突：当这一轮没读到，下一轮再读
    static string? TryRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
    }

    const string LiveText = "document.querySelector('nav[aria-label=\"页面\"] > span').innerText";

    /// 实时状态：扫描之后别的程序删掉的文件，后台跟踪落进树，界面安静刷新——目录列表、汇总、搜索都跟着少，顶栏写明同步了几项
    static async Task CheckLiveTracking(WebView2 web, string folder)
    {
        string victim = Path.Combine(folder, "外部删除.bin"), keep = Path.Combine(folder, "外部保留.bin");
        File.WriteAllBytes(victim, new byte[192 * 1024]);
        File.WriteAllBytes(keep, new byte[64 * 1024]);
        int before;
        using (var reply = await Call(web, "scan.summary")) before = Result(reply, "scan.summary").GetProperty("scanId").GetInt32();
        await web.ExecuteScriptAsync("document.querySelector('button[title=\"重新扫描（F5）\"]').click()");
        await Wait(async () =>
        {
            using var reply = await Call(web, "scan.summary");
            return reply.RootElement.GetProperty("result") is { ValueKind: JsonValueKind.Object } s && s.GetProperty("scanId").GetInt32() > before
                && await web.ExecuteScriptAsync(MapIdle) == "true";
        }, "界面重新扫描完成（带上外部删除的夹具）");
        await Wait(async () => await web.ExecuteScriptAsync(LiveText + ".includes('实时跟踪中')") == "true", "顶栏写明在实时跟踪");
        await web.ExecuteScriptAsync("document.querySelector('button.listbtn[aria-pressed=\"false\"]')?.click()");
        const string rows = "document.querySelector('section[aria-label=\"目录列表\"] [role=\"listbox\"]')";
        await Wait(async () => await web.ExecuteScriptAsync($"!!{rows} && {rows}.innerText.includes('外部删除.bin')") == "true", "目录列表里有外部删除的夹具");
        long files;
        using (var reply = await Call(web, "scan.summary")) files = Result(reply, "scan.summary").GetProperty("files").GetInt64();

        File.Delete(victim); // 相当于别的程序删掉了它
        await Wait(async () => await web.ExecuteScriptAsync($"!{rows}.innerText.includes('外部删除.bin') && {rows}.innerText.includes('外部保留.bin')") == "true",
            "别的程序删掉的文件不用重扫就从目录列表里消失，没删的还在");
        Assert(await web.ExecuteScriptAsync(LiveText + ".includes('已同步别的程序删除的 1 项')") == "true", "顶栏写明同步了别的程序删除的 1 项");
        using (var reply = await Call(web, "scan.summary"))
            Assert(Result(reply, "scan.summary").GetProperty("files").GetInt64() == files - 1, "汇总的文件数跟着少一个");
        using (var reply = await Call(web, "search.query", new { query = "外部删除", metric = "alloc", limit = 10 }))
            Assert(Result(reply, "search.query").GetProperty("total").GetInt64() == 0, "搜索也搜不到了");
        await web.ExecuteScriptAsync("document.querySelector('button.listbtn[aria-pressed=\"true\"]')?.click()");
        File.Delete(keep);
        Console.WriteLine("实时状态：别的程序删掉的文件不用重扫就从界面消失");
    }

    /// 智能搜索：顶栏默认按相关度、能切占用；一条都对不上时给近似结果并写明
    static async Task CheckSmartSearch(WebView2 web)
    {
        using (var reply = await Call(web, "search.query", new { query = "mdule", metric = "alloc", limit = 300, smart = true, sort = "relevance" }))
        {
            var r = Result(reply, "search.query");
            Assert(r.GetProperty("fuzzy").GetBoolean() && r.GetProperty("total").GetInt64() >= 24, "桥接上智能搜索：漏了字母的 mdule 近似找到 module-*");
        }
        using (var reply = await Call(web, "search.query", new { query = "mdule", metric = "alloc", limit = 300 }))
            Assert(!Result(reply, "search.query").GetProperty("fuzzy").GetBoolean() && Result(reply, "search.query").GetProperty("total").GetInt64() == 0,
                "不开智能匹配：照旧只认子串");
        string Type(string text) => $$"""
            (() => { const i = document.getElementById('search-input'); i.focus(); i.value = {{JsonSerializer.Serialize(text)}}; i.dispatchEvent(new Event('input', { bubbles: true })); })()
            """;
        await web.ExecuteScriptAsync(Type("module-1"));
        const string sort = "document.querySelector('.search .sort')";
        await Wait(async () => await web.ExecuteScriptAsync($"!!{sort} && {sort}.querySelector('[aria-pressed=\"true\"]').textContent === '相关度'") == "true",
            "搜索结果默认按相关度，排序按钮可见");
        await web.ExecuteScriptAsync(Type("mdule"));
        await Wait(async () => await web.ExecuteScriptAsync("document.querySelector('.search .note.fuzzy')?.innerText.includes('近似结果') === true") == "true",
            "一条都对不上时写明下面是近似结果");
        await web.ExecuteScriptAsync(Type(""));
        await web.ExecuteScriptAsync("document.getElementById('search-input').blur()");
    }

    /// 工作区局部视图切换（JS 表达式）
    static string NavButton(string label) =>
        $"[...document.querySelectorAll('nav[aria-label=\"页面\"] button')].find(b => b.textContent.trim() === '{label}')";

    const string PressEscape = "document.activeElement.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))";

    /// 澄净工作区：地图优先、无左侧导航，清理和详情按需展开。
    static async Task CheckShell(MainForm form, WebView2 web, string screenshots)
    {
        Assert(await web.ExecuteScriptAsync("""
            (() => {
              const items = [...document.querySelectorAll('nav[aria-label="页面"] button')];
              const current = items.filter(b => b.getAttribute('aria-current') === 'page');
              return items.map(b => b.textContent.trim()).join('|') === '资源地图|最近文件'
                && current.length === 1 && current[0].textContent.trim() === '资源地图'
                && !document.querySelector('aside.side') && !!document.querySelector('section[aria-label="空间概览"]');
            })()
            """) == "true", "局部切换为资源地图/最近文件，扫描后显示地图和紧凑概览，无左侧导航");

        // 度量和着色收进「显示选项」：平时顶栏上没有「占用空间」按钮
        const string buttons = "[...document.querySelectorAll('button')]";
        Assert(await web.ExecuteScriptAsync($"!{buttons}.some(b => b.textContent.trim() === '占用空间')") == "true", "度量切换不长期占顶栏");
        await web.ExecuteScriptAsync($"(() => {{ const b = {buttons}.find(b => b.textContent.trim() === '显示选项'); b.focus(); b.click(); }})()");
        await Wait(async () => await web.ExecuteScriptAsync("""
            (() => {
              const d = document.querySelector('[role="dialog"][aria-label="显示选项"]');
              return !!d && [...d.querySelectorAll('button[aria-pressed="true"]')].map(b => b.textContent.trim()).join('|') === '占用空间|类别';
            })()
            """) == "true", "显示选项弹层里有度量和着色两组，默认占用空间、类别");
        await web.ExecuteScriptAsync(PressEscape);
        await Wait(async () => await web.ExecuteScriptAsync("!document.querySelector('[role=\"dialog\"][aria-label=\"显示选项\"]')") == "true", "Esc 关掉显示选项");

        // 详情面板按需打开；Esc 关闭并把焦点还给触发点
        const string toggle = "document.querySelector('section[aria-label=\"资源地图\"] [aria-label=\"右侧面板\"] button')";
        const string panel = "document.querySelector('aside[aria-label=\"详情\"]')";
        Assert(await web.ExecuteScriptAsync($"!{panel}") == "true", "扫描完成后详情面板默认收起");
        await web.ExecuteScriptAsync($"(() => {{ const b = {toggle}; b.focus(); b.click(); }})()");
        await Wait(async () => await web.ExecuteScriptAsync($"!!{panel}") == "true", "点「详情」打开详情面板");
        await web.ExecuteScriptAsync(PressEscape);
        await Wait(async () => await web.ExecuteScriptAsync($"!{panel} && document.activeElement === {toggle}") == "true",
            "Esc 关闭详情面板，焦点回到「详情」按钮");

        // 清理建议在右栏展示，地图持续可见。
        await web.ExecuteScriptAsync("[...document.querySelectorAll('[aria-label=\"右侧面板\"] button')].find(b => b.textContent === '清理').click()");
        await Wait(async () => await web.ExecuteScriptAsync($$"""
            (() => {
              const p = document.querySelector('aside[aria-label="清理建议"]');
              return !!p
                && [...p.querySelectorAll('button')].some(b => b.textContent.trim() === '复制脚本')
                && !!document.querySelector('section[aria-label="资源地图"]');
            })()
            """) == "true", "清理右栏保留复制脚本，资源地图持续可见");
        foreach (int width in new[] { 1440, 1100 })
        {
            form.WindowState = FormWindowState.Normal;
            form.ClientSize = new Size(form.LogicalToDeviceUnits(width), form.LogicalToDeviceUnits(860));
            await Task.Delay(200);
            Assert(await web.ExecuteScriptAsync("(() => { const r = document.querySelector('section[aria-label=\"资源地图\"] .canvas').getBoundingClientRect(); return r.width > innerWidth * .5 && r.height > innerHeight * .5; })()") == "true", $"{width} 宽度下开着清理右栏，地图宽高仍超过窗口一半");
            Assert(await web.ExecuteScriptAsync("[...document.querySelectorAll('.workspace button')].every(e => { const r = e.getBoundingClientRect(); return r.width === 0 || (r.left >= 0 && r.right <= innerWidth + 1); })") == "true", $"{width} 宽度下工作区按钮无横向溢出");
            await using var image = File.Create(Path.Combine(screenshots, $"map-air-{width}.png"));
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image).WaitAsync(TimeSpan.FromSeconds(20));
        }
        await web.ExecuteScriptAsync("document.querySelector('button[aria-label=\"关闭面板\"]').click()");
        Assert(await web.ExecuteScriptAsync("document.querySelector('section[aria-label=\"资源地图\"]').getBoundingClientRect().width > innerWidth * .9") == "true", "关掉右栏后地图收回空间");
        await web.ExecuteScriptAsync("document.querySelector('.report > summary').click()");
        Assert(await web.ExecuteScriptAsync("document.querySelector('.report').open && document.querySelector('.report').innerText.includes('拒绝访问入口') && document.querySelector('.report').innerText.includes('常规扫描')") == "true", "扫描方式和异常计数进入可展开的扫描详情");
        await web.ExecuteScriptAsync("document.querySelector('.report > summary').focus()");
        await web.ExecuteScriptAsync(PressEscape);
        Assert(await web.ExecuteScriptAsync("!document.querySelector('.report').open") == "true", "Esc 关闭扫描详情");
        var minimum = form.MinimumSize;
        form.MinimumSize = Size.Empty;
        foreach (int width in new[] { 600, 500, 360 })
        {
            form.ClientSize = new Size(form.LogicalToDeviceUnits(width), form.LogicalToDeviceUnits(900));
            await Task.Delay(200);
            await web.ExecuteScriptAsync("document.querySelector('.report > summary').click()");
            Assert(await web.ExecuteScriptAsync("(() => { const r = document.querySelector('.report-content').getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth + 1 && r.bottom <= innerHeight; })()") == "true", $"{width} 窄窗口扫描详情弹层完整落在视口内");
            await web.ExecuteScriptAsync("document.querySelector('.report > summary').focus()");
            await web.ExecuteScriptAsync(PressEscape);
            await web.ExecuteScriptAsync($"(() => {{ const b = {buttons}.find(b => b.textContent.trim() === '显示选项'); b.focus(); b.click(); }})()");
            Assert(await web.ExecuteScriptAsync("(() => { const r = document.querySelector('[role=\"dialog\"][aria-label=\"显示选项\"]').getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth + 1; })()") == "true", $"{width} 窄窗口显示选项不越界");
            await web.ExecuteScriptAsync(PressEscape);
            await web.ExecuteScriptAsync($"(() => {{ const b = {toggle}; b.focus(); b.click(); }})()");
            Assert(await web.ExecuteScriptAsync("(() => { const r = document.querySelector('aside.panel').getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth + 1 && !!document.querySelector('section[aria-label=\"资源地图\"]'); })()") == "true", $"{width} 窄窗口右栏覆盖显示且地图保留");
            await web.ExecuteScriptAsync("document.querySelector('button[aria-label=\"关闭面板\"]').click()");
            Assert(await web.ExecuteScriptAsync($"!document.querySelector('aside.panel') && document.activeElement === {toggle}") == "true", $"{width} 窄窗口右栏关闭后恢复触发按钮焦点");
        }
        form.MinimumSize = minimum;
        form.ClientSize = new Size(form.LogicalToDeviceUnits(1440), form.LogicalToDeviceUnits(860));
    }

    static async Task CheckRecentPage(MainForm form, WebView2 web, string screenshots)
    {
        const string page = "document.querySelector('section[aria-label=\"最近文件\"]')";
        string open = $"{NavButton("最近文件")}.click()";
        await web.ExecuteScriptAsync(open);
        await Wait(async () => await web.ExecuteScriptAsync($"{page}?.querySelectorAll('tbody tr').length === 27") == "true", "最近文件页加载真实的 27 个文件");
        Assert(await web.ExecuteScriptAsync($"{page}.innerText.includes('备份.zip')") == "false", "最近文件页面不显示窗口之外的旧文件");
        Assert(await web.ExecuteScriptAsync($"{page}.querySelector('tbody tr').innerText.includes('影片, 测试.mp4')") == "true", "默认按占用空间降序，最大文件在首行");
        Assert(await web.ExecuteScriptAsync($"{page}.innerText.includes('修改时间不等于写入量') && {page}.innerText.includes('按扫描时间')") == "true",
            "最近文件页注明扫描基准和修改时间的含义");
        Assert(await web.ExecuteScriptAsync($"{page}.innerText.includes('项目代码（已确认）')") == "true", "最近文件页展示所属对象和证据");

        foreach (var width in new[] { 1100, 1440 })
        {
            form.WindowState = FormWindowState.Normal;
            form.ClientSize = new Size(form.LogicalToDeviceUnits(width), form.LogicalToDeviceUnits(780));
            await Task.Delay(200);
            Assert(await web.ExecuteScriptAsync($$"""
                (() => {
                  const p = {{page}}, r = p.getBoundingClientRect();
                  return r.left >= 0 && r.right <= innerWidth + 1 &&
                    [...p.querySelectorAll('select, button, table')].every(e => {
                      const b = e.getBoundingClientRect(); return b.left >= r.left && b.right <= r.right + 1;
                    });
                })()
                """) == "true", $"最近文件页在 {width} 宽度下控件和列表不溢出");
            await using var stream = File.Create(Path.Combine(screenshots, $"recent-{width}.png"));
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream).WaitAsync(TimeSpan.FromSeconds(20));
        }

        await web.ExecuteScriptAsync($$"""
            (() => {
              const s = {{page}}.querySelector('select'); s.selectedIndex = 0;
              s.dispatchEvent(new Event('change', { bubbles: true }));
            })()
            """);
        await Wait(async () => await web.ExecuteScriptAsync($"{page}?.querySelectorAll('tbody tr').length === 26") == "true", "最近一天排除三天前的文件");
        Assert(await web.ExecuteScriptAsync($"{page}.innerText.includes('module-0.ts')") == "false", "一天筛选确实改变显示内容");
        await web.ExecuteScriptAsync($$"""
            (() => {
              const s = {{page}}.querySelectorAll('select')[1]; s.value = 'modified';
              s.dispatchEvent(new Event('change', { bubbles: true }));
            })()
            """);
        await Wait(async () => await web.ExecuteScriptAsync($"{page}?.querySelector('caption')?.textContent.includes('按修改时间') && {page}?.querySelectorAll('tbody tr').length === 26") == "true", "修改时间排序加载完成");
        Assert(await web.ExecuteScriptAsync($$"""
            (() => { const dates = [...{{page}}.querySelectorAll('tbody .modified')].map(e => e.textContent);
              return dates.every((d, i) => i === 0 || dates[i - 1] >= d); })()
            """) == "true", "最近文件页按修改时间从新到旧显示");

        await web.ExecuteScriptAsync($"{page}.querySelector('button[aria-label=\"定位 module-23.ts\"]').click()");
        await Wait(async () => await web.ExecuteScriptAsync("!!document.querySelector('section[aria-label=\"资源地图\"]') && document.querySelector('aside[aria-label=\"详情\"]')?.innerText.includes('module-23.ts') && document.querySelector('nav[aria-label=\"当前位置\"] [aria-current=\"location\"]')?.textContent === '项目代码'") == "true",
            "最近文件定位完成：切回空间浏览，地图、详情面板和异步目录路径均已更新");
        Assert(await web.ExecuteScriptAsync("document.querySelector('nav[aria-label=\"当前位置\"]').innerText.includes('项目代码')") == "true",
            "定位进入文件所在目录");

        await web.ExecuteScriptAsync(open);
        await Wait(async () => await web.ExecuteScriptAsync($"{page}?.querySelectorAll('tbody tr').length === 26") == "true", "再次打开保留一天筛选");
        await web.ExecuteScriptAsync($"{page}.querySelector('input[type=\"checkbox\"]').click()");
        await Wait(async () => await web.ExecuteScriptAsync($"{page}?.querySelectorAll('tbody tr').length === 25") == "true", "仅当前目录排除上层影片");
        Assert(await web.ExecuteScriptAsync($"{page}.innerText.includes('影片, 测试.mp4')") == "false", "目录限定确实改变显示内容");
        await web.ExecuteScriptAsync($"{NavButton("资源地图")}.click()");
    }

    /// 只读查询、任务状态查询与取消不排写许可；实际写操作仍串行。同一任务编号复用结果、不重复执行，改了参数就拒绝。
    static async Task CheckReadsDuringDelete(WebView2 web, string folder)
    {
        // 零字节文件不占空间；建在夹具里，收尾时跟夹具一起删。
        string bulk = Path.Combine(folder, "批量删除");
        Directory.CreateDirectory(bulk);
        for (int i = 0; i < 3000; i++) File.Create(Path.Combine(bulk, $"f{i}")).Dispose();
        int scanId;
        using (var reply = await Call(web, "scan.start", new { path = folder, mode = "walk" }))
            scanId = Result(reply, "scan.start").GetProperty("scanId").GetInt32();
        await Wait(async () =>
        {
            using var reply = await Call(web, "scan.summary");
            return reply.RootElement.GetProperty("ok").GetBoolean() &&
                   reply.RootElement.GetProperty("result") is { ValueKind: JsonValueKind.Object } summary &&
                   summary.GetProperty("scanId").GetInt32() == scanId;
        }, "重新扫描完成");
        int bulkId;
        using (var reply = await Call(web, "search.query", new { query = "批量删除", metric = "alloc", limit = 10 }))
            bulkId = Result(reply, "search.query").GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("isDir").GetBoolean()).GetProperty("id").GetInt32();

        // 删除要带确认框发的令牌（DESIGN §9.2）：先预检拿令牌；不带令牌直接删必须被拒
        string token;
        using (var reply = await Call(web, "node.checkDelete", new { nodeIds = new[] { bulkId } }))
            token = Result(reply, "node.checkDelete").GetProperty("confirmationToken").GetString() ?? "";
        Assert(token.Length > 0, "删除预检发了确认令牌");
        using (var reply = await Call(web, "node.delete", new { nodeIds = new[] { bulkId }, permanent = true }))
            Assert(IsFriendlyError(reply, "确认") && Directory.Exists(bulk), "不带确认令牌的删除被拒绝，目录还在");
        string operationId = Guid.NewGuid().ToString("N");
        var request = new { nodeIds = new[] { bulkId }, permanent = true, confirmationToken = token, operationId };
        var replies = await Burst(web,
            ("node.delete", request),
            ("tree.view", new { nodeId = 0, metric = "alloc", maxNodes = 12000 }),
            ("scan.cancel", new { }), // 没有扫描在跑，取消是空操作，只用来看它还排不排在删除后面
            ("scan.issues", new { kind = "denied" }),
            ("tree.recent", new { }),
            ("tree.owners", new { }),
            ("tree.related", new { nodeId = 0 }),
            ("scan.savedSummaries", new { }),
            ("operation.status", new { operationId }));
        var (delete, view, cancel, issues) = (replies[0], replies[1], replies[2], replies[3]);
        Assert(replies[4..].All(r => r.Reply.GetProperty("ok").GetBoolean() && r.Order < delete.Order),
            "最近文件、所属对象聚合、关联详情、已保存的摘要、任务状态是只读查询，不排在删除后面");
        Assert(delete.Reply.GetProperty("ok").GetBoolean() && !Directory.Exists(bulk), "永久删除 3000 个文件的目录：" + delete.Reply);
        Assert(view.Reply.GetProperty("ok").GetBoolean(), "删除进行中的视图请求应答 ok");
        Assert(view.Order < delete.Order, $"删除进行中视图请求能及时返回，不排在删除后面（视图 {view.Ms:0} ms，删除 {delete.Ms:0} ms）");
        Assert(cancel.Reply.GetProperty("ok").GetBoolean() && cancel.Order < delete.Order, "取消扫描不再排在长删除之后");
        Assert(cancel.Reply.GetProperty("result").GetProperty("message").GetString() == "没有正在进行的扫描", "没有扫描可取消时如实说，不算失败");
        Assert(issues.Reply.GetProperty("ok").GetBoolean() && issues.Order < delete.Order, "异常清单是只读查询，不排在删除后面");
        Console.WriteLine($"删除进行中：视图 {view.Ms:0} ms 返回，删除 {delete.Ms:0} ms 返回");
        using (var reply = await Call(web, "operation.status", new { operationId }))
        {
            var status = Result(reply, "operation.status");
            Assert(status.GetProperty("status").GetString() == "completed" && status.GetProperty("completed").GetInt32() == 1
                && status.GetProperty("processedEntries").GetInt64() == 3001, "任务状态分开一个确认目标和 3001 个文件与目录，终态可查");
        }
        Directory.CreateDirectory(bulk);
        string sentinel = Path.Combine(bulk, "重新出现的文件.txt");
        File.WriteAllText(sentinel, "不在原确认清单内");
        using (var reply = await Call(web, "node.delete", request))
            Assert(reply.RootElement.GetProperty("ok").GetBoolean() && File.Exists(sentinel), "同一任务编号复用结果，不会再删同一路径下新出现的文件");
        using (var reply = await Call(web, "node.delete", new { nodeIds = new[] { bulkId }, permanent = false, confirmationToken = token, operationId }))
            Assert(IsFriendlyError(reply, "不同请求") && File.Exists(sentinel), "同一任务编号改了参数被拒绝");
    }

    /// 桥接层的运行中取消：第一条带进度的运行事件一到就发取消和状态查询；后台进入 cancelled，已删的不回滚，计数和磁盘对得上。
    static async Task CheckOperationCancellation(WebView2 web, string folder)
    {
        string target = Path.Combine(folder, "取消夹具");
        Directory.CreateDirectory(target);
        for (int i = 0; i < 4000; i++) File.WriteAllText(Path.Combine(target, $"item-{i}.txt"), "fixture");
        int scanId;
        using (var reply = await Call(web, "scan.start", new { path = folder, mode = "walk" }))
            scanId = Result(reply, "scan.start").GetProperty("scanId").GetInt32();
        await Wait(async () =>
        {
            using var reply = await Call(web, "scan.summary");
            return reply.RootElement.GetProperty("result") is { ValueKind: JsonValueKind.Object } summary && summary.GetProperty("scanId").GetInt32() == scanId;
        }, "取消夹具扫描完成");
        int nodeId;
        using (var reply = await Call(web, "search.query", new { query = "取消夹具", metric = "alloc", limit = 10 }))
            nodeId = Result(reply, "search.query").GetProperty("items")[0].GetProperty("id").GetInt32();
        string token;
        using (var reply = await Call(web, "node.checkDelete", new { nodeIds = new[] { nodeId } }))
            token = Result(reply, "node.checkDelete").GetProperty("confirmationToken").GetString()!;
        string operationId = Guid.NewGuid().ToString("N");
        int deleteId = NextId(), cancelId = NextId(), statusId = NextId();
        string request = JsonSerializer.Serialize(new { id = deleteId, method = "node.delete", @params = new { nodeIds = new[] { nodeId }, permanent = true, confirmationToken = token, operationId } }, DiskLens.Core.Json.Options);
        await web.ExecuteScriptAsync($$"""
            (() => {
              const request = {{request}}, result = window.__cancelFixture = { events: [], replies: {}, requested: false };
              window.chrome.webview.addEventListener('message', function handler(e) {
                const m = e.data;
                if (m.event === 'operation.progress' && m.data.operationId === request.params.operationId) {
                  result.events.push(m.data);
                  if (!result.requested && m.data.status === 'running' && m.data.processedEntries > 0) {
                    result.requested = true;
                    window.chrome.webview.postMessage({ id: {{cancelId}}, method: 'operation.cancel', params: { operationId: request.params.operationId } });
                    window.chrome.webview.postMessage({ id: {{statusId}}, method: 'operation.status', params: { operationId: request.params.operationId } });
                  }
                }
                if ([{{deleteId}}, {{cancelId}}, {{statusId}}].includes(m.id)) result.replies[m.id] = m;
                if (Object.keys(result.replies).length === 3) window.chrome.webview.removeEventListener('message', handler);
              });
              window.chrome.webview.postMessage(request);
            })()
            """);
        await Wait(async () => await web.ExecuteScriptAsync("Object.keys(window.__cancelFixture.replies).length === 3") == "true", "运行中取消、状态查询与执行结果均返回");
        using var observations = JsonDocument.Parse(await web.ExecuteScriptAsync("window.__cancelFixture"));
        var events = observations.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert(events.Any(e => e.GetProperty("status").GetString() == "running" && e.GetProperty("processedEntries").GetInt64() > 0), "真实删除中收到逐文件进度");
        Assert(events.Where(e => e.GetProperty("status").GetString() is "queued" or "running").Any(e => e.GetProperty("freedBytes").ValueKind == JsonValueKind.Null), "执行中释放量未知时为 null");
        using var statusReply = await Call(web, "operation.status", new { operationId });
        var status = Result(statusReply, "取消后的任务状态");
        Assert(status.GetProperty("status").GetString() == "cancelled", "取消后后台进入 cancelled 终态");
        int remaining = Directory.GetFiles(target).Length;
        Assert(remaining > 0 && remaining < 4000, "取消保留后续文件，同时不回滚已完成的删除");
        Assert(status.GetProperty("processedEntries").GetInt64() == 4000 - remaining, "部分删除的计数和实际剩余文件对得上");
        Assert(status.GetProperty("skipped").GetInt32() == 1 && status.GetProperty("completed").GetInt32() == 0, "没做完的目录目标计为跳过，不冒称完成");
        using (var reply = await Call(web, "operation.cancel", new { operationId }))
            Assert(Result(reply, "再次取消").GetProperty("status").GetString() == "cancelled", "终态重复取消不改变结果");
    }

    static void DeleteInSandbox(string sandbox, string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(sandbox) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝清理沙箱以外的路径：" + full);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    static void Assert(bool condition, string what)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(what);
    }

    static T Throws<T>(Action action) where T : Exception
    {
        _assertions++;
        try { action(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException($"期望抛出 {typeof(T).Name}，但调用成功了");
    }

    /// 应答必须 ok，返回 result。
    static JsonElement Result(JsonDocument reply, string what)
    {
        Assert(reply.RootElement.GetProperty("ok").GetBoolean(), $"{what} 应答 ok：{ErrorText(reply)}");
        return reply.RootElement.GetProperty("result");
    }

    static string ErrorText(JsonDocument reply) =>
        reply.RootElement.TryGetProperty("error", out var e) ? e.GetProperty("message").GetString() ?? "" : "";

    /// ok=false，消息是中文（含汉字、不含 .NET 异常类型名）；part 不为空时还要包含这段文字。
    static bool IsFriendlyError(JsonDocument reply, string? part)
    {
        if (reply.RootElement.GetProperty("ok").GetBoolean()) return false;
        string message = ErrorText(reply);
        bool chinese = message.Any(c => c is >= '\u4e00' and <= '\u9fff');
        return chinese && !message.Contains("Exception") && (part == null || message.Contains(part));
    }

    static async Task Wait(Func<Task<bool>> predicate, string what)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (await predicate()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(what);
    }

    static int NextId() => ++_requestId;

    static Task<JsonDocument> Call(WebView2 web, string method, object? parameters = null) =>
        Send(web, NextId(), id => new { id, method, @params = parameters ?? new { } });

    /// 经真实的 window.chrome.webview 发一条请求，等同 id 的应答回来。
    static async Task<JsonDocument> Send(WebView2 web, int id, Func<int, object> message)
    {
        string request = JsonSerializer.Serialize(message(id), DiskLens.Core.Json.Options);
        await web.ExecuteScriptAsync("window.__diskLensReply = null;" +
            "window.chrome.webview.addEventListener('message', function handler(e) {" +
            $"if(e.data.id !== {id}) return;" +
            "window.__diskLensReply = e.data; window.chrome.webview.removeEventListener('message', handler); });" +
            $"window.chrome.webview.postMessage({request});");
        await Wait(async () => await web.ExecuteScriptAsync("window.__diskLensReply !== null") == "true", request);
        return JsonDocument.Parse(await web.ExecuteScriptAsync("window.__diskLensReply"));
    }

    /// 同一段脚本里连发几条请求，按发出顺序返回每条应答的到达名次和从发出算起的毫秒数。
    static async Task<(int Order, double Ms, JsonElement Reply)[]> Burst(WebView2 web, params (string Method, object Params)[] calls)
    {
        int[] ids = calls.Select(_ => NextId()).ToArray();
        string requests = JsonSerializer.Serialize(calls.Select((c, i) => new { id = ids[i], method = c.Method, @params = c.Params }),
            DiskLens.Core.Json.Options);
        await web.ExecuteScriptAsync("(() => { const t0 = performance.now(), reqs = " + requests + ", got = window.__diskLensBurst = {};" +
            "window.chrome.webview.addEventListener('message', function handler(e) {" +
            "if (!reqs.some(r => r.id === e.data.id)) return;" +
            "got[e.data.id] = { order: Object.keys(got).length, ms: performance.now() - t0, reply: e.data };" +
            "if (Object.keys(got).length === reqs.length) window.chrome.webview.removeEventListener('message', handler); });" +
            "reqs.forEach(r => window.chrome.webview.postMessage(r)); })();");
        await Wait(async () => await web.ExecuteScriptAsync($"Object.keys(window.__diskLensBurst).length === {calls.Length}") == "true",
            "连发请求的应答");
        using var got = JsonDocument.Parse(await web.ExecuteScriptAsync("window.__diskLensBurst"));
        return ids.Select(id => got.RootElement.GetProperty(id.ToString()))
            .Select(x => (x.GetProperty("order").GetInt32(), x.GetProperty("ms").GetDouble(), x.GetProperty("reply").Clone())).ToArray();
    }
}
