using System.Collections.Immutable;
using System.Reflection;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Xunit;
using Xunit.Abstractions;

namespace Cadoryx.Tests;

public sealed class SketchRegionNaturalChartTests(ITestOutputHelper output)
{
    private static readonly OcctGeometryKernel Kernel=new();
    internal static CadSketch Capsule(bool mirror=false,double distance=.1)
    {
        const double angle=.38;
        Point2d P(double x,double y)
        {if(mirror)x=-x;return new(3+x*Math.Cos(angle)-y*Math.Sin(angle),-2+x*Math.Sin(angle)+y*Math.Cos(angle));}
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Proven natural chart",RigidTransform3d.Identity));
        d.AddLine(P(0,-2),P(8,-2));var first=d.Value.Lines[0];
        d.AddArc(P(8,-2),P(10,0),P(8,2),first.End);var right=d.Value.Arcs[0];
        d.AddLine(P(8,2),P(0,2),right.End);var last=d.Value.Lines[^1];
        d.AddArc(P(0,2),P(-2,0),P(0,-2),last.End,existingEnd:first.Start);var left=d.Value.Arcs[^1];
        d.Change(s=>s with{Constraints=s.Constraints.AddRange(new[]{
            new EndpointTangentConstraint(SketchConstraintId.New(),first.Id,right.Id,first.End),
            new EndpointTangentConstraint(SketchConstraintId.New(),right.Id,last.Id,right.End),
            new EndpointTangentConstraint(SketchConstraintId.New(),last.Id,left.Id,last.End),
            new EndpointTangentConstraint(SketchConstraintId.New(),left.Id,first.Id,left.End)})});
        return SketchRegionOffsetAuthoring.Create(d.Value,[first.Id],distance,SketchRegionAuthorJoin.Miter,Kernel).Sketch;
    }
    private static HashSet<SketchEntityId> SourcePoints(CadSketch s)=>s.Lines.SelectMany(l=>new[]{l.Start,l.End})
        .Concat(s.Arcs.SelectMany(a=>new[]{a.Start,a.Middle,a.End})).ToHashSet();

