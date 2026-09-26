using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

/// <summary>Current document scale; asset bytes are unique referenced payloads, not process memory.</summary>
public sealed record DocumentScaleReport(int Definitions,int Occurrences,int Features,int UniqueAssets,long AssetBytes)
{
    public static DocumentScaleReport Measure(DocumentSnapshot snapshot,IAssetStore assets)
    {
        ArgumentNullException.ThrowIfNull(snapshot);ArgumentNullException.ThrowIfNull(assets);
        long bytes=0;int count=0;
        foreach(var id in snapshot.ReferencedAssets())
        {
            using var lease=assets.Acquire(id);
            bytes=checked(bytes+lease.Length);count++;
        }
        return new(snapshot.Definitions.Count,snapshot.EnumerateOccurrences().Count(),
            snapshot.Features.Count,count,bytes);
    }
}
