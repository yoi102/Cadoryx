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
        var branchChanges=new List<(int Depth,IReadOnlyDictionary<ComponentSlotId,ComponentSlotId> Children,DefinitionId Before,DefinitionId After)>();
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
                branchChanges.Add((depth,replacements,ownerId,newId));
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
        var identity=new PartIdentityClone(document,source.Id,partId,featureIds,bodyIds,sketchIds);
        var references=document.TopologyReferences;
        var referenceIds=new Dictionary<TopologyReferenceId,TopologyReference>();
        foreach(var reference in document.TopologyReferences.Values.Where(r=>featureIds.ContainsKey(r.FeatureId)))
        {
            var copy=identity.Reference(reference);
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
                    origin=referenceIds.TryGetValue(oldOrigin.Id,out var mapped)?mapped:identity.Reference(oldOrigin);
                binding=oldBinding with{TargetFeatureId=target,Origin=origin,
                    ExactEdge=oldBinding.ExactEdge is {} edge?edge with{FeatureId=target}:null,
                    SupportFace=oldBinding.SupportFace is {} face?face with{FeatureId=target}:null};
            }
            var recipe=identity.Recipe(feature.Recipe);var history=feature.TopologyHistory;
            if(history is not null&&feature.Recipe is RepetitionRecipe oldLayout&&recipe is RepetitionRecipe newLayout)
            {
                var copies=oldLayout.CopyIds.Zip(newLayout.CopyIds).ToDictionary(pair=>pair.First,pair=>pair.Second);
                history=history with{Entries=history.Entries.Select(e=>e with{CopyId=e.CopyId is {} id&&copies.TryGetValue(id,out var replacement)?replacement:
                    throw new CadValidationException("Independent repetition history has a foreign copy path.")}).ToImmutableArray()};
            }
            features=features.Add(featureIds[oldId],feature with{Id=featureIds[oldId],PartId=partId,
                Inputs=feature.Inputs.Select(id=>featureIds[id]).ToImmutableArray(),
                OutputBodyId=bodyIds[feature.OutputBodyId],Recipe=recipe,TopologyHistory=history,
                SketchSource=identity.Profile(feature.SketchSource),TopologyBinding=binding});
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
            sketches=sketches.Add(newId,sketch with{Id=newId,PartId=partId,
                Projections=sketch.Projections.Select(p=>p with{SourceSketch=identity.Sketch(p.SourceSketch)}).ToImmutableArray(),
                NativeProjections=sketch.NativeProjections.Select(p=>p with{Selection=identity.Exact(p.Selection)}).ToImmutableArray()});
        }
        definitions=definitions.Add(partId,source with{Id=partId,
            Bodies=source.Bodies.Select(id=>bodyIds[id]).ToImmutableArray(),
            Features=source.Features.Select(id=>featureIds[id]).ToImmutableArray()});
        var parentAssembly=(AssemblyDefinition)definitions[parentOwner];
        int index=parentAssembly.Children.FindIndex(s=>s.Id==parentSlot);
        // Explicit source-to-copy links participate in this identity clone. Apply the
        // definition change before the array postprocessor sees a shared cloned owner.
        var linkedSlots=new HashSet<ComponentSlotId>{parentSlot};
        foreach(var pattern in document.AssemblyPatterns.Values.Where(p=>p.Kind!=RepetitionKind.Mirror&&p.Owner==selected.OwnerId&&p.Source==selected.Slot.Id))
            foreach(var id in pattern.CopySlots)
            {
                var branch=branchChanges.LastOrDefault(b=>b.Before==selected.OwnerId);
                linkedSlots.Add(branch.Children is not null?branch.Children[id]:id);
            }
        definitions=definitions.SetItem(parentOwner,parentAssembly with{Children=parentAssembly.Children.SetItem(index,
            parentAssembly.Children[index] with{DefinitionId=partId}).Select(s=>linkedSlots.Contains(s.Id)?s with{DefinitionId=partId}:s).ToImmutableArray()});
        var queries=document.HistoryQueries;
        foreach(var query in document.HistoryQueries.Values.Where(q=>featureIds.ContainsKey(q.TargetFeatureId)&&featureIds.ContainsKey(q.Source.FeatureId)))
        {var copy=query with{Id=HistoryQueryId.New(),Source=identity.Reference(query.Source),TargetFeatureId=identity.Feature(query.TargetFeatureId)};queries=queries.Add(copy.Id,copy);}
        var sections=document.AssociatedSections;
        foreach(var section in document.AssociatedSections.Values.Where(s=>featureIds.ContainsKey(s.FeatureId)))
        {var copy=section with{FeatureId=identity.Feature(section.FeatureId)};sections=sections.Add(copy.FeatureId,copy);}
        var remap=new OccurrenceIdentityRemap(document,path,identity);
        foreach(var (depth,children,before,after) in branchChanges)remap.Add(depth,children,before,after);
        var candidate=remap.Apply(document with{Definitions=definitions,Bodies=bodies,Features=features,Sketches=sketches,
            TopologyReferences=references,HistoryQueries=queries,AssociatedSections=sections,
            Datums=identity.Datums(),Parameters=identity.Parameters()});
        cancellationToken.ThrowIfCancellationRequested();
        ResultPath=new(document.Id,ids);
        ResultPartId=partId;
        return Task.FromResult(new PreparedDocumentEdit(candidate.WithNewState()));
    }
}
