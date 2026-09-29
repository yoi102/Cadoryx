using Cadoryx.Db;
using Cadoryx.Lang.Strings;
using System.Collections.Immutable;
namespace Cadoryx.Commands;

/// <summary>Reevaluates the changed feature and its dependent closure into an isolated candidate.</summary>
public sealed class RecomputeCommand(FeatureId featureId,GeometryRecipe recipe,SketchProfileReference? replacementSketchSource=null) : ICadDocumentCommand
{
    public string Name=>Strings.EditFeatureParameters;
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        var doc=context.Snapshot;var original=doc.Features[featureId];
        if(FeatureSuppression.Blocked(doc).Contains(featureId))
            throw new CadValidationException("Restore the suppressed feature chain before editing its parameters.");
        if(original.Recipe.GetType()!=recipe.GetType())throw new CadValidationException("Changing feature kind requires a new identity.");
        if(replacementSketchSource is not null)
        {
            if(original.SketchSource is null||recipe is not ExtrudeRecipe)
                throw new CadValidationException("Only an existing linked extrusion can change sketch source.");
            replacementSketchSource.Resolve(doc,original.PartId,recipe);
        }
        return FeatureRecompute.PrepareAsync(context,[featureId],new Dictionary<FeatureId,GeometryRecipe>{{featureId,recipe}},cancellationToken,
            replacementSketchSource is null?null:new Dictionary<FeatureId,SketchProfileReference>{{featureId,replacementSketchSource}});
    }
}

/// <summary>Suppresses a feature or restores it and recomputes its dependent chain.</summary>
public sealed class SetFeatureSuppressionCommand(FeatureId featureId,bool suppressed) : ICadDocumentCommand
{
    public string Name=>Strings.ResourceManager.GetString(suppressed?"SuppressFeature":"RestoreFeature",Strings.Culture)!;
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        if(!context.Snapshot.Features.TryGetValue(featureId,out var feature))
            throw new CadValidationException("Feature no longer exists.");
        if(feature.IsSuppressed==suppressed)
            return Task.FromResult(new PreparedDocumentEdit(context.Snapshot,[]));
        var staged=context.Snapshot with
        {
            Features=context.Snapshot.Features.SetItem(featureId,feature with{IsSuppressed=suppressed})
        };
        return FeatureRecompute.PrepareAsync(context with{Snapshot=staged},[featureId],null,cancellationToken,
            reconcileOutputs:true);
    }
}

