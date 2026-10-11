using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

internal static class FaceFeatureCommandGuard
{
    internal static async Task ConfirmAsync(DocumentCommandContext context,GeometryRecipe recipe,CancellationToken token)
    {
        recipe.Validate();
        if(!FaceFeatureRecipes.HasCurrentSource(context.Snapshot,recipe))throw new CadValidationException("Face feature source changed. Reselect its faces explicitly.");
        if(context.Kernel is not IExactTopologyResolver resolver)throw new NotSupportedException("Kernel has no exact topology resolver.");
        foreach(var face in FaceFeatureRecipes.Faces(recipe))await resolver.ConfirmAsync(context.Snapshot,face,context.Assets,token).ConfigureAwait(false);
        if(recipe is PlanarPushPullRecipe or DefeatureRecipe)
        {
            if(context.Kernel is not IFaceEditResolver faceResolver)throw new NotSupportedException("Kernel has no native face-edit resolver.");
            await faceResolver.ConfirmFaceEditRecipeAsync(recipe,context.Assets,token).ConfigureAwait(false);
        }
    }
}
public sealed class AddFaceFeatureCommand(BodyId bodyId,GeometryRecipe recipe,string name):ICadDocumentCommand
{
    public string Name=>name;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        if(recipe is not (ShellRecipe or DraftRecipe or PlanarPushPullRecipe or DefeatureRecipe))throw new CadValidationException("Expected a supported face feature recipe.");
        recipe.Validate();
        var doc=context.Snapshot;CadGuard.Name(name);
        if(!doc.Bodies.TryGetValue(bodyId,out var body)||body.Producer is not {} sourceId||FaceFeatureRecipes.Faces(recipe).First().FeatureId!=sourceId)
            throw new CadValidationException("Select the current displayed output of the face feature source.");
        if(doc.Layers[body.LayerId].IsLocked||doc.ExternalParts.ContainsKey(body.PartId))throw new CadValidationException("The source layer is locked or its external part must be detached.");
        var current=recipe is DraftRecipe draft?draft.Refresh(doc,body.PartId):recipe;await FaceFeatureCommandGuard.ConfirmAsync(context,current,token);
        var source=doc.Features[sourceId];var id=FeatureId.New();var output=BodyId.New();var part=(PartDefinition)doc.Definitions[body.PartId];
        var staged=new FeatureDefinition(id,body.PartId,name,current,[sourceId],output,source.Result);DatumPlanes.Order(doc with{Features=doc.Features.Add(id,staged)});
        var result=await context.Kernel.EvaluateAsync(current,context.Assets,token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();var nextBody=body with{Id=output,Name=name,Producer=id,Geometry=result.Geometry,
                Appearance=recipe is PlanarPushPullRecipe or DefeatureRecipe?body.Appearance with{PreserveSourceStyles=false}:body.Appearance};
            staged=staged with{Result=result.Geometry,TopologyHistory=result.TopologyHistory,OutputMetadata=BodyOutputMetadata.FromBody(nextBody)};
            return new((doc with{Bodies=doc.Bodies.Remove(bodyId).Add(output,nextBody),Features=doc.Features.SetItem(sourceId,source with{OutputMetadata=BodyOutputMetadata.FromBody(body)}).Add(id,staged),
                Definitions=doc.Definitions.SetItem(part.Id,part with{Bodies=part.Bodies.Remove(bodyId).Add(output),Features=part.Features.Add(id)})}).WithNewState(),[result]);
        }
        catch{result.Dispose();throw;}
    }
}
/// <summary>Replaces author-confirmed faces on the same source feature; never reuses an old map slot by itself.</summary>
public sealed class ReselectFaceFeatureCommand(FeatureId id,GeometryRecipe recipe):ICadDocumentCommand
{
    public string Name=>"Reselect face feature";
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        var old=context.Snapshot.Features[id];
        recipe.Validate();
        if(old.Recipe is not (ShellRecipe or DraftRecipe or PlanarPushPullRecipe or DefeatureRecipe)||old.Recipe.GetType()!=recipe.GetType()||old.Inputs.Length!=1||
            FaceFeatureRecipes.Faces(recipe).First().FeatureId!=old.Inputs[0])throw new CadValidationException("Reselection must retain face feature kind and source identity.");
        await FaceFeatureCommandGuard.ConfirmAsync(context,recipe,token);
        return await new RecomputeCommand(id,recipe).PrepareAsync(context,token).ConfigureAwait(false);
    }
}
