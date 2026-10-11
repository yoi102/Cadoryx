using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public sealed record DatumSourceSlot(int Index,string ShapeKind,ExactTopologySelection? Selection,
    ExactDatumGeometry? Geometry,string? Diagnostic);
public sealed record DatumSourcePage(int Total,IReadOnlyList<DatumSourceSlot> Items);

public interface IDatumGeometryResolver
{
    Task<ExactDatumGeometry> ResolveDatumGeometryAsync(DocumentSnapshot document,ExactTopologySelection selection,IAssetStore assets,CancellationToken token=default);
    Task ConfirmDatumGeometryAsync(ExactDatumGeometry evidence,IAssetStore assets,CancellationToken token=default);
    Task<DatumSourcePage> InspectDatumSourcesAsync(DocumentSnapshot document,FeatureId feature,GeometryRevisionId revision,
        IAssetStore assets,int offset,int limit,CancellationToken token=default)=>
        throw new NotSupportedException("Kernel has no original datum source inspection.");
}
