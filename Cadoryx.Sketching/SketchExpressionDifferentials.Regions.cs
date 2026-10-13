using System.Collections.Immutable;
using Cadoryx.Db;
using Scalar=Cadoryx.Sketching.SketchContactDifferentials.Scalar;
using Vector=Cadoryx.Sketching.SketchContactDifferentials.Vector;

namespace Cadoryx.Sketching;

internal sealed partial class SketchExpressionDifferentials
{
    private Node RegionAuthor(SketchRegionOffsetAuthor group,SketchEntityId id)
    {
        int loopIndex=Enumerable.Range(0,group.Loops.Length).Single(i=>group.Loops[i].Outputs.Any(o=>o.Curve==id));
        var loop=group.Loops[loopIndex];int index=Enumerable.Range(0,loop.Outputs.Length).Single(i=>loop.Outputs[i].Curve==id);
        var output=loop.Outputs[index];var geometry=output.Geometry;
        if(output.Role==SketchRegionAuthorRole.Parallel)
        {
            var raw=Parallel(output);var start=Bound(index,true);var end=Bound(index,false);
            return raw with{First=geometry.Reversed?end:start,Last=geometry.Reversed?start:end,Reversed=geometry.Reversed};
        }
        int previous=(index+loop.Outputs.Length-1)%loop.Outputs.Length,next=(index+1)%loop.Outputs.Length;
        var before=loop.Outputs[previous];var after=loop.Outputs[next];
        if(before.Role!=SketchRegionAuthorRole.Parallel||after.Role!=SketchRegionAuthorRole.Parallel||
            !output.SourceSlots.SequenceEqual(new[]{before.SourceSlots[0],after.SourceSlots[0]}))
            throw new CadValidationException("Region bridge lost its two declared adjacent parallel authors.");
        var a=Jet(Parallel(before),Bound(previous,false),0,false)[0];
        var b=Jet(Parallel(after),Bound(next,true),0,false)[0];
        if(output.Role==SketchRegionAuthorRole.BevelBridge)
        {
            var line=Line(a,b);RequirePoint(a,geometry.At(0));RequirePoint(b,geometry.At(1));
            return line with{First=WithValue(line.First,geometry.FirstParameter),Last=WithValue(line.Last,geometry.LastParameter),Reversed=geometry.Reversed};
        }
        var source=Author(group.Sources[loopIndex][before.SourceSlots[0]].Entity);
        var sourceEnd=before.Geometry.Reversed?source.First:source.Last;
        var center=Jet(source,sourceEnd,0,sourceEnd.Value==source.Last.Value)[0];
        var ellipse=geometry.Primitive?.Ellipse??throw new CadValidationException("Round region bridge lost its exact circular author.");
        RequirePoint(center,ellipse.Center);double r2=group.Distance*group.Distance;
        var cosine=new Vector(C(ellipse.Cosine.X),C(ellipse.Cosine.Y));var sine=new Vector(C(ellipse.Sine.X),C(ellipse.Sine.Y));
        if(Math.Abs(ellipse.Cosine.X*ellipse.Cosine.X+ellipse.Cosine.Y*ellipse.Cosine.Y-r2)>1e-9||
            Math.Abs(ellipse.Sine.X*ellipse.Sine.X+ellipse.Sine.Y*ellipse.Sine.Y-r2)>1e-9)
            throw new CadValidationException("Round bridge radius differs from the whole-region distance.");
        Scalar Angle(Vector point,double target)
        {
            var v=point-center;var x=(v.X*cosine.X+v.Y*cosine.Y)/r2;var y=(v.X*sine.X+v.Y*sine.Y)/r2;
            var angle=Scalar.Atan2(y,x);double branch=Math.Round((target-angle.Value)/Math.Tau);
            if(Math.Abs(angle.Value+branch*Math.Tau-target)>1e-8)throw new CadValidationException("Round bridge angle has no proven current native branch.");
            return WithValue(angle,target);
        }
        var from=Angle(a,geometry.Reversed?geometry.LastParameter:geometry.FirstParameter);
        var to=Angle(b,geometry.Reversed?geometry.FirstParameter:geometry.LastParameter);
        RequirePoint(a,geometry.At(0));RequirePoint(b,geometry.At(1));
        return Conic(center,cosine,sine,geometry.Reversed?to:from,geometry.Reversed?from:to,geometry.Reversed);

        Node Parallel(SketchRegionOutputAuthor o)
        {
            if(o.Role!=SketchRegionAuthorRole.Parallel||o.SourceSlots.Length!=1)throw new CadValidationException("Expected an explicit parallel source.");
            var basis=Author(group.Sources[loopIndex][o.SourceSlots[0]].Entity);
            var operation=o.BasisDepth==2?o.Geometry.Basis!:o.Geometry;
            var result=new Node(SketchCurveExpressionKind.Offset,basis.First,basis.Last,o.Geometry.Reversed,operation.SignedOffset,basis,null);
            return o.BasisDepth==2?new(SketchCurveExpressionKind.Continuation,basis.First,basis.Last,o.Geometry.Reversed,0,result,null):result;
        }
        Scalar Bound(int segment,bool start)
        {
            var current=loop.Outputs[segment];var g=current.Geometry;
            double u=start?(g.Reversed?g.LastParameter:g.FirstParameter):(g.Reversed?g.FirstParameter:g.LastParameter);
            var raw=Parallel(current);
            if(regionChart?.Contains(group)==true||
                SketchRegionSolveChart.ProvesNaturalBoundary(sketch,group,loopIndex,current.SourceSlots[0],start))
            {
                // The full natural source bound is a Scalar, including line
                // length and arc angle sensitivities. Never freeze it to the
                // previously persisted parameter or its current numeric value.
                // A proof for this one natural joint does not defer the mixed
                // loop's native intersections, bridges or final replay.
                var sourceNode=Author(group.Sources[loopIndex][current.SourceSlots[0]].Entity);
                return start?(g.Reversed?sourceNode.Last:sourceNode.First):(g.Reversed?sourceNode.First:sourceNode.Last);
            }
            int adjacent=(segment+(start?-1:1)+loop.Outputs.Length)%loop.Outputs.Length;
            var other=loop.Outputs[adjacent];
            if(loop.Outputs.Length==1||other.Role!=SketchRegionAuthorRole.Parallel)
            {
                var sourceNode=Author(group.Sources[loopIndex][current.SourceSlots[0]].Entity);
                var natural=start?(g.Reversed?sourceNode.Last:sourceNode.First):(g.Reversed?sourceNode.First:sourceNode.Last);
                if(Math.Abs(u-natural.Value)>1e-8*Math.Max(1,Math.Abs(u)))
                    throw new CadValidationException("A bridge endpoint must retain the current source endpoint parameter.");
                return WithValue(natural,u);
            }
            var h=other.Geometry;double v=start?(h.Reversed?h.FirstParameter:h.LastParameter):(h.Reversed?h.LastParameter:h.FirstParameter);
            var first=Jet(raw,C(u),1,u==g.LastParameter);var second=Jet(Parallel(other),C(v),1,v==h.LastParameter);
            SketchRegionCurveSensitivity Sensitivity(Vector[] jet)=>new(new(jet[0].X.Value,jet[0].Y.Value),new(jet[1].X.Value,jet[1].Y.Value),
                Enumerable.Range(0,coordinates.Length).Select(k=>new Point2d(jet[0].X.Gradient[k],jet[0].Y.Gradient[k])).ToImmutableArray());
            var derivative=SketchRegionIntersectionSensitivity.Differentiate(Sensitivity(first),Sensitivity(second));
            return new(u,derivative.First.ToArray());
        }
    }
    private Scalar WithValue(Scalar value,double current)
    {Finite(value);return new(current,value.Gradient);}
    private void RequirePoint(Vector actual,Point2d expected)
    {
        if(double.Hypot(actual.X.Value-(expected.X-origin.X),actual.Y.Value-(expected.Y-origin.Y))>1e-7)
            throw new CadValidationException("Region differential differs from the current complete native output.");
    }
}
