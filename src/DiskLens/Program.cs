namespace DiskLens;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // 先按系统语言：命令行写错、窗口还没起来时的提示也看得懂；MainForm 再按保存的设置定
        Core.L.Current = Core.L.FromSystem();
        try
        {
            string? scanPath = null;
            Core.ScanMode? mode = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--scan" && i + 1 < args.Length) scanPath = Path.GetFullPath(args[++i]);
                else if (args[i] == "--mode" && i + 1 < args.Length &&
                         Enum.TryParse<Core.ScanMode>(args[++i], true, out var parsed) && Enum.IsDefined(parsed)) mode = parsed;
                else throw new ArgumentException(Core.L.T("用法：DiskLens.exe [--scan 文件夹路径] [--mode auto|mft|walk]",
                    "Usage: DiskLens.exe [--scan folder] [--mode auto|mft|walk]", "使い方：DiskLens.exe [--scan フォルダー] [--mode auto|mft|walk]"));
            }
            Application.Run(new MainForm(scanPath, mode));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Core.L.T("DiskLens 无法启动", "DiskLens can't start", "DiskLens を起動できません"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
