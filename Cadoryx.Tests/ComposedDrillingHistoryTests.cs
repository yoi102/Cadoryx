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

public sealed class ComposedDrillingHistoryTests
{
    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets {get;}=new();internal OcctGeometryKernel Kernel {get;}=new();
        internal CadDocumentSession Session {get;}internal FeatureDefinition Source {get;private set;}=null!;
        internal Fixture()=>Session=new(DocumentSnapshot.Create("Composed drilling"),Assets,Kernel,new InlineSessionDispatcher());
        internal async Task Init(bool imported=false)
        {
            if(imported)
            {
                using var files=new TestFiles();using var shape=ShapeFactory.CreateBox(40,30,15);
                string path=files.PathFor("source.step");ShapeExchange.WriteStep(shape,path);using var reread=ShapeExchange.ReadStep(path);
                using var stored=OcctGeometryBridge.StoreShape(reread,Assets);
                await Session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"STEP source"));
            }
            else await Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(40,30,15,RigidTransform3d.Identity),"Authored source"));
            Source=Session.Snapshot.Features.Values.Single();
        }
        internal async Task<FeatureDefinition> Hole(HoleHead head,bool through=true)
        {
            await Session.ExecuteAsync(new AddHoleCommand(Source.OutputBodyId,new(Source.Result,RigidTransform3d.Translate(0,0,15),new(5,5),2,
                through,through?0:6,head,head==HoleHead.Plain?0:4,head==HoleHead.Counterbore?2:0,head==HoleHead.Countersink?Math.PI/2:0),"Seed"));
            return Session.Snapshot.Features.Values.Single(f=>f.Recipe is HoleRecipe);
        }
        internal ExactTopologySelection[] Pick(FeatureDefinition feature)
        {
            using var shape=OcctGeometryBridge.ReadShape(feature.Result,Assets);using var map=RepairSnapshot.Create(shape);
            return map.Topology.Where(t=>t.Kind is ShapeKind.Face or ShapeKind.Edge).Select(t=>new ExactTopologySelection(Session.Snapshot.Id,
                feature.Id,feature.Result.Revision,feature.Result.AssetId,map.Fingerprint,t.Selection.Index,
                t.Kind==ShapeKind.Face?HistoryShapeKind.Face:HistoryShapeKind.Edge,OcctGeometryKernel.HistoryAdapterVersion)).ToArray();
        }
        internal async Task<DrawingHoleEvidence[]> Holes()
        {
            var document=Session.Snapshot;var body=document.Bodies.Values.Single();using var shape=OcctGeometryBridge.ReadShape(body.Geometry,Assets);
            using var map=RepairSnapshot.Create(shape);var values=new List<DrawingHoleEvidence>();
            foreach(var item in map.Topology.Where(t=>t.Kind==ShapeKind.Face))
            {
                using var face=map.CopySubshape(item.Selection);if(face.GetFaceSurfaceSnapshot().SurfaceType!=SurfaceGeometryType.Cylinder)continue;
                var reference=await Kernel.ResolveDrawingTopologyAsync(document,document.EnumerateOccurrences().Single().Path,body.Id,item.Selection.Index,map.Fingerprint,Assets);
                try{values.Add(await Kernel.ResolveDrawingHoleAsync(document,reference,Assets));}catch(CadValidationException){ }
            }
            return values.ToArray();
        }
        public async ValueTask DisposeAsync(){await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }

    [Theory][InlineData(false,false,HoleHead.Counterbore)][InlineData(false,true,HoleHead.Counterbore)]
    [InlineData(false,false,HoleHead.Countersink)][InlineData(false,true,HoleHead.Countersink)]
    [InlineData(true,false,HoleHead.Counterbore)][InlineData(true,true,HoleHead.Counterbore)]
    [InlineData(true,false,HoleHead.Countersink)][InlineData(true,true,HoleHead.Countersink)]
    public async Task OriginalStepAndAuthoredFacesAndEdgesCrossBothNativeStagesAndReopen(bool imported,bool through,HoleHead head)
    {
        await using var f=new Fixture();await f.Init(imported);using var files=new TestFiles();
        var original=f.Pick(f.Source);var before=f.Session.Snapshot;byte[] sourceBytes;
        using(var lease=f.Assets.Acquire(f.Source.Result.AssetId))sourceBytes=lease.Content.ToArray();
        var hole=await f.Hole(head,through);var after=f.Session.Snapshot;var history=Assert.IsType<TopologyHistory>(hole.TopologyHistory);
        Assert.Equal(3,history.SchemaVersion);Assert.Equal(OcctGeometryKernel.ComposedHoleHistoryAdapterVersion,history.AdapterVersion);
        Assert.Empty(history.AdditionalSources);history.ValidateFor(hole);
        var results=new List<(ExactTopologySelection Selection,HistoryResolution Result)>();
        foreach(var kind in new[]{HistoryShapeKind.Face,HistoryShapeKind.Edge})
        {
            var chosen=original.First(s=>s.Kind==kind&&TopologyHistoryReduction.Resolve(history,s.FullTopologyIndex,kind).Status==HistoryResolutionStatus.Resolved);
            int count=f.Assets.Count;var traced=await f.Kernel.TraceExactAsync(after,chosen,hole.Id,f.Assets);
            Assert.Equal(count,f.Assets.Count);Assert.Equal(HistoryResolutionStatus.Resolved,traced.Status);Assert.Equal(1,traced.CompletedSteps);
            Assert.Equal(hole.Result.AssetId,traced.Target!.Asset);results.Add((chosen,traced));
        }
        using(var lease=f.Assets.Acquire(f.Source.Result.AssetId))Assert.Equal(sourceBytes,lease.Content.ToArray());
        if(!imported)Assert.Equal(HistoryResolutionStatus.Resolved,(await f.Kernel.TraceAsync(after,
            TopologyReference.Box(after,f.Source.Id,BoxBoundary.XMin),hole.Id,f.Assets)).Status);
        var proof=Assert.Single(await f.Holes());Assert.Equal(head,proof.Head);Assert.Equal(through,proof.Through);
        Assert.Equal(2,proof.DiameterMm,6);Assert.Equal(through?15:6,proof.DepthMm,6);Assert.Equal(4,proof.HeadDiameterMm,6);
        Assert.Equal(head==HoleHead.Counterbore?2:1,proof.HeadDepthMm,6);
        string saved=files.PathFor("composed.cadoryx");var storage=new CadDocumentStorage();await f.Session.SaveAsync(storage,saved);
        Assert.Equal(4,FormatEvolutionTests.Manifest(saved).Sections.Single(s=>s.Kind=="history").SchemaVersion);
        Assert.Contains("cadoryx.composed-drilling-history.1",FormatEvolutionTests.Manifest(saved).RequiredCapabilities);
        using(var loaded=await storage.LoadAsync(saved,f.Assets))
        {
            Assert.Equal(history.Entries.ToArray(),loaded.Snapshot.Features[hole.Id].TopologyHistory!.Entries.ToArray());
            foreach(var item in results)Assert.Equal(item.Result,await f.Kernel.TraceExactAsync(loaded.Snapshot,item.Selection,hole.Id,f.Assets));
        }
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
        int held=f.Assets.Count;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new RecomputeCommand(hole.Id,((HoleRecipe)hole.Recipe) with{Through=false,Depth=100})));
        Assert.Same(after,f.Session.Snapshot);Assert.Equal(held,f.Assets.Count);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Kernel.TraceExactAsync(after,results[0].Selection,hole.Id,f.Assets,cancel.Token));
        Assert.Equal(held,f.Assets.Count);
    }

    [Theory][InlineData(HoleHead.Plain,2)][InlineData(HoleHead.Plain,4)]
    [InlineData(HoleHead.Counterbore,2)][InlineData(HoleHead.Counterbore,4)]
    [InlineData(HoleHead.Countersink,2)][InlineData(HoleHead.Countersink,4)]
    public async Task GeneratedSeedShaftCrossesIncludedPatternAndRetainsOriginalHoleNumberAndTemplate(HoleHead head,int count)
    {
        await using var f=new Fixture();await f.Init();using var files=new TestFiles();var seed=await f.Hole(head);
        var proof=Assert.Single(await f.Holes());var selection=f.Pick(seed).Single(s=>s.Kind==HistoryShapeKind.Face&&s.FullTopologyIndex==proof.Source.FullTopologyIndex);
        var sheet=DrawingSheetTemplate.Create("Seed history");var view=Guid.NewGuid();
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,view,"Top",DrawingViewKind.Top,new(90,60),1));
        var table=new DrawingHoleTable(Guid.NewGuid(),view,"Holes",proof.Source.Path,proof.Source.DefinitionId,RigidTransform3d.Translate(0,0,15),new(25,100))
            {Rows=[new(Guid.NewGuid(),7,proof,new(0,0))]};
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(sheet.Id,table));
        var layout=new RepetitionRecipe(seed.Result,RepetitionKind.Linear,RigidTransform3d.Identity,count,10,[..Enumerable.Range(0,count).Select(_=>Guid.NewGuid())],Output:RepetitionOutput.Fused);
        await f.Session.ExecuteAsync(new AddHolePatternCommand(seed.OutputBodyId,new(seed.Id,(HoleRecipe)seed.Recipe,layout),"Pattern"));
        var pattern=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is HolePatternRecipe);Assert.Equal(3,pattern.TopologyHistory!.SchemaVersion);
        var traced=await f.Kernel.TraceExactAsync(f.Session.Snapshot,selection,pattern.Id,f.Assets);Assert.Equal(HistoryResolutionStatus.Resolved,traced.Status);
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(sheet.Id,view,null));
        var current=f.Session.Snapshot.DrawingSheets[sheet.Id];var row=current.HoleTables.Single().Rows.Single();
        Assert.Null(row.StaleReason);Assert.Equal(7,row.Number);Assert.Equal(table.Rows.Single().Id,row.Id);
        Assert.Equal(proof.Source,row.Evidence.Source.HistoryOrigin);Assert.Equal(head,row.Evidence.Head);
        Assert.Equal(proof.DiameterMm,row.Evidence.DiameterMm,6);Assert.Equal(proof.DepthMm,row.Evidence.DepthMm,6);
        Assert.Equal(DrawingHoleSpecificationSource.CurrentGeometry,row.Evidence.SpecificationSource);Assert.Equal(count,(await f.Holes()).Length);
        var template=Guid.NewGuid();await f.Session.ExecuteAsync(DrawingTemplateCommands.Save(sheet.Id,template,"Keep source"));
        var before=f.Session.Snapshot.DrawingSheets[sheet.Id].HoleTables.Single();
        await f.Session.ExecuteAsync(DrawingTemplateCommands.Apply(sheet.Id,template));
        Assert.Equal(before,f.Session.Snapshot.DrawingSheets[sheet.Id].HoleTables.Single());
        var storage=new CadDocumentStorage(holeResolver:f.Kernel);string path=files.PathFor("pattern.cadoryx");await f.Session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,f.Assets))
        {
            Assert.True(row.Evidence.SameProof(loaded.Snapshot.DrawingSheets[sheet.Id].HoleTables.Single().Rows.Single().Evidence));
            Assert.Equal(traced,await f.Kernel.TraceExactAsync(loaded.Snapshot,selection,pattern.Id,f.Assets));
            TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("pattern.pdf"));
        }
        var good=f.Session.Snapshot;await f.Session.ExecuteAsync(new RecomputeCommand(seed.Id,((HoleRecipe)seed.Recipe) with{Diameter=2.5}));
        Assert.Equal(HistoryResolutionStatus.Stale,(await f.Kernel.TraceExactAsync(f.Session.Snapshot,selection,pattern.Id,f.Assets)).Status);
        Assert.NotNull(f.Session.Snapshot.DrawingSheets[sheet.Id].HoleTables.Single().Rows.Single().StaleReason);
        Assert.Contains(proof.Source.Asset,f.Session.Snapshot.ReferencedAssets());
        await f.Session.UndoAsync();Assert.Same(good,f.Session.Snapshot);
    }

    [Theory][InlineData("swap")][InlineData("adapter")][InlineData("missing")][InlineData("terminal")][InlineData("fingerprint")]
    public async Task BothSemanticAndExactEntrancesRejectForgedFlattenedMaps(string mutation)
    {
        await using var f=new Fixture();await f.Init();var hole=await f.Hole(HoleHead.Countersink);var document=f.Session.Snapshot;
        var history=hole.TopologyHistory!;var faces=history.Entries.Where(e=>e.SourceKind==HistoryShapeKind.Face&&e.Evolution==TopologyEvolution.Unchanged).Take(2).ToArray();
        Assert.Equal(2,faces.Length);
        var changed=mutation switch
        {
            "swap"=>history with{Entries=[..history.Entries.Select(e=>e==faces[0]?e with{ResultIndex=faces[1].ResultIndex}:e==faces[1]?e with{ResultIndex=faces[0].ResultIndex}:e)]},
            "adapter"=>history with{AdapterVersion=OcctGeometryKernel.HistoryAdapterVersion},
            "missing"=>history with{Entries=[..history.Entries.Where(e=>e.SourceIndex!=faces[0].SourceIndex)]},
            "terminal"=>history with{Entries=[..history.Entries.Select(e=>e==faces[0]?e with{Evolution=TopologyEvolution.Ambiguous,ResultIndex=null,ResultKind=null}:e)]},
            _=>history with{SourceFingerprint=new string('0',64)}
        };
        changed.ValidateFor(hole);document=document with{Features=document.Features.SetItem(hole.Id,hole with{TopologyHistory=changed})};
        int count=f.Assets.Count;var exact=f.Pick(f.Source).First(s=>s.Kind==HistoryShapeKind.Face);
        foreach(var result in new[]{await f.Kernel.TraceExactAsync(document,exact,hole.Id,f.Assets),await f.Kernel.TraceAsync(document,
            TopologyReference.Box(document,f.Source.Id,BoxBoundary.XMin),hole.Id,f.Assets)})
        {Assert.Equal(mutation=="fingerprint"?HistoryResolutionStatus.Stale:HistoryResolutionStatus.Unsupported,result.Status);Assert.Null(result.Target);}
        Assert.Equal(count,f.Assets.Count);
    }

    [Fact]
    public async Task ExcludedSeedNeverClaimsAPhysicalSeedToFinalHistory()
    {
        await using var f=new Fixture();await f.Init();var seed=await f.Hole(HoleHead.Plain);var chosen=f.Pick(seed).First();
        var layout=new RepetitionRecipe(seed.Result,RepetitionKind.Mirror,new(new(15,0,0),Quaterniond.FromEulerDegrees(0,90,0)),1,0,[Guid.NewGuid()],IncludeSource:false,Output:RepetitionOutput.Fused);
        var recipe=new HolePatternRecipe(seed.Id,(HoleRecipe)seed.Recipe,layout);
        using(var result=await f.Kernel.EvaluateAsync(recipe,f.Assets)){Assert.Null(result.TopologyHistory);Assert.Contains(result.Diagnostics,d=>d.Code=="HISTORY.UNSUPPORTED");}
        await f.Session.ExecuteAsync(new AddHolePatternCommand(seed.OutputBodyId,recipe,"Excluded seed"));
        var target=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is HolePatternRecipe);Assert.Null(target.TopologyHistory);
        var trace=await f.Kernel.TraceExactAsync(f.Session.Snapshot,chosen,target.Id,f.Assets);Assert.Equal(HistoryResolutionStatus.Unsupported,trace.Status);Assert.Null(trace.Target);
    }

    [Theory][InlineData(1)][InlineData(2)]
    public async Task TrueOldSectionMigratesWithoutRewritingAlgorithmEvidence(int version)
    {
        await using var f=new Fixture();await f.Init();var hole=await f.Hole(HoleHead.Plain);using var files=new TestFiles();
        var storage=new CadDocumentStorage();string path=files.PathFor("old-history.cadoryx");await f.Session.SaveAsync(storage,path);
        if(version==1)FormatEvolutionTests.RewriteSection(path,"history",bytes=>
        {
            var packed=MessagePackSerializer.Deserialize<PackHistories>(bytes);
            return MessagePackSerializer.Serialize(packed with{Histories=[..packed.Histories.Select(h=>h with{AdditionalSources=null})]});
        });
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=[..m.Sections.Select(s=>s.Kind=="history"?s with{SchemaVersion=version}:s)]});
        using(var loaded=await storage.LoadAsync(path,f.Assets))
        {
            var actual=loaded.Snapshot.Features[hole.Id].TopologyHistory!;
            Assert.Equal(hole.TopologyHistory!.SchemaVersion,actual.SchemaVersion);Assert.Equal(hole.TopologyHistory.AdapterVersion,actual.AdapterVersion);
            Assert.Equal(hole.TopologyHistory.Entries.ToArray(),actual.Entries.ToArray());Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");
        }
    }

    [Theory][InlineData(1,3,0)][InlineData(2,3,0)][InlineData(1,1,5)][InlineData(2,1,5)][InlineData(1,1,6)][InlineData(2,1,6)]
    public async Task OldEnvelopesCannotSmuggleComposedMapsOrNewTerminalStates(int sectionVersion,int historyVersion,int evolution)
    {
        await using var f=new Fixture();await f.Init();await f.Hole(HoleHead.Plain);using var files=new TestFiles();var storage=new CadDocumentStorage();
        string path=files.PathFor("smuggled-history.cadoryx");await f.Session.SaveAsync(storage,path);
        FormatEvolutionTests.RewriteSection(path,"history",bytes=>
        {
            var data=MessagePackSerializer.Deserialize<PackHistories>(bytes);var h=data.Histories.Single();
            h=h with{Version=historyVersion,Entries=[..h.Entries.Select((e,i)=>i==0&&evolution!=0?e with{Evolution=evolution,Result=null,ResultKind=null}:e)]};
            return MessagePackSerializer.Serialize(data with{Histories=[h]});
        });
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=[..m.Sections.Select(s=>s.Kind=="history"?s with{SchemaVersion=sectionVersion}:s)]});
        int count=f.Assets.Count;await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
    }
}
