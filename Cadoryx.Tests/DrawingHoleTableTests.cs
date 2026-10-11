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

public sealed partial class DrawingHoleTableTests
{
    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets {get;}=new();internal OcctGeometryKernel Kernel {get;}=new();
        internal CadDocumentSession Session {get;}internal Guid Sheet {get;private set;}internal Guid View {get;private set;}
        internal Fixture(){Session=new(DocumentSnapshot.Create("Hole table"),Assets,Kernel,new InlineSessionDispatcher());}
        internal async Task Init(HoleHead head=HoleHead.Plain,bool through=false,bool standard=false)
        {
            await Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(12,10,15,RigidTransform3d.Identity),"Source"));
            var body=Session.Snapshot.Bodies.Values.Single();
            await Session.ExecuteAsync(new AddHoleCommand(body.Id,new(body.Geometry,RigidTransform3d.Translate(0,0,15),new(5,6),
                standard?3.4:2,through,through?0:6,head,head==HoleHead.Plain?0:4,head==HoleHead.Counterbore?2:0,
                head==HoleHead.Countersink?Math.PI/2:0,standard?new(3,ClearanceFit.Normal):null),"Hole"));
            await AddSheet();
        }
        internal async Task AddSheet()
        {
            var sheet=DrawingSheetTemplate.Create("Holes");Sheet=sheet.Id;View=Guid.NewGuid();
            await Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await Session.ExecuteAsync(TechnicalDrawingCommands.AddView(Sheet,View,"Top",DrawingViewKind.Top,new(90,60),2));
        }
        internal async Task<DrawingHoleEvidence> Evidence(OccurrencePath? path=null)
            =>Assert.Single(await AllEvidence(path));
        internal async Task<List<DrawingHoleEvidence>> AllEvidence(OccurrencePath? path=null)
        {
            var doc=Session.Snapshot;var body=doc.Bodies.Values.Single();using var shape=OcctGeometryBridge.ReadShape(body.Geometry,Assets);
            using var map=RepairSnapshot.Create(shape);var values=new List<DrawingHoleEvidence>();
            foreach(var item in map.Topology.Where(t=>t.Kind==ShapeKind.Face))
            {
                using var face=map.CopySubshape(item.Selection);if(face.GetFaceSurfaceSnapshot().SurfaceType!=SurfaceGeometryType.Cylinder)continue;
                var source=await Kernel.ResolveDrawingTopologyAsync(doc,path??doc.EnumerateOccurrences().Single().Path,body.Id,item.Selection.Index,map.Fingerprint,Assets);
                try{values.Add(await Kernel.ResolveDrawingHoleAsync(doc,source,Assets));}catch(CadValidationException){ }
            }
            return values;
        }
        internal DrawingHoleTable Table(DrawingHoleEvidence evidence)=>new(Guid.NewGuid(),View,"Hole locations",evidence.Source.Path,
            evidence.Source.DefinitionId,RigidTransform3d.Translate(0,0,15),new(25,90))
            {Rows=[new(Guid.NewGuid(),1,evidence,new(0,0))]};
        internal DrawingHoleTable Current=>Session.Snapshot.DrawingSheets[Sheet].HoleTables.Single();
        public async ValueTask DisposeAsync(){await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
    [Theory][InlineData(HoleHead.Plain,false)][InlineData(HoleHead.Plain,true)]
    [InlineData(HoleHead.Counterbore,false)][InlineData(HoleHead.Counterbore,true)]
    [InlineData(HoleHead.Countersink,false)][InlineData(HoleHead.Countersink,true)]
    public async Task CurrentOriginalProofIncludesHeadAndTotalBlindDepth(HoleHead head,bool through)
    {
        await using var f=new Fixture();await f.Init(head,through);var evidence=await f.Evidence();
        Assert.Equal(head,evidence.Head);Assert.Equal(through,evidence.Through);Assert.Equal(through?15:6,evidence.DepthMm,6);
        Assert.Equal(2,evidence.DiameterMm,6);Assert.Equal(DrawingHoleSpecificationSource.CurrentAuthoredHole,evidence.SpecificationSource);
        if(head!=HoleHead.Plain){Assert.Equal(4,evidence.HeadDiameterMm,6);Assert.Equal(head==HoleHead.Counterbore?2:1,evidence.HeadDepthMm,6);}
        var table=f.Table(evidence);var before=f.Session.Snapshot;await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,table));
        Assert.Equal(5,f.Current.Rows[0].CoordinatesMm.X,6);Assert.Equal(6,f.Current.Rows[0].CoordinatesMm.Y,6);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Equal(table.Id,f.Current.Id);
    }
    [Fact] public async Task AuthoredStandardAndImportedStepAreDifferentSpecificationSources()
    {
        await using var f=new Fixture();await f.Init(standard:true);var authored=await f.Evidence();
        Assert.Equal(new MetricClearanceSpec(3,ClearanceFit.Normal),authored.Standard);
        using var files=new TestFiles();var doc=f.Session.Snapshot;using var shape=OcctGeometryBridge.ReadShape(doc.Bodies.Values.Single().Geometry,f.Assets);
        string step=files.PathFor("hole.step");ShapeExchange.WriteStep(shape,step);using var imported=ShapeExchange.ReadStep(step);
        await using var g=new Fixture();using var stored=OcctGeometryBridge.StoreShape(imported,g.Assets);
        await g.Session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"STEP hole"));await g.AddSheet();var actual=await g.Evidence();
        Assert.Equal(DrawingHoleSpecificationSource.CurrentGeometry,actual.SpecificationSource);Assert.Null(actual.Standard);Assert.Null(actual.AuthoredFeature);
        Assert.Equal(authored.DiameterMm,actual.DiameterMm,6);Assert.Equal(authored.DepthMm,actual.DepthMm,6);
    }
    [Fact] public async Task RepeatedHolesAreProvedOnTheCurrentPatternSolidAndBecomeStaleAfterItsRebuild()
    {
        await using var f=new Fixture();await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(30,30,5,RigidTransform3d.Identity),"Plate"));
        var box=f.Session.Snapshot.Bodies.Values.Single();var recipe=new HoleRecipe(box.Geometry,RigidTransform3d.Translate(0,0,5),new(5,5),2);
        await f.Session.ExecuteAsync(new AddHoleCommand(box.Id,recipe,"Seed"));var seed=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is HoleRecipe);
        var layout=new RepetitionRecipe(seed.Result,RepetitionKind.Linear,RigidTransform3d.Identity,3,8,
            [Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid()],RepetitionOutput.Fused);
        await f.Session.ExecuteAsync(new AddHolePatternCommand(seed.OutputBodyId,new(seed.Id,recipe,layout),"Three holes"));await f.AddSheet();
        var evidence=await f.AllEvidence();Assert.Equal(3,evidence.Count);Assert.All(evidence,e=>
        {Assert.Equal(5,e.DepthMm,6);Assert.Equal(2,e.DiameterMm,6);Assert.Null(e.Standard);Assert.Equal(DrawingHoleSpecificationSource.CurrentGeometry,e.SpecificationSource);});
        var table=f.Table(evidence[0]) with{LocalFrame=RigidTransform3d.Translate(0,0,5),
            Rows=[..evidence.Select((e,i)=>new DrawingHoleRow(Guid.NewGuid(),i+1,e,new(0,0)))]};
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,table));Assert.Equal(new double[]{5,13,21},f.Current.Rows.Select(r=>r.CoordinatesMm.X).Order());
        await f.Session.ExecuteAsync(new RecomputeCommand(seed.Id,recipe with{Diameter=3}));Assert.All(f.Current.Rows,r=>Assert.NotNull(r.StaleReason));
        Assert.All(f.Current.Rows,r=>Assert.Equal(2,r.Evidence.DiameterMm,6));
    }
    [Fact] public async Task SaveReopenKeepsRowIdentityProofLayoutAndVectorPdf()
    {
        await using var f=new Fixture();await f.Init(HoleHead.Counterbore);using var files=new TestFiles();var table=f.Table(await f.Evidence());
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,table));var expected=f.Current;
        string path=files.PathFor("holes.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(holeResolver:f.Kernel),path);
        using var loaded=await new CadDocumentStorage(holeResolver:f.Kernel).LoadAsync(path,f.Assets);var actual=loaded.Snapshot.DrawingSheets[f.Sheet].HoleTables.Single();
        Assert.Equal(expected.Id,actual.Id);Assert.Equal(expected.Rows[0].Id,actual.Rows[0].Id);Assert.True(expected.Rows[0].Evidence.SameProof(actual.Rows[0].Evidence));
        var layout=DrawingPageLayout.Create(loaded.Snapshot.DrawingSheets[f.Sheet],1,1);
        Assert.Contains(layout.Labels,l=>l.Text=="CB 4 x 2");Assert.Contains(layout.Labels,l=>l.Text=="6");Assert.Contains(layout.Labels,l=>l.Text=="O");
        TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("holes.pdf"));
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.DeleteRow(f.Sheet,table.Id,expected.Rows[0].Id));Assert.Empty(f.Current.Rows);
        await f.Session.UndoAsync();Assert.Equal(expected.Rows[0].Id,f.Current.Rows[0].Id);
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Move(f.Sheet,table.Id,new(35,100)));Assert.Equal(new Point2d(35,100),f.Current.PositionMm);
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Delete(f.Sheet,table.Id));Assert.Empty(f.Session.Snapshot.DrawingSheets[f.Sheet].HoleTables);
    }
    [Theory][InlineData("outer")][InlineData("cavity")][InlineData("partial")][InlineData("filled")]
    public async Task OuterMaterialClosedCavityAndRemovedWallsCannotCreateManufacturingRows(string kind)
    {
        await using var f=new Fixture();using var box=ShapeFactory.CreateBox(12,10,15);using var cylinder=ShapeFactory.CreateCylinder(1,15);
        using var map=RepairSnapshot.Create(box);using var hole=LocalFeatures.Hole(map,new(LocalHoleMode.ThroughAll,new(5,6,15),new(0,0,-1),1));
        using var closed=ShapeFactory.CreateCylinder(1,5);using var closedTool=closed.Transformed(ShapeTransform.CreateTranslation(5,6,5));
        using var cavity=box.Cut(closedTool);using var trim=ShapeFactory.CreateBox(7,10,15);using var trimTool=trim.Transformed(ShapeTransform.CreateTranslation(5,0,0));
        using var partial=hole.RequireShape().Cut(trimTool);using var fill=cylinder.Transformed(ShapeTransform.CreateTranslation(5,6,0));using var filled=hole.RequireShape().Fuse(fill);
        var shape=kind switch{"outer"=>cylinder,"cavity"=>cavity,"partial"=>partial,_=>filled};using var stored=OcctGeometryBridge.StoreShape(shape,f.Assets);
        await f.Session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Current geometry"));await f.AddSheet();
        var body=f.Session.Snapshot.Bodies.Values.Single();using var original=OcctGeometryBridge.ReadShape(body.Geometry,f.Assets);using var snapshot=RepairSnapshot.Create(original);
        var before=f.Session.Snapshot;
        foreach(var item in snapshot.Topology.Where(t=>t.Kind==ShapeKind.Face))
        {
            using var face=snapshot.CopySubshape(item.Selection);if(face.GetFaceSurfaceSnapshot().SurfaceType!=SurfaceGeometryType.Cylinder)continue;
            var source=await f.Kernel.ResolveDrawingTopologyAsync(before,before.EnumerateOccurrences().Single().Path,body.Id,item.Selection.Index,snapshot.Fingerprint,f.Assets);
            await Assert.ThrowsAsync<CadValidationException>(()=>f.Kernel.ResolveDrawingHoleAsync(before,source,f.Assets));
        }
        Assert.Same(before,f.Session.Snapshot);Assert.Empty(f.Session.Snapshot.DrawingSheets[f.Sheet].HoleTables);
    }
    [Theory][InlineData("diameter")][InlineData("depth")][InlineData("faces")][InlineData("entry")][InlineData("axis")][InlineData("source")]
    public async Task ForgedCurrentProofIsRejectedAtomically(string kind)
    {
        await using var f=new Fixture();await f.Init();var e=await f.Evidence();var forged=kind switch
        {"diameter"=>e with{DiameterMm=3},"depth"=>e with{DepthMm=7},"faces"=>e with{ProvenFaceIndices=e.ProvenFaceIndices.Add(99999)},
            "entry"=>e with{Entry=e.Entry+new Vector3d(1,0,0)},"axis"=>e with{Direction=e.Direction*-1},_=>e with{Source=e.Source with{Fingerprint=new('0',64)}}};
        var before=f.Session.Snapshot;await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(forged))));Assert.Same(before,f.Session.Snapshot);
    }
    [Fact] public async Task ChangedHoleRetainsOldRowAndRequiresExplicitCurrentReselection()
    {
        await using var f=new Fixture();await f.Init();var old=await f.Evidence();await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(old)));
        var table=f.Current;var feature=f.Session.Snapshot.Features[old.Source.FeatureId!.Value];var before=f.Session.Snapshot;
        await f.Session.ExecuteAsync(new RecomputeCommand(feature.Id,((HoleRecipe)feature.Recipe) with{Diameter=3}));
        Assert.NotNull(f.Current.Rows[0].StaleReason);Assert.True(old.SameProof(f.Current.Rows[0].Evidence));
        using var files=new TestFiles();var pdf=files.PathFor("holes.pdf");File.WriteAllText(pdf,"Previous valid PDF");
        Assert.Throws<CadValidationException>(()=>TechnicalDrawingPdf.Write(f.Session.Snapshot,pdf));Assert.Equal("Previous valid PDF",File.ReadAllText(pdf));
        var actual=await f.Evidence();await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Current with
        {Rows=[f.Current.Rows[0] with{Evidence=actual}]},true));Assert.Equal(table.Rows[0].Id,f.Current.Rows[0].Id);Assert.Null(f.Current.Rows[0].StaleReason);
        TechnicalDrawingPdf.Write(f.Session.Snapshot,pdf);await f.Session.UndoAsync();await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
    }
    [Fact] public async Task NumericOriginAxesAndExplicitOtherInstancesDetermineCoordinates()
    {
        await using var f=new Fixture();await f.Init();var e=await f.Evidence();var doc=f.Session.Snapshot;var other=ComponentSlotId.New();
        await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(new(doc.Id,[]),e.Source.DefinitionId,other,"Second",RigidTransform3d.Translate(30,40,0)));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));
        var second=await f.Evidence(new(doc.Id,[other]));var table=f.Table(e) with{Rows=[new(Guid.NewGuid(),1,e,new(0,0)),new(Guid.NewGuid(),2,second,new(0,0))]};
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,table));Assert.Equal(new Point2d(35,46),f.Current.Rows[1].CoordinatesMm);
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Current with
        {LocalFrame=new(new(1,2,15),Quaterniond.FromEulerDegrees(0,0,90))},true));
        Assert.Equal(4,f.Current.Rows[0].CoordinatesMm.X,6);Assert.Equal(-4,f.Current.Rows[0].CoordinatesMm.Y,6);
        var before=f.Session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Current with
        {LocalFrame=new(new(0,0,15),Quaterniond.FromEulerDegrees(0,90,0))},true)));Assert.Same(before,f.Session.Snapshot);
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.DeleteView(f.Sheet,f.View));Assert.Empty(f.Session.Snapshot.DrawingSheets[f.Sheet].HoleTables);
    }
    [Fact] public async Task LegacyV7HasNoHoleTablesAndRejectsEvenNewEmptyArray()
    {
        await using var f=new Fixture();await f.Init();using var files=new TestFiles();var path=files.PathFor("legacy.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(holeResolver:f.Kernel),path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="drawings"?s with{SchemaVersion=7}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage(holeResolver:f.Kernel).LoadAsync(path,f.Assets));
        FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
        {var book=MessagePackSerializer.Deserialize<PackDrawingBook>(bytes);return MessagePackSerializer.Serialize(book with{Sheets=book.Sheets.Select(s=>s with{HoleTables=null}).ToArray()});});
        using var loaded=await new CadDocumentStorage(holeResolver:f.Kernel).LoadAsync(path,f.Assets);Assert.Empty(loaded.Snapshot.DrawingSheets[f.Sheet].HoleTables);
    }
    [Fact] public async Task OneExplicitRowCanBeReselectedWhileOtherStaleRowsKeepTheirOldEvidence()
    {
        await using var f=new Fixture();await f.Init();var first=await f.Evidence();var doc=f.Session.Snapshot;var slot=ComponentSlotId.New();
        await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(new(doc.Id,[]),first.Source.DefinitionId,slot,"Second",RigidTransform3d.Translate(30,0,0)));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));var second=await f.Evidence(new(doc.Id,[slot]));
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(first) with
        {Rows=[new(Guid.NewGuid(),1,first,new(0,0)),new(Guid.NewGuid(),2,second,new(0,0))]}));
        var feature=f.Session.Snapshot.Features[first.Source.FeatureId!.Value];
        await f.Session.ExecuteAsync(new RecomputeCommand(feature.Id,((HoleRecipe)feature.Recipe) with{Diameter=3}));
        Assert.All(f.Current.Rows,r=>Assert.NotNull(r.StaleReason));var updated=await f.Evidence(new(doc.Id,[slot]));var table=f.Current;
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,table with{Rows=table.Rows.SetItem(1,table.Rows[1] with{Evidence=updated})},true));
        Assert.NotNull(f.Current.Rows[0].StaleReason);Assert.True(first.SameProof(f.Current.Rows[0].Evidence));Assert.Null(f.Current.Rows[1].StaleReason);
    }
    [Theory][InlineData("cancel")][InlineData("detach")][InlineData("change")]
    public async Task LateNativeHoleEvidenceCannotOutliveCancelCloseOrDocumentChange(string mode)
    {
        await using var f=new Fixture();await f.Init();var evidence=await f.Evidence();var gate=new GatedKernel(f.Kernel);
        var vm=new CadDocumentViewModel(f.Session,gate,new CadMessageLog());using var cancel=new CancellationTokenSource();
        var pending=vm.InspectDrawingHoleAsync(evidence.Source,cancel.Token);await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if(mode=="cancel")cancel.Cancel();
            else if(mode=="detach"){vm.Detach();await f.Session.DisposeAsync();Assert.True(f.Assets.Count>0);}
            else await f.Session.ExecuteAsync(ResourceCommands.AddPart(DefinitionId.New(),"Changed"));
            gate.Release.TrySetResult();await Assert.ThrowsAnyAsync<Exception>(()=>pending);
        }
        finally{gate.Release.TrySetResult();vm.Detach();}
    }
    private sealed class GatedKernel(OcctGeometryKernel native):IGeometryKernel,IDrawingHoleResolver
    {
        internal TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version=>native.Version;public bool Supports(GeometryRecipe recipe)=>native.Supports(recipe);
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)=>native.EvaluateAsync(recipe,assets,token);
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>native.ImportAsync(path,assets,token);
        public Task ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,token);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,options,token);
        public async Task<DrawingHoleEvidence> ResolveDrawingHoleAsync(DocumentSnapshot doc,DrawingTopologyReference source,IAssetStore assets,CancellationToken token=default)
        {var result=await native.ResolveDrawingHoleAsync(doc,source,assets,CancellationToken.None);Entered.TrySetResult();await Release.Task;return result;}
    }
    [Theory][InlineData("null")][InlineData("duplicate")][InlineData("evidence")][InlineData("coordinates")][InlineData("diameter")][InlineData("depth")]
    public async Task CorruptHoleTablesFailBeforeLoadPublishesUsableState(string kind)
    {
        await using var f=new Fixture();await f.Init();await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(await f.Evidence())));
        using var files=new TestFiles();var path=files.PathFor("broken.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(holeResolver:f.Kernel),path);
        FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
        {
            var book=MessagePackSerializer.Deserialize<PackDrawingBook>(bytes);var s=book.Sheets[0];var table=s.HoleTables![0];var row=table.Rows[0];
            return MessagePackSerializer.Serialize(book with{Sheets=[s with{HoleTables=kind switch
            {"null"=>null,"duplicate"=>[table,table],"evidence"=>[table with{Rows=[row with{Evidence=row.Evidence with{Faces=null!}}]}],
                "diameter"=>[table with{Rows=[row with{Evidence=row.Evidence with{Diameter=3}}]}],
                "depth"=>[table with{Rows=[row with{Evidence=row.Evidence with{Depth=7}}]}],
                _=>[table with{Rows=[row with{Coordinates=[123,456]}]}]}}]});
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage(holeResolver:f.Kernel).LoadAsync(path,f.Assets));
    }
    [Fact] public async Task SaveRequiresNativeProofAndKeepsOldFileWhenCurrentEvidenceIsForged()
    {
        await using var f=new Fixture();await f.Init();await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(await f.Evidence())));
        using var files=new TestFiles();var path=files.PathFor("protected.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(holeResolver:f.Kernel),path);
        var bytes=File.ReadAllBytes(path);var document=f.Session.Snapshot;var sheet=document.DrawingSheets[f.Sheet];var table=f.Current;var row=table.Rows[0];
        var forged=document with{DrawingSheets=document.DrawingSheets.SetItem(sheet.Id,sheet with{HoleTables=[table with
            {Rows=[row with{Evidence=row.Evidence with{DepthMm=7}}]}]})};
        await Assert.ThrowsAsync<CadValidationException>(()=>new CadDocumentStorage(holeResolver:f.Kernel).SaveAsync(forged,f.Assets,path));Assert.Equal(bytes,File.ReadAllBytes(path));
        await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().LoadAsync(path,f.Assets));
        await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().SaveAsync(document,f.Assets,path));Assert.Equal(bytes,File.ReadAllBytes(path));
    }
    [Fact] public async Task LoadedHoleTablesPreserveAuthorProofThroughTimelineInsertion()
    {
        await using var f=new Fixture();await f.Init();await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,f.Table(await f.Evidence())));
        using var files=new TestFiles();var path=files.PathFor("timeline.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(holeResolver:f.Kernel),path);
        using var loaded=await new CadDocumentStorage(holeResolver:f.Kernel).LoadAsync(path,f.Assets);
        await using var reopened=new CadDocumentSession(loaded.Snapshot,f.Assets,f.Kernel,new InlineSessionDispatcher());
        var part=f.Current.CoordinateDefinition;var before=reopened.Snapshot;await reopened.RollbackFeaturesAsync(part,1);
        await reopened.ExecuteAsync(new InsertFeatureAtCursorCommand(new AddBodyCommand(new CylinderRecipe(1,2,RigidTransform3d.Translate(20,0,0)),"Branch",part),reopened.FeatureCursor!));
        var table=reopened.Snapshot.DrawingSheets[f.Sheet].HoleTables.Single();Assert.Equal(f.Current.Id,table.Id);
        Assert.Equal(f.Current.Rows[0].Id,table.Rows[0].Id);Assert.True(f.Current.Rows[0].Evidence.SameProof(table.Rows[0].Evidence));Assert.Null(table.Rows[0].StaleReason);
        await reopened.UndoAsync();Assert.Same(before,reopened.Snapshot);
    }
}
