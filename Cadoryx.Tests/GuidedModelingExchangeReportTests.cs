using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Xunit;

namespace Cadoryx.Tests;

public sealed class GuidedModelingExchangeReportTests
{
    public static IEnumerable<object[]> Cases()=>from kind in CurvedConsumerTestScene.Kinds
        from edited in new[]{false,true} from known in new[]{false,true} from format in new[]{"STEP","IGES"}
        select new object[]{kind,edited,known,format};
    [Theory,MemberData(nameof(Cases))]
    public async Task ActualSavedObservationSummaryUsesReportedValuesAndLegacyStaysUnknown(string kind,bool edited,bool known,string format)
    {
        using var files=new Files();await using var f=await CurvedConsumerTestScene.Create(kind,false,true,true);
        if(edited)
        {
            await using var editor=f.Parameters(f.Kernel);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
            await editor.ConfirmCommand.ExecuteAsync(null);
        }
        var before=f.Session.Snapshot;var feature=before.Features[f.Feature!.Value];var report=Assert.IsType<GuidedModelingReport>(feature.GuidedModelingReport);
        CurvedConsumerTestScene.Verify(f.Assets,feature.Result,edited?f.EditedVolume:f.OriginalVolume);
        var storage=new CadDocumentStorage();string source=files.Path("source.cadoryx");await f.Session.SaveAsync(storage,source);
        if(!known)
        {
            string entryPath=FormatEvolutionTests.Manifest(source).Sections.Single(s=>s.Kind==GuidedModelingReportStorage.Kind).Path;
            using(var zip=ZipFile.Open(source,ZipArchiveMode.Update))zip.GetEntry(entryPath)!.Delete();
            FormatEvolutionTests.RewriteManifest(source,m=>m with{Sections=m.Sections.Where(s=>s.Kind!=GuidedModelingReportStorage.Kind).ToImmutableArray(),
                RequiredCapabilities=m.RequiredCapabilities.Where(c=>c!=GuidedModelingReportStorage.Capability).ToImmutableArray()});
        }
        int count=f.Assets.Count;string archive=files.Path("audit.zip");
        using(var loaded=await storage.LoadAsync(source,f.Assets))
        {
            Assert.Equal(feature.Result,loaded.Snapshot.Features[feature.Id].Result);
            if(!known)Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.GUIDED_REPORT_UNKNOWN");
            await new ExchangeMeasurementService(f.Kernel).WriteAsync(loaded.Snapshot,f.Assets,archive,
                new(DeliveryOutput.ExchangeAudit,IncludeStep:format=="STEP",IncludeIges:format=="IGES"));
        }
        var measurement=await ExchangeMeasurementService.VerifyAsync(archive);var exported=Assert.Single(measurement.Formats);
        var observations=exported.ExportDiagnostics.Where(d=>d.Code=="EXPORT.GUIDED_MODELING_OBSERVATIONS").ToArray();
        if(known)
        {
            string message=Assert.Single(observations).Message;
            Assert.Contains(report.SdkVersion,message);Assert.Contains(report.NativeBridgeVersion,message);Assert.Contains(report.NativeAbiVersion,message);
            Assert.Contains(report.ResultAsset.ToString(),message);Assert.Contains(report.ResultRevision.ToString(),message);Assert.Contains(report.BuiltRecipeSha256,message);
            Assert.Contains($"stages {report.Stages.Length}",message);
            foreach(var(operation,label)in new[]{(GuidedModelingOperation.CurvePreparation,"curve-preparation"),(GuidedModelingOperation.Sweep,"sweep"),(GuidedModelingOperation.Loft,"loft")})
            {
                var values=report.Stages.Where(s=>s.Operation==operation&&s.NativeApproximationErrorEstimate is not null).Select(s=>s.NativeApproximationErrorEstimate!.Value).ToArray();
                string value=values.Length==0?"not reported":values.Max().ToString("R",CultureInfo.InvariantCulture);
                Assert.Contains(label+" estimate: "+value,message);
            }
            Assert.Contains("not a maximum total modeling-error certificate or topology identity proof",message);
        }
        else Assert.Empty(observations);
        Assert.Contains(exported.ExportDiagnostics,d=>d.Code=="EXPORT.GUIDED_GENERATED_BREP"&&d.Message.Contains("No per-result"));
        Assert.Equal("WithinTolerance",exported.Comparisons.Single(c=>c.Aspect=="WorldBoundsMm").Status);
        Assert.Equal("WithinTolerance",exported.Comparisons.Single(c=>c.Aspect=="SurfaceAreaMm2").Status);
        Assert.Equal(format=="STEP"?"WithinTolerance":"NotComparable",exported.Comparisons.Single(c=>c.Aspect=="SolidVolumeMm3").Status);
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
    }
    private sealed class Files:IDisposable
    {
        public string Root{get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"CadoryxGuidedExchangeReport-"+Guid.NewGuid().ToString("N"));
        public Files()=>Directory.CreateDirectory(Root);public string Path(string name)=>System.IO.Path.Combine(Root,name);
        public void Dispose()=>Directory.Delete(Root,true);
    }
}
