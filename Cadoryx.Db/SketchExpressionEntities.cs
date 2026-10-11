using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum SketchExpressionSourceKind { Line=1,Circle,Arc,Bezier,Spline,Ellipse,Freeform,Expression }

/// <summary>Copied local author provenance. BasisDepth is an author-tree path, never a native index.
/// Frozen sources retain their provenance and value without participating in the dependency graph.</summary>
public sealed record SketchExpressionSource(SketchEntityId Entity,SketchExpressionSourceKind Kind,
    SketchCurveExpressionGeometry Geometry,int BasisDepth,bool IsFrozen=false);

/// <summary>An owned exact reference curve. Profile/endpoint consumers must explicitly support
/// this table before it can be changed into a non-construction boundary.</summary>
public sealed record SketchExpressionCurve(SketchEntityId Id,SketchCurveExpressionGeometry Geometry,
    SketchExpressionSource? Source=null,bool IsConstruction=true)
{public SketchExpressionEndpoints? Endpoints {get;init;}}

public static class SketchExpressionEntities
{
    public static (SketchExpressionSourceKind Kind,SketchCurveExpressionGeometry Geometry) Author(CadSketch sketch,SketchEntityId id)
    {
        Point2d Point(SketchEntityId point)=>sketch.Points.SingleOrDefault(p=>p.Id==point)?.Position??
            throw new CadValidationException("Expression source has a missing author point.");
        SketchBoundaryCurve curve;SketchExpressionSourceKind kind;
        if(sketch.ExpressionCurves.SingleOrDefault(e=>e.Id==id) is {} expression)
            return(SketchExpressionSourceKind.Expression,expression.Geometry);
        if(sketch.Lines.SingleOrDefault(e=>e.Id==id) is {} line)
        {kind=SketchExpressionSourceKind.Line;curve=new(Point(line.Start),Point(line.End));}
        else if(sketch.Circles.SingleOrDefault(e=>e.Id==id) is {} circle)
        {
            kind=SketchExpressionSourceKind.Circle;var g=new SketchEllipseGeometry(Point(circle.Center),new(circle.Radius,0),new(0,circle.Radius),0,Math.Tau);
            curve=new(g.At(0),g.At(1)){Ellipse=g};
        }
        else if(sketch.Arcs.SingleOrDefault(e=>e.Id==id) is {} arc)
        {kind=SketchExpressionSourceKind.Arc;curve=new(Point(arc.Start),Point(arc.End),Point(arc.Middle));}
        else if(sketch.Beziers.SingleOrDefault(e=>e.Id==id) is {} bezier)
        {kind=SketchExpressionSourceKind.Bezier;curve=new(Point(bezier.Start),Point(bezier.End)){BezierControl=Point(bezier.Control)};}
        else if(sketch.Splines.SingleOrDefault(e=>e.Id==id) is {} spline)
        {kind=SketchExpressionSourceKind.Spline;curve=new(Point(spline.Controls[0]),Point(spline.Controls[^1])){SplineControls=spline.Controls.Select(Point).ToImmutableArray(),SplineKnots=spline.Knots};}
        else if(sketch.Ellipses.SingleOrDefault(e=>e.Id==id) is {} ellipse)
        {kind=SketchExpressionSourceKind.Ellipse;curve=new(ellipse.Geometry.At(0),ellipse.Geometry.At(1)){Ellipse=ellipse.Geometry};}
        else if(sketch.Freeforms.SingleOrDefault(e=>e.Id==id) is {} freeform)
        {kind=SketchExpressionSourceKind.Freeform;curve=new(freeform.Geometry.At(0),freeform.Geometry.At(1)){Freeform=freeform.Geometry};}
        else throw new CadValidationException("The explicitly identified expression source is missing.");
        return(kind,SketchCurveExpressionGeometry.FromBoundary(curve));
    }

