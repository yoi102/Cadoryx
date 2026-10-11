using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.IO;

/// <summary>Read-only snapshot measurement with one atomic output archive.
/// Re-import differences are report content, not an import success guarantee.</summary>
public sealed class ExchangeMeasurementService(IGeometryKernel kernel)
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private static string[] Formats(DeliveryOptions options)=>new[]{(options.IncludeStep,"STEP"),(options.IncludeIges,"IGES"),(options.IncludeStl,"STL")}
        .Where(p=>p.Item1).Select(p=>p.Item2).ToArray();
    public async Task<DeliveryResult> WriteAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,DeliveryOptions options,
        bool overwrite=false,IProgress<DeliveryProgress>? progress=null,CancellationToken token=default)
    {
        options.Validate();snapshot.Validate();token.ThrowIfCancellationRequested();
        if(options.Output!=DeliveryOutput.ExchangeAudit||Formats(options).Length==0)throw new CadValidationException("Select at least one exchange format for measurement.");
        if(kernel is not IExchangeObservationService observer)throw new CadValidationException("The kernel has no native exchange observation service.");
        path=Path.GetFullPath(path);if(!Path.GetExtension(path).Equals(".zip",StringComparison.OrdinalIgnoreCase))throw new CadValidationException("Exchange measurement requires a .zip output.");
        if(!overwrite&&File.Exists(path))throw new IOException("Output exists; replacement was not requested.");
        var leases=new List<IAssetLease>();string? work=null;
        try
        {
            foreach(var id in snapshot.ReferencedAssets()){token.ThrowIfCancellationRequested();leases.Add(assets.Acquire(id));}
            return await Task.Run(async()=>
            {
                string directory=Path.GetDirectoryName(path)!;Directory.CreateDirectory(directory);
                work=Path.Combine(directory,".cadoryx-exchange-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
                string[] formats=Formats(options);int total=2+formats.Length*2,completed=0;
                void Stage(string stage){token.ThrowIfCancellationRequested();progress?.Report(new(stage,completed++,total));token.ThrowIfCancellationRequested();}
                Stage("Source observation");var source=await observer.ObserveExchangeAsync(snapshot,assets,options.VisibleOnly,token);
                var measured=ImmutableArray.CreateBuilder<ExchangeMeasuredFormat>();var diagnostics=ImmutableArray.CreateBuilder<CadDiagnostic>();
                var exportOptions=new CadExportOptions(options.LinearDeflectionMm,options.AngularDeflectionRad,VisibleOnly:options.VisibleOnly);
                foreach(string format in formats)
                {
                    string entry="export."+format.ToLowerInvariant(),file=Path.Combine(work,entry);Stage("Export "+format);
                    var exported=await kernel.ExportAsync(snapshot,assets,file,exportOptions,token);diagnostics.AddRange(exported.Diagnostics);
                    string before=await Hash(file,token);Stage("Re-import "+format);
                    using var reopened=await kernel.ImportAsync(file,assets,token);
                    var observed=await observer.ObserveExchangeAsync(reopened.Snapshot,assets,false,token);string after=await Hash(file,token);
                    if(before!=after)throw new IOException("The measured exchange file changed during re-import.");
                    measured.Add(new(format,entry,new FileInfo(file).Length,after,observed,exported.Diagnostics,reopened.Diagnostics,
                        ExchangeMeasuredReport.Compare(source,observed,format,exportOptions)));
                }
                var report=new ExchangeMeasuredReport(1,snapshot.Id.ToString(),snapshot.StateId.ToString(),DateTimeOffset.UtcNow,
                    kernel.Version,ExchangeMeasuredReport.LocalBasis,options,[..snapshot.ReferencedAssets().Select(a=>a.Sha256).Distinct().Order()],source,measured.ToImmutable(),
                    ["External CAD reader/writer results","Per-face color assignment, transparency and full style semantics","Source PMI inventory and semantic PMI preservation",
                     "Complete geometric equivalence or maximum mesh approximation error","Feature history, sketch constraints, drawing sheets and assembly mate transfer"]);
                await File.WriteAllTextAsync(Path.Combine(work,"report.json"),JsonSerializer.Serialize(report,Json),token);
                Stage("Archive and verify");string archive=Path.Combine(work,"output.zip");
                using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create))
                    foreach(string entryName in measured.Select(m=>m.Entry).Prepend("report.json"))
                    {
                        token.ThrowIfCancellationRequested();var entry=zip.CreateEntry(entryName,CompressionLevel.Fastest);
                        await using var input=File.OpenRead(Path.Combine(work,entryName));await using var output=entry.Open();await input.CopyToAsync(output,token);
                    }
                await VerifyAsync(archive,token);token.ThrowIfCancellationRequested();File.Move(archive,path,overwrite);
                diagnostics.Add(new("EXCHANGE.MEASURED",ExchangeMeasuredReport.LocalBasis));
                progress?.Report(new("Ready",total,total));
                return new DeliveryResult(path,snapshot.Id,snapshot.StateId,source.BodyInstances,diagnostics.ToImmutable());
            },token).ConfigureAwait(false);
        }
        finally{try{if(work is not null&&Directory.Exists(work))Directory.Delete(work,true);}finally{foreach(var lease in leases)lease.Dispose();}}
    }
    private static async Task<string> Hash(string path,CancellationToken token)
    {await using var input=File.OpenRead(path);return Convert.ToHexStringLower(await SHA256.HashDataAsync(input,token));}
    public static async Task<ExchangeMeasuredReport> VerifyAsync(string path,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();using var zip=ZipFile.OpenRead(path);
        var allowed=new HashSet<string>(StringComparer.Ordinal){"report.json","export.step","export.iges","export.stl"};
        if(zip.Entries.Count is <2 or >4||zip.Entries.Select(e=>e.FullName).Distinct(StringComparer.Ordinal).Count()!=zip.Entries.Count||
            zip.Entries.Any(e=>!allowed.Contains(e.FullName)||e.Length<0||e.Length>8L*1024*1024*1024)||zip.Entries.Sum(e=>e.Length)>16L*1024*1024*1024)
            throw new InvalidDataException("Invalid exchange measurement entries or sizes.");
        var entry=zip.GetEntry("report.json")??throw new InvalidDataException("Missing measurement report.");
        if(entry.Length>4*1024*1024)throw new InvalidDataException("Measurement report is too large.");
        ExchangeMeasuredReport report;
        try
        {
            await using var input=entry.Open();
            report=await JsonSerializer.DeserializeAsync<ExchangeMeasuredReport>(input,Json,token)??throw new InvalidDataException("Empty measurement report.");
        }
        catch(JsonException ex){throw new InvalidDataException("Invalid measurement report JSON.",ex);}
        if(report.SchemaVersion!=1||report.Basis!=ExchangeMeasuredReport.LocalBasis||string.IsNullOrWhiteSpace(report.Kernel)||report.Options is null||
            !Guid.TryParse(report.DocumentId,out var document)||document==Guid.Empty||!Guid.TryParse(report.StateId,out var state)||state==Guid.Empty||
            report.Formats.IsDefaultOrEmpty||report.Formats.Any(m=>m is null)||
            report.SourceAssetHashes.IsDefaultOrEmpty||report.SourceAssetHashes.Any(h=>!IsHash(h))||
            !report.SourceAssetHashes.SequenceEqual(report.SourceAssetHashes.Distinct().Order())||
            report.NotMeasured.IsDefaultOrEmpty||report.NotMeasured.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Invalid exchange measurement report identity or basis.");
        report.Options.Validate();ValidateObservation(report.Source);
        var expected=Formats(report.Options);
        if(report.Options.Output!=DeliveryOutput.ExchangeAudit||report.Formats.Length!=expected.Length||
            !report.Formats.Select(m=>m.Format).Order().SequenceEqual(expected.Order())||zip.Entries.Count!=report.Formats.Length+1)
            throw new InvalidDataException("Measurement formats differ from options.");
        foreach(var measured in report.Formats)
        {
            token.ThrowIfCancellationRequested();ValidateObservation(measured.Reopened);
            if(measured.Entry!="export."+measured.Format.ToLowerInvariant()||!IsHash(measured.Sha256)||measured.Bytes<=0||
                measured.ExportDiagnostics.IsDefault||measured.ImportDiagnostics.IsDefault||measured.ExportDiagnostics.Any(d=>d is null)||measured.ImportDiagnostics.Any(d=>d is null)||
                measured.Comparisons.IsDefaultOrEmpty||measured.Comparisons.Any(c=>c is null)||
                !measured.Comparisons.SequenceEqual(ExchangeMeasuredReport.Compare(report.Source,measured.Reopened,measured.Format,
                    new(report.Options.LinearDeflectionMm,report.Options.AngularDeflectionRad,VisibleOnly:report.Options.VisibleOnly))))
                throw new InvalidDataException("Invalid measured comparisons or entry.");
            var payload=zip.GetEntry(measured.Entry)??throw new InvalidDataException("Missing measured exchange file.");
            if(payload.Length!=measured.Bytes)throw new InvalidDataException("Measured exchange size mismatch.");
            await using var input=payload.Open();if(Convert.ToHexStringLower(await SHA256.HashDataAsync(input,token))!=measured.Sha256)
                throw new InvalidDataException("Measured exchange hash mismatch.");
        }
        return report;
    }
    private static bool IsHash(string? value)=>value is {Length:64}&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidateObservation(ExchangeModelObservation? value)
    {
        if(value is null||value.BodyInstances is <1 or >256||value.PartDefinitions is <1 or >256||value.SolidBodies<0||value.OtherBodies<0||
            value.SolidBodies+value.OtherBodies!=value.BodyInstances||value.TopologyFaces is <0 or >250000||value.TopologyEdges<0||
            value.PartReuseCounts.IsDefaultOrEmpty||value.PartReuseCounts.Length!=value.PartDefinitions||value.PartReuseCounts.Any(n=>n<1)||
            value.Occurrences.IsDefaultOrEmpty||value.Occurrences.Length>4096||value.OverallColorPalette.IsDefaultOrEmpty||value.SourceStyleColorPalette.IsDefault||value.EffectiveColorPalette.IsDefaultOrEmpty||
            !CanonicalPalette(value.OverallColorPalette)||!CanonicalPalette(value.SourceStyleColorPalette)||!CanonicalPalette(value.EffectiveColorPalette))
            throw new InvalidDataException("Invalid exchange observation counts.");
        value.BoundsMm.Validate();CadGuard.Finite(value.SurfaceAreaMm2,value.KnownSolidVolumeMm3);
        if(value.SurfaceAreaMm2<0||value.KnownSolidVolumeMm3<0)throw new InvalidDataException("Invalid exchange mass properties.");
        var groups=new Dictionary<int,ExchangeOccurrenceObservation>();var reuse=new Dictionary<int,int>();
        long bodies=0;
        for(int i=0;i<value.Occurrences.Length;i++)
        {
            var item=value.Occurrences[i];if(item is null||item.Order!=i||item.Depth is <1 or >128||item.DefinitionGroup<0||item.BodyCount is <0 or >256||
                string.IsNullOrWhiteSpace(item.Name)||string.IsNullOrWhiteSpace(item.DefinitionName))throw new InvalidDataException("Invalid exchange graph observation.");
            item.WorldTransform.Validate();
            if((i==0&&item.Depth!=1)||(i>0&&item.Depth>value.Occurrences[i-1].Depth+1)||
                (item.Assembly?item.BodyCount!=0:item.BodyCount<1))throw new InvalidDataException("Invalid exchange occurrence depth or body count.");
            if(groups.TryGetValue(item.DefinitionGroup,out var definition))
            {
                if(definition.DefinitionName!=item.DefinitionName||definition.Assembly!=item.Assembly||definition.BodyCount!=item.BodyCount)
                    throw new InvalidDataException("Inconsistent observed definition group.");
            }
            else
            {
                if(item.DefinitionGroup!=groups.Count)throw new InvalidDataException("Noncanonical observed definition groups.");
                groups.Add(item.DefinitionGroup,item);
            }
            bodies+=item.BodyCount;
            if(!item.Assembly)reuse[item.DefinitionGroup]=reuse.GetValueOrDefault(item.DefinitionGroup)+1;
        }
        if(bodies!=value.BodyInstances||reuse.Count!=value.PartDefinitions||!value.PartReuseCounts.SequenceEqual(reuse.Values.Order()))
            throw new InvalidDataException("Observation graph and summary counts differ.");
    }
    private static bool CanonicalPalette(ImmutableArray<uint> palette)=>palette.SequenceEqual(palette.Distinct().Order());
}
