using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using MessagePack;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed partial class DrawingAnnotationTests
{
    [Theory][InlineData("cancel")][InlineData("detach")][InlineData("close")][InlineData("change")]
    public async Task LatePlanarInspectionCannotReturnUsableEvidenceAfterLifetimeOrStateChanges(string mode)
    {
        var f=new Fixture();var proof=await Planar(f);var gate=new GatedKernel(f.Kernel);var vm=new CadDocumentViewModel(f.Session,gate,new CadMessageLog());
        using var cancel=new CancellationTokenSource();
        var pending=vm.InspectDrawingPlanarSizeAsync(proof.Source,proof.OppositeSource,false,cancel.Token);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if(mode=="cancel")cancel.Cancel();
            else if(mode=="detach")vm.Detach();
            else if(mode=="close"){vm.Detach();await f.DisposeAsync();Assert.True(f.Assets.Count>0);}
            else await f.Session.ExecuteAsync(ResourceCommands.AddPart(DefinitionId.New(),"Changed"));
            gate.Release.TrySetResult();
            if(mode=="cancel")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
            else await Assert.ThrowsAsync<StaleDocumentException>(()=>pending);
        }
        finally{gate.Release.TrySetResult();vm.Detach();await f.DisposeAsync();}
        Assert.Equal(0,f.Assets.Count);
    }

    private static async Task<DrawingPlanarSizeEvidence> Planar(Fixture f,bool internalWidth=false)
    {
        await f.Init();
        if(internalWidth)
        {
            var part=f.Session.Snapshot.Definitions.Values.OfType<PartDefinition>().Single();
            using var wire=ShapeFactory.CreatePolygonWire([new(0,0,0),new(20,0,0),new(20,12,0),new(13,12,0),
                new(13,5,0),new(7,5,0),new(7,12,0),new(0,12,0)],true);
            using var face=ShapeFactory.CreatePlanarFace(wire);using var vector=GpVec.Create(0,0,4);using var shape=face.Extrude(vector);
            using var stored=OcctGeometryBridge.StoreShape(shape,f.Assets);
            await f.Session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Slot",part.Id));
            await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));
        }
        var document=f.Session.Snapshot;var body=document.Bodies.Values.Single(b=>b.Name==(internalWidth?"Slot":"Part"));
        using var solid=OcctGeometryBridge.ReadShape(body.Geometry,f.Assets);using var map=RepairSnapshot.Create(solid);
        var axis=internalWidth?new Vector3d(1,0,0):Vector3d.UnitZ;
        var items=new List<(int Index,double Position)>();
        foreach(var item in map.Topology.Where(t=>t.Kind==ShapeKind.Face))
        {
            using var face=map.CopySubshape(item.Selection);var s=face.GetFaceSurfaceSnapshot();
            var at=face.EvaluateFace((s.FirstUParameter+s.LastUParameter)/2,(s.FirstVParameter+s.LastVParameter)/2);
            var normal=new Vector3d(at.Normal.X,at.Normal.Y,at.Normal.Z);
            if(Math.Abs(normal.Dot(axis))>1-1e-8)items.Add((item.Selection.Index,new Vector3d(at.Point.X,at.Point.Y,at.Point.Z).Dot(axis)));
        }
        var chosen=items.OrderBy(i=>i.Position).Skip(internalWidth?1:0).Take(2).ToArray();
        var path=document.EnumerateOccurrences().Single().Path;
        var first=await f.Kernel.ResolveDrawingTopologyAsync(document,path,body.Id,chosen[0].Index,map.Fingerprint,f.Assets);
        var second=await f.Kernel.ResolveDrawingTopologyAsync(document,path,body.Id,chosen[1].Index,map.Fingerprint,f.Assets);
        return await f.Kernel.ResolveDrawingPlanarSizeAsync(document,first,second,internalWidth,f.Assets);
    }
    private static DrawingDatumSymbol Median(Fixture f,DrawingPlanarSizeEvidence proof,string name="A")=>
        new(Guid.NewGuid(),f.View,name,proof.Source,new(35,30)){FeatureType=DrawingDatumFeatureType.PlanarSizeMedianPlane,PlanarSizeEvidence=proof};

    [Theory]
    [InlineData(false,DrawingStandard.ISO)][InlineData(false,DrawingStandard.GB)][InlineData(false,DrawingStandard.ASME)]
    [InlineData(true,DrawingStandard.ISO)][InlineData(true,DrawingStandard.GB)][InlineData(true,DrawingStandard.ASME)]
    public async Task CompletePlanarMedianOwnsBothFiniteFacesAndRoundtripsWithManufacturingBindings(bool internalWidth,DrawingStandard standard)
    {
        var f=new Fixture();var assets=f.Assets;
        await using(f)
        {
            using var files=new TestFiles();var proof=await Planar(f,internalWidth);Assert.Equal(internalWidth?6:30,proof.WidthMm,6);Assert.Equal(internalWidth,proof.Internal);
            await f.Session.ExecuteAsync(new EditDocumentCommand("Standard",d=>d with{DrawingSheets=d.DrawingSheets.SetItem(f.Sheet,d.DrawingSheets[f.Sheet] with{Standard=standard})}));
            var datum=Median(f,proof);await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum));
            var source=await f.Reference(bodyId:proof.Source.BodyId,excluding:proof.Source.FullTopologyIndex);
            if(source.FullTopologyIndex==proof.OppositeSource.FullTopologyIndex)source=await f.Reference(bodyId:proof.Source.BodyId,skip:2);
            var frame=new DrawingFeatureControlFrame(Guid.NewGuid(),f.View,source,new(65,50),DrawingGeometricCharacteristic.Perpendicularity,.03)
                {DatumSystem=[new([new(datum.Id,DrawingMaterialCondition.Maximum)])]};
            await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,frame));
            var sheet=f.Session.Snapshot.DrawingSheets[f.Sheet];var actual=sheet.ControlFrames.Single();
            Assert.Equal(proof.OppositeSource,actual.DatumBindings.Single().OppositeSource);Assert.True(proof.SameProof(sheet.DatumSymbols.Single().PlanarSizeEvidence!));
            var original=f.Session.Snapshot;
            await f.Session.ExecuteAsync(DrawingTemplateCommands.Save(f.Sheet,Guid.NewGuid(),"Two faces"));
            var template=f.Session.Snapshot.DrawingTemplates.Values.Single();
            await f.Session.ExecuteAsync(DrawingTemplateCommands.Apply(f.Sheet,template.Id));
            Assert.Equal(sheet.DatumSymbols.Single(),f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.Single());
            var path=files.PathFor("planar.cadoryx");var storage=new CadDocumentStorage(datumResolver:f.Kernel);await f.Session.SaveAsync(storage,path);
            Assert.Equal(14,FormatEvolutionTests.Manifest(path).Sections.Single(s=>s.Kind=="drawings").SchemaVersion);
            Assert.Contains("cadoryx.planar-manufacturing-sizes.1",FormatEvolutionTests.Manifest(path).RequiredCapabilities);
            using(var loaded=await storage.LoadAsync(path,f.Assets))
            {
                var next=loaded.Snapshot.DrawingSheets[f.Sheet];Assert.True(proof.SameProof(next.DatumSymbols.Single().PlanarSizeEvidence!));
                Assert.Equal(actual.DatumBindings.ToArray(),next.ControlFrames.Single().DatumBindings.ToArray());
                TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("planar.pdf"));
            }
            await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().LoadAsync(path,f.Assets));
            var old=File.ReadAllBytes(path);await Assert.ThrowsAsync<NotSupportedException>(()=>f.Session.SaveAsync(new CadDocumentStorage(),path));Assert.Equal(old,File.ReadAllBytes(path));
            await f.Session.UndoAsync();await f.Session.UndoAsync();Assert.Same(original,f.Session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("width")][InlineData("opposite-anchor")][InlineData("opposite-face")][InlineData("missing-domain")]
    [InlineData("internal-type")][InlineData("same-face")][InlineData("missing-proof")]
    public async Task PlanarSizeForgeryIsRejectedBeforePublishingAnyPartOfTheAnnotation(string mutation)
    {
        await using var f=new Fixture();var proof=await Planar(f);var datum=Median(f,proof);
        var forged=mutation switch
        {
            "width"=>proof with{WidthMm=31},
            "opposite-anchor"=>proof with{OppositeSource=proof.OppositeSource with{LocalPoint=proof.OppositeSource.LocalPoint+new Vector3d(1,0,0)}},
            "opposite-face"=>proof with{OppositeSource=await f.Reference(excluding:proof.OppositeSource.FullTopologyIndex)},
            "missing-domain"=>proof with{PrismBoundaryEdgeIndices=[1,2,3,4,5,6]},
            "internal-type"=>proof with{Internal=true},
            "same-face"=>proof with{OppositeSource=proof.Source},
            _=>proof
        };
        datum=datum with{PlanarSizeEvidence=mutation=="missing-proof"?null:forged};
        var before=f.Session.Snapshot;int count=f.Assets.Count;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum)));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
    }

    [Theory][InlineData(DrawingGeometricCharacteristic.Flatness)][InlineData(DrawingGeometricCharacteristic.Position)]
    public async Task MedianPlaneUsesNonDiameterZoneAndMaterialConditionWithCompleteProof(DrawingGeometricCharacteristic characteristic)
    {
        await using var f=new Fixture();var proof=await Planar(f);
        var surface=await f.Reference(excluding:proof.Source.FullTopologyIndex);if(surface.FullTopologyIndex==proof.OppositeSource.FullTopologyIndex)surface=await f.Reference(skip:2);
        var datum=new DrawingDatumSymbol(Guid.NewGuid(),f.View,"B",surface,new(25,25));await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum));
        var frame=new DrawingFeatureControlFrame(Guid.NewGuid(),f.View,proof.Source,new(60,60),characteristic,.02,DrawingToleranceTarget.MedianPlane,false,DrawingMaterialCondition.Maximum)
            {PlanarSizeEvidence=proof,Datums=characteristic==DrawingGeometricCharacteristic.Position?[datum.Id]:[]};
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,frame));var before=f.Session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,frame with{DiameterZone=true},true)));
        Assert.Same(before,f.Session.Snapshot);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,frame with{Characteristic=DrawingGeometricCharacteristic.Cylindricity},true)));
        Assert.Same(before,f.Session.Snapshot);
    }

    [Theory][InlineData("width")][InlineData("opposite-anchor")][InlineData("boundary")]
    public async Task PersistedPlanarProofMustReverifySecondOriginalFaceAndEntireFiniteDomain(string mutation)
    {
        await using var f=new Fixture();var proof=await Planar(f);using var files=new TestFiles();await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,Median(f,proof)));
        var storage=new CadDocumentStorage(datumResolver:f.Kernel);var path=files.PathFor("forged.cadoryx");await f.Session.SaveAsync(storage,path);
        FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
        {
            var book=MessagePackSerializer.Deserialize<PackDrawingBook>(bytes);var sheet=book.Sheets.Single();var datum=sheet.Datums!.Single();var p=datum.PlanarSize!;
            p=mutation switch{"width"=>p with{Width=31},"opposite-anchor"=>p with{OppositeSource=p.OppositeSource with{Parameter=p.OppositeSource.Parameter+.1}},
                _=>p with{PrismBoundaryEdges=[1,2,3,4,5,6]}};
            return MessagePackSerializer.Serialize(book with{Sheets=[sheet with{Datums=[datum with{PlanarSize=p}]}]});
        });
        int count=f.Assets.Count;await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
        var kept=files.PathFor("keep.cadoryx");File.WriteAllText(kept,"Keep");
        var old=f.Session.Snapshot;var sheet0=old.DrawingSheets[f.Sheet];var datum0=sheet0.DatumSymbols.Single();
        var forged=old with{DrawingSheets=old.DrawingSheets.SetItem(f.Sheet,sheet0 with{DatumSymbols=[datum0 with{PlanarSizeEvidence=proof with{WidthMm=31}}]})};
        await Assert.ThrowsAsync<CadValidationException>(()=>storage.SaveAsync(forged,f.Assets,kept));Assert.Equal("Keep",File.ReadAllText(kept));
    }

    internal static void StripDrawingPlanarFieldsForLegacy(string path)=>FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
    {
        var book=JsonNode.Parse(MessagePackSerializer.ConvertToJson(bytes))!.AsArray();
        foreach(var sheet in book[0]!.AsArray())
        {
            if(sheet![11] is {} datums)foreach(var datum in datums.AsArray()){var d=datum!.AsArray();while(d.Count>8)d.RemoveAt(d.Count-1);}
            if(sheet[12] is {} frames)foreach(var frame in frames.AsArray())
            {
                var f=frame!.AsArray();while(f.Count>14)f.RemoveAt(f.Count-1);
                if(f.Count>13&&f[13] is {} bindings)foreach(var binding in bindings.AsArray()){var b=binding!.AsArray();while(b.Count>3)b.RemoveAt(b.Count-1);}
            }
        }
        return MessagePackSerializer.ConvertFromJson(book.ToJsonString());
    });
    [Theory][InlineData(false)][InlineData(true)]
    public async Task GenuineVersionElevenMigratesWithoutLosingHistoryAndNewNilCannotHideInOldEnvelope(bool forgedNil)
    {
        await using var f=new Fixture();await f.Init();using var files=new TestFiles();var datum=new DrawingDatumSymbol(Guid.NewGuid(),f.View,"A",await f.Reference(),new(35,30));
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum));
        var path=files.PathFor("v11.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),path);
        if(!forgedNil)StripDrawingPlanarFieldsForLegacy(path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=[..m.Sections.Select(s=>s.Kind=="drawings"?s with{SchemaVersion=11}:s)]});
        if(forgedNil)await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,f.Assets));
        else using(var loaded=await new CadDocumentStorage().LoadAsync(path,f.Assets))Assert.Equal(datum,loaded.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.Single());
    }

    [Fact]
    public async Task BothAuthorsContinueAcrossCornerBooleanAndAChangedOriginKeepsBothOldProofsAndAssets()
    {
        await using var f=new Fixture();var proof=await Planar(f);var datum=Median(f,proof);await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum));
        var original=f.Session.Snapshot;var source=original.Bodies[proof.Source.BodyId];var part=source.PartId;
        await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,4,34,RigidTransform3d.Translate(8,18,-2)),"Corner cutter",part));
        var cutter=f.Session.Snapshot.Bodies.Values.Single(b=>b.Name=="Corner cutter");
        await f.Session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[source.Id,cutter.Id]));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));
        var current=f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.Single();
        Assert.Null(current.StaleReason);Assert.Equal(proof.Source,current.Source.HistoryOrigin);
        Assert.Equal(proof.OppositeSource,current.PlanarSizeEvidence!.OppositeSource.HistoryOrigin);
        Assert.Equal(30,current.PlanarSizeEvidence.WidthMm,6);Assert.NotEqual(proof.Source.BodyId,current.Source.BodyId);
        var producing=original.Features[proof.Source.FeatureId!.Value];
        await f.Session.ExecuteAsync(new RecomputeCommand(producing.Id,new BoxRecipe(10,20,31,RigidTransform3d.Identity)));
        var stale=f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.Single();Assert.NotNull(stale.StaleReason);
        Assert.True(current.PlanarSizeEvidence.SameProof(stale.PlanarSizeEvidence!));Assert.Equal(current.Source,stale.Source);
        Assert.Contains(proof.Source.Asset,f.Session.Snapshot.ReferencedAssets());Assert.Contains(proof.OppositeSource.Asset,f.Session.Snapshot.ReferencedAssets());
    }
}

