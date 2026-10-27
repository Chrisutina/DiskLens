using System.Globalization;
using DiskLens.Core;

namespace DiskLens.Check;

/// 界面语言（DESIGN §8.8）：启动语言怎么定、settings.json 的读写、后台文字跟着切、清理脚本内嵌段的翻译表。
static class LanguageChecks
{
    public static void Run(CheckContext ctx)
    {
        var before = L.Current;
        try
        {
            Resolve(ctx);
            Settings(ctx, ctx.Sandbox());
            Switch(ctx);
            ScriptHelpers(ctx);
        }
        finally { L.Current = before; }
    }

    static void Resolve(CheckContext ctx)
    {
        ctx.Equal(L.Lang.En, L.Resolve("en", "zh-CN"), "保存过的选择优先于系统语言");
        ctx.Equal(L.Lang.Zh, L.Resolve("zh-CN", "ja-JP"), "保存过中文：日文系统也用中文");
        ctx.Equal(L.Lang.Ja, L.Resolve(null, "ja-JP"), "没保存过：日文系统用日文");
        ctx.Equal(L.Lang.Zh, L.Resolve(null, "zh-TW"), "繁体中文系统也用中文");
        ctx.Equal(L.Lang.En, L.Resolve(null, "de-DE"), "其余系统语言一律英文");
        ctx.Equal(L.Lang.Zh, L.Resolve("fr", ""), "存的值不认识、系统语言拿不到：中文");
        ctx.True(L.Parse("zh-CN") == L.Lang.Zh && L.Parse("en") == L.Lang.En && L.Parse("ja") == L.Lang.Ja
            && L.Parse("EN") == null && L.Parse("zh") == null && L.Parse(null) == null, "只认 zh-CN / en / ja 三个代码");
    }

    static void Settings(CheckContext ctx, string sandbox)
    {
        string path = Path.Combine(sandbox, "data", "settings.json");
        var store = new SettingsStore(path);
        ctx.True(store.Language == null, "还没有设置文件：null，不抛");
        ctx.True(store.SaveLanguage("ja") && new SettingsStore(path).Language == "ja", "存下日文，换个实例（相当于重启）读回来还是日文");
        ctx.True(File.ReadAllText(path) == "{\"version\":1,\"language\":\"ja\"}", "文件带版本号，只有语言一项");
        ctx.True(!store.SaveLanguage("fr") && store.Language == "ja", "不认识的语言不存，原来的不动");
        ctx.True(!File.Exists(path + ".tmp"), "写完不留临时文件");

        File.WriteAllText(path, "{\"version\":1,\"lang");
        ctx.True(store.Language == null, "文件坏了：当没有");
        File.WriteAllText(path, "{\"version\":9,\"language\":\"en\"}");
        ctx.True(store.Language == null, "版本不对：当没有");
        File.WriteAllText(path, "{\"version\":1,\"language\":\"de\"}");
        ctx.True(store.Language == null, "语言不认识：当没有");

        // 设置文件的位置被一个目录占着：存不下，返回 false，不抛
        string blocked = Path.Combine(sandbox, "blocked", "settings.json");
        Directory.CreateDirectory(blocked);
        var stuck = new SettingsStore(blocked);
        ctx.True(!stuck.SaveLanguage("en") && stuck.Language == null, "存不下：返回 false、不抛，读也是 null");
    }

    static void Switch(CheckContext ctx)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            L.Current = L.Lang.En;
            ctx.True(L.T("中", "en", "ja") == "en" && L.Code == "en", "英文：取英文、代码 en");
            ctx.True(L.Plural(1, "item") == "1 item" && L.Plural(1234, "item") == "1,234 items" && L.Plural(2, "entry", "entries") == "2 entries",
                "英文单复数：1 不加 s，其余加 s 或用给的复数");
            ctx.True(Purposes.Name(Purposes.Games) == "Games" && Roles.Name(Roles.Save) == "Saves"
                && Roles.EvidenceName((int)Evidence.Confirmed) == "Confirmed" && Roles.OwnerKindName((int)OwnerKind.Game) == "Game", "用途、角色、证据、所属对象种类的英文名");
            L.Current = L.Lang.Ja;
            ctx.True(L.T("中", "en", "ja") == "ja" && L.Code == "ja" && Purposes.Name(Purposes.Games) == "ゲーム", "日文");
            L.Current = L.Lang.Zh;
            ctx.True(L.T("中", "en", "ja") == "中" && L.Code == "zh-CN" && Purposes.Name(Purposes.Games) == "游戏", "中文");
        }
        finally { CultureInfo.CurrentCulture = culture; }

        ctx.True(Purposes.NamesEn.Length == Purposes.Count && Purposes.NamesJa.Length == Purposes.Count
            && Roles.NamesEn.Length == Roles.Count && Roles.NamesJa.Length == Roles.Count
            && Roles.EvidenceNamesEn.Length == Roles.EvidenceNames.Length && Roles.EvidenceNamesJa.Length == Roles.EvidenceNames.Length
            && Roles.OwnerKindNamesEn.Length == Roles.OwnerKindNames.Length && Roles.OwnerKindNamesJa.Length == Roles.OwnerKindNames.Length,
            "名字表的英文、日文一条不少");
    }

    /// 清理脚本内嵌的 C# 和 PowerShell 按语言整句替换：表里每条原文都还在模板里（改了模板措辞却忘了改表会在这里翻脸），英文版里不剩中文
    static void ScriptHelpers(CheckContext ctx)
    {
        var missing = CleanupAdvisor.HelperText.Where(t => !CleanupAdvisor.HelperSource.Contains(t.Zh, StringComparison.Ordinal)).Select(t => t.Zh).ToList();
        ctx.True(missing.Count == 0, "翻译表的每条原文都在内嵌段里" + (missing.Count > 0 ? "；找不到：" + missing[0] : ""));

        L.Current = L.Lang.Zh;
        ctx.True(string.Join("\n", CleanupAdvisor.Helpers()) == CleanupAdvisor.HelperSource.Replace("\r\n", "\n"), "中文界面：内嵌段原样");

        L.Current = L.Lang.En;
        string en = string.Join("\n", CleanupAdvisor.Helpers());
        var cjk = en.Split('\n').FirstOrDefault(line => line.Any(c => c is >= (char)0x3000 and <= (char)0x9FFF or >= (char)0xFF00 and <= (char)0xFFEF));
        ctx.True(cjk == null, "英文界面：内嵌段里不剩中文和全角标点" + (cjk != null ? "；比如：" + cjk.Trim() : ""));
        ctx.True(en.Contains("Lines.Add(\"Skipped \" + path + \": \" + why)", StringComparison.Ordinal) && en.Contains("Write-Warning \"Skipped ${Path}: ", StringComparison.Ordinal),
            "英文界面：报错整句换成英文，拼接和变量不动");

        L.Current = L.Lang.Ja;
        string ja = string.Join("\n", CleanupAdvisor.Helpers());
        ctx.True(CleanupAdvisor.HelperText.All(t => !ja.Contains(t.Zh, StringComparison.Ordinal)), "日文界面：表里的中文原文一条不剩");
        L.Current = L.Lang.Zh;
    }
}
