using System.Globalization;
using System.Text;

namespace DiskLens.Core;

public static partial class CleanupAdvisor
{
    /// 独立脚本不能依赖已安装的 DiskLens DLL；删除逻辑内嵌成一段 C#，兼容 PS 5.1 的编译器（C# 5：不用插值、?.、模式匹配）。
    /// 和 DeleteService 同一套底线，只是确认清单换成了「扫描开始的时刻」——整棵子树的清单太大，粘贴不进 PowerShell：
    /// - 目标在生成时记下文件身份，运行时在删除用的同一个句柄上核对，对不上（被换成别的东西）就不删。
    /// - 目标里面的条目，创建、修改、变更（ChangeTime：改名、移动、改属性都会更新）三个时间有一个晚于扫描开始就留着：
    ///   扫描之后新建、改过、挪进来的都不是用户确认过的。目录也一样，挪进来的旧目录整个留下，不往里钻。
    /// - 从卷根到正在处理的目录都握着不共享删除权限的句柄，中途改不了名、换不成链接；链接只删链接本身。
    /// - 名字是 .ssh、.gnupg（密钥）或以点、空格结尾的，运行时再拦一道。
    /// 和 NativeFs.Delete、OpenGuard 是同一套句柄写法，改一边记得改另一边。
    internal static readonly string HelperSource = """
        # 删除逻辑在下面这段 C# 里：目标先核对文件身份；扫描开始之后新建、改过、挪进来的条目一律保留；
        # 目录一路握着不共享删除权限的句柄，中途改不了名、换不成链接；链接只删链接本身；核对和删除用同一个句柄。
        if (-not ('DiskLensScriptV2' -as [type])) {
        Add-Type -ErrorAction Stop -TypeDefinition @'
        using System;
        using System.Collections.Generic;
        using System.ComponentModel;
        using System.Globalization;
        using System.IO;
        using System.Runtime.InteropServices;
        using Microsoft.Win32.SafeHandles;
        public static class DiskLensScriptV2 {
            const uint ListDirectory = 0x1, ReadAttributes = 0x80, WriteAttributes = 0x100, DeleteAccess = 0x10000;
            const uint AttrReadOnly = 0x1, AttrDirectory = 0x10, AttrReparse = 0x400, AttrNormal = 0x80, NameSurrogate = 0x20000000;
            [StructLayout(LayoutKind.Sequential)] struct Basic { public long Created, Accessed, Written, Changed; public uint Attributes; }
            [StructLayout(LayoutKind.Sequential)] struct Tag { public uint Attributes, ReparseTag; }
            [StructLayout(LayoutKind.Sequential)] struct Info { public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Find {
                public uint Attributes, CreatedLow, CreatedHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, SizeHigh, SizeLow, Reserved0, Reserved1;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string ShortName;
            }
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out Basic info, uint size);
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out Tag info, uint size);
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref Basic info, uint size);
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref uint flags, uint size);
            [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref byte delete, uint size);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr FindFirstFileW(string pattern, out Find data);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool FindNextFileW(IntPtr find, out Find data);
            [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr find);
            static readonly uint BasicSize = (uint)Marshal.SizeOf(typeof(Basic));

            sealed class Opened { public SafeFileHandle Handle; public bool CanDelete, CanWrite; public int Withheld; }
            sealed class Frame { public string Path; public Opened Dir; public uint Attributes; public bool Clean = true, Keep; public List<Find> Items; public int Next; }
            sealed class Report {
                public readonly List<string> Lines = new List<string>();
                int errors, kept; string example;
                public void Fail(string path, string why) { if (++errors <= 20) Lines.Add("已跳过 " + path + "：" + why); }
                public void Keep(string path) { if (kept++ == 0) example = path; }
                public string[] Done() {
                    if (errors > 20) Lines.Add("另有 " + (errors - 20) + " 项没能删除");
                    if (kept > 0) Lines.Add("已保留 " + kept + " 项扫描之后新建、改过或挪进来的条目（例如 " + example + "）；要删请重新扫描后再复制脚本");
                    return Lines.ToArray();
                }
            }

            // 自检用：列一个目录之前先调一下，这时它和它的祖先都握着句柄；平时是 null
            public static Action<string> BeforeList;

            public static string[] Remove(string path, string identity, long scanStart) { return Run(path, identity, scanStart, false); }
            public static string[] Clear(string path, string identity, long scanStart) { return Run(path, identity, scanStart, true); }

            static string[] Run(string path, string identity, long scanStart, bool contents) {
                var report = new Report();
                var chain = new List<SafeFileHandle>();
                var frames = new Stack<Frame>();
                try {
                    var parents = new List<string>();
                    for (string p = Parent(path); p != null; p = Parent(p)) parents.Insert(0, p);
                    foreach (string p in parents) chain.Add(Guard(p));
                    Walk(path, identity, scanStart, contents, report, frames);
                } catch (Exception ex) {
                    report.Fail(path, ex.Message);
                } finally {
                    foreach (Frame f in frames) f.Dir.Handle.Dispose();
                    for (int i = chain.Count - 1; i >= 0; i--) chain[i].Dispose();
                }
                return report.Done();
            }

            static void Walk(string path, string identity, long start, bool contents, Report report, Stack<Frame> frames) {
                string[] parts = identity.Split('-');
                if (parts.Length != 4) throw new FormatException("文件身份的格式不对，请回 DiskLens 重新复制脚本");
                uint kind = uint.Parse(parts[3], NumberStyles.HexNumber);
                Opened target;
                int error = Open(path, (kind & AttrDirectory) != 0, !contents, out target);
                if (error != 0) { report.Fail(path, Gone(error) ? "已经不在了" : Describe(error)); return; }
                Basic basic;
                bool link;
                try {
                    Info info;
                    if (!GetFileInformationByHandle(target.Handle, out info)) throw new Win32Exception();
                    if (info.Volume != uint.Parse(parts[0], NumberStyles.HexNumber)
                        || (((ulong)info.IndexHigh << 32) | info.IndexLow) != ulong.Parse(parts[1], NumberStyles.HexNumber)
                        || (((long)info.CreatedHigh << 32) | info.CreatedLow) != long.Parse(parts[2], NumberStyles.HexNumber)
                        || (info.Attributes & (AttrDirectory | AttrReparse)) != kind) {
                        report.Fail(path, "生成脚本之后这个位置换成了别的东西，没有删除；请重新扫描");
                        target.Handle.Dispose();
                        return;
                    }
                    link = Read(target.Handle, out basic);
                } catch { target.Handle.Dispose(); throw; }
                bool directory = (basic.Attributes & AttrDirectory) != 0 && !link;
                if (!directory) {
                    if (contents) report.Fail(path, link ? "是链接（联接点或符号链接），不穿过链接删除" : "已不是目录");
                    else if (basic.Written > start || basic.Changed > start) report.Fail(path, "扫描之后改过，没有删除；要删请重新扫描");
                    else Delete(target, basic.Attributes, path, report);
                    target.Handle.Dispose();
                    return;
                }
                frames.Push(new Frame { Path = path, Dir = target, Attributes = basic.Attributes, Keep = contents });
                while (frames.Count > 0) {
                    Frame f = frames.Peek();
                    if (f.Items == null) {
                        if (BeforeList != null) BeforeList(f.Path);
                        f.Items = List(f.Path, report);
                        if (f.Items == null) { f.Items = new List<Find>(); f.Clean = false; }
                    }
                    if (f.Next < f.Items.Count) {
                        Find item = f.Items[f.Next++];
                        if (!Child(f.Path + "\\" + item.Name, item, start, report, frames)) f.Clean = false;
                        continue;
                    }
                    frames.Pop();
                    bool removed = false;
                    if (!f.Keep && f.Clean) {
                        if (f.Dir.CanDelete) removed = Delete(f.Dir, f.Attributes, f.Path, report);
                        else report.Fail(f.Path, f.Dir.Withheld == 32 ? "里面的东西删掉了，目录本身被其他程序占用（比如有窗口正开着它）" : "里面的东西删掉了，目录本身没有删除的权限");
                    }
                    f.Dir.Handle.Dispose();
                    if (!removed && frames.Count > 0) frames.Peek().Clean = false;
                }
            }

            // 目录里的一项：返回 false 表示它还在（留着、删不掉）；真目录压栈，弹栈时再定
            static bool Child(string path, Find item, long start, Report report, Stack<Frame> frames) {
                if (string.Equals(item.Name, ".ssh", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Name, ".gnupg", StringComparison.OrdinalIgnoreCase)) {
                    report.Fail(path, "密钥目录，不删除");
                    return false;
                }
                if (item.Name.EndsWith(".") || item.Name.EndsWith(" ")) {
                    report.Fail(path, "名字以点或空格结尾，系统会把它当成别的路径；请在资源管理器里处理");
                    return false;
                }
                bool listedDir = (item.Attributes & AttrDirectory) != 0;
                Opened entry;
                int error = Open(path, listedDir, true, out entry);
                if (error != 0) {
                    if (Gone(error)) return true;
                    report.Fail(path, Describe(error));
                    return false;
                }
                Basic basic;
                bool link;
                try { link = Read(entry.Handle, out basic); } catch { entry.Handle.Dispose(); throw; }
                bool directory = (basic.Attributes & AttrDirectory) != 0;
                if (basic.Created > start || basic.Written > start || basic.Changed > start || directory != listedDir) {
                    report.Keep(path);
                    entry.Handle.Dispose();
                    return false;
                }
                if (directory && !link) {
                    frames.Push(new Frame { Path = path, Dir = entry, Attributes = basic.Attributes });
                    return true;
                }
                bool done = Delete(entry, basic.Attributes, path, report);
                entry.Handle.Dispose();
                return done;
            }

            // 目录不共享删除权限：握着它的时候谁也改不了名。拿不到删除权限就退一步只守着，里面的照样能清
            static int Open(string path, bool directory, bool forDelete, out Opened opened) {
                opened = null;
                uint read = ReadAttributes | (directory ? ListDirectory : 0u);
                uint[] tries = !forDelete ? new[] { read } : directory
                    ? new[] { read | DeleteAccess | WriteAttributes, read | DeleteAccess, read }
                    : new[] { read | DeleteAccess | WriteAttributes, read | DeleteAccess };
                int error = 0;
                foreach (uint access in tries) {
                    SafeFileHandle handle = CreateFileW(Native(path), access, directory ? 3u : 7u, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (!handle.IsInvalid) {
                        opened = new Opened { Handle = handle, CanDelete = (access & DeleteAccess) != 0, CanWrite = (access & WriteAttributes) != 0, Withheld = error };
                        return 0;
                    }
                    error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    if (error != 5 && error != 32) break;
                }
                return error;
            }

            static SafeFileHandle Guard(string dir) {
                SafeFileHandle handle = CreateFileW(Native(dir), ListDirectory | ReadAttributes, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
                try {
                    Basic basic;
                    if (Read(handle, out basic)) throw new IOException(dir + " 是链接（联接点或符号链接），不穿过链接删除");
                    if ((basic.Attributes & AttrDirectory) == 0) throw new IOException(dir + " 已不是目录");
                    return handle;
                } catch { handle.Dispose(); throw; }
            }

            // 返回是不是链接（名字代理型重解析点：junction、符号链接、挂载点；云占位、去重不算）
            static bool Read(SafeFileHandle handle, out Basic basic) {
                if (!GetFileInformationByHandleEx(handle, 0, out basic, BasicSize)) throw new Win32Exception();
                Tag tag;
                if (!GetFileInformationByHandleEx(handle, 9, out tag, 8)) throw new Win32Exception();
                return (tag.Attributes & AttrReparse) != 0 && (tag.ReparseTag & NameSurrogate) != 0;
            }

            static bool Delete(Opened entry, uint attributes, string path, Report report) {
                int error = MarkDeleted(entry.Handle);
                if (error == 5 && entry.CanWrite && (attributes & AttrReadOnly) != 0) {
                    Basic change = new Basic();
                    change.Attributes = attributes & ~AttrReadOnly;
                    if (change.Attributes == 0) change.Attributes = AttrNormal;
                    if (SetFileInformationByHandle(entry.Handle, 0, ref change, BasicSize)) error = MarkDeleted(entry.Handle);
                }
                if (error == 0 || Gone(error)) return true;
                report.Fail(path, error == 145 ? "目录不为空：里面出现了新东西，这个目录先保留" : Describe(error));
                return false;
            }

            // DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE；老系统、FAT 不认就退回普通删除
            static int MarkDeleted(SafeFileHandle handle) {
                uint flags = 0x13;
                if (SetFileInformationByHandle(handle, 21, ref flags, 4)) return 0;
                int error = Marshal.GetLastWin32Error();
                if (error != 87 && error != 50 && error != 1) return error;
                byte delete = 1;
                return SetFileInformationByHandle(handle, 4, ref delete, 1) ? 0 : Marshal.GetLastWin32Error();
            }

            static List<Find> List(string dir, Report report) {
                var items = new List<Find>();
                Find data;
                IntPtr find = FindFirstFileW(Native(dir).TrimEnd('\\') + "\\*", out data);
                if (find == new IntPtr(-1)) { report.Fail(dir, Describe(Marshal.GetLastWin32Error())); return null; }
                try {
                    do { if (data.Name != "." && data.Name != "..") items.Add(data); } while (FindNextFileW(find, out data));
                    int error = Marshal.GetLastWin32Error();
                    if (error != 18) { report.Fail(dir, Describe(error)); return null; }
                } finally { FindClose(find); }
                return items;
            }

            static string Parent(string path) {
                string trimmed = path.TrimEnd('\\');
                int at = trimmed.LastIndexOf('\\');
                if (at < 0) return null;
                string up = trimmed.Substring(0, at);
                if (up.Length == 2 && up[1] == ':') return up + "\\";
                if (up.StartsWith("\\\\") && up.IndexOf('\\', 2) < 0) return null;
                return up;
            }

            static string Native(string path) {
                return path.StartsWith(@"\\?\") ? path : path.StartsWith(@"\\") ? @"\\?\UNC\" + path.Substring(2) : @"\\?\" + path;
            }

            static bool Gone(int error) { return error == 2 || error == 3 || error == 303; }

            static string Describe(int error) {
                if (error == 5) return "没有权限，或受系统保护";
                if (error == 32 || error == 33) return "被其他程序占用";
                return new Win32Exception(error).Message;
            }
        }
        '@
        }
        # 脚本正文只调这两个；出错只警告、不中断。$DiskLensScanStart 缺了就当 0：什么都算「扫描之后的」，一律不删
        function Remove-DiskLensItem([string]$Path, [string]$Identity) {
            try { foreach ($line in [DiskLensScriptV2]::Remove($Path, $Identity, [long]$DiskLensScanStart)) { Write-Warning $line } }
            catch { Write-Warning "已跳过 ${Path}：$($_.Exception.GetBaseException().Message)" }
        }
        function Clear-DiskLensFolder([string]$Path, [string]$Identity) {
            try { foreach ($line in [DiskLensScriptV2]::Clear($Path, $Identity, [long]$DiskLensScanStart)) { Write-Warning $line } }
            catch { Write-Warning "已跳过 ${Path}：$($_.Exception.GetBaseException().Message)" }
        }
        """;

