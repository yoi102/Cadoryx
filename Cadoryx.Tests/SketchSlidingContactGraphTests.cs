using System.Collections.Immutable;
using System.Globalization;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed class SketchSlidingContactGraphTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach(var mode in new[]{FiniteContactParameterMode.SolveA,FiniteContactParameterMode.SolveB,FiniteContactParameterMode.SolveBoth})
        foreach(var pair in new[]{("Circle",false),("Ellipse",true),("RationalSpline",false)})
        foreach(var continuity in new[]{FiniteContactContinuity.Tangent,FiniteContactContinuity.Curvature})
            yield return[pair.Item1,pair.Item2,continuity,mode];
    }

    private static ParameterBook Drive(CadSketch sketch,double seedA,double seedB,ParameterBook? existing=null)
    {
        var contact=sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        var parameters=(existing?.Parameters??[]).ToBuilder();
        var bindings=(existing?.Bindings??[]).Where(b=>b.Target.Kind!=ParameterTargetKind.SketchConstraint||b.Target.Owner!=sketch.Id.Value).ToImmutableArray().ToBuilder();
        foreach(bool a in new[]{true,false})
        {
            if(!(a?contact.SolvesA:contact.SolvesB))continue;
            string side=a?"A":"B",prefix="contact_"+sketch.Id.Value.ToString("N")+"_"+side;
            var target=ParameterTarget.Sketch(sketch.Id,contact.Id,"Seed"+side);
            var parameter=new CadParameter(Guid.NewGuid(),prefix,ParameterDimension.Scalar,(a?seedA:seedB).ToString("R",CultureInfo.InvariantCulture));
            parameters.Add(parameter);bindings.Add(new(Guid.NewGuid(),parameter.Id,target));
            parameters.Add(new(Guid.NewGuid(),prefix+"_actual",ParameterDimension.Scalar,"",ParameterTarget.Sketch(sketch.Id,contact.Id,"Parameter"+side)));
        }
        return new(parameters.ToImmutable(),bindings.ToImmutable());
    }

    // Exact area of a quadratic Bezier segment closed by its chord, multiplied by the 2 mm extrusion.
    private static double ExpectedVolume(CadSketch sketch,SketchEntityId id)
    {
        var curve=sketch.Beziers.Single(b=>b.Id==id);Point2d P(SketchEntityId point)=>sketch.Points.Single(p=>p.Id==point).Position;
        var start=P(curve.Start);var end=P(curve.End);var control=P(curve.Control);
        return 2*Math.Abs((end.X-start.X)*(control.Y-start.Y)-(end.Y-start.Y)*(control.X-start.X))/3;
    }

    private static void References(DocumentSnapshot snapshot,CadSketch sketch)
    {
        var values=ParameterGraph.Evaluate(snapshot);var contact=sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        foreach(var parameter in snapshot.Parameters.Parameters.Where(p=>p.Reference is {} target&&target.Owner==sketch.Id.Value))
            Assert.Equal(parameter.Reference!.Property=="ParameterA"?contact.ParameterA:contact.ParameterB,values[parameter.Id].Value);
    }

    [Theory][MemberData(nameof(Cases))]
    public async Task AutomaticSeedsRebuildExactBodyAndAssociatedDrawingWithAtomicUndoReopen(string kind,bool opposite,FiniteContactContinuity continuity,FiniteContactParameterMode mode)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var authored=SketchSlidingContactSolveTests.Pair(kind,opposite,continuity,mode,false) with{PartId=part};
            await session.ExecuteAsync(new UpsertSketchCommand(authored,new ManagedSketchConstraintSolver()));
            var seed=session.Snapshot.Sketches[authored.Id];var contact=seed.Constraints.OfType<FiniteParametricContactConstraint>().Single();
            var profile=SketchProfileReference.CreateBezierSegment(seed,contact.B);
            await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,seed.Plane)),"Sliding body",part,sketchSource:profile));
            Assert.Equal(ExpectedVolume(seed,contact.B),session.Snapshot.Features.Values.Single().Result.VolumeMm3,6);
            var sheet=TechnicalDrawingSheet.A4Landscape("Automatic contact");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,80),1));
            var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(Drive(seed,.40,.54)));
            var after=session.Snapshot;var solved=after.Sketches[seed.Id];var actual=solved.Constraints.OfType<FiniteParametricContactConstraint>().Single();
            SketchFiniteParametricContact.RequireSolvedRelations(solved);
            Assert.Equal(mode,actual.ParameterMode);Assert.Equal(continuity,actual.Continuity);
            if(actual.SolvesA){Assert.Equal(.40,actual.SlidingA!.Seed);actual.SlidingA.Validate(actual.ParameterA);}
            else Assert.Equal(contact.ParameterA,actual.ParameterA);
            if(actual.SolvesB){Assert.Equal(.54,actual.SlidingB!.Seed);actual.SlidingB.Validate(actual.ParameterB);}
            else Assert.Equal(contact.ParameterB,actual.ParameterB);
            var feature=after.Features.Values.Single();var view=after.DrawingSheets[sheet.Id].Views.Single();
            Assert.Equal(ExpectedVolume(solved,contact.B),feature.Result.VolumeMm3,6);
            Assert.True(Math.Abs(feature.Result.VolumeMm3-before.Features[feature.Id].Result.VolumeMm3)>1e-7);
            Assert.NotEqual(before.Features[feature.Id].Result.Revision,feature.Result.Revision);
            Assert.Equal(solved.Revision,feature.SketchSource!.Revision);Assert.Null(view.StaleReason);Assert.NotEmpty(view.Strokes);
            Assert.Equal(feature.Result.Revision,view.Sources.Single().Revision);References(after,solved);
            Assert.Equal(seed.Ellipses,solved.Ellipses);Assert.Equal(seed.Freeforms,solved.Freeforms);
            long generation=session.Generation;int count=assets.Count;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(Drive(solved,1,1))));
            Assert.Same(after,session.Snapshot);Assert.Equal(generation,session.Generation);Assert.Equal(count,assets.Count);
            using(var canceled=new CancellationTokenSource())
            {
                canceled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ExecuteAsync(new SetParametersCommand(Drive(solved,.42,.55)),canceled.Token));
            }
            Assert.Same(after,session.Snapshot);Assert.Equal(generation,session.Generation);Assert.Equal(count,assets.Count);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var storage=new CadDocumentStorage();string path=files.PathFor("sliding-body.cadoryx");await session.SaveAsync(storage,path);
            using var reopened=await storage.LoadAsync(path,assets);reopened.Snapshot.Validate();
            var reopenedSketch=reopened.Snapshot.Sketches[seed.Id];SketchFiniteParametricContact.RequireSolvedRelations(reopenedSketch);
            Assert.Equal(actual,reopenedSketch.Constraints.OfType<FiniteParametricContactConstraint>().Single());
            Assert.Equal(feature.Result.VolumeMm3,reopened.Snapshot.Features[feature.Id].Result.VolumeMm3,6);
            Assert.Equal(view.Sources.ToArray(),reopened.Snapshot.DrawingSheets[sheet.Id].Views.Single().Sources.ToArray());References(reopened.Snapshot,reopenedSketch);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][MemberData(nameof(Cases))]
    public async Task IndependentBodyKeepsSlidingAuthorsAndExplicitOwnDriversWithoutChangingOriginal(string kind,bool opposite,FiniteContactContinuity continuity,FiniteContactParameterMode mode)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var authored=SketchSlidingContactSolveTests.Pair(kind,opposite,continuity,mode,false) with{PartId=part};
            await session.ExecuteAsync(new UpsertSketchCommand(authored,new ManagedSketchConstraintSolver()));
            var seed=session.Snapshot.Sketches[authored.Id];var contact=seed.Constraints.OfType<FiniteParametricContactConstraint>().Single();
            await session.ExecuteAsync(new SetParametersCommand(Drive(seed,.34,.46)));
            seed=session.Snapshot.Sketches[seed.Id];var profile=SketchProfileReference.CreateBezierSegment(seed,contact.B);
            await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,seed.Plane)),"Original body",part,sketchSource:profile));
            var root=new OccurrencePath(session.Snapshot.Id,[]);var slot=ComponentSlotId.New();
            await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,part,slot,"Independent",RigidTransform3d.Translate(20,0,0)));
            var clone=AssemblyOccurrenceCommands.MakePartIndependent(root.Append(slot));await session.ExecuteAsync(clone);
            var original=session.Snapshot.Sketches.Values.Single(s=>s.PartId==part);
            var copied=session.Snapshot.Sketches.Values.Single(s=>s.PartId==clone.ResultPartId);
            var originalFeature=session.Snapshot.Features.Values.Single(f=>f.PartId==part);
            var copiedFeature=session.Snapshot.Features.Values.Single(f=>f.PartId==clone.ResultPartId);
            Assert.NotEqual(original.Id,copied.Id);Assert.Equal(original.Constraints.ToArray(),copied.Constraints.ToArray());
            Assert.Equal(original.Ellipses,copied.Ellipses);Assert.Equal(original.Freeforms,copied.Freeforms);
            Assert.Contains(session.Snapshot.Parameters.Bindings,b=>b.Target.Owner==copied.Id.Value);
            var sheet=TechnicalDrawingSheet.A4Landscape("Independent automatic contact");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,80),1));
            var before=session.Snapshot;
            await session.ExecuteAsync(new SetParametersCommand(Drive(copied,.40,.54,before.Parameters)));
            var after=session.Snapshot;var changed=after.Sketches[copied.Id];var changedFeature=after.Features[copiedFeature.Id];
            SketchFiniteParametricContact.RequireSolvedRelations(changed);
            Assert.Same(original,after.Sketches[original.Id]);Assert.Same(originalFeature,after.Features[originalFeature.Id]);
            Assert.Equal(ExpectedVolume(changed,contact.B),changedFeature.Result.VolumeMm3,6);
            Assert.NotEqual(copiedFeature.Result.Revision,changedFeature.Result.Revision);
            Assert.Contains(after.Parameters.Bindings,b=>b.Target.Owner==original.Id.Value);
            References(after,original);References(after,changed);
            var view=after.DrawingSheets[sheet.Id].Views.Single();Assert.Null(view.StaleReason);Assert.NotEmpty(view.Strokes);
            Assert.Contains(view.Sources,s=>s.Revision==changedFeature.Result.Revision);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var storage=new CadDocumentStorage();string path=files.PathFor("independent-sliding.cadoryx");await session.SaveAsync(storage,path);
            using var reopened=await storage.LoadAsync(path,assets);reopened.Snapshot.Validate();
            Assert.Equal(changed.Constraints.ToArray(),reopened.Snapshot.Sketches[copied.Id].Constraints.ToArray());
            Assert.Equal(original.Constraints.ToArray(),reopened.Snapshot.Sketches[original.Id].Constraints.ToArray());
            References(reopened.Snapshot,reopened.Snapshot.Sketches[copied.Id]);
        }
        Assert.Equal(0,assets.Count);
    }
}
