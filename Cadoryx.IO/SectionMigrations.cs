using System.Collections.Immutable;
using System.Text.Json;

namespace Cadoryx.IO;

/// <summary>Deterministic migrations over declared section contracts, committed as an atomic section set.</summary>
public sealed partial class CadSectionMigrationRegistry
{
    private readonly List<Migration> sectionMigrations=[];
    private readonly object gate=new();
    public static ImmutableDictionary<string,SectionFormat> CurrentFormats {get;}=new[]
    {
        new SectionFormat("document",5,"messagepack"),new("structure",3,"messagepack"),new("features",5,"messagepack"),
        new("presentation",2,"messagepack"),new("geometry",1,"messagepack"),new("sketches",2,"messagepack"),new("topology",1,"messagepack"),new("history",2,"messagepack")
    }.ToImmutableDictionary(x=>x.Kind,StringComparer.Ordinal);

    public CadSectionMigrationRegistry(bool includeBuiltIns=true)
    {
        if(!includeBuiltIns)return;
        foreach(string kind in new[]{"document","structure","features","presentation"})
        {
            string k=kind;
            RegisterStep("json-to-messagepack-"+k,[new(k,1,"json")],[new(k,2,"messagepack")],input=>
            {
                object dto=k switch
                {
                    "document"=>Json<DocumentSection>(input[k]),"structure"=>Json<StructureSection>(input[k]),
                    "features"=>Json<FeaturesSection>(input[k]),"presentation"=>Json<PresentationSection>(input[k]),_=>throw new NotSupportedException(k)
                };
                return [new(new(k,2,"messagepack"),MessagePackSections.Encode(k,dto))];
            });
        }
        RegisterStep("extract-geometry-table",[new("structure",2,"messagepack"),new("features",2,"messagepack")],
            [CurrentFormats["structure"],new("features",3,"messagepack"),CurrentFormats["geometry"]],input=>
                MessagePackSections.SplitGeometry(MessagePackSections.Decode<StructureSection>("structure",input["structure"].Bytes),
                    MessagePackSections.Decode<FeaturesSection>("features",input["features"].Bytes),featureVersion:3));
        // Document v3 requires an explicit sketch registry, including when empty. Old documents had none.
        RegisterStep("introduce-sketch-registry",[new("document",2,"messagepack")],[new("document",3,"messagepack"),new("sketches",1,"messagepack")],input=>
            [new(new("document",3,"messagepack"),input["document"].Bytes),new(new("sketches",1,"messagepack"),MessagePackSections.EncodeSketches([]))]);
        RegisterStep("introduce-topology-references",[new("document",3,"messagepack")],[new("document",4,"messagepack"),CurrentFormats["topology"]],input=>
            [new(new("document",4,"messagepack"),input["document"].Bytes),new(CurrentFormats["topology"],MessagePackSections.EncodeTopology([]))]);
        RegisterStep("introduce-topology-history",[new("document",4,"messagepack")],[CurrentFormats["document"],new("history",1,"messagepack")],input=>
            [new(CurrentFormats["document"],input["document"].Bytes),new(new("history",1,"messagepack"),MessagePackSections.EncodeHistories([]))]);
        RegisterStep("history-source-arguments",[new("history",1,"messagepack")],[CurrentFormats["history"]],input=>
            [new(CurrentFormats["history"],MessagePackSections.UpgradeHistorySources(input["history"].Bytes))]);
        RegisterStep("sketch-revisions",[new("sketches",1,"messagepack")],[CurrentFormats["sketches"]],input=>
            [new(CurrentFormats["sketches"],MessagePackSections.UpgradeSketchRevisions(input["sketches"].Bytes))]);
        RegisterStep("sketch-feature-references",[new("features",3,"messagepack")],[new("features",4,"messagepack")],input=>
            [new(new("features",4,"messagepack"),MessagePackSections.UpgradeSketchFeatureReferences(input["features"].Bytes))]);
        RegisterStep("local-box-edge-recipes",[new("features",4,"messagepack")],[CurrentFormats["features"]],input=>
            [new(CurrentFormats["features"],MessagePackSections.UpgradeLocalFeatures(input["features"].Bytes))]);
    }
    private static T Json<T>(SectionPayload input)=>JsonSerializer.Deserialize<T>(input.Bytes.Span,CadJson.Options)??throw new InvalidDataException("Null JSON section.");

