using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackFaceEdit([property:Key(0)] PackExactTopologySelection[] Faces,[property:Key(1)] double? Distance=null,[property:Key(2)] double[]? Normal=null);

internal static partial class MessagePackSections
{
    private static PackRecipe PackFaceEditRecipe(PlanarPushPullRecipe value)
    {value.Validate();return new("planar-push-pull",[],null,[G(value.Source)],[],0,FaceEdit:new([PackExact(value.Face)],value.Distance,[value.OutwardNormal.X,value.OutwardNormal.Y,value.OutwardNormal.Z]));}
    private static PackRecipe PackFaceEditRecipe(DefeatureRecipe value)
    {value.Validate();return new("defeature",[],null,[G(value.Source)],[],0,FaceEdit:new(value.Faces.Select(PackExact).ToArray()));}
    private static GeometryRecipe ReadFaceEditRecipe(PackRecipe value)
    {
        if(value.Numbers is not {Length:0}||value.Placement is not null||value.Sources is not {Length:1}||value.Profile is not {Length:0}||value.Operation!=0||
            value.Holes is not null||value.PolygonHoles is not null||value.MixedCurves is not null||value.MixedHoles is not null||value.Islands is not null||
            value.SplineControls is not null||value.SplineHoles is not null||value.SplineKnots is not null||value.SplineHoleKnots is not null||
            value.Repetition is not null||value.Hole is not null||value.Extrusion is not null||value.Guided is not null||value.FaceFeature is not null||
            value.RevolveAxis is not null||value.HolePattern is not null||value.ExtrusionPattern is not null||value.ContourLocal is not null||
            value.FaceEdit?.Faces is not {Length:>=1 and <=256} faces||faces.Any(f=>f is null))throw new InvalidDataException("Unexpected face-edit companion.");
        var edit=value.FaceEdit;GeometryRecipe recipe;
        if(value.Kind=="planar-push-pull"&&faces.Length==1&&edit.Distance is {} distance&&edit.Normal is {Length:3} normal)
            recipe=new PlanarPushPullRecipe(G(value.Sources[0]),UnpackExact(faces[0]),distance,new(normal[0],normal[1],normal[2]));
        else if(value.Kind=="defeature"&&edit.Distance is null&&edit.Normal is null)
            recipe=new DefeatureRecipe(G(value.Sources[0]),faces.Select(UnpackExact).ToImmutableArray());
        else throw new InvalidDataException("Invalid face-edit family payload.");
        recipe.Validate();return recipe;
    }
    internal static ReadOnlyMemory<byte> UpgradeFaceEditFeatures(ReadOnlyMemory<byte> bytes)
    {
        static bool Contains(PackRecipe r)=>r.FaceEdit is not null||r.Kind is "planar-push-pull" or "defeature"||
            r.ExtrusionPattern is {} pattern&&Contains(pattern.Template)||r.Guided?.Sweep is {} sweep&&Contains(sweep.Section)||
            r.Guided?.Loft is {} loft&&loft.Channels.Any(c=>c.Profiles.Any(Contains));
        if(Read<PackFeaturesV3>(bytes).Features.Any(f=>f.Recipe.FaceEdit is not null||f.Recipe.Kind is "planar-push-pull" or "defeature"||
            f.Recipe.ExtrusionPattern is {} pattern&&Contains(pattern.Template)||f.Recipe.Guided?.Sweep is {} sweep&&Contains(sweep.Section)||
            f.Recipe.Guided?.Loft is {} loft&&loft.Channels.Any(c=>c.Profiles.Any(Contains))))throw new InvalidDataException("Face editing in legacy feature schema.");
        return bytes;
    }
}
