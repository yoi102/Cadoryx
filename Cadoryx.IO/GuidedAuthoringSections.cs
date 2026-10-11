using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.IO;

internal static partial class MessagePackSections
{
    private static PackSketchProfileReference GuidedSource(SketchProfileReference s)=>new(s.SketchId.Value,s.Revision,s.Lines.Select(id=>id.Value).ToArray(),
        s.CircleId?.Value,s.HoleCircleIds.Select(id=>id.Value).ToArray(),s.PolygonHoleLines.Select(h=>h.Select(id=>id.Value).ToArray()).ToArray(),
        s.ArcId?.Value,s.MixedBoundaryIds.Select(id=>id.Value).ToArray(),s.MixedHoleIds.Select(h=>h.Select(id=>id.Value).ToArray()).ToArray(),
        s.IslandCircleIds.Select(id=>id.Value).ToArray(),s.IslandPolygonLines.Select(h=>h.Select(id=>id.Value).ToArray()).ToArray(),
        s.IslandMixedIds.Select(h=>h.Select(id=>id.Value).ToArray()).ToArray(),s.BezierId?.Value,s.SplineId?.Value,
        s.HoleSplineIds.Select(id=>id.Value).ToArray(),s.Regions.Select(PackRegion).ToArray());
    private static SketchProfileReference GuidedSource(PackSketchProfileReference s)
    {
        if(s.Lines is null||s.PolygonHoles?.Any(h=>h is null)==true||s.MixedHoles?.Any(h=>h is null)==true||
            s.IslandPolygons?.Any(h=>h is null)==true||s.IslandMixed?.Any(h=>h is null)==true)
            throw new InvalidDataException("Malformed guided profile selection.");
        ImmutableArray<SketchEntityId> Ids(Guid[]? values)=>(values??[]).Select(id=>new SketchEntityId(id)).ToImmutableArray();
        ImmutableArray<ImmutableArray<SketchEntityId>> Groups(Guid[][]? values)=>(values??[]).Select(Ids).ToImmutableArray();
        return new(new(s.Sketch),s.Revision,Ids(s.Lines),s.Circle is {} c?new SketchEntityId(c):null,Ids(s.Holes),Groups(s.PolygonHoles))
        {ArcId=s.Arc is {} a?new SketchEntityId(a):null,BezierId=s.Bezier is {} b?new SketchEntityId(b):null,
            SplineId=s.Spline is {} spline?new SketchEntityId(spline):null,MixedBoundaryIds=Ids(s.MixedBoundary),MixedHoleIds=Groups(s.MixedHoles),
            IslandCircleIds=Ids(s.IslandCircles),IslandPolygonLines=Groups(s.IslandPolygons),IslandMixedIds=Groups(s.IslandMixed),
            HoleSplineIds=Ids(s.HoleSplines),Regions=(s.Regions??[]).Select(region=>UnpackRegion(region)).ToImmutableArray()};
    }
    private static PackRecipe GuidedProfile(SketchProfile profile)=>R(new ExtrudeRecipe(profile,1,RigidTransform3d.Identity));
    private static SketchProfile GuidedProfile(PackRecipe payload)
    {
        if(payload is null||!payload.Kind.StartsWith("extrude",StringComparison.Ordinal)||payload.Guided is not null||
            payload.Extrusion is not null||payload.Repetition is not null||payload.Hole is not null||payload.Sources.Length!=0)
            throw new InvalidDataException("A guided section payload must be a plain profile.");
        if(R(payload) is not ExtrudeRecipe {Distance:1,Placement:var frame} recipe||frame!=RigidTransform3d.Identity)
            throw new InvalidDataException("Unexpected section wrapper parameters.");
        recipe.Profile.Validate();return recipe.Profile;
    }
    private static PackRecipe PackGuided(SweepRecipe recipe)
    {
        recipe.Validate();return new("sweep",[],null,[],[],0,Guided:new(new(GuidedProfile(recipe.Section.Profile),T(recipe.Section.Placement),
            recipe.Path.Curves.Select(PackCurve).ToArray(),T(recipe.Path.Placement),(int)recipe.Output,(int)recipe.Corner,
            recipe.Section.Source is {} source?GuidedSource(source):null,
            recipe.Path.Source is {} path?new(path.Sketch.Value,path.Revision,path.Edges.Select(e=>e.Entity.Value).ToArray(),path.Edges.Select(e=>e.Reverse).ToArray()):null)));
    }
    private static PackRecipe PackGuided(LoftRecipe recipe)
    {
        recipe.Validate();return new("loft",[],null,[],[],0,Guided:new(Loft:new(recipe.Stations.Select(T).ToArray(),
            recipe.Channels.Select(c=>new PackLoftChannel(c.Id,c.Parent,c.Profiles.Select(GuidedProfile).ToArray(),
                c.Sources.IsEmpty?null:c.Sources.Select(s=>s is null?null:GuidedSource(s)).ToArray())).ToArray(),(int)recipe.Output,recipe.Ruled)));
    }
    private static GeometryRecipe ReadGuided(PackRecipe payload)
    {
        if(payload.Numbers.Length!=0||payload.Placement is not null||payload.Sources.Length!=0||payload.Profile.Length!=0||payload.Operation!=0||
            payload.Holes is not null||payload.PolygonHoles is not null||payload.MixedCurves is not null||payload.MixedHoles is not null||
            payload.Islands is not null||payload.SplineControls is not null||payload.SplineHoles is not null||payload.Repetition is not null||payload.Hole is not null||payload.Extrusion is not null)
            throw new InvalidDataException("Unexpected guided authoring companion payload.");
        var guided=payload.Guided!;GeometryRecipe recipe;
        if(payload.Kind=="sweep"&&guided.Sweep is {} s&&guided.Loft is null)
        {
            if(s.Path is not {Length:>=1 and <=256})throw new InvalidDataException("Invalid directed sweep path count.");
            SketchPathReference? pathSource=null;
            if(s.PathSource is {} p)
            {
                if(p.Entities is not {Length:>=1 and <=256}||p.Reverse is null||p.Reverse.Length!=p.Entities.Length)
                    throw new InvalidDataException("Invalid directed path source payload.");
                pathSource=new(new(p.Sketch),p.Revision,p.Entities.Select((id,i)=>new SketchPathEdge(new(id),p.Reverse[i])).ToImmutableArray());
            }
            recipe=new SweepRecipe(new(GuidedProfile(s.Section),T(s.Frame)){Source=s.SectionSource is {} source?GuidedSource(source):null},
                new(s.Path.Select(ReadCurve).ToImmutableArray(),T(s.PathFrame)){Source=pathSource},(GuidedOutput)s.Output,(SweepCorner)s.Corner);
        }
        else if(payload.Kind=="loft"&&guided.Loft is {} l&&guided.Sweep is null)
        {
            if(l.Stations is not {Length:>=2 and <=128}||l.Channels is not {Length:>=1 and <=64}||
                l.Channels.Any(c=>c is null||c.Profiles is null||c.Profiles.Length!=l.Stations.Length))
                throw new InvalidDataException("Invalid loft station/correspondence count.");
            recipe=new LoftRecipe(l.Stations.Select(T).ToImmutableArray(),l.Channels.Select(c=>new LoftChannel(c.Id,c.Parent,
                c.Profiles.Select(GuidedProfile).ToImmutableArray())
                {Sources=(c.Sources??[]).Select(s=>s is null?null:GuidedSource(s)).ToImmutableArray()}).ToImmutableArray(),(GuidedOutput)l.Output,l.Ruled);
        }
        else throw new InvalidDataException("Guided recipe kind and body disagree.");
        recipe.Validate();return recipe;
    }
    internal static ReadOnlyMemory<byte> UpgradeGuidedAuthoring(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackFeaturesV3>(bytes).Features.Any(f=>f.Recipe.Guided is not null||f.Recipe.Kind is "sweep" or "loft"))
            throw new InvalidDataException("Guided authoring in legacy schema.");
        return bytes;
    }
}
