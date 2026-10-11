using System.Collections.Immutable;
using System.Security.Cryptography;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.IO;

public sealed class DocumentFileService(IDocumentStorage storage, IGeometryKernel kernel,
    IDocumentDeliveryService delivery) : IDocumentFileService
{
    public async Task<DrawingTemplateDefinition> ReadDrawingTemplateAsync(string path,CancellationToken token=default)
    {
        if(string.IsNullOrWhiteSpace(path)||path.Length>4096||!Path.IsPathFullyQualified(path))throw new CadValidationException("An absolute template path is required.");
        token.ThrowIfCancellationRequested();var entry=await Task.Run(()=>DrawingTemplateFile.Read(path),token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();return entry;
    }
    public async Task<DocumentFileReceipt> WriteAsync(DocumentSnapshot snapshot, IAssetStore assets,
        DocumentFileRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Path) || request.Path.Length > 4096 || !Path.IsPathFullyQualified(request.Path))
            throw new CadValidationException("An absolute output path is required.");
        var path = Path.GetFullPath(request.Path); var ext = Path.GetExtension(path).ToLowerInvariant();
        if((request.Kind=="template_export")!=(request.TemplateId is not null))throw new CadValidationException("Template identity is required only for template export.");
        var expected = request.Kind switch
        {
            "save" when request.Export is null && request.Delivery is null => new[] { ".cadoryx" },
            "pdf" when request.Export is null && request.Delivery is null => [".pdf"],
            "template_export" when request.Export is null && request.Delivery is null => [".json"],
            "export" when request.Delivery is null => [".step", ".stp", ".iges", ".igs", ".stl"],
            "report" when request.Export is null && request.Delivery is not null => request.Delivery.Output switch
            {
                DeliveryOutput.Package or DeliveryOutput.ExchangeAudit => [".zip"],
                DeliveryOutput.BomCsv => [".csv"], DeliveryOutput.ReviewHtml => [".html"],
                _ => throw new CadValidationException("Unknown report output.")
            },
            _ => throw new CadValidationException("Invalid file operation or options.")
        };
        if (!expected.Contains(ext)) throw new CadValidationException("Invalid output extension for " + request.Kind + ".");
        request.Export?.Validate(); request.Delivery?.Validate(); snapshot.Validate();
        if (!request.Overwrite && File.Exists(path)) throw new IOException("Output exists; replacement was not requested.");
        using var lease = new DocumentAssetLease(snapshot, assets);
        var directory = Path.GetDirectoryName(path)!; Directory.CreateDirectory(directory);
        // Preserve the selected extension for native exporters. Only the final move publishes an output.
        var temp = Path.Combine(directory, ".cadoryx-file-" + Guid.NewGuid().ToString("N") + ext);
        try
        {
            ImmutableArray<CadDiagnostic> diagnostics = []; string format;
            if (request.Kind == "save")
            { await storage.SaveAsync(snapshot, assets, temp, token).ConfigureAwait(false); format = "CADORYX"; }
            else if(request.Kind=="pdf")
            {await Task.Run(()=>TechnicalDrawingPdf.Write(snapshot,temp),token).ConfigureAwait(false);format="DrawingPDF";}
            else if(request.Kind=="template_export")
            {
                var entry=snapshot.DrawingTemplates.GetValueOrDefault(request.TemplateId!.Value)??throw new CadValidationException("Exact drawing template is missing.");
                await Task.Run(()=>DrawingTemplateFile.Write(entry,temp),token).ConfigureAwait(false);format="DrawingTemplateV1";
            }
            else if (request.Kind == "export")
            {
                var result = await kernel.ExportAsync(snapshot, assets, temp, request.Export ?? new(), token).ConfigureAwait(false);
                diagnostics = result.Diagnostics; format = result.Format;
            }
            else
            {
                var result = await delivery.WriteAsync(snapshot, assets, temp, request.Delivery!, token: token).ConfigureAwait(false);
                diagnostics = result.Diagnostics; format = request.Delivery!.Output.ToString();
            }
            if (diagnostics.Any(d => d.IsError)) throw new CadValidationException("File generation reported an error: " +
                string.Join("; ", diagnostics.Where(d => d.IsError).Select(d => d.Message)));
            long bytes; string hash;
            await using (var stream = File.OpenRead(temp))
            { bytes = stream.Length; hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)); }
            token.ThrowIfCancellationRequested(); File.Move(temp, path, request.Overwrite);
            // Publication has completed. A later cancellation must still return its real receipt.
            return new(request.Kind, path, format, bytes, hash, snapshot.Id, snapshot.StateId, diagnostics);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
