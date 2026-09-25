using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Xunit;

namespace Cadoryx.Tests;

public sealed class StorageFaultMatrixTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedArchiveWriteKeepsOldDocumentAndRemovesTemporaryFile(bool cancel)
    {
        using var files=new TestFiles();
        var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        var path=files.PathFor("target.cadoryx");
        await storage.SaveAsync(DocumentSnapshot.Create("Old valid document"),assets,path);
        var original=File.ReadAllBytes(path);
        using var first=assets.Stage([1,2,3]);using var second=assets.Stage([4,5,6]);
        var next=DocumentSnapshot.Create("New document") with{Extensions=[
            new PreservedSection("vendor.first",1,first.Id,"messagepack"),
            new PreservedSection("vendor.second",1,second.Id,"messagepack")]};
        using var cts=new CancellationTokenSource();
        var fault=new FaultingAssetStore(assets,2,cts,cancel);
        if(cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>storage.SaveAsync(next,fault,path,cts.Token));
        else
            await Assert.ThrowsAsync<IOException>(()=>storage.SaveAsync(next,fault,path));
        Assert.Equal(2,fault.ContentReads);
        Assert.Equal(original,File.ReadAllBytes(path));
        Assert.Equal(new[]{"target.cadoryx"},Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
        using var loaded=await storage.LoadAsync(path,assets);
        Assert.Equal("Old valid document",loaded.Snapshot.Name);
    }

    [Fact] public async Task FailureWhileStagingLaterExtensionRollsBackEarlierAsset()
    {
        using var files=new TestFiles();
        var writerAssets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        using(var first=writerAssets.Stage([1,2,3]))
        using(var second=writerAssets.Stage([4,5,6]))
        {
            var snapshot=DocumentSnapshot.Create("Two extensions") with{Extensions=[
                new PreservedSection("vendor.first",1,first.Id,"messagepack"),
                new PreservedSection("vendor.second",1,second.Id,"messagepack")]};
            await storage.SaveAsync(snapshot,writerAssets,files.PathFor("two.cadoryx"));
        }
        Assert.Equal(0,writerAssets.Count);
        var readerAssets=new MemoryAssetStore();var fault=new FaultingAssetStore(readerAssets,failOnStage:2);
        await Assert.ThrowsAsync<IOException>(()=>storage.LoadAsync(files.PathFor("two.cadoryx"),fault));
        Assert.Equal(2,fault.StageCalls);
        Assert.Equal(0,readerAssets.Count);
        using var loaded=await storage.LoadAsync(files.PathFor("two.cadoryx"),readerAssets);
        Assert.Equal(2,loaded.Snapshot.Extensions.Length);
    }

    private sealed class FaultingAssetStore(IAssetStore inner,int failOnContent=0,CancellationTokenSource? cancellation=null,
        bool cancel=false,int failOnStage=0):IAssetStore
    {
        private readonly int contentFault=failOnContent;
        private readonly int stageFault=failOnStage;
        private readonly CancellationTokenSource? cancelSource=cancellation;
        private readonly bool cancelInstead=cancel;
        public int ContentReads {get;private set;}
        public int StageCalls {get;private set;}
        public IAssetLease Stage(ReadOnlySpan<byte> bytes)
        {
            if(++StageCalls==stageFault)throw new IOException("Injected asset staging failure.");
            return inner.Stage(bytes);
        }
        public IAssetLease Acquire(AssetId id)=>new FaultingLease(inner.Acquire(id),this);
        private sealed class FaultingLease(IAssetLease inner,FaultingAssetStore owner):IAssetLease
        {
            public AssetId Id=>inner.Id;
            public ReadOnlyMemory<byte> Content
            {
                get
                {
                    if(++owner.ContentReads==owner.contentFault)
                    {
                        if(owner.cancelInstead){owner.cancelSource!.Cancel();return inner.Content;}
                        throw new IOException("Injected archive payload read failure.");
                    }
                    return inner.Content;
                }
            }
            public void Dispose()=>inner.Dispose();
        }
    }
}
