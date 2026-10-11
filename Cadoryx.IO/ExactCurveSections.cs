using Cadoryx.Db;

namespace Cadoryx.IO;

internal static partial class MessagePackSections
{
    internal static ReadOnlyMemory<byte> UpgradeExactCurveSketches(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackSketches>(bytes).Sketches.Any(s=>(s.Splines??[]).Any(c=>c.Knots is not null||c.Controls.Length>SketchSplineGeometry.MaximumControls)))
            throw new InvalidDataException("Exact author curves in legacy sketch schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeExactCurveFeatures(ReadOnlyMemory<byte> bytes,bool ellipsesOnly=false,bool freeformsOnly=false,bool periodicIntervalsOnly=false)
    {
        void Curves(PackMixedCurve[]? curves)
        {
            foreach(var c in curves??[])
            {
                if(periodicIntervalsOnly){RequireLegacyPeriodicInterval(c.Freeform);continue;}
                if(c.Freeform is not null||!freeformsOnly&&(c.Ellipse is not null||!ellipsesOnly&&(c.SplineKnots is not null||c.SplineControls?.Length>SketchSplineGeometry.MaximumControls)))
                    throw new InvalidDataException("Exact author curve in legacy feature boundary.");
            }
        }
        void Island(PackIsland i)
        {
            if(!periodicIntervalsOnly&&!ellipsesOnly&&!freeformsOnly&&(i.SplineKnots is not null||i.SplineHoleKnots is not null||i.SplineControls?.Length>SketchSplineGeometry.MaximumControls||
                i.SplineHoles?.Any(h=>h.Length>SketchSplineGeometry.MaximumControls)==true))
                throw new InvalidDataException("Exact author curve in legacy island.");
            Curves(i.MixedCurves);foreach(var h in i.MixedHoles??[])Curves(h);
            foreach(var child in i.Islands??[])Island(child);
        }
        void Recipe(PackRecipe r)
        {
            if(!periodicIntervalsOnly&&!ellipsesOnly&&!freeformsOnly&&(r.SplineKnots is not null||r.SplineHoleKnots is not null||r.SplineControls?.Length>SketchSplineGeometry.MaximumControls||
                r.SplineHoles?.Any(h=>h.Length>SketchSplineGeometry.MaximumControls)==true))
                throw new InvalidDataException("Exact author curve in legacy feature recipe.");
            Curves(r.MixedCurves);foreach(var h in r.MixedHoles??[])Curves(h);
            foreach(var island in r.Islands??[])Island(island);
            if(r.ExtrusionPattern is {} pattern)Recipe(pattern.Template);
            if(r.Guided?.Sweep is {} sweep){Recipe(sweep.Section);Curves(sweep.Path);}
            if(r.Guided?.Loft is {} loft)foreach(var channel in loft.Channels)foreach(var profile in channel.Profiles)Recipe(profile);
        }
        foreach(var f in Read<PackFeaturesV3>(bytes).Features)
        {
            var r=f.Recipe;
            Recipe(new(r.Kind,r.Numbers,r.Placement,[],r.Profile,r.Operation,r.Holes,r.PolygonHoles,r.MixedCurves,r.MixedHoles,r.Islands,
                r.SplineControls,r.SplineHoles,r.Repetition,r.Hole,r.Extrusion,r.Guided,r.FaceFeature,r.RevolveAxis,r.HolePattern,r.ExtrusionPattern,
                r.SplineKnots,r.SplineHoleKnots));
        }
        return bytes;
    }
}
