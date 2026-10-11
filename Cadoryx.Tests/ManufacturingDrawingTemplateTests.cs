using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;
using MessagePack;

namespace Cadoryx.Tests;

public sealed class ManufacturingDrawingTemplateTests
{
    [Theory]
    [InlineData(DrawingPaperFormat.A4,297,210)]
    [InlineData(DrawingPaperFormat.A3,420,297)]
    [InlineData(DrawingPaperFormat.A2,594,420)]
    [InlineData(DrawingPaperFormat.A1,841,594)]
    [InlineData(DrawingPaperFormat.A0,1189,841)]
    [InlineData(DrawingPaperFormat.AnsiA,279.4,215.9)]
    [InlineData(DrawingPaperFormat.AnsiB,431.8,279.4)]
    [InlineData(DrawingPaperFormat.AnsiC,558.8,431.8)]
    [InlineData(DrawingPaperFormat.AnsiD,863.6,558.8)]
    [InlineData(DrawingPaperFormat.AnsiE,1117.6,863.6)]
    public void PresetsUsePhysicalDimensionsAndKeepEveryTemplatePrimitiveInsideTheFrame(DrawingPaperFormat paper,double width,double height)
    {
        foreach(bool landscape in new[]{true,false})
        {
            var sheet=DrawingSheetTemplate.Create("A",paper:paper,landscape:landscape);
            Assert.Equal(landscape?width:height,sheet.WidthMm,8);Assert.Equal(landscape?height:width,sheet.HeightMm,8);
            sheet.Validate(DocumentId.New());var layout=DrawingPageLayout.Create(sheet,2,4);
            Assert.Contains(layout.Labels,l=>l.Text=="2/4");
            foreach(var point in layout.Lines.SelectMany(l=>new[]{l.A,l.B}).Concat(layout.Labels.Select(l=>l.Position)))
            {
                Assert.InRange(point.X,20,sheet.WidthMm-10);Assert.InRange(point.Y,10,sheet.HeightMm-10);
            }
        }
    }

