using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Xunit;
using Xunit.Abstractions;

namespace Cadoryx.Tests;

public sealed class SketchRegionIndependentMixedChartTests(ITestOutputHelper output)
{
    private static readonly OcctGeometryKernel Kernel=new();
    public static IEnumerable<object[]> Cases()=>from kind in new[]{"capsule","bezier","spline"}
        from mirror in new[]{false,true} from distance in new[]{.1,-.1}
        from style in Enum.GetValues<SketchRegionAuthorJoin>() select new object[]{kind,mirror,distance,style};

    private static (CadSketch Sketch,Guid Natural,Guid Native) Mixed(string kind,bool mirror,double distance,SketchRegionAuthorJoin join)
    {
        var natural=kind=="capsule"?SketchRegionNaturalChartTests.Capsule(mirror,distance):
            SketchRegionNaturalFreeformChartTests.Ring(kind=="spline",mirror,true,distance);
        var naturalId=natural.RegionOffsets.Single().Id;
        var draft=new SketchDraft(natural,Kernel);
        // Explicit source lines avoid the interactive rectangle tool's existing
        // fixed origin and X/Y dimensions. The following test owns every source
        // prescription and checks the original equation count without disabling
        // or changing any production defaults.
        double left=mirror?-38:30,right=mirror?-30:38;
        draft.AddLine(new(left,10),new(right,10));var first=draft.Value.Lines[^1];
        draft.AddLine(new(right,10),new(right,14),first.End);var side=draft.Value.Lines[^1];
        draft.AddLine(new(right,14),new(left,14),side.End);var top=draft.Value.Lines[^1];
        draft.AddLine(new(left,14),new(left,10),top.End,first.Start);
        var nativeId=draft.OffsetExpressionRegions([first.Id],distance,join,Kernel);
        return (draft.Value,naturalId,nativeId);
    }

