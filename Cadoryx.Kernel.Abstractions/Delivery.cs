using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public enum DeliveryOutput { Package, BomCsv, ReviewHtml }
public sealed record DeliveryOptions(DeliveryOutput Output=DeliveryOutput.Package,bool VisibleOnly=true,
    bool IncludeStep=true,bool IncludeIges=false,bool IncludeStl=false,double LinearDeflectionMm=.1,double AngularDeflectionRad=.5)
{
    public void Validate()
    {
        if(!Enum.IsDefined(Output))throw new CadValidationException("Unknown delivery output.");
        new CadExportOptions(LinearDeflectionMm,AngularDeflectionRad).Validate();
    }
}
public sealed record DeliveryProgress(string Stage,int Completed,int Total);
public sealed record DeliveryResult(string Path,DocumentId DocumentId,DocumentStateId StateId,int PartInstances,
    ImmutableArray<CadDiagnostic> Diagnostics);
public interface IDocumentDeliveryService
{
    Task<DeliveryResult> WriteAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,DeliveryOptions options,
        bool overwrite=false,IProgress<DeliveryProgress>? progress=null,CancellationToken token=default);
}
