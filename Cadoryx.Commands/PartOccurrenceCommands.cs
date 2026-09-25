using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;

namespace Cadoryx.Commands;

/// <summary>Copies a part's editable identity graph while retaining immutable geometry assets.</summary>
public sealed class MakePartIndependentCommand(OccurrencePath path) : ICadDocumentCommand
{
    public string Name => Strings.ResourceManager.GetString("MakePartIndependent",Strings.Culture)??"Make part independent";
    public OccurrencePath? ResultPath { get; private set; }
    public DefinitionId? ResultPartId { get; private set; }

    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document=context.Snapshot;
        var selected=OccurrencePlacement.Resolve(document,path);
        if(document.Definitions[selected.Slot.DefinitionId] is not PartDefinition source)
            throw new CadValidationException("Selected instance is not a part.");
        if(document.EnumerateOccurrences().Count(o=>o.DefinitionId==source.Id)<=1)
            throw new CadValidationException("Part is already used by only one instance.");

        var counts=document.EnumerateOccurrences().GroupBy(o=>o.DefinitionId).ToDictionary(g=>g.Key,g=>g.Count());
        var definitions=document.Definitions;
        var ids=path.Slots.ToArray();
        var ownerId=document.RootAssemblyId;
        DefinitionId parentOwner=default;ComponentSlotId parentSlot=default;
        for(int depth=0;depth<ids.Length;depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner=(AssemblyDefinition)definitions[ownerId];
            if(ownerId!=document.RootAssemblyId&&counts.GetValueOrDefault(ownerId)>1)
            {
                var newId=DefinitionId.New();
                var replacements=owner.Children.ToDictionary(s=>s.Id,s=>ComponentSlotId.New());
                var clone=owner with{Id=newId,Children=owner.Children.Select(s=>s with{Id=replacements[s.Id]}).ToImmutableArray()};
                definitions=definitions.Add(newId,clone);
                var parent=(AssemblyDefinition)definitions[parentOwner];
                int parentIndex=parent.Children.FindIndex(s=>s.Id==parentSlot);
                definitions=definitions.SetItem(parentOwner,parent with{Children=parent.Children.SetItem(parentIndex,
                    parent.Children[parentIndex] with{DefinitionId=newId})});
                ids[depth]=replacements[ids[depth]];
                owner=clone;ownerId=newId;
            }
            var slot=owner.Children.Single(s=>s.Id==ids[depth]);
            parentOwner=ownerId;parentSlot=slot.Id;ownerId=slot.DefinitionId;
        }
        var partId=DefinitionId.New();
        var featureIds=source.Features.ToDictionary(id=>id,_=>FeatureId.New());
        var bodyIds=source.Bodies.ToDictionary(id=>id,_=>BodyId.New());
        foreach(var featureId in source.Features)
        {
            var feature=document.Features[featureId];
            if(!bodyIds.ContainsKey(feature.OutputBodyId))bodyIds.Add(feature.OutputBodyId,BodyId.New());
        }
        var sketchIds=document.Sketches.Values.Where(s=>s.PartId==source.Id)
            .ToDictionary(s=>s.Id,_=>SketchId.New());
        TopologyReference CopyReference(TopologyReference reference)
        {
            if(!featureIds.TryGetValue(reference.FeatureId,out var mappedFeature)||
               !bodyIds.TryGetValue(reference.OutputBodyId,out var mappedBody))
                throw new CadValidationException("Part has a topology reference outside its own feature graph.");
            return reference with{Id=TopologyReferenceId.New(),FeatureId=mappedFeature,OutputBodyId=mappedBody};
        }
        var references=document.TopologyReferences;
        var referenceIds=new Dictionary<TopologyReferenceId,TopologyReference>();
        foreach(var reference in document.TopologyReferences.Values.Where(r=>featureIds.ContainsKey(r.FeatureId)))
        {
            var copy=CopyReference(reference);
            references=references.Add(copy.Id,copy);
            referenceIds.Add(reference.Id,copy);
        }
        var features=document.Features;
        foreach(var oldId in source.Features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var feature=document.Features[oldId];
            if(feature.Inputs.Any(input=>!featureIds.ContainsKey(input)))
                throw new CadValidationException("Part feature depends on another part.");
            FeatureTopologyBinding? binding=null;
            if(feature.TopologyBinding is {} oldBinding)
            {
                if(!featureIds.TryGetValue(oldBinding.TargetFeatureId,out var target))
                    throw new CadValidationException("Part binding target belongs to another part.");
                TopologyReference? origin=null;
                if(oldBinding.Origin is {} oldOrigin)
                    origin=referenceIds.TryGetValue(oldOrigin.Id,out var mapped)?mapped:CopyReference(oldOrigin);
                binding=oldBinding with{TargetFeatureId=target,Origin=origin,
                    ExactEdge=oldBinding.ExactEdge is {} edge?edge with{FeatureId=target}:null,
                    SupportFace=oldBinding.SupportFace is {} face?face with{FeatureId=target}:null};
            }
            var sketchSource=feature.SketchSource is {} sketch
                ?sketch with{SketchId=sketchIds[sketch.SketchId]}:null;
            features=features.Add(featureIds[oldId],feature with{Id=featureIds[oldId],PartId=partId,
                Inputs=feature.Inputs.Select(id=>featureIds[id]).ToImmutableArray(),
                OutputBodyId=bodyIds[feature.OutputBodyId],SketchSource=sketchSource,TopologyBinding=binding});
        }
        var bodies=document.Bodies;
        foreach(var oldId in source.Bodies)
        {
            var body=document.Bodies[oldId];
            bodies=bodies.Add(bodyIds[oldId],body with{Id=bodyIds[oldId],PartId=partId,
                Producer=body.Producer is {} producer?featureIds[producer]:null});
        }
        var sketches=document.Sketches;
        foreach(var (oldId,newId) in sketchIds)
        {
            var sketch=document.Sketches[oldId];
            sketches=sketches.Add(newId,sketch with{Id=newId,PartId=partId});
        }
        definitions=definitions.Add(partId,source with{Id=partId,
            Bodies=source.Bodies.Select(id=>bodyIds[id]).ToImmutableArray(),
            Features=source.Features.Select(id=>featureIds[id]).ToImmutableArray()});
        var parentAssembly=(AssemblyDefinition)definitions[parentOwner];
        int index=parentAssembly.Children.FindIndex(s=>s.Id==parentSlot);
        definitions=definitions.SetItem(parentOwner,parentAssembly with{Children=parentAssembly.Children.SetItem(index,
            parentAssembly.Children[index] with{DefinitionId=partId})});
        ResultPath=new(document.Id,ids);
        ResultPartId=partId;
        return Task.FromResult(new PreparedDocumentEdit((document with{Definitions=definitions,Bodies=bodies,
            Features=features,Sketches=sketches,TopologyReferences=references}).WithNewState()));
    }
}
