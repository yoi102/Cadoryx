using System.Collections.Immutable;
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

public class ExtrusionTests
{
    private static SketchProfile Square=>new([new(0,0),new(2,0),new(2,2),new(0,2)]);
    [Theory][InlineData(ExtrusionSide.Positive,0,10,40)][InlineData(ExtrusionSide.Negative,-10,0,40)]
    [InlineData(ExtrusionSide.Symmetric,-5,5,40)][InlineData(ExtrusionSide.TwoSides,-3,10,52)]
    public async Task DistanceSidesHaveExactBoundsAndUndo(ExtrusionSide side,double min,double max,double volume)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Sides"),assets,kernel,new InlineSessionDispatcher()))
        {
            var before=session.Snapshot;var r=new ExtrudeRecipe(Square,10,RigidTransform3d.Identity,new(side,side==ExtrusionSide.TwoSides?3:0));
            await session.ExecuteAsync(new AddExtrusionCommand(r,"Extrusion"));var after=session.Snapshot;after.Validate();
            var body=after.Bodies.Values.Single();Assert.Equal(volume,body.Geometry.VolumeMm3,6);Assert.Equal(min,body.Geometry.Bounds.Min.Z,5);Assert.Equal(max,body.Geometry.Bounds.Max.Z,5);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ObliquePlaneClipsActualGeometryIncludingRotatedCoordinates(bool rotate)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var frame=rotate?new RigidTransform3d(new(30,40,50),Quaterniond.FromEulerDegrees(20,70,35)):RigidTransform3d.Identity;
        var plane=frame*new RigidTransform3d(new(0,0,10),Quaterniond.FromEulerDegrees(0,45,0));
        using(var r=await kernel.EvaluateAsync(new ExtrudeRecipe(Square,0,frame,new(Termination:ExtrusionTermination.Plane,LimitPlane:plane)),assets))
            Assert.Equal(36,r.Geometry.VolumeMm3,5); // z=10-x, integrated over [0,2]^2
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(ExtrusionOperation.Cut,ExtrusionTermination.Distance,6,1776)]
    [InlineData(ExtrusionOperation.Cut,ExtrusionTermination.Through,0,1740)]
    [InlineData(ExtrusionOperation.Fuse,ExtrusionTermination.Distance,5,1820)]
    public async Task BooleanTargetsPreserveMetadataAndRecompute(ExtrusionOperation operation,ExtrusionTermination termination,double distance,double expected)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Target"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Target"));var body=session.Snapshot.Bodies.Values.Single();
        var placement=RigidTransform3d.Translate(3,4,15);var side=operation==ExtrusionOperation.Cut?ExtrusionSide.Negative:ExtrusionSide.Positive;
        await session.ExecuteAsync(new AddExtrusionCommand(new(Square,distance,placement,new(side,Operation:operation,Source:body.Geometry,Termination:termination)),"Extrusion",targetBody:body.Id));
        var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);Assert.Equal(expected,feature.Result.VolumeMm3,5);Assert.Null(feature.TopologyHistory);
        Assert.Equal(body.Appearance,session.Snapshot.Bodies.Values.Single().Appearance);Assert.Equal(body.Producer,feature.Inputs.Single());
        await session.ExecuteAsync(new RecomputeCommand(body.Producer!.Value,new BoxRecipe(11,12,15,RigidTransform3d.Identity)));session.Snapshot.Validate();
        var recipe=(ExtrudeRecipe)session.Snapshot.Features[feature.Id].Recipe;Assert.Equal(session.Snapshot.Features[body.Producer.Value].Result,recipe.Options!.Source);
    }
    [Fact] public async Task DirectionalThroughNeverCutsBehindAuthoredProfile()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Directional"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Target"));var body=session.Snapshot.Bodies.Values.Single();
        await session.ExecuteAsync(new AddExtrusionCommand(new(Square,0,RigidTransform3d.Translate(3,4,8),new(Operation:ExtrusionOperation.Cut,Source:body.Geometry,Termination:ExtrusionTermination.Through)),"Partial axis",targetBody:body.Id));
        Assert.Equal(1800-4*7,session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,5);
    }
    [Fact] public async Task InvalidLimitsAndDisconnectedFuseRejectAtomically()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Reject"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Target"));var body=session.Snapshot.Bodies.Values.Single();var before=session.Snapshot;int count=assets.Count;
        foreach(var plane in new[]{RigidTransform3d.Translate(0,0,-5),new RigidTransform3d(new(0,0,1),Quaterniond.FromEulerDegrees(0,45,0)),new RigidTransform3d(new(0,0,1),Quaterniond.FromEulerDegrees(0,90,0))})
        {
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddExtrusionCommand(new(Square,0,RigidTransform3d.Identity,new(Termination:ExtrusionTermination.Plane,LimitPlane:plane)),"Bad",body.PartId)));
            Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
        }
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddExtrusionCommand(new(Square,5,RigidTransform3d.Translate(100,0,0),new(Operation:ExtrusionOperation.Fuse,Source:body.Geometry)),"Disconnected",targetBody:body.Id)));
        Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
    }
    [Fact] public async Task ExactLimitFaceIsReferenceOnlyAndFreezesOnRevisionChange()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Exact face"),assets,kernel,new InlineSessionDispatcher());
        var box=new BoxRecipe(10,12,15,RigidTransform3d.Identity);await session.ExecuteAsync(new AddBodyCommand(box,"Limit"));var body=session.Snapshot.Bodies.Values.Single();var f=session.Snapshot.Features[body.Producer!.Value];
        using var shape=OcctGeometryBridge.ReadShape(f.Result,assets);using var map=RepairSnapshot.Create(shape);
        var index=map.Topology.Single(t=>{if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);return BoxTopology.Matches(face,box,TopologyKind.Face,BoxBoundary.ZMax);}).Selection.Index;
        var selection=new ExactTopologySelection(session.Snapshot.Id,f.Id,f.Result.Revision,f.Result.AssetId,map.Fingerprint,index,HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion);
        var r=new ExtrudeRecipe(Square,0,RigidTransform3d.Translate(3,4,0),new(Termination:ExtrusionTermination.PlanarFace,LimitFace:selection,LimitGeometry:f.Result));
        var before=session.Snapshot;await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddExtrusionCommand(r with{Options=r.Options! with{LimitFace=selection with{Fingerprint=new string('0',64)}}},"Wrong",body.PartId)));Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(new AddExtrusionCommand(r,"To top support plane",body.PartId));Assert.Equal(2,session.Snapshot.Bodies.Count);
        var extrusion=session.Snapshot.Features.Values.Single(e=>e.Recipe is ExtrudeRecipe);Assert.Empty(extrusion.Inputs);Assert.Equal(60,extrusion.Result.VolumeMm3,5);
        await session.ExecuteAsync(new RecomputeCommand(f.Id,box with{Z=20}));Assert.True(session.Snapshot.Features[extrusion.Id].IsStale);Assert.Equal(extrusion.Result,session.Snapshot.Features[extrusion.Id].Result);session.Snapshot.Validate();
    }
    [Fact] public async Task DatumSourceParameterAndTwoSideBindingsDriveJointGraph()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Datum"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Source"));var body=session.Snapshot.Bodies.Values.Single();var source=body.Producer!.Value;
        var datum=new CadDatumPlane(Guid.NewGuid(),body.PartId,"Top",DatumPlaneKind.Offset,DatumPlaneSource.Box(source,BoxBoundary.ZMax));
        await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Planes=ImmutableDictionary<Guid,CadDatumPlane>.Empty.Add(datum.Id,datum)}));datum=session.Snapshot.Datums.Planes[datum.Id];
        await session.ExecuteAsync(new AddExtrusionCommand(new(Square,0,RigidTransform3d.Identity,new(Termination:ExtrusionTermination.Plane,LimitPlane:datum.Frame,Datum:datum.Id,DatumRevision:datum.Revision)),"Until",body.PartId));
        var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);await session.ExecuteAsync(new RecomputeCommand(source,new BoxRecipe(10,12,20,RigidTransform3d.Identity)));
        Assert.Equal(80,session.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
        var height=new CadParameter(Guid.NewGuid(),"height",ParameterDimension.Length,"17 mm");
        await session.ExecuteAsync(new SetParametersCommand(new([height],[new(Guid.NewGuid(),height.Id,ParameterTarget.Feature(source,"Z"))])));
        Assert.Equal(68,session.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
        await session.ExecuteAsync(new RecomputeCommand(feature.Id,new ExtrudeRecipe(Square,5,RigidTransform3d.Identity,new(ExtrusionSide.TwoSides,3))));
        var p=new CadParameter(Guid.NewGuid(),"back",ParameterDimension.Length,"7 mm");
        await session.ExecuteAsync(new SetParametersCommand(new([p],[new(Guid.NewGuid(),p.Id,ParameterTarget.Feature(feature.Id,"Backward"))])));
        Assert.Equal(48,session.Snapshot.Features[feature.Id].Result.VolumeMm3,5);session.Snapshot.Validate();
    }
    [Fact] public async Task StorageRoundTripAndExplicitLegacyGuard()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Store"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddExtrusionCommand(new(Square,5,RigidTransform3d.Identity,new(ExtrusionSide.TwoSides,3)),"Advanced"));var path=files.PathFor("advanced.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using(var loaded=await new CadDocumentStorage().LoadAsync(path,assets)){loaded.Snapshot.Validate();var saved=Assert.IsType<ExtrudeRecipe>(loaded.Snapshot.Features.Values.Single().Recipe);Assert.Equal(3,saved.Options!.Backward);}
        Assert.Contains("cadoryx.advanced-extrusion.1",FormatEvolutionTests.Manifest(path).RequiredCapabilities);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=24}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
    }
    [Fact] public async Task HoleIslandProfileRetainsExactGeometryAndStorageInReverseDirection()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Islands"),assets,kernel,new InlineSessionDispatcher()))
        {
            var profile=SketchProfile.FromCircle(new(0,0),4) with{Holes=[new(new(0,0),2)],Islands=[new([],new(new(0,0),1))]};
            await session.ExecuteAsync(new AddExtrusionCommand(new(profile,8,RigidTransform3d.Identity,new(ExtrusionSide.Negative)),"Reverse islands"));
            var body=session.Snapshot.Bodies.Values.Single();Assert.InRange(Math.Abs(104*Math.PI-body.Geometry.VolumeMm3),0,1e-4);Assert.Equal(BodyKind.Compound,body.Geometry.Kind);
            var path=files.PathFor("islands.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
            using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);loaded.Snapshot.Validate();
            await using var reopened=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher());var feature=loaded.Snapshot.Features.Values.Single();
            await reopened.ExecuteAsync(new RecomputeCommand(feature.Id,feature.Recipe));Assert.Equal(body.Geometry.VolumeMm3,reopened.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task ProductionEditorPreservesFrozenCurvesAndInvalidatesPreview()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Frozen curve"),assets,kernel,new InlineSessionDispatcher()))
        {
            var profile=SketchProfile.FromCircle(new(0,0),4) with{Holes=[new(new(0,0),1)]};
            await session.ExecuteAsync(new AddBodyCommand(new ExtrudeRecipe(profile,5,RigidTransform3d.Identity),"Legacy circle"));var feature=session.Snapshot.Features.Values.Single();
            var document=new Cadoryx.ViewModels.CadDocumentViewModel(session,kernel,new Cadoryx.ViewModels.Services.Platform.Notifications.CadMessageLog());
            try
            {
                FeatureId? routed=null;document.ExtrusionEditRequested+=(_,id)=>routed=id;document.EditFeature(feature.Id);Assert.Equal(feature.Id,routed);Assert.Null(document.SolidHandleRecipe());
                await using var editor=new Cadoryx.ViewModels.ExtrusionEditorViewModel(document,kernel,feature.Id);
                editor.Side=ExtrusionSide.Negative;editor.Distance=8;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                editor.Distance=9;Assert.False(editor.CanConfirm);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
                Assert.InRange(Math.Abs(135*Math.PI-session.Snapshot.Features[feature.Id].Result.VolumeMm3),0,1e-4);
                Assert.Equal(profile,((ExtrudeRecipe)session.Snapshot.Features[feature.Id].Recipe).Profile);
            }
            finally{document.Detach();}
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task LinkedSketchRebuildsAdvancedRecipeAndCancellationIsAtomic()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new Cadoryx.Sketching.ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var draft=new SketchDraft(CadSketch.Create(part,"Profile",RigidTransform3d.Translate(10,20,30)));draft.AddCircle(new(0,0),2);
            await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));var sketch=session.Snapshot.Sketches[draft.Value.Id];var reference=SketchProfileReference.CreateCircle(sketch,sketch.Circles[0].Id);
            await session.ExecuteAsync(new AddExtrusionCommand(new(SketchProfile.FromCircle(new(0,0),2),8,sketch.Plane,new(ExtrusionSide.Symmetric)),"Linked",part,sketchSource:reference));
            var feature=session.Snapshot.Features.Values.Single();var before=session.Snapshot;
            using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ExecuteAsync(new RecomputeCommand(feature.Id,feature.Recipe),cancel.Token));Assert.Same(before,session.Snapshot);
            draft=new SketchDraft(sketch);draft.Change(s=>s with{Circles=s.Circles.SetItem(0,s.Circles[0] with{Radius=3}),
                Constraints=s.Constraints.Select(c=>c is RadiusConstraint r?r with{Radius=3}:c).ToImmutableArray()});
            await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));var changed=session.Snapshot.Features[feature.Id];
            Assert.Equal(72*Math.PI,changed.Result.VolumeMm3,5);Assert.Equal(ExtrusionSide.Symmetric,((ExtrudeRecipe)changed.Recipe).Options!.Side);
            Assert.NotEqual(reference.Revision,changed.SketchSource!.Revision);session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(ExtrusionSide.Negative,-8,0)][InlineData(ExtrusionSide.Positive,0,8)]
    public async Task PlaneTerminationWorksOnEitherSide(ExtrusionSide side,double min,double max)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var result=await kernel.EvaluateAsync(new ExtrudeRecipe(Square,0,RigidTransform3d.Identity,
            new(side,Termination:ExtrusionTermination.Plane,LimitPlane:RigidTransform3d.Translate(0,0,side==ExtrusionSide.Negative?-8:8))),assets))
        {Assert.Equal(32,result.Geometry.VolumeMm3,5);Assert.Equal(min,result.Geometry.Bounds.Min.Z,5);Assert.Equal(max,result.Geometry.Bounds.Max.Z,5);}
        Assert.Equal(0,assets.Count);
    }
}
