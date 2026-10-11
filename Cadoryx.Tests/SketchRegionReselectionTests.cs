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

public sealed partial class SketchRegionReselectionTests
{
    private static readonly OcctGeometryKernel Kernel=new();
    private static (CadSketch Sketch,Guid Group,ImmutableArray<ImmutableArray<SketchEntityId>> Slots) Circles(bool nested=false)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Reselection",RigidTransform3d.Identity));
        foreach(double radius in nested?new[]{8d,6d,4d,2d}:new[]{3d})d.AddCircle(new(0,0),radius);
        var originals=d.Value.Circles.Select(c=>c.Id).ToArray();
        foreach(double radius in nested?new[]{9d,7d,5d,3d}:new[]{5d})d.AddCircle(new(20,0),radius);
        var replacements=d.Value.Circles.Skip(originals.Length).Select(c=>ImmutableArray.Create(c.Id)).ToImmutableArray();
        var result=SketchRegionOffsetAuthoring.Create(d.Value,originals,.2,SketchRegionAuthorJoin.Miter,Kernel);
        return(result.Sketch,result.Group,replacements);
    }
    [Theory][InlineData(SketchRegionAuthorJoin.Miter,.25)][InlineData(SketchRegionAuthorJoin.Round,.25)][InlineData(SketchRegionAuthorJoin.Bevel,.25)]
    [InlineData(SketchRegionAuthorJoin.Miter,-.25)][InlineData(SketchRegionAuthorJoin.Round,-.25)][InlineData(SketchRegionAuthorJoin.Bevel,-.25)]
    public void ExplicitRectangleSlotMapChangesSourcesAndPreservesCompleteOutputIdentities(SketchRegionAuthorJoin join,double distance)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Rectangles",RigidTransform3d.Identity));d.AddRectangle(new(0,0),new(8,4));
        var original=d.Value.Lines[0].Id;d.AddRectangle(new(20,0),new(30,6));var replacement=d.Value.Lines.Skip(4).Select(l=>l.Id).ToImmutableArray();
        var authored=SketchRegionOffsetAuthoring.Create(d.Value,[original],distance,join,Kernel);var s=authored.Sketch;
        var editor=new SketchDraft(s,Kernel);editor.ReselectRegionSources(authored.Group,[replacement],Kernel);var after=editor.Value;
        Assert.Equal(s.RegionOffsets[0].Id,after.RegionOffsets[0].Id);Assert.False(after.RegionOffsets[0].IsFrozen);
        Assert.Equal(replacement.AsEnumerable(),after.RegionOffsets[0].Sources[0].Select(v=>v.Entity));
        Assert.Equal(s.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)),after.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)));
        Assert.Equal(s.Points.Select(p=>p.Id),after.Points.Select(p=>p.Id));Assert.Equal(s.Constraints,after.Constraints);
        Assert.All(after.ExpressionCurves,c=>Assert.True(c.Geometry.At(.5).X>15));Kernel.ConfirmExpressionRegions(after);SketchAssociativeEndpoints.RequireSolved(after);
        editor.Undo();Assert.Same(s,editor.Value);Assert.False(editor.CanUndo);editor.Redo();Assert.Same(after,editor.Value);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void FrozenCompleteCircleMapResumesAllLoopsAndRetainsTheirExplicitContainment(bool nested)
    {
        var(s,id,slots)=Circles(nested);s=s with{RegionOffsets=[s.RegionOffsets[0] with{IsFrozen=true,FrozenReason="Explicit last value"}]};
        var d=new SketchDraft(s,Kernel);d.ReselectRegionSources(id,slots,Kernel);var after=d.Value;
        Assert.False(after.RegionOffsets[0].IsFrozen);Assert.Null(after.RegionOffsets[0].FrozenReason);
        Assert.Equal(s.RegionOffsets[0].Loops.Select(l=>(l.Depth,l.Parent,l.InputTraversalReversed)),after.RegionOffsets[0].Loops.Select(l=>(l.Depth,l.Parent,l.InputTraversalReversed)));
        Assert.Equal(s.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)),after.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)));
        Assert.All(after.ExpressionCurves,c=>Assert.True(c.Geometry.At(.5).X>8));Kernel.ConfirmExpressionRegions(after);
        d.Undo();Assert.Same(s,d.Value);d.Redo();Assert.Same(after,d.Value);
    }
    [Theory][InlineData("default")][InlineData("missing_loop")][InlineData("missing_slot")][InlineData("foreign")][InlineData("self")][InlineData("duplicate")]
    public void UnprovableMapsLeaveTheWholeDraftAndHistoryUnchanged(string fault)
    {
        var(s,id,slots)=Circles(fault=="duplicate");var d=new SketchDraft(s,Kernel);
        slots=fault switch
        {
            "default"=>default,"missing_loop"=>[],"missing_slot"=>[[]],"foreign"=>[[SketchEntityId.New()]],
            "self"=>[[s.ExpressionCurves[0].Id]],_=>slots.Select(_=>slots[0]).ToImmutableArray()
        };
        Assert.Throws<CadValidationException>(()=>d.ReselectRegionSources(id,slots,Kernel));Assert.Same(s,d.Value);Assert.False(d.CanUndo);Assert.False(d.CanRedo);Kernel.ConfirmExpressionRegions(s);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExplicitNewCurveFamilyAndDirectionKeepOnlyProvedAuthorRoles(bool clockwise)
    {
        var(s,id,_)=Circles();var d=new SketchDraft(s,Kernel);
        d.AddEllipse(new(new(20,0),new(5,0),new(0,2),0,clockwise?-Math.Tau:Math.Tau));var source=d.Value.Ellipses[0].Id;var before=d.Value;
        d.ReselectRegionSources(id,[[source]],Kernel);var after=d.Value;
        Assert.Equal(SketchExpressionSourceKind.Ellipse,after.RegionOffsets[0].Sources[0][0].Kind);
        Assert.Equal(before.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)),after.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)));
        Kernel.ConfirmExpressionRegions(after);SketchAssociativeEndpoints.RequireSolved(after);
        var(doc,part)=SketchCommandTests.Document();after=after with{PartId=part};doc=doc with{Sketches=doc.Sketches.Add(after.Id,after)};
        var root=Assert.Single(SketchRegionAuthorModel.Roots(after,id));var profile=new SketchProfileReference(after.Id,after.Revision,[]){MixedBoundaryIds=root.MixedCurveIds};
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(doc,part,new ExtrudeRecipe(new([]),2,after.Plane)),"Mapped ellipse",part,sketchSource:profile));
        const int count=16384;double step=Math.Tau/count,sum=0;
        for(int i=0;i<=count;i++){double t=i*step;sum+=(i==0||i==count?1:i%2==0?2:4)*Math.Sqrt(25*Math.Sin(t)*Math.Sin(t)+4*Math.Cos(t)*Math.Cos(t));}
        double area=10*Math.PI+.2*sum*step/3+.04*Math.PI;
        Assert.InRange(Math.Abs(Assert.Single(session.Snapshot.Features.Values).Result.VolumeMm3-2*area),0,1e-6);
    }
    [Fact] public void DependencyCycleIsRejectedBeforeAnyNativeReselectionEvaluation()
    {
        var(s,id,_)=Circles();var d=new SketchDraft(s,Kernel);var child=d.OffsetExpressionRegions([s.ExpressionCurves[0].Id],.1,SketchRegionAuthorJoin.Miter,Kernel);
        var before=d.Value;var replacement=before.RegionOffsets.Single(g=>g.Id==child).Loops[0].Outputs[0].Curve;var provider=new CountingProvider();
        Assert.Throws<CadValidationException>(()=>d.ReselectRegionSources(id,[[replacement]],provider));Assert.Same(before,d.Value);Assert.Equal(0,provider.Calls);
    }
    private sealed class CountingProvider:IExactSketchExpressionRegions,ISketchExpressionRegionVerifier
    {
        public int Calls;
        public ExactExpressionRegionOffset OffsetExpressionRegions(ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> loops,double distance,ExactExpressionRegionJoin join)
        {Calls++;return Kernel.OffsetExpressionRegions(loops,distance,join);}
        public void ConfirmExpressionRegions(CadSketch sketch,CancellationToken token=default)=>Kernel.ConfirmExpressionRegions(sketch,token);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RetainedDriverOrReferenceRejectsExplicitReselectionUntilDetached(bool reference)
    {
        using var files=new TestFiles();var(s,id,slots)=Circles();
        // Use a production document part, retaining the exact authored source values.
        var(doc,part)=SketchCommandTests.Document();s=s with{PartId=part};
        var target=ParameterTarget.SketchRegion(s.Id,id);var parameter=new CadParameter(Guid.NewGuid(),"distance",ParameterDimension.Length,reference?"":".2 mm",reference?target:null);
        doc=doc with{Sketches=doc.Sketches.Add(s.Id,s),Parameters=new([parameter],reference?[]:[new(Guid.NewGuid(),parameter.Id,target)])};
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(s,Kernel);d.ReselectRegionSources(id,slots,Kernel);var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver(Kernel))));Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(new SetParametersCommand(ParameterBook.Empty));await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver(Kernel)));
        var after=session.Snapshot;Assert.True(SketchRegionAuthorModel.SameTable(d.Value,after.Sketches[s.Id]));
        string path=files.PathFor("reselected.cadoryx");var storage=new CadDocumentStorage(regionVerifier:Kernel);await session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,assets)){Assert.True(SketchRegionAuthorModel.SameTable(after.Sketches[s.Id],loaded.Snapshot.Sketches[s.Id]));Kernel.ConfirmExpressionRegions(loaded.Snapshot.Sketches[s.Id]);}
        await session.UndoAsync();Assert.NotEqual(after.StateId,session.Snapshot.StateId);await session.RedoAsync();Assert.Same(after,session.Snapshot);
    }
}
