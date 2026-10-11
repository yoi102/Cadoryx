using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

/// <summary>Author identity and derived current attachment remain separate.
/// A failed chain never carries a usable current attachment.</summary>
public sealed record DrawingAttachmentHistoryResolution(DrawingTopologyReference Author,
    DrawingTopologyReference? Current,HistoryResolution History);

public interface IDrawingTopologyHistoryResolver
{
    Task<DrawingAttachmentHistoryResolution> ResolveDrawingHistoryAsync(DocumentSnapshot document,
        DrawingTopologyReference author,DrawingSource explicitTarget,IAssetStore assets,CancellationToken token=default);
}
