using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

/// <summary>Writes a named, captured author snapshot. The receipt describes the published bytes.</summary>
public sealed record DocumentFileRequest(string Kind, string Path, bool Overwrite = false,
    CadExportOptions? Export = null, DeliveryOptions? Delivery = null,Guid? TemplateId=null);
public sealed record DocumentFileReceipt(string Kind, string Path, string Format, long Bytes, string Sha256,
    DocumentId DocumentId, DocumentStateId StateId, ImmutableArray<CadDiagnostic> Diagnostics);
public interface IDocumentFileService
{
    Task<DrawingTemplateDefinition> ReadDrawingTemplateAsync(string path,CancellationToken token=default)=>
        throw new NotSupportedException("Drawing template import is unavailable.");
    Task<DocumentFileReceipt> WriteAsync(DocumentSnapshot snapshot, IAssetStore assets,
        DocumentFileRequest request, CancellationToken token = default);
}
