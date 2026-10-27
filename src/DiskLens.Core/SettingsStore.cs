using System.Diagnostics;
using System.Text.Json;

namespace DiskLens.Core;

/// 界面设置（现在只有语言），存在 data 目录的 settings.json（DESIGN §2.1）。管理员和标准权限共用这一份：
/// 以管理员身份重启之后，语言不会变回去。写法同 SummaryStore：临时文件写完刷盘再改名覆盖；读坏了当没有。公开方法都不抛。
public sealed class SettingsStore(string path)
{
    public const int Version = 1;

    sealed record FileDto(int Version, string? Language);

    static readonly object Gate = new();

    public string FilePath { get; } = path;

    /// 保存过的界面语言（zh-CN / en / ja）；没存过、读不了、不认识都是 null
    public string? Language
    {
        get
        {
            try
            {
                lock (Gate)
                {
                    if (!File.Exists(FilePath)) return null;
                    var file = JsonSerializer.Deserialize<FileDto>(File.ReadAllBytes(FilePath), Json.Options);
                    return file?.Version == Version && L.Parse(file.Language) != null ? file.Language : null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Trace.TraceWarning("读取界面设置失败（{0}）：{1}", FilePath, ex.Message);
                return null;
            }
        }
    }

    /// 记下界面语言；存不下只记警告、返回 false（这次运行照样用新语言，下次启动按系统语言）
    public bool SaveLanguage(string code)
    {
        if (L.Parse(code) == null) return false;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                string temp = FilePath + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(JsonSerializer.SerializeToUtf8Bytes(new FileDto(Version, code), Json.Options));
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, FilePath, overwrite: true);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("界面设置没存上（{0}）：{1}", FilePath, ex.Message);
            return false;
        }
    }
}
