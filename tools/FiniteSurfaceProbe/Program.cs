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

if(args.Length!=3)throw new ArgumentException("Usage: FiniteSurfaceProbe frozen-candidate window-evidence output");
string frozen=Path.GetFullPath(args[0]),evidence=Path.GetFullPath(args[1]),output=Path.GetFullPath(args[2]);
Directory.CreateDirectory(output);var rows=new List<object>();
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
void Progress(string message)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
foreach(string path in Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll"))
    Require(Hash(path)==Hash(Path.Combine(frozen,Path.GetFileName(path))),"Consumer differs from frozen application: "+path);
var kernel=new OcctGeometryKernel(new IsolatedMetricThreadExecutor(Path.Combine(frozen,"Cadoryx.wpf.exe")),new IsolatedDrawingExecutor(Path.Combine(frozen,"Cadoryx.wpf.exe")));
var storage=new CadDocumentStorage(datumResolver:kernel);
foreach(var(language,caption) in new[]{("en-US",DrawingCaptionLanguage.English),("zh-CN",DrawingCaptionLanguage.Chinese),("ja-JP",DrawingCaptionLanguage.Japanese)})
foreach(string kind in new[]{"plane","sphere","cylinder","cut"})
{
    var assets=new MemoryAssetStore();Progress(language+" "+kind+" reopen actual window author");
    using(var loaded=await storage.LoadAsync(Path.Combine(evidence,$"surface-{kind}-{language}.cadoryx"),assets))
    await using(var session=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
    {
        var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);var recipe=(ExtrudeRecipe)feature.Recipe;var options=recipe.Options!;
        Require(options.Termination==ExtrusionTermination.FiniteSurface&&options.SurfaceBranch==ExtrusionSurfaceBranch.Near&&
            options.Operation==(kind=="cut"?ExtrusionOperation.Cut:ExtrusionOperation.Fuse)&&options.Side==(kind=="cut"?ExtrusionSide.Negative:ExtrusionSide.Positive),"Stored controls differ from author intent");
        NativeOracle(recipe,feature.Result,assets,kind,2);
        Guid? drawing=null;
        if(kind=="sphere")
        {
            var sketch=session.Snapshot.Sketches[feature.SketchSource!.SketchId];var radius=new CadParameter(Guid.NewGuid(),"radius",ParameterDimension.Length,"3 mm");
            var book=new ParameterBook([radius],[new(Guid.NewGuid(),radius.Id,ParameterTarget.Sketch(sketch.Id,sketch.Constraints.OfType<RadiusConstraint>().Single().Id))]);
            var sheet=DrawingSheetTemplate.Create("Finite surface termination");sheet=sheet with{Template=sheet.Template! with{Language=caption}};drawing=sheet.Id;
            await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Top",DrawingViewKind.Top,new(100,75),3));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(180,75),3));
            var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(book));var driven=session.Snapshot;feature=driven.Features[feature.Id];recipe=(ExtrudeRecipe)feature.Recipe;
            NativeOracle(recipe,feature.Result,assets,kind,3);
            Require(driven.DrawingSheets[sheet.Id].Views.All(v=>v.StaleReason is null&&v.Strokes.Length>0&&v.Sources.Any(s=>s.Feature==feature.Id&&s.Revision==feature.Result.Revision)),"Linked drawing failed atomic refresh");
            int count=assets.Count;bool refused=false;
            try{await session.ExecuteAsync(new SetParametersCommand(book with{Parameters=[radius with{Expression="6 mm"}]}));}catch(CadValidationException){refused=true;}
            Require(refused&&ReferenceEquals(driven,session.Snapshot)&&assets.Count==count,"Invalid support domain changed author or assets");
            await session.UndoAsync();Require(ReferenceEquals(before,session.Snapshot),"Parameter undo");await session.RedoAsync();Require(ReferenceEquals(driven,session.Snapshot),"Parameter redo");
            TechnicalDrawingPdf.Write(driven,Path.Combine(output,"finite-surface-"+language+".pdf"));
        }
        var accepted=session.Snapshot;int retained=assets.Count;bool branchRejected=false;
        try{await session.ExecuteAsync(new RecomputeCommand(feature.Id,recipe with{Options=recipe.Options! with{SurfaceBranch=ExtrusionSurfaceBranch.Far}}));}
        catch(CadValidationException){branchRejected=true;}
        Require(branchRejected&&ReferenceEquals(accepted,session.Snapshot)&&assets.Count==retained,"Far silently accepted a Near cap or retained assets");
        string path=Path.Combine(output,$"surface-{kind}-{language}.cadoryx");await session.SaveAsync(storage,path);
        using(var reopened=await storage.LoadAsync(path,assets)){reopened.Snapshot.Validate();NativeOracle((ExtrudeRecipe)reopened.Snapshot.Features[feature.Id].Recipe,reopened.Snapshot.Features[feature.Id].Result,assets,kind,kind=="sphere"?3:2);}
        rows.Add(new{language,kind,feature=feature.Id.ToString(),support=options.SupportFace!.FeatureId.ToString(),stop=options.LimitFace!.FeatureId.ToString(),volume=feature.Result.VolumeMm3,drawing});
    }
    Require(assets.Count==0,"Consumer retained assets");Progress(language+" "+kind+" PASS; zero assets");
}
var app=Directory.GetFiles(AppContext.BaseDirectory,"Cadoryx.*.dll").Select(p=>new{path=p,sha256=Hash(p)}).ToArray();
var native=Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>Path.GetDirectoryName(m.FileName)==Path.Combine(AppContext.BaseDirectory,"occt"))
    .Select(m=>new{path=m.FileName,sha256=Hash(m.FileName)}).ToArray();
