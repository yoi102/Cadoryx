using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    private GeometryResult EvaluateFiniteSurfaceNewBody(ExtrudeRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        recipe.Validate();var options=recipe.Options!;
        if(options.Operation!=ExtrusionOperation.NewBody||options.Termination!=ExtrusionTermination.FiniteSurface)
            throw new CadValidationException("Expected an independent finite surface extrusion.");
        // The existing native Until builder needs a base and a member support.
        // Use one private exact box strictly behind the authored starting plane.
        // Only its proved added material becomes the independent output. Native
        // failure or any finite-domain proof failure has no alternate path.
        using var unit=Create(new ExtrudeRecipe(recipe.Profile,1,recipe.Placement),assets,token);
        var bounds=SurfaceFrameBounds(unit,recipe.Placement);
        var placement=recipe.Placement*RigidTransform3d.Translate(bounds.Min.X-1,bounds.Min.Y-1,
            options.Side==ExtrusionSide.Positive?-1:0);
        using var seed=Create(new BoxRecipe(bounds.Max.X-bounds.Min.X+2,bounds.Max.Y-bounds.Min.Y+2,1,placement),assets,token);
        using var staged=OcctGeometryBridge.StoreShape(seed,assets);using var map=RepairSnapshot.Create(seed);
        var faces=seed.GetFaces();
        try
        {
            var support=faces.Single(f=>FiniteSurfaceIsProfilePlane(f,recipe.Placement,0));
            int index=RepairSnapshot.FindTopologyIndex(seed,support);
            var selection=new ExactTopologySelection(options.LimitFace!.DocumentId,FeatureId.New(),staged.Geometry.Revision,
                staged.Geometry.AssetId,map.Fingerprint,index,HistoryShapeKind.Face,HistoryAdapterVersion);
            var internalRecipe=recipe with{Options=options with{Operation=ExtrusionOperation.Fuse,Source=staged.Geometry,SupportFace=selection}};
            internalRecipe.Validate();token.ThrowIfCancellationRequested();
            return EvaluateFiniteSurfaceExtrusion(internalRecipe,seed,assets,token,extractNewBody:true);
        }
        finally{foreach(var face in faces)face.Dispose();}
    }

    private static Shape CopyFiniteSurfaceMaterial(Shape shape,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(shape.Kind==ShapeKind.Solid){RequireFaceFeatureSolid(shape,token);return shape.Clone();}
        var summary=shape.GetTopologySummary();
        if(shape.Kind!=ShapeKind.Compound||summary.UniqueCounts.SolidCount<1||
            summary.UniqueCounts.SolidCount!=summary.OccurrenceCounts.SolidCount||summary.UniqueCounts.CompSolidCount!=0||
            !shape.GetValidationReport(true,true).IsValid||!shape.GetSelfInterferenceReport().IsClear)
            throw new CadValidationException("Independent finite surface material must contain complete noninterfering exact solids.");
        var solids=shape.GetSubShapes(ShapeKind.Solid);
        try
        {
            foreach(var solid in solids){token.ThrowIfCancellationRequested();RequireFaceFeatureSolid(solid,token);}
            using var covered=ShapeFactory.CreateCompound(solids);var cover=covered.GetTopologySummary();
            if(summary.UniqueCounts with{CompoundCount=0}!=cover.UniqueCounts with{CompoundCount=0}||
                summary.OccurrenceCounts with{CompoundCount=0}!=cover.OccurrenceCounts with{CompoundCount=0})
                throw new CadValidationException("Independent finite surface material has repeated or loose topology outside its complete solids.");
            return shape.Clone();
        }
        finally{foreach(var solid in solids)solid.Dispose();}
    }
}
