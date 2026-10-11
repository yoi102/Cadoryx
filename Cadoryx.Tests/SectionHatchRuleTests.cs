using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SectionHatchRuleTests
{
    [Fact]
    public async Task ExplicitMaterialRulesFollowAssignmentsAndPersistWithoutChangingGeometry()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Material hatching"),assets,kernel,new InlineSessionDispatcher());
        var metal=new CadMaterial(MaterialId.New(),"Steel",7.85e-6);
        await session.ExecuteAsync(ResourceCommands.AddMaterial(metal));
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,20,12,RigidTransform3d.Identity),"First",material:metal.Id));
        var first=Assert.Single(session.Snapshot.Bodies.Values);
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,20,12,RigidTransform3d.Translate(40,0,0)),"Second"));
        var second=session.Snapshot.Bodies.Values.Single(b=>b.Id!=first.Id);
        var sheet=TechnicalDrawingSheet.A4Landscape("A");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        var section=Guid.NewGuid();var detail=Guid.NewGuid();
        var settings=new DrawingSectionHatching(new(30,2),false)
            {Materials=[new(metal.Id,new(0,4))]};
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,section,"A-A",DrawingViewKind.Section,
            new(70,80),2,sectionNormal:Vector3d.UnitZ,sectionOffsetMm:6,hatching:settings));
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,detail,"B",DrawingViewKind.Detail,
            new(180,80),4,parentView:section,detailCenter:new(10,10),detailRadius:5));
        TechnicalDrawingView View(Guid id)=>session.Snapshot.DrawingSheets[sheet.Id].Views.Single(v=>v.Id==id);
        void Family(TechnicalDrawingView view,bool allHorizontal)
        {
            var lines=view.Strokes.Where(s=>s.Kind==DrawingLineKind.SectionHatch).ToArray();Assert.NotEmpty(lines);
            foreach(var line in lines)
            {
                var a=line.Points[0];var b=line.Points[1];
                if(allHorizontal||Math.Max(a.X,b.X)<30)
                {
                    Assert.InRange(Math.Abs(b.Y-a.Y),0,1e-8);
                    double grid=a.Y*view.Scale/4;Assert.InRange(Math.Abs(grid-Math.Round(grid)),0,1e-8);
                }
                else
                {
                    Assert.InRange(Math.Abs((b.Y-a.Y)-Math.Tan(Math.PI/6)*(b.X-a.X)),0,1e-8);
                    double grid=(-a.X*.5+a.Y*Math.Sqrt(.75))*view.Scale/2;
                    Assert.InRange(Math.Abs(grid-Math.Round(grid)),0,1e-8);
                }
            }
        }
        Family(View(section),false);Family(View(detail),false);var before=session.Snapshot;
        await session.ExecuteAsync(DocumentEdits.SetBodyProperties(second.Id,second.Name,second.Appearance,
            second.IsVisible,second.LayerId,metal.Id));
        Family(View(section),true);Family(View(detail),true);Assert.Null(View(detail).StaleReason);
        Assert.Equal(first.Geometry.AssetId,session.Snapshot.Bodies[first.Id].Geometry.AssetId);
        Assert.Equal(second.Geometry.AssetId,session.Snapshot.Bodies[second.Id].Geometry.AssetId);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);Family(View(section),false);
        await session.RedoAsync();Family(View(section),true);
        var path=files.PathFor("rules.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using(var loaded=await new CadDocumentStorage().LoadAsync(path,assets))
        {
            var actual=loaded.Snapshot.DrawingSheets[sheet.Id].Views.Single(v=>v.Id==section).Hatching!;
            Assert.Equal(settings.Default,actual.Default);Assert.False(actual.AlternateSources);
            Assert.Equal(settings.Materials.ToArray(),actual.Materials.ToArray());
        }
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>
            s.Kind=="drawings"?s with{SchemaVersion=3}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
    }

    [Fact]
    public async Task DisabledRulesKeepExactContoursAndUndoRestoresHatches()
    {
        var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Disabled hatching"),assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Box"));
        var sheet=TechnicalDrawingSheet.A4Landscape("A");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        var id=Guid.NewGuid();await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,id,"A-A",DrawingViewKind.Section,
            new(60,70),1,sectionNormal:Vector3d.UnitZ,sectionOffsetMm:5));
        var before=session.Snapshot;
        await session.ExecuteAsync(TechnicalDrawingCommands.SetSectionHatching(sheet.Id,id,new(new DrawingHatchStyle(75,3,false))));
        var view=session.Snapshot.DrawingSheets[sheet.Id].Views[0];Assert.Null(view.StaleReason);
        Assert.NotEmpty(view.Strokes);Assert.DoesNotContain(view.Strokes,s=>s.Kind==DrawingLineKind.SectionHatch);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
        Assert.Contains(session.Snapshot.DrawingSheets[sheet.Id].Views[0].Strokes,s=>s.Kind==DrawingLineKind.SectionHatch);
        foreach(var settings in new[]{new DrawingSectionHatching(new(double.NaN,2)),new(new(-1,2)),
            new(new(180,2)),new(new(45,0)),new(new(45,101)),
            new(new(45,2)){Materials=[new(MaterialId.New(),new(0,2))]}})
        {
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(TechnicalDrawingCommands.SetSectionHatching(sheet.Id,id,settings)));
            Assert.Same(before,session.Snapshot);
        }
    }

    [Fact]
    public async Task VersionThreeCacheMigratesWithoutImplicitReprojection()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("V3"),assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Box"));
        var sheet=TechnicalDrawingSheet.A4Landscape("A");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"A-A",DrawingViewKind.Section,
            new(60,70),1,sectionNormal:Vector3d.UnitZ,sectionOffsetMm:5));
        var path=files.PathFor("v3.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        DrawingAnnotationTests.StripNewFieldsForLegacy(path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>
            s.Kind=="drawings"?s with{SchemaVersion=3}:s).ToImmutableArray()});
        using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        var old=session.Snapshot.DrawingSheets[sheet.Id].Views[0];var view=loaded.Snapshot.DrawingSheets[sheet.Id].Views[0];
        Assert.Null(view.Hatching);Assert.Null(view.StaleReason);Assert.Equal(old.Strokes.Length,view.Strokes.Length);
        for(int i=0;i<old.Strokes.Length;i++)Assert.Equal(old.Strokes[i].Points.ToArray(),view.Strokes[i].Points.ToArray());
        Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");
    }
}
