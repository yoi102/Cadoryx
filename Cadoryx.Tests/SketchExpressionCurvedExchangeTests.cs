using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;
using Xunit.Abstractions;

namespace Cadoryx.Tests;

public sealed class SketchExpressionCurvedExchangeTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases()=>from kind in CurvedConsumerTestScene.Kinds
        from reverse in new[]{false,true} from tilted in new[]{false,true} from edited in new[]{false,true}
        from format in new[]{"STEP","IGES"} select new object[]{kind,reverse,tilted,edited,format};

    [Theory,MemberData(nameof(Cases))]
    public async Task SavedCurvedResultExportsAndMeasuresActualReimportWithoutClaimingExactAuthorTransfer(
        string kind,bool reverse,bool tilted,bool edited,string format)
    {
        using var files=new Files($"{kind}-r{reverse}-t{tilted}-e{edited}-{format}");
        await using var f=await CurvedConsumerTestScene.Create(kind,reverse,tilted,true);
        if(edited)
        {
            await using var editor=f.Parameters(f.Kernel);await editor.PreviewCommand.ExecuteAsync(null);
            Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
            Assert.Null(f.Document.PreviewScene);Assert.Single(f.Session.Snapshot.Parameters.Bindings);
        }
        var before=f.Session.Snapshot;var result=before.Features[f.Feature!.Value].Result;
        double expected=edited?f.EditedVolume:f.OriginalVolume;
        CurvedConsumerTestScene.Verify(f.Assets,result,expected);
        var storage=new CadDocumentStorage();await f.Session.SaveAsync(storage,files.Path("source.cadoryx"));
        using var restored=await storage.LoadAsync(files.Path("source.cadoryx"),f.Assets);
        SameSavedAuthors(before,restored.Snapshot);
        Assert.Equal(result,restored.Snapshot.Features[f.Feature.Value].Result);
        int count=f.Assets.Count;bool dirty=f.Session.IsDirty;long generation=f.Session.Generation;
        byte[] sourceBytes;using(var lease=f.Assets.Acquire(result.AssetId))sourceBytes=lease.Content.ToArray();
        var options=new DeliveryOptions(DeliveryOutput.ExchangeAudit,IncludeStep:format=="STEP",IncludeIges:format=="IGES");
        string archive=files.Path("audit.zip");
        await new ExchangeMeasurementService(f.Kernel).WriteAsync(restored.Snapshot,f.Assets,archive,options);
        var report=await ExchangeMeasurementService.VerifyAsync(archive);var measured=Assert.Single(report.Formats);
        Assert.Equal(before.Id.ToString(),report.DocumentId);Assert.Equal(before.StateId.ToString(),report.StateId);
        Assert.Equal(ExchangeMeasuredReport.LocalBasis,report.Basis);
        Assert.InRange(Math.Abs(report.Source.KnownSolidVolumeMm3-expected),0,1e-7);
        Assert.Equal("WithinTolerance",measured.Comparisons.Single(c=>c.Aspect=="WorldBoundsMm").Status);
        Assert.Equal("WithinTolerance",measured.Comparisons.Single(c=>c.Aspect=="SurfaceAreaMm2").Status);
        Assert.Equal(format=="STEP"?"WithinTolerance":"NotComparable",measured.Comparisons.Single(c=>c.Aspect=="SolidVolumeMm3").Status);
        Assert.Contains(measured.ExportDiagnostics,d=>d.Code=="EXPORT.EXPRESSION_AUTHOR_VALUES");
        var warning=Assert.Single(measured.ExportDiagnostics.Where(d=>d.Code=="EXPORT.GUIDED_GENERATED_BREP"));
        Assert.Contains("stored generated",warning.Message);Assert.Contains("No per-result",warning.Message);
        Assert.Contains("not the exact author expression",warning.Message);
        Assert.DoesNotContain(measured.ExportDiagnostics,d=>d.Code=="EXPORT.EXPRESSION_EXACT_CANONICALIZATION");
        var loss=ExchangeLossReport.Create(restored.Snapshot,options);var row=Assert.Single(loss.Formats);
        Assert.Contains(row.Lost,text=>text.Contains("Guided author profiles"));
        Assert.Contains(row.Conditional,text=>text.Contains("proven prism branches"));
        if(format=="STEP")Assert.DoesNotContain("Exact BRep",row.Preserved);
        using(var zip=ZipFile.OpenRead(archive))
        {
            var entry=zip.GetEntry(measured.Entry)!;Assert.Equal(measured.Bytes,entry.Length);
            using var stream=entry.Open();Assert.Equal(measured.Sha256,Convert.ToHexStringLower(SHA256.HashData(stream)));
            zip.GetEntry(measured.Entry)!.ExtractToFile(files.Path(measured.Entry));
        }
        using(var imported=await f.Kernel.ImportAsync(files.Path(measured.Entry),f.Assets))
        {
            Assert.Empty(imported.Snapshot.Sketches);Assert.Empty(imported.Snapshot.Parameters.Bindings);
            foreach(var body in imported.Snapshot.Bodies.Values)
            {
                using var shape=OcctGeometryBridge.ReadShape(body.Geometry,f.Assets);
                Assert.True(shape.GetValidationReport(true,true).IsValid);
                if(format=="STEP")
                {
                    Assert.Equal(BodyKind.Solid,body.Geometry.Kind);
                    Assert.Equal(1,shape.GetTopologySummary().UniqueCounts.SolidCount);
                    Assert.True(shape.GetSelfInterferenceReport().IsClear);
                }
                else Assert.Equal(0,shape.GetTopologySummary().UniqueCounts.SolidCount);
            }
        }
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(dirty,f.Session.IsDirty);Assert.Equal(generation,f.Session.Generation);
        CurvedConsumerTestScene.SameAuthors(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
        using(var lease=f.Assets.Acquire(result.AssetId))Assert.Equal(sourceBytes,lease.Content.ToArray());
        Assert.Empty(Directory.GetDirectories(files.Root,".cadoryx-exchange-*"));
        var evidence=new{kind,reverse,tilted,edited,format,expectedVolume=expected,generatedVolume=report.Source.KnownSolidVolumeMm3,
            sourceModelingVolumeDifference=Math.Abs(report.Source.KnownSolidVolumeMm3-expected),reopenedVolume=measured.Reopened.KnownSolidVolumeMm3,
            comparisons=measured.Comparisons,generatedSourceUnchanged=true,authorSemanticsAbsentAfterExchange=true,
            completeGeometryEquivalence="NOT PROVEN",externalCad="NOT RUN"};
        await File.WriteAllTextAsync(files.Path("result.json"),JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));
        output.WriteLine(JsonSerializer.Serialize(evidence));
    }

    private static void SameSavedAuthors(DocumentSnapshot expected,DocumentSnapshot actual)
    {
        Assert.Equal(expected.Id,actual.Id);Assert.Equal(expected.StateId,actual.StateId);
        foreach(var sketch in expected.Sketches.Values)
        {
            var restored=actual.Sketches[sketch.Id];Assert.Equal(sketch.Revision,restored.Revision);
            Assert.True(SketchExpressionEntities.SameTable(sketch,restored));
            Assert.Equal(sketch.Points.ToArray(),restored.Points.ToArray());Assert.Equal(sketch.Constraints.ToArray(),restored.Constraints.ToArray());
        }
        // Persisted ImmutableArray values are independently owned after loading;
        // compare all serialized recipe values rather than their backing arrays.
        foreach(var feature in expected.Features.Values)
        {
            var restored=actual.Features[feature.Id];Assert.Equal(feature.Result,restored.Result);
            Assert.Equal(JsonSerializer.Serialize(feature,CadJson.Options),JsonSerializer.Serialize(restored,CadJson.Options));
        }
        Assert.Equal(JsonSerializer.Serialize(expected.Parameters,CadJson.Options),JsonSerializer.Serialize(actual.Parameters,CadJson.Options));
    }

    [Theory]
    [InlineData("sweep","STEP")][InlineData("sweep","IGES")]
    [InlineData("loft","STEP")][InlineData("loft","IGES")]
    public async Task ProvenPrismBranchesAreNotReportedAsNativeCurvePreparation(string kind,string format)
    {
        using var files=new Files($"prism-{kind}-{format}");var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Prism exchange"),assets,kernel,new InlineSessionDispatcher()))
        {
            var profile=SketchProfile.FromCircle(new(0,0),1);
            GeometryRecipe recipe=kind=="sweep"?
                new SweepRecipe(new(profile,RigidTransform3d.Identity),new([new(new(0,0),new(0,5))],new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0)))):
                new LoftRecipe([RigidTransform3d.Identity,RigidTransform3d.Translate(0,0,5)],[new(Guid.NewGuid(),null,[profile,profile])],Ruled:true);
            await session.ExecuteAsync(new AddBodyCommand(recipe,kind));var before=session.Snapshot;
            var report=await kernel.ExportAsync(before,assets,files.Path("prism."+format.ToLowerInvariant()),new CadExportOptions());
            Assert.DoesNotContain(report.Diagnostics,d=>d.Code=="EXPORT.GUIDED_GENERATED_BREP");Assert.Same(before,session.Snapshot);
            var loss=Assert.Single(ExchangeLossReport.Create(before,new(IncludeStep:format=="STEP",IncludeIges:format=="IGES")).Formats);
            Assert.Contains(loss.Conditional,text=>text.Contains("proven prism branches"));
        }
        Assert.Equal(0,assets.Count);
    }
    private sealed class Files:IDisposable
    {
        private readonly bool retained;
        public string Root{get;}
        public Files(string label)
        {
            string? evidence=Environment.GetEnvironmentVariable("CADORYX_CURVED_EXCHANGE_EVIDENCE_ROOT");retained=!string.IsNullOrWhiteSpace(evidence);
            Root=retained?System.IO.Path.Combine(System.IO.Path.GetFullPath(evidence!),label):System.IO.Path.Combine(System.IO.Path.GetTempPath(),"CadoryxCurvedExchange-"+Guid.NewGuid().ToString("N"));
            if(Directory.Exists(Root))throw new IOException("Immutable curved exchange evidence already exists.");Directory.CreateDirectory(Root);
        }
        public string Path(string name)=>System.IO.Path.Combine(Root,name);
        public void Dispose(){if(!retained)Directory.Delete(Root,true);}
    }
}
