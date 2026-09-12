using System.Text.Json.Nodes;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;
public sealed class RecoveryTests
{
    [Fact] public async Task ActiveRunIsExcludedAndOnlyTwoCommittedGenerationsRemain()
    {
        using var files = new TestFiles(); var storage = new CadDocumentStorage(); var assets = new MemoryAssetStore();
        string root = files.PathFor("recovery"); var id = Guid.NewGuid(); RecoveryKey key;
        using (var writer = new CadRecoveryStore(root, storage))
        using (var reader = new CadRecoveryStore(root, storage))
        {
            key = new(writer.RunId, id);
            for (int i = 0; i < 4; i++) await writer.WriteAsync(id, DocumentSnapshot.Create("Version " + i), assets, "original.cadoryx");
            Assert.Empty((await reader.ScanAsync()).Entries);
            await Assert.ThrowsAsync<IOException>(() => reader.OpenAsync(key, assets));
            Assert.Equal(2, Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length);
        }
        using var reopened = new CadRecoveryStore(root, storage);
        var entry = Assert.Single((await reopened.ScanAsync()).Entries);
        Assert.Equal("Version 3", entry.Name); Assert.Equal(2, entry.SnapshotCount);
        using var loaded = await reopened.OpenAsync(key, assets); Assert.Equal("Version 3", loaded.Document.Snapshot.Name);
    }

