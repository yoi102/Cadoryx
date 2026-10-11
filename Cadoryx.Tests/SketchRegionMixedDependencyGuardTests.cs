using System.Collections.Immutable;
using System.Reflection;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchRegionMixedDependencyGuardTests
{
    private static readonly OcctGeometryKernel Kernel=new();
    public static IEnumerable<object[]> Directions()=>from naturalConsumesNative in new[]{false,true}
        from mirror in new[]{false,true} from distance in new[]{.1,-.1}
        select new object[]{naturalConsumesNative,mirror,distance};

    [Theory,MemberData(nameof(Directions))]
    public void RealGeneratedVertexDependenciesInEitherDirectionCannotUseIndependentGroupDeferral(
        bool naturalConsumesNative,bool mirror,double distance)
    {
        var original=SketchRegionNaturalChartTests.Capsule(mirror,distance);
        CadSketch dependent,independent;Guid natural;
        if(!naturalConsumesNative)
        {
            natural=original.RegionOffsets.Single().Id;
            var generated=original.RegionOffsets.Single().Loops[0].Outputs[0].Endpoints.Start;
            dependent=AddRectangleGroup(original,generated,false).Sketch;
            independent=AddRectangleGroup(original,generated,true).Sketch;
        }
        else
        {
            var sourceIds=original.Lines.Select(l=>l.Id).Concat(original.Arcs.Select(a=>a.Id)).ToHashSet();
            var pointIds=original.Lines.SelectMany(l=>new[]{l.Start,l.End})
                .Concat(original.Arcs.SelectMany(a=>new[]{a.Start,a.Middle,a.End})).ToHashSet();
            var bare=original with{RegionOffsets=[],ExpressionCurves=[],Points=original.Points.Where(p=>pointIds.Contains(p.Id)).ToImmutableArray()};
            var(rectangle,native)=AddRectangleGroup(bare,null,false);
            rectangle=ConstructionProviders(rectangle);
            var generated=rectangle.RegionOffsets.Single(g=>g.Id==native).Loops[0].Outputs[0].Endpoints.Start;
            var old=original.Lines[0].Start;
            var a=original.Points.Single(p=>p.Id==old).Position;
            var b=rectangle.Points.Single(p=>p.Id==generated).Position;
            rectangle=rectangle with{Points=rectangle.Points.Where(p=>p.Id!=old).Select(p=>pointIds.Contains(p.Id)?
                p with{Position=new(p.Position.X+b.X-a.X,p.Position.Y+b.Y-a.Y)}:p).ToImmutableArray()};
            rectangle=ReplaceCapsuleVertex(rectangle,sourceIds,old,generated);
            var draft=new SketchDraft(rectangle,Kernel);
            natural=draft.OffsetExpressionRegions(sourceIds,distance,SketchRegionAuthorJoin.Miter,Kernel);
            dependent=draft.Value;
            var free=SketchEntityId.New();independent=dependent with{Points=dependent.Points.Add(
                new(free,b){Origin=SketchPointOrigin.Independent})};
            independent=ReplaceCapsuleVertex(independent,sourceIds,generated,free);
        }
        // Both fixtures have complete actual native authors. Only the dependency
        // identity changes; equal coordinates cannot be treated as independence.
        dependent.Validate();independent.Validate();
        Kernel.ConfirmExpressionRegions(dependent);Kernel.ConfirmExpressionRegions(independent);
        Assert.Null(Chart(dependent));
        RequireOnlyNaturalGroup(Chart(independent),natural);
        Assert.False(SketchRegionAuthorModel.Order(dependent).IsEmpty);
    }

    [Theory]
    [InlineData(1,false)] [InlineData(3,false)] [InlineData(7,false)]
    [InlineData(1,true)] [InlineData(3,true)] [InlineData(7,true)]
    public void GeneratedVertexProvidersFollowEveryLiveExpressionSourceAndStopAtExplicitFrozenAuthors(int depth,bool frozen)
    {
        var original=SketchRegionNaturalChartTests.Capsule();var natural=original.RegionOffsets.Single().Id;
        var draft=new SketchDraft(original,Kernel);
        var curve=original.RegionOffsets.Single().Loops[0].Outputs[0].Curve;
        for(int i=0;i<depth;i++)
        {
            curve=draft.AddExactOffset(curve,.015);
            draft.AttachExpressionEndpoints(curve);
        }
        if(frozen)draft.FreezeExpression(curve);
        var generated=draft.Value.ExpressionCurves.Single(e=>e.Id==curve).Endpoints!.Start;
        var dependent=AddRectangleGroup(draft.Value,generated,false).Sketch;
        dependent.Validate();Kernel.ConfirmExpressionRegions(dependent);
        Assert.All(dependent.ExpressionCurves.Where(e=>e.Source is not null),e=>Assert.NotEqual(e.Id,e.Source!.Entity));
        var chart=Chart(dependent);
        if(frozen)RequireOnlyNaturalGroup(chart,natural);else Assert.Null(chart);
        // A separate independent point at the exact same location breaks the
        // live provider edge without altering any native source geometry.
        var independent=AddRectangleGroup(draft.Value,generated,true).Sketch;
        independent.Validate();Kernel.ConfirmExpressionRegions(independent);
        RequireOnlyNaturalGroup(Chart(independent),natural);
    }

    private static (CadSketch Sketch,Guid Group) AddRectangleGroup(CadSketch sketch,SketchEntityId? generated,bool detach)
    {
        // Explicit region selection still requires degree two. Provider curves
        // are construction authors with unchanged structural endpoint equations,
        // so sharing a generated vertex cannot create a branch in the new loop.
        if(generated is not null)sketch=ConstructionProviders(sketch);
        var position=generated is {} id?sketch.Points.Single(p=>p.Id==id).Position:new Point2d(30,10);
        var points=new List<SketchPoint>();
        var first=generated is {} existing&&!detach?existing:SketchEntityId.New();
        if(generated is null||detach)points.Add(new(first,position){Origin=SketchPointOrigin.Independent});
        foreach(var offset in new[]{new Point2d(8,0),new Point2d(8,5),new Point2d(0,5)})
            points.Add(new(SketchEntityId.New(),new(position.X+offset.X,position.Y+offset.Y)){Origin=SketchPointOrigin.Independent});
        var ids=new[]{first}.Concat(points.TakeLast(3).Select(p=>p.Id)).ToArray();
        var lines=Enumerable.Range(0,4).Select(i=>new SketchLine(SketchEntityId.New(),ids[i],ids[(i+1)%4])).ToImmutableArray();
        var draft=new SketchDraft(sketch with{Points=sketch.Points.AddRange(points),Lines=sketch.Lines.AddRange(lines)},Kernel);
        var group=draft.OffsetExpressionRegions(lines.Select(l=>l.Id),.2,SketchRegionAuthorJoin.Round,Kernel);
        return(draft.Value,group);
    }

    private static CadSketch ConstructionProviders(CadSketch sketch)=>sketch with
        {ExpressionCurves=sketch.ExpressionCurves.Select(e=>e with{IsConstruction=true}).ToImmutableArray()};

    private static CadSketch ReplaceCapsuleVertex(CadSketch sketch,HashSet<SketchEntityId> sources,SketchEntityId from,SketchEntityId to)
    {
        SketchEntityId Map(SketchEntityId id)=>id==from?to:id;
        return sketch with
        {
            Lines=sketch.Lines.Select(l=>sources.Contains(l.Id)?l with{Start=Map(l.Start),End=Map(l.End)}:l).ToImmutableArray(),
            Arcs=sketch.Arcs.Select(a=>sources.Contains(a.Id)?a with{Start=Map(a.Start),Middle=Map(a.Middle),End=Map(a.End)}:a).ToImmutableArray(),
            Constraints=sketch.Constraints.Select(c=>c is EndpointTangentConstraint t&&sources.Contains(t.A)&&sources.Contains(t.B)?
                t with{Joint=Map(t.Joint)}:c).ToImmutableArray()
        };
    }

    private static object? Chart(CadSketch sketch)=>typeof(ManagedSketchConstraintSolver).Assembly
        .GetType("Cadoryx.Sketching.SketchRegionSolveChart",true)!
        .GetMethod("TryCreate",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[sketch]);
    private static void RequireOnlyNaturalGroup(object? chart,Guid natural)
    {
        Assert.NotNull(chart);
        var type=chart.GetType();
        Assert.False((bool)type.GetProperty("IsComplete",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(chart)!);
        var groups=(ImmutableHashSet<Guid>)type.GetProperty("GroupIds",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(chart)!;
        Assert.Equal(natural,Assert.Single(groups));
    }
}
