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

if(args.Length is not (3 or 4)||args.Length==4&&args[3]!="--brep-only")throw new ArgumentException("Usage: ModeledThreadDrawingProbe frozen-candidate window-evidence output [--brep-only]");
string frozen=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[2]);Directory.CreateDirectory(output);var rows=new List<object>();
void Progress(string value)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {value}{Environment.NewLine}");
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
foreach(string path in Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll"))
    if(Hash(path)!=Hash(Path.Combine(frozen,Path.GetFileName(path))))throw new InvalidDataException("Consumer differs from frozen production DLL: "+path);
#if BOUNDED_NATIVE_DRAWING
var kernel=new OcctGeometryKernel(new IsolatedMetricThreadExecutor(Path.Combine(frozen,"Cadoryx.wpf.exe")),
    new IsolatedDrawingExecutor(Path.Combine(frozen,"Cadoryx.wpf.exe")));
#else
var kernel=new OcctGeometryKernel(new IsolatedMetricThreadExecutor(Path.Combine(frozen,"Cadoryx.wpf.exe")));
#endif
var storage=new CadDocumentStorage(datumResolver:kernel);
foreach(var(language,caption) in new[]{("en-US",DrawingCaptionLanguage.English),("zh-CN",DrawingCaptionLanguage.Chinese),("ja-JP",DrawingCaptionLanguage.Japanese)})
{
    var assets=new MemoryAssetStore();
    Progress(language+" load window document");
    using(var loaded=await storage.LoadAsync(Path.Combine(args[1],"thread-modeled-"+language+".cadoryx"),assets))
    await using(var session=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
    {
        var original=session.Snapshot;var feature=original.Features.Values.Single(f=>f.Recipe is HoleRecipe);var recipe=(HoleRecipe)feature.Recipe;
        if(recipe.Thread?.Representation!=ThreadHoleRepresentation.Modeled)throw new InvalidDataException("Window did not save modeled teeth.");
        Progress(language+" rebuild LH teeth");await session.ExecuteAsync(new RecomputeCommand(feature.Id,recipe with{Thread=recipe.Thread with{Hand=ThreadHand.Left,DepthMm=4.25}}));
        var doc=session.Snapshot;var current=doc.Features[feature.Id];var body=doc.Bodies[current.OutputBodyId];
        if(current.Result.AssetId==feature.Result.AssetId||current.Result.VolumeMm3>=feature.Result.VolumeMm3)throw new InvalidOperationException("Modeled edit did not rebuild physical teeth.");
        using var shape=OcctGeometryBridge.ReadShape(body.Geometry,assets);using var map=RepairSnapshot.Create(shape);
        var tooth=map.Topology.First(t=>{if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);
            return face.GetFaceSurfaceSnapshot().SurfaceType is not (SurfaceGeometryType.Plane or SurfaceGeometryType.Cylinder);});
        Progress(language+" resolve exact tooth attachment");var source=await kernel.ResolveDrawingTopologyAsync(doc,doc.EnumerateOccurrences().Single().Path,body.Id,tooth.Selection.Index,map.Fingerprint,assets);
        Progress(language+" prove tooth and complete pilot");var evidence=await kernel.ResolveDrawingHoleAsync(doc,source,assets);
        if(evidence.Thread!=MetricThreadHoles.Create(6,ThreadHand.Left,false,4.25,ThreadHoleRepresentation.Modeled)||
            Math.Abs(evidence.DiameterMm-5)>1e-6||Math.Abs(evidence.DepthMm-8)>1e-6||!evidence.ProvenFaceIndices.Contains(source.FullTopologyIndex))
            throw new InvalidOperationException("Native tooth/complete pilot proof differs from current modeled author.");
        if(args.Length==4)
        {
            string brep=Path.Combine(output,"verified-lh-thread.brep");ShapeExchange.WriteBrep(shape,brep);
            File.WriteAllText(Path.Combine(output,"fixture.json"),JsonSerializer.Serialize(new{language,brep,sha256=Hash(brep),
                sourceDocument=Path.Combine(args[1],"thread-modeled-"+language+".cadoryx"),recipe=current.Recipe,evidence,current.Result}));
            Console.WriteLine("Verified LH thread native projection fixture prepared.");return;
        }
        var sheet=DrawingSheetTemplate.Create("Modeled metric threads");sheet=sheet with{Template=sheet.Template! with{Language=caption}};
        await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));var view=Guid.NewGuid();
        Progress(language+" project Top view");await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,view,"Top",DrawingViewKind.Top,new(95,60),3));
        var table=new DrawingHoleTable(Guid.NewGuid(),view,"M6 modeled",source.Path,source.DefinitionId,RigidTransform3d.Translate(0,0,12),new(20,100))
            {Rows=[new(Guid.NewGuid(),1,evidence,new(0,0))]};Progress(language+" put exact hole table");await session.ExecuteAsync(DrawingHoleTableCommands.Put(sheet.Id,table));
        var accepted=session.Snapshot;var labels=DrawingPageLayout.Create(accepted.DrawingSheets[sheet.Id],1,1).Labels;
        if(!labels.Any(l=>l.Text.Contains("M6 x 1 LH")&&l.Text.Contains("L 4.25")))throw new InvalidOperationException("Modeled thread label missing.");
        string native=Path.Combine(output,"modeled-thread-"+language+".cadoryx");Progress(language+" save drawing document");await session.SaveAsync(storage,native);
        Progress(language+" reopen drawing document");
        using(var reopened=await storage.LoadAsync(native,assets))
            if(!reopened.Snapshot.DrawingSheets[sheet.Id].HoleTables[0].Rows[0].Evidence.SameProof(evidence))throw new InvalidOperationException("Reopen changed exact modeled proof.");
        string pdf=Path.Combine(output,"modeled-thread-"+language+".pdf");Progress(language+" write vector PDF");TechnicalDrawingPdf.Write(accepted,pdf);
        rows.Add(new{language,pdf,native,designation=evidence.Thread!.Designation,threadDepth=evidence.Thread.DepthMm,
            pilotDiameter=evidence.DiameterMm,drilledDepth=evidence.DepthMm,toothSlot=source.FullTopologyIndex,provenFaces=evidence.ProvenFaceIndices,
            originalVolume=feature.Result.VolumeMm3,editedVolume=current.Result.VolumeMm3,labels=labels.Select(l=>new{l.Text,l.MaxWidthMm})});
    }
    if(assets.Count!=0)throw new InvalidOperationException("Drawing assets remain.");
    Console.WriteLine(language+" modeled tooth/drawing/reopen PASS");
}
var app=Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll").Select(p=>new{path=p,sha256=Hash(p)}).ToArray();
var modules=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>Path.GetDirectoryName(m.FileName)==Path.Combine(AppContext.BaseDirectory,"occt"))
    .Select(m=>new{path=m.FileName,sha256=Hash(m.FileName)}).ToArray();
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=true,frozen,applicationPayload=app,nativeModules=modules,rows},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("Frozen modeled thread consumer PASS: three languages, independent tooth proof, retained author/reopen, vector PDF and zero assets.");
