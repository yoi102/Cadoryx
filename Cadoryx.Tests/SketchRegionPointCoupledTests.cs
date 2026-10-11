using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using MathNet.Numerics.LinearAlgebra;
using Xunit;
using Xunit.Abstractions;

namespace Cadoryx.Tests;

public sealed class SketchRegionPointCoupledTests(ITestOutputHelper output)
{
    private static readonly OcctGeometryKernel Kernel=new();
    public static IEnumerable<object[]> Cases()=>from kind in new[]{"capsule","bezier","spline"}
        from direction in new[]{false,true} from mirror in new[]{false,true}
        from distance in new[]{.1,-.1} from joinKind in Enum.GetValues<SketchRegionAuthorJoin>()
        select new object[]{kind,direction,mirror,distance,joinKind};

    [Theory,MemberData(nameof(Cases))]
    public async Task RealPointCouplingSolvesEveryOriginalStructuralRowAndBothMaterialsWithoutOffManifoldNaturalRequests(
        string kind,bool naturalConsumesNative,bool mirror,double distance,SketchRegionAuthorJoin join)
    {
        var fixture=PointCoupledFixture.Create(kind,naturalConsumesNative,mirror,distance,join);
        var original=fixture.Sketch;PointCoupledFixture.RequireProof(fixture);
        var independent=PointCoupledFixture.IndependentSourcePoints(original);
        var authored=original with{Constraints=original.Constraints.AddRange(original.Points.Where(p=>independent.Contains(p.Id))
            .Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,new(p.Position.X+.4,p.Position.Y-.25))))};
        var naturalSources=original.RegionOffsets.Single(g=>g.Id==fixture.Natural).Sources.SelectMany(l=>l).Select(s=>s.Entity).ToHashSet();
        var checking=new CheckingProvider(naturalSources);
        var report=await new ManagedSketchConstraintSolver(checking,Kernel).SolveAsync(authored);
        output.WriteLine($"kind={kind};direction={naturalConsumesNative};mirror={mirror};distance={distance:R};join={join};iterations={report.Iterations};variables={report.VariableCount};equations={report.EquationCount};natural={checking.NaturalRequests};native={checking.NativeRequests};offManifold={checking.OffManifoldNaturalRequests};diagnostic={report.Diagnostic};residual={report.MaxNormalizedResidual:R}");
        Assert.True(report.Succeeded,report.Diagnostic);var solved=report.Solution!;
        Assert.Equal(0,checking.OffManifoldNaturalRequests);Assert.InRange(checking.NaturalRequests,1,int.MaxValue);Assert.InRange(checking.NativeRequests,2,int.MaxValue);
        Assert.Equal(authored.Points.Length*2,report.VariableCount);
        Assert.Equal(independent.Count*2+(kind=="capsule"?4:8)+authored.ExpressionCurves.Length*4,report.EquationCount);
        Assert.InRange(report.Iterations,1,80);Assert.InRange(report.VariableCount,1,256);Assert.InRange(report.EquationCount,1,512);
        Assert.Equal(original.Points.Select(p=>p.Id),solved.Points.Select(p=>p.Id));
        Assert.Equal(original.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)),solved.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)));
        foreach(var point in original.Points)
        {
            var actual=solved.Points.Single(p=>p.Id==point.Id).Position;
            double error=double.Hypot(actual.X-point.Position.X-.4,actual.Y-point.Position.Y+.25);
            var fixes=string.Join("|",authored.Constraints.OfType<FixPointConstraint>().Where(c=>c.Point==point.Id).Select(c=>c.Position));
            Assert.True(error<=1e-7,$"point={point.Id};origin={point.Origin};independent={independent.Contains(point.Id)};before={point.Position};actual={actual};error={error:R};fixes={fixes}");
        }
        foreach(var group in original.RegionOffsets)
            Assert.True(SketchRegionJointAuthors.SameStructure(group.Loops[0].Joints,solved.RegionOffsets.Single(g=>g.Id==group.Id).Loops[0].Joints));
        new SketchRegionEvaluationContext(Kernel).ValidateSolved(authored,solved);SketchAssociativeEndpoints.RequireSolved(solved);Kernel.ConfirmExpressionRegions(solved);
        var(doc,part)=SketchCommandTests.Document();solved=PointCoupledFixture.MaterialCurves(solved with{PartId=part});doc=doc with{Sketches=doc.Sketches.Add(solved.Id,solved)};
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        foreach(var group in solved.RegionOffsets)
        {
            var root=Assert.Single(SketchRegionAuthorModel.Roots(solved,group.Id));
            var profile=new SketchProfileReference(solved.Id,solved.Revision,[]){MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};
            var before=session.Snapshot.Features.Keys.ToHashSet();
            await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,solved.Plane)),"Coupled material",part,sketchSource:profile));
            var feature=Assert.Single(session.Snapshot.Features.Values,f=>!before.Contains(f.Id));
            Assert.InRange(Math.Abs(feature.Result.VolumeMm3-PointCoupledFixture.Expected(kind,group.Id==fixture.Natural,join,distance,1)),0,1e-7);
        }
    }

    public static IEnumerable<object[]> JacobianCases()=>from kind in new[]{"capsule","bezier","spline"}
        from direction in new[]{false,true} from mirror in new[]{false,true} from factor in new[]{.7,1.4}
        select new object[]{kind,direction,mirror,factor};
    [Theory,MemberData(nameof(JacobianCases))]
    public void FullResidualJacobianAgreesWithIndependentCentralDifferenceForEverySourceAndGeneratedColumn(
        string kind,bool naturalConsumesNative,bool mirror,double factor)
    {
        var fixture=PointCoupledFixture.Create(kind,naturalConsumesNative,mirror,.1,SketchRegionAuthorJoin.Round);
        const double angle=.21;Point2d Transform(Point2d p)=>new(17+factor*(p.X*Math.Cos(angle)-p.Y*Math.Sin(angle)),-11+factor*(p.X*Math.Sin(angle)+p.Y*Math.Cos(angle)));
        var transformed=fixture.Sketch with{Points=fixture.Sketch.Points.Select(p=>p with{Position=Transform(p.Position)}).ToImmutableArray()};
        var overrides=fixture.Sketch.RegionOffsets.ToDictionary(g=>g.Id,g=>g.Distance*factor);
        transformed=new SketchRegionEvaluationContext(Kernel).Refresh(transformed,synchronizeGeneratedPoints:true,distanceOverrides:overrides);
        fixture=fixture with{Sketch=transformed};PointCoupledFixture.RequireProof(fixture);
        var source=PointCoupledFixture.IndependentSourcePoints(transformed);
        var authored=transformed with{Constraints=transformed.Constraints.AddRange(transformed.Points.Where(p=>source.Contains(p.Id))
            .Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)))};
        var type=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchEquationSystem",true)!;
        var options=new SketchSolveOptions();
        var system=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,
            [authored,options,new SketchRegionEvaluationContext(Kernel),CancellationToken.None],CultureInfo.InvariantCulture)!;
        var x=((double[])type.GetProperty("Initial",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(system)!).ToArray();
        var residual=type.GetMethod("Residual",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var jacobian=(Matrix<double>)type.GetMethod("Jacobian",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(system,[x,CancellationToken.None])!;
        var first=(double[])residual.Invoke(system,[x])!;Assert.Equal(first.Length,jacobian.RowCount);Assert.Equal(x.Length,jacobian.ColumnCount);
        // Undo each row's tolerance multiplier before the independent difference.
        // A 1e9 multiplier otherwise turns rounding in a true zero derivative
        // into a false .005 discrepancy. All rows/columns and3e-5 remain checked.
        double normalizationScale=(double)type.GetField("scale",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(system)!;
        var rowUnits=Enumerable.Repeat(options.LinearToleranceMm/normalizationScale,first.Length).ToArray();
        var groups=((IEnumerable)type.GetProperty("Groups",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(system)!).Cast<object>();
        foreach(var group in groups)
        {
            var groupType=group.GetType();var constraint=(SketchConstraint)groupType.GetProperty("Constraint")!.GetValue(group)!;
            int start=(int)groupType.GetProperty("Start")!.GetValue(group)!;
            Assert.True(constraint is FixPointConstraint or SketchExpressionEndpointEquation or EndpointTangentConstraint or EndpointCurvatureConstraint);
            if(constraint is EndpointTangentConstraint or EndpointCurvatureConstraint)rowUnits[start]=options.AngularTolerance;
            if(constraint is EndpointCurvatureConstraint)rowUnits[start+1]=options.CurvatureTolerancePerMm*normalizationScale;
        }
        const double h=1e-6;double largest=0;
        for(int column=0;column<x.Length;column++)
        {
            var plus=x.ToArray();var minus=x.ToArray();plus[column]+=h;minus[column]-=h;
            var a=(double[])residual.Invoke(system,[plus])!;var b=(double[])residual.Invoke(system,[minus])!;
            for(int row=0;row<first.Length;row++)
            {
                double expected=(a[row]*rowUnits[row]-b[row]*rowUnits[row])/(2*h),actual=jacobian[row,column]*rowUnits[row];
                double difference=Math.Abs(expected-actual)/Math.Max(1,Math.Max(Math.Abs(expected),Math.Abs(actual)));
                largest=Math.Max(largest,difference);Assert.True(double.IsFinite(actual)&&difference<=3e-5,$"row={row},column={column},expected={expected:R},actual={actual:R},relative={difference:R}");
            }
        }
        output.WriteLine($"kind={kind};direction={naturalConsumesNative};mirror={mirror};scale={factor:R};rows={first.Length};columns={x.Length};largestRelativeDifference={largest:R}");
    }

    [Theory][InlineData(1,false)][InlineData(3,false)][InlineData(7,false)]
    [InlineData(1,true)][InlineData(3,true)][InlineData(7,true)]
    public void PointRouteStillRefusesIntermediateLiveGeometryProvidersAndStopsAtExplicitFrozenSources(int depth,bool frozen)
    {
        var source=SketchRegionNaturalChartTests.Capsule();var draft=new SketchDraft(source,Kernel);var curve=source.RegionOffsets.Single().Loops[0].Outputs[0].Curve;
        for(int i=0;i<depth;i++){curve=draft.AddExactOffset(curve,.015);draft.AttachExpressionEndpoints(curve);}
        if(frozen)draft.FreezeExpression(curve);
        var point=draft.Value.ExpressionCurves.Single(e=>e.Id==curve).Endpoints!.Start;
        var candidate=PointCoupledFixture.Rectangle(draft.Value,point,false,.1,SketchRegionAuthorJoin.Round).Sketch;
        candidate.Validate();Kernel.ConfirmExpressionRegions(candidate);
        var chart=PointCoupledFixture.Chart(candidate,"TryCreateForEquations");
        if(frozen){Assert.NotNull(chart);Assert.False((bool)chart.GetType().GetProperty("IsPointCoupled",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(chart)!);}
        else Assert.Null(chart);
    }

    public static IEnumerable<object[]> IdentityCases()=>from kind in new[]{"capsule","bezier","spline"}
        from direction in new[]{false,true} from mirror in new[]{false,true}
        select new object[]{kind,direction,mirror};
    [Theory,MemberData(nameof(IdentityCases))]
    public void EqualCoordinatesWithAnExplicitNewIndependentIdRemoveTheProviderEdgeWithoutGuessingAnIdentity(
        string kind,bool naturalConsumesNative,bool mirror)
    {
        var fixture=PointCoupledFixture.Create(kind,naturalConsumesNative,mirror,.1,SketchRegionAuthorJoin.Round);PointCoupledFixture.RequireProof(fixture);
        var original=fixture.Sketch;var shared=original.Points.Single(p=>p.Id==fixture.Shared);var free=SketchEntityId.New();
        var consumer=naturalConsumesNative?fixture.Natural:fixture.Native;
        var sources=original.RegionOffsets.Single(g=>g.Id==consumer).Sources.SelectMany(l=>l).Select(s=>s.Entity).ToHashSet();
        var independent=PointCoupledFixture.ReplaceVertex(original with{Points=original.Points.Add(new(free,shared.Position){Origin=SketchPointOrigin.Independent})},sources,fixture.Shared,free);
        independent.Validate();Kernel.ConfirmExpressionRegions(independent);Assert.Equal(shared.Position,independent.Points.Single(p=>p.Id==free).Position);
        var chart=PointCoupledFixture.Chart(independent,"TryCreateForEquations");Assert.NotNull(chart);
        Assert.False((bool)chart.GetType().GetProperty("IsPointCoupled",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(chart)!);
        Assert.NotNull(PointCoupledFixture.Chart(independent,"TryCreate"));
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

internal sealed record PointCoupledFixture(CadSketch Sketch,Guid Natural,Guid Native,SketchEntityId Shared)
{
    private static readonly OcctGeometryKernel Kernel=new();
    internal static PointCoupledFixture Create(string kind,bool naturalConsumesNative,bool mirror,double distance,SketchRegionAuthorJoin join)
    {
        var original=kind=="capsule"?SketchRegionNaturalChartTests.Capsule(mirror,distance):SketchRegionNaturalFreeformChartTests.Ring(kind=="spline",mirror,true,distance);
        if(!naturalConsumesNative)
        {
            var natural=original.RegionOffsets.Single().Id;var shared=original.RegionOffsets.Single().Loops[0].Outputs[0].Endpoints.Start;
            var(rectangle,native)=Rectangle(original,shared,mirror,distance,join);return new(rectangle,natural,native,shared);
        }
        var sourceIds=original.RegionOffsets.Single().Sources.SelectMany(l=>l).Select(s=>s.Entity).ToHashSet();var pointIds=SourcePoints(original);
        var bare=original with{RegionOffsets=[],ExpressionCurves=[],Points=original.Points.Where(p=>pointIds.Contains(p.Id)).ToImmutableArray()};
        var(nativeSketch,nativeGroup)=Rectangle(bare,null,mirror,distance,join);nativeSketch=ConstructionProviders(nativeSketch);
        var generated=nativeSketch.RegionOffsets.Single(g=>g.Id==nativeGroup).Loops[0].Outputs[0].Endpoints.Start;
        var previous=kind=="capsule"?original.Lines[0].Start:kind=="bezier"?original.Beziers[0].Start:original.Splines[0].Controls[0];
        var old=original.Points.Single(p=>p.Id==previous).Position;var replacement=nativeSketch.Points.Single(p=>p.Id==generated).Position;
        nativeSketch=nativeSketch with{Points=nativeSketch.Points.Where(p=>p.Id!=previous).Select(p=>pointIds.Contains(p.Id)?p with{Position=new(p.Position.X+replacement.X-old.X,p.Position.Y+replacement.Y-old.Y)}:p).ToImmutableArray()};
        nativeSketch=ReplaceVertex(nativeSketch,sourceIds,previous,generated);var draft=new SketchDraft(nativeSketch,Kernel);
        var naturalGroup=draft.OffsetExpressionRegions(sourceIds,distance,SketchRegionAuthorJoin.Miter,Kernel);return new(draft.Value,naturalGroup,nativeGroup,generated);
    }
    internal static (CadSketch Sketch,Guid Group) Rectangle(CadSketch sketch,SketchEntityId? generated,bool mirror,double distance,SketchRegionAuthorJoin join)
    {
        if(generated is not null)sketch=ConstructionProviders(sketch);
        var position=generated is {} id?sketch.Points.Single(p=>p.Id==id).Position:new Point2d(30,10);
        var points=new List<SketchPoint>();var first=generated??SketchEntityId.New();
        if(generated is null)points.Add(new(first,position){Origin=SketchPointOrigin.Independent});double width=mirror?-8:8;
        foreach(var offset in new[]{new Point2d(width,0),new Point2d(width,5),new Point2d(0,5)})points.Add(new(SketchEntityId.New(),new(position.X+offset.X,position.Y+offset.Y)){Origin=SketchPointOrigin.Independent});
        var ids=new[]{first}.Concat(points.TakeLast(3).Select(p=>p.Id)).ToArray();
        var lines=Enumerable.Range(0,4).Select(i=>new SketchLine(SketchEntityId.New(),ids[i],ids[(i+1)%4])).ToImmutableArray();
        var draft=new SketchDraft(sketch with{Points=sketch.Points.AddRange(points),Lines=sketch.Lines.AddRange(lines)},Kernel);
        var group=draft.OffsetExpressionRegions(lines.Select(l=>l.Id),distance,join,Kernel);return(draft.Value,group);
    }
    internal static HashSet<SketchEntityId> SourcePoints(CadSketch s)=>s.Lines.SelectMany(l=>new[]{l.Start,l.End}).Concat(s.Arcs.SelectMany(a=>new[]{a.Start,a.Middle,a.End}))
        .Concat(s.Beziers.SelectMany(b=>new[]{b.Start,b.Control,b.End})).Concat(s.Splines.SelectMany(sp=>sp.Controls)).ToHashSet();
    internal static HashSet<SketchEntityId> IndependentSourcePoints(CadSketch sketch)
    {
        // Origin records how a point was created and collected. An authored line,
        // arc or polynomial handle may be CurveGenerated without an equation.
        // Only actual declared structural providers exclude a source prescription.
        var providers=SketchAssociativeEndpoints.Equations(sketch).Select(e=>e.Point).ToHashSet();
        var points=SourcePoints(sketch);points.ExceptWith(providers);return points;
    }
    private static CadSketch ConstructionProviders(CadSketch sketch)=>sketch with{ExpressionCurves=sketch.ExpressionCurves.Select(e=>e with{IsConstruction=true}).ToImmutableArray()};
    internal static CadSketch MaterialCurves(CadSketch sketch)=>sketch with{ExpressionCurves=sketch.ExpressionCurves.Select(e=>e with{IsConstruction=false}).ToImmutableArray()};
    internal static CadSketch ReplaceVertex(CadSketch sketch,HashSet<SketchEntityId> sources,SketchEntityId from,SketchEntityId to)
    {
        SketchEntityId Map(SketchEntityId id)=>id==from?to:id;
        return sketch with{Lines=sketch.Lines.Select(l=>sources.Contains(l.Id)?l with{Start=Map(l.Start),End=Map(l.End)}:l).ToImmutableArray(),
            Arcs=sketch.Arcs.Select(a=>sources.Contains(a.Id)?a with{Start=Map(a.Start),Middle=Map(a.Middle),End=Map(a.End)}:a).ToImmutableArray(),
            Beziers=sketch.Beziers.Select(b=>sources.Contains(b.Id)?b with{Start=Map(b.Start),Control=Map(b.Control),End=Map(b.End)}:b).ToImmutableArray(),
            Splines=sketch.Splines.Select(s=>sources.Contains(s.Id)?s with{Controls=s.Controls.Select(Map).ToImmutableArray()}:s).ToImmutableArray(),
            Constraints=sketch.Constraints.Select(c=>c switch{EndpointTangentConstraint t when sources.Contains(t.A)&&sources.Contains(t.B)=>t with{Joint=Map(t.Joint)},
                EndpointCurvatureConstraint t when sources.Contains(t.A)&&sources.Contains(t.B)=>t with{Joint=Map(t.Joint)},_=>c}).ToImmutableArray()};
    }
    internal static object? Chart(CadSketch sketch,string method)=>typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchRegionSolveChart",true)!
        .GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[sketch]);
    internal static void RequireProof(PointCoupledFixture fixture)
    {
        var s=fixture.Sketch;s.Validate();Kernel.ConfirmExpressionRegions(s);Assert.Null(Chart(s,"TryCreate"));
        var chart=Chart(s,"TryCreateForEquations");Assert.NotNull(chart);var type=chart.GetType();
        Assert.True((bool)type.GetProperty("IsPointCoupled",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(chart)!);
        var ids=(ImmutableHashSet<Guid>)type.GetProperty("GroupIds",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(chart)!;Assert.Equal(fixture.Natural,Assert.Single(ids));
        var proof=type.GetProperty("PointCouplingProof",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(chart)!;
        var paths=((IEnumerable)proof.GetType().GetProperty("Couplings",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(proof)!).Cast<object>().ToArray();Assert.NotEmpty(paths);
        foreach(var path in paths)
        {
            object P(string name)=>path.GetType().GetProperty(name)!.GetValue(path)!;
            var point=(SketchEntityId)P("Point");var consumer=(Guid)P("ConsumerGroup");var source=(SketchEntityId)P("SourceCurve");var provider=(Guid)P("ProviderGroup");var curve=(SketchEntityId)P("ProviderCurve");
            Assert.Equal(fixture.Shared,point);Assert.NotEqual(consumer,provider);Assert.Equal(SketchPointOrigin.CurveGenerated,s.Points.Single(p=>p.Id==point).Origin);
            Assert.Contains(s.RegionOffsets.Single(g=>g.Id==consumer).Sources.SelectMany(l=>l),a=>a.Entity==source);
            var output=Assert.Single(s.RegionOffsets.Single(g=>g.Id==provider).Loops.SelectMany(l=>l.Outputs),o=>o.Curve==curve);
            Assert.True(output.Endpoints.Start==point||output.Endpoints.End==point);
        }
    }
    internal static double Expected(string kind,bool natural,SketchRegionAuthorJoin join,double distance,double factor)
    {
        double d=distance*factor;
        if(!natural){double a=8*factor,b=5*factor;return 3*(d<0||join==SketchRegionAuthorJoin.Miter?(a+2*d)*(b+2*d):a*b+2*d*(a+b)+(join==SketchRegionAuthorJoin.Round?Math.PI:2)*d*d);}
        double radius=2*factor,area=kind=="capsule"?16*factor*radius+Math.PI*radius*radius:10*radius*radius/3;
        double perimeter=kind=="capsule"?16*factor+Math.Tau*radius:4*radius*(1+Math.Asinh(1)/Math.Sqrt(2));return 3*(area+d*perimeter+Math.PI*d*d);
    }
}
