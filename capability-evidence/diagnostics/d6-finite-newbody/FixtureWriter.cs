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
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

internal static class FixtureWriter
{
    static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    static double Expected(string kind)=>kind switch
    {
        "sphere"=>Math.PI*4*12-2*Math.PI/3*(216-Math.Pow(32,1.5)),
        "cylinder"=>4*(48-(2*Math.Sqrt(32)+36*Math.Asin(1d/3))),
        _=>96
    };
    static async Task<int> Main(string[] args)
    {
        Require(args.Length==1&&!Directory.Exists(args[0]),"Fresh fixture evidence directory required.");
        Process.GetCurrentProcess().PriorityClass=ProcessPriorityClass.BelowNormal;
        Directory.CreateDirectory(args[0]);var rows=new List<object>();
        var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage(surfaceExtrusionResolver:kernel);
        foreach(var kind in new[]{"plane","sphere","cylinder"})foreach(bool negative in new[]{false,true})
        {
            string label=kind+(negative?"-negative":"-positive");
            await using(var session=new CadDocumentSession(DocumentSnapshot.Create("D6 "+label),assets,kernel,new InlineSessionDispatcher()))
            {
                await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,5,RigidTransform3d.Identity),"Unconsumed original"));
                var original=session.Snapshot.Bodies.Values.Single();
                using(var limit=kind switch
                {
                    "sphere"=>SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Sphere,new(new(5,5,18),new(1,0,0),new(0,1,0)),new(0,Math.Tau,-Math.PI/2,0),6),
                    "cylinder"=>SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Cylinder,new(new(5,0,18),new(1,0,0),new(0,0,-1)),new(0,Math.PI,0,10),6),
                    _=>SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Plane,new(new(5,5,12),new(1,0,0),new(0,1,0)),new(-5,5,-5,5))
                })
                using(var stored=OcctGeometryBridge.StoreShape(limit,assets))
                    await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Explicit finite limit",original.PartId));
                var feature=session.Snapshot.Features.Values.Single(f=>f.Name=="Explicit finite limit");
                ExactTopologySelection selection;
                using(var shape=OcctGeometryBridge.ReadShape(feature.Result,assets))
                using(var map=RepairSnapshot.Create(shape))
                {
                    var faces=shape.GetFaces();
                    try{Require(faces.Length==1,"Fixture limit must be one exact face.");selection=new(session.Snapshot.Id,feature.Id,feature.Result.Revision,feature.Result.AssetId,map.Fingerprint,RepairSnapshot.FindTopologyIndex(shape,faces[0]),HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion);}
                    finally{foreach(var face in faces)face.Dispose();}
                }
                var frame=negative?new RigidTransform3d(new(0,10,6),Quaterniond.FromAxisAngle(new(1,0,0),Math.PI)):RigidTransform3d.Translate(0,0,6);
                var recipe=new ExtrudeRecipe(kind=="sphere"?SketchProfile.FromCircle(new(5,5),2):new([new(3,3),new(7,3),new(7,7),new(3,7)]),0,frame,
                    new(negative?ExtrusionSide.Negative:ExtrusionSide.Positive,Operation:ExtrusionOperation.NewBody,Termination:ExtrusionTermination.FiniteSurface,LimitFace:selection,LimitGeometry:feature.Result,SurfaceBranch:ExtrusionSurfaceBranch.Near));
                var baseline=session.Snapshot;await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Independent finite material",original.PartId));
                var result=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
                Require(result.Inputs.IsEmpty&&recipe.Options!.Source is null&&recipe.Options.SupportFace is null,"Private support leaked into author.");
                Require(session.Snapshot.Bodies[original.Id]==original,"Original body changed.");
                Require(Math.Abs(result.Result.VolumeMm3-Expected(kind))<1e-5,"Independent analytic fixture volume differs.");
                session.Snapshot.Validate();await kernel.ConfirmFiniteSurfaceExtrusionAsync(recipe,result.Result,assets);
                string path=Path.Combine(args[0],label+".cadoryx");await session.SaveAsync(storage,path);
                using(var loaded=await storage.LoadAsync(path,assets))
                {
                    loaded.Snapshot.Validate();var author=loaded.Snapshot.Features[result.Id];
                    Require(author.Recipe is ExtrudeRecipe saved&&saved.Options==recipe.Options&&saved.Placement==recipe.Placement&&
                        saved.Profile.Circle==recipe.Profile.Circle&&saved.Profile.Points.SequenceEqual(recipe.Profile.Points)&&author.Inputs.IsEmpty,"Saved independent author differs.");
                    Require(Math.Abs(author.Result.VolumeMm3-Expected(kind))<1e-5,"Saved volume differs.");
                }
                rows.Add(new{label,kind,negative,path,sha256=Hash(path),documentId=session.Snapshot.Id,featureId=result.Id,
                    partId=original.PartId,limitFeatureId=feature.Id,expectedVolumeMm3=Expected(kind),
                    frame,side=recipe.Options!.Side.ToString(),branch="Near",capability=CadDocumentStorage.FiniteSurfaceNewBodyCapability,
                    originalBodiesPreserved=true,seedAssetNotAuthored=true,savedReopened=true});
            }
            Require(assets.Count==0&&assets.SizeBytes==0,"Fixture writer retained assets.");
            Console.WriteLine(label+" saved/reopened; analytic volume="+Expected(kind).ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        }
        using var process=Process.GetCurrentProcess();string nativeDirectory=Path.Combine(AppContext.BaseDirectory,"occt");
        var native=process.Modules.Cast<ProcessModule>().Where(m=>string.Equals(Path.GetDirectoryName(m.FileName),nativeDirectory.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))
            .Select(m=>new{name=m.ModuleName,path=m.FileName,sha256=Hash(m.FileName!)}).ToArray();
        Require(native.Length==62,"Actual fixture native binding must contain all62 modules.");
        File.WriteAllText(Path.Combine(args[0],"result.json"),JsonSerializer.Serialize(new{state="SIX_EXACT_D6_WINDOW_FIXTURES_CREATED_REOPENED_PENDING_INDEPENDENT_AUDIT",
            recordedAt=DateTimeOffset.UtcNow,rows,native,assetsRemaining=assets.Count,desktopInputStarted=false,actualWpfWindowAcceptance=false,
            performanceAcceptance=false,productionApplied=false,cleanup=false,AF="ACTIVE",G="USER_DEFERRED"},new JsonSerializerOptions{WriteIndented=true}));
        return 0;
    }
}
