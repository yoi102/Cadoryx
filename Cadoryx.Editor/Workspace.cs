using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

/// <summary>Explicitly owns document lifetimes; no ambient DI scope is assumed.</summary>
public sealed class CadWorkspace(IGeometryKernel kernel,IAssetStore assets,ISessionDispatcher dispatcher) : IAsyncDisposable
{
    private readonly List<CadDocumentSession> documents=[];
    public IReadOnlyList<CadDocumentSession> Documents=>documents.AsReadOnly();
    public CadDocumentSession Create(string name)=>Attach(DocumentSnapshot.Create(name));
    public CadDocumentSession Attach(DocumentSnapshot snapshot,string? filePath=null)
    {
        var session=new CadDocumentSession(snapshot,assets,kernel,dispatcher,filePath);documents.Add(session);return session;
    }
    public async Task CloseAsync(CadDocumentSession session)
    {
        if(!documents.Contains(session))return;
        await session.DisposeAsync();documents.Remove(session);
    }
    public async ValueTask DisposeAsync(){foreach(var session in documents.ToArray())await CloseAsync(session);}
}
