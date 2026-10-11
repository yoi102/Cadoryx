using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchRegionAuthorStorageTests
{
    private static readonly OcctGeometryKernel Kernel=new();
    private static (DocumentSnapshot Document,CadSketch Sketch) Rectangle(SketchRegionAuthorJoin join=SketchRegionAuthorJoin.Round,double distance=.25)
    {
        var (doc,part)=SketchCommandTests.Document();var d=new SketchDraft(CadSketch.Create(part,"Region author",RigidTransform3d.Identity));d.AddRectangle(new(0,0),new(8,4));
        var created=SketchRegionOffsetAuthoring.Create(d.Value,[d.Value.Lines[0].Id],distance,join,Kernel);
        return(doc with{Sketches=doc.Sketches.Add(created.Sketch.Id,created.Sketch)},created.Sketch);
    }
    [Theory][InlineData(SketchRegionAuthorJoin.Miter,.25)][InlineData(SketchRegionAuthorJoin.Round,.25)][InlineData(SketchRegionAuthorJoin.Bevel,.25)]
    [InlineData(SketchRegionAuthorJoin.Miter,-.25)][InlineData(SketchRegionAuthorJoin.Round,-.25)][InlineData(SketchRegionAuthorJoin.Bevel,-.25)]
    public void CreationOwnsTheCompleteGroupAndSharedGeneratedEndpoints(SketchRegionAuthorJoin join,double distance)
    {
        var(_,s)=Rectangle(join,distance);var g=Assert.Single(s.RegionOffsets);Assert.False(g.IsFrozen);Assert.Equal(join,g.Join);Assert.Equal(distance,g.Distance);
        var loop=Assert.Single(g.Loops);Assert.Equal(distance>0&&join!=SketchRegionAuthorJoin.Miter?8:4,loop.Outputs.Length);
        Assert.Equal(loop.Outputs.Select(o=>o.Curve),s.ExpressionCurves.Select(c=>c.Id));Assert.All(s.ExpressionCurves,c=>Assert.Null(c.Source));
        for(int i=0;i<loop.Outputs.Length;i++)Assert.Equal(loop.Outputs[i].Endpoints.End,loop.Outputs[(i+1)%loop.Outputs.Length].Endpoints.Start);
        Assert.All(loop.Outputs.SelectMany(o=>new[]{o.Endpoints.Start,o.Endpoints.End}),id=>Assert.Equal(SketchPointOrigin.CurveGenerated,s.Points.Single(p=>p.Id==id).Origin));
        SketchAssociativeEndpoints.RequireSolved(s);Assert.Single(SketchRegionAuthorModel.Order(s));Kernel.ConfirmExpressionRegions(s);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void JsonAndMessagePackPreserveTheWholeRecipeAfterExpressionsAttach(bool json)
    {
        var(_,s)=Rectangle();var registry=ImmutableDictionary<SketchId,CadSketch>.Empty.Add(s.Id,s with{RegionOffsets=[]});
        var bytes=json?SketchRegionSectionStorage.EncodeJson([s]):SketchRegionSectionStorage.EncodeMessagePack([s]);
        var loaded=(json?SketchRegionSectionStorage.AttachJson(bytes,registry):SketchRegionSectionStorage.AttachMessagePack(bytes,registry))[s.Id];
        Assert.Equal(SketchRegionSectionStorage.EncodeJson([s]),SketchRegionSectionStorage.EncodeJson([loaded]));Kernel.ConfirmExpressionRegions(loaded);
        Assert.Throws<InvalidDataException>(()=>json?SketchRegionSectionStorage.AttachJson(bytes,ImmutableDictionary<SketchId,CadSketch>.Empty):
            SketchRegionSectionStorage.AttachMessagePack(bytes,ImmutableDictionary<SketchId,CadSketch>.Empty));
    }
    [Fact] public async Task MainFileReplaysBeforeSaveAndOpenAndRequiresAnExplicitBackend()
    {
        using var files=new TestFiles();var(doc,s)=Rectangle();var assets=new MemoryAssetStore();var path=files.PathFor("region.cadoryx");var storage=new CadDocumentStorage(regionVerifier:Kernel);
        await storage.SaveAsync(doc,assets,path);var manifest=FormatEvolutionTests.Manifest(path);
        Assert.Equal("0.7.8",manifest.ApplicationVersion);Assert.Equal(60,manifest.Sections.Single(e=>e.Kind=="document").SchemaVersion);Assert.Equal(18,manifest.Sections.Length);
        Assert.Contains("cadoryx.associative-expression-regions.1",manifest.RequiredCapabilities);
        using(var loaded=await storage.LoadAsync(path,assets))Assert.Equal(SketchRegionSectionStorage.EncodeJson([s]),SketchRegionSectionStorage.EncodeJson(loaded.Snapshot.Sketches.Values));
        await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().LoadAsync(path,assets));Assert.Equal(0,assets.Count);
        var before=File.ReadAllBytes(path);await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().SaveAsync(doc,assets,path));Assert.Equal(before,File.ReadAllBytes(path));
        using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>storage.SaveAsync(doc,assets,path,cancel.Token));Assert.Equal(before,File.ReadAllBytes(path));
    }
    [Fact] public async Task Document57AddsOnlyAnEmptyGroupTableAndDoesNotInventARecipeFromOffsetTrees()
    {
        using var files=new TestFiles();var(doc,s)=Rectangle();var assets=new MemoryAssetStore();var path=files.PathFor("v57.cadoryx");var storage=new CadDocumentStorage();
        s=s with{RegionOffsets=[]};doc=doc with{Sketches=doc.Sketches.SetItem(s.Id,s)};await storage.SaveAsync(doc,assets,path);
        var geometry=SketchExpressionSectionStorage.EncodeMessagePack([s]);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(e=>e.Kind=="document"?e with{SchemaVersion=57}:e).ToImmutableArray()});
        using var loaded=await storage.LoadAsync(path,assets);Assert.Empty(loaded.Snapshot.Sketches[s.Id].RegionOffsets);
        Assert.Equal(geometry,SketchExpressionSectionStorage.EncodeMessagePack(loaded.Snapshot.Sketches.Values));Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");
    }
    [Fact] public async Task NewGroupPayloadCannotMasqueradeAsAnOldDocumentAndCurrentCannotOmitTheRequiredTable()
    {
        using var files=new TestFiles();var(doc,_)=Rectangle();var assets=new MemoryAssetStore();var path=files.PathFor("new.cadoryx");var storage=new CadDocumentStorage(regionVerifier:Kernel);
        await storage.SaveAsync(doc,assets,path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(e=>e.Kind=="document"?e with{SchemaVersion=57}:e).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(0,assets.Count);
        await storage.SaveAsync(doc,assets,path);using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))zip.GetEntry("sections/sketch-regions.msgpack")!.Delete();
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Where(e=>e.Kind!="sketch-regions").ToImmutableArray()});
        await Assert.ThrowsAnyAsync<Exception>(()=>storage.LoadAsync(path,assets));Assert.Equal(0,assets.Count);
    }
    [Fact] public void AWholeGroupFrozenSnapshotCanRetainMissingSourceAuthorsWithoutInventingDependencies()
    {
        var(_,s)=Rectangle();var g=s.RegionOffsets.Single() with{IsFrozen=true,FrozenReason="Explicit source removal"};
        var frozen=s with{RegionOffsets=[g],Lines=[],Constraints=[]};frozen.Validate();Assert.Single(SketchRegionAuthorModel.Order(frozen));Kernel.ConfirmExpressionRegions(frozen);
        Assert.Throws<CadValidationException>(()=>(frozen with{RegionOffsets=[g with{IsFrozen=false,FrozenReason=null}]}).Validate());
    }
    [Theory][InlineData("algorithm")][InlineData("duplicate-output")][InlineData("point-origin")][InlineData("distance")][InlineData("traversal")][InlineData("reason")]
    public void ForgedLocalGroupAuthorValuesRejectBeforeNativeReplay(string fault)
    {
        var(_,s)=Rectangle();var g=s.RegionOffsets.Single();var loop=g.Loops[0];var o=loop.Outputs[0];
        if(fault=="algorithm")g=g with{Algorithm="unknown"};if(fault=="reason")g=g with{IsFrozen=true};if(fault=="distance")g=g with{Distance=.5};
        if(fault=="duplicate-output")g=g with{Loops=[loop with{Outputs=loop.Outputs.SetItem(1,o)}]};
        if(fault=="traversal")g=g with{Loops=[loop with{Outputs=loop.Outputs.SetItem(0,o with{SourceTraversalReversed=!o.SourceTraversalReversed})}]};
        if(fault=="point-origin")s=s with{Points=s.Points.Select(p=>p.Id==o.Endpoints.Start?p with{Origin=SketchPointOrigin.Unknown}:p).ToImmutableArray()};
        s=s with{RegionOffsets=[g]};Assert.Throws<CadValidationException>(s.Validate);
    }
    [Fact] public void APrimitiveUsingItsOwnGroupOutputPointIsAnExplicitSourceCycle()
    {
        var(doc,part)=SketchCommandTests.Document();var d=new SketchDraft(CadSketch.Create(part,"Circle group",RigidTransform3d.Identity));d.AddCircle(new(0,0),3);
        var s=SketchRegionOffsetAuthoring.Create(d.Value,[d.Value.Circles[0].Id],.25,SketchRegionAuthorJoin.Miter,Kernel).Sketch;
        var g=s.RegionOffsets[0];var o=g.Loops[0].Outputs[0];var circle=s.Circles[0] with{Center=o.Endpoints.Start};s=s with{Circles=[circle]};
        var author=SketchExpressionEntities.Author(s,circle.Id);var geometry=author.Geometry.Offset(.25);
        var replacement=o with{Geometry=geometry};var curve=s.ExpressionCurves[0] with{Geometry=geometry};
        s=s with{ExpressionCurves=[curve],RegionOffsets=[g with{Sources=[[g.Sources[0][0] with{Geometry=author.Geometry}]],Loops=[g.Loops[0] with{Outputs=[replacement]}]}]};
        Assert.Throws<CadValidationException>(s.Validate);
    }
    [Fact] public async Task ForgedBridgeWithOtherwiseMatchingMetadataFailsExactReplayAndPreservesTheOldFile()
    {
        using var files=new TestFiles();var(doc,s)=Rectangle();var g=s.RegionOffsets[0];var loop=g.Loops[0];int index=Enumerable.Range(0,loop.Outputs.Length).First(i=>loop.Outputs[i].Role==SketchRegionAuthorRole.RoundBridge);
        var output=loop.Outputs[index];var primitive=output.Geometry.Primitive!;var originalEllipse=primitive.Ellipse!.Value;var ellipse=originalEllipse with{Center=new(originalEllipse.Center.X+.001,originalEllipse.Center.Y)};
        var geometry=SketchCurveExpressionGeometry.FromBoundary(new(ellipse.At(0),ellipse.At(1)){Ellipse=ellipse});
        var changed=output with{Geometry=geometry};var curve=s.ExpressionCurves.Single(c=>c.Id==output.Curve);
        var forged=s with{ExpressionCurves=s.ExpressionCurves.Replace(curve,curve with{Geometry=geometry}),RegionOffsets=[g with{Loops=[loop with{Outputs=loop.Outputs.SetItem(index,changed)}]}]};
        forged.Validate();Assert.Throws<CadValidationException>(()=>Kernel.ConfirmExpressionRegions(forged));
        var assets=new MemoryAssetStore();var storage=new CadDocumentStorage(regionVerifier:Kernel);var path=files.PathFor("atomic.cadoryx");await storage.SaveAsync(doc,assets,path);var before=File.ReadAllBytes(path);
        await Assert.ThrowsAsync<CadValidationException>(()=>storage.SaveAsync(doc with{Sketches=doc.Sketches.SetItem(s.Id,forged)},assets,path));Assert.Equal(before,File.ReadAllBytes(path));Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("missing-field")][InlineData("wrong-version")][InlineData("null-source")][InlineData("source-slot")][InlineData("algorithm")]
    public void MalformedJsonGroupPayloadsRejectBeforeAttachingAnyTable(string fault)
    {
        var(_,s)=Rectangle();var original=s with{RegionOffsets=[]};var registry=ImmutableDictionary<SketchId,CadSketch>.Empty.Add(s.Id,original);
        var root=JsonNode.Parse(SketchRegionSectionStorage.EncodeJson([s]))!;var group=root["sketches"]![0]!["groups"]![0]!;
        if(fault=="missing-field")group.AsObject().Remove("algorithm");if(fault=="wrong-version")root["version"]=99;
        if(fault=="null-source")group["sources"]![0]![0]=null;
        if(fault=="source-slot")group["loops"]![0]!["outputs"]![0]!["sources"]![0]=999;
        if(fault=="algorithm")group["algorithm"]="unknown";
        var bytes=System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());Assert.Throws<InvalidDataException>(()=>SketchRegionSectionStorage.AttachJson(bytes,registry));
        Assert.Same(original,registry[s.Id]);Assert.Empty(registry[s.Id].RegionOffsets);
    }
    [Fact] public void TrailingMessagePackAndOversizedHeaderRejectBeforeAttachingAnyTable()
    {
        var(_,s)=Rectangle();var registry=ImmutableDictionary<SketchId,CadSketch>.Empty.Add(s.Id,s with{RegionOffsets=[]});
        var bytes=SketchRegionSectionStorage.EncodeMessagePack([s]).Concat(new byte[]{0}).ToArray();
        Assert.Throws<InvalidDataException>(()=>SketchRegionSectionStorage.AttachMessagePack(bytes,registry));
        var buffer=new System.Buffers.ArrayBufferWriter<byte>();var writer=new MessagePack.MessagePackWriter(buffer);
        writer.WriteArrayHeader(2);writer.Write(SketchRegionSectionStorage.SchemaVersion);writer.WriteArrayHeader(10001);writer.Flush();
        Assert.Throws<InvalidDataException>(()=>SketchRegionSectionStorage.AttachMessagePack(buffer.WrittenMemory,registry));Assert.Empty(registry[s.Id].RegionOffsets);
    }
    [Fact] public void SolverCannotDropTheWholeGroupWhileReturningTheSameExpressionTable()
    {
        var(_,s)=Rectangle();var forged=s with{RegionOffsets=[]};forged.Validate();Assert.True(SketchExpressionEntities.SameTable(s,forged));
        Assert.Throws<CadValidationException>(()=>SketchExpressionEntities.ValidateSolved(s,forged));
    }
}
