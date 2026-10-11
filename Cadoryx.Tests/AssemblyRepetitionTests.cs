using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AssemblyRepetitionTests
{
    [Theory][InlineData(RepetitionKind.Linear,false,8)][InlineData(RepetitionKind.Linear,false,-8)]
    [InlineData(RepetitionKind.Circular,false,Math.PI/2)][InlineData(RepetitionKind.Circular,false,-Math.PI/2)]
    [InlineData(RepetitionKind.Circular,true,0)]
    public async Task ParentLocalArraysShareDefinitionAndRetainExactSeedOrientation(RepetitionKind kind,bool full,double step)
    {
        using var files=new TestFiles();await using var f=new Fixture();var frame=new RigidTransform3d(new(1,2,1),Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/2));
        var pattern=f.Pattern(kind,4,step) with{Frame=frame,FullCircle=full};var before=f.Session.Snapshot;
        await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,pattern));var after=f.Session.Snapshot;after.Validate();
        var owner=(AssemblyDefinition)after.Definitions[f.Owner];Assert.Equal(4,owner.Children.Length);
        var first=owner.Children.Single(s=>s.Id==pattern.CopySlots[0]);Assert.Equal(f.Part,first.DefinitionId);
        var expected=kind==RepetitionKind.Linear?new Vector3d(4,2+step,1):new Vector3d(1,2+(full||step>0?3:-3),1);
        Assert.InRange((first.LocalTransform.Translation-expected).Length,0,1e-8);
        var expectedDirection=Quaterniond.FromAxisAngle(Vector3d.UnitZ,kind==RepetitionKind.Linear?.31:.31+(full?Math.PI/2:step)).Rotate(new(1,0,0));
        Assert.InRange((first.LocalTransform.Rotation.Rotate(new(1,0,0))-expectedDirection).Length,0,1e-9);
        var seedWorld=after.EnumerateOccurrences().Single(o=>o.Path.Equals(f.Path)).WorldTransform;
        var copyWorld=after.EnumerateOccurrences().Single(o=>o.Path.Equals(f.Parent.Append(first.Id))).WorldTransform;
        Assert.InRange((copyWorld.Translation-seedWorld.Translation).Length,kind==RepetitionKind.Linear?Math.Abs(step)-1e-8:4.24264068-1e-7,kind==RepetitionKind.Linear?Math.Abs(step)+1e-8:4.24264068+1e-7);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
        var storage=new CadDocumentStorage();var file=files.PathFor("array.cadoryx");await f.Session.SaveAsync(storage,file);
        using var loaded=await storage.LoadAsync(file,f.Assets);loaded.Snapshot.Validate();
        Assert.True(pattern.CopySlots.SequenceEqual(loaded.Snapshot.AssemblyPatterns[pattern.Id].CopySlots));Assert.True(owner.Children.SequenceEqual(((AssemblyDefinition)loaded.Snapshot.Definitions[f.Owner]).Children));
        Assert.Contains("cadoryx.assembly-patterns.1",FormatEvolutionTests.Manifest(file).RequiredCapabilities);
    }

    [Fact] public async Task SourcePoseAndDefinitionUpdateAllCopiesAndRemovedSlotsNeverReturn()
    {
        await using var f=new Fixture();var p=f.Pattern(RepetitionKind.Linear,4,8);await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,p));
        var pose=new RigidTransform3d(new(6,3,2),Quaterniond.FromAxisAngle(new(1,2,3),.7));await f.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(f.Path,pose));
        foreach(var (id,index) in p.CopySlots.Select((id,index)=>(id,index)))
        {var slot=OccurrencePlacement.Resolve(f.Session.Snapshot,f.Parent.Append(id)).Slot;Assert.Equal(pose.Rotation,slot.LocalTransform.Rotation);Assert.Equal(pose.Translation+new Vector3d(8*(index+1),0,0),slot.LocalTransform.Translation);}
        var other=DefinitionId.New();await f.Session.ExecuteAsync(ResourceCommands.AddPart(other,"Replacement"));await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.Replace(f.Path,other));
        Assert.All(((AssemblyDefinition)f.Session.Snapshot.Definitions[f.Owner]).Children,s=>Assert.Equal(other,s.DefinitionId));
        var oldCopies=p.CopySlots;p=f.Session.Snapshot.AssemblyPatterns[p.Id].Resize(2);await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,p));
        var survived=p.CopySlots[0];var grown=p.Resize(4);await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,grown));
        Assert.Equal(survived,grown.CopySlots[0]);Assert.DoesNotContain(grown.CopySlots[1],oldCopies);
        f.Session.Snapshot.Validate();
    }

    [Theory][InlineData("move")][InlineData("replace")][InlineData("remove")][InlineData("source-remove")][InlineData("source-reparent")][InlineData("generated-source")]
    public async Task LayoutOwnershipRejectsIndependentEditsAndDetachReleasesSlots(string action)
    {
        await using var f=new Fixture();var p=f.Pattern(RepetitionKind.Linear,3,8);await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,p));
        var copy=f.Parent.Append(p.CopySlots[0]);var root=new OccurrencePath(f.Session.Snapshot.Id,[]);var before=f.Session.Snapshot;
        ICadDocumentCommand cmd=action switch
        {
            "move"=>DocumentEdits.MoveOccurrence(copy,RigidTransform3d.Translate(30,0,0)),"replace"=>AssemblyOccurrenceCommands.Replace(copy,f.Owner),
            "remove"=>AssemblyOccurrenceCommands.Remove(copy),"source-remove"=>AssemblyOccurrenceCommands.Remove(f.Path),
            "source-reparent"=>AssemblyOccurrenceCommands.Reparent(f.Path,root),
            _=>AssemblyRepetitionCommands.Set(copy,p with{Id=Guid.NewGuid(),Source=p.CopySlots[0],CopySlots=[ComponentSlotId.New(),ComponentSlotId.New()]})
        };
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Session.ExecuteAsync(cmd));Assert.Same(before,f.Session.Snapshot);
        await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Detach(p.Id));Assert.Empty(f.Session.Snapshot.AssemblyPatterns);
        await f.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(copy,RigidTransform3d.Translate(30,0,0)));Assert.Equal(new Vector3d(30,0,0),OccurrencePlacement.Resolve(f.Session.Snapshot,copy).Slot.LocalTransform.Translation);
    }

    [Fact] public async Task ParameterDimensionsStageTogetherAndConstraintConflictsRollbackExactly()
    {
        await using var f=new Fixture();var p=f.Pattern(RepetitionKind.Circular,4,1);await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,p));
        var count=new CadParameter(Guid.NewGuid(),"count",ParameterDimension.Scalar,"10");var angle=new CadParameter(Guid.NewGuid(),"angle",ParameterDimension.Angle,"20 deg");
        var book=new ParameterBook([count,angle],[new(Guid.NewGuid(),count.Id,ParameterTarget.AssemblyPattern(p.Id,"Count")),new(Guid.NewGuid(),angle.Id,ParameterTarget.AssemblyPattern(p.Id,"Angle"))]);
        await f.Session.ExecuteAsync(new SetParametersCommand(book));p=f.Session.Snapshot.AssemblyPatterns[p.Id];Assert.Equal(10,p.Count);Assert.Equal(Math.PI/9,p.Step,12);
        var copy=f.Parent.Append(p.CopySlots[0]);var world=f.Session.Snapshot.EnumerateOccurrences().Single(o=>o.Path.Equals(copy)).WorldTransform;
        var fixedCopy=new AssemblyConstraint(AssemblyConstraintId.New(),"Fixed array item",AssemblyConstraintKind.Fixed,copy,f.Part,FixedWorld:world);
        await f.Session.ExecuteAsync(new EditDocumentCommand("Fix",d=>d with{AssemblyConstraints=d.AssemblyConstraints.Add(fixedCopy.Id,fixedCopy)}));var before=f.Session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new SetParametersCommand(book with{Parameters=[count,angle with{Expression="21 deg"}]})));Assert.Same(before,f.Session.Snapshot);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,p with{Step=.2})));Assert.Same(before,f.Session.Snapshot);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(AssemblyRepetitionCommands.Detach(p.Id)));Assert.Same(before,f.Session.Snapshot);
    }

    [Fact] public async Task EnabledDistanceConstraintProtectsGeneratedSlotAndDisabledEndpointCanBeRemoved()
    {
        await using var f=new Fixture();var p=f.Pattern(RepetitionKind.Linear,3,8);await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,p));var copy=f.Parent.Append(p.CopySlots[1]);
        var c=new AssemblyConstraint(AssemblyConstraintId.New(),"Spacing",AssemblyConstraintKind.Distance,f.Path,f.Part,copy,f.Part,TargetDistanceMm:16);
        await f.Session.ExecuteAsync(new EditDocumentCommand("Distance",d=>d with{AssemblyConstraints=d.AssemblyConstraints.Add(c.Id,c)}));var before=f.Session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,p with{Step=9})));Assert.Same(before,f.Session.Snapshot);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,p.Resize(2))));Assert.Same(before,f.Session.Snapshot);
        await f.Session.ExecuteAsync(new EditDocumentCommand("Disable",d=>d with{AssemblyConstraints=d.AssemblyConstraints.SetItem(c.Id,c with{IsEnabled=false})}));
        await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,p.Resize(2)));Assert.Equal(AssemblyConstraintStatus.Disabled,f.Session.Snapshot.AssemblyConstraints[c.Id].Evaluate(f.Session.Snapshot).Status);
    }

    [Fact] public async Task SourceDatumIsConvertedIntoParentCoordinatesAndCancellationIsAtomic()
    {
        await using var f=new Fixture();var axis=new CadDatumAxis(Guid.NewGuid(),f.Part,"Direction",DatumAxisKind.Independent,Direction:Vector3d.UnitZ);
        await f.Session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Axes=DatumBook.Empty.Axes.Add(axis.Id,axis)}));
        axis=f.Session.Snapshot.Datums.Axes[axis.Id];var p=f.Pattern(RepetitionKind.Linear,3,8) with{Datum=axis.Id,DatumRevision=axis.Revision};await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,p));
        var source=OccurrencePlacement.Resolve(f.Session.Snapshot,f.Path).Slot;var expected=source.LocalTransform*axis.Frame;
        Assert.True(DatumPlanes.SameFrame(expected,f.Session.Snapshot.AssemblyPatterns[p.Id].Frame));
        var pose=new RigidTransform3d(new(4,2,1),Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/2));await f.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(f.Path,pose));
        var copy=OccurrencePlacement.Resolve(f.Session.Snapshot,f.Parent.Append(p.CopySlots[0])).Slot;
        Assert.InRange((copy.LocalTransform.Translation-pose.Translation-pose.Rotation.Rotate(axis.Frame.Rotation.Rotate(new(1,0,0)))*8).Length,0,1e-8);
        var before=f.Session.Snapshot;using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,f.Session.Snapshot.AssemblyPatterns[p.Id].Resize(4)),cancel.Token));Assert.Same(before,f.Session.Snapshot);
    }

    [Fact] public async Task SharedParentIsolationCopiesLayoutDriversAndConstraintPathsOnlyOnChosenBranch()
    {
        using var files=new TestFiles();await using var f=new Fixture();var p=f.Pattern(RepetitionKind.Linear,3,8);await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,p));
        var count=new CadParameter(Guid.NewGuid(),"count",ParameterDimension.Scalar,"3");await f.Session.ExecuteAsync(new SetParametersCommand(new([count],[new(Guid.NewGuid(),count.Id,ParameterTarget.AssemblyPattern(p.Id,"Count"))])));
        var root=new OccurrencePath(f.Session.Snapshot.Id,[]);var shared=ComponentSlotId.New();await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,f.Owner,shared,"Other group",RigidTransform3d.Translate(100,0,0)));
        var mate=new AssemblyConstraint(AssemblyConstraintId.New(),"Array distance",AssemblyConstraintKind.Distance,f.Path,f.Part,f.Parent.Append(p.CopySlots[0]),f.Part,TargetDistanceMm:8);
        await f.Session.ExecuteAsync(new EditDocumentCommand("Mate",d=>d with{AssemblyConstraints=d.AssemblyConstraints.Add(mate.Id,mate)}));var before=f.Session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(f.Path,RigidTransform3d.Translate(8,0,0))));Assert.Same(before,f.Session.Snapshot);
        var command=AssemblyOccurrenceCommands.MakeIndependent(f.Parent);await f.Session.ExecuteAsync(command);var after=f.Session.Snapshot;
        var owner=OccurrencePlacement.Resolve(after,command.ResultPath!).Slot.DefinitionId;Assert.NotEqual(f.Owner,owner);
        var cloned=after.AssemblyPatterns.Values.Single(a=>a.Owner==owner);Assert.NotEqual(p.Id,cloned.Id);Assert.NotEqual(p.Source,cloned.Source);Assert.DoesNotContain(cloned.CopySlots[0],p.CopySlots);
        Assert.Equal(AssemblyConstraintStatus.Satisfied,after.AssemblyConstraints[mate.Id].Evaluate(after).Status);Assert.Equal(cloned.Source,after.AssemblyConstraints[mate.Id].PrimaryPath.Slots.Last());
        Assert.Equal(2,after.Parameters.Bindings.Length);Assert.Equal(p.Source,((AssemblyDefinition)after.Definitions[f.Owner]).Children[0].Id);
        await f.Session.ExecuteAsync(new SetParametersCommand(after.Parameters with{Parameters=[count with{Expression="4"}]}));Assert.All(f.Session.Snapshot.AssemblyPatterns.Values,a=>Assert.Equal(4,a.Count));
        var file=files.PathFor("independent-array.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),file);using var loaded=await new CadDocumentStorage().LoadAsync(file,f.Assets);loaded.Snapshot.Validate();Assert.Equal(2,loaded.Snapshot.AssemblyPatterns.Count);
    }

    [Theory][InlineData(true)][InlineData(false)]
    public async Task NestedLinkedAssemblySourceIsolationRemapsEveryAuthoredAliasAndClonesEachLayoutOnce(bool part)
    {
        await using var f=new Fixture();var inner=f.Pattern(RepetitionKind.Linear,3,8);await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,inner));
        var rootArray=new AssemblyRepetition(Guid.NewGuid(),f.Session.Snapshot.RootAssemblyId,f.Parent.Slots.Last(),"Groups",RepetitionKind.Linear,RigidTransform3d.Identity,2,100,[ComponentSlotId.New()]);
        await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Parent,rootArray));var groupCopy=new OccurrencePath(f.Session.Snapshot.Id,[rootArray.CopySlots.Single()]);
        var alias=groupCopy.Append(f.Path.Slots.Last());var generatedChild=groupCopy.Append(inner.CopySlots[0]);var snapshot=f.Session.Snapshot;
        var distance=(snapshot.EnumerateOccurrences().Single(o=>o.Path.Equals(alias)).WorldTransform.Translation-snapshot.EnumerateOccurrences().Single(o=>o.Path.Equals(f.Path)).WorldTransform.Translation).Length;
        var mate=new AssemblyConstraint(AssemblyConstraintId.New(),"Across linked groups",AssemblyConstraintKind.Distance,f.Path,f.Part,alias,f.Part,TargetDistanceMm:distance);
        var own=new AssemblyConstraint(AssemblyConstraintId.New(),"Generated child fixed",AssemblyConstraintKind.Fixed,generatedChild,f.Part,FixedWorld:snapshot.EnumerateOccurrences().Single(o=>o.Path.Equals(generatedChild)).WorldTransform);
        await f.Session.ExecuteAsync(new EditDocumentCommand("Aliases",d=>d with{AssemblyConstraints=d.AssemblyConstraints.Add(mate.Id,mate).Add(own.Id,own)}));
        var before=f.Session.Snapshot;
        if(part)await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.MakePartIndependent(f.Path));
        else await f.Session.ExecuteAsync(AssemblyOccurrenceCommands.MakeIndependent(f.Parent));
        var after=f.Session.Snapshot;after.Validate();var newOwner=OccurrencePlacement.Resolve(after,f.Parent).Slot.DefinitionId;
        Assert.NotEqual(f.Owner,newOwner);Assert.Equal(newOwner,OccurrencePlacement.Resolve(after,groupCopy).Slot.DefinitionId);
        Assert.Equal(3,after.AssemblyPatterns.Count);Assert.Single(after.AssemblyPatterns.Values,p=>p.Owner==newOwner);
        var cloned=after.AssemblyPatterns.Values.Single(p=>p.Owner==newOwner);Assert.Equal(cloned.Source,after.AssemblyConstraints[mate.Id].PrimaryPath.Slots.Last());
        Assert.Equal(cloned.Source,after.AssemblyConstraints[mate.Id].SecondaryPath!.Slots.Last());Assert.Equal(cloned.CopySlots[0],after.AssemblyConstraints[own.Id].PrimaryPath.Slots.Last());
        Assert.Equal(AssemblyConstraintStatus.Satisfied,after.AssemblyConstraints[mate.Id].Evaluate(after).Status);Assert.Equal(AssemblyConstraintStatus.Satisfied,after.AssemblyConstraints[own.Id].Evaluate(after).Status);
        Assert.Equal(inner.Source,((AssemblyDefinition)after.Definitions[f.Owner]).Children[0].Id);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }

    [Theory][InlineData("duplicate")][InlineData("missing-owner")][InlineData("pose")][InlineData("schema")]
    public async Task MalformedStoredLayoutCannotAdoptUnrelatedSlots(string mode)
    {
        using var files=new TestFiles();await using var f=new Fixture();var p=f.Pattern(RepetitionKind.Linear,3,8);await f.Session.ExecuteAsync(AssemblyRepetitionCommands.Set(f.Path,p));var file=files.PathFor("bad.cadoryx");var storage=new CadDocumentStorage();await f.Session.SaveAsync(storage,file);
        if(mode=="schema")FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="document"?s with{SchemaVersion=30}:s).ToImmutableArray()});
        else FormatEvolutionTests.RewriteSection(file,"assembly-patterns",bytes=>
        {
            var pack=MessagePackSerializer.Deserialize<PackAssemblyPatterns>(bytes);var a=pack.Patterns[0];a=mode switch
            {"duplicate"=>a with{Copies=[a.Copies[0],a.Copies[0]]},"missing-owner"=>a with{Owner=Guid.NewGuid()},_=>a with{Frame=a.Frame with{Qz=Math.Sin(.25),Qw=Math.Cos(.25)}}};
            return MessagePackSerializer.Serialize(pack with{Patterns=[a]});
        });
        await Assert.ThrowsAnyAsync<Exception>(()=>storage.LoadAsync(file,f.Assets));Assert.Equal(0,f.Assets.Count);
    }

    private sealed class Fixture:IAsyncDisposable
    {
        public MemoryAssetStore Assets {get;}=new();public CadDocumentSession Session {get;}
        public DefinitionId Part {get;}=DefinitionId.New();public DefinitionId Owner {get;}=DefinitionId.New();
        public OccurrencePath Path {get;}
        public OccurrencePath Parent=>new(Path.DocumentId,Path.Slots.Take(Path.Slots.Length-1));
        public Fixture()
        {
            var d=DocumentSnapshot.Create("Instance arrays");var group=ComponentSlotId.New();var source=ComponentSlotId.New();var root=(AssemblyDefinition)d.Definitions[d.RootAssemblyId];
            d=d with{Definitions=d.Definitions.Add(Part,new PartDefinition(Part,"Part",[],[])).Add(Owner,new AssemblyDefinition(Owner,"Group",
                [new(source,Part,"Seed",new(new(4,2,1),Quaterniond.FromAxisAngle(Vector3d.UnitZ,.31)))]))
                .SetItem(root.Id,root with{Children=[new(group,Owner,"Group",new(new(50,10,0),Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/2)))]})};
            Path=new(d.Id,[group,source]);Session=new(d,Assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        }
        public AssemblyRepetition Pattern(RepetitionKind kind,int count,double step)=>new(Guid.NewGuid(),Owner,Path.Slots.Last(),"Array",kind,RigidTransform3d.Identity,count,step,Enumerable.Range(0,count-1).Select(_=>ComponentSlotId.New()).ToImmutableArray());
        public async ValueTask DisposeAsync(){await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
}