    public void RegisterStep(string name,IEnumerable<SectionFormat> inputs,IEnumerable<SectionFormat> outputs,
        Func<IReadOnlyDictionary<string,SectionPayload>,IReadOnlyList<SectionPayload>> migrate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);ArgumentNullException.ThrowIfNull(migrate);
        var from=inputs.ToImmutableArray();var to=outputs.ToImmutableArray();
        if(from.IsEmpty||to.IsEmpty||from.Select(f=>f.Kind).Distinct().Count()!=from.Length||to.Select(f=>f.Kind).Distinct().Count()!=to.Length||
            from.Concat(to).Any(f=>string.IsNullOrWhiteSpace(f.Kind)||f.Version<1||string.IsNullOrWhiteSpace(f.Encoding)))
            throw new ArgumentException("Invalid migration contracts.");
        foreach(var target in to)
            if(from.FirstOrDefault(f=>f.Kind==target.Kind) is {Kind:not null} source&&target.Version<=source.Version)
                throw new ArgumentException("A migrated section must advance its schema version.");
        lock(gate)
        {
            if(sectionMigrations.Any(m=>m.Name==name||m.Inputs.Intersect(from).Any()))
                throw new ArgumentException("Ambiguous or duplicate migration source contract.");
            sectionMigrations.Add(new(name,from,to,migrate));
        }
    }

    public IReadOnlyDictionary<string,SectionPayload> Migrate(IEnumerable<SectionPayload> sections,
        IReadOnlyDictionary<string,SectionFormat> targets,int maxSectionBytes=32*1024*1024,long maxTotalBytes=128L*1024*1024,
        CancellationToken cancellationToken=default)
    {
        Migration[] rules;lock(gate)rules=sectionMigrations.ToArray();
        var values=sections.ToImmutableDictionary(p=>p.Format.Kind,StringComparer.Ordinal);
        ValidateBudget(values.Values);
        for(int iteration=0;iteration<64;iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(targets.All(t=>values.TryGetValue(t.Key,out var value)&&value.Format==t.Value))return values;
            var applicable=rules.Where(m=>m.Inputs.All(f=>values.TryGetValue(f.Kind,out var p)&&p.Format==f)).ToArray();
            if(applicable.Length==0)throw new NotSupportedException("Missing migration path for: "+string.Join(", ",targets.Values.Where(t=>!values.TryGetValue(t.Kind,out var p)||p.Format!=t)));
            var rule=applicable[0];
            if(rule.Outputs.Any(f=>values.ContainsKey(f.Kind)&&!rule.Inputs.Any(i=>i.Kind==f.Kind)))
                throw new InvalidDataException("Migration output collides with an existing section.");
            var input=rule.Inputs.ToImmutableDictionary(f=>f.Kind,f=>values[f.Kind],StringComparer.Ordinal);
            var result=rule.Apply(input)??throw new InvalidDataException("Migration returned null.");
            cancellationToken.ThrowIfCancellationRequested();
            if(result.Count!=rule.Outputs.Length||!result.Select(p=>p.Format).ToHashSet().SetEquals(rule.Outputs))
                throw new InvalidDataException("Migration violated its output contract: "+rule.Name);
            var staged=values.RemoveRange(rule.Inputs.Select(f=>f.Kind)).AddRange(result.Select(p=>KeyValuePair.Create(p.Format.Kind,p)));
            ValidateBudget(staged.Values);values=staged;
        }
        throw new NotSupportedException("Migration step limit exceeded.");
        void ValidateBudget(IEnumerable<SectionPayload> payloads)
        {
            long total=0;
            foreach(var p in payloads)
            {
                if(p.Bytes.Length>maxSectionBytes)throw new InvalidDataException("Migrated section exceeds its limit.");
                total=checked(total+p.Bytes.Length);if(total>maxTotalBytes)throw new InvalidDataException("Migrated sections exceed their total limit.");
            }
        }
    }
    private sealed record Migration(string Name,ImmutableArray<SectionFormat> Inputs,ImmutableArray<SectionFormat> Outputs,
        Func<IReadOnlyDictionary<string,SectionPayload>,IReadOnlyList<SectionPayload>> Apply);
}
