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
        new SectionFormat("document",16,"messagepack"),new("structure",3,"messagepack"),new("features",19,"messagepack"),
        new("presentation",2,"messagepack"),new("geometry",1,"messagepack"),new("sketches",6,"messagepack"),new("topology",1,"messagepack"),new("history",2,"messagepack"),new("history-queries",1,"messagepack"),new("feature-bindings",2,"messagepack"),new("external-parts",1,"messagepack")
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
        RegisterStep("introduce-topology-history",[new("document",4,"messagepack")],[new("document",5,"messagepack"),new("history",1,"messagepack")],input=>
            [new(new("document",5,"messagepack"),input["document"].Bytes),new(new("history",1,"messagepack"),MessagePackSections.EncodeHistories([]))]);
        RegisterStep("introduce-history-queries",[new("document",5,"messagepack")],[new("document",6,"messagepack"),CurrentFormats["history-queries"]],input=>
            [new(new("document",6,"messagepack"),input["document"].Bytes),new(CurrentFormats["history-queries"],MessagePackSections.EncodeHistoryQueries([]))]);
        RegisterStep("introduce-feature-bindings",[new("document",6,"messagepack")],[new("document",7,"messagepack"),CurrentFormats["feature-bindings"]],input=>
            [new(new("document",7,"messagepack"),input["document"].Bytes),new(CurrentFormats["feature-bindings"],MessagePackSections.EncodeFeatureBindings([]))]);
        RegisterStep("document-grid-settings",[new("document",7,"messagepack")],[new("document",8,"messagepack")],input=>
            [new(new("document",8,"messagepack"),MessagePackSections.UpgradeDocumentGrid(input["document"].Bytes))]);
        RegisterStep("document-background-color",[new("document",8,"messagepack")],[new("document",10,"messagepack")],input=>
            [new(new("document",10,"messagepack"),MessagePackSections.UpgradeDocumentBackground(input["document"].Bytes))]);
        RegisterStep("document-solid-background-to-gradient",[new("document",9,"messagepack")],[new("document",10,"messagepack")],input=>
            [new(new("document",10,"messagepack"),MessagePackSections.UpgradeSolidDocumentBackground(input["document"].Bytes))]);
        RegisterStep("document-origin-axes",[new("document",10,"messagepack")],[new("document",11,"messagepack")],input=>
            [new(new("document",11,"messagepack"),MessagePackSections.UpgradeDocumentOrigin(input["document"].Bytes))]);
        RegisterStep("document-work-plane",[new("document",11,"messagepack")],[new("document",12,"messagepack")],input=>
            [new(new("document",12,"messagepack"),MessagePackSections.UpgradeDocumentWorkPlane(input["document"].Bytes))]);
        RegisterStep("document-assembly-constraints",[new("document",12,"messagepack")],[new("document",13,"messagepack")],input=>
            [new(new("document",13,"messagepack"),MessagePackSections.UpgradeDocumentAssemblyConstraints(input["document"].Bytes))]);
        RegisterStep("document-assembly-axes",[new("document",13,"messagepack")],[new("document",14,"messagepack")],input=>
            [new(new("document",14,"messagepack"),MessagePackSections.UpgradeDocumentAssemblyAxes(input["document"].Bytes))]);
        RegisterStep("introduce-external-parts",[new("document",14,"messagepack")],
            [new("document",15,"messagepack"),CurrentFormats["external-parts"]],input=>
            [new(new("document",15,"messagepack"),input["document"].Bytes),
                new(CurrentFormats["external-parts"],MessagePackSections.EncodeExternalParts([]))]);
        RegisterStep("document-assembly-angle-and-plane",[new("document",15,"messagepack")],
            [CurrentFormats["document"]],input=>
            [new(CurrentFormats["document"],MessagePackSections.UpgradeDocumentAssemblyAngles(input["document"].Bytes))]);
        RegisterStep("feature-stale-state",[new("features",5,"messagepack")],[new("features",6,"messagepack")],input=>
            [new(new("features",6,"messagepack"),input["features"].Bytes)]);
        RegisterStep("local-chamfer-two-distances",[new("features",6,"messagepack")],[new("features",7,"messagepack")],input=>
            [new(new("features",7,"messagepack"),MessagePackSections.UpgradeLocalChamferTwoDistances(input["features"].Bytes))]);
        RegisterStep("local-multi-edge-and-variable-radius",[new("features",7,"messagepack")],[new("features",8,"messagepack")],input=>
            [new(new("features",8,"messagepack"),MessagePackSections.UpgradeLocalMultiEdgeAndVariableRadius(input["features"].Bytes))]);
        RegisterStep("bound-variable-radius",[new("features",8,"messagepack")],[new("features",9,"messagepack")],input=>
            [new(new("features",9,"messagepack"),MessagePackSections.UpgradeBoundVariableRadius(input["features"].Bytes))]);
        RegisterStep("bound-local-chamfer",[new("features",9,"messagepack")],[new("features",10,"messagepack")],input=>
            [new(new("features",10,"messagepack"),input["features"].Bytes)]);
        RegisterStep("circular-sketch-profiles",[new("features",10,"messagepack")],[new("features",11,"messagepack")],input=>
            [new(new("features",11,"messagepack"),MessagePackSections.UpgradeCircularSketchProfiles(input["features"].Bytes))]);
        RegisterStep("circular-sketch-holes",[new("features",11,"messagepack")],[new("features",12,"messagepack")],input=>
            [new(new("features",12,"messagepack"),MessagePackSections.UpgradeCircularHoles(input["features"].Bytes))]);
        RegisterStep("polygon-sketch-holes",[new("features",12,"messagepack")],[new("features",13,"messagepack")],input=>
            [new(new("features",13,"messagepack"),MessagePackSections.UpgradePolygonHoles(input["features"].Bytes))]);
        RegisterStep("arc-segment-features",[new("features",13,"messagepack")],[new("features",14,"messagepack")],input=>
            [new(new("features",14,"messagepack"),MessagePackSections.UpgradeArcSegmentFeatures(input["features"].Bytes))]);
        RegisterStep("mixed-curve-features",[new("features",14,"messagepack")],[new("features",15,"messagepack")],input=>
            [new(new("features",15,"messagepack"),MessagePackSections.UpgradeMixedCurveFeatures(input["features"].Bytes))]);
        RegisterStep("expanded-mixed-features",[new("features",15,"messagepack")],[new("features",16,"messagepack")],input=>
            [new(new("features",16,"messagepack"),MessagePackSections.UpgradeExpandedMixedFeatures(input["features"].Bytes))]);
        RegisterStep("single-level-islands",[new("features",16,"messagepack")],[new("features",17,"messagepack")],input=>
            [new(new("features",17,"messagepack"),MessagePackSections.UpgradeIslandFeatures(input["features"].Bytes))]);
        RegisterStep("quadratic-bezier-features",[new("features",17,"messagepack")],[new("features",18,"messagepack")],input=>
            [new(new("features",18,"messagepack"),MessagePackSections.UpgradeBezierFeatures(input["features"].Bytes))]);
        RegisterStep("cubic-spline-features",[new("features",18,"messagepack")],[CurrentFormats["features"]],input=>
            [new(CurrentFormats["features"],MessagePackSections.UpgradeSplineFeatures(input["features"].Bytes))]);
        RegisterStep("exact-local-bindings",[new("feature-bindings",1,"messagepack")],[CurrentFormats["feature-bindings"]],input=>
            [new(CurrentFormats["feature-bindings"],MessagePackSections.UpgradeExactFeatureBindings(input["feature-bindings"].Bytes))]);
        RegisterStep("history-source-arguments",[new("history",1,"messagepack")],[CurrentFormats["history"]],input=>
            [new(CurrentFormats["history"],MessagePackSections.UpgradeHistorySources(input["history"].Bytes))]);
        RegisterStep("sketch-revisions",[new("sketches",1,"messagepack")],[new("sketches",2,"messagepack")],input=>
            [new(new("sketches",2,"messagepack"),MessagePackSections.UpgradeSketchRevisions(input["sketches"].Bytes))]);
        RegisterStep("sketch-angular-constraints",[new("sketches",2,"messagepack")],[new("sketches",3,"messagepack")],input=>
            [new(new("sketches",3,"messagepack"),MessagePackSections.UpgradeAngularSketchConstraints(input["sketches"].Bytes))]);
        RegisterStep("three-point-sketch-arcs",[new("sketches",3,"messagepack")],[new("sketches",4,"messagepack")],input=>
            [new(new("sketches",4,"messagepack"),MessagePackSections.UpgradeSketchArcs(input["sketches"].Bytes))]);
        RegisterStep("quadratic-sketch-beziers",[new("sketches",4,"messagepack")],[new("sketches",5,"messagepack")],input=>
            [new(new("sketches",5,"messagepack"),MessagePackSections.UpgradeSketchBeziers(input["sketches"].Bytes))]);
        RegisterStep("cubic-sketch-splines",[new("sketches",5,"messagepack")],[CurrentFormats["sketches"]],input=>
            [new(CurrentFormats["sketches"],MessagePackSections.UpgradeSketchSplines(input["sketches"].Bytes))]);
        RegisterStep("sketch-feature-references",[new("features",3,"messagepack")],[new("features",4,"messagepack")],input=>
            [new(new("features",4,"messagepack"),MessagePackSections.UpgradeSketchFeatureReferences(input["features"].Bytes))]);
        RegisterStep("local-box-edge-recipes",[new("features",4,"messagepack")],[new("features",6,"messagepack")],input=>
            [new(new("features",6,"messagepack"),MessagePackSections.UpgradeLocalFeatures(input["features"].Bytes))]);
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