    /// 内嵌那段的注释和报错按界面语言换：左边是模板里的原文（连着引号、拼接一起写，换得准），中文界面原样用。
    /// 模板改了措辞要跟着改这张表；DiskLens.Check 核对每条原文都还在、英文版里不剩中文。
    internal static readonly (string Zh, string En, string Ja)[] HelperText =
    [
        ("# 删除逻辑在下面这段 C# 里：目标先核对文件身份；扫描开始之后新建、改过、挪进来的条目一律保留；",
            "# The deletion logic is in the C# below: each target's file identity is checked first; anything created, changed or moved in after the scan started is always kept;",
            "# 削除の処理は下の C# にあります。対象はまずファイルの ID を照合し、スキャン開始後に作成・変更・移動された項目はすべて残します。"),
        ("# 目录一路握着不共享删除权限的句柄，中途改不了名、换不成链接；链接只删链接本身；核对和删除用同一个句柄。",
            "# folders stay open without sharing delete access, so they can't be renamed or swapped for links midway; links delete only the link itself; checking and deleting use the same handle.",
            "# フォルダーは削除権限を共有しないハンドルで開いたままにするため、途中で名前の変更やリンクへの置き換えはできません。リンクはリンク自体だけを削除し、照合と削除は同じハンドルで行います。"),
        ("Lines.Add(\"已跳过 \" + path + \"：\" + why)",
            "Lines.Add(\"Skipped \" + path + \": \" + why)",
            "Lines.Add(\"スキップ：\" + path + \"：\" + why)"),
        ("Lines.Add(\"另有 \" + (errors - 20) + \" 项没能删除\")",
            "Lines.Add((errors - 20) + \" more items could not be deleted\")",
            "Lines.Add(\"ほかに \" + (errors - 20) + \" 項目を削除できませんでした\")"),
        ("Lines.Add(\"已保留 \" + kept + \" 项扫描之后新建、改过或挪进来的条目（例如 \" + example + \"）；要删请重新扫描后再复制脚本\")",
            "Lines.Add(\"Kept \" + kept + \" items created, changed or moved in after the scan (for example \" + example + \"); to delete them, rescan and copy the script again\")",
            "Lines.Add(\"スキャン後に作成・変更・移動された \" + kept + \" 項目を残しました（例：\" + example + \"）。削除するには再スキャンしてからスクリプトをコピーし直してください\")"),
        ("// 自检用：列一个目录之前先调一下，这时它和它的祖先都握着句柄；平时是 null",
            "// For self-checks: called before listing a folder, while it and its ancestors are held open; null otherwise",
            "// 自己検査用：フォルダーを列挙する前に呼ばれる。そのときフォルダーと祖先はハンドルで開いたまま。普段は null"),
        ("\"文件身份的格式不对，请回 DiskLens 重新复制脚本\"",
            "\"The file identity format is wrong; copy the script from DiskLens again\"",
            "\"ファイル ID の形式が正しくありません。DiskLens からスクリプトをコピーし直してください\""),
        ("Gone(error) ? \"已经不在了\" :",
            "Gone(error) ? \"already gone\" :",
            "Gone(error) ? \"すでにありません\" :"),
        ("\"生成脚本之后这个位置换成了别的东西，没有删除；请重新扫描\"",
            "\"This location was replaced by something else after the script was generated, so it was not deleted; rescan\"",
            "\"スクリプトの生成後にこの場所が別のものに置き換わったため、削除していません。再スキャンしてください\""),
        ("link ? \"是链接（联接点或符号链接），不穿过链接删除\" : \"已不是目录\"",
            "link ? \"it is a link (junction or symbolic link); not deleting through links\" : \"no longer a folder\"",
            "link ? \"リンク（ジャンクションまたはシンボリック リンク）のため、リンク越しには削除しません\" : \"フォルダーではなくなっています\""),
        ("\"扫描之后改过，没有删除；要删请重新扫描\"",
            "\"changed after the scan, so it was not deleted; rescan to delete it\"",
            "\"スキャン後に変更されたため、削除していません。削除するには再スキャンしてください\""),
        ("\"里面的东西删掉了，目录本身被其他程序占用（比如有窗口正开着它）\" : \"里面的东西删掉了，目录本身没有删除的权限\"",
            "\"its contents were deleted, but the folder itself is in use by another program (for example, a window has it open)\" : \"its contents were deleted, but there is no permission to delete the folder itself\"",
            "\"中身は削除しましたが、フォルダー自体は別のプログラムが使用中です（ウィンドウで開いているなど）\" : \"中身は削除しましたが、フォルダー自体を削除する権限がありません\""),
        ("// 目录里的一项：返回 false 表示它还在（留着、删不掉）；真目录压栈，弹栈时再定",
            "// One item in a folder: false means it is still there (kept or undeletable); real folders are pushed and decided when popped",
            "// フォルダー内の 1 項目：false はまだ残っている（残した・削除できない）ことを示す。実フォルダーはスタックに積み、取り出すときに決める"),
        ("\"密钥目录，不删除\"",
            "\"key folder; not deleted\"",
            "\"鍵のフォルダーのため削除しません\""),
        ("\"名字以点或空格结尾，系统会把它当成别的路径；请在资源管理器里处理\"",
            "\"the name ends with a dot or space, so Windows treats it as a different path; handle it in File Explorer\"",
            "\"名前の末尾がピリオドかスペースのため、Windows は別のパスとして扱います。エクスプローラーで操作してください\""),
        ("// 目录不共享删除权限：握着它的时候谁也改不了名。拿不到删除权限就退一步只守着，里面的照样能清",
            "// Folders do not share delete access: while one is held, nobody can rename it. Without delete access, just hold it; its contents can still be cleaned",
            "// フォルダーは削除権限を共有しない：開いている間は誰も名前を変更できない。削除権限が取れなければ開いておくだけにし、中身はそのまま削除できる"),
        ("dir + \" 是链接（联接点或符号链接），不穿过链接删除\"",
            "dir + \" is a link (junction or symbolic link); not deleting through links\"",
            "dir + \" はリンク（ジャンクションまたはシンボリック リンク）のため、リンク越しには削除しません\""),
        ("dir + \" 已不是目录\"",
            "dir + \" is no longer a folder\"",
            "dir + \" はフォルダーではなくなっています\""),
        ("// 返回是不是链接（名字代理型重解析点：junction、符号链接、挂载点；云占位、去重不算）",
            "// Returns whether it is a link (name-surrogate reparse points: junctions, symbolic links, mount points; cloud placeholders and dedup do not count)",
            "// リンクかどうかを返す（名前サロゲート型の再解析ポイント：ジャンクション、シンボリック リンク、マウント ポイント。クラウドのプレースホルダーや重複除去は含まない）"),
        ("\"目录不为空：里面出现了新东西，这个目录先保留\"",
            "\"folder not empty: something new appeared inside, so the folder is kept\"",
            "\"フォルダーが空ではありません：中に新しいものができたため、このフォルダーは残します\""),
        ("// DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE；老系统、FAT 不认就退回普通删除",
            "// DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE; fall back to a plain delete on older systems or FAT",
            "// DELETE | POSIX_SEMANTICS | IGNORE_READONLY_ATTRIBUTE。古いシステムや FAT が対応しなければ通常の削除に戻る"),
        ("return \"没有权限，或受系统保护\";",
            "return \"no permission, or protected by the system\";",
            "return \"権限がないか、システムで保護されています\";"),
        ("return \"被其他程序占用\";",
            "return \"in use by another program\";",
            "return \"別のプログラムが使用中です\";"),
        ("# 脚本正文只调这两个；出错只警告、不中断。$DiskLensScanStart 缺了就当 0：什么都算「扫描之后的」，一律不删",
            "# The script body calls only these two; errors only warn and never stop the script. If $DiskLensScanStart is missing it counts as 0: everything is after the scan, so nothing is deleted",
            "# スクリプト本体が呼ぶのはこの 2 つだけ。エラーは警告するだけで中断しない。$DiskLensScanStart がなければ 0 とみなし、すべてスキャン後のものとして何も削除しない"),
        ("Write-Warning \"已跳过 ${Path}：",
            "Write-Warning \"Skipped ${Path}: ",
            "Write-Warning \"スキップ：${Path}："),
    ];

