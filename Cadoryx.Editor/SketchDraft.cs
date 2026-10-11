using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

/// <summary>Local authoring history. Nothing is published to the document until confirmation.</summary>
public sealed partial class SketchDraft(CadSketch initial,IExactSketchExpressionRegions? regionKernel=null)
{
    private SketchRegionEvaluationContext? regionEvaluation=regionKernel is null?null:new(regionKernel);
    private sealed record State(CadSketch Sketch,ImmutableArray<SketchCornerReplacement> Corners);
    private readonly Stack<State> undo=[];
    private readonly Stack<State> redo=[];
    public ImmutableArray<SketchCornerReplacement> CornerReplacements {get;private set;}=[];
    public CadSketch Value {get;private set;}=initial;
    public bool CanUndo=>undo.Count>0;
    public bool CanRedo=>redo.Count>0;
    public void Change(Func<CadSketch,CadSketch> change)
        =>Change(change,false);
    private void Change(Func<CadSketch,CadSketch> change,bool regionEdit)
    {
        var next=change(Value);
        Accept(next,regionEdit,refresh:true);
    }
    private void Accept(CadSketch next,bool regionEdit,bool refresh)
    {
        if(!regionEdit)RequireUnchangedRegionOutputs(Value,next);
        SketchPointUsage.ValidateDrivenEdit(Value,next);
        SketchExpressionEntities.ValidateDrivenEdit(Value,next);
        if(refresh)next=next.RegionOffsets.IsEmpty?SketchExpressionEntities.Refresh(next):
            (regionEvaluation??throw new NotSupportedException("Editing whole-region authors requires the exact evaluator.")).Refresh(next,synchronizeGeneratedPoints:true);
        next.Validate();
        SketchProjectionGeometry.ValidateDrivenEdit(Value,next);
        SketchNativeProjectionGeometry.ValidateDrivenEdit(Value,next);
        if(next.Id!=Value.Id||next.PartId!=Value.PartId||next.Revision!=Value.Revision)throw new CadValidationException("Draft identity cannot change.");
        undo.Push(new(Value,CornerReplacements));redo.Clear();Value=next;
    }
    public void Undo(){if(undo.TryPop(out var previous)){redo.Push(new(Value,CornerReplacements));Value=previous.Sketch;CornerReplacements=previous.Corners;}}
    public void Redo(){if(redo.TryPop(out var next)){undo.Push(new(Value,CornerReplacements));Value=next.Sketch;CornerReplacements=next.Corners;}}
    public void UseSolvedCoordinates(CadSketch solved)
    {SketchPointUsage.RequireUnchangedOrigins(Value,solved);Value=solved with{Revision=Value.Revision};}
    public void AddPoint(Point2d position,bool construction=false)=>Change(s=>s with{Points=s.Points.Add(new(SketchEntityId.New(),position,construction){Origin=SketchPointOrigin.Independent})});
    public void AddEllipse(SketchEllipseGeometry geometry,bool construction=false)=>Change(s=>s with{Ellipses=s.Ellipses.Add(new(SketchEntityId.New(),geometry,construction))});
    public void AddFreeform(SketchFreeformGeometry geometry,bool construction=false)=>Change(s=>s with{Freeforms=s.Freeforms.Add(new(SketchEntityId.New(),geometry,construction))});
    public void ExposeCurveEndpoints(SketchEntityId id,SketchEntityId? start=null,SketchEntityId? end=null)=>
        Change(s=>SketchFixedCurveEndpoints.Attach(s,id,start,end));
    public void EditFreeform(SketchEntityId id,SketchFreeformGeometry geometry)=>Change(s=>
    {
        var curve=s.Freeforms.SingleOrDefault(e=>e.Id==id)??throw new CadValidationException("Select an exact freeform curve.");
        if(curve.Geometry.Kind!=geometry.Kind)throw new CadValidationException("Editing must retain the freeform family.");
        return SketchFixedCurveEndpoints.Synchronize(s with{Freeforms=s.Freeforms.Replace(curve,curve with{Geometry=geometry})},id);
    });
    public void EditEllipse(SketchEntityId id,SketchEllipseGeometry geometry)=>Change(s=>
    {
        var ellipse=s.Ellipses.SingleOrDefault(e=>e.Id==id)??throw new CadValidationException("Select an exact ellipse.");
        return SketchFixedCurveEndpoints.Synchronize(s with{Ellipses=s.Ellipses.Replace(ellipse,ellipse with{Geometry=geometry})},id);
    });
    public void AddLine(Point2d a,Point2d b,SketchEntityId? start=null,SketchEntityId? end=null,bool construction=false)=>Change(s=>
    {
        var points=s.Points;var p=start??SketchEntityId.New();var q=end??SketchEntityId.New();
        if(start is null)points=points.Add(new(p,a){Origin=SketchPointOrigin.CurveGenerated});if(end is null)points=points.Add(new(q,b){Origin=SketchPointOrigin.CurveGenerated});
        return s with{Points=points,Lines=s.Lines.Add(new(SketchEntityId.New(),p,q,construction))};
    });
    public void AddCircle(Point2d center,double radius,SketchEntityId? existingCenter=null,bool construction=false)=>Change(s=>
    {
        var point=existingCenter??SketchEntityId.New();var circle=SketchEntityId.New();
        return s with{Points=existingCenter is null?s.Points.Add(new(point,center){Origin=SketchPointOrigin.CurveGenerated}):s.Points,
            Circles=s.Circles.Add(new(circle,point,radius,construction)),Constraints=s.Constraints.Add(new RadiusConstraint(SketchConstraintId.New(),circle,radius))};
    });
    public void AddArc(Point2d start,Point2d middle,Point2d end,SketchEntityId? existingStart=null,
        SketchEntityId? existingMiddle=null,SketchEntityId? existingEnd=null,bool construction=false)=>Change(s=>
    {
        _=SketchArcGeometry.Through(start,middle,end);
        var p=existingStart??SketchEntityId.New();var m=existingMiddle??SketchEntityId.New();var q=existingEnd??SketchEntityId.New();
        var points=s.Points;
        if(existingStart is null)points=points.Add(new(p,start){Origin=SketchPointOrigin.CurveGenerated});
        if(existingMiddle is null)points=points.Add(new(m,middle){Origin=SketchPointOrigin.CurveGenerated});
        if(existingEnd is null)points=points.Add(new(q,end){Origin=SketchPointOrigin.CurveGenerated});
        return s with{Points=points,Arcs=s.Arcs.Add(new(SketchEntityId.New(),p,m,q,construction))};
    });
    public void AddBezier(Point2d start,Point2d control,Point2d end,SketchEntityId? existingStart=null,
        SketchEntityId? existingControl=null,SketchEntityId? existingEnd=null,bool construction=false)=>Change(s=>
    {
        SketchBezierGeometry.Validate(start,control,end);
        var p=existingStart??SketchEntityId.New();var c=existingControl??SketchEntityId.New();var q=existingEnd??SketchEntityId.New();
        var points=s.Points;
        if(existingStart is null)points=points.Add(new(p,start){Origin=SketchPointOrigin.CurveGenerated});
        if(existingControl is null)points=points.Add(new(c,control){Origin=SketchPointOrigin.CurveGenerated});
        if(existingEnd is null)points=points.Add(new(q,end){Origin=SketchPointOrigin.CurveGenerated});
        return s with{Points=points,Beziers=s.Beziers.Add(new(SketchEntityId.New(),p,c,q,construction))};
    });
    public void AddSpline(IEnumerable<Point2d> controls,bool construction=false,SketchEntityId? existingStart=null,SketchEntityId? existingEnd=null)=>
        AddSpline(CubicSplineDefinition.Uniform(controls.ToImmutableArray()),construction,existingStart,existingEnd);
    public void AddSpline(CubicSplineDefinition geometry,bool construction=false,SketchEntityId? existingStart=null,SketchEntityId? existingEnd=null)=>Change(s=>
    {
        geometry.Validate();var locations=geometry.Controls;
        if(existingStart is {} startId&&s.Points.Single(p=>p.Id==startId).Position!=locations[0]||
            existingEnd is {} endId&&s.Points.Single(p=>p.Id==endId).Position!=locations[^1])
            throw new CadValidationException("Selected endpoint identities must match the authored controls.");
        var ids=locations.Select((_,i)=>i==0&&existingStart is {} start?start:i==locations.Length-1&&existingEnd is {} end?end:SketchEntityId.New()).ToImmutableArray();
        return s with{Points=s.Points.AddRange(locations.Select((p,i)=>(p,i)).Where(v=>!(v.i==0&&existingStart is not null||v.i==locations.Length-1&&existingEnd is not null))
                .Select(v=>new SketchPoint(ids[v.i],v.p){Origin=SketchPointOrigin.CurveGenerated})),
            Splines=s.Splines.Add(new(SketchEntityId.New(),ids,construction){Knots=CubicSplineKnotVector.From(geometry)})};
    });
    public void AddRectangle(Point2d a,Point2d b,bool construction=false)=>Change(s=>
    {
        var min=new Point2d(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y));var max=new Point2d(Math.Max(a.X,b.X),Math.Max(a.Y,b.Y));
        var p=new[]{min,new Point2d(max.X,min.Y),max,new Point2d(min.X,max.Y)}.Select(x=>new SketchPoint(SketchEntityId.New(),x){Origin=SketchPointOrigin.CurveGenerated}).ToImmutableArray();
        var l=Enumerable.Range(0,4).Select(i=>new SketchLine(SketchEntityId.New(),p[i].Id,p[(i+1)%4].Id,construction)).ToImmutableArray();
        return s with{Points=s.Points.AddRange(p),Lines=s.Lines.AddRange(l),Constraints=[..s.Constraints,
            new FixPointConstraint(SketchConstraintId.New(),p[0].Id,min),new HorizontalConstraint(SketchConstraintId.New(),l[0].Id),
            new VerticalConstraint(SketchConstraintId.New(),l[1].Id),new HorizontalConstraint(SketchConstraintId.New(),l[2].Id),new VerticalConstraint(SketchConstraintId.New(),l[3].Id),
            new OffsetXConstraint(SketchConstraintId.New(),p[0].Id,p[1].Id,max.X-min.X),new OffsetYConstraint(SketchConstraintId.New(),p[0].Id,p[3].Id,max.Y-min.Y)]};
    });
    public void MovePoint(SketchEntityId id,Point2d position)=>Change(s=>s with{Points=s.Points.Select(p=>p.Id==id?p with{Position=position}:p).ToImmutableArray()});
    public void TrimLine(SketchEntityId id,Point2d click)=>Change(s=>SketchLineEditing.Trim(s,id,click));
    public void ExtendLine(SketchEntityId id,Point2d click)=>Change(s=>SketchLineEditing.Extend(s,id,click));
    public void TrimCurve(SketchEntityId id,Point2d click)=>Change(s=>SketchCurveEditing.Trim(s,id,click));
    public void ExtendCurve(SketchEntityId id,Point2d click)=>Change(s=>SketchCurveEditing.Extend(s,id,click));
    public void OffsetCurve(SketchEntityId id,double distance)=>Change(s=>SketchCurveEditing.Offset(s,id,distance));
    public void OffsetLoop(SketchEntityId id,double distance)=>OffsetLoop(id,distance,SketchOffsetJoin.Miter);
    public SketchLoopOffset.CreatedBoundary OffsetLoop(SketchEntityId id,double distance,SketchOffsetJoin join)
    {
        var result=SketchLoopOffset.Create(Value,id,distance,join);Change(_=>result.Sketch);return result;
    }
    public SketchRegionOffset.Result OffsetRegions(IEnumerable<SketchEntityId> seeds,double distance,SketchOffsetJoin join=SketchOffsetJoin.Miter)
    {
        var result=SketchRegionOffset.Create(Value,seeds,distance,join);Change(_=>result.Sketch);return result;
    }
    private void ChangeCorners(SketchCornerEdit edit)
    {Change(_=>edit.Sketch);CornerReplacements=CornerReplacements.AddRange(edit.Replacements);}
    public void Fillet(SketchEntityId first,SketchEntityId second,double radius,bool constrainRadius=false)=>ChangeCorners(SketchCornerEditing.Lines(Value,first,second,true,radius,0,0,constrainRadius));
    public void Chamfer(SketchEntityId first,SketchEntityId second,double distanceA,double distanceB)=>ChangeCorners(SketchCornerEditing.Lines(Value,first,second,false,0,distanceA,distanceB));
    public void EditCorners(IEnumerable<SketchEntityId> joints,bool fillet,double radius,double distanceA,double distanceB,bool constrainRadius=false)=>
        ChangeCorners(SketchCornerEditing.Loop(Value,joints,fillet,radius,distanceA,distanceB,constrainRadius));
    public void DetachProjection(SketchEntityId id)=>Change(s=>
    {
        if(s.NativeProjections.SingleOrDefault(p=>p.TargetEntity==id) is {} native)
            return s with{NativeProjections=s.NativeProjections.Remove(native),Constraints=s.Constraints.Where(c=>!native.Anchors.Contains(c.Id)).ToImmutableArray()};
        var p=s.Projections.SingleOrDefault(p=>p.TargetEntity==id)??throw new CadValidationException("Select a projected entity.");
        return s with{Projections=s.Projections.Remove(p),Constraints=s.Constraints.Where(c=>!p.Anchors.Contains(c.Id)).ToImmutableArray()};
    });
    public void Project(CadSketch source,SketchEntityId id)=>Change(s=>
    {
        if(source.PartId!=s.PartId||source.Id==s.Id)throw new CadValidationException("Select a different sketch in the same part.");
        SketchProjectionGeometry.Points(source,id);var d=new SketchDraft(s);
        bool conic=source.Ellipses.Any(e=>e.Id==id)||source.Circles.Any(c=>c.Id==id)||source.Arcs.Any(a=>a.Id==id);
        var transform=s.Plane.Inverse()*source.Plane;
        if(source.Freeforms.SingleOrDefault(e=>e.Id==id) is {} freeform)
        {
            d.AddFreeform(freeform.Geometry.Project(transform),true);var projected=d.Value;
            return projected with{Projections=projected.Projections.Add(new(source.Id,id,source.Revision,projected.Freeforms[^1].Id,[]))};
        }
        if(source.Ellipses.Any(e=>e.Id==id)||conic&&Math.Abs(transform.Rotation.Rotate(Vector3d.UnitZ).Z)<1-1e-10)
        {
            d.AddEllipse(SketchProjectionGeometry.Conic(source,id).Project(transform),true);var nextEllipse=d.Value;
            return nextEllipse with{Projections=nextEllipse.Projections.Add(new(source.Id,id,source.Revision,nextEllipse.Ellipses[^1].Id,[]))};
        }
        var curve=SketchAnalyticalCurve.From(source,id);
        if(curve.Closed)d.AddCircle(curve.Arc!.Value.Center,curve.Arc.Value.Radius,construction:true);
        else if(curve.Arc is not null)d.AddArc(curve.Start,curve.At(.5),curve.End,construction:true);
        else d.AddLine(curve.Start,curve.End,construction:true);
        var next=d.Value;var target=curve.Closed?next.Circles[^1].Id:curve.Arc is not null?next.Arcs[^1].Id:next.Lines[^1].Id;
        var targetPoints=SketchProjectionGeometry.Points(next,target);var anchors=targetPoints.Select(_=>SketchConstraintId.New()).ToImmutableArray();
        var constraints=next.Constraints.AddRange(targetPoints.Select((p,i)=>new FixPointConstraint(anchors[i],p,next.Points.Single(v=>v.Id==p).Position)));
        if(curve.Closed)anchors=anchors.Add(((RadiusConstraint)next.Constraints[^1]).Id);
        next=next with{Constraints=constraints,Projections=[new(source.Id,id,source.Revision,target,anchors)]};
        var refreshed=SketchProjectionGeometry.Refresh(next,new Dictionary<SketchId,CadSketch>{{source.Id,source}},false);
        return refreshed with{Projections=s.Projections.Add(refreshed.Projections[0])};
    });
    public void SetConstruction(IEnumerable<SketchEntityId> ids,bool value)=>Change(s=>
    {
        var selected=ids.ToHashSet();
        if(selected.Count==0||selected.Any(id=>!s.Points.Any(p=>p.Id==id)&&!s.Lines.Any(l=>l.Id==id)&&!s.Circles.Any(c=>c.Id==id)&&!s.Arcs.Any(a=>a.Id==id)&&!s.Beziers.Any(b=>b.Id==id)&&!s.Splines.Any(b=>b.Id==id)&&!s.Ellipses.Any(e=>e.Id==id)&&!s.Freeforms.Any(e=>e.Id==id)&&!s.ExpressionCurves.Any(e=>e.Id==id)))
            throw new CadValidationException("Select existing sketch geometry.");
        if(!value)foreach(var id in selected.Where(id=>s.ExpressionCurves.Any(c=>c.Id==id&&c.Endpoints is null)))
            s=SketchAssociativeEndpoints.Attach(s,id);
        return s with{Points=s.Points.Select(p=>selected.Contains(p.Id)?p with{IsConstruction=value}:p).ToImmutableArray(),
            Lines=s.Lines.Select(l=>selected.Contains(l.Id)?l with{IsConstruction=value}:l).ToImmutableArray(),
            Circles=s.Circles.Select(c=>selected.Contains(c.Id)?c with{IsConstruction=value}:c).ToImmutableArray(),
            Arcs=s.Arcs.Select(a=>selected.Contains(a.Id)?a with{IsConstruction=value}:a).ToImmutableArray(),
            Beziers=s.Beziers.Select(b=>selected.Contains(b.Id)?b with{IsConstruction=value}:b).ToImmutableArray(),
            Splines=s.Splines.Select(b=>selected.Contains(b.Id)?b with{IsConstruction=value}:b).ToImmutableArray(),
            Ellipses=s.Ellipses.Select(e=>selected.Contains(e.Id)?e with{IsConstruction=value}:e).ToImmutableArray(),
            Freeforms=s.Freeforms.Select(e=>selected.Contains(e.Id)?e with{IsConstruction=value}:e).ToImmutableArray(),
            ExpressionCurves=s.ExpressionCurves.Select(e=>selected.Contains(e.Id)?e with{IsConstruction=value}:e).ToImmutableArray()};
    });
    public void RemoveEntities(IEnumerable<SketchEntityId> ids)=>Change(s=>
    {
        var removed=ids.ToHashSet();
        foreach(var line in s.Lines)if(removed.Contains(line.Start)||removed.Contains(line.End))removed.Add(line.Id);
        foreach(var circle in s.Circles)if(removed.Contains(circle.Center))removed.Add(circle.Id);
        foreach(var arc in s.Arcs)if(removed.Contains(arc.Start)||removed.Contains(arc.Middle)||removed.Contains(arc.End))removed.Add(arc.Id);
        foreach(var bezier in s.Beziers)if(removed.Contains(bezier.Start)||removed.Contains(bezier.Control)||removed.Contains(bezier.End))removed.Add(bezier.Id);
        foreach(var spline in s.Splines)if(spline.Controls.Any(removed.Contains))removed.Add(spline.Id);
        foreach(var(id,ends) in SketchFixedCurveEndpoints.All(s))if(removed.Contains(ends.Start)||removed.Contains(ends.End))removed.Add(id);
        foreach(var c in s.ExpressionCurves)if(c.Endpoints is {} e&&(removed.Contains(e.Start)||removed.Contains(e.End)))removed.Add(c.Id);
        var removedAnchors=s.Projections.Where(p=>removed.Contains(p.TargetEntity)).SelectMany(p=>p.Anchors)
            .Concat(s.NativeProjections.Where(p=>removed.Contains(p.TargetEntity)).SelectMany(p=>p.Anchors)).ToHashSet();
        foreach(var(_,ends) in SketchFixedCurveEndpoints.All(s).Where(e=>removed.Contains(e.Curve)))
        {removedAnchors.Add(ends.StartAnchor);removedAnchors.Add(ends.EndAnchor);}
        return s with{Freeforms=s.Freeforms.Where(e=>!removed.Contains(e.Id)).ToImmutableArray(),NativeProjections=s.NativeProjections.Where(p=>!removed.Contains(p.TargetEntity)).ToImmutableArray(),Ellipses=s.Ellipses.Where(e=>!removed.Contains(e.Id)).ToImmutableArray(),Projections=s.Projections.Where(p=>!removed.Contains(p.TargetEntity)).ToImmutableArray(),Points=s.Points.Where(p=>!removed.Contains(p.Id)).ToImmutableArray(),Lines=s.Lines.Where(l=>!removed.Contains(l.Id)).ToImmutableArray(),
            Circles=s.Circles.Where(c=>!removed.Contains(c.Id)).ToImmutableArray(),Arcs=s.Arcs.Where(a=>!removed.Contains(a.Id)).ToImmutableArray(),
            Beziers=s.Beziers.Where(b=>!removed.Contains(b.Id)).ToImmutableArray(),
            Splines=s.Splines.Where(b=>!removed.Contains(b.Id)).ToImmutableArray(),
            ExpressionCurves=s.ExpressionCurves.Where(c=>!removed.Contains(c.Id)).ToImmutableArray(),
            Constraints=s.Constraints.Where(c=>!removedAnchors.Contains(c.Id)&&!SketchConstraintEditing.Targets(c).Any(removed.Contains)).ToImmutableArray()};
    });
}

