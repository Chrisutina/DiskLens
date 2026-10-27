using System.Diagnostics;

namespace DiskLens.Core;

/// 任务和去重记录保留到进程结束；满额拒绝新任务，不淘汰旧 ID 后冒险重复执行。
public sealed class OperationStore
{
    readonly Lock _sync = new();
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    public event Action<OperationStatusDto>? Changed;

    public (Entry Entry, bool Created) Register(string? id, string kind, string fingerprint)
    {
        id ??= Guid.NewGuid().ToString("N");
        ValidateId(id);
        Entry entry;
        lock (_sync)
        {
            if (_entries.TryGetValue(id, out entry!))
            {
                if (entry.Fingerprint != fingerprint || entry.Snapshot.Kind != kind)
                    throw new ArgumentException(L.T("任务编号已用于不同请求；没有再次执行", "This task ID was already used for a different request; not run again", "このタスク ID は別の要求で使用済みのため、再実行していません"));
                return (entry, false);
            }
            if (_entries.Count >= 1024) throw new InvalidOperationException(L.T("本次启动已记录 1024 个任务，请保存工作并重启应用后再执行", "1,024 tasks have been recorded since startup; save your work and restart the app before running more", "起動後に 1,024 件のタスクを記録しました。作業を保存し、アプリを再起動してから実行してください"));
            entry = new Entry(id, kind, fingerprint, Publish);
            _entries.Add(id, entry);
        }
        Publish(entry.Snapshot);
        return (entry, true);
    }

    void Publish(OperationStatusDto state)
    {
        try { Changed?.Invoke(state); }
        catch (Exception ex) { Trace.TraceWarning("任务状态通知失败：{0}", ex.Message); }
    }

    static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException(L.T("任务编号无效", "Invalid task ID", "タスク ID が無効です"));
    }

    public OperationStatusDto? Status(string id)
    {
        ValidateId(id);
        lock (_sync) return _entries.GetValueOrDefault(id)?.Snapshot;
    }

    public OperationStatusDto Cancel(string id)
    {
        ValidateId(id);
        Entry entry;
        lock (_sync) entry = _entries.GetValueOrDefault(id) ?? throw new ArgumentException(L.T("找不到该任务；请勿重复提交删除", "Task not found; don't submit the deletion again", "タスクが見つかりません。削除を再送信しないでください"));
        return entry.Cancel();
    }

    public sealed class Entry
    {
        readonly Lock _sync = new();
        readonly Action<OperationStatusDto> _publish;
        readonly CancellationTokenSource _cancel = new();
        readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationStatusDto _state;
        Exception? _error;
        public string Fingerprint { get; }
        public CancellationToken Token => _cancel.Token;
        public OperationStatusDto Snapshot { get { lock (_sync) return _state; } }
        public bool External { get; set; }

        internal Entry(string id, string kind, string fingerprint, Action<OperationStatusDto> publish)
        {
            Fingerprint = fingerprint;
            _publish = publish;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _state = new(id, kind, "queued", now, now, null, 0, 0, 0, 0, 0, null);
        }

        static bool Terminal(string status) => status is "completed" or "cancelled" or "failed" or "external";

        void Update(Func<OperationStatusDto, OperationStatusDto> update)
        {
            OperationStatusDto state;
            lock (_sync)
            {
                if (Terminal(_state.Status)) return;
                state = update(_state) with { UpdatedAtMs = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _state.UpdatedAtMs + 1) };
                _state = state;
            }
            // 不在状态锁里发 UI 回调；消费者按 UpdatedAtMs 丢弃并发产生的旧事件。
            _publish(state);
        }

        public void Start() => Update(s => s with { Status = s.Status == "cancelling" ? "cancelling" : "running" });

        public void Report(DeleteProgressDto p) => Update(s => s with
        {
            CurrentPath = p.CurrentPath, Total = p.Total, Completed = p.Completed, Failed = p.Failed,
            Skipped = p.Skipped, ProcessedEntries = p.ProcessedEntries, FreedBytes = p.FreedBytes,
        });

        public OperationStatusDto Cancel()
        {
            if (Terminal(Snapshot.Status)) return Snapshot;
            Update(s => s with { Status = "cancelling", Message = L.T("正在停止后续项目；已经完成的操作不会回滚", "Stopping the remaining items; finished operations aren't rolled back", "残りの項目を止めています。完了した操作は元に戻しません") });
            if (Snapshot.Status == "cancelling") _cancel.Cancel();
            return Snapshot;
        }

        public void Complete(OpResultDto result)
        {
            Update(s => s with
            {
                Status = External ? "external" : s.Status == "cancelling" ? "cancelled" : result.Ok ? "completed" : "failed",
                CurrentPath = null, Result = result, Message = result.Message,
                FreedBytes = External ? null : result.FreedBytes,
                Skipped = Math.Max(s.Skipped, s.Total - s.Completed - s.Failed),
            });
            _finished.TrySetResult();
        }

        public void Fail(Exception error, string message)
        {
            lock (_sync) _error = error;
            Update(s => s with
            {
                Status = error is OperationCanceledException ? "cancelled" : "failed",
                CurrentPath = null, Message = message, Skipped = Math.Max(s.Skipped, s.Total - s.Completed - s.Failed),
            });
            _finished.TrySetResult();
        }

        public async Task<OpResultDto> ResultAsync()
        {
            await _finished.Task;
            lock (_sync)
            {
                if (_error != null) throw _error;
                return _state.Result!;
            }
        }
    }
}
