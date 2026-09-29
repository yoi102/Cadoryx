using Cadoryx.Commands;
using Cadoryx.Cli;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using System.IO.Compression;
using System.Collections.Immutable;
using System.Text.Json;
using OcctSharp;
using PdfSharp.Pdf.IO;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class M11TechnicalDrawingTests
{
    [Fact] public async Task NativeProjectionDimensionRefreshUndoRoundtripAndMultipagePdf()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Drawing 零件"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
        var body=Assert.Single(session.Snapshot.Bodies.Values);
        var feature=Assert.Single(session.Snapshot.Features.Values);
        var firstPath=session.Snapshot.EnumerateOccurrences().Single(o=>o.DefinitionId==body.PartId).Path;
        var secondSlot=ComponentSlotId.New();
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(new(session.Snapshot.Id,[]),body.PartId,
            secondSlot,"B",RigidTransform3d.Translate(40,0,0)));
        var secondPath=new OccurrencePath(session.Snapshot.Id,[secondSlot]);
        var sheet=TechnicalDrawingSheet.A4Landscape("Sheet A") with{Title="工程图纸"};
        await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(TechnicalDrawingSheet.A4Landscape("Sheet B")));
        var viewId=Guid.NewGuid();
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,viewId,"Front",DrawingViewKind.Front,
            new(100,95),1));
        var view=Assert.Single(session.Snapshot.DrawingSheets[sheet.Id].Views);
        Assert.NotEmpty(view.Strokes);Assert.Contains(view.Strokes,s=>!s.Hidden);
        Assert.Equal(2,view.Sources.Length);Assert.All(view.Sources,s=>Assert.Equal(feature.Id,s.Feature));
        using var shape=OcctGeometryBridge.ReadShape(body.Geometry,assets);
        using var map=RepairSnapshot.Create(shape);
        var face=map.Topology.First(t=>t.Kind==ShapeKind.Face).Selection.Index;
        var datumA=await kernel.ResolveAssemblyDatumAsync(session.Snapshot,firstPath,body.Id,face,map.Fingerprint,assets);
        var datumB=await kernel.ResolveAssemblyDatumAsync(session.Snapshot,secondPath,body.Id,face,map.Fingerprint,assets);
        var dimension=new TechnicalDrawingDimension(Guid.NewGuid(),viewId,DrawingMeasureKind.Length,
            datumA,datumB,new(120,45),0);
        await session.ExecuteAsync(TechnicalDrawingCommands.AddDimension(sheet.Id,dimension));
        Assert.True(Assert.Single(session.Snapshot.DrawingSheets[sheet.Id].Dimensions).Value>0);
        await session.ExecuteAsync(TechnicalDrawingCommands.SetDimensionTolerance(sheet.Id,dimension.Id,.2,.1));
        Assert.Equal(.2,Assert.Single(session.Snapshot.DrawingSheets[sheet.Id].Dimensions).UpperTolerance);
        var beforeInvalidTolerance=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(
            TechnicalDrawingCommands.SetDimensionTolerance(sheet.Id,dimension.Id,-1,0)));
        Assert.Same(beforeInvalidTolerance,session.Snapshot);
        var current=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(
            TechnicalDrawingCommands.ReselectDimension(sheet.Id,dimension with
            {First=datumA with{Fingerprint=new string('0',64)}})));
        Assert.Same(current,session.Snapshot);
        await session.ExecuteAsync(TechnicalDrawingCommands.EditView(sheet.Id,viewId,new(110,90),.5));
        await session.UndoAsync();Assert.Same(current,session.Snapshot);
        await session.RedoAsync();Assert.Equal(.5,session.Snapshot.DrawingSheets[sheet.Id].Views[0].Scale);
        var saved=files.PathFor("drawing.cadoryx");await session.SaveAsync(new CadDocumentStorage(),saved);
        using(var loaded=await new CadDocumentStorage().LoadAsync(saved,assets))
        {
            var restored=loaded.Snapshot.DrawingSheets[sheet.Id];
            Assert.Equal(2,loaded.Snapshot.DrawingSheets.Count);
            Assert.Equal(.5,Assert.Single(restored.Views).Scale);
            Assert.Equal(datumA.Fingerprint,Assert.Single(restored.Dimensions).First.Fingerprint);
            Assert.Equal(.1,Assert.Single(restored.Dimensions).LowerTolerance);
            var pdf=files.PathFor("drawing.pdf");TechnicalDrawingPdf.Write(loaded.Snapshot,pdf);
            Assert.StartsWith("%PDF",System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(pdf),0,4));
            using var opened=PdfReader.Open(pdf,PdfDocumentOpenMode.Import);
            Assert.Equal(2,opened.PageCount);
            using var output=new StringWriter();using var error=new StringWriter();
            var cliPdf=files.PathFor("drawing-cli.pdf");
            Assert.Equal(0,await CadCommandLine.RunAsync(["drawing-pdf",saved,"--output",cliPdf,"--memory"],output,error));
            using var cliOpened=PdfReader.Open(cliPdf,PdfDocumentOpenMode.Import);
            Assert.Equal(2,cliOpened.PageCount);
        }
        var oldValue=session.Snapshot.DrawingSheets[sheet.Id].Dimensions[0].Value;
        await session.ExecuteAsync(new RecomputeCommand(feature.Id,new BoxRecipe(12,20,30,RigidTransform3d.Identity)));
        var refreshed=session.Snapshot.DrawingSheets[sheet.Id];
        Assert.Null(refreshed.Views[0].StaleReason);
        Assert.Equal(session.Snapshot.Bodies[body.Id].Geometry.AssetId,refreshed.Views[0].Sources[0].Asset);
        Assert.NotNull(refreshed.Dimensions[0].StaleReason);Assert.Equal(oldValue,refreshed.Dimensions[0].Value);
        await session.UndoAsync();Assert.Null(session.Snapshot.DrawingSheets[sheet.Id].Dimensions[0].StaleReason);
        var previousLines=session.Snapshot.DrawingSheets[sheet.Id].Views[0].Strokes;
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Remove(secondPath));
        var missing=session.Snapshot.DrawingSheets[sheet.Id];
        Assert.NotNull(missing.Views[0].StaleReason);
        Assert.Equal(previousLines,missing.Views[0].Strokes);
        Assert.NotNull(missing.Dimensions[0].StaleReason);
        await session.UndoAsync();Assert.Null(session.Snapshot.DrawingSheets[sheet.Id].Views[0].StaleReason);
    }

    [Fact] public void ForgedCurrentSourceAndInvalidHierarchyAreRejected()
    {
        var document=DocumentSnapshot.Create("Test");var sheet=TechnicalDrawingSheet.A4Landscape("A");
        var view=new TechnicalDrawingView(Guid.NewGuid(),"View",DrawingViewKind.Top,null,new(50,50),1,
            [new(new(document.Id,[ComponentSlotId.New()]),BodyId.New(),null,GeometryRevisionId.New(),
                new(new string('a',64)),RigidTransform3d.Identity)],[],null);
        Assert.Throws<CadValidationException>(()=>(document with
        {DrawingSheets=document.DrawingSheets.Add(sheet.Id,sheet with{Views=[view]})}).Validate());
        var cyclic=view with{ParentView=view.Id,StaleReason="Missing source"};
        Assert.Throws<CadValidationException>(()=>(document with
        {DrawingSheets=document.DrawingSheets.Add(sheet.Id,sheet with{Views=[cyclic]})}).Validate());
    }

    [Fact] public async Task FourStandardViewsAndSectionUseNativeGeometry()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Views"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
        var sheet=TechnicalDrawingSheet.A4Landscape("Views");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        foreach(var kind in Enum.GetValues<DrawingViewKind>().Where(kind=>kind!=DrawingViewKind.Detail))
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),kind.ToString(),
                kind,new(55+20*(int)kind,70),.5,sectionNormal:kind==DrawingViewKind.Section?Vector3d.UnitZ:null,
                sectionOffsetMm:kind==DrawingViewKind.Section?15:0));
        var views=session.Snapshot.DrawingSheets[sheet.Id].Views;
        Assert.Equal(5,views.Length);Assert.All(views,v=>Assert.NotEmpty(v.Strokes));
        Assert.All(views,v=>Assert.Null(v.StaleReason));
    }

    [Fact] public async Task ChildViewsFollowParentPlacementAndScaleInOneUndoEntry()
    {
        var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Children"),assets,
            new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Box"));
        var sheet=TechnicalDrawingSheet.A4Landscape("Sheet");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        var parent=Guid.NewGuid();var child=Guid.NewGuid();
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,parent,"Front",DrawingViewKind.Front,new(50,60),1));
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,child,"Top",DrawingViewKind.Top,new(90,60),.5,
            parentView:parent));
        var before=session.Snapshot;
        await session.ExecuteAsync(TechnicalDrawingCommands.EditView(sheet.Id,parent,new(60,70),2));
        var views=session.Snapshot.DrawingSheets[sheet.Id].Views;
        Assert.Equal(new Point2d(140,70),views.Single(v=>v.Id==child).CenterMm);
        Assert.Equal(1,views.Single(v=>v.Id==child).Scale);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
    }

    [Fact] public async Task DetailViewFollowsParentAndCropsExactProjection()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Detail"),assets,
            new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,20,20,RigidTransform3d.Identity),"Box"));
        var sheet=TechnicalDrawingSheet.A4Landscape("Detail sheet");
        await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await session.ExecuteAsync(TechnicalDrawingCommands.EditSheet(sheet.Id,sheet.Name,297,210,
            LengthUnit.Millimeter,sheet.Title,null,DrawingStandard.ASME));
        var parent=Guid.NewGuid();var detail=Guid.NewGuid();
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,parent,"Front",DrawingViewKind.Front,new(75,70),1));
        var parentView=Assert.Single(session.Snapshot.DrawingSheets[sheet.Id].Views);
        var focus=parentView.Strokes.SelectMany(s=>s.Points).First();
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,detail,"A",DrawingViewKind.Detail,
            new(180,70),2,parentView:parent,detailCenter:focus,detailRadius:4));
        var drawing=session.Snapshot.DrawingSheets[sheet.Id];
        Assert.Equal(DrawingStandard.ASME,drawing.Standard);
        Assert.Equal(parentView.Strokes,drawing.Views.Single(v=>v.Id==detail).Strokes);
        var layout=DrawingPageLayout.Create(drawing,1,1);
        Assert.Contains(layout.Labels,l=>l.Text.Contains("third-angle"));
        Assert.Contains(layout.Lines,l=>l.A.X>170&&l.A.X<190&&l.B.X>170&&l.B.X<190);
        var saved=files.PathFor("detail.cadoryx");await session.SaveAsync(new CadDocumentStorage(),saved);
        using(var loaded=await new CadDocumentStorage().LoadAsync(saved,assets))
        {
            var restored=loaded.Snapshot.DrawingSheets[sheet.Id];
            Assert.Equal(DrawingStandard.ASME,restored.Standard);
            Assert.Equal(focus,restored.Views.Single(v=>v.Id==detail).DetailCenter);
            Assert.Equal(4,restored.Views.Single(v=>v.Id==detail).DetailRadius);
            TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("detail.pdf"));
        }
        var feature=Assert.Single(session.Snapshot.Features.Values);
        await session.ExecuteAsync(new RecomputeCommand(feature.Id,new BoxRecipe(22,20,20,RigidTransform3d.Identity)));
        drawing=session.Snapshot.DrawingSheets[sheet.Id];
        Assert.Equal(drawing.Views.Single(v=>v.Id==parent).Strokes,drawing.Views.Single(v=>v.Id==detail).Strokes);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(
            TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Nested",DrawingViewKind.Detail,
                new(200,100),2,parentView:detail,detailCenter:focus,detailRadius:2)));
    }

    [Fact] public async Task LegacyDrawingSectionMigratesOnlyWhenNewFieldsAreAbsent()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        var document=DocumentSnapshot.Create("Legacy drawing");var sheet=TechnicalDrawingSheet.A4Landscape("A");
        document=document with{DrawingSheets=document.DrawingSheets.Add(sheet.Id,sheet)};
        var path=files.PathFor("legacy-drawing.cadoryx");await storage.SaveAsync(document,assets,path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>
            s.Kind=="drawings"?s with{SchemaVersion=1}:s).ToImmutableArray()});
        using var migrated=await storage.LoadAsync(path,assets);
        Assert.Equal(DrawingStandard.ISO,migrated.Snapshot.DrawingSheets[sheet.Id].Standard);
        Assert.Contains(migrated.Diagnostics,d=>d.Code=="IO.MIGRATED");
    }

    [Fact] public async Task VersionNineteenCreatesEmptyDrawingBookButCurrentCannotOmitIt()
    {
        using var files=new TestFiles();var path=files.PathFor("version.cadoryx");
        var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        await storage.SaveAsync(DocumentSnapshot.Create("Older"),assets,path);
        var manifest=FormatEvolutionTests.Manifest(path);
        var drawings=manifest.Sections.Single(s=>s.Kind=="drawings");
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            zip.GetEntry(drawings.Path)!.Delete();zip.GetEntry("manifest.json")!.Delete();
            using var output=zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(output,manifest with{Sections=[..manifest.Sections.Where(s=>s.Kind!="drawings")]},CadJson.Options);
        }
        await Assert.ThrowsAsync<NotSupportedException>(()=>storage.LoadAsync(path,assets));
        FormatEvolutionTests.RewriteManifest(path,m=>m with
        {Sections=[..m.Sections.Select(s=>s.Kind=="document"?s with{SchemaVersion=19}:s)]});
        using var loaded=await storage.LoadAsync(path,assets);
        Assert.Empty(loaded.Snapshot.DrawingSheets);
        Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");
    }
}
