using System.Collections.Immutable;
using System.IO.Compression;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using Xunit;

namespace Cadoryx.Tests;
public sealed class M6EngineeringReviewTests
{
    private static GeometryInstance[] Inputs(DocumentSnapshot d)=>CadScene.FromDocument(d).Items.Select(i=>new GeometryInstance(i.Path,i.BodyId,i.Geometry,i.WorldTransform)).ToArray();
    [Fact] public async Task AssociatedSectionRecomputesAtomicallyAndRetainsIdentityAcrossUndoAndSave()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var s=new CadDocumentSession(DocumentSnapshot.Create("Review"),assets,kernel,new InlineSessionDispatcher());
        await s.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
        var box=s.Snapshot.Features.Values.Single();
        await s.ExecuteAsync(new CreateSectionCommand(Inputs(s.Snapshot),new(Vector3d.UnitZ,15),"Section",true,SectionOutput.Faces));
        var section=Assert.Single(s.Snapshot.AssociatedSections).Key;var before=s.Snapshot;var output=before.Features[section].OutputBodyId;
        Assert.Equal(200,Area(before.Features[section].Result,assets),6);
        await s.ExecuteAsync(new RecomputeCommand(box.Id,new BoxRecipe(25,20,30,RigidTransform3d.Identity)));
        Assert.Equal(500,Area(s.Snapshot.Features[section].Result,assets),6);Assert.Equal(output,s.Snapshot.Features[section].OutputBodyId);
        Assert.Null(s.Snapshot.AssociatedSections[section].StaleReason);var after=s.Snapshot;
        await s.UndoAsync();Assert.Same(before,s.Snapshot);await s.RedoAsync();Assert.Same(after,s.Snapshot);
        var storage=new CadDocumentStorage();await s.SaveAsync(storage,files.PathFor("section.cadoryx"));
        using var loaded=await storage.LoadAsync(files.PathFor("section.cadoryx"),assets);
        Assert.Equal(after.AssociatedSections[section].Sources.ToArray(),loaded.Snapshot.AssociatedSections[section].Sources.ToArray());
        Assert.Equal(after.Features[section].Result,loaded.Snapshot.Features[section].Result);
    }
    [Fact] public async Task AssociationFreezesOnMissingOrNonIntersectingSourceAndDetachPermitsModeling()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var s=new CadDocumentSession(DocumentSnapshot.Create("Review"),assets,kernel,new InlineSessionDispatcher());
        await s.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Box"));
        var source=s.Snapshot.Features.Values.Single();
        await s.ExecuteAsync(new CreateSectionCommand(Inputs(s.Snapshot),new(Vector3d.UnitZ,5),"Cut",true));
        var id=Assert.Single(s.Snapshot.AssociatedSections).Key;var old=s.Snapshot.Features[id].Result;
        await s.ExecuteAsync(new RecomputeCommand(source.Id,new BoxRecipe(10,10,2,RigidTransform3d.Identity)));
        Assert.True(s.Snapshot.Features[id].IsStale);Assert.Equal(old,s.Snapshot.Features[id].Result);
        await s.UndoAsync();Assert.False(s.Snapshot.Features[id].IsStale);
        var root=(AssemblyDefinition)s.Snapshot.Definitions[s.Snapshot.RootAssemblyId];
        await s.ExecuteAsync(new EditDocumentCommand("Remove source occurrence",d=>d with{Definitions=d.Definitions.SetItem(root.Id,root with{
            Children=[..root.Children.Where(c=>c.DefinitionId!=source.PartId)]})}));
        Assert.Contains("missing",s.Snapshot.AssociatedSections[id].StaleReason!);Assert.Equal(old,s.Snapshot.Features[id].Result);
        await s.ExecuteAsync(new EditAssociatedSectionCommand(id,Vector3d.UnitZ,5,SectionOutput.Curves,true));
        Assert.Empty(s.Snapshot.AssociatedSections);Assert.False(s.Snapshot.Features[id].IsStale);
    }
    [Fact] public async Task PlacementChangeRefreshAndInvalidPlaneAreAtomic()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var s=new CadDocumentSession(DocumentSnapshot.Create("Review"),assets,kernel,new InlineSessionDispatcher());
        await s.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Box"));
        var source=Inputs(s.Snapshot).Single();await s.ExecuteAsync(new CreateSectionCommand([source],new(Vector3d.UnitZ,5),"Cut",true));
        var id=Assert.Single(s.Snapshot.AssociatedSections).Key;var root=(AssemblyDefinition)s.Snapshot.Definitions[s.Snapshot.RootAssemblyId];
        await s.ExecuteAsync(new EditDocumentCommand("Move",d=>d with{Definitions=d.Definitions.SetItem(root.Id,root with{
            Children=[..root.Children.Select(c=>c.Id==source.Path.Slots[0]?c with{LocalTransform=RigidTransform3d.Translate(100,0,0)}:c)]})}));
        Assert.InRange(s.Snapshot.Features[id].Result.Bounds.Min.X,99.999,100.001);var before=s.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>s.ExecuteAsync(new EditAssociatedSectionCommand(id,Vector3d.Zero,5,SectionOutput.Faces)));
        Assert.Same(before,s.Snapshot);
        await s.ExecuteAsync(new EditAssociatedSectionCommand(id,Vector3d.UnitZ,3,SectionOutput.Faces));
        Assert.Equal(100,Area(s.Snapshot.Features[id].Result,assets),6);
        Assert.Equal(3,s.Snapshot.AssociatedSections[id].OffsetMm);
    }
    [Fact] public async Task FilledSectionPreservesHoleArea()
    {
        var assets=new MemoryAssetStore();var k=new OcctGeometryKernel();
        using var box=await k.EvaluateAsync(new BoxRecipe(20,20,20,RigidTransform3d.Identity),assets);
        using var hole=await k.EvaluateAsync(new CylinderRecipe(3,20,RigidTransform3d.Translate(10,10,0)),assets);
        using var cut=await k.EvaluateAsync(new BooleanRecipe(BooleanOperation.Cut,[box.Geometry,hole.Geometry]),assets);
        var input=new GeometryInstance(new(DocumentId.New(),[ComponentSlotId.New()]),BodyId.New(),cut.Geometry,RigidTransform3d.Identity);
        using var result=await k.SectionFacesAsync([input],new(Vector3d.UnitZ,10),assets);
        Assert.Equal(400-Math.PI*9,Area(result.Geometry,assets),5);
    }
    [Fact] public async Task PreparedPreviewCanCommitExactReviewedAssetsAndRejectDirectSectionGeometryEdits()
    {
        var assets=new MemoryAssetStore();var k=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(DocumentSnapshot.Create("Preview"),assets,k,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Box"));
        var source=session.Snapshot.Features.Values.Single();
        await session.ExecuteAsync(new CreateSectionCommand(Inputs(session.Snapshot),new(Vector3d.UnitZ,5),"Section",true));
        var id=Assert.Single(session.Snapshot.AssociatedSections).Key;
        using var preview=await UpdateAssociatedSections.PrepareCommandAsync(new RecomputeCommand(source.Id,new BoxRecipe(20,10,10,RigidTransform3d.Identity)),
            new(session.Snapshot,session.Generation,assets,k),CancellationToken.None);
        preview.Snapshot.Validate();var result=preview.Snapshot.Features[id].Result;
        await session.ExecuteAsync(new EditDocumentCommand("Commit reviewed preview",_=>preview.Snapshot));
        Assert.Equal(result,session.Snapshot.Features[id].Result);
        var before=session.Snapshot;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new RecomputeCommand(id,new ImportedRecipe(source.Result))));
        Assert.Same(before,session.Snapshot);
    }
    [Fact] public async Task CurrentDocumentRejectsMissingReviewSectionAndOldDocumentMigratesExplicitly()
    {
        using var files=new TestFiles();var path=files.PathFor("missing.cadoryx");var assets=new MemoryAssetStore();
        await new CadDocumentStorage().SaveAsync(DocumentSnapshot.Create("Empty"),assets,path);
        var manifest=FormatEvolutionTests.Manifest(path);var entry=manifest.Sections.Single(s=>s.Kind=="engineering-review");
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            zip.GetEntry(entry.Path)!.Delete();zip.GetEntry("manifest.json")!.Delete();
            using var output=zip.CreateEntry("manifest.json").Open();System.Text.Json.JsonSerializer.Serialize(output,
                manifest with{Sections=[..manifest.Sections.Where(s=>s.Kind!="engineering-review")]},CadJson.Options);
        }
        await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            var drawings=manifest.Sections.Single(s=>s.Kind=="drawings");zip.GetEntry(drawings.Path)!.Delete();
            zip.GetEntry("manifest.json")!.Delete();using var output=zip.CreateEntry("manifest.json").Open();
            System.Text.Json.JsonSerializer.Serialize(output,manifest with{Sections=[..manifest.Sections.Where(s=>s.Kind is not ("engineering-review" or "drawings"))
                .Select(s=>s.Kind=="document"?s with{SchemaVersion=16}:s)]},CadJson.Options);
        }
        using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);Assert.Empty(loaded.Snapshot.Dimensions);Assert.Empty(loaded.Snapshot.AssociatedSections);
    }
    [Fact] public async Task SparseAssemblyBroadPhaseReportsPruningWithoutInventedDistances()
    {
        var assets=new MemoryAssetStore();var k=new OcctGeometryKernel();using var box=await k.EvaluateAsync(new BoxRecipe(10,10,10,RigidTransform3d.Identity),assets);
        var doc=DocumentId.New();var inputs=Enumerable.Range(0,64).Select(i=>new GeometryInstance(new(doc,[ComponentSlotId.New()]),BodyId.New(),box.Geometry,RigidTransform3d.Translate(i*30,0,0))).ToArray();
        var report=await k.CheckInterferenceCandidatesAsync(inputs,1e-7,assets);Assert.Empty(report.Pairs);Assert.Equal(2016,report.BroadPhaseSeparatedPairs);
        inputs[1]=inputs[1] with{WorldTransform=RigidTransform3d.Translate(5,0,0)};
        report=await k.CheckInterferenceCandidatesAsync(inputs,1e-7,assets);Assert.Equal(500,Assert.Single(report.Pairs).OverlapVolumeMm3,6);Assert.Equal(2015,report.BroadPhaseSeparatedPairs);
    }
    [Fact] public async Task DimensionsAndBookmarksRoundTripAndUndoWithoutAssets()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var s=new CadDocumentSession(DocumentSnapshot.Create("Review"),assets,kernel,new InlineSessionDispatcher());
        var d=new EngineeringDimension(Guid.NewGuid(),"Length",EngineeringDimensionKind.Length,Vector3d.Zero,new(10,0,0),Vector3d.Zero,Vector3d.UnitZ);
        await s.ExecuteAsync(new EditDimensionCommand(d));Assert.Equal(d,Assert.Single(CadScene.FromDocument(s.Snapshot).Dimensions));
        var b=new ReviewBookmark(Guid.NewGuid(),"View",new(new(10,10,10),Vector3d.Zero,Vector3d.UnitZ,1,30,45,.1,1000,false,true),null,false,true,2,5,false,2,[],null);
        await s.ExecuteAsync(new EditDocumentCommand("Save view",doc=>doc with{ReviewBookmarks=doc.ReviewBookmarks.Add(b.Id,b)}));
        var storage=new CadDocumentStorage();await s.SaveAsync(storage,files.PathFor("review.cadoryx"));
        using var loaded=await storage.LoadAsync(files.PathFor("review.cadoryx"),assets);
        Assert.Equal(d,loaded.Snapshot.Dimensions[d.Id]);Assert.Equal(b.Primary,loaded.Snapshot.ReviewBookmarks[b.Id].Primary);
        await s.ExecuteAsync(new EditDimensionCommand(d,true));Assert.Empty(s.Snapshot.Dimensions);await s.UndoAsync();Assert.Equal(d,s.Snapshot.Dimensions[d.Id]);Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task DeferredZipSurvivesOriginalDeletionAndMaterializesOnlyOnAccess()
    {
        using var files=new TestFiles();var k=new OcctGeometryKernel();var memory=new MemoryAssetStore();
        await using var s=new CadDocumentSession(DocumentSnapshot.Create("Lazy"),memory,k,new InlineSessionDispatcher());
        await s.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
        await s.ExecuteAsync(new AddBodyCommand(new BoxRecipe(11,20,30,RigidTransform3d.Identity),"Box2"));
        var path=files.PathFor("lazy.cadoryx");await new CadDocumentStorage().SaveAsync(s.Snapshot,memory,path);
        using var disk=new DiskAssetStore(files.PathFor("cache"));
        using(var loaded=await new CadDocumentStorage(deferGeometryAssets:true).LoadAsync(path,disk))
        {
            Assert.Equal(2,disk.DeferredCount);Assert.Equal(0,disk.ReadCount);File.Delete(path);
            using(var shape=OcctGeometryBridge.ReadShape(loaded.Snapshot.Bodies.Values.First().Geometry,disk))Assert.NotNull(shape);
            Assert.Equal(1,disk.DeferredCount);
            await new CadDocumentStorage().SaveAsync(loaded.Snapshot,disk,path);Assert.Equal(0,disk.DeferredCount);
        }
        Assert.Equal(0,disk.Count);
    }
    [Fact] public async Task DeferredCorruptionIsRejectedBeforeNativeUseAndStrictLoadRejectsImmediately()
    {
        using var files=new TestFiles();var k=new OcctGeometryKernel();var memory=new MemoryAssetStore();
        await using var s=new CadDocumentSession(DocumentSnapshot.Create("Bad"),memory,k,new InlineSessionDispatcher());
        await s.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Box"));
        var path=files.PathFor("bad.cadoryx");await new CadDocumentStorage().SaveAsync(s.Snapshot,memory,path);
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            var e=zip.Entries.Single(x=>x.FullName.StartsWith("assets/"));string name=e.FullName;int size=(int)e.Length;e.Delete();
            using var output=zip.CreateEntry(name).Open();output.Write(new byte[size]);
        }
        using var disk=new DiskAssetStore(files.PathFor("cache"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,disk));
        using(var loaded=await new CadDocumentStorage(deferGeometryAssets:true).LoadAsync(path,disk))
        {
            using var lease=disk.Acquire(loaded.Snapshot.Bodies.Values.Single().Geometry.AssetId);
            Assert.Throws<InvalidDataException>(()=>lease.Content);Assert.Equal(1,disk.DeferredCount);
        }
        Assert.Equal(0,disk.Count);
    }
    [Fact] public void InvalidDimensionsRejectNonfiniteOrDegenerateGeometry()
    {
        var d=new EngineeringDimension(Guid.NewGuid(),"D",EngineeringDimensionKind.Length,Vector3d.Zero,new(10,0,0),new(10,10,0),Vector3d.UnitZ);
        d.Validate();Assert.Throws<CadValidationException>(()=>(d with{Second=Vector3d.Zero}).Validate());
        Assert.Throws<CadValidationException>(()=>(d with{FlyoutMm=double.NaN}).Validate());
        Assert.Throws<CadValidationException>(()=>(d with{Kind=EngineeringDimensionKind.Angle,Third=new(20,0,0)}).Validate());
    }
    private static double Area(GeometryAssetRef reference,IAssetStore assets)
    {
        using var shape=OcctGeometryBridge.ReadShape(reference,assets);var faces=shape.GetSubShapes(OcctSharp.ShapeKind.Face);
        try{return faces.Sum(f=>f.InspectProperties(OcctSharp.InspectionPropertyKind.Area).Mass);}finally{foreach(var f in faces)f.Dispose();}
    }
}
