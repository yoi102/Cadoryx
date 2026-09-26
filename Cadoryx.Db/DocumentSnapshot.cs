using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Immutable business state. All mutations are validated before a session publishes them.</summary>
public sealed record DocumentSnapshot(DocumentId Id,DocumentStateId StateId,string Name,DefinitionId RootAssemblyId,
    DocumentSettings Settings,ImmutableDictionary<DefinitionId,CadDefinition> Definitions,
    ImmutableDictionary<BodyId,CadBody> Bodies,ImmutableDictionary<FeatureId,FeatureDefinition> Features,
    ImmutableDictionary<LayerId,CadLayer> Layers,ImmutableDictionary<MaterialId,CadMaterial> Materials,
    ImmutableArray<PreservedSection> Extensions=default,ImmutableArray<AssetId> RetainedAssets=default,
    ImmutableDictionary<AssetId,AssetFormat>? RetainedAssetFormats=null)
{
    public ImmutableDictionary<SketchId,CadSketch> Sketches {get;init;}=ImmutableDictionary<SketchId,CadSketch>.Empty;
    public ImmutableDictionary<TopologyReferenceId,TopologyReference> TopologyReferences {get;init;}=ImmutableDictionary<TopologyReferenceId,TopologyReference>.Empty;
    public ImmutableDictionary<HistoryQueryId,HistoryQuery> HistoryQueries {get;init;}=ImmutableDictionary<HistoryQueryId,HistoryQuery>.Empty;
    public ImmutableDictionary<AssemblyConstraintId,AssemblyConstraint> AssemblyConstraints {get;init;}=
        ImmutableDictionary<AssemblyConstraintId,AssemblyConstraint>.Empty;
    public ImmutableDictionary<DefinitionId,ExternalPartLink> ExternalParts {get;init;}=
        ImmutableDictionary<DefinitionId,ExternalPartLink>.Empty;
    public static DocumentSnapshot Create(string name)
    {
        CadGuard.Name(name);var root=DefinitionId.New();var layer=LayerId.New();
        return new(DocumentId.New(),DocumentStateId.New(),name,root,new(),
            ImmutableDictionary<DefinitionId,CadDefinition>.Empty.Add(root,new AssemblyDefinition(root,name,[])),
            ImmutableDictionary<BodyId,CadBody>.Empty,ImmutableDictionary<FeatureId,FeatureDefinition>.Empty,
            ImmutableDictionary<LayerId,CadLayer>.Empty.Add(layer,new(layer,"Default")),ImmutableDictionary<MaterialId,CadMaterial>.Empty);
    }
    public IEnumerable<GeometryAssetRef> ReferencedGeometry()=>Bodies.Values.Select(x=>x.Geometry).Concat(Features.Values.SelectMany(x=>x.Recipe.AssetInputs.Append(x.Result)));
    public IEnumerable<AssetId> ReferencedAssets() => ReferencedGeometry()
        .SelectMany(g=>g.Source is {} s?new[]{g.AssetId,s.ContextAssetId}:new[]{g.AssetId})
        .Concat(Extensions.IsDefault?[]:Extensions.Select(x=>x.PayloadAssetId)).Concat(RetainedAssets.IsDefault?[]:RetainedAssets).Distinct();

    public DocumentSnapshot WithNewState() => this with { StateId=DocumentStateId.New() };
    public void Validate()
    {
        CadGuard.Id(Id);CadGuard.Id(StateId);CadGuard.Name(Name);Settings.Validate();
        foreach(var (id,reference) in TopologyReferences)
        {
            reference.Validate();
            if(id!=reference.Id||reference.DocumentId!=Id)throw new CadValidationException("Invalid topology reference ownership.");
            // Deleted producers remain diagnosable and can be restored by exact undo.
            if(Features.TryGetValue(reference.FeatureId,out var producer)&&producer.OutputBodyId!=reference.OutputBodyId)
                throw new CadValidationException("Topology output identity mismatch.");
        }
        foreach(var (id,query) in HistoryQueries)
        {
            query.Validate(Id);
            if(id!=query.Id)throw new CadValidationException("History query key mismatch.");
        }
        if(!Extensions.IsDefault)foreach(var extension in Extensions){CadGuard.Name(extension.Kind);extension.PayloadAssetId.Validate();if(extension.SchemaVersion<1)throw new CadValidationException("Invalid extension version.");}
        if(!RetainedAssets.IsDefault)foreach(var asset in RetainedAssets)asset.Validate();
        if(RetainedAssetFormats is not null)foreach(var (id,format) in RetainedAssetFormats){id.Validate();format.Validate();}
        if(!Definitions.TryGetValue(RootAssemblyId,out var root)||root is not AssemblyDefinition) throw new CadValidationException("Missing root assembly.");
        if(Layers.Count==0) throw new CadValidationException("A document requires a layer.");
        foreach(var (id,sketch) in Sketches)
        {
            sketch.Validate();
            if(id!=sketch.Id||!Definitions.TryGetValue(sketch.PartId,out var owner)||owner is not PartDefinition)
                throw new CadValidationException("Invalid sketch ownership.");
        }
        var slots=new HashSet<ComponentSlotId>();var ownedBodies=new HashSet<BodyId>();var ownedFeatures=new HashSet<FeatureId>();
        foreach(var (id,definition) in Definitions)
        {
            CadGuard.Id(id);CadGuard.Name(definition.Name);if(id!=definition.Id)throw new CadValidationException("Definition key mismatch.");
            if(definition is AssemblyDefinition a)
            {
                if(a.Children.IsDefault)throw new CadValidationException("Uninitialized children.");
                foreach(var child in a.Children)
                {
                    CadGuard.Id(child.Id);CadGuard.Name(child.Name);child.LocalTransform.Validate();
                    if(!slots.Add(child.Id)||!Definitions.ContainsKey(child.DefinitionId)||child.DefinitionId==RootAssemblyId)throw new CadValidationException("Invalid or duplicated assembly slot.");
                }
            }
            else if(definition is PartDefinition p)
            {
                if(p.Bodies.IsDefault||p.Features.IsDefault)throw new CadValidationException("Uninitialized part.");
                foreach(var body in p.Bodies)
                    if(!ownedBodies.Add(body)||!Bodies.TryGetValue(body,out var b)||b.PartId!=id)throw new CadValidationException("Invalid body ownership.");
                foreach(var feature in p.Features)
                    if(!ownedFeatures.Add(feature)||!Features.TryGetValue(feature,out var f)||f.PartId!=id)throw new CadValidationException("Invalid feature ownership.");
            }
            else throw new CadValidationException("Unknown definition type.");
        }
        if(ownedBodies.Count!=Bodies.Count || ownedFeatures.Count!=Features.Count)throw new CadValidationException("Orphan body or feature.");
        foreach(var (id,b) in Bodies)
        {
            CadGuard.Id(id);CadGuard.Name(b.Name);b.Geometry.Validate();
            if(b.Id!=id||!Layers.ContainsKey(b.LayerId)||(b.MaterialId is {} m&&!Materials.ContainsKey(m))||
                (b.Producer is {} f&&(!Features.TryGetValue(f,out var feature)||feature.OutputBodyId!=id||feature.Result!=b.Geometry)))
                throw new CadValidationException("Invalid body references.");
        }
        foreach(var (id,f) in Features)
        {
            CadGuard.Id(id);CadGuard.Id(f.OutputBodyId);CadGuard.Name(f.Name);f.Recipe.Validate();f.Result.Validate();
            f.SketchSource?.ValidateCache(this,f);
            f.TopologyHistory?.ValidateFor(f);
            if(f.IsStale&&f.TopologyHistory is not null)throw new CadValidationException("A stale feature cannot retain topology history.");
            if(f.Recipe is HistoryFilletRecipe or HistoryChamferRecipe)
            {
                var binding=f.TopologyBinding??throw new CadValidationException("Bound local feature has no confirmed binding.");
                binding.Validate(Id);
                var source=f.Recipe is HistoryFilletRecipe fillet?fillet.Source:((HistoryChamferRecipe)f.Recipe).Source;
                var edgeIndex=f.Recipe is HistoryFilletRecipe f1?f1.FullTopologyIndex:((HistoryChamferRecipe)f.Recipe).FullTopologyIndex;
                if(f.Inputs.Length!=1||f.Inputs[0]!=binding.TargetFeatureId||edgeIndex!=binding.FullTopologyIndex||
                    source.Revision!=binding.TargetRevision||source.AssetId!=binding.TargetAsset||
                    !Features.TryGetValue(binding.TargetFeatureId,out var target)||target.PartId!=f.PartId||
                    !f.IsStale&&(target.Result.Revision!=binding.TargetRevision||target.Result.AssetId!=binding.TargetAsset||target.IsStale))
                    throw new CadValidationException("Bound local feature differs from its target.");
                if(f.Recipe is HistoryChamferRecipe chamfer&&
                    (binding.SupportFace is null||chamfer.SupportFaceIndex!=binding.SupportFace.FullTopologyIndex)||
                    f.Recipe is HistoryFilletRecipe&&binding.SupportFace is not null)
                    throw new CadValidationException("Bound local feature support face differs from its recipe.");
            }
            else if(f.TopologyBinding is not null)throw new CadValidationException("Unexpected feature topology binding.");
            if(id!=f.Id||f.SchemaVersion!=1||f.Inputs.IsDefault||f.Inputs.Distinct().Count()!=f.Inputs.Length)throw new CadValidationException("Invalid feature.");
            if(f.OutputMetadata is {} metadata)
            {
                CadGuard.Name(metadata.Name);
                if(!Layers.ContainsKey(metadata.Layer)||metadata.Material is {} material&&!Materials.ContainsKey(material))throw new CadValidationException("Invalid retained output metadata.");
            }
            foreach(var input in f.Inputs)
                if(!Features.TryGetValue(input,out var upstream)||upstream.PartId!=f.PartId)throw new CadValidationException("Invalid feature dependency.");
            if(f.TopologyHistory is {} history)
                for(int argument=0;argument<f.Inputs.Length;argument++)
                {
                    var geometry=Features[f.Inputs[argument]].Result;var source=history.GetSource(argument);
                    if(geometry.Revision!=source.Revision||geometry.AssetId!=source.Asset)
                        throw new CadValidationException("History operand order differs from its upstream features.");
                }
            if(!f.IsStale&&f.Recipe is LocalFeatureRecipe local&&
                (f.Inputs.Length!=1||Features[f.Inputs[0]].Recipe is not BoxRecipe box||local.Box!=box||local.Source!=Features[f.Inputs[0]].Result))
                throw new CadValidationException("Local feature must reference its current upstream box.");
        }
        foreach(var (id,l) in Layers){CadGuard.Id(id);CadGuard.Name(l.Name);if(id!=l.Id)throw new CadValidationException("Layer key mismatch.");}
        foreach(var (id,m) in Materials){CadGuard.Id(id);CadGuard.Name(m.Name);CadGuard.Positive(m.DensityKgPerMm3);if(id!=m.Id)throw new CadValidationException("Material key mismatch.");}
        ValidateDag(Definitions.Keys,id=>Definitions[id] is AssemblyDefinition a?a.Children.Select(x=>x.DefinitionId):[]);
        ValidateDag(Features.Keys,id=>Features[id].Inputs);
        if(ExternalParts.Count>4096)throw new CadValidationException("Too many external part links.");
        foreach(var (id,link) in ExternalParts)
        {
            link.Validate();
            if(id!=link.TargetPartId||!Definitions.TryGetValue(id,out var definition)||
               definition is not PartDefinition part||!part.Features.IsEmpty||
               part.Bodies.Any(body=>Bodies[body].Producer is not null))
                throw new CadValidationException("External part must be a frozen part snapshot.");
        }
        if(AssemblyConstraints.Count>4096)throw new CadValidationException("Too many assembly constraints.");
        var constraintOccurrences=AssemblyConstraints.Count>0?EnumerateOccurrences().ToDictionary(o=>o.Path):null;
        foreach(var (id,constraint) in AssemblyConstraints)
        {
            if(id!=constraint.Id)throw new CadValidationException("Assembly constraint key mismatch.");
            var evaluation=constraint.Evaluate(this,constraintOccurrences!);
            if(constraint.Kind==AssemblyConstraintKind.Fixed&&evaluation.Status==AssemblyConstraintStatus.Unsatisfied)
                throw new CadValidationException("A fixed assembly instance cannot move until its constraint is disabled or removed.");
        }
    }
    private static void ValidateDag<T>(IEnumerable<T> keys,Func<T,IEnumerable<T>> children) where T:notnull
    {
        var done=new HashSet<T>();var active=new HashSet<T>();
        void Visit(T id,int depth)
        {
            if(depth>128)throw new CadValidationException("Graph depth exceeds 128.");
            if(done.Contains(id))return;
            if(!active.Add(id))throw new CadValidationException("Cyclic dependency.");
            foreach(var c in children(id))Visit(c,depth+1);
            active.Remove(id);done.Add(id);
        }
        foreach(var id in keys)Visit(id,0);
    }
    public IEnumerable<CadOccurrence> EnumerateOccurrences(int limit=100000)
    {
        int count=0;
        IEnumerable<CadOccurrence> Expand(DefinitionId id,OccurrencePath path,RigidTransform3d world,bool visible,CadAppearance? appearance,int depth)
        {
            if(depth>128)throw new CadValidationException("Assembly depth exceeds 128.");
            if(Definitions[id] is not AssemblyDefinition assembly)yield break;
            foreach(var slot in assembly.Children)
            {
                if(++count>limit)throw new CadValidationException("Expanded occurrence limit exceeded.");
                var p=path.Append(slot.Id);var t=world*slot.LocalTransform;var v=visible&&slot.IsVisible;var a=slot.AppearanceOverride??appearance;
                yield return new(p,slot.DefinitionId,slot.Name,t,v,a);
                foreach(var child in Expand(slot.DefinitionId,p,t,v,a,depth+1))yield return child;
            }
        }
        return Expand(RootAssemblyId,new(Id,[]),RigidTransform3d.Identity,true,null,0);
    }
}
