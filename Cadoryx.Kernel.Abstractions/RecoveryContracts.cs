using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public readonly record struct RecoveryKey(Guid RunId, Guid SessionId);
public sealed record RecoveryEntry(RecoveryKey Key, string Name, string? OriginalPath,
    DateTimeOffset CapturedAt, DocumentId DocumentId, DocumentStateId StateId, int SnapshotCount);
public sealed record RecoveryScan(IReadOnlyList<RecoveryEntry> Entries, IReadOnlyList<CadDiagnostic> Diagnostics);

/// <summary>Holds an exclusive source lease while the caller makes a durable replacement.</summary>
public abstract class RecoveryDocument : IDisposable
{
    public abstract LoadedDocument Document { get; }
    public abstract RecoveryEntry Entry { get; }
    public abstract Task RetireAsync(CancellationToken cancellationToken = default);
    public abstract void Dispose();
}

public interface IRecoveryStore : IDisposable
{
    Task WriteAsync(Guid sessionId, DocumentSnapshot snapshot, IAssetStore assets, string? originalPath,
        CancellationToken cancellationToken = default);
    Task ClearAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<RecoveryScan> ScanAsync(CancellationToken cancellationToken = default);
    Task<RecoveryDocument> OpenAsync(RecoveryKey key, IAssetStore assets, CancellationToken cancellationToken = default);
    Task DiscardAsync(RecoveryKey key, CancellationToken cancellationToken = default);
}
