using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public interface IDrawingPlanarSizeResolver
{
    Task<DrawingPlanarSizeEvidence> ResolveDrawingPlanarSizeAsync(DocumentSnapshot document,DrawingTopologyReference source,
        DrawingTopologyReference opposite,bool internalWidth,IAssetStore assets,CancellationToken token=default);
}
