using Cadoryx.Db;

namespace Cadoryx.Commands;

public sealed class AddContourLocalCommand(BodyId bodyId,ContourLocalRecipe recipe,string name):ICadDocumentCommand
{
    public string Name=>name;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        var doc=context.Snapshot;CadGuard.Name(name);recipe.Validate();
        if(!doc.Bodies.TryGetValue(bodyId,out var body)||body.Producer is not {} sourceId||!recipe.HasCurrentSource(doc)||recipe.Programs[0].Seed.FeatureId!=sourceId)
            throw new CadValidationException("Choose the current exact solid output for all contour programs.");
        if(doc.Layers[body.LayerId].IsLocked||doc.ExternalParts.ContainsKey(body.PartId))throw new CadValidationException("The contour source layer is locked or its external part must be detached.");
        var source=doc.Features[sourceId];var part=(PartDefinition)doc.Definitions[body.PartId];var id=FeatureId.New();var output=BodyId.New();
        var result=await context.Kernel.EvaluateAsync(recipe,context.Assets,token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();var nextBody=body with{Id=output,Name=name,Producer=id,Geometry=result.Geometry,Appearance=body.Appearance with{PreserveSourceStyles=false}};
            var feature=new FeatureDefinition(id,body.PartId,name,recipe,[sourceId],output,result.Geometry,OutputMetadata:BodyOutputMetadata.FromBody(nextBody)){TopologyHistory=result.TopologyHistory};
            return new((doc with{Bodies=doc.Bodies.Remove(bodyId).Add(output,nextBody),Features=doc.Features.SetItem(sourceId,source with{OutputMetadata=BodyOutputMetadata.FromBody(body)}).Add(id,feature),
                Definitions=doc.Definitions.SetItem(part.Id,part with{Bodies=part.Bodies.Remove(bodyId).Add(output),Features=part.Features.Add(id)})}).WithNewState(),[result]);
        }
        catch{result.Dispose();throw;}
    }
}
public sealed class ReselectContourLocalCommand(FeatureId id,ContourLocalRecipe recipe):ICadDocumentCommand
{
    public string Name=>"Reselect contour source";
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        recipe.Validate();var doc=context.Snapshot;
        if(!doc.Features.TryGetValue(id,out var feature)||feature.Recipe is not ContourLocalRecipe previous||feature.Inputs.Length!=1||
            recipe.Operation!=previous.Operation||recipe.Programs[0].Seed.FeatureId!=feature.Inputs[0]||!recipe.HasCurrentSource(doc))
            throw new CadValidationException("Reselect current contours on the same source feature.");
        return FeatureRecompute.PrepareAsync(context,[id],new Dictionary<FeatureId,GeometryRecipe>{{id,recipe}},token);
    }
}
