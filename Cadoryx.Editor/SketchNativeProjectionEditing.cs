using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

public sealed partial class SketchDraft
{
    /// <summary>The caller must confirm copied evidence against its native source before publishing.</summary>
    public SketchEntityId ProjectNative(ExactSketchEdgeProjection evidence,bool linked=true,SketchEntityId? replace=null)
    {
        evidence.Validate();SketchEntityId target=replace??SketchEntityId.New();
        Change(s=>
        {
            if(evidence.Part!=s.PartId||evidence.TargetPlane!=s.Plane)throw new CadValidationException("Edge projection has a different sketch owner or plane.");
            var next=s;ImmutableArray<SketchConstraintId> anchors=[];
            if(replace is {} id)
            {
                var previous=s.NativeProjections.SingleOrDefault(p=>p.TargetEntity==id)??throw new CadValidationException("Explicitly select a native projection to replace.");
                next=next with{NativeProjections=next.NativeProjections.Remove(previous)};
                if(evidence.Curve.Freeform is {} freeform)
                {
                    var old=next.Freeforms.SingleOrDefault(e=>e.Id==id)??throw new CadValidationException("Reselection changes curve type; create a new projected entity.");
                    if(old.Geometry.Kind!=freeform.Kind)throw new CadValidationException("Reselection changes curve family; create a new projected entity.");
                    next=next with{Freeforms=next.Freeforms.Replace(old,old with{Geometry=freeform})};
                }
                else if(evidence.Curve.Ellipse is {} ellipse)
                {
                    var old=next.Ellipses.SingleOrDefault(e=>e.Id==id)??throw new CadValidationException("Reselection changes curve type; create a new projected entity.");
                    next=next with{Ellipses=next.Ellipses.Replace(old,old with{Geometry=ellipse})};
                }
                else
                {
                    var line=next.Lines.SingleOrDefault(l=>l.Id==id)??throw new CadValidationException("Reselection changes curve type; create a new projected entity.");
                    var start=next.Points.Single(p=>p.Id==line.Start);var end=next.Points.Single(p=>p.Id==line.End);
                    next=next with{Points=next.Points.Replace(start,start with{Position=evidence.Curve.Start}).Replace(end,end with{Position=evidence.Curve.End})};
                }
                next=next with{Constraints=next.Constraints.Where(c=>!previous.Anchors.Contains(c.Id)).ToImmutableArray()};
            }
            else if(evidence.Curve.Freeform is {} freeform)next=next with{Freeforms=next.Freeforms.Add(new(target,freeform,true))};
            else if(evidence.Curve.Ellipse is {} ellipse)next=next with{Ellipses=next.Ellipses.Add(new(target,ellipse,true))};
            else
            {
                var start=SketchEntityId.New();var end=SketchEntityId.New();
                next=next with{Points=next.Points.Add(new(start,evidence.Curve.Start){Origin=SketchPointOrigin.CurveGenerated}).Add(new(end,evidence.Curve.End){Origin=SketchPointOrigin.CurveGenerated}),Lines=next.Lines.Add(new(target,start,end,true))};
            }
            next=SketchFixedCurveEndpoints.Synchronize(next,target);
            if(linked)
            {
                var points=SketchProjectionGeometry.Points(next,target);anchors=points.Select(_=>SketchConstraintId.New()).ToImmutableArray();
                next=next with{Constraints=next.Constraints.AddRange(points.Select((id,index)=>new FixPointConstraint(anchors[index],id,next.Points.Single(p=>p.Id==id).Position))),
                    NativeProjections=next.NativeProjections.Add(new(target,evidence.Selection,evidence.Source,evidence.TargetPlane,anchors))};
            }
            return next;
        });return target;
    }
}
