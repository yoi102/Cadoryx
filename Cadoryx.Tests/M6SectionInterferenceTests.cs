using System.Collections.Immutable;
using System.Text.Json;
using Cadoryx.Cli;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class M6SectionInterferenceTests
{
    [Theory] [InlineData(0,100)] [InlineData(1,80)] [InlineData(2,60)]
    public async Task SectionProducesActualWorldCurvesInEachPlane(int axis,double length)
    {
        var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        using var box=await kernel.EvaluateAsync(new BoxRecipe(10,20,30,RigidTransform3d.Identity),assets);
        var n=axis switch{0=>new Vector3d(1,0,0),1=>new(0,1,0),_=>Vector3d.UnitZ};
        using var result=await kernel.SectionAsync([Instance(box.Geometry,RigidTransform3d.Translate(100,200,300))],new(n,axis switch{0=>105,1=>210,_=>315}),assets);
        Assert.Equal(BodyKind.Wire,result.Geometry.Kind);using var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets);
        var edges=shape.GetSubShapes(OcctSharp.ShapeKind.Edge);
        try{Assert.Equal(4,edges.Length);Assert.Equal(length,edges.Sum(e=>e.InspectProperties(OcctSharp.InspectionPropertyKind.Length).Mass),6);}
        finally{foreach(var edge in edges)edge.Dispose();}
    }
    [Fact] public async Task ObliqueSectionOfRotatedBodyUsesPlaneNormal()
    {
        var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        using var box=await kernel.EvaluateAsync(new BoxRecipe(10,10,10,RigidTransform3d.Identity),assets);
        var rotation=Quaterniond.FromAxisAngle(new(1,0,0),Math.PI/4);var normal=rotation.Rotate(Vector3d.UnitZ);
        using var result=await kernel.SectionAsync([Instance(box.Geometry,new(Vector3d.Zero,rotation))],new(normal,5),assets);
        using var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets);var edges=shape.GetSubShapes(OcctSharp.ShapeKind.Edge);
        try{Assert.Equal(40,edges.Sum(e=>e.InspectProperties(OcctSharp.InspectionPropertyKind.Length).Mass),6);}
        finally{foreach(var edge in edges)edge.Dispose();}
    }
    [Theory] [InlineData("step")] [InlineData("iges")]
    public async Task SectionCommandIsOneUndoableIndependentPartAndRoundTrips(string extension)
    {
        using var files=new TestFiles();using var assets=new DiskAssetStore(files.PathFor("cache"));var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Section"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
        var before=session.Snapshot;var inputs=Inputs(before);
        await session.ExecuteAsync(new CreateSectionCommand(inputs,new(Vector3d.UnitZ,15),"Cut at 15"));
        var section=session.Snapshot.Bodies.Values.Single(b=>b.Geometry.Kind==BodyKind.Wire);var after=session.Snapshot;
        Assert.NotEqual(inputs[0].BodyId,section.Id);Assert.Equal(2,after.Definitions.Values.OfType<PartDefinition>().Count());
        Assert.Empty(after.Features[section.Producer!.Value].Inputs);
        await session.UndoAsync();Assert.Equal(before.StateId,session.Snapshot.StateId);
        await session.RedoAsync();Assert.Equal(after,session.Snapshot);
        var storage=new CadDocumentStorage();await session.SaveAsync(storage,files.PathFor("section.cadoryx"));
        using var loaded=await storage.LoadAsync(files.PathFor("section.cadoryx"),assets);Assert.Equal(section,loaded.Snapshot.Bodies[section.Id]);
        var curvesOnly=after with{Bodies=after.Bodies.ToImmutableDictionary(p=>p.Key,p=>p.Value with{IsVisible=p.Key==section.Id})};
        await kernel.ExportAsync(curvesOnly,assets,files.PathFor("section."+extension));
        using var reimport=await kernel.ImportAsync(files.PathFor("section."+extension),assets);
        Assert.NotEmpty(reimport.Snapshot.Bodies);Assert.All(reimport.Snapshot.Bodies.Values,b=>Assert.Equal(BodyKind.Wire,b.Geometry.Kind));
    }
    [Fact] public async Task SectionRejectsEmptyStaleAndLockedDestinationWithoutPartialCommit()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Section"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Box"));
        var before=session.Snapshot;int count=assets.Count;var input=Inputs(before);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new CreateSectionCommand(input,new(Vector3d.UnitZ,100),"Empty")));
        Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new CreateSectionCommand([input[0] with{WorldTransform=RigidTransform3d.Translate(1,0,0)}],new(Vector3d.UnitZ,5),"Stale")));
        await session.ExecuteAsync(new EditDocumentCommand("Lock",s=>s with{Layers=s.Layers.ToImmutableDictionary(p=>p.Key,p=>p.Value with{IsLocked=true})}));
        var locked=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new CreateSectionCommand(input,new(Vector3d.UnitZ,5),"Locked")));
        Assert.Same(locked,session.Snapshot);
    }
    [Theory] [InlineData(15,BodyPairRelation.Separated,5,0)] [InlineData(10,BodyPairRelation.Touching,0,0)] [InlineData(5,BodyPairRelation.Interfering,0,500)]
    public async Task NativePairClassificationDistinguishesSeparationContactAndVolume(double translation,BodyPairRelation relation,double distance,double overlap)
    {
        var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        using var box=await kernel.EvaluateAsync(new BoxRecipe(10,10,10,RigidTransform3d.Identity),assets);
        var report=await kernel.CheckInterferenceAsync([Instance(box.Geometry,RigidTransform3d.Identity),Instance(box.Geometry,RigidTransform3d.Translate(translation,0,0))],1e-7,assets);
        var pair=Assert.Single(report.Pairs);Assert.Equal(relation,pair.Relation);Assert.Equal(distance,pair.DistanceMm,5);Assert.Equal(overlap,pair.OverlapVolumeMm3,5);
    }
    [Fact] public async Task ContainmentAndRepeatedInstancesAreNotCollapsedIntoOneDefinition()
    {
        var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        using var box=await kernel.EvaluateAsync(new BoxRecipe(10,10,10,RigidTransform3d.Identity),assets);
        using var small=await kernel.EvaluateAsync(new BoxRecipe(2,2,2,RigidTransform3d.Identity),assets);
        var report=await kernel.CheckInterferenceAsync([Instance(box.Geometry,RigidTransform3d.Identity),Instance(small.Geometry,RigidTransform3d.Translate(1,1,1)),Instance(box.Geometry,RigidTransform3d.Translate(20,0,0))],1e-7,assets);
        Assert.Equal(3,report.Pairs.Length);var contained=Assert.Single(report.Pairs.Where(p=>p.Relation==BodyPairRelation.Contained));
        Assert.Equal(8,contained.OverlapVolumeMm3,6);Assert.Equal(2,report.Pairs.Count(p=>p.Relation==BodyPairRelation.Separated));
    }
    [Fact] public async Task ReviewLimitsDuplicatesAndCancellationReleaseAssets()
    {
        var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();using var box=await kernel.EvaluateAsync(new BoxRecipe(1,1,1,RigidTransform3d.Identity),assets);
        var first=Instance(box.Geometry,RigidTransform3d.Identity);
        await Assert.ThrowsAsync<CadValidationException>(()=>kernel.CheckInterferenceAsync([first,first],1e-7,assets));
        await Assert.ThrowsAsync<CadValidationException>(()=>kernel.CheckInterferenceAsync(Enumerable.Range(0,33).Select(_=>Instance(box.Geometry,RigidTransform3d.Identity)).ToArray(),1e-7,assets));
        await Assert.ThrowsAsync<CadValidationException>(()=>kernel.SectionAsync([first],new(Vector3d.Zero,0),assets));
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>kernel.SectionAsync([first],new(Vector3d.UnitZ,.5),assets,cancellation.Token));
        Assert.Equal(1,assets.Count);
    }
    [Fact] public async Task InterferenceUiLocatesPairAndInvalidatesAfterEditing()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Review"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Box"));
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Translate(5,0,0)),"Tool"));
        var vm=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            vm.Selection.Replace(Inputs(session.Snapshot).Select(i=>new SelectionTarget(i.Path,i.BodyId,i.Geometry.Revision)));
            await vm.Review.CheckInterferenceAsync();var pair=Assert.Single(vm.Review.InterferenceRows);vm.Review.SelectedInterference=pair;
            vm.Review.LocateInterferenceCommand.Execute(null);Assert.Equal(2,vm.Selection.Items.Length);Assert.Single(vm.Review.InterferenceRows);
            await session.ExecuteAsync(DocumentEdits.RenameBody(pair.Finding.First.BodyId,"Renamed"));Assert.Empty(vm.Review.InterferenceRows);
        }
        finally{vm.Detach();}
    }
    [Fact] public async Task CliInterferenceReportPreservesInstanceIdentityAndDoesNotOverwrite()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Audit"),assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"A"));
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Translate(5,0,0)),"B"));
        var input=files.PathFor("audit.cadoryx");await session.SaveAsync(new CadDocumentStorage(),input);var output=files.PathFor("audit.json");var errors=new StringWriter();
        Assert.Equal(0,await CadCommandLine.RunAsync(["interference",input,"--output",output],new StringWriter(),errors));
        using var json=JsonDocument.Parse(File.ReadAllText(output));var pairs=json.RootElement.GetProperty("report").GetProperty("pairs");
        Assert.Equal("Interfering",pairs[0].GetProperty("relation").GetString());Assert.Equal(500,pairs[0].GetProperty("overlapVolumeMm3").GetDouble(),5);
        Assert.Equal(1,await CadCommandLine.RunAsync(["interference",input,"--output",output],new StringWriter(),errors));
    }
    private static GeometryInstance Instance(GeometryAssetRef geometry,RigidTransform3d transform)=>new(new(DocumentId.New(),[ComponentSlotId.New()]),BodyId.New(),geometry,transform);
    private static GeometryInstance[] Inputs(DocumentSnapshot snapshot)=>CadScene.FromDocument(snapshot).Items.Select(i=>new GeometryInstance(i.Path,i.BodyId,i.Geometry,i.WorldTransform)).ToArray();
}
