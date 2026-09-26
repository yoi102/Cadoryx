using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Occt;
using Cadoryx.Kernel.Abstractions;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ExternalPartLinkTests
{
    [Fact] public async Task LinkRefreshConflictMissingSourceAndDetachPreserveSnapshot()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        var sourcePath=files.PathFor("source.cadoryx");
        await using var source=new CadDocumentSession(DocumentSnapshot.Create("Source"),assets,
            new OcctGeometryKernel(),new InlineSessionDispatcher());
        await source.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Source box"));
        var sourcePart=Assert.Single(source.Snapshot.Definitions.Values.OfType<PartDefinition>());
        await source.SaveAsync(storage,sourcePath);

        var targetPath=files.PathFor("target.cadoryx");
        var initial=DocumentSnapshot.Create("Target");
        await using var target=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var partId=DefinitionId.New();var slot=ComponentSlotId.New();
        var link=ExternalPartCommands.Link(storage,sourcePath,new(initial.Id,[]),partId,slot,"Linked box",
            sourcePart.Id,targetPath);
        await target.ExecuteAsync(link);
        Assert.Equal(partId,OccurrencePlacement.Resolve(target.Snapshot,link.ResultPath).Slot.DefinitionId);
        Assert.Equal(ExternalPartStatus.Current,ExternalPartCommands.Check(target.Snapshot,partId,targetPath));
        Assert.Empty(((PartDefinition)target.Snapshot.Definitions[partId]).Features);
        await target.SaveAsync(storage,targetPath);
        using(var loaded=await storage.LoadAsync(targetPath,assets))
            Assert.Equal(source.Snapshot.Id,loaded.Snapshot.ExternalParts[partId].SourceDocumentId);

        await source.ExecuteAsync(new AddBodyCommand(new BoxRecipe(5,5,5,RigidTransform3d.Translate(15,0,0)),
            "Second source box",sourcePart.Id));
        await source.SaveAsync(storage,sourcePath);
        Assert.Equal(ExternalPartStatus.SourceChanged,ExternalPartCommands.Check(target.Snapshot,partId,targetPath));
        await target.ExecuteAsync(ExternalPartCommands.Refresh(storage,partId,targetPath));
        Assert.Equal(2,((PartDefinition)target.Snapshot.Definitions[partId]).Bodies.Length);
        Assert.Equal(ExternalPartStatus.Current,ExternalPartCommands.Check(target.Snapshot,partId,targetPath));
        await target.UndoAsync();Assert.Single(((PartDefinition)target.Snapshot.Definitions[partId]).Bodies);
        await target.RedoAsync();Assert.Equal(2,((PartDefinition)target.Snapshot.Definitions[partId]).Bodies.Length);

        var body=((PartDefinition)target.Snapshot.Definitions[partId]).Bodies[0];
        await target.ExecuteAsync(DocumentEdits.RenameBody(body,"Local edit"));
        Assert.Equal(ExternalPartStatus.LocalChanged,ExternalPartCommands.Check(target.Snapshot,partId,targetPath));
        await Assert.ThrowsAsync<CadValidationException>(()=>target.ExecuteAsync(
            ExternalPartCommands.Refresh(storage,partId,targetPath)));
        await target.UndoAsync();
        File.Move(sourcePath,files.PathFor("moved.cadoryx"));
        Assert.Equal(ExternalPartStatus.SourceMissing,ExternalPartCommands.Check(target.Snapshot,partId,targetPath));
        await target.SaveAsync(storage,targetPath);
        using(var loaded=await storage.LoadAsync(targetPath,assets))
            Assert.Equal(2,((PartDefinition)loaded.Snapshot.Definitions[partId]).Bodies.Length);
        await target.ExecuteAsync(ExternalPartCommands.Detach(partId));
        Assert.False(target.Snapshot.ExternalParts.ContainsKey(partId));
        Assert.Equal(2,((PartDefinition)target.Snapshot.Definitions[partId]).Bodies.Length);
    }

    [Fact] public async Task SourceIdentityReplacementIsRejectedWithoutChangingTarget()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        var path=files.PathFor("source.cadoryx");
        await using var source=new CadDocumentSession(DocumentSnapshot.Create("Source"),assets,
            new OcctGeometryKernel(),new InlineSessionDispatcher());
        await source.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,2,2,RigidTransform3d.Identity),"A"));
        await source.SaveAsync(storage,path);
        var targetDoc=DocumentSnapshot.Create("Target");
        await using var target=new CadDocumentSession(targetDoc,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var partId=DefinitionId.New();
        await target.ExecuteAsync(ExternalPartCommands.Link(storage,path,new(targetDoc.Id,[]),partId,
            ComponentSlotId.New(),"Linked"));
        await using(var replacement=new CadDocumentSession(DocumentSnapshot.Create("Replacement"),assets,
            new OcctGeometryKernel(),new InlineSessionDispatcher()))
        {
            await replacement.ExecuteAsync(new AddBodyCommand(new BoxRecipe(3,3,3,RigidTransform3d.Identity),"B"));
            await replacement.SaveAsync(storage,path);
        }
        var before=target.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>target.ExecuteAsync(
            ExternalPartCommands.Refresh(storage,partId)));
        Assert.Same(before,target.Snapshot);
    }
}
