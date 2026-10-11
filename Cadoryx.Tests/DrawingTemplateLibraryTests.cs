using System.Collections.Immutable;
using System.Text.Json.Nodes;
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

public sealed class DrawingTemplateLibraryTests
{
    private static TechnicalDrawingSheet ContentSheet()
    {
        var sheet=DrawingSheetTemplate.Create("Manufacturing");
        return sheet with{Template=sheet.Template! with
        {
            CustomFields=[new(Guid.NewGuid(),"Process","Milling",new(30,105),70)],
            Notes=[new(Guid.NewGuid(),"1. Deburr all edges\n2. Protect finished surfaces",new(30,122),70)],
            Zones=new(8,6)
        }};
    }
    private static CadDocumentSession Session(DocumentSnapshot snapshot,MemoryAssetStore assets)=>
        new(snapshot,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
    internal static void StripLibraryFieldsForLegacy(string path)=>FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
    {
        var book=MessagePackSerializer.Deserialize<PackDrawingBook>(bytes);
        return MessagePackSerializer.Serialize(book with{Templates=null,Sheets=book.Sheets.Select(s=>s with
            {HoleTables=null,Template=s.Template is {} t?t with{Fields=null,Notes=null,Zones=null}:null}).ToArray()});
    });

    [Fact] public async Task TemplateApplicationPreservesExactViewsAnnotationsAndUndoWhileCopiesAreIndependent()
    {
        var assets=new MemoryAssetStore();await using var session=Session(DocumentSnapshot.Create("Source"),assets);
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
        var source=ContentSheet();var target=DrawingSheetTemplate.Create("Target");
        await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(source));await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(target));
        var view=Guid.NewGuid();await session.ExecuteAsync(TechnicalDrawingCommands.AddView(target.Id,view,"Front",DrawingViewKind.Front,new(80,65),1));
        var body=session.Snapshot.Bodies.Values.Single();var occurrence=session.Snapshot.EnumerateOccurrences().Single();
        using(var shape=OcctGeometryBridge.ReadShape(body.Geometry,assets))using(var map=RepairSnapshot.Create(shape))
        {
            int index=Enumerable.Range(0,map.Topology.Count).First(i=>map.Topology[i].Kind==ShapeKind.Face);
            var reference=await new OcctGeometryKernel().ResolveDrawingTopologyAsync(session.Snapshot,occurrence.Path,body.Id,index,map.Fingerprint,assets);
            await session.ExecuteAsync(DrawingAnnotationCommands.PutDatum(target.Id,new(Guid.NewGuid(),view,"A",reference,new(50,40))));
        }
        var id=Guid.NewGuid();await session.ExecuteAsync(DrawingTemplateCommands.Save(source.Id,id,"Production"));
        var before=session.Snapshot;var old=before.DrawingSheets[target.Id];
        await session.ExecuteAsync(DrawingTemplateCommands.Apply(target.Id,id));var applied=session.Snapshot.DrawingSheets[target.Id];
        Assert.Equal(old.Id,applied.Id);Assert.Equal(old.Name,applied.Name);Assert.Equal(old.Views.ToArray(),applied.Views.ToArray());
        Assert.Equal(old.DatumSymbols.ToArray(),applied.DatumSymbols.ToArray());Assert.Equal(old.Dimensions.ToArray(),applied.Dimensions.ToArray());
        Assert.Equal(old.ControlFrames.ToArray(),applied.ControlFrames.ToArray());Assert.Equal(old.Balloons.ToArray(),applied.Balloons.ToArray());
        Assert.NotEqual(source.Template!.CustomFields[0].Id,applied.Template!.CustomFields[0].Id);
        Assert.NotEqual(before.DrawingTemplates[id].Template.CustomFields[0].Id,applied.Template.CustomFields[0].Id);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();
        await session.ExecuteAsync(DrawingTemplateCommands.Delete(id));Assert.Equal(applied.Template,session.Snapshot.DrawingSheets[target.Id].Template);
        await session.ExecuteAsync(DrawingTemplateCommands.PutField(target.Id,applied.Template.CustomFields[0] with{Value="Turning"},true));
        Assert.Equal("Milling",session.Snapshot.DrawingSheets[source.Id].Template!.CustomFields[0].Value);
    }

    [Fact] public async Task SourceFreeFileImportsIntoAnotherDocumentAndRejectsModelPayload()
    {
        using var files=new TestFiles();var sheet=ContentSheet();var entry=DrawingTemplateDefinition.Capture(sheet,Guid.NewGuid(),"Workshop");
        var path=files.PathFor("workshop.cadtemplate");DrawingTemplateFile.Write(entry,path);
        var read=DrawingTemplateFile.Read(path);Assert.Equal(entry,read);
        var text=File.ReadAllText(path);Assert.DoesNotContain("Sources",text);Assert.DoesNotContain("Asset",text);Assert.DoesNotContain("Views",text);
        var assets=new MemoryAssetStore();await using var destination=Session(DocumentSnapshot.Create("Destination"),assets);
        await destination.ExecuteAsync(DrawingTemplateCommands.Import(read));var imported=destination.Snapshot.DrawingTemplates.Values.Single();
        Assert.NotEqual(entry.Id,imported.Id);Assert.Equal(entry.Template,imported.Template);Assert.Empty(destination.Snapshot.Bodies);
        var target=DrawingSheetTemplate.Create("New drawing");await destination.ExecuteAsync(TechnicalDrawingCommands.AddSheet(target));
        await destination.ExecuteAsync(DrawingTemplateCommands.Apply(target.Id,imported.Id));Assert.Empty(destination.Snapshot.DrawingSheets[target.Id].Views);
        var json=JsonNode.Parse(text)!;json["Template"]!["Sources"]=new JsonArray();File.WriteAllText(path,json.ToJsonString());
        Assert.Throws<InvalidDataException>(()=>DrawingTemplateFile.Read(path));
    }

    [Fact] public async Task ExplicitReplacementNameConflictsAndMissingIdentitiesAreAtomic()
    {
        var assets=new MemoryAssetStore();var sheet=ContentSheet();await using var session=Session(DocumentSnapshot.Create("Library"),assets);
        await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));var id=Guid.NewGuid();
        await session.ExecuteAsync(DrawingTemplateCommands.Save(sheet.Id,id,"Shop"));var before=session.Snapshot;
        foreach(var command in new[]{DrawingTemplateCommands.Save(sheet.Id,id,"Wrong"),DrawingTemplateCommands.Save(sheet.Id,Guid.NewGuid(),"Wrong",true),
            DrawingTemplateCommands.Save(sheet.Id,Guid.NewGuid()," shop "),DrawingTemplateCommands.Apply(sheet.Id,Guid.NewGuid()),DrawingTemplateCommands.Delete(Guid.NewGuid())})
        {await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(command));Assert.Same(before,session.Snapshot);}
        await session.ExecuteAsync(DrawingTemplateCommands.Save(sheet.Id,id,"Renamed",true));Assert.Equal("Renamed",session.Snapshot.DrawingTemplates[id].Name);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
    }

    [Fact] public async Task PhysicalContentEditsAndZonesRoundTripAndPdfKeepsAllAuthoredText()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var sheet=ContentSheet();
        await using var session=Session(DocumentSnapshot.Create("Content"),assets);await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        var field=sheet.Template!.CustomFields[0];var note=sheet.Template.Notes[0];
        await session.ExecuteAsync(DrawingTemplateCommands.PutField(sheet.Id,field with{Value="Grinding",PositionMm=new(30,100)},true));
        await session.ExecuteAsync(DrawingTemplateCommands.PutNote(sheet.Id,note with{Text="去毛刺\nProtect surfaces",PositionMm=new(30,118)},true));
        await session.ExecuteAsync(DrawingTemplateCommands.SetZones(sheet.Id,new(6,4,true,true)));
        var id=Guid.NewGuid();await session.ExecuteAsync(DrawingTemplateCommands.Save(sheet.Id,id,"Custom"));
        var native=files.PathFor("content.cadoryx");await session.SaveAsync(new CadDocumentStorage(),native);
        using var loaded=await new CadDocumentStorage().LoadAsync(native,assets);
        Assert.Equal(session.Snapshot.DrawingSheets[sheet.Id].Template,loaded.Snapshot.DrawingSheets[sheet.Id].Template);
        Assert.Equal(session.Snapshot.DrawingTemplates[id],loaded.Snapshot.DrawingTemplates[id]);
        var layout=DrawingPageLayout.Create(loaded.Snapshot.DrawingSheets[sheet.Id],1,1);
        foreach(string text in new[]{"Process","Grinding","去毛刺","Protect surfaces","1","6","A","D"})Assert.Contains(layout.Labels,l=>l.Text==text);
        Assert.All(layout.Lines.SelectMany(l=>new[]{l.A,l.B}).Concat(layout.Labels.Select(l=>l.Position)),p=>
        {Assert.InRange(p.X,0,sheet.WidthMm);Assert.InRange(p.Y,0,sheet.HeightMm);});
        TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("content.pdf"));
        var before=session.Snapshot;await session.ExecuteAsync(DrawingTemplateCommands.DeleteContent(sheet.Id,field.Id));
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(DrawingTemplateCommands.SetZones(sheet.Id,null));Assert.Null(session.Snapshot.DrawingSheets[sheet.Id].Template!.Zones);
    }

    [Theory][InlineData("overlap")][InlineData("title")][InlineData("outside")][InlineData("id")][InlineData("newline")][InlineData("empty")][InlineData("nan")][InlineData("note-size")][InlineData("zone-count")][InlineData("zone-size")]
    public async Task InvalidContentIsRejectedWithoutPublishingAnyPartialState(string kind)
    {
        var assets=new MemoryAssetStore();var sheet=ContentSheet();await using var session=Session(DocumentSnapshot.Create("Atomic"),assets);
        await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));var before=session.Snapshot;var f=sheet.Template!.CustomFields[0];
        ICadDocumentCommand command=kind switch
        {
            "overlap"=>DrawingTemplateCommands.PutNote(sheet.Id,new(Guid.NewGuid(),"Overlaps",new(31,106),70)),
            "title"=>DrawingTemplateCommands.PutField(sheet.Id,new(Guid.NewGuid(),"Title overlap","X",new(120,150),50)),
            "outside"=>DrawingTemplateCommands.PutNote(sheet.Id,new(Guid.NewGuid(),"Outside",new(280,10),70)),
            "id"=>DrawingTemplateCommands.PutNote(sheet.Id,new(f.Id,"Duplicate",new(30,150),70)),
            "newline"=>DrawingTemplateCommands.PutField(sheet.Id,f with{Value="Line\nTwo"},true),
            "empty"=>DrawingTemplateCommands.PutNote(sheet.Id,new(Guid.NewGuid(),"",new(30,150),70)),
            "nan"=>DrawingTemplateCommands.PutField(sheet.Id,f with{WidthMm=double.NaN},true),
            "note-size"=>DrawingTemplateCommands.PutNote(sheet.Id,new(Guid.NewGuid(),new('X',97),new(30,150),70)),
            "zone-count"=>DrawingTemplateCommands.SetZones(sheet.Id,new(0,6)),
            _=>DrawingTemplateCommands.SetZones(sheet.Id,new(32,6))
        };
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(command));Assert.Same(before,session.Snapshot);
    }

    [Fact] public async Task LegacyV6RetainsExactLayoutAndRejectsEvenEmptyNewFields()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var sheet=DrawingSheetTemplate.Create("Legacy");
        var doc=DocumentSnapshot.Create("Legacy") with{DrawingSheets=ImmutableDictionary<Guid,TechnicalDrawingSheet>.Empty.Add(sheet.Id,sheet)};
        var path=files.PathFor("v6.cadoryx");await new CadDocumentStorage().SaveAsync(doc,assets,path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="drawings"?s with{SchemaVersion=6}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
        StripLibraryFieldsForLegacy(path);using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        Assert.Empty(loaded.Snapshot.DrawingTemplates);Assert.Equal(sheet.Template,loaded.Snapshot.DrawingSheets[sheet.Id].Template);
        var expected=DrawingPageLayout.Create(sheet,1,1);var actual=DrawingPageLayout.Create(loaded.Snapshot.DrawingSheets[sheet.Id],1,1);
        Assert.Equal(expected.Lines.ToArray(),actual.Lines.ToArray());Assert.Equal(expected.Labels.ToArray(),actual.Labels.ToArray());
    }

    [Theory][InlineData("null-library")][InlineData("duplicate-library")][InlineData("null-field")][InlineData("bad-point")][InlineData("bad-zone")][InlineData("missing-frame")]
    public async Task MalformedStoredLibraryAndContentFailBeforeAUsableDocumentIsPublished(string kind)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var sheet=ContentSheet();
        await using var session=Session(DocumentSnapshot.Create("Stored"),assets);await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await session.ExecuteAsync(DrawingTemplateCommands.Save(sheet.Id,Guid.NewGuid(),"Workshop"));var path=files.PathFor(kind+".cadoryx");
        await session.SaveAsync(new CadDocumentStorage(),path);
        FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
        {
            var book=MessagePackSerializer.Deserialize<PackDrawingBook>(bytes);var t=book.Sheets[0].Template!;
            book=kind switch
            {
                "null-library"=>book with{Templates=null},
                "duplicate-library"=>book with{Templates=[book.Templates![0],book.Templates[0]]},
                "missing-frame"=>book with{Templates=[book.Templates![0] with{Template=null!}]},
                _=>book with{Sheets=[book.Sheets[0] with{Template=kind switch
                {"null-field"=>t with{Fields=[null!]},"bad-point"=>t with{Fields=[t.Fields![0] with{Position=[30]}]},_=>t with{Zones=new(33,6,false,false)}}}]}
            };return MessagePackSerializer.Serialize(book);
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
    }

    [Fact] public void InvalidExportKeepsExistingFileAndBadVersionsOrOversizedImportsFail()
    {
        using var files=new TestFiles();var path=files.PathFor("template.cadtemplate");var entry=DrawingTemplateDefinition.Capture(ContentSheet(),Guid.NewGuid(),"Saved");
        DrawingTemplateFile.Write(entry,path);var before=File.ReadAllBytes(path);
        Assert.Throws<CadValidationException>(()=>DrawingTemplateFile.Write(entry with{Name=""},path));Assert.Equal(before,File.ReadAllBytes(path));
        var json=JsonNode.Parse(before)!;json["Version"]=2;File.WriteAllText(path,json.ToJsonString());Assert.Throws<InvalidDataException>(()=>DrawingTemplateFile.Read(path));
        File.WriteAllText(path,new(' ',128*1024+1));Assert.Throws<InvalidDataException>(()=>DrawingTemplateFile.Read(path));
    }
}
