using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackSketches([property:Key(0)] PackSketch[] Sketches);
[MessagePackObject]
public sealed record PackSketch([property:Key(0)] Guid Id,[property:Key(1)] Guid Part,[property:Key(2)] string Name,
    [property:Key(3)] PackTransform Plane,[property:Key(4)] PackSketchPoint[] Points,[property:Key(5)] PackSketchLine[] Lines,
    [property:Key(6)] PackSketchCircle[] Circles,[property:Key(7)] PackSketchConstraint[] Constraints,[property:Key(8)] Guid Revision=default,
    [property:Key(9)] PackSketchArc[]? Arcs=null,[property:Key(10)] PackSketchBezier[]? Beziers=null,
    [property:Key(11)] PackSketchSpline[]? Splines=null,[property:Key(12)] PackSketchProjection[]? Projections=null,
    [property:Key(13)] PackSketchEllipse[]? Ellipses=null,[property:Key(14)] PackSketchNativeProjection[]? NativeProjections=null,[property:Key(15)] PackSketchFreeform[]? Freeforms=null);
[MessagePackObject]
public sealed record PackSketchProjection([property:Key(0)] Guid SourceSketch,[property:Key(1)] Guid SourceEntity,
    [property:Key(2)] Guid SourceRevision,[property:Key(3)] Guid TargetEntity,[property:Key(4)] Guid[] Anchors);
[MessagePackObject]
public sealed record PackSketchPoint([property:Key(0)] Guid Id,[property:Key(1)] double X,[property:Key(2)] double Y,[property:Key(3)] bool Construction,[property:Key(4)] int Origin=0);
[MessagePackObject]
public sealed record PackSketchLine([property:Key(0)] Guid Id,[property:Key(1)] Guid Start,[property:Key(2)] Guid End,[property:Key(3)] bool Construction);
[MessagePackObject]
public sealed record PackSketchCircle([property:Key(0)] Guid Id,[property:Key(1)] Guid Center,[property:Key(2)] double Radius,[property:Key(3)] bool Construction);
[MessagePackObject]
public sealed record PackSketchArc([property:Key(0)] Guid Id,[property:Key(1)] Guid Start,[property:Key(2)] Guid Middle,
    [property:Key(3)] Guid End,[property:Key(4)] bool Construction);
[MessagePackObject]
public sealed record PackSketchBezier([property:Key(0)] Guid Id,[property:Key(1)] Guid Start,[property:Key(2)] Guid Control,
    [property:Key(3)] Guid End,[property:Key(4)] bool Construction);
[MessagePackObject]
public sealed record PackSketchSpline([property:Key(0)] Guid Id,[property:Key(1)] Guid[] Controls,
    [property:Key(2)] bool Construction,[property:Key(3)] PackSplineKnots? Knots=null);
[MessagePackObject]
public sealed record PackSketchConstraint([property:Key(0)] Guid Id,[property:Key(1)] string Kind,[property:Key(2)] Guid[] Targets,
    [property:Key(3)] double[] Values,[property:Key(4)] bool Enabled);

