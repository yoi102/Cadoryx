using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class M6WorkspaceToolsTests
{
    [Fact] public async Task HistoryCountsSharedPayloadOnceAndTrimsWithoutChangingCurrentState()
    {
        var assets=new MemoryAssetStore();using var first=assets.Stage([1,2,3]);using var second=assets.Stage([4,5,6,7]);
        var initial=DocumentSnapshot.Create("History") with{RetainedAssets=[first.Id]};
        await using var session=Session(initial,assets);
        await session.ExecuteAsync(new EditDocumentCommand("Rename",s=>s with{Name="Renamed"}));
        Assert.Equal(0,session.HistoryUsage.AdditionalAssetBytes);
        await session.ExecuteAsync(new EditDocumentCommand("Replace",s=>s with{RetainedAssets=[second.Id]}));
        var current=session.Snapshot;var generation=session.Generation;
        Assert.Equal(3,session.HistoryUsage.AdditionalAssetBytes);Assert.Equal(1,session.HistoryUsage.AdditionalAssets);
        first.Dispose();second.Dispose();
        await session.ConfigureHistoryAsync(50,2);
        Assert.Same(current,session.Snapshot);Assert.Equal(generation,session.Generation);
        Assert.False(session.CanUndo);Assert.True(session.IsDirty);
        Assert.Equal(4,assets.SizeBytes);Assert.Equal(0,session.HistoryUsage.AdditionalAssetBytes);
    }

    [Fact] public async Task HistoryCountBudgetPreservesReachableUndoRedoChain()
    {
        await using var session=Session(DocumentSnapshot.Create("0"),new MemoryAssetStore());
        for(int i=1;i<=5;i++){string name=i.ToString();await session.ExecuteAsync(new EditDocumentCommand("Rename",s=>s with{Name=name}));}
        await session.UndoAsync();await session.UndoAsync();
        await session.ConfigureHistoryAsync(3,0);
        Assert.Equal(1,session.HistoryUsage.UndoEntries);Assert.Equal(2,session.HistoryUsage.RedoEntries);
        await session.UndoAsync();Assert.Equal("2",session.Snapshot.Name);Assert.False(session.CanUndo);
        await session.RedoAsync();await session.RedoAsync();await session.RedoAsync();
        Assert.Equal("5",session.Snapshot.Name);Assert.False(session.CanRedo);
    }

    [Fact] public async Task UndoReevaluatesAdditionalPayloadAndDropsUnaffordableRedo()
    {
        var assets=new MemoryAssetStore();using var blob=assets.Stage(new byte[16]);
        await using var session=Session(DocumentSnapshot.Create("History"),assets);
        await session.ConfigureHistoryAsync(50,0);
        await session.ExecuteAsync(new EditDocumentCommand("Attach",s=>s with{RetainedAssets=[blob.Id]}));
        blob.Dispose();Assert.True(session.CanUndo);Assert.Equal(0,session.HistoryUsage.AdditionalAssetBytes);
        await session.UndoAsync();Assert.Empty(session.Snapshot.ReferencedAssets());
        Assert.False(session.CanRedo);Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task ClearingHistoryPreservesSavedStateAndCapturedAssets()
    {
        var assets=new MemoryAssetStore();using var blob=assets.Stage([1,2,3,4]);
        var initial=DocumentSnapshot.Create("Saved") with{RetainedAssets=[blob.Id]};
        await using var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher(),"saved.cadoryx");
        using var capture=session.Capture();blob.Dispose();
        await session.ExecuteAsync(new EditDocumentCommand("Remove",s=>s with{RetainedAssets=[]}));
        await session.UndoAsync();Assert.False(session.IsDirty);
        await session.ClearHistoryAsync();Assert.False(session.CanUndo);Assert.False(session.CanRedo);Assert.False(session.IsDirty);
        Assert.Equal(4,assets.SizeBytes);
        await session.DisposeAsync();Assert.Equal(4,assets.SizeBytes);capture.Dispose();Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task NewEditDropsRedoPayloadAndInvalidLimitsDoNotApply()
    {
        var assets=new MemoryAssetStore();using var blob=assets.Stage([7,8,9]);
        await using var session=Session(DocumentSnapshot.Create("History"),assets);
        await session.ExecuteAsync(new EditDocumentCommand("Attach",s=>s with{RetainedAssets=[blob.Id]}));blob.Dispose();
        await session.UndoAsync();Assert.Equal(3,session.HistoryUsage.AdditionalAssetBytes);
        await session.ExecuteAsync(new EditDocumentCommand("Branch",s=>s with{Name="Branch"}));
        Assert.Equal(0,assets.Count);Assert.False(session.CanRedo);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>session.ConfigureHistoryAsync(0,12));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>session.ConfigureHistoryAsync(12,-1));
        Assert.Equal(50,session.HistoryLimit);
    }

    [Fact] public void SearchDistinguishesRepeatedDeepInstancesAndCapsResults()
    {
        var (snapshot,_,_)=Fixture();
        var result=ModelTreeSearch.Find(snapshot,"needle");
        Assert.Equal(2,result.Hits.Count);Assert.False(result.Truncated);
        Assert.NotEqual(result.Hits[0].Path,result.Hits[1].Path);
        Assert.Equal("Left / Insert / Needle",result.Hits[0].Breadcrumb);
        var limited=ModelTreeSearch.Find(snapshot,"needle",1);Assert.True(limited.Truncated);Assert.Single(limited.Hits);
        Assert.Empty(ModelTreeSearch.Find(snapshot," ").Hits);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        Assert.Throws<OperationCanceledException>(()=>ModelTreeSearch.Find(snapshot,"missing",200,cancel.Token));
    }

    [Fact] public async Task SearchLocateMaterializesOnlyChosenBranchAndRejectsStaleResults()
    {
        var (snapshot,body,bytes)=Fixture();var assets=new MemoryAssetStore();using var blob=assets.Stage(bytes);
        await using var session=Session(snapshot,assets);var vm=new CadDocumentViewModel(session,new OcctGeometryKernel(),new CadMessageLog());
        var tree=new ModelTreeToolboxViewModel(new Icons());
        try
        {
            tree.Bind(vm);tree.SearchText="needle";await tree.SearchCommand.ExecuteAsync(null);
            Assert.All(tree.Items,n=>Assert.False(n.IsExpanded));Assert.Equal(2,tree.SearchResults.Count);
            var hit=tree.SearchResults[1];tree.SelectedSearchHit=hit;tree.LocateSearchResultCommand.Execute(null);
            Assert.False(tree.Items[0].IsExpanded);Assert.True(tree.Items[1].IsExpanded);
            Assert.Equal(hit.Body,Assert.Single(vm.Selection.Items));
            var row=tree.Items[1].Children.Single().Children.Single();Assert.True(row.IsSelected);Assert.Equal(body.Id,row.Target!.BodyId);
            await session.ExecuteAsync(new EditDocumentCommand("Rename",s=>s with{Name="Changed"}));
            Assert.Empty(tree.SearchResults);tree.SelectedSearchHit=hit;tree.LocateSearchResultCommand.Execute(null);Assert.Null(tree.SelectedSearchHit);
            Assert.Null(tree.Reveal(new(DocumentId.New(),hit.Path.Slots)));
        }
        finally{tree.Bind(null);vm.Detach();}
    }

    [Fact] public async Task ModelTreeAndViewportSelectionStayInSync()
    {
        var (snapshot,body,bytes)=Fixture();var assets=new MemoryAssetStore();using var blob=assets.Stage(bytes);
        await using var session=Session(snapshot,assets);
        var vm=new CadDocumentViewModel(session,new OcctGeometryKernel(),new CadMessageLog());
        var tree=new ModelTreeToolboxViewModel(new Icons());
        try
        {
            tree.Bind(vm);
            var target=snapshot.EnumerateOccurrences()
                .Where(o=>o.DefinitionId==body.PartId)
                .Select(o=>new SelectionTarget(o.Path,body.Id,body.Geometry.Revision)).Last();
            vm.Selection.Replace([target]);
            var selected=tree.Items.SelectMany(n=>n.Children).SelectMany(n=>n.Children)
                .Single(n=>n.IsSelected);
            Assert.Equal(target,selected.Target);
            Assert.True(selected.IsChecked);
            tree.Select(tree.Items[0]);
            Assert.Equal(tree.Items[0].Path,vm.Selection.Occurrence);
            Assert.Empty(vm.Selection.Items);
            Assert.True(tree.Items[0].IsSelected);
            vm.Selection.SelectOccurrence(null);
            Assert.DoesNotContain(tree.Items,n=>n.IsSelected);
        }
        finally{tree.Bind(null);vm.Detach();}
    }

    [Fact] public void MeasurementUsesNestedWorldTransformsAndCountsRepeatedInstances()
    {
        var (snapshot,body,_)=Fixture();var targets=Targets(snapshot,body);
        var measure=SelectionMeasurement.Measure(snapshot,targets.Append(targets[0]))!;
        Assert.Equal(2,measure.BodyInstances);Assert.Equal(48,measure.VolumeSumMm3);Assert.Equal(0.048,measure.MassSumKg!.Value,10);
        // Parent rotations rotate both the local offset and the non-cubic local box.
        Assert.Equal(7,measure.WorldEnvelope.Min.X,9);Assert.Equal(0,measure.WorldEnvelope.Min.Y,9);
        Assert.Equal(24,measure.WorldEnvelope.Max.X,9);Assert.Equal(3,measure.WorldEnvelope.Max.Y,9);
        Assert.Equal(4,measure.WorldEnvelope.Max.Z,9);
        Assert.Equal(Math.Sqrt(14.5*14.5+0.5*0.5),measure.EnvelopeCenterDistanceMm!.Value,9);
    }

    [Fact] public void MeasurementRejectsStaleForeignAndMismatchedSelections()
    {
        var (snapshot,body,_)=Fixture();var target=Targets(snapshot,body)[0];
        Assert.Throws<CadValidationException>(()=>SelectionMeasurement.Measure(snapshot,[target with{GeometryRevision=GeometryRevisionId.New()}]));
        Assert.Throws<CadValidationException>(()=>SelectionMeasurement.Measure(snapshot,[target with{Path=new(DocumentId.New(),target.Path.Slots)}]));
        Assert.Throws<CadValidationException>(()=>SelectionMeasurement.Measure(snapshot,[target with{Path=new(snapshot.Id,[ComponentSlotId.New()])}]));
        Assert.Null(SelectionMeasurement.Measure(snapshot,[]));
    }

    [Fact] public void MeasurementDistinguishesMissingMaterialAndNonVolumetricBodies()
    {
        var (snapshot,body,_)=Fixture();var targets=Targets(snapshot,body);
        snapshot=snapshot with{Bodies=snapshot.Bodies.SetItem(body.Id,body with{MaterialId=null})};
        Assert.Null(SelectionMeasurement.Measure(snapshot,targets)!.MassSumKg);
        snapshot=snapshot with{Bodies=snapshot.Bodies.SetItem(body.Id,body with{Geometry=body.Geometry with{Kind=BodyKind.Sheet}})};
        var result=SelectionMeasurement.Measure(snapshot,targets)!;
        Assert.Null(result.MassSumKg);Assert.Equal(0,result.VolumeSumMm3);Assert.Equal(0,result.VolumetricInstances);
    }

    [Fact] public async Task PropertiesMeasurementsRespectDocumentUnitsAndRefreshAfterUndo()
    {
        var (snapshot,body,bytes)=Fixture();var assets=new MemoryAssetStore();using var blob=assets.Stage(bytes);
        snapshot=snapshot with{Settings=snapshot.Settings with{DisplayUnit=LengthUnit.Centimeter,DecimalPlaces=2}};
        await using var session=Session(snapshot,assets);var vm=new CadDocumentViewModel(session,new OcctGeometryKernel(),new CadMessageLog());
        var panel=new PropertiesToolboxViewModel(new Icons());
        try
        {
            panel.Bind(vm);vm.Selection.Replace(Targets(snapshot,body));
            Assert.Contains(panel.Measurements,r=>r.Value.EndsWith(" cm"));Assert.Contains(panel.Measurements,r=>r.Value.Contains(" cm³"));
            await session.ExecuteAsync(DocumentEdits.SetSettings(snapshot.Settings with{DisplayUnit=LengthUnit.Inch}));
            Assert.Contains(panel.Measurements,r=>r.Value.EndsWith(" in"));
            await session.UndoAsync();Assert.Contains(panel.Measurements,r=>r.Value.EndsWith(" cm"));
        }
        finally{panel.Bind(null);vm.Detach();}
    }

    private static CadDocumentSession Session(DocumentSnapshot snapshot,IAssetStore assets)=>new(snapshot,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
    private static SelectionTarget[] Targets(DocumentSnapshot snapshot,CadBody body)=>snapshot.EnumerateOccurrences()
        .Where(o=>o.DefinitionId==body.PartId).Select(o=>new SelectionTarget(o.Path,body.Id,body.Geometry.Revision)).ToArray();
    private static (DocumentSnapshot Snapshot,CadBody Body,byte[] Bytes) Fixture()
    {
        byte[] bytes=[21,22,23];var store=new MemoryAssetStore();using var blob=store.Stage(bytes);
        var snapshot=DocumentSnapshot.Create("Assembly");var part=DefinitionId.New();var group=DefinitionId.New();var bodyId=BodyId.New();
        var material=MaterialId.New();var child=ComponentSlotId.New();
        var body=new CadBody(bodyId,part,"Needle",new(blob.Id,GeometryRevisionId.New(),BodyKind.Solid,new(new(0,0,0),new(2,3,4)),24),
            null,snapshot.Layers.Keys.Single(),new(),MaterialId:material);
        snapshot=snapshot with
        {
            Bodies=snapshot.Bodies.Add(bodyId,body),Materials=snapshot.Materials.Add(material,new(material,"Test material",0.001)),
            Definitions=snapshot.Definitions.Add(part,new PartDefinition(part,"Part",[bodyId],[]))
                .Add(group,new AssemblyDefinition(group,"Group",[new(child,part,"Insert",RigidTransform3d.Translate(1,0,0))]))
                .SetItem(snapshot.RootAssemblyId,new AssemblyDefinition(snapshot.RootAssemblyId,"Root",[
                    new(ComponentSlotId.New(),group,"Left",new(new(10,0,0),Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/2))),
                    new(ComponentSlotId.New(),group,"Right",RigidTransform3d.Translate(21,0,0))]))
        };
        snapshot.Validate();return(snapshot,body,bytes);
    }
    private sealed class Icons:IToolboxIconProvider
    {public object ModelTree=>"";public object Properties=>"";public object Modeling=>"";public object Messages=>"";}
}
