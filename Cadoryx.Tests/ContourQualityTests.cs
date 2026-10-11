using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;
using Operation=Cadoryx.Db.LocalFeatureOperation;

namespace Cadoryx.Tests;

public sealed partial class ContourLocalTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExplicitFinalContourWorksOnOffsetAndHollowShellWithoutBoxAncestryRebinding(bool shell)
    {
        await using var f=await Fixture.Create();
        if(shell)
        {
            var top=f.Select(ShapeKind.Face,b=>Math.Abs(b.Minimum.Z-15)<1e-5);
            await f.Session.ExecuteAsync(new AddFaceFeatureCommand(f.Source.OutputBodyId,new ShellRecipe(f.Source.Result,[top],1),"Hollow shell"));
        }
        else await f.Session.ExecuteAsync(new AddSolidOffsetCommand(f.Source.OutputBodyId,new(f.Source.Result,-1),"Inner offset"));
        var source=f.Session.Snapshot.Features.Values.Single(v=>v.Recipe is ShellRecipe or SolidOffsetRecipe);ExactTopologySelection seed;
        double at=shell?0:1;
        using(var shape=OcctGeometryBridge.ReadShape(source.Result,f.Assets))using(var map=RepairSnapshot.Create(shape))
        {
            var edge=map.Topology.First(t=>{if(t.Kind!=ShapeKind.Edge)return false;using var sub=map.CopySubshape(t.Selection);var b=sub.GetBoundingBox();return Math.Abs(b.Maximum.X-at)<1e-5&&Math.Abs(b.Maximum.Y-at)<1e-5&&b.SizeZ>(shell?14.99:12.99);});
            seed=new(f.Session.Snapshot.Id,source.Id,source.Result.Revision,source.Result.AssetId,map.Fingerprint,edge.Selection.Index,HistoryShapeKind.Edge,OcctGeometryKernel.HistoryAdapterVersion);
        }
        var chain=await f.Kernel.DiscoverContourAsync(f.Session.Snapshot,seed,Operation.Fillet,null,f.Assets);
        await f.Session.ExecuteAsync(new AddContourLocalCommand(source.OutputBodyId,new(source.Result,Operation.Fillet,[new(Guid.NewGuid(),seed,chain,new ConstantContourRadius(.2))]),"Final contour"));
        var final=f.Session.Snapshot.Features.Values.Single(v=>v.Recipe is ContourLocalRecipe);Assert.Equal(source.Id,final.Inputs.Single());
        Assert.Equal((shell?680:1040)-(shell?15:13)*(1-Math.PI/4)*.04,final.Result.VolumeMm3,4);
        using var result=OcctGeometryBridge.ReadShape(final.Result,f.Assets);Assert.True(result.GetValidationReport(true,true).IsValid);Assert.True(result.GetSelfInterferenceReport().IsClear);
    }
    [Fact]public async Task ChamferProgramLengthDriversRetainSupportOrientationAndRejectDrivenFieldRemoval()
    {
        await using var f=await Fixture.Create();var seed=f.Select(ShapeKind.Edge,b=>Math.Abs(b.Maximum.X)<1e-5&&Math.Abs(b.Maximum.Y)<1e-5&&b.SizeZ>14.99);
        var face=f.Select(ShapeKind.Face,b=>Math.Abs(b.Maximum.X)<1e-5);var chain=await f.Kernel.DiscoverContourAsync(f.Session.Snapshot,seed,Operation.Chamfer,face,f.Assets);
        var program=new ContourLocalProgram(Guid.NewGuid(),seed,chain,null,face,1,2);await f.Session.ExecuteAsync(new AddContourLocalCommand(f.Source.OutputBodyId,new(f.Source.Result,Operation.Chamfer,[program]),"Chamfer"));
        var feature=f.Session.Snapshot.Features.Values.Single(v=>v.Recipe is ContourLocalRecipe);var recipe=(ContourLocalRecipe)feature.Recipe;
        var target=ParameterTarget.Feature(feature.Id,ContourParameterTargets.Property(program.Id,"SecondDistance"));var p=new CadParameter(Guid.NewGuid(),"width",ParameterDimension.Length,"3 mm");var b=new ParameterBinding(Guid.NewGuid(),p.Id,target);
        await f.Session.ExecuteAsync(new SetParametersCommand(new([p],[b])));var after=f.Session.Snapshot;
        Assert.Equal(1777.5,after.Features[feature.Id].Result.VolumeMm3,5);Assert.Equal(face,((ContourLocalRecipe)after.Features[feature.Id].Recipe).Programs[0].SupportFace);int count=f.Assets.Count;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new RecomputeCommand(feature.Id,recipe with{Programs=[program with{SecondDistance=null}]})));
        Assert.Same(after,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
        using var files=new TestFiles();var storage=new CadDocumentStorage(contourResolver:f.Kernel);var path=files.PathFor("chamfer.cadoryx");await f.Session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,f.Assets)){loaded.Snapshot.Validate();Assert.Equal(3,ParameterTargets.Read(loaded.Snapshot,target).Value);Assert.Equal(face,((ContourLocalRecipe)loaded.Snapshot.Features[feature.Id].Recipe).Programs[0].SupportFace);}
        Assert.Equal(count,f.Assets.Count);
    }
    [Fact]public async Task ClosedCircularContourBuildsButDiscontinuousLawSeamRejectsAtomically()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Closed contour"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(10,15,RigidTransform3d.Identity),"Cylinder"));var source=session.Snapshot.Features.Values.Single();ExactTopologySelection seed;
            using(var shape=OcctGeometryBridge.ReadShape(source.Result,assets))using(var map=RepairSnapshot.Create(shape))
            {
                var edge=map.Topology.First(t=>{if(t.Kind!=ShapeKind.Edge)return false;using var sub=map.CopySubshape(t.Selection);return Math.Abs(sub.GetBoundingBox().Maximum.Z)<1e-5;});
                seed=new(session.Snapshot.Id,source.Id,source.Result.Revision,source.Result.AssetId,map.Fingerprint,edge.Selection.Index,HistoryShapeKind.Edge,OcctGeometryKernel.HistoryAdapterVersion);
            }
            var chain=await kernel.DiscoverContourAsync(session.Snapshot,seed,Operation.Fillet,null,assets);Assert.True(chain.IsClosed);Assert.Equal(20*Math.PI,chain.Length,5);
            var program=new ContourLocalProgram(Guid.NewGuid(),seed,chain,new ConstantContourRadius(1));var recipe=new ContourLocalRecipe(source.Result,Operation.Fillet,[program]);
            var before=session.Snapshot;int count=assets.Count;var history=session.HistoryUsage;
            await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new AddContourLocalCommand(source.OutputBodyId,recipe with{Programs=[program with{Radius=new LinearContourRadius(1,2)}]},"Bad seam")));
            Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);Assert.Equal(history,session.HistoryUsage);
            await session.ExecuteAsync(new AddContourLocalCommand(source.OutputBodyId,recipe,"Closed fillet"));session.Snapshot.Validate();
            using var result=OcctGeometryBridge.ReadShape(session.Snapshot.Bodies.Values.Single().Geometry,assets);Assert.True(result.GetValidationReport(true,true).IsValid);Assert.True(result.GetSelfInterferenceReport().IsClear);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("oversized")][InlineData("support")][InlineData("overlap")]
    public async Task InvalidGeometryAndConflictingProgramsNeverPublishPartialShapes(string failure)
    {
        await using var f=await Fixture.Create();var seed=f.Select(ShapeKind.Edge,b=>Math.Abs(b.Maximum.X)<1e-5&&Math.Abs(b.Maximum.Y)<1e-5&&b.SizeZ>14.99);
        var chain=await f.Kernel.DiscoverContourAsync(f.Session.Snapshot,seed,Operation.Fillet,null,f.Assets);var before=f.Session.Snapshot;int count=f.Assets.Count;var history=f.Session.HistoryUsage;
        if(failure=="support")
        {
            var face=f.Select(ShapeKind.Face,b=>b.Minimum.X>9.99);
            await Assert.ThrowsAnyAsync<Exception>(()=>f.Kernel.DiscoverContourAsync(before,seed,Operation.Chamfer,face,f.Assets));
        }
        else
        {
            var p=new ContourLocalProgram(Guid.NewGuid(),seed,chain,new ConstantContourRadius(failure=="oversized"?100:.5));
            var recipe=new ContourLocalRecipe(f.Source.Result,Operation.Fillet,failure=="overlap"?[p,p with{Id=Guid.NewGuid()}]:[p]);
            await Assert.ThrowsAnyAsync<Exception>(()=>f.Session.ExecuteAsync(new AddContourLocalCommand(f.Source.OutputBodyId,recipe,"Failure")));
        }
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);Assert.Equal(history,f.Session.HistoryUsage);
    }
    [Fact]public async Task NativeHistoryFinalSelectionAndDrivenDrawingSurviveFilesWithoutGuessingOldIndices()
    {
        using var files=new TestFiles();await using var f=await Fixture.Create();var original=TopologyReference.Box(f.Session.Snapshot,f.Source.Id,BoxBoundary.XMax,BoxBoundary.YMax);
        var feature=await AddTestContour(f,"constant");var traced=await f.Kernel.TraceAsync(f.Session.Snapshot,original,feature.Id,f.Assets);
        Assert.Equal(HistoryResolutionStatus.Resolved,traced.Status);Assert.NotNull(traced.Target);
        var sheet=TechnicalDrawingSheet.A4Landscape("Contour drawing");await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(70,90),1));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Section",DrawingViewKind.Section,new(140,90),1,sectionNormal:Vector3d.UnitZ,sectionOffsetMm:5));
        var recipe=(ContourLocalRecipe)feature.Recipe;var target=ParameterTarget.Feature(feature.Id,ContourParameterTargets.Property(recipe.Programs[0].Id,"Radius"));
        var p=new CadParameter(Guid.NewGuid(),"radius",ParameterDimension.Length,"0.8 mm");await f.Session.ExecuteAsync(new SetParametersCommand(new([p],[new(Guid.NewGuid(),p.Id,target)])));
        var after=f.Session.Snapshot;Assert.All(after.DrawingSheets[sheet.Id].Views,v=>{Assert.Null(v.StaleReason);Assert.NotEmpty(v.Strokes);Assert.Equal(after.Features[feature.Id].Result.AssetId,v.Sources.Single().Asset);});
        var storage=new CadDocumentStorage(contourResolver:f.Kernel);string path=files.PathFor("drawing.cadoryx");await f.Session.SaveAsync(storage,path);int count=f.Assets.Count;
        using(var loaded=await storage.LoadAsync(path,f.Assets)){loaded.Snapshot.Validate();TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("drawing.pdf"));Assert.True(new FileInfo(files.PathFor("drawing.pdf")).Length>1000);}
        path=files.PathFor("contour.step");await f.Kernel.ExportAsync(after,f.Assets,path);using(var loaded=await f.Kernel.ImportAsync(path,f.Assets))Assert.Equal(after.Bodies.Values.Single().Geometry.VolumeMm3,loaded.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,4);
        Assert.Equal(count,f.Assets.Count);
        var current=after.Features[feature.Id];ExactTopologySelection selected;
        using(var shape=OcctGeometryBridge.ReadShape(current.Result,f.Assets))using(var map=RepairSnapshot.Create(shape))
        {
            var edge=map.Topology.First(t=>{if(t.Kind!=ShapeKind.Edge)return false;using var sub=map.CopySubshape(t.Selection);var b=sub.GetBoundingBox();return b.Minimum.X>9.99&&b.Minimum.Y>11.99&&b.SizeZ>14.99;});
            selected=new(after.Id,feature.Id,current.Result.Revision,current.Result.AssetId,map.Fingerprint,edge.Selection.Index,HistoryShapeKind.Edge,OcctGeometryKernel.HistoryAdapterVersion);
        }
        await f.Session.ExecuteAsync(new ExactLocalFeatureCommand(selected,Operation.Fillet,.2));var successor=f.Session.Snapshot.Features.Values.Single(f=>f.Recipe is HistoryFilletRecipe);
        await f.Session.ExecuteAsync(new SetParametersCommand(new([p with{Expression="0.9 mm"}],[new ParameterBinding(after.Parameters.Bindings.Single().Id,p.Id,target)])));
        Assert.True(f.Session.Snapshot.Features[successor.Id].IsStale);Assert.Equal(successor.Result,f.Session.Snapshot.Features[successor.Id].Result);
    }
}
