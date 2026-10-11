using System.Collections.Immutable;

namespace Cadoryx.Db;

public sealed record SketchPathEdge(SketchEntityId Entity,bool Reverse=false);
public sealed record SketchPathReference(SketchId Sketch,Guid Revision,ImmutableArray<SketchPathEdge> Edges)
{
    public SweepPath Resolve(DocumentSnapshot doc,DefinitionId part,bool refresh=false)
    {
        CadGuard.Id(Sketch);
        if(Revision==Guid.Empty||Edges.IsDefaultOrEmpty||Edges.Length>256||Edges.Any(e=>e is null)||
            Edges.Select(e=>e.Entity).Distinct().Count()!=Edges.Length||
            !doc.Sketches.TryGetValue(Sketch,out var sketch)||sketch.PartId!=part||!refresh&&sketch.Revision!=Revision)
            throw new CadValidationException("Sweep path identity, revision or directed edge selection is invalid.");
        sketch.Validate();var points=sketch.Points.ToDictionary(p=>p.Id);
        if(SketchNativeProjectionGeometry.HasStaleInputs(doc,Sketch))throw new CadValidationException("The sweep path has a stale native projection source.");
        Point2d P(SketchEntityId id)=>points[id].Position;
        var curves=Edges.Select(e=>
        {
            CadGuard.Id(e.Entity);SketchBoundaryCurve curve;
            if(sketch.Lines.FirstOrDefault(l=>l.Id==e.Entity) is {} line&&!line.IsConstruction)curve=new(P(line.Start),P(line.End));
            else if(sketch.Arcs.FirstOrDefault(a=>a.Id==e.Entity) is {} arc&&!arc.IsConstruction)curve=new(P(arc.Start),P(arc.End),P(arc.Middle));
            else if(sketch.Beziers.FirstOrDefault(b=>b.Id==e.Entity) is {} bezier&&!bezier.IsConstruction)
                curve=new(P(bezier.Start),P(bezier.End)){BezierControl=P(bezier.Control)};
            else if(sketch.Splines.FirstOrDefault(s=>s.Id==e.Entity) is {} spline&&!spline.IsConstruction)
                curve=new(P(spline.Controls[0]),P(spline.Controls[^1])){SplineControls=spline.Controls.Select(P).ToImmutableArray(),SplineKnots=spline.Knots};
            else if(sketch.Freeforms.FirstOrDefault(c=>c.Id==e.Entity) is {} freeform&&!freeform.IsConstruction)
                curve=new(freeform.Geometry.At(0),freeform.Geometry.At(1)){Freeform=freeform.Geometry};
            else if(sketch.Ellipses.FirstOrDefault(c=>c.Id==e.Entity) is {} ellipse&&!ellipse.IsConstruction)
                curve=new(ellipse.Geometry.At(0),ellipse.Geometry.At(1)){Ellipse=ellipse.Geometry};
            else if(sketch.ExpressionCurves.FirstOrDefault(c=>c.Id==e.Entity) is {} expression&&!expression.IsConstruction)
            {
                SketchAssociativeEndpoints.RequireSolved(sketch,curve:e.Entity);
                curve=new(expression.Geometry.At(0),expression.Geometry.At(1)){Expression=expression.Geometry};
            }
            else throw new CadValidationException("An explicitly selected path curve is missing, construction-only or unsupported.");
            return e.Reverse?curve.Reversed():curve;
        }).ToImmutableArray();
        var result=new SweepPath(curves,sketch.Plane){Source=this with{Revision=sketch.Revision}};result.Validate();return result;
    }
}

