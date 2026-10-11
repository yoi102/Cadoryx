using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.Cli;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;
using Xunit;

namespace Cadoryx.Tests;

public sealed class ExchangeMeasurementTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData("rectangle",false,"IGES")][InlineData("rectangle",true,"IGES")]
    [InlineData("triangle",false,"IGES")][InlineData("triangle",true,"IGES")]
    [InlineData("slot",false,"IGES")][InlineData("slot",true,"IGES")]
    [InlineData("rectangle",false,"STEP")][InlineData("rectangle",true,"STEP")]
    [InlineData("triangle",false,"STEP")][InlineData("triangle",true,"STEP")]
    [InlineData("slot",false,"STEP")][InlineData("slot",true,"STEP")]
    public async Task LocatedBrepMeasuredExportRetainsAllFiniteExtrusionSidesAndCapturedState(string profile,bool rotated,string format)
    {
        using var files=new Files();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Finite extrusion"),assets,kernel,new InlineSessionDispatcher()))
        {
            GpPoint[] points=profile switch
            {
                "rectangle"=>[new(0,0,0),new(20,0,0),new(20,12,0),new(0,12,0)],
                "triangle"=>[new(0,0,0),new(20,0,0),new(0,12,0)],
                _=>[new(0,0,0),new(20,0,0),new(20,12,0),new(13,12,0),new(13,5,0),new(7,5,0),new(7,12,0),new(0,12,0)]
            };
            using var wire=ShapeFactory.CreatePolygonWire(points,true);using var face=ShapeFactory.CreatePlanarFace(wire);using var direction=GpVec.Create(0,0,4);
            using var shape=face.Extrude(direction);using var move=GpTrsf.Create(30,-20,10,1,2,3,rotated?.6:0);using var placed=shape.Transformed(move);
            using var stored=OcctGeometryBridge.StoreShape(placed,assets);await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),profile));
            var before=session.Snapshot;bool dirty=session.IsDirty;
            await new ExchangeMeasurementService(kernel).WriteAsync(before,assets,files.Path("audit.zip"),new(DeliveryOutput.ExchangeAudit,IncludeStep:format=="STEP",IncludeIges:format=="IGES"));
            var report=await ExchangeMeasurementService.VerifyAsync(files.Path("audit.zip"));var measured=Assert.Single(report.Formats);
            output.WriteLine($"{format}: source/reopened faces {report.Source.TopologyFaces}/{measured.Reopened.TopologyFaces}; area {report.Source.SurfaceAreaMm2:R}/{measured.Reopened.SurfaceAreaMm2:R} mm2.");
            Assert.Equal(report.Source.TopologyFaces,measured.Reopened.TopologyFaces);
            Assert.Equal("WithinTolerance",measured.Comparisons.Single(c=>c.Aspect=="SurfaceAreaMm2").Status);
            Assert.Equal("WithinTolerance",measured.Comparisons.Single(c=>c.Aspect=="WorldBoundsMm").Status);
            Assert.Equal(format=="IGES"?"NotComparable":"WithinTolerance",measured.Comparisons.Single(c=>c.Aspect=="SolidVolumeMm3").Status);
            Assert.Equal(before.StateId,session.Snapshot.StateId);Assert.Equal(dirty,session.IsDirty);
            Assert.Equal(before.Bodies.Values.Single().Geometry,session.Snapshot.Bodies.Values.Single().Geometry);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory]
    [InlineData("mm","step",1)][InlineData("inch","step",25.4)][InlineData("meter","step",1000)]
    [InlineData("mm","iges",1)][InlineData("inch","iges",25.4)][InlineData("meter","iges",1000)]
    public async Task IndependentlyDeclaredSourceUnitsKeepMeasuredMillimeterBoundsAndArea(string unit,string format,double scale)
    {
        using var files=new Files();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var loaded=await kernel.ImportAsync(ExchangeFixtureTests.Fixture($"unit-{unit}.{format}"),assets))
        {
            var snapshot=loaded.Snapshot;await new ExchangeMeasurementService(kernel).WriteAsync(snapshot,assets,files.Path("audit.zip"),new(DeliveryOutput.ExchangeAudit,IncludeIges:true));
            var report=await ExchangeMeasurementService.VerifyAsync(files.Path("audit.zip"));
            Assert.Equal(snapshot.Id.ToString(),report.DocumentId);Assert.Equal(snapshot.StateId.ToString(),report.StateId);
            Assert.Equal(scale,report.Source.BoundsMm.Max.X,5);Assert.Equal(2*scale,report.Source.BoundsMm.Max.Y,5);Assert.Equal(3*scale,report.Source.BoundsMm.Max.Z,5);
            Assert.Equal(22*scale*scale,report.Source.SurfaceAreaMm2,3);
            foreach(var result in report.Formats)
            {
                Assert.Equal("WithinTolerance",result.Comparisons.Single(c=>c.Aspect=="WorldBoundsMm").Status);
                Assert.Equal("WithinTolerance",result.Comparisons.Single(c=>c.Aspect=="SurfaceAreaMm2").Status);
                Assert.Equal("NotMeasured",result.Comparisons.Single(c=>c.Aspect=="PMI").Status);
            }
            Assert.Equal(ExchangeMeasuredReport.LocalBasis,report.Basis);Assert.Contains("External CAD reader/writer results",report.NotMeasured);
            Assert.Empty(Directory.GetDirectories(files.Root,".cadoryx-exchange-*"));
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("STEP")][InlineData("IGES")][InlineData("STL")]
    public async Task RotatedSharedColoredSourceReportsRealReimportAndRepresentationalLoss(string format)
    {
        using var files=new Files();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var loaded=await kernel.ImportAsync(ExchangeFixtureTests.Fixture("rotated-colors.step"),assets))
        {
            await new DocumentDeliveryService(new CadDocumentStorage(),kernel).WriteAsync(loaded.Snapshot,assets,files.Path("audit.zip"),
                new(DeliveryOutput.ExchangeAudit,IncludeStep:format=="STEP",IncludeIges:format=="IGES",IncludeStl:format=="STL"));
            var report=await ExchangeMeasurementService.VerifyAsync(files.Path("audit.zip"));var result=Assert.Single(report.Formats);
            Assert.Equal(2,report.Source.BodyInstances);Assert.Equal(1,report.Source.PartDefinitions);Assert.Equal([2],report.Source.PartReuseCounts.ToArray());
            Assert.Equal(-35,report.Source.BoundsMm.Min.X,5);Assert.Equal(100,report.Source.BoundsMm.Max.X,5);
            Assert.Equal("WithinTolerance",result.Comparisons.Single(c=>c.Aspect=="WorldBoundsMm").Status);
            Assert.Contains(0xFFFF0000u,report.Source.EffectiveColorPalette);Assert.Contains(0xFF00FF00u,report.Source.EffectiveColorPalette);Assert.Contains(0xFF0000FFu,report.Source.EffectiveColorPalette);
            if(format=="STEP")
            {
                Assert.Equal("Same",result.Comparisons.Single(c=>c.Aspect=="PartDefinitionSharing").Status);
                Assert.Equal("Same",result.Comparisons.Single(c=>c.Aspect=="EffectiveColorPalette").Status);
                Assert.Equal("WithinTolerance",result.Comparisons.Single(c=>c.Aspect=="SolidVolumeMm3").Status);
            }
            else
            {
                Assert.True(result.Reopened.OtherBodies>0);Assert.Equal("NotComparable",result.Comparisons.Single(c=>c.Aspect=="SolidVolumeMm3").Status);
                Assert.Equal("Different",result.Comparisons.Single(c=>c.Aspect=="PartDefinitionSharing").Status);
            }
            if(format=="STL")
            {
                Assert.Equal("Absent",result.Comparisons.Single(c=>c.Aspect=="IntrinsicUnitDeclaration").Status);
                Assert.Equal("Different",result.Comparisons.Single(c=>c.Aspect=="EffectiveColorPalette").Status);
            }
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("Source observation")][InlineData("Re-import STEP")][InlineData("Archive and verify")]
    public async Task CancellationAtMeasurementStagesPreservesTargetAndReleasesSnapshotLeases(string stage)
    {
        using var files=new Files();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var loaded=await kernel.ImportAsync(ExchangeFixtureTests.Fixture("unit-mm.step"),assets))
        {
            string path=files.Path("audit.zip");await File.WriteAllTextAsync(path,"previous");int count=assets.Count;using var cancel=new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ExchangeMeasurementService(kernel).WriteAsync(loaded.Snapshot,assets,path,
                new(DeliveryOutput.ExchangeAudit),true,new ProgressCallback(p=>{if(p.Stage==stage)cancel.Cancel();}),cancel.Token));
            Assert.Equal("previous",await File.ReadAllTextAsync(path));Assert.Equal(count,assets.Count);Assert.Empty(Directory.GetDirectories(files.Root));
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact]
    public async Task AReaderFailureAfterARealExportCannotPublishAPartialArchive()
    {
        using var files=new Files();var assets=new MemoryAssetStore();var native=new OcctGeometryKernel();
        using(var loaded=await native.ImportAsync(ExchangeFixtureTests.Fixture("unit-mm.step"),assets))
        {
            string path=files.Path("audit.zip");await File.WriteAllTextAsync(path,"previous");
            await Assert.ThrowsAsync<IOException>(()=>new ExchangeMeasurementService(new FailingReader(native)).WriteAsync(loaded.Snapshot,assets,path,new(DeliveryOutput.ExchangeAudit),true));
            Assert.Equal("previous",await File.ReadAllTextAsync(path));Assert.Empty(Directory.GetDirectories(files.Root));
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("bytes")][InlineData("comparison")][InlineData("basis")][InlineData("entry")]
    [InlineData("null-format")][InlineData("missing-comparisons")][InlineData("null-comparison")]
    [InlineData("graph-count")][InlineData("reuse-count")][InlineData("graph-group")][InlineData("palette")][InlineData("bad-json")]
    public async Task ArchiveVerificationRejectsChangedExchangeBytesOrForgedMeasurementClaims(string mutation)
    {
        using var files=new Files();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var loaded=await kernel.ImportAsync(ExchangeFixtureTests.Fixture("unit-mm.step"),assets))
            await new ExchangeMeasurementService(kernel).WriteAsync(loaded.Snapshot,assets,files.Path("audit.zip"),new(DeliveryOutput.ExchangeAudit));
        using(var zip=ZipFile.Open(files.Path("audit.zip"),ZipArchiveMode.Update))
        {
            if(mutation=="bytes")
            {
                var entry=zip.GetEntry("export.step")!;entry.Delete();using var writer=new StreamWriter(zip.CreateEntry("export.step").Open());writer.Write("changed");
            }
            else
            {
                var entry=zip.GetEntry("report.json")!;JsonNode json;using(var reader=new StreamReader(entry.Open()))json=JsonNode.Parse(reader.ReadToEnd())!;entry.Delete();
                if(mutation=="comparison")json["formats"]![0]!["comparisons"]![0]!["status"]="Same";
                if(mutation=="basis")json["basis"]="Externally certified";
                if(mutation=="entry")json["formats"]![0]!["entry"]="../export.step";
                if(mutation=="null-format")json["formats"]![0]=null;
                if(mutation=="missing-comparisons")json["formats"]![0]!.AsObject().Remove("comparisons");
                if(mutation=="null-comparison")json["formats"]![0]!["comparisons"]![0]=null;
                if(mutation=="graph-count")json["source"]!["bodyInstances"]=2;
                if(mutation=="reuse-count")json["source"]!["partReuseCounts"]![0]=2;
                if(mutation=="graph-group")json["source"]!["occurrences"]![0]!["definitionGroup"]=99;
                if(mutation=="palette")json["source"]!["effectiveColorPalette"]!.AsArray().Add(json["source"]!["effectiveColorPalette"]![0]!.DeepClone());
                using var writer=new StreamWriter(zip.CreateEntry("report.json").Open());writer.Write(mutation=="bad-json"?"{":json.ToJsonString());
            }
        }
        await Assert.ThrowsAsync<InvalidDataException>(()=>ExchangeMeasurementService.VerifyAsync(files.Path("audit.zip")));Assert.Equal(0,assets.Count);
    }
    [Fact]
    public async Task CliMeasurementAndVerificationPreserveNoOverwriteAndCancellationExitCodes()
    {
        using var files=new Files();string source=ExchangeFixtureTests.Fixture("unit-inch.step"),path=files.Path("audit.zip");var error=new StringWriter();
        Assert.Equal(0,await CadCommandLine.RunAsync(["exchange-audit",source,"--output",path,"--iges","--stl","--memory"],new StringWriter(),error));Assert.Equal("",error.ToString());
        byte[] original=await File.ReadAllBytesAsync(path);Assert.Equal(1,await CadCommandLine.RunAsync(["exchange-audit",source,"--output",path],new StringWriter(),new StringWriter()));Assert.Equal(original,await File.ReadAllBytesAsync(path));
        Assert.Equal(0,await CadCommandLine.RunAsync(["verify-exchange",path],new StringWriter(),new StringWriter()));
        Assert.Equal(2,await CadCommandLine.RunAsync(["exchange-audit",source,"--output",path,"--no-step"],new StringWriter(),new StringWriter()));
        using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Equal(130,await CadCommandLine.RunAsync(["exchange-audit",source,"--output",files.Path("cancel.zip")],new StringWriter(),new StringWriter(),cancel.Token));
        Assert.False(File.Exists(files.Path("cancel.zip")));
    }
    [Fact]
    public async Task VisibilityAndExplicitColorOverridesAreObservedFromTheCapturedDocument()
    {
        using var files=new Files();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var loaded=await kernel.ImportAsync(ExchangeFixtureTests.Fixture("rotated-colors.step"),assets))
        {
            var body=loaded.Snapshot.Bodies.Values.Single();var snapshot=loaded.Snapshot with{Bodies=loaded.Snapshot.Bodies.SetItem(body.Id,body with{Appearance=new(0xFFFFFF00)})};
            var assembly=snapshot.Definitions.Values.OfType<AssemblyDefinition>().First(a=>a.Children.Length==2);
            snapshot=snapshot with{Definitions=snapshot.Definitions.SetItem(assembly.Id,assembly with{Children=assembly.Children.SetItem(0,assembly.Children[0] with{IsVisible=false})})};
            foreach(bool visible in new[]{true,false})
            {
                await new ExchangeMeasurementService(kernel).WriteAsync(snapshot,assets,files.Path(visible+".zip"),new(DeliveryOutput.ExchangeAudit,VisibleOnly:visible));
                var report=await ExchangeMeasurementService.VerifyAsync(files.Path(visible+".zip"));
                Assert.Equal(visible?1:2,report.Source.BodyInstances);Assert.Equal([0xFFFFFF00u],report.Source.EffectiveColorPalette.ToArray());
                Assert.Equal("Same",Assert.Single(report.Formats).Comparisons.Single(c=>c.Aspect=="EffectiveColorPalette").Status);
            }
            var model=new DeliveryViewModel(snapshot){OutputIndex=3,IncludeIges=true};Assert.Equal(DeliveryOutput.ExchangeAudit,model.GetOptions().Output);
            model.IncludeStep=model.IncludeIges=model.IncludeStl=false;Assert.Throws<CadValidationException>(()=>model.GetOptions());
        }
        Assert.Equal(0,assets.Count);
    }
    private sealed class ProgressCallback(Action<DeliveryProgress> callback):IProgress<DeliveryProgress>{public void Report(DeliveryProgress p)=>callback(p);}
    private sealed class FailingReader(OcctGeometryKernel native):IGeometryKernel,IExchangeObservationService
    {
        public string Version=>native.Version;public bool Supports(GeometryRecipe r)=>native.Supports(r);
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe r,IAssetStore a,CancellationToken t=default)=>native.EvaluateAsync(r,a,t);
        public Task<LoadedDocument> ImportAsync(string p,IAssetStore a,CancellationToken t=default)=>throw new IOException("Injected reader failure after real export.");
        public Task ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CancellationToken t=default)=>native.ExportAsync(s,a,p,t);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CadExportOptions o,CancellationToken t=default)=>native.ExportAsync(s,a,p,o,t);
        public Task<ExchangeModelObservation> ObserveExchangeAsync(DocumentSnapshot s,IAssetStore a,bool v=true,CancellationToken t=default)=>native.ObserveExchangeAsync(s,a,v,t);
    }
    private sealed class Files:IDisposable
    {
        public string Root {get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"CadoryxExchangeMeasurement-"+Guid.NewGuid().ToString("N"));
        public Files()=>Directory.CreateDirectory(Root);public string Path(string name)=>System.IO.Path.Combine(Root,name);
        public void Dispose()=>Directory.Delete(Root,true);
    }
}
