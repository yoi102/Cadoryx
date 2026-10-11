using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

/// <summary>Rebuilds only explicitly mapped reflected bodies, within the original edit's resource lifetime.</summary>
public static class AssemblyMirrorUpdate
{
    public static async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,DocumentSnapshot next,CancellationToken token)
    {
        var resources=new List<IDisposable>();
        try
        {
            foreach(var previous in context.Snapshot.AssemblyPatterns.Values.Where(p=>p.Kind==RepetitionKind.Mirror&&next.AssemblyPatterns.ContainsKey(p.Id)))
                foreach(var link in previous.Mirrors)
                {
                    if(next.Definitions.GetValueOrDefault(link.Target)!=context.Snapshot.Definitions[link.Target]||
                        link.Bodies.Any(b=>next.Bodies.GetValueOrDefault(b.Target)!=context.Snapshot.Bodies[b.Target])||
                        next.Sketches.Values.Any(s=>s.PartId==link.Target)||
                        next.Datums.Planes.Values.Any(d=>d.PartId==link.Target)||next.Datums.Axes.Values.Any(d=>d.PartId==link.Target)||next.Datums.Points.Values.Any(d=>d.PartId==link.Target))
                        throw new CadValidationException("Mirror definitions are controlled by their source. Detach the mirror before editing a derived definition.");
                }
            foreach(var patternId in AssemblyMirrors.OrderedPatterns(next))
            {
                var pattern=next.AssemblyPatterns[patternId];
                token.ThrowIfCancellationRequested();pattern.Validate();var (_,seed)=pattern.Resolve(next);
                var old=pattern.Mirrors.ToDictionary(m=>m.Source);var links=new Dictionary<DefinitionId,AssemblyMirrorDefinition>();
                var visiting=new HashSet<DefinitionId>();
                async Task<DefinitionId> Mirror(DefinitionId sourceId,int depth)
                {
                    token.ThrowIfCancellationRequested();
                    if(depth>128)throw new CadValidationException("Mirror hierarchy exceeds its definition or depth limit.");
                    if(!visiting.Add(sourceId))throw new CadValidationException("Mirror hierarchy contains a definition cycle.");
                    if(links.TryGetValue(sourceId,out var cached)){visiting.Remove(sourceId);return cached.Target;}
                    if(links.Count>=4096)throw new CadValidationException("Mirror hierarchy exceeds its definition or depth limit.");
                    var previous=old.GetValueOrDefault(sourceId);var targetId=previous?.Target??DefinitionId.New();
                    var source=next.Definitions.GetValueOrDefault(sourceId)??throw new CadValidationException("Mirror source definition is missing.");
                    if(source is PartDefinition part)
                    {
                        var outputs=ImmutableArray.CreateBuilder<AssemblyMirrorBody>();var ids=ImmutableArray.CreateBuilder<BodyId>();
                        foreach(var id in part.Bodies)
                        {
                            token.ThrowIfCancellationRequested();var body=next.Bodies[id];
                            if(body.Producer is {} producer&&(next.Features[producer].IsStale||next.Features[producer].IsSuppressed))
                                throw new CadValidationException("Mirror source has a stale active body. Resolve its authoring inputs first.");
                            var prior=previous?.Bodies.SingleOrDefault(b=>b.Source==id);var targetBody=prior?.Target??BodyId.New();
                            GeometryAssetRef geometry;
                            if(prior is not null&&prior.SourceAsset==body.Geometry.AssetId&&prior.SourceRevision==body.Geometry.Revision&&next.Bodies.TryGetValue(targetBody,out var current))geometry=current.Geometry;
                            else
                            {
                                var recipe=new RepetitionRecipe(body.Geometry,RepetitionKind.Mirror,RigidTransform3d.Identity,1,0,[Guid.NewGuid()],IncludeSource:false);
                                var result=await context.Kernel.EvaluateAsync(recipe,context.Assets,token).ConfigureAwait(false);resources.Add(result);geometry=result.Geometry;
                                if(geometry.Kind!=body.Geometry.Kind)throw new CadValidationException("Exact mirror changed the source geometry kind.");
                            }
                            next=next with{Bodies=next.Bodies.SetItem(targetBody,AssemblyMirrors.CopyBody(body,targetBody,targetId,geometry))};ids.Add(targetBody);
                            outputs.Add(new(id,targetBody,body.Geometry.AssetId,body.Geometry.Revision,geometry.AssetId,geometry.Revision));
                        }
                        if(previous is not null)next=next with{Bodies=next.Bodies.RemoveRange(previous.Bodies.Select(b=>b.Target).Except(ids))};
                        var target=new PartDefinition(targetId,source.Name,ids.ToImmutable(),[]);
                        if(next.Definitions.GetValueOrDefault(targetId) is not PartDefinition existing||existing.Name!=target.Name||!existing.Bodies.SequenceEqual(target.Bodies)||!existing.Features.IsEmpty)
                            next=next with{Definitions=next.Definitions.SetItem(targetId,target)};
                        links.Add(sourceId,new(sourceId,targetId,outputs.ToImmutable(),[]));
                    }
                    else if(source is AssemblyDefinition assembly)
                    {
                        var children=ImmutableArray.CreateBuilder<ComponentSlot>();var slots=ImmutableArray.CreateBuilder<AssemblyMirrorSlot>();
                        foreach(var child in assembly.Children)
                        {
                            var targetChild=await Mirror(child.DefinitionId,depth+1).ConfigureAwait(false);
                            var id=previous?.Slots.SingleOrDefault(s=>s.Source==child.Id)?.Target??ComponentSlotId.New();
                            children.Add(child with{Id=id,DefinitionId=targetChild,LocalTransform=AssemblyMirrors.Placement(RigidTransform3d.Identity,child.LocalTransform)});
                            slots.Add(new(child.Id,id));
                        }
                        var target=new AssemblyDefinition(targetId,source.Name,children.ToImmutable());
                        if(next.Definitions.GetValueOrDefault(targetId) is not AssemblyDefinition existing||existing.Name!=target.Name||!existing.Children.SequenceEqual(target.Children))
                            next=next with{Definitions=next.Definitions.SetItem(targetId,target)};
                        links.Add(sourceId,new(sourceId,targetId,[],slots.ToImmutable()));
                    }
                    else throw new CadValidationException("Unsupported mirror source definition.");
                    visiting.Remove(sourceId);return targetId;
                }
                await Mirror(seed.DefinitionId,0).ConfigureAwait(false);
                next=next with{AssemblyPatterns=next.AssemblyPatterns.SetItem(pattern.Id,pattern with{Mirrors=links.Values.OrderBy(l=>l.Source.Value).ToImmutableArray()})};
                // Make this exact output available to subsequent outer mirror layouts.
                next=AssemblyRepetitionUpdate.Apply(context.Snapshot,next,token,patternIds:new HashSet<Guid>{pattern.Id});
            }
            return new(next,resources);
        }
        catch{foreach(var resource in resources)resource.Dispose();throw;}
    }
}