internal static class FeatureRecompute
{
    internal static async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,IEnumerable<FeatureId> roots,
        IReadOnlyDictionary<FeatureId,GeometryRecipe>? replacements,CancellationToken cancellationToken,
        IReadOnlyDictionary<FeatureId,SketchProfileReference>? sketchSourceReplacements=null,bool reconcileOutputs=false)
    {
        var doc=context.Snapshot;var pending=roots.ToHashSet();
        bool added;
        do {added=false;foreach(var f in doc.Features.Values)if(f.Inputs.Any(pending.Contains))added|=pending.Add(f.Id);}while(added);
        if(pending.Any(id=>doc.Features[id].IsSuppressed&&doc.AssociatedSections.ContainsKey(id)))
            throw new CadValidationException("Detach the associated section before suppressing its feature.");
        if(pending.Any(id=>doc.Bodies.TryGetValue(doc.Features[id].OutputBodyId,out var body)?doc.Layers[body.LayerId].IsLocked:
            doc.Features[id].OutputMetadata is {} metadata&&doc.Layers[metadata.Layer].IsLocked))
            throw new CadValidationException(Strings.LayerLocked);
        var done=new HashSet<FeatureId>();var resources=new List<IDisposable>();
        try
        {
            while(done.Count<pending.Count)
            {
                var f=doc.Features.Values.FirstOrDefault(f=>pending.Contains(f.Id)&&!done.Contains(f.Id)&&f.Inputs.All(x=>!pending.Contains(x)||done.Contains(x)))
                    ??throw new CadValidationException("Cyclic recompute graph.");
                cancellationToken.ThrowIfCancellationRequested();
                void Freeze()
                {
                    doc=doc with{Features=doc.Features.SetItem(f.Id,f with{IsStale=true,TopologyHistory=null})};
                    done.Add(f.Id);
                }
                if(f.IsSuppressed||f.Inputs.Any(input=>doc.Features[input].IsStale)){Freeze();continue;}
                GeometryRecipe current=replacements?.GetValueOrDefault(f.Id)??f.Recipe;
                var sketchSource=sketchSourceReplacements?.GetValueOrDefault(f.Id)??f.SketchSource;
                if(sketchSource is not null)
                {
                    current=sketchSource.Resolve(doc,f.PartId,current,refresh:true);
                    sketchSource=sketchSource with{Revision=doc.Sketches[sketchSource.SketchId].Revision};
                }
                if(current is BooleanRecipe boolean && f.Inputs.Length==boolean.Inputs.Length)
                    current=boolean with {Inputs=f.Inputs.Select(id=>doc.Features[id].Result).ToImmutableArray()};
                if(current is TransformRecipe transform && f.Inputs.Length==1)
                    current=transform with {Source=doc.Features[f.Inputs[0]].Result};
                if(current is LocalFeatureRecipe local&&f.Inputs.Length==1)
                    current=local with{Source=doc.Features[f.Inputs[0]].Result,Box=doc.Features[f.Inputs[0]].Recipe as BoxRecipe??throw new CadValidationException("Local feature requires a box source.")};
                if(current is HistoryFilletRecipe or HistoryChamferRecipe)
                {
                    var binding=f.TopologyBinding??throw new CadValidationException("Bound local feature has no selection.");
                    if(f.Inputs.Length!=1||f.Inputs[0]!=binding.TargetFeatureId)throw new CadValidationException("Bound local feature dependency changed.");
                    var upstream=doc.Features[binding.TargetFeatureId];
                    if(upstream.Result.Revision!=binding.TargetRevision||upstream.Result.AssetId!=binding.TargetAsset)
                    {Freeze();continue;}
                    if(binding.Origin is {} origin)
                        await FeatureBindingGuard.ConfirmAsync(context with{Snapshot=doc},origin,
                            binding.TargetFeatureId,new(binding.TargetRevision,binding.TargetAsset,binding.FullTopologyIndex,HistoryShapeKind.Edge,binding.AdapterVersion),cancellationToken);
                    else await FeatureBindingGuard.ConfirmExactAsync(context with{Snapshot=doc},binding.ExactEdge!,binding.SupportFace,cancellationToken);
                    if(binding.SupportFace is {} face&&binding.Origin is not null)
                        await FeatureBindingGuard.WithFaceAsync(context with{Snapshot=doc},binding,face,cancellationToken);
                    current=current switch
                    {
                        HistoryFilletRecipe fillet=>fillet with{Source=upstream.Result,FullTopologyIndex=binding.FullTopologyIndex},
                        HistoryChamferRecipe chamfer=>chamfer with{Source=upstream.Result,FullTopologyIndex=binding.FullTopologyIndex,
                            SupportFaceIndex=binding.SupportFace!.FullTopologyIndex},
                        _=>current
                    };
                }
                var result=await context.Kernel.EvaluateAsync(current,context.Assets,cancellationToken).ConfigureAwait(false);
                resources.Add(result);
                doc=doc with {Features=doc.Features.SetItem(f.Id,f with {Recipe=current,Result=result.Geometry,SketchSource=sketchSource,TopologyHistory=result.TopologyHistory,IsStale=false})};
                var part=(PartDefinition)doc.Definitions[f.PartId];
                bool terminal=!doc.Features.Values.Any(other=>other.Inputs.Contains(f.Id));
                if(doc.Bodies.TryGetValue(f.OutputBodyId,out var body))
                {
                    doc=doc with{Features=doc.Features.SetItem(f.Id,doc.Features[f.Id] with{OutputMetadata=BodyOutputMetadata.FromBody(body)})};
                    if(result.Geometry.Kind==BodyKind.Empty)
                        doc=doc with {Bodies=doc.Bodies.Remove(body.Id),Definitions=doc.Definitions.SetItem(part.Id,part with{Bodies=part.Bodies.Remove(body.Id)})};
                    else doc=doc with {Bodies=doc.Bodies.SetItem(body.Id,body with {Geometry=result.Geometry})};
                }
                else if(terminal&&result.Geometry.Kind!=BodyKind.Empty)
                {
                    var metadata=f.OutputMetadata??new(f.Name,doc.Layers.Keys.OrderBy(x=>x.Value).First(),new(),true,null);
                    var restored=new CadBody(f.OutputBodyId,f.PartId,metadata.Name,result.Geometry,f.Id,metadata.Layer,metadata.Appearance,metadata.Visible,metadata.Material);
                    doc=doc with {Bodies=doc.Bodies.Add(restored.Id,restored),Definitions=doc.Definitions.SetItem(part.Id,part with{Bodies=part.Bodies.Add(restored.Id)})};
                }
                done.Add(f.Id);
            }
            if(reconcileOutputs||pending.Any(id=>doc.Features[id].IsSuppressed))
            {
                var touched=pending.ToHashSet();
                do
                {
                    added=false;
                    foreach(var id in touched.ToArray())
                        foreach(var input in doc.Features[id].Inputs)added|=touched.Add(input);
                }while(added);
                doc=ReconcileFeatureBodies(doc,touched);
            }
            return new(doc.WithNewState(),resources);
        }
        catch{foreach(var resource in resources)resource.Dispose();throw;}
    }

    private static DocumentSnapshot ReconcileFeatureBodies(DocumentSnapshot document,HashSet<FeatureId> touched)
    {
        foreach(var partId in touched.Select(id=>document.Features[id].PartId).Distinct())
        {
            var part=(PartDefinition)document.Definitions[partId];
            foreach(var featureId in part.Features.Where(touched.Contains))
            {
                var feature=document.Features[featureId];
                bool active=!feature.IsSuppressed&&!feature.IsStale&&feature.Result.Kind!=BodyKind.Empty;
                bool hasActiveConsumer=document.Features.Values.Any(other=>other.PartId==partId&&
                    !other.IsSuppressed&&!other.IsStale&&other.Inputs.Contains(featureId));
                bool show=active&&!hasActiveConsumer;
                if(document.Bodies.TryGetValue(feature.OutputBodyId,out var body))
                {
                    document=document with{Features=document.Features.SetItem(featureId,feature with
                        {OutputMetadata=BodyOutputMetadata.FromBody(body)})};
                    if(!show)
                    {
                        document=document with{Bodies=document.Bodies.Remove(body.Id),
                            Definitions=document.Definitions.SetItem(partId,part with{Bodies=part.Bodies.Remove(body.Id)})};
                        part=(PartDefinition)document.Definitions[partId];
                    }
                }
                else if(show)
                {
                    var metadata=feature.OutputMetadata??new BodyOutputMetadata(feature.Name,
                        document.Layers.Keys.OrderBy(x=>x.Value).First(),new(),true,null);
                    var restored=new CadBody(feature.OutputBodyId,partId,metadata.Name,feature.Result,
                        feature.Id,metadata.Layer,metadata.Appearance,metadata.Visible,metadata.Material);
                    document=document with{Bodies=document.Bodies.Add(restored.Id,restored),
                        Definitions=document.Definitions.SetItem(partId,part with{Bodies=part.Bodies.Add(restored.Id)})};
                    part=(PartDefinition)document.Definitions[partId];
                }
            }
        }
        return document;
    }
}
