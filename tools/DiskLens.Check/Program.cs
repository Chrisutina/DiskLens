using System.Text;
using DiskLens.Check;

// 自检入口：dotnet run --project tools/DiskLens.Check -- <area|all> [选项]
Console.OutputEncoding = Encoding.UTF8;

var areas = new Dictionary<string, Action<CheckContext>>(StringComparer.OrdinalIgnoreCase)
{
    ["tree"] = TreeChecks.Run,
    ["mft"] = MftChecks.Run,
    ["walk"] = WalkChecks.Run,
    ["search"] = SearchChecks.Run,
    ["queries"] = QueryChecks.Run,
    ["purpose"] = PurposeChecks.Run,
    ["cleanup"] = CleanupChecks.Run,
    ["delete"] = DeleteChecks.Run,
    ["service"] = ServiceChecks.Run,
    ["summaries"] = SummaryChecks.Run,
    ["watch"] = WatchChecks.Run,
    ["i18n"] = LanguageChecks.Run,
};

if (args.Length == 0 || (args[0] != "all" && !areas.ContainsKey(args[0])))
{
    Console.WriteLine($"用法：DiskLens.Check <{string.Join('|', areas.Keys)}|all> [--选项 值]");
    return 2;
}

var targets = args[0] == "all" ? areas.Keys.ToList() : [args[0]];
int failures = 0;
foreach (var name in targets)
{
    Console.WriteLine($"[{name}]");
    var ctx = new CheckContext(name, args[1..]);
    try { areas[name](ctx); }
    catch (Exception ex) { ctx.Fail($"未捕获的异常：{ex}"); }
    failures += ctx.Failures;
    Console.WriteLine($"[{name}] {(ctx.Failures == 0 ? "通过" : $"失败 {ctx.Failures} 项")}，共 {ctx.Assertions} 条断言");
}
return failures == 0 ? 0 : 1;
