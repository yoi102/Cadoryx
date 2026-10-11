using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;
using MessagePack;

namespace Cadoryx.Tests;

public sealed class AssemblyMirrorTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SplittingOneSharedSourceKeepsEachReflectedPathAndItsOwnExactDefinition(bool sharedParent)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Split mirrored source"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Identity),"Shared leaf"));
            var original=session.Snapshot.Bodies.Values.Single();var root=new OccurrencePath(session.Snapshot.Id,[]);
            var first=session.Snapshot.EnumerateOccurrences().Single().Path;var group=DefinitionId.New();var groupSlot=ComponentSlotId.New();
            await session.ExecuteAsync(AssemblyOccurrenceCommands.CreateAssembly(root,group,groupSlot,"Group",RigidTransform3d.Identity));var source=root.Append(groupSlot);
            await session.ExecuteAsync(AssemblyOccurrenceCommands.Reparent(first,source));first=source.Append(first.Slots.Single());
            var secondSlot=ComponentSlotId.New();await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(source,original.PartId,secondSlot,"Second shared leaf",RigidTransform3d.Translate(8,0,0)));
            var mirror=new AssemblyRepetition(Guid.NewGuid(),session.Snapshot.RootAssemblyId,groupSlot,"Group mirror",RepetitionKind.Mirror,RigidTransform3d.Identity,2,0,[ComponentSlotId.New()]);
            await session.ExecuteAsync(AssemblyRepetitionCommands.Set(source,mirror));mirror=session.Snapshot.AssemblyPatterns[mirror.Id];
            var beforeRoot=mirror.Mirrors.Single(m=>m.Source==group);var beforeLeaf=mirror.Mirrors.Single(m=>m.Source==original.PartId);
            var reflected=root.Append(mirror.CopySlots.Single());var left=reflected.Append(beforeRoot.Slots.Single(s=>s.Source==first.Slots.Last()).Target);
            var right=reflected.Append(beforeRoot.Slots.Single(s=>s.Source==secondSlot).Target);
            var constraints=new[]{left,right}.Select((path,i)=>new AssemblyConstraint(AssemblyConstraintId.New(),"Reflected "+i,AssemblyConstraintKind.Fixed,path,beforeLeaf.Target,
                FixedWorld:session.Snapshot.EnumerateOccurrences().Single(o=>o.Path.Equals(path)).WorldTransform)).ToArray();
            await session.ExecuteAsync(new EditDocumentCommand("Explicit mirror references",d=>d with{AssemblyConstraints=d.AssemblyConstraints.AddRange(constraints.Select(c=>new KeyValuePair<AssemblyConstraintId,AssemblyConstraint>(c.Id,c)))}));
            var sheet=TechnicalDrawingSheet.A4Landscape("Split source");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Left",DrawingViewKind.Top,new(80,80),1,left));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Right",DrawingViewKind.Top,new(160,80),1,right));
            if(sharedParent)await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,group,ComponentSlotId.New(),"Other original group",RigidTransform3d.Translate(40,0,0)));
            var previous=session.Snapshot;var independent=AssemblyOccurrenceCommands.MakePartIndependent(first);await session.ExecuteAsync(independent);
            var after=session.Snapshot;after.Validate();mirror=after.AssemblyPatterns[mirror.Id];
            var selectedPart=independent.ResultPartId!.Value;var selected=mirror.Mirrors.Single(m=>m.Source==selectedPart);
            var unchanged=mirror.Mirrors.Single(m=>m.Source==original.PartId);
            Assert.NotEqual(selected.Target,unchanged.Target);Assert.Equal(beforeLeaf.Target,unchanged.Target);
            Assert.Equal(beforeLeaf.Bodies.Single().ResultRevision,selected.Bodies.Single().ResultRevision);
            Assert.Equal(selected.Target,after.AssemblyConstraints[constraints[0].Id].PrimaryDefinitionId);
            Assert.Equal(unchanged.Target,after.AssemblyConstraints[constraints[1].Id].PrimaryDefinitionId);
            foreach(var c in constraints)Assert.Equal(AssemblyConstraintStatus.Satisfied,after.AssemblyConstraints[c.Id].Evaluate(after).Status);
            var views=after.DrawingSheets[sheet.Id].Views;Assert.All(views,v=>Assert.Null(v.StaleReason));
            Assert.Equal(selected.Bodies.Single().Target,views.Single(v=>v.Name=="Left").Sources.Single().Body);
            Assert.Equal(unchanged.Bodies.Single().Target,views.Single(v=>v.Name=="Right").Sources.Single().Body);
            await session.UndoAsync();Assert.Same(previous,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var clonedBody=after.Bodies.Values.Single(b=>b.PartId==selectedPart);var oldUnchanged=after.Bodies[unchanged.Bodies.Single().Target].Geometry;
            await session.ExecuteAsync(new RecomputeCommand(clonedBody.Producer!.Value,new BoxRecipe(5,3,2,RigidTransform3d.Identity)));
            Assert.Equal(oldUnchanged,session.Snapshot.Bodies[unchanged.Bodies.Single().Target].Geometry);
            Assert.Equal(30,session.Snapshot.Bodies[selected.Bodies.Single().Target].Geometry.VolumeMm3,7);
            Assert.All(session.Snapshot.DrawingSheets[sheet.Id].Views,v=>Assert.Null(v.StaleReason));
            var file=files.PathFor("split-mirror.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
            using var loaded=await new CadDocumentStorage().LoadAsync(file,assets);loaded.Snapshot.Validate();
            Assert.Equal(selected.Target,loaded.Snapshot.AssemblyConstraints[constraints[0].Id].PrimaryDefinitionId);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task SourcePropertiesFollowWithoutRebuildingGeometryAndDerivedCycleIsRejectedAtomically()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Mirror properties"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Identity),"Source"));var body=session.Snapshot.Bodies.Values.Single();
            var source=session.Snapshot.EnumerateOccurrences().Single().Path;var root=new OccurrencePath(session.Snapshot.Id,[]);
            var group=DefinitionId.New();var groupSlot=ComponentSlotId.New();await session.ExecuteAsync(AssemblyOccurrenceCommands.CreateAssembly(root,group,groupSlot,"Source assembly",RigidTransform3d.Identity));
            var parent=root.Append(groupSlot);await session.ExecuteAsync(AssemblyOccurrenceCommands.Reparent(source,parent));
            var pattern=new AssemblyRepetition(Guid.NewGuid(),session.Snapshot.RootAssemblyId,groupSlot,"Mirror",RepetitionKind.Mirror,RigidTransform3d.Identity,2,0,[ComponentSlotId.New()]);
            await session.ExecuteAsync(AssemblyRepetitionCommands.Set(parent,pattern));pattern=session.Snapshot.AssemblyPatterns[pattern.Id];
            var link=pattern.Mirrors.Single(m=>m.Source==body.PartId);var target=link.Bodies.Single().Target;var geometry=session.Snapshot.Bodies[target].Geometry;
            var material=new CadMaterial(MaterialId.New(),"Steel",7.85e-6);var layer=new CadLayer(LayerId.New(),"Manufacturing",0xFF345678);
            await session.ExecuteAsync(new EditDocumentCommand("Resources",d=>d with{Materials=d.Materials.Add(material.Id,material),Layers=d.Layers.Add(layer.Id,layer)}));
            await session.ExecuteAsync(DocumentEdits.SetBodyProperties(body.Id,"Edited source",new(0xFF123456,true),false,layer.Id,material.Id));
            var reflected=session.Snapshot.Bodies[target];Assert.Equal(geometry,reflected.Geometry);Assert.Equal("Edited source",reflected.Name);
            Assert.Equal(layer.Id,reflected.LayerId);Assert.Equal(material.Id,reflected.MaterialId);Assert.False(reflected.IsVisible);Assert.Equal(new CadAppearance(0xFF123456,true),reflected.Appearance);
            var previous=session.Snapshot;var count=assets.Count;var derivedRoot=pattern.Mirrors.Single(m=>m.Source==group).Target;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(parent,derivedRoot,ComponentSlotId.New(),"Cyclic mirror",RigidTransform3d.Identity)));
            Assert.Same(previous,session.Snapshot);Assert.Equal(count,assets.Count);
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(DocumentEdits.SetVisibility(target,true)));Assert.Same(previous,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task IsolatedParentRetainsSharedMirrorLeavesAndExplicitNestedReferencePaths()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Nested mirror isolation"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Identity),"Source"));var part=session.Snapshot.Bodies.Values.Single().PartId;
            var root=new OccurrencePath(session.Snapshot.Id,[]);var owner=DefinitionId.New();var parentSlot=ComponentSlotId.New();
            await session.ExecuteAsync(AssemblyOccurrenceCommands.CreateAssembly(root,owner,parentSlot,"Parent",RigidTransform3d.Identity));var parent=root.Append(parentSlot);
            var group=DefinitionId.New();var sourceSlot=ComponentSlotId.New();await session.ExecuteAsync(AssemblyOccurrenceCommands.CreateAssembly(parent,group,sourceSlot,"Source group",RigidTransform3d.Identity));var source=parent.Append(sourceSlot);
            var first=ComponentSlotId.New();var second=ComponentSlotId.New();
            await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(source,part,first,"First",RigidTransform3d.Identity));
            await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(source,part,second,"Second",RigidTransform3d.Translate(8,0,0)));
            var pattern=new AssemblyRepetition(Guid.NewGuid(),owner,sourceSlot,"Nested mirror",RepetitionKind.Mirror,RigidTransform3d.Identity,2,0,[ComponentSlotId.New()]);
            await session.ExecuteAsync(AssemblyRepetitionCommands.Set(source,pattern));pattern=session.Snapshot.AssemblyPatterns[pattern.Id];
            var rootLink=pattern.Mirrors.Single(m=>m.Source==group);var leaf=pattern.Mirrors.Single(m=>m.Source==part);
            var output=parent.Append(pattern.CopySlots.Single());var path=output.Append(rootLink.Slots.Single(s=>s.Source==second).Target);
            var world=session.Snapshot.EnumerateOccurrences().Single(o=>o.Path.Equals(path)).WorldTransform;
            var mate=new AssemblyConstraint(AssemblyConstraintId.New(),"Nested output fixed",AssemblyConstraintKind.Fixed,path,leaf.Target,FixedWorld:world);
            await session.ExecuteAsync(new EditDocumentCommand("Nested reference",d=>d with{AssemblyConstraints=d.AssemblyConstraints.Add(mate.Id,mate)}));
            var sheet=TechnicalDrawingSheet.A4Landscape("Nested mirror");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,100),1,path));
            await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,owner,ComponentSlotId.New(),"Other parent",RigidTransform3d.Translate(40,0,0)));
            var previous=session.Snapshot;await session.ExecuteAsync(AssemblyOccurrenceCommands.MakeIndependent(parent));var after=session.Snapshot;after.Validate();
            var newOwner=OccurrencePlacement.Resolve(after,parent).Slot.DefinitionId;var copied=after.AssemblyPatterns.Values.Single(p=>p.Owner==newOwner);
            var newRoot=copied.Mirrors.Single(m=>m.Source==group);var newLeaf=copied.Mirrors.Single(m=>m.Source==part);
            Assert.NotEqual(rootLink.Target,newRoot.Target);Assert.NotEqual(leaf.Target,newLeaf.Target);
            Assert.Single(((AssemblyDefinition)after.Definitions[newRoot.Target]).Children.Select(s=>s.DefinitionId).Distinct());
            var expected=parent.Append(copied.CopySlots.Single()).Append(newRoot.Slots.Single(s=>s.Source==second).Target);
            Assert.Equal(expected,after.AssemblyConstraints[mate.Id].PrimaryPath);Assert.Equal(newLeaf.Target,after.AssemblyConstraints[mate.Id].PrimaryDefinitionId);
            Assert.Equal(AssemblyConstraintStatus.Satisfied,after.AssemblyConstraints[mate.Id].Evaluate(after).Status);
            var drawing=after.DrawingSheets[sheet.Id].Views.Single();Assert.Null(drawing.StaleReason);Assert.Equal(expected,drawing.Sources.Single().Path);
            Assert.Equal(newLeaf.Bodies.Single().Target,drawing.Sources.Single().Body);Assert.Equal(leaf.Bodies.Single().ResultRevision,newLeaf.Bodies.Single().ResultRevision);
            await session.UndoAsync();Assert.Same(previous,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task LateNativeMirrorCancellationOrFailureReleasesStagedResultsAndRestoresExactSnapshot(bool cancel)
    {
        var assets=new MemoryAssetStore();using var cancellation=new CancellationTokenSource();var kernel=new InterruptedMirrorKernel(cancellation);
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Mirror atomicity"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Identity),"Source"));
            var source=session.Snapshot.EnumerateOccurrences().Single().Path;var part=session.Snapshot.Bodies.Values.Single().PartId;
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,2,3,RigidTransform3d.Translate(8,0,0)),"Second",part));
            var pattern=new AssemblyRepetition(Guid.NewGuid(),session.Snapshot.RootAssemblyId,source.Slots.Single(),"Mirror",RepetitionKind.Mirror,RigidTransform3d.Identity,2,0,[ComponentSlotId.New()]);
            var previous=session.Snapshot;var count=assets.Count;kernel.Interrupt=true;kernel.Cancel=cancel;
            if(cancel)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ExecuteAsync(AssemblyRepetitionCommands.Set(source,pattern),cancellation.Token));
            else await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(AssemblyRepetitionCommands.Set(source,pattern)));
            Assert.Same(previous,session.Snapshot);Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }

    private sealed class InterruptedMirrorKernel(CancellationTokenSource cancellation):IGeometryKernel
    {
        private readonly OcctGeometryKernel native=new();private int calls;
        public bool Interrupt {get;set;}public bool Cancel {get;set;}public string Version=>native.Version;
        public bool Supports(GeometryRecipe recipe)=>native.Supports(recipe);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)
        {
            if(Interrupt&&recipe is RepetitionRecipe&&++calls==2)throw new CadValidationException("Injected second native mirror failure.");
            var result=await native.EvaluateAsync(recipe,assets,token);
            if(Interrupt&&Cancel&&recipe is RepetitionRecipe)cancellation.Cancel();return result;
        }
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>native.ImportAsync(path,assets,token);
        public Task ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken token=default)=>native.ExportAsync(snapshot,assets,path,token);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>native.ExportAsync(snapshot,assets,path,options,token);
    }

    [Fact] public async Task NestedAssociatedMirrorsUpdateInnerBeforeOuterAndPreserveExactDoubleReflection()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Nested mirrors"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Translate(1,2,3)),"Source"));
            var body=session.Snapshot.Bodies.Values.Single();var root=new OccurrencePath(session.Snapshot.Id,[]);
            var group=DefinitionId.New();var groupSlot=ComponentSlotId.New();var source=session.Snapshot.EnumerateOccurrences().Single().Path;
            await session.ExecuteAsync(AssemblyOccurrenceCommands.CreateAssembly(root,group,groupSlot,"Group",RigidTransform3d.Identity));
            var parent=root.Append(groupSlot);await session.ExecuteAsync(AssemblyOccurrenceCommands.Reparent(source,parent));source=parent.Append(source.Slots.Single());
            var inner=new AssemblyRepetition(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),group,source.Slots.Last(),"Inner",RepetitionKind.Mirror,RigidTransform3d.Identity,2,0,[ComponentSlotId.New()]);
            await session.ExecuteAsync(AssemblyRepetitionCommands.Set(source,inner));
            var outer=new AssemblyRepetition(Guid.Parse("00000000-0000-0000-0000-000000000001"),session.Snapshot.RootAssemblyId,groupSlot,"Outer",RepetitionKind.Mirror,RigidTransform3d.Identity,2,0,[ComponentSlotId.New()]);
            await session.ExecuteAsync(AssemblyRepetitionCommands.Set(parent,outer));
            Assert.Equal(new[]{inner.Id,outer.Id},AssemblyMirrors.OrderedPatterns(session.Snapshot));
            var previous=session.Snapshot;var before=previous.AssemblyPatterns[outer.Id];var innerOutput=previous.AssemblyPatterns[inner.Id].Mirrors.Single().Target;
            var doubleOutput=before.Mirrors.Single(m=>m.Source==innerOutput).Bodies.Single().Target;
            await session.ExecuteAsync(new RecomputeCommand(body.Producer!.Value,new BoxRecipe(5,3,2,RigidTransform3d.Translate(1,2,3))));
            var after=session.Snapshot;after.Validate();
            Assert.NotEqual(previous.Bodies[doubleOutput].Geometry.Revision,after.Bodies[doubleOutput].Geometry.Revision);
            Assert.Equal(before.Mirrors.Select(m=>m.Target),after.AssemblyPatterns[outer.Id].Mirrors.Select(m=>m.Target));
            using var expected=OcctGeometryBridge.ReadShape(after.Bodies[body.Id].Geometry,assets);
            using var actual=OcctGeometryBridge.ReadShape(after.Bodies[doubleOutput].Geometry,assets);
            using var extra=actual.Cut(expected);using var missing=expected.Cut(actual);
            Assert.Equal(0,extra.GetTopologySummary().UniqueCounts.SolidCount);Assert.Equal(0,missing.GetTopologySummary().UniqueCounts.SolidCount);
            await session.UndoAsync();Assert.Same(previous,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task SharedParentIsolationCopiesMirrorIdentitiesAndRemapsOnlyChosenReferences(bool independentPart)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Isolated mirror"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Identity),"Source"));
            var body=session.Snapshot.Bodies.Values.Single();var root=new OccurrencePath(session.Snapshot.Id,[]);var source=session.Snapshot.EnumerateOccurrences().Single().Path;
            var group=DefinitionId.New();var leftSlot=ComponentSlotId.New();var rightSlot=ComponentSlotId.New();
            await session.ExecuteAsync(AssemblyOccurrenceCommands.CreateAssembly(root,group,leftSlot,"Left",RigidTransform3d.Identity));var left=root.Append(leftSlot);
            await session.ExecuteAsync(AssemblyOccurrenceCommands.Reparent(source,left));source=left.Append(source.Slots.Single());
            var mirror=new AssemblyRepetition(Guid.NewGuid(),group,source.Slots.Last(),"Mirror",RepetitionKind.Mirror,RigidTransform3d.Identity,2,0,[ComponentSlotId.New()]);
            await session.ExecuteAsync(AssemblyRepetitionCommands.Set(source,mirror));
            var link=session.Snapshot.AssemblyPatterns[mirror.Id].Mirrors.Single();var mirrored=left.Append(mirror.CopySlots.Single());
            await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,group,rightSlot,"Right",RigidTransform3d.Translate(30,0,0)));var right=root.Append(rightSlot);
            var world=session.Snapshot.EnumerateOccurrences().Single(o=>o.Path.Equals(mirrored)).WorldTransform;
            var fixedMirror=new AssemblyConstraint(AssemblyConstraintId.New(),"Selected mirror",AssemblyConstraintKind.Fixed,mirrored,link.Target,FixedWorld:world);
            var fixedOther=new AssemblyConstraint(AssemblyConstraintId.New(),"Other mirror",AssemblyConstraintKind.Fixed,right.Append(mirror.CopySlots.Single()),link.Target,FixedWorld:RigidTransform3d.Translate(30,0,0)*world);
            await session.ExecuteAsync(new EditDocumentCommand("Fix mirrors",d=>d with{AssemblyConstraints=d.AssemblyConstraints.Add(fixedMirror.Id,fixedMirror).Add(fixedOther.Id,fixedOther)}));
            var sheet=TechnicalDrawingSheet.A4Landscape("Chosen mirror");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,100),1,mirrored));
            var previous=session.Snapshot;
            await session.ExecuteAsync(independentPart?AssemblyOccurrenceCommands.MakePartIndependent(source):AssemblyOccurrenceCommands.MakeIndependent(left));
            var after=session.Snapshot;after.Validate();var copiedOwner=OccurrencePlacement.Resolve(after,left).Slot.DefinitionId;
            var copied=after.AssemblyPatterns.Values.Single(p=>p.Owner==copiedOwner);var output=copied.Mirrors.Single();
            Assert.NotEqual(link.Target,output.Target);Assert.NotEqual(link.Bodies.Single().Target,output.Bodies.Single().Target);
            Assert.Equal(link.Bodies.Single().ResultRevision,output.Bodies.Single().ResultRevision);
            Assert.Equal(independentPart,body.PartId!=output.Source);
            Assert.Equal(group,OccurrencePlacement.Resolve(after,right).Slot.DefinitionId);
            Assert.Equal(AssemblyConstraintStatus.Satisfied,after.AssemblyConstraints[fixedMirror.Id].Evaluate(after).Status);
            Assert.Equal(AssemblyConstraintStatus.Satisfied,after.AssemblyConstraints[fixedOther.Id].Evaluate(after).Status);
            Assert.Equal(output.Target,after.AssemblyConstraints[fixedMirror.Id].PrimaryDefinitionId);
            Assert.Equal(link.Target,after.AssemblyConstraints[fixedOther.Id].PrimaryDefinitionId);
            var drawing=after.DrawingSheets[sheet.Id].Views.Single();Assert.Null(drawing.StaleReason);
            Assert.Equal(output.Bodies.Single().Target,drawing.Sources.Single().Body);
            Assert.Equal(left.Append(copied.CopySlots.Single()),drawing.Sources.Single().Path);
            await session.UndoAsync();Assert.Same(previous,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var file=files.PathFor("isolated.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
            using var loaded=await new CadDocumentStorage().LoadAsync(file,assets);loaded.Snapshot.Validate();
            Assert.Equal(AssemblyConstraintStatus.Satisfied,loaded.Snapshot.AssemblyConstraints[fixedMirror.Id].Evaluate(loaded.Snapshot).Status);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("schema")][InlineData("identity")][InlineData("revision")]
    public async Task StoredMirrorRejectsUndeclaredLayoutAndForgedSourceEvidence(string mode)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Mirror guards"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Identity),"Source"));var source=session.Snapshot.EnumerateOccurrences().Single().Path;
            var pattern=new AssemblyRepetition(Guid.NewGuid(),session.Snapshot.RootAssemblyId,source.Slots.Single(),"Mirror",RepetitionKind.Mirror,RigidTransform3d.Identity,2,0,[ComponentSlotId.New()]);
            await session.ExecuteAsync(AssemblyRepetitionCommands.Set(source,pattern));var file=files.PathFor("mirror-guard.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
            if(mode=="schema")FormatEvolutionTests.RewriteManifest(file,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="document"?s with{SchemaVersion=31}:s).ToImmutableArray()});
            else FormatEvolutionTests.RewriteSection(file,"assembly-patterns",bytes=>
            {
                var pack=MessagePackSerializer.Deserialize<PackAssemblyPatterns>(bytes);var p=pack.Patterns.Single();var link=p.Mirrors!.Single();
                link=mode=="identity"?link with{Target=link.Source}:link with{Bodies=[link.Bodies.Single() with{SourceRevision=Guid.NewGuid()}]};
                return MessagePackSerializer.Serialize(pack with{Patterns=[p with{Mirrors=[link]}]});
            });
            var baseline=assets.Count;await Assert.ThrowsAnyAsync<Exception>(()=>new CadDocumentStorage().LoadAsync(file,assets));Assert.Equal(baseline,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExactMirrorUnderArbitraryParentAndSeedPosesRetainsHierarchyAndSharedDefinitions(bool nested)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Exact assembly mirror"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Translate(1,2,3)),"Asymmetric source"));
            var initial=session.Snapshot;var sourceBody=initial.Bodies.Values.Single();var source=initial.EnumerateOccurrences().Single().Path;
            var root=new OccurrencePath(initial.Id,[]);var groupId=DefinitionId.New();var groupSlot=ComponentSlotId.New();
            var parentPose=new RigidTransform3d(new(31,-7,12),Quaterniond.FromAxisAngle(new(1,3,2),.831));
            await session.ExecuteAsync(AssemblyOccurrenceCommands.CreateAssembly(root,groupId,groupSlot,"Parent",parentPose));var parent=root.Append(groupSlot);
            await session.ExecuteAsync(AssemblyOccurrenceCommands.Reparent(source,parent));source=parent.Append(source.Slots.Single());
            if(nested)
            {
                var child=DefinitionId.New();var slot=ComponentSlotId.New();
                await session.ExecuteAsync(AssemblyOccurrenceCommands.CreateAssembly(parent,child,slot,"Nested source",RigidTransform3d.Translate(2,3,4)));
                var childPath=parent.Append(slot);await session.ExecuteAsync(AssemblyOccurrenceCommands.Reparent(source,childPath));
                await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(childPath,sourceBody.PartId,ComponentSlotId.New(),"Shared leaf",new(new(-2,6,8),Quaterniond.FromAxisAngle(new(2,1,1),.412))));
                source=childPath;
            }
            await session.ExecuteAsync(DocumentEdits.MoveOccurrence(source,new(new(4,-3,9),Quaterniond.FromAxisAngle(new(3,1,2),.713))));
            var plane=new RigidTransform3d(new(7,2,-1),Quaterniond.FromAxisAngle(new(2,1,3),.533));
            var placement=OccurrencePlacement.Resolve(session.Snapshot,source);
            var pattern=new AssemblyRepetition(Guid.NewGuid(),placement.OwnerId,placement.Slot.Id,"Mirror",RepetitionKind.Mirror,plane,2,0,[ComponentSlotId.New()]);
            var before=session.Snapshot;await session.ExecuteAsync(AssemblyRepetitionCommands.Set(source,pattern));var after=session.Snapshot;after.Validate();
            pattern=after.AssemblyPatterns[pattern.Id];Assert.Equal(nested?2:1,pattern.Mirrors.Length);
            var mirrorPath=parent.Append(pattern.CopySlots.Single());var originalLeaves=after.EnumerateOccurrences().Where(o=>o.Path.Slots.Take(source.Slots.Length).SequenceEqual(source.Slots)&&after.Definitions[o.DefinitionId] is PartDefinition).ToArray();
            var outputLeaves=after.EnumerateOccurrences().Where(o=>o.Path.Slots.Take(mirrorPath.Slots.Length).SequenceEqual(mirrorPath.Slots)&&after.Definitions[o.DefinitionId] is PartDefinition).ToArray();
            Assert.Equal(nested?2:1,outputLeaves.Length);Assert.Single(outputLeaves.Select(o=>o.DefinitionId).Distinct());
            var worldPlane=parentPose*plane;var normal=worldPlane.Rotation.Rotate(Vector3d.UnitZ);
            using var reflect=GpTrsf.CreatePlaneMirror(worldPlane.Translation.X,worldPlane.Translation.Y,worldPlane.Translation.Z,normal.X,normal.Y,normal.Z);
            foreach(var occurrence in originalLeaves)
            {
                using var shape=OcctGeometryBridge.ReadShape(sourceBody.Geometry,assets);using var native=OcctGeometryBridge.ToNative(occurrence.WorldTransform);
                using var placed=shape.Transformed(native);using var expected=placed.Transformed(reflect);
                var outputPath=mirrorPath;
                foreach(var slot in occurrence.Path.Slots.Skip(source.Slots.Length))
                    outputPath=outputPath.Append(pattern.Mirrors.SelectMany(m=>m.Slots).Single(m=>m.Source==slot).Target);
                var output=outputLeaves.Single(o=>o.Path.Equals(outputPath));
                var body=after.Bodies[((PartDefinition)after.Definitions[output.DefinitionId]).Bodies.Single()];
                using var local=OcctGeometryBridge.ReadShape(body.Geometry,assets);using var transform=OcctGeometryBridge.ToNative(output.WorldTransform);using var actual=local.Transformed(transform);
                using var extra=actual.Cut(expected);using var missing=expected.Cut(actual);
                Assert.Equal(0,extra.GetTopologySummary().UniqueCounts.SolidCount);Assert.Equal(0,missing.GetTopologySummary().UniqueCounts.SolidCount);
                Assert.True(actual.GetValidationReport(true,true).IsValid);Assert.True(actual.GetSelfInterferenceReport().IsClear);
            }
            var file=files.PathFor("mirror.cadoryx");await session.SaveAsync(new CadDocumentStorage(),file);
            using(var loaded=await new CadDocumentStorage().LoadAsync(file,assets)){loaded.Snapshot.Validate();Assert.Equal(pattern.Mirrors.Length,loaded.Snapshot.AssemblyPatterns[pattern.Id].Mirrors.Length);}
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            await kernel.ExportAsync(after,assets,files.PathFor("mirror.step"));using var reopened=await kernel.ImportAsync(files.PathFor("mirror.step"),assets);
            Assert.Equal(nested?4:2,reopened.Snapshot.EnumerateOccurrences().Count(o=>reopened.Snapshot.Definitions[o.DefinitionId] is PartDefinition));
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task SourceChangesRebuildOnlyExactBodiesAndDetachUnlocksFrozenDefinition()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Linked mirror"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Identity),"Source"));
            var source=session.Snapshot.EnumerateOccurrences().Single().Path;var body=session.Snapshot.Bodies.Values.Single();
            var pattern=new AssemblyRepetition(Guid.NewGuid(),session.Snapshot.RootAssemblyId,source.Slots.Single(),"Mirror",RepetitionKind.Mirror,RigidTransform3d.Identity,2,0,[ComponentSlotId.New()]);
            await session.ExecuteAsync(AssemblyRepetitionCommands.Set(source,pattern));var link=Assert.Single(session.Snapshot.AssemblyPatterns[pattern.Id].Mirrors);var output=Assert.Single(link.Bodies);
            var original=session.Snapshot.Bodies[output.Target].Geometry;
            var copied=new OccurrencePath(session.Snapshot.Id,[pattern.CopySlots.Single()]);var sheet=TechnicalDrawingSheet.A4Landscape("Mirrored source");
            await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,100),1,copied));
            await session.ExecuteAsync(new RecomputeCommand(body.Producer!.Value,new BoxRecipe(5,3,2,RigidTransform3d.Identity)));
            var after=session.Snapshot;var updated=Assert.Single(after.AssemblyPatterns[pattern.Id].Mirrors);Assert.Equal(link.Target,updated.Target);Assert.Equal(output.Target,updated.Bodies.Single().Target);
            Assert.NotEqual(original.Revision,after.Bodies[output.Target].Geometry.Revision);Assert.Equal(30,after.Bodies[output.Target].Geometry.VolumeMm3,8);
            Assert.Null(after.DrawingSheets[sheet.Id].Views.Single().StaleReason);Assert.Equal(after.Bodies[output.Target].Geometry.Revision,after.DrawingSheets[sheet.Id].Views.Single().Sources.Single().Revision);
            var moved=RigidTransform3d.Translate(2,3,4);var revision=after.Bodies[output.Target].Geometry.Revision;
            await session.ExecuteAsync(DocumentEdits.MoveOccurrence(source,moved));Assert.Equal(revision,session.Snapshot.Bodies[output.Target].Geometry.Revision);
            var before=session.Snapshot;await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Identity),"Unauthorized derived edit",link.Target)));Assert.Same(before,session.Snapshot);
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(AssemblyOccurrenceCommands.Remove(source)));Assert.Same(before,session.Snapshot);
            await session.ExecuteAsync(AssemblyRepetitionCommands.Detach(pattern.Id));await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Identity),"Independent edit",link.Target));session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
}
