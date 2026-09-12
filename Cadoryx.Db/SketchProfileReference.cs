using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Authored line identities plus the exact sketch revision used to produce a feature.</summary>
public sealed record SketchProfileReference(SketchId SketchId,Guid Revision,ImmutableArray<SketchEntityId> Lines)
{
    public static SketchProfileReference Create(CadSketch sketch,IEnumerable<SketchEntityId> lines)=>new(sketch.Id,sketch.Revision,lines.ToImmutableArray());
    public GeometryRecipe Resolve(DocumentSnapshot document,DefinitionId part,GeometryRecipe recipe,bool refresh=false)
    {
        CadGuard.Id(SketchId);
        if(Revision==Guid.Empty||Lines.IsDefaultOrEmpty||Lines.Distinct().Count()!=Lines.Length)
            throw new CadValidationException("Invalid sketch profile reference.");
        if(!document.Sketches.TryGetValue(SketchId,out var sketch)||sketch.PartId!=part)
            throw new CadValidationException("The profile sketch is missing or belongs to another part.");
        if(!refresh&&Revision!=sketch.Revision)throw new CadValidationException("The feature references an outdated sketch revision.");
        var profile=SketchProfileBuilder.Polygon(sketch,Lines);
        return recipe switch
        {
            ExtrudeRecipe e=>e with{Profile=profile,Placement=sketch.Plane},
            RevolveRecipe r=>r with{Profile=profile,Placement=sketch.Plane},
            _=>throw new CadValidationException("Only extrusion and revolution can reference a sketch profile.")
        };
    }
    public void ValidateCache(DocumentSnapshot document,FeatureDefinition feature)
    {
        var resolved=Resolve(document,feature.PartId,feature.Recipe);
        bool matches=(feature.Recipe,resolved) switch
        {
            (ExtrudeRecipe a,ExtrudeRecipe b)=>a.Placement==b.Placement&&a.Profile.Points.SequenceEqual(b.Profile.Points),
            (RevolveRecipe a,RevolveRecipe b)=>a.Placement==b.Placement&&a.Profile.Points.SequenceEqual(b.Profile.Points),
            _=>false
        };
        if(!matches)throw new CadValidationException("Cached feature profile differs from its sketch.");
    }
}
