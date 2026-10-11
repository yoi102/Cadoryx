using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using MessagePack;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class HolePatternTests
{
    [Theory][InlineData(RepetitionKind.Linear,HoleHead.Plain,true)][InlineData(RepetitionKind.Circular,HoleHead.Plain,true)]
    [InlineData(RepetitionKind.Mirror,HoleHead.Plain,true)][InlineData(RepetitionKind.Mirror,HoleHead.Plain,false)]
    [InlineData(RepetitionKind.Linear,HoleHead.Counterbore,true)][InlineData(RepetitionKind.Circular,HoleHead.Countersink,true)]
    public async Task ReplaysDrillingOnOnePlateWithExactCountDirectionHeadAndRoundtrip(RepetitionKind kind,HoleHead head,bool include)
    {
        using var files=new TestFiles();await using var f=new Fixture();await f.Initialize(head);
        var layout=f.Layout(kind,include);var recipe=new HolePatternRecipe(f.Seed.Id,(HoleRecipe)f.Seed.Recipe,layout);
        var before=f.Session.Snapshot;double removal=4500-f.Seed.Result.VolumeMm3;
        await f.Session.ExecuteAsync(new AddHolePatternCommand(f.Seed.OutputBodyId,recipe,"Holes"));var after=f.Session.Snapshot;
        var pattern=after.Features.Values.Single(p=>p.Recipe is HolePatternRecipe);var body=Assert.Single(after.Bodies.Values);
        Assert.Equal(4500-layout.Count*removal,body.Geometry.VolumeMm3,5);Assert.Equal(pattern.OutputBodyId,body.Id);
        using(var shape=OcctGeometryBridge.ReadShape(body.Geometry,f.Assets))
        using(var map=RepairSnapshot.Create(shape))
        {
            Assert.Single(map.Topology,t=>t.Kind==ShapeKind.Solid);Assert.True(shape.GetValidationReport(true,true).IsValid);
            Assert.True(shape.GetSelfInterferenceReport().IsClear);
            if(!include)
            {
                using var cutter=ShapeFactory.CreateCylinder(1,5);using var move=OcctGeometryBridge.ToNative(RigidTransform3d.Translate(5,5,0));
                using var seedTool=cutter.Transformed(move);using var common=shape.Common(seedTool);
                Assert.Equal(5*Math.PI,common.InspectProperties(InspectionPropertyKind.Volume).Mass,5);
            }
        }
        // The rotated countersink's pcurve origin changes on BRep normalization.
        // A valid solid alone cannot authorize original-to-final topology evidence.
        bool verified=include&&!(kind==RepetitionKind.Circular&&head==HoleHead.Countersink);
        if(verified)Assert.Equal(3,pattern.TopologyHistory!.SchemaVersion);else Assert.Null(pattern.TopologyHistory);
        after.Validate();await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
        var path=files.PathFor("holes.cadoryx");var storage=new CadDocumentStorage();await f.Session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,f.Assets))
        {
            loaded.Snapshot.Validate();var loadedPattern=(HolePatternRecipe)loaded.Snapshot.Features[pattern.Id].Recipe;
            Assert.True(layout.CopyIds.SequenceEqual(loadedPattern.Layout.CopyIds));Assert.Equal(recipe.Template,loadedPattern.Template);
            using var result=await f.Kernel.EvaluateAsync(loadedPattern,f.Assets);Assert.Equal(body.Geometry.VolumeMm3,result.Geometry.VolumeMm3,5);
            if(!verified){Assert.Null(result.TopologyHistory);Assert.Contains(result.Diagnostics,d=>d.Code=="HISTORY.UNSUPPORTED");}
        }
        Assert.Contains("cadoryx.hole-replay.1",FormatEvolutionTests.Manifest(path).RequiredCapabilities);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=28}:s).ToImmutableArray()});
        int count=f.Assets.Count;await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
    }

    [Fact] public async Task SeedAndLayoutDriversRebuildSuccessorDrawingSurvivingCopiesSuppressionAndIndependentPart()
    {
        await using var f=new Fixture();await f.Initialize();
        var axis=new CadDatumAxis(Guid.NewGuid(),f.Seed.PartId,"Layout axis",DatumAxisKind.Independent,Direction:Vector3d.UnitZ);
        await f.Session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Axes=DatumBook.Empty.Axes.Add(axis.Id,axis)}));axis=f.Session.Snapshot.Datums.Axes[axis.Id];
        var layout=f.Layout(RepetitionKind.Linear);layout=layout with{Frame=axis.Frame,Datum=axis.Id,DatumRevision=axis.Revision};
        await f.Session.ExecuteAsync(new AddHolePatternCommand(f.Seed.OutputBodyId,new(f.Seed.Id,(HoleRecipe)f.Seed.Recipe,layout),"Holes"));
        var pattern=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is HolePatternRecipe);
        var sheet=TechnicalDrawingSheet.A4Landscape("Repeated holes");await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,100),1));
        var count=new CadParameter(Guid.NewGuid(),"count",ParameterDimension.Scalar,"4");var spacing=new CadParameter(Guid.NewGuid(),"spacing",ParameterDimension.Length,"6 mm");
        var diameter=new CadParameter(Guid.NewGuid(),"diameter",ParameterDimension.Length,"4 mm");
        await f.Session.ExecuteAsync(new SetParametersCommand(new([count,spacing,diameter],
            [new(Guid.NewGuid(),count.Id,ParameterTarget.Feature(pattern.Id,"Count")),new(Guid.NewGuid(),spacing.Id,ParameterTarget.Feature(pattern.Id,"Spacing")),
                new(Guid.NewGuid(),diameter.Id,ParameterTarget.Feature(f.Seed.Id,"Diameter"))])));
        var changed=f.Session.Snapshot;var current=(HolePatternRecipe)changed.Features[pattern.Id].Recipe;
        Assert.Equal(4500-4*20*Math.PI,changed.Features[pattern.Id].Result.VolumeMm3,5);Assert.Equal(4,current.Layout.Count);
        Assert.True(layout.CopyIds.SequenceEqual(current.Layout.CopyIds.Take(3)));
        Assert.All(changed.DrawingSheets[sheet.Id].Views,v=>{Assert.Null(v.StaleReason);Assert.Equal(changed.Features[pattern.Id].Result.Revision,v.Sources.Single().Revision);});
        var slot=ComponentSlotId.New();var root=new OccurrencePath(changed.Id,[]);
        await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,f.Seed.PartId,slot,"Second",RigidTransform3d.Translate(50,0,0)));
        var command=AssemblyOccurrenceCommands.MakePartIndependent(root.Append(slot));await f.Session.ExecuteAsync(command);
        var copied=f.Session.Snapshot.Features.Values.Single(p=>p.PartId==command.ResultPartId&&p.Recipe is HolePatternRecipe);var copiedRecipe=(HolePatternRecipe)copied.Recipe;
        Assert.Equal(copied.PartId,f.Session.Snapshot.Features[copiedRecipe.Seed].PartId);Assert.NotEqual(current.Layout.CopyIds,copiedRecipe.Layout.CopyIds);
        Assert.Equal(copied.PartId,f.Session.Snapshot.Datums.Axes[copiedRecipe.Layout.Datum!.Value].PartId);f.Session.Snapshot.Validate();
        await f.Session.ExecuteAsync(new SetFeatureSuppressionCommand(f.Seed.Id,true));Assert.True(f.Session.Snapshot.Features[pattern.Id].IsStale);
        Assert.False(f.Session.Snapshot.Features[copied.Id].IsStale);await f.Session.ExecuteAsync(new SetFeatureSuppressionCommand(f.Seed.Id,false));
        Assert.False(f.Session.Snapshot.Features[pattern.Id].IsStale);Assert.Equal(4500-4*20*Math.PI,f.Session.Snapshot.Features[pattern.Id].Result.VolumeMm3,5);
    }

    [Fact] public async Task OutsideDuplicateForeignTemplateInvalidCountsAndCancellationAreAtomic()
    {
        await using var f=new Fixture();await f.Initialize();var before=f.Session.Snapshot;int count=f.Assets.Count;
        var layout=f.Layout(RepetitionKind.Linear);var recipe=new HolePatternRecipe(f.Seed.Id,(HoleRecipe)f.Seed.Recipe,layout);
        foreach(var invalid in new[]{recipe with{Layout=layout with{Step=50}},recipe with{Layout=layout with{Step=.000001}},
            recipe with{Template=recipe.Template with{Diameter=3}},recipe with{Seed=FeatureId.New()},recipe with{Layout=layout with{Count=0}},
            recipe with{Layout=layout with{Output=RepetitionOutput.Compound}}})
        {
            await Assert.ThrowsAnyAsync<Exception>(()=>f.Session.ExecuteAsync(new AddHolePatternCommand(f.Seed.OutputBodyId,invalid,"Invalid")));
            Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
        }
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Session.ExecuteAsync(new AddHolePatternCommand(f.Seed.OutputBodyId,recipe,"Cancelled"),cancel.Token));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
    }

    [Theory][InlineData("hidden")][InlineData("seed")][InlineData("template")][InlineData("foreign-kind")]
    public async Task ForgedStoredReplayIsRejectedWithoutRetainingAssets(string mutation)
    {
        using var files=new TestFiles();await using var f=new Fixture();await f.Initialize();
        await f.Session.ExecuteAsync(new AddHolePatternCommand(f.Seed.OutputBodyId,new(f.Seed.Id,(HoleRecipe)f.Seed.Recipe,f.Layout(RepetitionKind.Linear)),"Holes"));
        var storage=new CadDocumentStorage();var path=files.PathFor("invalid.cadoryx");await f.Session.SaveAsync(storage,path);int count=f.Assets.Count;
        FormatEvolutionTests.RewriteSection(path,"features",bytes=>
        {
            var pack=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes,MessagePackSerializerOptions.Standard);
            return MessagePackSerializer.Serialize(pack with{Features=pack.Features.Select(feature=>
            {
                if(feature.Recipe.HolePattern is not {} p)return feature;var r=feature.Recipe;
                r=mutation switch
                {
                    "hidden"=>r with{Repetition=p.Layout},"seed"=>r with{HolePattern=p with{Seed=Guid.NewGuid()}},
                    "template"=>r with{HolePattern=p with{Hole=p.Hole with{Diameter=4}}},_=>r with{Kind="box",Sources=[],Numbers=[1,2,3]}
                };
                return feature with{Recipe=r};
            }).ToArray()},MessagePackSerializerOptions.Standard);
        });
        await Assert.ThrowsAnyAsync<Exception>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
    }

    [Fact] public async Task SuppressedAuthorDimensionsRemainDrivenAndRestoreAfterSaveReopen()
    {
        using var files=new TestFiles();await using var f=new Fixture();await f.Initialize();
        await f.Session.ExecuteAsync(new AddHolePatternCommand(f.Seed.OutputBodyId,new(f.Seed.Id,(HoleRecipe)f.Seed.Recipe,f.Layout(RepetitionKind.Linear)),"Holes"));
        var pattern=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is HolePatternRecipe);
        var count=new CadParameter(Guid.NewGuid(),"count",ParameterDimension.Scalar,"3");
        var spacing=new CadParameter(Guid.NewGuid(),"spacing",ParameterDimension.Length,"8 mm");
        var diameter=new CadParameter(Guid.NewGuid(),"diameter",ParameterDimension.Length,"2 mm");
        var book=new ParameterBook([count,spacing,diameter],[new(Guid.NewGuid(),count.Id,ParameterTarget.Feature(pattern.Id,"Count")),
            new(Guid.NewGuid(),spacing.Id,ParameterTarget.Feature(pattern.Id,"Spacing")),new(Guid.NewGuid(),diameter.Id,ParameterTarget.Feature(f.Seed.Id,"Diameter"))]);
        await f.Session.ExecuteAsync(new SetParametersCommand(book));await f.Session.ExecuteAsync(new SetFeatureSuppressionCommand(f.Seed.Id,true));
        var frozen=f.Session.Snapshot.Features[pattern.Id].Result;var suppressed=f.Session.Snapshot;
        await f.Session.ExecuteAsync(new SetParametersCommand(book with{Parameters=[count with{Expression="4"},spacing with{Expression="6 mm"},diameter with{Expression="3 mm"}]}));
        var staged=f.Session.Snapshot;staged.Validate();Assert.True(staged.Features[pattern.Id].IsStale);Assert.Equal(frozen,staged.Features[pattern.Id].Result);
        Assert.Equal(4,((HolePatternRecipe)staged.Features[pattern.Id].Recipe).Layout.Count);Assert.Equal(3,((HoleRecipe)staged.Features[f.Seed.Id].Recipe).Diameter);
        Assert.DoesNotContain(ParameterTargets.All(staged),p=>p.Owner==pattern.Id.Value||p.Owner==f.Seed.Id.Value);
        await f.Session.UndoAsync();Assert.Same(suppressed,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(staged,f.Session.Snapshot);
        var storage=new CadDocumentStorage();var path=files.PathFor("suppressed.cadoryx");await f.Session.SaveAsync(storage,path);
        using var loaded=await storage.LoadAsync(path,f.Assets);await using var reopened=new CadDocumentSession(loaded.Snapshot,f.Assets,f.Kernel,new InlineSessionDispatcher());
        await reopened.ExecuteAsync(new SetFeatureSuppressionCommand(f.Seed.Id,false));var restored=reopened.Snapshot;restored.Validate();
        Assert.False(restored.Features[pattern.Id].IsStale);Assert.Equal(4500-4*5*Math.PI*1.5*1.5,restored.Features[pattern.Id].Result.VolumeMm3,5);
        Assert.True(((HolePatternRecipe)staged.Features[pattern.Id].Recipe).Layout.CopyIds.SequenceEqual(((HolePatternRecipe)restored.Features[pattern.Id].Recipe).Layout.CopyIds));
    }

    private sealed class Fixture:IAsyncDisposable
    {
        public MemoryAssetStore Assets {get;}=new();public OcctGeometryKernel Kernel {get;}=new();
        public CadDocumentSession Session {get;}public FeatureDefinition Seed {get;private set;}=null!;
        public Fixture()=>Session=new(DocumentSnapshot.Create("Hole replay"),Assets,Kernel,new InlineSessionDispatcher());
        public async Task Initialize(HoleHead head=HoleHead.Plain)
        {
            await Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(30,30,5,RigidTransform3d.Identity),"Plate"));var box=Session.Snapshot.Features.Values.Single();
            var recipe=new HoleRecipe(box.Result,RigidTransform3d.Translate(0,0,5),new(5,5),2,Head:head,
                HeadDiameter:head==HoleHead.Plain?0:4,HeadDepth:head==HoleHead.Counterbore?1:0,HeadAngle:head==HoleHead.Countersink?Math.PI/2:0);
            await Session.ExecuteAsync(new AddHoleCommand(box.OutputBodyId,recipe,"Seed"));Seed=Session.Snapshot.Features.Values.Single(f=>f.Recipe is HoleRecipe);
        }
        public RepetitionRecipe Layout(RepetitionKind kind,bool include=true)
        {
            var frame=kind==RepetitionKind.Mirror?new RigidTransform3d(new(15,0,0),Quaterniond.FromEulerDegrees(0,90,0)):
                kind==RepetitionKind.Circular?RigidTransform3d.Translate(10,10,5):RigidTransform3d.Identity;
            int count=kind==RepetitionKind.Mirror?(include?2:1):3;
            return new(Seed.Result,kind,frame,count,kind==RepetitionKind.Mirror?0:kind==RepetitionKind.Circular?0:8,
                Enumerable.Range(0,count).Select(_=>Guid.NewGuid()).ToImmutableArray(),RepetitionOutput.Fused,include,kind==RepetitionKind.Circular);
        }
        public async ValueTask DisposeAsync(){await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
}
