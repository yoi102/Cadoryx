using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.Commands;

internal static class FeatureBindingGuard
{
    public static async Task<FeatureTopologyBinding> ConfirmExactAsync(DocumentCommandContext context,
        ExactTopologySelection edge,ExactTopologySelection? supportFace,CancellationToken token)
    {
        if(context.Kernel is not IExactTopologyResolver resolver)throw new NotSupportedException("Kernel has no exact topology resolver.");
        var doc=context.Snapshot;
        await resolver.ConfirmAsync(doc,edge,context.Assets,token).ConfigureAwait(false);
        if(edge.Kind!=HistoryShapeKind.Edge)throw new CadValidationException("Select an edge.");
        if(supportFace is not null)
        {
            await resolver.ConfirmAsync(doc,supportFace,context.Assets,token).ConfigureAwait(false);
            if(supportFace.Kind!=HistoryShapeKind.Face||supportFace.FeatureId!=edge.FeatureId||
                supportFace.Revision!=edge.Revision||supportFace.Asset!=edge.Asset||supportFace.Fingerprint!=edge.Fingerprint)
                throw new CadValidationException("Support face must belong to the same exact BRep.");
        }
        var binding=new FeatureTopologyBinding(null,edge.FeatureId,edge.Revision,edge.Asset,edge.FullTopologyIndex,
            edge.AdapterVersion,2){ExactEdge=edge,SupportFace=supportFace};
        binding.Validate(doc.Id);return binding;
    }
    public static async Task<FeatureTopologyBinding> WithFaceAsync(DocumentCommandContext context,
        FeatureTopologyBinding binding,ExactTopologySelection face,CancellationToken token)
    {
        if(context.Kernel is not IExactTopologyResolver resolver)throw new NotSupportedException("Kernel has no exact topology resolver.");
        await resolver.ConfirmAsync(context.Snapshot,face,context.Assets,token).ConfigureAwait(false);
        var result=binding with{SupportFace=face,SchemaVersion=2};result.Validate(context.Snapshot.Id);return result;
    }
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

internal static class BoundLocalCreation
{
    public static async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,FeatureTopologyBinding binding,
        GeometryRecipe recipe,string name,CancellationToken token)
    {
        var doc=context.Snapshot;var target=binding.TargetFeatureId;var producer=doc.Features[target];
        if(!doc.Bodies.TryGetValue(producer.OutputBodyId,out var source)||source.Producer!=target)
            throw new CadValidationException("Target feature has no current body output.");
        if(doc.Layers[source.LayerId].IsLocked)throw new CadValidationException(Strings.LayerLocked);
        var result=await context.Kernel.EvaluateAsync(recipe,context.Assets,token).ConfigureAwait(false);
        try
        {
            var id=FeatureId.New();var body=source with{Id=BodyId.New(),Producer=id,Name=name,Geometry=result.Geometry};
            var part=(PartDefinition)doc.Definitions[source.PartId];
            var feature=new FeatureDefinition(id,part.Id,name,recipe,[target],body.Id,result.Geometry,OutputMetadata:BodyOutputMetadata.FromBody(body))
                {TopologyBinding=binding,TopologyHistory=result.TopologyHistory};
            doc=doc with{Bodies=doc.Bodies.Remove(source.Id).Add(body.Id,body),
                Features=doc.Features.SetItem(target,producer with{OutputMetadata=BodyOutputMetadata.FromBody(source)}).Add(id,feature),
                Definitions=doc.Definitions.SetItem(part.Id,part with{Bodies=part.Bodies.Remove(source.Id).Add(body.Id),Features=part.Features.Add(id)})};
            return new(doc.WithNewState(),[result]);
        }
        catch{result.Dispose();throw;}
    }
}

/// <summary>Creates a real fillet consuming a verified cross-feature edge. A diagnostic query alone does not authorize it.</summary>
public sealed class HistoryFilletCommand(TopologyReference origin,FeatureId target,double radius,HistoryTarget confirmedTarget,double? endRadius=null) : ICadDocumentCommand
{
    public string Name=>Strings.Fillet;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        CadGuard.Positive(radius);if(endRadius is {} end)CadGuard.Positive(end);
        var (binding,locator)=await FeatureBindingGuard.ConfirmAsync(context,origin,target,confirmedTarget,token);
        var recipe=new HistoryFilletRecipe(context.Snapshot.Features[target].Result,locator.FullTopologyIndex,radius,endRadius);
        return await BoundLocalCreation.PrepareAsync(context,binding,recipe,Name,token).ConfigureAwait(false);
    }
}

