using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchRegionOffsetTests
{
    private static SketchDraft Draft()=>new(CadSketch.Create(DefinitionId.New(),"Regions",RigidTransform3d.Identity));
    private static SketchProfileReference Reference(CadSketch sketch,SketchRegionReference root)=>
        new(sketch.Id,sketch.Revision,root.PolygonLineIds,root.CircleId){MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};
    private static SketchEntityId[] Nested(SketchDraft d)
    {
        d.AddRectangle(new(0,0),new(100,80));var outer=d.Value.Lines[0].Id;
        d.AddCircle(new(50,40),20);var hole=d.Value.Circles[^1].Id;
        d.AddRectangle(new(40,30),new(60,50));var island=d.Value.Lines[^4].Id;
        d.AddCircle(new(50,40),4);return [outer,hole,island,d.Value.Circles[^1].Id];
    }
    [Theory] [InlineData(1,false)] [InlineData(-1,false)] [InlineData(1,true)] [InlineData(-1,true)]
    public async Task NestedMaterialUsesParityIndependentOfSeedOrderAndWinding(double distance,bool reverse)
    {
        var (initial,part)=SketchCommandTests.Document();var d=new SketchDraft(CadSketch.Create(part,"Nested",RigidTransform3d.Identity));var seeds=Nested(d);
        if(reverse)d.Change(s=>s with{Lines=s.Lines.Select(l=>l with{Start=l.End,End=l.Start}).ToImmutableArray()});
        var before=d.Value;var result=d.OffsetRegions(reverse?seeds.Reverse():seeds,distance);
        Assert.Single(result.Roots);Assert.Equal(new[]{0,1,2,3},result.Boundaries.Select(b=>b.Depth).Order().ToArray());
        Assert.Equal(before.Constraints,result.Sketch.Constraints);Assert.Equal(before.Points.ToArray(),result.Sketch.Points.Take(before.Points.Length).ToArray());
        Assert.Equal(20-distance,result.Sketch.Circles.Single(c=>c.Id==result.Boundaries.Single(b=>b.Source.CircleId==seeds[1]).Created.CircleId).Radius);
        Assert.Equal(4-distance,result.Sketch.Circles.Single(c=>c.Id==result.Boundaries.Single(b=>b.Source.CircleId==seeds[3]).Created.CircleId).Radius);
        var snapshot=initial with{Sketches=initial.Sketches.Add(result.Sketch.Id,result.Sketch)};
        var recipe=Reference(result.Sketch,result.Roots[0]).Resolve(snapshot,part,new ExtrudeRecipe(new([]),3,result.Sketch.Plane));
        using var body=await new OcctGeometryKernel().EvaluateAsync(recipe,new MemoryAssetStore());
        double area=(100+2*distance)*(80+2*distance)-Math.PI*Math.Pow(20-distance,2)+Math.Pow(20+2*distance,2)-Math.PI*Math.Pow(4-distance,2);
        // Native mass integration has numerical tolerance even though the authored boundaries are analytical.
        Assert.InRange(Math.Abs(area*3-body.Geometry.VolumeMm3),0,area*3*1e-8);
        d.Undo();Assert.Same(before,d.Value);d.Redo();Assert.Same(result.Sketch,d.Value);
    }
    [Fact] public void DuplicateSeedsDeduplicateOnlyTheExplicitBoundaries()
    {
        var d=Draft();var seeds=Nested(d);var before=d.Value;
        var result=d.OffsetRegions(new[]{seeds[0],before.Lines[1].Id,seeds[0]},2);
        Assert.Single(result.Boundaries);Assert.Single(result.Roots);Assert.Empty(result.Roots[0].Children);
        Assert.Equal(before.Circles,result.Sketch.Circles);Assert.Equal(0,result.Boundaries[0].Depth);
        d.Undo();result=d.OffsetRegions(new[]{seeds[1]},2);
        Assert.Equal(22,result.Sketch.Circles[^1].Radius);Assert.Equal(0,result.Boundaries[0].Depth);
    }
    [Theory] [InlineData(1)] [InlineData(-1)]
    public async Task MixedArcBoundaryAndSeparateRootsProduceExactNativeSolids(double distance)
    {
        var (initial,part)=SketchCommandTests.Document();var d=new SketchDraft(CadSketch.Create(part,"Mixed",RigidTransform3d.Identity));
        d.AddRectangle(new(0,0),new(40,20));var top=d.Value.Lines[2];d.RemoveEntities(new[]{top.Id});
        d.AddArc(new(40,20),new(20,40),new(0,20),existingStart:top.Start,existingEnd:top.End);var arc=d.Value.Arcs[0].Id;
        d.AddCircle(new(20,20),5);var hole=d.Value.Circles[^1].Id;d.AddCircle(new(100,0),10);var separate=d.Value.Circles[^1].Id;
        var result=d.OffsetRegions(new[]{hole,separate,arc},distance);Assert.Equal(2,result.Roots.Length);
        var snapshot=initial with{Sketches=initial.Sketches.Add(d.Value.Id,d.Value)};
        foreach(var root in result.Roots)
        {
            var recipe=Reference(d.Value,root).Resolve(snapshot,part,new ExtrudeRecipe(new([]),2,d.Value.Plane));
            using var body=await new OcctGeometryKernel().EvaluateAsync(recipe,new MemoryAssetStore());
            double area=root.CircleId is not null?Math.PI*Math.Pow(10+distance,2):
                (40+2*distance)*(20+distance)+Math.PI*Math.Pow(20+distance,2)/2-Math.PI*Math.Pow(5-distance,2);
            Assert.Equal(area*2,body.Geometry.VolumeMm3,5);
        }
    }
    [Theory] [InlineData(0)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)]
    public void InvalidDistanceKeepsDraftAndHistory(double distance)
    {
        var d=Draft();d.AddCircle(new(0,0),4);var before=d.Value;
        var error=Assert.Throws<CadValidationException>(()=>d.OffsetRegions(new[]{before.Circles[0].Id},distance));
        Assert.Equal("RegionOffsetDistance",error.Data[SketchCurveFailure.ResourceKey]);Assert.Same(before,d.Value);Assert.False(d.CanRedo);
        d.Undo();Assert.Empty(d.Value.Circles);
    }
    [Fact] public void TouchOverlapCollapseAndContainmentChangesRejectTheWholeOperation()
    {
        var touch=Draft();touch.AddRectangle(new(0,0),new(10,10));var first=touch.Value.Lines[0].Id;
        touch.AddCircle(new(5,5),5);var before=touch.Value;
        Assert.Throws<CadValidationException>(()=>touch.OffsetRegions(new[]{first,before.Circles[0].Id},1));Assert.Same(before,touch.Value);
        var merge=Draft();merge.AddCircle(new(0,0),4);merge.AddCircle(new(9,0),4);before=merge.Value;
        Assert.Throws<CadValidationException>(()=>merge.OffsetRegions(before.Circles.Select(c=>c.Id),1));Assert.Same(before,merge.Value);
        var nesting=Draft();nesting.AddRectangle(new(0,0),new(10,10));nesting.AddCircle(new(5,5),1);before=nesting.Value;
        Assert.Throws<CadValidationException>(()=>nesting.OffsetRegions(new[]{before.Lines[0].Id,before.Circles[0].Id},-4.8));Assert.Same(before,nesting.Value);
        var collapse=Draft();collapse.AddCircle(new(0,0),10);collapse.AddCircle(new(0,0),1);before=collapse.Value;
        Assert.Throws<CadValidationException>(()=>collapse.OffsetRegions(before.Circles.Select(c=>c.Id),2));Assert.Same(before,collapse.Value);
    }
    [Fact] public void ForeignOpenBranchedConstructionAndFreeCurveSelectionsReject()
    {
        var d=Draft();d.AddRectangle(new(0,0),new(10,10));var seed=d.Value.Lines[0].Id;
        d.AddLine(new(0,0),new(-5,0),start:d.Value.Lines[0].Start);d.AddCircle(new(30,0),4,construction:true);
        var before=d.Value;
        foreach(var id in new[]{seed,before.Lines[^1].Id,before.Circles[0].Id,before.Points[0].Id,SketchEntityId.New()})
        {Assert.Throws<CadValidationException>(()=>d.OffsetRegions(new[]{id},1));Assert.Same(before,d.Value);}
        var free=Draft();free.AddRectangle(new(0,0),new(10,10));var top=free.Value.Lines[2];free.RemoveEntities(new[]{top.Id});
        free.AddBezier(new(10,10),new(5,15),new(0,10),existingStart:top.Start,existingEnd:top.End);before=free.Value;
        Assert.Throws<CadValidationException>(()=>free.OffsetRegions(new[]{before.Beziers[0].Id},1));Assert.Same(before,free.Value);
        Assert.Throws<CadValidationException>(()=>free.OffsetRegions(Array.Empty<SketchEntityId>(),1));
    }
    [Fact] public void BoundaryAndDepthLimitsRejectBeforePublishing()
    {
        var d=Draft();for(int i=0;i<65;i++)d.AddCircle(new(i*10,0),2);var before=d.Value;
        var error=Assert.Throws<CadValidationException>(()=>d.OffsetRegions(before.Circles.Select(c=>c.Id),.1));
        Assert.Equal("RegionOffsetLimit",error.Data[SketchCurveFailure.ResourceKey]);Assert.Same(before,d.Value);
        var nested=Draft();for(int i=0;i<14;i++)nested.AddCircle(new(0,0),100-i*5);before=nested.Value;
        error=Assert.Throws<CadValidationException>(()=>nested.OffsetRegions(before.Circles.Select(c=>c.Id),.1));
        Assert.Equal("RegionOffsetLimit",error.Data[SketchCurveFailure.ResourceKey]);Assert.Same(before,nested.Value);
    }
    [Fact] public void MaximumBoundaryAndDepthBudgetsKeepExactAuthors()
    {
        var d=Draft();for(int i=0;i<64;i++)d.AddCircle(new(i*10,0),2);
        var result=d.OffsetRegions(d.Value.Circles.Select(c=>c.Id),.1);Assert.Equal(64,result.Roots.Length);Assert.Equal(64,result.Boundaries.Length);
        var nested=Draft();for(int i=0;i<13;i++)nested.AddCircle(new(0,0),100-i*5);
        result=nested.OffsetRegions(nested.Value.Circles.Select(c=>c.Id),.1);Assert.Equal(12,result.Boundaries.Max(b=>b.Depth));
        var seeds=result.Boundaries.Select(b=>b.Created.CircleId!.Value).Reverse().ToArray();
        var root=SketchRegionOffset.SelectedRegions(result.Sketch,seeds).Single();
        Assert.Equal(result.Roots[0].CircleId,root.CircleId);Assert.Equal(result.Roots[0].Children[0].CircleId,root.Children[0].CircleId);
    }
    [Fact] public async Task ExplicitOffsetRegionReferencesRecomputeAndReopenWithTheSameAuthors()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Linked",RigidTransform3d.Identity));var seeds=Nested(d);var result=d.OffsetRegions(seeds,1);
        await session.ExecuteAsync(new UpsertSketchCommand(result.Sketch,solver));var sketch=session.Snapshot.Sketches[d.Value.Id];
        var created=result.Boundaries.Select(b=>b.Created.CircleId??b.Created.PolygonLineIds[0]).Reverse();
        var root=SketchRegionOffset.SelectedRegions(sketch,created).Single();var reference=Reference(sketch,root);
        await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,sketch.Plane)),"Offset material",part,sketchSource:reference));
        var baseline=session.Snapshot;var feature=baseline.Features.Values.Single();var hole=root.Children[0].CircleId!.Value;
        var changed=sketch with{Circles=sketch.Circles.Select(c=>c.Id==hole?c with{Radius=c.Radius-1}:c).ToImmutableArray()};
        await session.ExecuteAsync(new UpsertSketchCommand(changed,solver));var after=session.Snapshot;var updated=after.Features[feature.Id];
        Assert.InRange(Math.Abs(updated.Result.VolumeMm3-feature.Result.VolumeMm3-3*Math.PI*(19*19-18*18)),0,.001);
        Assert.Equal(hole,updated.SketchSource!.Regions[0].CircleId);Assert.False(updated.IsStale);after.Validate();
        var path=files.PathFor("linked-offset.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using(var loaded=await new CadDocumentStorage().LoadAsync(path,assets))
        {loaded.Snapshot.Validate();Assert.Equal(hole,loaded.Snapshot.Features[feature.Id].SketchSource!.Regions[0].CircleId);}
        await session.UndoAsync();Assert.Same(baseline,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
    }
    [Fact] public async Task ProductionCommandPreviewCancelConfirmUndoAndStorageRemainAtomic()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Source",RigidTransform3d.Identity));var seeds=Nested(d);
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var sketch=session.Snapshot.Sketches[d.Value.Id];var baseline=session.Snapshot;
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());SketchEntityId[] createdSeeds=[];
        try
        {
            await using(var cancel=new SketchEditorViewModel(document,kernel,solver,sketch,false){Tool="OffsetRegions",OffsetDistance=1})
            {
                foreach(var seed in seeds)cancel.Select(seed,true);cancel.ApplyRegionOffsetCommand.Execute(null);
                Assert.Equal(sketch.Lines.Length*2,cancel.Sketch.Lines.Length);await cancel.PreviewCommand.ExecuteAsync(null);
                Assert.True(cancel.CanConfirm,cancel.Status);Assert.Same(baseline,session.Snapshot);cancel.CancelCommand.Execute(null);
            }
            await using var editor=new SketchEditorViewModel(document,kernel,solver,sketch,false){Tool="OffsetRegions",OffsetDistance=1};
            foreach(var seed in seeds)editor.Select(seed,true);editor.ApplyRegionOffsetCommand.Execute(null);
            createdSeeds=editor.SelectedEntities.ToArray();
            await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        }
        finally{document.Detach();}
        var after=session.Snapshot;var committed=after.Sketches[sketch.Id];
        var regions=SketchRegionOffset.Create(committed,createdSeeds,.25); // copied explicit output selection remains editable
        Assert.Single(regions.Roots);Assert.Equal(4,regions.Boundaries.Length);
        var path=files.PathFor("region-offset.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using(var loaded=await new CadDocumentStorage().LoadAsync(path,assets))
        {Assert.Equal(committed.Lines.ToArray(),loaded.Snapshot.Sketches[sketch.Id].Lines.ToArray());Assert.Equal(committed.Circles.ToArray(),loaded.Snapshot.Sketches[sketch.Id].Circles.ToArray());loaded.Snapshot.Validate();}
        await session.UndoAsync();Assert.Same(baseline,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
    }
}
