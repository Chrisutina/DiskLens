using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DiskLens.Core;

/// 交给外壳之前先问一句「这东西进得了回收站吗」。FOF_NOCONFIRMATION 下，回收站被关、条目超过容量、
/// 网络位置和 U 盘这几种情况，外壳不吭声就直接永久删除了——用户选的明明是可以反悔的那条路。
static class RecycleBinPolicy
{
    /// 返回拒绝理由；null 表示可以交给外壳（拿不准的情况也放行，事后还有 Nuked 的如实报告兜底）。
    internal static string? Refusal(string path, long bytes)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return L.T("网络位置没有回收站", "Network locations have no Recycle Bin", "ネットワーク上の場所にはごみ箱がありません");
        string? root = Volumes.GetVolumeRoot(path);
        if (root == null) return null;
        string type = Volumes.GetDriveType(root);
        if (type == "network") return L.T("网络位置没有回收站", "Network locations have no Recycle Bin", "ネットワーク上の場所にはごみ箱がありません");
        if (type == "removable") return L.T("可移动磁盘没有回收站", "Removable drives have no Recycle Bin", "リムーバブル ドライブにはごみ箱がありません");
        if (PolicyOn("NoRecycleFiles")) return L.T("系统策略关闭了回收站", "A system policy turned off the Recycle Bin", "システムのポリシーでごみ箱が無効になっています");
        string? guid = VolumeGuid(root);
        if (guid == null) return null;
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{guid}");
        if (key == null) return null;
        if (key.GetValue("NukeOnDelete") is int nuke && nuke != 0) return L.T("这个盘的回收站设成了「不将文件移到回收站」", "This drive's Recycle Bin is set to \"Don't move files to the Recycle Bin\"", "このドライブのごみ箱は「ファイルをごみ箱に移動しないで、削除と同時に消す」に設定されています");
        if (key.GetValue("MaxCapacity") is int mb && mb > 0 && bytes > (long)mb << 20)
            return L.T($"比这个盘回收站的容量上限（{mb:N0} MB）还大", $"Larger than this drive's Recycle Bin limit ({mb:N0} MB)", $"このドライブのごみ箱の上限（{mb:N0} MB）より大きいです");
        return null;
    }

    static bool PolicyOn(string name)
    {
        const string sub = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(sub);
            if (key?.GetValue(name) is int v && v != 0) return true;
        }
        return false;
    }

    /// "C:\" → "{xxxxxxxx-…}"（BitBucket 下的子键名）；拿不到返回 null。
    static string? VolumeGuid(string root)
    {
        var buffer = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(root.EndsWith('\\') ? root : root + "\\", buffer, buffer.Capacity)) return null;
        string name = buffer.ToString(); // \\?\Volume{guid}\
        int open = name.IndexOf('{'), close = name.IndexOf('}');
        return open >= 0 && close > open ? name[open..(close + 1)] : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int length);
}
