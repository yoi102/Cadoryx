using System.IO.Compression;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Commands;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;
namespace Cadoryx.Tests;
public sealed class StorageTests
{
    [Fact] public async Task NativeAssetsFeaturesAndUnitsRoundTrip()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Roundtrip"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            await session.SaveAsync(storage,files.PathFor("model.cadoryx"));Assert.False(session.IsDirty);
            var saved=session.Snapshot;
            await session.ExecuteAsync(new EditDocumentCommand("units",d=>d with{Settings=d.Settings with{DisplayUnit=LengthUnit.Inch}}));
            Assert.True(session.IsDirty);await session.UndoAsync();Assert.False(session.IsDirty);
            using var loaded=await storage.LoadAsync(files.PathFor("model.cadoryx"),assets);
            Assert.Equal(saved.Id,loaded.Snapshot.Id);Assert.Equal(saved.StateId,loaded.Snapshot.StateId);
            Assert.Equal(saved.Bodies.Keys,loaded.Snapshot.Bodies.Keys);
            Assert.Equal(saved.Bodies.Values.Single().Geometry,loaded.Snapshot.Bodies.Values.Single().Geometry);
            Assert.IsType<BoxRecipe>(loaded.Snapshot.Features.Values.Single().Recipe);
            Assert.Equal(LengthUnit.Millimeter,(await storage.ReadSettingsAsync(files.PathFor("model.cadoryx"))).DisplayUnit);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task TamperedPayloadRejectedWithoutAssetLeak()
    {
        using var files=new TestFiles();var path=files.PathFor("bad.cadoryx");var store=new MemoryAssetStore();var storage=new CadDocumentStorage();
        await storage.SaveAsync(DocumentSnapshot.Create("Good"),store,path);
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            var entry=zip.GetEntry("sections/document.msgpack")!;entry.Delete();
            using var writer=new StreamWriter(zip.CreateEntry("sections/document.msgpack").Open());writer.Write("{}");
        }
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,store));Assert.Equal(0,store.Count);
    }
    [Fact] public async Task DuplicateOrUnsafeEntryRejected()
    {
        using var files=new TestFiles();var path=files.PathFor("bad.cadoryx");var storage=new CadDocumentStorage();var store=new MemoryAssetStore();
        await storage.SaveAsync(DocumentSnapshot.Create("Good"),store,path);
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update)){using var writer=new StreamWriter(zip.CreateEntry("../escape").Open());writer.Write("bad");}
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,store));
    }
    [Fact] public void MigrationMustHaveEveryStep()
    {
        var registry=new CadSectionMigrationRegistry();byte[] json="{\"a\":1}"u8.ToArray();
        Assert.Throws<NotSupportedException>(()=>registry.Read<JsonElement>("sample",1,2,json));
        registry.Register("sample",1,old=>JsonSerializer.SerializeToElement(new{a=old.GetProperty("a").GetInt32()+1}));
        Assert.Equal(2,registry.Read<JsonElement>("sample",1,2,json).GetProperty("a").GetInt32());
        Assert.Throws<NotSupportedException>(()=>registry.Read<JsonElement>("sample",3,2,json));
    }
    [Fact] public async Task SaveCapturesStateWithoutClearingNewerEdits()
    {
        using var files=new TestFiles();var store=new MemoryAssetStore();await using var session=new CadDocumentSession(DocumentSnapshot.Create("A"),store,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var storage=new PausedStorage();var save=session.SaveAsync(storage,files.PathFor("paused.cadoryx"));await storage.Started.Task;
        await session.ExecuteAsync(new EditDocumentCommand("rename",d=>d with{Name="B"}));storage.Release.SetResult();await save;
        Assert.True(session.IsDirty);Assert.Equal("A",storage.Saved!.Name);
        await session.UndoAsync();Assert.False(session.IsDirty);
    }
    private sealed class PausedStorage:IDocumentStorage
    {
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DocumentSnapshot? Saved {get;private set;}
        public async Task SaveAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken token=default){Saved=snapshot;Started.SetResult();await Release.Task;}
        public Task<LoadedDocument> LoadAsync(string path,IAssetStore assets,CancellationToken token=default)=>throw new NotSupportedException();
        public Task<DocumentSettings> ReadSettingsAsync(string path,CancellationToken token=default)=>throw new NotSupportedException();
    }
}
internal sealed class TestFiles:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"CadoryxTests",Guid.NewGuid().ToString("N"));
    public TestFiles()=>Directory.CreateDirectory(root);
    public string PathFor(string file)=>Path.Combine(root,file);
    public void Dispose()
    {
        var boundary=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"CadoryxTests"))+Path.DirectorySeparatorChar;
        if(!Path.GetFullPath(root).StartsWith(boundary,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException();
        Directory.Delete(root,true);
    }
}
