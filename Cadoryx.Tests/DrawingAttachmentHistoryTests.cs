using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.IO;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed partial class DrawingAnnotationTests
{
    [Fact]
    public async Task ExplicitViewSourceContinuesSurfaceDatumAndFrameWithoutReplacingAuthorAndExplicitReselectionStillInvalidates()
    {
        await using var f=new Fixture();await f.Init();using var files=new TestFiles();
        var author=await f.Reference();var controlled=await f.Reference(excluding:author.FullTopologyIndex);
        Assert.Contains(nameof(DrawingTopologyReference),author.ToString());
        Assert.DoesNotContain("AuthorSelection",System.Text.Json.JsonSerializer.Serialize(author));
        var datum=new DrawingDatumSymbol(Guid.NewGuid(),f.View,"A",author,new(35,30));
        var frame=new DrawingFeatureControlFrame(Guid.NewGuid(),f.View,controlled,new(65,30),DrawingGeometricCharacteristic.Perpendicularity,.02){Datums=[datum.Id]};
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum));
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,frame));var before=f.Session.Snapshot;
        var templateId=Guid.NewGuid();
        await f.Session.ExecuteAsync(DrawingTemplateCommands.Save(f.Sheet,templateId,"Preserve annotations"));
        var beforeTemplate=f.Session.Snapshot.DrawingSheets[f.Sheet];
        await f.Session.ExecuteAsync(DrawingTemplateCommands.Apply(f.Sheet,templateId));
        var appliedTemplate=f.Session.Snapshot.DrawingSheets[f.Sheet];
        Assert.Equal(beforeTemplate.DatumSymbols.Single(),appliedTemplate.DatumSymbols.Single());
        Assert.Equal(beforeTemplate.ControlFrames.Single(),appliedTemplate.ControlFrames.Single());
        before=f.Session.Snapshot;
        var original=before.Bodies.Values.Single();
        await f.Session.ExecuteAsync(new AddHoleCommand(original.Id,new(original.Geometry,RigidTransform3d.Translate(0,0,30),new(5,10),2,true,0),"Hole"));
        var unselected=f.Session.Snapshot;Assert.NotNull(unselected.DrawingSheets[f.Sheet].Views.Single().StaleReason);
        Assert.Equal(author,unselected.DrawingSheets[f.Sheet].DatumSymbols.Single().Source);
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));
        var document=f.Session.Snapshot;document.Validate();var sheet=document.DrawingSheets[f.Sheet];
        var currentDatum=sheet.DatumSymbols.Single();var currentFrame=sheet.ControlFrames.Single();
        Assert.Null(currentDatum.StaleReason);Assert.Null(currentFrame.StaleReason);
        Assert.Equal(author,currentDatum.Source.HistoryOrigin);Assert.Equal(controlled,currentFrame.Source.HistoryOrigin);
        Assert.Contains(nameof(DrawingTopologyReference),currentDatum.Source.ToString());
        var json=System.Text.Json.JsonSerializer.Serialize(currentDatum.Source);
        using(var parsed=System.Text.Json.JsonDocument.Parse(json))
        {
            Assert.Equal(author.FullTopologyIndex,parsed.RootElement.GetProperty("HistoryOrigin").GetProperty("FullTopologyIndex").GetInt32());
            Assert.Equal(System.Text.Json.JsonValueKind.Null,parsed.RootElement.GetProperty("HistoryOrigin").GetProperty("HistoryOrigin").ValueKind);
        }
        Assert.NotEqual(author.BodyId,currentDatum.Source.BodyId);Assert.Equal(currentDatum.Source,currentFrame.DatumBindings.Single().Source);
        var saved=files.PathFor("continued.cadoryx");await Assert.ThrowsAsync<NotSupportedException>(()=>f.Session.SaveAsync(new CadDocumentStorage(),saved));
        var storage=new CadDocumentStorage(datumResolver:f.Kernel);await f.Session.SaveAsync(storage,saved);
        await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().LoadAsync(saved,f.Assets));
        using(var loaded=await storage.LoadAsync(saved,f.Assets))
        {
            Assert.Equal(currentDatum.Source,loaded.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.Single().Source);
            TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("continued.pdf"));
        }
        var kept=files.PathFor("kept.cadoryx");File.WriteAllText(kept,"Keep");
        var forged=currentDatum.Source with{LocalPoint=currentDatum.Source.LocalPoint+Vector3d.UnitZ};
        var forgedSheet=sheet with{DatumSymbols=[currentDatum with{Source=forged}],ControlFrames=[currentFrame with{DatumBindings=[new(datum.Id,currentDatum.FeatureType,forged)]}]};
        await Assert.ThrowsAsync<CadValidationException>(()=>storage.SaveAsync(document with{DrawingSheets=document.DrawingSheets.SetItem(f.Sheet,forgedSheet)},f.Assets,kept));
        Assert.Equal("Keep",File.ReadAllText(kept));
        await f.Session.UndoAsync();Assert.Same(unselected,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(document,f.Session.Snapshot);
        // Selecting the same current face explicitly starts a new author binding and requires frame confirmation.
        var raw=currentDatum.Source with{HistoryOrigin=null};
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,currentDatum with{Source=raw},true));
        Assert.NotNull(f.Session.Snapshot.DrawingSheets[f.Sheet].ControlFrames.Single().StaleReason);
        Assert.Equal(currentFrame.DatumBindings.Single(),f.Session.Snapshot.DrawingSheets[f.Sheet].ControlFrames.Single().DatumBindings.Single());
    }

    [Fact]
    public async Task GeneratedHoleAndSizeProofsContinueToSecondHoleAndChangedOriginRetainsOldBindingsAndAssets()
    {
        await using var f=new Fixture();await f.Init(cylinder:true);using var files=new TestFiles();
        var cylinder=f.Session.Snapshot.Bodies.Values.Single();
        await f.Session.ExecuteAsync(new AddHoleCommand(cylinder.Id,new(cylinder.Geometry,RigidTransform3d.Translate(0,0,20),new(0,0),2,true,0),"First hole"));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));
        var feature=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is HoleRecipe);
        var a=await f.Reference(cylinder:true);var b=await f.Reference(cylinder:true,excluding:a.FullTopologyIndex);
        var pa=await f.Kernel.ResolveDrawingCylinderSizeAsync(f.Session.Snapshot,a,f.Assets);
        var author=pa.Internal?a:b;var controlled=pa.Internal?b:a;
        var datum=new DrawingDatumSymbol(Guid.NewGuid(),f.View,"A",author,new(35,30)){FeatureType=DrawingDatumFeatureType.CylinderSizeAxis};
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum));
        var frame=new DrawingFeatureControlFrame(Guid.NewGuid(),f.View,controlled,new(65,30),DrawingGeometricCharacteristic.Position,.025,DrawingToleranceTarget.Axis,true,DrawingMaterialCondition.Maximum)
            {DatumSystem=[new([new(datum.Id,DrawingMaterialCondition.Maximum)])]};
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutControlFrame(f.Sheet,frame));
        var proof=await f.Kernel.ResolveDrawingHoleAsync(f.Session.Snapshot,author,f.Assets);
        var table=new DrawingHoleTable(Guid.NewGuid(),f.View,"Holes",author.Path,author.DefinitionId,RigidTransform3d.Identity,new(25,90))
            {Rows=[new(Guid.NewGuid(),7,proof,new(0,0))]};
        await f.Session.ExecuteAsync(DrawingHoleTableCommands.Put(f.Sheet,table));
        var first=f.Session.Snapshot.Bodies.Values.Single();
        await f.Session.ExecuteAsync(new AddHoleCommand(first.Id,new(first.Geometry,RigidTransform3d.Translate(0,0,20),new(3,0),1,true,0),"Second hole"));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));
        var good=f.Session.Snapshot;good.Validate();var sheet=good.DrawingSheets[f.Sheet];
        Assert.Null(sheet.DatumSymbols.Single().StaleReason);Assert.Null(sheet.ControlFrames.Single().StaleReason);
        var row=sheet.HoleTables.Single().Rows.Single();Assert.True(row.StaleReason is null,row.StaleReason);Assert.Equal(author,row.Evidence.Source.HistoryOrigin);
        Assert.Equal(7,row.Number);Assert.Equal(table.Rows.Single().Id,row.Id);Assert.Equal(2,row.Evidence.DiameterMm,6);Assert.Equal(20,row.Evidence.DepthMm,6);
        Assert.Equal(DrawingHoleSpecificationSource.CurrentGeometry,row.Evidence.SpecificationSource);Assert.Null(row.Evidence.AuthoredFeature);
        Assert.Equal(controlled,sheet.ControlFrames.Single().Source.HistoryOrigin);
        var storage=new CadDocumentStorage(datumResolver:f.Kernel);var saved=files.PathFor("hole-history.cadoryx");await f.Session.SaveAsync(storage,saved);
        using(var loaded=await storage.LoadAsync(saved,f.Assets))Assert.True(row.Evidence.SameProof(loaded.Snapshot.DrawingSheets[f.Sheet].HoleTables.Single().Rows.Single().Evidence));
        await f.Session.ExecuteAsync(new RecomputeCommand(feature.Id,((HoleRecipe)feature.Recipe) with{Diameter=3}));
        var stale=f.Session.Snapshot;var old=stale.DrawingSheets[f.Sheet];Assert.NotNull(old.DatumSymbols.Single().StaleReason);Assert.NotNull(old.ControlFrames.Single().StaleReason);
        Assert.NotNull(old.HoleTables.Single().Rows.Single().StaleReason);Assert.True(row.Evidence.SameProof(old.HoleTables.Single().Rows.Single().Evidence));
        Assert.Contains(author.Asset,stale.ReferencedAssets());Assert.Contains(row.Evidence.Source.Asset,stale.ReferencedAssets());
        var staleFile=files.PathFor("stale-history.cadoryx");await f.Session.SaveAsync(storage,staleFile);
        using(var loaded=await storage.LoadAsync(staleFile,f.Assets))Assert.Equal(author,loaded.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.Single().Source.HistoryOrigin);
        var pdf=files.PathFor("keep.pdf");File.WriteAllText(pdf,"Keep");Assert.Throws<CadValidationException>(()=>TechnicalDrawingPdf.Write(stale,pdf));Assert.Equal("Keep",File.ReadAllText(pdf));
        await f.Session.UndoAsync();Assert.Same(good,f.Session.Snapshot);
    }

    internal static void StripDrawingHistoryFieldsForLegacy(string path)
    {
        StripDrawingPlanarFieldsForLegacy(path);
        FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
    {
        var book=JsonNode.Parse(MessagePackSerializer.ConvertToJson(bytes))!.AsArray();
        static void Attachment(JsonNode? node){if(node is null)return;var a=node.AsArray();while(a.Count>14)a.RemoveAt(a.Count-1);}
        static void Size(JsonNode? node){if(node is not null)Attachment(node[0]);}
        foreach(var node in book[0]!.AsArray())
        {
            var sheet=node!.AsArray();
            if(sheet.Count>11&&sheet[11] is {} datums)foreach(var d in datums.AsArray()){Attachment(d![3]);if(d.AsArray().Count>7)Size(d[7]);}
            if(sheet.Count>12&&sheet[12] is {} frames)foreach(var f in frames.AsArray())
            {Attachment(f![2]);if(f.AsArray().Count>12)Size(f[12]);if(f.AsArray().Count>13&&f[13] is {} bindings)foreach(var b in bindings.AsArray())Attachment(b![2]);}
            if(sheet.Count>13&&sheet[13] is {} balloons)foreach(var b in balloons.AsArray())Attachment(b![2]);
            if(sheet.Count>15&&sheet[15] is {} tables)foreach(var t in tables.AsArray())foreach(var r in t![7]!.AsArray())Attachment(r![2]![0]);
        }
        return MessagePackSerializer.ConvertFromJson(book.ToJsonString());
        });
    }

    [Fact]
    public async Task TwoExplicitReachableTargetsCannotChooseOneEvenWhenTheyShareTheSameResultAsset()
    {
        await using var f=new Fixture();await f.Init();var author=await f.Reference();
        var datum=new DrawingDatumSymbol(Guid.NewGuid(),f.View,"A",author,new(35,30));await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum));
        var original=f.Session.Snapshot.Bodies.Values.Single();
        await f.Session.ExecuteAsync(new AddHoleCommand(original.Id,new(original.Geometry,RigidTransform3d.Translate(0,0,30),new(5,10),2,true,0),"Hole"));
        var hole=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is HoleRecipe);var body=f.Session.Snapshot.Bodies[hole.OutputBodyId];
        var copy=hole with{Id=FeatureId.New(),OutputBodyId=BodyId.New(),Name="Second history target"};
        await f.Session.ExecuteAsync(new EditDocumentCommand("Explicit history branch",d=>d with
        {
            Features=d.Features.Add(copy.Id,copy),Bodies=d.Bodies.Add(copy.OutputBodyId,body with{Id=copy.OutputBodyId,Producer=copy.Id,Name=copy.Name}),
            Definitions=d.Definitions.SetItem(hole.PartId,((PartDefinition)d.Definitions[hole.PartId]) with{Bodies=((PartDefinition)d.Definitions[hole.PartId]).Bodies.Add(copy.OutputBodyId),Features=((PartDefinition)d.Definitions[hole.PartId]).Features.Add(copy.Id)})
        }));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));var sheet=f.Session.Snapshot.DrawingSheets[f.Sheet];
        Assert.Equal(2,sheet.Views.Single().Sources.Length);Assert.Equal(author,sheet.DatumSymbols.Single().Source);Assert.Null(sheet.DatumSymbols.Single().Source.HistoryOrigin);
        Assert.Contains("one explicit reachable",sheet.DatumSymbols.Single().StaleReason);f.Session.Snapshot.Validate();
    }

    [Theory][InlineData("origin-anchor")][InlineData("current-anchor")][InlineData("nested-origin")]
    public async Task ContinuedAttachmentForgeryFailsLoadingAndPublishesNoAssets(string mutation)
    {
        await using var f=new Fixture();await f.Init();using var files=new TestFiles();var author=await f.Reference();
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,new(Guid.NewGuid(),f.View,"A",author,new(35,30))));
        var body=f.Session.Snapshot.Bodies.Values.Single();
        await f.Session.ExecuteAsync(new AddHoleCommand(body.Id,new(body.Geometry,RigidTransform3d.Translate(0,0,30),new(5,10),2,true,0),"Hole"));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.ReselectView(f.Sheet,f.View,null));
        Assert.Null(f.Session.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.Single().StaleReason);
        var path=files.PathFor("forged-history.cadoryx");var storage=new CadDocumentStorage(datumResolver:f.Kernel);await f.Session.SaveAsync(storage,path);
        FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
        {
            var book=MessagePackSerializer.Deserialize<PackDrawingBook>(bytes);var sheet=book.Sheets.Single();var datum=sheet.Datums!.Single();var reference=datum.Source;
            reference=mutation switch
            {
                "origin-anchor"=>reference with{HistoryOrigin=reference.HistoryOrigin! with{Point=[reference.HistoryOrigin.Point[0],reference.HistoryOrigin.Point[1],reference.HistoryOrigin.Point[2]+1]}},
                "current-anchor"=>reference with{Point=[reference.Point[0],reference.Point[1],reference.Point[2]+1]},
                _=>reference with{HistoryOrigin=reference.HistoryOrigin! with{HistoryOrigin=reference.HistoryOrigin}}
            };
            return MessagePackSerializer.Serialize(book with{Sheets=[sheet with{Datums=[datum with{Source=reference}]}]});
        });
        int count=f.Assets.Count;await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task TrueV10MigratesAndV10NewNilIsRejected(bool forgedNil)
    {
        await using var f=new Fixture();await f.Init();using var files=new TestFiles();
        var datum=new DrawingDatumSymbol(Guid.NewGuid(),f.View,"A",await f.Reference(),new(35,30));
        await f.Session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(f.Sheet,datum));
        var path=files.PathFor("legacy-history.cadoryx");var storage=new CadDocumentStorage();await f.Session.SaveAsync(storage,path);
        if(!forgedNil)StripDrawingHistoryFieldsForLegacy(path);else StripDrawingPlanarFieldsForLegacy(path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="drawings"?s with{SchemaVersion=10}:s).ToImmutableArray()});
        int count=f.Assets.Count;
        if(forgedNil)await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));
        else using(var loaded=await storage.LoadAsync(path,f.Assets))Assert.Equal(datum.Source,loaded.Snapshot.DrawingSheets[f.Sheet].DatumSymbols.Single().Source);
        Assert.Equal(count,f.Assets.Count);
    }
}
