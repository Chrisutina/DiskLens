namespace DiskLens.Core;

/// 扩展名 → 类别 K。前端 categories.ts 里的名字和颜色按同样的编号排。
public static class FileCategories
{
    public const byte Other = 0, Folder = 1, Video = 2, Image = 3, Audio = 4, Document = 5, Archive = 6,
        Code = 7, Executable = 8, System = 9, DiskImage = 10, Data = 11, Unaccounted = 12;

    public const int Count = 13;

    static readonly Dictionary<string, byte> Map = Build();

    public static byte FromExtension(string ext) => Map.TryGetValue(ext, out var k) ? k : Other;

    public static string Describe(string ext) => ext.ToLowerInvariant() switch
    {
        "log" or "etl" or "evtx" => L.T("日志", "Log", "ログ"),
        "db" or "sqlite" or "sqlite3" or "mdb" or "accdb" or "ldf" or "mdf" or "ndf" => L.T("数据库", "Database", "データベース"),
        "pak" or "bundle" or "unity3d" or "assets" or "vpk" or "bsa" or "ba2" or "wad" => L.T("游戏或应用资源数据", "Game or app resource data", "ゲームまたはアプリのリソース データ"),
        "bin" or "dat" => L.T("通用二进制数据，具体格式未知", "Generic binary data; exact format unknown", "汎用のバイナリ データ（形式は不明）"),
        "safetensors" or "gguf" or "onnx" => L.T("模型格式（按扩展名推断）", "Model format (inferred from the extension)", "モデル形式（拡張子から推定）"),
        "ckpt" or "pt" or "pth" => L.T("检查点或数据文件，具体格式需上下文确认", "Checkpoint or data file; the exact format depends on context", "チェックポイントまたはデータ ファイル（形式は文脈で判断が必要）"),
        _ => ext.Length == 0 ? L.T("无扩展名，格式未知", "No extension; format unknown", "拡張子なし（形式は不明）") : "." + ext + L.T(" 文件（按扩展名识别）", " file (identified by extension)", " ファイル（拡張子で識別）"),
    };

    /// 这几个文件按名字归到系统类，扩展名帮不上忙。
    public static bool IsSystemFileName(ReadOnlySpan<char> name) =>
        name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("DumpStack.log.tmp", StringComparison.OrdinalIgnoreCase);

    static Dictionary<string, byte> Build()
    {
        var m = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        void Add(byte k, string list)
        {
            foreach (var e in list.Split(' ', StringSplitOptions.RemoveEmptyEntries)) m[e] = k;
        }
        Add(Video, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg ts m2ts mts vob 3gp rmvb rm f4v ogv");
        Add(Image, "jpg jpeg png gif bmp webp tif tiff heic heif raw cr2 cr3 nef arw dng psd ai svg ico avif jxl exr hdr dds tga kra xcf");
        Add(Audio, "mp3 flac wav aac ogg m4a wma opus ape aiff aif mid midi dsf dff wv");
        Add(Document, "pdf doc docx xls xlsx ppt pptx txt md rtf odt ods odp epub mobi azw3 csv tex chm xps djvu one pages numbers key");
        Add(Archive, "zip rar 7z tar gz tgz bz2 xz zst cab lz lzma lz4 apk xapk jar deb rpm nupkg whl crx msix appx msixbundle appxbundle");
        Add(Code, "c h cpp hpp cc cxx cs java kt kts py js ts jsx tsx mjs cjs go rs rb php swift m mm lua sh ps1 psm1 bat cmd vue svelte html htm css scss less json jsonc yaml yml toml xml ini cfg conf sql gradle cmake mk ipynb r dart scala pl sln slnx csproj props targets");
        Add(Executable, "exe dll sys msi msp ocx drv efi so dylib com scr cpl mui node pyd lib a o obj pdb winmd");
        Add(System, "cat mum manifest blf regtrans-ms");
        Add(DiskImage, "iso img vhd vhdx vmdk vdi qcow2 wim esd swm dmg ova gho avhdx");
        Add(Data, "db sqlite sqlite3 mdb accdb dat log bak tmp cache etl evtx ldf mdf ndf dmp hprof idx pack npy npz h5 hdf5 parquet arrow feather safetensors ckpt pt pth onnx gguf bin pak bundle unity3d assets vpk bsa ba2 wad");
        return m;
    }
}
