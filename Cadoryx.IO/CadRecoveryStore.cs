using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.IO;

/// <summary>
/// Each process owns a run lock. Immutable snapshot/metadata pairs are published by renaming
/// metadata last. Readers ignore incomplete pairs and never enter a live process's directory.
/// </summary>
public sealed class CadRecoveryStore : IRecoveryStore
{
    private readonly string root;
    private readonly IDocumentStorage storage;
    private readonly SemaphoreSlim queue = new(1, 1);
    private FileStream? owner;
    private bool disposed;
    public Guid RunId { get; } = Guid.NewGuid();
    private const int MaxRuns = 1000, MaxSessions = 1000, MaxRecords = 100;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 16 };

    public CadRecoveryStore(string root, IDocumentStorage storage)
    {
        this.root = Path.GetFullPath(root);
        this.storage = storage;
        // Directory creation is lazy: recovery IO errors must not prevent the CAD shell from starting.
    }

    public async Task WriteAsync(Guid sessionId, DocumentSnapshot snapshot, IAssetStore assets, string? originalPath,
        CancellationToken cancellationToken = default)
    {
        await queue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOwner();
            var directory = SessionPath(new(RunId, sessionId));
            Directory.CreateDirectory(directory); CheckPath(directory);
            var revision = Guid.NewGuid();
            var model = Path.Combine(directory, revision.ToString("N") + ".cadoryx");
            var metadata = Path.Combine(directory, revision.ToString("N") + ".json");
            var temp = metadata + ".tmp";
            bool published = false;
            try
            {
                await storage.SaveAsync(snapshot, assets, model, cancellationToken).ConfigureAwait(false);
                long sequence = checked(ReadRecords(directory, new(RunId, sessionId), []).Select(r => r.Sequence).DefaultIfEmpty().Max() + 1);
                var record = new RecoveryRecord(1, sequence, RunId, sessionId, revision, snapshot.Id, snapshot.StateId,
                    snapshot.Name, originalPath, DateTimeOffset.UtcNow);
                await WriteDurableAsync(temp, JsonSerializer.SerializeToUtf8Bytes(record, Json), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temp, metadata);
                DeleteFile(Path.Combine(directory, "inactive"));
                published = true;
                // Keep two committed generations. Failure to prune is harmless; a later write retries.
                try
                {
                    var keep = ReadRecords(directory, new(RunId, sessionId), []).OrderByDescending(r => r.Sequence)
                        .ThenByDescending(r => r.Revision).Take(2).Select(r => r.Revision.ToString("N")).ToHashSet();
                    foreach (var file in Directory.EnumerateFiles(directory))
                    {
                        string stem = Path.GetFileNameWithoutExtension(file);
                        if (Guid.TryParseExact(stem, "N", out _) && !keep.Contains(stem)) DeleteFile(file);
                    }
                }
                catch (Exception ex) when (Recoverable(ex)) { }
            }
            finally
            {
                DeleteFile(temp);
                if (!published) { DeleteFile(metadata); DeleteFile(model); }
            }
        }
        finally { queue.Release(); }
    }

    public async Task ClearAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await queue.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var directory = SessionPath(new(RunId, sessionId));
            if (Directory.Exists(directory)) await ClearDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        }
        finally { queue.Release(); }
    }

    public Task<RecoveryScan> ScanAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ThrowIfDisposed();
        var entries = new List<RecoveryEntry>(); var diagnostics = new List<CadDiagnostic>();
        CheckPath(root);
        if (!Directory.Exists(root)) return new RecoveryScan(entries, diagnostics);
        var runs = Directory.EnumerateDirectories(root).Take(MaxRuns + 1).ToArray();
        if (runs.Length > MaxRuns) diagnostics.Add(new("RECOVERY.LIMIT", "Recovery run scan limit reached."));
        foreach (var run in runs.Take(MaxRuns))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(run), "N", out var runId) || runId == RunId) continue;
            try
            {
                using var claim = TryClaimRun(runId);
                if (claim is null) continue;
                var sessions = Directory.EnumerateDirectories(run).Take(MaxSessions + 1).ToArray();
                if (sessions.Length > MaxSessions) diagnostics.Add(new("RECOVERY.LIMIT", "Recovery document scan limit reached."));
                foreach (var directory in sessions.Take(MaxSessions))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var sessionId)) continue;
                    try
                    {
                        CheckPath(directory);
                        if (File.Exists(Path.Combine(directory, "inactive"))) continue;
                        var records = ReadRecords(directory, new(runId, sessionId), diagnostics);
                        if (records.Count > 0)
                        {
                            var newest = records.OrderByDescending(r => r.Sequence).ThenByDescending(r => r.Revision).First();
                            entries.Add(Entry(newest, records.Count));
                        }
                    }
                    catch (Exception ex) when (Recoverable(ex)) { diagnostics.Add(new("RECOVERY.SCAN", ex.Message)); }
                }
            }
            catch (Exception ex) when (Recoverable(ex)) { diagnostics.Add(new("RECOVERY.SCAN", ex.Message)); }
        }
        return new RecoveryScan(entries.OrderByDescending(e => e.CapturedAt).ToArray(), diagnostics);
    }, cancellationToken);

    public async Task<RecoveryDocument> OpenAsync(RecoveryKey key, IAssetStore assets, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var claim = TryClaimRun(key.RunId) ?? throw new IOException("This recovery session is in use by another Cadoryx process.");
        try
        {
            var directory = SessionPath(key);
            if (File.Exists(Path.Combine(directory, "inactive"))) throw new FileNotFoundException("Recovery session was already closed or recovered.");
            var diagnostics = new List<CadDiagnostic>();
            var records = ReadRecords(directory, key, diagnostics).OrderByDescending(r => r.Sequence).ThenByDescending(r => r.Revision).ToArray();
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var model = Path.Combine(directory, record.Revision.ToString("N") + ".cadoryx"); CheckPath(model);
                    var loaded = await Task.Run(() => storage.LoadAsync(model, assets, cancellationToken), cancellationToken).ConfigureAwait(false);
                    if (loaded.Snapshot.Id != record.DocumentId || loaded.Snapshot.StateId != record.StateId || loaded.Snapshot.Name != record.Name)
                    { loaded.Dispose(); throw new InvalidDataException("Recovery metadata does not match the snapshot."); }
                    if (diagnostics.Count > 0)
                    {
                        var previous = loaded;
                        var leases = new List<IAssetLease>();
                        try
                        {
                            foreach (var id in previous.Snapshot.ReferencedAssets()) leases.Add(assets.Acquire(id));
                            loaded = new LoadedDocument(previous.Snapshot, leases,
                                [..previous.Diagnostics, new("RECOVERY.FALLBACK", "Some recovery records could not be read; a valid completed snapshot was recovered.")]);
                            leases.Clear();
                        }
                        finally { previous.Dispose(); foreach (var lease in leases) lease.Dispose(); }
                    }
                    var result = new OpenedRecovery(this, directory, loaded, Entry(record, records.Length), claim!);
                    claim = null; return result;
                }
                catch (Exception ex) when (Recoverable(ex)) { diagnostics.Add(new("RECOVERY.INVALID", ex.Message)); }
            }
            throw new InvalidDataException("No valid recovery snapshot is available. " + string.Join("; ", diagnostics.Select(d => d.Message)));
        }
        finally { claim?.Dispose(); }
    }

    public async Task DiscardAsync(RecoveryKey key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using (var claim = TryClaimRun(key.RunId) ?? throw new IOException("This recovery session is in use."))
            await ClearDirectoryAsync(SessionPath(key), cancellationToken).ConfigureAwait(false);
        TryDeleteEmptyRun(key.RunId);
    }

    private List<RecoveryRecord> ReadRecords(string directory, RecoveryKey key, List<CadDiagnostic> diagnostics)
    {
        CheckPath(directory);
        var records = new List<RecoveryRecord>();
        if (!Directory.Exists(directory)) return records;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Take(MaxRecords))
        {
            try
            {
                CheckPath(file);
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var revision)) continue;
                if (new FileInfo(file).Length > 65536) throw new InvalidDataException("Recovery metadata exceeds its limit.");
                var record = JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllBytes(file), Json) ?? throw new InvalidDataException("Null recovery metadata.");
                if (record.Version != 1 || record.Sequence <= 0 || record.RunId != key.RunId || record.SessionId != key.SessionId || record.Revision != revision)
                    throw new InvalidDataException("Invalid recovery metadata identity or version.");
                CadGuard.Id(record.DocumentId); CadGuard.Id(record.StateId); CadGuard.Name(record.Name);
                if (record.OriginalPath?.Length > 32768 || record.CapturedAt == default) throw new InvalidDataException("Invalid recovery metadata.");
                if (!File.Exists(Path.Combine(directory, revision.ToString("N") + ".cadoryx"))) throw new InvalidDataException("Recovery payload is missing.");
                records.Add(record);
            }
            catch (Exception ex) when (Recoverable(ex)) { diagnostics.Add(new("RECOVERY.METADATA", ex.Message)); }
        }
        return records;
    }

    private void EnsureOwner()
    {
        ThrowIfDisposed();
        if (owner is not null) return;
        var run = RunPath(RunId); Directory.CreateDirectory(run); CheckPath(run);
        owner = new FileStream(Path.Combine(run, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private FileStream? TryClaimRun(Guid runId)
    {
        if (runId == RunId) throw new InvalidOperationException("Cannot claim the current recovery run.");
        var run = RunPath(runId);
        if (!Directory.Exists(run)) throw new DirectoryNotFoundException("Recovery run no longer exists.");
        var path = Path.Combine(run, "owner.lock"); CheckPath(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return null; }
    }
    private async Task ClearDirectoryAsync(string directory, CancellationToken token)
    {
        CheckPath(directory);
        if (!Directory.Exists(directory)) return;
        // Publish a tombstone first: interrupted cleanup must not resurrect an explicitly closed document.
        var marker = Path.Combine(directory, "inactive");
        var temp = marker + ".tmp";
        token.ThrowIfCancellationRequested();
        try
        {
            await WriteDurableAsync(temp, "closed"u8.ToArray(), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); File.Move(temp, marker, true);
        }
        finally { DeleteFile(temp); }
        foreach (var file in Directory.EnumerateFiles(directory)) if (file != marker) DeleteFile(file);
        // With no payloads left, removing the marker cannot resurrect a document after a crash.
        if (!Directory.EnumerateDirectories(directory).Any())
        {
            DeleteFile(marker); Directory.Delete(directory, false);
        }
    }
    private string RunPath(Guid runId)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Invalid recovery run ID.");
        var path = Path.Combine(root, runId.ToString("N")); CheckPath(path); return path;
    }
    private string SessionPath(RecoveryKey key)
    {
        if (key.SessionId == Guid.Empty) throw new ArgumentException("Invalid recovery session ID.");
        var path = Path.Combine(RunPath(key.RunId), key.SessionId.ToString("N")); CheckPath(path); return path;
    }
    private void CheckPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Recovery path is outside its root.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recovery paths cannot traverse symbolic links or junctions.");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }
    private void DeleteFile(string path) { CheckPath(path); if (File.Exists(path)) File.Delete(path); }
    private async Task WriteDurableAsync(string path, byte[] bytes, CancellationToken token)
    {
        CheckPath(path);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await file.WriteAsync(bytes, token).ConfigureAwait(false); await file.FlushAsync(token).ConfigureAwait(false); file.Flush(true);
    }
    private static bool Recoverable(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
    private static RecoveryEntry Entry(RecoveryRecord record, int count) => new(new(record.RunId, record.SessionId), record.Name,
        record.OriginalPath, record.CapturedAt, record.DocumentId, record.StateId, count);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
    private void TryDeleteEmptyRun(Guid runId)
    {
        try
        {
            var run = RunPath(runId);
            if (!Directory.Exists(run) || Directory.EnumerateDirectories(run).Any()) return;
            string lockPath = Path.Combine(run, "owner.lock");
            if (Directory.EnumerateFiles(run).Any(path => path != lockPath)) return;
            // A competing claim holds FileShare.None; deletion then fails and leaves the directory intact.
            DeleteFile(lockPath); Directory.Delete(run, false);
        }
        catch (Exception ex) when (Recoverable(ex)) { }
    }
    public void Dispose()
    {
        queue.Wait();
        try
        {
            if (disposed) return;
            disposed = true; owner?.Dispose(); owner = null; TryDeleteEmptyRun(RunId);
        }
        finally { queue.Release(); }
    }
    private sealed record RecoveryRecord(int Version, long Sequence, Guid RunId, Guid SessionId, Guid Revision, DocumentId DocumentId,
        DocumentStateId StateId, string Name, string? OriginalPath, DateTimeOffset CapturedAt);
    private sealed class OpenedRecovery(CadRecoveryStore store, string directory, LoadedDocument document,
        RecoveryEntry entry, FileStream claim) : RecoveryDocument
    {
        private bool disposed;
        public override LoadedDocument Document => document;
        public override RecoveryEntry Entry => entry;
        public override Task RetireAsync(CancellationToken cancellationToken = default)
        { ObjectDisposedException.ThrowIf(disposed, this); return store.ClearDirectoryAsync(directory, cancellationToken); }
        public override void Dispose()
        {
            if (disposed) return; disposed = true; document.Dispose(); claim.Dispose(); store.TryDeleteEmptyRun(entry.Key.RunId);
        }
    }
}
