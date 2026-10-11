using Cadoryx.Db;

namespace Cadoryx.IO;

internal static partial class MessagePackSections
{
    private static PackRecipe PackAdvancedExtrusion(ExtrudeRecipe recipe)
    {
        recipe.Validate();var options=recipe.Options!;
        var packed=R(recipe with{Options=null,Distance=recipe.Distance==0?1:recipe.Distance});
        var numbers=packed.Numbers.ToArray();numbers[0]=recipe.Distance;
        return packed with{Numbers=numbers,Sources=options.AssetInputs.Select(G).ToArray(),Extrusion=new((int)options.Side,options.Backward,
            (int)options.Operation,(int)options.Termination,options.LimitPlane is {} plane?T(plane):null,options.Datum,options.DatumRevision,
            options.LimitFace is {} face?PackExact(face):null,options.SupportFace is {} support?PackExact(support):null,
            options.SurfaceBranch is {} branch?(int)branch:null)};
    }
    private static ExtrudeRecipe ReadAdvancedExtrusion(PackRecipe r)
    {
        var e=r.Extrusion!;
        if(!r.Kind.StartsWith("extrude",StringComparison.Ordinal)||r.Operation!=0||r.Numbers.Length==0||
            r.Sources.Length!=(e.Operation==(int)ExtrusionOperation.NewBody?0:1)+(e.Face is null?0:1))
            throw new InvalidDataException("Malformed advanced extrusion payload.");
        var numbers=r.Numbers.ToArray();numbers[0]=r.Numbers[0]==0?1:r.Numbers[0];
        var legacy=(ExtrudeRecipe)R(r with{Numbers=numbers,Sources=[],Extrusion=null});
        var options=new ExtrusionOptions((ExtrusionSide)e.Side,e.Backward,(ExtrusionOperation)e.Operation,
            e.Operation==(int)ExtrusionOperation.NewBody?null:G(r.Sources[0]),(ExtrusionTermination)e.Termination,
            e.Plane is {} plane?T(plane):null,e.Datum,e.DatumRevision,e.Face is {} face?UnpackExact(face):null,
            e.Face is null?null:G(r.Sources[^1]),e.Support is {} support?UnpackExact(support):null,
            e.SurfaceBranch is {} branch?(ExtrusionSurfaceBranch)branch:null);
        var recipe=legacy with{Distance=r.Numbers[0],Options=options};recipe.Validate();return recipe;
    }
    internal static ReadOnlyMemory<byte> UpgradeAdvancedExtrusion(ReadOnlyMemory<byte> bytes)
    {
        if(Read<PackFeaturesV3>(bytes).Features.Any(f=>f.Recipe.Extrusion is not null))
            throw new InvalidDataException("Advanced extrusion in legacy schema.");
        return bytes;
    }
    internal static ReadOnlyMemory<byte> UpgradeFiniteSurfaceExtrusion(ReadOnlyMemory<byte> bytes)
    {
        static bool Has(PackExtrusion? e)=>e is not null&&(e.Termination==(int)ExtrusionTermination.FiniteSurface||e.Support is not null||e.SurfaceBranch is not null);
        static bool Nested(PackRecipe r)=>Has(r.Extrusion)||r.ExtrusionPattern is {} p&&Nested(p.Template)||
            r.Guided?.Sweep is {} s&&Nested(s.Section)||r.Guided?.Loft is {} l&&l.Channels.Any(c=>c.Profiles.Any(Nested));
        if(Read<PackFeaturesV3>(bytes).Features.Any(f=>Has(f.Recipe.Extrusion)||f.Recipe.ExtrusionPattern is {} p&&Nested(p.Template)||
            f.Recipe.Guided?.Sweep is {} s&&Nested(s.Section)||f.Recipe.Guided?.Loft is {} l&&l.Channels.Any(c=>c.Profiles.Any(Nested))))
            throw new InvalidDataException("Finite surface extrusion cannot occur in legacy feature schema.");
        return bytes;
    }
}
