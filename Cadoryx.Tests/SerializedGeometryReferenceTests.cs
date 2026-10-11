using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using MessagePack;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

[Collection("IsolatedNativeDeadlines")]
public sealed class SerializedGeometryReferenceTests
{
    public static IEnumerable<object[]> CurvedCases()=>from kind in CurvedConsumerTestScene.Kinds
        from reverse in new[]{false,true} from tilted in new[]{false,true} select new object[]{kind,reverse,tilted};

    [Theory,MemberData(nameof(CurvedCases))]
    public async Task GuidedProductionSaveCloseAndFreshReopenOwnOneGraphWithoutOriginalCaptureProof(string kind,bool reverse,bool tilted)
    {
        using var files=new TestFiles();string path=files.PathFor("references.cadoryx");
        var scene=await CurvedConsumerTestScene.Create(kind,reverse,tilted,true);
        FeatureId featureId=scene.Feature!.Value;GeometryAssetRef geometry;double expected=scene.OriginalVolume;
        try
        {
            geometry=scene.Session.Snapshot.Features[featureId].Result;
            Assert.NotNull(geometry.SerializedReferences);Assert.Equal(AssetFormatPolicy.CurrentReferenceTable,geometry.SerializedReferences.Format);
            using(var graph=OcctGeometryBridge.ReadReferencedShape(geometry,scene.Assets))VerifyGraph(graph);
            await scene.Session.SaveAsync(new CadDocumentStorage(),path);
            var manifest=FormatEvolutionTests.Manifest(path);
            Assert.Equal(2,manifest.Sections.Single(s=>s.Kind=="geometry").SchemaVersion);
            Assert.Contains("cadoryx.native-reference-tables.1",manifest.RequiredCapabilities);
            Assert.Contains(manifest.Assets,a=>a.Id==geometry.SerializedReferences.TableAssetId&&a.Format==AssetFormatPolicy.CurrentReferenceTable);
        }
        finally{await scene.DisposeAsync();}
        Assert.Equal(0,scene.Assets.Count);
        foreach(bool deferred in new[]{false,true})
        {
            using var assets=new DiskAssetStore(files.PathFor("asset-cache"));
            using(var loaded=await new CadDocumentStorage(deferGeometryAssets:deferred).LoadAsync(path,assets))
            {
                Assert.Equal(geometry,loaded.Snapshot.Features[featureId].Result);
                Assert.Contains(geometry.SerializedReferences!.TableAssetId,loaded.Snapshot.ReferencedAssets());
                CurvedConsumerTestScene.Verify(assets,geometry,expected);
                using var first=OcctGeometryBridge.ReadReferencedShape(geometry,assets);
                using var second=OcctGeometryBridge.ReadReferencedShape(geometry,assets);
                VerifyGraph(first);VerifyGraph(second);
                Assert.All(first.Members,member=>Assert.Equal(-1,RepairSnapshot.FindTopologyIndex(second.Root,member)));
                Assert.Null(loaded.Snapshot.Features[featureId].TopologyHistory);
            }
            Assert.Equal(0,assets.Count);
        }
    }

    [Fact]
    public void ActualBorrowedCaptureAndLiveGraphSurviveSourceAndAssetsWithoutSharingIndependentReads()
    {
        var assets=new MemoryAssetStore();BrepReferenceRestoreResult graph;
        using(var root=ShapeFactory.CreateBox(10,20,30))
        using(var members=root.GetTopologyAdjacency(ShapeKind.Edge,ShapeKind.Face))
        using(var stored=OcctGeometryBridge.StoreShapeWithReferences(root,members.Items.Concat(members.Ancestors),assets,
            captureEvidence:report=>{Assert.True(report.NativeSourceCaptureProven);Assert.NotNull(report.SourceDomainId);}))
        {
            Assert.Equal(2,assets.Count);graph=OcctGeometryBridge.ReadReferencedShape(stored.Geometry,assets);
            using var ordinary=OcctGeometryBridge.ReadShape(stored.Geometry with{SerializedReferences=null},assets);
            Assert.All(graph.Members,member=>Assert.Equal(-1,RepairSnapshot.FindTopologyIndex(ordinary,member)));
        }
        Assert.Equal(0,assets.Count);
        using(graph)
        {
            VerifyGraph(graph);Assert.Equal(6000,graph.Root.InspectProperties(InspectionPropertyKind.Volume).Mass,7);
        }
    }

