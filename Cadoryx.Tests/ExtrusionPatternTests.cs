using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using MessagePack;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class ExtrusionPatternTests
{
    [Theory][InlineData(ExtrusionOperation.Fuse,RepetitionKind.Linear,true)][InlineData(ExtrusionOperation.Cut,RepetitionKind.Linear,true)]
    [InlineData(ExtrusionOperation.Fuse,RepetitionKind.Circular,true)][InlineData(ExtrusionOperation.Cut,RepetitionKind.Circular,true)]
    [InlineData(ExtrusionOperation.Fuse,RepetitionKind.Mirror,true)][InlineData(ExtrusionOperation.Cut,RepetitionKind.Mirror,true)]
    [InlineData(ExtrusionOperation.Fuse,RepetitionKind.Mirror,false)][InlineData(ExtrusionOperation.Cut,RepetitionKind.Mirror,false)]
    public async Task ReplaysAsymmetricProfileOperationOnOneSolidAndRoundtrips(ExtrusionOperation operation,RepetitionKind kind,bool include)
    {
        using var files=new TestFiles();await using var f=new Fixture();await f.Initialize(operation);
        var layout=f.Layout(kind,include);var recipe=new ExtrusionPatternRecipe(f.Seed.Id,(ExtrudeRecipe)f.Seed.Recipe,layout);
        var before=f.Session.Snapshot;await f.Session.ExecuteAsync(new AddExtrusionPatternCommand(f.Seed.OutputBodyId,recipe,"Repeated operation"));
        var after=f.Session.Snapshot;after.Validate();var pattern=after.Features.Values.Single(p=>p.Recipe is ExtrusionPatternRecipe);
        double delta=operation==ExtrusionOperation.Fuse?6:-4;Assert.Equal(4500+layout.Count*delta,Assert.Single(after.Bodies.Values).Geometry.VolumeMm3,5);
        Assert.Null(pattern.TopologyHistory);
        using(var shape=OcctGeometryBridge.ReadShape(pattern.Result,f.Assets))using(var map=RepairSnapshot.Create(shape))
        {
            Assert.Single(map.Topology,p=>p.Kind==ShapeKind.Solid);Assert.True(shape.GetValidationReport(true,true).IsValid);Assert.True(shape.GetSelfInterferenceReport().IsClear);
            if(kind==RepetitionKind.Mirror&&!include)
            {
                using var seed=OcctGeometryBridge.ReadShape(f.Seed.Result,f.Assets);using var mirror=GpTrsf.CreatePlaneMirror(15,0,0,1,0,0);
                using var expected=seed.Transformed(mirror);using var extra=shape.Cut(expected);using var missing=expected.Cut(shape);
                Assert.InRange(extra.InspectProperties(InspectionPropertyKind.Volume).Mass,0,1e-5);Assert.InRange(missing.InspectProperties(InspectionPropertyKind.Volume).Mass,0,1e-5);
            }
        }
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
        var storage=new CadDocumentStorage();var path=files.PathFor("replay.cadoryx");await f.Session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,f.Assets))
        {
            loaded.Snapshot.Validate();var restored=(ExtrusionPatternRecipe)loaded.Snapshot.Features[pattern.Id].Recipe;
            Assert.True(recipe.SameTemplate(restored.Template));Assert.True(layout.CopyIds.SequenceEqual(restored.Layout.CopyIds));
            using var result=await f.Kernel.EvaluateAsync(restored,f.Assets);Assert.Equal(pattern.Result.VolumeMm3,result.Geometry.VolumeMm3,5);
        }
        Assert.Contains("cadoryx.extrusion-replay.1",FormatEvolutionTests.Manifest(path).RequiredCapabilities);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=29}:s).ToImmutableArray()});
        int count=f.Assets.Count;await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
    }

    [Theory][InlineData(ExtrusionSide.Negative,ExtrusionTermination.Through,0,4470)]
    [InlineData(ExtrusionSide.Symmetric,ExtrusionTermination.Distance,4,4488)]
    [InlineData(ExtrusionSide.TwoSides,ExtrusionTermination.Distance,1,4488)]
    [InlineData(ExtrusionSide.Negative,ExtrusionTermination.Plane,0,4482)]
    public async Task EachCopyRetainsDirectedExtentAndTransformedLocalLimit(ExtrusionSide side,ExtrusionTermination termination,double distance,double expected)
    {
        await using var f=new Fixture();await f.Initialize(ExtrusionOperation.Cut,side,termination,distance);
        await f.Session.ExecuteAsync(new AddExtrusionPatternCommand(f.Seed.OutputBodyId,new(f.Seed.Id,(ExtrudeRecipe)f.Seed.Recipe,f.Layout(RepetitionKind.Linear)),"Directed"));
        f.Session.Snapshot.Validate();Assert.InRange(Assert.Single(f.Session.Snapshot.Bodies.Values).Geometry.VolumeMm3,expected-1e-4,expected+1e-4);
    }

    [Fact] public async Task SeedDriversDatumDrawingSuppressionAndIndependentGraphStayAssociative()
    {
        await using var f=new Fixture();await f.Initialize(ExtrusionOperation.Fuse);
        var axis=new CadDatumAxis(Guid.NewGuid(),f.Seed.PartId,"Axis",DatumAxisKind.Independent,Direction:Vector3d.UnitZ);
        await f.Session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Axes=DatumBook.Empty.Axes.Add(axis.Id,axis)}));axis=f.Session.Snapshot.Datums.Axes[axis.Id];
        var layout=f.Layout(RepetitionKind.Linear) with{Frame=axis.Frame,Datum=axis.Id,DatumRevision=axis.Revision};
        await f.Session.ExecuteAsync(new AddExtrusionPatternCommand(f.Seed.OutputBodyId,new(f.Seed.Id,(ExtrudeRecipe)f.Seed.Recipe,layout),"Repeated"));
        var pattern=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is ExtrusionPatternRecipe);var sheet=TechnicalDrawingSheet.A4Landscape("Replay");
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,100),1));
        var count=new CadParameter(Guid.NewGuid(),"count",ParameterDimension.Scalar,"4");var spacing=new CadParameter(Guid.NewGuid(),"spacing",ParameterDimension.Length,"6 mm");
        var height=new CadParameter(Guid.NewGuid(),"height",ParameterDimension.Length,"4 mm");
        await f.Session.ExecuteAsync(new SetParametersCommand(new([count,spacing,height],[new(Guid.NewGuid(),count.Id,ParameterTarget.Feature(pattern.Id,"Count")),
            new(Guid.NewGuid(),spacing.Id,ParameterTarget.Feature(pattern.Id,"Spacing")),new(Guid.NewGuid(),height.Id,ParameterTarget.Feature(f.Seed.Id,"Distance"))])));
        var driven=f.Session.Snapshot;Assert.Equal(4532,driven.Features[pattern.Id].Result.VolumeMm3,5);
        Assert.All(driven.DrawingSheets[sheet.Id].Views,v=>{Assert.Null(v.StaleReason);Assert.Equal(driven.Features[pattern.Id].Result.Revision,v.Sources.Single().Revision);});
        var root=new OccurrencePath(driven.Id,[]);var slot=ComponentSlotId.New();await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,f.Seed.PartId,slot,"Second",RigidTransform3d.Translate(50,0,0)));
        var independent=AssemblyOccurrenceCommands.MakePartIndependent(root.Append(slot));await f.Session.ExecuteAsync(independent);
        var cloned=f.Session.Snapshot.Features.Values.Single(p=>p.PartId==independent.ResultPartId&&p.Recipe is ExtrusionPatternRecipe);var clone=(ExtrusionPatternRecipe)cloned.Recipe;
        Assert.Equal(cloned.PartId,f.Session.Snapshot.Features[clone.Seed].PartId);Assert.Equal(cloned.PartId,f.Session.Snapshot.Datums.Axes[clone.Layout.Datum!.Value].PartId);
        await f.Session.ExecuteAsync(new SetFeatureSuppressionCommand(f.Seed.Id,true));Assert.True(f.Session.Snapshot.Features[pattern.Id].IsStale);Assert.False(f.Session.Snapshot.Features[cloned.Id].IsStale);
        await f.Session.ExecuteAsync(new SetFeatureSuppressionCommand(f.Seed.Id,false));f.Session.Snapshot.Validate();Assert.Equal(4532,f.Session.Snapshot.Features[pattern.Id].Result.VolumeMm3,5);
    }

    [Fact] public async Task LinkedSketchRadiusUpdatesAllOperationsAfterReopenWithoutCopyingTheBase()
    {
        using var files=new TestFiles();await using var f=new Fixture();await f.Initialize(ExtrusionOperation.Cut);
        await f.Session.UndoAsync();var plate=Assert.Single(f.Session.Snapshot.Bodies.Values);var solver=new ManagedSketchConstraintSolver();
        var draft=new SketchDraft(CadSketch.Create(plate.PartId,"Tool",RigidTransform3d.Translate(0,0,5)));draft.AddCircle(new(5,5),1);
        await f.Session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));var sketch=f.Session.Snapshot.Sketches[draft.Value.Id];var reference=SketchProfileReference.CreateCircle(sketch,sketch.Circles.Single().Id);
        var extrusion=new ExtrudeRecipe(new([],new(new(5,5),1)),0,sketch.Plane,new(ExtrusionSide.Negative,Operation:ExtrusionOperation.Cut,Source:plate.Geometry,Termination:ExtrusionTermination.Through));
        await f.Session.ExecuteAsync(new AddExtrusionCommand(extrusion,"Linked seed",targetBody:plate.Id,sketchSource:reference));var seed=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is ExtrudeRecipe);
        var layout=new RepetitionRecipe(seed.Result,RepetitionKind.Linear,RigidTransform3d.Identity,3,8,[Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid()],RepetitionOutput.Fused);
        await f.Session.ExecuteAsync(new AddExtrusionPatternCommand(seed.OutputBodyId,new(seed.Id,(ExtrudeRecipe)seed.Recipe,layout),"Linked copies"));var pattern=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is ExtrusionPatternRecipe);
        var radius=new CadParameter(Guid.NewGuid(),"radius",ParameterDimension.Length,"2 mm");
        await f.Session.ExecuteAsync(new SetParametersCommand(new([radius],[new(Guid.NewGuid(),radius.Id,ParameterTarget.Sketch(sketch.Id,sketch.Constraints.OfType<RadiusConstraint>().Single().Id))])));
        Assert.Equal(2,f.Session.Snapshot.Sketches[sketch.Id].Circles.Single().Radius,10);
        AssertCircularCuts(f.Session.Snapshot.Features[pattern.Id].Result,2,f.Assets);Assert.Single(f.Session.Snapshot.Bodies);
        var file=files.PathFor("linked.cadoryx");var storage=new CadDocumentStorage();await f.Session.SaveAsync(storage,file);
        using var loaded=await storage.LoadAsync(file,f.Assets);await using var reopened=new CadDocumentSession(loaded.Snapshot,f.Assets,f.Kernel,new InlineSessionDispatcher());
        await reopened.ExecuteAsync(new SetParametersCommand(loaded.Snapshot.Parameters with{Parameters=[radius with{Expression="1.5 mm"}]}));
        reopened.Snapshot.Validate();Assert.Equal(1.5,reopened.Snapshot.Sketches[sketch.Id].Circles.Single().Radius,10);
        AssertCircularCuts(reopened.Snapshot.Features[pattern.Id].Result,1.5,f.Assets);
        Assert.Equal(seed.Id,((ExtrusionPatternRecipe)reopened.Snapshot.Features[pattern.Id].Recipe).Seed);
    }

    [Fact] public async Task InvalidSourceNoOpDisconnectedFuseAndCancelledWorkLeaveBaselineAndAssets()
    {
        await using var f=new Fixture();await f.Initialize(ExtrusionOperation.Fuse);var before=f.Session.Snapshot;int assets=f.Assets.Count;
        var recipe=new ExtrusionPatternRecipe(f.Seed.Id,(ExtrudeRecipe)f.Seed.Recipe,f.Layout(RepetitionKind.Linear));
        foreach(var invalid in new[]{recipe with{Seed=FeatureId.New()},recipe with{Template=recipe.Template with{Distance=4}},
            recipe with{Layout=recipe.Layout with{Step=50}},recipe with{Layout=recipe.Layout with{Step=1e-7}},
            recipe with{Layout=recipe.Layout with{Output=RepetitionOutput.Compound}}})
        {await Assert.ThrowsAnyAsync<Exception>(()=>f.Session.ExecuteAsync(new AddExtrusionPatternCommand(f.Seed.OutputBodyId,invalid,"Invalid")));Assert.Same(before,f.Session.Snapshot);Assert.Equal(assets,f.Assets.Count);}
        using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Session.ExecuteAsync(new AddExtrusionPatternCommand(f.Seed.OutputBodyId,recipe,"Cancelled"),cancel.Token));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(assets,f.Assets.Count);
    }

    [Theory][InlineData("seed")][InlineData("template")][InlineData("hidden")][InlineData("nested-sources")]
    public async Task ForgedStoredOperationIsRejectedWithoutRetainedAssets(string mutation)
    {
        using var files=new TestFiles();await using var f=new Fixture();await f.Initialize(ExtrusionOperation.Cut);
        await f.Session.ExecuteAsync(new AddExtrusionPatternCommand(f.Seed.OutputBodyId,new(f.Seed.Id,(ExtrudeRecipe)f.Seed.Recipe,f.Layout(RepetitionKind.Linear)),"Repeated"));
        var storage=new CadDocumentStorage();var path=files.PathFor("bad.cadoryx");await f.Session.SaveAsync(storage,path);int assets=f.Assets.Count;
        FormatEvolutionTests.RewriteSection(path,"features",bytes=>
        {
            var pack=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes,MessagePackSerializerOptions.Standard);
            return MessagePackSerializer.Serialize(pack with{Features=pack.Features.Select(feature=>
            {
                if(feature.Recipe.ExtrusionPattern is not {} p)return feature;var r=feature.Recipe;
                r=mutation switch
                {
                    "seed"=>r with{ExtrusionPattern=p with{Seed=Guid.NewGuid()}},
                    "template"=>r with{ExtrusionPattern=p with{Template=p.Template with{Numbers=[4]}}},
                    "hidden"=>r with{Repetition=p.Layout},
                    _=>r with{ExtrusionPattern=p with{Template=p.Template with{Sources=[null!]}}}
                };
                return feature with{Recipe=r};
            }).ToArray()},MessagePackSerializerOptions.Standard);
        });
        await Assert.ThrowsAnyAsync<Exception>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(assets,f.Assets.Count);
    }

    private static void AssertCircularCuts(GeometryAssetRef geometry,double radius,MemoryAssetStore assets)
    {
        using var actual=OcctGeometryBridge.ReadShape(geometry,assets);Shape expected=ShapeFactory.CreateBox(30,30,5);
        try
        {
            for(int i=0;i<3;i++)
            {
                using var cylinder=ShapeFactory.CreateCylinder(radius,5);using var position=OcctGeometryBridge.ToNative(RigidTransform3d.Translate(5+i*8,5,0));
                using var tool=cylinder.Transformed(position);var next=expected.Cut(tool);expected.Dispose();expected=next;
            }
            // Native default volume integration of trimmed prism surfaces is non-adaptive.
            // Prove actual material against independently constructed analytic cylinder cutters, in both directions.
            using var extra=actual.Cut(expected);using var missing=expected.Cut(actual);
            Assert.Equal(0,extra.GetTopologySummary().UniqueCounts.SolidCount);Assert.Equal(0,missing.GetTopologySummary().UniqueCounts.SolidCount);
            double analytic=4500-3*5*Math.PI*radius*radius;Assert.InRange(geometry.VolumeMm3,analytic-1e-3,analytic+1e-3);
            Assert.True(actual.GetValidationReport(true,true).IsValid);Assert.True(actual.GetSelfInterferenceReport().IsClear);
        }
        finally{expected.Dispose();}
    }

    private sealed class Fixture:IAsyncDisposable
    {
        public MemoryAssetStore Assets {get;}=new();public OcctGeometryKernel Kernel {get;}=new();public CadDocumentSession Session {get;}
        public FeatureDefinition Seed {get;private set;}=null!;
        public Fixture()=>Session=new(DocumentSnapshot.Create("Extrusion replay"),Assets,Kernel,new InlineSessionDispatcher());
        public async Task Initialize(ExtrusionOperation operation,ExtrusionSide? side=null,ExtrusionTermination termination=ExtrusionTermination.Distance,double? distance=null)
        {
            await Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(30,30,5,RigidTransform3d.Identity),"Plate"));var plate=Session.Snapshot.Bodies.Values.Single();
            var options=new ExtrusionOptions(side??(operation==ExtrusionOperation.Cut?ExtrusionSide.Negative:ExtrusionSide.Positive),
                side==ExtrusionSide.TwoSides?2:0,operation,plate.Geometry,termination,
                termination==ExtrusionTermination.Plane?RigidTransform3d.Translate(0,0,2):null);
            var profile=new SketchProfile([new(0,0),new(2,0),new(1,2)]);
            await Session.ExecuteAsync(new AddExtrusionCommand(new(profile,distance??(operation==ExtrusionOperation.Fuse?3:2),RigidTransform3d.Translate(5,5,5),options),"Seed",targetBody:plate.Id));
            Seed=Session.Snapshot.Features.Values.Single(p=>p.Recipe is ExtrudeRecipe);
        }
        public RepetitionRecipe Layout(RepetitionKind kind,bool include=true)
        {
            int count=kind==RepetitionKind.Mirror?(include?2:1):3;
            var frame=kind==RepetitionKind.Mirror?new RigidTransform3d(new(15,0,0),Quaterniond.FromEulerDegrees(0,90,0)):
                kind==RepetitionKind.Circular?RigidTransform3d.Translate(10,10,5):RigidTransform3d.Identity;
            return new(Seed.Result,kind,frame,count,kind==RepetitionKind.Linear?8:0,Enumerable.Range(0,count).Select(_=>Guid.NewGuid()).ToImmutableArray(),RepetitionOutput.Fused,include,kind==RepetitionKind.Circular);
        }
        public async ValueTask DisposeAsync(){await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
}
