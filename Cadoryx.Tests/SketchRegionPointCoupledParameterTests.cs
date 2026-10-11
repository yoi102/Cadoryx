using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Xunit;
using Xunit.Abstractions;

namespace Cadoryx.Tests;

public sealed class SketchRegionPointCoupledParameterTests(ITestOutputHelper output)
{
    private static readonly OcctGeometryKernel Kernel=new();
    public static IEnumerable<object[]> Cases()=>from kind in new[]{"capsule","bezier","spline"}
        from direction in new[]{false,true} from joinKind in Enum.GetValues<SketchRegionAuthorJoin>()
        select new object[]{kind,direction,joinKind};
    [Theory,MemberData(nameof(Cases))]
    public async Task FullSourceAndBothDistanceDriversRecomputeBothCoupledMaterialsInOneHistoryStepAndRetainBothFormats(
        string kind,bool naturalConsumesNative,SketchRegionAuthorJoin join)
    {
        using var files=new TestFiles();var fixture=PointCoupledFixture.Create(kind,naturalConsumesNative,false,.1,join);PointCoupledFixture.RequireProof(fixture);
        var(doc,part)=SketchCommandTests.Document();var source=PointCoupledFixture.MaterialCurves(fixture.Sketch with{PartId=part});
        var independent=PointCoupledFixture.IndependentSourcePoints(source);
        source=source with{Constraints=source.Constraints.AddRange(source.Points.Where(p=>independent.Contains(p.Id)).Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)))};
        var assets=new MemoryAssetStore();var solver=new ManagedSketchConstraintSolver(Kernel);await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new UpsertSketchCommand(source,solver));var initial=session.Snapshot.Sketches[source.Id];var features=new Dictionary<Guid,FeatureId>();
        foreach(var group in initial.RegionOffsets)
        {
            var root=Assert.Single(SketchRegionAuthorModel.Roots(initial,group.Id));
            var profile=new SketchProfileReference(initial.Id,initial.Revision,[]){MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};var previous=session.Snapshot.Features.Keys.ToHashSet();
            await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,initial.Plane)),"Coupled C1 material",part,sketchSource:profile));
            features.Add(group.Id,session.Snapshot.Features.Keys.Single(id=>!previous.Contains(id)));
        }
        var before=session.Snapshot;var anchor=source.Points.Single(p=>p.Id==fixture.Shared).Position;const double factor=1.12;
        var scale=new CadParameter(Guid.NewGuid(),"scale",ParameterDimension.Scalar,R(factor));var parameters=new List<CadParameter>{scale};var bindings=new List<ParameterBinding>();int index=0;
        foreach(var group in initial.RegionOffsets)
        {
            var p=new CadParameter(Guid.NewGuid(),"distance"+index++,ParameterDimension.Length,R(group.Distance)+" mm * scale");
            parameters.Add(p);bindings.Add(new(Guid.NewGuid(),p.Id,ParameterTarget.SketchRegion(initial.Id,group.Id)));
        }
        foreach(var fixedPoint in initial.Constraints.OfType<FixPointConstraint>().Where(c=>independent.Contains(c.Point)))foreach(string axis in new[]{"X","Y"})
        {
            double coordinate=axis=="X"?fixedPoint.Position.X:fixedPoint.Position.Y,origin=axis=="X"?anchor.X:anchor.Y,shift=axis=="X"?.4:-.25;
            var p=new CadParameter(Guid.NewGuid(),"source"+index++,ParameterDimension.Length,"("+R(coordinate)+" mm - "+R(origin)+" mm) * scale + "+R(origin+shift)+" mm");
            parameters.Add(p);bindings.Add(new(Guid.NewGuid(),p.Id,ParameterTarget.Sketch(initial.Id,fixedPoint.Id,axis)));
        }
        var book=new ParameterBook(parameters.ToImmutableArray(),(naturalConsumesNative?bindings.AsEnumerable().Reverse():bindings).ToImmutableArray());
        await session.ExecuteAsync(new SetParametersCommand(book,solver));var accepted=session.Snapshot;var solved=accepted.Sketches[initial.Id];
        Require(accepted);await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(accepted,session.Snapshot);
        var storage=new CadDocumentStorage(regionVerifier:Kernel);
        foreach(bool json in new[]{false,true})
        {
            string path=files.PathFor(json?"coupled-json.cadoryx":"coupled-msgpack.cadoryx");await session.SaveAsync(storage,path);
            if(json)SketchExpressionSectionTests.Rewrite(path,"sketch-regions",_=>SketchRegionSectionStorage.EncodeJson([solved]),"json",2);
            using var loaded=await storage.LoadAsync(path,assets);Require(loaded.Snapshot);var actual=loaded.Snapshot.Sketches[initial.Id];
            Assert.True(SketchRegionAuthorModel.SameTable(solved,actual));Assert.True(SketchExpressionEntities.SameTable(solved,actual));
            Assert.Equal(solved.Points.AsEnumerable(),actual.Points.AsEnumerable());Assert.Equal(solved.Constraints.AsEnumerable(),actual.Constraints.AsEnumerable());
            Assert.Equal(book.Parameters.OrderBy(p=>p.Id),loaded.Snapshot.Parameters.Parameters);Assert.Equal(book.Bindings.OrderBy(b=>b.Id),loaded.Snapshot.Parameters.Bindings);
            var manifest=FormatEvolutionTests.Manifest(path);Assert.Equal("0.7.8",manifest.ApplicationVersion);Assert.Equal(60,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);Assert.Equal(18,manifest.Sections.Length);
        }
        int assetCount=assets.Count;var cycle=book with{Parameters=book.Parameters.Replace(scale,scale with{Expression="scale"})};
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(cycle,solver)));Assert.Same(accepted,session.Snapshot);Assert.Equal(assetCount,assets.Count);
        var offset=book.Parameters.First(p=>p.Name.StartsWith("distance",StringComparison.Ordinal));var collapse=book with{Parameters=book.Parameters.Replace(offset,offset with{Expression="-100 mm"})};
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(collapse,solver)));Assert.Same(accepted,session.Snapshot);Assert.Equal(assetCount,assets.Count);
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ExecuteAsync(new SetParametersCommand(book,solver),cancellation.Token));
        Assert.Same(accepted,session.Snapshot);Assert.Equal(assetCount,assets.Count);
        output.WriteLine($"kind={kind};direction={naturalConsumesNative};join={join};bindings={book.Bindings.Length};groups={solved.RegionOffsets.Length};sourceIndependent={independent.Count};sharedGenerated={fixture.Shared};writer=.7.8;document=60;json=2;msgpack=3");

        void Require(DocumentSnapshot snapshot)
        {
            ParameterGraph.ValidateApplied(snapshot);var value=snapshot.Sketches[initial.Id];SketchAssociativeEndpoints.RequireSolved(value);Kernel.ConfirmExpressionRegions(value);
            Assert.Equal(initial.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)),value.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)));
            foreach(var point in source.Points)
            {
                var actual=value.Points.Single(p=>p.Id==point.Id).Position;var expected=new Point2d(anchor.X+factor*(point.Position.X-anchor.X)+.4,anchor.Y+factor*(point.Position.Y-anchor.Y)-.25);
                Assert.InRange(double.Hypot(actual.X-expected.X,actual.Y-expected.Y),0,1e-7);
            }
            foreach(var group in initial.RegionOffsets)
            {
                var actual=value.RegionOffsets.Single(g=>g.Id==group.Id);Assert.Equal(group.Distance*factor,actual.Distance);
                Assert.True(SketchRegionJointAuthors.SameStructure(group.Loops[0].Joints,actual.Loops[0].Joints));
                Assert.InRange(Math.Abs(snapshot.Features[features[group.Id]].Result.VolumeMm3-PointCoupledFixture.Expected(kind,group.Id==fixture.Natural,join,.1,factor)),0,1e-7);
            }
        }
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task EveryRealNaturalProviderOfOneSharedPointRemainsAnEquationAndDisagreeingPrescriptionsCannotBePublished(bool disagree)
    {
        var first=PointCoupledFixture.Create("capsule",false,false,.1,SketchRegionAuthorJoin.Round);var second=SketchRegionNaturalChartTests.Capsule(false,.1);
        var original=first.Sketch;var firstGroup=original.RegionOffsets.Single(g=>g.Id==first.Natural);var secondGroup=second.RegionOffsets.Single();
        var firstOutput=firstGroup.Loops[0].Outputs[0];var source=firstGroup.Sources[0][firstOutput.SourceSlots[0]].Entity;
        int lineIndex=Array.FindIndex(original.Lines.ToArray(),l=>l.Id==source);
        var corresponding=lineIndex>=0?second.Lines[lineIndex].Id:second.Arcs[Array.FindIndex(original.Arcs.ToArray(),a=>a.Id==source)].Id;
        var secondOutput=Assert.Single(secondGroup.Loops[0].Outputs,o=>o.SourceSlots.Length==1&&secondGroup.Sources[0][o.SourceSlots[0]].Entity==corresponding);
        var secondPoint=firstOutput.SourceTraversalReversed==secondOutput.SourceTraversalReversed?secondOutput.Endpoints.Start:secondOutput.Endpoints.End;
        var shared=original.Points.Single(p=>p.Id==first.Shared).Position;var counterpart=second.Points.Single(p=>p.Id==secondPoint).Position;
        Assert.InRange(double.Hypot(shared.X-counterpart.X,shared.Y-counterpart.Y),0,1e-7);
        SketchEntityId Map(SketchEntityId id)=>id==secondPoint?first.Shared:id;
        var joined=original with{Points=original.Points.AddRange(second.Points.Where(p=>p.Id!=secondPoint)),Lines=original.Lines.AddRange(second.Lines),Arcs=original.Arcs.AddRange(second.Arcs),
            Constraints=original.Constraints.AddRange(second.Constraints),
            ExpressionCurves=original.ExpressionCurves.AddRange(second.ExpressionCurves.Select(e=>{var ends=e.Endpoints!;return e with{Endpoints=ends with{Start=Map(ends.Start),End=Map(ends.End)}};})),
            RegionOffsets=original.RegionOffsets.AddRange(second.RegionOffsets.Select(g=>g with{Loops=g.Loops.Select(l=>l with{Outputs=l.Outputs.Select(o=>o with{Endpoints=o.Endpoints with{Start=Map(o.Endpoints.Start),End=Map(o.Endpoints.End)}}).ToImmutableArray()}).ToImmutableArray()}))};
        joined.Validate();Kernel.ConfirmExpressionRegions(joined);Assert.Null(PointCoupledFixture.Chart(joined,"TryCreate"));
        var chart=PointCoupledFixture.Chart(joined,"TryCreateForEquations");Assert.NotNull(chart);
        var proof=chart.GetType().GetProperty("PointCouplingProof",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(chart)!;
        var paths=((IEnumerable)proof.GetType().GetProperty("Couplings",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(proof)!).Cast<object>().ToArray();
        var providerIds=paths.Select(p=>(Guid)p.GetType().GetProperty("ProviderGroup")!.GetValue(p)!).ToHashSet();Assert.Contains(first.Natural,providerIds);Assert.Contains(secondGroup.Id,providerIds);
        var independent=PointCoupledFixture.IndependentSourcePoints(joined);
        var secondSources=PointCoupledFixture.SourcePoints(second).ToHashSet();
        var authored=joined with{Constraints=joined.Constraints.AddRange(joined.Points.Where(p=>independent.Contains(p.Id)).Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,
            new(p.Position.X+(disagree&&secondSources.Contains(p.Id)?.6:.4),p.Position.Y-.25))))};
        var report=await new ManagedSketchConstraintSolver(Kernel).SolveAsync(authored);output.WriteLine($"disagree={disagree};iterations={report.Iterations};equations={report.EquationCount};diagnostic={report.Diagnostic};residual={report.MaxNormalizedResidual:R}");
        Assert.Equal(authored.Points.Length*2,report.VariableCount);Assert.Equal(independent.Count*2+8+authored.ExpressionCurves.Length*4,report.EquationCount);
        Assert.InRange(report.Iterations,1,80);Assert.InRange(report.VariableCount,1,256);Assert.InRange(report.EquationCount,1,512);
        if(disagree){Assert.False(report.Succeeded);Assert.Null(report.Solution);}
        else
        {
            Assert.True(report.Succeeded,report.Diagnostic);new SketchRegionEvaluationContext(Kernel).ValidateSolved(authored,report.Solution!);SketchAssociativeEndpoints.RequireSolved(report.Solution!);Kernel.ConfirmExpressionRegions(report.Solution!);
            foreach(var point in joined.Points){var actual=report.Solution!.Points.Single(p=>p.Id==point.Id).Position;Assert.InRange(double.Hypot(actual.X-point.Position.X-.4,actual.Y-point.Position.Y+.25),0,1e-7);}
        }
    }
    private static string R(double value)=>value.ToString("R",CultureInfo.InvariantCulture);
}
