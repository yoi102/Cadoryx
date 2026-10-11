using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum GuidedOutput { Solid=0, Sheet=1 }
public enum SweepCorner { Transformed=0, RightCorner=1, RoundCorner=2 }

/// <summary>Authored order and direction are authoritative. Native code must not reorder this open chain.</summary>
public sealed record SweepPath(ImmutableArray<SketchBoundaryCurve> Curves,RigidTransform3d Placement)
{
    public SketchPathReference? Source {get;init;}
    public void Validate()
    {
        Placement.Validate();
        if(Curves.IsDefaultOrEmpty||Curves.Length>256)throw new CadValidationException("A sweep needs one bounded open path.");
        for(int i=0;i<Curves.Length;i++)
        {
            var c=Curves[i]??throw new CadValidationException("Missing sweep curve.");
            CadGuard.Finite(c.Start.X,c.Start.Y,c.End.X,c.End.Y);
            if(c.SplineControls.IsDefault||c.SplineKnots is not null&&c.SplineControls.IsEmpty)throw new CadValidationException("Uninitialized path spline.");
            if((c.Middle is null?0:1)+(c.BezierControl is null?0:1)+(c.SplineControls.IsEmpty?0:1)+(c.Ellipse is null?0:1)+(c.Freeform is null?0:1)+(c.Expression is null?0:1)>1)
                throw new CadValidationException("A path edge needs one curve definition.");
            if(c.Expression is {} expression)
            {
                expression.Validate();
                if(Distance(c.Start,expression.At(0))>1e-7||Distance(c.End,expression.At(1))>1e-7||Distance(c.Start,c.End)<=1e-7||
                    double.Hypot(expression.Evaluate(0).First.X,expression.Evaluate(0).First.Y)<=1e-10||
                    double.Hypot(expression.Evaluate(1).First.X,expression.Evaluate(1).First.Y)<=1e-10)
                    throw new CadValidationException("An expression path must retain its exact interval and nonzero endpoint tangents.");
            }
            else if(c.Freeform is {} freeform)
            {
                freeform.Validate();
                if(Distance(c.Start,freeform.At(0))>1e-7||Distance(c.End,freeform.At(1))>1e-7||Distance(c.Start,c.End)<=1e-7||
                   double.Hypot(freeform.Evaluate(0).Derivative.X,freeform.Evaluate(0).Derivative.Y)<=1e-10||
                   double.Hypot(freeform.Evaluate(1).Derivative.X,freeform.Evaluate(1).Derivative.Y)<=1e-10)
                    throw new CadValidationException("A freeform path must match its interval and have nonzero endpoint tangents.");
            }
            else if(c.Ellipse is {} ellipse)
            {
                ellipse.Validate();
                if(Distance(c.Start,ellipse.At(0))>1e-7||Distance(c.End,ellipse.At(1))>1e-7||Distance(c.Start,c.End)<=1e-7)
                    throw new CadValidationException("A finite conic path must match its interval and remain open.");
            }
            else if(c.Middle is {} m)_=SketchArcGeometry.Through(c.Start,m,c.End);
            else if(c.BezierControl is {} b)
            {
                CadGuard.Finite(b.X,b.Y);
                if(Distance(c.Start,c.End)<=1e-7||Distance(c.Start,b)<=1e-7||Distance(b,c.End)<=1e-7)
                    throw new CadValidationException("A path Bezier needs distinct endpoints and nonzero endpoint tangents.");
            }
            else if(!c.SplineControls.IsEmpty)
            {
                c.SplineDefinition.Validate();
                foreach(var p in c.SplineControls)CadGuard.Finite(p.X,p.Y);
                if(Distance(c.Start,c.End)<=1e-7||Distance(c.SplineControls[0],c.SplineControls[1])<=1e-7||
                    Distance(c.SplineControls[^2],c.SplineControls[^1])<=1e-7)
                    throw new CadValidationException("A path spline needs distinct endpoints and nonzero endpoint tangents.");
                if(c.SplineControls[0]!=c.Start||c.SplineControls[^1]!=c.End)
                    throw new CadValidationException("Path spline endpoints differ from its controls.");
            }
            else if(Distance(c.Start,c.End)<=1e-7)throw new CadValidationException("Zero length path edge.");
            if(i>0&&Distance(Curves[i-1].End,c.Start)>1e-7)
                throw new CadValidationException("Path order/direction has a gap. Choose the directed chain explicitly.");
        }
        if(Distance(Curves[0].Start,Curves[^1].End)<=1e-7)
            throw new CadValidationException("This sweep contract requires an open path.");
    }
    private static double Distance(Point2d a,Point2d b)=>double.Hypot(a.X-b.X,a.Y-b.Y);
}