    [Fact] public async Task DirtyCheckpointPreservesExactAssetsAndNeverWritesOriginalOrSavepoint()
    {
        using var files = new TestFiles(); var storage = new CadDocumentStorage(); var assets = new MemoryAssetStore(); var kernel = new OcctGeometryKernel();
        var snapshot = DocumentSnapshot.Create("Recovery"); string original = files.PathFor("original.cadoryx");
        await storage.SaveAsync(snapshot, assets, original); byte[] before = await File.ReadAllBytesAsync(original);
        DocumentSnapshot expected;
        using (var store = new CadRecoveryStore(files.PathFor("recovery"), storage))
        {
            var recovery = new DocumentRecoveryService(store);
            await using var session = new CadDocumentSession(snapshot, assets, kernel, new InlineSessionDispatcher(), original);
            recovery.Track(session); await recovery.CheckpointAllAsync();
            Assert.False(Directory.Exists(files.PathFor("recovery")));
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10, 20, 30, RigidTransform3d.Identity), "Box"));
            expected = session.Snapshot; long generation = session.Generation;
            await recovery.CheckpointAsync(session); Assert.True(session.IsDirty); Assert.Equal(generation, session.Generation);
            await recovery.CheckpointAllAsync(); Assert.Single(Directory.GetFiles(files.PathFor("recovery"), "*.json", SearchOption.AllDirectories));
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(original)); Assert.Equal(0, assets.Count);
        using var restarted = new CadRecoveryStore(files.PathFor("recovery"), storage);
        var entry = Assert.Single((await restarted.ScanAsync()).Entries);
        using var recovered = await restarted.OpenAsync(entry.Key, assets);
        Assert.Equal(original, recovered.Entry.OriginalPath);
        Assert.Equal(expected.StateId, recovered.Document.Snapshot.StateId);
        Assert.Equal(expected.Bodies.Values.Single().Geometry, recovered.Document.Snapshot.Bodies.Values.Single().Geometry);
        await using var workspace = new CadWorkspace(kernel, assets, new InlineSessionDispatcher());
        var sessionCopy = workspace.AttachRecovered(recovered.Document.Snapshot, recovered.Entry.OriginalPath);
        Assert.Null(sessionCopy.FilePath); Assert.True(sessionCopy.IsDirty); Assert.Equal(original, sessionCopy.RecoveryOriginPath);
    }

    [Fact] public async Task NewerCorruptSnapshotFallsBackWithoutLeakingAssets()
    {
        using var files = new TestFiles(); var storage = new CadDocumentStorage(); var assets = new MemoryAssetStore();
        string root = files.PathFor("recovery");
        using (var store = new CadRecoveryStore(root, storage))
        {
            await using var session = new CadDocumentSession(DocumentSnapshot.Create("Good"), assets, new OcctGeometryKernel(), new InlineSessionDispatcher());
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10, 20, 30, RigidTransform3d.Identity), "Exact geometry"));
            await store.WriteAsync(session.SessionId, session.Snapshot, assets, null);
            await session.ExecuteAsync(new EditDocumentCommand("rename", d => d with { Name = "Damaged" }));
            await store.WriteAsync(session.SessionId, session.Snapshot, assets, null);
        }
        var damaged = Metadata(root).Single(m => m.Json["name"]!.GetValue<string>() == "Damaged");
        await File.WriteAllTextAsync(Path.ChangeExtension(damaged.Path, ".cadoryx"), "broken");
        using var reader = new CadRecoveryStore(root, storage);
        var entry = Assert.Single((await reader.ScanAsync()).Entries);
        using (var loaded = await reader.OpenAsync(entry.Key, assets))
        {
            Assert.Equal("Good", loaded.Document.Snapshot.Name);
            Assert.Single(loaded.Document.Snapshot.Bodies); Assert.True(assets.Count > 0);
            Assert.Contains(loaded.Document.Diagnostics, d => d.Code == "RECOVERY.FALLBACK");
        }
        Assert.Equal(0, assets.Count); Assert.Equal(2, Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length);
    }

    [Fact] public async Task FailureAfterPayloadWriteLeavesPreviousCheckpointRecoverable()
    {
        using var files = new TestFiles(); var real = new CadDocumentStorage(); var storage = new ControlledStorage(real); var assets = new MemoryAssetStore();
        string root = files.PathFor("recovery"); var id = Guid.NewGuid();
        using (var writer = new CadRecoveryStore(root, storage))
        {
            await writer.WriteAsync(id, DocumentSnapshot.Create("Good"), assets, null); storage.FailAfterWrite = true;
            await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(id, DocumentSnapshot.Create("Partial"), assets, null));
        }
        using var reader = new CadRecoveryStore(root, real); var entry = Assert.Single((await reader.ScanAsync()).Entries);
        using var recovered = await reader.OpenAsync(entry.Key, assets); Assert.Equal("Good", recovered.Document.Snapshot.Name);
        Assert.Single(Directory.GetFiles(root, "*.cadoryx", SearchOption.AllDirectories));
    }

    [Fact] public async Task EditingDuringCheckpointIsCapturedOnTheNextTick()
    {
        using var files = new TestFiles(); var storage = new ControlledStorage(new CadDocumentStorage()) { Pause = true }; var assets = new MemoryAssetStore();
        string root = files.PathFor("recovery");
        using (var store = new CadRecoveryStore(root, storage))
        {
            var recovery = new DocumentRecoveryService(store);
            await using var session = new CadDocumentSession(DocumentSnapshot.Create("Before"), assets, new OcctGeometryKernel(), new InlineSessionDispatcher());
            recovery.Track(session); var task = recovery.CheckpointAsync(session); await storage.Started.Task;
            await session.ExecuteAsync(new EditDocumentCommand("name", d => d with { Name = "After" }));
            storage.Release.TrySetResult(); await task; Assert.True(session.IsDirty);
            await recovery.CheckpointAllAsync();
        }
        using var reader = new CadRecoveryStore(root, new CadDocumentStorage());
        var entry = Assert.Single((await reader.ScanAsync()).Entries); Assert.Equal("After", entry.Name); Assert.Equal(2, entry.SnapshotCount);
    }

    [Fact] public async Task CloseWaitsForInflightCheckpointThenDeletesOnlyItsSession()
    {
        using var files = new TestFiles(); var storage = new ControlledStorage(new CadDocumentStorage()) { Pause = true }; var assets = new MemoryAssetStore();
        string root = files.PathFor("recovery");
        using (var store = new CadRecoveryStore(root, storage))
        {
            var recovery = new DocumentRecoveryService(store);
            await using var a = new CadDocumentSession(DocumentSnapshot.Create("Close me"), assets, new OcctGeometryKernel(), new InlineSessionDispatcher());
            await using var b = new CadDocumentSession(DocumentSnapshot.Create("Keep me"), assets, new OcctGeometryKernel(), new InlineSessionDispatcher());
            recovery.Track(a); recovery.Track(b);
            var write = recovery.CheckpointAsync(a); await storage.Started.Task;
            var close = recovery.ForgetAsync(a); Assert.False(close.IsCompleted);
            storage.Release.TrySetResult(); await Task.WhenAll(write, close); await recovery.CheckpointAllAsync();
        }
        using var reader = new CadRecoveryStore(root, new CadDocumentStorage());
        Assert.Equal("Keep me", Assert.Single((await reader.ScanAsync()).Entries).Name);
    }

    [Fact] public async Task SaveAndUndoToSavepointRemoveStaleRecoveryButNewDirtyStateCanReturn()
    {
        using var files = new TestFiles(); var storage = new CadDocumentStorage(); var assets = new MemoryAssetStore(); string root = files.PathFor("recovery");
        using (var store = new CadRecoveryStore(root, storage))
        {
            var recovery = new DocumentRecoveryService(store);
            await using var session = new CadDocumentSession(DocumentSnapshot.Create("Saved"), assets, new OcctGeometryKernel(), new InlineSessionDispatcher());
            recovery.Track(session); await recovery.CheckpointAsync(session);
            await session.SaveAsync(storage, files.PathFor("model.cadoryx")); await recovery.CheckpointAsync(session);
            Assert.Empty(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories));
            await session.ExecuteAsync(new EditDocumentCommand("name", d => d with { Name = "Changed" })); await recovery.CheckpointAsync(session);
            await session.UndoAsync(); await recovery.CheckpointAsync(session);
            Assert.Empty(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories));
            await session.RedoAsync(); await recovery.CheckpointAsync(session);
        }
        using var reader = new CadRecoveryStore(root, storage); Assert.Equal("Changed", Assert.Single((await reader.ScanAsync()).Entries).Name);
    }

    [Fact] public async Task SourceClaimPreventsConcurrentRecoveryAndRetirementFollowsDurableCopy()
    {
        using var files = new TestFiles(); var storage = new CadDocumentStorage(); var assets = new MemoryAssetStore(); string root = files.PathFor("recovery");
        using (var writer = new CadRecoveryStore(root, storage)) await writer.WriteAsync(Guid.NewGuid(), DocumentSnapshot.Create("Source"), assets, null);
        using var reader = new CadRecoveryStore(root, storage); using var other = new CadRecoveryStore(root, storage);
        var entry = Assert.Single((await reader.ScanAsync()).Entries);
        using (var source = await reader.OpenAsync(entry.Key, assets))
        {
            Assert.Empty((await other.ScanAsync()).Entries);
            await Assert.ThrowsAsync<IOException>(() => other.DiscardAsync(entry.Key));
            await reader.WriteAsync(Guid.NewGuid(), source.Document.Snapshot, assets, source.Entry.OriginalPath);
            await source.RetireAsync();
        }
        Assert.Empty((await other.ScanAsync()).Entries); // replacement is still owned by a running reader
        reader.Dispose(); Assert.Single((await other.ScanAsync()).Entries);
    }

    [Fact] public async Task AllCorruptSnapshotsRemainAvailableForDiagnosisAndOtherDocumentsStillList()
    {
        using var files = new TestFiles(); var storage = new CadDocumentStorage(); var assets = new MemoryAssetStore(); string root = files.PathFor("recovery");
        using (var writer = new CadRecoveryStore(root, storage))
        {
            await writer.WriteAsync(Guid.NewGuid(), DocumentSnapshot.Create("Corrupt"), assets, null);
            await writer.WriteAsync(Guid.NewGuid(), DocumentSnapshot.Create("Good"), assets, null);
        }
        var bad = Metadata(root).Single(m => m.Json["name"]!.GetValue<string>() == "Corrupt");
        await File.WriteAllTextAsync(Path.ChangeExtension(bad.Path, ".cadoryx"), "broken");
        using var reader = new CadRecoveryStore(root, storage); var entries = (await reader.ScanAsync()).Entries;
        Assert.Equal(2, entries.Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.OpenAsync(entries.Single(e => e.Name == "Corrupt").Key, assets));
        Assert.True(File.Exists(bad.Path)); Assert.Equal(0, assets.Count);
    }

    [Fact] public async Task CancellationPreservesCommittedSnapshotAndEmptyIdsCannotEscapeRoot()
    {
        using var files = new TestFiles(); var storage = new CadDocumentStorage(); var assets = new MemoryAssetStore(); string root = files.PathFor("recovery");
        using var writer = new CadRecoveryStore(root, storage); var id = Guid.NewGuid();
        await writer.WriteAsync(id, DocumentSnapshot.Create("Keep"), assets, null);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.ClearAsync(id, cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(id, DocumentSnapshot.Create("Canceled"), assets, null, cancel.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => writer.ClearAsync(Guid.Empty));
        Assert.Single(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories));
    }

    [Fact] public async Task MetadataCannotRedirectPayloadOutsideRecoveryRoot()
    {
        using var files = new TestFiles(); var storage = new CadDocumentStorage(); var assets = new MemoryAssetStore(); string root = files.PathFor("recovery");
        string original = files.PathFor("must-not-touch.cadoryx"); await File.WriteAllTextAsync(original, "original");
        using (var writer = new CadRecoveryStore(root, storage)) await writer.WriteAsync(Guid.NewGuid(), DocumentSnapshot.Create("A"), assets, original);
        var record = Metadata(root).Single(); record.Json["revision"] = "../../must-not-touch";
        await File.WriteAllTextAsync(record.Path, record.Json.ToJsonString());
        using var reader = new CadRecoveryStore(root, storage); var scan = await reader.ScanAsync();
        Assert.Empty(scan.Entries); Assert.NotEmpty(scan.Diagnostics); Assert.Equal("original", await File.ReadAllTextAsync(original));
    }

    [Fact] public async Task OneDocumentWriteFailureDoesNotStopOtherCheckpointsAndCanBeRetried()
    {
        using var files = new TestFiles(); var storage = new ControlledStorage(new CadDocumentStorage()) { FailName = "Fail" };
        var assets = new MemoryAssetStore(); string root = files.PathFor("recovery");
        using (var store = new CadRecoveryStore(root, storage))
        {
            var recovery = new DocumentRecoveryService(store); var errors = new List<Exception>(); recovery.Failed += (_, ex) => errors.Add(ex);
            await using var a = new CadDocumentSession(DocumentSnapshot.Create("Fail"), assets, new OcctGeometryKernel(), new InlineSessionDispatcher());
            await using var b = new CadDocumentSession(DocumentSnapshot.Create("Good"), assets, new OcctGeometryKernel(), new InlineSessionDispatcher());
            recovery.Track(a); recovery.Track(b); await recovery.CheckpointAllAsync();
            Assert.Single(errors); Assert.Equal("Good", Metadata(root).Single().Json["name"]!.GetValue<string>());
            storage.FailName = null; await recovery.CheckpointAllAsync(); Assert.Equal(2, Metadata(root).Length);
        }
    }

    [Fact] public async Task FailedDurableReplacementLeavesSourceAvailableForNextRestart()
    {
        using var files = new TestFiles(); var storage = new CadDocumentStorage(); var assets = new MemoryAssetStore(); string root = files.PathFor("recovery");
        using (var writer = new CadRecoveryStore(root, storage)) await writer.WriteAsync(Guid.NewGuid(), DocumentSnapshot.Create("Keep source"), assets, "source.cadoryx");
        using (var reader = new CadRecoveryStore(root, new ControlledStorage(storage) { FailAfterWrite = true }))
        {
            var entry = Assert.Single((await reader.ScanAsync()).Entries);
            using var source = await reader.OpenAsync(entry.Key, assets);
            await Assert.ThrowsAsync<IOException>(() => reader.WriteAsync(Guid.NewGuid(), source.Document.Snapshot, assets, source.Entry.OriginalPath));
            // No RetireAsync: a failed handover must retain the old durable source.
        }
        using var restarted = new CadRecoveryStore(root, storage);
        var remaining = Assert.Single((await restarted.ScanAsync()).Entries);
        using var recovered = await restarted.OpenAsync(remaining.Key, assets); Assert.Equal("Keep source", recovered.Document.Snapshot.Name);
    }

    [Fact] public async Task NormalCloseRemovesEmptyDirectoriesAcrossRepeatedRuns()
    {
        using var files = new TestFiles(); var storage = new CadDocumentStorage(); var assets = new MemoryAssetStore(); string root = files.PathFor("recovery");
        for (int i = 0; i < 8; i++)
        {
            using var store = new CadRecoveryStore(root, storage); var id = Guid.NewGuid();
            await store.WriteAsync(id, DocumentSnapshot.Create("Closed"), assets, null); await store.ClearAsync(id);
        }
        Assert.Empty(Directory.EnumerateDirectories(root));
    }

    [Fact] public async Task ClockMovingBackwardsDoesNotSelectOrRetainAnOlderState()
    {
        using var files = new TestFiles(); var storage = new CadDocumentStorage(); var assets = new MemoryAssetStore(); string root = files.PathFor("recovery");
        using (var store = new CadRecoveryStore(root, storage))
        {
            var id = Guid.NewGuid(); await store.WriteAsync(id, DocumentSnapshot.Create("Old"), assets, null);
            var old = Metadata(root).Single(); old.Json["capturedAt"] = DateTimeOffset.UtcNow.AddYears(1);
            await File.WriteAllTextAsync(old.Path, old.Json.ToJsonString());
            await store.WriteAsync(id, DocumentSnapshot.Create("New"), assets, null);
            await store.WriteAsync(id, DocumentSnapshot.Create("Newest"), assets, null);
            Assert.DoesNotContain(Metadata(root), m => m.Json["name"]!.GetValue<string>() == "Old");
        }
        using var reader = new CadRecoveryStore(root, storage); var entry = Assert.Single((await reader.ScanAsync()).Entries);
        Assert.Equal("Newest", entry.Name); using var recovered = await reader.OpenAsync(entry.Key, assets);
        Assert.Equal("Newest", recovered.Document.Snapshot.Name);
    }

    [Fact] public async Task RecoveryCenterKeepsFailedRestoresAndCanceledDiscardsAvailable()
    {
        using var files = new TestFiles(); var storage = new CadDocumentStorage(); var assets = new MemoryAssetStore(); string root = files.PathFor("recovery");
        using (var writer = new CadRecoveryStore(root, storage)) await writer.WriteAsync(Guid.NewGuid(), DocumentSnapshot.Create("Keep"), assets, null);
        using var reader = new CadRecoveryStore(root, storage); bool confirmed = false;
        var vm = new Cadoryx.ViewModels.RecoveryCenterViewModel(reader, _ => Task.FromResult(false), _ => confirmed);
        await vm.RefreshCommand.ExecuteAsync(null); Assert.Single(vm.Entries);
        await vm.RestoreCommand.ExecuteAsync(null); Assert.Single(vm.Entries); Assert.False(vm.IsBusy);
        await vm.DiscardCommand.ExecuteAsync(null); Assert.Single(vm.Entries);
        confirmed = true; await vm.DiscardCommand.ExecuteAsync(null); Assert.Empty(vm.Entries);
        Assert.Empty(Directory.EnumerateDirectories(root));
    }

    private static (string Path, JsonObject Json)[] Metadata(string root) => Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
        .Select(path => (path, JsonNode.Parse(File.ReadAllText(path))!.AsObject())).ToArray();
    private sealed class ControlledStorage(IDocumentStorage inner) : IDocumentStorage
    {
        public bool FailAfterWrite { get; set; }
        public string? FailName { get; set; }
        public bool Pause { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task SaveAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CancellationToken cancellationToken = default)
        {
            if (Pause) { Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); Pause = false; }
            await inner.SaveAsync(snapshot, assets, path, cancellationToken);
            if (FailAfterWrite || snapshot.Name == FailName) throw new IOException("Injected failure after payload write.");
        }
        public Task<LoadedDocument> LoadAsync(string path, IAssetStore assets, CancellationToken cancellationToken = default) => inner.LoadAsync(path, assets, cancellationToken);
        public Task<DocumentSettings> ReadSettingsAsync(string path, CancellationToken cancellationToken = default) => inner.ReadSettingsAsync(path, cancellationToken);
    }
}
