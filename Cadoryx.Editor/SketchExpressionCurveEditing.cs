using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

public sealed partial class SketchDraft
{
    public void TrimExpression(SketchEntityId id,double first,double last,IExactSketchExpressionGeometry geometry)=>
        Change(s=>SketchExpressionCurveEditing.Keep(s,id,first,last,geometry));
    public void RemoveExpressionInterval(SketchEntityId id,double first,double last,IExactSketchExpressionGeometry geometry)=>
        Change(s=>SketchExpressionCurveEditing.Remove(s,id,first,last,geometry));
    public void TrimExpressionAt(SketchEntityId id,Point2d click,IExactSketchGeometry geometry,IExactSketchExpressionGeometry expressions)=>
        Change(s=>SketchExpressionCurveEditing.RemoveAt(s,id,click,geometry,expressions));
    public void ExtendExpression(SketchEntityId id,bool start,double amount,IExactSketchExpressionGeometry geometry)=>
        Change(s=>SketchExpressionCurveEditing.Extend(s,id,start,amount,geometry));
    public void ExtendExpressionAt(SketchEntityId id,Point2d click,IExactSketchGeometry geometry,IExactSketchExpressionGeometry expressions)=>
        Change(s=>SketchExpressionCurveEditing.ExtendAt(s,id,click,geometry,expressions));
    public SketchEntityId OffsetExactCurve(SketchEntityId id,double distance,IExactSketchExpressionGeometry geometry)
    {
        if(Value.RegionOffsets.Any(g=>g.Loops.Any(l=>l.Outputs.Any(o=>o.Curve==id))))
            throw new CadValidationException("Offset the complete region or detach it before offsetting an individual output segment.");
        SketchEntityId created=default;Change(s=>{var result=SketchExpressionCurveEditing.Offset(s,id,distance,geometry);created=result.Id;return result.Sketch;});return created;
    }
}

