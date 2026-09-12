using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;
public sealed class DocumentResourcesTests
{
    [Fact] public async Task CreationTargetFlowsThroughPreviewCommitAndAllSharedInstances()
    {
        await using var f=new Fixture();var a=await f.AddPart("A");var b=await f.AddPart("B");await f.DuplicateRoot(b);
        var layer=new CadLayer(LayerId.New(),"Red",0xFFFF0000);var material=new CadMaterial(MaterialId.New(),"Steel",7.85e-6);
        await f.Session.ExecuteAsync(ResourceCommands.AddLayer(layer));await f.Session.ExecuteAsync(ResourceCommands.AddMaterial(material));
        var vm=f.Vm;vm.SelectedTargetPart=a;await vm.PreviewCommand.ExecuteAsync(null);Assert.True(vm.HasPreview);
        vm.SelectedTargetPart=b;Assert.False(vm.HasPreview);Assert.Equal(0,f.Assets.Count);
        vm.SelectedCreationLayer=layer.Id;vm.SelectedCreationMaterial=material.Id;
        var before=f.Session.Snapshot;await vm.PreviewCommand.ExecuteAsync(null);
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(2,vm.PreviewScene!.Items.Length);
        var geometry=vm.PreviewGeometry;int calls=f.Kernel.Evaluations;await vm.ConfirmCommand.ExecuteAsync(null);
        var body=Assert.Single(f.Session.Snapshot.Bodies.Values);
        Assert.Equal(b,body.PartId);Assert.Equal(layer.Id,body.LayerId);Assert.Equal(material.Id,body.MaterialId);Assert.Equal(geometry,body.Geometry);
        Assert.Equal(calls,f.Kernel.Evaluations);Assert.All(vm.Scene.Items,i=>Assert.Equal(layer.Argb,i.Argb));
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Equal(body,f.Session.Snapshot.Bodies[body.Id]);
    }

