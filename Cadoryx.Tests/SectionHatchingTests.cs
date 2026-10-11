using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using System.Collections.Immutable;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class SectionHatchingTests
{
    [Theory]
    [InlineData(0,0,1,6)]
    [InlineData(0,0,-1,-6)]
    [InlineData(0,1,1,4.242640687119285)]
    public async Task OriginalAnnularMaterialClipsStraightHatchesAndLeavesCavityEmpty(double nx,double ny,double nz,double offset)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using var outer=ShapeFactory.CreateCylinder(10,12);using var hole=ShapeFactory.CreateCylinder(3,12);
        using var ring=outer.Cut(hole);using var geometry=OcctGeometryBridge.StoreShape(ring,assets);
        var input=new GeometryInstance(new(DocumentId.New(),[ComponentSlotId.New()]),BodyId.New(),geometry.Geometry,RigidTransform3d.Identity);
        var normal=new Vector3d(nx,ny,nz).Normalized();
        var projection=await kernel.ProjectAsync([input],DrawingViewKind.Section,normal,offset,assets);
        var hatches=projection.Strokes.Where(s=>s.Kind==DrawingLineKind.SectionHatch).ToArray();
        Assert.True(hatches.Length>5);Assert.Contains(projection.Strokes,s=>s.Kind!=DrawingLineKind.SectionHatch);
        var up=Math.Abs(normal.Z)<.9?Vector3d.UnitZ:new Vector3d(0,1,0);
        var u=up.Cross(normal).Normalized();var v=normal.Cross(u).Normalized();
        Vector3d World(Point2d p)=>normal*offset+u*p.X+v*p.Y;
        foreach(var line in hatches)
        {
            Assert.False(line.Hidden);Assert.False(line.Closed);Assert.Equal(2,line.Points.Length);
            var a=World(line.Points[0]);var b=World(line.Points[1]);var d=b-a;
            var t=Math.Clamp(-(a.X*d.X+a.Y*d.Y)/(d.X*d.X+d.Y*d.Y),0,1);
            var closest=a+d*t;
            Assert.True(closest.X*closest.X+closest.Y*closest.Y>=9-1e-6,"A hatch crosses the cylindrical cavity.");
            foreach(var p in new[]{a,b,(a+b)*.5})
            {
                Assert.InRange(p.Z,-1e-6,12+1e-6);
                Assert.InRange(p.X*p.X+p.Y*p.Y,9-1e-6,100+1e-6);
            }
        }
        using var after=OcctGeometryBridge.StoreShape(ring,assets);
        Assert.Equal(geometry.Geometry.AssetId,after.Geometry.AssetId);
        Assert.Equal(geometry.Geometry.VolumeMm3,after.Geometry.VolumeMm3);
    }

    [Fact]
    public async Task ScaleRebuildRefreshUndoSaveAndLegacyMigrationKeepHatchesAssociated()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Manufacturing section"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,20,12,RigidTransform3d.Identity),"Box"));
        var sheet=TechnicalDrawingSheet.A4Landscape("Section");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        var section=Guid.NewGuid();var detail=Guid.NewGuid();
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,section,"A-A",DrawingViewKind.Section,
            new(75,70),1,sectionNormal:Vector3d.UnitZ,sectionOffsetMm:6));
        int Count()=>session.Snapshot.DrawingSheets[sheet.Id].Views.Single(v=>v.Id==section).Strokes.Count(s=>s.Kind==DrawingLineKind.SectionHatch);
        var initial=session.Snapshot;int initialCount=Count();Assert.True(initialCount>5);
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,detail,"Detail",DrawingViewKind.Detail,
            new(170,70),2,parentView:section,detailCenter:new(10,10),detailRadius:4));
        var beforeScale=session.Snapshot;
        await session.ExecuteAsync(TechnicalDrawingCommands.EditView(sheet.Id,section,new(75,70),.5));
        Assert.True(Count()<initialCount);Assert.Null(session.Snapshot.DrawingSheets[sheet.Id].Views.Single(v=>v.Id==section).StaleReason);
        var detailView=session.Snapshot.DrawingSheets[sheet.Id].Views.Single(v=>v.Id==detail);
        Assert.Equal(session.Snapshot.DrawingSheets[sheet.Id].Views.Single(v=>v.Id==section).Sources,detailView.Sources);
        Assert.True(detailView.Strokes.Count(s=>s.Kind==DrawingLineKind.SectionHatch)>Count());
        await session.UndoAsync();Assert.Same(beforeScale,session.Snapshot);Assert.Equal(initialCount,Count());
        await session.RedoAsync();
        var feature=Assert.Single(session.Snapshot.Features.Values);
        await session.ExecuteAsync(new RecomputeCommand(feature.Id,new BoxRecipe(30,20,12,RigidTransform3d.Identity)));
        var view=session.Snapshot.DrawingSheets[sheet.Id].Views.Single(v=>v.Id==section);
        Assert.Equal(session.Snapshot.Bodies[feature.OutputBodyId].Geometry.Revision,view.Sources[0].Revision);
        Assert.True(Count()>0);Assert.Null(view.StaleReason);
        var path=files.PathFor("section.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using(var loaded=await new CadDocumentStorage().LoadAsync(path,assets))
        {
            var actual=loaded.Snapshot.DrawingSheets[sheet.Id].Views.Single(v=>v.Id==section).Strokes;
            Assert.Equal(view.Strokes.Length,actual.Length);
            for(int i=0;i<actual.Length;i++)
            {
                Assert.Equal(view.Strokes[i].Kind,actual[i].Kind);Assert.Equal(view.Strokes[i].Hidden,actual[i].Hidden);
                Assert.Equal(view.Strokes[i].Closed,actual[i].Closed);Assert.Equal(view.Strokes[i].Points.ToArray(),actual[i].Points.ToArray());
            }
            var layout=DrawingPageLayout.Create(loaded.Snapshot.DrawingSheets[sheet.Id],1,1);
            Assert.Contains(layout.Lines,line=>line.WidthMm==.13&&!line.Hidden);
            TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("section.pdf"));
        }
        // A downgraded cache must never smuggle the new hatch kind through v2.
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>
            s.Kind=="drawings"?s with{SchemaVersion=2}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,assets));

        await session.UndoAsync();await session.UndoAsync();await session.UndoAsync();
        Assert.Same(initial,session.Snapshot);
    }

    [Fact]
    public async Task LegacyUnhatchedSectionRequiresExplicitRefreshAndCancellationDoesNotPublish()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Legacy section"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Box"));
        var sheet=TechnicalDrawingSheet.A4Landscape("A");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        var id=Guid.NewGuid();await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,id,"A-A",DrawingViewKind.Section,
            new(50,50),1,sectionNormal:Vector3d.UnitZ,sectionOffsetMm:5));
        var current=session.Snapshot;var old=current.DrawingSheets[sheet.Id];
        var legacy=current with{DrawingSheets=current.DrawingSheets.SetItem(sheet.Id,old with{Views=old.Views.Select(v=>
            v with{Strokes=v.Strokes.Where(s=>s.Kind!=DrawingLineKind.SectionHatch).ToImmutableArray()}).ToImmutableArray()})};
        var path=files.PathFor("legacy-section.cadoryx");await new CadDocumentStorage().SaveAsync(legacy,assets,path);
        DrawingAnnotationTests.StripNewFieldsForLegacy(path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>
            s.Kind=="drawings"?s with{SchemaVersion=2}:s).ToImmutableArray()});
        using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        Assert.NotNull(loaded.Snapshot.DrawingSheets[sheet.Id].Views[0].StaleReason);
        Assert.Throws<CadValidationException>(()=>TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("stale.pdf")));
        await using var restored=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher());
        await restored.ExecuteAsync(TechnicalDrawingCommands.Refresh());
        Assert.Contains(restored.Snapshot.DrawingSheets[sheet.Id].Views[0].Strokes,s=>s.Kind==DrawingLineKind.SectionHatch);
        Assert.Null(restored.Snapshot.DrawingSheets[sheet.Id].Views[0].StaleReason);
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ExecuteAsync(TechnicalDrawingCommands.Refresh(),cancellation.Token));
        Assert.Same(current,session.Snapshot);
    }

    [Fact]
    public async Task SectionDetailsReclipAtTheirOwnPaperSpacingWhenTheirScaleChanges()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Detail line spacing"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,20,12,RigidTransform3d.Identity),"Box"));
        var sheet=TechnicalDrawingSheet.A4Landscape("A");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        var section=Guid.NewGuid();var detail=Guid.NewGuid();
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,section,"A-A",DrawingViewKind.Section,
            new(50,60),1,sectionNormal:Vector3d.UnitZ,sectionOffsetMm:6));
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,detail,"B",DrawingViewKind.Detail,
            new(170,70),2,parentView:section,detailCenter:new(10,10),detailRadius:4));
        TechnicalDrawingView View(Guid id)=>session.Snapshot.DrawingSheets[sheet.Id].Views.Single(v=>v.Id==id);
        void PaperSpacing(TechnicalDrawingView view)
        {
            var offsets=view.Strokes.Where(s=>s.Kind==DrawingLineKind.SectionHatch)
                .Select(s=>(s.Points[0].Y-s.Points[0].X)*Math.Sqrt(.5)*view.Scale).Order().ToArray();
            Assert.True(offsets.Length>5);
            for(int i=1;i<offsets.Length;i++)Assert.InRange(offsets[i]-offsets[i-1],2.5-1e-8,2.5+1e-8);
        }
        PaperSpacing(View(section));PaperSpacing(View(detail));
        int parentLines=View(section).Strokes.Length,oldDetailLines=View(detail).Strokes.Length;
        var before=session.Snapshot;
        await session.ExecuteAsync(TechnicalDrawingCommands.EditView(sheet.Id,detail,new(170,70),4));
        Assert.True(View(detail).Strokes.Length>oldDetailLines);Assert.Equal(parentLines,View(section).Strokes.Length);
        PaperSpacing(View(detail));Assert.Null(View(detail).StaleReason);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
        await session.RedoAsync();PaperSpacing(View(detail));
    }

    [Fact]
    public async Task MixedCompoundHatchesOnlyClosedSolidsAndPreservesOpenSheetSection()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using var box=ShapeFactory.CreateBox(10,10,10);
        using var sheetWire=ShapeFactory.CreatePolygonWire([new(20,0,0),new(20,10,0),new(20,10,10),new(20,0,10)],true);
        using var sheet=ShapeFactory.CreatePlanarFace(sheetWire);
        using var mixed=ShapeFactory.CreateCompound([box,sheet]);using var stored=OcctGeometryBridge.StoreShape(mixed,assets);
        var input=new GeometryInstance(new(DocumentId.New(),[ComponentSlotId.New()]),BodyId.New(),stored.Geometry,RigidTransform3d.Identity);
        var result=await kernel.ProjectAsync([input],DrawingViewKind.Section,Vector3d.UnitZ,5,assets);
        var hatches=result.Strokes.Where(s=>s.Kind==DrawingLineKind.SectionHatch).ToArray();Assert.NotEmpty(hatches);
        Assert.All(hatches,s=>Assert.All(s.Points,p=>Assert.InRange(p.X,-1e-6,10+1e-6)));
        Assert.Contains(result.Strokes,s=>s.Kind!=DrawingLineKind.SectionHatch&&s.Points.Any(p=>Math.Abs(p.X-20)<1e-6));
        using var sheetStored=OcctGeometryBridge.StoreShape(sheet,assets);
        var unhatched=await kernel.ProjectAsync([input with{Geometry=sheetStored.Geometry}],DrawingViewKind.Section,Vector3d.UnitZ,5,assets);
        Assert.NotEmpty(unhatched.Strokes);Assert.DoesNotContain(unhatched.Strokes,s=>s.Kind==DrawingLineKind.SectionHatch);
    }

    [Fact]
    public async Task InvalidAndExcessiveHatchingRejectBeforePublishingAView()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using var box=ShapeFactory.CreateBox(10,10,10);using var geometry=OcctGeometryBridge.StoreShape(box,assets);
        var input=new GeometryInstance(new(DocumentId.New(),[ComponentSlotId.New()]),BodyId.New(),geometry.Geometry,RigidTransform3d.Identity);
        foreach(var spacing in new[]{0d,-1,double.NaN,double.PositiveInfinity,1e-300,1e-9})
            await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ProjectAsync([input],DrawingViewKind.Section,
                Vector3d.UnitZ,5,assets,sectionHatchSpacingMm:spacing));
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Invalid scale"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Box"));
        var sheet=TechnicalDrawingSheet.A4Landscape("A");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,
            Guid.NewGuid(),"A-A",DrawingViewKind.Section,new(50,50),0,sectionNormal:Vector3d.UnitZ,sectionOffsetMm:5)));
        Assert.Same(before,session.Snapshot);Assert.Empty(session.Snapshot.DrawingSheets[sheet.Id].Views);
    }
}
