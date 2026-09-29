using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.IO;

public sealed record DeliveryFile(string Name,long Bytes,string Sha256);
public sealed record DeliveryManifest(int SchemaVersion,string DocumentId,string StateId,string Name,DateTimeOffset CreatedUtc,
    DeliveryOptions Options,string Kernel,DeliveryFile[] Files,CadDiagnostic[] Diagnostics);

/// <summary>Snapshot in, sibling temporary files out. Publishing is the only operation touching the target.</summary>
public sealed class DocumentDeliveryService(IDocumentStorage storage,IGeometryKernel kernel):IDocumentDeliveryService
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    public async Task<DeliveryResult> WriteAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,DeliveryOptions options,
        bool overwrite=false,IProgress<DeliveryProgress>? progress=null,CancellationToken token=default)
    {
        options.Validate();token.ThrowIfCancellationRequested();
        path=Path.GetFullPath(path);var ext=options.Output switch{DeliveryOutput.Package=>".zip",DeliveryOutput.BomCsv=>".csv",_=>".html"};
        if(!Path.GetExtension(path).Equals(ext,StringComparison.OrdinalIgnoreCase))throw new CadValidationException("Expected delivery extension "+ext);
        if(!overwrite&&File.Exists(path))throw new IOException("Output exists; replacement was not requested.");
        var leases=new List<IAssetLease>();
        string? work=null;
        try
        {
            foreach(var id in snapshot.ReferencedAssets()){token.ThrowIfCancellationRequested();leases.Add(assets.Acquire(id));}
            // Native exporters already run on workers; pure report and ZIP work also stay off the UI thread.
            return await Task.Run(async()=>
            {
                var bom=BillOfMaterials.Create(snapshot,options.VisibleOnly,token);
                var directory=Path.GetDirectoryName(path)!;Directory.CreateDirectory(directory);
                work=Path.Combine(directory,".cadoryx-delivery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
                var temp=Path.Combine(work,"output"+ext);var diagnostics=ImmutableArray.CreateBuilder<CadDiagnostic>();
                diagnostics.Add(new("DELIVERY.SCOPE","Native document contains all geometry and history; BOM and exchange files use document visibility, not temporary viewport filters."));
                diagnostics.Add(new("DELIVERY.METADATA","BOM volume and mass use cached solid metadata. Missing density, non-solid or stale geometry gives unknown values; instance overlap is not subtracted."));
                if(options.Output==DeliveryOutput.BomCsv)await File.WriteAllTextAsync(temp,EngineeringReport.Csv(bom),new UTF8Encoding(true),token);
                else if(options.Output==DeliveryOutput.ReviewHtml)await File.WriteAllTextAsync(temp,EngineeringReport.Html(snapshot,bom,token),Encoding.UTF8,token);
                else
                {
                    var payload=Path.Combine(work,"payload");Directory.CreateDirectory(payload);
                    int total=6+(options.IncludeStep?1:0)+(options.IncludeIges?1:0)+(options.IncludeStl?1:0),completed=0;
                    void Stage(string name){token.ThrowIfCancellationRequested();progress?.Report(new(name,completed++,total));}
                    Stage("Native document");await storage.SaveAsync(snapshot,assets,Path.Combine(payload,"model.cadoryx"),token);
                    Stage("BOM and review");
                    await File.WriteAllTextAsync(Path.Combine(payload,"bom.csv"),EngineeringReport.Csv(bom),new UTF8Encoding(true),token);
                    await File.WriteAllTextAsync(Path.Combine(payload,"bom.json"),JsonSerializer.Serialize(bom,Json),token);
                    await File.WriteAllTextAsync(Path.Combine(payload,"review.html"),EngineeringReport.Html(snapshot,bom,token),token);
                    foreach(var format in new[]{(options.IncludeStep,"step"),(options.IncludeIges,"iges"),(options.IncludeStl,"stl")}.Where(x=>x.Item1))
                    {
                        Stage(format.Item2.ToUpperInvariant());
                        var result=await kernel.ExportAsync(snapshot,assets,Path.Combine(payload,"model."+format.Item2),
                            new(options.LinearDeflectionMm,options.AngularDeflectionRad,VisibleOnly:options.VisibleOnly),token);
                        diagnostics.AddRange(result.Diagnostics);
                    }
                    Stage("Exchange capability report");
                    await File.WriteAllTextAsync(Path.Combine(payload,"exchange-loss.json"),
                        JsonSerializer.Serialize(ExchangeLossReport.Create(snapshot,options),Json),token);
                    Stage("Manifest");var entries=new List<DeliveryFile>();
                    foreach(var file in Directory.GetFiles(payload).Order(StringComparer.Ordinal))
                    {
                        await using var stream=File.OpenRead(file);entries.Add(new(Path.GetFileName(file),stream.Length,Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,token))));
                    }
                    var manifest=new DeliveryManifest(2,snapshot.Id.ToString(),snapshot.StateId.ToString(),snapshot.Name,DateTimeOffset.UtcNow,options,kernel.Version,entries.ToArray(),diagnostics.ToArray());
                    await File.WriteAllTextAsync(Path.Combine(payload,"manifest.json"),JsonSerializer.Serialize(manifest,Json),token);
                    Stage("Archive");
                    using(var zip=ZipFile.Open(temp,ZipArchiveMode.Create))
                        foreach(var file in Directory.GetFiles(payload).Order(StringComparer.Ordinal))
                        {
                            token.ThrowIfCancellationRequested();var entry=zip.CreateEntry(Path.GetFileName(file),CompressionLevel.Fastest);
                            await using var source=File.OpenRead(file);await using var destination=entry.Open();await source.CopyToAsync(destination,token);
                        }
                    Stage("Verify");await VerifyAsync(temp,token);
                    progress?.Report(new("Ready",total,total));
                }
                token.ThrowIfCancellationRequested();File.Move(temp,path,overwrite);
                return new DeliveryResult(path,snapshot.Id,snapshot.StateId,bom.Parts.Sum(p=>p.Quantity),diagnostics.ToImmutable());
            },token).ConfigureAwait(false);
        }
        finally
        {
            try{if(work is not null&&Directory.Exists(work))Directory.Delete(work,true);}
            finally{foreach(var lease in leases)lease.Dispose();}
        }
    }
    // No extraction, fixed payload names, bounded entry count/manifest/total expanded bytes.
    public static async Task<DeliveryManifest> VerifyAsync(string path,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();using var zip=ZipFile.OpenRead(path);
        var allowed=new HashSet<string>(StringComparer.Ordinal){"manifest.json","model.cadoryx","bom.csv","bom.json","review.html","model.step","model.iges","model.stl","exchange-loss.json"};
        if(zip.Entries.Count is <5 or >9||zip.Entries.Select(e=>e.FullName).Distinct(StringComparer.Ordinal).Count()!=zip.Entries.Count||
            zip.Entries.Any(e=>!allowed.Contains(e.FullName)||e.Length<0||e.Length>8L*1024*1024*1024)||zip.Entries.Sum(e=>e.Length)>16L*1024*1024*1024)
            throw new InvalidDataException("Invalid delivery archive entries or size.");
        var manifestEntry=zip.GetEntry("manifest.json")??throw new InvalidDataException("Missing delivery manifest.");
        if(manifestEntry.Length>1024*1024)throw new InvalidDataException("Manifest too large.");
        DeliveryManifest manifest;
        await using(var input=manifestEntry.Open())manifest=await JsonSerializer.DeserializeAsync<DeliveryManifest>(input,Json,token)??throw new InvalidDataException("Empty manifest.");
        if(manifest.SchemaVersion is not (1 or 2)||manifest.Options is null||manifest.Files is null||manifest.Diagnostics is null||
            !Guid.TryParse(manifest.DocumentId,out var docId)||docId==Guid.Empty||!Guid.TryParse(manifest.StateId,out var stateId)||stateId==Guid.Empty||
            manifest.Files.Length!=zip.Entries.Count-1||manifest.Files.Any(f=>f is null)||manifest.Files.Select(f=>f.Name).Distinct(StringComparer.Ordinal).Count()!=manifest.Files.Length)
            throw new InvalidDataException("Invalid delivery manifest.");
        manifest.Options.Validate();
        var expected=new HashSet<string>(StringComparer.Ordinal){"model.cadoryx","bom.csv","bom.json","review.html"};
        if(manifest.SchemaVersion==2)expected.Add("exchange-loss.json");
        if(manifest.Options.IncludeStep)expected.Add("model.step");if(manifest.Options.IncludeIges)expected.Add("model.iges");if(manifest.Options.IncludeStl)expected.Add("model.stl");
        if(manifest.Options.Output!=DeliveryOutput.Package||!expected.SetEquals(manifest.Files.Select(f=>f.Name)))throw new InvalidDataException("Manifest payload differs from declared options.");
        if(manifest.SchemaVersion==2)
        {
            var reportEntry=zip.GetEntry("exchange-loss.json")!;
            if(reportEntry.Length>65536)throw new InvalidDataException("Exchange report is too large.");
            ExchangeLossReport report;
            await using(var input=reportEntry.Open())
                report=await JsonSerializer.DeserializeAsync<ExchangeLossReport>(input,Json,token)??
                    throw new InvalidDataException("Empty exchange report.");
            var formats=new HashSet<string>(StringComparer.Ordinal);
            if(manifest.Options.IncludeStep)formats.Add("STEP");
            if(manifest.Options.IncludeIges)formats.Add("IGES");
            if(manifest.Options.IncludeStl)formats.Add("STL");
            if(report.SchemaVersion!=1||report.DocumentId!=manifest.DocumentId||report.StateId!=manifest.StateId||
               report.Formats is null||report.Formats.Any(f=>f is null||f.Preserved is null||f.Lost is null||f.Conditional is null)||
               !formats.SetEquals(report.Formats.Select(f=>f.Format)))
                throw new InvalidDataException("Exchange report differs from delivery manifest.");
        }
        foreach(var file in manifest.Files)
        {
            token.ThrowIfCancellationRequested();var entry=zip.GetEntry(file.Name)??throw new InvalidDataException("Missing delivery file.");
            if(entry.Length!=file.Bytes)throw new InvalidDataException("Delivery file size mismatch.");
            await using var stream=entry.Open();
            if(!StringComparer.Ordinal.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,token)),file.Sha256))throw new InvalidDataException("Delivery file hash mismatch: "+file.Name);
        }
        return manifest;
    }
}
