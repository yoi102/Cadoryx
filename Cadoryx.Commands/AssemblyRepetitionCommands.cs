using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

public static class AssemblyRepetitionCommands
{
    public static ICadDocumentCommand Set(OccurrencePath source,AssemblyRepetition pattern)=>new EditDocumentCommand("Edit assembly array",doc=>
    {
        pattern.Validate();var placement=OccurrencePlacement.Resolve(doc,source);
        if(!placement.CanMoveIndependently||placement.OwnerId!=pattern.Owner||placement.Slot.Id!=pattern.Source)
            throw new CadValidationException("Select the exact array source under an independent parent assembly.");
        if(doc.AssemblyPatterns.TryGetValue(pattern.Id,out var old))
        {
            if(old.Owner!=pattern.Owner||old.Source!=pattern.Source||old.Kind!=pattern.Kind)
                throw new CadValidationException("An array edit cannot change its owner, source or layout kind.");
            if(!old.Mirrors.SequenceEqual(pattern.Mirrors))throw new CadValidationException("Mirror output identities cannot be changed by a layout edit.");
            if(!old.CopySlots.Take(Math.Min(old.CopySlots.Length,pattern.CopySlots.Length)).SequenceEqual(pattern.CopySlots.Take(old.CopySlots.Length)))
                throw new CadValidationException("Surviving array slot identities must be retained.");
        }
        else if(!pattern.Mirrors.IsEmpty)throw new CadValidationException("A new mirror cannot adopt existing derived definitions.");
        if(pattern.CopySlots.Except(old?.CopySlots??[]).Any(id=>doc.Definitions.Values.OfType<AssemblyDefinition>().Any(a=>a.Children.Any(s=>s.Id==id))||
            doc.AssemblyConstraints.Values.Any(c=>c.PrimaryPath.Slots.Contains(id)||c.SecondaryPath?.Slots.Contains(id)==true)))
            throw new CadValidationException("New array output identities are already in use.");
        var removed=old is null?[]:old.CopySlots.Except(pattern.CopySlots).ToArray();
        var owner=(AssemblyDefinition)doc.Definitions[pattern.Owner];
        return doc with{AssemblyPatterns=doc.AssemblyPatterns.SetItem(pattern.Id,pattern),
            Definitions=doc.Definitions.SetItem(owner.Id,owner with{Children=owner.Children.Where(s=>!removed.Contains(s.Id)).ToImmutableArray()})};
    });
    public static ICadDocumentCommand Detach(Guid id)=>new EditDocumentCommand("Detach assembly array",doc=>
    {
        if(!doc.AssemblyPatterns.ContainsKey(id))throw new CadValidationException("Assembly array no longer exists.");
        if(doc.Parameters.Parameters.Any(p=>p.Reference?.Kind==ParameterTargetKind.AssemblyPattern&&p.Reference.Owner==id)||
            doc.Parameters.Bindings.Any(b=>b.Target.Kind==ParameterTargetKind.AssemblyPattern&&b.Target.Owner==id))
            throw new CadValidationException("Detach array parameter bindings and references before removing its layout.");
        return doc with{AssemblyPatterns=doc.AssemblyPatterns.Remove(id)};
    });
}

