namespace Cadoryx.Db;

/// <summary>Exact supporting circles and a separate finite-domain proof. Never merges point identities.</summary>
public static class SketchCircularContact
{
    public static void ValidateTargets(CadSketch sketch,CircularContactConstraint relation)
    {
        bool Line(SketchEntityId id)=>sketch.Lines.Any(l=>l.Id==id);
        bool Circular(SketchEntityId id)=>sketch.Circles.Any(c=>c.Id==id)||sketch.Arcs.Any(a=>a.Id==id);
        if(relation.A==relation.B)throw Invalid("Circular contact requires two distinct curves.");
        bool linePair=Line(relation.A)&&Circular(relation.B)||Line(relation.B)&&Circular(relation.A);
        if(linePair&&relation.Branch is CircularContactBranch.LineLeft or CircularContactBranch.LineRight)return;
        if(Circular(relation.A)&&Circular(relation.B)&&relation.Branch is CircularContactBranch.External or CircularContactBranch.AContainsB or CircularContactBranch.BContainsA)return;
        throw Invalid("Select line/circular curves with a line side, or two circular curves with an explicit external/containment branch.");
    }
    private static CadValidationException Invalid(string message)
    {var error=new CadValidationException(message);error.Data["SketchContact.Message"]="ContactInvalidTargets";return error;}
    public static void RequireSolvedContacts(CadSketch sketch,double tolerance=1e-7)
    {
        foreach(var relation in sketch.Constraints.OfType<CircularContactConstraint>().Where(c=>c.IsEnabled))
            if(!TryContactPoint(sketch,relation,tolerance,out _,out var diagnostic))
            {
                var error=new CadValidationException(diagnostic);
                error.Data["SketchContact.Message"]=diagnostic=="SKETCH.CONTACT_OUTSIDE_DOMAIN"?"ContactOutsideDomain":"ContactBranchFailure";
                throw error;
            }
    }

    public static (Point2d Center,double Radius) Support(CadSketch sketch,SketchEntityId id)
    {
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        if(sketch.Circles.SingleOrDefault(c=>c.Id==id) is {} circle)return(points[circle.Center],circle.Radius);
        var arc=sketch.Arcs.SingleOrDefault(a=>a.Id==id)??throw new CadValidationException("Contact requires a circle or three-point arc.");
        var geometry=SketchArcGeometry.Through(points[arc.Start],points[arc.Middle],points[arc.End]);
        return(geometry.Center,geometry.Radius);
    }

    public static bool TryContactPoint(CadSketch sketch,CircularContactConstraint relation,double tolerance,
        out Point2d contact,out string diagnostic)
    {
        CadGuard.Positive(tolerance);ValidateTargets(sketch,relation);contact=default;diagnostic="SKETCH.CONTACT_BRANCH";
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        var line=sketch.Lines.SingleOrDefault(l=>l.Id==relation.A||l.Id==relation.B);
        if(line is not null)
        {
            var circular=line.Id==relation.A?relation.B:relation.A;var s=Support(sketch,circular);
            var a=points[line.Start];var b=points[line.End];double dx=b.X-a.X,dy=b.Y-a.Y,length=double.Hypot(dx,dy);
            if(length<=tolerance)return false;
            double signed=(dx*(s.Center.Y-a.Y)-dy*(s.Center.X-a.X))/length;
            int sign=relation.Branch==CircularContactBranch.LineLeft?1:-1;
            if(Math.Abs(signed-sign*s.Radius)>tolerance)return false;
            double along=((s.Center.X-a.X)*dx+(s.Center.Y-a.Y)*dy)/length;
            contact=new(a.X+dx/length*along,a.Y+dy/length*along);
            diagnostic="SKETCH.CONTACT_OUTSIDE_DOMAIN";
            if(along < -tolerance||along > length+tolerance||!Contains(sketch,circular,contact,tolerance))return false;
        }
        else
        {
            var a=Support(sketch,relation.A);var b=Support(sketch,relation.B);
            double dx=b.Center.X-a.Center.X,dy=b.Center.Y-a.Center.Y,length=double.Hypot(dx,dy);
            double distance=relation.Branch switch
            {CircularContactBranch.External=>a.Radius+b.Radius,CircularContactBranch.AContainsB=>a.Radius-b.Radius,_=>b.Radius-a.Radius};
            if(length<=tolerance||distance<=tolerance||Math.Abs(length-distance)>tolerance)return false;
            double sign=relation.Branch==CircularContactBranch.BContainsA?-1:1;
            contact=new(a.Center.X+sign*dx/length*a.Radius,a.Center.Y+sign*dy/length*a.Radius);
            diagnostic="SKETCH.CONTACT_OUTSIDE_DOMAIN";
            if(!Contains(sketch,relation.A,contact,tolerance)||!Contains(sketch,relation.B,contact,tolerance))return false;
        }
        diagnostic="SKETCH.CONTACT_VERIFIED";return true;
    }

    private static bool Contains(CadSketch sketch,SketchEntityId id,Point2d point,double tolerance)
    {
        if(sketch.Arcs.SingleOrDefault(a=>a.Id==id) is not {} arc)return true;
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        var start=points[arc.Start];var end=points[arc.End];
        var geometry=SketchArcGeometry.Through(start,points[arc.Middle],end);
        if(double.Hypot(point.X-start.X,point.Y-start.Y)<=tolerance||double.Hypot(point.X-end.X,point.Y-end.Y)<=tolerance)return true;
        double angle=Math.Atan2(point.Y-geometry.Center.Y,point.X-geometry.Center.X);
        double delta=(geometry.SweepAngle>0?angle-geometry.StartAngle:geometry.StartAngle-angle)%Math.Tau;
        if(delta<0)delta+=Math.Tau;
        return delta<=Math.Abs(geometry.SweepAngle)+tolerance/geometry.Radius;
    }
}
