namespace DiskLens.Core;

/// 扩展记录的贡献：属性归基记录，但要等所有块解析完、确认基记录有效之后才能合并。
internal struct MftExtension
{
    internal int Record;
    internal ulong BaseRef;
    internal long Size, Alloc;
    internal bool HasData;
    internal NodeFlags Flags;
    internal uint Tag;
    internal bool VolumeMount;
    internal int FirstLink, LinkCount;   // 在所属块（汇总后在全局）链接表里的区间
}

/// 一个解析任务处理完一块的产出。记录的标量直接写进 MftTable 的数组（每条记录只有一个写入者），这里只放变长的名字。
internal sealed class MftBlockResult
{
    internal MftLink[] Links = [];
    internal char[] Chars = [];
    internal MftExtension[] Extensions = [];
}

/// 解析阶段的错误汇总：Warn 最多留 50 条，一台忙碌的系统盘上 fixup 撕裂能轻松把它灌满，
/// 后面真正重要的对账警告就进不去了，所以这里只露前几条，其余合并成一句。
internal sealed class MftIssues(ScanCounters counters, string root)
{
    const int Shown = 5;
    int _count;

    internal int Count => Volatile.Read(ref _count);

    internal void Report(long number, string reason)
    {
        int n = Interlocked.Increment(ref _count);
        counters.Record(ScanIssueKind.Error, Path.Join(root, "$MFT"), L.T($"记录 {number} 被跳过：{reason}", $"Record {number} skipped: {reason}", $"レコード {number} をスキップしました：{reason}"));
        if (n <= Shown) counters.Warn(L.T($"MFT 记录 {number} 被跳过：{reason}", $"MFT record {number} skipped: {reason}", $"MFT レコード {number} をスキップしました：{reason}"));
    }

    internal void Summarize()
    {
        if (_count > Shown) counters.Warn(L.T($"另有 {_count - Shown:N0} 条 MFT 记录因损坏或不一致被跳过", $"{_count - Shown:N0} more MFT records were skipped as damaged or inconsistent", $"ほかに {_count - Shown:N0} 件の MFT レコードを、破損または不整合のためスキップしました"));
    }
}

/// 按记录号索引的整卷快照。
internal sealed class MftTable(int count, int recordSize, long maxBytes, long clusterSize = 0)
{
    internal const byte InUse = 1, Dir = 2, Ext = 4, Bad = 8, HasData = 16, Visited = 32, Mount = 64;

    internal readonly int Count = count;
    internal readonly int RecordSize = recordSize;
    internal readonly long MaxBytes = maxBytes;
    internal readonly long ClusterSize = clusterSize;

    internal readonly byte[] State = new byte[count];
    internal readonly ushort[] Sequence = new ushort[count];
    internal readonly long[] Size = new long[count], Alloc = new long[count], Mtime = new long[count];
    internal readonly NodeFlags[] Flags = new NodeFlags[count];
    internal readonly uint[] Tag = new uint[count];

    internal MftLink[] Links = [];
    internal int LinkCount;
    internal char[] Chars = [];
    internal MftExtension[] Extensions = [];

    internal bool IsBaseInUse(long r) => (ulong)r < (ulong)Count && (State[r] & (InUse | Ext | Bad)) == InUse;

    /// 各块的变长结果拼成全局数组。拼完就把块丢掉，不让它和全局数组在内存里并存太久。
    internal void Gather(MftBlockResult?[] blocks)
    {
        long links = 0, chars = 0;
        foreach (var b in blocks)
            if (b != null) { links += b.Links.Length; chars += b.Chars.Length; }
        if (links > int.MaxValue || chars > int.MaxValue) throw new InvalidDataException(L.T("MFT 名字总量超出可处理范围", "The total length of MFT names is beyond what can be handled", "MFT の名前の総量が処理できる範囲を超えています"));
        Links = new MftLink[links];
        Chars = new char[chars];
        var extensions = new List<MftExtension>();
        int li = 0, ci = 0;
        for (int i = 0; i < blocks.Length; i++)
        {
            var b = blocks[i];
            if (b == null) continue;
            blocks[i] = null;
            b.Links.CopyTo(Links, li);
            if (ci != 0)
                for (int k = li; k < li + b.Links.Length; k++) Links[k].NameOffset += ci;
            b.Chars.CopyTo(Chars, ci);
            foreach (var e in b.Extensions)
            {
                var x = e;
                x.FirstLink += li;
                extensions.Add(x);
            }
            li += b.Links.Length;
            ci += b.Chars.Length;
        }
        LinkCount = li;
        Extensions = extensions.ToArray();
    }

