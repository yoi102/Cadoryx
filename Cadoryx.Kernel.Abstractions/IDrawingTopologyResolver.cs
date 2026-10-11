using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public sealed record DrawingTopologySlot(int Index,string Kind,DrawingTopologyReference? Reference,string? Diagnostic);
public sealed record DrawingTopologyPage(int Total,IReadOnlyList<DrawingTopologySlot> Items);

public interface IDrawingTopologyResolver
{
    Task<DrawingTopologyPage> InspectDrawingTopologyAsync(DocumentSnapshot document,OccurrencePath path,BodyId body,
        GeometryRevisionId revision,IAssetStore assets,int offset,int limit,CancellationToken token=default)=>
        throw new NotSupportedException("Kernel has no original drawing topology inspector.");
    Task<DrawingTopologyReference> ResolveDrawingTopologyAsync(DocumentSnapshot document,OccurrencePath path,
        BodyId body,int fullTopologyIndex,string fingerprint,IAssetStore assets,CancellationToken token=default);
}
