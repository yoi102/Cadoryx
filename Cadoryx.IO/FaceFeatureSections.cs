using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackFaceFeature([property:Key(0)] PackExactTopologySelection[]? ClosingFaces=null,
    [property:Key(1)] double Thickness=0,[property:Key(2)] bool Outward=false,[property:Key(3)] PackDraftFaceProgram[]? Programs=null);
[MessagePackObject]
public sealed record PackDraftFaceProgram([property:Key(0)] Guid Id,[property:Key(1)] PackExactTopologySelection Face,
    [property:Key(2)] double Angle,[property:Key(3)] PackTransform Plane,[property:Key(4)] double[] Pull,
    [property:Key(5)] int Propagation,[property:Key(6)] Guid? Datum,[property:Key(7)] Guid? DatumRevision);

internal static partial class MessagePackSections
{
    private static PackRecipe PackFaceFeatureRecipe(ShellRecipe recipe)
    {recipe.Validate();return new("shell",[],null,[G(recipe.Source)],[],0,FaceFeature:new(recipe.ClosingFaces.Select(PackExact).ToArray(),recipe.Thickness,recipe.Outward));}
    private static PackRecipe PackFaceFeatureRecipe(DraftRecipe recipe)
    {
        recipe.Validate();return new("draft",[],null,[G(recipe.Source)],[],0,FaceFeature:new(Programs:recipe.Programs.Select(p=>new PackDraftFaceProgram(p.Id,PackExact(p.Face),p.Angle,T(p.NeutralPlane),
            [p.PullDirection.X,p.PullDirection.Y,p.PullDirection.Z],(int)p.Propagation,p.Datum,p.DatumRevision)).ToArray()));
    }
    private static GeometryRecipe ReadFaceFeatureRecipe(PackRecipe payload)
    {
        if(payload.Numbers is not {Length:0}||payload.Placement is not null||payload.Sources is not {Length:1}||payload.Profile is not {Length:0}||payload.Operation!=0||
            payload.Holes is not null||payload.PolygonHoles is not null||payload.MixedCurves is not null||payload.MixedHoles is not null||payload.Islands is not null||
            payload.SplineControls is not null||payload.SplineHoles is not null||payload.Repetition is not null||payload.Hole is not null||payload.Extrusion is not null||payload.Guided is not null)
            throw new InvalidDataException("Unexpected shell/draft companion payload.");
        var value=payload.FaceFeature!;GeometryRecipe recipe;
        if(payload.Kind=="shell"&&value.Programs is null&&value.ClosingFaces is {Length:>=1 and <=256} faces&&faces.All(f=>f is not null))
            recipe=new ShellRecipe(G(payload.Sources[0]),faces.Select(UnpackExact).ToImmutableArray(),value.Thickness,value.Outward);
        else if(payload.Kind=="draft"&&value.ClosingFaces is null&&value.Thickness==0&&!value.Outward&&value.Programs is {Length:>=1 and <=256} programs&&
            programs.All(p=>p is not null&&p.Face is not null&&p.Plane is not null&&p.Pull is {Length:3}))
            recipe=new DraftRecipe(G(payload.Sources[0]),programs.Select(p=>new DraftFaceProgram(p.Id,UnpackExact(p.Face),p.Angle,T(p.Plane),new(p.Pull[0],p.Pull[1],p.Pull[2]),
                (FaceDraftPropagation)p.Propagation,p.Datum,p.DatumRevision)).ToImmutableArray());
        else throw new InvalidDataException("Invalid shell/draft body or kind.");
        recipe.Validate();return recipe;
    }
    internal static ReadOnlyMemory<byte> UpgradeFaceFeatures(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackFeaturesV3>(bytes).Features.Any(f=>f.Recipe.FaceFeature is not null||f.Recipe.Kind is "shell" or "draft"))
            throw new InvalidDataException("Shell/draft payload in legacy feature schema.");
        return bytes;
    }
}
