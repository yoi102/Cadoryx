using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using MessagePack;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class CurveRegionRevolveTests
{
    private static CubicSplineRegion Spline()=>new([new(8,0),new(28d/3,2),new(32d/3,2),new(12,0)]);
    private static SketchProfile Cap(string kind)
    {
        if(kind=="bezier")return new([]){Bezier=new(new(8,0),new(10,2),new(12,0))};
        if(kind=="spline")return new([]){Spline=Spline()};
        var edge=new SketchBoundaryCurve(new(8,0),new(12,0));
        edge=kind=="mixed-bezier"?edge with{BezierControl=new(10,2)}:edge with{SplineControls=Spline().Controls};
        return new([]){BoundaryCurves=[edge,new(new(12,0),new(10,0)),new(new(10,0),new(8,0))]};
    }
    [Theory][InlineData("bezier")][InlineData("spline")][InlineData("mixed-bezier")][InlineData("mixed-spline")]
    public async Task ExactCurveRegionsMatchAnalyticalFirstAreaMoments(string kind)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        double moment=kind.Contains("bezier")?80d/3:40;
        foreach(double angle in new[]{Math.PI/3,Math.PI,Math.Tau})
        {
            using var result=await kernel.EvaluateAsync(new RevolveRecipe(Cap(kind),angle,RigidTransform3d.Translate(3,5,7)),assets);
            Assert.Equal(BodyKind.Solid,result.Geometry.Kind);Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-angle*moment),0,.0001);
            using var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets);
            Assert.True(shape.GetValidationReport(true,true).IsValid);Assert.True(shape.GetSelfInterferenceReport().IsClear);
            Assert.Equal(1,shape.GetTopologySummary().UniqueCounts.SolidCount);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("circle")][InlineData("polygon")][InlineData("bezier")][InlineData("spline")]
    public async Task EachExactHoleKindPreservesIndependentAnalyticalVolume(string kind)
    {
        var profile=SketchProfile.FromCircle(new(10,0),4);double removed;
        switch(kind)
        {
            case "circle":profile=profile with{Holes=[new(new(10,0),1)]};removed=10*Math.PI;break;
            case "polygon":profile=profile with{PolygonHoles=[[new(9,-1),new(11,-1),new(11,1),new(9,1)]]};removed=40;break;
            case "bezier":profile=profile with{MixedHoles=[Cap("mixed-bezier").BoundaryCurves]};removed=80d/3;break;
            default:profile=profile with{SplineHoles=[Spline()]};removed=40;break;
        }
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        foreach(double angle in new[]{Math.PI,Math.Tau})
        {
            using var result=await kernel.EvaluateAsync(new RevolveRecipe(profile,angle,RigidTransform3d.Identity),assets);
            Assert.Equal(BodyKind.Solid,result.Geometry.Kind);Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-angle*(160*Math.PI-removed)),0,.002);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task RecursiveIslandsProduceThreeExplicitSolidsWithNoOverlap()
    {
        var inner=new SketchIslandRegion([],new(new(10,0),.5));
        var island=new SketchIslandRegion([],new(new(10,0),2)){Holes=[new(new(10,0),1)],Islands=[inner]};
        var profile=SketchProfile.FromCircle(new(10,0),5) with{Holes=[new(new(10,0),3)],Islands=[island]};
        var assets=new MemoryAssetStore();
        foreach(double angle in new[]{Math.PI,Math.Tau})
        {
            using var result=await new OcctGeometryKernel().EvaluateAsync(new RevolveRecipe(profile,angle,RigidTransform3d.Identity),assets);
            Assert.Equal(BodyKind.Compound,result.Geometry.Kind);Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-192.5*Math.PI*angle),0,.002);
            using var native=OcctGeometryBridge.ReadShape(result.Geometry,assets);
            Assert.Equal(3,native.GetTopologySummary().UniqueCounts.SolidCount);Assert.True(native.GetSelfInterferenceReport().IsClear);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task DerivativeExtremaAcceptControlCrossingAndRejectActualCrossingOrInternalTangency(bool spline)
    {
        SketchProfile Region(double control)=>spline?new([]){Spline=new([new(.1,-3),new(control,-1),new(control,1),new(.1,3)])}:
            new([]){Bezier=new(new(.1,-1),new(control,0),new(.1,1))};
        var accepted=Region(spline?-.02:-.05);var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var result=await kernel.EvaluateAsync(new RevolveRecipe(accepted,Math.Tau,RigidTransform3d.Identity),assets))
            Assert.True(result.Geometry.VolumeMm3>0);
        foreach(double control in new[]{spline?-.1/3:-.1,-.2})
            Assert.Throws<CadValidationException>(()=>new RevolveRecipe(Region(control),Math.Tau,RigidTransform3d.Identity).Validate());
        var knotCrossing=new SketchProfile([]){Spline=new([new(.1,-4),new(-.04,-2),new(-.04,0),new(-.04,2),new(.1,4)])};
        Assert.Throws<CadValidationException>(()=>new RevolveRecipe(knotCrossing,Math.Tau,RigidTransform3d.Identity).Validate());
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("bezier")][InlineData("spline")][InlineData("mixed-bezier")][InlineData("mixed-spline")]
    public async Task CurveRevolutionRoundTripsCustomAxisAndRejectsUndeclaredOldSchema(string kind)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=new RevolveRecipe(Cap(kind),Math.PI,RigidTransform3d.Identity){Axis=new(new(2,0,0),Vector3d.UnitZ)};
            await session.ExecuteAsync(new AddBodyCommand(recipe,kind,part));
            var path=files.PathFor(kind+".cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,path);int count=assets.Count;
            using(var loaded=await storage.LoadAsync(path,assets))
            {
                var saved=Assert.IsType<RevolveRecipe>(loaded.Snapshot.Features.Values.Single().Recipe);
                Assert.True(SketchProfileReference.SameProfile(recipe.Profile,saved.Profile));Assert.Equal(recipe.Axis,saved.Axis);
                using var result=await kernel.EvaluateAsync(saved,assets);
                Assert.Equal(session.Snapshot.Features.Values.Single().Result.VolumeMm3,result.Geometry.VolumeMm3,5);
            }
            Assert.Contains("cadoryx.curved-region-revolve.1",FormatEvolutionTests.Manifest(path).RequiredCapabilities);
            FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=30}:s).ToImmutableArray()});
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ProductionCurveSelectionRecomputeCancelUndoAndCompleteCachedProfileValidation(bool spline)
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var draft=new SketchDraft(CadSketch.Create(part,"Curve",RigidTransform3d.Identity));
            if(spline)draft.AddSpline(Spline().Controls);else draft.AddBezier(new(8,0),new(10,2),new(12,0));
            await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));
            var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                document.SelectedTargetPart=part;document.SelectedSketchId=draft.Value.Id;document.StartSketchFeature("Revolve");
                document.SelectedSketchProfile=document.SketchProfiles.Single(p=>spline?p.Source.SplineId is not null:p.Source.BezierId is not null);
                document.AngleDegrees=180;var before=session.Snapshot;
                await document.PreviewCommand.ExecuteAsync(null);Assert.True(document.HasPreview,document.ToolStatus);Assert.Same(before,session.Snapshot);
                document.CancelCommand.Execute(null);Assert.False(document.HasPreview);Assert.Same(before,session.Snapshot);
                document.StartSketchFeature("Revolve");document.SelectedSketchProfile=document.SketchProfiles.Single(p=>spline?p.Source.SplineId is not null:p.Source.BezierId is not null);
                document.AngleDegrees=180;await document.PreviewCommand.ExecuteAsync(null);await document.ConfirmCommand.ExecuteAsync(null);
                var feature=Assert.Single(session.Snapshot.Features.Values);Assert.NotNull(feature.SketchSource);var committed=session.Snapshot;
                var edit=new SketchDraft(committed.Sketches[draft.Value.Id]);
                var point=spline?edit.Value.Splines[0].Controls[1]:edit.Value.Beziers[0].Control;
                var position=edit.Value.Points.Single(p=>p.Id==point).Position;edit.MovePoint(point,new(position.X,position.Y+1));
                await session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver));var after=session.Snapshot;
                Assert.NotEqual(feature.Result.VolumeMm3,after.Features[feature.Id].Result.VolumeMm3);after.Validate();
                await session.UndoAsync();Assert.Same(committed,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
                var good=(RevolveRecipe)after.Features[feature.Id].Recipe;
                var bad=good with{Profile=Cap(spline?"spline":"bezier")};
                Assert.Throws<CadValidationException>(()=>after.Features[feature.Id].SketchSource!.ValidateCache(after,after.Features[feature.Id] with{Recipe=bad}));
            }
            finally{document.Detach();}
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task InvalidRegionAndPreCancelledEvaluationLeaveAssetsUntouched()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var malformed=SketchProfile.FromCircle(new(10,0),3) with{Holes=[new(new(13,0),1)]};
        await Assert.ThrowsAsync<CadValidationException>(()=>kernel.EvaluateAsync(new RevolveRecipe(malformed,Math.Tau,RigidTransform3d.Identity),assets));
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>kernel.EvaluateAsync(new RevolveRecipe(Cap("spline"),Math.Tau,RigidTransform3d.Identity),assets,cancelled.Token));
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FrozenPolygonHolesAndIslandsSurviveAngleEditingPersistenceAndStep(bool island)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var profile=new SketchProfile([new(4,-4),new(16,-4),new(16,4),new(4,4)]){Holes=[new(new(10,0),2)]};
        if(island)profile=profile with{Islands=[new([],new(new(10,0),1))]};
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new RevolveRecipe(profile,Math.Tau,RigidTransform3d.Identity),"Regions",part));
            var feature=session.Snapshot.Features.Values.Single();var before=session.Snapshot;
            var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                document.EditFeature(feature.Id);Assert.False(document.CanEditProfilePoints);document.AngleDegrees=180;
                await document.PreviewCommand.ExecuteAsync(null);Assert.True(document.HasPreview,document.ToolStatus);
                await document.ConfirmCommand.ExecuteAsync(null);
                var edited=Assert.IsType<RevolveRecipe>(session.Snapshot.Features[feature.Id].Recipe);
                Assert.True(SketchProfileReference.SameProfile(profile,edited.Profile));
                Assert.Equal(feature.Result.VolumeMm3/2,session.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
                var after=session.Snapshot;await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
                var path=files.PathFor("regions.cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,path);
                using(var loaded=await storage.LoadAsync(path,assets))Assert.True(SketchProfileReference.SameProfile(profile,((RevolveRecipe)loaded.Snapshot.Features[feature.Id].Recipe).Profile));
                var step=files.PathFor("regions.step");await kernel.ExportAsync(after,assets,step);
                using var exchanged=ShapeExchange.ReadStep(step);
                var solids=exchanged.GetSubShapes(ShapeKind.Solid);
                try
                {
                    Assert.Equal(island?2:1,solids.Length);
                    Assert.InRange(Math.Abs(solids.Sum(s=>Math.Abs(s.InspectProperties(InspectionPropertyKind.Volume).Mass))-after.Features[feature.Id].Result.VolumeMm3),0,.002);
                }
                finally{foreach(var solid in solids)solid.Dispose();}
                FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=30}:s).ToImmutableArray()});
                await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
            }
            finally{document.Detach();}
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CurveAxisAndAngleExpressionsRebuildBooleanAndDrawingAtomically(bool spline)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var axis=new CadDatumAxis(Guid.NewGuid(),part,"Spin",DatumAxisKind.Independent,Origin:new(2,0,0),Direction:Vector3d.UnitZ);
            await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Axes=DatumBook.Empty.Axes.Add(axis.Id,axis)}));axis=session.Snapshot.Datums.Axes[axis.Id];
            var draft=new SketchDraft(CadSketch.Create(part,"Cap",RigidTransform3d.Identity));
            if(spline)draft.AddSpline(Spline().Controls);else draft.AddBezier(new(8,0),new(10,2),new(12,0));
            await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));
            var sketch=session.Snapshot.Sketches[draft.Value.Id];
            var source=spline?SketchProfileReference.CreateSplineSegment(sketch,sketch.Splines[0].Id):SketchProfileReference.CreateBezierSegment(sketch,sketch.Beziers[0].Id);
            var seed=new RevolveRecipe(new([]),Math.PI,RigidTransform3d.Identity){Axis=new(axis.Frame.Translation,axis.Frame.Rotation.Rotate(Vector3d.UnitZ),axis.Id,axis.Revision)};
            await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,seed),"Curve tool",part,sketchSource:source));
            var spin=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(40,40,15,RigidTransform3d.Translate(-20,-20,-5)),"Stock",part));
            var stock=session.Snapshot.Features.Values.Single(f=>f.Id!=spin.Id);
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[stock.OutputBodyId,spin.OutputBodyId]));
            var cut=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
            var sheet=TechnicalDrawingSheet.A4Landscape("Curved recess");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,100),1));
            var before=session.Snapshot;
            var offset=new CadParameter(Guid.NewGuid(),"offset",ParameterDimension.Length,"3 mm");
            var angle=new CadParameter(Guid.NewGuid(),"turn",ParameterDimension.Angle,"90 deg");
            var book=new ParameterBook([offset,angle],[new(Guid.NewGuid(),offset.Id,ParameterTarget.Datum(axis.Id,"X")),new(Guid.NewGuid(),angle.Id,ParameterTarget.Feature(spin.Id,"Angle"))]);
            await session.ExecuteAsync(new SetParametersCommand(book));var after=session.Snapshot;
            double volume=(spline?28:56d/3)*Math.PI/2;
            Assert.Equal(volume,after.Features[spin.Id].Result.VolumeMm3,5);Assert.Equal(24000-volume,after.Features[cut.Id].Result.VolumeMm3,5);
            Assert.All(after.DrawingSheets.Values.SelectMany(s=>s.Views),v=>{Assert.Null(v.StaleReason);Assert.Equal(after.Features[cut.Id].Result.Revision,v.Sources.Single().Revision);});
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var invalid=book with{Parameters=book.Parameters.Select(p=>p.Id==offset.Id?p with{Expression="10 mm"}:p).ToImmutableArray()};int count=assets.Count;
            await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new SetParametersCommand(invalid)));Assert.Same(after,session.Snapshot);Assert.Equal(count,assets.Count);
            var path=files.PathFor("linked.cadoryx");var storage=new CadDocumentStorage(datumResolver:kernel);await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))
            await using(var reopened=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
            {
                var changed=book with{Parameters=book.Parameters.Select(p=>p.Id==angle.Id?p with{Expression="180 deg"}:p).ToImmutableArray()};
                await reopened.ExecuteAsync(new SetParametersCommand(changed));Assert.Equal(volume*2,reopened.Snapshot.Features[spin.Id].Result.VolumeMm3,5);
                Assert.Equal(24000-volume*2,reopened.Snapshot.Features[cut.Id].Result.VolumeMm3,5);reopened.Snapshot.Validate();
            }
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExactCurveMayTerminateOnAxisWithoutAnInternalAxisContact(bool spline)
    {
        var profile=spline?new SketchProfile([]){Spline=new([new(0,-1),new(1,-1d/3),new(1,1d/3),new(0,1)])}:
            new SketchProfile([]){Bezier=new(new(0,-1),new(2,0),new(0,1))};
        var assets=new MemoryAssetStore();
        using(var result=await new OcctGeometryKernel().EvaluateAsync(new RevolveRecipe(profile,Math.Tau,RigidTransform3d.Identity),assets))
        {
            Assert.Equal(BodyKind.Solid,result.Geometry.Kind);
            // Integrate pi*r(z)^2 for r=1-z^2 and r=3/4*(1-z^2), independently of BRep.
            Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-(spline?3*Math.PI/5:16*Math.PI/15)),0,.00001);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task TiltedPlacedCustomAxisKeepsCurveAndHoleRadialSideAndDirectedSweep(bool reverse)
    {
        var placement=new RigidTransform3d(new(3,5,7),Quaterniond.FromEulerDegrees(20,30,15));
        var localDirection=new Vector3d(1,0,1).Normalized()*(reverse?-1:1);var localPoint=new Vector3d(2,0,-3);
        var profile=SketchProfile.FromCircle(new(9,-10),2) with{Holes=[new(new(9,-10),.5)]};
        var recipe=new RevolveRecipe(profile,Math.PI/3,placement){Axis=new(placement.Apply(localPoint),placement.Rotation.Rotate(localDirection))};
        var assets=new MemoryAssetStore();
        using(var result=await new OcctGeometryKernel().EvaluateAsync(recipe,assets))
            Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-Math.Sqrt(98)*3.75*Math.PI*Math.PI/3),0,.0001);
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CurrentRevolutionRejectsAnotherOperationsPayloadBeforeChangingAssets(bool axis)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var profile=new SketchProfile([new(4,-1),new(8,-1),new(8,1),new(4,1)]);
            await session.ExecuteAsync(new AddBodyCommand(new RevolveRecipe(profile,Math.PI,RigidTransform3d.Identity)
                {Axis=axis?new(new(2,0,0),Vector3d.UnitZ):null},"Ring",part));
            var path=files.PathFor("bad.cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,path);int count=assets.Count;
            FormatEvolutionTests.RewriteSection(path,"features",bytes=>
            {
                var data=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);var item=data.Features.Single();
                return MessagePackSerializer.Serialize(data with{Features=[item with{Recipe=item.Recipe with{Operation=1}}]});
            });
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
}