    [Theory][InlineData(false,.1)][InlineData(true,.1)][InlineData(false,-.1)][InlineData(true,-.1)]
    public async Task EnabledIdentityBasedG1MovesSourcesAndOutputsInOneBudgetWithoutOffManifoldNativeRequests(bool mirror,double distance)
    {
        var original=Capsule(mirror,distance);var ids=SourcePoints(original);
        const double factor=1.2;
        var authored=original with{Constraints=original.Constraints.AddRange(original.Points.Where(p=>ids.Contains(p.Id))
            .Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,new(2+factor*p.Position.X,-1+factor*p.Position.Y))))};
        authored.Validate();var provider=new ClosedRequestProvider();
        var result=await new ManagedSketchConstraintSolver(provider,Kernel).SolveAsync(authored);
        output.WriteLine($"mirror={mirror};distance={distance:R};iterations={result.Iterations};nativeRequests={provider.Requests};diagnostic={result.Diagnostic};residual={result.MaxNormalizedResidual:R}");
        Assert.True(result.Succeeded,result.Diagnostic);var solved=result.Solution!;
        Assert.InRange(result.Iterations,1,80);Assert.InRange(result.VariableCount,1,256);Assert.InRange(result.EquationCount,1,512);
        Assert.Equal(0,provider.OffManifoldRequests);
        Assert.Equal(authored.Points.Length*2,result.VariableCount);
        Assert.Equal(ids.Count*2+4+authored.ExpressionCurves.Length*4,result.EquationCount);
        Assert.Equal(authored.Points.Select(p=>p.Id),solved.Points.Select(p=>p.Id));
        Assert.Equal(authored.ExpressionCurves.Select(c=>c.Id),solved.ExpressionCurves.Select(c=>c.Id));
        Assert.Equal(authored.ExpressionCurves.Select(c=>c.Endpoints),solved.ExpressionCurves.Select(c=>c.Endpoints));
        Assert.True(SketchRegionJointAuthors.SameStructure(authored.RegionOffsets[0].Loops[0].Joints,solved.RegionOffsets[0].Loops[0].Joints));
        new SketchRegionEvaluationContext(Kernel).ValidateSolved(authored,solved);SketchAssociativeEndpoints.RequireSolved(solved);
        foreach(var fixedPoint in authored.Constraints.OfType<FixPointConstraint>())
        {
            var actual=solved.Points.Single(p=>p.Id==fixedPoint.Point).Position;
            Assert.InRange(double.Hypot(actual.X-fixedPoint.Position.X,actual.Y-fixedPoint.Position.Y),0,1e-7);
        }
        var(doc,part)=SketchCommandTests.Document();solved=solved with{PartId=part};doc=doc with{Sketches=doc.Sketches.Add(solved.Id,solved)};
        var root=Assert.Single(SketchRegionAuthorModel.Roots(solved,solved.RegionOffsets[0].Id));
        var profile=new SketchProfileReference(solved.Id,solved.Revision,[]){MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(doc,part,new ExtrudeRecipe(new([]),3,solved.Plane)),"Verified capsule",part,sketchSource:profile));
        double radius=2*factor,length=8*factor,area=length*2*radius+Math.PI*radius*radius+distance*(2*length+Math.Tau*radius)+Math.PI*distance*distance;
        Assert.InRange(Math.Abs(Assert.Single(session.Snapshot.Features.Values).Result.VolumeMm3-3*area),0,1e-7);
        Assert.Equal(original.RegionOffsets,authored.RegionOffsets);
    }

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public async Task DisablingAnyRequiredJoinKeepsTheOriginalSingularRefusal(int join)
    {
        var s=Capsule();var relation=s.Constraints.OfType<EndpointTangentConstraint>().ElementAt(join);
        s=s with{Constraints=s.Constraints.Replace(relation,relation with{IsEnabled=false})};s.Validate();
        var result=await new ManagedSketchConstraintSolver(Kernel).SolveAsync(s);
        Assert.False(result.Succeeded);Assert.Null(result.Solution);Assert.Equal("SKETCH.SINGULAR_EVALUATION",result.Diagnostic);
        Kernel.ConfirmExpressionRegions(s);
    }
    [Fact] public async Task RemovingRelationOrLegacyEmptyBranchCannotReuseAnEarlierSolveProof()
    {
        var s=Capsule();Assert.True((await new ManagedSketchConstraintSolver(Kernel).SolveAsync(s)).Succeeded);
        var removed=s with{Constraints=s.Constraints.Remove(s.Constraints.OfType<EndpointTangentConstraint>().First())};
        var legacy=s with{RegionOffsets=s.RegionOffsets.Select(g=>g with{Algorithm=SketchRegionJointAuthors.LegacyAlgorithm,
            Loops=g.Loops.Select(l=>l with{Joints=[]}).ToImmutableArray()}).ToImmutableArray()};
        foreach(var value in new[]{removed,legacy})
        {
            value.Validate();var result=await new ManagedSketchConstraintSolver(Kernel).SolveAsync(value);
            Assert.False(result.Succeeded);Assert.Null(result.Solution);Assert.Equal("SKETCH.SINGULAR_EVALUATION",result.Diagnostic);
        }
        Kernel.ConfirmExpressionRegions(s);
    }

    [Theory][InlineData(false,0d)][InlineData(false,.37)][InlineData(false,1d)]
    [InlineData(true,0d)][InlineData(true,.37)][InlineData(true,1d)]
    public void FullNaturalBoundsHaveEverySourceCoordinateGradientIncludingMovingLineLengthAndArcAngles(bool mirror,double parameterValue)
    {
        var s=Capsule(mirror);var assembly=typeof(ManagedSketchConstraintSolver).Assembly;
        var chartType=assembly.GetType("Cadoryx.Sketching.SketchRegionSolveChart")!;
        var chart=chartType.GetMethod("TryCreate",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[s]);Assert.NotNull(chart);
        var scalar=assembly.GetType("Cadoryx.Sketching.SketchContactDifferentials")!.GetNestedType("Scalar",BindingFlags.NonPublic)!;
        var method=assembly.GetType("Cadoryx.Sketching.SketchExpressionDifferentials")!.GetMethod("EvaluateRegionContact",BindingFlags.Static|BindingFlags.NonPublic)!;
        var indices=s.Points.Select((p,i)=>(p.Id,Index:2*i)).ToDictionary(p=>p.Id,p=>p.Index);
        var x=s.Points.SelectMany(p=>new[]{p.Position.X,p.Position.Y}).Append(parameterValue).ToArray();var gradient=new double[x.Length];gradient[^1]=1;
        var parameter=Activator.CreateInstance(scalar,[parameterValue,gradient])!;
        object Prop(object value,string name)=>value.GetType().GetProperty(name)!.GetValue(value)!;
        double Value(object value)=>(double)Prop(value,"Value");double[] Gradient(object value)=>(double[])Prop(value,"Gradient");
        var loop=s.RegionOffsets[0].Loops[0];
        foreach(var output in loop.Outputs)
        {
            var actual=method.Invoke(null,[s,output.Curve,parameter,x,indices,new Dictionary<SketchEntityId,int>(),default(Point2d),1d,1,false,chart])!;
            var fields=new[]{Prop(Prop(actual,"Position"),"X"),Prop(Prop(actual,"Position"),"Y"),Prop(actual,"Speed")};
            double[] Oracle(double[] coordinates)
            {
                var trial=s with{Points=s.Points.Select(p=>p with{Position=new(coordinates[indices[p.Id]],coordinates[indices[p.Id]+1])}).ToImmutableArray()};
                var source=s.RegionOffsets[0].Sources[0][output.SourceSlots[0]];
                var basis=SketchExpressionEntities.Author(trial,source.Entity).Geometry;
                var geometry=basis.Offset(s.RegionOffsets[0].Distance*(output.SourceTraversalReversed?-1:1)) with{Reversed=output.Geometry.Reversed};
                var value=geometry.Evaluate(coordinates[^1]);return [value.Point.X,value.Point.Y,double.Hypot(value.First.X,value.First.Y)];
            }
            var expected=Oracle(x);for(int row=0;row<3;row++)Near(expected[row],Value(fields[row]),1e-8);
            foreach(var col in SourcePoints(s).SelectMany(id=>new[]{indices[id],indices[id]+1}))
            {
                const double h=2e-6;var plus=(double[])x.Clone();var minus=(double[])x.Clone();plus[col]+=h;minus[col]-=h;
                var a=Oracle(plus);var b=Oracle(minus);
                for(int row=0;row<3;row++)Near((a[row]-b[row])/(2*h),Gradient(fields[row])[col],3e-5);
            }
            var sourceColumns=SourcePoints(s).SelectMany(id=>new[]{indices[id],indices[id]+1}).Append(x.Length-1).ToHashSet();
            foreach(int col in Enumerable.Range(0,x.Length).Where(i=>!sourceColumns.Contains(i)))Assert.All(fields,f=>Assert.Equal(0,Gradient(f)[col]));
        }
    }
    private static void Near(double expected,double actual,double tolerance)=>Assert.InRange(Math.Abs(expected-actual),0,tolerance*Math.Max(1,Math.Abs(expected)));

    private sealed class ClosedRequestProvider:IExactSketchExpressionRegions
    {
        public int Requests {get;private set;}
        public int OffManifoldRequests {get;private set;}
        public ExactExpressionRegionOffset OffsetExpressionRegions(ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> loops,double distance,ExactExpressionRegionJoin join)
        {
            Requests++;
            foreach(var loop in loops)for(int i=0;i<loop.Length;i++)
            {
                var before=loop[i];var after=loop[(i+1)%loop.Length];
                var a=before.Geometry.Evaluate(before.Reversed?0:1);var b=after.Geometry.Evaluate(after.Reversed?1:0);
                double speedA=double.Hypot(a.First.X,a.First.Y),speedB=double.Hypot(b.First.X,b.First.Y);
                double cross=(a.First.X*b.First.Y-a.First.Y*b.First.X)/(speedA*speedB);
                if(double.Hypot(a.Point.X-b.Point.X,a.Point.Y-b.Point.Y)>1e-7||Math.Abs(cross)>1e-9)OffManifoldRequests++;
            }
            return Kernel.OffsetExpressionRegions(loops,distance,join);
        }
    }
}
