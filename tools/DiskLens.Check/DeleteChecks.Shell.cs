using DiskLens.Core;

namespace DiskLens.Check;

// 外壳操作的自检：只测纯判定部分，绝不真的开窗口、开浏览器、提权、跑命令或清空回收站。
static partial class DeleteChecks
{
    static void ShellPure(CheckContext ctx, string root)
    {
        _ = root;
        // 顶栏外部搜索：只发用户输入的词，引擎白名单，编码正确，超长截断，空的和带控制字符的拒绝
        ctx.True(ShellOps.BuildQueryUrl("google", "  Steam 着色器缓存 & #1 ") == "https://www.google.com/search?q=Steam%20%E7%9D%80%E8%89%B2%E5%99%A8%E7%BC%93%E5%AD%98%20%26%20%231",
            "Google：去掉首尾空白，空格、汉字、& 和 # 都按 URL 编码");
        ctx.True(ShellOps.BuildQueryUrl(" Bing ", "pagefile.sys").StartsWith("https://www.bing.com/search?q=pagefile.sys"), "Bing：引擎名不分大小写、容忍空白");
        foreach (string? engine in new[] { "baidu", "file", "", null, "https://evil.example/?q=" })
            ctx.True(Throws(() => ShellOps.BuildQueryUrl(engine, "x")) is ArgumentException, $"引擎「{engine ?? "null"}」不在白名单：拒绝");
        foreach (string? query in new[] { "", "   ", null })
            ctx.True(Throws(() => ShellOps.BuildQueryUrl("bing", query)) is ArgumentException, "空搜索词：拒绝，不打开空白搜索页");
        ctx.True(Throws(() => ShellOps.BuildQueryUrl("bing", "a\r\nb")) is ArgumentException, "带控制字符：拒绝");
        string longUrl = ShellOps.BuildQueryUrl("bing", new string('字', 250));
        ctx.True(Uri.UnescapeDataString(longUrl[longUrl.IndexOf('=')..][1..]).Length == 200, "超过 200 个字截断到 200");
        string emoji = ShellOps.BuildQueryUrl("bing", new string('a', 199) + "😀tail");
        ctx.True(Uri.UnescapeDataString(emoji[emoji.IndexOf('=')..][1..]) == new string('a', 199), "截断不劈开代理对");
        ctx.True(ShellOps.TryNormalizeWebUrl(ShellOps.BuildQueryUrl("google", "C:\\Users\\x"), out var uri) && uri.Host == "www.google.com",
            "反斜杠被编码进查询，交给浏览器的地址主机不变");
    }

    static Exception? Throws(Action action)
    {
        try { action(); return null; }
        catch (Exception ex) { return ex; }
    }
}