    [Fact]
    public void RehashedDeclaredSourceAddressNeverPromotesImportedSourceProof()
    {
        using var root=ShapeFactory.CreateBox(10,20,30);using var members=root.GetTopologyAdjacency(ShapeKind.Edge,ShapeKind.Face);
        var captured=BrepReferenceSnapshot.Capture(root,[members.Items[0]]);byte[] brep=captured.CopyBrepBytes(),table=captured.CopyReferenceTable();
        int row=44+BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(40,4));
        int declared=BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(row,4));
        int spoofed=(declared+1)%captured.Report.DeclaredSourceTopologyCount;
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(row,4),spoofed);Rehash(brep,table);
        SerializedReferenceAssetPolicy.VerifyBinding(brep,table);
        var imported=BrepReferenceSnapshot.Open(brep,table);
        Assert.False(imported.Report.NativeSourceCaptureProven);Assert.Null(imported.Report.SourceDomainId);
        Assert.Equal(spoofed,imported.Report.Rows[0].DeclaredSourceTopologyIndex);
        Assert.Throws<InvalidOperationException>(()=>imported.RestoreFromProvenNativeSource());
        using var graph=imported.Restore();VerifyGraph(graph);
        Assert.Equal(declared,OcctGeometryBridge.FindDecodedReferenceIndex(graph,0));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void TableStageFailureOrCancellationReleasesBothAssets(bool cancel)
    {
        using var root=ShapeFactory.CreateBox(10,20,30);using var token=new CancellationTokenSource();
        var assets=new FailingTableStore(cancel?token:null);
        if(cancel)Assert.Throws<OperationCanceledException>(()=>OcctGeometryBridge.StoreShapeWithReferences(root,[root],assets,token.Token));
        else Assert.Throws<IOException>(()=>OcctGeometryBridge.StoreShapeWithReferences(root,[root],assets));
        Assert.Equal(0,assets.Inner.Count);
    }

    [Theory][InlineData("missing")][InlineData("schema")][InlineData("legacy-schema")][InlineData("capability")]
    [InlineData("role")][InlineData("payload")]
    public async Task ArchiveReferenceFaultsRejectWithoutLeakingAssets(string fault)
    {
        using var files=new TestFiles();string path=files.PathFor("bad-reference.cadoryx");var sourceAssets=new MemoryAssetStore();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Reference faults"),sourceAssets,new OcctGeometryKernel(),new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(Straight(),"Sweep"));await session.SaveAsync(new CadDocumentStorage(),path);
        }
        Assert.Equal(0,sourceAssets.Count);
        if(fault is "missing" or "schema" or "payload")
            FormatEvolutionTests.RewriteSection(path,"geometry",bytes=>
            {
                var table=MessagePackSerializer.Deserialize<PackGeometryTable>(bytes);
                return MessagePackSerializer.Serialize(table with{Items=table.Items.Select(g=>g with{SerializedReferences=g.SerializedReferences! with
                    {TableHash=fault is "missing"?new string('0',64):fault is "payload"?g.Hash:g.SerializedReferences.TableHash,SchemaVersion=fault=="schema"?99:1}}).ToArray()});
            });
        else FormatEvolutionTests.RewriteManifest(path,m=>fault switch
        {
            "legacy-schema"=>m with{Sections=m.Sections.Select(s=>s.Kind=="geometry"?s with{SchemaVersion=1}:s).ToImmutableArray()},
            "capability"=>m with{RequiredCapabilities=m.RequiredCapabilities.Remove("cadoryx.native-reference-tables.1")},
            _=>m with{Assets=m.Assets.Select(a=>a.Format?.MediaType==AssetFormat.ReferenceTableMediaType?a with{Format=AssetFormat.Opaque}:a).ToImmutableArray()}
        });
        var assets=new MemoryAssetStore();
        if(fault=="role")await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
        else await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
        Assert.Equal(0,assets.Count);
    }

    [Fact]
    public async Task FrozenLegacyGeometryIsNeverAssignedReferenceEvidenceDuringMigration()
    {
        var assets=new MemoryAssetStore();using var files=new TestFiles();string path=files.PathFor("legacy.cadoryx");
        using(var loaded=await new CadDocumentStorage().LoadAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","Storage","v2-box.cadoryx"),assets))
        {
            Assert.All(loaded.Snapshot.ReferencedGeometry(),g=>Assert.Null(g.SerializedReferences));
            await new CadDocumentStorage().SaveAsync(loaded.Snapshot,assets,path);
            using var reopened=await new CadDocumentStorage().LoadAsync(path,assets);
            Assert.All(reopened.Snapshot.ReferencedGeometry(),g=>Assert.Null(g.SerializedReferences));
            Assert.Equal(loaded.Snapshot.ReferencedAssets(),reopened.Snapshot.ReferencedAssets());
            Assert.Throws<CadValidationException>(()=>OcctGeometryBridge.ReadReferencedShape(reopened.Snapshot.Bodies.Values.Single().Geometry,assets));
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact]
    public async Task GuidedReferenceBudgetLeavesLargeSupportedLoftGeometryAvailableWithoutPartialProof()
    {
        const int sides=140;
        SketchProfile Polygon(double radius)=>new(Enumerable.Range(0,sides).Select(i=>
            new Point2d(radius*Math.Cos(i*Math.Tau/sides),radius*Math.Sin(i*Math.Tau/sides))).ToImmutableArray());
        var recipe=new LoftRecipe([RigidTransform3d.Identity,RigidTransform3d.Translate(0,0,5)],
            [new(Guid.NewGuid(),null,[Polygon(1),Polygon(2)])],GuidedOutput.Solid,Ruled:true);
        var assets=new MemoryAssetStore();
        using(var result=await new OcctGeometryKernel().EvaluateForDocumentAsync(recipe,assets))
        {
            Assert.Null(result.Geometry.SerializedReferences);Assert.Contains(result.Diagnostics,d=>d.Code=="MODELING.REFERENCE_LIMIT");
            using var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets);var counts=shape.GetTopologySummary().UniqueCounts;
            Assert.True(counts.EdgeCount+counts.FaceCount>BrepReferenceSnapshot.MaximumReferences);
            Assert.True(shape.IsValid);Assert.Equal(BodyKind.Solid,result.Geometry.Kind);
            double area=sides*Math.Sin(Math.Tau/sides)/2;
            Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-35*area/3),0,1e-7);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData(DrawingViewKind.Front)][InlineData(DrawingViewKind.Section)]
    public async Task IsolatedDrawingTransfersReferenceTableAndRetainsExactProjection(DrawingViewKind kind)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var source=await kernel.EvaluateForDocumentAsync(Straight(),assets))
        {
            Assert.NotNull(source.Geometry.SerializedReferences);int held=assets.Count;
            GeometryInstance[] instances=[new(new(DocumentId.New(),[]),BodyId.New(),source.Geometry,RigidTransform3d.Identity)];
            Vector3d? normal=kind==DrawingViewKind.Section?Vector3d.UnitZ:null;
            var expected=await kernel.ProjectAsync(instances,kind,normal,5,assets);
            var worker=new IsolatedDrawingExecutor(Path.Combine(AppContext.BaseDirectory,"Cadoryx.Cli.exe"));
            var actual=await worker.ProjectAsync(instances,kind,normal,5,assets);
            Assert.NotEmpty(actual.Strokes);Assert.Equal(JsonSerializer.Serialize(expected,CadJson.Options),JsonSerializer.Serialize(actual,CadJson.Options));
            Assert.Equal(held,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }

    private static void VerifyGraph(BrepReferenceRestoreResult graph)
    {
        Assert.False(graph.Report.NativeSourceCaptureProven);Assert.Null(graph.Report.SourceDomainId);
        Assert.True(graph.Report.NativeDecodedMembershipProven);Assert.False(graph.Report.ProvesCompleteOccurrencePaths);
        Assert.False(graph.Report.ProvesComposedReferenceIdentity);Assert.NotEmpty(graph.Members);
        for(int i=0;i<graph.Members.Count;i++)
            Assert.Equal(RepairSnapshot.FindTopologyIndex(graph.Root,graph.Members[i]),OcctGeometryBridge.FindDecodedReferenceIndex(graph,i));
    }
    private static void Rehash(byte[] brep,byte[] table)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);hash.AppendData(brep);hash.AppendData(table.AsSpan(0,table.Length-32));
        hash.GetHashAndReset().CopyTo(table.AsSpan(table.Length-32));
    }
    private static SweepRecipe Straight()=>new(new(SketchProfile.FromCircle(new(0,0),1),RigidTransform3d.Identity),
        new([new(new(0,0),new(0,10))],new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0))),GuidedOutput.Solid);
    private sealed class FailingTableStore(CancellationTokenSource? token):IAssetStore
    {
        public MemoryAssetStore Inner{get;}=new();
        public IAssetLease Acquire(AssetId id)=>Inner.Acquire(id);
        public IAssetLease Stage(ReadOnlySpan<byte> bytes)
        {
            if(!bytes.StartsWith("OCRREF25"u8))return Inner.Stage(bytes);
            if(token is null)throw new IOException("Injected table stage failure.");
            var lease=Inner.Stage(bytes);token.Cancel();return lease;
        }
    }
}
