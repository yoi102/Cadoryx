using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AssemblyOccurrenceTests
{
    [Fact] public async Task InsertReplaceRemoveAndReparentPreserveIdentityAndWorldPlacement()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var initial=DocumentSnapshot.Create("Assembly editing");
        var first=DefinitionId.New();var second=DefinitionId.New();var assembly=DefinitionId.New();
        initial=initial with{Definitions=initial.Definitions.Add(first,new PartDefinition(first,"A",[],[]))
            .Add(second,new PartDefinition(second,"B",[],[]))};
        var root=new OccurrencePath(initial.Id,[]);var groupSlot=ComponentSlotId.New();var item=ComponentSlotId.New();
        await using var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(AssemblyOccurrenceCommands.CreateAssembly(root,assembly,groupSlot,"Group",
            new(new(40,2,0),Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/2))));
        var groupPath=root.Append(groupSlot);
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,first,item,"Item",RigidTransform3d.Translate(10,0,0)));
        var itemPath=root.Append(item);
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Replace(itemPath,second));
        Assert.Equal(second,OccurrencePlacement.Resolve(session.Snapshot,itemPath).Slot.DefinitionId);
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Reparent(itemPath,groupPath));
        var moved=groupPath.Append(item);var world=session.Snapshot.EnumerateOccurrences().Single(o=>o.Path.Equals(moved)).WorldTransform;
        Assert.InRange((world.Translation-new Vector3d(10,0,0)).Length,0,1e-8);
        Assert.InRange((world.Rotation.Rotate(new(1,0,0))-new Vector3d(1,0,0)).Length,0,1e-8);
        Assert.Equal(second,OccurrencePlacement.Resolve(session.Snapshot,moved).Slot.DefinitionId);
        var edited=session.Snapshot;
        await session.UndoAsync();
        Assert.NotNull(OccurrencePlacement.Resolve(session.Snapshot,itemPath));
        await session.RedoAsync();Assert.Same(edited,session.Snapshot);
        var path=files.PathFor("assembly.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using(var loaded=await new CadDocumentStorage().LoadAsync(path,assets))
        {
            Assert.Equal(second,OccurrencePlacement.Resolve(loaded.Snapshot,moved).Slot.DefinitionId);
            var restoredWorld=loaded.Snapshot.EnumerateOccurrences().Single(o=>o.Path.Equals(moved)).WorldTransform;
            Assert.InRange((restoredWorld.Translation-world.Translation).Length,0,1e-8);
        }
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Remove(moved));
        Assert.DoesNotContain(session.Snapshot.EnumerateOccurrences(),o=>o.Path.Equals(moved));
        await session.UndoAsync();Assert.NotNull(OccurrencePlacement.Resolve(session.Snapshot,moved));
    }

    [Fact] public async Task SharedAssemblyIsolationChangesOnlyChosenOccurrenceAndRejectsCycles()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var doc=DocumentSnapshot.Create("Shared groups");
        var root=(AssemblyDefinition)doc.Definitions[doc.RootAssemblyId];
        var part=DefinitionId.New();var group=DefinitionId.New();var inner=ComponentSlotId.New();
        var left=ComponentSlotId.New();var right=ComponentSlotId.New();
        var shared=new AssemblyDefinition(group,"Reusable",[new(inner,part,"Child",RigidTransform3d.Translate(2,0,0))]);
        doc=doc with{Definitions=doc.Definitions.Add(part,new PartDefinition(part,"Part",[],[])).Add(group,shared)
            .SetItem(root.Id,root with{Children=[new(left,group,"Left",RigidTransform3d.Identity),
                new(right,group,"Right",RigidTransform3d.Translate(100,0,0))]})};
        var rootPath=new OccurrencePath(doc.Id,[]);var leftPath=rootPath.Append(left);var rightPath=rootPath.Append(right);
        await using var session=new CadDocumentSession(doc,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(
            DocumentEdits.MoveOccurrence(leftPath.Append(inner),RigidTransform3d.Translate(4,0,0))));
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(
            AssemblyOccurrenceCommands.Insert(leftPath,part,ComponentSlotId.New(),"Blocked",RigidTransform3d.Identity)));
        var extra=ComponentSlotId.New();
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(rootPath,part,extra,"Extra",RigidTransform3d.Identity));
        var beforeRejected=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(
            AssemblyOccurrenceCommands.Reparent(rootPath.Append(extra),leftPath)));
        Assert.Same(beforeRejected,session.Snapshot);
        var isolation=AssemblyOccurrenceCommands.MakeIndependent(leftPath);
        await session.ExecuteAsync(isolation);
        var selected=isolation.ResultPath!;Assert.Equal(leftPath,selected);
        var leftDefinition=OccurrencePlacement.Resolve(session.Snapshot,selected).Slot.DefinitionId;
        Assert.NotEqual(group,leftDefinition);
        var clonedChild=Assert.Single(((AssemblyDefinition)session.Snapshot.Definitions[leftDefinition]).Children).Id;
        Assert.NotEqual(inner,clonedChild);
        await session.ExecuteAsync(DocumentEdits.MoveOccurrence(selected.Append(clonedChild),RigidTransform3d.Translate(5,0,0)));
        Assert.Equal(new Vector3d(5,0,0),OccurrencePlacement.Resolve(session.Snapshot,selected.Append(clonedChild)).Slot.LocalTransform.Translation);
        Assert.Equal(new Vector3d(2,0,0),OccurrencePlacement.Resolve(session.Snapshot,rightPath.Append(inner)).Slot.LocalTransform.Translation);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(
            AssemblyOccurrenceCommands.Replace(selected,doc.RootAssemblyId)));
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(
            AssemblyOccurrenceCommands.Reparent(selected,selected)));
        var path=files.PathFor("independent.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
        Assert.Equal(leftDefinition,OccurrencePlacement.Resolve(loaded.Snapshot,selected).Slot.DefinitionId);
        Assert.Equal(group,OccurrencePlacement.Resolve(loaded.Snapshot,rightPath).Slot.DefinitionId);
        Assert.Equal(new Vector3d(5,0,0),OccurrencePlacement.Resolve(loaded.Snapshot,selected.Append(clonedChild)).Slot.LocalTransform.Translation);
        var recoveryRoot=files.PathFor("recovery");
        using(var writer=new CadRecoveryStore(recoveryRoot,new CadDocumentStorage()))
            await writer.WriteAsync(session.SessionId,session.Snapshot,assets,path);
        using var reader=new CadRecoveryStore(recoveryRoot,new CadDocumentStorage());
        var entry=Assert.Single((await reader.ScanAsync()).Entries);
        using var recovered=await reader.OpenAsync(entry.Key,assets);
        Assert.Equal(leftDefinition,OccurrencePlacement.Resolve(recovered.Document.Snapshot,selected).Slot.DefinitionId);
        Assert.Equal(group,OccurrencePlacement.Resolve(recovered.Document.Snapshot,rightPath).Slot.DefinitionId);
    }

    [Fact] public async Task NestedSharedPathClonesOnlyItsAssemblyBranch()
    {
        var assets=new MemoryAssetStore();var doc=DocumentSnapshot.Create("Nested shared");
        var root=(AssemblyDefinition)doc.Definitions[doc.RootAssemblyId];
        var outer=DefinitionId.New();var inner=DefinitionId.New();var part=DefinitionId.New();
        var a=ComponentSlotId.New();var b=ComponentSlotId.New();var nested=ComponentSlotId.New();var leaf=ComponentSlotId.New();
        doc=doc with{Definitions=doc.Definitions.Add(part,new PartDefinition(part,"Leaf",[],[]))
            .Add(inner,new AssemblyDefinition(inner,"Inner",[new(leaf,part,"Part",RigidTransform3d.Identity)]))
            .Add(outer,new AssemblyDefinition(outer,"Outer",[new(nested,inner,"Inner",RigidTransform3d.Identity)]))
            .SetItem(root.Id,root with{Children=[new(a,outer,"A",RigidTransform3d.Identity),
                new(b,outer,"B",RigidTransform3d.Translate(100,0,0))]})};
        await using var session=new CadDocumentSession(doc,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var source=new OccurrencePath(doc.Id,[a,nested]);
        var command=AssemblyOccurrenceCommands.MakeIndependent(source);await session.ExecuteAsync(command);
        var changed=command.ResultPath!;
        Assert.NotEqual(source,changed);Assert.Equal(a,changed.Slots[0]);Assert.NotEqual(nested,changed.Slots[1]);
        var isolatedOuter=OccurrencePlacement.Resolve(session.Snapshot,new(doc.Id,[a])).Slot.DefinitionId;
        var isolatedInner=OccurrencePlacement.Resolve(session.Snapshot,changed).Slot.DefinitionId;
        Assert.NotEqual(outer,isolatedOuter);Assert.NotEqual(inner,isolatedInner);
        Assert.Equal(inner,OccurrencePlacement.Resolve(session.Snapshot,new(doc.Id,[b,nested])).Slot.DefinitionId);
        var clonedLeaf=Assert.Single(((AssemblyDefinition)session.Snapshot.Definitions[isolatedInner]).Children).Id;
        await session.ExecuteAsync(DocumentEdits.MoveOccurrence(changed.Append(clonedLeaf),RigidTransform3d.Translate(7,0,0)));
        Assert.Equal(Vector3d.Zero,OccurrencePlacement.Resolve(session.Snapshot,new(doc.Id,[b,nested,leaf])).Slot.LocalTransform.Translation);
        Assert.Equal(new Vector3d(7,0,0),OccurrencePlacement.Resolve(session.Snapshot,changed.Append(clonedLeaf)).Slot.LocalTransform.Translation);
        session.Snapshot.Validate();
    }
}