internal static partial class MessagePackSections
{
    internal static byte[] EncodeSketches(IEnumerable<CadSketch> sketches)=>Serialize(new PackSketches(sketches.OrderBy(s=>s.Id.Value).Select(s=>new PackSketch(
        s.Id.Value,s.PartId.Value,s.Name,T(s.Plane),s.Points.Select(p=>new PackSketchPoint(p.Id.Value,p.Position.X,p.Position.Y,p.IsConstruction,(int)p.Origin)).ToArray(),
        s.Lines.Select(l=>new PackSketchLine(l.Id.Value,l.Start.Value,l.End.Value,l.IsConstruction)).ToArray(),
        s.Circles.Select(c=>new PackSketchCircle(c.Id.Value,c.Center.Value,c.Radius,c.IsConstruction)).ToArray(),
        s.Constraints.Where(c=>!SketchFiniteParametricContact.UsesExpression(s,c)).Select(SketchConstraint).ToArray(),s.Revision,
        s.Arcs.IsEmpty?null:s.Arcs.Select(a=>new PackSketchArc(a.Id.Value,a.Start.Value,a.Middle.Value,a.End.Value,a.IsConstruction)).ToArray(),
        s.Beziers.IsEmpty?null:s.Beziers.Select(b=>new PackSketchBezier(b.Id.Value,b.Start.Value,b.Control.Value,b.End.Value,b.IsConstruction)).ToArray(),
        s.Splines.IsEmpty?null:s.Splines.Select(b=>new PackSketchSpline(b.Id.Value,b.Controls.Select(id=>id.Value).ToArray(),b.IsConstruction,PackKnots(b.Knots))).ToArray(),
        s.Projections.IsEmpty?null:s.Projections.Select(p=>new PackSketchProjection(p.SourceSketch.Value,p.SourceEntity.Value,p.SourceRevision,p.TargetEntity.Value,p.Anchors.Select(id=>id.Value).ToArray())).ToArray(),
        s.Ellipses.IsEmpty?null:s.Ellipses.Select(e=>new PackSketchEllipse(e.Id.Value,PackEllipse(e.Geometry)!,e.IsConstruction,PackEndpoints(e.Endpoints))).ToArray(),
        s.NativeProjections.IsEmpty?null:s.NativeProjections.Select(p=>new PackSketchNativeProjection(p.TargetEntity.Value,PackExact(p.Selection),G(p.Source),T(p.TargetPlane),p.Anchors.Select(id=>id.Value).ToArray())).ToArray(),
        s.Freeforms.IsEmpty?null:s.Freeforms.Select(e=>new PackSketchFreeform(e.Id.Value,PackFreeform(e.Geometry)!,e.IsConstruction,PackEndpoints(e.Endpoints))).ToArray())).ToArray()));

