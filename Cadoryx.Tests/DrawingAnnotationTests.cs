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

public sealed partial class DrawingAnnotationTests
{
    internal static void StripNewFieldsForLegacy(string path)=>FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
    {
        var packed=MessagePackSerializer.Deserialize<PackDrawingBook>(bytes);
        return MessagePackSerializer.Serialize(packed with{Templates=null,Sheets=packed.Sheets.Select(s=>s with
            {Datums=null,Frames=null,Balloons=null,PartsTable=null,HoleTables=null,
                Template=s.Template is {} t?t with{Fields=null,Notes=null,Zones=null}:null}).ToArray()});
    });
    private sealed class Fixture : IAsyncDisposable
    {
        internal MemoryAssetStore Assets {get;}=new();internal OcctGeometryKernel Kernel {get;}=new();
        internal CadDocumentSession Session {get;}
        internal Guid Sheet {get;private set;}internal Guid View {get;private set;}
        internal Fixture(){Session=new(DocumentSnapshot.Create("Annotations"),Assets,Kernel,new InlineSessionDispatcher());}
        internal async Task Init(bool cylinder=false,bool freeform=false)
        {
            if(freeform)
            {
                using var shape=ShapeExchange.ReadBrep(Path.Combine(AppContext.BaseDirectory,"Fixtures","Exchange","minipc3-difficult-trim-solid.brep"));
                using var stored=OcctGeometryBridge.StoreShape(shape,Assets);
                await Session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Freeform"));
            }
            else await Session.ExecuteAsync(new AddBodyCommand(cylinder?new CylinderRecipe(5,20,RigidTransform3d.Identity):new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Part"));
            var sheet=DrawingSheetTemplate.Create("A");Sheet=sheet.Id;View=Guid.NewGuid();
            await Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await Session.ExecuteAsync(TechnicalDrawingCommands.AddView(Sheet,View,"Top",DrawingViewKind.Top,new(100,75),1));
        }
        internal async Task<DrawingTopologyReference> Reference(bool freeform=false,bool cylinder=false,OccurrencePath? path=null,
            bool plane=false,int skip=0,BodyId? bodyId=null,int? excluding=null)
        {
            var document=Session.Snapshot;var body=bodyId is {} selected?document.Bodies[selected]:document.Bodies.Values.Single();
            using var shape=OcctGeometryBridge.ReadShape(body.Geometry,Assets);using var map=RepairSnapshot.Create(shape);
            var item=map.Topology.Where(t=>
            {
                if(t.Kind!=ShapeKind.Face||t.Selection.Index==excluding)return false;using var face=map.CopySubshape(t.Selection);var type=face.GetFaceSurfaceSnapshot().SurfaceType;
                return freeform?type==SurfaceGeometryType.BSplineSurface:cylinder?type==SurfaceGeometryType.Cylinder:plane?type==SurfaceGeometryType.Plane:true;
            }).Skip(skip).First();
            return await Kernel.ResolveDrawingTopologyAsync(document,path??document.EnumerateOccurrences().Single().Path,body.Id,item.Selection.Index,map.Fingerprint,Assets);
        }
        public ValueTask DisposeAsync()=>Session.DisposeAsync();
    }

    [Fact] public async Task DatumRenamePreservesFrameIdentityDependencyAndRoundtripUndo()
    {
        await using var f=new Fixture();await f.Init();using var files=new TestFiles();var source=await f.Reference();
        var datum=new DrawingDatumSymbol(Guid.NewGuid(),f.View,"A",source,new(35,30));
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum));
        var frame=new DrawingFeatureControlFrame(Guid.NewGuid(),f.View,await f.Reference(excluding:source.FullTopologyIndex),new(65,30),DrawingGeometricCharacteristic.Perpendicularity,.02){Datums=[datum.Id]};
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,frame));var before=f.Session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DrawingAnnotationCommands.Delete(f.Sheet,datum.Id)));
        Assert.Same(before,f.Session.Snapshot);
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.RenameDatum(f.Sheet,datum.Id,"B"));
        var sheet=f.Session.Snapshot.DrawingSheets[f.Sheet];Assert.Equal(datum.Id,sheet.ControlFrames[0].Datums[0]);
        Assert.Equal(2,DrawingPageLayout.Create(sheet,1,1).Labels.Count(l=>l.Text=="B"));
        var file=files.PathFor("annotations.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),file);
        using(var loaded=await new CadDocumentStorage().LoadAsync(file,f.Assets))
        {
            Assert.Equal(sheet.DatumSymbols.ToArray(),loaded.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.ToArray());
            Assert.Equal(frame.Source,loaded.Snapshot.DrawingSheets[f.Sheet].ControlFrames[0].Source);
            TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("annotations.pdf"));
        }
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();
        Assert.Equal("B",f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols[0].Name);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task FreeformAndAnalyticAttachmentsAreReverifiedAndForgeryIsAtomic(bool freeform)
    {
        await using var f=new Fixture();await f.Init(freeform:freeform);var reference=await f.Reference(freeform);
        Assert.Equal(freeform?DrawingAttachmentGeometry.GeneralFace:DrawingAttachmentGeometry.PlaneFace,reference.Geometry);
        var datum=new DrawingDatumSymbol(Guid.NewGuid(),f.View,"A",reference,new(35,30));var before=f.Session.Snapshot;
        foreach(var forged in new[]{reference with{LocalPoint=reference.LocalPoint+Vector3d.UnitZ},reference with{Fingerprint=new('0',64)},
            reference with{Revision=GeometryRevisionId.New()},reference with{BoundaryEdgeIndex=99999},reference with{Path=new(DocumentId.New(),reference.Path.Slots)}})
        {
            await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum with{Source=forged})));
            Assert.Same(before,f.Session.Snapshot);
        }
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum));
        Assert.Null(f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols[0].StaleReason);
    }

    [Fact] public async Task GeometryChangeRetainsOldAnnotationsBlocksPdfAndRequiresExplicitReselection()
    {
        await using var f=new Fixture();await f.Init();var source=await f.Reference();using var files=new TestFiles();
        var datum=new DrawingDatumSymbol(Guid.NewGuid(),f.View,"A",source,new(35,30));
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum));
        var frame=new DrawingFeatureControlFrame(Guid.NewGuid(),f.View,await f.Reference(excluding:source.FullTopologyIndex),new(60,30),DrawingGeometricCharacteristic.Position,.1){Datums=[datum.Id]};
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,frame));
        var before=f.Session.Snapshot;var feature=before.Features.Values.Single();
        await f.Session.ExecuteAsync(new RecomputeCommand(feature.Id,new BoxRecipe(12,20,30,RigidTransform3d.Identity)));
        var sheet=f.Session.Snapshot.DrawingSheets[f.Sheet];Assert.Equal(source,sheet.DatumSymbols[0].Source);
        Assert.NotNull(sheet.DatumSymbols[0].StaleReason);Assert.NotNull(sheet.ControlFrames[0].StaleReason);
        var pdf=files.PathFor("old.pdf");File.WriteAllText(pdf,"Keep old file");
        Assert.Throws<CadValidationException>(()=>TechnicalDrawingPdf.Write(f.Session.Snapshot,pdf));Assert.Equal("Keep old file",File.ReadAllText(pdf));
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.Move(f.Sheet,datum.Id,new(40,30)));
        Assert.NotNull(f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols[0].StaleReason);
        var next=await f.Reference();await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum with{Source=next},true));
        Assert.NotNull(f.Session.Snapshot.DrawingSheets[f.Sheet].ControlFrames[0].StaleReason);
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,frame with{Source=await f.Reference(excluding:next.FullTopologyIndex)},true));
        TechnicalDrawingPdf.Write(f.Session.Snapshot,pdf);
        for(int i=0;i<4;i++)await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
    }

    [Fact] public async Task QuantityCountsSharedRecursiveInstancesOnceAcrossBodiesAndViews()
    {
        await using var f=new Fixture();await f.Init();var doc=f.Session.Snapshot;var part=doc.Definitions.Values.OfType<PartDefinition>().Single();
        var assembly=DefinitionId.New();var assemblySlot=ComponentSlotId.New();
        await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.CreateAssembly(new(doc.Id,[]),assembly,assemblySlot,"Nested",RigidTransform3d.Translate(10,0,0)));
        var slot=ComponentSlotId.New();await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(new(doc.Id,[assemblySlot]),part.Id,slot,"Other instance",RigidTransform3d.Translate(30,0,0)));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(f.Sheet,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(180,80),1));
        var source=await f.Reference(path:new(doc.Id,[assemblySlot,slot]));var balloon=new DrawingAssemblyBalloon(Guid.NewGuid(),f.View,source,new(35,55),1);
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutBalloon(f.Sheet,balloon));
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.SetPartsTable(f.Sheet,new(30,130)));
        var sheet=f.Session.Snapshot.DrawingSheets[f.Sheet];Assert.Equal(2,sheet.Balloons[0].Quantity);
        Assert.Contains(DrawingPageLayout.Create(sheet,1,1).Labels,l=>l.Text=="2");
        var before=f.Session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DrawingAnnotationCommands.PutBalloon(f.Sheet,balloon with{Id=Guid.NewGuid()})));
        Assert.Same(before,f.Session.Snapshot);
        // A moved instance keeps exact topology and updates only projection/leader placement.
        await f.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(new(doc.Id,[assemblySlot,slot]),RigidTransform3d.Translate(45,0,0)));
        Assert.Null(f.Session.Snapshot.DrawingSheets[f.Sheet].Balloons[0].StaleReason);
    }

    [Theory][InlineData(DrawingGeometricCharacteristic.Straightness)][InlineData(DrawingGeometricCharacteristic.Flatness)]
    [InlineData(DrawingGeometricCharacteristic.Circularity)][InlineData(DrawingGeometricCharacteristic.Cylindricity)]
    [InlineData(DrawingGeometricCharacteristic.ProfileLine)][InlineData(DrawingGeometricCharacteristic.ProfileSurface)]
    [InlineData(DrawingGeometricCharacteristic.Parallelism)][InlineData(DrawingGeometricCharacteristic.Perpendicularity)]
    [InlineData(DrawingGeometricCharacteristic.Angularity)][InlineData(DrawingGeometricCharacteristic.Position)]
    [InlineData(DrawingGeometricCharacteristic.CircularRunout)][InlineData(DrawingGeometricCharacteristic.TotalRunout)]
    public async Task CharacteristicsProduceVectorSymbolsAndRejectIllegalDatumAndSizeCombinations(DrawingGeometricCharacteristic characteristic)
    {
        await using var f=new Fixture();await f.Init(cylinder:true);var source=await f.Reference(cylinder:characteristic!=DrawingGeometricCharacteristic.Flatness,plane:characteristic==DrawingGeometricCharacteristic.Flatness);
        var datumSource=await f.Reference(plane:true,excluding:source.FullTopologyIndex);var datumType=DrawingDatumFeatureType.Surface;
        if(characteristic is DrawingGeometricCharacteristic.CircularRunout or DrawingGeometricCharacteristic.TotalRunout)
        {
            var part=f.Session.Snapshot.Definitions.Values.OfType<PartDefinition>().Single();
            await f.Session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(2,10,RigidTransform3d.Translate(15,0,0)),"Datum shaft",part.Id));
            await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));
            datumSource=await f.Reference(cylinder:true,bodyId:f.Session.Snapshot.Bodies.Values.Single(b=>b.Name=="Datum shaft").Id);
            datumType=DrawingDatumFeatureType.CylinderSizeAxis;
        }
        var datum=new DrawingDatumSymbol(Guid.NewGuid(),f.View,"A",datumSource,new(25,25)){FeatureType=datumType};await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum));
        bool form=characteristic is DrawingGeometricCharacteristic.Straightness or DrawingGeometricCharacteristic.Flatness or DrawingGeometricCharacteristic.Circularity or DrawingGeometricCharacteristic.Cylindricity;
        bool profile=characteristic is DrawingGeometricCharacteristic.ProfileLine or DrawingGeometricCharacteristic.ProfileSurface;
        var frame=new DrawingFeatureControlFrame(Guid.NewGuid(),f.View,source,new(65,25),characteristic,.02){Datums=form||profile?[]:[datum.Id]};
        var baseline=DrawingPageLayout.Create(f.Session.Snapshot.DrawingSheets[f.Sheet],1,1).Lines.Length;
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,frame));
        Assert.True(DrawingPageLayout.Create(f.Session.Snapshot.DrawingSheets[f.Sheet],1,1).Lines.Length>baseline+10);
        Assert.Throws<CadValidationException>(()=>(frame with{ToleranceMm=0}).Validate(f.Session.Snapshot.Id));
        Assert.Throws<CadValidationException>(()=>(frame with{MaterialCondition=DrawingMaterialCondition.Maximum}).Validate(f.Session.Snapshot.Id));
        if(!profile)Assert.Throws<CadValidationException>(()=>(frame with{Datums=form?[datum.Id]:[]}).Validate(f.Session.Snapshot.Id));
    }

    [Fact] public async Task CylindricalSizeModifiersAndOrderedDatumIdsPersistAndDoNotMasqueradeAsMeasurements()
    {
        await using var f=new Fixture();await f.Init(cylinder:true);var source=await f.Reference(cylinder:true);
        var a=new DrawingDatumSymbol(Guid.NewGuid(),f.View,"A",await f.Reference(plane:true),new(25,25));var b=a with{Id=Guid.NewGuid(),Name="B",PositionMm=new(25,40),Source=await f.Reference(plane:true,skip:1)};
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,a));await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,b));
        var frame=new DrawingFeatureControlFrame(Guid.NewGuid(),f.View,source,new(65,25),DrawingGeometricCharacteristic.Position,.01,DrawingToleranceTarget.Axis,true,DrawingMaterialCondition.Maximum){Datums=[b.Id,a.Id]};
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,frame));
        Assert.Equal(new[]{b.Id,a.Id},f.Session.Snapshot.DrawingSheets[f.Sheet].ControlFrames[0].Datums.ToArray());
        var labels=DrawingPageLayout.Create(f.Session.Snapshot.DrawingSheets[f.Sheet],1,1).Labels;
        Assert.Contains(labels,l=>l.Text=="M");Assert.Contains(labels,l=>l.Text=="0.01");
        Assert.Throws<CadValidationException>(()=>(frame with{Datums=[a.Id,a.Id]}).Validate(f.Session.Snapshot.Id));
    }

    [Theory][InlineData("null")][InlineData("duplicate")][InlineData("missing-datum")][InlineData("boundary")][InlineData("quantity")][InlineData("old-schema")]
    public async Task MalformedAnnotationsFailBeforeDocumentPublication(string mutation)
    {
        await using var f=new Fixture();await f.Init();using var files=new TestFiles();var source=await f.Reference();
        var datum=new DrawingDatumSymbol(Guid.NewGuid(),f.View,"A",source,new(35,30));await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum));
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,new DrawingFeatureControlFrame(Guid.NewGuid(),f.View,await f.Reference(excluding:source.FullTopologyIndex),new(65,30),DrawingGeometricCharacteristic.Position,.1){Datums=[datum.Id]}));
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutBalloon(f.Sheet,new(Guid.NewGuid(),f.View,source,new(35,55),1)));
        var path=files.PathFor(mutation+".cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),path);
        if(mutation=="old-schema")FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="drawings"?s with{SchemaVersion=5}:s).ToImmutableArray()});
        else FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
        {
            var book=MessagePackSerializer.Deserialize<PackDrawingBook>(bytes);var s=book.Sheets[0];
            s=mutation switch{
                "null"=>s with{Datums=[null!]},"duplicate"=>s with{Datums=[s.Datums![0],s.Datums[0]]},
                "missing-datum"=>s with{Frames=[s.Frames![0] with{Datums=[Guid.NewGuid()]}]},
                "quantity"=>s with{Balloons=[s.Balloons![0] with{Quantity=99}]},
                _=>s with{Datums=[s.Datums![0] with{Source=s.Datums[0].Source with{Boundary=-1}}]}};
            return MessagePackSerializer.Serialize(book with{Sheets=[s]});
        });
        var before=f.Assets.Count;await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,f.Assets));Assert.Equal(before,f.Assets.Count);
    }

    [Fact] public async Task VersionFiveMigratesWithoutMovingOrRebuildingViewsAndEmptyNewFieldsAreRejectedInLegacyPayload()
    {
        await using var f=new Fixture();await f.Init();using var files=new TestFiles();var path=files.PathFor("legacy.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="drawings"?s with{SchemaVersion=5}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,f.Assets));
        StripNewFieldsForLegacy(path);using var loaded=await new CadDocumentStorage().LoadAsync(path,f.Assets);
        var old=DrawingPageLayout.Create(f.Session.Snapshot.DrawingSheets[f.Sheet],1,1);var next=DrawingPageLayout.Create(loaded.Snapshot.DrawingSheets[f.Sheet],1,1);
        Assert.Equal(old.Lines.ToArray(),next.Lines.ToArray());Assert.Equal(old.Labels.ToArray(),next.Labels.ToArray());
    }

    [Theory][InlineData("cancel")][InlineData("detach")][InlineData("replace")][InlineData("change")]
    public async Task LateNativePickCannotPublishAfterCancelCloseReplacementOrDocumentChange(string mode)
    {
        var assets=new MemoryAssetStore();var native=new OcctGeometryKernel();var kernel=new GatedKernel(native);
        var session=new CadDocumentSession(DocumentSnapshot.Create("Late"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Box"));
        var doc=session.Snapshot;var body=doc.Bodies.Values.Single();using var shape=OcctGeometryBridge.ReadShape(body.Geometry,assets);using var map=RepairSnapshot.Create(shape);
        var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());int results=0,failures=0;
        vm.DrawingTopologyPicked+=(_,_)=>results++;vm.DrawingDatumPickFailed+=(_,_)=>failures++;
        try
        {
            vm.RequestDrawingTopologyPick(TopologyKind.Face);
            var pending=vm.ReceiveDrawingDatumPickAsync(doc.EnumerateOccurrences().Single().Path,body.Id,map.Topology.First(t=>t.Kind==ShapeKind.Face).Selection.Index,map.Fingerprint);
            await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if(mode=="detach"){vm.Detach();await session.DisposeAsync();Assert.True(assets.Count>0);}
            else if(mode=="cancel")vm.CancelDrawingDatumPick();
            else if(mode=="replace")vm.RequestDrawingDatumPick(TopologyKind.Edge);
            else await session.ExecuteAsync(ResourceCommands.AddPart(DefinitionId.New(),"Changed"));
            kernel.Release.TrySetResult();await pending;Assert.Equal(0,results);Assert.Equal(mode=="change"?1:0,failures);
            if(mode=="replace")Assert.True(vm.IsDrawingDatumPickPending);
        }
        finally{kernel.Release.TrySetResult();vm.Detach();await session.DisposeAsync();}
        Assert.Equal(0,assets.Count);
    }
    private sealed class GatedKernel(OcctGeometryKernel native):IGeometryKernel,IDrawingTopologyResolver,IDrawingPlanarSizeResolver
    {
        internal TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version=>native.Version;public bool Supports(GeometryRecipe recipe)=>native.Supports(recipe);
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)=>native.EvaluateAsync(recipe,assets,token);
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>native.ImportAsync(path,assets,token);
        public Task ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,token);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot doc,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>native.ExportAsync(doc,assets,path,options,token);
        public async Task<DrawingTopologyReference> ResolveDrawingTopologyAsync(DocumentSnapshot doc,OccurrencePath path,BodyId body,int index,string fingerprint,IAssetStore assets,CancellationToken token=default)
        {
            var pending=native.ResolveDrawingTopologyAsync(doc,path,body,index,fingerprint,assets,CancellationToken.None);
            Entered.TrySetResult();await Release.Task;return await pending;
        }
        public async Task<DrawingPlanarSizeEvidence> ResolveDrawingPlanarSizeAsync(DocumentSnapshot doc,DrawingTopologyReference first,
            DrawingTopologyReference second,bool internalWidth,IAssetStore assets,CancellationToken token=default)
        {
            var pending=native.ResolveDrawingPlanarSizeAsync(doc,first,second,internalWidth,assets,CancellationToken.None);
            Entered.TrySetResult();await Release.Task;return await pending;
        }
    }
}
