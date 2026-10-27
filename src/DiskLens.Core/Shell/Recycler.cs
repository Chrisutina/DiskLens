using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DiskLens.Core;

/// 回收站里一个条目的结局。Recycled=false 且 Done=true 表示系统没把它放进回收站而是直接删了
/// （回收站被设成「不移到回收站」、文件超过回收站容量……）：用户选的是可恢复的路，这件事必须如实说。
public sealed record RecycleItem(string Path, bool Done, bool Recycled, string? Error);

public sealed record RecycleResult(bool Ok, string? Error, IReadOnlyList<RecycleItem>? Items = null);

/// IFileOperation 回收站删除。调用方必须在 STA 线程上（ShellOps.MoveToRecycleBin 负责切线程）。
static class Recycler
{
    // DESIGN §7 的标志组合：FOFX_RECYCLEONDELETE | FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOFX_EARLYFAILURE，
    // 外加 FOF_NORECURSEREPARSE：万一系统退回到真删，也不要钻进目录里的 junction。
    const uint Flags = 0x4 | 0x10 | 0x40 | 0x400 | 0x8000 | 0x80000 | 0x100000;
    const int ShellMaxPath = 259;
    static readonly Guid ClsidFileOperation = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    internal static RecycleResult Move(IReadOnlyList<string> inputs)
    {
        var report = new Dictionary<string, RecycleItem>(StringComparer.OrdinalIgnoreCase);
        var wanted = new List<string>();
        foreach (string input in inputs)
        {
            string path = input;
            string? refusal = Preflight(ref path);
            if (refusal != null) report[path] = new(path, false, false, refusal);
            else wanted.Add(path);
        }
        if (wanted.Count == 0) return Finish(report, null);

        var sink = new Sink();
        object? instance = null;
        var shellItems = new List<object>();
        IFileOperation? operation = null;
        uint cookie = 0;
        string? failure = null;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidFileOperation, true)!);
            operation = (IFileOperation)instance!;
            operation.SetOperationFlags(Flags);
            operation.Advise(sink, out cookie);
            var iid = typeof(IShellItem).GUID;
            foreach (string path in wanted)
            {
                int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
                if (hr < 0)
                {
                    // 单个条目建不出来（刚被删了、权限不够）不该拖累整批
                    report[path] = new(path, false, false, Marshal.GetExceptionForHR(hr)?.Message ?? $"0x{hr:X8}");
                    continue;
                }
                shellItems.Add(item);
                operation.DeleteItem(item, IntPtr.Zero);
            }
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out bool aborted);
            if (aborted) failure = L.T("部分条目未能移到回收站，请检查占用和权限", "Some items couldn't be moved to the Recycle Bin; check whether they're in use and the permissions", "一部の項目をごみ箱に移動できませんでした。使用中でないかと権限を確認してください");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { failure = ex.Message; } // COM 边界上什么都可能抛（E_INVALIDARG 会变成 ArgumentException）：统一变成这一批的失败说明，不能把宿主带崩
        finally
        {
            if (cookie != 0)
                try { operation!.Unadvise(cookie); } catch (COMException) { }
            foreach (object item in shellItems) Marshal.FinalReleaseComObject(item);
            if (instance != null) Marshal.FinalReleaseComObject(instance);
        }
        foreach (var item in sink.Items.Values) report[item.Path] = item;
        return Finish(report, failure);
    }

    static RecycleResult Finish(Dictionary<string, RecycleItem> report, string? failure)
    {
        var items = report.Values.ToArray();
        bool ok = failure == null && items.All(i => i.Done);
        return new(ok, ok ? null : failure ?? items.FirstOrDefault(i => i.Error != null)?.Error ?? L.T("未能移到回收站", "Couldn't move it to the Recycle Bin", "ごみ箱に移動できませんでした"), items);
    }

    /// 提交之前能判定的失败：路径不合规、被保护规则拦下、系统回收站处理不了的位置。
    static string? Preflight(ref string path)
    {
        try { path = Protection.Normalize(path); }
        catch (ArgumentException ex) { return ex.Message; }
        var protection = Protection.EvaluatePath(path);
        if (protection.Blocked) return protection.Reason;
        // 外壳的路径解析有 MAX_PATH 的限制，硬塞会得到一个莫名其妙的错误，不如提前说清楚
        if (path.Length > ShellMaxPath) return L.T("路径超过 259 个字符，系统回收站处理不了，请改用永久删除", "The path is longer than 259 characters, which the Recycle Bin can't handle; use permanent delete instead", "パスが 259 文字を超えているため、ごみ箱では扱えません。完全に削除を使ってください");
        // 可移动磁盘和网络位置没有回收站；带 FOF_NOCONFIRMATION 提交的话，系统会不声不响地直接删掉
        if (GetDriveTypeW(Path.GetPathRoot(path)!.TrimEnd('\\') + "\\") != 3) return L.T("这个位置没有回收站（可移动磁盘或网络位置），请改用永久删除", "This location has no Recycle Bin (removable drive or network location); use permanent delete instead", "この場所にはごみ箱がありません（リムーバブル ドライブまたはネットワーク上の場所）。完全に削除を使ってください");
        return null;
    }

    /// 进度回调：只用 PostDeleteItem。psiNewlyCreated 非空 = 条目在回收站里有了新身份 = 真的是回收而不是直接删除。
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class Sink : IFileOperationProgressSink
    {
        public readonly Dictionary<string, RecycleItem> Items = new(StringComparer.OrdinalIgnoreCase);

        public void PostDeleteItem(uint flags, IShellItem item, int hr, IShellItem? created)
        {
            try
            {
                item.GetDisplayName(0x80058000, out string path); // SIGDN_FILESYSPATH
                bool done = hr >= 0;
                string? error = done ? null : Marshal.GetExceptionForHR(hr)?.Message ?? $"0x{hr:X8}";
                Items[path] = new(path, done, done && created != null, error);
            }
            catch (COMException) { } // 回调里的异常会被当成失败码还给系统，别添乱；拿不到的条目由调用方按磁盘现状判断
        }

        public void StartOperations() { }
        public void FinishOperations(int hr) { }
        public void PreRenameItem(uint flags, IShellItem item, string newName) { }
        public void PostRenameItem(uint flags, IShellItem item, string newName, int hr, IShellItem? created) { }
        public void PreMoveItem(uint flags, IShellItem item, IShellItem destination, string newName) { }
        public void PostMoveItem(uint flags, IShellItem item, IShellItem destination, string newName, int hr, IShellItem? created) { }
        public void PreCopyItem(uint flags, IShellItem item, IShellItem destination, string newName) { }
        public void PostCopyItem(uint flags, IShellItem item, IShellItem destination, string newName, int hr, IShellItem? created) { }
        public void PreDeleteItem(uint flags, IShellItem item) { }
        public void PreNewItem(uint flags, IShellItem destination, string newName) { }
        public void PostNewItem(uint flags, IShellItem destination, string newName, string template, uint attributes, int hr, IShellItem? created) { }
        public void UpdateProgress(uint total, uint sofar) { }
        public void ResetTimer() { }
        public void PauseTimer() { }
        public void ResumeTimer() { }
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperationProgressSink
    {
        void StartOperations();
        void FinishOperations(int hr);
        void PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, int hr, IShellItem? created);
        void PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, int hr, IShellItem? created);
        void PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, int hr, IShellItem? created);
        void PreDeleteItem(uint flags, IShellItem item);
        void PostDeleteItem(uint flags, IShellItem item, int hr, IShellItem? created);
        void PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName);
        void PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int hr, IShellItem? created);
        void UpdateProgress(uint total, uint sofar);
        void ResetTimer();
        void PauseTimer();
        void ResumeTimer();
    }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperation
    {
        void Advise(IFileOperationProgressSink sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IntPtr sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void DeleteItem(IShellItem item, IntPtr sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string name, IntPtr bind, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetDriveTypeW(string root);
}
