using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Occt;
using Cadoryx.Kernel.Abstractions;
using Xunit;

namespace Cadoryx.Tests;

public class HoleTests
{
    private static HoleRecipe Recipe(GeometryAssetRef source,bool through=true,HoleHead head=HoleHead.Plain)=>
        new(source,RigidTransform3d.Translate(0,0,15),new(5,6),2,through,through?0:6,head,
            head==HoleHead.Plain?0:head==HoleHead.Counterbore?4:6,head==HoleHead.Counterbore?2:0,head==HoleHead.Countersink?Math.PI/2:0);
    [Theory][InlineData(true,HoleHead.Plain,15)][InlineData(false,HoleHead.Plain,6)]
    [InlineData(true,HoleHead.Counterbore,21)][InlineData(false,HoleHead.Counterbore,12)]
    [InlineData(true,HoleHead.Countersink,65.0/3)][InlineData(false,HoleHead.Countersink,38.0/3)]
    public async Task ControlledModesProduceExactVolumeUndoAndNoAssetLeak(bool through,HoleHead head,double removedPi)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Hole"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Source"));var body=session.Snapshot.Bodies.Values.Single();
            var before=session.Snapshot;await session.ExecuteAsync(new AddHoleCommand(body.Id,Recipe(body.Geometry,through,head),"Hole"));
            var after=session.Snapshot;after.Validate();var hole=after.Features.Values.Single(f=>f.Recipe is HoleRecipe);
            Assert.Equal(1800-removedPi*Math.PI,hole.Result.VolumeMm3,5);Assert.Equal(BodyKind.Solid,hole.Result.Kind);
            if(head!=HoleHead.Plain)Assert.Equal(3,hole.TopologyHistory!.SchemaVersion);
            Assert.Equal(body.Producer,hole.Inputs.Single());
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task OverdeepAndOffTargetAndInvalidInputsRejectAtomically()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Invalid"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Source"));var body=session.Snapshot.Bodies.Values.Single();var r=Recipe(body.Geometry);
        var before=session.Snapshot;int count=assets.Count;
        foreach(var invalid in new[]{r with{Diameter=-1},r with{Through=false,Depth=100},r with{Position=new(100,100)},r with{Head=HoleHead.Counterbore,HeadDiameter=4,HeadDepth=100},
            r with{Head=HoleHead.Countersink,HeadDiameter=6,HeadAngle=Math.PI/6},r with{Through=false,Depth=1,Head=HoleHead.Counterbore,HeadDiameter=4,HeadDepth=2},
            r with{Through=false,Depth=0},r with{Through=true,Depth=6},r with{Head=(HoleHead)77},r with{Datum=Guid.Empty,DatumRevision=Guid.NewGuid()},
            r with{Standard=new(3,ClearanceFit.Normal)}})
        {
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddHoleCommand(body.Id,invalid,"Invalid")));
            Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
        }
        using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ExecuteAsync(new AddHoleCommand(body.Id,r,"Cancel"),cancel.Token));Assert.Same(before,session.Snapshot);
        var reversed=r with{Frame=new(r.Frame.Translation,Quaterniond.FromEulerDegrees(180,0,0)),Position=new(5,-6)};
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddHoleCommand(body.Id,reversed,"Reversed")));Assert.Same(before,session.Snapshot);
        var internalEntry=r with{Frame=RigidTransform3d.Translate(0,0,8)};
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddHoleCommand(body.Id,internalEntry,"Behind entry")));Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,2,2,RigidTransform3d.Translate(20,0,0)),"Other",body.PartId));
        await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Fuse,session.Snapshot.Bodies.Keys));var multi=session.Snapshot.Bodies.Values.Single();before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddHoleCommand(multi.Id,Recipe(multi.Geometry),"Multi")));Assert.Same(before,session.Snapshot);
    }
    [Theory][InlineData(HoleHead.Plain)][InlineData(HoleHead.Counterbore)][InlineData(HoleHead.Countersink)]
    public async Task RotatedFrameUsesAuthoredEntryAndDrillingDirection(HoleHead head)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Side"),assets,kernel,new InlineSessionDispatcher());
        var rotation=Quaterniond.FromEulerDegrees(0,90,0);var placement=new RigidTransform3d(new(30,40,50),rotation);
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,placement),"Source"));var body=session.Snapshot.Bodies.Values.Single();
        var r=Recipe(body.Geometry,false,head);r=r with{Frame=placement*r.Frame};
        await session.ExecuteAsync(new AddHoleCommand(body.Id,r,"Side"));
        double removed=head==HoleHead.Plain?6:head==HoleHead.Counterbore?12:38.0/3;
        Assert.Equal(1800-removed*Math.PI,session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,5);
    }
    [Fact] public async Task SourceDatumAndParametersRecomputeDownstreamAndStandardRequiresDetach()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Linked"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Source"));var body=session.Snapshot.Bodies.Values.Single();var source=body.Producer!.Value;
        var datum=new CadDatumPlane(Guid.NewGuid(),body.PartId,"Top",DatumPlaneKind.Offset,DatumPlaneSource.Box(source,BoxBoundary.ZMax));
        await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Planes=ImmutableDictionary<Guid,CadDatumPlane>.Empty.Add(datum.Id,datum)}));datum=session.Snapshot.Datums.Planes[datum.Id];
        var standard=new MetricClearanceSpec(3,ClearanceFit.Normal);
        await session.ExecuteAsync(new AddHoleCommand(body.Id,Recipe(body.Geometry) with{Standard=standard,Diameter=MetricClearanceHoles.Diameter(standard),Frame=datum.Frame,Datum=datum.Id,DatumRevision=datum.Revision},"M3"));
        var hole=session.Snapshot.Features.Values.Single(f=>f.Recipe is HoleRecipe);var p=new CadParameter(Guid.NewGuid(),"diameter",ParameterDimension.Length,"4 mm");var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(new([p],[new(Guid.NewGuid(),p.Id,ParameterTarget.Feature(hole.Id,"Diameter"))]))));Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(new RecomputeCommand(hole.Id,((HoleRecipe)hole.Recipe) with{Standard=null}));
        await session.ExecuteAsync(new SetParametersCommand(new([p],[new(Guid.NewGuid(),p.Id,ParameterTarget.Feature(hole.Id,"Diameter"))])));
        var pierced=session.Snapshot.Bodies.Values.Single();
        await session.ExecuteAsync(new AddRepetitionCommand(pierced.Id,new(pierced.Geometry,RepetitionKind.Linear,RigidTransform3d.Identity,2,100,[Guid.NewGuid(),Guid.NewGuid()]),"Two pierced parts"));
        await session.ExecuteAsync(new RecomputeCommand(source,new BoxRecipe(10,12,20,RigidTransform3d.Identity)));var changed=(HoleRecipe)session.Snapshot.Features[hole.Id].Recipe;
        Assert.Equal(20,changed.Frame.Translation.Z,6);Assert.Equal(session.Snapshot.Datums.Planes[datum.Id].Revision,changed.DatumRevision);
        Assert.Equal(2400-80*Math.PI,session.Snapshot.Features[hole.Id].Result.VolumeMm3,5);
        Assert.Equal(4800-160*Math.PI,session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,5);session.Snapshot.Validate();
        before=session.Snapshot;await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new RecomputeCommand(hole.Id,changed with{Frame=RigidTransform3d.Identity})));Assert.Same(before,session.Snapshot);
    }
    [Fact] public async Task StorageCatalogFrameAndLegacyMigrationAndMasqueradeGuards()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Storage"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Source"));var body=session.Snapshot.Bodies.Values.Single();var path=files.PathFor("old.cadoryx");
        await session.SaveAsync(new CadDocumentStorage(),path);
        using(var zip=System.IO.Compression.ZipFile.Open(path,System.IO.Compression.ZipArchiveMode.Update))zip.GetEntry("sections/assembly-patterns.msgpack")!.Delete();
        // Emulate the actual legacy four-field point payload before declaring its document version.
        SketchLegacyPointFixture.Prepare(path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Where(s=>s.Kind!="assembly-patterns").Select(s=>s.Kind=="features"?s with{SchemaVersion=23}:s.Kind=="document"?s with{SchemaVersion=22}:s).ToImmutableArray(),
            RequiredCapabilities=m.RequiredCapabilities.Where(c=>c!="cadoryx.assembly-patterns.1").ToImmutableArray()});
        using(var old=await new CadDocumentStorage().LoadAsync(path,assets)){old.Snapshot.Validate();Assert.IsType<BoxRecipe>(old.Snapshot.Features.Values.Single().Recipe);}
        var standard=new MetricClearanceSpec(4,ClearanceFit.Close);var recipe=Recipe(body.Geometry,false,HoleHead.Counterbore) with{Diameter=MetricClearanceHoles.Diameter(standard),Standard=standard,HeadDiameter=7};
        await session.ExecuteAsync(new AddHoleCommand(body.Id,recipe,"M4"));path=files.PathFor("hole.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using(var loaded=await new CadDocumentStorage().LoadAsync(path,assets)){loaded.Snapshot.Validate();Assert.Equal(recipe,loaded.Snapshot.Features.Values.Single(f=>f.Recipe is HoleRecipe).Recipe);}
        Assert.Contains("cadoryx.standard-holes.1",FormatEvolutionTests.Manifest(path).RequiredCapabilities);Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["features"].Version,FormatEvolutionTests.Manifest(path).Sections.Single(s=>s.Kind=="features").SchemaVersion);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=23}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task IndependentDatumSourceDependencyOrdersBeforeHoleInEitherDrivingBindingOrder(bool reverse)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Dependency"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Target"));var target=session.Snapshot.Bodies.Values.Single();
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,15,RigidTransform3d.Translate(50,0,0)),"Datum source",target.PartId));var origin=session.Snapshot.Bodies.Values.Single(b=>b.Name=="Datum source");
        var datum=new CadDatumPlane(Guid.NewGuid(),target.PartId,"Top from other source",DatumPlaneKind.Offset,DatumPlaneSource.Box(origin.Producer!.Value,BoxBoundary.ZMax));
        await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Planes=ImmutableDictionary<Guid,CadDatumPlane>.Empty.Add(datum.Id,datum)}));datum=session.Snapshot.Datums.Planes[datum.Id];
        await session.ExecuteAsync(new AddHoleCommand(target.Id,Recipe(target.Geometry) with{Frame=datum.Frame,Position=new(-45,6),Datum=datum.Id,DatumRevision=datum.Revision},"Hole"));
        var hole=session.Snapshot.Features.Values.Single(f=>f.Recipe is HoleRecipe);var height=new CadParameter(Guid.NewGuid(),"height",ParameterDimension.Length,"20 mm");
        ParameterBinding[] bindings=[new(Guid.NewGuid(),height.Id,ParameterTarget.Feature(target.Producer!.Value,"Z")),new(Guid.NewGuid(),height.Id,ParameterTarget.Feature(origin.Producer.Value,"Z"))];
        await session.ExecuteAsync(new SetParametersCommand(new([height],(reverse?bindings.Reverse():bindings).ToImmutableArray())));
        var result=session.Snapshot.Features[hole.Id];Assert.Equal(2400-20*Math.PI,result.Result.VolumeMm3,5);Assert.Equal(20,((HoleRecipe)result.Recipe).Frame.Translation.Z,6);session.Snapshot.Validate();
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SimultaneousShaftAndHeadDiametersValidateFinalRecipeInEitherOrder(bool reverse)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Dimensions"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Source"));var body=session.Snapshot.Bodies.Values.Single();
        await session.ExecuteAsync(new AddHoleCommand(body.Id,Recipe(body.Geometry,false,HoleHead.Counterbore),"Hole"));var hole=session.Snapshot.Features.Values.Single(f=>f.Recipe is HoleRecipe);
        var diameter=new CadParameter(Guid.NewGuid(),"diameter",ParameterDimension.Length,"5 mm");var head=new CadParameter(Guid.NewGuid(),"head",ParameterDimension.Length,"8 mm");
        ParameterBinding[] bindings=[new(Guid.NewGuid(),diameter.Id,ParameterTarget.Feature(hole.Id,"Diameter")),new(Guid.NewGuid(),head.Id,ParameterTarget.Feature(hole.Id,"HeadDiameter"))];
        await session.ExecuteAsync(new SetParametersCommand(new([diameter,head],(reverse?bindings.Reverse():bindings).ToImmutableArray())));
        Assert.Equal(1800-(32+25)*Math.PI,session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,5);session.Snapshot.Validate();
    }
    [Fact] public async Task HoleExportsExactStepAndThreeFormatsAndRestoresSuppressedSourceOutput()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Exchange"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Source"));var body=session.Snapshot.Bodies.Values.Single();
            await session.ExecuteAsync(new AddHoleCommand(body.Id,Recipe(body.Geometry,false,HoleHead.Countersink),"Hole"));var hole=session.Snapshot.Features.Values.Single(f=>f.Recipe is HoleRecipe);var accepted=session.Snapshot;
            foreach(string extension in new[]{"step","iges","stl"}){var path=files.PathFor("hole."+extension);await kernel.ExportAsync(accepted,assets,path);Assert.True(new FileInfo(path).Length>100);}
            using(var native=OcctSharp.ShapeExchange.ReadStep(files.PathFor("hole.step")))Assert.Equal(hole.Result.VolumeMm3,native.InspectProperties(OcctSharp.InspectionPropertyKind.Volume).Mass,5);
            using(var loaded=await kernel.ImportAsync(files.PathFor("hole.step"),assets)){loaded.Snapshot.Validate();Assert.Equal(hole.Result.VolumeMm3,loaded.Snapshot.Bodies.Values.Sum(b=>b.Geometry.VolumeMm3),5);}
            await session.ExecuteAsync(new SetFeatureSuppressionCommand(hole.Id,true));Assert.Equal(1800,session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,6);
            await session.ExecuteAsync(new SetFeatureSuppressionCommand(hole.Id,false));Assert.Equal(hole.Result.VolumeMm3,session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,5);session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
}