    internal static byte[] UpgradeSketchRevisions(ReadOnlyMemory<byte> bytes)
    {
        RequireSketchPointFields(bytes,4);
        var legacy=Read<PackSketches>(bytes);
        if(legacy.Sketches.Any(s=>s.Revision!=Guid.Empty))throw new InvalidDataException("Unexpected revision in legacy sketch data.");
        // Stable baseline on every load of the same legacy document; subsequent edits receive fresh revisions.
        return SerializeLegacySketches(new PackSketches(legacy.Sketches.Select(s=>s with{Revision=s.Id}).ToArray()));
    }
    internal static ReadOnlyMemory<byte> UpgradeAngularSketchConstraints(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackSketches>(bytes);
        if(old.Sketches.Any(s=>s.Constraints.Any(c=>c.Kind is "tangent-line-circle" or "angle-lines")))
            throw new InvalidDataException("Angular sketch constraint in legacy sketches schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeSketchArcs(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackSketches>(bytes);
        if(old.Sketches.Any(s=>s.Arcs is not null))throw new InvalidDataException("Three-point arc in legacy sketches schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeSketchBeziers(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackSketches>(bytes);
        if(old.Sketches.Any(s=>s.Beziers is not null))throw new InvalidDataException("Bezier in legacy sketches schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeSketchSplines(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackSketches>(bytes);
        if(old.Sketches.Any(s=>s.Splines is not null))throw new InvalidDataException("Spline in legacy sketches schema.");
        return bytes;
    }

    internal static ReadOnlyMemory<byte> UpgradeSketchProjections(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackSketches>(bytes).Sketches.Any(s=>s.Projections is not null))throw new InvalidDataException("Sketch projection in legacy schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeSketchBasicRelations(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackSketches>(bytes).Sketches.Any(s=>s.Constraints.Any(c=>c.Kind is "midpoint-point-line" or "symmetric-points-line")))
            throw new InvalidDataException("Basic sketch relation in legacy schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeSketchEndpointTangency(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackSketches>(bytes).Sketches.Any(s=>s.Constraints.Any(c=>c.Kind=="endpoint-tangent-curves")))
            throw new InvalidDataException("Endpoint tangency in legacy sketch schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeSketchCircularContact(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackSketches>(bytes).Sketches.Any(s=>s.Constraints.Any(c=>c.Kind=="circular-contact")))
            throw new InvalidDataException("Finite circular contact in legacy sketch schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeSketchEndpointCurvature(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackSketches>(bytes).Sketches.Any(s=>s.Constraints.Any(c=>c.Kind=="endpoint-curvature-curves")))
            throw new InvalidDataException("Endpoint curvature continuity in legacy sketch schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeSketchFiniteContact(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackSketches>(bytes).Sketches.Any(s=>s.Constraints.Any(c=>c.Kind=="finite-parametric-contact")))
            throw new InvalidDataException("Finite parametric contact in legacy sketch schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeSketchMixedFiniteContinuity(ReadOnlyMemory<byte> bytes)
    {
        foreach(var sketch in Read<PackSketches>(bytes).Sketches)
        {
            var allowed=sketch.Lines.Select(c=>c.Id).Concat((sketch.Beziers??[]).Select(c=>c.Id)).Concat((sketch.Splines??[]).Select(c=>c.Id)).ToHashSet();
            if(sketch.Constraints.Any(c=>c.Kind=="finite-parametric-curvature"||c.Kind=="finite-parametric-contact"&&c.Targets.Any(id=>!allowed.Contains(id))))
                throw new InvalidDataException("Mixed or curvature contact cannot appear in the legacy finite-contact schema.");
        }
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeSketchSlidingContact(ReadOnlyMemory<byte> bytes)
    {
        if(HasSlidingContact(bytes))throw new InvalidDataException("Automatic contact authors cannot appear in the legacy fixed-contact schema.");
        return bytes;
    }
    internal static bool HasSlidingContact(ReadOnlyMemory<byte> bytes)=>Read<PackSketches>(bytes).Sketches
        .Any(s=>s.Constraints.Any(c=>c.Kind=="sliding-parametric-contact"));
    internal static ReadOnlyMemory<byte> UpgradeSketchParametricTangency(ReadOnlyMemory<byte> bytes)
    {
        foreach(var sketch in Read<PackSketches>(bytes).Sketches)
        {
            var parametric=(sketch.Beziers??[]).Select(b=>b.Id).Concat((sketch.Splines??[]).Select(s=>s.Id)).ToHashSet();
            if(sketch.Constraints.Any(c=>c.Kind=="endpoint-tangent-curves"&&c.Targets.Take(2).Any(parametric.Contains)))
                throw new InvalidDataException("Parametric endpoint tangency in legacy sketch schema.");
        }
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeSketchArcRadius(ReadOnlyMemory<byte> bytes)
    {
        foreach(var sketch in Read<PackSketches>(bytes).Sketches)
        {
            var arcs=(sketch.Arcs??[]).Select(a=>a.Id).ToHashSet();
            if(sketch.Constraints.Any(c=>c.Kind is "radius" or "equal-radius"&&c.Targets.Any(arcs.Contains)))
                throw new InvalidDataException("Arc radius relation in legacy sketch schema.");
        }
        return bytes;
    }
    internal static ImmutableDictionary<SketchId,CadSketch> DecodeSketches(ReadOnlyMemory<byte> bytes,IReadOnlyDictionary<AssetId,AssetFormat>? formats=null)
    {
        GeometryAssetRef NativeSource(PackGeometry packed)
        {
            var geometry=G(packed);if(formats is null)return geometry;
            if(!formats.TryGetValue(geometry.AssetId,out var format))throw new InvalidDataException("Missing native projection asset.");
            AssetFormatPolicy.RequireBRep(format);var context=geometry.Source;
            if(context is not null)
            {
                if(!formats.TryGetValue(context.ContextAssetId,out var contextFormat))throw new InvalidDataException("Missing native projection XDE context.");
                AssetFormatPolicy.RequireXde(contextFormat);context=context with{Format=contextFormat};
            }
            return AttachReferenceFormat(geometry with{Format=format,Source=context},formats);
        }
        RequireSketchPointFields(bytes,5);
        var result=ImmutableDictionary.CreateBuilder<SketchId,CadSketch>();
        foreach(var s in Read<PackSketches>(bytes).Sketches)
        {
            var sketch=new CadSketch(new(s.Id),new(s.Part),s.Name,T(s.Plane),
                s.Points.Select(p=>new SketchPoint(new(p.Id),new(p.X,p.Y),p.Construction){Origin=(SketchPointOrigin)p.Origin}).ToImmutableArray(),
                s.Lines.Select(l=>new SketchLine(new(l.Id),new(l.Start),new(l.End),l.Construction)).ToImmutableArray(),
                s.Circles.Select(c=>new SketchCircle(new(c.Id),new(c.Center),c.Radius,c.Construction)).ToImmutableArray(),
                s.Constraints.Select(SketchConstraint).ToImmutableArray())
                {Revision=s.Revision,Arcs=s.Arcs is null?[]:s.Arcs.Select(a=>new SketchArc(new(a.Id),new(a.Start),new(a.Middle),new(a.End),a.Construction)).ToImmutableArray(),
                    Beziers=s.Beziers is null?[]:s.Beziers.Select(b=>new SketchBezier(new(b.Id),new(b.Start),new(b.Control),new(b.End),b.Construction)).ToImmutableArray(),
                    Splines=s.Splines is null?[]:s.Splines.Select(b=>new SketchSpline(new(b.Id),b.Controls.Select(id=>new SketchEntityId(id)).ToImmutableArray(),b.Construction){Knots=ReadKnots(b.Knots)}).ToImmutableArray(),
                    Projections=s.Projections is null?[]:s.Projections.Select(p=>new SketchProjection(new(p.SourceSketch),new(p.SourceEntity),p.SourceRevision,new(p.TargetEntity),p.Anchors.Select(id=>new SketchConstraintId(id)).ToImmutableArray())).ToImmutableArray(),
                    Freeforms=s.Freeforms is null?[]:s.Freeforms.Select(e=>new SketchFreeform(new(e.Id),ReadFreeform(e.Geometry)??throw new InvalidDataException("Missing freeform geometry."),e.Construction){Endpoints=ReadEndpoints(e.Endpoints)}).ToImmutableArray(),
                    Ellipses=s.Ellipses is null?[]:s.Ellipses.Select(e=>new SketchEllipse(new(e.Id),ReadEllipse(e.Geometry)??throw new InvalidDataException("Missing ellipse geometry."),e.Construction){Endpoints=ReadEndpoints(e.Endpoints)}).ToImmutableArray(),
                    NativeProjections=s.NativeProjections is null?[]:s.NativeProjections.Select(p=>new SketchNativeProjection(new(p.TargetEntity),UnpackExact(p.Selection),NativeSource(p.Source),T(p.Plane),p.Anchors.Select(id=>new SketchConstraintId(id)).ToImmutableArray())).ToImmutableArray()};
            sketch.Validate();result.Add(sketch.Id,sketch);
        }
        return result.ToImmutable();
    }
    internal static PackSketchConstraint SketchConstraint(SketchConstraint c)
    {
        (string Kind,SketchEntityId[] Targets,double[] Values) data=c switch
        {
            FixPointConstraint f=>("fix-point",[f.Point],[f.Position.X,f.Position.Y]),
            CoincidentConstraint p=>("coincident",[p.A,p.B],[]),HorizontalConstraint h=>("horizontal",[h.Line],[]),VerticalConstraint v=>("vertical",[v.Line],[]),
            DistanceConstraint d=>("distance",[d.A,d.B],[d.Distance]),OffsetXConstraint x=>("offset-x",[x.A,x.B],[x.Offset]),OffsetYConstraint y=>("offset-y",[y.A,y.B],[y.Offset]),
            LengthConstraint l=>("length",[l.Line],[l.Length]),ParallelConstraint p=>("parallel",[p.A,p.B],[]),PerpendicularConstraint p=>("perpendicular",[p.A,p.B],[]),
            EqualLengthConstraint e=>("equal-length",[e.A,e.B],[]),RadiusConstraint r=>("radius",[r.Circle],[r.Radius]),EqualRadiusConstraint e=>("equal-radius",[e.A,e.B],[]),
            TangentConstraint t=>("tangent-line-circle",[t.Line,t.Circle],[]),AngleConstraint a=>("angle-lines",[a.A,a.B],[a.AngleRadians]),
            MidpointConstraint m=>("midpoint-point-line",[m.Point,m.Line],[]),SymmetryConstraint s=>("symmetric-points-line",[s.A,s.B,s.Axis],[]),
            EndpointTangentConstraint t=>("endpoint-tangent-curves",[t.A,t.B,t.Joint],[(double)t.Branch]),
            EndpointCurvatureConstraint t=>("endpoint-curvature-curves",[t.A,t.B,t.Joint],[(double)t.Branch]),
            CircularContactConstraint t=>("circular-contact",[t.A,t.B],[(double)t.Branch]),
            FiniteParametricContactConstraint t when t.ParameterMode!=FiniteContactParameterMode.Fixed=>("sliding-parametric-contact",[t.A,t.B],
                [t.ParameterA,t.ParameterB,(double)t.Branch,(double)t.Continuity,(double)t.ParameterMode,
                 t.SlidingA?.Seed??0,t.SlidingA?.Lower??0,t.SlidingA?.Upper??0,t.SlidingB?.Seed??0,t.SlidingB?.Lower??0,t.SlidingB?.Upper??0]),
            FiniteParametricContactConstraint t=>(t.Continuity==FiniteContactContinuity.Curvature?"finite-parametric-curvature":"finite-parametric-contact",[t.A,t.B],[t.ParameterA,t.ParameterB,(double)t.Branch]),
            _=>throw new NotSupportedException("Unknown sketch constraint cannot be persisted.")
        };
        return new(c.Id.Value,data.Kind,data.Targets.Select(t=>t.Value).ToArray(),data.Values,c.IsEnabled);
    }
    internal static SketchConstraint SketchConstraint(PackSketchConstraint c)
    {
        var (targets,values)=c.Kind switch
        {
            "fix-point"=>(1,2),"horizontal" or "vertical"=>(1,0),"length" or "radius"=>(1,1),
            "distance" or "offset-x" or "offset-y"=>(2,1),"coincident" or "parallel" or "perpendicular" or "equal-length" or "equal-radius" or "tangent-line-circle"=>(2,0),
            "angle-lines"=>(2,1),
            "midpoint-point-line"=>(2,0),"symmetric-points-line"=>(3,0),
            "endpoint-tangent-curves" or "endpoint-curvature-curves"=>(3,1),
            "circular-contact"=>(2,1),
            "finite-parametric-contact" or "finite-parametric-curvature"=>(2,3),
            "sliding-parametric-contact"=>(2,11),
            _=>throw new NotSupportedException("Unknown sketch constraint: "+c.Kind)
        };
        if(c.Targets.Length!=targets||c.Values.Length!=values)throw new InvalidDataException("Malformed sketch constraint arguments.");
        var id=new SketchConstraintId(c.Id);var a=new SketchEntityId(c.Targets[0]);var b=targets==2?new SketchEntityId(c.Targets[1]):default;
        double value=values>0?c.Values[0]:0;bool enabled=c.Enabled;
        return c.Kind switch
        {
            "fix-point"=>new FixPointConstraint(id,a,new(value,c.Values[1]),enabled),"coincident"=>new CoincidentConstraint(id,a,b,enabled),
            "horizontal"=>new HorizontalConstraint(id,a,enabled),"vertical"=>new VerticalConstraint(id,a,enabled),
            "distance"=>new DistanceConstraint(id,a,b,value,enabled),"offset-x"=>new OffsetXConstraint(id,a,b,value,enabled),"offset-y"=>new OffsetYConstraint(id,a,b,value,enabled),
            "length"=>new LengthConstraint(id,a,value,enabled),"parallel"=>new ParallelConstraint(id,a,b,enabled),"perpendicular"=>new PerpendicularConstraint(id,a,b,enabled),
            "equal-length"=>new EqualLengthConstraint(id,a,b,enabled),"radius"=>new RadiusConstraint(id,a,value,enabled),"equal-radius"=>new EqualRadiusConstraint(id,a,b,enabled),
            "tangent-line-circle"=>new TangentConstraint(id,a,b,enabled),"angle-lines"=>new AngleConstraint(id,a,b,value,enabled),
            "midpoint-point-line"=>new MidpointConstraint(id,a,new(c.Targets[1]),enabled),
            "symmetric-points-line"=>new SymmetryConstraint(id,a,new(c.Targets[1]),new(c.Targets[2]),enabled),
            "endpoint-tangent-curves"=>new EndpointTangentConstraint(id,a,new(c.Targets[1]),new(c.Targets[2]),
                value==1?EndpointTangentBranch.Opposed:throw new InvalidDataException("Unsupported endpoint tangent branch."),enabled),
            "endpoint-curvature-curves"=>new EndpointCurvatureConstraint(id,a,new(c.Targets[1]),new(c.Targets[2]),
                value==1?EndpointTangentBranch.Opposed:throw new InvalidDataException("Unsupported endpoint curvature branch."),enabled),
            "finite-parametric-contact" or "finite-parametric-curvature"=>new FiniteParametricContactConstraint(id,a,b,value,c.Values[1],c.Values[2] switch
                {1=>FiniteContactTangentBranch.SameDirection,2=>FiniteContactTangentBranch.OppositeDirection,
                    _=>throw new InvalidDataException("Unsupported finite contact branch.")},enabled)
                {Continuity=c.Kind=="finite-parametric-curvature"?FiniteContactContinuity.Curvature:FiniteContactContinuity.Tangent},
            "sliding-parametric-contact"=>Sliding(),
            "circular-contact"=>new CircularContactConstraint(id,a,b,value switch
                {1=>CircularContactBranch.LineLeft,2=>CircularContactBranch.LineRight,3=>CircularContactBranch.External,
                    4=>CircularContactBranch.AContainsB,5=>CircularContactBranch.BContainsA,_=>throw new InvalidDataException("Unsupported circular contact branch.")},enabled),
            _=>throw new NotSupportedException(c.Kind)
        };
        FiniteParametricContactConstraint Sliding()
        {
            var mode=c.Values[4] switch{2=>FiniteContactParameterMode.SolveA,3=>FiniteContactParameterMode.SolveB,4=>FiniteContactParameterMode.SolveBoth,
                _=>throw new InvalidDataException("An automatic contact needs an explicit nonfixed mode.")};
            var relation=new FiniteParametricContactConstraint(id,a,b,value,c.Values[1],c.Values[2] switch
            {1=>FiniteContactTangentBranch.SameDirection,2=>FiniteContactTangentBranch.OppositeDirection,_=>throw new InvalidDataException("Invalid contact direction.")},enabled)
            {ParameterMode=mode,Continuity=c.Values[3] switch{1=>FiniteContactContinuity.Tangent,2=>FiniteContactContinuity.Curvature,_=>throw new InvalidDataException("Invalid contact continuity.")},
             SlidingA=mode is FiniteContactParameterMode.SolveA or FiniteContactParameterMode.SolveBoth?new(c.Values[5],c.Values[6],c.Values[7]):null,
             SlidingB=mode is FiniteContactParameterMode.SolveB or FiniteContactParameterMode.SolveBoth?new(c.Values[8],c.Values[9],c.Values[10]):null};
            if(!relation.SolvesA&&c.Values.Skip(5).Take(3).Any(v=>v!=0)||!relation.SolvesB&&c.Values.Skip(8).Take(3).Any(v=>v!=0))
                throw new InvalidDataException("Fixed contact parameters cannot contain hidden sliding authors.");
            return relation;
        }
    }
}
