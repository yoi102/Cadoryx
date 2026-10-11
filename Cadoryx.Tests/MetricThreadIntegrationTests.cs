using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using MessagePack;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class MetricThreadIntegrationTests
{
    [Theory][InlineData(3,true,ThreadHand.Right)][InlineData(4,false,ThreadHand.Left)]
    [InlineData(5,true,ThreadHand.Left)][InlineData(6,false,ThreadHand.Right)]
    [InlineData(8,true,ThreadHand.Right)][InlineData(10,false,ThreadHand.Left)]
    [InlineData(12,true,ThreadHand.Left)][InlineData(16,false,ThreadHand.Right)]
    public async Task PilotVolumeNativeProofAndStorageRetainDistinctThreadIntent(int nominal,bool through,ThreadHand hand)
    {
        await using var f=await Fixture.Create(nominal,through,hand,true);var feature=f.Hole;
        double pilot=MetricThreadHoles.Size(nominal).PilotDiameterMm;
        Assert.Equal(40*40*20-Math.PI*pilot*pilot/4*(through?20:8),feature.Result.VolumeMm3,5);
        var evidence=await f.Evidence();Assert.Equal(((HoleRecipe)feature.Recipe).Thread,evidence.Thread);
        Assert.Equal(DrawingHoleSpecificationSource.CurrentAuthoredHole,evidence.SpecificationSource);
        Assert.Equal(pilot,evidence.DiameterMm,6);Assert.Equal(through?20:8,evidence.DepthMm,6);
        Assert.Equal(through?0:6,evidence.Thread!.DepthMm);Assert.NotEqual(nominal,evidence.DiameterMm);
        using var files=new TestFiles();string path=files.PathFor("thread.cadoryx");
        await f.Session.SaveAsync(new CadDocumentStorage(holeResolver:f.Kernel),path);
        var manifest=FormatEvolutionTests.Manifest(path);Assert.Contains("cadoryx.thread-hole-intent.1",manifest.RequiredCapabilities);
        Assert.Equal(60,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);
        Assert.Equal(43,manifest.Sections.Single(s=>s.Kind=="features").SchemaVersion);
        Assert.Equal(14,manifest.Sections.Single(s=>s.Kind=="drawings").SchemaVersion);
        using var loaded=await new CadDocumentStorage(holeResolver:f.Kernel).LoadAsync(path,f.Assets);
        Assert.Equal(feature.Recipe,loaded.Snapshot.Features[feature.Id].Recipe);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task FinalPilotAndThreadDepthValidateAtomicallyInEitherBindingOrder(bool reverse)
    {
        await using var f=await Fixture.Create(8,false);var hole=f.Hole;
        var drill=new CadParameter(Guid.NewGuid(),"drilled",ParameterDimension.Length,"12 mm");
        var thread=new CadParameter(Guid.NewGuid(),"threaded",ParameterDimension.Length,"10 mm");
        ParameterBinding[] bindings=[new(Guid.NewGuid(),drill.Id,ParameterTarget.Feature(hole.Id,"Depth")),
            new(Guid.NewGuid(),thread.Id,ParameterTarget.Feature(hole.Id,"ThreadDepth"))];
        var book=new ParameterBook([drill,thread],[..reverse?bindings.Reverse():bindings]);
        Assert.Contains(ParameterTarget.Feature(hole.Id,"ThreadDepth"),ParameterTargets.All(f.Session.Snapshot));
        await f.Session.ExecuteAsync(new SetParametersCommand(book));var after=f.Session.Snapshot;
        Assert.Equal(12,((HoleRecipe)after.Features[hole.Id].Recipe).Depth);Assert.Equal(10,((HoleRecipe)after.Features[hole.Id].Recipe).Thread!.DepthMm);
        int held=f.Assets.Count;await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new SetParametersCommand(book with{Parameters=[drill,thread with{Expression="13 mm"}]})));
        Assert.Same(after,f.Session.Snapshot);Assert.Equal(held,f.Assets.Count);
        var diameter=new CadParameter(Guid.NewGuid(),"pilot",ParameterDimension.Length,"8 mm");
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new SetParametersCommand(new([diameter],[new(Guid.NewGuid(),diameter.Id,ParameterTarget.Feature(hole.Id,"Diameter"))]))));
        Assert.Same(after,f.Session.Snapshot);await f.Session.UndoAsync();Assert.Equal(6,((HoleRecipe)f.Hole.Recipe).Thread!.DepthMm);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }

    [Theory][InlineData("features")][InlineData("drawings")]
    public async Task PreviousSectionsCannotSmuggleThreadRecipeOrManufacturingProof(string section)
    {
        using var files=new TestFiles();await using var f=await Fixture.Create(6,false);await f.AddTable();
        string path=files.PathFor("legacy.cadoryx");var storage=new CadDocumentStorage(holeResolver:f.Kernel);
        await f.Session.SaveAsync(storage,path);int held=f.Assets.Count;
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind==section?s with{SchemaVersion=section=="features"?39:12}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(held,f.Assets.Count);
    }

    [Fact] public async Task LegacyOrdinaryHoleMigratesWithoutInventingThreadIntent()
    {
        using var files=new TestFiles();await using var f=await Fixture.Create(6,false);
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Hole.Id,((HoleRecipe)f.Hole.Recipe) with{Thread=null}));await f.AddTable();
        string path=files.PathFor("old.cadoryx");var storage=new CadDocumentStorage(holeResolver:f.Kernel);await f.Session.SaveAsync(storage,path);
        // Emulate the actual legacy four-field point payload before declaring its document version.
        SketchLegacyPointFixture.Prepare(path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind switch
        {"document"=>s with{SchemaVersion=46},"features"=>s with{SchemaVersion=39},"drawings"=>s with{SchemaVersion=12},_=>s}).ToImmutableArray()});
        using var loaded=await storage.LoadAsync(path,f.Assets);loaded.Snapshot.Validate();
        Assert.Null(((HoleRecipe)loaded.Snapshot.Features[f.Hole.Id].Recipe).Thread);Assert.Null(loaded.Snapshot.DrawingSheets[f.Sheet].HoleTables[0].Rows[0].Evidence.Thread);
    }

    [Theory][InlineData("catalog")][InlineData("pitch")][InlineData("hand")][InlineData("depth")][InlineData("modeled")][InlineData("clearance")]
    public async Task ForgedCurrentThreadPayloadRejectsBeforeRetainingAssets(string field)
    {
        using var files=new TestFiles();await using var f=await Fixture.Create(8,false);var storage=new CadDocumentStorage();string path=files.PathFor("forged.cadoryx");
        await f.Session.SaveAsync(storage,path);int held=f.Assets.Count;
        FormatEvolutionTests.RewriteSection(path,"features",bytes=>
        {
            var pack=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);return MessagePackSerializer.Serialize(pack with{Features=pack.Features.Select(p=>
            {
                if(p.Recipe.Hole is not {} h)return p;var t=h.Thread!;
                t=field switch{"catalog"=>t with{Catalog="unknown"},"pitch"=>t with{PitchMm=1},"hand"=>t with{Hand=77},
                    "depth"=>t with{DepthMm=9},"modeled"=>t with{Representation=1},_=>t};
                return p with{Recipe=p.Recipe with{Hole=h with{Thread=t,NominalMm=field=="clearance"?8:null,Fit=field=="clearance"?1:null,Catalog=field=="clearance"?MetricClearanceHoles.Catalog:null}}};
            }).ToArray()});
        });
        await Assert.ThrowsAnyAsync<Exception>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(held,f.Assets.Count);
    }

    [Fact] public async Task HoleTableReprovesThreadOnlyEditAndRejectsForgedSameGeometryIntent()
    {
        using var files=new TestFiles();await using var f=await Fixture.Create(8,false);await f.AddTable();var before=f.Session.Snapshot;
        var row=before.DrawingSheets[f.Sheet].HoleTables[0].Rows[0];var forged=row.Evidence with{Thread=row.Evidence.Thread! with{Hand=ThreadHand.Left}};
        Assert.False(row.Evidence.SameProof(forged));
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,
            before.DrawingSheets[f.Sheet].HoleTables[0] with{Rows=[row with{Evidence=forged}]},true)));
        Assert.Same(before,f.Session.Snapshot);
        var recipe=(HoleRecipe)f.Hole.Recipe;await f.Session.ExecuteAsync(new RecomputeCommand(f.Hole.Id,recipe with{Thread=recipe.Thread! with{Hand=ThreadHand.Left,DepthMm=5}}));
        var after=f.Session.Snapshot;var current=after.DrawingSheets[f.Sheet].HoleTables[0].Rows[0];Assert.True(current.StaleReason is null,current.StaleReason);
        Assert.Equal(ThreadHand.Left,current.Evidence.Thread!.Hand);Assert.Equal(5,current.Evidence.Thread.DepthMm);
        Assert.Equal(before.Features[f.Hole.Id].Result.VolumeMm3,f.Hole.Result.VolumeMm3,6);
        var labels=DrawingPageLayout.Create(after.DrawingSheets[f.Sheet],1,1).Labels;
        Assert.Contains(labels,l=>l.Text.Contains("M8 x 1.25 LH")&&l.Text.Contains("L 5")&&l.Text.Contains("symbolic"));
        string path=files.PathFor("table.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(holeResolver:f.Kernel),path);
        using var loaded=await new CadDocumentStorage(holeResolver:f.Kernel).LoadAsync(path,f.Assets);
        Assert.True(current.Evidence.SameProof(loaded.Snapshot.DrawingSheets[f.Sheet].HoleTables[0].Rows[0].Evidence));
        TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("thread.pdf"));
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }

    [Fact] public async Task ReapplyingDrawingTemplateKeepsFullyIdenticalOriginalProof()
    {
        await using var f=await Fixture.Create(6,false);await f.AddTable();
        var before=f.Session.Snapshot.DrawingSheets[f.Sheet].HoleTables[0];var proof=before.Rows[0].Evidence;
        var template=Guid.NewGuid();await f.Session.ExecuteAsync(DrawingTemplateCommands.Save(f.Sheet,template,"Thread drawing"));
        await f.Session.ExecuteAsync(DrawingTemplateCommands.Apply(f.Sheet,template));
        var after=f.Session.Snapshot.DrawingSheets[f.Sheet].HoleTables[0];Assert.Equal(before,after);
        Assert.Same(proof,after.Rows[0].Evidence);
        Assert.True(proof.SameProof(await f.Evidence()));
    }

    [Fact] public async Task ExchangeWarnsOfSymbolicLossAndImportedPilotNeverInfersThread()
    {
        using var files=new TestFiles();await using var f=await Fixture.Create(6,true);
        foreach(string extension in new[]{"step","iges","stl"})
        {
            var report=await f.Kernel.ExportAsync(f.Session.Snapshot,f.Assets,files.PathFor("thread."+extension),new CadExportOptions());
            Assert.Contains(report.Diagnostics,d=>d.Code=="EXPORT.THREAD_INTENT");
        }
        using var loaded=await f.Kernel.ImportAsync(files.PathFor("thread.step"),f.Assets);
        Assert.All(loaded.Snapshot.Features.Values,p=>Assert.IsNotType<HoleRecipe>(p.Recipe));
        var body=loaded.Snapshot.Bodies.Values.Single();using var shape=OcctGeometryBridge.ReadShape(body.Geometry,f.Assets);using var map=RepairSnapshot.Create(shape);
        var wall=map.Topology.Single(t=>{if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);return face.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder;});
        var source=await f.Kernel.ResolveDrawingTopologyAsync(loaded.Snapshot,loaded.Snapshot.EnumerateOccurrences().Single(o=>o.DefinitionId==body.PartId).Path,body.Id,wall.Selection.Index,map.Fingerprint,f.Assets);
        var evidence=await f.Kernel.ResolveDrawingHoleAsync(loaded.Snapshot,source,f.Assets);
        Assert.Null(evidence.Thread);Assert.Equal(DrawingHoleSpecificationSource.CurrentGeometry,evidence.SpecificationSource);
        Assert.All(ExchangeLossReport.Create(f.Session.Snapshot,new()).Formats,r=>Assert.Contains(r.Lost,l=>l.Contains("Thread designation")));
    }

    [Fact] public async Task PatternRebuildAndIndependentAuthorGraphRetainThreadWithoutClaimingAuthoredPatternProof()
    {
        await using var f=await Fixture.Create(6,true);var seed=f.Hole;var recipe=(HoleRecipe)seed.Recipe;
        var layout=new RepetitionRecipe(seed.Result,RepetitionKind.Linear,RigidTransform3d.Identity,2,12,[Guid.NewGuid(),Guid.NewGuid()],RepetitionOutput.Fused);
        await f.Session.ExecuteAsync(new AddHolePatternCommand(seed.OutputBodyId,new(seed.Id,recipe,layout),"Two threads"));
        var pattern=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is HolePatternRecipe);
        Assert.Equal(32000-2*20*Math.PI*2.5*2.5,pattern.Result.VolumeMm3,5);
        var changed=recipe with{Thread=recipe.Thread! with{Hand=ThreadHand.Left}};await f.Session.ExecuteAsync(new RecomputeCommand(seed.Id,changed));
        Assert.Equal(seed.Result,f.Hole.Result);Assert.Equal(pattern.Result,f.Session.Snapshot.Features[pattern.Id].Result);
        Assert.Equal(changed.Thread,((HolePatternRecipe)f.Session.Snapshot.Features[pattern.Id].Recipe).Template.Thread);
        var root=new OccurrencePath(f.Session.Snapshot.Id,[]);var slot=ComponentSlotId.New();
        await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,seed.PartId,slot,"Copy",RigidTransform3d.Translate(60,0,0)));
        await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.MakePartIndependent(root.Append(slot)));
        var independent=f.Session.Snapshot.Features.Values.Single(p=>p.PartId!=seed.PartId&&p.Recipe is HoleRecipe);
        Assert.Equal(changed.Thread,((HoleRecipe)independent.Recipe).Thread);
        await f.Session.ExecuteAsync(new RecomputeCommand(seed.Id,recipe));Assert.Equal(ThreadHand.Left,((HoleRecipe)f.Session.Snapshot.Features[independent.Id].Recipe).Thread!.Hand);
        f.Session.Snapshot.Validate();
    }

    [Fact] public async Task ThreadDepthDriverKeepsExactGeometryAndUpdatesCurrentTableInOneUndoEntry()
    {
        await using var f=await Fixture.Create(8,false);await f.AddTable();var before=f.Session.Snapshot;var hole=f.Hole;
        var p=new CadParameter(Guid.NewGuid(),"threadDepth",ParameterDimension.Length,"5 mm");
        await f.Session.ExecuteAsync(new SetParametersCommand(new([p],[new(Guid.NewGuid(),p.Id,ParameterTarget.Feature(hole.Id,"ThreadDepth"))])));
        var after=f.Session.Snapshot;Assert.Equal(hole.Result,f.Hole.Result);
        var row=after.DrawingSheets[f.Sheet].HoleTables[0].Rows[0];Assert.Null(row.StaleReason);Assert.Equal(5,row.Evidence.Thread!.DepthMm);
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Session.ExecuteAsync(new RecomputeCommand(hole.Id,((HoleRecipe)f.Hole.Recipe) with{Thread=null})));
        Assert.Same(after,f.Session.Snapshot);await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }

    [Fact] public async Task EditorPreviewCancelEditAndExplicitDetachPreserveAuthorAndPilot()
    {
        await using var f=await Fixture.Create(8,false);var document=new CadDocumentViewModel(f.Session,f.Kernel,new CadMessageLog());
        try
        {
            var before=f.Session.Snapshot;
            await using(var editor=new HoleEditorViewModel(document,f.Kernel,f.Hole.Id))
            {
                Assert.True(editor.UseThread);Assert.Equal(8,editor.ThreadNominal);Assert.False(editor.CanSetDiameter);Assert.False(editor.CanUseStandard);
                editor.ThreadHand=ThreadHand.Left;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                Assert.Same(before,f.Session.Snapshot);editor.CancelCommand.Execute(null);Assert.Null(document.PreviewScene);Assert.Same(before,f.Session.Snapshot);
            }
            await using(var editor=new HoleEditorViewModel(document,f.Kernel,f.Hole.Id))
            {
                editor.ThreadDepth=9;await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);
                editor.ThreadDepth=5;editor.ThreadHand=ThreadHand.Left;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                await editor.ConfirmCommand.ExecuteAsync(null);Assert.Equal(5,((HoleRecipe)f.Hole.Recipe).Thread!.DepthMm);
            }
            await using(var editor=new HoleEditorViewModel(document,f.Kernel,f.Hole.Id))
            {
                editor.UseThread=false;Assert.Equal(6.8,editor.Diameter);Assert.True(editor.CanSetDiameter);Assert.True(editor.CanUseStandard);
                editor.Diameter=7;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
                Assert.Null(((HoleRecipe)f.Hole.Recipe).Thread);Assert.Equal(7,((HoleRecipe)f.Hole.Recipe).Diameter);
            }
        }
        finally{document.Detach();}
    }

    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets {get;}=new();internal OcctGeometryKernel Kernel {get;}=new();internal CadDocumentSession Session {get;}
        internal FeatureDefinition Hole=>Session.Snapshot.Features.Values.Single(p=>p.Recipe is HoleRecipe);
        internal Guid Sheet {get;private set;}internal Guid View {get;private set;}
        private Fixture()=>Session=new(DocumentSnapshot.Create("Metric threads"),Assets,Kernel,new InlineSessionDispatcher());
        internal static async Task<Fixture> Create(int nominal,bool through,ThreadHand hand=ThreadHand.Right,bool rotated=false)
        {
            var f=new Fixture();var placement=rotated?new RigidTransform3d(new(100,200,300),Quaterniond.FromEulerDegrees(13,37,-21)):RigidTransform3d.Identity;
            await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(40,40,20,placement),"Block"));var body=f.Session.Snapshot.Bodies.Values.Single();
            await f.Session.ExecuteAsync(new AddHoleCommand(body.Id,new(body.Geometry,placement*RigidTransform3d.Translate(0,0,20),new(10,10),
                MetricThreadHoles.Size(nominal).PilotDiameterMm,through,through?0:8,Thread:MetricThreadHoles.Create(nominal,hand,through,through?0:6)),"Thread"));return f;
        }
        internal async Task<DrawingHoleEvidence> Evidence()
        {
            var doc=Session.Snapshot;var body=doc.Bodies.Values.Single();using var shape=OcctGeometryBridge.ReadShape(body.Geometry,Assets);using var map=RepairSnapshot.Create(shape);
            var wall=map.Topology.Single(t=>{if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);return face.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder;});
            var source=await Kernel.ResolveDrawingTopologyAsync(doc,doc.EnumerateOccurrences().Single().Path,body.Id,wall.Selection.Index,map.Fingerprint,Assets);
            return await Kernel.ResolveDrawingHoleAsync(doc,source,Assets);
        }
        internal async Task AddTable()
        {
            var sheet=DrawingSheetTemplate.Create("Thread holes");Sheet=sheet.Id;View=Guid.NewGuid();
            await Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));await Session.ExecuteAsync(TechnicalDrawingCommands.AddView(Sheet,View,"Top",DrawingViewKind.Top,new(90,60),1));
            var evidence=await Evidence();await Session.ExecuteAsync(DrawingHoleTableCommands.Put(Sheet,new(Guid.NewGuid(),View,"Threads",evidence.Source.Path,
                evidence.Source.DefinitionId,RigidTransform3d.Translate(0,0,20),new(25,90)){Rows=[new(Guid.NewGuid(),1,evidence,new(0,0))]}));
        }
        public async ValueTask DisposeAsync(){await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
}
