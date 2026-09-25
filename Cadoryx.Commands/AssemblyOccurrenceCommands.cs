using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;

namespace Cadoryx.Commands;

/// <summary>Edits exact occurrence paths. Shared assembly definitions require explicit isolation.</summary>
public static class AssemblyOccurrenceCommands
{
    private static string Label(string key,string fallback)=>Strings.ResourceManager.GetString(key,Strings.Culture)??fallback;
    public static ICadDocumentCommand CreateAssembly(OccurrencePath parent,DefinitionId definitionId,ComponentSlotId slotId,
        string name,RigidTransform3d local)=>new EditDocumentCommand(Label("CreateSiblingAssembly","Create assembly"),document=>
    {
        CadGuard.Id(definitionId);CadGuard.Id(slotId);CadGuard.Name(name);local.Validate();
        if(document.Definitions.ContainsKey(definitionId))throw new CadValidationException("Assembly ID is already in use.");
        var owner=Owner(document,parent);
        if(SlotIdReserved(document,slotId))
            throw new CadValidationException("Instance ID is already in use.");
        var created=new AssemblyDefinition(definitionId,name,[]);
        return document with{Definitions=document.Definitions.Add(definitionId,created).SetItem(owner.Id,
            owner with{Children=owner.Children.Add(new(slotId,definitionId,name,local))})};
    });

    public static ICadDocumentCommand Insert(OccurrencePath parent,DefinitionId definition,ComponentSlotId slotId,
        string name,RigidTransform3d local)=>new EditDocumentCommand(Label("InsertInstance","Insert instance"),document=>
    {
        CadGuard.Id(slotId);CadGuard.Name(name);local.Validate();
        if(!document.Definitions.ContainsKey(definition))throw new CadValidationException("Instance definition is missing.");
        var owner=Owner(document,parent);
        if(SlotIdReserved(document,slotId))
            throw new CadValidationException("Instance ID is already in use.");
        return WithOwner(document,owner with{Children=owner.Children.Add(new(slotId,definition,name,local))});
    });

    public static ICadDocumentCommand Replace(OccurrencePath path,DefinitionId definition)=>new EditDocumentCommand(Label("ReplaceInstance","Replace instance"),document=>
    {
        if(!document.Definitions.ContainsKey(definition))throw new CadValidationException("Replacement definition is missing.");
        var (owner,slot,index)=Slot(document,path);
        return slot.DefinitionId==definition?document:WithOwner(document,owner with
            {Children=owner.Children.SetItem(index,slot with{DefinitionId=definition})});
    });

    public static ICadDocumentCommand Remove(OccurrencePath path)=>new EditDocumentCommand(Label("RemoveInstance","Remove instance"),document=>
    {
        var (owner,_,index)=Slot(document,path);
        return WithOwner(document,owner with{Children=owner.Children.RemoveAt(index)});
    });

    public static ICadDocumentCommand Reparent(OccurrencePath path,OccurrencePath destination)=>
        new EditDocumentCommand(Label("ReparentInstance","Reparent instance"),document=>
    {
        if(path.DocumentId!=destination.DocumentId||destination.Slots.Length>=path.Slots.Length&&
           destination.Slots.Take(path.Slots.Length).SequenceEqual(path.Slots))
            throw new CadValidationException("An instance cannot be moved inside itself.");
        var (sourceOwner,slot,index)=Slot(document,path);
        var targetOwner=Owner(document,destination);
        if(sourceOwner.Id==targetOwner.Id)return document;
        var world=World(document,path);
        var parentWorld=World(document,destination);
        var local=parentWorld.Inverse()*world;local.Validate();
        var definitions=document.Definitions.SetItem(sourceOwner.Id,sourceOwner with{Children=sourceOwner.Children.RemoveAt(index)});
        var target=(AssemblyDefinition)definitions[targetOwner.Id];
        definitions=definitions.SetItem(target.Id,target with{Children=target.Children.Add(slot with{LocalTransform=local})});
        return document with{Definitions=definitions};
    });

    public static MakeAssemblyIndependentCommand MakeIndependent(OccurrencePath path)=>new(path);

    private static bool SlotIdReserved(DocumentSnapshot document,ComponentSlotId id)=>
        document.Definitions.Values.OfType<AssemblyDefinition>().Any(a=>a.Children.Any(s=>s.Id==id))||
        document.AssemblyConstraints.Values.Any(c=>c.PrimaryPath.Slots.Contains(id)||c.SecondaryPath?.Slots.Contains(id)==true);

    public static MakePartIndependentCommand MakePartIndependent(OccurrencePath path)=>new(path);

