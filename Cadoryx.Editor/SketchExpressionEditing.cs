using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Editor;

public sealed partial class SketchDraft
{
    public SketchEntityId AddExpression(SketchCurveExpressionGeometry geometry)
    {var id=SketchEntityId.New();Change(s=>s with{ExpressionCurves=s.ExpressionCurves.Add(new(id,geometry))});return id;}
    public SketchEntityId AddExactOffset(SketchEntityId source,double signedDistance,bool linked=true)
    {
        var id=SketchEntityId.New();Change(s=>
        {
            var author=SketchExpressionEntities.Author(s,source);
            return s with{ExpressionCurves=s.ExpressionCurves.Add(new(id,author.Geometry.Offset(signedDistance),
                linked?new(source,author.Kind,author.Geometry,1):null))};
        });return id;
    }
    public void EditExpression(SketchEntityId id,SketchCurveExpressionGeometry geometry)=>Change(s=>
    {
        var curve=Expression(s,id);return s with{ExpressionCurves=s.ExpressionCurves.Replace(curve,curve with{Geometry=geometry})};
    });
    public void DetachExpression(SketchEntityId id)=>Change(s=>
    {
        var curve=Expression(s,id);return s with{ExpressionCurves=s.ExpressionCurves.Replace(curve,curve with{Source=null})};
    });
    public void FreezeExpression(SketchEntityId id)=>Change(s=>
    {
        var curve=Expression(s,id);var source=curve.Source??throw new CadValidationException("Select an expression with author provenance.");
        return s with{ExpressionCurves=s.ExpressionCurves.Replace(curve,curve with{Source=source with{IsFrozen=true}})};
    });
    public void ResumeExpression(SketchEntityId id)=>Change(s=>
    {
        var curve=Expression(s,id);var source=curve.Source??throw new CadValidationException("A detached expression needs explicit source selection.");
        return s with{ExpressionCurves=s.ExpressionCurves.Replace(curve,curve with{Source=source with{IsFrozen=false}})};
    });
    public void RemoveExpression(SketchEntityId id)=>Change(s=>s with{ExpressionCurves=s.ExpressionCurves.Remove(Expression(s,id)),
        Constraints=s.Constraints.Where(c=>!SketchConstraintEditing.Targets(c).Contains(id)).ToImmutableArray()});
    public void AttachExpressionEndpoints(SketchEntityId id,SketchEntityId? start=null,SketchEntityId? end=null)=>
        Change(s=>SketchAssociativeEndpoints.Attach(s,id,start,end));
    public void SetExpressionConstruction(SketchEntityId id,bool construction)=>Change(s=>
    {
        var c=Expression(s,id);
        if(!construction&&c.Endpoints is null)s=SketchAssociativeEndpoints.Attach(s,id);
        c=Expression(s,id);return s with{ExpressionCurves=s.ExpressionCurves.Replace(c,c with{IsConstruction=construction})};
    });
    private static SketchExpressionCurve Expression(CadSketch sketch,SketchEntityId id)=>
        sketch.ExpressionCurves.SingleOrDefault(e=>e.Id==id)??throw new CadValidationException("Select an exact expression entity.");
}
