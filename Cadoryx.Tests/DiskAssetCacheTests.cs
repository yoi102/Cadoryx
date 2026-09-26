using Cadoryx.Kernel.Abstractions;
using Xunit;

namespace Cadoryx.Tests;

public sealed class DiskAssetCacheTests
{
    [Fact] public void CleanupSkipsActiveStoreAndOutstandingConsumersAfterDisposal()
    {
        using var files=new TestFiles();var root=files.PathFor("cache");var store=new DiskAssetStore(root);var lease=store.Stage([1,2,3]);
        Assert.Equal(1,DiskAssetCache.Reclaim(root).Skipped);store.Dispose();
        Assert.Equal(1,DiskAssetCache.Reclaim(root).Skipped);Assert.Equal(new byte[]{1,2,3},lease.Content.ToArray());
        lease.Dispose();Assert.False(Directory.Exists(store.DirectoryPath));
    }
    [Fact] public void ValidOrphanIsReclaimedAtNextStoreStartup()
    {
        using var files=new TestFiles();var root=files.PathFor("cache");var orphan=Orphan(root);
        using var store=new DiskAssetStore(root);Assert.False(Directory.Exists(orphan));
        Assert.True(Directory.Exists(store.DirectoryPath));Assert.Equal(0,DiskAssetCache.Reclaim(root).Removed);
    }
    [Fact] public void CleanupAccountsBytesAndDoesNotRecurseIntoUnknownContent()
    {
        using var files=new TestFiles();var root=files.PathFor("cache");var orphan=Orphan(root);var unknown=Orphan(root);
        Directory.CreateDirectory(Path.Combine(unknown,"nested"));File.WriteAllText(Path.Combine(unknown,"nested","user.txt"),"keep");
        var report=DiskAssetCache.Reclaim(root);Assert.Equal(1,report.Removed);Assert.Equal(1,report.Skipped);Assert.Equal(4,report.ReclaimedBytes);
        Assert.False(Directory.Exists(orphan));Assert.True(File.Exists(Path.Combine(unknown,"nested","user.txt")));
    }
    [Theory] [InlineData("unknown")] [InlineData("signature")] [InlineData("missing")]
    public void CleanupRefusesUnprovableOwnership(string reason)
    {
        using var files=new TestFiles();var root=files.PathFor("cache");var orphan=Orphan(root);
        if(reason=="unknown")File.WriteAllText(Path.Combine(orphan,"keep.txt"),"user data");
        if(reason=="signature")File.WriteAllText(Path.Combine(orphan,".owner"),"invalid");
        if(reason=="missing")File.Delete(Path.Combine(orphan,".owner"));
        var legacy=Path.Combine(root,"assets-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(legacy);File.WriteAllText(Path.Combine(legacy,"legacy.txt"),"keep");
        Assert.Equal(0,DiskAssetCache.Reclaim(root).Removed);Assert.True(Directory.Exists(orphan));Assert.True(Directory.Exists(legacy));
    }
    [Fact] public void CleanupHasBoundedWorkAndSecondPassIsIdempotent()
    {
        using var files=new TestFiles();var root=files.PathFor("cache");for(int i=0;i<3;i++)Orphan(root);
        Assert.Equal(2,DiskAssetCache.Reclaim(root,2).Removed);Assert.Equal(1,DiskAssetCache.Reclaim(root).Removed);
        Assert.Equal(0,DiskAssetCache.Reclaim(root).Scanned);Assert.Throws<ArgumentOutOfRangeException>(()=>DiskAssetCache.Reclaim(root,0));
    }
    [Fact] public async Task ConcurrentCleanersDoNotTouchLivePayload()
    {
        using var files=new TestFiles();var root=files.PathFor("cache");using var store=new DiskAssetStore(root);using var lease=store.Stage([2,4,6]);
        for(int i=0;i<5;i++)Orphan(root);
        await Task.WhenAll(Enumerable.Range(0,4).Select(_=>Task.Run(()=>DiskAssetCache.Reclaim(root))));
        Assert.Equal(new byte[]{2,4,6},lease.Content.ToArray());Assert.Equal(1,store.Count);
    }
    private static string Orphan(string root)
    {
        string name="assets-v2-"+Guid.NewGuid().ToString("N");string directory=Path.Combine(root,name);Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,".owner"),"Cadoryx.AssetCache.2\n"+name);
        File.WriteAllBytes(Path.Combine(directory,new string('a',64)+".bin"),[1,3,5,7]);return directory;
    }
}
