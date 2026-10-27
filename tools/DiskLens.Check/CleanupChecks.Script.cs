using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DiskLens.Core;

namespace DiskLens.Check;

// 清理脚本（DESIGN §6.2）的自检：文本内容、校验、恶意文件名，以及用真正的 PowerShell 做「只解析不执行」的语法检查。
// 唯一会执行脚本的 ScriptExecutes 只跑沙箱里造的假目录，验的是辅助函数「不跟随 junction」这件最要命的事。
static partial class CleanupChecks
{
    /// 和被测代码口径一致的 1024 进制格式化（前端 formatBytes 的 C# 版）。
    static string Fmt(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return (u == 0 || v >= 99.95 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture)) + " " + units[u];
    }

    static CleanupScriptDto Build(Full f, IEnumerable<CleanupSelection> picks) =>
        CleanupAdvisor.BuildScript(f.W.Sims.Single().Tree, f.Hits.Select(h => h.S).ToList(), new(picks.ToArray()), f.L.Ctx(), f.Env, FakeId);

    /// 合成树上的路径在磁盘上不存在：生成脚本时读到的文件身份一律给这一个，期望的命令行带上它的写法
    static readonly NativeFs.Identity Fake = new(0x1234ABCD, 0x0001000000002A2A, 0x01DC2A2A2A2A2A2A, 0x10);
    static NativeFs.Identity? FakeId(string _) => Fake;
    static readonly string Tok = CleanupAdvisor.IdentityToken(Fake);

    static void ScriptContent(CheckContext ctx, Full f)
    {
        var all = f.Hits.Select(h => h.S).ToList();
        var r = Build(f, all.Select(s => new CleanupSelection(s.Id)));
        string script = r.Script;
        var lines = script.Split("\r\n");

        ctx.Equal(all.Count, r.Items, "Items = 选中的建议条数");
        ctx.Equal(all.Sum(s => s.Bytes), r.Bytes, "Bytes = 各条合计");
        ctx.True(r.NeedsAdmin, "有标了需要管理员的段落");
        ctx.True(!Regex.IsMatch(script, "(?<!\r)\n|\r(?!\n)"), "只用 CRLF 换行");
        ctx.True(script.EndsWith("\r\n"), "以换行结尾");
        ctx.True(Regex.IsMatch(lines[0], @"^# DiskLens 清理脚本 · \d{4}-\d{2}-\d{2} \d{2}:\d{2} · " + all.Count + " 项 · 约 " + Regex.Escape(Fmt(r.Bytes)) + "$"), "首行格式：" + lines[0]);
        ctx.Equal("# 删除不进回收站，无法恢复。运行前请关闭浏览器、IDE 和相关程序。", lines[1], "第二行");
        ctx.Equal("# 用法：在 PowerShell 中粘贴运行；标了「需要管理员」的段落请在管理员 PowerShell 中运行。", lines[2], "第三行");
        ctx.Equal("$ErrorActionPreference = 'Continue'", lines[3], "第四行");
        ctx.Equal("$DiskLensScanStart = " + DateTimeOffset.FromUnixTimeMilliseconds(f.L.Ctx().ScanStartedAtMs).ToFileTime(), lines[5], "第六行：扫描开始的时刻（FILETIME）");

        foreach (var needle in new[] { "function Remove-DiskLensItem([string]$Path, [string]$Identity) {", "function Clear-DiskLensFolder([string]$Path, [string]$Identity) {",
            "[DiskLensScriptV2]::Remove($Path, $Identity, [long]$DiskLensScanStart)", "[DiskLensScriptV2]::Clear($Path, $Identity, [long]$DiskLensScanStart)",
            "basic.Created > start || basic.Written > start || basic.Changed > start", "生成脚本之后这个位置换成了别的东西",
            "SetFileInformationByHandle(handle, 21, ref flags, 4)", "Write-Warning" })
            ctx.True(script.Contains(needle), "辅助函数里应该有：" + needle);
        ctx.True(!script.Contains("-Recurse", Ic), "脚本里（连注释）都不许出现 -Recurse");
        ctx.True(!Regex.IsMatch(script, @"Directory\]?::Delete\([^)]*\$true"), "目录只能逐层持有句柄后删除，不能整树删除");
        ctx.True(!script.Contains("USERPROFILE", Ic), "路径一律写绝对路径：换个账号运行也删同一批东西");
        ctx.True(!Regex.IsMatch(script, @"Remove-Item[^\r\n]*-Rec", RegexOptions.IgnoreCase), "不存在带递归参数的 Remove-Item");

        // 各种动作的写法
        void Has(string line) => ctx.True(lines.Contains(line), "脚本里应有这一行：" + line);
        void Section(string title, params string[] body)
        {
            int at = Array.IndexOf(lines, title);
            ctx.True(at > 0, "应有段落标题：" + title);
            if (at <= 0) return;
            ctx.Equal("", lines[at - 1], "段落前空一行：" + title);
            for (int i = 0; i < body.Length; i++) ctx.Equal(body[i], at + 1 + i < lines.Length ? lines[at + 1 + i] : null, $"「{title}」第 {i + 1} 行");
        }
        Section($"# ── 临时文件 · {Fmt(3 * MB)} · ~\\AppData\\Local\\Temp", $"Clear-DiskLensFolder '{f.L.Temp}' '{Tok}'");
        Section($"# ── 系统临时文件（需要管理员） · {Fmt(2 * MB)} · Q:\\Windows\\Temp", $@"Clear-DiskLensFolder 'Q:\Windows\Temp' '{Tok}'");
        Section($"# ── 回收站 · {Fmt(10 * MB)} · Q:\\$Recycle.Bin", "Clear-RecycleBin -DriveLetter Q -Force");
        Section($"# ── npm 缓存 · {Fmt(12 * MB)} · ~\\AppData\\Local\\npm-cache", "npm cache clean --force");
        Section($"# ── 传递优化缓存（需要管理员） · {Fmt(1 * MB)} · Q:\\Windows\\ServiceProfiles\\NetworkService\\AppData\\Local\\Microsoft\\Windows\\DeliveryOptimization\\Cache",
            "Delete-DeliveryOptimizationCache -Force");
        Section($"# ── 休眠文件（需要管理员） · {Fmt(6 * GB)} · Q:\\hiberfil.sys", "powercfg /h off");
        Section($"# ── Windows 组件存储（需要管理员） · {Fmt(900 * MB)} · Q:\\Windows\\WinSxS", "Dism.exe /Online /Cleanup-Image /StartComponentCleanup");
        Has($@"Remove-DiskLensItem '{f.L.Local}\Microsoft\Windows\Explorer\thumbcache_256.db' '{Tok}'");
        Has("Start-Process 'ms-settings:storagesense'");
        Has("Start-Process 'SystemPropertiesPerformance.exe'");
        Has("Start-Process 'SystemPropertiesProtection.exe'");
        int installer = Array.FindIndex(lines, x => x.StartsWith("# ── Windows 安装缓存"));
        ctx.True(installer > 0 && lines[installer + 1].StartsWith("# ") && lines[installer + 2] is "" or null, "info 类只有注释");
        ctx.True(lines.Where(x => x.StartsWith("Start-Process")).All(x => lines[Array.IndexOf(lines, x) - 1].StartsWith("# ")), "每个 Start-Process 前面有一行注释说明");

        // 只选一部分：只写选中的节点，大小也只算选中的；空数组等于整条
        var modules = f.Hits.First(h => h.S.Id == "node-modules");
        int mid = modules.S.NodeIds.First(n => modules.Sim.PathOf(n).EndsWith(@"proj-mid\node_modules", Ic));
        var part = Build(f, [new CleanupSelection("node-modules", [mid])]);
        ctx.Equal(1, part.Items, "子集：一项");
        ctx.Equal(50 * MB, part.Bytes, "子集：只算选中的 50 MB");
        ctx.True(!part.NeedsAdmin, "子集：不需要管理员");
        ctx.Equal(1, part.Script.Split("\r\n").Count(x => x.StartsWith("Remove-DiskLensItem ")), "子集：只有一行删除");
        ctx.True(part.Script.Contains($@"'{f.L.Profile}\Desktop\proj-mid\node_modules'"), "子集：写的是选中的那个");
        ctx.Equal(3, Build(f, [new CleanupSelection("node-modules", [])]).Script.Split("\r\n").Count(x => x.StartsWith("Remove-DiskLensItem ")), "空数组 = 整条");

        var two = Build(f, [new CleanupSelection("user-temp"), new CleanupSelection("dev-caches-npm")]);
        ctx.True(!two.NeedsAdmin && two.Items == 2 && two.Bytes == 15 * MB, "两条不需要管理员的：NeedsAdmin=false，15 MB");
        var order = Build(f, [new CleanupSelection("dev-caches-npm"), new CleanupSelection("user-temp")]).Script;
        ctx.True(order.IndexOf("# ── npm 缓存", StringComparison.Ordinal) < order.IndexOf("# ── 临时文件", StringComparison.Ordinal), "段落按选中顺序排列");

        // 生成时的几道闸：不知道扫描时刻、卷不记 ChangeTime、条目已经不在、读不到身份——删除类条目只写注释，不算一项
        CleanupSelection[] mixed = [new("user-temp"), new("thumb-cache")];
        CleanupScriptDto With(CleanupContext c, Func<string, NativeFs.Identity?> id) =>
            CleanupAdvisor.BuildScript(f.W.Sims.Single().Tree, all, new(mixed), c, f.Env, id);
        static bool Deletes(CleanupScriptDto s) => s.Script.Split("\r\n").Any(x => x.StartsWith("Clear-DiskLensFolder ") || x.StartsWith("Remove-DiskLensItem "));
        var noTime = With(f.L.Ctx() with { ScanStartedAtMs = 0 }, FakeId);
        ctx.True(noTime.Items == 0 && !Deletes(noTime) && noTime.Script.Contains("不知道这次扫描是什么时候开始的"), "不知道扫描开始时刻：删除类条目全部只写注释");
        var exfat = With(f.L.Ctx() with { FileSystem = "exFAT" }, FakeId).Script.Split("\r\n");
        ctx.True(exfat.Any(x => x.StartsWith("# 已跳过：这个卷是 exFAT")) && !exfat.Any(x => x.StartsWith("Clear-DiskLensFolder "))
            && exfat.Any(x => x.StartsWith("Remove-DiskLensItem ") && x.EndsWith($"thumbcache_256.db' '{Tok}'")), "exFAT 不记 ChangeTime：目录不交给脚本，单个文件照写");
        var gone = With(f.L.Ctx(), _ => null);
        ctx.True(gone.Items == 0 && !Deletes(gone) && gone.Script.Contains("# 已跳过：生成脚本时它已经不在了"), "生成时已经不在：只写注释，不算一项");
        var denied = With(f.L.Ctx(), _ => throw new System.ComponentModel.Win32Exception(5)).Script;
        ctx.True(denied.Contains("读不到它的文件身份（没有权限，或受系统保护）"), "读不到文件身份：写明原因跳过");
    }

    static void ScriptValidation(CheckContext ctx, Full f)
    {
        var tree = f.W.Sims.Single().Tree;
        var all = f.Hits.Select(h => h.S).ToList();
        var cc = f.L.Ctx();
        void Try(string why, IReadOnlyList<SuggestionDto> list, params CleanupSelection[] sel) =>
            Throws(ctx, () => CleanupAdvisor.BuildScript(tree, list, new(sel), cc, f.Env), why);
        SuggestionDto S(string id) => all.First(s => s.Id == id);

        Try("没有勾选", all);
        Try("建议 ID 不在传入的列表里", all, new CleanupSelection("nope"));
        Try("NodeIds 不是该建议 NodeIds 的子集", all, new CleanupSelection("node-modules", [S("user-temp").NodeIds[0]]));
        Try("NodeIds 里有不存在的节点", all, new CleanupSelection("node-modules", [123_456_789]));
        Try("同一条建议选了两次", all, new CleanupSelection("user-temp"), new CleanupSelection("user-temp"));
        Try("命令不在白名单", all.Select(s => s.Id == "dev-caches-npm" ? s with { Command = "calc.exe" } : s).ToList(), new CleanupSelection("dev-caches-npm"));
        Try("命令缺失", all.Select(s => s.Id == "dev-caches-npm" ? s with { Command = null } : s).ToList(), new CleanupSelection("dev-caches-npm"));
        Try("设置入口不在白名单", all.Select(s => s.Id == "windows-old" ? s with { Target = "calc.exe" } : s).ToList(), new CleanupSelection("windows-old"));
        Try("未知的动作", all.Select(s => s.Id == "user-temp" ? s with { Action = "format" } : s).ToList(), new CleanupSelection("user-temp"));
        // 传进来的列表里没有的建议，即使 Analyze 能产生也不行：脚本只信调用方给的这份
        Try("列表里没有这一条", all.Where(s => s.Id != "user-temp").ToList(), new CleanupSelection("user-temp"));
        Throws(ctx, () => CleanupAdvisor.BuildScript(tree, all, new(null!), cc, f.Env), "Selections 为 null");
    }

    // ---------------- 恶意文件名 ----------------

    sealed record Hostile(string Script, string[] Safe, int Unsafe, string[] GuardedPaths, string[] Offered, Layout L);

    /// 把怪名字当成项目目录名放进 build-outputs。名字都不是 Windows 能创建的全集，但树是合成的，生成器不能指望名字规矩。
    /// Analyze 自己会筛掉被 Protection 拦的；脚本生成器那一层要独立兜底，所以脚本是用手工拼的建议（带上全部节点）生成的。
    static Hostile HostileScript()
    {
        var l = new Layout(@"Q:\");
        string[] safe =
        [
            "it's",
            "Bob’s ‘quoted’ ‚low‛ files",
            "中文 目录 空格",
            "x $env_USERPROFILE $(calc) ${x} `n `\"q", // 冒号不放：Windows 名字里本来就不许有，Protection 会按「路径无效」拦掉
            "a;b&c|d(e)f{g}h[i]#j@k,l!m%n^o~p=q+",
            // D2 收窄了规则：只有卷根下第一段 `$` 开头才算系统元数据，桌面深处的 `$` 名字是普通目录，照样要转义写对
            "$env_USERPROFILE $(calc) ${x} `n `\"q",
        ];
        string[] bad = ["evil\nStart-Process calc", "bell" + (char)7 + "x", "sep" + (char)0x2028 + "x"];
        string[] guarded = [".ssh"]; // .ssh：Protection 的密钥目录规则
        string Target(string name) => l.Profile + @"\Desktop\" + name + @"\target";
        var w = new World(l.TreeRoot);
        foreach (var name in safe.Concat(bad).Concat(guarded))
        {
            w.File(l.Profile + @"\Desktop\" + name + @"\Cargo.toml", KB);
            w.File(Target(name) + @"\x", 5 * MB);
        }
        var env = FakeEnv(l);
        var h = w.Analyze(env).Single(x => x.S.Id == "build-outputs");
        var forged = h.S with { NodeIds = safe.Concat(bad).Concat(guarded).Select(n => h.Sim.Id(Target(n))).ToArray() };
        var script = CleanupAdvisor.BuildScript(h.Sim.Tree, [forged], new([new CleanupSelection("build-outputs")]), l.Ctx(), env, FakeId);
        return new(script.Script, safe.Select(Target).ToArray(), bad.Length, guarded.Select(Target).ToArray(), h.Paths, l);
    }

    static void ScriptHostileNames(CheckContext ctx)
    {
        var h = HostileScript();
        var lines = h.Script.Split("\r\n");
        ctx.True(h.GuardedPaths.All(p => !h.Offered.Contains(p)), "Analyze 不把会被 Protection 拦下的条目摆出来");
        ctx.Equal(h.Unsafe, lines.Count(x => x.StartsWith("# 已跳过一个路径含换行或控制字符的条目")), "含换行/控制字符的条目跳过并在脚本里注释原因");
        ctx.Equal(h.GuardedPaths.Length, lines.Count(x => x.StartsWith("# 已跳过受保护的条目（")), "受保护的条目跳过并写明原因");
        ctx.Equal(h.Safe.Length, lines.Count(x => x.StartsWith("Remove-DiskLensItem ")), "其余条目都有一行删除命令");
        ctx.True(lines.All(x => !x.StartsWith("Start-Process calc") && !x.Contains("\n") && !x.Contains((char)7) && !x.Contains((char)0x2028)), "名字里的换行没有逃出注释");
        ctx.True(lines.Where(x => x.StartsWith("Remove-DiskLensItem ")).All(x => x.StartsWith($"Remove-DiskLensItem '{h.L.Profile}\\") && x.EndsWith("'")), "删除命令格式规整");
        ctx.True(h.Script.Contains(@"\Desktop\it''s\target'"), "单引号写成两个");
        ctx.True(h.Script.Contains("Bob’’s ‘‘quoted’’ ‚‚low‛‛ files"), "弯引号也要成对写：PowerShell 把它们当单引号");
    }

    /// TEMP 被设成家目录这种怪配置：Run 有 Protection 兜底，脚本更不能把它原样写出去。
    static void ScriptProtection(CheckContext ctx)
    {
        var l = new Layout(@"Q:\");
        var w = new World(l.TreeRoot);
        w.File(l.Profile + @"\Desktop\a.bin", 3 * MB);
        var broken = FakeEnv(l, new() { ["TEMP"] = l.Profile });
        var hits = w.Analyze(broken);
        Absent(ctx, hits, "user-temp", "TEMP 指向用户配置文件根目录时 Analyze 不提供清理");
        var sim = hits.Count > 0 ? hits[0].Sim : w.Sims.Single();
        var forged = new SuggestionDto("user-temp", "safe", "临时文件", "测试", 3 * MB, 1, "deleteContents", null, null, false, true, false, [sim.Id(l.Profile)], [], null);
        var script = CleanupAdvisor.BuildScript(sim.Tree, [forged], new([new CleanupSelection("user-temp")]), l.Ctx(), broken, FakeId);
        ctx.True(!script.Script.Contains("Clear-DiskLensFolder (") && !script.Script.Split("\r\n").Any(x => x.StartsWith("Clear-DiskLensFolder ")), "脚本里没有清空用户配置文件的命令");
        ctx.True(script.Script.Contains("# 已跳过受保护的条目（"), "脚本里写明了跳过的原因");
        ctx.Equal(0, script.Items, "整条都被跳过就不算一项");
        ctx.Equal(0L, script.Bytes, "也不计入大小");

        // 反过来：直接扫 Temp 目录本身时，建议的节点就是扫描根，DeleteService 允许把它当目录清空，脚本也要写
        var root = new World(l.Temp);
        root.File(l.Temp + @"\a.tmp", 3 * MB);
        var rootHits = root.Analyze(FakeEnv(l));
        if (Get(ctx, rootHits, "user-temp", "扫描根就是 Temp") is { } t)
        {
            ctx.Equal(FileTree.RootId, t.S.NodeIds.Single(), "节点就是扫描根");
            var s = CleanupAdvisor.BuildScript(t.Sim.Tree, [t.S], new([new CleanupSelection("user-temp")]), l.Ctx(), FakeEnv(l), FakeId);
            ctx.True(s.Script.Contains($"Clear-DiskLensFolder '{l.Temp}'"), "扫描根上的 deleteContents 照常写进脚本");
        }
    }

    // ---------------- 真 PowerShell：只解析不执行 ----------------

    const string ParseChecker = """
        param([string]$Script, [string]$Out)
        $text = [IO.File]::ReadAllText($Script, [Text.Encoding]::UTF8)
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)
        $lines = New-Object System.Collections.Generic.List[string]
        foreach ($e in $errors) { $lines.Add("ERR`t" + $e.Message) }
        foreach ($d in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) { $lines.Add("DEF`t" + $d.Name) }
        foreach ($c in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.CommandAst] }, $true)) {
            $name = $c.GetCommandName()
            if ($name -in 'Remove-Item', 'ri', 'rm', 'del', 'erase', 'rd', 'rmdir') {
                foreach ($el in $c.CommandElements) {
                    if ($el -is [System.Management.Automation.Language.CommandParameterAst] -and 'Recurse'.StartsWith($el.ParameterName, [StringComparison]::OrdinalIgnoreCase)) { $lines.Add("RECURSE`t" + $c.Extent.Text) }
                }
            }
            if ($name -eq 'Clear-DiskLensFolder' -or $name -eq 'Remove-DiskLensItem') {
                $arg = $c.CommandElements[1]
                if ($arg -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $lines.Add("LIT`t" + $arg.Value) }
                elseif ($arg -is [System.Management.Automation.Language.ParenExpressionAst]) {
                    $inner = $arg.Pipeline.PipelineElements[0]
                    if ($inner.GetCommandName() -eq 'Join-Path' -and $inner.CommandElements[1].VariablePath.UserPath -eq 'env:USERPROFILE') { $lines.Add("USER`t" + $inner.CommandElements[2].Value) }
                    else { $lines.Add("BAD`t" + $arg.Extent.Text) }
                }
            }
        }
        [IO.File]::WriteAllLines($Out, $lines, (New-Object System.Text.UTF8Encoding($false)))
        """;

    static List<(string Name, string Exe)>? _engines;

    static List<(string Name, string Exe)> Engines(CheckContext ctx)
    {
        if (_engines != null) return _engines;
        var list = new List<(string, string)>();
        string ps5 = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (File.Exists(ps5)) list.Add(("Windows PowerShell 5.1", ps5));
        else ctx.Info("没有 powershell.exe，跳过 5.1 的检查");
        string? pwsh = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), "pwsh.exe")).FirstOrDefault(File.Exists);
        if (pwsh != null && Exec(new ProcessStartInfo(pwsh, "-NoProfile -NonInteractive -Command exit 0")).Exit == 0) list.Add(("PowerShell 7", pwsh));
        else ctx.Info("没装 pwsh（或别名不可用），只检查 Windows PowerShell 5.1");
        return _engines = list;
    }

    static (int Exit, string Output) Exec(ProcessStartInfo psi, int timeoutMs = 120_000)
    {
        psi.UseShellExecute = false; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { p.Kill(true); return (-1, "超时"); }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    static ProcessStartInfo PowerShell(string exe, string file, params string[] args)
    {
        var psi = new ProcessStartInfo(exe);
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }.Concat(args)) psi.ArgumentList.Add(a);
        return psi;
    }

    /// 期望的字面量：一律 LIT\t<完整路径>（不再有 $env:USERPROFILE 写法）。
    static string Encoded(string path) => "LIT\t" + path;

    static void ParseOnly(CheckContext ctx, string dir, string engineName, string exe, string name, string script, IEnumerable<string> expected)
    {
        string file = Path.Combine(dir, name + ".ps1"), output = Path.Combine(dir, name + ".out");
        File.WriteAllText(file, script, new UTF8Encoding(true));
        File.Delete(output);
        var (exit, text) = Exec(PowerShell(exe, Path.Combine(dir, "parse.ps1"), file, output));
        ctx.True(File.Exists(output), $"{engineName}：{name} 解析器没有产出（退出码 {exit}）：{text}");
        if (!File.Exists(output)) return;
        var result = File.ReadAllLines(output, Encoding.UTF8);
        ctx.Equal("", string.Join(" / ", result.Where(x => x.StartsWith("ERR\t"))), $"{engineName}：{name} 零解析错误");
        ctx.Equal("", string.Join(" / ", result.Where(x => x.StartsWith("RECURSE\t") || x.StartsWith("BAD\t"))), $"{engineName}：{name} 没有带递归参数的 Remove-Item");
        ctx.Equal("Clear-DiskLensFolder,Remove-DiskLensItem",
            string.Join(",", result.Where(x => x.StartsWith("DEF\t")).Select(x => x[4..]).OrderBy(x => x, StringComparer.Ordinal)), $"{engineName}：{name} 两个辅助函数");
        ctx.Equal(Set(expected), Set(result.Where(x => x.StartsWith("LIT\t") || x.StartsWith("USER\t"))), $"{engineName}：{name} 的路径字面量解析回来和树里的路径一致");
    }

    static void ScriptParses(CheckContext ctx, Full f, string sandbox)
    {
        string dir = Path.Combine(sandbox, "ps");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "parse.ps1"), ParseChecker, new UTF8Encoding(true));

        var all = f.Hits.Select(h => h.S).ToList();
        string full = Build(f, all.Select(s => new CleanupSelection(s.Id))).Script;
        var tree = f.W.Sims.Single().Tree;
        var fullExpected = all.Where(s => s.Action is "deleteContents" or "deleteNodes").SelectMany(s => s.NodeIds).Select(n => Encoded(tree.GetPath(n))).ToList();
        var hostile = HostileScript();
        var hostileExpected = hostile.Safe.Select(Encoded).ToList();

        foreach (var (engine, exe) in Engines(ctx))
        {
            ParseOnly(ctx, dir, engine, exe, "full", full, fullExpected);
            ParseOnly(ctx, dir, engine, exe, "hostile", hostile.Script, hostileExpected);
        }
        ctx.Info($"语法检查（只解析不执行）：{string.Join("、", Engines(ctx).Select(e => e.Name))}；脚本 {full.Length} 字符 / 恶意名字脚本 {hostile.Script.Length} 字符");
    }

    // ---------------- 真 PowerShell：沙箱里实跑辅助函数 ----------------

    /// 警告逐条写进文件再数：控制台输出会按宽度折行，Windows PowerShell 5.1 折行时还会往中间插「警告:」前缀，按文字数不可靠
    const string RunWrapper = """
        param([string]$Script, [string]$Warnings)
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $found = New-Object System.Collections.Generic.List[string]
        & $Script 3>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.WarningRecord]) { $found.Add($_.Message); 'WARNING: ' + $_.Message } else { $_ }
        }
        [IO.File]::WriteAllLines($Warnings, $found, (New-Object System.Text.UTF8Encoding($false)))
        """;

    static void ScriptExecutes(CheckContext ctx, string sandbox)
    {
        string psDir = Path.Combine(sandbox, "ps");
        Directory.CreateDirectory(psDir);
        File.WriteAllText(Path.Combine(psDir, "wrapper.ps1"), RunWrapper, new UTF8Encoding(true));
        int index = 0;
        foreach (var (engine, exe) in Engines(ctx))
        {
            string root = Path.Combine(sandbox, "run-" + index++);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            string fileSystem = Volumes.GetFileSystem(root);
            // 每次生成都当作「刚扫完」：之前造好的算扫描时就有的，之后再动的都算扫描之后的。
            // 毫秒是截掉的，同一毫秒里刚造的文件时间会比它晚一点，所以往后让 1 ms
            CleanupContext Now() => new(false, root, root, IntPtr.Zero, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1, fileSystem);
            string decoy = Path.Combine(sandbox, "decoy-" + index);
            (int Code, string Output, string[] Warnings) Run(string name, string text)
            {
                string file = Path.Combine(psDir, $"{name}-{index}.ps1"), warnings = Path.Combine(psDir, $"{name}-{index}.warnings");
                File.WriteAllText(file, text, new UTF8Encoding(true));
                File.Delete(warnings);
                var psi = PowerShell(exe, Path.Combine(psDir, "wrapper.ps1"), file, warnings);
                // 每次实跑都把 USERPROFILE 指向沙箱里的 decoy：生成器哪天退回 $env:USERPROFILE 写法，也只会删到沙箱里
                psi.Environment["USERPROFILE"] = decoy;
                var (code, output) = Exec(psi);
                return (code, output, File.Exists(warnings) ? File.ReadAllLines(warnings, Encoding.UTF8) : ["（没有写出警告清单）"]);
            }
            void Make(string rel)
            {
                var p = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, "x");
            }
            void Junction(string link, string target)
            {
                var psi = new ProcessStartInfo("cmd.exe") { Arguments = $"/c mklink /J \"{Path.Combine(root, link)}\" \"{Path.Combine(root, target)}\"" };
                var (exit, text) = Exec(psi);
                ctx.Equal(0, exit, $"造 junction {link}：{text}");
            }
            string weird = "weird it's ’q’ 中文 $x [1]";
            Make(@"victim\plain.txt"); Make(@"victim\ro.txt"); File.SetAttributes(Path.Combine(root, @"victim\ro.txt"), FileAttributes.ReadOnly);
            Make(@"victim\sub\inner.txt"); Make(@"keep\keep.txt"); Junction(@"victim\sub\link", "keep");
            Make(@"contents\c1.txt"); Make(@"contents\csub\c2.txt"); Make(@"keep2\k2.txt"); Junction(@"contents\cjunc", "keep2");
            Make("file-only.txt"); Make(@"keep3\k3.txt"); Junction("linkonly", "keep3");
            Make(weird + @"\a.txt");
            // 超过 260 个字符的深路径（pnpm 的 node_modules 常见）：造的时候要加 \\?\ 前缀，脚本里不加
            string deepTop = Path.Combine(root, "deep"), deepName = string.Join(@"\", Enumerable.Repeat("a_very_long_directory_name_for_testing_1234567890", 8));
            Directory.CreateDirectory(@"\\?\" + Path.Combine(deepTop, deepName));
            File.WriteAllText(@"\\?\" + Path.Combine(deepTop, deepName, "f.txt"), "x");

            var sim = new Sim(root);
            sim.File(Path.Combine(root, @"victim\plain.txt")); sim.Dir(Path.Combine(root, "contents"));
            sim.File(Path.Combine(root, "file-only.txt")); sim.Dir(Path.Combine(root, "linkonly"), NodeFlags.Reparse); sim.File(Path.Combine(root, weird, "a.txt"));
            sim.Dir(Path.Combine(root, "deep"));
            sim.Seal();
            SuggestionDto FakeOn(Sim s, string id, string action, params string[] rel) =>
                new(id, "caution", id, "测试用", 1, rel.Length, action, null, null, false, true, true, rel.Select(r => s.Id(Path.Combine(root, r))).ToArray(), [], null);
            SuggestionDto Fake(string id, string action, params string[] rel) => FakeOn(sim, id, action, rel);
            var list = new List<SuggestionDto> { Fake("exec-nodes", "deleteNodes", "victim", "file-only.txt", "linkonly", weird, "deep"), Fake("exec-contents", "deleteContents", "contents") };
            var env = FakeEnv(new Layout(root), new() { ["USERPROFILE"] = root });
            var built = CleanupAdvisor.BuildScript(sim.Tree, list, new([new("exec-nodes"), new("exec-contents")]), Now(), env);
            ctx.True(!built.Script.Contains("USERPROFILE", Ic) && built.Script.Contains($"Remove-DiskLensItem '{Path.Combine(root, "victim")}' '"), "实跑脚本写的是扫描时的绝对路径，带着文件身份");

            // 扫描时的用户目录是 root；换个账号跑脚本，USERPROFILE 指向的是 decoy。脚本照样只删 root 下的，decoy 一个不能少
            if (Directory.Exists(decoy)) Directory.Delete(decoy, true);
            string[] decoyFiles = [@"victim\plain.txt", @"contents\c1.txt", "file-only.txt"];
            foreach (var rel in decoyFiles)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(decoy, rel))!);
                File.WriteAllText(Path.Combine(decoy, rel), "decoy");
            }

            var (code, output, warned) = Run("exec", built.Script);
            // pwsh 会往 USERPROFILE 里写自己的启动缓存，所以只点名查这三个
            ctx.True(decoyFiles.All(rel => File.Exists(Path.Combine(decoy, rel))), $"{engine}：换账号运行，另一个账号下的同名目录一个没动；输出：{output}");
            ctx.Equal(0, code, $"{engine}：脚本退出码；输出：{output}");
            ctx.True(!Directory.Exists(Path.Combine(root, "victim")), $"{engine}：普通目录（含只读文件、嵌套 junction）被删干净；输出：{output}");
            ctx.True(File.Exists(Path.Combine(root, @"keep\keep.txt")), $"{engine}：目录里的 junction 只删链接，目标 keep 里的文件还在");
            ctx.True(!File.Exists(Path.Combine(root, "file-only.txt")), $"{engine}：单个文件被删");
            ctx.True(!Directory.Exists(Path.Combine(root, "linkonly")) && File.Exists(Path.Combine(root, @"keep3\k3.txt")), $"{engine}：直接点名的 junction 只删链接，目标还在");
            ctx.True(!Directory.Exists(@"\\?\" + deepTop), $"{engine}：超过 260 字符的深目录被删；输出：{output}");
            ctx.True(!Directory.Exists(Path.Combine(root, weird)), $"{engine}：怪名字（单引号、弯引号、中文、$、方括号）的目录被删");
            ctx.True(Directory.Exists(Path.Combine(root, "contents")) && Directory.GetFileSystemEntries(Path.Combine(root, "contents")).Length == 0, $"{engine}：清空内容、保留目录本身");
            ctx.True(File.Exists(Path.Combine(root, @"keep2\k2.txt")), $"{engine}：被清空目录里的 junction 只删链接，目标 keep2 还在");
            ctx.True(warned.Length == 0, $"{engine}：一路没有警告；警告：{string.Join(" / ", warned)}；输出：{output}");

            // 不存在的路径、被占用的文件都只警告、不中断：再跑一遍同一份脚本，目标全没了
            var (again, outAgain, warnedAgain) = Run("exec", built.Script);
            ctx.Equal(0, again, $"{engine}：重复运行的退出码");
            ctx.True(warnedAgain.Any(w => w.StartsWith("已跳过 ") && w.EndsWith("已经不在了")), $"{engine}：目标已不存在时给警告而不是崩溃；输出：{outAgain}");

            string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
            // 在同一进程里改名成功才能证明失败路径释放了已拿到的父链句柄。
            string releaseProbe = $"\r\n$ErrorActionPreference = 'Stop'\r\n[IO.Directory]::Move({PsQuote(root)}, {PsQuote(root + "-released")})\r\n" +
                $"[IO.Directory]::Move({PsQuote(root + "-released")}, {PsQuote(root)})\r\nWrite-Output 'guards-released'\r\n";

            // 扫描之后、跑脚本之前，要清空的目录自己、或者要删的条目的上一级被换成了 junction：
            // 脚本得整条跳过，链接指向的地方（扫描范围以外）一个文件都不能少
            var swapSim = new Sim(root);
            swapSim.File(Path.Combine(root, @"swapped\s.txt")); swapSim.File(Path.Combine(root, @"anc\child.txt"));
            swapSim.Seal();
            Make(@"keep4\k4.txt"); Junction("swapped", "keep4");
            Make(@"keep5\child.txt"); Junction("anc", "keep5");
            var swap = CleanupAdvisor.BuildScript(swapSim.Tree,
                [FakeOn(swapSim, "swap-contents", "deleteContents", "swapped"), FakeOn(swapSim, "swap-nodes", "deleteNodes", @"anc\child.txt")],
                new([new("swap-contents"), new("swap-nodes")]), Now(), env);
            var (swapCode, swapOut, swapWarned) = Run("swap", swap.Script + releaseProbe);
            ctx.Equal(0, swapCode, $"{engine}：链接替换场景的退出码；输出：{swapOut}");
            ctx.True(File.Exists(Path.Combine(root, @"keep4\k4.txt")), $"{engine}：要清空的目录被换成 junction，链接指向的目录没被清空；输出：{swapOut}");
            ctx.True(File.Exists(Path.Combine(root, @"keep5\child.txt")), $"{engine}：上一级被换成 junction，没有穿过链接删到别处；输出：{swapOut}");
            ctx.Equal(2, swapWarned.Count(w => w.Contains("是链接")), $"{engine}：两处都写明了跳过的原因；警告：{string.Join(" / ", swapWarned)}");
            ctx.True(Directory.Exists(root) && !Directory.Exists(root + "-released") && swapOut.Contains("guards-released"), $"{engine}：拒绝链接后父链守卫释放；输出：{swapOut}");

            // 在真正枚举之前插入竞争者：同时尝试改名当前目录和祖先。此时必须仍持有句柄，不能靠重复检查碰运气。
            Make(@"race-clear\nested\a.txt"); Make(@"race-remove\nested\b.txt");
            var raceSim = new Sim(root);
            raceSim.File(Path.Combine(root, @"race-clear\nested\a.txt"));
            raceSim.File(Path.Combine(root, @"race-remove\nested\b.txt"));
            raceSim.Seal();
            var race = CleanupAdvisor.BuildScript(raceSim.Tree,
                [FakeOn(raceSim, "race-clear", "deleteContents", "race-clear"), FakeOn(raceSim, "race-remove", "deleteNodes", "race-remove")],
                new([new("race-clear"), new("race-remove")]), Now(), env);
            const string raceProbe = """
                $script:guardChecks = 0
                [DiskLensScriptV2]::BeforeList = [Action[string]]{
                    param($Path)
                    foreach ($candidate in @($Path, [IO.Path]::GetDirectoryName($Path))) {
                        $moved = $false
                        try { [IO.Directory]::Move($candidate, $candidate + '-race'); $moved = $true }
                        catch {
                            $code = $_.Exception.GetBaseException().HResult -band 0xffff
                            if ($code -ne 32 -and $code -ne 5) { throw }
                            $script:guardChecks++
                        }
                        if ($moved) {
                            [IO.Directory]::Move($candidate + '-race', $candidate)
                            throw '目录在枚举期间仍能被替换'
                        }
                    }
                }
                """;
            // 钩子挂在辅助函数之后、第一段正文之前
            int body = race.Script.IndexOf("\r\n\r\n# ── ", StringComparison.Ordinal);
            string raceMarker = Path.Combine(psDir, $"race-{index}.out");
            string raceText = race.Script.Insert(body, "\r\n" + raceProbe.Replace("\r\n", "\n").Replace("\n", "\r\n"))
                + releaseProbe + $"[IO.File]::WriteAllText({PsQuote(raceMarker)}, [string]$script:guardChecks)\r\n";
            File.Delete(raceMarker);
            var (raceCode, raceOut, raceWarned) = Run("race", raceText);
            ctx.Equal(0, raceCode, $"{engine}：运行中替换场景退出码；输出：{raceOut}");
            ctx.Equal("8", File.Exists(raceMarker) ? File.ReadAllText(raceMarker) : "未写结果", $"{engine}：清空/删除目标及嵌套子目录枚举时，目标和祖先的 8 次替换都被阻止；输出：{raceOut}");
            ctx.True(Directory.Exists(Path.Combine(root, "race-clear")) && !Directory.EnumerateFileSystemEntries(Path.Combine(root, "race-clear")).Any()
                && !Directory.Exists(Path.Combine(root, "race-remove")), $"{engine}：竞争被阻止后仍完成清空/删除；输出：{raceOut}");
            ctx.True(raceWarned.Length == 0 && !raceOut.Contains("Exception"), $"{engine}：成功后所有守卫释放，可改名祖先；警告：{string.Join(" / ", raceWarned)}；输出：{raceOut}");

            // 生成脚本之后才出现的东西：新建的文件、新建的 .ssh 私钥、从别处挪进来的旧目录、改过的旧文件，一个都不能删；
            // 原样留着的旧东西照删。目标被换成同名新文件的，整个跳过
            Make(@"late-clear\old.txt"); Make(@"late-clear\old-sub\o.txt"); Make(@"late-clear\keep-me\edited.txt");
            Make(@"late-remove\old.txt"); Make("late-file.txt"); Make(@"outside\moved\m.txt");
            var lateSim = new Sim(root);
            foreach (string rel in new[] { @"late-clear\old.txt", @"late-clear\old-sub\o.txt", @"late-clear\keep-me\edited.txt", @"late-remove\old.txt", "late-file.txt" })
                lateSim.File(Path.Combine(root, rel));
            lateSim.Seal();
            var late = CleanupAdvisor.BuildScript(lateSim.Tree,
                [FakeOn(lateSim, "late-clear", "deleteContents", "late-clear"), FakeOn(lateSim, "late-remove", "deleteNodes", "late-remove", "late-file.txt")],
                new([new("late-clear"), new("late-remove")]), Now(), env);
            Thread.Sleep(100); // 文件时间按时钟滴答取整：隔开一点，下面这些改动一定晚于「扫描开始」
            Make(@"late-clear\new.txt"); Make(@"late-clear\.ssh\id_rsa"); Make(@"late-remove\.ssh\id_rsa");
            File.AppendAllText(Path.Combine(root, @"late-clear\keep-me\edited.txt"), "changed");
            Directory.Move(Path.Combine(root, @"outside\moved"), Path.Combine(root, @"late-remove\moved"));
            File.Delete(Path.Combine(root, "late-file.txt"));
            Make("late-file.txt");
            var (lateCode, lateOut, lateWarned) = Run("late", late.Script);
            string Late(string rel) => Path.Combine(root, rel);
            string lateWhy = $"警告：{string.Join(" / ", lateWarned)}；输出：{lateOut}";
            ctx.Equal(0, lateCode, $"{engine}：生成后又有变化的场景退出码；{lateWhy}");
            ctx.True(!File.Exists(Late(@"late-clear\old.txt")) && !Directory.Exists(Late(@"late-clear\old-sub")) && !File.Exists(Late(@"late-remove\old.txt")),
                $"{engine}：扫描时就在、之后没动过的照删；{lateWhy}");
            ctx.True(File.Exists(Late(@"late-clear\new.txt")) && File.Exists(Late(@"late-clear\.ssh\id_rsa")) && File.Exists(Late(@"late-remove\.ssh\id_rsa")),
                $"{engine}：生成脚本之后新建的文件、私钥都留着；{lateWhy}");
            ctx.True(File.Exists(Late(@"late-clear\keep-me\edited.txt")), $"{engine}：扫描之后改过的文件留着；{lateWhy}");
            ctx.True(File.Exists(Late(@"late-remove\moved\m.txt")), $"{engine}：扫描之后挪进来的旧目录整个留着，里面的旧文件也不动；{lateWhy}");
            ctx.True(File.Exists(Late("late-file.txt")) && lateWarned.Any(w => w.Contains("late-file.txt") && w.Contains("换成了别的东西")),
                $"{engine}：目标被换成同名新文件：不删，写明原因；{lateWhy}");
            ctx.True(lateWarned.Count(w => w.Contains("密钥目录")) == 2 && lateWarned.Any(w => w.StartsWith("已保留 ")),
                $"{engine}：私钥目录单独点名，其余新东西汇总成一句「已保留」；{lateWhy}");
        }
    }
}
