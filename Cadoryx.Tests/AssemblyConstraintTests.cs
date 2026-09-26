using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AssemblyConstraintTests
{
    private static (DocumentSnapshot Document,OccurrencePath A,OccurrencePath B,DefinitionId PartA,DefinitionId PartB) Pair()
    {
        var doc=DocumentSnapshot.Create("Relations");var root=(AssemblyDefinition)doc.Definitions[doc.RootAssemblyId];
        var partA=DefinitionId.New();var partB=DefinitionId.New();var a=ComponentSlotId.New();var b=ComponentSlotId.New();
        doc=doc with{Definitions=doc.Definitions.Add(partA,new PartDefinition(partA,"A",[],[]))
            .Add(partB,new PartDefinition(partB,"B",[],[]))
            .SetItem(root.Id,root with{Children=[new(a,partA,"A",RigidTransform3d.Identity),
                new(b,partB,"B",RigidTransform3d.Translate(10,0,0))]})};
        return (doc,new(doc.Id,[a]),new(doc.Id,[b]),partA,partB);
    }

    [Fact] public async Task FixedPoseBlocksMotionUntilExplicitDisableAndRecaptureAndRoundtrips()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var seed=Pair();
        await using var session=new CadDocumentSession(seed.Document,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var id=AssemblyConstraintId.New();
        await session.ExecuteAsync(AssemblyConstraintCommands.AddFixed(id,"Ground A",seed.A));
        Assert.Equal(AssemblyConstraintStatus.Satisfied,session.Snapshot.AssemblyConstraints[id].Evaluate(session.Snapshot).Status);
        var provenance=new TopologyReference(TopologyReferenceId.New(),seed.Document.Id,FeatureId.New(),BodyId.New(),
            GeometryRevisionId.New(),TopologyKind.Face,BoxBoundary.XMin,Policy:TopologyRebindPolicy.ExactRevision);
        await session.ExecuteAsync(AssemblyConstraintCommands.SetTopologyAnchor(id,true,provenance));
        Assert.Equal(AssemblyConstraintStatus.TopologyStale,session.Snapshot.AssemblyConstraints[id].Evaluate(session.Snapshot).Status);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(
            DocumentEdits.MoveOccurrence(seed.A,RigidTransform3d.Translate(4,0,0))));
        await session.ExecuteAsync(AssemblyConstraintCommands.SetTopologyAnchor(id,true,null));
        var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(
            DocumentEdits.MoveOccurrence(seed.A,RigidTransform3d.Translate(4,0,0))));
        Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(AssemblyConstraintCommands.SetEnabled(id,false));
        await session.ExecuteAsync(DocumentEdits.MoveOccurrence(seed.A,RigidTransform3d.Translate(4,0,0)));
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(AssemblyConstraintCommands.SetEnabled(id,true)));
        await session.ExecuteAsync(AssemblyConstraintCommands.Retarget(id,seed.A));
        await session.ExecuteAsync(AssemblyConstraintCommands.SetEnabled(id,true));
        Assert.Equal(AssemblyConstraintStatus.Satisfied,session.Snapshot.AssemblyConstraints[id].Evaluate(session.Snapshot).Status);
        var path=files.PathFor("fixed.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        Assert.Equal(id,Assert.Single(loaded.Snapshot.AssemblyConstraints.Keys));
        Assert.Equal(AssemblyConstraintStatus.Satisfied,loaded.Snapshot.AssemblyConstraints[id].Evaluate(loaded.Snapshot).Status);
        var moved=session.Snapshot;await session.UndoAsync();await session.RedoAsync();Assert.Same(moved,session.Snapshot);
    }

    [Fact] public async Task PointPairAdjustmentPreservesRotationAndRejectsConflictsOrUndefinedDirection()
    {
        var seed=Pair();await using var session=new CadDocumentSession(seed.Document,new MemoryAssetStore(),
            new OcctGeometryKernel(),new InlineSessionDispatcher());
        var first=AssemblyConstraintId.New();
        await session.ExecuteAsync(AssemblyConstraintCommands.AddPair(first,"Keep 10",AssemblyConstraintKind.Distance,
            seed.A,seed.B,Vector3d.Zero,Vector3d.Zero,10));
        var second=AssemblyConstraintId.New();
        await session.ExecuteAsync(AssemblyConstraintCommands.AddPair(second,"Want 5",AssemblyConstraintKind.Distance,
            seed.A,seed.B,Vector3d.Zero,Vector3d.Zero,5));
        var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(AssemblyConstraintCommands.AdjustPair(second)));
        Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(AssemblyConstraintCommands.SetEnabled(first,false));
        await session.ExecuteAsync(AssemblyConstraintCommands.AdjustPair(second));
        Assert.Equal(AssemblyConstraintStatus.Satisfied,session.Snapshot.AssemblyConstraints[second].Evaluate(session.Snapshot).Status);
        Assert.Equal(new Vector3d(5,0,0),OccurrencePlacement.Resolve(session.Snapshot,seed.B).Slot.LocalTransform.Translation);
        await session.ExecuteAsync(AssemblyConstraintCommands.Remove(second));
        var coincide=AssemblyConstraintId.New();
        await session.ExecuteAsync(AssemblyConstraintCommands.AddPair(coincide,"Contact",AssemblyConstraintKind.Coincident,
            seed.A,seed.B,new(2,0,0),new(0,1,0),0));
        await session.ExecuteAsync(AssemblyConstraintCommands.AdjustPair(coincide));
        Assert.Equal(AssemblyConstraintStatus.Satisfied,session.Snapshot.AssemblyConstraints[coincide].Evaluate(session.Snapshot).Status);
        Assert.Equal(new Vector3d(2,-1,0),OccurrencePlacement.Resolve(session.Snapshot,seed.B).Slot.LocalTransform.Translation);
        await session.ExecuteAsync(AssemblyConstraintCommands.Remove(coincide));
        await session.ExecuteAsync(DocumentEdits.MoveOccurrence(seed.B,RigidTransform3d.Identity));
        var zero=AssemblyConstraintId.New();
        await session.ExecuteAsync(AssemblyConstraintCommands.AddPair(zero,"No direction",AssemblyConstraintKind.Distance,
            seed.A,seed.B,Vector3d.Zero,Vector3d.Zero,5));
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(AssemblyConstraintCommands.AdjustPair(zero)));
    }

    [Fact] public async Task StalePathsDefinitionsAndTopologyNeverRebindImplicitly()
    {
        var seed=Pair();await using var session=new CadDocumentSession(seed.Document,new MemoryAssetStore(),
            new OcctGeometryKernel(),new InlineSessionDispatcher());
        var id=AssemblyConstraintId.New();
        await session.ExecuteAsync(AssemblyConstraintCommands.AddPair(id,"Pair",AssemblyConstraintKind.Distance,
            seed.A,seed.B,Vector3d.Zero,Vector3d.Zero,10));
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Replace(seed.B,seed.PartA));
        Assert.Equal(AssemblyConstraintStatus.DefinitionChanged,session.Snapshot.AssemblyConstraints[id].Evaluate(session.Snapshot).Status);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(AssemblyConstraintCommands.AdjustPair(id)));
        await session.ExecuteAsync(AssemblyConstraintCommands.Retarget(id,seed.A,seed.B));
        Assert.Equal(AssemblyConstraintStatus.Satisfied,session.Snapshot.AssemblyConstraints[id].Evaluate(session.Snapshot).Status);
        var provenance=new TopologyReference(TopologyReferenceId.New(),seed.Document.Id,FeatureId.New(),BodyId.New(),
            GeometryRevisionId.New(),TopologyKind.Face,BoxBoundary.XMin,Policy:TopologyRebindPolicy.ExactRevision);
        await session.ExecuteAsync(AssemblyConstraintCommands.SetTopologyAnchor(id,true,provenance));
        Assert.Equal(AssemblyConstraintStatus.TopologyStale,session.Snapshot.AssemblyConstraints[id].Evaluate(session.Snapshot).Status);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(AssemblyConstraintCommands.Retarget(id,seed.A,seed.B)));
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Remove(seed.B));
        Assert.Equal(AssemblyConstraintStatus.MissingInstance,session.Snapshot.AssemblyConstraints[id].Evaluate(session.Snapshot).Status);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(
            new(seed.Document.Id,[]),seed.PartB,seed.B.Slots[^1],"Reused",RigidTransform3d.Identity)));
        Assert.Equal(1,UnusedDefinitionCommands.CountRemovable(session.Snapshot));
    }

    [Fact] public void DocumentV12MigratesToAnExplicitEmptyConstraintTable()
    {
        var old=new PackDocument(Guid.NewGuid(),Guid.NewGuid(),"Legacy",Guid.NewGuid(),0,3,1e-7,1e-9,
            true,10,false,DocumentSettings.DefaultBackgroundTopArgb,DocumentSettings.DefaultBackgroundBottomArgb,
            true,0,20);
        var bytes=MessagePackSerializer.Serialize(old);
        var registry=new CadSectionMigrationRegistry();
        var migrated=registry.Migrate([new(new("document",12,"messagepack"),bytes)],
            new Dictionary<string,SectionFormat>{{"document",CadSectionMigrationRegistry.CurrentFormats["document"]}});
        Assert.Equal(16,migrated["document"].Format.Version);
        Assert.Empty(MessagePackSerializer.Deserialize<PackDocument>(migrated["document"].Bytes).AssemblyConstraints!);
    }

    [Fact] public async Task CoaxialAdjustmentAlignsDirectionsAndPreservesAxialSlideAndRoundtrips()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var seed=Pair();
        await using var session=new CadDocumentSession(seed.Document,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(DocumentEdits.MoveOccurrence(seed.B,new(new(10,0,5),
            Quaterniond.FromAxisAngle(new(0,1,0),Math.PI/2))));
        var id=AssemblyConstraintId.New();
        await session.ExecuteAsync(AssemblyConstraintCommands.AddAxisPair(id,"Coaxial",AssemblyConstraintKind.Coaxial,
            seed.A,seed.B,Vector3d.Zero,Vector3d.Zero,Vector3d.UnitZ,Vector3d.UnitZ));
        Assert.Equal(AssemblyConstraintStatus.Unsatisfied,session.Snapshot.AssemblyConstraints[id].Evaluate(session.Snapshot).Status);
        await session.ExecuteAsync(AssemblyConstraintCommands.AdjustPair(id));
        var result=session.Snapshot.AssemblyConstraints[id].Evaluate(session.Snapshot);
        Assert.Equal(AssemblyConstraintStatus.Satisfied,result.Status);
        Assert.Equal(new Vector3d(0,0,5),OccurrencePlacement.Resolve(session.Snapshot,seed.B).Slot.LocalTransform.Translation);
        var path=files.PathFor("coaxial.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        Assert.Equal(AssemblyConstraintKind.Coaxial,loaded.Snapshot.AssemblyConstraints[id].Kind);
        Assert.Equal(AssemblyConstraintStatus.Satisfied,loaded.Snapshot.AssemblyConstraints[id].Evaluate(loaded.Snapshot).Status);
        await session.UndoAsync();Assert.Equal(AssemblyConstraintStatus.Unsatisfied,
            session.Snapshot.AssemblyConstraints[id].Evaluate(session.Snapshot).Status);
        await session.RedoAsync();Assert.Equal(AssemblyConstraintStatus.Satisfied,
            session.Snapshot.AssemblyConstraints[id].Evaluate(session.Snapshot).Status);
    }

    [Fact] public async Task ParallelAdjustmentKeepsAnchorAndExistingFixedRelationBlocksMovement()
    {
        var seed=Pair();await using var session=new CadDocumentSession(seed.Document,new MemoryAssetStore(),
            new OcctGeometryKernel(),new InlineSessionDispatcher());
        var fixedId=AssemblyConstraintId.New();await session.ExecuteAsync(AssemblyConstraintCommands.AddFixed(fixedId,"Ground B",seed.B));
        var id=AssemblyConstraintId.New();
        await session.ExecuteAsync(AssemblyConstraintCommands.AddAxisPair(id,"Parallel",AssemblyConstraintKind.ParallelAxes,
            seed.A,seed.B,Vector3d.Zero,new(1,0,0),Vector3d.UnitZ,new(1,0,0)));
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(AssemblyConstraintCommands.AdjustPair(id)));
        await session.ExecuteAsync(AssemblyConstraintCommands.SetEnabled(fixedId,false));
        var before=session.Snapshot.EnumerateOccurrences().Single(o=>o.Path.Equals(seed.B)).WorldTransform.Apply(new(1,0,0));
        await session.ExecuteAsync(AssemblyConstraintCommands.AdjustPair(id));
        var after=session.Snapshot.EnumerateOccurrences().Single(o=>o.Path.Equals(seed.B)).WorldTransform.Apply(new(1,0,0));
        Assert.True((after-before).Length<1e-7);
        Assert.Equal(AssemblyConstraintStatus.Satisfied,session.Snapshot.AssemblyConstraints[id].Evaluate(session.Snapshot).Status);
        await session.ExecuteAsync(AssemblyConstraintCommands.SetAxes(id,new(0,0,2),new(0,1,0)));
        Assert.Equal(Vector3d.UnitZ,session.Snapshot.AssemblyConstraints[id].PrimaryLocalAxis);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(
            AssemblyConstraintCommands.SetAxes(id,Vector3d.Zero,Vector3d.UnitZ)));
        await session.ExecuteAsync(AssemblyConstraintCommands.SetAnchors(id,new(2,0,0),new(0,3,0)));
        Assert.Equal(new Vector3d(0,3,0),session.Snapshot.AssemblyConstraints[id].SecondaryLocalPoint);
    }

    [Fact] public void DocumentV13AddsOptionalAxesWithoutChangingPointRelations()
    {
        var id=Guid.NewGuid();var a=Guid.NewGuid();var b=Guid.NewGuid();
        var old=new PackDocument(Guid.NewGuid(),Guid.NewGuid(),"Legacy",Guid.NewGuid(),0,3,1e-7,1e-9,
            true,10,false,DocumentSettings.DefaultBackgroundTopArgb,DocumentSettings.DefaultBackgroundBottomArgb,
            true,0,20,0,0,[new(id,"Pair",(int)AssemblyConstraintKind.Distance,[a],Guid.NewGuid(),[b],Guid.NewGuid(),
                [0d,0,0],[0d,0,0],10,null,null,null,true,1)]);
        var migrated=new CadSectionMigrationRegistry().Migrate([new(new("document",13,"messagepack"),
            MessagePackSerializer.Serialize(old))],new Dictionary<string,SectionFormat>
            {{"document",CadSectionMigrationRegistry.CurrentFormats["document"]}});
        Assert.Equal(16,migrated["document"].Format.Version);
        var restored=Assert.Single(MessagePackSerializer.Deserialize<PackDocument>(migrated["document"].Bytes).AssemblyConstraints!);
        Assert.Null(restored.PrimaryAxis);Assert.Null(restored.SecondaryAxis);
        Assert.Equal(id,restored.Id);
    }
}
