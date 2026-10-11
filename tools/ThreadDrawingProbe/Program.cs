using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;

if(args.Length!=2)throw new ArgumentException("Usage: ThreadDrawingProbe window-evidence output");
string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);var rows=new List<object>();
foreach(var(language,caption) in new[]{("en-US",DrawingCaptionLanguage.English),("zh-CN",DrawingCaptionLanguage.Chinese),("ja-JP",DrawingCaptionLanguage.Japanese)})
{
    var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage(holeResolver:kernel);
    using(var loaded=await storage.LoadAsync(Path.Combine(args[0],"thread-"+language+".cadoryx"),assets))
    await using(var session=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
    {
        var original=session.Snapshot;var feature=original.Features.Values.Single(f=>f.Recipe is HoleRecipe);var recipe=(HoleRecipe)feature.Recipe;
        await session.ExecuteAsync(new RecomputeCommand(feature.Id,recipe with{Thread=recipe.Thread! with{Hand=ThreadHand.Left}}));
        if(session.Snapshot.Features[feature.Id].Result!=feature.Result)throw new InvalidOperationException("Symbolic edit changed original geometry.");
        var sheet=DrawingSheetTemplate.Create("Thread pilots");sheet=sheet with{Template=sheet.Template! with{Language=caption}};
        await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));var view=Guid.NewGuid();
        await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,view,"Top",DrawingViewKind.Top,new(95,60),3));
        var doc=session.Snapshot;var body=doc.Bodies.Values.Single();using var shape=OcctGeometryBridge.ReadShape(body.Geometry,assets);using var map=RepairSnapshot.Create(shape);
        var wall=map.Topology.Single(t=>{if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);return face.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder;});
        var source=await kernel.ResolveDrawingTopologyAsync(doc,doc.EnumerateOccurrences().Single().Path,body.Id,wall.Selection.Index,map.Fingerprint,assets);
        var evidence=await kernel.ResolveDrawingHoleAsync(doc,source,assets);
        if(evidence.Thread!=MetricThreadHoles.Create(6,ThreadHand.Left,false,5)||Math.Abs(evidence.DiameterMm-5)>1e-6||Math.Abs(evidence.DepthMm-8)>1e-6)
            throw new InvalidOperationException("Native thread intent proof differs from expected pilot/depth.");
        var table=new DrawingHoleTable(Guid.NewGuid(),view,"M6 threads",evidence.Source.Path,evidence.Source.DefinitionId,RigidTransform3d.Translate(0,0,12),new(20,110))
            {Rows=[new(Guid.NewGuid(),1,evidence,new(0,0))]};table=table with{PositionMm=new(20,100)};await session.ExecuteAsync(DrawingHoleTableCommands.Put(sheet.Id,table));
        var current=session.Snapshot;var labels=DrawingPageLayout.Create(current.DrawingSheets[sheet.Id],1,1).Labels;
        if(!labels.Any(l=>l.Text.Contains("M6 x 1 LH")&&l.Text.Contains("L 5")))throw new InvalidOperationException("Thread label missing.");
        string native=Path.Combine(output,"thread-"+language+".cadoryx");await session.SaveAsync(storage,native);
        using(var reopened=await storage.LoadAsync(native,assets))
            if(!reopened.Snapshot.DrawingSheets[sheet.Id].HoleTables[0].Rows[0].Evidence.SameProof(evidence))throw new InvalidOperationException("Reopen changed thread proof.");
        string pdf=Path.Combine(output,"thread-"+language+".pdf");TechnicalDrawingPdf.Write(current,pdf);
        rows.Add(new{language,pdf,native,designation=evidence.Thread!.Designation,threadDepth=evidence.Thread.DepthMm,pilotDiameter=evidence.DiameterMm,drilledDepth=evidence.DepthMm,labels=labels.Select(l=>new{l.Text,l.MaxWidthMm})});
    }
    if(assets.Count!=0)throw new InvalidOperationException("Drawing assets remain.");
}
var app=Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll").Select(p=>new{path=p,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}).ToArray();
var modules=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>Path.GetDirectoryName(m.FileName)==Path.Combine(AppContext.BaseDirectory,"occt"))
    .Select(m=>new{path=m.FileName,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(m.FileName)))}).ToArray();
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=true,applicationPayload=app,nativeModules=modules,rows},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("Frozen C5 drawing consumer PASS: three languages, native proof, reopened author data, vector PDF and zero assets.");