public sealed record AuthoringSection(SketchProfile Profile,RigidTransform3d Placement)
{
    public SketchProfileReference? Source {get;init;}
    public void Validate(){Profile.Validate();Placement.Validate();}
    public static void RequireSingleBoundary(SketchProfile p)
    {
        p.Validate();
        if(!p.Holes.IsEmpty||!p.PolygonHoles.IsEmpty||!p.MixedHoles.IsEmpty||!p.SplineHoles.IsEmpty||!p.Islands.IsEmpty)
            throw new CadValidationException("Each loft correspondence channel contains one boundary; author holes as explicit child channels.");
    }
}

public sealed record SweepRecipe(AuthoringSection Section,SweepPath Path,GuidedOutput Output=GuidedOutput.Solid,
    SweepCorner Corner=SweepCorner.Transformed) : GeometryRecipe
{
    public override void Validate()
    {
        if(Section is null||Path is null||!Enum.IsDefined(Output)||!Enum.IsDefined(Corner))
            throw new CadValidationException("Invalid sweep payload.");
        Section.Validate();Path.Validate();
        if(Output==GuidedOutput.Sheet)AuthoringSection.RequireSingleBoundary(Section.Profile);
    }
}

/// <summary>All channels use the same authored station order. Parent is an explicit hole/island relation, never an inferred index.</summary>
public sealed record LoftChannel(Guid Id,Guid? Parent,ImmutableArray<SketchProfile> Profiles)
{
    public ImmutableArray<SketchProfileReference?> Sources {get;init;}=[];
}
public sealed record LoftRecipe(ImmutableArray<RigidTransform3d> Stations,ImmutableArray<LoftChannel> Channels,
    GuidedOutput Output=GuidedOutput.Solid,bool Ruled=false) : GeometryRecipe
{
    public override void Validate()
    {
        if(!Enum.IsDefined(Output)||Stations.IsDefault||Stations.Length is <2 or >128||Channels.IsDefaultOrEmpty||Channels.Length>64)
            throw new CadValidationException("Invalid ordered loft sections.");
        foreach(var s in Stations)s.Validate();
        var ids=new HashSet<Guid>();
        foreach(var c in Channels)
        {
            if(c is null||c.Id==Guid.Empty||c.Parent==Guid.Empty||c.Parent==c.Id||!ids.Add(c.Id)||c.Profiles.IsDefault||c.Profiles.Length!=Stations.Length||
                c.Sources.IsDefault||!c.Sources.IsEmpty&&c.Sources.Length!=Stations.Length)
                throw new CadValidationException("Loft correspondence identity or station count differs.");
            foreach(var profile in c.Profiles)AuthoringSection.RequireSingleBoundary(profile);
        }
        if(Channels.Count(c=>c.Parent is null)!=1||Output==GuidedOutput.Sheet&&Channels.Length!=1)
            throw new CadValidationException("Loft needs one root boundary; sheet output needs a single channel.");
        var map=Channels.ToDictionary(c=>c.Id);
        foreach(var c in Channels)
        {
            var seen=new HashSet<Guid>{c.Id};var parent=c.Parent;
            while(parent is {} p)
            {
                if(!map.TryGetValue(p,out var next)||!seen.Add(p)||seen.Count>12)
                    throw new CadValidationException("Loft channel parent is missing, cyclic or too deep.");
                parent=next.Parent;
            }
        }
        // This initial ordered section contract rejects reversed station progress. It never sorts sections.
        var axis=Stations[0].Rotation.Rotate(Vector3d.UnitZ);double previous=0;
        for(int i=1;i<Stations.Length;i++)
        {
            double progress=(Stations[i].Translation-Stations[0].Translation).Dot(axis);
            if(progress-previous<=1e-6||Stations[i].Rotation.Rotate(Vector3d.UnitZ).Dot(axis)<=1e-7)
                throw new CadValidationException("Loft section order or normal reverses the authored progression.");
            previous=progress;
        }
    }
}
