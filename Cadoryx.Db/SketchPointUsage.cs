using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Author point usage includes every typed curve and explicit projection target.
/// Coordinates, nearest neighbours and native topology enumeration do not establish ownership.</summary>
public static class SketchPointUsage
{
    public static HashSet<SketchEntityId> Referenced(CadSketch sketch)
    {
        var used=sketch.Lines.SelectMany(l=>new[]{l.Start,l.End})
            .Concat(sketch.Circles.Select(c=>c.Center))
            .Concat(sketch.Arcs.SelectMany(c=>new[]{c.Start,c.Middle,c.End}))
            .Concat(sketch.Beziers.SelectMany(c=>new[]{c.Start,c.Control,c.End}))
            .Concat(sketch.Splines.SelectMany(c=>c.Controls))
            .Concat(SketchFixedCurveEndpoints.All(sketch).SelectMany(c=>new[]{c.Ends.Start,c.Ends.End}))
            .Concat(sketch.ExpressionCurves.Where(c=>c.Endpoints is not null).SelectMany(c=>new[]{c.Endpoints!.Start,c.Endpoints.End}))
            .ToHashSet();
        var points=sketch.Points.Select(p=>p.Id).ToHashSet();
        used.UnionWith(sketch.Projections.Select(p=>p.TargetEntity).Where(points.Contains));
        used.UnionWith(sketch.NativeProjections.Select(p=>p.TargetEntity).Where(points.Contains));
        return used;
    }
    /// <summary>Only explicitly generated, now unreferenced points in this operation's
    /// candidate set may be collected. Unknown old point origins stay editable and stored.</summary>
    public static ImmutableHashSet<SketchEntityId> GeneratedOrphans(CadSketch sketch,IEnumerable<SketchEntityId> candidates)
    {
        var selected=candidates.ToHashSet();var used=Referenced(sketch);
        return sketch.Points.Where(p=>selected.Contains(p.Id)&&p.Origin==SketchPointOrigin.CurveGenerated&&!used.Contains(p.Id))
            .Select(p=>p.Id).ToImmutableHashSet();
    }
    public static void ValidateDrivenEdit(CadSketch before,CadSketch after)
    {
        var previous=before.Points.ToDictionary(p=>p.Id,p=>p.Origin);
        if(after.Points.Any(p=>previous.TryGetValue(p.Id,out var origin)&&origin!=p.Origin))
            throw new CadValidationException("An existing point origin cannot be reinterpreted by an edit.");
    }
    public static void RequireUnchangedOrigins(CadSketch authored,CadSketch solved)
    {
        if(!authored.Points.Select(p=>(p.Id,p.IsConstruction,p.Origin)).SequenceEqual(solved.Points.Select(p=>(p.Id,p.IsConstruction,p.Origin))))
            throw new CadValidationException("Solver changed point identity, construction or author origin.");
    }
}
