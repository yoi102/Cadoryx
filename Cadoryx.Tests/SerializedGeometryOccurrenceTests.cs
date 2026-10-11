using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using MessagePack;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

[Collection("IsolatedNativeDeadlines")]
public sealed class SerializedGeometryOccurrenceTests
{
    public static IEnumerable<object[]> CurvedCases()=>SerializedGeometryReferenceTests.CurvedCases();
    [Theory,MemberData(nameof(CurvedCases))]
    public async Task ActualGuidedAuthoringSaveCloseFreshAndDeferredReopenRetainsCompleteCurrentPathsOnly(string kind,bool reverse,bool tilted)
    {
        using var files=new TestFiles();string path=files.PathFor("occurrences.cadoryx");
        var scene=await CurvedConsumerTestScene.Create(kind,reverse,tilted,false);GeometryAssetRef geometry;BodyId body;FeatureId feature;
        try
        {
            using var result=await scene.Kernel.EvaluateGuidedForDocumentWithOccurrencesAsync(scene.Recipe,scene.Assets);
            geometry=result.Geometry;Assert.NotNull(geometry.SerializedOccurrences);Assert.Null(geometry.SerializedReferences);
            CurvedConsumerTestScene.Verify(scene.Assets,geometry,scene.OriginalVolume);
            var snapshot=Add(scene.Session.Snapshot,scene.Part,result.Geometry,scene.Recipe,result.GuidedModelingReport);
            body=Assert.Single(snapshot.Bodies.Keys);feature=Assert.Single(snapshot.Features.Keys);
            using(var graph=OcctGeometryBridge.ReadOccurrenceShape(geometry,scene.Assets))VerifyGraph(graph);
            await new CadDocumentStorage().SaveAsync(snapshot,scene.Assets,path);
            var manifest=FormatEvolutionTests.Manifest(path);
            Assert.Equal("0.7.9",manifest.ApplicationVersion);
            Assert.Equal(2,manifest.Sections.Single(s=>s.Kind=="geometry").SchemaVersion);
            var section=Assert.Single(manifest.Sections,s=>s.Kind==OccurrenceReferenceSectionStorage.Kind);
            Assert.Equal(1,section.SchemaVersion);Assert.True(section.Required);Assert.Equal("messagepack",section.Encoding);
            Assert.Contains(OccurrenceReferenceSectionStorage.Capability,manifest.RequiredCapabilities);
            Assert.DoesNotContain("cadoryx.native-reference-tables.1",manifest.RequiredCapabilities);
            Assert.Contains(manifest.Assets,a=>a.Id==geometry.SerializedOccurrences.TableAssetId&&a.Format==AssetFormatPolicy.CurrentOccurrenceTable);
        }
        finally{await scene.DisposeAsync();}
        Assert.Equal(0,scene.Assets.Count);
        foreach(bool deferred in new[]{false,true})
        {
            using var assets=new DiskAssetStore(files.PathFor("fresh-cache"));
            using(var loaded=await new CadDocumentStorage(deferGeometryAssets:deferred).LoadAsync(path,assets))
            {
                Assert.Equal(geometry,loaded.Snapshot.Bodies[body].Geometry);
                Assert.Same(loaded.Snapshot.Bodies[body].Geometry,loaded.Snapshot.Features[feature].Result);
                Assert.Contains(geometry.SerializedOccurrences!.TableAssetId,loaded.Snapshot.ReferencedAssets());
                using var graph=OcctGeometryBridge.ReadOccurrenceShape(geometry,assets);VerifyGraph(graph);
                using var second=OcctGeometryBridge.ReadOccurrenceShape(geometry,assets);
                Assert.All(graph.Members,member=>Assert.Empty(second.FindSameIdentityOccurrences(member)));
                using var selected=OcctGeometryBridge.ResolveCurrentOccurrence(loaded.Snapshot,loaded.Snapshot.Id,body,geometry,[],assets);
                Assert.True(selected.IsValid);Assert.InRange(Math.Abs(Math.Abs(selected.InspectProperties(InspectionPropertyKind.Volume).Mass)-geometry.VolumeMm3),0,1e-7);
                Assert.Null(loaded.Snapshot.Features[feature].TopologyHistory);
            }
            Assert.Equal(0,assets.Count);
        }
    }
    [Fact]
    public void ActualCaptureCallbackProvesOnlyLiveSourceAndOwnedGraphSurvivesAllOtherOwners()
    {
        var assets=new MemoryAssetStore();BrepOccurrenceRestoreResult graph;Shape ordinary;
        using(var root=ShapeFactory.CreateBox(10,20,30))
        using(var stored=OcctGeometryBridge.StoreShapeWithOccurrences(root,assets,captureEvidence:report=>
            {Assert.True(report.NativeSourceCaptureProven);Assert.NotNull(report.SourceDomainId);Assert.True(report.ProvesCompleteDecodedOccurrencePaths);Assert.False(report.ProvesComposedReferenceIdentity);}))
        {
            Assert.Equal(2,assets.Count);graph=OcctGeometryBridge.ReadOccurrenceShape(stored.Geometry,assets);
            ordinary=OcctGeometryBridge.ReadShape(stored.Geometry,assets);
        }
        Assert.Equal(0,assets.Count);
        using(graph){VerifyGraph(graph);Assert.Equal(6000,graph.Root.InspectProperties(InspectionPropertyKind.Volume).Mass,7);}
        using(ordinary)
        {
            Assert.True(ordinary.IsValid);Assert.Equal(6000,ordinary.InspectProperties(InspectionPropertyKind.Volume).Mass,7);
            using var files=new TestFiles();string path=files.PathFor("transferred.brep");ShapeExchange.WriteBrep(ordinary,path);
            using var fresh=ShapeExchange.ReadBrep(path);Assert.Equal(6000,fresh.InspectProperties(InspectionPropertyKind.Volume).Mass,7);
        }
    }
    [Fact]
    public void SharedNativeSolidThroughTwoParentsRejectsShapeOnlyResolutionButKeepsExplicitPaths()
    {
        var assets=new MemoryAssetStore();using var box=ShapeFactory.CreateBox(1,2,3);
        using var first=ShapeFactory.CreateCompound([box]);using var second=ShapeFactory.CreateCompound([box]);
        using var compound=ShapeFactory.CreateCompound([first,second]);
        using(var stored=OcctGeometryBridge.StoreShapeWithOccurrences(compound,assets))
        using(var graph=OcctGeometryBridge.ReadOccurrenceShape(stored.Geometry,assets))
        {
            VerifyGraph(graph);Assert.Equal(175,graph.Report.Rows.Count);
            var solids=graph.Report.Rows.Where(r=>r.Kind==ShapeKind.Solid).ToArray();Assert.Equal(2,solids.Length);
            using var a=graph.Resolve(solids[0].Path);using var b=graph.Resolve(solids[1].Path);
            Assert.Equal(2,graph.FindSameIdentityOccurrences(a).Count);Assert.Throws<InvalidOperationException>(()=>graph.ResolveUnique(a));
            Assert.Equal(0,RepairSnapshot.FindTopologyIndex(a,b));
            using var foreign=ShapeFactory.CreateBox(1,2,3);Assert.Empty(graph.FindSameIdentityOccurrences(foreign));
            Assert.Throws<InvalidOperationException>(()=>graph.ResolveUnique(foreign));
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("throw")][InlineData("cancel")][InlineData("callback")][InlineData("callback-cancel")]
    public void TableStageAndCallbackFailuresOrCancellationReleaseEveryStagedAsset(string fault)
    {
        using var root=ShapeFactory.CreateBox(10,20,30);using var cancellation=new CancellationTokenSource();
        var assets=new TableStore(fault,cancellation);
        Action<BrepOccurrenceReport>? callback=fault=="callback"?_=>throw new IOException("Callback failure"):fault=="callback-cancel"?_=>cancellation.Cancel():null;
        if(fault.Contains("cancel",StringComparison.Ordinal))Assert.Throws<OperationCanceledException>(()=>OcctGeometryBridge.StoreShapeWithOccurrences(root,assets,cancellation.Token,callback));
        else Assert.Throws<IOException>(()=>OcctGeometryBridge.StoreShapeWithOccurrences(root,assets,cancellation.Token,callback));
        Assert.Equal(0,assets.Inner.Count);
    }
    [Theory][InlineData("missing")][InlineData("schema")][InlineData("capability")][InlineData("missing-section")]
    [InlineData("optional")][InlineData("legacy-geometry")][InlineData("legacy-document")][InlineData("role")]
    [InlineData("payload")][InlineData("foreign-revision")][InlineData("duplicate")][InlineData("wrong-boundary")]
    public async Task ArchiveFaultsRejectWithoutPublishingOrLeakingPartialDocument(string fault)
    {
        using var files=new TestFiles();string path=files.PathFor("bad.cadoryx");var held=new MemoryAssetStore();
        using(var root=ShapeFactory.CreateBox(10,20,30))using(var stored=OcctGeometryBridge.StoreShapeWithOccurrences(root,held))
            await new CadDocumentStorage().SaveAsync(Document(stored.Geometry),held,path);
        Assert.Equal(0,held.Count);
        if(fault is "missing" or "schema" or "payload" or "foreign-revision" or "duplicate" or "wrong-boundary")
            FormatEvolutionTests.RewriteSection(path,OccurrenceReferenceSectionStorage.Kind,bytes=>
            {
                var p=MessagePackSerializer.Deserialize<PackOccurrenceReferences>(bytes);var row=p.Entries[0];
                row=fault switch
                {
                    "missing"=>row with{TableHash=new string('0',64)},"schema"=>row with{SchemaVersion=99},
                    "payload"=>row with{TableHash=row.BoundaryHash},"foreign-revision"=>row with{Revision=Guid.NewGuid()},
                    "wrong-boundary"=>row with{BoundaryHash=new string('0',64)},_=>row
                };
                return MessagePackSerializer.Serialize(p with{Entries=fault=="duplicate"?[row,row]:[row]});
            });
        else FormatEvolutionTests.RewriteManifest(path,m=>fault switch
        {
            "capability"=>m with{RequiredCapabilities=m.RequiredCapabilities.Remove(OccurrenceReferenceSectionStorage.Capability)},
            "missing-section"=>m with{Sections=m.Sections.Remove(m.Sections.Single(s=>s.Kind==OccurrenceReferenceSectionStorage.Kind))},
            "optional"=>m with{Sections=m.Sections.Select(s=>s.Kind==OccurrenceReferenceSectionStorage.Kind?s with{Required=false}:s).ToImmutableArray()},
            "legacy-geometry"=>m with{Sections=m.Sections.Select(s=>s.Kind=="geometry"?s with{SchemaVersion=1}:s).ToImmutableArray()},
            "legacy-document"=>m with{Sections=m.Sections.Select(s=>s.Kind=="document"?s with{SchemaVersion=59}:s).ToImmutableArray()},
            _=>m with{Assets=m.Assets.Select(a=>a.Format?.MediaType==AssetFormat.OccurrenceTableMediaType?a with{Format=AssetFormat.Opaque}:a).ToImmutableArray()}
        });
        var assets=new MemoryAssetStore();
        if(fault=="role")await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
        else await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(DrawingViewKind.Front)][InlineData(DrawingViewKind.Section)]
    public async Task ActualDrawingWorkerTransfersOccurrenceTableAndMatchesDirectProjection(DrawingViewKind kind)
    {
        var assets=new MemoryAssetStore();using var root=ShapeFactory.CreateBox(10,20,30);
        using(var stored=OcctGeometryBridge.StoreShapeWithOccurrences(root,assets))
        {
            GeometryInstance[] instances=[new(new(DocumentId.New(),[]),BodyId.New(),stored.Geometry,RigidTransform3d.Translate(11,-7,3))];
            Vector3d? normal=kind==DrawingViewKind.Section?Vector3d.UnitZ:null;
            var expected=await new OcctGeometryKernel().ProjectAsync(instances,kind,normal,10,assets);
            var worker=new IsolatedDrawingExecutor(Path.Combine(AppContext.BaseDirectory,"Cadoryx.Cli.exe"));
            var actual=await worker.ProjectAsync(instances,kind,normal,10,assets);
            Assert.NotEmpty(actual.Strokes);Assert.Equal(JsonSerializer.Serialize(expected,CadJson.Options),JsonSerializer.Serialize(actual,CadJson.Options));Assert.Equal(2,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact]
    public async Task SeparatePartitionReattachesSharedBodyResultAndRetainedImportedRecipeSource()
    {
        var assets=new MemoryAssetStore();using var files=new TestFiles();string path=files.PathFor("source.cadoryx");
        using var root=ShapeFactory.CreateBox(10,20,30);
        using(var stored=OcctGeometryBridge.StoreShapeWithOccurrences(root,assets))
        {
            var snapshot=Document(stored.Geometry);await new CadDocumentStorage().SaveAsync(snapshot,assets,path);
            using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);
            var body=Assert.Single(loaded.Snapshot.Bodies.Values);var feature=Assert.Single(loaded.Snapshot.Features.Values);
            Assert.Same(body.Geometry,feature.Result);Assert.Same(body.Geometry,Assert.IsType<ImportedRecipe>(feature.Recipe).Source);
            Assert.Equal(stored.Geometry,body.Geometry);Assert.Empty(loaded.Snapshot.Extensions);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact]
    public void CurrentSelectionRejectsForeignStaleAndInvalidPathWithoutUsingCoordinatesOrIndicesAsIdentity()
    {
        var assets=new MemoryAssetStore();using var root=ShapeFactory.CreateBox(10,20,30);
        using(var stored=OcctGeometryBridge.StoreShapeWithOccurrences(root,assets))
        {
            var snapshot=Document(stored.Geometry);var body=Assert.Single(snapshot.Bodies.Keys);
            Assert.Throws<CadValidationException>(()=>OcctGeometryBridge.ResolveCurrentOccurrence(snapshot,DocumentId.New(),body,stored.Geometry,[],assets));
            Assert.Throws<CadValidationException>(()=>OcctGeometryBridge.ResolveCurrentOccurrence(snapshot,snapshot.Id,body,stored.Geometry with{Revision=GeometryRevisionId.New()},[],assets));
            var producer=Assert.Single(snapshot.Features.Values);
            var stale=snapshot with{Features=snapshot.Features.SetItem(producer.Id,producer with{IsStale=true})};
            Assert.Throws<CadValidationException>(()=>OcctGeometryBridge.ResolveCurrentOccurrence(stale,snapshot.Id,body,stored.Geometry,[],assets));
            Assert.Throws<ArgumentException>(()=>OcctGeometryBridge.ResolveCurrentOccurrence(snapshot,snapshot.Id,body,stored.Geometry,[999],assets));
            Assert.Equal(2,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact]
    public void RehashedDeclaredSourceIndexCannotManufactureOriginalCaptureProof()
    {
        using var root=ShapeFactory.CreateBox(10,20,30);var snapshot=BrepOccurrenceSnapshot.Capture(root);
        byte[] brep=snapshot.CopyBrepBytes(),table=snapshot.CopyOccurrenceTable();
        int row=48+BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(44,4));
        int count=BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(12,4));int source=BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(row,4));
        int changed=(source+1)%count;BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(row,4),changed);Rehash(brep,table);
        SerializedOccurrenceAssetPolicy.VerifyBinding(brep,table);var imported=BrepOccurrenceSnapshot.Open(brep,table);
        Assert.False(imported.Report.NativeSourceCaptureProven);Assert.Null(imported.Report.SourceDomainId);
        Assert.Equal(changed,imported.Report.Rows[0].DeclaredSourceTopologyIndex);
        Assert.Throws<InvalidOperationException>(()=>imported.RestoreFromProvenNativeSource());using var graph=imported.Restore();VerifyGraph(graph);
    }
    [Fact]
    public void RehashedTokenDirectionPassesIntegrityButActualNativeGraphRejectsFalseIdentity()
    {
        var assets=new MemoryAssetStore();using var root=ShapeFactory.CreateBox(10,20,30);
        using(var stored=OcctGeometryBridge.StoreShapeWithOccurrences(root,assets))
        using(var brep=assets.Acquire(stored.Geometry.AssetId))
        using(var original=assets.Acquire(stored.Geometry.SerializedOccurrences!.TableAssetId))
        {
            byte[] table=original.Content.ToArray();Assert.Equal((byte)'+',table[48]);table[48]=(byte)'-';Rehash(brep.Content.ToArray(),table);
            SerializedOccurrenceAssetPolicy.VerifyBinding(brep.Content.Span,table);
            using var modified=assets.Stage(table);
            var declaration=stored.Geometry with{SerializedOccurrences=stored.Geometry.SerializedOccurrences with{TableAssetId=modified.Id}};
            Assert.Throws<ArgumentException>(()=>OcctGeometryBridge.ReadOccurrenceShape(declaration,assets));
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact]
    public void CompleteEvidenceBudgetRejectsPartialCaptureWhileOriginalLargeGeometryStillStores()
    {
        Assert.Equal(512,SerializedOccurrenceAssetPolicy.MaximumOccurrences);Assert.Equal(64,SerializedOccurrenceAssetPolicy.MaximumDepth);
        Assert.Equal(64*1024*1024,SerializedOccurrenceAssetPolicy.MaximumBytes);
        var assets=new MemoryAssetStore();using var box=ShapeFactory.CreateBox(1,2,3);
        var boxes=Enumerable.Range(0,8).Select(_=>ShapeFactory.CreateBox(1,2,3)).ToArray();
        try
        {
            using var compound=ShapeFactory.CreateCompound(boxes);
            Assert.Throws<ArgumentException>(()=>OcctGeometryBridge.StoreShapeWithOccurrences(compound,assets));Assert.Equal(0,assets.Count);
            using(var ordinary=OcctGeometryBridge.StoreShape(compound,assets))
            {Assert.Null(ordinary.Geometry.SerializedOccurrences);Assert.InRange(Math.Abs(ordinary.Geometry.VolumeMm3-48),0,1e-7);}
        }
        finally{foreach(var shape in boxes)shape.Dispose();}
        Assert.Equal(0,assets.Count);
    }
    [Fact]
    public async Task LegacyMigrationAndNewCapabilityRemainAbsentWhenNoCompleteOccurrencesWereCaptured()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();string path=files.PathFor("legacy.cadoryx");
        using(var loaded=await new CadDocumentStorage().LoadAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","Storage","v2-box.cadoryx"),assets))
        {
            Assert.All(loaded.Snapshot.ReferencedGeometry(),g=>Assert.Null(g.SerializedOccurrences));
            await new CadDocumentStorage().SaveAsync(loaded.Snapshot,assets,path);
            var manifest=FormatEvolutionTests.Manifest(path);Assert.DoesNotContain(OccurrenceReferenceSectionStorage.Capability,manifest.RequiredCapabilities);
            Assert.DoesNotContain(manifest.Sections,s=>s.Kind==OccurrenceReferenceSectionStorage.Kind);
            using var fresh=await new CadDocumentStorage().LoadAsync(path,assets);Assert.All(fresh.Snapshot.ReferencedGeometry(),g=>Assert.Null(g.SerializedOccurrences));
        }
        Assert.Equal(0,assets.Count);
    }
    private static void VerifyGraph(BrepOccurrenceRestoreResult graph)
    {
        Assert.False(graph.Report.NativeSourceCaptureProven);Assert.Null(graph.Report.SourceDomainId);
        Assert.True(graph.Report.ProvesCompleteDecodedOccurrencePaths);Assert.False(graph.Report.ProvesComposedReferenceIdentity);
        Assert.NotEmpty(graph.Report.Rows);Assert.Equal(graph.Report.Rows.Count,graph.Members.Count);
        foreach(var row in graph.Report.Rows)
        {
            using var member=graph.Resolve(row.Path);Assert.Contains(row.ReferenceIndex,graph.FindExactIdentityOccurrences(member));
            Assert.Equal(row.SameIdentityPathCount,graph.FindSameIdentityOccurrences(member).Count);
            if(row.IsShapeIdentityAmbiguous)Assert.Throws<InvalidOperationException>(()=>graph.ResolveUnique(member));
        }
    }
    private static DocumentSnapshot Document(GeometryAssetRef geometry)
    {
        var (snapshot,part)=SketchCommandTests.Document();return Add(snapshot,part,geometry,new ImportedRecipe(geometry),null);
    }
    private static DocumentSnapshot Add(DocumentSnapshot snapshot,DefinitionId part,GeometryAssetRef geometry,GeometryRecipe recipe,GuidedModelingReport? report)
    {
        var feature=FeatureId.New();var body=BodyId.New();var definition=(PartDefinition)snapshot.Definitions[part];var layer=snapshot.Layers.Keys.First();
        var created=new FeatureDefinition(feature,part,"Complete current paths",recipe,[],body,geometry){GuidedModelingReport=report};
        var result=snapshot with{Definitions=snapshot.Definitions.SetItem(part,definition with{Bodies=definition.Bodies.Add(body),Features=definition.Features.Add(feature)}),
            Bodies=snapshot.Bodies.Add(body,new(body,part,"Complete current paths",geometry,feature,layer,new())),Features=snapshot.Features.Add(feature,created)};
        result.Validate();return result;
    }
    private static void Rehash(byte[] brep,byte[] table)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);hash.AppendData(brep);hash.AppendData(table.AsSpan(0,table.Length-32));hash.GetHashAndReset().CopyTo(table.AsSpan(table.Length-32));
    }
    private sealed class TableStore(string fault,CancellationTokenSource cancellation):IAssetStore
    {
        public MemoryAssetStore Inner{get;}=new();public IAssetLease Acquire(AssetId id)=>Inner.Acquire(id);
        public IAssetLease Stage(ReadOnlySpan<byte> bytes)
        {
            if(!bytes.StartsWith("OCROCC26"u8))return Inner.Stage(bytes);
            if(fault=="throw")throw new IOException("Table stage failure");
            var lease=Inner.Stage(bytes);if(fault=="cancel")cancellation.Cancel();return lease;
        }
    }
}
