using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>A selected closed region. Children alternate subtractive holes and additive islands.</summary>
public sealed record SketchRegionReference(SketchEntityId? CircleId=null,
    ImmutableArray<SketchEntityId> PolygonLineIds=default,
    ImmutableArray<SketchEntityId> MixedCurveIds=default,
    SketchEntityId? SplineId=null,
    ImmutableArray<SketchRegionReference> Children=default)
{
    public ImmutableArray<SketchEntityId> PolygonLineIds {get;init;}=PolygonLineIds.IsDefault?[]:PolygonLineIds;
    public ImmutableArray<SketchEntityId> MixedCurveIds {get;init;}=MixedCurveIds.IsDefault?[]:MixedCurveIds;
    public ImmutableArray<SketchRegionReference> Children {get;init;}=Children.IsDefault?[]:Children;
}

/// <summary>Authored boundary identities plus the exact sketch revision used to produce a feature.</summary>
public sealed record SketchProfileReference(SketchId SketchId,Guid Revision,ImmutableArray<SketchEntityId> Lines,SketchEntityId? CircleId=null,
    ImmutableArray<SketchEntityId> HoleCircleIds=default,ImmutableArray<ImmutableArray<SketchEntityId>> PolygonHoleLines=default)
{
    public SketchEntityId? ArcId {get;init;}
    public SketchEntityId? BezierId {get;init;}
    public SketchEntityId? SplineId {get;init;}
    public ImmutableArray<SketchEntityId> MixedBoundaryIds {get;init;}=[];
    public ImmutableArray<ImmutableArray<SketchEntityId>> MixedHoleIds {get;init;}=[];
    public ImmutableArray<SketchEntityId> HoleSplineIds {get;init;}=[];
    public ImmutableArray<SketchRegionReference> Regions {get;init;}=[];
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
        if(Revision==Guid.Empty||Lines.IsDefault||MixedBoundaryIds.IsDefault||MixedHoleIds.IsDefault||HoleSplineIds.IsDefault||Regions.IsDefault||
           IslandCircleIds.IsDefault||IslandPolygonLines.IsDefault||IslandMixedIds.IsDefault||Lines.Distinct().Count()!=Lines.Length||
           (CircleId is null&&ArcId is null&&BezierId is null&&SplineId is null&&Lines.IsEmpty&&MixedBoundaryIds.IsEmpty)||
           (CircleId is not null&&(!Lines.IsEmpty||ArcId is not null||BezierId is not null||SplineId is not null||!MixedBoundaryIds.IsEmpty))||
           (ArcId is not null&&(!Lines.IsEmpty||BezierId is not null||SplineId is not null||!MixedBoundaryIds.IsEmpty))||
           ((ArcId is not null||BezierId is not null)&&!HoleSplineIds.IsEmpty)||
           (SplineId is not null&&(ArcId is not null||!Lines.IsEmpty||!MixedBoundaryIds.IsEmpty))||
           (BezierId is not null&&(!Lines.IsEmpty||SplineId is not null||!MixedBoundaryIds.IsEmpty||!HoleCircleIds.IsDefaultOrEmpty||
               !PolygonHoleLines.IsDefaultOrEmpty||!MixedHoleIds.IsDefaultOrEmpty||!IslandsEmpty))||
           (!MixedBoundaryIds.IsEmpty&&(!Lines.IsEmpty||MixedBoundaryIds.Length is <3 or >256||
               MixedBoundaryIds.Distinct().Count()!=MixedBoundaryIds.Length))||
           (!HoleCircleIds.IsDefaultOrEmpty&&(HoleCircleIds.Length>64||HoleCircleIds.Distinct().Count()!=HoleCircleIds.Length||HoleCircleIds.Contains(CircleId??default)))||
           PolygonHoleLines.Length+HoleCircleIds.Length+MixedHoleIds.Length+HoleSplineIds.Length>64||
           HoleSplineIds.Distinct().Count()!=HoleSplineIds.Length||HoleSplineIds.Contains(SplineId??default)||
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
        if(!Regions.IsEmpty&&(!HoleCircleIds.IsEmpty||!PolygonHoleLines.IsEmpty||!MixedHoleIds.IsEmpty||
            !HoleSplineIds.IsEmpty||!IslandsEmpty))
            throw new CadValidationException("Nested regions cannot be combined with legacy flat hole selections.");
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
        if(!HoleSplineIds.IsDefaultOrEmpty)
        {
            profile=profile with{SplineHoles=HoleSplineIds.Select(id=>
                SketchProfileBuilder.SplineSegment(sketch,id).Spline!).ToImmutableArray()};
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
        if(!Regions.IsEmpty)
        {
            var used=new HashSet<SketchEntityId>(Lines.Concat(MixedBoundaryIds));
            if(CircleId is {} outerCircle)used.Add(outerCircle);
            if(SplineId is {} outerSpline)used.Add(outerSpline);
            int count=0;
            SketchProfile Boundary(SketchRegionReference region,int depth)
            {
                if(depth>12||++count>64||region is null||region.PolygonLineIds.IsDefault||
                    region.MixedCurveIds.IsDefault||region.Children.IsDefault)
                    throw new CadValidationException("Nested sketch region limit or missing fields.");
                int kinds=(region.CircleId is null?0:1)+(region.SplineId is null?0:1)+
                    (region.PolygonLineIds.IsEmpty?0:1)+(region.MixedCurveIds.IsEmpty?0:1);
                if(kinds!=1)throw new CadValidationException("A nested region needs exactly one closed boundary.");
                IEnumerable<SketchEntityId> ids=region.CircleId is {} circle?[circle]:
                    region.SplineId is {} spline?[spline]:
                    region.PolygonLineIds.IsEmpty?region.MixedCurveIds:region.PolygonLineIds;
                foreach(var id in ids)if(!used.Add(id))
                    throw new CadValidationException("Nested regions reuse a boundary entity.");
                return region.CircleId is {} c?SketchProfileBuilder.Circle(sketch,c):
                    region.SplineId is {} s?SketchProfileBuilder.SplineSegment(sketch,s):
                    !region.PolygonLineIds.IsEmpty?SketchProfileBuilder.Polygon(sketch,region.PolygonLineIds):
                    SketchProfileBuilder.Mixed(sketch,region.MixedCurveIds);
            }
            static ImmutableArray<SketchBoundaryCurve> Curves(SketchProfile p)=>p.Circle is {} c?SketchMixedProfile.Circle(c):
                p.Spline is {} s?SketchSplineGeometry.ValidationBoundary(s):
                !p.BoundaryCurves.IsEmpty?p.BoundaryCurves:SketchMixedProfile.Polygon(p.Points);
            static SketchIslandRegion Island(SketchProfile p)=>new(p.Points,p.Circle,p.BoundaryCurves)
            {Spline=p.Spline,Holes=p.Holes,PolygonHoles=p.PolygonHoles,MixedHoles=p.MixedHoles,
             SplineHoles=p.SplineHoles,Islands=p.Islands};
            SketchProfile Populate(SketchProfile parent,ImmutableArray<SketchRegionReference> holes,int depth)
            {
                foreach(var holeRef in holes)
                {
                    var hole=Boundary(holeRef,depth);
                    if(hole.Circle is {} circle)parent=parent with{Holes=parent.Holes.Add(circle)};
                    else if(hole.Spline is {} spline)parent=parent with{SplineHoles=parent.SplineHoles.Add(spline)};
                    else if(!hole.BoundaryCurves.IsEmpty)parent=parent with{MixedHoles=parent.MixedHoles.Add(hole.BoundaryCurves)};
                    else parent=parent with{PolygonHoles=parent.PolygonHoles.Add(hole.Points)};
                    foreach(var islandRef in holeRef.Children)
                    {
                        var island=Boundary(islandRef,depth+1);
                        SketchMixedProfile.ValidateIslands([Curves(hole)],[Curves(island)]);
                        island=Populate(island,islandRef.Children,depth+2);
                        parent=parent with{Islands=parent.Islands.Add(Island(island))};
                    }
                }
                parent.Validate();return parent;
            }
            profile=Populate(profile,Regions,1);
        }
        return recipe switch
        {
            ExtrudeRecipe e=>e with{Profile=profile,Placement=sketch.Plane},
            RevolveRecipe r when CircleId is null&&ArcId is null&&BezierId is null&&SplineId is null&&MixedBoundaryIds.IsEmpty&&HoleCircleIds.IsDefaultOrEmpty&&MixedHoleIds.IsDefaultOrEmpty&&HoleSplineIds.IsEmpty&&Regions.IsEmpty&&IslandsEmpty=>r with{Profile=profile,Placement=sketch.Plane},
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
                a.Profile.Points.SequenceEqual(b.Profile.Points)&&SketchMixedProfile.SameLoop(a.Profile.BoundaryCurves,b.Profile.BoundaryCurves)&&
                a.Profile.Holes.AsSpan().SequenceEqual(b.Profile.Holes.AsSpan())&&a.Profile.PolygonHoles.Length==b.Profile.PolygonHoles.Length&&
                a.Profile.PolygonHoles.Zip(b.Profile.PolygonHoles).All(pair=>pair.First.SequenceEqual(pair.Second))&&
                a.Profile.MixedHoles.Length==b.Profile.MixedHoles.Length&&
                a.Profile.MixedHoles.Zip(b.Profile.MixedHoles).All(pair=>SketchMixedProfile.SameLoop(pair.First,pair.Second))&&
                a.Profile.SplineHoles.Length==b.Profile.SplineHoles.Length&&
                a.Profile.SplineHoles.Zip(b.Profile.SplineHoles).All(pair=>pair.First.Controls.SequenceEqual(pair.Second.Controls))&&
                a.Profile.Islands.Length==b.Profile.Islands.Length&&
                a.Profile.Islands.Zip(b.Profile.Islands).All(pair=>SameIsland(pair.First,pair.Second)),
            (RevolveRecipe a,RevolveRecipe b)=>a.Placement==b.Placement&&a.Profile.Points.SequenceEqual(b.Profile.Points),
            _=>false
        };
        if(!matches)throw new CadValidationException("Cached feature profile differs from its sketch.");
    }
    private static bool SameIsland(SketchIslandRegion a,SketchIslandRegion b)=>
        a.Circle==b.Circle&&a.Points.SequenceEqual(b.Points)&&
        SketchMixedProfile.SameLoop(a.BoundaryCurves,b.BoundaryCurves)&&
        (a.Spline is null&&b.Spline is null||a.Spline is not null&&b.Spline is not null&&
            a.Spline.Controls.SequenceEqual(b.Spline.Controls))&&
        a.Holes.SequenceEqual(b.Holes)&&a.PolygonHoles.Length==b.PolygonHoles.Length&&
        a.PolygonHoles.Zip(b.PolygonHoles).All(p=>p.First.SequenceEqual(p.Second))&&
        a.MixedHoles.Length==b.MixedHoles.Length&&
        a.MixedHoles.Zip(b.MixedHoles).All(p=>SketchMixedProfile.SameLoop(p.First,p.Second))&&
        a.SplineHoles.Length==b.SplineHoles.Length&&
        a.SplineHoles.Zip(b.SplineHoles).All(p=>p.First.Controls.SequenceEqual(p.Second.Controls))&&
        a.Islands.Length==b.Islands.Length&&a.Islands.Zip(b.Islands).All(p=>SameIsland(p.First,p.Second));
}
