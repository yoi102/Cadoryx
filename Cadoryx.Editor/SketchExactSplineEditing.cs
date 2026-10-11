using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

public sealed partial class SketchDraft
{
    public void SetSplineKnots(SketchEntityId id,CubicSplineKnotVector knots)=>Change(s=>
    {
        var curve=SketchExactSplineEditing.Definition(s,id);
        try{knots.WithControls(curve.Controls).Validate();}
        catch(CadValidationException error){throw SketchCurveFailure.Create("CurveInvalidKnots",error.Message);}
        var spline=s.Splines.Single(c=>c.Id==id);
        return s with{Splines=s.Splines.Replace(spline,spline with{Knots=knots})};
    });
    public void TrimSpline(SketchEntityId id,double first,double last)=>Change(s=>
        SketchExactSplineEditing.Keep(s,id,first,last));
    public void RemoveSplineInterval(SketchEntityId id,double first,double last)=>Change(s=>
        SketchExactSplineEditing.Remove(s,id,first,last));
    public void TrimSplineAt(SketchEntityId id,Point2d click,IExactSketchGeometry geometry)=>Change(s=>
        SketchExactSplineEditing.RemoveAt(s,id,click,geometry));
    public void ExtendSpline(SketchEntityId id,bool start,double amount)=>Change(s=>
        SketchExactSplineEditing.Extend(s,id,start,amount));
    public void ExtendSplineAt(SketchEntityId id,Point2d click,IExactSketchGeometry geometry)=>Change(s=>
        SketchExactSplineEditing.ExtendAt(s,id,click,geometry));
}

/// <summary>Exact author edits; a split never inherits the source curve identity.</summary>
public static class SketchExactSplineEditing
{
    public static CubicSplineDefinition Definition(CadSketch sketch,SketchEntityId id)
    {
        sketch.Validate();
        if(sketch.Projections.Any(p=>p.TargetEntity==id))throw new CadValidationException("Detach the projection before editing its author curve.");
        var spline=sketch.Splines.SingleOrDefault(s=>s.Id==id)??throw new CadValidationException("Select one finite cubic spline.");
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        var definition=CubicSplineKnotVector.Definition(spline.Controls.Select(p=>points[p]).ToImmutableArray(),spline.Knots);
        definition.Validate();return definition;
    }
    public static CadSketch Keep(CadSketch s,SketchEntityId id,double first,double last)
    {
        SketchExactCurveEditing.RequireInterval(first,last);
        var curve=Definition(s,id);var piece=curve.Trim(first,last);
        return Replace(s,id,[(piece,first==0,last==1)],true);
    }
    public static CadSketch Remove(CadSketch s,SketchEntityId id,double first,double last)
    {
        SketchExactCurveEditing.RequireInterval(first,last);
        var curve=Definition(s,id);_=curve.Trim(first,last);
        var pieces=new List<(CubicSplineDefinition,bool,bool)>();
        if(first>0)pieces.Add((curve.Trim(0,first),true,false));
        if(last<1)pieces.Add((curve.Trim(last,1),false,true));
        return Replace(s,id,pieces,pieces.Count==1);
    }
    public static CadSketch Extend(CadSketch s,SketchEntityId id,bool start,double amount)
    {
        SketchExactCurveEditing.RequireExtension(amount);
        return Replace(s,id,[(Definition(s,id).ExtendEndpoint(start,amount),!start,start)],true);
    }
    public static CadSketch RemoveAt(CadSketch s,SketchEntityId id,Point2d click,IExactSketchGeometry geometry)
    {
        var (first,last)=SketchExactCurveEditing.RemoveInterval(s,id,ExactSketchCurve.Cubic(Definition(s,id)),click,geometry);
        return Remove(s,id,first,last);
    }
    public static CadSketch ExtendAt(CadSketch s,SketchEntityId id,Point2d click,IExactSketchGeometry geometry)
    {
        var definition=Definition(s,id);
        var (start,amount)=SketchExactCurveEditing.Extension(s,id,ExactSketchCurve.Cubic(definition),click,geometry,
            (side,length)=>ExactSketchCurve.Cubic(definition.ExtendEndpoint(side,length)));
        return Extend(s,id,start,amount);
    }
    private static CadSketch Replace(CadSketch s,SketchEntityId id,IReadOnlyList<(CubicSplineDefinition Curve,bool Start,bool End)> pieces,bool keepId)
    {
        var original=s.Splines.Single(c=>c.Id==id);var points=s.Points.ToBuilder();var replacements=new List<SketchSpline>();
        foreach(var piece in pieces)
        {
            piece.Curve.Validate();var ids=ImmutableArray.CreateBuilder<SketchEntityId>();
            for(int i=0;i<piece.Curve.Controls.Length;i++)
            {
                var pointId=i==0&&piece.Start?original.Controls[0]:i==piece.Curve.Controls.Length-1&&piece.End?original.Controls[^1]:SketchEntityId.New();
                if(!points.Any(p=>p.Id==pointId))points.Add(new(pointId,piece.Curve.Controls[i]){Origin=SketchPointOrigin.CurveGenerated});
                ids.Add(pointId);
            }
            replacements.Add(new(keepId?id:SketchEntityId.New(),ids.ToImmutable(),original.IsConstruction){Knots=CubicSplineKnotVector.From(piece.Curve)});
        }
        var splines=s.Splines.Remove(original).AddRange(replacements);
        var candidate=s with{Points=points.ToImmutable(),Splines=splines};
        var removed=SketchPointUsage.GeneratedOrphans(candidate,original.Controls).ToHashSet();if(!keepId)removed.Add(id);
        var constraints=s.Constraints.Where(c=>!SketchConstraintEditing.Targets(c).Any(removed.Contains)&&
            SketchConstraintEditing.KeepEndpointRelation(c,id,replacements.Count==1?replacements[0].Controls[0]:default,
                replacements.Count==1?replacements[0].Controls[^1]:default)).ToImmutableArray();
        return s with{Points=points.Where(p=>!removed.Contains(p.Id)).ToImmutableArray(),Splines=splines,Constraints=constraints};
    }
}