    internal static string[] Helpers()
    {
        string s = HelperSource;
        if (L.Current != L.Lang.Zh)
            foreach (var (zh, en, ja) in HelperText) s = s.Replace(zh, L.T(zh, en, ja), StringComparison.Ordinal);
        return s.Replace("\r\n", "\n").Split('\n');
    }

    readonly record struct Row(string Path, long Bytes, string? Blocked, string? Function, bool Dir);

    /// 只用规则表里写死的模板拼 PowerShell；调用方不持锁。
    public static CleanupScriptDto BuildScript(FileTree tree, IReadOnlyList<SuggestionDto> suggestions,
        CleanupScriptRequest req, CleanupContext ctx) => BuildScript(tree, suggestions, req, ctx, PurposeEnv.Real);

    /// <param name="identity">读条目的文件身份，null 表示它已经不在了；自检的合成树在磁盘上不存在，塞假的进来</param>
    internal static CleanupScriptDto BuildScript(FileTree tree, IReadOnlyList<SuggestionDto> suggestions,
        CleanupScriptRequest req, CleanupContext ctx, PurposeEnv env, Func<string, NativeFs.Identity?>? identity = null)
    {
        identity ??= DeletionSnapshot.TryIdentity;
        var picks = Validate(suggestions, req);
        var paths = new CleanupPaths(env, ctx.VolumeRoot);
        // 这一刻之后新建、改过、挪进来的东西脚本一律不删。不知道扫描时刻就分不出来，删除类条目全部跳过
        long? since = ctx.ScanStartedAtMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ctx.ScanStartedAtMs).ToFileTime() : null;
        // 挪进目录的旧东西只能靠 ChangeTime 认出来，FAT、exFAT 不记它：那种卷上的目录不交给脚本
        bool tracksMoves = ctx.FileSystem is { } fs && (fs.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || fs.Equals("ReFS", StringComparison.OrdinalIgnoreCase));