/// <summary>Exact finite edits with explicit new endpoints. Splits never inherit a source ID.</summary>
public static class SketchExpressionCurveEditing
{
    private static SketchCurveExpressionGeometry Prepared(SketchCurveExpressionGeometry value,IExactSketchExpressionGeometry geometry)
    {
        var prepared=geometry.PrepareExpression(value);
        if(!value.SameValues(prepared))throw new CadValidationException("Expression preparation changed the copied author geometry.");
        return prepared;
    }
    private static SketchExpressionCurve Definition(CadSketch sketch,SketchEntityId id)
    {
        if(sketch.RegionOffsets.Any(g=>g.Loops.Any(l=>l.Outputs.Any(o=>o.Curve==id))))
            throw new CadValidationException("Detach the complete region before editing an individual output segment.");
        sketch.Validate();if(sketch.Projections.Any(p=>p.TargetEntity==id)||sketch.NativeProjections.Any(p=>p.TargetEntity==id))
            throw new CadValidationException("Detach the projection before editing its author curve.");
        var c=sketch.ExpressionCurves.SingleOrDefault(c=>c.Id==id)??throw new CadValidationException("Select an exact expression entity.");
        if(c.Source is {IsFrozen:true})throw new CadValidationException("Resume or detach the frozen expression before editing it.");
        return c;
    }
    private static ExactSketchCurve Exact(SketchCurveExpressionGeometry g)=>new(g.At(0),g.At(1),Expression:g);
    public static CadSketch Keep(CadSketch s,SketchEntityId id,double first,double last,IExactSketchExpressionGeometry geometry)
    {
        SketchExactCurveEditing.RequireInterval(first,last);var c=Definition(s,id);
        return Replace(s,c,[(c.Geometry.Trim(first,last),first==0,last==1)],true,geometry);
    }
    public static CadSketch Remove(CadSketch s,SketchEntityId id,double first,double last,IExactSketchExpressionGeometry geometry)
    {
        SketchExactCurveEditing.RequireInterval(first,last);return RemoveCore(s,Definition(s,id),first,last,geometry);
    }
    private static CadSketch RemoveCore(CadSketch s,SketchExpressionCurve c,double first,double last,IExactSketchExpressionGeometry geometry)
    {
        var g=c.Geometry;
        if(SketchExpressionPeriod.FullPeriod(g) is not null)
        {
            double a=last,b=first+1;
            if(a>=b)throw new CadValidationException("The periodic trim removes the whole curve.");
            var retained=a>=0&&b<=1?g.Trim(a,b):new(SketchCurveExpressionKind.Continuation,null,g,0,
                g.Reversed?g.LastParameter-b*g.ParameterSpan:g.FirstParameter+a*g.ParameterSpan,
                g.Reversed?g.LastParameter-a*g.ParameterSpan:g.FirstParameter+b*g.ParameterSpan,g.Reversed);
            return Replace(s,c,[(retained,false,false)],true,geometry);
        }
        var pieces=new List<(SketchCurveExpressionGeometry,bool,bool)>();
        if(first>0)pieces.Add((g.Trim(0,first),true,false));if(last<1)pieces.Add((g.Trim(last,1),false,true));
        return Replace(s,c,pieces,pieces.Count==1,geometry);
    }
    public static CadSketch RemoveAt(CadSketch s,SketchEntityId id,Point2d click,IExactSketchGeometry geometry,IExactSketchExpressionGeometry expressions)
    {
        var c=Definition(s,id);var interval=SketchExactCurveEditing.RemoveInterval(s,id,Exact(c.Geometry),click,geometry,SketchExpressionPeriod.FullPeriod(c.Geometry) is not null,true);
        return RemoveCore(s,c,interval.First,interval.Last,expressions);
    }
    private static SketchCurveExpressionGeometry Continue(SketchCurveExpressionGeometry g,bool start,double amount)
    {
        SketchExactCurveEditing.RequireExtension(amount);
        if(SketchExpressionPeriod.FullPeriod(g) is not null||double.Hypot(g.At(0).X-g.At(1).X,g.At(0).Y-g.At(1).Y)<=1e-7)
            throw new CadValidationException("A closed expression has no endpoint to extend.");
        bool lower=start!=g.Reversed;
        return g.ContinueNativeRange(lower?g.FirstParameter-amount*g.ParameterSpan:g.FirstParameter,
            lower?g.LastParameter:g.LastParameter+amount*g.ParameterSpan);
    }
    public static CadSketch Extend(CadSketch s,SketchEntityId id,bool start,double amount,IExactSketchExpressionGeometry geometry)
    {
        var c=Definition(s,id);return Replace(s,c,[(Continue(c.Geometry,start,amount),!start,start)],true,geometry);
    }
    public static CadSketch ExtendAt(CadSketch s,SketchEntityId id,Point2d click,IExactSketchGeometry geometry,IExactSketchExpressionGeometry expressions)
    {
        var c=Definition(s,id);var extension=SketchExactCurveEditing.Extension(s,id,Exact(c.Geometry),click,geometry,(start,amount)=>Exact(Continue(c.Geometry,start,amount)));
        return Extend(s,id,extension.Start,extension.Amount,expressions);
    }
    public static (CadSketch Sketch,SketchEntityId Id) Offset(CadSketch s,SketchEntityId id,double distance,IExactSketchExpressionGeometry geometry)
    {
        s.Validate();CadGuard.Finite(distance);if(Math.Abs(distance)<=1e-7)throw new CadValidationException("Offset distance must be nonzero.");
        var author=SketchExpressionEntities.Author(s,id);
        // The existing open-curve tool uses the directed left normal. The exact
        // expression contract uses the directed right normal.
        var g=Prepared(author.Geometry.Offset(-distance),geometry);var created=SketchEntityId.New();
        bool construction=s.Beziers.Any(c=>c.Id==id&&c.IsConstruction)||s.Splines.Any(c=>c.Id==id&&c.IsConstruction)||
            s.Ellipses.Any(c=>c.Id==id&&c.IsConstruction)||s.Freeforms.Any(c=>c.Id==id&&c.IsConstruction)||s.ExpressionCurves.Any(c=>c.Id==id&&c.IsConstruction);
        var result=s with{ExpressionCurves=s.ExpressionCurves.Add(new(created,g,new(id,author.Kind,author.Geometry,1),true))};
        result=SketchAssociativeEndpoints.Attach(result,created);var value=result.ExpressionCurves.Single(c=>c.Id==created);
        result=result with{ExpressionCurves=result.ExpressionCurves.Replace(value,value with{IsConstruction=construction})};result.Validate();return(result,created);
    }
    private static CadSketch Replace(CadSketch s,SketchExpressionCurve original,IReadOnlyList<(SketchCurveExpressionGeometry Geometry,bool Start,bool End)> pieces,
        bool keepId,IExactSketchExpressionGeometry geometry)
    {
        var points=s.Points.ToBuilder();var replacements=new List<SketchExpressionCurve>();
        foreach(var piece in pieces)
        {
            var g=Prepared(piece.Geometry,geometry);var old=original.Endpoints;
            SketchEntityId Point(bool keep,SketchEntityId? existing,double t)
            {
                if(keep&&existing is {} id)return id;
                var created=SketchEntityId.New();points.Add(new(created,g.At(t),original.IsConstruction){Origin=SketchPointOrigin.CurveGenerated});return created;
            }
            var ends=old is not null||!original.IsConstruction?new SketchExpressionEndpoints(Point(piece.Start,old?.Start,0),Point(piece.End,old?.End,1),
                keepId&&piece.Start&&old is not null?old.StartEquation:SketchConstraintId.New(),keepId&&piece.End&&old is not null?old.EndEquation:SketchConstraintId.New()):null;
            var source=original.Source;
            if(source is not null&&g.Basis is {} basis&&basis.SameValues(original.Geometry)&&g.Kind==SketchCurveExpressionKind.Continuation)
                source=source with{BasisDepth=source.BasisDepth+1};
            replacements.Add(original with{Id=keepId?original.Id:SketchEntityId.New(),Geometry=g,Source=source,Endpoints=ends});
        }
        var candidate=s with{Points=points.ToImmutable(),ExpressionCurves=s.ExpressionCurves.Remove(original).AddRange(replacements)};
        var removed=SketchPointUsage.GeneratedOrphans(candidate,original.Endpoints is {} endsBefore?[endsBefore.Start,endsBefore.End]:[]).ToHashSet();
        if(!keepId)removed.Add(original.Id);
        var endpoint=replacements.Count==1?replacements[0].Endpoints:null;
        candidate=candidate with{Points=candidate.Points.Where(p=>!removed.Contains(p.Id)).ToImmutableArray(),
            Constraints=s.Constraints.Where(c=>!SketchConstraintEditing.Targets(c).Any(removed.Contains)&&
                SketchConstraintEditing.KeepEndpointRelation(c,original.Id,endpoint?.Start??default,endpoint?.End??default)).ToImmutableArray()};
        return candidate;
    }
}
