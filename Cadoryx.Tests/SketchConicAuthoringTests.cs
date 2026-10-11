using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchConicAuthoringTests
{
    private static SketchEllipseGeometry Ellipse(double radius=5,double sweep=Math.Tau)=>new(new(12,3),new(radius,0),new(0,2),.7,sweep);
    [Theory] [InlineData("circle")] [InlineData("arc")] [InlineData("ellipse")]
    public void ObliqueAuthoredConicProjectionRetainsIntervalAndRejectsEditedCache(string kind)
    {
        var part=DefinitionId.New();var source=new SketchDraft(CadSketch.Create(part,"Source",new(new(2,3,7),Quaterniond.FromAxisAngle(new(1,0,0),.6))));
        if(kind=="circle")source.AddCircle(new(12,3),5);
        else if(kind=="arc")source.AddArc(new(7,3),new(12,8),new(17,3));
        else source.AddEllipse(Ellipse(sweep:-2.8));
        var id=SketchProjectionGeometry.Entities(source.Value).Single();var target=new SketchDraft(CadSketch.Create(part,"Target",RigidTransform3d.Translate(1,0,0)));
        target.Project(source.Value,id);var projection=target.Value.Projections.Single();var e=target.Value.Ellipses.Single();Assert.Empty(projection.Anchors);
        var transform=target.Value.Plane.Inverse()*source.Value.Plane;var original=SketchProjectionGeometry.Conic(source.Value,id);
        foreach(double t in new[]{0d,.13,.37,.89,1})
        {
            var p=original.At(t);var q=transform.Apply(new(p.X,p.Y,0));var actual=e.Geometry.At(t);
            Assert.InRange(double.Hypot(q.X-actual.X,q.Y-actual.Y),0,1e-7);
        }
        SketchProjectionGeometry.ValidateGraph(new Dictionary<SketchId,CadSketch>{{source.Value.Id,source.Value},{target.Value.Id,target.Value}});
        var before=target.Value;Assert.Throws<CadValidationException>(()=>target.EditEllipse(e.Id,Ellipse(9)));Assert.Same(before,target.Value);
        var corrupt=before with{Ellipses=before.Ellipses.Replace(e,e with{Geometry=e.Geometry with{Center=new(100,100)}})};
        Assert.Throws<CadValidationException>(()=>SketchProjectionGeometry.ValidateGraph(new Dictionary<SketchId,CadSketch>{{source.Value.Id,source.Value},{corrupt.Id,corrupt}}));
        target.DetachProjection(e.Id);target.EditEllipse(e.Id,Ellipse(9));Assert.Empty(target.Value.Projections);
        target.Undo();target.Undo();Assert.Same(before,target.Value);target.Redo();Assert.Empty(target.Value.Projections);
        target.RemoveEntities([e.Id]);Assert.Empty(target.Value.Ellipses);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LinkedConicRecomputeNativeModelUndoAndReopenPreserveExactValues(bool finite)
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var source=new SketchDraft(CadSketch.Create(part,"Source",RigidTransform3d.Identity));source.AddEllipse(Ellipse(sweep:finite?2.2:Math.Tau));
        await session.ExecuteAsync(new UpsertSketchCommand(source.Value,solver));var s=session.Snapshot.Sketches[source.Value.Id];
        var target=new SketchDraft(CadSketch.Create(part,"Target",new(new(0,0,10),Quaterniond.FromAxisAngle(new(1,0,0),.4))));target.Project(s,s.Ellipses[0].Id);
        var id=target.Value.Ellipses[0].Id;target.SetConstruction([id],false);await session.ExecuteAsync(new UpsertSketchCommand(target.Value,solver));var t=session.Snapshot.Sketches[target.Value.Id];
        var profile=SketchProfileReference.CreateMixed(t,[id]);var recipe=(ExtrudeRecipe)profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),4,t.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Linked conic solid",part,sketchSource:profile));
        var before=session.Snapshot;var feature=before.Features.Values.Single();double factor=finite?(2.2-Math.Sin(2.2))/2:Math.PI;
        Assert.InRange(Math.Abs(feature.Result.VolumeMm3-5*2*Math.Cos(.4)*factor*4),0,1e-5);
        var changed=new SketchDraft(s);changed.EditEllipse(s.Ellipses[0].Id,Ellipse(7,finite?2.2:Math.Tau));
        await session.ExecuteAsync(new UpsertSketchCommand(changed.Value,solver));var after=session.Snapshot;var output=after.Features[feature.Id];
        Assert.NotEqual(feature.Result.Revision,output.Result.Revision);Assert.False(output.IsStale);
        Assert.InRange(Math.Abs(output.Result.VolumeMm3-7*2*Math.Cos(.4)*factor*4),0,1e-5);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        string file=files.PathFor("conic.cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,file);
        using(var loaded=await storage.LoadAsync(file,assets))
        {
            loaded.Snapshot.Validate();var reopened=loaded.Snapshot.Sketches[t.Id];Assert.Equal(after.Sketches[t.Id].Ellipses[0],reopened.Ellipses[0]);
            Assert.Single(reopened.Projections);var linked=loaded.Snapshot.Features[feature.Id];
            Assert.NotNull(((ExtrudeRecipe)linked.Recipe).Profile.BoundaryCurves[0].Ellipse);
        }
        await session.DisposeAsync();Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task EllipseHolesAndRecursiveIslandsUseNativeRegionValidation()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Regions",RigidTransform3d.Identity));
        foreach(double radius in new[]{10d,6,2})d.AddEllipse(new(new(20,0),new(radius,0),new(0,radius/2),.3,Math.Tau));
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var s=session.Snapshot.Sketches[d.Value.Id];
        var profile=SketchProfileReference.CreateMixed(s,[s.Ellipses[0].Id]) with{Regions=[new(MixedCurveIds:[s.Ellipses[1].Id],Children:[new(MixedCurveIds:[s.Ellipses[2].Id])])]};
        var recipe=(ExtrudeRecipe)profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,s.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Regions",part,sketchSource:profile));
        Assert.InRange(Math.Abs(session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3-(50-18+2)*Math.PI*3),0,1e-5);
        var before=session.Snapshot;var bad=new SketchDraft(s);bad.EditEllipse(s.Ellipses[1].Id,s.Ellipses[1].Geometry with{Center=new(29,0)});
        await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new UpsertSketchCommand(bad.Value,solver)));Assert.Same(before,session.Snapshot);
        await session.DisposeAsync();Assert.Equal(0,assets.Count);
    }
    [Theory] [InlineData("sketches",12)] [InlineData("features",32)]
    public async Task EllipseDataCannotMasqueradeAsLegacySchema(string kind,int version)
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Conic",RigidTransform3d.Identity));d.AddEllipse(Ellipse());await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));
        var s=session.Snapshot.Sketches[d.Value.Id];var p=SketchProfileReference.CreateMixed(s,[s.Ellipses[0].Id]);var r=p.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,s.Plane));
        await session.ExecuteAsync(new AddBodyCommand(r,"Conic",part,sketchSource:p));string file=files.PathFor("legacy.cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,file);int count=assets.Count;
        FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind==kind?s with{SchemaVersion=version}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(file,assets));Assert.Equal(count,assets.Count);
    }
    [Fact] public void ConicInteriorAxisCrossingRejectsEvenWhenBothEndpointsHavePositiveRadius()
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Crossing",RigidTransform3d.Identity));d.AddEllipse(new(new(2,3),new(5,0),new(0,1),-.3,Math.Tau));
        var profile=SketchProfileBuilder.Mixed(d.Value,[d.Value.Ellipses[0].Id]);
        Assert.True(profile.BoundaryCurves[0].Start.X>0);Assert.Throws<CadValidationException>(()=>new RevolveRecipe(profile,Math.Tau,RigidTransform3d.Identity).Validate());
    }
}