        // 路径、大小和保护结论在锁里读完，之后读身份、拼字符串就不占树锁了。
        var rows = new List<Row>[picks.Count];
        lock (tree.Sync)
            for (int i = 0; i < picks.Count; i++)
            {
                rows[i] = [];
                if (picks[i].S.Action is not (DeleteContents or DeleteNodes)) continue;
                bool contents = picks[i].S.Action == DeleteContents;
                foreach (int n in picks[i].Nodes)
                {
                    if (!tree.IsValid(n)) throw new ArgumentException(L.T("清理建议已过期，请刷新清理列表", "This cleanup suggestion is out of date; refresh the cleanup list", "このクリーンアップの提案は古くなっています。クリーンアップの一覧を更新してください"));
                    string path = tree.GetPath(n);
                    bool dir = tree.IsDir(n) && !DeleteService.IsLink(tree, n);
                    // 和 DeleteService 的删除计划同一套判定：删整个节点看整棵子树（目录里藏着 .ssh 也得拦），扫描根的 deleteContents
                    // 走它那个特例。脚本是用户手动粘贴去跑的，生成时这一道和运行时脚本自己那一道都不能少。
                    var guard = n == FileTree.RootId ? Protection.EvaluatePath(path)
                        : contents ? Protection.Evaluate(tree, n) : Protection.EvaluateTree(tree, n);
                    if (guard.Blocked) { rows[i].Add(new(path, 0, guard.Reason ?? L.T("受保护的路径", "Protected path", "保護されたパス"), null, dir)); continue; }
                    if (!contents) { rows[i].Add(new(path, tree.CountedValue(n, Metric.Alloc), null, null, dir)); continue; }
                    // 清空目录：子项里只要有一个被拦，就别整个 Clear，改成逐个删能删的，被拦的写注释
                    var kids = tree.LiveChildren(n).ToArray();
                    var verdicts = kids.Select(c => Protection.EvaluateTree(tree, c)).ToArray();
                    if (verdicts.All(v => !v.Blocked)) { rows[i].Add(new(path, tree.CountedValue(n, Metric.Alloc), null, null, dir)); continue; }
                    for (int k = 0; k < kids.Length; k++)
                        rows[i].Add(new(tree.GetPath(kids[k]), verdicts[k].Blocked ? 0 : tree.CountedValue(kids[k], Metric.Alloc),
                            verdicts[k].Blocked ? verdicts[k].Reason ?? L.T("受保护的路径", "Protected path", "保護されたパス") : null, "Remove-DiskLensItem",
                            tree.IsDir(kids[k]) && !DeleteService.IsLink(tree, kids[k])));
                }
            }

