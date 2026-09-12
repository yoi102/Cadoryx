using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

/// <summary>Serializes checkpoints and close cleanup; the desktop host supplies the timer.</summary>
public sealed class DocumentRecoveryService(IRecoveryStore store)
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Tracked> tracked = [];
    private readonly SemaphoreSlim queue = new(1, 1);
    public event EventHandler<Exception>? Failed;
    public void Track(CadDocumentSession session)
    {
        lock (gate) tracked.Add(session.SessionId, new(session));
    }

    public async Task CheckpointAllAsync(CancellationToken cancellationToken = default)
    {
        // Timer ticks coalesce; manual per-document checkpoints and close operations wait their turn.
        if (!await queue.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            Tracked[] current; lock (gate) current = tracked.Values.ToArray();
            foreach (var item in current)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { await CheckpointCoreAsync(item, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { Report(ex); }
            }
        }
        finally { queue.Release(); }
    }
    public async Task CheckpointAsync(CadDocumentSession session, CancellationToken cancellationToken = default)
    {
        await queue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Tracked? item; lock (gate) tracked.TryGetValue(session.SessionId, out item);
            if (item is null) throw new InvalidOperationException("Document is not tracked for recovery.");
            await CheckpointCoreAsync(item, cancellationToken).ConfigureAwait(false);
        }
        finally { queue.Release(); }
    }
    private async Task CheckpointCoreAsync(Tracked item, CancellationToken token)
    {
        lock (gate) if (!tracked.ContainsKey(item.Session.SessionId)) return;
        if (item.Session.IsClosing) return;
        using var capture = item.Session.Capture();
        if (!capture.IsDirty)
        {
            if (item.State is not null)
            {
                await store.ClearAsync(item.Session.SessionId, token).ConfigureAwait(false);
                item.State = null; item.Path = null;
            }
            return;
        }
        string? path = capture.FilePath ?? item.Session.RecoveryOriginPath;
        if (item.State == capture.Snapshot.StateId && item.Path == path) return;
        await Task.Run(() => store.WriteAsync(item.Session.SessionId, capture.Snapshot, item.Session.Assets, path, token), token).ConfigureAwait(false);
        item.State = capture.Snapshot.StateId; item.Path = path;
    }

    public async Task ForgetAsync(CadDocumentSession session)
    {
        // Remove before waiting. A tick already holding input leases may finish, but cleanup follows it.
        lock (gate) tracked.Remove(session.SessionId);
        await queue.WaitAsync().ConfigureAwait(false);
        try { await store.ClearAsync(session.SessionId).ConfigureAwait(false); }
        catch (Exception ex) { Report(ex); }
        finally { queue.Release(); }
    }
    public async Task DrainAsync()
    { await queue.WaitAsync().ConfigureAwait(false); queue.Release(); }
    private void Report(Exception ex) { try { Failed?.Invoke(this, ex); } catch { } }
    private sealed class Tracked(CadDocumentSession session)
    {
        public CadDocumentSession Session { get; } = session;
        public DocumentStateId? State { get; set; }
        public string? Path { get; set; }
    }
}
