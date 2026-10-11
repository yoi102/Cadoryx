using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Commands;

/// <summary>Expands selected, already validated boundaries using explicit corner correspondence only.</summary>
internal static class SketchCornerProfileUpdate
{
    internal static IReadOnlyDictionary<FeatureId,SketchProfileReference> Build(DocumentSnapshot baseline,CadSketch candidate,
        IReadOnlyList<SketchCornerReplacement> edits,IReadOnlyList<FeatureId> selected)
    {
        var result=new Dictionary<FeatureId,SketchProfileReference>();
        if(selected.Count==0)return result;
        if(selected.Distinct().Count()!=selected.Count||edits.Count==0||edits.Select(e=>e.Joint).Distinct().Count()!=edits.Count)
            throw SketchCornerFailure.Create("CornerCorrespondenceInvalid");
        if(!baseline.Sketches.TryGetValue(candidate.Id,out var old)||old.Revision!=candidate.Revision||old.PartId!=candidate.PartId)
            throw SketchCornerFailure.Create("CornerCorrespondenceInvalid");
        var affectedLines=edits.SelectMany(e=>new[]{e.LineA,e.LineB}).ToHashSet();
        static IEnumerable<SketchEntityId> RegionLines(SketchRegionReference region)=>region.PolygonLineIds.Concat(region.MixedCurveIds).Concat(region.Children.SelectMany(RegionLines));
        foreach(var feature in baseline.Features.Values.Where(f=>f.SketchSource?.SketchId==old.Id))
        {
            var source=feature.SketchSource!;
            var targets=source.Lines.Concat(source.MixedBoundaryIds).Concat(source.PolygonHoleLines.SelectMany(h=>h)).Concat(source.MixedHoleIds.SelectMany(h=>h))
                .Concat(source.IslandPolygonLines.SelectMany(h=>h)).Concat(source.IslandMixedIds.SelectMany(h=>h)).Concat(source.Regions.SelectMany(RegionLines));
            if(targets.Any(affectedLines.Contains)&&!selected.Contains(feature.Id))throw SketchCornerFailure.Create("CornerProfileSelection",feature.Name);
        }
        var oldIds=old.Points.Select(p=>p.Id).Concat(old.Lines.Select(l=>l.Id)).Concat(old.Arcs.Select(a=>a.Id))
            .Concat(old.Circles.Select(c=>c.Id)).Concat(old.Beziers.Select(c=>c.Id)).Concat(old.Splines.Select(c=>c.Id)).ToHashSet();
        var fresh=new HashSet<SketchEntityId>();
        foreach(var edit in edits)
        {
            var a=old.Lines.SingleOrDefault(l=>l.Id==edit.LineA);var b=old.Lines.SingleOrDefault(l=>l.Id==edit.LineB);
            if(a is null||b is null||a.Id==b.Id||a.IsConstruction||b.IsConstruction||
                !new[]{a.Start,a.End}.Intersect(new[]{b.Start,b.End}).SequenceEqual(new[]{edit.Joint})||
                candidate.Points.Any(p=>p.Id==edit.Joint)||old.Projections.Any(p=>p.TargetEntity==a.Id||p.TargetEntity==b.Id))
                throw SketchCornerFailure.Create("CornerCorrespondenceInvalid");
            foreach(var id in new[]{edit.Connection,edit.PointA,edit.PointB})
                if(oldIds.Contains(id)||!fresh.Add(id))throw SketchCornerFailure.Create("CornerCorrespondenceInvalid");
            var line=candidate.Lines.SingleOrDefault(l=>l.Id==edit.Connection);var arc=candidate.Arcs.SingleOrDefault(l=>l.Id==edit.Connection);
            if((line is null)==(arc is null)||line is not null&&(line.Start!=edit.PointA||line.End!=edit.PointB||line.IsConstruction)||
                arc is not null&&(arc.Start!=edit.PointA||arc.End!=edit.PointB||arc.IsConstruction))
                throw SketchCornerFailure.Create("CornerCorrespondenceInvalid");
        }
        foreach(var id in edits.SelectMany(e=>new[]{e.LineA,e.LineB}).Distinct())
        {
            var original=old.Lines.Single(l=>l.Id==id);var expected=original;
            foreach(var e in edits.Where(e=>e.LineA==id||e.LineB==id))
            {
                var point=e.LineA==id?e.PointA:e.PointB;
                if(expected.Start==e.Joint)expected=expected with{Start=point};
                else if(expected.End==e.Joint)expected=expected with{End=point};
                else throw SketchCornerFailure.Create("CornerCorrespondenceInvalid");
            }
            if(candidate.Lines.SingleOrDefault(l=>l.Id==id)!=expected)
                throw SketchCornerFailure.Create("CornerCorrespondenceInvalid");
        }
        foreach(var id in selected)
        {
            if(!baseline.Features.TryGetValue(id,out var feature)||feature.SketchSource is not {} source||source.SketchId!=old.Id||
                feature.Recipe is not (ExtrudeRecipe or RevolveRecipe))throw SketchCornerFailure.Create("CornerProfileSelection");
            source.Resolve(baseline,feature.PartId,feature.Recipe);
            if(source.CircleId is not null||source.ArcId is not null||source.BezierId is not null||source.SplineId is not null||
                !source.HoleCircleIds.IsEmpty||!source.PolygonHoleLines.IsEmpty||!source.MixedHoleIds.IsEmpty||!source.HoleSplineIds.IsEmpty||
                !source.Regions.IsEmpty||!source.IslandCircleIds.IsEmpty||!source.IslandPolygonLines.IsEmpty||!source.IslandMixedIds.IsEmpty)
                throw SketchCornerFailure.Create("CornerProfileSingleLoop");
            var boundary=source.Lines.IsEmpty?source.MixedBoundaryIds:source.Lines;var expanded=new List<SketchEntityId>();var used=new HashSet<SketchEntityId>();
            foreach(var e in edits)
                if(boundary.Contains(e.LineA)!=boundary.Contains(e.LineB))throw SketchCornerFailure.Create("CornerProfileAdjacency");
            for(int i=0;i<boundary.Length;i++)
            {
                var a=boundary[i];var b=boundary[(i+1)%boundary.Length];expanded.Add(a);
                var matches=edits.Where(e=>(e.LineA==a&&e.LineB==b)||(e.LineA==b&&e.LineB==a)).ToArray();
                if(matches.Length>1)throw SketchCornerFailure.Create("CornerProfileAdjacency");
                if(matches.Length==1){var e=matches[0];if(!used.Add(e.Joint))throw SketchCornerFailure.Create("CornerProfileAdjacency");expanded.Add(e.Connection);}
            }
            if(edits.Any(e=>boundary.Contains(e.LineA)&&!used.Contains(e.Joint)))throw SketchCornerFailure.Create("CornerProfileAdjacency");
            if(used.Count==0)throw SketchCornerFailure.Create("CornerProfileSelection");
            bool curved=expanded.Any(id=>candidate.Arcs.Any(a=>a.Id==id)||candidate.Beziers.Any(a=>a.Id==id)||candidate.Splines.Any(a=>a.Id==id));
            result.Add(id,source with{Lines=curved?[]:expanded.ToImmutableArray(),MixedBoundaryIds=curved?expanded.ToImmutableArray():[]});
        }
        return result;
    }
}
