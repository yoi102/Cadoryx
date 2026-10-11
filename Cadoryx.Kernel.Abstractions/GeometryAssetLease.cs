using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

/// <summary>Owns the BRep and its optional reference table for the same lifetime.</summary>
public static class GeometryAssetLease
{
    public static IAssetLease Acquire(GeometryAssetRef geometry,IAssetStore assets)
    {
        geometry.Validate();var leases=new List<IAssetLease>();
        try
        {
            foreach(var id in geometry.BoundaryAssets().Distinct())leases.Add(assets.Acquire(id));
            return new BoundaryLease(leases.ToArray());
        }
        catch{foreach(var lease in leases)lease.Dispose();throw;}
    }
    public static IAssetLease Own(IAssetLease boundary,IAssetLease table)=>new BoundaryLease([boundary,table]);
    private sealed class BoundaryLease(IAssetLease[] leases):IAssetLease
    {
        public AssetId Id=>leases[0].Id;
        public ReadOnlyMemory<byte> Content=>leases[0].Content;
        public long Length=>leases[0].Length;
        public void Dispose(){foreach(var lease in leases)lease.Dispose();}
    }
}
