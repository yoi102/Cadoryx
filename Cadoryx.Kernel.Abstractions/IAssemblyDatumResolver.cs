using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public interface IAssemblyDatumResolver
{
    Task<AssemblyDatumReference> ResolveAssemblyDatumAsync(DocumentSnapshot document,
        OccurrencePath path,BodyId bodyId,int fullTopologyIndex,string fingerprint,
        IAssetStore assets,CancellationToken token=default);
}
