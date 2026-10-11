using Cadoryx.Db;

namespace Cadoryx.Commands;

public sealed class AddExtrusionPatternCommand(BodyId bodyId,ExtrusionPatternRecipe recipe,string name):ICadDocumentCommand
{
    public string Name=>name;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        var document=context.Snapshot;CadGuard.Name(name);recipe.Validate();
        if(!document.Bodies.TryGetValue(bodyId,out var body)||body.Producer!=recipe.Seed||body.Geometry!=recipe.Source)
            throw new CadValidationException("Select the current displayed output of the authored extrusion seed.");
        if(document.Layers[body.LayerId].IsLocked||document.ExternalParts.ContainsKey(body.PartId))
            throw new CadValidationException("Extrusion replay target is locked or externally linked.");
        var current=recipe.Refresh(document,body.PartId);
        if(!recipe.SameTemplate(current.Template))throw new CadValidationException("Extrusion seed recipe changed; select its current recipe explicitly.");
        var seed=document.Features[recipe.Seed];var part=(PartDefinition)document.Definitions[body.PartId];
        var id=FeatureId.New();var output=BodyId.New();var feature=new FeatureDefinition(id,body.PartId,name,current,[recipe.Seed],output,seed.Result);
        DatumPlanes.Order(document with{Features=document.Features.Add(id,feature)});
        var result=await context.Kernel.EvaluateAsync(current,context.Assets,token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();var nextBody=body with{Id=output,Name=name,Producer=id,Geometry=result.Geometry};
            feature=feature with{Result=result.Geometry,OutputMetadata=BodyOutputMetadata.FromBody(nextBody)};
            return new((document with{Bodies=document.Bodies.Remove(bodyId).Add(output,nextBody),
                Features=document.Features.SetItem(seed.Id,seed with{OutputMetadata=BodyOutputMetadata.FromBody(body)}).Add(id,feature),
                Definitions=document.Definitions.SetItem(part.Id,part with{Bodies=part.Bodies.Remove(bodyId).Add(output),Features=part.Features.Add(id)})}).WithNewState(),[result]);
        }
        catch{result.Dispose();throw;}
    }
}
