using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Editor;

/// <summary>Local authoring history. Nothing is published to the document until confirmation.</summary>
public sealed class SketchDraft(CadSketch initial)
{
    private readonly Stack<CadSketch> undo=[];
    private readonly Stack<CadSketch> redo=[];
    public CadSketch Value {get;private set;}=initial;
    public bool CanUndo=>undo.Count>0;
    public bool CanRedo=>redo.Count>0;
    public void Change(Func<CadSketch,CadSketch> change)
    {
        var next=change(Value);next.Validate();
        if(next.Id!=Value.Id||next.PartId!=Value.PartId||next.Revision!=Value.Revision)throw new CadValidationException("Draft identity cannot change.");
        undo.Push(Value);redo.Clear();Value=next;
    }
    public void Undo(){if(undo.TryPop(out var previous)){redo.Push(Value);Value=previous;}}
    public void Redo(){if(redo.TryPop(out var next)){undo.Push(Value);Value=next;}}
    public void UseSolvedCoordinates(CadSketch solved)=>Value=solved with{Revision=Value.Revision};
    public void AddPoint(Point2d position,bool construction=false)=>Change(s=>s with{Points=s.Points.Add(new(SketchEntityId.New(),position,construction))});
    public void AddLine(Point2d a,Point2d b,SketchEntityId? start=null,SketchEntityId? end=null,bool construction=false)=>Change(s=>
    {
        var points=s.Points;var p=start??SketchEntityId.New();var q=end??SketchEntityId.New();
        if(start is null)points=points.Add(new(p,a));if(end is null)points=points.Add(new(q,b));
        return s with{Points=points,Lines=s.Lines.Add(new(SketchEntityId.New(),p,q,construction))};
    });
    public void AddCircle(Point2d center,double radius,SketchEntityId? existingCenter=null,bool construction=false)=>Change(s=>
    {
        var point=existingCenter??SketchEntityId.New();var circle=SketchEntityId.New();
        return s with{Points=existingCenter is null?s.Points.Add(new(point,center)):s.Points,
            Circles=s.Circles.Add(new(circle,point,radius,construction)),Constraints=s.Constraints.Add(new RadiusConstraint(SketchConstraintId.New(),circle,radius))};
    });
    public void AddRectangle(Point2d a,Point2d b,bool construction=false)=>Change(s=>
    {
        var min=new Point2d(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y));var max=new Point2d(Math.Max(a.X,b.X),Math.Max(a.Y,b.Y));
        var p=new[]{min,new Point2d(max.X,min.Y),max,new Point2d(min.X,max.Y)}.Select(x=>new SketchPoint(SketchEntityId.New(),x)).ToImmutableArray();
        var l=Enumerable.Range(0,4).Select(i=>new SketchLine(SketchEntityId.New(),p[i].Id,p[(i+1)%4].Id,construction)).ToImmutableArray();
        return s with{Points=s.Points.AddRange(p),Lines=s.Lines.AddRange(l),Constraints=[..s.Constraints,
            new FixPointConstraint(SketchConstraintId.New(),p[0].Id,min),new HorizontalConstraint(SketchConstraintId.New(),l[0].Id),
            new VerticalConstraint(SketchConstraintId.New(),l[1].Id),new HorizontalConstraint(SketchConstraintId.New(),l[2].Id),new VerticalConstraint(SketchConstraintId.New(),l[3].Id),
            new OffsetXConstraint(SketchConstraintId.New(),p[0].Id,p[1].Id,max.X-min.X),new OffsetYConstraint(SketchConstraintId.New(),p[0].Id,p[3].Id,max.Y-min.Y)]};
    });
    public void MovePoint(SketchEntityId id,Point2d position)=>Change(s=>s with{Points=s.Points.Select(p=>p.Id==id?p with{Position=position}:p).ToImmutableArray()});
    public void RemoveEntities(IEnumerable<SketchEntityId> ids)=>Change(s=>
    {
        var removed=ids.ToHashSet();
        foreach(var line in s.Lines)if(removed.Contains(line.Start)||removed.Contains(line.End))removed.Add(line.Id);
        foreach(var circle in s.Circles)if(removed.Contains(circle.Center))removed.Add(circle.Id);
        return s with{Points=s.Points.Where(p=>!removed.Contains(p.Id)).ToImmutableArray(),Lines=s.Lines.Where(l=>!removed.Contains(l.Id)).ToImmutableArray(),
            Circles=s.Circles.Where(c=>!removed.Contains(c.Id)).ToImmutableArray(),Constraints=s.Constraints.Where(c=>!SketchConstraintEditing.Targets(c).Any(removed.Contains)).ToImmutableArray()};
    });
}

public static class SketchConstraintEditing
{
    public static SketchEntityId[] Targets(SketchConstraint c)=>c switch
    {
        FixPointConstraint f=>[f.Point],CoincidentConstraint p=>[p.A,p.B],HorizontalConstraint h=>[h.Line],VerticalConstraint v=>[v.Line],
        DistanceConstraint d=>[d.A,d.B],OffsetXConstraint x=>[x.A,x.B],OffsetYConstraint y=>[y.A,y.B],LengthConstraint l=>[l.Line],
        ParallelConstraint p=>[p.A,p.B],PerpendicularConstraint p=>[p.A,p.B],EqualLengthConstraint e=>[e.A,e.B],RadiusConstraint r=>[r.Circle],EqualRadiusConstraint e=>[e.A,e.B],
        _=>throw new NotSupportedException()
    };
    public static double? Value(SketchConstraint c)=>c switch
    {FixPointConstraint f=>f.Position.X,DistanceConstraint d=>d.Distance,OffsetXConstraint x=>x.Offset,OffsetYConstraint y=>y.Offset,LengthConstraint l=>l.Length,RadiusConstraint r=>r.Radius,_=>null};
    public static SketchConstraint SetValue(SketchConstraint c,double value,double y=0)=>c switch
    {FixPointConstraint f=>f with{Position=new(value,y)},DistanceConstraint d=>d with{Distance=value},OffsetXConstraint x=>x with{Offset=value},OffsetYConstraint o=>o with{Offset=value},LengthConstraint l=>l with{Length=value},RadiusConstraint r=>r with{Radius=value},_=>c};
}
