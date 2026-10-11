using System.Collections.Immutable;
using System.Text.Json;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackGuidedModelingReports([property:Key(0)] PackGuidedModelingReportEntry[] Entries);
[MessagePackObject]
public sealed record PackGuidedModelingReportEntry([property:Key(0)] Guid Feature,[property:Key(1)] byte[] ReportJson);
public sealed record GuidedModelingReportEntry(FeatureId Feature,GuidedModelingReport Report);

/// <summary>Independent report section. Legacy feature/author encodings retain
/// their versions; missing legacy reports remain unknown. MessagePack1 wraps
/// owned bounded UTF-8 report JSON1, including the complete as-built author recipe.</summary>
public static class GuidedModelingReportStorage
{
    public const string Kind="guided-modeling-reports";
    public const string Capability="cadoryx.guided-modeling-reports.1";
    public static SectionFormat Format{get;}=new(Kind,1,"messagepack");
    private static readonly MessagePackSerializerOptions Pack=MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);
    public static byte[] EncodeMessagePack(IEnumerable<FeatureDefinition> features)
    {
        var entries=features.Where(f=>f.GuidedModelingReport is not null).OrderBy(f=>f.Id.Value).Select(f=>
        {
            f.GuidedModelingReport!.ValidateFor(f.Result);
            return new PackGuidedModelingReportEntry(f.Id.Value,JsonSerializer.SerializeToUtf8Bytes(f.GuidedModelingReport,CadJson.Options));
        }).ToArray();
        return MessagePackSerializer.Serialize(new PackGuidedModelingReports(entries),Pack);
    }
    public static byte[] EncodeJson(IEnumerable<FeatureDefinition> features)=>JsonSerializer.SerializeToUtf8Bytes(
        features.Where(f=>f.GuidedModelingReport is not null).OrderBy(f=>f.Id.Value).Select(f=>new GuidedModelingReportEntry(f.Id,f.GuidedModelingReport!)).ToArray(),CadJson.Options);
    public static ImmutableDictionary<FeatureId,FeatureDefinition> Attach(ReadOnlyMemory<byte> bytes,string encoding,int version,
        ImmutableDictionary<FeatureId,FeatureDefinition> features)
    {
        if(version!=1||encoding is not ("messagepack" or "json"))throw new NotSupportedException("Unsupported guided modeling report section version or encoding.");
        GuidedModelingReportEntry[] entries;
        if(encoding=="json")entries=JsonSerializer.Deserialize<GuidedModelingReportEntry[]>(bytes.Span,CadJson.Options)??throw new InvalidDataException("Missing guided modeling report entries.");
        else
        {
            var packed=MessagePackSerializer.Deserialize<PackGuidedModelingReports>(bytes,Pack);
            if(packed.Entries is null||packed.Entries.Any(e=>e is null||e.ReportJson is null))throw new InvalidDataException("Missing copied guided modeling report data.");
            entries=packed.Entries.Select(e=>new GuidedModelingReportEntry(new(e.Feature),
                JsonSerializer.Deserialize<GuidedModelingReport>(e.ReportJson,CadJson.Options)??throw new InvalidDataException("Missing guided modeling report."))).ToArray();
        }
        if(entries.Length==0||entries.Any(e=>e is null||e.Feature.Value==Guid.Empty||e.Report is null)||entries.Select(e=>e.Feature).Distinct().Count()!=entries.Length)
            throw new InvalidDataException("Empty, duplicate or malformed guided modeling report entries.");
        foreach(var entry in entries)
        {
            if(!features.TryGetValue(entry.Feature,out var feature))throw new InvalidDataException("A guided modeling report refers to a missing feature.");
            try{entry.Report.ValidateFor(feature.Result);}
            catch(CadValidationException error){throw new InvalidDataException("Invalid guided modeling observations or generated-result context.",error);}
            features=features.SetItem(feature.Id,feature with{GuidedModelingReport=entry.Report});
        }
        return features;
    }
}
