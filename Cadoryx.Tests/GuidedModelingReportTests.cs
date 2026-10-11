using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using MessagePack;
using Xunit;
using Xunit.Abstractions;

namespace Cadoryx.Tests;

public sealed class GuidedModelingReportTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases()=>from kind in CurvedConsumerTestScene.Kinds
        from reverse in new[]{false,true} from tilted in new[]{false,true} from edited in new[]{false,true}
        select new object[]{kind,reverse,tilted,edited};
    [Theory,MemberData(nameof(Cases))]
    public async Task ActualCurvedReportOwnsAsBuiltAuthorsNativeControlsAndResultAcrossBothEncodingsAndRelease(
        string kind,bool reverse,bool tilted,bool edited)
    {
        using var files=new Files();var f=await CurvedConsumerTestScene.Create(kind,reverse,tilted,true);
        GuidedModelingReport report=null!;GeometryAssetRef result=null!;string copied=null!;
        try
        {
            var original=f.Session.Snapshot;var first=original.Features[f.Feature!.Value];
            if(edited)
            {
                await using var editor=f.Parameters(f.Kernel);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                await editor.ConfirmCommand.ExecuteAsync(null);var current=f.Session.Snapshot;
                await f.Session.UndoAsync();Assert.Same(original,f.Session.Snapshot);Assert.Same(first.GuidedModelingReport,f.Session.Snapshot.Features[first.Id].GuidedModelingReport);
                await f.Session.RedoAsync();Assert.Same(current,f.Session.Snapshot);
            }
            var before=f.Session.Snapshot;var feature=before.Features[f.Feature.Value];result=feature.Result;
            CurvedConsumerTestScene.Verify(f.Assets,result,edited?f.EditedVolume:f.OriginalVolume);
            report=Assert.IsType<GuidedModelingReport>(feature.GuidedModelingReport);report.ValidateFor(result);
            Assert.Equal(f.Kernel.Version,report.SdkVersion);Assert.Equal(GuidedModelingReport.RecipeHash(feature.Recipe),report.BuiltRecipeSha256);
            Assert.Equal(result.AssetId,report.ResultAsset);Assert.Equal(result.Revision,report.ResultRevision);
            Assert.Equal(AssetFormatPolicy.CurrentBRep.WriterVersion,result.Format!.WriterVersion);Assert.Contains(result.Format.WriterVersion!,report.SdkVersion);
            Assert.Null(feature.TopologyHistory);
            var preparations=report.Stages.Where(s=>s.Operation==GuidedModelingOperation.CurvePreparation).ToArray();Assert.NotEmpty(preparations);
            Assert.All(preparations,s=>{Assert.Equal(1e-10,s.RequestedTolerance);Assert.Equal(64,s.MaximumSegments);Assert.Equal(11,s.MaximumDegree);
                Assert.NotNull(s.NativeApproximationErrorEstimate);Assert.InRange(s.NativeApproximationErrorEstimate!.Value,0,1e-10);Assert.NotNull(s.NativeOptionsJson);});
            Assert.Contains(report.Stages,s=>s.Operation==(f.IsLoft?GuidedModelingOperation.Loft:GuidedModelingOperation.Sweep));
            if(f.IsLoft)Assert.All(report.Stages.Where(s=>s.Operation==GuidedModelingOperation.Loft),s=>Assert.Null(s.NativeApproximationErrorEstimate));
            copied=JsonSerializer.Serialize(report,GuidedModelingReport.JsonOptions);int count=f.Assets.Count;
            foreach(bool json in new[]{false,true})
            {
                string path=files.Path(json?"report-json.cadoryx":"report-messagepack.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),path);
                if(json)SketchExpressionSectionTests.Rewrite(path,GuidedModelingReportStorage.Kind,_=>GuidedModelingReportStorage.EncodeJson(before.Features.Values),"json",1);
                var manifest=FormatEvolutionTests.Manifest(path);Assert.Equal(19,manifest.Sections.Length);Assert.Equal("0.7.8",manifest.ApplicationVersion);
                Assert.Equal(60,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);Assert.Equal(43,manifest.Sections.Single(s=>s.Kind=="features").SchemaVersion);
                Assert.Contains(GuidedModelingReportStorage.Capability,manifest.RequiredCapabilities);
                Assert.True(manifest.Sections.Single(s=>s.Kind==GuidedModelingReportStorage.Kind).Required);
                using var loaded=await new CadDocumentStorage().LoadAsync(path,f.Assets);var restored=loaded.Snapshot.Features[feature.Id];
                Assert.Equal(copied,JsonSerializer.Serialize(restored.GuidedModelingReport,GuidedModelingReport.JsonOptions));
                Assert.Equal(JsonSerializer.Serialize(feature,CadJson.Options),JsonSerializer.Serialize(restored,CadJson.Options));
                Assert.Equal(result,restored.Result);Assert.DoesNotContain(loaded.Diagnostics,d=>d.Code=="IO.GUIDED_REPORT_UNKNOWN");
                Assert.Same(before,f.Session.Snapshot);
            }
            Assert.Equal(count,f.Assets.Count);output.WriteLine($"{kind}: stages={report.Stages.Length}; sdk={report.SdkVersion}; result={report.ResultAsset}; original volume tolerance1e-7 retained.");
        }
        finally{await f.DisposeAsync();}
        report.ValidateFor(result);Assert.Equal(copied,JsonSerializer.Serialize(report,GuidedModelingReport.JsonOptions));Assert.Equal(0,f.Assets.Count);
    }

    private static readonly string[] Faults=["schema","adapter","revision","asset","recipe-hash","recipe-kind","sdk-null","stages-null","stages-empty",
        "input-hash","output-hash","error-negative","error-excess","options-tolerance","options-duplicates","options-string","no-build-stage",
        "missing-feature","duplicate-feature","entry-null","missing-section","missing-capability","optional-section","section-version","legacy-document"];
    public static IEnumerable<object[]> BadCases()=>from fault in Faults from json in new[]{false,true} select new object[]{fault,json};
    [Theory,MemberData(nameof(BadCases))]
    public async Task MalformedReportOrRequiredSectionNeverAttachesOrLeaks(string fault,bool json)
    {
        using var files=new Files();await using var f=await CurvedConsumerTestScene.Create("bezier-tube",false,false,true);
        string path=files.Path("bad.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),path);int count=f.Assets.Count;var before=f.Session.Snapshot;
        if(fault is "missing-section" or "missing-capability" or "optional-section" or "section-version" or "legacy-document")
        {
            if(fault=="missing-section")
            {
                string entryPath=FormatEvolutionTests.Manifest(path).Sections.Single(s=>s.Kind==GuidedModelingReportStorage.Kind).Path;
                using var zip=ZipFile.Open(path,ZipArchiveMode.Update);zip.GetEntry(entryPath)!.Delete();
            }
            FormatEvolutionTests.RewriteManifest(path,m=>fault switch
            {
                "missing-section"=>m with{Sections=m.Sections.Where(s=>s.Kind!=GuidedModelingReportStorage.Kind).ToImmutableArray()},
                "missing-capability"=>m with{RequiredCapabilities=m.RequiredCapabilities.Where(c=>c!=GuidedModelingReportStorage.Capability).ToImmutableArray()},
                "optional-section"=>m with{Sections=m.Sections.Select(s=>s.Kind==GuidedModelingReportStorage.Kind?s with{Required=false}:s).ToImmutableArray()},
                "section-version"=>m with{Sections=m.Sections.Select(s=>s.Kind==GuidedModelingReportStorage.Kind?s with{SchemaVersion=2}:s).ToImmutableArray()},
                _=>m with{Sections=m.Sections.Select(s=>s.Kind=="document"?s with{SchemaVersion=59}:s).ToImmutableArray()}
            });
        }
        else
        {
            var root=JsonNode.Parse(GuidedModelingReportStorage.EncodeJson(before.Features.Values))!.AsArray();var entry=root[0]!;var report=entry["report"]!;var stage=report["stages"]![0]!;
            switch(fault)
            {
                case "schema":report["schemaVersion"]=9;break;case "adapter":report["adapterVersion"]="unverified";break;
                case "revision":report["resultRevision"]!["value"]=Guid.NewGuid();break;
                case "asset":report["resultAsset"]!["sha256"]=new string('a',64);break;
                case "recipe-hash":report["builtRecipeSha256"]=new string('a',64);break;
                case "recipe-kind":report["builtRecipe"]=JsonNode.Parse(JsonSerializer.Serialize<GeometryRecipe>(new BoxRecipe(1,2,3,RigidTransform3d.Identity),GuidedModelingReport.JsonOptions));break;
                case "sdk-null":report["sdkVersion"]=null;break;case "stages-null":report["stages"]=null;break;case "stages-empty":report["stages"]=new JsonArray();break;
                case "input-hash":stage["inputBrepSha256"]![0]="bad";break;case "output-hash":stage["outputBrepSha256"]="bad";break;
                case "error-negative":stage["nativeApproximationErrorEstimate"]=-1;break;case "error-excess":stage["nativeApproximationErrorEstimate"]=1e-4;break;
                case "options-tolerance":stage["nativeOptionsJson"]="{\"tolerance\":1,\"maximumSegments\":64,\"maximumDegree\":11}";break;
                case "options-duplicates":stage["nativeOptionsJson"]="{\"tolerance\":1e-10,\"tolerance\":1e-10,\"maximumSegments\":64,\"maximumDegree\":11}";break;
                case "options-string":stage["nativeOptionsJson"]="{\"tolerance\":\"1e-10\",\"maximumSegments\":64,\"maximumDegree\":11}";break;
                case "no-build-stage":report["stages"]=new JsonArray(stage.DeepClone());break;
                case "missing-feature":entry["feature"]!["value"]=Guid.NewGuid();break;case "duplicate-feature":root.Add(entry.DeepClone());break;case "entry-null":root[0]=null;break;
            }
            byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(root,GuidedModelingReport.JsonOptions);
            if(!json)
            {
                var entries=root.Select(n=>n is null?null!:new PackGuidedModelingReportEntry(n["feature"]!["value"]!.GetValue<Guid>(),
                    JsonSerializer.SerializeToUtf8Bytes(n["report"],GuidedModelingReport.JsonOptions))).ToArray();
                bytes=MessagePackSerializer.Serialize(new PackGuidedModelingReports(entries),MessagePackSerializerOptions.Standard);
            }
            SketchExpressionSectionTests.Rewrite(path,GuidedModelingReportStorage.Kind,_=>bytes,json?"json":"messagepack",1);
        }
        if(fault=="section-version")await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().LoadAsync(path,f.Assets));
        else await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,f.Assets));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task LegacyGuidedResultRetainsBytesAndUnknownEstimatesWithoutInventingCurrentPolicy(bool json)
    {
        using var files=new Files();await using var f=await CurvedConsumerTestScene.Create("spline-loft",false,true,true);
        string path=files.Path("legacy.cadoryx");await f.Session.SaveAsync(new CadDocumentStorage(),path);var manifest=FormatEvolutionTests.Manifest(path);
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))zip.GetEntry(manifest.Sections.Single(s=>s.Kind==GuidedModelingReportStorage.Kind).Path)!.Delete();
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Where(s=>s.Kind!=GuidedModelingReportStorage.Kind).ToImmutableArray(),
            RequiredCapabilities=m.RequiredCapabilities.Where(c=>c!=GuidedModelingReportStorage.Capability).ToImmutableArray()});
        if(json)SketchExpressionSectionTests.Rewrite(path,"sketch-expressions",_=>SketchExpressionSectionStorage.EncodeJson(f.Session.Snapshot.Sketches.Values),"json",3);
        using var loaded=await new CadDocumentStorage().LoadAsync(path,f.Assets);var original=f.Session.Snapshot.Features[f.Feature!.Value];var restored=loaded.Snapshot.Features[original.Id];
        Assert.Null(restored.GuidedModelingReport);Assert.Equal(original.Result,restored.Result);Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.GUIDED_REPORT_UNKNOWN");
        string resaved=files.Path("legacy-resaved.cadoryx");await new CadDocumentStorage().SaveAsync(loaded.Snapshot,f.Assets,resaved);
        Assert.Equal(18,FormatEvolutionTests.Manifest(resaved).Sections.Length);Assert.DoesNotContain(GuidedModelingReportStorage.Capability,FormatEvolutionTests.Manifest(resaved).RequiredCapabilities);
    }

    [Theory][InlineData("sweep",false)][InlineData("sweep",true)][InlineData("loft",false)][InlineData("loft",true)]
    public async Task ProvenPrismHasCopiedResultContextAndNoInventedNativeApproximation(string kind,bool tilted)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var pose=tilted?new RigidTransform3d(new(11,-7,3),Quaterniond.FromEulerDegrees(23,41,17)):RigidTransform3d.Identity;
        var profile=SketchProfile.FromCircle(new(0,0),1);GeometryRecipe recipe=kind=="sweep"?
            new SweepRecipe(new(profile,pose),new([new(new(0,0),new(0,5))],pose*new RigidTransform3d(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0)))):
            new LoftRecipe([pose,pose*RigidTransform3d.Translate(0,0,5)],[new(Guid.NewGuid(),null,[profile,profile])],Ruled:true);
        using(var result=await kernel.EvaluateAsync(recipe,assets))
        {
            var report=Assert.IsType<GuidedModelingReport>(result.GuidedModelingReport);report.ValidateFor(result.Geometry);var stage=Assert.Single(report.Stages);
            Assert.Equal(GuidedModelingOperation.Prism,stage.Operation);Assert.Null(stage.NativeApproximationErrorEstimate);Assert.Null(stage.RequestedTolerance);Assert.Null(stage.NativeOptionsJson);
            Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-5*Math.PI),0,1e-7);
        }
        Assert.Equal(0,assets.Count);
    }
    private sealed class Files:IDisposable
    {
        public string Root{get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"CadoryxGuidedReport-"+Guid.NewGuid().ToString("N"));
        public Files()=>Directory.CreateDirectory(Root);public string Path(string name)=>System.IO.Path.Combine(Root,name);
        public void Dispose()=>Directory.Delete(Root,true);
    }
}
