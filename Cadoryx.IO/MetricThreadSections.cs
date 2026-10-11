using Cadoryx.Db;

namespace Cadoryx.IO;

internal static partial class MessagePackSections
{
    private static PackMetricThread? Thread(MetricThreadSpec? spec)=>spec is null?null:
        new(MetricThreadHoles.Catalog,spec.NominalMm,spec.PitchMm,(int)spec.Hand,spec.Through,spec.DepthMm,(int)spec.Representation,spec.Profile);
    private static MetricThreadSpec? Thread(PackMetricThread? spec)
    {
        if(spec is null)return null;
        if(spec.Catalog!=MetricThreadHoles.Catalog)throw new InvalidDataException("Unknown metric thread pilot catalog.");
        var result=new MetricThreadSpec(spec.NominalMm,spec.PitchMm,(ThreadHand)spec.Hand,spec.Through,spec.DepthMm,(ThreadHoleRepresentation)spec.Representation,spec.Profile);
        result.Validate();return result;
    }
    internal static ReadOnlyMemory<byte> UpgradeThreadHoleFeatures(ReadOnlyMemory<byte> bytes)
    {
        // Inspect all recipe companions, including payloads attached to an incompatible kind.
        static void RequireLegacy(PackRecipe r)
        {
            if(r.Hole?.Thread is not null||r.HolePattern?.Hole.Thread is not null)
                throw new InvalidDataException("Thread intent in a legacy feature section.");
            if(r.ExtrusionPattern is {} p)RequireLegacy(p.Template);
            if(r.Guided?.Sweep is {} sweep)RequireLegacy(sweep.Section);
            foreach(var channel in r.Guided?.Loft?.Channels??[])
                foreach(var profile in channel.Profiles)RequireLegacy(profile);
        }
        foreach(var f in Read<PackFeaturesV3>(bytes).Features)
        {
            var r=f.Recipe;
            RequireLegacy(new(r.Kind,r.Numbers,r.Placement,[],r.Profile,r.Operation,r.Holes,r.PolygonHoles,
                r.MixedCurves,r.MixedHoles,r.Islands,r.SplineControls,r.SplineHoles,r.Repetition,r.Hole,r.Extrusion,
                r.Guided,r.FaceFeature,r.RevolveAxis,r.HolePattern,r.ExtrusionPattern,r.SplineKnots,r.SplineHoleKnots,r.ContourLocal,r.FaceEdit));
        }
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeThreadHoleDrawings(ReadOnlyMemory<byte> bytes)
    {
        var book=Read<PackDrawingBook>(bytes);
        if(book.Sheets.Any(s=>s.HoleTables?.Any(t=>t.Rows.Any(r=>r.Evidence.Thread is not null))==true))
            throw new InvalidDataException("Thread hole proof in a legacy drawing section.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeModeledThreadFeatures(ReadOnlyMemory<byte> bytes)
    {
        static void LegacyThread(PackMetricThread? t)
        {
            if(t is not null&&(t.Representation!=0||t.Profile is not null))
                throw new InvalidDataException("Modeled thread intent or profile in a legacy symbolic-thread feature section.");
        }
        static void Recipe(PackRecipe r)
        {
            LegacyThread(r.Hole?.Thread);LegacyThread(r.HolePattern?.Hole.Thread);
            if(r.ExtrusionPattern is {} p)Recipe(p.Template);
            if(r.Guided?.Sweep is {} sweep)Recipe(sweep.Section);
            foreach(var channel in r.Guided?.Loft?.Channels??[])foreach(var profile in channel.Profiles)Recipe(profile);
        }
        foreach(var f in Read<PackFeaturesV3>(bytes).Features)
        {
            var r=f.Recipe;
            Recipe(new(r.Kind,r.Numbers,r.Placement,[],r.Profile,r.Operation,r.Holes,r.PolygonHoles,
                r.MixedCurves,r.MixedHoles,r.Islands,r.SplineControls,r.SplineHoles,r.Repetition,r.Hole,r.Extrusion,
                r.Guided,r.FaceFeature,r.RevolveAxis,r.HolePattern,r.ExtrusionPattern,r.SplineKnots,r.SplineHoleKnots,r.ContourLocal,r.FaceEdit));
        }
        return bytes;
    }

    internal static ReadOnlyMemory<byte> UpgradeModeledThreadDrawings(ReadOnlyMemory<byte> bytes)
    {
        var book=Read<PackDrawingBook>(bytes);
        if(book.Sheets.Any(s=>s.HoleTables?.Any(t=>t.Rows.Any(r=>r.Evidence.Thread is {} thread&&
            (thread.Representation!=0||thread.Profile is not null)))==true))
            throw new InvalidDataException("Modeled thread proof in a legacy symbolic-thread drawing section.");
        return bytes;
    }
}
