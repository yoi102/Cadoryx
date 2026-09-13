using Cadoryx.Db;
using Strings = Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.Commands;

public sealed class LocalFeatureCommand(TopologyReference edge,LocalFeatureOperation operation,double size) : ICadDocumentCommand
{
    public string Name=>operation==LocalFeatureOperation.Fillet?Strings.Fillet:Strings.Chamfer;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
    {
        edge.Validate();var doc=context.Snapshot;
        if(edge.Kind!=TopologyKind.Edge||edge.SecondBoundary is null||edge.DocumentId!=doc.Id||
            !doc.Bodies.TryGetValue(edge.OutputBodyId,out var source)||source.Producer!=edge.FeatureId||source.Geometry.Revision!=edge.OriginRevision)
            throw new CadValidationException("Select an edge of the current box output.");
        if(doc.Layers[source.LayerId].IsLocked)throw new CadValidationException("Output layer is locked.");
        var producer=doc.Features[edge.FeatureId];
        if(producer.Recipe is not BoxRecipe box)throw new CadValidationException("Only box edges are supported.");
        var recipe=new LocalFeatureRecipe(source.Geometry,box,edge.Boundary,edge.SecondBoundary.Value,operation,size);recipe.Validate();
        var result=await context.Kernel.EvaluateAsync(recipe,context.Assets,token).ConfigureAwait(false);
        try
        {
            var id=FeatureId.New();var body=source with{Id=BodyId.New(),Producer=id,Name=Name,Geometry=result.Geometry};var part=(PartDefinition)doc.Definitions[source.PartId];
            var feature=new FeatureDefinition(id,part.Id,Name,recipe,[producer.Id],body.Id,result.Geometry,OutputMetadata:BodyOutputMetadata.FromBody(body)){TopologyHistory=result.TopologyHistory};
            doc=doc with{Bodies=doc.Bodies.Remove(source.Id).Add(body.Id,body),
                Features=doc.Features.SetItem(producer.Id,producer with{OutputMetadata=BodyOutputMetadata.FromBody(source)}).Add(id,feature),
                TopologyReferences=doc.TopologyReferences.SetItem(edge.Id,edge),
                Definitions=doc.Definitions.SetItem(part.Id,part with{Bodies=part.Bodies.Remove(source.Id).Add(body.Id),Features=part.Features.Add(id)})};
            return new(doc.WithNewState(),[result]);
        }
        catch{result.Dispose();throw;}
    }
}
