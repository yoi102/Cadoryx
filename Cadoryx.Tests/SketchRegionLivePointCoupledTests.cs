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
using MathNet.Numerics.LinearAlgebra;
using Xunit;
using Xunit.Abstractions;

namespace Cadoryx.Tests;

public sealed class SketchRegionLivePointCoupledTests(ITestOutputHelper output)
{
    private static readonly OcctGeometryKernel Kernel=new();
    public static IEnumerable<object[]> Cases()=>from kind in new[]{"capsule","bezier","spline"}
        from direction in new[]{false,true} from depth in new[]{1,3,7} from mirror in new[]{false,true}
        from distance in new[]{.1,-.1} from joinKind in Enum.GetValues<SketchRegionAuthorJoin>()
        select new object[]{kind,direction,depth,mirror,distance,joinKind};

    [Theory,MemberData(nameof(Cases))]
    public async Task EveryLivePointPathSolvesWithoutPrescribingGeneratedVariablesAndRetainsBothNativeMaterialsAndStorage(
        string kind,bool direction,int depth,bool mirror,double distance,SketchRegionAuthorJoin join)
    {
        var fixture=Create(kind,direction,depth,mirror,distance,join);RequireLiveProof(fixture,depth);
        var original=fixture.Sketch;var independent=PointCoupledFixture.IndependentSourcePoints(original);
        var authored=original with{Constraints=original.Constraints.AddRange(original.Points.Where(p=>independent.Contains(p.Id))
            .Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,new(p.Position.X+.4,p.Position.Y-.25))))};
        var checking=new CheckingProvider(original.RegionOffsets.Single(g=>g.Id==fixture.Natural).Sources.SelectMany(l=>l).Select(s=>s.Entity).ToHashSet());
        var report=await new ManagedSketchConstraintSolver(checking,Kernel).SolveAsync(authored);
        output.WriteLine($"kind={kind};direction={direction};depth={depth};mirror={mirror};distance={distance:R};join={join};iterations={report.Iterations};variables={report.VariableCount};equations={report.EquationCount};natural={checking.NaturalRequests};native={checking.NativeRequests};offManifold={checking.OffManifoldNaturalRequests};residual={report.MaxNormalizedResidual:R};diagnostic={report.Diagnostic}");
        Assert.True(report.Succeeded,report.Diagnostic);var solved=report.Solution!;
        Assert.Equal(0,checking.OffManifoldNaturalRequests);Assert.InRange(checking.NaturalRequests,1,int.MaxValue);Assert.InRange(checking.NativeRequests,2,int.MaxValue);
        Assert.Equal(authored.Points.Length*2,report.VariableCount);
        Assert.Equal(independent.Count*2+(kind=="capsule"?4:8)+authored.ExpressionCurves.Length*4,report.EquationCount);
        Assert.InRange(report.Iterations,1,80);Assert.InRange(report.VariableCount,1,256);Assert.InRange(report.EquationCount,1,512);
        Assert.Equal(original.Points.Select(p=>p.Id),solved.Points.Select(p=>p.Id));
        Assert.Equal(original.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)),solved.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)));
        Assert.Equal(original.ExpressionCurves.Select(c=>(c.Id,c.Source?.Entity,c.Source?.Kind,c.Source?.BasisDepth)),
            solved.ExpressionCurves.Select(c=>(c.Id,c.Source?.Entity,c.Source?.Kind,c.Source?.BasisDepth)));
        foreach(var point in original.Points)
        {
            var actual=solved.Points.Single(p=>p.Id==point.Id).Position;
            Assert.InRange(double.Hypot(actual.X-point.Position.X-.4,actual.Y-point.Position.Y+.25),0,1e-7);
        }
        foreach(var group in original.RegionOffsets)
            Assert.True(SketchRegionJointAuthors.SameStructure(group.Loops[0].Joints,solved.RegionOffsets.Single(g=>g.Id==group.Id).Loops[0].Joints));
        new SketchRegionEvaluationContext(Kernel).ValidateSolved(authored,solved);SketchAssociativeEndpoints.RequireSolved(solved);Kernel.ConfirmExpressionRegions(solved);
        var(doc,part)=SketchCommandTests.Document();var owned=solved.RegionOffsets.SelectMany(g=>g.Loops.SelectMany(l=>l.Outputs)).Select(o=>o.Curve).ToHashSet();
        solved=solved with{PartId=part,ExpressionCurves=solved.ExpressionCurves.Select(e=>e with{IsConstruction=!owned.Contains(e.Id)}).ToImmutableArray()};
        doc=doc with{Sketches=doc.Sketches.Add(solved.Id,solved)};
        var assets=new MemoryAssetStore();using var files=new TestFiles();
        await using(var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher()))
        {
            foreach(var group in solved.RegionOffsets)
            {
                var root=Assert.Single(SketchRegionAuthorModel.Roots(solved,group.Id));
                var profile=new SketchProfileReference(solved.Id,solved.Revision,[]){MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};
                var before=session.Snapshot;var ids=before.Features.Keys.ToHashSet();
                await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(before,part,new ExtrudeRecipe(new([]),3,solved.Plane)),"Live point material",part,sketchSource:profile));
                var accepted=session.Snapshot;var feature=Assert.Single(accepted.Features.Values,f=>!ids.Contains(f.Id));
                Assert.InRange(Math.Abs(feature.Result.VolumeMm3-PointCoupledFixture.Expected(kind,group.Id==fixture.Natural,join,distance,1)),0,1e-7);
                await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(accepted,session.Snapshot);
            }
            var storage=new CadDocumentStorage(regionVerifier:Kernel);
            foreach(bool json in new[]{false,true})
            {
                var file=files.PathFor(json?"live-json.cadoryx":"live-msgpack.cadoryx");await session.SaveAsync(storage,file);
                if(json)SketchExpressionSectionTests.Rewrite(file,"sketch-regions",_=>SketchRegionSectionStorage.EncodeJson([solved]),"json",2);
                using var loaded=await storage.LoadAsync(file,assets);var restored=loaded.Snapshot.Sketches[solved.Id];
                Assert.True(SketchRegionAuthorModel.SameTable(solved,restored));Assert.True(SketchExpressionEntities.SameTable(solved,restored));
                Assert.Equal(solved.Points.AsEnumerable(),restored.Points.AsEnumerable());Assert.Equal(solved.Constraints.AsEnumerable(),restored.Constraints.AsEnumerable());
                SketchAssociativeEndpoints.RequireSolved(restored);Kernel.ConfirmExpressionRegions(restored);
                Assert.Equal(session.Snapshot.Features.Keys.OrderBy(id=>id.Value),loaded.Snapshot.Features.Keys.OrderBy(id=>id.Value));
                foreach(var id in session.Snapshot.Features.Keys)Assert.Equal(session.Snapshot.Features[id].Result.VolumeMm3,loaded.Snapshot.Features[id].Result.VolumeMm3);
            }
        }
        Assert.Equal(0,assets.Count);Assert.Equal(0,assets.SizeBytes);
    }

    public static IEnumerable<object[]> Jacobians()=>from kind in new[]{"capsule","bezier","spline"}
        from direction in new[]{false,true} from depth in new[]{1,3,7} from factor in new[]{.7,1.4}
        select new object[]{kind,direction,depth,factor};
    [Theory,MemberData(nameof(Jacobians))]
    public void EverySourceAndGeneratedColumnMatchesIndependentCentralDifferencesAfterRotationAndScaling(string kind,bool direction,int depth,double factor)
    {
        var fixture=Create(kind,direction,depth,true,.1,SketchRegionAuthorJoin.Round);var source=fixture.Sketch;
        const double angle=.21;Point2d Transform(Point2d p)=>new(17+factor*(p.X*Math.Cos(angle)-p.Y*Math.Sin(angle)),-11+factor*(p.X*Math.Sin(angle)+p.Y*Math.Cos(angle)));
        var transformed=source with{Points=source.Points.Select(p=>p with{Position=Transform(p.Position)}).ToImmutableArray(),
            ExpressionCurves=source.ExpressionCurves.Select(e=>e.Source is not null?e with{Geometry=e.Geometry with{SignedOffset=e.Geometry.SignedOffset*factor}}:e).ToImmutableArray()};
        transformed=new SketchRegionEvaluationContext(Kernel).Refresh(transformed,synchronizeGeneratedPoints:true,distanceOverrides:source.RegionOffsets.ToDictionary(g=>g.Id,g=>g.Distance*factor));
        var independent=PointCoupledFixture.IndependentSourcePoints(transformed);
        var authored=transformed with{Constraints=transformed.Constraints.AddRange(transformed.Points.Where(p=>independent.Contains(p.Id))
            .Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)))};
        var type=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchEquationSystem",true)!;var options=new SketchSolveOptions();
        var system=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,[authored,options,new SketchRegionEvaluationContext(Kernel),CancellationToken.None],CultureInfo.InvariantCulture)!;
        var x=((double[])type.GetProperty("Initial",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(system)!).ToArray();
        var residual=type.GetMethod("Residual",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var jacobian=(Matrix<double>)type.GetMethod("Jacobian",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(system,[x,CancellationToken.None])!;
        var initial=(double[])residual.Invoke(system,[x])!;Assert.Equal(initial.Length,jacobian.RowCount);Assert.Equal(x.Length,jacobian.ColumnCount);
        double scale=(double)type.GetField("scale",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(system)!;
        var units=Enumerable.Repeat(options.LinearToleranceMm/scale,initial.Length).ToArray();
        foreach(var group in ((IEnumerable)type.GetProperty("Groups",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(system)!).Cast<object>())
        {
            var t=group.GetType();var constraint=(SketchConstraint)t.GetProperty("Constraint")!.GetValue(group)!;int first=(int)t.GetProperty("Start")!.GetValue(group)!;
            Assert.True(constraint is FixPointConstraint or SketchExpressionEndpointEquation or EndpointTangentConstraint or EndpointCurvatureConstraint);
            if(constraint is EndpointTangentConstraint or EndpointCurvatureConstraint)units[first]=options.AngularTolerance;
            if(constraint is EndpointCurvatureConstraint)units[first+1]=options.CurvatureTolerancePerMm*scale;
        }
        const double h=1e-6;double largest=0;
        for(int column=0;column<x.Length;column++)
        {
            var plus=x.ToArray();var minus=x.ToArray();plus[column]+=h;minus[column]-=h;
            var a=(double[])residual.Invoke(system,[plus])!;var b=(double[])residual.Invoke(system,[minus])!;
            for(int row=0;row<initial.Length;row++)
            {
                double expected=(a[row]*units[row]-b[row]*units[row])/(2*h),actual=jacobian[row,column]*units[row];
                double difference=Math.Abs(expected-actual)/Math.Max(1,Math.Max(Math.Abs(expected),Math.Abs(actual)));
                largest=Math.Max(largest,difference);Assert.True(double.IsFinite(actual)&&difference<=3e-5,$"row={row};column={column};expected={expected:R};actual={actual:R};relative={difference:R}");
            }
        }
        output.WriteLine($"kind={kind};direction={direction};depth={depth};factor={factor:R};rows={initial.Length};columns={x.Length};largest={largest:R}");
    }

    public static IEnumerable<object[]> FrozenCases()=>from kind in new[]{"capsule","bezier","spline"} from direction in new[]{false,true} from depth in new[]{1,3,7} select new object[]{kind,direction,depth};
    [Theory,MemberData(nameof(FrozenCases))]
    public void ExplicitFrozenIntermediateProviderStillStopsTheLiveDependencyPath(string kind,bool direction,int depth)
    {
        var fixture=Create(kind,direction,depth,false,.1,SketchRegionAuthorJoin.Round);
        var provider=SketchAssociativeEndpoints.Equations(fixture.Sketch).First(e=>e.Point==fixture.Shared).Curve;
        var draft=new SketchDraft(fixture.Sketch,Kernel);draft.FreezeExpression(provider);var frozen=draft.Value;
        frozen.Validate();Kernel.ConfirmExpressionRegions(frozen);
        var old=PointCoupledFixture.Chart(frozen,"TryCreateForEquations");var current=PointCoupledFixture.Chart(frozen,"TryCreateForAllEquations");
        Assert.NotNull(old);Assert.NotNull(current);
        Assert.False((bool)current.GetType().GetProperty("IsLivePointCoupled",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(current)!);
    }

    private static PointCoupledFixture Create(string kind,bool direction,int depth,bool mirror,double distance,SketchRegionAuthorJoin join)
    {
        var fixture=PointCoupledFixture.Create(kind,direction,mirror,distance,join);var original=fixture.Sketch;
        var provider=original.RegionOffsets.Single(g=>g.Id==(direction?fixture.Native:fixture.Natural));
        var root=provider.Loops[0].Outputs[0];Assert.Equal(fixture.Shared,root.Endpoints.Start);
        var draft=new SketchDraft(original,Kernel);var curve=root.Curve;
        for(int i=0;i<depth;i++){curve=draft.AddExactOffset(curve,.015);draft.AttachExpressionEndpoints(curve);}
        var candidate=draft.Value;var generated=candidate.ExpressionCurves.Single(e=>e.Id==curve).Endpoints!.Start;
        var before=original.Points.Single(p=>p.Id==fixture.Shared).Position;var after=candidate.Points.Single(p=>p.Id==generated).Position;
        var consumer=candidate.RegionOffsets.Single(g=>g.Id==(direction?fixture.Natural:fixture.Native));
        var sources=consumer.Sources.SelectMany(l=>l).Select(s=>s.Entity).ToHashSet();
        var points=sources.SelectMany(id=>(ImmutableArray<SketchEntityId>)typeof(SketchAssociativeEndpoints).GetMethod("AuthorPoints",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[candidate,id])!).ToHashSet();
        candidate=candidate with{Points=candidate.Points.Select(p=>p.Id!=fixture.Shared&&points.Contains(p.Id)?p with{Position=new(p.Position.X+after.X-before.X,p.Position.Y+after.Y-before.Y)}:p).ToImmutableArray()};
        candidate=PointCoupledFixture.ReplaceVertex(candidate,sources,fixture.Shared,generated);
        candidate=new SketchRegionEvaluationContext(Kernel).Refresh(candidate,synchronizeGeneratedPoints:true);
        candidate.Validate();Kernel.ConfirmExpressionRegions(candidate);return fixture with{Sketch=candidate,Shared=generated};
    }
    private static void RequireLiveProof(PointCoupledFixture fixture,int depth)
    {
        Assert.Null(PointCoupledFixture.Chart(fixture.Sketch,"TryCreateForEquations"));
        var chart=PointCoupledFixture.Chart(fixture.Sketch,"TryCreateForAllEquations");Assert.NotNull(chart);
        var type=chart.GetType();Assert.True((bool)type.GetProperty("IsLivePointCoupled",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(chart)!);
        var ids=(ImmutableHashSet<Guid>)type.GetProperty("GroupIds",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(chart)!;
        Assert.Equal(fixture.Natural,Assert.Single(ids));
        var proof=type.GetProperty("LivePointCouplingProof",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(chart)!;
        var couplings=((IEnumerable)proof.GetType().GetProperty("Couplings",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(proof)!).Cast<object>().ToArray();
        Assert.NotEmpty(couplings);
        foreach(var coupling in couplings)
        {
            var t=coupling.GetType();Assert.Equal(fixture.Shared,(SketchEntityId)t.GetProperty("Point")!.GetValue(coupling)!);
            var path=(ImmutableArray<SketchEntityId>)t.GetProperty("LivePath")!.GetValue(coupling)!;
            Assert.Equal(depth+1,path.Length);Assert.Equal(path.Length,path.Distinct().Count());
            Assert.Equal((SketchEntityId)t.GetProperty("PointProvider")!.GetValue(coupling)!,path[0]);
            for(int i=0;i<path.Length-1;i++)Assert.Equal(path[i+1],fixture.Sketch.ExpressionCurves.Single(e=>e.Id==path[i]).Source!.Entity);
            var owner=(Guid)t.GetProperty("ProviderGroup")!.GetValue(coupling)!;
            Assert.Contains(fixture.Sketch.RegionOffsets.Single(g=>g.Id==owner).Loops.SelectMany(l=>l.Outputs),o=>o.Curve==path[^1]);
        }
    }
    private sealed class CheckingProvider(HashSet<SketchEntityId> naturalSources):IExactSketchExpressionRegions
    {
        public int NaturalRequests {get;private set;}public int NativeRequests {get;private set;}public int OffManifoldNaturalRequests {get;private set;}
        public ExactExpressionRegionOffset OffsetExpressionRegions(ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> loops,double distance,ExactExpressionRegionJoin join)
        {
            if(loops.SelectMany(l=>l).All(s=>naturalSources.Contains(s.Id)))
            {
                NaturalRequests++;
                foreach(var loop in loops)for(int i=0;i<loop.Length;i++)
                {
                    var before=loop[i];var after=loop[(i+1)%loop.Length];var a=before.Geometry.Evaluate(before.Reversed?0:1);var b=after.Geometry.Evaluate(after.Reversed?1:0);
                    double cross=(a.First.X*b.First.Y-a.First.Y*b.First.X)/(double.Hypot(a.First.X,a.First.Y)*double.Hypot(b.First.X,b.First.Y));
                    if(double.Hypot(a.Point.X-b.Point.X,a.Point.Y-b.Point.Y)>1e-7||Math.Abs(cross)>1e-9)OffManifoldNaturalRequests++;
                }
            }
            else NativeRequests++;
            return Kernel.OffsetExpressionRegions(loops,distance,join);
        }
    }
}
