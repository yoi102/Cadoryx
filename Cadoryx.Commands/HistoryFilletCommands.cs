using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.Commands;

internal static class FeatureBindingGuard
{
    public static async Task<(FeatureTopologyBinding Binding,HistoryTarget Target)> ConfirmAsync(DocumentCommandContext context,
        TopologyReference origin,FeatureId targetId,HistoryTarget expected,CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var doc=context.Snapshot;origin.Validate();
        if(origin.DocumentId!=doc.Id||origin.Kind!=TopologyKind.Edge||origin.Policy!=TopologyRebindPolicy.Semantic||
            !doc.Features.TryGetValue(origin.FeatureId,out var source)||!doc.Features.TryGetValue(targetId,out var target)||
            source.PartId!=target.PartId||source.Id==target.Id||target.IsStale||target.Result.Kind==BodyKind.Empty)
            throw new CadValidationException("Select a current edge history path in the same part.");
        if(context.Kernel is not ITopologyHistoryResolver resolver)throw new NotSupportedException("Kernel has no history resolver.");
        var result=await resolver.TraceAsync(doc,origin,targetId,context.Assets,token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if(result.Status!=HistoryResolutionStatus.Resolved||result.Target is not {Kind:HistoryShapeKind.Edge} locator||
            locator.Revision!=target.Result.Revision||locator.Asset!=target.Result.AssetId)
            throw new CadValidationException("Edge history is not uniquely verified: "+result.Diagnostic);
        if(expected!=locator)throw new CadValidationException("Confirmed edge changed; analyze and confirm again.");
        var binding=new FeatureTopologyBinding(origin,targetId,locator.Revision,locator.Asset,locator.FullTopologyIndex,locator.AdapterVersion);
        binding.Validate(doc.Id);return (binding,locator);
    }
}

/// <summary>Creates a real fillet consuming a verified cross-feature edge. A diagnostic query alone does not authorize it.</summary>
public sealed class HistoryFilletCommand(TopologyReference origin,FeatureId target,double radius,HistoryTarget confirmedTarget) : ICadDocumentCommand
{
    public string Name=>Strings.Fillet;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        CadGuard.Positive(radius);
        var (binding,locator)=await FeatureBindingGuard.ConfirmAsync(context,origin,target,confirmedTarget,token);
        var doc=context.Snapshot;var producer=doc.Features[target];
        if(!doc.Bodies.TryGetValue(producer.OutputBodyId,out var source)||source.Producer!=target)
            throw new CadValidationException("Target feature has no current body output.");
        if(doc.Layers[source.LayerId].IsLocked)throw new CadValidationException(Strings.LayerLocked);
        var recipe=new HistoryFilletRecipe(producer.Result,locator.FullTopologyIndex,radius);
        var result=await context.Kernel.EvaluateAsync(recipe,context.Assets,token).ConfigureAwait(false);
        try
        {
            var id=FeatureId.New();var body=source with{Id=BodyId.New(),Producer=id,Name=Name,Geometry=result.Geometry};
            var part=(PartDefinition)doc.Definitions[source.PartId];
            var feature=new FeatureDefinition(id,part.Id,Name,recipe,[target],body.Id,result.Geometry,OutputMetadata:BodyOutputMetadata.FromBody(body))
                {TopologyBinding=binding,TopologyHistory=result.TopologyHistory};
            doc=doc with{Bodies=doc.Bodies.Remove(source.Id).Add(body.Id,body),
                Features=doc.Features.SetItem(target,producer with{OutputMetadata=BodyOutputMetadata.FromBody(source)}).Add(id,feature),
                Definitions=doc.Definitions.SetItem(part.Id,part with{Bodies=part.Bodies.Remove(source.Id).Add(body.Id),Features=part.Features.Add(id)})};
            return new(doc.WithNewState(),[result]);
        }
        catch{result.Dispose();throw;}
    }
}

/// <summary>Explicitly replaces one consumer binding after the caller reviewed an exact history target.</summary>
public sealed class RebindHistoryFilletCommand(FeatureId consumer,TopologyReference origin,HistoryTarget confirmedTarget) : ICadDocumentCommand
{
    public string Name=>"Reselect bound fillet edge";
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        var doc=context.Snapshot;
        if(!doc.Features.TryGetValue(consumer,out var feature)||feature.Recipe is not HistoryFilletRecipe old||feature.TopologyBinding is not {} previous)
            throw new CadValidationException("Select a bound fillet feature.");
        var target=previous.TargetFeatureId;
        var (binding,locator)=await FeatureBindingGuard.ConfirmAsync(context,origin,target,confirmedTarget,token);
        var recipe=old with{Source=doc.Features[target].Result,FullTopologyIndex=locator.FullTopologyIndex};
        var staged=doc with{Features=doc.Features.SetItem(consumer,feature with{TopologyBinding=binding})};
        return await FeatureRecompute.PrepareAsync(context with{Snapshot=staged},[consumer],
            new Dictionary<FeatureId,GeometryRecipe>{{consumer,recipe}},token).ConfigureAwait(false);
    }
}
