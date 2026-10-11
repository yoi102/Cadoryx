using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public interface IDrawingHoleResolver
{
    Task<DrawingHoleEvidence> ResolveDrawingHoleAsync(DocumentSnapshot document,DrawingTopologyReference shaft,
        IAssetStore assets,CancellationToken token=default);
}