        var body = new List<string>();
        long total = 0;
        int items = 0;
        bool needsAdmin = false;
        for (int i = 0; i < picks.Count; i++)
        {
            var s = picks[i].S;
            var lines = new List<string>();
            long bytes = s.Bytes;
            bool counted = true;
            switch (s.Action)
            {
                case DeleteContents or DeleteNodes:
                    string function = s.Action == DeleteContents ? "Clear-DiskLensFolder" : "Remove-DiskLensItem";
                    bytes = 0;
                    foreach (var row in rows[i])
                    {
                        string path = row.Path;
                        // 换行或控制字符一旦进了脚本，注释会变成命令、字符串会被截断；这种条目宁可不删。
                        if (path.Any(IsUnsafe)) { lines.Add(L.T("# 已跳过一个路径含换行或控制字符的条目，无法安全写进脚本，请手动处理：", "# Skipped an item whose path has a line break or control character; it can't be written into the script safely. Handle it by hand: ", "# パスに改行または制御文字を含む項目をスキップしました。スクリプトに安全に書き込めないため、手動で対応してください：") + Clean(path)); continue; }
                        if (row.Blocked != null) { lines.Add(L.T($"# 已跳过受保护的条目（{Clean(row.Blocked)}）：{Clean(path)}", $"# Skipped a protected item ({Clean(row.Blocked)}): {Clean(path)}", $"# 保護された項目をスキップしました（{Clean(row.Blocked)}）：{Clean(path)}")); continue; }
                        if (since is null) { lines.Add(L.T("# 已跳过：不知道这次扫描是什么时候开始的，分不出扫描之后才出现的东西，请重新扫描：", "# Skipped: the scan start time is unknown, so items that appeared after the scan can't be told apart. Rescan: ", "# スキップ：スキャンの開始時刻が分からず、スキャン後にできたものを区別できません。再スキャンしてください：") + Clean(path)); continue; }
                        if (row.Dir && !tracksMoves)
                        {
                            lines.Add(L.T($"# 已跳过：这个卷是 {Clean(ctx.FileSystem ?? "未知的文件系统")}，不记录改名和移动的时间，脚本确认不了目录里的东西都是扫描时就有的；请用 DiskLens 的「立即清理」：{Clean(path)}",
                                $"# Skipped: this volume is {Clean(ctx.FileSystem ?? "an unknown file system")}, which doesn't record rename and move times, so the script can't confirm everything inside existed at scan time. Use \"Clean up now\" in DiskLens: {Clean(path)}",
                                $"# スキップ：このボリュームは {Clean(ctx.FileSystem ?? "不明なファイル システム")} で、名前の変更や移動の時刻を記録しないため、中身がすべてスキャン時からあったものか確認できません。DiskLens の「今すぐクリーンアップ」を使ってください：{Clean(path)}"));
                            continue;
                        }
                        NativeFs.Identity? id;
                        try { id = identity(path); }
                        catch (Exception ex) when (NativeFs.IsEntryError(ex))
                        {
                            lines.Add(L.T($"# 已跳过：读不到它的文件身份（{Clean(NativeFs.Describe(ex))}），运行时没法核对，请手动处理：{Clean(path)}", $"# Skipped: its file identity can't be read ({Clean(NativeFs.Describe(ex))}), so it can't be checked at run time. Handle it by hand: {Clean(path)}", $"# スキップ：ファイル ID を読み取れないため（{Clean(NativeFs.Describe(ex))}）、実行時に照合できません。手動で対応してください：{Clean(path)}"));
                            continue;
                        }
                        if (id is not { } who) { lines.Add(L.T("# 已跳过：生成脚本时它已经不在了（可能被别的程序删了）：", "# Skipped: it was already gone when the script was generated (maybe deleted by another program): ", "# スキップ：スクリプトの生成時にはすでにありませんでした（別のプログラムが削除した可能性があります）：") + Clean(path)); continue; }
                        lines.Add($"{row.Function ?? function} {Quote(path)} '{IdentityToken(who)}'");
                        bytes += row.Bytes;
                    }
                    counted = bytes > 0 || lines.Any(l => !l.StartsWith('#'));
                    break;
                case Command:
                    lines.Add(s.Command!);
                    break;
                case EmptyRecycleBin:
                    if (DriveLetter(paths.Volume) is { } letter) lines.Add($"Clear-RecycleBin -DriveLetter {letter} -Force");
                    else { lines.Add(L.T("# 无法确定要清空的盘符，请手动清空回收站", "# Can't tell which drive to empty; empty the Recycle Bin by hand", "# 空にするドライブを特定できません。ごみ箱を手動で空にしてください")); counted = false; }
                    break;
                case OpenSettings:
                    lines.Add(L.T("# 请在打开的系统界面里手动处理：", "# Handle this by hand in the system window that opens: ", "# 開いたシステムの画面で手動で対応してください：") + Clean(s.Description));
                    lines.Add("Start-Process " + Quote(s.Target!));
                    break;
                default: // info：只有说明
                    lines.Add("# " + Clean(s.Description));
                    break;
            }
            if (counted) { total += bytes; items++; needsAdmin |= s.RequiresAdmin; }

            string place = Clean(s.DisplayPath);
            body.Add("");
            body.Add($"# ── {Clean(s.Title)}{(s.RequiresAdmin ? L.T("（需要管理员）", " (needs administrator)", "（管理者が必要）") : "")} · {FormatBytes(bytes)}{(place.Length > 0 ? " · " + place : "")}");
            body.AddRange(lines);
        }

