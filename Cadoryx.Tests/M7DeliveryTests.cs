using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using Cadoryx.Cli;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Xunit;

namespace Cadoryx.Tests;

public sealed class M7DeliveryTests
{
    private static DocumentSnapshot Assembly()
    {
        var d=DocumentSnapshot.Create("Assembly");var p=DefinitionId.New();var a=DefinitionId.New();var b=BodyId.New();var material=MaterialId.New();
        var geometry=new GeometryAssetRef(new(new string('a',64)),GeometryRevisionId.New(),BodyKind.Solid,new(Vector3d.Zero,new(10,20,30)),6000);
        return d with{Materials=d.Materials.Add(material,new(material,"Steel",.00000785)),
            Bodies=d.Bodies.Add(b,new(b,p,"Body",geometry,null,d.Layers.Keys.Single(),new(),MaterialId:material)),
            Definitions=d.Definitions.Add(p,new PartDefinition(p,"Part",[b],[])).Add(a,new AssemblyDefinition(a,"Shared assembly",
                [new(ComponentSlotId.New(),p,"Leaf",RigidTransform3d.Identity)]))
                .SetItem(d.RootAssemblyId,new AssemblyDefinition(d.RootAssemblyId,d.Name,
                    [new(ComponentSlotId.New(),a,"Branch A",RigidTransform3d.Identity),new(ComponentSlotId.New(),a,"Branch B",RigidTransform3d.Translate(100,0,0))]))};
    }
    [Fact] public void SharedSubassemblyCountsPathsWithoutDoubleCountingDefinitions()
    {
        var d=Assembly();var bom=BillOfMaterials.Create(d);var part=Assert.Single(bom.Parts);
        Assert.Equal(2,part.Quantity);Assert.Equal(.0942,part.TotalMassKg!.Value,8);Assert.Equal(4,bom.Occurrences.Length);
        Assert.Equal(4,bom.Occurrences.Select(o=>o.Path).Distinct().Count());
        Assert.All(bom.Occurrences.Where(o=>!o.IsAssembly),o=>Assert.Contains(bom.Occurrences,parent=>parent.Path==o.ParentPath));
    }
    [Fact] public void VisibilityIncludesAncestorBodyAndLayerAndAllRestoresCounts()
    {
        var d=Assembly();var root=(AssemblyDefinition)d.Definitions[d.RootAssemblyId];
        d=d with{Definitions=d.Definitions.SetItem(root.Id,root with{Children=root.Children.SetItem(0,root.Children[0] with{IsVisible=false})})};
        Assert.Equal(1,Assert.Single(BillOfMaterials.Create(d).Parts).Quantity);Assert.Equal(2,Assert.Single(BillOfMaterials.Create(d,false).Parts).Quantity);
        var layer=d.Layers.Values.Single();d=d with{Layers=d.Layers.SetItem(layer.Id,layer with{IsVisible=false})};
        Assert.Empty(BillOfMaterials.Create(d).Parts);Assert.Single(BillOfMaterials.Create(d,false).Parts);
        d=d with{Layers=d.Layers.SetItem(layer.Id,layer),Bodies=d.Bodies.ToImmutableDictionary(x=>x.Key,x=>x.Value with{IsVisible=false})};
        Assert.Empty(BillOfMaterials.Create(d).Parts);
    }
    [Theory][InlineData(BodyKind.Sheet)][InlineData(BodyKind.Mesh)][InlineData(BodyKind.Compound)]
    public void NonSolidMassIsUnknown(BodyKind kind)
    {
        var d=Assembly();d=d with{Bodies=d.Bodies.ToImmutableDictionary(x=>x.Key,x=>x.Value with{Geometry=x.Value.Geometry with{Kind=kind}})};
        var p=Assert.Single(BillOfMaterials.Create(d).Parts);Assert.Null(p.UnitVolumeMm3);Assert.Null(p.TotalMassKg);
    }
    [Fact] public void MissingDensityDoesNotPretendMassIsZero()
    {
        var d=Assembly();d=d with{Bodies=d.Bodies.ToImmutableDictionary(x=>x.Key,x=>x.Value with{MaterialId=null})};
        var p=Assert.Single(BillOfMaterials.Create(d).Parts);Assert.Equal(6000,p.UnitVolumeMm3);Assert.Null(p.UnitMassKg);
    }
    [Theory][InlineData("=HYPERLINK(\"x\")")][InlineData("  +123")][InlineData("@SUM(A1)")][InlineData("\tformula")]
    public void CsvNeutralizesFormulasAndHtmlEscapesUserText(string name)
    {
        var d=Assembly();var p=d.Definitions.Values.OfType<PartDefinition>().Single();
        d=d with{Name="<script>alert(1)</script>",Definitions=d.Definitions.SetItem(p.Id,p with{Name=name})};var bom=BillOfMaterials.Create(d);
        Assert.Contains("\"'"+name.Replace("\"","\"\""),EngineeringReport.Csv(bom));
        var html=EngineeringReport.Html(d,bom);Assert.DoesNotContain("<script>",html);Assert.Contains("&lt;script&gt;",html);Assert.Contains("default-src 'none'",html);
    }
    [Fact] public async Task AllFormatsPackageRoundtripsSnapshotAndKeepsSavePoint()
    {
        using var files=new TempFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Delivery"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
            var before=session.Snapshot;var dirty=session.IsDirty;var path=files.PathFor("package.zip");
            var result=await new DocumentDeliveryService(storage,kernel).WriteAsync(before,assets,path,new(IncludeIges:true,IncludeStl:true));
            var manifest=await DocumentDeliveryService.VerifyAsync(path);Assert.Equal(8,manifest.Files.Length);Assert.Equal(before.StateId.ToString(),manifest.StateId);
            Assert.Equal(2,manifest.SchemaVersion);
            Assert.Equal(1,result.PartInstances);Assert.Same(before,session.Snapshot);Assert.Equal(dirty,session.IsDirty);
            using(var zip=ZipFile.OpenRead(path))foreach(var file in manifest.Files)zip.GetEntry(file.Name)!.ExtractToFile(files.PathFor(file.Name));
            var loss=System.Text.Json.JsonSerializer.Deserialize<ExchangeLossReport>(
                File.ReadAllText(files.PathFor("exchange-loss.json")),new System.Text.Json.JsonSerializerOptions
                {PropertyNameCaseInsensitive=true})!;
            Assert.Equal(new[]{"STEP","IGES","STL"},loss.Formats.Select(f=>f.Format));
            Assert.Contains(loss.Formats.Single(f=>f.Format=="STL").Lost,x=>x.Contains("Exact BRep"));
            using(var native=await storage.LoadAsync(files.PathFor("model.cadoryx"),assets))Assert.Equal(before.StateId,native.Snapshot.StateId);
            foreach(var ext in new[]{"step","iges","stl"})using(var imported=await kernel.ImportAsync(files.PathFor("model."+ext),assets))Assert.NotEmpty(imported.Snapshot.Bodies);
            Assert.Empty(Directory.GetDirectories(files.Root,".cadoryx-delivery-*"));
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(DeliveryOutput.BomCsv,"bom.csv")][InlineData(DeliveryOutput.ReviewHtml,"review.html")]
    public async Task StandaloneReportsAndNoOverwrite(DeliveryOutput mode,string name)
    {
        using var files=new TempFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var service=new DocumentDeliveryService(new CadDocumentStorage(),kernel);var path=files.PathFor(name);var d=DocumentSnapshot.Create("Empty");
        await service.WriteAsync(d,assets,path,new(mode));var bytes=File.ReadAllBytes(path);Assert.NotEmpty(bytes);
        await Assert.ThrowsAsync<IOException>(()=>service.WriteAsync(d,assets,path,new(mode)));Assert.Equal(bytes,File.ReadAllBytes(path));
        await service.WriteAsync(d with{Name="Changed"},assets,path,new(mode),true);Assert.Empty(Directory.GetDirectories(files.Root));
    }
    [Fact] public async Task CancellationDuringPackagingPreservesTargetAndReleasesLeases()
    {
        using var files=new TempFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var path=files.PathFor("existing.zip");File.WriteAllText(path,"old");
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Cancel"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));using var cancel=new CancellationTokenSource();
            var progress=new CallbackProgress(p=>{if(p.Stage=="Manifest")cancel.Cancel();});
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new DocumentDeliveryService(new CadDocumentStorage(),kernel)
                .WriteAsync(session.Snapshot,assets,path,new(),true,progress,cancel.Token));
            Assert.Equal("old",File.ReadAllText(path));Assert.Empty(Directory.GetDirectories(files.Root));
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task ExportFailurePreservesTargetAndRemovesPartialPackage()
    {
        using var files=new TempFiles();var path=files.PathFor("existing.zip");File.WriteAllText(path,"old");
        var service=new DocumentDeliveryService(new CadDocumentStorage(),new FailingKernel());
        await Assert.ThrowsAsync<IOException>(()=>service.WriteAsync(DocumentSnapshot.Create("Failure"),new MemoryAssetStore(),path,new(),true));
        Assert.Equal("old",File.ReadAllText(path));Assert.Empty(Directory.GetDirectories(files.Root));
    }
    [Theory][InlineData("tamper")][InlineData("extra")][InlineData("missing")][InlineData("duplicate")][InlineData("manifest")]
    public async Task VerifyRejectsDamagedAndUnexpectedPayloads(string fault)
    {
        using var files=new TempFiles();var path=files.PathFor("bad.zip");
        await new DocumentDeliveryService(new CadDocumentStorage(),new OcctGeometryKernel()).WriteAsync(DocumentSnapshot.Create("Integrity"),new MemoryAssetStore(),path,new(IncludeStep:false));
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))
        {
            if(fault is "tamper" or "missing")zip.GetEntry("bom.csv")!.Delete();
            if(fault=="tamper"){using var writer=new StreamWriter(zip.CreateEntry("bom.csv").Open());writer.Write("changed");}
            if(fault=="extra")zip.CreateEntry("../outside.txt");
            if(fault=="duplicate")zip.CreateEntry("bom.csv");
            if(fault=="manifest")
            {
                var entry=zip.GetEntry("manifest.json")!;string json;using(var reader=new StreamReader(entry.Open()))json=reader.ReadToEnd();entry.Delete();
                using var writer=new StreamWriter(zip.CreateEntry("manifest.json").Open());writer.Write(json.Replace("\"schemaVersion\": 2","\"schemaVersion\": 3"));
            }
        }
        await Assert.ThrowsAsync<InvalidDataException>(()=>DocumentDeliveryService.VerifyAsync(path));
    }
    [Fact] public async Task CommandLineDeliveryReportsAndVerificationHaveConsistentExitCodes()
    {
        using var files=new TempFiles();var input=files.PathFor("input.cadoryx");await new CadDocumentStorage().SaveAsync(DocumentSnapshot.Create("CLI"),new MemoryAssetStore(),input);
        foreach(var pair in new[]{("bom","csv"),("report","html"),("deliver","zip")})
        {
            var path=files.PathFor("out."+pair.Item2);var args=new List<string>{pair.Item1,input,"--output",path};if(pair.Item1=="deliver")args.Add("--no-step");
            var errors=new StringWriter();Assert.Equal(0,await CadCommandLine.RunAsync(args.ToArray(),new StringWriter(),errors));Assert.Equal("",errors.ToString());
            Assert.Equal(1,await CadCommandLine.RunAsync(args.ToArray(),new StringWriter(),new StringWriter()));
        }
        Assert.Equal(0,await CadCommandLine.RunAsync(["verify-delivery",files.PathFor("out.zip")],new StringWriter(),new StringWriter()));
        Assert.Equal(2,await CadCommandLine.RunAsync(["deliver",input],new StringWriter(),new StringWriter()));
        Assert.Equal(2,await CadCommandLine.RunAsync(["bom",input,"--output",input],new StringWriter(),new StringWriter()));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        Assert.Equal(130,await CadCommandLine.RunAsync(["report",input,"--output",files.PathFor("cancel.html")],new StringWriter(),new StringWriter(),cancel.Token));
    }
    [Fact] public void DeliveryOptionsAndPreviewUseTheSameVisibilityAndUnits()
    {
        var vm=new DeliveryViewModel(Assembly()){IncludeIges=true,IncludeStl=true,AngularDeflectionDegrees=45,VisibleOnly=false};
        Assert.Equal(2,Assert.Single(vm.Bom.Parts).Quantity);Assert.False(vm.GetOptions().VisibleOnly);Assert.Equal(Math.PI/4,vm.GetOptions().AngularDeflectionRad);
        vm.LinearDeflectionMm=0;Assert.Throws<CadValidationException>(()=>vm.GetOptions());
    }
    private sealed class CallbackProgress(Action<DeliveryProgress> callback):IProgress<DeliveryProgress>{public void Report(DeliveryProgress value)=>callback(value);}
    private sealed class FailingKernel:IGeometryKernel
    {
        public string Version=>"fault-test";
        public bool Supports(GeometryRecipe recipe)=>false;
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe r,IAssetStore a,CancellationToken t=default)=>throw new NotSupportedException();
        public Task<LoadedDocument> ImportAsync(string p,IAssetStore a,CancellationToken t=default)=>throw new NotSupportedException();
        public Task ExportAsync(DocumentSnapshot d,IAssetStore a,string p,CancellationToken t=default)=>throw new IOException("Injected export failure");
        public Task<CadExportReport> ExportAsync(DocumentSnapshot d,IAssetStore a,string p,CadExportOptions o,CancellationToken t=default)=>throw new IOException("Injected export failure");
    }
    private sealed class TempFiles:IDisposable
    {
        public string Root {get;}=Path.Combine(Path.GetTempPath(),"CadoryxM7-"+Guid.NewGuid().ToString("N"));
        public TempFiles()=>Directory.CreateDirectory(Root);
        public string PathFor(string name)=>Path.Combine(Root,name);
        public void Dispose()=>Directory.Delete(Root,true);
    }
}
