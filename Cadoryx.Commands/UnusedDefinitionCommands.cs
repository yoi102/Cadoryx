using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;

namespace Cadoryx.Commands;

/// <summary>Removes unused definitions only when no saved diagnostic still names their features.</summary>
public static class UnusedDefinitionCommands
{
    public static int CountRemovable(DocumentSnapshot document)=>Removable(document).Count;

    public static ICadDocumentCommand Prune()=>new EditDocumentCommand(
        Strings.ResourceManager.GetString("PruneUnusedDefinitions",Strings.Culture)??"Remove unused definitions",document=>
    {
        var unused=Removable(document);
        if(unused.Count==0)return document;
        var definitions=document.Definitions;
        var bodies=document.Bodies;
        var features=document.Features;
        var sketches=document.Sketches;
        var externalParts=document.ExternalParts;
        foreach(var id in unused)
        {
            if(definitions[id] is PartDefinition part)
            {
                foreach(var body in part.Bodies)bodies=bodies.Remove(body);
                foreach(var feature in part.Features)features=features.Remove(feature);
                foreach(var sketch in sketches.Values.Where(s=>s.PartId==id).ToArray())sketches=sketches.Remove(sketch.Id);
            }
            definitions=definitions.Remove(id);
            externalParts=externalParts.Remove(id);
        }
        return document with{Definitions=definitions,Bodies=bodies,Features=features,Sketches=sketches,
            ExternalParts=externalParts};
    });

    private static HashSet<DefinitionId> Removable(DocumentSnapshot document)
    {
        var reachable=new HashSet<DefinitionId>();
        void Visit(DefinitionId id)
        {
            if(!reachable.Add(id))return;
            if(document.Definitions[id] is AssemblyDefinition assembly)
                foreach(var slot in assembly.Children)Visit(slot.DefinitionId);
        }
        Visit(document.RootAssemblyId);
        var protectedFeatures=document.TopologyReferences.Values.Select(r=>r.FeatureId)
            .Concat(document.HistoryQueries.Values.SelectMany(q=>new[]{q.Source.FeatureId,q.TargetFeatureId})).ToHashSet();
        bool changed;
        do
        {
            changed=false;
            foreach(var feature in document.Features.Values.Where(f=>reachable.Contains(f.PartId)||
                protectedFeatures.Contains(f.Id)))
            {
                if(feature.TopologyBinding is not {} binding)continue;
                changed|=protectedFeatures.Add(binding.TargetFeatureId);
                if(binding.Origin is {} origin)changed|=protectedFeatures.Add(origin.FeatureId);
            }
        }while(changed);
        var constrained=document.AssemblyConstraints.Values.SelectMany(c=>c.SecondaryDefinitionId is {} secondary
            ?new[]{c.PrimaryDefinitionId,secondary}:new[]{c.PrimaryDefinitionId}).ToHashSet();
        return document.Definitions.Keys.Where(id=>!reachable.Contains(id)&&!constrained.Contains(id)&&
            (document.Definitions[id] is not PartDefinition part||!part.Features.Any(protectedFeatures.Contains)))
            .ToHashSet();
    }
}