        string when = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var script = new List<string>
        {
            L.T($"# DiskLens 清理脚本 · {when} · {items} 项 · 约 {FormatBytes(total)}", $"# DiskLens cleanup script · {when} · {L.Plural(items, "item")} · about {FormatBytes(total)}",
                $"# DiskLens クリーンアップ スクリプト · {when} · {items} 項目 · 約 {FormatBytes(total)}"),
            L.T("# 删除不进回收站，无法恢复。运行前请关闭浏览器、IDE 和相关程序。", "# Deletions skip the Recycle Bin and can't be undone. Close browsers, IDEs and related programs before running.", "# 削除はごみ箱を経由せず、元に戻せません。実行前にブラウザー・IDE・関連するプログラムを閉じてください。"),
            L.T("# 用法：在 PowerShell 中粘贴运行；标了「需要管理员」的段落请在管理员 PowerShell 中运行。", "# Usage: paste into PowerShell and run; run sections marked \"needs administrator\" in an administrator PowerShell.", "# 使い方：PowerShell に貼り付けて実行します。「管理者が必要」と書かれたセクションは管理者の PowerShell で実行してください。"),
            "$ErrorActionPreference = 'Continue'",
            L.T("# 扫描开始的时刻（UTC FILETIME）：这之后新建、改过、挪进来的东西一律不删，要删请重新扫描后再复制脚本", "# Scan start time (UTC FILETIME): anything created, changed or moved in after it is never deleted; to delete those, rescan and copy the script again", "# スキャン開始時刻（UTC FILETIME）：これ以降に作成・変更・移動されたものは削除しません。削除するには再スキャンしてからスクリプトをコピーし直してください"),
            $"$DiskLensScanStart = {since ?? 0}",
        };
        script.AddRange(Helpers());
        script.AddRange(body);
        return new(string.Join("\r\n", script) + "\r\n", total, items, needsAdmin);
    }

    /// 写进脚本的文件身份：卷序列号-文件 ID-创建时间-类型（目录、重解析两位），十六进制，运行时逐项比对
    internal static string IdentityToken(NativeFs.Identity id) =>
        $"{id.Volume:X8}-{id.FileId:X16}-{id.Created:X16}-{id.Kind:X}";

    /// 建议 ID 必须在传入的列表里，NodeIds 必须是该建议 NodeIds 的子集；命令和设置入口也再核对一遍白名单。
    static List<(SuggestionDto S, int[] Nodes)> Validate(IReadOnlyList<SuggestionDto> suggestions, CleanupScriptRequest req)
    {
        if (req.Selections is not { Length: > 0 }) throw new ArgumentException(L.T("请先勾选要清理的项目", "Select the items to clean up first", "先にクリーンアップする項目を選んでください"));
        var picks = new List<(SuggestionDto, int[])>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sel in req.Selections)
        {
            var s = suggestions.FirstOrDefault(x => x.Id == sel?.SuggestionId)
                ?? throw new ArgumentException(L.T($"清理建议「{sel?.SuggestionId}」不存在或已过期，请刷新清理列表", $"Cleanup suggestion \"{sel?.SuggestionId}\" doesn't exist or is out of date; refresh the cleanup list", $"クリーンアップの提案「{sel?.SuggestionId}」が存在しないか古くなっています。クリーンアップの一覧を更新してください"));
            if (!seen.Add(s.Id)) throw new ArgumentException(L.T($"清理建议「{s.Title}」被重复选中", $"Cleanup suggestion \"{s.Title}\" was selected more than once", $"クリーンアップの提案「{s.Title}」が重複して選択されています"));
            if (s.Action is not (DeleteContents or DeleteNodes or EmptyRecycleBin or Command or OpenSettings or Info))
                throw new ArgumentException(L.T($"清理建议「{s.Title}」的动作类型未知", $"Cleanup suggestion \"{s.Title}\" has an unknown action type", $"クリーンアップの提案「{s.Title}」の操作の種類が不明です"));
            if (s.Action == Command && (s.Command is null || !ShellOps.AllowedCommands.ContainsKey(s.Command)))
                throw new ArgumentException(L.T($"清理建议「{s.Title}」的命令不在白名单内", $"The command of cleanup suggestion \"{s.Title}\" isn't on the allowlist", $"クリーンアップの提案「{s.Title}」のコマンドは許可リストにありません"));
            if (s.Action == OpenSettings && (s.Target is null || !ShellOps.AllowedSettings.Contains(s.Target)))
                throw new ArgumentException(L.T($"清理建议「{s.Title}」的设置入口不在白名单内", $"The settings page of cleanup suggestion \"{s.Title}\" isn't on the allowlist", $"クリーンアップの提案「{s.Title}」の設定ページは許可リストにありません"));
            // 空数组和 null 一样表示整条建议（契约如此）；脚本只是文字，真删除的 Run 才要对空数组较真。
            int[] nodes = sel!.NodeIds is { Length: > 0 } ? sel.NodeIds.Distinct().ToArray() : s.NodeIds;
            var allowed = s.NodeIds.ToHashSet();
            if (nodes.Any(n => !allowed.Contains(n))) throw new ArgumentException(L.T($"选中的条目不属于清理建议「{s.Title}」", $"The selected items don't belong to cleanup suggestion \"{s.Title}\"", $"選択した項目はクリーンアップの提案「{s.Title}」に含まれていません"));
            picks.Add((s, nodes));
        }
        return picks;
    }

    /// PowerShell 单引号字符串：' 写两个。注意弯引号 ‘ ’ ‚ ‛ 在 PowerShell 眼里也是单引号，文件名里的 ’ 很常见，
    /// 不成对写的话字符串会被它提前截断，后面的路径就变成代码了。
    static readonly string QuoteChars = new(new[] { (char)39, (char)0x2018, (char)0x2019, (char)0x201A, (char)0x201B });

    static string Quote(string text)
    {
        var sb = new StringBuilder(text.Length + 2).Append('\'');
        foreach (char c in text)
        {
            sb.Append(c);
            if (QuoteChars.Contains(c)) sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    static bool IsUnsafe(char c) => char.IsControl(c) || c == (char)0x2028 || c == (char)0x2029;

    /// 注释里的文字：换行和控制字符一律换成空格，防止「注释里换行变成命令」。
    static string Clean(string? text) => text is null ? "" : string.Concat(text.Select(c => IsUnsafe(c) ? ' ' : c));

    static char? DriveLetter(string volume) => volume.Length >= 2 && char.IsAsciiLetter(volume[0]) && volume[1] == ':' ? char.ToUpperInvariant(volume[0]) : null;

    /// 1024 进制，口径和前端 formatBytes 一致：两位整数以内一位小数，三位整数不带小数。
    static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = Math.Max(bytes, 0);
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return (u == 0 || v >= 99.95 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture)) + " " + units[u];
    }
}