    [Theory,MemberData(nameof(Cases))]
    public async Task ProvenNaturalAndIndependentNativeIntersectionOrBridgeGroupsSolveTogetherWithEveryOriginalRow(
        string kind,bool mirror,double distance,SketchRegionAuthorJoin join)
    {
        var(original,naturalId,nativeId)=Mixed(kind,mirror,distance,join);
        var sourcePoints=original.Lines.SelectMany(l=>new[]{l.Start,l.End})
            .Concat(original.Arcs.SelectMany(a=>new[]{a.Start,a.Middle,a.End}))
            .Concat(original.Beziers.SelectMany(b=>new[]{b.Start,b.Control,b.End}))
            .Concat(original.Splines.SelectMany(s=>s.Controls)).ToHashSet();
        const double factor=1.15;
        var authored=original with{Constraints=original.Constraints.AddRange(original.Points.Where(p=>sourcePoints.Contains(p.Id))
            .Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,new(2+factor*p.Position.X,-1+factor*p.Position.Y))))};
        authored.Validate();
        var naturalSources=authored.RegionOffsets.Single(g=>g.Id==naturalId).Sources.SelectMany(l=>l).Select(s=>s.Entity).ToHashSet();
        var provider=new CheckingProvider(naturalSources);
        var result=await new ManagedSketchConstraintSolver(provider,Kernel).SolveAsync(authored);
        output.WriteLine($"kind={kind};mirror={mirror};distance={distance:R};join={join};iterations={result.Iterations};naturalCalls={provider.NaturalRequests};nativeCalls={provider.NativeRequests};diagnostic={result.Diagnostic};residual={result.MaxNormalizedResidual:R}");
        Assert.True(result.Succeeded,result.Diagnostic);var solved=result.Solution!;
        Assert.Equal(0,provider.OffManifoldNaturalRequests);
        Assert.InRange(provider.NaturalRequests,1,int.MaxValue);Assert.InRange(provider.NativeRequests,2,int.MaxValue);
        Assert.Equal(authored.Points.Length*2,result.VariableCount);
        int relationRows=kind=="capsule"?4:8;
        Assert.Equal(sourcePoints.Count*2+relationRows+authored.ExpressionCurves.Length*4,result.EquationCount);
        Assert.InRange(result.Iterations,1,80);Assert.InRange(result.VariableCount,1,256);Assert.InRange(result.EquationCount,1,512);
        Assert.Equal(original.Points.Select(p=>p.Id),solved.Points.Select(p=>p.Id));
        Assert.Equal(original.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)),solved.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)));
        foreach(var group in original.RegionOffsets)
            Assert.True(SketchRegionJointAuthors.SameStructure(group.Loops[0].Joints,solved.RegionOffsets.Single(g=>g.Id==group.Id).Loops[0].Joints));
        Assert.All(solved.RegionOffsets.Single(g=>g.Id==naturalId).Loops[0].Joints,j=>Assert.Equal(SketchRegionJointKind.SourceEndpoints,j.Kind));
        Assert.Contains(solved.RegionOffsets.Single(g=>g.Id==nativeId).Loops[0].Joints,j=>j.Kind!=SketchRegionJointKind.SourceEndpoints);
        new SketchRegionEvaluationContext(Kernel).ValidateSolved(authored,solved);
        SketchAssociativeEndpoints.RequireSolved(solved);Kernel.ConfirmExpressionRegions(solved);
        foreach(var point in authored.Constraints.OfType<FixPointConstraint>())
        {
            var actual=solved.Points.Single(p=>p.Id==point.Point).Position;
            Assert.InRange(double.Hypot(actual.X-point.Position.X,actual.Y-point.Position.Y),0,1e-7);
        }
        var(doc,part)=SketchCommandTests.Document();solved=solved with{PartId=part};doc=doc with{Sketches=doc.Sketches.Add(solved.Id,solved)};
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        foreach(var group in solved.RegionOffsets)
        {
            var root=Assert.Single(SketchRegionAuthorModel.Roots(solved,group.Id));
            var profile=new SketchProfileReference(solved.Id,solved.Revision,[]){MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};
            var before=session.Snapshot.Features.Keys.ToHashSet();
            await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,solved.Plane)),
                "Mixed chart solid",part,sketchSource:profile));
            var feature=Assert.Single(session.Snapshot.Features.Values,f=>!before.Contains(f.Id));
            double area;
            if(group.Id==naturalId)
            {
                double radius=2*factor;
                double sourceArea=kind=="capsule"?16*factor*radius+Math.PI*radius*radius:10*radius*radius/3;
                double perimeter=kind=="capsule"?16*factor+Math.Tau*radius:4*radius*(1+Math.Asinh(1)/Math.Sqrt(2));
                area=sourceArea+distance*perimeter+Math.PI*distance*distance;
            }
            else
            {
                double a=8*factor,b=4*factor;
                area=distance<0||(join==SketchRegionAuthorJoin.Miter)?(a+2*distance)*(b+2*distance):
                    a*b+2*distance*(a+b)+(join==SketchRegionAuthorJoin.Round?Math.PI:2)*distance*distance;
            }
            Assert.InRange(Math.Abs(feature.Result.VolumeMm3-3*area),0,1e-7);
        }
    }

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public async Task MissingOneNaturalProofCannotBeHiddenByAnIndependentValidBridgeGroup(int missing)
    {
        var(s,_,_)=Mixed("capsule",false,.1,SketchRegionAuthorJoin.Round);
        var proof=s.Constraints.OfType<EndpointTangentConstraint>().ElementAt(missing);
        s=s with{Constraints=s.Constraints.Replace(proof,proof with{IsEnabled=false})};
        var report=await new ManagedSketchConstraintSolver(Kernel).SolveAsync(s);
        Assert.False(report.Succeeded);Assert.Null(report.Solution);Assert.Equal("SKETCH.SINGULAR_EVALUATION",report.Diagnostic);
        Kernel.ConfirmExpressionRegions(s);
    }

    private sealed class CheckingProvider(HashSet<SketchEntityId> naturalSources):IExactSketchExpressionRegions
    {
        public int NaturalRequests {get;private set;}
        public int NativeRequests {get;private set;}
        public int OffManifoldNaturalRequests {get;private set;}
        public ExactExpressionRegionOffset OffsetExpressionRegions(ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> loops,double distance,ExactExpressionRegionJoin join)
        {
            bool natural=loops.SelectMany(l=>l).All(s=>naturalSources.Contains(s.Id));
            if(natural)
            {
                NaturalRequests++;
                foreach(var loop in loops)for(int i=0;i<loop.Length;i++)
                {
                    var before=loop[i];var after=loop[(i+1)%loop.Length];
                    var a=before.Geometry.Evaluate(before.Reversed?0:1);var b=after.Geometry.Evaluate(after.Reversed?1:0);
                    double cross=(a.First.X*b.First.Y-a.First.Y*b.First.X)/(double.Hypot(a.First.X,a.First.Y)*double.Hypot(b.First.X,b.First.Y));
                    if(double.Hypot(a.Point.X-b.Point.X,a.Point.Y-b.Point.Y)>1e-7||Math.Abs(cross)>1e-9)OffManifoldNaturalRequests++;
                }
            }
            else NativeRequests++;
            return Kernel.OffsetExpressionRegions(loops,distance,join);
        }
    }
}
