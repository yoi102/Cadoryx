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

public sealed class SketchRegionNaturalFreeformChartTests(ITestOutputHelper output)
{
    private static readonly OcctGeometryKernel Kernel=new();
    internal static CadSketch Ring(bool spline,bool mirror,bool curvature,double distance)
    {
        const double angle=.29;
        Point2d P(double x,double y)
        {if(mirror)x=-x;return new(1+x*Math.Cos(angle)-y*Math.Sin(angle),-3+x*Math.Sin(angle)+y*Math.Cos(angle));}
        Point2d[] ends=[P(0,-2),P(2,0),P(0,2),P(-2,0)];
        Point2d[] handles=[P(2,-2),P(2,2),P(-2,2),P(-2,-2)];
        var draft=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Natural polynomial ring",RigidTransform3d.Identity));
        var curves=new List<SketchEntityId>();var joints=new List<SketchEntityId>();
        SketchEntityId? first=null,last=null;
        for(int i=0;i<4;i++)
        {
            var a=ends[i];var b=handles[i];var c=ends[(i+1)%4];
            if(spline)
            {
                // Exact degree elevation of the same quadratic, with a clamped
                // cubic author. Area/perimeter below are independent formulas.
                draft.AddSpline([a,new((a.X+2*b.X)/3,(a.Y+2*b.Y)/3),new((c.X+2*b.X)/3,(c.Y+2*b.Y)/3),c],
                    existingStart:last,existingEnd:i==3?first:null);
                var curve=draft.Value.Splines[^1];curves.Add(curve.Id);first??=curve.Controls[0];last=curve.Controls[^1];
            }
            else
            {
                draft.AddBezier(a,b,c,existingStart:last,existingEnd:i==3?first:null);
                var curve=draft.Value.Beziers[^1];curves.Add(curve.Id);first??=curve.Start;last=curve.End;
            }
            joints.Add(last.Value);
        }
        draft.Change(s=>s with{Constraints=Enumerable.Range(0,4).Select(i=>curvature?
            (SketchConstraint)new EndpointCurvatureConstraint(SketchConstraintId.New(),curves[i],curves[(i+1)%4],joints[i]):
            new EndpointTangentConstraint(SketchConstraintId.New(),curves[i],curves[(i+1)%4],joints[i])).ToImmutableArray()});
        return SketchRegionOffsetAuthoring.Create(draft.Value,[curves[0]],distance,SketchRegionAuthorJoin.Miter,Kernel).Sketch;
    }

    public static IEnumerable<object[]> Cases()=>from spline in new[]{false,true} from mirror in new[]{false,true}
        from curvature in new[]{false,true} from distance in new[]{.1,-.1} select new object[]{spline,mirror,curvature,distance};

