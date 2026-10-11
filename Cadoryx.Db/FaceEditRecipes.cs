using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Extrudes the actual finite planar face domain along its proved outward normal.</summary>
public sealed record PlanarPushPullRecipe(GeometryAssetRef Source,ExactTopologySelection Face,double Distance,Vector3d OutwardNormal):GeometryRecipe
{
    public override IEnumerable<GeometryAssetRef> AssetInputs=>[Source];
    public override void Validate()
    {
        FaceFeatureRecipes.ValidateSelections(Source,[Face]);CadGuard.Finite(Distance,OutwardNormal.X,OutwardNormal.Y,OutwardNormal.Z);
        if(Math.Abs(Distance)<=1e-6||Math.Abs(Distance)>100000||Math.Abs(OutwardNormal.Length-1)>1e-9)
            throw new CadValidationException("Planar face push/pull requires a signed distance above 0.000001 mm and a unit native outward normal.");
    }
}
/// <summary>Heals explicitly selected source faces with native defeaturing; never simply opens a shell.</summary>
public sealed record DefeatureRecipe(GeometryAssetRef Source,ImmutableArray<ExactTopologySelection> Faces):GeometryRecipe
{
    public override IEnumerable<GeometryAssetRef> AssetInputs=>[Source];
    public override void Validate()
    {
        if(Faces.IsDefault)throw new CadValidationException("Initialize the face removal collection.");
        FaceFeatureRecipes.ValidateSelections(Source,Faces);
    }
}