    private static SketchCurveExpressionGeometry Basis(SketchExpressionCurve curve)
    {
        var source=curve.Source!;var node=curve.Geometry;
        if(source.BasisDepth is <1 or >=SketchCurveExpressionGeometry.MaximumDepth)
            throw new CadValidationException("An expression source needs an explicit operation path within the author budget.");
        for(int i=0;i<source.BasisDepth;i++)node=node.Basis??throw new CadValidationException("Expression source path is incomplete.");
        return node;
    }
    private static string Family(SketchCurveExpressionGeometry geometry)
    {
        geometry.Validate();while(geometry.Basis is {} basis)geometry=basis;var p=geometry.Primitive!;
        return p.Freeform is {} freeform?"Freeform."+freeform.Kind:p.Ellipse is not null?"Ellipse":
            !p.SplineControls.IsDefaultOrEmpty?"Spline":p.BezierControl is not null?"Bezier":p.Middle is not null?"Arc":"Line";
    }
    public static bool SameFamily(SketchCurveExpressionGeometry first,SketchCurveExpressionGeometry second)=>Family(first)==Family(second);

    /// <summary>Refreshes one independent author after its dependencies have been
    /// evaluated by the complete region/expression graph.</summary>
    public static CadSketch RefreshAuthor(CadSketch sketch,SketchEntityId id)
    {
        var curve=sketch.ExpressionCurves.Single(c=>c.Id==id);ValidateValue(curve);
        if(curve.Source is not {IsFrozen:false} source)return sketch;
        var author=Author(sketch,source.Entity);
        if(author.Kind!=source.Kind||!SameFamily(author.Geometry,source.Geometry))
            throw new CadValidationException("Expression source changed family; explicitly reselect it.");
        if(source.Geometry.SameValues(author.Geometry))return sketch;
        var path=new List<SketchCurveExpressionGeometry>();var node=curve.Geometry;
        for(int i=0;i<source.BasisDepth;i++){path.Add(node);node=node.Basis!;}
        var geometry=author.Geometry;
        for(int i=path.Count-1;i>=0;i--)
        {
            var operation=path[i];var previousBasis=operation.Basis!;
            bool whole=operation.FirstParameter==previousBasis.FirstParameter&&operation.LastParameter==previousBasis.LastParameter;
            geometry=operation with{Basis=geometry,FirstParameter=whole?geometry.FirstParameter:operation.FirstParameter,
                LastParameter=whole?geometry.LastParameter:operation.LastParameter,
                Reversed=operation.Reversed^previousBasis.Reversed^geometry.Reversed};
        }
        geometry.Validate();
        return sketch with{ExpressionCurves=sketch.ExpressionCurves.Replace(curve,curve with{Geometry=geometry,Source=source with{Geometry=author.Geometry}})};
    }

    private static void ValidateValue(SketchExpressionCurve curve)
    {
        CadGuard.Id(curve.Id);curve.Geometry.Validate();
        if(!curve.IsConstruction&&curve.Endpoints is null)throw new CadValidationException("Exact expression boundaries require associative endpoints.");
        if(curve.Source is not {} source)return;
        CadGuard.Id(source.Entity);source.Geometry.Validate();
        if(source.Entity==curve.Id||!Enum.IsDefined(source.Kind)||!Basis(curve).SameValues(source.Geometry))
            throw new CadValidationException("Expression provenance must name a distinct source and retain its exact copied basis.");
    }

