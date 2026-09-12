using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.IO;

internal static class AssetCatalog
{
    internal static IReadOnlyList<AssetEntry> Build(DocumentSnapshot snapshot,IAssetStore assets,CancellationToken token)
    {
        var metadata=new Dictionary<AssetId,AssetFormat>();
        var geometryIds=new HashSet<AssetId>();
        foreach(var geometry in snapshot.ReferencedGeometry())
        {
            token.ThrowIfCancellationRequested();
            Add(geometry.AssetId,geometry.Format,false);
            if(geometry.Source is {} source)Add(source.ContextAssetId,source.Format,true);
        }
        void Add(AssetId id,AssetFormat? supplied,bool xde)
        {
            geometryIds.Add(id);
            if(supplied is null){using var lease=assets.Acquire(id);supplied=AssetFormatPolicy.Inspect(lease.Content.Span);}
            if(xde)AssetFormatPolicy.RequireXde(supplied);else AssetFormatPolicy.RequireBRep(supplied);
            if(metadata.TryGetValue(id,out var old)&&old!=supplied)throw new InvalidDataException("Conflicting format/provenance for the same asset.");
            metadata[id]=supplied;
        }
        var extensions=snapshot.Extensions.IsDefault?[]:snapshot.Extensions.Select(e=>e.PayloadAssetId).ToHashSet();
        var result=new List<AssetEntry>();
        foreach(var id in snapshot.ReferencedAssets().OrderBy(a=>a.Sha256,StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if(extensions.Contains(id)&&!geometryIds.Contains(id)&&(snapshot.RetainedAssets.IsDefault||!snapshot.RetainedAssets.Contains(id)))continue;
            if(extensions.Count==0&&!geometryIds.Contains(id))throw new InvalidDataException("Retained assets require an extension section.");
            using var lease=assets.Acquire(id);
            var format=metadata.GetValueOrDefault(id)??snapshot.RetainedAssetFormats?.GetValueOrDefault(id)??AssetFormat.Opaque;
            if(snapshot.RetainedAssetFormats?.TryGetValue(id,out var retained)==true&&retained!=format)
                throw new InvalidDataException("Retained format conflicts with geometry provenance.");
            // Unknown extension payloads are copied, not decoded, even when they declare a future OCCT format.
            if(geometryIds.Contains(id))AssetFormatPolicy.VerifyPayload(format,lease.Content.Span);
            else format.Validate();
            result.Add(new(id,"assets/"+id.Sha256+".bin",lease.Content.Length,id.Sha256,format));
        }
        return result;
    }
}
