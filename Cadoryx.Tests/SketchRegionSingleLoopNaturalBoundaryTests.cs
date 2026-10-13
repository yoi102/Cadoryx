using System.Collections.Immutable;
using System.Reflection;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchRegionSingleLoopNaturalBoundaryTests
{
    // Exact analytic D boundary: three lines, one semicircle, two natural G1
    // corners and two native line intersections. No native runtime is invoked.
    internal static CadSketch AnalyticMixedBoundary(double distance=.1)
    {
        var draft=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Mixed D",RigidTransform3d.Identity));
        draft.AddLine(new(-4,-2),new(4,-2));var bottom=draft.Value.Lines[0];
        draft.AddArc(new(4,-2),new(6,0),new(4,2),bottom.End);var arc=draft.Value.Arcs[0];
        draft.AddLine(new(4,2),new(-4,2),arc.End);var top=draft.Value.Lines[^1];
        draft.AddLine(new(-4,2),new(-4,-2),top.End,bottom.Start);var left=draft.Value.Lines[^1];
        var sketch=draft.Value with{Constraints=draft.Value.Constraints.AddRange(new SketchConstraint[]{
            new EndpointTangentConstraint(SketchConstraintId.New(),bottom.Id,arc.Id,bottom.End),
            new EndpointTangentConstraint(SketchConstraintId.New(),arc.Id,top.Id,arc.End)})};
        var ids=new[]{bottom.Id,arc.Id,top.Id,left.Id};
        var sources=ids.Select(id=>{var a=SketchExpressionEntities.Author(sketch,id);return new SketchRegionSourceAuthor(id,a.Kind,a.Geometry,false);}).ToImmutableArray();
        var geometries=sources.Select(s=>s.Geometry.Offset(distance)).ToArray();
        double[][] bounds=[[-distance,8], [geometries[1].FirstParameter,geometries[1].LastParameter], [0,8+distance], [-distance,4+distance]];
        var outputIds=Enumerable.Range(0,4).Select(_=>SketchEntityId.New()).ToArray();
        var pointIds=Enumerable.Range(0,4).Select(_=>SketchEntityId.New()).ToArray();
        var outputs=ImmutableArray.CreateBuilder<SketchRegionOutputAuthor>();
        var expressions=ImmutableArray.CreateBuilder<SketchExpressionCurve>();
        var points=sketch.Points.ToBuilder();
        for(int i=0;i<4;i++)
        {
            var g=geometries[i];int depth=1;
            if(bounds[i][0]<g.FirstParameter||bounds[i][1]>g.LastParameter){g=g.ContinueNativeRange(bounds[i][0],bounds[i][1]);depth=2;}
            else g=g with{FirstParameter=bounds[i][0],LastParameter=bounds[i][1]};
            var ends=new SketchExpressionEndpoints(pointIds[i],pointIds[(i+1)%4],SketchConstraintId.New(),SketchConstraintId.New());
            outputs.Add(new(outputIds[i],ends,SketchRegionAuthorRole.Parallel,[i],depth,false,g));
            expressions.Add(new(outputIds[i],g,IsConstruction:false){Endpoints=ends});
            points.Add(new(pointIds[i],g.At(0)){Origin=SketchPointOrigin.CurveGenerated});
        }
        var joints=Enumerable.Range(0,4).Select(i=>new SketchRegionJointAuthor(i,(i+1)%4,ids[i],ids[(i+1)%4],
            i<2?SketchRegionJointKind.SourceEndpoints:SketchRegionJointKind.NativeIntersection,
            outputs[i].Geometry.LastParameter,outputs[(i+1)%4].Geometry.FirstParameter,false,false)).ToImmutableArray();
        var loop=new SketchRegionLoopAuthor(0,null,false,outputs.ToImmutable()){Joints=joints};
        var group=new SketchRegionOffsetAuthor(Guid.NewGuid(),[sources],distance,SketchRegionAuthorJoin.Miter,[loop]){Algorithm=SketchRegionJointAuthors.CurrentAlgorithm};
        sketch=sketch with{Points=points.ToImmutable(),ExpressionCurves=expressions.ToImmutable(),RegionOffsets=[group]};
        sketch.Validate();SketchAssociativeEndpoints.RequireSolved(sketch);return sketch;
    }

    private static Type Chart=>typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchRegionSolveChart")!;
    private static bool Prove(CadSketch s,SketchRegionOffsetAuthor group,int slot,bool start)
        =>(bool)Chart.GetMethod("ProvesNaturalBoundary",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[s,group,0,slot,start])!;

    [Theory][InlineData(.1,true)][InlineData(.1,false)][InlineData(-.1,true)][InlineData(-.1,false)]
    public void OneNaturalProofNeverDefersTheOtherNativeCornersOrTheWholeLoop(double distance,bool start)
    {
        var s=AnalyticMixedBoundary(distance);var g=s.RegionOffsets[0];
        Assert.True(Prove(s,g,1,start));Assert.False(Prove(s,g,3,start));
        Assert.Null(Chart.GetMethod("TryCreate",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[s]));
        Assert.Equal(2,g.Loops[0].Joints.Count(j=>j.Kind==SketchRegionJointKind.SourceEndpoints));
        Assert.Equal(2,g.Loops[0].Joints.Count(j=>j.Kind==SketchRegionJointKind.NativeIntersection));
    }
    [Theory][InlineData(0)][InlineData(1)]
    public void DisabledProofDoesNotAuthorizeItsNaturalBoundary(int index)
    {
        var s=AnalyticMixedBoundary();var c=s.Constraints.OfType<EndpointTangentConstraint>().ElementAt(index);
        s=s with{Constraints=s.Constraints.Replace(c,c with{IsEnabled=false})};
        Assert.False(Prove(s,s.RegionOffsets[0],1,index==0));
    }
    [Fact]public void EqualCoordinatesWithDifferentEndpointIdentityAreNotAProof()
    {
        var s=AnalyticMixedBoundary();var arc=s.Arcs[0];var point=s.Points.Single(p=>p.Id==arc.Start);var id=SketchEntityId.New();
        s=s with{Points=s.Points.Add(point with{Id=id}),Arcs=s.Arcs.Replace(arc,arc with{Start=id})};
        Assert.False(Prove(s,s.RegionOffsets[0],1,true));
    }
    [Theory][InlineData(SketchRegionJointKind.NativeIntersection)][InlineData(SketchRegionJointKind.RoundBridge)][InlineData(SketchRegionJointKind.BevelBridge)]
    public void OtherDeclaredKindsCannotBorrowTheG1BoundaryProof(SketchRegionJointKind kind)
    {
        var s=AnalyticMixedBoundary();var g=s.RegionOffsets[0];var l=g.Loops[0];
        g=g with{Loops=[l with{Joints=l.Joints.Replace(l.Joints[0],l.Joints[0] with{Kind=kind})}]};s=s with{RegionOffsets=[g]};
        Assert.False(Prove(s,g,1,true));
    }
    [Fact]public void ForeignGroupAndLegacyConnectionTableCannotReuseTheProof()
    {
        var s=AnalyticMixedBoundary();var g=s.RegionOffsets[0];
        Assert.False(Prove(s,g with{FrozenReason="same values, foreign instance"},1,true));
        var legacy=g with{Algorithm=SketchRegionJointAuthors.LegacyAlgorithm,Loops=[g.Loops[0] with{Joints=[]}]};
        Assert.False(Prove(s with{RegionOffsets=[legacy]},legacy,1,true));
        var reversed=g with{Loops=[g.Loops[0] with{Joints=g.Loops[0].Joints.Replace(g.Loops[0].Joints[0],g.Loops[0].Joints[0] with{NextTraversalReversed=true})}]};
        Assert.False(Prove(s with{RegionOffsets=[reversed]},reversed,1,true));
    }
    [Theory][InlineData(.1,0d)][InlineData(.1,.37)][InlineData(.1,1d)]
    [InlineData(-.1,0d)][InlineData(-.1,.37)][InlineData(-.1,1d)]
    public void MixedLoopNaturalArcKeepsAllAnalyticSourceAndParameterColumns(double distance,double parameterValue)
    {
        var s=AnalyticMixedBoundary(distance);var curve=s.RegionOffsets[0].Loops[0].Outputs[1].Curve;var source=s.Arcs[0];
        var assembly=typeof(ManagedSketchConstraintSolver).Assembly;
        var scalar=assembly.GetType("Cadoryx.Sketching.SketchContactDifferentials")!.GetNestedType("Scalar",BindingFlags.NonPublic)!;
        var method=assembly.GetType("Cadoryx.Sketching.SketchExpressionDifferentials")!.GetMethod("EvaluateRegionContact",BindingFlags.Static|BindingFlags.NonPublic)!;
        var indices=s.Points.Select((p,i)=>(p.Id,Index:2*i)).ToDictionary(p=>p.Id,p=>p.Index);
        var x=s.Points.SelectMany(p=>new[]{p.Position.X,p.Position.Y}).Append(parameterValue).ToArray();
        var gradient=new double[x.Length];gradient[^1]=1;var parameter=Activator.CreateInstance(scalar,[parameterValue,gradient])!;
        var actual=method.Invoke(null,[s,curve,parameter,x,indices,new Dictionary<SketchEntityId,int>(),default(Point2d),1d,1,false,null])!;
        object Prop(object v,string name)=>v.GetType().GetProperty(name)!.GetValue(v)!;
        double Value(object v)=>(double)Prop(v,"Value");double[] Gradient(object v)=>(double[])Prop(v,"Gradient");
        var fields=new[]{Prop(Prop(actual,"Position"),"X"),Prop(Prop(actual,"Position"),"Y"),Prop(actual,"Speed")};
        double[] Oracle(double[] coordinates)
        {
            var trial=s with{Points=s.Points.Select(p=>p with{Position=new(coordinates[indices[p.Id]],coordinates[indices[p.Id]+1])}).ToImmutableArray()};
            var basis=SketchExpressionEntities.Author(trial,source.Id).Geometry;
            var value=basis.Offset(distance).Evaluate(coordinates[^1]);
            return [value.Point.X,value.Point.Y,double.Hypot(value.First.X,value.First.Y)];
        }
        var expected=Oracle(x);for(int row=0;row<3;row++)Near(expected[row],Value(fields[row]),1e-8);
        var columns=new[]{source.Start,source.Middle,source.End}.SelectMany(id=>new[]{indices[id],indices[id]+1}).Append(x.Length-1).ToHashSet();
        foreach(int col in columns)
        {
            const double h=2e-6;var plus=(double[])x.Clone();var minus=(double[])x.Clone();plus[col]+=h;minus[col]-=h;
            if(col==x.Length-1&&(parameterValue==0||parameterValue==1))
            {
                double sign=parameterValue==0?1:-1;var one=(double[])x.Clone();var two=(double[])x.Clone();one[col]+=sign*h;two[col]+=sign*2*h;
                var a=Oracle(one);var b=Oracle(two);for(int row=0;row<3;row++)Near(sign*(-3*expected[row]+4*a[row]-b[row])/(2*h),Gradient(fields[row])[col],3e-5);
            }
            else
            {
                var a=Oracle(plus);var b=Oracle(minus);for(int row=0;row<3;row++)Near((a[row]-b[row])/(2*h),Gradient(fields[row])[col],3e-5);
            }
        }
        foreach(int col in Enumerable.Range(0,x.Length).Where(i=>!columns.Contains(i)))Assert.All(fields,f=>Assert.Equal(0,Gradient(f)[col]));
    }
    private static void Near(double expected,double actual,double tolerance)=>Assert.InRange(Math.Abs(expected-actual),0,tolerance*Math.Max(1,Math.Abs(expected)));
}
