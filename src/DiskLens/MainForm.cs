using System.Globalization;
using System.Runtime.InteropServices;
using DiskLens.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DiskLens;

sealed class MainForm : Form
{
    // DESIGN §2.1 的窗口底色 #11151F / 标题文字 #E6E9F0（BackColor 和 WebView2 默认底色一起用，免得启动闪白）。
    // 跟浅色页面底色（theme.css 的 --page / --ink）一致，启动时不会从深色闪成浅色
    static readonly Color Chrome = Color.FromArgb(244, 244, 245);
    static readonly Color ChromeText = Color.FromArgb(24, 24, 27);

    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Chrome };
    readonly Label _status = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = ChromeText, Padding = new Padding(32),
    };
    readonly string? _scanPath;
    readonly ScanMode? _mode;
    readonly SettingsStore _settings = new(DataPaths().Settings);
    BridgeHost? _bridge;

    /// <param name="language">指定界面语言（zh-CN / en / ja），不看保存的设置；UiCheck 用它固定成中文。平时是 null</param>
    public MainForm(string? scanPath, ScanMode? mode, string? language = null)
    {
        _scanPath = scanPath;
        _mode = mode;
        // 界面语言（DESIGN §8.8）：保存过的选择优先，没有就按系统界面语言；后台文字和页面地址里的 ?lang= 都按它
        L.Current = L.Parse(language) ?? L.Resolve(_settings.Language, CultureInfo.CurrentUICulture.Name);
        Text = L.T("DiskLens 磁盘透镜", "DiskLens", "DiskLens");
        _status.Text = L.T("正在打开磁盘透镜…", "Opening DiskLens…", "DiskLens を開いています…");
        BackColor = Chrome;
        Font = new Font(L.T("Microsoft YaHei UI", "Segoe UI", "Yu Gothic UI"), 10);
        StartPosition = FormStartPosition.CenterScreen;
        // 前端版式按 CSS 像素定的：高 DPI 下窗口也要按比例放大，不然 150% 缩放时只剩 960 宽，排版直接挤爆。
        // 最小尺寸取 §8.4 的 1100×700（§2.1 写的 1024×640 比前端能排下的最小宽度还小）。
        MinimumSize = new Size(LogicalToDeviceUnits(1100), LogicalToDeviceUnits(700));
        Size = new Size(LogicalToDeviceUnits(1440), LogicalToDeviceUnits(900));
        if (Screen.PrimaryScreen is { WorkingArea: var area } && (area.Width < Width || area.Height < Height))
            WindowState = FormWindowState.Maximized;
        using (var icon = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico"))
            if (icon != null) Icon = new Icon(icon);
        Controls.Add(_status);
        Controls.Add(_web);
        _web.Visible = false;
        Shown += async (_, _) => await InitializeWebView();
        FormClosed += (_, _) => { _bridge?.Dispose(); _web.Dispose(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // COLORREF 是 0x00BBGGRR，所以 #11151F 写成 0x1F1511。
        int dark = 0, caption = ToColorRef(Chrome), text = ToColorRef(ChromeText);
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));
    }

    static int ToColorRef(Color c) => c.R | c.G << 8 | c.B << 16;

    /// §2.1：程序数据（WebView2 目录、扫描摘要）放在 exe 旁的 data\ 下。装在 Program Files 之类建不了目录的地方时，退回 %LOCALAPPDATA%\DiskLens。
    /// 提权和不提权的进程不能共用同一个 WebView2 目录，所以按权限分开；扫描摘要跟着分（理由见 §2.1）。
    static (string WebView, string Summaries, string Settings) DataPaths()
    {
        string who = Volumes.IsElevated() ? "admin" : "user";
        string root = Path.Combine(AppContext.BaseDirectory, "data");
        try { Directory.CreateDirectory(Path.Combine(root, "webview2-" + who)); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskLens");
        }
        // 界面设置两种权限共用一份：以管理员身份重启之后语言不变
        return (Path.Combine(root, "webview2-" + who), Path.Combine(root, $"scan-summaries-{who}.json"), Path.Combine(root, "settings.json"));
    }

    async Task InitializeWebView()
    {
        try
        {
            var devUrl = Environment.GetEnvironmentVariable("DISKLENS_DEV_URL");
            var origin = new Uri("https://disklens.local/index.html");
            if (!string.IsNullOrWhiteSpace(devUrl))
            {
                if (!Uri.TryCreate(devUrl, UriKind.Absolute, out var dev) || !dev.IsLoopback ||
                    dev.Scheme is not ("http" or "https"))
                    throw new InvalidOperationException(L.T("DISKLENS_DEV_URL 只能使用本机 http/https 地址。",
                        "DISKLENS_DEV_URL must be a local http/https address.", "DISKLENS_DEV_URL にはローカルの http/https アドレスだけを指定できます。"));
                origin = dev;
            }
            var content = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (string.IsNullOrWhiteSpace(devUrl) && !File.Exists(Path.Combine(content, "index.html")))
                throw new FileNotFoundException(L.T("找不到界面文件。请在项目目录运行 .\\build.ps1，再打开 DiskLens.exe。",
                    "The UI files are missing. Run .\\build.ps1 in the project folder, then open DiskLens.exe again.",
                    "画面のファイルが見つかりません。プロジェクトのフォルダーで .\\build.ps1 を実行してから DiskLens.exe を開いてください。"));

            var data = DataPaths();
            var environment = await CoreWebView2Environment.CreateAsync(null, data.WebView);
            if (IsDisposed) return;
            await _web.EnsureCoreWebView2Async(environment);
            if (IsDisposed) return;
            var core = _web.CoreWebView2;
            if (Directory.Exists(content))
                core.SetVirtualHostNameToFolderMapping("disklens.local", content, CoreWebView2HostResourceAccessKind.Deny);
            var settings = core.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPinchZoomEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreHostObjectsAllowed = false; // 桥接只走 postMessage，不给页面开宿主对象这扇门。
#if DEBUG
            settings.AreDevToolsEnabled = true;
#else
            settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("DISKLENS_DEVTOOLS") == "1";
#endif
            _bridge = new BridgeHost(this, core, origin, _scanPath, _mode, new SummaryStore(data.Summaries), _settings);
            core.NavigationStarting += (_, e) => e.Cancel = !BridgeHost.IsSameOrigin(e.Uri, origin);
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.NavigationCompleted += (_, e) =>
            {
                // 被上面 NavigationStarting 拦下的外链、拖进窗口的文件也会走到这里，状态是 OperationCanceled，
                // 那是我们自己拦的，不是界面坏了；不放过的话拖个文件进来就整屏变成「加载失败」。
                if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    ShowFailure(L.T($"界面加载失败：{e.WebErrorStatus}。请重启 DiskLens 后重试。",
                        $"The UI failed to load: {e.WebErrorStatus}. Restart DiskLens and try again.",
                        $"画面を読み込めませんでした：{e.WebErrorStatus}。DiskLens を再起動してもう一度お試しください。"));
            };
            _web.Visible = true;
            _web.BringToFront();
            // 页面一开始就用这个语言，不先闪一下别的语言（i18n.ts 读 ?lang=）
            core.Navigate(new UriBuilder(origin) { Query = "lang=" + L.Code }.Uri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) ShowFailure(L.T($"无法打开磁盘透镜：{ex.Message}\n\n如缺少 WebView2 Runtime，请安装 Microsoft Edge WebView2 Runtime 后重试。",
                $"DiskLens couldn't open: {ex.Message}\n\nIf the WebView2 Runtime is missing, install the Microsoft Edge WebView2 Runtime and try again.",
                $"DiskLens を開けませんでした：{ex.Message}\n\nWebView2 Runtime がない場合は、Microsoft Edge WebView2 Runtime をインストールしてからもう一度お試しください。"));
        }
    }

    void ShowFailure(string message)
    {
        _web.Visible = false;
        _status.Text = message;
        _status.BringToFront();
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
