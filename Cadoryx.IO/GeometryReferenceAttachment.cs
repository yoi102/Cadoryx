using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.IO;

/// <summary>Reattaches separately stored metadata to all geometry owners, including retained recipe, datum and sketch sources.</summary>
internal static class GeometryReferenceAttachment
{
    internal static DocumentSnapshot Map(DocumentSnapshot snapshot,Func<GeometryAssetRef,GeometryAssetRef> map)
    {
        ExactDatumGeometry? Exact(ExactDatumGeometry? exact)=>exact is null?null:exact with{Source=map(exact.Source)};
        DatumPlaneSource Source(DatumPlaneSource source)=>source with{Exact=Exact(source.Exact)};
        return snapshot with
        {
            Bodies=snapshot.Bodies.ToImmutableDictionary(p=>p.Key,p=>p.Value with{Geometry=map(p.Value.Geometry)}),
            Features=snapshot.Features.ToImmutableDictionary(p=>p.Key,p=>p.Value with{Result=map(p.Value.Result),Recipe=Recipe(p.Value.Recipe,map)}),
            Datums=snapshot.Datums with
            {
                Planes=snapshot.Datums.Planes.ToImmutableDictionary(p=>p.Key,p=>p.Value with{Primary=Source(p.Value.Primary),Secondary=p.Value.Secondary is {} secondary?Source(secondary):null}),
                Axes=snapshot.Datums.Axes.ToImmutableDictionary(p=>p.Key,p=>p.Value with{Exact=Exact(p.Value.Exact)}),
                Points=snapshot.Datums.Points.ToImmutableDictionary(p=>p.Key,p=>p.Value with{Exact=Exact(p.Value.Exact)})
            },
            Sketches=snapshot.Sketches.ToImmutableDictionary(p=>p.Key,p=>p.Value with
                {NativeProjections=p.Value.NativeProjections.Select(projection=>projection with{Source=map(projection.Source)}).ToImmutableArray()})
        };
    }
    private static GeometryRecipe Recipe(GeometryRecipe recipe,Func<GeometryAssetRef,GeometryAssetRef> map)
    {
        if(!recipe.AssetInputs.Any())return recipe;
        return recipe switch
        {
            ImportedRecipe r=>r with{Source=map(r.Source)},TransformRecipe r=>r with{Source=map(r.Source)},
            BooleanRecipe r=>r with{Inputs=r.Inputs.Select(map).ToImmutableArray()},
            LocalFeatureRecipe r=>r with{Source=map(r.Source)},HistoryFilletRecipe r=>r with{Source=map(r.Source)},HistoryChamferRecipe r=>r with{Source=map(r.Source)},
            RepetitionRecipe r=>r with{Source=map(r.Source)},HoleRecipe r=>r with{Source=map(r.Source)},
            HolePatternRecipe r=>r with{Layout=r.Layout with{Source=map(r.Layout.Source)},Template=r.Template with{Source=map(r.Template.Source)}},
            ExtrusionPatternRecipe r=>r with{Layout=r.Layout with{Source=map(r.Layout.Source)},Template=(ExtrudeRecipe)Recipe(r.Template,map)},
            ExtrudeRecipe {Options:{} options} r=>r with{Options=options with
                {Source=options.Source is {} source?map(source):null,LimitGeometry=options.LimitGeometry is {} limit?map(limit):null}},
            ShellRecipe r=>r with{Source=map(r.Source)},DraftRecipe r=>r with{Source=map(r.Source)},SolidOffsetRecipe r=>r with{Source=map(r.Source)},
            ContourLocalRecipe r=>r with{Source=map(r.Source)},PlanarPushPullRecipe r=>r with{Source=map(r.Source)},DefeatureRecipe r=>r with{Source=map(r.Source)},
            _=>throw new InvalidDataException("Unsupported recipe geometry attachment; cannot discard a stored occurrence source.")
        };
    }
}