    public static ICadDocumentCommand RenameOccurrence(OccurrencePath path,string name)=>
        new EditDocumentCommand(Label("RenameOccurrence","Rename instance"),document=>
    {
        CadGuard.Name(name);
        var (owner,slot,index)=Slot(document,path);
        return slot.Name==name?document:WithOwner(document,owner with
            {Children=owner.Children.SetItem(index,slot with{Name=name})});
    });

    public static ICadDocumentCommand RenameDefinition(DefinitionId id,string name)=>
        new EditDocumentCommand(Label("RenameDefinition","Rename definition"),document=>
    {
        CadGuard.Name(name);
        if(id==document.RootAssemblyId||!document.Definitions.TryGetValue(id,out var definition))
            throw new CadValidationException("Only an existing non-root definition can be renamed.");
        return definition.Name==name?document:document with
            {Definitions=document.Definitions.SetItem(id,definition with{Name=name})};
    });

    private static AssemblyDefinition Owner(DocumentSnapshot document,OccurrencePath path)
    {
        if(path.DocumentId!=document.Id)throw new CadValidationException("Instance path belongs to another document.");
        if(path.Slots.IsEmpty)return (AssemblyDefinition)document.Definitions[document.RootAssemblyId];
        var occurrence=OccurrencePlacement.Resolve(document,path);
        if(document.Definitions[occurrence.Slot.DefinitionId] is not AssemblyDefinition assembly)
            throw new CadValidationException("Target instance is not an assembly.");
        if(document.EnumerateOccurrences().Count(o=>o.DefinitionId==assembly.Id)>1)
            throw new CadValidationException("Shared assembly must be made independent before editing its children.");
        return assembly;
    }

    private static (AssemblyDefinition Owner,ComponentSlot Slot,int Index) Slot(DocumentSnapshot document,OccurrencePath path)
    {
        var placement=OccurrencePlacement.Resolve(document,path);
        if(!placement.CanMoveIndependently)
            throw new CadValidationException("Shared parent must be made independent before editing this instance.");
        var owner=(AssemblyDefinition)document.Definitions[placement.OwnerId];
        int index=owner.Children.FindIndex(s=>s.Id==placement.Slot.Id);
        return (owner,placement.Slot,index);
    }

    private static RigidTransform3d World(DocumentSnapshot document,OccurrencePath path)
    {
        if(path.DocumentId!=document.Id)throw new CadValidationException("Instance path belongs to another document.");
        if(path.Slots.IsEmpty)return RigidTransform3d.Identity;
        var owner=document.RootAssemblyId;var world=RigidTransform3d.Identity;
        foreach(var id in path.Slots)
        {
            if(document.Definitions[owner] is not AssemblyDefinition assembly)
                throw new CadValidationException("Path crosses a part definition.");
            var slot=assembly.Children.SingleOrDefault(s=>s.Id==id)??throw new CadValidationException("Instance path no longer exists.");
            world*=slot.LocalTransform;owner=slot.DefinitionId;
        }
        return world;
    }

    private static DocumentSnapshot WithOwner(DocumentSnapshot document,AssemblyDefinition owner)=>
        document with{Definitions=document.Definitions.SetItem(owner.Id,owner)};

    public sealed class MakeAssemblyIndependentCommand(OccurrencePath path):ICadDocumentCommand
    {
        public string Name=>Label("MakeAssemblyIndependent","Make assembly independent");
        public OccurrencePath? ResultPath {get;private set;}
        public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document=context.Snapshot;
            var selected=OccurrencePlacement.Resolve(document,path);
            if(document.Definitions[selected.Slot.DefinitionId] is not AssemblyDefinition)
                throw new CadValidationException("Only assembly definitions can be made independent here.");
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
            var selectedDefinition=(AssemblyDefinition)definitions[ownerId];
            if(counts.GetValueOrDefault(ownerId)>1)
            {
                var newId=DefinitionId.New();
                var clone=selectedDefinition with{Id=newId,Children=selectedDefinition.Children.Select(s=>s with{Id=ComponentSlotId.New()}).ToImmutableArray()};
                definitions=definitions.Add(newId,clone);
                var parent=(AssemblyDefinition)definitions[parentOwner];
                int index=parent.Children.FindIndex(s=>s.Id==parentSlot);
                definitions=definitions.SetItem(parentOwner,parent with{Children=parent.Children.SetItem(index,
                    parent.Children[index] with{DefinitionId=newId})});
            }
            ResultPath=new(document.Id,ids);
            return Task.FromResult(new PreparedDocumentEdit(ReferenceEquals(definitions,document.Definitions)
                ?document:(document with{Definitions=definitions}).WithNewState()));
        }
    }
}
