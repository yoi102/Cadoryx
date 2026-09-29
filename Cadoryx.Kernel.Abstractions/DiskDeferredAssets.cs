using System.Security.Cryptography;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;
public sealed partial class DiskAssetStore
{
    public IAssetFileLease StageFile(string path,CancellationToken token=default)
    {
        // Keep one read-only source handle across hashing and copying; callers may replace/delete the original after return.
        using var source=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(source.Length==0)throw new InvalidDataException("Empty archive.");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[65536];int n;
        while((n=source.Read(buffer))>0){token.ThrowIfCancellationRequested();hash.AppendData(buffer,0,n);}
        var id=new AssetId(Convert.ToHexStringLower(hash.GetHashAndReset()));source.Position=0;
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);token.ThrowIfCancellationRequested();
            if(!entries.TryGetValue(id,out var entry))
            {
                var destination=Path.Combine(directory,id+".bin");bool created=false;
                try
                {
                    using var file=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);created=true;
                    while((n=source.Read(buffer))>0){token.ThrowIfCancellationRequested();file.Write(buffer,0,n);}
                }
                catch{if(created)File.Delete(destination);throw;}
                entries.Add(id,entry=new(destination,source.Length));
            }
            entry.References++;return new Lease(this,id,entry);
        }
    }
    public IAssetLease StageDeferred(AssetId id,long length,Func<byte[]> materialize,IDisposable backing)
    {
        id.Validate();ArgumentNullException.ThrowIfNull(materialize);ArgumentNullException.ThrowIfNull(backing);
        if(length is <=0 or >int.MaxValue)throw new InvalidDataException("Invalid deferred asset length.");
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(entries.TryGetValue(id,out var existing))
            {
                if(existing.Length!=length)throw new InvalidDataException("Asset length conflicts with existing identity.");
                existing.References++;backing.Dispose();return new Lease(this,id,existing);
            }
            var entry=new Entry(Path.Combine(directory,id+".bin"),length){References=1,Materialize=materialize,Backing=backing};
            entries.Add(id,entry);return new Lease(this,id,entry);
        }
    }
    private void EnsureMaterialized(AssetId id,Entry entry)
    {
        if(entry.Materialize is not {} load)return;
        var bytes=load();
        if(bytes.LongLength!=entry.Length||Convert.ToHexStringLower(SHA256.HashData(bytes))!=id.Sha256)
            throw new InvalidDataException("Deferred asset integrity check failed: "+id);
        bool created=false;
        try{using var file=new FileStream(entry.Path,FileMode.CreateNew,FileAccess.Write,FileShare.None);created=true;file.Write(bytes);}
        catch{if(created)File.Delete(entry.Path);throw;}
        entry.Materialize=null;entry.Backing?.Dispose();entry.Backing=null;
    }
    private Stream OpenRead(AssetId id,Entry entry)
    {
        lock(gate)
        {
            EnsureMaterialized(id,entry);
            return new FileStream(entry.Path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        }
    }
}
