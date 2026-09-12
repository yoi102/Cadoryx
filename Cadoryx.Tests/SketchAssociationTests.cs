using System.Collections.Immutable;
using System.Security.Cryptography;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchAssociationTests
{
    [Fact] public async Task SketchEditRebuildsExtrusionAndBooleanAsOneExactUndoableChange()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new InspectingKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var solver=new ManagedSketchConstraintSolver();
        var sketch=SketchTestData.Rectangle(part);await session.ExecuteAsync(new UpsertSketchCommand(sketch,solver));sketch=session.Snapshot.Sketches[sketch.Id];
        await session.ExecuteAsync(Extrude(sketch,10));var feature=session.Snapshot.Features.Values.Single();
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Cut tool",part));
        var ids=session.Snapshot.Bodies.Values.OrderByDescending(b=>b.Geometry.VolumeMm3).Select(b=>b.Id).ToArray();
        await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,ids));var before=session.Snapshot;var output=Assert.Single(before.Bodies.Values);Assert.Equal(11000,output.Geometry.VolumeMm3,3);
        await session.SaveAsync(new CadDocumentStorage(),files.PathFor("saved.cadoryx"));int calls=kernel.Calls;long generation=session.Generation;
        await session.ExecuteAsync(new UpsertSketchCommand(SketchCommandTests.Resize(sketch,60),solver));var after=session.Snapshot;
        Assert.Equal(calls+2,kernel.Calls);Assert.Equal(generation+1,session.Generation);Assert.Equal(17000,after.Bodies[output.Id].Geometry.VolumeMm3,3);
        Assert.NotEqual(sketch.Revision,after.Sketches[sketch.Id].Revision);Assert.Equal(after.Sketches[sketch.Id].Revision,after.Features[feature.Id].SketchSource!.Revision);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);Assert.False(session.IsDirty);await session.RedoAsync();Assert.Same(after,session.Snapshot);Assert.Equal(calls+2,kernel.Calls);
        await session.SaveAsync(new CadDocumentStorage(),files.PathFor("linked.cadoryx"));
        using var loaded=await new CadDocumentStorage().LoadAsync(files.PathFor("linked.cadoryx"),assets);
        Assert.Equal(after.Sketches[sketch.Id].Revision,loaded.Snapshot.Sketches[sketch.Id].Revision);
        Assert.Equal(after.Features[feature.Id].SketchSource!.Lines.ToArray(),loaded.Snapshot.Features[feature.Id].SketchSource!.Lines.ToArray());
        Assert.Equal(after.Bodies[output.Id],loaded.Snapshot.Bodies[output.Id]);loaded.Snapshot.Validate();
    }
    [Fact] public async Task MultipleRootsRebuildTheirCommonDependentOnlyOnce()
    {
        var (initial,part)=SketchCommandTests.Document();var kernel=new InspectingKernel();await using var session=new CadDocumentSession(initial,new MemoryAssetStore(),kernel,new InlineSessionDispatcher());
        var solver=new ManagedSketchConstraintSolver();var sketch=SketchTestData.Rectangle(part);await session.ExecuteAsync(new UpsertSketchCommand(sketch,solver));sketch=session.Snapshot.Sketches[sketch.Id];
        await session.ExecuteAsync(Extrude(sketch,10));await session.ExecuteAsync(Extrude(sketch,20));
        await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Fuse,session.Snapshot.Bodies.Keys));int before=kernel.Calls;
        await session.ExecuteAsync(new UpsertSketchCommand(SketchCommandTests.Resize(sketch,50),solver));
        Assert.Equal(before+3,kernel.Calls);Assert.Equal(30000,Assert.Single(session.Snapshot.Bodies.Values).Geometry.VolumeMm3,3);
    }
    [Fact] public async Task DownstreamFailureAndLockedLayerKeepSketchGeometryAndAssetsUnchanged()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new InspectingKernel();await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var solver=new ManagedSketchConstraintSolver();var sketch=SketchTestData.Rectangle(part);await session.ExecuteAsync(new UpsertSketchCommand(sketch,solver));sketch=session.Snapshot.Sketches[sketch.Id];
        await session.ExecuteAsync(Extrude(sketch,10));await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"B",part));
        await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Fuse,session.Snapshot.Bodies.Keys));var before=session.Snapshot;int count=assets.Count;
        kernel.FailBoolean=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(SketchCommandTests.Resize(sketch,60),solver)));
        Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
        kernel.FailBoolean=false;var layer=before.Layers.Values.Single();await session.ExecuteAsync(ResourceCommands.UpdateLayer(layer with{IsLocked=true}));before=session.Snapshot;int calls=kernel.Calls;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(SketchCommandTests.Resize(sketch,60),solver)));
        Assert.Same(before,session.Snapshot);Assert.Equal(calls,kernel.Calls);
    }
    [Fact] public async Task InvalidProfileDeletionStaleRevisionAndCrossPartReferencesAreRejected()
    {
        var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var solver=new ManagedSketchConstraintSolver();var draft=SketchTestData.Rectangle(part);await session.ExecuteAsync(new UpsertSketchCommand(draft,solver));var s=session.Snapshot.Sketches[draft.Id];await session.ExecuteAsync(Extrude(s,5));var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new RemoveSketchCommand(s.Id)));
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(draft,solver)));
        var broken=s with{Lines=s.Lines.RemoveAt(0),Constraints=s.Constraints.Where(c=>!SketchConstraintEditing.Targets(c).Contains(s.Lines[0].Id)).ToImmutableArray()};
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(broken,solver)));Assert.Same(before,session.Snapshot);
        var f=before.Features.Values.Single();Assert.Throws<CadValidationException>(()=>(before with{Features=before.Features.SetItem(f.Id,f with{SketchSource=f.SketchSource! with{Revision=Guid.NewGuid()}})}).Validate());
        var other=DefinitionId.New();await session.ExecuteAsync(ResourceCommands.AddPart(other,"Other"));
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddBodyCommand(f.Recipe,"Wrong part",other,sketchSource:f.SketchSource)));
    }
    [Fact] public async Task LinkedRevolutionFollowsItsSketchPlaneAndDimensions()
    {
        var (initial,part)=SketchCommandTests.Document();await using var session=new CadDocumentSession(initial,new MemoryAssetStore(),new OcctGeometryKernel(),new InlineSessionDispatcher());
        var s=CadSketch.Create(part,"Revolve",RigidTransform3d.Translate(4,5,6));var draft=new SketchDraft(s);draft.AddRectangle(new(10,0),new(20,5));
        var solver=new ManagedSketchConstraintSolver();await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));s=session.Snapshot.Sketches[s.Id];
        await session.ExecuteAsync(new AddBodyCommand(new RevolveRecipe(SketchProfileBuilder.Polygon(s,s.Lines.Select(l=>l.Id)),Math.PI*2,s.Plane),"Revolve",part,sketchSource:SketchProfileReference.Create(s,s.Lines.Select(l=>l.Id))));
        var before=Assert.Single(session.Snapshot.Bodies.Values).Geometry.VolumeMm3;
        await session.ExecuteAsync(new UpsertSketchCommand(SketchCommandTests.Resize(s,20),solver));var after=Assert.Single(session.Snapshot.Bodies.Values).Geometry.VolumeMm3;
        Assert.True(after>before*2);session.Snapshot.Validate();
    }
    [Fact] public async Task FrozenM4S1FileMigratesWithStableBaselineRevisionAndNoInventedAssociation()
    {
        var path=Path.Combine(AppContext.BaseDirectory,"Fixtures","Storage","m4s1-sketches.cadoryx");
        Assert.Equal("7ef68017601b94e031858cd401ee07e0a02b2652a677c95a23cafd5e7806eb46",Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();using var a=await storage.LoadAsync(path,assets);using var b=await storage.LoadAsync(path,assets);
        var sketch=Assert.Single(a.Snapshot.Sketches.Values);Assert.Equal(sketch.Id.Value,sketch.Revision);Assert.Equal(sketch.Revision,b.Snapshot.Sketches[sketch.Id].Revision);
        Assert.All(a.Snapshot.Features.Values,f=>Assert.Null(f.SketchSource));Assert.Contains(a.Diagnostics,d=>d.Code=="IO.MIGRATED");
        using var files=new TestFiles();await storage.SaveAsync(a.Snapshot,assets,files.PathFor("new.cadoryx"));using var current=await storage.LoadAsync(files.PathFor("new.cadoryx"),assets);
        Assert.Empty(current.Diagnostics);Assert.Equal(a.Snapshot.StateId,current.Snapshot.StateId);Assert.Equal(a.Snapshot.Bodies.Values.OrderBy(x=>x.Id.Value),current.Snapshot.Bodies.Values.OrderBy(x=>x.Id.Value));
        var manifest=FormatEvolutionTests.Manifest(files.PathFor("new.cadoryx"));Assert.Equal(5,manifest.Sections.Single(s=>s.Kind=="features").SchemaVersion);Assert.Equal(2,manifest.Sections.Single(s=>s.Kind=="sketches").SchemaVersion);
    }
    internal static AddBodyCommand Extrude(CadSketch s,double distance)=>new(new ExtrudeRecipe(SketchProfileBuilder.Polygon(s,s.Lines.Select(l=>l.Id)),distance,s.Plane),"Linked extrusion",s.PartId,sketchSource:SketchProfileReference.Create(s,s.Lines.Select(l=>l.Id)));
    internal sealed class InspectingKernel:IGeometryKernel
    {
        private readonly IGeometryKernel inner=new OcctGeometryKernel();public int Calls {get;private set;}public bool FailBoolean {get;set;}public string Version=>inner.Version;
        public bool Supports(GeometryRecipe recipe)=>inner.Supports(recipe);
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken cancellationToken=default)
        {Calls++;if(FailBoolean&&recipe is BooleanRecipe)throw new InvalidOperationException("Controlled downstream failure.");return inner.EvaluateAsync(recipe,assets,cancellationToken);}
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken cancellationToken=default)=>inner.ImportAsync(path,assets,cancellationToken);
        public Task ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken cancellationToken=default)=>inner.ExportAsync(snapshot,assets,path,cancellationToken);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CadExportOptions options,CancellationToken cancellationToken=default)=>inner.ExportAsync(snapshot,assets,path,options,cancellationToken);
    }
}
