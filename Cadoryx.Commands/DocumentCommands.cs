using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

public sealed record DocumentCommandContext(DocumentSnapshot Snapshot,long Generation,IAssetStore Assets,IGeometryKernel Kernel);
public interface ICadDocumentCommand
{
    string Name { get; }
    Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken);
}
public sealed class PreparedDocumentEdit(DocumentSnapshot snapshot,IEnumerable<IDisposable>? temporary=null) : IDisposable
{
    private readonly IDisposable[] resources=temporary?.ToArray()??[];
    public DocumentSnapshot Snapshot { get; }=snapshot;
    public void Dispose(){foreach(var resource in resources)resource.Dispose();}
}
public sealed record DocumentChangeSet(DocumentId DocumentId,DocumentStateId Before,DocumentStateId After,long Generation,
    ImmutableArray<BodyId> Added,ImmutableArray<BodyId> Removed,ImmutableArray<BodyId> Changed,bool StructureChanged)
{
    public static DocumentChangeSet Between(DocumentSnapshot a,DocumentSnapshot b,long generation) => new(
        b.Id,a.StateId,b.StateId,generation,b.Bodies.Keys.Except(a.Bodies.Keys).ToImmutableArray(),
        a.Bodies.Keys.Except(b.Bodies.Keys).ToImmutableArray(),
        b.Bodies.Keys.Intersect(a.Bodies.Keys).Where(id=>a.Bodies[id]!=b.Bodies[id]).ToImmutableArray(),!ReferenceEquals(a.Definitions,b.Definitions));
}
public sealed class AddBodyCommand(GeometryRecipe recipe,string name,DefinitionId? targetPart=null) : ICadDocumentCommand
{
    public string Name=>"创建 "+name;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        CadGuard.Name(name);recipe.Validate();
        var result=await context.Kernel.EvaluateAsync(recipe,context.Assets,cancellationToken).ConfigureAwait(false);
        try
        {
            var doc=context.Snapshot;
            var part=targetPart is {} id ? doc.Definitions.GetValueOrDefault(id) as PartDefinition : doc.Definitions.Values.OfType<PartDefinition>().FirstOrDefault();
            if(targetPart is not null&&part is null)throw new CadValidationException("Target is not a part.");
            if(part is null)
            {
                part=new(DefinitionId.New(),"Part 1",[],[]);
                var root=(AssemblyDefinition)doc.Definitions[doc.RootAssemblyId];
                doc=doc with {Definitions=doc.Definitions.Add(part.Id,part).SetItem(root.Id,root with {Children=root.Children.Add(new(ComponentSlotId.New(),part.Id,part.Name,RigidTransform3d.Identity))})};
            }
            var body=BodyId.New();var feature=FeatureId.New();var layer=doc.Layers.Keys.First();
            var b=new CadBody(body,part.Id,name,result.Geometry,feature,layer,new());
            var f=new FeatureDefinition(feature,part.Id,name,recipe,[],body,result.Geometry);
            doc=doc with {Bodies=doc.Bodies.Add(body,b),Features=doc.Features.Add(feature,f),
                Definitions=doc.Definitions.SetItem(part.Id,part with {Bodies=part.Bodies.Add(body),Features=part.Features.Add(feature)})};
            return new(doc.WithNewState(),[result]);
        }
        catch {result.Dispose();throw;}
    }
}
public sealed class BooleanCommand(BooleanOperation operation,IEnumerable<BodyId> inputBodies) : ICadDocumentCommand
{
    private readonly BodyId[] ids=inputBodies.Distinct().ToArray();
    public string Name=>operation.ToString();
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        if(ids.Length<2)throw new CadValidationException("Select at least two bodies.");
        var doc=context.Snapshot;var inputs=ids.Select(id=>doc.Bodies[id]).ToArray();
        if(inputs.Select(x=>x.PartId).Distinct().Count()!=1)throw new CadValidationException("Boolean operands must belong to the same part; edit the part definition first.");
        if(inputs.Any(x=>doc.Layers[x.LayerId].IsLocked))throw new CadValidationException("A selected body is on a locked layer.");
        var recipe=new BooleanRecipe(operation,inputs.Select(x=>x.Geometry).ToImmutableArray());
        var result=await context.Kernel.EvaluateAsync(recipe,context.Assets,cancellationToken).ConfigureAwait(false);
        try
        {
            var part=(PartDefinition)doc.Definitions[inputs[0].PartId];var body=BodyId.New();var fid=FeatureId.New();
            var feature=new FeatureDefinition(fid,part.Id,Name,recipe,inputs.Where(x=>x.Producer.HasValue).Select(x=>x.Producer!.Value).ToImmutableArray(),body,result.Geometry);
            var b=new CadBody(body,part.Id,Name,result.Geometry,fid,inputs[0].LayerId,inputs[0].Appearance);
            feature=feature with{OutputMetadata=BodyOutputMetadata.FromBody(b)};
            bool empty=result.Geometry.Kind==BodyKind.Empty;
            doc=doc with {Bodies=empty?doc.Bodies.RemoveRange(ids):doc.Bodies.RemoveRange(ids).Add(body,b),Features=doc.Features.Add(fid,feature),
                Definitions=doc.Definitions.SetItem(part.Id,part with {Bodies=empty?part.Bodies.RemoveRange(ids):part.Bodies.RemoveRange(ids).Add(body),Features=part.Features.Add(fid)})};
            return new(doc.WithNewState(),[result]);
        }
        catch{result.Dispose();throw;}
    }
}
public sealed class EditDocumentCommand(string name,Func<DocumentSnapshot,DocumentSnapshot> edit) : ICadDocumentCommand
{
    public string Name=>name;
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();var result=edit(context.Snapshot);
        return Task.FromResult(new PreparedDocumentEdit(ReferenceEquals(result,context.Snapshot)?result:result.WithNewState()));
    }
}
public static class DocumentEdits
{
    public static ICadDocumentCommand RenameBody(BodyId id,string name)=>new EditDocumentCommand("重命名",doc=>
    {
        CadGuard.Name(name);var b=EditableBody(doc,id);return b.Name==name?doc:doc with {Bodies=doc.Bodies.SetItem(id,b with {Name=name})};
    });
    public static ICadDocumentCommand SetAppearance(BodyId id,CadAppearance appearance)=>new EditDocumentCommand("修改外观",doc=>
    {
        var b=EditableBody(doc,id);return b.Appearance==appearance?doc:doc with {Bodies=doc.Bodies.SetItem(id,b with {Appearance=appearance})};
    });
    public static ICadDocumentCommand SetVisibility(BodyId id,bool visible)=>new EditDocumentCommand("修改可见性",doc=>
    {
        var b=EditableBody(doc,id);return b.IsVisible==visible?doc:doc with {Bodies=doc.Bodies.SetItem(id,b with {IsVisible=visible})};
    });
    public static ICadDocumentCommand MoveSlot(DefinitionId owner,ComponentSlotId id,RigidTransform3d placement)=>new EditDocumentCommand("移动实例",doc=>
    {
        placement.Validate();var a=(AssemblyDefinition)doc.Definitions[owner];var index=a.Children.FindIndex(x=>x.Id==id);
        if(index<0)throw new CadValidationException("Slot not in this assembly.");
        if(a.Children[index].LocalTransform==placement)return doc;
        return doc with {Definitions=doc.Definitions.SetItem(owner,a with {Children=a.Children.SetItem(index,a.Children[index] with {LocalTransform=placement})})};
    });
    private static CadBody EditableBody(DocumentSnapshot doc,BodyId id)
    {
        var b=doc.Bodies[id];if(doc.Layers[b.LayerId].IsLocked)throw new CadValidationException("Layer is locked.");return b;
    }
}
internal static class ArrayExtensions
{
    public static int FindIndex<T>(this ImmutableArray<T> array,Func<T,bool> predicate)
    {for(int i=0;i<array.Length;i++)if(predicate(array[i]))return i;return -1;}
}
