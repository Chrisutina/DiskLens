namespace DiskLens.Core;

/// 确认框发出的令牌：把用户在确认框里看到的对象清单钉住，执行时只认这一份（DESIGN §9.2）。
/// 一次性；最多留 32 个，旧的先作废；换扫描时全部作废。宿主的只读请求不占许可，所以自带锁。
public sealed class ConfirmationStore
{
    /// <param name="SuggestionId">null 表示右键删除（node.checkDelete 发的）</param>
    /// <param name="NodeIds">请求里原样的节点；清理建议里 null 表示整条</param>
    /// <param name="Permanent">清理建议确认时的永久删除开关；右键删除在执行时才选，所以是 null</param>
    public sealed record Entry(FileTree Tree, string? SuggestionId, int[]? NodeIds, bool? Permanent, DeletionSnapshot? Snapshot)
    {
        /// 同一棵树、同一条建议、同一组节点、同一个永久删除开关，才是用户确认过的那次操作。
        public bool Matches(FileTree tree, string? suggestionId, int[]? nodeIds, bool? permanent) =>
            ReferenceEquals(tree, Tree) && suggestionId == SuggestionId && permanent == Permanent &&
            (NodeIds is null ? nodeIds is null : nodeIds is not null && NodeIds.ToHashSet().SetEquals(nodeIds));
    }

    const int Capacity = 32;
    readonly Lock _sync = new();
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly Queue<string> _order = new();

    public string Issue(Entry entry)
    {
        string token = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            _entries[token] = entry;
            _order.Enqueue(token);
            while (_order.Count > Capacity) _entries.Remove(_order.Dequeue());
        }
        return token;
    }

    /// 取出并作废令牌；没有、用过或被挤掉都要求重新确认。
    public Entry Take(string? token)
    {
        lock (_sync)
        {
            if (token is null || !_entries.Remove(token, out var entry))
                throw new ArgumentException(L.T("确认已过期或已经用过，请重新打开确认框", "The confirmation expired or was already used; open the confirmation again", "確認の有効期限が切れたか、使用済みです。確認画面を開き直してください"));
            return entry;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _order.Clear();
        }
    }
}
