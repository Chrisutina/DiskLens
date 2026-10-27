using DiskLens.Core;

namespace DiskLens.Check;

// Run 的校验与实删。树是内存里合成的，根放在 .sandbox/cleanup 里：就算校验被绕过，DeleteService 面对的也只有沙箱里的路径。
// command / openSettings 类的建议绝不拿来做 Run 测试——校验万一失守，它们会真的开终端、真的去清用户的缓存。
static partial class CleanupChecks
{
    static void Throws(CheckContext ctx, Action act, string why)
    {
        try { act(); ctx.Fail($"{why}：应该抛 ArgumentException，却什么都没发生"); }
        catch (ArgumentException ex) { ctx.True(ex.Message.Any(c => c > 0x2E80), $"{why}：错误消息要用中文，实际：{ex.Message}"); }
        catch (Exception ex) { ctx.Fail($"{why}：应该抛 ArgumentException，实际 {ex.GetType().Name}：{ex.Message}"); }
    }

    static void RunGuards(CheckContext ctx, string sandbox)
    {
        var l = new Layout(Path.Combine(sandbox, "guard"));
        var f = new Full(l);
        var sim = f.W.Sims.Single();
        var cc = l.Ctx();
        // 真文件：校验失手时它才会被误删
        string probe = l.Temp + @"\probe.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
        File.WriteAllText(probe, "x");

        SuggestionDto S(string id) => f.Hits.First(h => h.S.Id == id).S;
        void Try(string why, SuggestionDto s, CleanupRunRequest req) =>
            Throws(ctx, () => CleanupAdvisor.Run(sim.Tree, s, req, cc, CancellationToken.None, f.Env), why);

        var temp = S("user-temp");
        var browser = S("browser-cache");
        var build = S("build-outputs");
        Try("请求 ID 和建议不一致", temp, new("build-outputs"));
        Try("规则表里没有的建议", temp with { Id = "nope" }, new("nope"));
        Try("DTO 被改过：命令", temp with { Command = "calc.exe" }, new("user-temp"));
        Try("DTO 被改过：动作", temp with { Action = "deleteNodes" }, new("user-temp"));
        Try("DTO 被改过：设置入口", temp with { Target = "ms-settings:storagesense" }, new("user-temp"));
        Try("可勾选建议里夹了别的节点", build, new("build-outputs", [sim.Id(l.Temp)]));
        Try("不存在的节点", build, new("build-outputs", [987_654_321]));
        Try("整条建议里夹了别的节点", temp, new("user-temp", [sim.Id(l.Temp), sim.Id(l.Win + @"\Temp")]));
        Try("不可勾选的建议只选一部分", browser, new("browser-cache", [browser.NodeIds[0]]));
        Try("空数组不能当成整条", build, new("build-outputs", []));
        ctx.True(File.Exists(probe), "所有校验失败的调用都没有动过磁盘");
    }

    /// 真的跑一遍：沙箱里造文件，WalkScanner 扫出真树，Analyze → Run，只删该删的。
    static void RunExecutes(CheckContext ctx, string sandbox)
    {
        var l = new Layout(Path.Combine(sandbox, "exec-run"));
        string desk = l.Profile + @"\Desktop";
        void Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[16 * 1024]); // 太小的文件会住在 MFT 里，alloc 为 0，建议就被当成空的了
        }
        Write(l.Temp + @"\a.tmp"); Write(l.Temp + @"\sub\b.tmp");
        Write(desk + @"\rust1\Cargo.toml"); Write(desk + @"\rust1\target\debug\x.bin");
        Write(desk + @"\dotnet1\app.csproj"); Write(desk + @"\dotnet1\obj\o.bin");

        var tree = WalkScanner.Scan(l.TreeRoot, new ScanCounters(), CancellationToken.None);
        tree.Seal();
        var env = FakeEnv(l);
        var cc = l.Ctx();
        var list = CleanupAdvisor.Analyze(tree, cc, CancellationToken.None, env);
        var temp = list.FirstOrDefault(s => s.Id == "user-temp");
        var build = list.FirstOrDefault(s => s.Id == "build-outputs");
        ctx.True(temp != null && build != null, "真树上应该有 user-temp 和 build-outputs");
        if (temp == null || build == null) return;

        var result = CleanupAdvisor.Run(tree, temp, new("user-temp"), cc, CancellationToken.None, env);
        ctx.True(result.Ok, "清空 Temp：" + result.Message + string.Join("；", result.Errors.Select(e => e.Message)));
        ctx.True(Directory.Exists(l.Temp), "清空内容、保留目录本身");
        ctx.Equal(0, Directory.GetFileSystemEntries(l.Temp).Length, "Temp 里的东西都没了");

        // 只勾 target：obj 和标记文件都不能动。构建产物默认永久删除，所以不会碰回收站。
        int target = build.NodeIds.First(n => tree.GetPath(n).Equals(desk + @"\rust1\target", Ic));
        result = CleanupAdvisor.Run(tree, build, new("build-outputs", [target]), cc, CancellationToken.None, env);
        ctx.True(result.Ok, "删 target：" + string.Join("；", result.Errors.Select(e => e.Message)));
        ctx.True(!Directory.Exists(desk + @"\rust1\target"), "勾选的 target 被删了");
        ctx.True(Directory.Exists(desk + @"\dotnet1\obj") && File.Exists(desk + @"\rust1\Cargo.toml"), "没勾的 obj 和标记文件还在");

        result = CleanupAdvisor.Run(tree, build, new("build-outputs"), cc, CancellationToken.None, env);
        ctx.True(result.Ok, "整条删：" + string.Join("；", result.Errors.Select(e => e.Message)));
        ctx.True(!Directory.Exists(desk + @"\dotnet1\obj") && File.Exists(desk + @"\dotnet1\app.csproj"), "obj 被删，csproj 还在");
        ctx.True(CleanupAdvisor.Analyze(tree, cc, CancellationToken.None, env).All(s => s.Id is not ("user-temp" or "build-outputs")), "删完之后建议消失");
    }
}
