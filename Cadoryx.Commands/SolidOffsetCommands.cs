using Cadoryx.Db;

namespace Cadoryx.Commands;

/// <summary>Consumes the exact displayed whole-solid output; source changes are dependency changes, not topology reselection.</summary>
public sealed class AddSolidOffsetCommand(BodyId bodyId,SolidOffsetRecipe recipe,string name):ICadDocumentCommand
{
    public string Name=>name;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        var doc=context.Snapshot;CadGuard.Name(name);recipe.Validate();
        if(!doc.Bodies.TryGetValue(bodyId,out var body)||body.Producer is not {} sourceId||
            !doc.Features.TryGetValue(sourceId,out var source)||source.IsStale||source.IsSuppressed||source.Result!=recipe.Source)
            throw new CadValidationException("Select the current exact output of a source feature.");
        if(doc.Layers[body.LayerId].IsLocked||doc.ExternalParts.ContainsKey(body.PartId))
            throw new CadValidationException("The source layer is locked or its external part must be detached.");
        var id=FeatureId.New();var output=BodyId.New();var part=(PartDefinition)doc.Definitions[body.PartId];
        var staged=new FeatureDefinition(id,body.PartId,name,recipe,[sourceId],output,source.Result);
        DatumPlanes.Order(doc with{Features=doc.Features.Add(id,staged)});
        var result=await context.Kernel.EvaluateAsync(recipe,context.Assets,token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();var nextBody=body with{Id=output,Name=name,Producer=id,Geometry=result.Geometry,
                Appearance=body.Appearance with{PreserveSourceStyles=false}};
            staged=staged with{Result=result.Geometry,TopologyHistory=result.TopologyHistory,OutputMetadata=BodyOutputMetadata.FromBody(nextBody)};
            return new((doc with{Bodies=doc.Bodies.Remove(bodyId).Add(output,nextBody),
                Features=doc.Features.SetItem(sourceId,source with{OutputMetadata=BodyOutputMetadata.FromBody(body)}).Add(id,staged),
                Definitions=doc.Definitions.SetItem(part.Id,part with{Bodies=part.Bodies.Remove(bodyId).Add(output),Features=part.Features.Add(id)})}).WithNewState(),[result]);
        }
        catch{result.Dispose();throw;}
    }
}
