using System.Security.Cryptography;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public interface IAssetLease : IDisposable
{
    AssetId Id { get; }
    ReadOnlyMemory<byte> Content { get; }
}
public interface IAssetStore
{
    IAssetLease Stage(ReadOnlySpan<byte> content);
    IAssetLease Acquire(AssetId id);
}
/// <summary>Content-addressed immutable assets. A lease is required for every consumer.</summary>
public sealed class MemoryAssetStore : IAssetStore
{
    private readonly object gate=new();
    private readonly Dictionary<AssetId,Entry> entries=[];
    private sealed class Entry(byte[] bytes) { public byte[] Bytes { get; }=bytes; public int References; }
    public int Count { get {lock(gate)return entries.Count;} }
    public long SizeBytes { get {lock(gate)return entries.Values.Sum(e=>(long)e.Bytes.Length);} }
    public IAssetLease Stage(ReadOnlySpan<byte> content)
    {
        if(content.IsEmpty)throw new ArgumentException("Empty geometry asset.");
        var copy=content.ToArray();var id=new AssetId(Convert.ToHexStringLower(SHA256.HashData(copy)));
        lock(gate)
        {
            if(!entries.TryGetValue(id,out var entry))entries.Add(id,entry=new(copy));
            entry.References++;return new Lease(this,id,entry.Bytes);
        }
    }
    public IAssetLease Acquire(AssetId id)
    {
        id.Validate();lock(gate)
        {
            if(!entries.TryGetValue(id,out var entry))throw new KeyNotFoundException($"Missing asset {id}.");
            entry.References++;return new Lease(this,id,entry.Bytes);
        }
    }
    private void Release(AssetId id) { lock(gate) { if(--entries[id].References==0) entries.Remove(id); } }
    private sealed class Lease(MemoryAssetStore owner,AssetId id,byte[] content) : IAssetLease
    {
        private int disposed;
        public AssetId Id { get; }=id;
        public ReadOnlyMemory<byte> Content { get {ObjectDisposedException.ThrowIf(disposed!=0,this);return content;} }
        public void Dispose() {if(Interlocked.Exchange(ref disposed,1)==0)owner.Release(Id);}
    }
}
public sealed class DocumentAssetLease : IDisposable
{
    private readonly List<IAssetLease> leases=[];
    public DocumentAssetLease(DocumentSnapshot snapshot,IAssetStore store)
    {
        try {foreach(var id in snapshot.ReferencedAssets())leases.Add(store.Acquire(id));}
        catch {Dispose();throw;}
    }
    public void Dispose(){foreach(var lease in leases)lease.Dispose();leases.Clear();}
}
