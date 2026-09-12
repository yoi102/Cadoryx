namespace Cadoryx.Db;

public enum LocalFeatureOperation { Fillet=0, Chamfer=1 }
/// <summary>One semantic box edge. The source recipe is a validated cache of the upstream feature.</summary>
public sealed record LocalFeatureRecipe(GeometryAssetRef Source,BoxRecipe Box,BoxBoundary First,BoxBoundary Second,
    LocalFeatureOperation Operation,double Size) : GeometryRecipe
{
    public override IEnumerable<GeometryAssetRef> AssetInputs=>[Source];
    public override void Validate()
    {
        Source.Validate();Box.Validate();CadGuard.Positive(Size);
        if(!Enum.IsDefined(Operation)||!Enum.IsDefined(First)||!Enum.IsDefined(Second)||(int)First/2>=(int)Second/2)
            throw new CadValidationException("Invalid local edge operation.");
    }
}