    private static ImmutableArray<SketchEntityId> Order(CadSketch sketch)
    {
        if(sketch.ExpressionCurves.IsDefault||sketch.ExpressionCurves.Length>10000)
            throw new CadValidationException("Invalid expression entity table.");
        var values=new Dictionary<SketchEntityId,SketchExpressionCurve>();
        foreach(var curve in sketch.ExpressionCurves)
        {ValidateValue(curve);if(!values.TryAdd(curve.Id,curve))throw new CadValidationException("Duplicate expression entity ID.");}
        var degree=values.Keys.ToDictionary(id=>id,_=>0);var children=values.Keys.ToDictionary(id=>id,_=>new List<SketchEntityId>());
        var providers=EquationsByPoint();
        foreach(var curve in values.Values)
            if(curve.Source is {IsFrozen:false} source)
            {
                var dependencies=new HashSet<SketchEntityId>();
                if(source.Kind==SketchExpressionSourceKind.Expression)
                {
                    if(!values.ContainsKey(source.Entity))throw new CadValidationException("Linked expression source was removed; explicitly freeze or detach first.");
                    dependencies.Add(source.Entity);
                }
                foreach(var point in SketchAssociativeEndpoints.AuthorPoints(sketch,source.Entity))
                    if(providers.TryGetValue(point,out var owners))dependencies.UnionWith(owners);
                foreach(var dependency in dependencies){degree[curve.Id]++;children[dependency].Add(curve.Id);}
            }
        var ready=new Queue<SketchEntityId>(degree.Where(p=>p.Value==0).Select(p=>p.Key));var ordered=ImmutableArray.CreateBuilder<SketchEntityId>();
        while(ready.TryDequeue(out var id))
        {ordered.Add(id);foreach(var child in children[id])if(--degree[child]==0)ready.Enqueue(child);}
        if(ordered.Count!=values.Count)throw new CadValidationException("Expression author dependencies contain a cycle.");
        return ordered.ToImmutable();

        Dictionary<SketchEntityId,HashSet<SketchEntityId>> EquationsByPoint()
        {
            var result=new Dictionary<SketchEntityId,HashSet<SketchEntityId>>();
            foreach(var e in SketchAssociativeEndpoints.Equations(sketch))
            {if(!result.TryGetValue(e.Point,out var owners))result.Add(e.Point,owners=[]);owners.Add(e.Curve);}
            return result;
        }
    }

    public static void ValidateLocal(CadSketch sketch)
    {
        SketchAssociativeEndpoints.Validate(sketch);
        _=Order(sketch);
        foreach(var curve in sketch.ExpressionCurves)
            if(curve.Source is {IsFrozen:false} source)
            {
                var author=Author(sketch,source.Entity);
                if(author.Kind!=source.Kind||!source.Geometry.SameValues(author.Geometry))
                    throw new CadValidationException("Linked expression basis differs from its current author; refresh the whole candidate before publishing.");
            }
    }

    /// <summary>Replaces only an explicitly captured author-tree prefix. Native parameter units,
    /// partial native operation ranges remain authored. A complete-basis operation follows the
    /// complete new basis domain and relative direction; incompatible partial windows fail.</summary>
    public static CadSketch Refresh(CadSketch sketch)
    {
        if(sketch.ExpressionCurves.IsDefault)throw new CadValidationException("Invalid expression entity table.");
        if(sketch.ExpressionCurves.IsEmpty)return sketch;
        var order=Order(sketch);var curves=sketch.ExpressionCurves.ToBuilder();
        var indices=curves.Select((c,i)=>(c.Id,i)).ToDictionary(p=>p.Id,p=>p.i);
        var authors=new Dictionary<SketchEntityId,(SketchExpressionSourceKind Kind,SketchCurveExpressionGeometry Geometry)>();bool changed=false;
        foreach(var id in order)
        {
            int index=indices[id];var curve=curves[index];
            if(curve.Source is not {IsFrozen:false} source)continue;
            (SketchExpressionSourceKind Kind,SketchCurveExpressionGeometry Geometry) author;
            if(indices.TryGetValue(source.Entity,out int sourceIndex))author=(SketchExpressionSourceKind.Expression,curves[sourceIndex].Geometry);
            else if(!authors.TryGetValue(source.Entity,out author)){author=Author(sketch,source.Entity);authors.Add(source.Entity,author);}
            if(author.Kind!=source.Kind||Family(author.Geometry)!=Family(source.Geometry))
                throw new CadValidationException("Expression source changed family; explicitly reselect it.");
            if(source.Geometry.SameValues(author.Geometry))continue;
            var path=new List<SketchCurveExpressionGeometry>();var node=curve.Geometry;
            for(int i=0;i<source.BasisDepth;i++){path.Add(node);node=node.Basis!;}
            var geometry=author.Geometry;
            for(int i=path.Count-1;i>=0;i--)
            {
                var operation=path[i];var previousBasis=operation.Basis!;
                bool whole=operation.FirstParameter==previousBasis.FirstParameter&&operation.LastParameter==previousBasis.LastParameter;
                geometry=operation with{Basis=geometry,FirstParameter=whole?geometry.FirstParameter:operation.FirstParameter,
                    LastParameter=whole?geometry.LastParameter:operation.LastParameter,
                    Reversed=operation.Reversed^previousBasis.Reversed^geometry.Reversed};
            }
            geometry.Validate();
            curves[index]=curve with{Geometry=geometry,Source=source with{Geometry=author.Geometry}};changed=true;
        }
        return changed?sketch with{ExpressionCurves=curves.ToImmutable()}:sketch;
    }

