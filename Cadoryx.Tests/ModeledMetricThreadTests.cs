using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ModeledMetricThreadTests
{
    [Theory][InlineData(3,true,ThreadHand.Left)][InlineData(4,false,ThreadHand.Right)]
    [InlineData(5,true,ThreadHand.Right)][InlineData(6,false,ThreadHand.Left)]
    [InlineData(8,true,ThreadHand.Left)][InlineData(10,false,ThreadHand.Right)]
    [InlineData(12,true,ThreadHand.Right)][InlineData(16,false,ThreadHand.Left)]
    public async Task CompleteToothSolidVolumeAndNativeReplaySurviveStorage(int nominal,bool through,ThreadHand hand)
    {
        await using var f=await Fixture.Create(nominal,through,hand);
        double pilot=f.Size.PilotDiameterMm,pitch=f.Size.PitchMm,height=8*pitch,length=through?height:4.25*pitch;
        double expected=40*40*height-Math.PI*pilot*pilot/4*(through?height:6*pitch)-
            GrooveVolume(nominal,pilot,pitch,length);
        Assert.InRange(Math.Abs(f.Hole.Result.VolumeMm3-expected),0,.002);
        using(var shape=OcctGeometryBridge.ReadShape(f.Hole.Result,f.Assets))
        {
            Assert.True(shape.IsValid);Assert.True(shape.GetSelfInterferenceReport().IsClear);
            Assert.Equal(1,shape.GetTopologySummary().UniqueCounts.SolidCount);
            Assert.True(shape.FaceCount>7);
        }
        Assert.Equal(MetricThreadHoles.ModeledProfile,f.Recipe.Thread!.Profile);
        using var files=new TestFiles();var storage=new CadDocumentStorage(threadVerifier:f.Kernel);
        string path=files.PathFor("modeled.cadoryx");await f.Session.SaveAsync(storage,path);
        var manifest=FormatEvolutionTests.Manifest(path);
        Assert.Contains("cadoryx.metric-thread-solid.1",manifest.RequiredCapabilities);
        Assert.Equal(60,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);
        Assert.Equal(43,manifest.Sections.Single(s=>s.Kind=="features").SchemaVersion);
        using var loaded=await storage.LoadAsync(path,f.Assets);
        Assert.Equal(f.Hole.Result,loaded.Snapshot.Features[f.Hole.Id].Result);
        Assert.Equal(f.Recipe,loaded.Snapshot.Features[f.Hole.Id].Recipe);
    }

    [Fact] public async Task ModeledHandDepthAndParameterEditsRebuildWhileSymbolicEditsKeepIdentity()
    {
        await using var f=await Fixture.Create();var before=f.Session.Snapshot;var result=f.Hole.Result;
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Hole.Id,f.Recipe with{Thread=f.Recipe.Thread! with{Hand=ThreadHand.Left}}));
        Assert.NotEqual(result.AssetId,f.Hole.Result.AssetId);Assert.Equal(result.VolumeMm3,f.Hole.Result.VolumeMm3,4);
        var parameter=new CadParameter(Guid.NewGuid(),"threadDepth",ParameterDimension.Length,"3 mm");
        await f.Session.ExecuteAsync(new SetParametersCommand(new([parameter],[new(Guid.NewGuid(),parameter.Id,ParameterTarget.Feature(f.Hole.Id,"ThreadDepth"))])));
        Assert.Equal(3,f.Recipe.Thread!.DepthMm);Assert.True(f.Hole.Result.VolumeMm3>result.VolumeMm3);
        var current=f.Session.Snapshot;int held=f.Assets.Count;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new SetParametersCommand(current.Parameters with{Parameters=[parameter with{Expression="0.5 mm"}]})));
        Assert.Same(current,f.Session.Snapshot);Assert.Equal(held,f.Assets.Count);
        await f.Session.UndoAsync();await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
        await f.Session.RedoAsync();await f.Session.RedoAsync();Assert.Same(current,f.Session.Snapshot);
        await f.Session.ExecuteAsync(new SetParametersCommand(ParameterBook.Empty));
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Hole.Id,f.Recipe with{Thread=f.Recipe.Thread! with{Representation=ThreadHoleRepresentation.Symbolic,Profile=null}}));
        var symbolic=f.Hole.Result;Assert.True(symbolic.VolumeMm3>result.VolumeMm3);
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Hole.Id,f.Recipe with{Thread=f.Recipe.Thread! with{Hand=ThreadHand.Right}}));
        Assert.Equal(symbolic,f.Hole.Result);
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Hole.Id,f.Recipe with{Thread=null}));Assert.Equal(symbolic,f.Hole.Result);
    }

    [Fact] public async Task ModeledStorageRequiresVerifierAndRejectsLegacyPayloadOrWrongResult()
    {
        await using var f=await Fixture.Create();using var files=new TestFiles();string path=files.PathFor("thread.cadoryx");
        await Assert.ThrowsAsync<NotSupportedException>(()=>f.Session.SaveAsync(new CadDocumentStorage(),path));
        Assert.False(File.Exists(path));var storage=new CadDocumentStorage(threadVerifier:f.Kernel);await f.Session.SaveAsync(storage,path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=[..m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=40}:s)]});
        int held=f.Assets.Count;await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(held,f.Assets.Count);
        using var pilot=await f.Kernel.EvaluateAsync(f.Recipe with{Thread=null},f.Assets);
        var doc=f.Session.Snapshot;var hole=f.Hole;
        var forged=doc with{Features=doc.Features.SetItem(hole.Id,hole with{Result=pilot.Geometry,TopologyHistory=null}),
            Bodies=doc.Bodies.SetItem(hole.OutputBodyId,doc.Bodies[hole.OutputBodyId] with{Geometry=pilot.Geometry})};
        await Assert.ThrowsAsync<CadValidationException>(()=>storage.SaveAsync(forged,f.Assets,files.PathFor("forged.cadoryx")));
    }

    [Fact] public async Task ModeledThreadStepRetainsTeethAndUsesDedicatedManufacturingProof()
    {
        await using var f=await Fixture.Create();using var files=new TestFiles();string path=files.PathFor("thread.step");
        var report=await f.Kernel.ExportAsync(f.Session.Snapshot,f.Assets,path,new CadExportOptions());
        Assert.Contains(report.Diagnostics,d=>d.Code=="EXPORT.THREAD_INTENT"&&d.Message.Contains("modeled authoring contains tooth geometry"));
        using var loaded=await f.Kernel.ImportAsync(path,f.Assets);var imported=loaded.Snapshot.Bodies.Values.Single();
        Assert.Equal(f.Hole.Result.VolumeMm3,imported.Geometry.VolumeMm3,4);
        using var original=OcctGeometryBridge.ReadShape(f.Hole.Result,f.Assets);using var reopened=OcctGeometryBridge.ReadShape(imported.Geometry,f.Assets);
        Assert.Equal(original.FaceCount,reopened.FaceCount);
        using var map=RepairSnapshot.Create(original);
        var wall=map.Topology.First(t=>{if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);return face.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder;});
        var doc=f.Session.Snapshot;var body=doc.Bodies[f.Hole.OutputBodyId];
        var reference=await f.Kernel.ResolveDrawingTopologyAsync(doc,doc.EnumerateOccurrences().Single().Path,body.Id,wall.Selection.Index,map.Fingerprint,f.Assets);
        var proof=await f.Kernel.ResolveDrawingHoleAsync(doc,reference,f.Assets);
        Assert.Equal(f.Recipe.Thread,proof.Thread);Assert.Equal(f.Hole.Id,proof.AuthoredFeature);
        Assert.Equal(f.Recipe.Diameter,proof.DiameterMm,6);Assert.Equal(f.Recipe.Depth,proof.DepthMm,6);
        Assert.Contains(reference.FullTopologyIndex,proof.ProvenFaceIndices);
    }

    [Fact] public async Task DedicatedToothProofRejectsForgedHandAndLegacyDrawingsAndSurvivesPdfStorage()
    {
        await using var f=await Fixture.Create();using var files=new TestFiles();
        var doc=f.Session.Snapshot;var body=doc.Bodies[f.Hole.OutputBodyId];var occurrence=doc.EnumerateOccurrences().Single();
        using var shape=OcctGeometryBridge.ReadShape(body.Geometry,f.Assets);using var map=RepairSnapshot.Create(shape);
        var tooth=map.Topology.First(t=>
        {
            if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);
            return face.GetFaceSurfaceSnapshot().SurfaceType is not (SurfaceGeometryType.Plane or SurfaceGeometryType.Cylinder);
        });
        var reference=await f.Kernel.ResolveDrawingTopologyAsync(doc,occurrence.Path,body.Id,tooth.Selection.Index,map.Fingerprint,f.Assets);
        Assert.Equal(DrawingAttachmentGeometry.GeneralFace,reference.Geometry);
        var evidence=await f.Kernel.ResolveDrawingHoleAsync(doc,reference,f.Assets);
        Assert.Equal(f.Recipe.Thread,evidence.Thread);Assert.Contains(reference.FullTopologyIndex,evidence.ProvenFaceIndices);
        var sheet=DrawingSheetTemplate.Create("Threads");var view=Guid.NewGuid();
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,view,"Top",DrawingViewKind.Top,new(90,60),1));
        var table=new DrawingHoleTable(Guid.NewGuid(),view,"Threads",occurrence.Path,occurrence.DefinitionId,
            RigidTransform3d.Identity,new(20,100)){Rows=[new(Guid.NewGuid(),1,evidence,new(12,20))]};
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(sheet.Id,table));
        var before=f.Session.Snapshot;int held=f.Assets.Count;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(sheet.Id,
            table with{Rows=[table.Rows[0] with{Evidence=evidence with{Thread=evidence.Thread! with{Hand=ThreadHand.Left}}}]},replace:true)));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(held,f.Assets.Count);
        var storage=new CadDocumentStorage(threadVerifier:f.Kernel,holeResolver:f.Kernel);
        string path=files.PathFor("thread-proof.cadoryx");await f.Session.SaveAsync(storage,path);
        using(var reopened=await storage.LoadAsync(path,f.Assets))
            Assert.True(evidence.SameProof(reopened.Snapshot.DrawingSheets[sheet.Id].HoleTables.Single().Rows.Single().Evidence));
        TechnicalDrawingPdf.Write(f.Session.Snapshot,files.PathFor("thread-proof.pdf"));
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=[..m.Sections.Select(s=>s.Kind=="drawings"?s with{SchemaVersion=13}:s)]});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));
    }

    [Fact] public async Task ModeledInvalidLengthAndCancellationKeepDocumentAndAssets()
    {
        await using var f=await Fixture.Create();var before=f.Session.Snapshot;int held=f.Assets.Count;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new RecomputeCommand(f.Hole.Id,
            f.Recipe with{Thread=f.Recipe.Thread! with{DepthMm=.5}})));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Session.ExecuteAsync(new RecomputeCommand(f.Hole.Id,
            f.Recipe with{Thread=f.Recipe.Thread! with{Hand=ThreadHand.Left}}),cancel.Token));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(held,f.Assets.Count);
    }

    [Fact] public async Task ModeledHoleReplayRebuildsAllTeethAfterSeedEditAndPreservesCopyIdsAndStorage()
    {
        await using var f=await Fixture.Create();using var files=new TestFiles();var seed=f.Hole;
        var layout=new RepetitionRecipe(seed.Result,RepetitionKind.Linear,RigidTransform3d.Identity,2,12,
            [Guid.NewGuid(),Guid.NewGuid()],RepetitionOutput.Fused);
        var before=f.Session.Snapshot;
        await f.Session.ExecuteAsync(new AddHolePatternCommand(seed.OutputBodyId,new(seed.Id,f.Recipe,layout),"Threads"));
        var pattern=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is HolePatternRecipe);
        double block=1600*8;Assert.InRange(Math.Abs(pattern.Result.VolumeMm3-(block-2*(block-seed.Result.VolumeMm3))),0,.002);
        var accepted=f.Session.Snapshot;await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
        await f.Session.RedoAsync();Assert.Same(accepted,f.Session.Snapshot);
        await f.Session.ExecuteAsync(new RecomputeCommand(seed.Id,f.Recipe with{Thread=f.Recipe.Thread! with{DepthMm=3.25,Hand=ThreadHand.Left}}));
        var changed=f.Session.Snapshot;var current=changed.Features[pattern.Id];var recipe=(HolePatternRecipe)current.Recipe;
        Assert.NotEqual(pattern.Result.AssetId,current.Result.AssetId);Assert.True(current.Result.VolumeMm3>pattern.Result.VolumeMm3);
        Assert.True(layout.CopyIds.SequenceEqual(recipe.Layout.CopyIds));Assert.Equal(ThreadHand.Left,recipe.Template.Thread!.Hand);
        using var shape=OcctGeometryBridge.ReadShape(current.Result,f.Assets);
        Assert.True(shape.IsValid);Assert.True(shape.GetSelfInterferenceReport().IsClear);Assert.Equal(1,shape.GetTopologySummary().UniqueCounts.SolidCount);
        var storage=new CadDocumentStorage(threadVerifier:f.Kernel);string path=files.PathFor("thread-replay.cadoryx");
        await f.Session.SaveAsync(storage,path);using var loaded=await storage.LoadAsync(path,f.Assets);
        Assert.Equal(current.Result,loaded.Snapshot.Features[pattern.Id].Result);
        await f.Session.UndoAsync();Assert.Same(accepted,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(changed,f.Session.Snapshot);
    }

    [Fact] public async Task RotatedThroughHoleKeepsPhysicalToothLengthInsteadOfGlobalBoundingBoxOverestimate()
    {
        await using var f=await Fixture.Create(6,true,ThreadHand.Right,rotated:true);
        double expected=12800-50*Math.PI-GrooveVolume(6,5,1,8);
        Assert.InRange(Math.Abs(f.Hole.Result.VolumeMm3-expected),0,.002);
        var storage=new CadDocumentStorage(threadVerifier:f.Kernel);using var files=new TestFiles();
        string path=files.PathFor("rotated-thread.cadoryx");await f.Session.SaveAsync(storage,path);
        using var reopened=await storage.LoadAsync(path,f.Assets);Assert.Equal(f.Hole.Result,reopened.Snapshot.Features[f.Hole.Id].Result);
    }

    private sealed class Fixture: IAsyncDisposable
    {
        internal MemoryAssetStore Assets {get;}=new();internal OcctGeometryKernel Kernel {get;}=new();
        internal CadDocumentSession Session {get;private set;}=null!;
        internal MetricThreadSize Size {get;private set;}
        internal FeatureDefinition Hole=>Session.Snapshot.Features.Values.Single(f=>f.Recipe is HoleRecipe);
        internal HoleRecipe Recipe=>(HoleRecipe)Hole.Recipe;
        internal static async Task<Fixture> Create(int nominal=6,bool through=false,ThreadHand hand=ThreadHand.Right,bool rotated=false)
        {
            var f=new Fixture{Size=MetricThreadHoles.Size(nominal)};
            f.Session=new(Cadoryx.Db.DocumentSnapshot.Create("Modeled thread"),f.Assets,f.Kernel,new InlineSessionDispatcher());
            try
            {
                double pitch=f.Size.PitchMm,height=8*pitch;
                var placement=rotated?new RigidTransform3d(new(100,200,300),Quaterniond.FromEulerDegrees(13,37,-21)):RigidTransform3d.Identity;
                await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(40,40,height,placement),"Block"));
                var body=f.Session.Snapshot.Bodies.Values.Single();
                var spec=MetricThreadHoles.Create(nominal,hand,through,through?0:4.25*pitch,ThreadHoleRepresentation.Modeled);
                await f.Session.ExecuteAsync(new AddHoleCommand(body.Id,new(body.Geometry,placement*RigidTransform3d.Translate(0,0,height),new(12,20),
                    f.Size.PilotDiameterMm,through,through?0:6*pitch,Thread:spec),"Thread"));return f;
            }
            catch{await f.Session.DisposeAsync();throw;}
        }
        public async ValueTask DisposeAsync(){await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
    private static double GrooveVolume(double nominal,double pilotDiameter,double pitch,double length)
    {
        // Independent integration of 60-degree flanks and P/8 major-diameter root flat.
        double r=nominal/2,p=pilotDiameter/2;
        return Math.Tau*length/pitch*((pitch/16+r/Math.Sqrt(3))*(r*r-p*p)-2/(3*Math.Sqrt(3))*(r*r*r-p*p*p));
    }
}