    [Theory,MemberData(nameof(Cases))]
    public async Task PolynomialG1AndG2SourcesMoveWithExactOffsetOutputsAndIndependentSolidVolume(bool spline,bool mirror,bool curvature,double distance)
    {
        var original=Ring(spline,mirror,curvature,distance);
        var sources=original.Beziers.SelectMany(b=>new[]{b.Start,b.Control,b.End})
            .Concat(original.Splines.SelectMany(s=>s.Controls)).ToHashSet();
        const double factor=1.12;
        var authored=original with{Constraints=original.Constraints.AddRange(original.Points.Where(p=>sources.Contains(p.Id))
            .Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,new(2+factor*p.Position.X,-1+factor*p.Position.Y))))};
        authored.Validate();
        var requests=new ClosedRequestProvider();
        var result=await new ManagedSketchConstraintSolver(requests,Kernel).SolveAsync(authored);
        output.WriteLine($"spline={spline};mirror={mirror};G2={curvature};distance={distance:R};iterations={result.Iterations};diagnostic={result.Diagnostic};residual={result.MaxNormalizedResidual:R}");
        Assert.True(result.Succeeded,result.Diagnostic);var solved=result.Solution!;
        Assert.Equal(0,requests.OffManifoldRequests);
        Assert.InRange(result.Iterations,1,80);Assert.InRange(result.VariableCount,1,256);Assert.InRange(result.EquationCount,1,512);
        Assert.Equal(authored.Points.Length*2,result.VariableCount);
        Assert.Equal(sources.Count*2+(curvature?8:4)+authored.ExpressionCurves.Length*4,result.EquationCount);
        Assert.Equal(original.Points.Select(p=>p.Id),solved.Points.Select(p=>p.Id));
        Assert.Equal(original.ExpressionCurves.Select(c=>c.Id),solved.ExpressionCurves.Select(c=>c.Id));
        Assert.Equal(original.ExpressionCurves.Select(c=>c.Endpoints),solved.ExpressionCurves.Select(c=>c.Endpoints));
        Assert.All(solved.RegionOffsets[0].Loops[0].Joints,j=>Assert.Equal(SketchRegionJointKind.SourceEndpoints,j.Kind));
        Assert.True(SketchRegionJointAuthors.SameStructure(original.RegionOffsets[0].Loops[0].Joints,solved.RegionOffsets[0].Loops[0].Joints));
        foreach(var fixedPoint in authored.Constraints.OfType<FixPointConstraint>())
        {
            var actual=solved.Points.Single(p=>p.Id==fixedPoint.Point).Position;
            Assert.InRange(double.Hypot(actual.X-fixedPoint.Position.X,actual.Y-fixedPoint.Position.Y),0,1e-7);
        }
        if(curvature)SketchEndpointCurvatureTests.RequireG2(solved);
        new SketchRegionEvaluationContext(Kernel).ValidateSolved(authored,solved);
        SketchAssociativeEndpoints.RequireSolved(solved);Kernel.ConfirmExpressionRegions(solved);
        var(doc,part)=SketchCommandTests.Document();solved=solved with{PartId=part};doc=doc with{Sketches=doc.Sketches.Add(solved.Id,solved)};
        var root=Assert.Single(SketchRegionAuthorModel.Roots(solved,solved.RegionOffsets[0].Id));
        var profile=new SketchProfileReference(solved.Id,solved.Revision,[]){MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(doc,part,new ExtrudeRecipe(new([]),3,solved.Plane)),"Natural polynomial offset",part,sketchSource:profile));
        double radius=2*factor,area=10*radius*radius/3;
        double perimeter=4*radius*(1+Math.Asinh(1)/Math.Sqrt(2));
        double expected=3*(area+distance*perimeter+Math.PI*distance*distance);
        Assert.InRange(Math.Abs(Assert.Single(session.Snapshot.Features.Values).Result.VolumeMm3-expected),0,1e-7);
        Assert.Equal(original.RegionOffsets,authored.RegionOffsets);
    }

    private sealed class ClosedRequestProvider:IExactSketchExpressionRegions
    {
        public int OffManifoldRequests {get;private set;}
        public ExactExpressionRegionOffset OffsetExpressionRegions(ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> loops,double distance,ExactExpressionRegionJoin join)
        {
            foreach(var loop in loops)for(int i=0;i<loop.Length;i++)
            {
                var previous=loop[i];var next=loop[(i+1)%loop.Length];
                var a=previous.Geometry.Evaluate(previous.Reversed?0:1);var b=next.Geometry.Evaluate(next.Reversed?1:0);
                double cross=(a.First.X*b.First.Y-a.First.Y*b.First.X)/
                    (double.Hypot(a.First.X,a.First.Y)*double.Hypot(b.First.X,b.First.Y));
                if(double.Hypot(a.Point.X-b.Point.X,a.Point.Y-b.Point.Y)>1e-7||Math.Abs(cross)>1e-9)OffManifoldRequests++;
            }
            return Kernel.OffsetExpressionRegions(loops,distance,join);
        }
    }
}
