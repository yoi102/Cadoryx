using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Authored boundary identities plus the exact sketch revision used to produce a feature.</summary>
public sealed record SketchProfileReference(SketchId SketchId,Guid Revision,ImmutableArray<SketchEntityId> Lines,SketchEntityId? CircleId=null,
    ImmutableArray<SketchEntityId> HoleCircleIds=default,ImmutableArray<ImmutableArray<SketchEntityId>> PolygonHoleLines=default)
{
    public SketchEntityId? ArcId {get;init;}
    public SketchEntityId? BezierId {get;init;}
    public SketchEntityId? SplineId {get;init;}
    public ImmutableArray<SketchEntityId> MixedBoundaryIds {get;init;}=[];
    public ImmutableArray<ImmutableArray<SketchEntityId>> MixedHoleIds {get;init;}=[];
    public ImmutableArray<SketchEntityId> IslandCircleIds {get;init;}=[];
    public ImmutableArray<ImmutableArray<SketchEntityId>> IslandPolygonLines {get;init;}=[];
    public ImmutableArray<ImmutableArray<SketchEntityId>> IslandMixedIds {get;init;}=[];
    public ImmutableArray<SketchEntityId> HoleCircleIds {get;init;}=HoleCircleIds.IsDefault?[]:HoleCircleIds;
    public ImmutableArray<ImmutableArray<SketchEntityId>> PolygonHoleLines {get;init;}=PolygonHoleLines.IsDefault?[]:PolygonHoleLines;
    public static SketchProfileReference Create(CadSketch sketch,IEnumerable<SketchEntityId> lines)=>new(sketch.Id,sketch.Revision,lines.ToImmutableArray());
    public static SketchProfileReference CreateCircle(CadSketch sketch,SketchEntityId circleId)=>new(sketch.Id,sketch.Revision,[],circleId);
    public static SketchProfileReference CreateArcSegment(CadSketch sketch,SketchEntityId arcId)=>new(sketch.Id,sketch.Revision,[]){ArcId=arcId};
    public static SketchProfileReference CreateBezierSegment(CadSketch sketch,SketchEntityId bezierId)=>new(sketch.Id,sketch.Revision,[]){BezierId=bezierId};
    public static SketchProfileReference CreateSplineSegment(CadSketch sketch,SketchEntityId splineId)=>new(sketch.Id,sketch.Revision,[]){SplineId=splineId};
    public static SketchProfileReference CreateMixed(CadSketch sketch,IEnumerable<SketchEntityId> orderedCurves)=>
        new(sketch.Id,sketch.Revision,[]){MixedBoundaryIds=orderedCurves.ToImmutableArray()};
    public static SketchProfileReference CreateWithHoles(CadSketch sketch,IEnumerable<SketchEntityId> lines,IEnumerable<SketchEntityId> holes)=>
        new(sketch.Id,sketch.Revision,lines.ToImmutableArray(),null,holes.ToImmutableArray());
    public static SketchProfileReference CreateCircleWithHoles(CadSketch sketch,SketchEntityId circleId,IEnumerable<SketchEntityId> holes)=>
        new(sketch.Id,sketch.Revision,[],circleId,holes.ToImmutableArray());
    public static SketchProfileReference CreateWithPolygonHoles(CadSketch sketch,IEnumerable<SketchEntityId> outer,
        IEnumerable<IEnumerable<SketchEntityId>> holes)=>new(sketch.Id,sketch.Revision,outer.ToImmutableArray(),null,[],
            holes.Select(h=>h.ToImmutableArray()).ToImmutableArray());
    public GeometryRecipe Resolve(DocumentSnapshot document,DefinitionId part,GeometryRecipe recipe,bool refresh=false)
    {
        CadGuard.Id(SketchId);
        if(Revision==Guid.Empty||Lines.IsDefault||MixedBoundaryIds.IsDefault||MixedHoleIds.IsDefault||
           IslandCircleIds.IsDefault||IslandPolygonLines.IsDefault||IslandMixedIds.IsDefault||Lines.Distinct().Count()!=Lines.Length||
           (CircleId is null&&ArcId is null&&BezierId is null&&SplineId is null&&Lines.IsEmpty&&MixedBoundaryIds.IsEmpty)||
           (CircleId is not null&&(!Lines.IsEmpty||ArcId is not null||BezierId is not null||SplineId is not null||!MixedBoundaryIds.IsEmpty))||
           (ArcId is not null&&(!Lines.IsEmpty||BezierId is not null||SplineId is not null||!MixedBoundaryIds.IsEmpty))||
           (SplineId is not null&&(ArcId is not null||!Lines.IsEmpty||!MixedBoundaryIds.IsEmpty||!HoleCircleIds.IsDefaultOrEmpty||
               !PolygonHoleLines.IsDefaultOrEmpty||!MixedHoleIds.IsDefaultOrEmpty||!IslandsEmpty))||
           (BezierId is not null&&(!Lines.IsEmpty||SplineId is not null||!MixedBoundaryIds.IsEmpty||!HoleCircleIds.IsDefaultOrEmpty||
               !PolygonHoleLines.IsDefaultOrEmpty||!MixedHoleIds.IsDefaultOrEmpty||!IslandsEmpty))||
           (!MixedBoundaryIds.IsEmpty&&(!Lines.IsEmpty||MixedBoundaryIds.Length is <3 or >256||
               MixedBoundaryIds.Distinct().Count()!=MixedBoundaryIds.Length))||
           (!HoleCircleIds.IsDefaultOrEmpty&&(HoleCircleIds.Length>64||HoleCircleIds.Distinct().Count()!=HoleCircleIds.Length||HoleCircleIds.Contains(CircleId??default)))||
           PolygonHoleLines.Length+HoleCircleIds.Length+MixedHoleIds.Length>64||
           IslandCircleIds.Length+IslandPolygonLines.Length+IslandMixedIds.Length>64||
           IslandCircleIds.Distinct().Count()!=IslandCircleIds.Length||
           HoleCircleIds.Concat(IslandCircleIds).Append(CircleId??default).Where(id=>id.Value!=Guid.Empty).Distinct().Count()!=
               HoleCircleIds.Length+IslandCircleIds.Length+(CircleId is null?0:1)||
           PolygonHoleLines.Any(h=>h.IsDefault||h.Length<3||h.Distinct().Count()!=h.Length)||
           MixedHoleIds.Any(h=>h.IsDefault||h.Length is <3 or >256||h.Distinct().Count()!=h.Length)||
           IslandPolygonLines.Any(h=>h.IsDefault||h.Length<3||h.Distinct().Count()!=h.Length)||
           IslandMixedIds.Any(h=>h.IsDefault||h.Length is <3 or >256||h.Distinct().Count()!=h.Length)||
           Lines.Concat(MixedBoundaryIds).Concat(PolygonHoleLines.SelectMany(h=>h)).Concat(MixedHoleIds.SelectMany(h=>h))
               .Concat(IslandPolygonLines.SelectMany(h=>h)).Concat(IslandMixedIds.SelectMany(h=>h))
               .Distinct().Count()!=Lines.Length+MixedBoundaryIds.Length+PolygonHoleLines.Sum(h=>h.Length)+MixedHoleIds.Sum(h=>h.Length)+
                   IslandPolygonLines.Sum(h=>h.Length)+IslandMixedIds.Sum(h=>h.Length))
            throw new CadValidationException("Invalid sketch profile reference.");
        if(!document.Sketches.TryGetValue(SketchId,out var sketch)||sketch.PartId!=part)
            throw new CadValidationException("The profile sketch is missing or belongs to another part.");
        if(!refresh&&Revision!=sketch.Revision)throw new CadValidationException("The feature references an outdated sketch revision.");
        var profile=CircleId is {} circle?SketchProfileBuilder.Circle(sketch,circle):
            ArcId is {} arc?SketchProfileBuilder.ArcSegment(sketch,arc):
            BezierId is {} bezier?SketchProfileBuilder.BezierSegment(sketch,bezier):
            SplineId is {} spline?SketchProfileBuilder.SplineSegment(sketch,spline):
            !MixedBoundaryIds.IsEmpty?SketchProfileBuilder.Mixed(sketch,MixedBoundaryIds):SketchProfileBuilder.Polygon(sketch,Lines);
        if(!HoleCircleIds.IsDefaultOrEmpty)
        {
            var holes=HoleCircleIds.Select(id=>SketchProfileBuilder.Circle(sketch,id).Circle!).ToImmutableArray();
            profile=profile with{Holes=holes};profile.Validate();
        }
        if(!PolygonHoleLines.IsDefaultOrEmpty)
        {
            profile=profile with{PolygonHoles=PolygonHoleLines.Select(ids=>SketchProfileBuilder.Polygon(sketch,ids).Points).ToImmutableArray()};
            profile.Validate();
        }
        if(!MixedHoleIds.IsDefaultOrEmpty)
        {
            profile=profile with{MixedHoles=MixedHoleIds.Select(ids=>SketchProfileBuilder.Mixed(sketch,ids).BoundaryCurves).ToImmutableArray()};
            profile.Validate();
        }
        if(IslandCircleIds.Length+IslandPolygonLines.Length+IslandMixedIds.Length>0)
        {
            var islands=IslandCircleIds.Select(id=>
                {var circle=SketchProfileBuilder.Circle(sketch,id).Circle!;return new SketchIslandRegion([],circle);})
                .Concat(IslandPolygonLines.Select(ids=>new SketchIslandRegion(SketchProfileBuilder.Polygon(sketch,ids).Points)))
                .Concat(IslandMixedIds.Select(ids=>new SketchIslandRegion([],null,SketchProfileBuilder.Mixed(sketch,ids).BoundaryCurves)))
                .ToImmutableArray();
            profile=profile with{Islands=islands};profile.Validate();
        }
        return recipe switch
        {
            ExtrudeRecipe e=>e with{Profile=profile,Placement=sketch.Plane},
            RevolveRecipe r when CircleId is null&&ArcId is null&&BezierId is null&&SplineId is null&&MixedBoundaryIds.IsEmpty&&HoleCircleIds.IsDefaultOrEmpty&&MixedHoleIds.IsDefaultOrEmpty&&IslandsEmpty=>r with{Profile=profile,Placement=sketch.Plane},
            _=>throw new CadValidationException("Only extrusion and revolution can reference a sketch profile.")
        };
    }
    private bool IslandsEmpty=>IslandCircleIds.IsEmpty&&IslandPolygonLines.IsEmpty&&IslandMixedIds.IsEmpty;
    public void ValidateCache(DocumentSnapshot document,FeatureDefinition feature)
    {
        var resolved=Resolve(document,feature.PartId,feature.Recipe);
        bool matches=(feature.Recipe,resolved) switch
        {
            (ExtrudeRecipe a,ExtrudeRecipe b)=>a.Placement==b.Placement&&a.Profile.Circle==b.Profile.Circle&&a.Profile.Arc==b.Profile.Arc&&a.Profile.Bezier==b.Profile.Bezier&&
                ((a.Profile.Spline is null&&b.Profile.Spline is null)||
                 (a.Profile.Spline is not null&&b.Profile.Spline is not null&&a.Profile.Spline.Controls.SequenceEqual(b.Profile.Spline.Controls)))&&
                a.Profile.Points.SequenceEqual(b.Profile.Points)&&a.Profile.BoundaryCurves.SequenceEqual(b.Profile.BoundaryCurves)&&
                a.Profile.Holes.AsSpan().SequenceEqual(b.Profile.Holes.AsSpan())&&a.Profile.PolygonHoles.Length==b.Profile.PolygonHoles.Length&&
                a.Profile.PolygonHoles.Zip(b.Profile.PolygonHoles).All(pair=>pair.First.SequenceEqual(pair.Second))&&
                a.Profile.MixedHoles.Length==b.Profile.MixedHoles.Length&&
                a.Profile.MixedHoles.Zip(b.Profile.MixedHoles).All(pair=>pair.First.SequenceEqual(pair.Second))&&
                a.Profile.Islands.Length==b.Profile.Islands.Length&&
                a.Profile.Islands.Zip(b.Profile.Islands).All(pair=>pair.First.Circle==pair.Second.Circle&&
                    pair.First.Points.SequenceEqual(pair.Second.Points)&&
                    (pair.First.BoundaryCurves.IsDefaultOrEmpty?ImmutableArray<SketchBoundaryCurve>.Empty:pair.First.BoundaryCurves)
                        .SequenceEqual(pair.Second.BoundaryCurves.IsDefaultOrEmpty?ImmutableArray<SketchBoundaryCurve>.Empty:pair.Second.BoundaryCurves)),
            (RevolveRecipe a,RevolveRecipe b)=>a.Placement==b.Placement&&a.Profile.Points.SequenceEqual(b.Profile.Points),
            _=>false
        };
        if(!matches)throw new CadValidationException("Cached feature profile differs from its sketch.");
    }
}