    [Fact]
    public async Task EditsPersistMetadataProjectionAndMarginsWithoutMovingModelViewsAndUndoIsExact()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Templates"),assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Box"));
        var sheet=DrawingSheetTemplate.Create("A",language:DrawingCaptionLanguage.Chinese);
        await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(75,60),1));
        var before=session.Snapshot;var oldView=before.DrawingSheets[sheet.Id].Views[0];
        var template=new DrawingSheetTemplate(DrawingPaperFormat.AnsiB,true,DrawingProjectionMethod.ThirdAngle,
            DrawingCaptionLanguage.Japanese,new(22,12,12,15),new("CAD-001","B","Cadoryx","Steel","Ra 1.6","ISO 2768-m",
                "Design","Check","Approve","2026-10-03"));
        await session.ExecuteAsync(TechnicalDrawingCommands.EditSheet(sheet.Id,"A",431.8,279.4,LengthUnit.Inch,
            "製造図面","Author",DrawingStandard.ASME,template));
        var updated=session.Snapshot.DrawingSheets[sheet.Id];Assert.Equal(template,updated.Template);
        Assert.Equal(oldView,updated.Views[0]);var layout=DrawingPageLayout.Create(updated,1,1);
        foreach(var text in new[]{"CAD-001","B","Cadoryx","Steel","Ra 1.6","ISO 2768-m","第三角法","2026-10-03","in"})
            Assert.Contains(layout.Labels,l=>l.Text==text);
        var path=files.PathFor("templates.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using(var loaded=await new CadDocumentStorage().LoadAsync(path,assets))
        {
            Assert.Equal(template,loaded.Snapshot.DrawingSheets[sheet.Id].Template);
            TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("templates.pdf"));
        }
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();
        Assert.Equal(template,session.Snapshot.DrawingSheets[sheet.Id].Template);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>
            s.Kind=="drawings"?s with{SchemaVersion=4}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
    }

    [Fact]
    public async Task LegacyFrameRemainsUnchangedAndNewTemplateCanBeExplicitlyRemoved()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var sheet=TechnicalDrawingSheet.A4Landscape("Legacy");
        var doc=DocumentSnapshot.Create("Legacy") with{DrawingSheets=ImmutableDictionary<Guid,TechnicalDrawingSheet>.Empty.Add(sheet.Id,sheet)};
        var layout=DrawingPageLayout.Create(sheet,1,1);var path=files.PathFor("v4.cadoryx");
        await new CadDocumentStorage().SaveAsync(doc,assets,path);
        DrawingAnnotationTests.StripNewFieldsForLegacy(path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>
            s.Kind=="drawings"?s with{SchemaVersion=4}:s).ToImmutableArray()});
        using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        Assert.Null(loaded.Snapshot.DrawingSheets[sheet.Id].Template);
        var actual=DrawingPageLayout.Create(loaded.Snapshot.DrawingSheets[sheet.Id],1,1);
        Assert.Equal(layout.Lines.ToArray(),actual.Lines.ToArray());Assert.Equal(layout.Labels.ToArray(),actual.Labels.ToArray());
        await using var session=new CadDocumentSession(loaded.Snapshot,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(TechnicalDrawingCommands.EditSheet(sheet.Id,sheet.Name,297,210,sheet.Unit,sheet.Title,null,
            template:DrawingSheetTemplate.Create("A").Template));
        Assert.NotNull(session.Snapshot.DrawingSheets[sheet.Id].Template);
        await session.ExecuteAsync(TechnicalDrawingCommands.EditSheet(sheet.Id,sheet.Name,297,210,sheet.Unit,sheet.Title,null,useLegacyTemplate:true));
        Assert.Null(session.Snapshot.DrawingSheets[sheet.Id].Template);
    }

    [Fact]
    public void InvalidPaperMarginsDatesAndOversizedFieldsAreRejected()
    {
        var sheet=DrawingSheetTemplate.Create("A");var t=sheet.Template!;
        foreach(var invalid in new[]{sheet with{WidthMm=300},sheet with{Template=t with{Margins=new(double.NaN)}},
            sheet with{Template=t with{Margins=new(100,100,100,100)}},
            sheet with{Template=t with{TitleBlock=new(IssueDate:"2026-02-30")}},
            sheet with{Template=t with{TitleBlock=new(DrawingNumber:new('W',29))}},
            sheet with{Title=new('文',97)},sheet with{Template=t with{TitleBlock=new(Organization:"Line\nTwo")}}})
            Assert.Throws<CadValidationException>(()=>invalid.Validate(DocumentId.New()));
        var custom=sheet with{WidthMm=350,Template=t with{Paper=DrawingPaperFormat.Custom,Margins=new(25,12,15,15)}};
        custom.Validate(DocumentId.New());
    }

    [Fact]
    public async Task MalformedNewPackedFieldsProduceFormatErrorsBeforePublishingADocument()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Untrusted fields"),assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Box"));
        var sheet=DrawingSheetTemplate.Create("A");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"A-A",DrawingViewKind.Section,
            new(60,70),1,sectionNormal:Vector3d.UnitZ,sectionOffsetMm:5));
        foreach(var mutation in new[]{"null-rule","bad-margins","null-title"})
        {
            var path=files.PathFor(mutation+".cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
            FormatEvolutionTests.RewriteSection(path,"drawings",bytes=>
            {
                var book=MessagePackSerializer.Deserialize<PackDrawingBook>(bytes);var packed=book.Sheets[0];
                if(mutation=="null-rule")packed=packed with{Views=packed.Views.Select(v=>v with
                    {Hatching=new(new(45,2.5,true),true,[null!])}).ToArray()};
                else packed=packed with{Template=mutation=="bad-margins"?
                    packed.Template! with{Margins=[20,10]}:packed.Template! with{TitleBlock=null!}};
                return MessagePackSerializer.Serialize(book with{Sheets=[packed]});
            });
            await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
        }
    }
}
