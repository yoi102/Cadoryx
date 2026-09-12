using Cadoryx.Db;
using System.Collections.Immutable;
namespace Cadoryx.Commands;

/// <summary>Reevaluates the changed feature and its dependent closure into an isolated candidate.</summary>
public sealed class RecomputeCommand(FeatureId featureId,GeometryRecipe recipe) : ICadDocumentCommand
{
    public string Name=>"修改特征参数";
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        var doc=context.Snapshot;var original=doc.Features[featureId];
        if(original.Recipe.GetType()!=recipe.GetType())throw new CadValidationException("Changing feature kind requires a new identity.");
        var pending=new HashSet<FeatureId>{featureId};
        bool added;
        do {added=false;foreach(var f in doc.Features.Values)if(f.Inputs.Any(pending.Contains))added|=pending.Add(f.Id);}while(added);
        var done=new HashSet<FeatureId>();var resources=new List<IDisposable>();
        try
        {
            while(done.Count<pending.Count)
            {
                var f=doc.Features.Values.FirstOrDefault(f=>pending.Contains(f.Id)&&!done.Contains(f.Id)&&f.Inputs.All(x=>!pending.Contains(x)||done.Contains(x)))
                    ??throw new CadValidationException("Cyclic recompute graph.");
                GeometryRecipe current=f.Id==featureId?recipe:f.Recipe;
                if(current is BooleanRecipe boolean && f.Inputs.Length==boolean.Inputs.Length)
                    current=boolean with {Inputs=f.Inputs.Select(id=>doc.Features[id].Result).ToImmutableArray()};
                if(current is TransformRecipe transform && f.Inputs.Length==1)
                    current=transform with {Source=doc.Features[f.Inputs[0]].Result};
                var result=await context.Kernel.EvaluateAsync(current,context.Assets,cancellationToken).ConfigureAwait(false);
                resources.Add(result);
                doc=doc with {Features=doc.Features.SetItem(f.Id,f with {Recipe=current,Result=result.Geometry})};
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
            return new(doc.WithNewState(),resources);
        }
        catch{foreach(var resource in resources)resource.Dispose();throw;}
    }
}
