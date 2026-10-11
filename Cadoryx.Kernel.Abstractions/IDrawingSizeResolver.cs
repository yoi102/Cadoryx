using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public interface IDrawingSizeResolver
{
    Task<DrawingCylinderSizeEvidence> ResolveDrawingCylinderSizeAsync(DocumentSnapshot document,DrawingTopologyReference source,
        IAssetStore assets,CancellationToken token=default);
}
