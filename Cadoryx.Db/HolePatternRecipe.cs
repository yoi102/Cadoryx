using System.Text.Json.Serialization;

namespace Cadoryx.Db;

/// <summary>Replays the seed drilling operation on one target. It does not copy the drilled base solid.</summary>
public sealed record HolePatternRecipe(FeatureId Seed,HoleRecipe Template,RepetitionRecipe Layout):GeometryRecipe
{
    [JsonIgnore] public GeometryAssetRef Source=>Layout.Source;
    public override IEnumerable<GeometryAssetRef> AssetInputs=>[Source,Template.Source];
    public override void Validate()
    {
        CadGuard.Id(Seed);Template.Validate();Layout.Validate();
        if(Layout.StylePolicy!=RepetitionStylePolicy.VerifiedInheritance||Layout.Output!=RepetitionOutput.Fused||Source.Kind!=BodyKind.Solid||Template.Source.Kind!=BodyKind.Solid)
            throw new CadValidationException("Hole replay modifies one solid target; it cannot produce compound copies.");
    }
    public HolePatternRecipe Refresh(DocumentSnapshot document,DefinitionId part)
    {
        if(!document.Features.TryGetValue(Seed,out var seed)||seed.PartId!=part||seed.Recipe is not HoleRecipe hole)
            throw new CadValidationException("Hole replay requires its authored hole seed in the same part.");
        if(seed.IsStale||seed.IsSuppressed)throw new DatumSourceUnavailableException();
        return this with{Template=hole,Layout=(Layout with{Source=seed.Result}).Refresh(document,part)};
    }
    public void ValidateApplied(DocumentSnapshot document,FeatureDefinition feature)
    {
        Validate();
        if(feature.Inputs.Length!=1||feature.Inputs[0]!=Seed||feature.SketchSource is not null||
            !document.Features.TryGetValue(Seed,out var seed)||seed.PartId!=feature.PartId||seed.Recipe is not HoleRecipe)
            throw new CadValidationException("Hole replay seed identity or ownership differs from its dependency.");
        Layout.ValidateApplied(document,feature);
        if(!feature.IsStale&&Template!=seed.Recipe)
            throw new CadValidationException("Hole replay template differs from its exact current seed recipe.");
    }
    public HoleRecipe At(int index,GeometryAssetRef target)
    {
        Validate();if(index<0||index>=Layout.Count)throw new CadValidationException("Hole replay index exceeds its authored layout.");
        if(Layout.IncludeSource&&index==0)return Template with{Source=target,Datum=null,DatumRevision=null};
        var frame=Layout.Frame;RigidTransform3d placement;var position=Template.Position;
        if(Layout.Kind==RepetitionKind.Mirror)
        {
            var normal=frame.Rotation.Rotate(Vector3d.UnitZ);
            Vector3d Reflect(Vector3d v)=>v-normal*(2*v.Dot(normal));
            var point=frame.Translation+Reflect(Template.Frame.Translation-frame.Translation);
            // A reflected frame has negative determinant. Flip local U and its coordinate together
            // to retain the reflected directed drilling line in a proper authoring frame.
            placement=DatumConstructions.Frame(point,Reflect(Template.Frame.Rotation.Rotate(Vector3d.UnitZ)),
                Reflect(Template.Frame.Rotation.Rotate(new(1,0,0)))*-1);
            position=position with{X=-position.X};
        }
        else
        {
            RigidTransform3d transform;
            if(Layout.Kind==RepetitionKind.Linear)
                transform=new(frame.Rotation.Rotate(new(1,0,0))*(index*Layout.Step),Quaterniond.Identity);
            else
            {
                var rotation=Quaterniond.FromAxisAngle(frame.Rotation.Rotate(Vector3d.UnitZ),
                    index*(Layout.FullCircle?Math.Tau/Layout.Count:Layout.Step));
                transform=new(frame.Translation-rotation.Rotate(frame.Translation),rotation);
            }
            placement=transform*Template.Frame;
        }
        return Template with{Source=target,Frame=placement,Position=position,Datum=null,DatumRevision=null};
    }
}
