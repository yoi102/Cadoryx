using System.Security.Cryptography;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

/// <summary>Session-scoped immutable payloads. Holding a lease does not load bytes;
/// only Content materializes a checked copy, cached for that consumer's lifetime.</summary>
public sealed class DiskAssetStore : IAssetStore,IAssetStoreStatistics,IDisposable
{
    private readonly object gate=new();
    private readonly Dictionary<AssetId,Entry> entries=[];
    private readonly string directory;
    private FileStream? ownerLock;
    private bool disposed;
    private sealed class Entry(string path,long length) { public string Path=path; public long Length=length; public int References; }
    public string DirectoryPath=>directory;
    public int Count {get {lock(gate)return entries.Count;}}
    public long SizeBytes {get {lock(gate)return entries.Values.Sum(e=>e.Length);}}
    public long ReadCount {get;private set;}
    public DiskAssetStore(string root)
    {
        root=Path.GetFullPath(root);Directory.CreateDirectory(root);
        // Cleanup is best-effort; a temporarily inaccessible cache is not a corrupt document.
        try{DiskAssetCache.Reclaim(root);}catch(IOException){}catch(UnauthorizedAccessException){}
        directory=Path.Combine(root,DiskAssetCache.Prefix+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ownerLock=DiskAssetCache.CreateOwner(directory);
    }
    public IAssetLease Stage(ReadOnlySpan<byte> content)
    {
        if(content.IsEmpty)throw new ArgumentException("Empty geometry asset.");
        var copy=content.ToArray();var id=new AssetId(Convert.ToHexStringLower(SHA256.HashData(copy)));
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(!entries.TryGetValue(id,out var entry))
            {
                var path=Path.Combine(directory,id.ToString()+".bin");
                bool created=false;
                try {using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);created=true;file.Write(copy);file.Flush(true);}
                catch {if(created&&File.Exists(path))File.Delete(path);throw;}
                entries.Add(id,entry=new(path,copy.Length));
            }
            entry.References++;return new Lease(this,id,entry);
        }
    }
    public IAssetLease Acquire(AssetId id)
    {
        id.Validate();lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(!entries.TryGetValue(id,out var entry))throw new KeyNotFoundException($"Missing asset {id}.");
            entry.References++;return new Lease(this,id,entry);
        }
    }
    private byte[] Read(AssetId id,Entry entry)
    {
        lock(gate)
        {
            var bytes=File.ReadAllBytes(entry.Path);
            if(bytes.LongLength!=entry.Length||Convert.ToHexStringLower(SHA256.HashData(bytes))!=id.ToString())
                throw new InvalidDataException($"Asset payload integrity check failed: {id}.");
            ReadCount++;return bytes;
        }
    }
    private void Release(AssetId id,Entry entry)
    {
        lock(gate)
        {
            if(--entry.References==0)
            {
                entries.Remove(id);
                try{File.Delete(entry.Path);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
            if(disposed&&entries.Count==0)ReleaseDirectory();
        }
    }
    public void Dispose()
    {
        lock(gate)
        {
            disposed=true;
            // Outstanding consumers keep their files until their own leases close.
            if(entries.Count==0)ReleaseDirectory();
        }
    }
    private void ReleaseDirectory()
    {
        if(ownerLock is null)return;
        ownerLock.Dispose();ownerLock=null;
        try
        {
            if(Directory.EnumerateFileSystemEntries(directory).Any(p=>Path.GetFileName(p)!=DiskAssetCache.Marker))return;
            File.Delete(Path.Combine(directory,DiskAssetCache.Marker));Directory.Delete(directory);
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
    private sealed class Lease(DiskAssetStore owner,AssetId id,Entry entry) : IAssetLease
    {
        private readonly object gate=new();private bool disposed;private byte[]? content;
        public AssetId Id=>id;
        public long Length {get {lock(gate){ObjectDisposedException.ThrowIf(disposed,this);return entry.Length;}}}
        public ReadOnlyMemory<byte> Content {get {lock(gate){ObjectDisposedException.ThrowIf(disposed,this);return content??=owner.Read(id,entry);}}}
        public void Dispose(){lock(gate){if(disposed)return;disposed=true;content=null;owner.Release(id,entry);}}
    }
}