public static class SketchConstraintEditing
{
    public static bool KeepEndpointRelation(SketchConstraint constraint,SketchEntityId curve,SketchEntityId start,SketchEntityId end)
    {
        // A trim or endpoint extension reparameterizes this curve. Never reinterpret
        // an authored interior parameter as contact with a different point.
        if(constraint is FiniteParametricContactConstraint contact&&(contact.A==curve||contact.B==curve))return false;
        var joint=constraint switch
        {
            EndpointTangentConstraint c when c.A==curve||c.B==curve=>c.Joint,
            EndpointCurvatureConstraint c when c.A==curve||c.B==curve=>c.Joint,
            _=>(SketchEntityId?)null
        };
        return joint is null||joint==start||joint==end;
    }
    public static SketchEntityId[] Targets(SketchConstraint c)=>c switch
    {
        FixPointConstraint f=>[f.Point],CoincidentConstraint p=>[p.A,p.B],HorizontalConstraint h=>[h.Line],VerticalConstraint v=>[v.Line],
        DistanceConstraint d=>[d.A,d.B],OffsetXConstraint x=>[x.A,x.B],OffsetYConstraint y=>[y.A,y.B],LengthConstraint l=>[l.Line],
        ParallelConstraint p=>[p.A,p.B],PerpendicularConstraint p=>[p.A,p.B],EqualLengthConstraint e=>[e.A,e.B],RadiusConstraint r=>[r.Circle],EqualRadiusConstraint e=>[e.A,e.B],
        TangentConstraint t=>[t.Line,t.Circle],AngleConstraint a=>[a.A,a.B],
        MidpointConstraint m=>[m.Point,m.Line],SymmetryConstraint s=>[s.A,s.B,s.Axis],
        EndpointTangentConstraint t=>[t.A,t.B,t.Joint],
        EndpointCurvatureConstraint t=>[t.A,t.B,t.Joint],
        CircularContactConstraint t=>[t.A,t.B],
        FiniteParametricContactConstraint t=>[t.A,t.B],
        _=>throw new NotSupportedException()
    };
    public static double? Value(SketchConstraint c)=>c switch
    {FixPointConstraint f=>f.Position.X,DistanceConstraint d=>d.Distance,OffsetXConstraint x=>x.Offset,OffsetYConstraint y=>y.Offset,LengthConstraint l=>l.Length,RadiusConstraint r=>r.Radius,AngleConstraint a=>a.AngleRadians*180/Math.PI,_=>null};
    public static SketchConstraint SetValue(SketchConstraint c,double value,double y=0)=>c switch
    {FixPointConstraint f=>f with{Position=new(value,y)},DistanceConstraint d=>d with{Distance=value},OffsetXConstraint x=>x with{Offset=value},OffsetYConstraint o=>o with{Offset=value},LengthConstraint l=>l with{Length=value},RadiusConstraint r=>r with{Radius=value},AngleConstraint a=>a with{AngleRadians=value*Math.PI/180},_=>c};
}
