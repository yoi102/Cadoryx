using System.Text;

namespace Cadoryx.Kernel.Abstractions;

public sealed record CacheCleanupReport(int Scanned,int Removed,int Skipped,int Failed,long ReclaimedBytes);

/// <summary>Reclaims only inactive v2 cache directories with a valid marker and an allowlisted flat layout.
/// No recursive traversal/deletion; legacy or unknown directories are never adopted.</summary>
public static class DiskAssetCache
{
    internal const string Prefix="assets-v2-";
    internal const string Marker=".owner";
    internal static string Signature(string name)=>"Cadoryx.AssetCache.2\n"+name;
    internal static FileStream CreateOwner(string directory)
    {
        var stream=new FileStream(Path.Combine(directory,Marker),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None);
        try{stream.Write(Encoding.UTF8.GetBytes(Signature(Path.GetFileName(directory))));stream.Flush(true);return stream;}
        catch{stream.Dispose();throw;}
    }
    public static CacheCleanupReport Reclaim(string root,int maximumDirectories=128)
    {
        if(maximumDirectories is <1 or >4096)throw new ArgumentOutOfRangeException(nameof(maximumDirectories));
        root=Path.GetFullPath(root);int scanned=0,removed=0,skipped=0,failed=0;long reclaimed=0;
        if(!Directory.Exists(root)||IsLink(root))return new(0,0,0,0,0);
        foreach(var candidate in Directory.EnumerateDirectories(root,Prefix+"*",SearchOption.TopDirectoryOnly).Take(maximumDirectories))
        {
            scanned++;
            try
            {
                var directory=Path.GetFullPath(candidate);var name=Path.GetFileName(directory);
                if(!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(directory),root.TrimEnd(Path.DirectorySeparatorChar))||
                    !name.StartsWith(Prefix,StringComparison.Ordinal)||!Guid.TryParseExact(name[Prefix.Length..],"N",out _)||IsLink(directory))
                {skipped++;continue;}
                var marker=Path.Combine(directory,Marker);
                if(!File.Exists(marker)||IsLink(marker)){skipped++;continue;}
                // Failure to acquire exclusive access means an active process/consumer, not permission to delete.
                FileStream owner;
                try{owner=new(marker,FileMode.Open,FileAccess.ReadWrite,FileShare.None);}
                catch(IOException){skipped++;continue;}
                using(owner)
                {
                    var expected=Encoding.UTF8.GetBytes(Signature(name));
                    if(owner.Length!=expected.Length){skipped++;continue;}
                    var bytes=new byte[expected.Length];owner.ReadExactly(bytes);
                    if(!bytes.AsSpan().SequenceEqual(expected)){skipped++;continue;}
                    var entries=Directory.GetFileSystemEntries(directory);
                    bool known=entries.All(path=>!IsLink(path)&&!Directory.Exists(path)&&
                        (Path.GetFileName(path)==Marker||IsPayload(Path.GetFileName(path))));
                    if(!known){skipped++;continue;}
                    foreach(var file in entries.Where(p=>Path.GetFileName(p)!=Marker))
                    {long length=new FileInfo(file).Length;File.Delete(file);reclaimed+=length;}
                }
                File.Delete(marker);Directory.Delete(directory);removed++;
            }
            catch(IOException){failed++;}
            catch(UnauthorizedAccessException){failed++;}
        }
        return new(scanned,removed,skipped,failed,reclaimed);
    }
    private static bool IsPayload(string name)=>name.Length==68&&name.EndsWith(".bin",StringComparison.Ordinal)&&name.AsSpan(0,64).ToArray().All(char.IsAsciiHexDigitLower);
    private static bool IsLink(string path)=>(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0;
}