public static class GuidedAuthoringSources
{
    public static bool HasUnavailableEntities(DocumentSnapshot doc,GeometryRecipe recipe)
    {
        if(recipe is SweepRecipe sweep)
        {
            if(sweep.Section.Source?.HasUnavailableEntities(doc)==true)return true;
            if(sweep.Path.Source is {} source)
            {
                if(!doc.Sketches.TryGetValue(source.Sketch,out var sketch))
                    throw new CadValidationException("The sweep path sketch is missing.");
                if(SketchNativeProjectionGeometry.HasStaleInputs(doc,source.Sketch))return true;
                var curves=sketch.Lines.Where(c=>!c.IsConstruction).Select(c=>c.Id)
                    .Concat(sketch.Arcs.Where(c=>!c.IsConstruction).Select(c=>c.Id))
                    .Concat(sketch.Beziers.Where(c=>!c.IsConstruction).Select(c=>c.Id))
                    .Concat(sketch.Splines.Where(c=>!c.IsConstruction).Select(c=>c.Id)).Concat(sketch.Ellipses.Where(c=>!c.IsConstruction).Select(c=>c.Id)).Concat(sketch.Freeforms.Where(c=>!c.IsConstruction).Select(c=>c.Id))
                    .Concat(sketch.ExpressionCurves.Where(c=>!c.IsConstruction).Select(c=>c.Id)).ToHashSet();
                return source.Edges.Any(e=>!curves.Contains(e.Entity));
            }
        }
        return recipe is LoftRecipe loft&&loft.Channels.SelectMany(c=>c.Sources).OfType<SketchProfileReference>()
            .Any(s=>s.HasUnavailableEntities(doc));
    }
    public static IEnumerable<SketchId> Sketches(GeometryRecipe recipe)=>recipe switch
    {
        SweepRecipe r=>new[]{r.Section.Source?.SketchId,r.Path.Source?.Sketch}.OfType<SketchId>(),
        LoftRecipe r=>r.Channels.SelectMany(c=>c.Sources).OfType<SketchProfileReference>().Select(s=>s.SketchId),
        _=>[]
    };
    public static IEnumerable<SketchId> Sketches(FeatureDefinition feature)=>
        Sketches(feature.Recipe).Concat(feature.SketchSource is {} s?[s.SketchId]:[]).Distinct();
    private static AuthoringSection Resolve(DocumentSnapshot doc,DefinitionId part,AuthoringSection section)
    {
        if(section.Source is not {} source)return section;
        var resolved=(ExtrudeRecipe)source.Resolve(doc,part,new ExtrudeRecipe(section.Profile,1,section.Placement),refresh:true);
        return new(resolved.Profile,resolved.Placement){Source=source with{Revision=doc.Sketches[source.SketchId].Revision}};
    }
    public static GeometryRecipe Refresh(DocumentSnapshot doc,DefinitionId part,GeometryRecipe recipe)
    {
        GeometryRecipe result=recipe;
        if(recipe is SweepRecipe sweep)
            result=sweep with{Section=Resolve(doc,part,sweep.Section),Path=sweep.Path.Source?.Resolve(doc,part,refresh:true)??sweep.Path};
        else if(recipe is LoftRecipe loft)
        {
            var stations=loft.Stations.ToArray();var frames=new Dictionary<int,RigidTransform3d>();
            var channels=loft.Channels.Select(channel=>
            {
                if(channel.Sources.IsEmpty)return channel;
                var profiles=channel.Profiles.ToArray();var sources=channel.Sources.ToArray();
                for(int i=0;i<sources.Length;i++)if(sources[i] is {} source)
                {
                    var section=Resolve(doc,part,new(profiles[i],stations[i]){Source=source});
                    if(frames.TryGetValue(i,out var earlier)&&!DatumPlanes.SameFrame(earlier,section.Placement))
                        throw new CadValidationException("Linked boundaries at one loft station have different sketch planes.");
                    frames[i]=section.Placement;profiles[i]=section.Profile;sources[i]=section.Source;
                }
                return channel with{Profiles=profiles.ToImmutableArray(),Sources=sources.ToImmutableArray()};
            }).ToImmutableArray();
            foreach(var (i,frame) in frames)stations[i]=frame;
            result=loft with{Stations=stations.ToImmutableArray(),Channels=channels};
        }
        result.Validate();return result;
    }
    public static void ValidateApplied(DocumentSnapshot doc,FeatureDefinition feature)
    {
        if(feature.Recipe is not (SweepRecipe or LoftRecipe))return;
        if(!feature.Inputs.IsEmpty||feature.SketchSource is not null)
            throw new CadValidationException("Guided source references belong to the recipe; feature body inputs must be empty.");
        // Even stale features retain valid source identities. Cache checks are required only for current outputs.
        foreach(var id in Sketches(feature.Recipe))if(!doc.Sketches.TryGetValue(id,out var s)||s.PartId!=feature.PartId)
            throw new CadValidationException("A guided source sketch is missing or belongs to another part.");
        if(feature.IsStale)return;
        void Section(SketchProfile profile,RigidTransform3d placement,SketchProfileReference? source)
        {source?.ValidateCache(doc,feature with{Recipe=new ExtrudeRecipe(profile,1,placement),SketchSource=null});}
        if(feature.Recipe is SweepRecipe sweep)
        {
            Section(sweep.Section.Profile,sweep.Section.Placement,sweep.Section.Source);
            if(sweep.Path.Source is {} source)
            {
                var resolved=source.Resolve(doc,feature.PartId);
                if(!DatumPlanes.SameFrame(sweep.Path.Placement,resolved.Placement)||!SketchMixedProfile.SameLoop(sweep.Path.Curves,resolved.Curves))
                    throw new CadValidationException("Cached sweep path differs from its selected sketch entities.");
            }
        }
        if(feature.Recipe is LoftRecipe loft)foreach(var channel in loft.Channels)
            for(int i=0;i<channel.Sources.Length;i++)Section(channel.Profiles[i],loft.Stations[i],channel.Sources[i]);
    }
}
