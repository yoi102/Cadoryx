using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    private sealed class StyledGeometryLease(IAssetLease geometry,IAssetLease context):IAssetLease
    {
        private int disposed;
        public AssetId Id=>geometry.Id;
        public ReadOnlyMemory<byte> Content=>geometry.Content;
        public void Dispose()
        {if(Interlocked.Exchange(ref disposed,1)==0)try{geometry.Dispose();}finally{context.Dispose();}}
    }

    private sealed class ExportStyleContexts(IAssetStore assets):IDisposable
    {
        private readonly Dictionary<AssetId,XdeDocument> documents=[];
        public XdeDocument Get(XdeSourceRef source)
        {
            if(documents.TryGetValue(source.ContextAssetId,out var cached))return cached;
            var document=OcctGeometryBridge.ReadContext(source.ContextAssetId,assets,source.Format);
            documents.Add(source.ContextAssetId,document);return document;
        }
        public void Dispose(){foreach(var document in documents.Values)document.Dispose();}
    }
}
