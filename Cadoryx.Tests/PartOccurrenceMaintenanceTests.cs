using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class PartOccurrenceMaintenanceTests
{
    [Fact] public async Task IndependentPartCopiesEditableGraphAndSharesImmutableGeometry()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var doc=DocumentSnapshot.Create("Parts");
        var partId=DefinitionId.New();var root=new OccurrencePath(doc.Id,[]);
        await using var session=new CadDocumentSession(doc,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(ResourceCommands.AddPart(partId,"Reusable"));
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box",partId));
        var originalBody=Assert.Single(session.Snapshot.Bodies.Values);
        var originalFeature=Assert.Single(session.Snapshot.Features.Values);
        var sketch=CadSketch.Create(partId,"Independent sketch",RigidTransform3d.Identity);
        var reference=TopologyReference.Box(session.Snapshot,originalFeature.Id,BoxBoundary.XMin);
        await session.ExecuteAsync(new EditDocumentCommand("Add references",s=>s with
            {Sketches=s.Sketches.Add(sketch.Id,sketch),TopologyReferences=s.TopologyReferences.Add(reference.Id,reference)}));
        var first=Assert.Single(((AssemblyDefinition)session.Snapshot.Definitions[doc.RootAssemblyId]).Children).Id;
        var second=ComponentSlotId.New();
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,partId,second,"Second",RigidTransform3d.Translate(50,0,0)));
        var command=AssemblyOccurrenceCommands.MakePartIndependent(root.Append(second));
        await session.ExecuteAsync(command);
        Assert.NotNull(command.ResultPartId);
        var cloneId=command.ResultPartId.Value;
        Assert.Equal(root.Append(second),command.ResultPath);
        Assert.Equal(partId,OccurrencePlacement.Resolve(session.Snapshot,root.Append(first)).Slot.DefinitionId);
        Assert.Equal(cloneId,OccurrencePlacement.Resolve(session.Snapshot,root.Append(second)).Slot.DefinitionId);
        var clonedPart=(PartDefinition)session.Snapshot.Definitions[cloneId];
        var clonedBody=session.Snapshot.Bodies[Assert.Single(clonedPart.Bodies)];
        var clonedFeature=session.Snapshot.Features[Assert.Single(clonedPart.Features)];
        Assert.NotEqual(originalBody.Id,clonedBody.Id);
        Assert.NotEqual(originalFeature.Id,clonedFeature.Id);
        Assert.Equal(originalBody.Geometry,clonedBody.Geometry);
        Assert.Equal(clonedFeature.Id,clonedBody.Producer);
        Assert.Equal(clonedBody.Id,clonedFeature.OutputBodyId);
        Assert.Contains(session.Snapshot.Sketches.Values,s=>s.PartId==cloneId&&s.Id!=sketch.Id);
        Assert.Contains(session.Snapshot.TopologyReferences.Values,r=>r.FeatureId==clonedFeature.Id&&r.OutputBodyId==clonedBody.Id);
        await session.ExecuteAsync(DocumentEdits.RenameBody(clonedBody.Id,"Copy only"));
        Assert.Equal("Box",session.Snapshot.Bodies[originalBody.Id].Name);
        var path=files.PathFor("independent-part.cadoryx");
        await session.SaveAsync(new CadDocumentStorage(),path);
        using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        Assert.Equal("Copy only",loaded.Snapshot.Bodies[clonedBody.Id].Name);
        Assert.Equal(originalBody.Geometry,loaded.Snapshot.Bodies[clonedBody.Id].Geometry);
    }

    [Fact] public async Task NestedPartIsolationRemapsOnlyChosenBranch()
    {
        var doc=DocumentSnapshot.Create("Nested part");var root=(AssemblyDefinition)doc.Definitions[doc.RootAssemblyId];
        var group=DefinitionId.New();var part=DefinitionId.New();
        var left=ComponentSlotId.New();var right=ComponentSlotId.New();var child=ComponentSlotId.New();
        doc=doc with{Definitions=doc.Definitions.Add(part,new PartDefinition(part,"Part",[],[]))
            .Add(group,new AssemblyDefinition(group,"Group",[new(child,part,"Part",RigidTransform3d.Identity)]))
            .SetItem(root.Id,root with{Children=[new(left,group,"Left",RigidTransform3d.Identity),
                new(right,group,"Right",RigidTransform3d.Translate(100,0,0))]})};
        await using var session=new CadDocumentSession(doc,new MemoryAssetStore(),new OcctGeometryKernel(),new InlineSessionDispatcher());
        var command=AssemblyOccurrenceCommands.MakePartIndependent(new(doc.Id,[left,child]));
        await session.ExecuteAsync(command);
        var changed=command.ResultPath!;
        Assert.NotEqual(child,changed.Slots[1]);
        Assert.NotEqual(group,OccurrencePlacement.Resolve(session.Snapshot,new(doc.Id,[left])).Slot.DefinitionId);
        Assert.NotEqual(part,OccurrencePlacement.Resolve(session.Snapshot,changed).Slot.DefinitionId);
        Assert.Equal(part,OccurrencePlacement.Resolve(session.Snapshot,new(doc.Id,[right,child])).Slot.DefinitionId);
        session.Snapshot.Validate();
    }

    [Fact] public async Task PruneRespectsDiagnosticsAndRenameScopeAndUndo()
    {
        var doc=DocumentSnapshot.Create("Maintenance");var rootPath=new OccurrencePath(doc.Id,[]);
        var part=DefinitionId.New();
        await using var session=new CadDocumentSession(doc,new MemoryAssetStore(),new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(ResourceCommands.AddPart(part,"Shared"));
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Box",part));
        var feature=Assert.Single(session.Snapshot.Features.Values);
        var first=Assert.Single(((AssemblyDefinition)session.Snapshot.Definitions[doc.RootAssemblyId]).Children).Id;
        var second=ComponentSlotId.New();
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(rootPath,part,second,"Second",RigidTransform3d.Identity));
        await session.ExecuteAsync(AssemblyOccurrenceCommands.RenameOccurrence(rootPath.Append(second),"Only second"));
        Assert.Equal("Shared",OccurrencePlacement.Resolve(session.Snapshot,rootPath.Append(first)).Slot.Name);
        await session.ExecuteAsync(AssemblyOccurrenceCommands.RenameDefinition(part,"Renamed source"));
        Assert.Equal("Renamed source",session.Snapshot.Definitions[part].Name);
        Assert.Equal("Only second",OccurrencePlacement.Resolve(session.Snapshot,rootPath.Append(second)).Slot.Name);
        var reference=TopologyReference.Box(session.Snapshot,feature.Id,BoxBoundary.XMin);
        await session.ExecuteAsync(new EditDocumentCommand("Keep diagnostic",s=>s with
            {TopologyReferences=s.TopologyReferences.Add(reference.Id,reference)}));
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Remove(rootPath.Append(second)));
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Remove(rootPath.Append(first)));
        Assert.Equal(0,UnusedDefinitionCommands.CountRemovable(session.Snapshot));
        await session.ExecuteAsync(UnusedDefinitionCommands.Prune());
        Assert.Contains(part,session.Snapshot.Definitions.Keys);
        await session.ExecuteAsync(new EditDocumentCommand("Remove diagnostic",s=>s with
            {TopologyReferences=s.TopologyReferences.Remove(reference.Id)}));
        Assert.Equal(1,UnusedDefinitionCommands.CountRemovable(session.Snapshot));
        var before=session.Snapshot;
        await session.ExecuteAsync(UnusedDefinitionCommands.Prune());
        Assert.DoesNotContain(part,session.Snapshot.Definitions.Keys);
        Assert.Empty(session.Snapshot.Bodies);
        Assert.Empty(session.Snapshot.Features);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
    }
}
