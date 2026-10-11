using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Occt;
using Cadoryx.Kernel.Abstractions;
using Xunit;

namespace Cadoryx.Tests;

public class RepetitionTests
{
    private static RepetitionRecipe Recipe(GeometryAssetRef source,RepetitionKind kind,int count,double step,RigidTransform3d? frame=null,
        RepetitionOutput output=RepetitionOutput.Compound,bool include=true,bool full=false)=>
        new(source,kind,frame??RigidTransform3d.Identity,count,step,Enumerable.Range(0,count).Select(_=>Guid.NewGuid()).ToImmutableArray(),output,include,full);
    [Theory][InlineData(true,2,48)][InlineData(false,1,24)]
    public async Task ExactPlaneMirrorPreservesVolumeAndPlacement(bool include,int count,double volume)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Mirror"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Source"));var source=session.Snapshot.Bodies.Values.Single();
            var frame=new RigidTransform3d(new(5,0,0),Quaterniond.FromAxisAngle(new(0,1,0),Math.PI/2));
            var before=session.Snapshot;await session.ExecuteAsync(new AddRepetitionCommand(source.Id,Recipe(source.Geometry,RepetitionKind.Mirror,count,0,frame,include:include),"Mirror"));
            var after=session.Snapshot;var result=after.Bodies.Values.Single();Assert.Equal(volume,result.Geometry.VolumeMm3,6);
            if(!include)Assert.Equal(BodyKind.Solid,result.Geometry.Kind);
            Assert.Equal(include?0:8,result.Geometry.Bounds.Min.X,5);Assert.Equal(10,result.Geometry.Bounds.Max.X,5);
            Assert.Equal(2,after.Features.Count);Assert.Single(after.Features[result.Producer!.Value].Inputs);
            Assert.Equal(4,Assert.IsType<TopologyHistory>(after.Features[result.Producer.Value].TopologyHistory).SchemaVersion);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(RepetitionOutput.Compound,72)][InlineData(RepetitionOutput.Fused,48)]
    public async Task OverlappingLinearCopiesHaveExplicitCompoundOrUnionSemantics(RepetitionOutput output,double volume)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Array"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Source"));var source=session.Snapshot.Bodies.Values.Single();
            await session.ExecuteAsync(new AddRepetitionCommand(source.Id,Recipe(source.Geometry,RepetitionKind.Linear,3,1,output:output),"Array"));
            var result=session.Snapshot.Bodies.Values.Single().Geometry;Assert.Equal(volume,result.VolumeMm3,5);Assert.Equal(4,result.Bounds.Max.X,5);session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CircularCopiesUseExplicitOriginAxisAndDistinctFullRing(bool full)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Circle"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,2,2,RigidTransform3d.Translate(15,0,0)),"Source"));var source=session.Snapshot.Bodies.Values.Single();
        await session.ExecuteAsync(new AddRepetitionCommand(source.Id,Recipe(source.Geometry,RepetitionKind.Circular,4,full?0:Math.PI/2,RigidTransform3d.Translate(5,0,0),full:full),"Ring"));
        var result=session.Snapshot.Bodies.Values.Single().Geometry;Assert.Equal(32,result.VolumeMm3,6);Assert.Equal(-7,result.Bounds.Min.X,5);Assert.Equal(17,result.Bounds.Max.X,5);
    }
    [Fact] public async Task ParametersDatumSourceAndBooleanDownstreamUpdateAtomicallyWithStableCopyIdsAndStorage()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Linked"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Source"));var source=session.Snapshot.Bodies.Values.Single();var sourceFeature=source.Producer!.Value;
            var datum=new CadDatumPlane(Guid.NewGuid(),source.PartId,"YZ + offset",DatumPlaneKind.Offset,DatumPlaneSource.Base(DocumentWorkPlaneKind.YZ),Value:5);
            await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Planes=ImmutableDictionary<Guid,CadDatumPlane>.Empty.Add(datum.Id,datum)}));datum=session.Snapshot.Datums.Planes[datum.Id];
            var recipe=Recipe(source.Geometry,RepetitionKind.Linear,3,10,datum.Frame) with{Datum=datum.Id,DatumRevision=datum.Revision};
            await session.ExecuteAsync(new AddRepetitionCommand(source.Id,recipe,"Array"));var array=session.Snapshot.Features.Values.Single(f=>f.Recipe is RepetitionRecipe);var ids=recipe.CopyIds;
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(100,100,100,RigidTransform3d.Translate(-50,-50,-50)),"Envelope",source.PartId));
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Common,session.Snapshot.Bodies.Keys));var downstream=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
            var count=new CadParameter(Guid.NewGuid(),"copies",ParameterDimension.Scalar,"4");var spacing=new CadParameter(Guid.NewGuid(),"spacing",ParameterDimension.Length,"12 mm");
            var before=session.Snapshot;
            await session.ExecuteAsync(new SetParametersCommand(new([count,spacing],[new(Guid.NewGuid(),count.Id,ParameterTarget.Feature(array.Id,"Count")),new(Guid.NewGuid(),spacing.Id,ParameterTarget.Feature(array.Id,"Spacing"))])));
            var after=session.Snapshot;var grown=Assert.IsType<RepetitionRecipe>(after.Features[array.Id].Recipe);Assert.Equal(ids,grown.CopyIds.Take(3));Assert.Equal(96,after.Features[downstream.Id].Result.VolumeMm3,5);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            await session.ExecuteAsync(new RecomputeCommand(sourceFeature,new BoxRecipe(4,3,4,RigidTransform3d.Identity)));Assert.Equal(192,session.Snapshot.Features[downstream.Id].Result.VolumeMm3,5);
            var changed=session.Snapshot.Datums.Planes[datum.Id] with{Kind=DatumPlaneKind.Angle,Value=Math.PI/2,Hinge=DatumHinge.U};
            await session.ExecuteAsync(new SetDatumsCommand(session.Snapshot.Datums with{Planes=session.Snapshot.Datums.Planes.SetItem(datum.Id,changed)}));
            Assert.Equal(session.Snapshot.Datums.Planes[datum.Id].Revision,Assert.IsType<RepetitionRecipe>(session.Snapshot.Features[array.Id].Recipe).DatumRevision);session.Snapshot.Validate();
            var path=files.PathFor("array.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
            loaded.Snapshot.Validate();var restored=Assert.IsType<RepetitionRecipe>(loaded.Snapshot.Features[array.Id].Recipe);Assert.Equal(grown.CopyIds.ToArray(),restored.CopyIds.ToArray());
            Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["features"].Version,FormatEvolutionTests.Manifest(path).Sections.Single(s=>s.Kind=="features").SchemaVersion);Assert.Contains("cadoryx.result-repetition.1",FormatEvolutionTests.Manifest(path).RequiredCapabilities);
            FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=22}:s).ToImmutableArray()});
            await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task InvalidCountsDimensionsSourcesIdentityAndCancellationRetainExactSnapshot()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Invalid"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Source"));var source=session.Snapshot.Bodies.Values.Single();var valid=Recipe(source.Geometry,RepetitionKind.Linear,3,5);
        var before=session.Snapshot;int assetsBefore=assets.Count;
        foreach(var invalid in new[]{valid with{Step=0},valid with{Count=300},valid with{Kind=(RepetitionKind)99},valid with{Datum=Guid.NewGuid(),DatumRevision=Guid.NewGuid()},
            valid with{CopyIds=[Guid.NewGuid(),Guid.Empty,Guid.NewGuid()]},valid with{Kind=RepetitionKind.Circular,Step=Math.PI},valid with{Kind=RepetitionKind.Circular,FullCircle=true}})
        {await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddRepetitionCommand(source.Id,invalid,"Invalid")));Assert.Same(before,session.Snapshot);Assert.Equal(assetsBefore,assets.Count);}
        using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ExecuteAsync(new AddRepetitionCommand(source.Id,valid,"Canceled"),cancel.Token));Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(new AddRepetitionCommand(source.Id,valid,"Array"));var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is RepetitionRecipe);before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new RecomputeCommand(feature.Id,valid with{CopyIds=valid.CopyIds.SetItem(0,Guid.NewGuid())})));Assert.Same(before,session.Snapshot);
        foreach(string expression in new[]{"2.5","257","4 mm"})
        {
            var p=new CadParameter(Guid.NewGuid(),"copies",ParameterDimension.Scalar,expression);
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(new([p],[new(Guid.NewGuid(),p.Id,ParameterTarget.Feature(feature.Id,"Count"))]))));Assert.Same(before,session.Snapshot);
        }
        var shrunk=valid.Resize(2);await session.ExecuteAsync(new RecomputeCommand(feature.Id,shrunk));var regrown=shrunk.Resize(3);Assert.NotEqual(valid.CopyIds[2],regrown.CopyIds[2]);
        await session.ExecuteAsync(new RecomputeCommand(feature.Id,regrown));Assert.Equal(valid.CopyIds.Take(2),regrown.CopyIds.Take(2));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SimultaneousCircularCountAndAngleWritesValidateFinalCandidateInEitherBindingOrder(bool reverse)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Dimensions"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(10,0,0)),"Source"));var source=session.Snapshot.Bodies.Values.Single();
        await session.ExecuteAsync(new AddRepetitionCommand(source.Id,Recipe(source.Geometry,RepetitionKind.Circular,3,Math.PI/2),"Circular"));var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is RepetitionRecipe);
        var count=new CadParameter(Guid.NewGuid(),"copies",ParameterDimension.Scalar,"8");var angle=new CadParameter(Guid.NewGuid(),"angle",ParameterDimension.Angle,"30 deg");
        ParameterBinding[] bindings=[new(Guid.NewGuid(),count.Id,ParameterTarget.Feature(feature.Id,"Count")),new(Guid.NewGuid(),angle.Id,ParameterTarget.Feature(feature.Id,"Angle"))];
        await session.ExecuteAsync(new SetParametersCommand(new([count,angle],(reverse?bindings.Reverse():bindings).ToImmutableArray())));
        Assert.Equal(8,Assert.IsType<RepetitionRecipe>(session.Snapshot.Features[feature.Id].Recipe).Count);Assert.Equal(8,session.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
    }
    [Fact] public async Task SignedLinearDirectionAndDatumRevisionRefreshFollowAuthoredBoxBoundaryAndSuppressRestore()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Datum"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Source"));var source=session.Snapshot.Bodies.Values.Single();var sourceFeature=source.Producer!.Value;
        var datum=new CadDatumPlane(Guid.NewGuid(),source.PartId,"Top",DatumPlaneKind.Offset,DatumPlaneSource.Box(sourceFeature,BoxBoundary.ZMax),Value:1);
        await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Planes=ImmutableDictionary<Guid,CadDatumPlane>.Empty.Add(datum.Id,datum)}));datum=session.Snapshot.Datums.Planes[datum.Id];
        var mirror=Recipe(source.Geometry,RepetitionKind.Mirror,1,0,datum.Frame,include:false) with{Datum=datum.Id,DatumRevision=datum.Revision};
        await session.ExecuteAsync(new AddRepetitionCommand(source.Id,mirror,"Mirror"));var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is RepetitionRecipe);
        Assert.Equal(6,feature.Result.Bounds.Min.Z,5);await session.ExecuteAsync(new RecomputeCommand(sourceFeature,new BoxRecipe(2,3,8,RigidTransform3d.Identity)));
        var recipe=Assert.IsType<RepetitionRecipe>(session.Snapshot.Features[feature.Id].Recipe);Assert.Equal(10,session.Snapshot.Features[feature.Id].Result.Bounds.Min.Z,5);
        Assert.Equal(session.Snapshot.Datums.Planes[datum.Id].Revision,recipe.DatumRevision);session.Snapshot.Validate();
        var before=session.Snapshot;await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new RecomputeCommand(feature.Id,recipe with{Frame=RigidTransform3d.Identity})));Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(new SetFeatureSuppressionCommand(feature.Id,true));Assert.True(session.Snapshot.Features[feature.Id].IsStale);Assert.Single(session.Snapshot.Bodies);
        await session.ExecuteAsync(new SetFeatureSuppressionCommand(feature.Id,false));session.Snapshot.Validate();Assert.False(session.Snapshot.Features[feature.Id].IsStale);
        await session.ExecuteAsync(new RecomputeCommand(feature.Id,recipe with{Datum=null,DatumRevision=null}));await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty));session.Snapshot.Validate();
        var signed=Recipe(session.Snapshot.Bodies.Values.Single().Geometry,RepetitionKind.Linear,3,-5,new(Vector3d.Zero,Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/2)));
        await session.ExecuteAsync(new AddRepetitionCommand(session.Snapshot.Bodies.Keys.Single(),signed,"Signed"));Assert.Equal(-10,session.Snapshot.Bodies.Values.Single().Geometry.Bounds.Min.Y,5);
    }
    [Fact] public async Task ExactMirrorExportsThreeFormatsAndStepRoundtripRetainsVolume()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Exchange"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Source"));var source=session.Snapshot.Bodies.Values.Single();
            var frame=new RigidTransform3d(new(5,0,0),Quaterniond.FromAxisAngle(new(0,1,0),Math.PI/2));
            await session.ExecuteAsync(new AddRepetitionCommand(source.Id,Recipe(source.Geometry,RepetitionKind.Mirror,2,0,frame),"Mirror"));
            foreach(string extension in new[]{"step","iges","stl"})
            {var path=files.PathFor("mirror."+extension);await kernel.ExportAsync(session.Snapshot,assets,path);Assert.True(new FileInfo(path).Length>100);}
            using(var exact=OcctSharp.ShapeExchange.ReadStep(files.PathFor("mirror.step")))
                Assert.Equal(48,exact.InspectProperties(OcctSharp.InspectionPropertyKind.Volume).Mass,5);
            using(var noColors=OcctSharp.XdeDocument.ReadStep(files.PathFor("mirror.step"),new OcctSharp.XdeStepReadOptions{ReadColors=false}))
            {
                double NativeVolume(OcctSharp.XdeLabel label)
                {if(label.IsAssembly)return label.GetComponents().Sum(c=>NativeVolume(c.ReferredShape));using var shape=label.Shape;return shape.InspectProperties(OcctSharp.InspectionPropertyKind.Volume).Mass;}
                Assert.Equal(48,noColors.GetFreeShapes().Sum(NativeVolume),5);
            }
            using var loaded=await kernel.ImportAsync(files.PathFor("mirror.step"),assets);loaded.Snapshot.Validate();
            double instanceVolume=loaded.Snapshot.EnumerateOccurrences().Where(o=>loaded.Snapshot.Definitions[o.DefinitionId] is PartDefinition)
                .Sum(o=>((PartDefinition)loaded.Snapshot.Definitions[o.DefinitionId]).Bodies.Sum(id=>loaded.Snapshot.Bodies[id].Geometry.VolumeMm3));
            Assert.True(Math.Abs(instanceVolume-48)<1e-5,"Instance volume "+instanceVolume+"; definitions: "+string.Join("; ",loaded.Snapshot.Definitions.Values.Select(d=>
                d.Name+" "+(d is PartDefinition p?"part volume="+p.Bodies.Sum(id=>loaded.Snapshot.Bodies[id].Geometry.VolumeMm3):"assembly children="+((AssemblyDefinition)d).Children.Length))));
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task LegacyVersionTwentyTwoWithoutRepetitionMigratesAndForeignDatumOwnerRejects()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();await using var session=new CadDocumentSession(DocumentSnapshot.Create("Migration"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Source"));var source=session.Snapshot.Bodies.Values.Single();var path=files.PathFor("old.cadoryx");
        await session.SaveAsync(new CadDocumentStorage(),path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=22}:s).ToImmutableArray()});
        using(var loaded=await new CadDocumentStorage().LoadAsync(path,assets)){loaded.Snapshot.Validate();Assert.IsType<BoxRecipe>(loaded.Snapshot.Features.Values.Single().Recipe);}
        var part=DefinitionId.New();await session.ExecuteAsync(ResourceCommands.AddPart(part,"Other"));var datum=new CadDatumPlane(Guid.NewGuid(),part,"Foreign",DatumPlaneKind.Offset,DatumPlaneSource.Base(DocumentWorkPlaneKind.XY));
        await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Planes=ImmutableDictionary<Guid,CadDatumPlane>.Empty.Add(datum.Id,datum)}));var before=session.Snapshot;
        var recipe=Recipe(source.Geometry,RepetitionKind.Linear,3,5) with{Datum=datum.Id,DatumRevision=session.Snapshot.Datums.Planes[datum.Id].Revision};
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddRepetitionCommand(source.Id,recipe,"Foreign")));Assert.Same(before,session.Snapshot);
    }
}