foreach(var module in native)Require(module.sha256==Hash(Path.Combine(frozen,"occt",Path.GetFileName(module.path))),"Native payload differs from frozen candidate");
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=true,frozen,applicationPayload=app,nativeModules=native,rows},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("Frozen finite surface consumer PASS: twelve actual window authors, independent native volume/start/cap areas and original finite coverage, circle drivers/drawings/PDFs, branch/invalid-domain rejection, undo/redo/reopen and zero assets.");

static void NativeOracle(ExtrudeRecipe recipe,GeometryAssetRef result,IAssetStore assets,string kind,double radius)
{
    static double Volume(Shape s)=>Math.Abs(s.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-10).Properties.Mass);
    static double Area(Shape s){var faces=s.GetFaces();try{return faces.Sum(f=>Math.Abs(f.InspectAdaptiveProperties(InspectionPropertyKind.Area,1e-10).Properties.Mass));}finally{foreach(var face in faces)face.Dispose();}}
    using var shape=OcctGeometryBridge.ReadShape(result,assets);using var source=OcctGeometryBridge.ReadShape(recipe.Options!.Source!,assets);
    using var stopSource=OcctGeometryBridge.ReadShape(recipe.Options.LimitGeometry!,assets);using var stopMap=RepairSnapshot.Create(stopSource);
    using var supportMap=RepairSnapshot.Create(source);
    using var stop=stopMap.CopySubshape(stopMap.Topology.Single(t=>t.Selection.Index==recipe.Options.LimitFace!.FullTopologyIndex).Selection);
    using var support=supportMap.CopySubshape(supportMap.Topology.Single(t=>t.Selection.Index==recipe.Options.SupportFace!.FullTopologyIndex).Selection);
    using var delta=kind=="cut"?source.Cut(shape):shape.Cut(source);using var onStop=delta.Common(stop);using var onStart=delta.Common(support);
    double expected=kind switch
    {
        "sphere"=>500+Math.PI*radius*radius*13-2*Math.PI/3*(216-Math.Pow(36-radius*radius,1.5)),
        "cylinder"=>500+4*(52-(2*Math.Sqrt(32)+36*Math.Asin(1d/3))),"cut"=>1872,_=>612
    };
    double expectedCap=kind switch{"sphere"=>12*Math.PI*(6-Math.Sqrt(36-radius*radius)),"cylinder"=>48*Math.Asin(1d/3),_=>16};
    double expectedStart=kind=="sphere"?Math.PI*radius*radius:16;
    if(!shape.IsValid||shape.GetTopologySummary().UniqueCounts.SolidCount!=1||Math.Abs(Volume(shape)-expected)>1e-5||
        Math.Abs(Area(onStop)-expectedCap)>1e-6||Math.Abs(Area(onStart)-expectedStart)>1e-6)
        throw new InvalidOperationException("Independent native volume or original start/cap area differs for "+kind);
}