    /// 扩展记录并入基记录。基记录不存在、已经不在用或序列号过期（在线卷上记录被复用）的，整条作废。
    internal void MergeExtensions(MftIssues issues, CancellationToken ct)
    {
        for (int n = 0; n < Extensions.Length; n++)
        {
            if ((n & 1023) == 0) ct.ThrowIfCancellationRequested();
            var e = Extensions[n];
            long baseNumber = (long)(e.BaseRef & MftRecordParser.ReferenceMask);
            if (!IsBaseInUse(baseNumber) || Sequence[baseNumber] != (ushort)(e.BaseRef >> 48))
            {
                for (int k = e.FirstLink; k < e.FirstLink + e.LinkCount; k++) Links[k].Record = -1;
                issues.Report(e.Record, L.T($"扩展记录的基记录 {baseNumber} 不存在或序列号过期", $"The base record {baseNumber} of an extension record is missing or has a stale sequence number", $"拡張レコードのベース レコード {baseNumber} が存在しないか、シーケンス番号が古くなっています"));
                continue;
            }
            int b = (int)baseNumber;
            if (e.HasData)
            {
                if ((State[b] & HasData) != 0) issues.Report(e.Record, L.T($"基记录 {b} 已有未命名 DATA，忽略扩展记录里重复的那份", $"Base record {b} already has an unnamed DATA attribute; ignoring the duplicate in the extension record", $"ベース レコード {b} にはすでに名前のない DATA があるため、拡張レコード内の重複分を無視します"));
                else { Size[b] = e.Size; State[b] |= HasData; }
            }
            if (e.Alloc > MaxBytes - Alloc[b]) issues.Report(e.Record, L.T($"并入后基记录 {b} 的占用超过卷容量，忽略扩展记录的占用", $"After merging, base record {b} would exceed the volume size; ignoring the extension record's allocation", $"統合するとベース レコード {b} の使用容量がボリューム容量を超えるため、拡張レコードの使用容量を無視します"));
            else Alloc[b] += e.Alloc;
            Flags[b] |= e.Flags & ~NodeFlags.Directory;
            if (e.Tag != 0 && Tag[b] == 0)
            {
                Tag[b] = e.Tag;
                if (e.VolumeMount) State[b] |= Mount;
            }
            for (int k = e.FirstLink; k < e.FirstLink + e.LinkCount; k++) Links[k].Record = b;
        }
    }
}

/// 每个解析线程一个，缓冲区复用。
internal sealed class MftBlockParser
{
    readonly MftTable _table;
    readonly ScanCounters _counters;
    readonly MftIssues _issues;
    readonly MftNames _names = new();
    readonly long[] _categories = new long[FileCategories.Count];
    readonly Dictionary<string, byte> _extCategories = new(StringComparer.Ordinal);
    readonly Dictionary<string, byte>.AlternateLookup<ReadOnlySpan<char>> _extLookup;

    internal MftBlockParser(MftTable table, ScanCounters counters, MftIssues issues)
    {
        _table = table;
        _counters = counters;
        _issues = issues;
        _extLookup = _extCategories.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <param name="length">必须是记录大小的整数倍</param>
    internal MftBlockResult Parse(byte[] buffer, int length, int firstRecord)
    {
        var t = _table;
        int size = t.RecordSize, count = length / size;
        _names.Reset();
        Array.Clear(_categories);
        long files = 0, dirs = 0, bytes = 0;
        List<MftExtension>? extensions = null;
        for (int i = 0; i < count; i++)
        {
            int number = firstRecord + i, firstName = _names.Count;
            var status = MftRecordParser.TryParse(buffer.AsSpan(i * size, size), number, t.MaxBytes, _names, out var info, out string? error, null, t.ClusterSize);
            if (status == MftParseStatus.Unused) continue;
            if (status == MftParseStatus.Corrupt)
            {
                t.State[number] = MftTable.Bad;
                _issues.Report(number, error!);
                continue;
            }
            for (int k = firstName; k < _names.Count; k++) _names.Links[k].Record = number;
            if (info.BaseReference != 0)
            {
                t.State[number] = MftTable.InUse | MftTable.Ext;
                (extensions ??= []).Add(new MftExtension
                {
                    Record = number, BaseRef = info.BaseReference, Size = info.Size, Alloc = info.Alloc, HasData = info.HasData,
                    Flags = info.Flags, Tag = info.ReparseTag, VolumeMount = info.VolumeMount, FirstLink = firstName, LinkCount = _names.Count - firstName,
                });
                continue;
            }
            t.State[number] = (byte)(MftTable.InUse | (info.IsDirectory ? MftTable.Dir : 0) | (info.HasData ? MftTable.HasData : 0)
                | (info.VolumeMount ? MftTable.Mount : 0));
            t.Sequence[number] = info.Sequence;
            t.Size[number] = info.Size;
            t.Alloc[number] = info.Alloc;
            t.Mtime[number] = info.Mtime;
            t.Flags[number] = info.Flags;
            t.Tag[number] = info.ReparseTag;
            if (info.IsDirectory) dirs++; else files++;
            bytes += info.Alloc;
            _categories[Category(number, info.IsDirectory, firstName)] += info.Alloc;
        }
        // 整块只加一次：24 个线程每条记录抢一次同一条缓存行，慢得让人怀疑人生
        _counters.AddBatch(files, dirs, bytes, _categories);
        return new MftBlockResult
        {
            Links = _names.Links.AsSpan(0, _names.Count).ToArray(),
            Chars = _names.Chars.AsSpan(0, _names.CharCount).ToArray(),
            Extensions = extensions?.ToArray() ?? [],
        };
    }

    /// 和 FileTree 的规则一致（目录 → 文件夹，元文件和那几个系统文件 → 系统，其余看扩展名），只用于进度条上的类别分段。
    byte Category(long number, bool directory, int firstName)
    {
        if (directory) return FileCategories.Folder;
        if (number < 16) return FileCategories.System;
        int pick = -1;
        for (int k = firstName; k < _names.Count; k++)
        {
            if (_names.Links[k].Namespace != 2) { pick = k; break; }
            if (pick < 0) pick = k;
        }
        if (pick < 0) return FileCategories.Other;
        var name = _names.Name(pick);
        if (FileCategories.IsSystemFileName(name)) return FileCategories.System;
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return FileCategories.Other;
        var ext = name[(dot + 1)..];
        if (ext.Length > 16 || ext.Contains(' ')) return FileCategories.Other;
        Span<char> lower = stackalloc char[16];
        var key = lower[..ext.ToLowerInvariant(lower)];
        if (_extLookup.TryGetValue(key, out byte known)) return known;
        string text = new(key);
        byte category = FileCategories.FromExtension(text);
        _extCategories[text] = category;
        return category;
    }
}
