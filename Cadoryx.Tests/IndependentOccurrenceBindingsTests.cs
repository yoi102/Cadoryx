using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed partial class IndependentAuthoringGraphTests
{
    [Fact] public async Task NestedIsolationKeepsExactMatesSiblingPathsWorkplaneAndDrawingOnChosenBranch()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Nested authoring"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Box"));
            var box=session.Snapshot.Features.Values.Single();var part=box.PartId;var sibling=DefinitionId.New();
            await session.ExecuteAsync(ResourceCommands.AddPart(sibling,"Sibling"));
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,2,3,RigidTransform3d.Identity),"Sibling box",sibling));
            var group=DefinitionId.New();var left=ComponentSlotId.New();var right=ComponentSlotId.New();var child=ComponentSlotId.New();var other=ComponentSlotId.New();
            await session.ExecuteAsync(new EditDocumentCommand("Nested sharing",d=>d with{Definitions=d.Definitions
                .Add(group,new AssemblyDefinition(group,"Group",[new(child,part,"Part",RigidTransform3d.Identity),new(other,sibling,"Sibling",RigidTransform3d.Translate(0,30,0))]))
                .SetItem(d.RootAssemblyId,((AssemblyDefinition)d.Definitions[d.RootAssemblyId]) with
                    {Children=[new(left,group,"Left",RigidTransform3d.Identity),new(right,group,"Right",RigidTransform3d.Translate(100,0,0))]})}));
            var selected=new OccurrencePath(session.Snapshot.Id,[left,child]);var unselected=new OccurrencePath(session.Snapshot.Id,[right,child]);
            var siblingPath=new OccurrencePath(session.Snapshot.Id,[left,other]);var parentPath=new OccurrencePath(session.Snapshot.Id,[left]);
            var plane=new CadDatumPlane(Guid.NewGuid(),part,"Top",DatumPlaneKind.Offset,DatumPlaneSource.Box(box.Id,BoxBoundary.ZMax));
            var book=DatumBook.Empty with{Planes=DatumBook.Empty.Planes.Add(plane.Id,plane),WorkPlane=new(plane.Id,selected,plane.Revision,RigidTransform3d.Identity)};
            await session.ExecuteAsync(new SetDatumsCommand(book));
            var top=Face(session,box,assets,true);
            AssemblyDatumReference Datum(OccurrencePath path)=>new(path,part,box.OutputBodyId,box.Id,box.Result.Revision,box.Result.AssetId,
                top.Fingerprint,top.FullTopologyIndex,AssemblyDatumGeometry.PlaneFace,new(5,6,15),Vector3d.UnitZ,0);
            var mate=new AssemblyConstraint(AssemblyConstraintId.New(),"Axes",AssemblyConstraintKind.ParallelAxes,selected,part,unselected,part,
                PrimaryLocalPoint:new(5,6,15),SecondaryLocalPoint:new(5,6,15),
                PrimaryLocalAxis:Vector3d.UnitZ,SecondaryLocalAxis:Vector3d.UnitZ,PrimaryDatum:Datum(selected),SecondaryDatum:Datum(unselected),SchemaVersion:2);
            var fixedParent=new AssemblyConstraint(AssemblyConstraintId.New(),"Group fixed",AssemblyConstraintKind.Fixed,parentPath,group,FixedWorld:RigidTransform3d.Identity);
            var fixedSibling=new AssemblyConstraint(AssemblyConstraintId.New(),"Sibling fixed",AssemblyConstraintKind.Fixed,siblingPath,sibling,FixedWorld:RigidTransform3d.Translate(0,30,0));
            var diagnostic=new AssemblyConstraint(AssemblyConstraintId.New(),"Old definition",AssemblyConstraintKind.Fixed,selected,DefinitionId.New(),FixedWorld:RigidTransform3d.Identity);
            await session.ExecuteAsync(new EditDocumentCommand("Keep mates",d=>d with{AssemblyConstraints=d.AssemblyConstraints
                .Add(mate.Id,mate).Add(fixedParent.Id,fixedParent).Add(fixedSibling.Id,fixedSibling).Add(diagnostic.Id,diagnostic)}));
            var sheet=TechnicalDrawingSheet.A4Landscape("Chosen occurrence");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,100),1,selected));
            var baseline=session.Snapshot;var count=assets.Count;using(var cancel=new CancellationTokenSource())
            {
                cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ExecuteAsync(AssemblyOccurrenceCommands.MakePartIndependent(selected),cancel.Token));
                Assert.Same(baseline,session.Snapshot);Assert.Equal(count,assets.Count);
            }
            var command=AssemblyOccurrenceCommands.MakePartIndependent(selected);await session.ExecuteAsync(command);var after=session.Snapshot;
            Assert.NotEqual(child,command.ResultPath!.Slots[1]);Assert.NotEqual(group,OccurrencePlacement.Resolve(after,parentPath).Slot.DefinitionId);
            Assert.Equal(part,OccurrencePlacement.Resolve(after,unselected).Slot.DefinitionId);
            foreach(var id in new[]{mate.Id,fixedParent.Id,fixedSibling.Id})Assert.Equal(AssemblyConstraintStatus.Satisfied,after.AssemblyConstraints[id].Evaluate(after).Status);
            Assert.Equal(AssemblyConstraintStatus.DefinitionChanged,after.AssemblyConstraints[diagnostic.Id].Evaluate(after).Status);
            var copiedDatum=after.Datums.Planes.Values.Single(p=>p.PartId==command.ResultPartId);Assert.Equal(copiedDatum.Id,after.Datums.WorkPlane!.Datum);
            Assert.Equal(command.ResultPath,after.Datums.WorkPlane.Occurrence);Assert.Equal(baseline.Settings.WorkPlane,after.Settings.WorkPlane);
            var drawing=after.DrawingSheets[sheet.Id].Views.Single();Assert.Null(drawing.StaleReason);Assert.Equal(command.ResultPath,drawing.Sources.Single().Path);
            Assert.Equal(command.ResultPartId,after.Features[drawing.Sources.Single().Feature!.Value].PartId);
            Assert.Equal(box.Result.Revision,drawing.Sources.Single().Revision);Assert.Equal(box.Result.AssetId,drawing.Sources.Single().Asset);
            Assert.NotEqual(siblingPath,after.AssemblyConstraints[fixedSibling.Id].PrimaryPath);
            Assert.Equal(sibling,OccurrencePlacement.Resolve(after,after.AssemblyConstraints[fixedSibling.Id].PrimaryPath).Slot.DefinitionId);
            after.Validate();await session.UndoAsync();Assert.Same(baseline,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            var storage=new CadDocumentStorage();var path=files.PathFor("nested.cadoryx");await session.SaveAsync(storage,path);
            using var loaded=await storage.LoadAsync(path,assets);loaded.Snapshot.Validate();
            Assert.All(new[]{mate.Id,fixedParent.Id,fixedSibling.Id},id=>Assert.Equal(AssemblyConstraintStatus.Satisfied,loaded.Snapshot.AssemblyConstraints[id].Evaluate(loaded.Snapshot).Status));
        }
        Assert.Equal(0,assets.Count);
    }
}