/// <summary>Runs in the initiating transaction before derived sections and drawings.</summary>
public static class AssemblyRepetitionUpdate
{
    public static DocumentSnapshot Apply(DocumentSnapshot before,DocumentSnapshot next,CancellationToken token,bool mirrorsReady=true,IReadOnlySet<Guid>? patternIds=null)
    {
        if(next.AssemblyPatterns.Count==0)return next;
        var changedSlots=new HashSet<ComponentSlotId>();
        var depth=next.EnumerateOccurrences().GroupBy(o=>o.DefinitionId).ToDictionary(g=>g.Key,g=>g.Max(o=>o.Path.Slots.Length));
        foreach(var pattern in next.AssemblyPatterns.Values.OrderBy(p=>p.Owner==next.RootAssemblyId?0:depth.GetValueOrDefault(p.Owner,129)).ThenBy(p=>p.Id))
        {
            if(patternIds is not null&&!patternIds.Contains(pattern.Id))continue;
            if(!mirrorsReady&&pattern.Kind==RepetitionKind.Mirror)continue;
            token.ThrowIfCancellationRequested();pattern.Validate();var (owner,seed)=pattern.Resolve(next);
            var outputDefinition=AssemblyMirrors.OutputDefinition(pattern,seed);
            var refreshed=pattern.Refresh(next);refreshed.Validate();
            before.AssemblyPatterns.TryGetValue(pattern.Id,out var old);
            AssemblyDefinition? previous=old is null?null:before.Definitions.GetValueOrDefault(old.Owner) as AssemblyDefinition;
            if(old is not null)
            {
                foreach(var id in old.CopySlots.Intersect(pattern.CopySlots))
                {
                    var original=previous!.Children.Single(s=>s.Id==id);var current=owner.Children.SingleOrDefault(s=>s.Id==id);
                    bool unchanged=current is not null&&current.DefinitionId==original.DefinitionId&&DatumPlanes.SameFrame(current.LocalTransform,original.LocalTransform);
                    bool applied=current is not null&&current.DefinitionId==outputDefinition&&DatumPlanes.SameFrame(current.LocalTransform,refreshed.Placement(refreshed.CopySlots.IndexOf(id)+1,seed.LocalTransform));
                    if(!unchanged&&!applied)
                        throw new CadValidationException("Generated instance pose or definition is controlled by its array. Detach the layout before editing it.");
                    if(!unchanged)changedSlots.Add(id);
                }
                foreach(var id in old.CopySlots.Except(pattern.CopySlots))changedSlots.Add(id);
            }
            var children=owner.Children.ToBuilder();bool changed=false;
            for(int i=0;i<refreshed.CopySlots.Length;i++)
            {
                var id=refreshed.CopySlots[i];var placement=refreshed.Placement(i+1,seed.LocalTransform);
                int index=-1;for(int j=0;j<children.Count;j++)if(children[j].Id==id){index=j;break;}
                if(index<0)
                {
                    children.Add(seed with{Id=id,DefinitionId=outputDefinition,Name=seed.Name+" · "+(i+2),LocalTransform=placement});changed=true;changedSlots.Add(id);
                }
                else
                {
                    var current=children[index];
                    if(current.DefinitionId!=outputDefinition||!DatumPlanes.SameFrame(current.LocalTransform,placement))
                    {children[index]=current with{DefinitionId=outputDefinition,LocalTransform=placement};changed=true;changedSlots.Add(id);}
                }
            }
            if(changed&&owner.Id!=next.RootAssemblyId&&next.EnumerateOccurrences().Count(o=>o.DefinitionId==owner.Id)>1)
                throw new CadValidationException("Shared parent assembly must be made independent before updating its array.");
            next=next with{AssemblyPatterns=next.AssemblyPatterns.SetItem(pattern.Id,refreshed),
                Definitions=changed?next.Definitions.SetItem(owner.Id,owner with{Children=children.ToImmutable()}):next.Definitions};
        }
        if(patternIds is null)AssemblyRepetitions.ValidateApplied(next,mirrorsReady);
        if(changedSlots.Count>0)
        {
            var occurrences=next.EnumerateOccurrences().ToDictionary(o=>o.Path);
            foreach(var constraint in next.AssemblyConstraints.Values.Where(c=>c.IsEnabled&&
                (c.PrimaryPath.Slots.Any(changedSlots.Contains)||c.SecondaryPath?.Slots.Any(changedSlots.Contains)==true)))
            {
                token.ThrowIfCancellationRequested();var evaluation=constraint.Evaluate(next,occurrences);
                if(evaluation.Status is AssemblyConstraintStatus.Unsatisfied or AssemblyConstraintStatus.MissingInstance or AssemblyConstraintStatus.DefinitionChanged)
                    throw new CadValidationException($"Assembly array conflicts with enabled constraint '{constraint.Name}': {evaluation.Explanation}");
            }
        }
        return next;
    }
}