    public static bool SameValues(SketchExpressionCurve a,SketchExpressionCurve b)=>a.Id==b.Id&&a.IsConstruction==b.IsConstruction&&a.Endpoints==b.Endpoints&&
        a.Geometry.SameValues(b.Geometry)&&(a.Source is null?b.Source is null:b.Source is {} other&&
        a.Source.Entity==other.Entity&&a.Source.Kind==other.Kind&&a.Source.BasisDepth==other.BasisDepth&&a.Source.IsFrozen==other.IsFrozen&&
        a.Source.Geometry.SameValues(other.Geometry));
    public static bool SameTable(CadSketch a,CadSketch b)=>!a.ExpressionCurves.IsDefault&&!b.ExpressionCurves.IsDefault&&
        a.ExpressionCurves.Length==b.ExpressionCurves.Length&&a.ExpressionCurves.Zip(b.ExpressionCurves).All(p=>SameValues(p.First,p.Second));

    public static void ValidateDrivenEdit(CadSketch before,CadSketch after)
    {
        foreach(var old in before.ExpressionCurves.Where(e=>e.Source is not null))
        {
            if(after.ExpressionCurves.SingleOrDefault(e=>e.Id==old.Id) is not {Source:{} source} current)continue;
            var previous=old.Source!;
            bool captured=source.Geometry.SameValues(previous.Geometry);
            if(!captured&&!source.IsFrozen)
            {var author=Author(after,source.Entity);captured=author.Kind==source.Kind&&source.Geometry.SameValues(author.Geometry);}
            if(source.Entity!=previous.Entity||source.Kind!=previous.Kind||source.BasisDepth!=previous.BasisDepth&&
                !SketchExpressionOperationProof.IsSingleContinuation(old,current)&&!SketchExpressionOperationProof.IsPeriodicComplement(old,current)||
                Family(previous.Geometry)!=Family(source.Geometry)||!captured||previous.IsFrozen&&source.IsFrozen&&!current.Geometry.SameValues(old.Geometry))
                throw new CadValidationException("Detach the expression before changing its copied basis or frozen author value.");
        }
    }

    /// <summary>Solver coordinates may change primitive sources, but its expression output must
    /// equal the deterministic derived author values. Frozen/owned values cannot be replaced.</summary>
    public static void ValidateSolved(CadSketch authored,CadSketch solved)
    {
        if(!SketchRegionAuthorModel.SameTable(authored,solved))throw new CadValidationException("Solver changed complete region author intent or derived values.");
        if(authored.ExpressionCurves.IsEmpty&&solved.ExpressionCurves.IsEmpty)return;
        var expected=Refresh(authored with{Points=solved.Points,Circles=solved.Circles});
        if(!SameTable(expected,solved))throw new CadValidationException("Solver changed expression identity, author operations or source evidence.");
    }
}
