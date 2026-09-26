using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

/// <summary>One undoable, frozen world-space section in a new independent part. No inferred topology bindings.</summary>
public sealed class CreateSectionCommand(IReadOnlyList<GeometryInstance> inputs,CuttingPlane plane,string name) : ICadDocumentCommand
{
    private readonly GeometryInstance[] instances=inputs.ToArray();
    public string Name=>name;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        CadGuard.Name(name);plane.Validate();var doc=context.Snapshot;GeometryInstanceGuard.Validate(doc,instances);
        var layer=doc.Layers.Values.FirstOrDefault(l=>!l.IsLocked)??throw new CadValidationException("No unlocked destination layer.");
        if(context.Kernel is not IGeometryReviewKernel review)throw new NotSupportedException("Section kernel is unavailable.");
        var result=await review.SectionAsync(instances,plane,context.Assets,cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(result.Geometry.Kind==BodyKind.Empty)throw new CadValidationException("The plane does not intersect the selected geometry.");
            var part=DefinitionId.New();var body=BodyId.New();var feature=FeatureId.New();
            var root=(AssemblyDefinition)doc.Definitions[doc.RootAssemblyId];
            var output=new CadBody(body,part,name,result.Geometry,feature,layer.Id,new(0xFFFFC247));
            doc=doc with
            {
                Definitions=doc.Definitions.Add(part,new PartDefinition(part,name,[body],[feature]))
                    .SetItem(root.Id,root with{Children=root.Children.Add(new(ComponentSlotId.New(),part,name,RigidTransform3d.Identity))}),
                Bodies=doc.Bodies.Add(body,output),Features=doc.Features.Add(feature,new(feature,part,name,
                    new ImportedRecipe(result.Geometry),[],body,result.Geometry,OutputMetadata:BodyOutputMetadata.FromBody(output)))
            };
            return new(doc.WithNewState(),[result]);
        }
        catch{result.Dispose();throw;}
    }
}
