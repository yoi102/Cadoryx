using Cadoryx.Db;

namespace Cadoryx.Commands;

/// <summary>Creates an advanced extrusion, consuming only its boolean target, never the limit reference body.</summary>
public sealed class AddExtrusionCommand(ExtrudeRecipe recipe,string name,DefinitionId? partId=null,BodyId? targetBody=null,
    SketchProfileReference? sketchSource=null):ICadDocumentCommand
{
    public string Name=>name;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        var doc=context.Snapshot;CadGuard.Name(name);recipe.Validate();
        var options=recipe.Options??throw new CadValidationException("Advanced extrusion options are required.");
        if(options.Operation==ExtrusionOperation.NewBody)
        {
            if(targetBody is not null)throw new CadValidationException("A new body cannot consume a target.");
            var current=recipe with{Options=options.Refresh(doc,partId??doc.Definitions.Values.OfType<PartDefinition>().SingleOrDefault()?.Id??default)};
            if(current.Options!.LimitFace is {} face)await ConfirmLimitAsync(context,face,token);
            return await new AddBodyCommand(current,name,partId,sketchSource:sketchSource).PrepareAsync(context,token);
        }
        if(targetBody is not {} bodyId||!doc.Bodies.TryGetValue(bodyId,out var body)||body.Producer is not {} sourceId||
            !doc.Features.TryGetValue(sourceId,out var source)||source.IsStale||source.IsSuppressed||source.Result!=options.Source||
            partId is {} requested&&requested!=body.PartId)
            throw new CadValidationException("Select the current exact output of the extrusion target.");
        if(doc.Layers[body.LayerId].IsLocked||doc.ExternalParts.ContainsKey(body.PartId))
            throw new CadValidationException("Extrusion target is locked or externally linked.");
        var effective=recipe with{Options=options.Refresh(doc,body.PartId)};
        if(sketchSource is not null)effective=(ExtrudeRecipe)sketchSource.Resolve(doc,body.PartId,effective);
        effective.Validate();
        var id=FeatureId.New();var output=BodyId.New();var part=(PartDefinition)doc.Definitions[body.PartId];
        var staged=new FeatureDefinition(id,body.PartId,name,effective,[sourceId],output,source.Result){SketchSource=sketchSource};
        effective.Options!.ValidateApplied(doc,staged);
        DatumPlanes.Order(doc with{Features=doc.Features.Add(id,staged)});
        if(effective.Options.LimitFace is {} selection)await ConfirmLimitAsync(context,selection,token);
        if(effective.Options.SupportFace is {} support)await ConfirmLimitAsync(context,support,token);
        var result=await context.Kernel.EvaluateAsync(effective,context.Assets,token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();var nextBody=body with{Id=output,Name=name,Producer=id,Geometry=result.Geometry};
            staged=staged with{Result=result.Geometry,TopologyHistory=result.TopologyHistory,OutputMetadata=BodyOutputMetadata.FromBody(nextBody)};
            return new((doc with{Bodies=doc.Bodies.Remove(bodyId).Add(output,nextBody),
                Features=doc.Features.SetItem(sourceId,source with{OutputMetadata=BodyOutputMetadata.FromBody(body)}).Add(id,staged),
                Definitions=doc.Definitions.SetItem(part.Id,part with{Bodies=part.Bodies.Remove(bodyId).Add(output),Features=part.Features.Add(id)})}).WithNewState(),[result]);
        }
        catch{result.Dispose();throw;}
    }
    internal static Task ConfirmLimitAsync(DocumentCommandContext context,ExactTopologySelection selection,CancellationToken token)=>
        context.Kernel is Cadoryx.Kernel.Abstractions.IExactTopologyResolver resolver?resolver.ConfirmAsync(context.Snapshot,selection,context.Assets,token):
        throw new CadValidationException("The kernel cannot verify an exact extrusion limit face.");
}
