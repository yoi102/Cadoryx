using Cadoryx.Db;

namespace Cadoryx.Commands;

/// <summary>Consumes a displayed source output; its producer and attributes remain in the feature graph.</summary>
public sealed class AddRepetitionCommand(BodyId bodyId,RepetitionRecipe recipe,string name):ICadDocumentCommand
{
    public string Name=>name;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        var doc=context.Snapshot;CadGuard.Name(name);
        if(!doc.Bodies.TryGetValue(bodyId,out var body)||body.Producer is not {} sourceId||
            !doc.Features.TryGetValue(sourceId,out var source)||source.IsStale||source.IsSuppressed||source.Result!=recipe.Source)
            throw new CadValidationException("Select the current exact output of a source feature.");
        if(doc.Layers[body.LayerId].IsLocked)throw new CadValidationException("Source layer is locked.");
        if(doc.ExternalParts.ContainsKey(body.PartId))throw new CadValidationException("Detach the external part before modeling.");
        var current=recipe.Refresh(doc,body.PartId);current.Validate();
        var id=FeatureId.New();var output=BodyId.New();var part=(PartDefinition)doc.Definitions[body.PartId];
        // Check the joint model graph before allocating native topology.
        var staged=new FeatureDefinition(id,body.PartId,name,current,[sourceId],output,source.Result);
        DatumPlanes.Order(doc with{Features=doc.Features.Add(id,staged)});
        var result=await context.Kernel.EvaluateAsync(current,context.Assets,token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();var nextBody=body with{Id=output,Name=name,Producer=id,Geometry=result.Geometry};
            staged=staged with{Result=result.Geometry,TopologyHistory=result.TopologyHistory,RepetitionPresentation=result.RepetitionPresentation,OutputMetadata=BodyOutputMetadata.FromBody(nextBody)};
            return new((doc with{Bodies=doc.Bodies.Remove(bodyId).Add(output,nextBody),
                Features=doc.Features.SetItem(sourceId,source with{OutputMetadata=BodyOutputMetadata.FromBody(body)}).Add(id,staged),
                Definitions=doc.Definitions.SetItem(part.Id,part with{Bodies=part.Bodies.Remove(bodyId).Add(output),Features=part.Features.Add(id)})}).WithNewState(),[result]);
        }
        catch{result.Dispose();throw;}
    }
}