    [Fact] public async Task AmbiguousMissingAndLockedCreationTargetsFailBeforeNativeWork()
    {
        await using var f=new Fixture();await f.AddPart("A");await f.AddPart("B");
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new AddBodyCommand(Box(),"Ambiguous")));
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new AddBodyCommand(Box(),"Missing",DefinitionId.New())));
        var layer=f.Session.Snapshot.Layers.Values.Single();await f.Session.ExecuteAsync(ResourceCommands.UpdateLayer(layer with{IsLocked=true}));
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new AddBodyCommand(Box(),"Locked",f.Vm.SelectedTargetPart,layer.Id)));
        Assert.Equal(0,f.Kernel.Evaluations);Assert.Empty(f.Session.Snapshot.Bodies);
    }

    [Fact] public async Task ResourceDefaultsArePerDocumentAndFollowUndoOfTheirCreation()
    {
        await using var f=new Fixture();await using var other=new Fixture();using var manager=new DocumentResourcesViewModel(f.Vm);
        manager.PartName="New target";await manager.AddPartCommand.ExecuteAsync(null);var part=manager.SelectedPart!.Id;
        Assert.Equal(part,f.Vm.SelectedTargetPart);Assert.Empty(other.Vm.TargetParts);
        await f.Session.UndoAsync();Assert.Empty(f.Vm.TargetParts);Assert.Null(f.Vm.SelectedTargetPart);
        await f.Session.RedoAsync();Assert.Equal(part,f.Vm.SelectedTargetPart);
        manager.MaterialName="Steel";manager.DensityKgPerM3=7850;await manager.AddMaterialCommand.ExecuteAsync(null);
        Assert.Equal(7.85e-6,f.Session.Snapshot.Materials.Values.Single().DensityKgPerMm3,12);Assert.Empty(other.Vm.CreationMaterials);
    }

    [Theory][InlineData("layer")][InlineData("material")]
    public async Task ResourceNamesRejectDuplicatesWithoutDirtyingState(string kind)
    {
        await using var f=new Fixture();
        await f.Session.ExecuteAsync(kind=="layer"?ResourceCommands.AddLayer(new(LayerId.New(),"Resource")):ResourceCommands.AddMaterial(new(MaterialId.New(),"Resource",1e-6)));
        var before=f.Session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(kind=="layer"?
            ResourceCommands.AddLayer(new(LayerId.New()," RESOURCE ")):ResourceCommands.AddMaterial(new(MaterialId.New()," RESOURCE ",1e-6))));
        Assert.Same(before,f.Session.Snapshot);
    }

    [Theory][InlineData(0)][InlineData(-1)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)]
    public async Task InvalidDensityIsRejectedAtomically(double density)
    {
        await using var f=new Fixture();var before=f.Session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(ResourceCommands.AddMaterial(new(MaterialId.New(),"Invalid",density))));
        Assert.Same(before,f.Session.Snapshot);
    }

    [Fact] public async Task LayerColorAndVisibilityAffectAllInstancesWithoutChangingGeometry()
    {
        await using var f=new Fixture();var body=await f.AddBox();await f.DuplicateRoot(body.PartId);var layer=f.Session.Snapshot.Layers[body.LayerId];
        int calls=f.Kernel.Evaluations;
        await f.Session.ExecuteAsync(ResourceCommands.UpdateLayer(layer with{Argb=0xFF123456}));
        Assert.All(f.Vm.Scene.Items,item=>Assert.Equal(0xFF123456u,item.Argb));Assert.Equal(2,f.Vm.Scene.Items.Length);
        await f.Session.ExecuteAsync(ResourceCommands.UpdateLayer(layer with{IsVisible=false}));Assert.Empty(f.Vm.Scene.Items);
        await f.Session.UndoAsync();Assert.All(f.Vm.Scene.Items,item=>Assert.Equal(0xFF123456u,item.Argb));
        Assert.Equal(calls,f.Kernel.Evaluations);Assert.Equal(body.Geometry,f.Session.Snapshot.Bodies[body.Id].Geometry);
    }

    [Fact] public async Task LayerLockBlocksPropertiesBooleanAndDependentRecompute()
    {
        await using var f=new Fixture();var a=await f.AddBox();var b=await f.AddBox();var layer=f.Session.Snapshot.Layers[a.LayerId];
        await f.Session.ExecuteAsync(ResourceCommands.UpdateLayer(layer with{IsLocked=true}));var before=f.Session.Snapshot;int calls=f.Kernel.Evaluations;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DocumentEdits.SetBodyProperties(a.Id,"Changed",new(),true,layer.Id,null)));
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new BooleanCommand(BooleanOperation.Fuse,[a.Id,b.Id])));
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new RecomputeCommand(a.Producer!.Value,Box(20))));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(calls,f.Kernel.Evaluations);
    }

    [Fact] public async Task ReferencedAndLastLayersCannotBeDeletedButUnusedResourcesCanBeUndone()
    {
        await using var f=new Fixture();var defaultLayer=f.Session.Snapshot.Layers.Keys.Single();
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(ResourceCommands.DeleteLayer(defaultLayer)));
        var extra=new CadLayer(LayerId.New(),"Unused");await f.Session.ExecuteAsync(ResourceCommands.AddLayer(extra));
        var body=await f.AddBox();await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(ResourceCommands.DeleteLayer(body.LayerId)));
        var unused=f.Session.Snapshot.Layers.Keys.Single(id=>id!=body.LayerId);
        await f.Session.ExecuteAsync(ResourceCommands.DeleteLayer(unused));Assert.False(f.Session.Snapshot.Layers.ContainsKey(unused));
        await f.Session.UndoAsync();Assert.True(f.Session.Snapshot.Layers.ContainsKey(unused));
    }

    [Fact] public async Task PropertyReassignmentUpdatesRetainedMetadataAndClearingMaterialEnablesDeletion()
    {
        await using var f=new Fixture();var body=await f.AddBox();var material=new CadMaterial(MaterialId.New(),"Steel",7.85e-6);
        var layer=new CadLayer(LayerId.New(),"Finish");await f.Session.ExecuteAsync(ResourceCommands.AddLayer(layer));await f.Session.ExecuteAsync(ResourceCommands.AddMaterial(material));
        await f.Session.ExecuteAsync(DocumentEdits.SetBodyProperties(body.Id,"Finished",new(0xFF010203,true),true,layer.Id,material.Id));
        var saved=f.Session.Snapshot;Assert.Equal(material.Id,saved.Features[body.Producer!.Value].OutputMetadata!.Material);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(ResourceCommands.DeleteMaterial(material.Id)));
        var properties=new PropertiesToolboxViewModel(new Icons());properties.Bind(f.Vm);
        var item=f.Vm.Scene.Items.Single();f.Vm.Selection.Replace([new(item.Path,item.BodyId,item.Geometry.Revision)]);
        Assert.Contains(properties.Properties,row=>row.Name==Cadoryx.Lang.Strings.Strings.MassKg);
        properties.ClearMaterialCommand.Execute(null);await properties.ApplyCommand.ExecuteAsync(null);properties.Bind(null);
        Assert.Null(f.Session.Snapshot.Features[body.Producer.Value].OutputMetadata!.Material);
        await f.Session.ExecuteAsync(ResourceCommands.DeleteMaterial(material.Id));Assert.Empty(f.Session.Snapshot.Materials);
        await f.Session.UndoAsync();await f.Session.UndoAsync();Assert.Equal(saved.StateId,f.Session.Snapshot.StateId);
    }

    [Fact] public async Task EmptyBooleanRetainsResourcesAndLockedOutputsStillBlockRecompute()
    {
        await using var f=new Fixture();var material=new CadMaterial(MaterialId.New(),"Steel",7.85e-6);
        await f.Session.ExecuteAsync(ResourceCommands.AddMaterial(material));
        var a=await f.AddBox(material:material.Id);var b=await f.AddBox(placement:RigidTransform3d.Translate(100,0,0));
        await f.Session.ExecuteAsync(new BooleanCommand(BooleanOperation.Common,[a.Id,b.Id]));Assert.Empty(f.Session.Snapshot.Bodies);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(ResourceCommands.DeleteMaterial(material.Id)));
        var layer=f.Session.Snapshot.Layers[a.LayerId];await f.Session.ExecuteAsync(ResourceCommands.UpdateLayer(layer with{IsLocked=true}));
        int calls=f.Kernel.Evaluations;await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new RecomputeCommand(b.Producer!.Value,Box())));
        Assert.Equal(calls,f.Kernel.Evaluations);
        await f.Session.ExecuteAsync(ResourceCommands.UpdateLayer(layer));await f.Session.ExecuteAsync(new RecomputeCommand(b.Producer!.Value,Box()));
        var restored=Assert.Single(f.Session.Snapshot.Bodies.Values);Assert.Equal(material.Id,restored.MaterialId);Assert.Equal(1000,restored.Geometry.VolumeMm3,6);
    }

    [Fact] public async Task MovingOneRootInstancePreservesSharedGeometryAndExactUndo()
    {
        await using var f=new Fixture();var body=await f.AddBox();await f.DuplicateRoot(body.PartId);
        var occurrences=f.Session.Snapshot.EnumerateOccurrences().ToArray();var before=f.Session.Snapshot;
        f.Vm.Selection.SelectOccurrence(occurrences[0].Path);Assert.True(f.Vm.Placement.CanMove);
        f.Vm.Placement.X=35;f.Vm.Placement.AngleDegrees=90;await f.Vm.Placement.ApplyCommand.ExecuteAsync(null);
        var moved=f.Session.Snapshot.EnumerateOccurrences().ToArray();Assert.Equal(35,moved[0].WorldTransform.Translation.X);Assert.Equal(occurrences[1],moved[1]);
        Assert.Equal(body.Geometry,f.Session.Snapshot.Bodies[body.Id].Geometry);Assert.Equal(1,f.Kernel.Evaluations);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);Assert.Equal(0,f.Vm.Placement.X);await f.Session.RedoAsync();Assert.Equal(35,f.Vm.Placement.X);
    }

    [Fact] public async Task NestedMoveUsesParentCoordinatesAndTranslationKeepsExactQuaternion()
    {
        await using var f=new Fixture();var body=await f.AddBox();var sub=DefinitionId.New();var child=ComponentSlotId.New();
        var rotation=Quaterniond.FromAxisAngle(new(1,2,3),0.731);var parent=new RigidTransform3d(new(10,0,0),Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/2));
        await f.Session.ExecuteAsync(new EditDocumentCommand("fixture",d=>d with{Definitions=d.Definitions.Add(sub,new AssemblyDefinition(sub,"Sub",[new(child,body.PartId,"Child",new(new(0,0,0),rotation))]))
            .SetItem(d.RootAssemblyId,new AssemblyDefinition(d.RootAssemblyId,"Root",[new(ComponentSlotId.New(),sub,"Sub",parent)]))}));
        var path=f.Session.Snapshot.EnumerateOccurrences().Last().Path;f.Vm.Selection.SelectOccurrence(path);f.Vm.Placement.X=12;await f.Vm.Placement.ApplyCommand.ExecuteAsync(null);
        var moved=f.Session.Snapshot.EnumerateOccurrences().Last();Assert.Equal(10,moved.WorldTransform.Translation.X,9);Assert.Equal(12,moved.WorldTransform.Translation.Y,9);
        Assert.Equal(rotation,OccurrencePlacement.Resolve(f.Session.Snapshot,path).Slot.LocalTransform.Rotation);
    }

    [Fact] public async Task ReusedParentCannotSilentlyMoveOtherOccurrencePaths()
    {
        await using var f=new Fixture();var part=await f.AddPart("Part");var sub=DefinitionId.New();
        await f.Session.ExecuteAsync(new EditDocumentCommand("fixture",d=>d with{Definitions=d.Definitions.Add(sub,new AssemblyDefinition(sub,"Sub",[new(ComponentSlotId.New(),part,"Child",RigidTransform3d.Identity)]))
            .SetItem(d.RootAssemblyId,new AssemblyDefinition(d.RootAssemblyId,"Root",[new(ComponentSlotId.New(),sub,"A",RigidTransform3d.Identity),new(ComponentSlotId.New(),sub,"B",RigidTransform3d.Translate(100,0,0))]))}));
        var path=f.Session.Snapshot.EnumerateOccurrences().First(o=>o.DefinitionId==part).Path;var before=f.Session.Snapshot;f.Vm.Selection.SelectOccurrence(path);
        Assert.False(f.Vm.Placement.CanMove);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(path,RigidTransform3d.Translate(5,0,0))));
        Assert.Same(before,f.Session.Snapshot);
    }

    [Fact] public async Task ResourceAndPlacementStateSurvivesSaveReopenAndRecovery()
    {
        await using var f=new Fixture();using var files=new TestFiles();var part=await f.AddPart("Machined");
        var layer=new CadLayer(LayerId.New(),"Finish",0xFFAABBCC);var material=new CadMaterial(MaterialId.New(),"Alloy",2.7e-6);
        await f.Session.ExecuteAsync(ResourceCommands.AddLayer(layer));await f.Session.ExecuteAsync(ResourceCommands.AddMaterial(material));
        await f.Session.ExecuteAsync(new AddBodyCommand(Box(),"Block",part,layer.Id,material.Id));
        var path=f.Session.Snapshot.EnumerateOccurrences().Single().Path;await f.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(path,RigidTransform3d.Translate(4,5,6)));
        var expected=f.Session.Snapshot;var storage=new CadDocumentStorage();string saved=files.PathFor("state.cadoryx");await f.Session.SaveAsync(storage,saved);
        using(var loaded=await storage.LoadAsync(saved,f.Assets))Check(loaded.Snapshot);
        string root=files.PathFor("recovery");using(var writer=new CadRecoveryStore(root,storage))await writer.WriteAsync(f.Session.SessionId,expected,f.Assets,saved);
        using var reader=new CadRecoveryStore(root,storage);using var recovered=await reader.OpenAsync((await reader.ScanAsync()).Entries.Single().Key,f.Assets);Check(recovered.Document.Snapshot);
        void Check(DocumentSnapshot actual)
        {
            Assert.Equal(expected.StateId,actual.StateId);Assert.Equal(expected.Bodies.Values.Single(),actual.Bodies.Values.Single());
            Assert.Equal(layer,actual.Layers[layer.Id]);Assert.Equal(material,actual.Materials[material.Id]);
            Assert.Equal(new Vector3d(4,5,6),actual.EnumerateOccurrences().Single().WorldTransform.Translation);
        }
    }

    private static BoxRecipe Box(double size=10)=>new(size,10,10,RigidTransform3d.Identity);
    private sealed class Fixture : IAsyncDisposable
    {
        public MemoryAssetStore Assets {get;}=new();public CountingKernel Kernel {get;}=new();
        public CadDocumentSession Session {get;}public CadDocumentViewModel Vm {get;}
        public Fixture(){Session=new(DocumentSnapshot.Create("Resources"),Assets,Kernel,new InlineSessionDispatcher());Vm=new(Session,Kernel,new CadMessageLog());}
        public async Task<DefinitionId> AddPart(string name){var id=DefinitionId.New();await Session.ExecuteAsync(ResourceCommands.AddPart(id,name));return id;}
        public async Task<CadBody> AddBox(RigidTransform3d? placement=null,MaterialId? material=null)
        {
            var previous=Session.Snapshot.Bodies.Keys.ToHashSet();await Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,placement??RigidTransform3d.Identity),"Box",Vm.SelectedTargetPart,material:material));
            return Session.Snapshot.Bodies.Values.Single(b=>!previous.Contains(b.Id));
        }
        public Task DuplicateRoot(DefinitionId part)=>Session.ExecuteAsync(new EditDocumentCommand("fixture",d=>
        {var root=(AssemblyDefinition)d.Definitions[d.RootAssemblyId];return d with{Definitions=d.Definitions.SetItem(root.Id,root with{Children=root.Children.Add(new(ComponentSlotId.New(),part,"Copy",RigidTransform3d.Translate(50,0,0)))})};}));
        public async ValueTask DisposeAsync(){await Vm.StopToolsAsync();Vm.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
    private sealed class CountingKernel : IGeometryKernel
    {
        private readonly OcctGeometryKernel inner=new();public int Evaluations {get;private set;}public string Version=>inner.Version;
        public bool Supports(GeometryRecipe recipe)=>inner.Supports(recipe);
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default){Evaluations++;return inner.EvaluateAsync(recipe,assets,token);}
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>inner.ImportAsync(path,assets,token);
        public Task ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken token=default)=>inner.ExportAsync(snapshot,assets,path,token);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>inner.ExportAsync(snapshot,assets,path,options,token);
    }
    private sealed class Icons : IToolboxIconProvider
    {public object ModelTree=>"";public object Properties=>"";public object Modeling=>"";public object Messages=>"";}
}
