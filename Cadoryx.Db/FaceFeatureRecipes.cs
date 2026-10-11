using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum FaceDraftPropagation { SelectedFaceOnly=0, TangentChain=1 }
public sealed record ShellRecipe(GeometryAssetRef Source,ImmutableArray<ExactTopologySelection> ClosingFaces,
    double Thickness,bool Outward=false):GeometryRecipe
{
    public override IEnumerable<GeometryAssetRef> AssetInputs=>[Source];
    public override void Validate()
    {
        FaceFeatureRecipes.ValidateSelections(Source,ClosingFaces);CadGuard.Positive(Thickness);
        if(Thickness<=1e-6||Thickness>100000)throw new CadValidationException("Invalid shell wall thickness.");
    }
}
public sealed record DraftFaceProgram(Guid Id,ExactTopologySelection Face,double Angle,RigidTransform3d NeutralPlane,
    Vector3d PullDirection,FaceDraftPropagation Propagation=FaceDraftPropagation.SelectedFaceOnly,Guid? Datum=null,Guid? DatumRevision=null)
{
    public string AngleProperty=>"Angle:"+Id.ToString("N");
    public void Validate()
    {
        if(Id==Guid.Empty||!Enum.IsDefined(Propagation)||(Datum is null)!=(DatumRevision is null)||Datum==Guid.Empty||DatumRevision==Guid.Empty)
            throw new CadValidationException("Invalid draft face program identity or propagation.");
        if(Face is null)throw new CadValidationException("A draft program needs an explicit face.");
        Face.Validate();NeutralPlane.Validate();CadGuard.Finite(Angle,PullDirection.X,PullDirection.Y,PullDirection.Z);
        if(Math.Abs(Angle)<=1e-4||Math.Abs(Angle)>=Math.PI/2||PullDirection.Length<1e-8||PullDirection.Length>1000000)
            throw new CadValidationException("Draft angle must exceed the kernel no-op threshold and remain below 90 degrees; choose a nonzero pull direction.");
    }
}
public sealed record DraftRecipe(GeometryAssetRef Source,ImmutableArray<DraftFaceProgram> Programs):GeometryRecipe
{
    public override IEnumerable<GeometryAssetRef> AssetInputs=>[Source];
    public override void Validate()
    {
        if(Programs.IsDefaultOrEmpty||Programs.Length>256||Programs.Any(p=>p is null)||Programs.Select(p=>p.Id).Distinct().Count()!=Programs.Length)
            throw new CadValidationException("A draft needs distinct bounded face programs.");
        foreach(var program in Programs)program.Validate();FaceFeatureRecipes.ValidateSelections(Source,Programs.Select(p=>p.Face));
    }
    public DraftRecipe Refresh(DocumentSnapshot doc,DefinitionId owner)=>this with{Programs=Programs.Select(p=>
    {
        if(p.Datum is not {} id)return p;
        if(!doc.Datums.Planes.TryGetValue(id,out var plane)||plane.PartId!=owner)
            throw new CadValidationException("Draft neutral datum is missing or belongs to another part.");
        if(plane.IsStale)throw new DatumSourceUnavailableException();
        return p with{NeutralPlane=plane.Frame,DatumRevision=plane.Revision};
    }).ToImmutableArray()};
}
public static class FaceFeatureRecipes
{
    public static IEnumerable<ExactTopologySelection> Faces(GeometryRecipe recipe)=>recipe switch
    {ShellRecipe r=>r.ClosingFaces,DraftRecipe r=>r.Programs.Select(p=>p.Face),PlanarPushPullRecipe r=>[r.Face],DefeatureRecipe r=>r.Faces,_=>[]};
    public static GeometryAssetRef Source(GeometryRecipe recipe)=>recipe switch
    {ShellRecipe r=>r.Source,DraftRecipe r=>r.Source,PlanarPushPullRecipe r=>r.Source,DefeatureRecipe r=>r.Source,_=>throw new CadValidationException("Not a face feature.")};
    internal static void ValidateSelections(GeometryAssetRef source,IEnumerable<ExactTopologySelection> selections)
    {
        source.Validate();var faces=selections.ToArray();
        if(source.Kind!=BodyKind.Solid||faces.Length is <1 or >256||faces.Any(f=>f is null)||faces.Select(f=>f.FullTopologyIndex).Distinct().Count()!=faces.Length)
            throw new CadValidationException("Select distinct faces of one solid source.");
        var first=faces[0];foreach(var face in faces)
        {
            face.Validate();
            if(face.Kind!=HistoryShapeKind.Face||face.DocumentId!=first.DocumentId||face.FeatureId!=first.FeatureId||face.Fingerprint!=first.Fingerprint||
                face.AdapterVersion!=first.AdapterVersion||face.Asset!=source.AssetId||face.Revision!=source.Revision)
                throw new CadValidationException("Selected faces do not share the exact source identity, revision and fingerprint.");
        }
    }
    public static bool HasCurrentSource(DocumentSnapshot doc,GeometryRecipe recipe)
    {
        var face=Faces(recipe).First();return face.DocumentId==doc.Id&&doc.Features.TryGetValue(face.FeatureId,out var target)&&
            !target.IsStale&&!target.IsSuppressed&&target.Result==Source(recipe);
    }
    public static void ValidateApplied(DocumentSnapshot doc,FeatureDefinition feature)
    {
        if(feature.Recipe is not (ShellRecipe or DraftRecipe or PlanarPushPullRecipe or DefeatureRecipe))return;var faces=Faces(feature.Recipe).ToArray();var first=faces[0];
        if(first.DocumentId!=doc.Id||feature.Inputs.Length!=1||feature.Inputs[0]!=first.FeatureId||
            !doc.Features.TryGetValue(first.FeatureId,out var target)||target.PartId!=feature.PartId||feature.SketchSource is not null||
            !feature.IsStale&&!HasCurrentSource(doc,feature.Recipe))throw new CadValidationException("Face feature source is foreign or stale.");
        if(feature.Recipe is DraftRecipe draft)
        {
            var refreshed=feature.IsStale?draft:draft.Refresh(doc,feature.PartId);
            if(!feature.IsStale&&draft.Programs.Zip(refreshed.Programs).Any(p=>p.First.DatumRevision!=p.Second.DatumRevision||!DatumPlanes.SameFrame(p.First.NeutralPlane,p.Second.NeutralPlane)))
                throw new CadValidationException("Draft neutral plane datum cache is stale.");
        }
    }
}