/// <summary>Uses an exact user-picked edge, including generated edges with no Box semantic ancestor.</summary>
public sealed class ExactLocalFeatureCommand(ExactTopologySelection edge,LocalFeatureOperation operation,double size,
    ExactTopologySelection? supportFace=null,double? secondDistance=null,double? endRadius=null) : ICadDocumentCommand
{
    public string Name=>operation==LocalFeatureOperation.Fillet?Strings.Fillet:Strings.Chamfer;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        if(!Enum.IsDefined(operation))throw new CadValidationException("Invalid local operation.");
        CadGuard.Positive(size);
        if(operation==LocalFeatureOperation.Fillet&&(supportFace is not null||secondDistance is not null))
            throw new CadValidationException("A fillet does not use a support face or second distance.");
        if(operation==LocalFeatureOperation.Chamfer&&(supportFace is null||endRadius is not null))
            throw new CadValidationException("A chamfer requires a selected support face.");
        var binding=await FeatureBindingGuard.ConfirmExactAsync(context,edge,supportFace,token).ConfigureAwait(false);
        var source=context.Snapshot.Features[edge.FeatureId].Result;
        GeometryRecipe recipe=operation==LocalFeatureOperation.Fillet
            ?new HistoryFilletRecipe(source,edge.FullTopologyIndex,size,endRadius)
            :new HistoryChamferRecipe(source,edge.FullTopologyIndex,supportFace!.FullTopologyIndex,size,secondDistance);
        return await BoundLocalCreation.PrepareAsync(context,binding,recipe,Name,token).ConfigureAwait(false);
    }
}

public sealed class HistoryChamferCommand(TopologyReference origin,FeatureId target,HistoryTarget confirmedTarget,
    ExactTopologySelection supportFace,double distance,double? secondDistance=null) : ICadDocumentCommand
{
    public string Name=>Strings.Chamfer;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        var (edge,locator)=await FeatureBindingGuard.ConfirmAsync(context,origin,target,confirmedTarget,token).ConfigureAwait(false);
        var binding=await FeatureBindingGuard.WithFaceAsync(context,edge,supportFace,token).ConfigureAwait(false);
        var recipe=new HistoryChamferRecipe(context.Snapshot.Features[target].Result,locator.FullTopologyIndex,
            supportFace.FullTopologyIndex,distance,secondDistance);
        return await BoundLocalCreation.PrepareAsync(context,binding,recipe,Name,token).ConfigureAwait(false);
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

/// <summary>Explicitly reselects an edge (and chamfer face) on a changed upstream result.</summary>
public sealed class RebindExactLocalFeatureCommand(FeatureId consumer,ExactTopologySelection edge,
    ExactTopologySelection? supportFace,double size,double? secondDistance=null,double? endRadius=null) : ICadDocumentCommand
{
    public string Name=>"Reselect local topology";
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        var doc=context.Snapshot;
        if(!doc.Features.TryGetValue(consumer,out var feature)||!feature.IsStale||
            feature.Recipe is not (HistoryFilletRecipe or HistoryChamferRecipe)||feature.TopologyBinding is not {} previous||
            previous.TargetFeatureId!=edge.FeatureId)
            throw new CadValidationException("Select a stale bound local feature and its current upstream output.");
        if(feature.Recipe is HistoryChamferRecipe&&supportFace is null||feature.Recipe is HistoryFilletRecipe&&supportFace is not null)
            throw new CadValidationException("Reselect the required edge and support face.");
        var binding=await FeatureBindingGuard.ConfirmExactAsync(context,edge,supportFace,token).ConfigureAwait(false);
        var source=doc.Features[edge.FeatureId].Result;
        GeometryRecipe recipe=feature.Recipe switch
        {
            HistoryFilletRecipe=>new HistoryFilletRecipe(source,edge.FullTopologyIndex,size,endRadius),
            HistoryChamferRecipe=>new HistoryChamferRecipe(source,edge.FullTopologyIndex,supportFace!.FullTopologyIndex,size,secondDistance),
            _=>throw new CadValidationException("Unsupported bound local feature.")
        };
        var staged=doc with{Features=doc.Features.SetItem(consumer,feature with{TopologyBinding=binding})};
        return await FeatureRecompute.PrepareAsync(context with{Snapshot=staged},[consumer],
            new Dictionary<FeatureId,GeometryRecipe>{{consumer,recipe}},token).ConfigureAwait(false);
    }
}
