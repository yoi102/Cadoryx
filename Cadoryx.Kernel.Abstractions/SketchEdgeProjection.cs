using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

/// <summary>Copied exact author values from one explicitly selected native edge; no borrowed handles.</summary>
public sealed record ExactSketchEdgeProjection(DefinitionId Part,ExactTopologySelection Selection,
    GeometryAssetRef Source,RigidTransform3d TargetPlane,ExactSketchCurve Curve)
{
    public void Validate()
    {
        CadGuard.Id(Part);Selection.Validate();Source.Validate();TargetPlane.Validate();Curve.Validate();
        if(Selection.Kind!=HistoryShapeKind.Edge||Selection.Asset!=Source.AssetId||Selection.Revision!=Source.Revision)
            throw new CadValidationException("Edge projection identity differs from its exact asset.");
    }
}
public interface ISketchEdgeProjector
{
    Task<ExactSketchEdgeProjection> ProjectEdgeAsync(DocumentSnapshot document,DefinitionId part,ExactTopologySelection selection,
        RigidTransform3d targetPlane,IAssetStore assets,CancellationToken token=default);
    Task ConfirmEdgeProjectionAsync(ExactSketchEdgeProjection evidence,IAssetStore assets,CancellationToken token=default);
}
