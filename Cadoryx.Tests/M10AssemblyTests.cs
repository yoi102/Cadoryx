using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class M10AssemblyTests
{
    private static (DocumentSnapshot Snapshot,OccurrencePath A,OccurrencePath B,OccurrencePath C) Three()
    {
        var document=DocumentSnapshot.Create("M10");var root=(AssemblyDefinition)document.Definitions[document.RootAssemblyId];
        var ids=new[]{DefinitionId.New(),DefinitionId.New(),DefinitionId.New()};
        var slots=new[]{ComponentSlotId.New(),ComponentSlotId.New(),ComponentSlotId.New()};
        var definitions=document.Definitions;
        for(int i=0;i<3;i++)definitions=definitions.Add(ids[i],new PartDefinition(ids[i],"Part "+i,[],[]));
        definitions=definitions.SetItem(root.Id,root with{Children=[
            new(slots[0],ids[0],"A",RigidTransform3d.Identity),
            new(slots[1],ids[1],"B",RigidTransform3d.Translate(5,2,0)),
            new(slots[2],ids[2],"C",RigidTransform3d.Translate(10,1,0))]});
        document=document with{Definitions=definitions};
        return(document,new(document.Id,[slots[0]]),new(document.Id,[slots[1]]),
            new(document.Id,[slots[2]]));
    }

    [Fact] public async Task ClosedLoopSolvesAtomicallyAndReportsRedundancyAndFreedom()
    {
        var seed=Three();await using var session=new CadDocumentSession(seed.Snapshot,new MemoryAssetStore(),
            new OcctGeometryKernel(),new InlineSessionDispatcher());
        foreach(var (a,b) in new[]{(seed.A,seed.B),(seed.B,seed.C),(seed.C,seed.A)})
            await session.ExecuteAsync(AssemblyConstraintCommands.AddPair(AssemblyConstraintId.New(),
                "Coincident",AssemblyConstraintKind.Coincident,a,b,Vector3d.Zero,Vector3d.Zero,0));
        var before=session.Snapshot;var plan=AssemblySolveCommands.Plan(before);
        Assert.Equal(AssemblySolveStatus.UnderConstrained,plan.Report.Status);
        Assert.Single(plan.Report.UnanchoredRoots);
        var freedom=Assert.Single(plan.Report.Components);
        Assert.True(freedom.RedundantEquations>0);
        Assert.Equal(freedom.LocalFreedom,freedom.NullspaceModes.Length);
        Assert.All(freedom.NullspaceModes,mode=>Assert.NotEmpty(mode));
        Assert.NotEmpty(plan.Report.RedundantRelations);
        Assert.Equal(3,plan.Report.Residuals.Length);
        Assert.All(plan.Report.Residuals,r=>Assert.Equal(AssemblyConstraintStatus.Satisfied,r.Status));
        Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(AssemblySolveCommands.Solve());
        Assert.NotSame(before,session.Snapshot);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
    }

    [Fact] public async Task IncompatibleFixedDriversNeverPublishPartialPose()
    {
        var seed=Three();await using var session=new CadDocumentSession(seed.Snapshot,new MemoryAssetStore(),
            new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(AssemblyConstraintCommands.AddFixed(AssemblyConstraintId.New(),"Ground A",seed.A));
        await session.ExecuteAsync(AssemblyConstraintCommands.AddFixed(AssemblyConstraintId.New(),"Ground C",seed.C));
        foreach(var path in new[]{seed.A,seed.C})
            await session.ExecuteAsync(AssemblyConstraintCommands.AddPair(AssemblyConstraintId.New(),"Mate",
                AssemblyConstraintKind.Coincident,path,seed.B,Vector3d.Zero,Vector3d.Zero,0));
        var before=session.Snapshot;var plan=AssemblySolveCommands.Plan(before);
        Assert.Equal(AssemblySolveStatus.Conflict,plan.Report.Status);
        Assert.NotEmpty(plan.Report.Conflicting);Assert.Same(before,plan.Candidate);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(AssemblySolveCommands.Solve()));
        Assert.Same(before,session.Snapshot);
    }

    [Fact] public async Task SimultaneousAxisAndPointRelationsRotateAndTranslateOnePart()
    {
        var seed=Three();await using var session=new CadDocumentSession(seed.Snapshot,new MemoryAssetStore(),
            new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(AssemblyConstraintCommands.AddFixed(AssemblyConstraintId.New(),"Ground",seed.A));
        await session.ExecuteAsync(AssemblyConstraintCommands.AddPair(AssemblyConstraintId.New(),"Origin",
            AssemblyConstraintKind.Coincident,seed.A,seed.B,Vector3d.Zero,Vector3d.Zero,0));
        await session.ExecuteAsync(AssemblyConstraintCommands.AddAxisPair(AssemblyConstraintId.New(),"Cross axes",
            AssemblyConstraintKind.AngleAxes,seed.A,seed.B,Vector3d.Zero,Vector3d.Zero,
            Vector3d.UnitZ,Vector3d.UnitZ,Math.PI/4));
        var plan=AssemblySolveCommands.Plan(session.Snapshot);
        Assert.Equal(AssemblySolveStatus.UnderConstrained,plan.Report.Status);
        Assert.All(plan.Report.Residuals,r=>Assert.Equal(AssemblyConstraintStatus.Satisfied,r.Status));
        Assert.Empty(plan.Report.UnanchoredRoots);
        Assert.True(Assert.Single(plan.Report.Components).LocalFreedom>0);
    }

    [Fact] public async Task OriginalBrepPlaneDatumIsExactPersistedAndStalesAfterRecompute()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Datums"),assets,kernel,
            new InlineSessionDispatcher());
        var recipe=new BoxRecipe(10,20,30,RigidTransform3d.Identity);
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Block"));
        var body=Assert.Single(session.Snapshot.Bodies.Values);var feature=Assert.Single(session.Snapshot.Features.Values);
        var root=(AssemblyDefinition)session.Snapshot.Definitions[session.Snapshot.RootAssemblyId];
        var a=ComponentSlotId.New();var b=ComponentSlotId.New();
        await session.ExecuteAsync(new EditDocumentCommand("Place two instances",d=>d with
        {Definitions=d.Definitions.SetItem(root.Id,root with{Children=[
            new(a,body.PartId,"A",RigidTransform3d.Identity),
            new(b,body.PartId,"B",RigidTransform3d.Translate(25,0,0))]})}));
        var pathA=new OccurrencePath(session.Snapshot.Id,[a]);
        var pathB=new OccurrencePath(session.Snapshot.Id,[b]);
        using var shape=OcctGeometryBridge.ReadShape(body.Geometry,assets);
        using var map=RepairSnapshot.Create(shape);
        var face=map.Topology.First(t=>t.Kind==ShapeKind.Face).Selection.Index;
        var datumA=await kernel.ResolveAssemblyDatumAsync(session.Snapshot,pathA,body.Id,face,map.Fingerprint,assets);
        var datumB=await kernel.ResolveAssemblyDatumAsync(session.Snapshot,pathB,body.Id,face,map.Fingerprint,assets);
        Assert.Equal(AssemblyDatumGeometry.PlaneFace,datumA.Geometry);
        Assert.Equal(body.Geometry.AssetId,datumA.Asset);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(
            AssemblyConstraintCommands.AddDatumPair(AssemblyConstraintId.New(),"Forged",
                AssemblyConstraintKind.Coincident,datumA with{LocalPoint=datumA.LocalPoint+Vector3d.UnitZ},datumB)));
        await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ResolveAssemblyDatumAsync(
            session.Snapshot,pathA,body.Id,face,new string('0',64),assets));
        var relation=AssemblyConstraintId.New();
        await session.ExecuteAsync(AssemblyConstraintCommands.AddDatumPair(relation,"Plane points",
            AssemblyConstraintKind.Coincident,datumA,datumB));
        var path=files.PathFor("datums.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using(var reopened=await new CadDocumentStorage().LoadAsync(path,assets))
        {
            Assert.Equal(map.Fingerprint,reopened.Snapshot.AssemblyConstraints[relation].PrimaryDatum!.Fingerprint);
            Assert.Equal(2,reopened.Snapshot.AssemblyConstraints[relation].SchemaVersion);
        }
        await session.ExecuteAsync(new RecomputeCommand(feature.Id,recipe with{X=12}));
        Assert.Equal(AssemblyConstraintStatus.TopologyStale,
            session.Snapshot.AssemblyConstraints[relation].Evaluate(session.Snapshot).Status);
        var revised=Assert.Single(session.Snapshot.Bodies.Values);
        using var newShape=OcctGeometryBridge.ReadShape(revised.Geometry,assets);
        using var newMap=RepairSnapshot.Create(newShape);
        var newFace=newMap.Topology.First(t=>t.Kind==ShapeKind.Face).Selection.Index;
        var freshA=await kernel.ResolveAssemblyDatumAsync(session.Snapshot,pathA,revised.Id,newFace,
            newMap.Fingerprint,assets);
        var freshB=await kernel.ResolveAssemblyDatumAsync(session.Snapshot,pathB,revised.Id,newFace,
            newMap.Fingerprint,assets);
        await session.ExecuteAsync(AssemblyConstraintCommands.ReselectDatums(relation,freshA,freshB));
        Assert.NotEqual(AssemblyConstraintStatus.TopologyStale,
            session.Snapshot.AssemblyConstraints[relation].Evaluate(session.Snapshot).Status);
    }

    [Fact] public async Task MultiLevelDependencyPreviewOrdersSourcesAndRejectsCycle()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        var cPath=files.PathFor("C.cadoryx");var bPath=files.PathFor("B.cadoryx");
        var aPath=files.PathFor("A.cadoryx");var kernel=new OcctGeometryKernel();
        await using var c=new CadDocumentSession(DocumentSnapshot.Create("C"),assets,kernel,
            new InlineSessionDispatcher());
        await c.ExecuteAsync(new AddBodyCommand(new BoxRecipe(3,4,5,RigidTransform3d.Identity),"Source"));
        var cPart=Assert.Single(c.Snapshot.Definitions.Values.OfType<PartDefinition>());
        await c.SaveAsync(storage,cPath);
        await using var b=new CadDocumentSession(DocumentSnapshot.Create("B"),assets,kernel,
            new InlineSessionDispatcher());
        var bPart=DefinitionId.New();
        await b.ExecuteAsync(ExternalPartCommands.Link(storage,cPath,new(b.Snapshot.Id,[]),bPart,
            ComponentSlotId.New(),"From C",cPart.Id,bPath));
        await b.SaveAsync(storage,bPath);
        await using var a=new CadDocumentSession(DocumentSnapshot.Create("A"),assets,kernel,
            new InlineSessionDispatcher());
        await a.ExecuteAsync(ExternalPartCommands.Link(storage,bPath,new(a.Snapshot.Id,[]),
            DefinitionId.New(),ComponentSlotId.New(),"From B",bPart,aPath));
        await a.SaveAsync(storage,aPath);
        var preview=await ExternalDependencyGraph.PreviewAsync(a.Snapshot,aPath,storage,assets);
        Assert.True(preview.CanRefresh);
        Assert.Equal(new[]{c.Snapshot.Id,b.Snapshot.Id,a.Snapshot.Id},preview.RefreshOrder);
        Assert.Equal(2,preview.Entries.Length);
        var before=c.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>c.ExecuteAsync(ExternalPartCommands.Link(
            storage,aPath,new(c.Snapshot.Id,[]),DefinitionId.New(),ComponentSlotId.New(),"Cycle")));
        Assert.Same(before,c.Snapshot);
    }

    [Fact] public async Task AnalyticCylinderAndCircularEdgeResolveFromLockedNativePackage()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Cylinder datums"),assets,kernel,
            new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(7,12,RigidTransform3d.Identity),"Cylinder"));
        var body=Assert.Single(session.Snapshot.Bodies.Values);
        var root=(AssemblyDefinition)session.Snapshot.Definitions[session.Snapshot.RootAssemblyId];
        var slot=ComponentSlotId.New();
        await session.ExecuteAsync(new EditDocumentCommand("Place cylinder",d=>d with
        {Definitions=d.Definitions.SetItem(root.Id,root with{Children=[
            new(slot,body.PartId,"Cylinder",RigidTransform3d.Identity)]})}));
        var path=new OccurrencePath(session.Snapshot.Id,[slot]);
        using var shape=OcctGeometryBridge.ReadShape(body.Geometry,assets);
        using var map=RepairSnapshot.Create(shape);
        var cylinder=map.Topology.First(t=>t.Kind==ShapeKind.Face&&IsCylinder(map,t.Selection)).Selection.Index;
        var circle=map.Topology.First(t=>t.Kind==ShapeKind.Edge&&IsCircle(map,t.Selection)).Selection.Index;
        var cylindrical=await kernel.ResolveAssemblyDatumAsync(session.Snapshot,path,body.Id,cylinder,
            map.Fingerprint,assets);
        var circular=await kernel.ResolveAssemblyDatumAsync(session.Snapshot,path,body.Id,circle,
            map.Fingerprint,assets);
        Assert.Equal(AssemblyDatumGeometry.CylinderFace,cylindrical.Geometry);
        Assert.Equal(AssemblyDatumGeometry.CircleEdge,circular.Geometry);
        Assert.InRange(cylindrical.RadiusMm,6.999,7.001);
        Assert.InRange(circular.RadiusMm,6.999,7.001);
        Assert.True(Math.Abs(cylindrical.LocalAxis.Dot(Vector3d.UnitZ))>.999);
        Assert.True(Math.Abs(circular.LocalAxis.Dot(Vector3d.UnitZ))>.999);
        static bool IsCylinder(RepairSnapshot map,RepairSelection selection)
        {using var s=map.CopySubshape(selection);return s.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder;}
        static bool IsCircle(RepairSnapshot map,RepairSelection selection)
        {using var s=map.CopySubshape(selection);return s.GetEdgeCurveSnapshot().CurveType==CurveGeometryType.Circle;}
    }
}
