using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using Cadoryx.wpf.Controls;

namespace Cadoryx.wpf.Diagnostics;

/// <summary>Opt-in owned desktop benchmark. CPU submission timings are not GPU/display latency.</summary>
internal static class M6BenchmarkRunner
{
    internal static async Task RunAsync(MainWindow window,string input,string output,int cycles)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);var reports=new List<object>();
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        try
        {
            if(cycles is <2 or >10000)throw new ArgumentOutOfRangeException(nameof(cycles));
            await ((App)Application.Current).StopRecoveryAsync();
            using var assets=new DiskAssetStore(Path.Combine(output,"cache"));var kernel=new OcctGeometryKernel();
            var storage=new CadDocumentStorage(deferGeometryAssets:true);
            var grid=new Grid();grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new());window.Content=grid;
            using var primary=new OcctViewportHost(assets);using var secondary=new OcctViewportHost(assets);Grid.SetColumn(secondary,1);
            grid.Children.Add(primary);grid.Children.Add(secondary);await Idle();
            var first=primary.Viewport??throw new InvalidOperationException("Primary viewport unavailable.");
            var second=secondary.Viewport??throw new InvalidOperationException("Secondary viewport unavailable.");
            var empty=new CadScene(DocumentId.New(),DocumentStateId.New(),[]);
            await using(var fixture=new CadDocumentSession(DocumentSnapshot.Create("Exact part"),assets,kernel,new InlineSessionDispatcher()))
            {
                await fixture.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));
                await storage.SaveAsync(fixture.Snapshot,assets,Path.Combine(output,"small.cadoryx"));
                var root=(AssemblyDefinition)fixture.Snapshot.Definitions[fixture.Snapshot.RootAssemblyId];var slot=root.Children.Single();
                var repeated=fixture.Snapshot with{Name="Synthetic repeated 1000",Definitions=fixture.Snapshot.Definitions.SetItem(root.Id,root with{
                    Children=[..Enumerable.Range(0,1000).Select(i=>slot with{Id=ComponentSlotId.New(),Name="Box "+i,
                        LocalTransform=RigidTransform3d.Translate(i%40*15,i/40*25,0)})]})};
                await storage.SaveAsync(repeated,assets,Path.Combine(output,"repeated-1000.cadoryx"));
            }
            var tiers=new List<(string Tier,string Path)>{("small-exact",Path.Combine(output,"small.cadoryx")),("synthetic-1000",Path.Combine(output,"repeated-1000.cadoryx"))};
            tiers.AddRange(input.Split('|').Select((path,index)=>("user-model-"+(index+1),Path.GetFullPath(path))));
            foreach(var (tier,path) in tiers)
            {
                await File.WriteAllTextAsync(Path.Combine(output,"progress.txt"),tier+": loading");
                string inputHash;using(var beforeStream=File.OpenRead(path))inputHash=Convert.ToHexStringLower(await SHA256.HashDataAsync(beforeStream));
                var timer=Stopwatch.StartNew();
                using var loaded=Path.GetExtension(path).Equals(".cadoryx",StringComparison.OrdinalIgnoreCase)?await storage.LoadAsync(path,assets):await kernel.ImportAsync(path,assets);
                double loadMs=timer.Elapsed.TotalMilliseconds;int deferredAtOpen=assets.DeferredCount;
                var scene=CadScene.FromDocument(loaded.Snapshot);var frames=new List<double>();var lifecycle=new List<object>();
                var process=Process.GetCurrentProcess();process.Refresh();long baseline=process.PrivateMemorySize64;
                timer.Restart();
                first.BeginProgressiveScene(scene);
                if(scene.Items.Length>0)first.RestoreCamera(SceneEnvelope.FitVisible(first.CaptureCamera(),scene.Items));
                int submitted=first.AppendProgressiveScene(32,TimeSpan.FromMilliseconds(35),redraw:true);
                double firstVisibleSubmitMs=timer.Elapsed.TotalMilliseconds;int firstBatchItems=submitted;
                var inputServiceMs=new List<double>();
                while(submitted<scene.Items.Length)
                {
                    submitted=first.AppendProgressiveScene(32,TimeSpan.FromMilliseconds(35),redraw:false);
                    long queued=Stopwatch.GetTimestamp();double servicedMs=-1;
                    _=Application.Current.Dispatcher.BeginInvoke(()=>servicedMs=Stopwatch.GetElapsedTime(queued).TotalMilliseconds,DispatcherPriority.Input);
                    await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Background);
                    if(servicedMs<0)throw new InvalidOperationException("Input priority work was starved by progressive submission.");
                    inputServiceMs.Add(servicedMs);
                }
                first.CompleteProgressiveScene();double firstSubmitMs=timer.Elapsed.TotalMilliseconds;
                var initialCamera=first.CaptureCamera();
                timer.Restart();first.SaveScreenshot(Path.Combine(output,tier+".png"));double captureMs=timer.Elapsed.TotalMilliseconds;
                second.SetScene(scene);second.SetProjection(CadProjection.Top);second.FitAll();
                int expectedContexts=scene.Items.Where(i=>i.Geometry.Source is not null).Select(i=>(i.Geometry.Source!.ContextAssetId,i.Geometry.Source.Format)).Distinct().Count();
                if(first.NativeXdeContextCount!=expectedContexts||second.NativeXdeContextCount!=expectedContexts)
                    throw new InvalidOperationException("XDE context sharing does not match the unique source catalog.");
                for(int frame=0;frame<60;frame++)
                {
                    var camera=first.CaptureCamera();var turn=Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/180);
                    timer.Restart();first.RestoreCamera(camera with{Eye=camera.Target+turn.Rotate(camera.Eye-camera.Target),Up=turn.Rotate(camera.Up)});first.Redraw();frames.Add(timer.Elapsed.TotalMilliseconds);
                    await Task.Delay(16);
                }
                double? nativeFitOrbitMedianMs=null,nativeFitOrbitP95Ms=null;
                CadCamera? nativeFitCamera=null;
                if(scene.Items.Length>=300)
                {
                    first.FitAll();nativeFitCamera=first.CaptureCamera();var fittedFrames=new List<double>();
                    for(int frame=0;frame<60;frame++)
                    {
                        var camera=first.CaptureCamera();var turn=Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/180);
                        timer.Restart();first.RestoreCamera(camera with{Eye=camera.Target+turn.Rotate(camera.Eye-camera.Target),Up=turn.Rotate(camera.Up)});
                        first.Redraw();fittedFrames.Add(timer.Elapsed.TotalMilliseconds);await Task.Delay(16);
                    }
                    fittedFrames.Sort();nativeFitOrbitMedianMs=fittedFrames[fittedFrames.Count/2];nativeFitOrbitP95Ms=fittedFrames[(int)(fittedFrames.Count*.95)];
                }
                for(int cycle=0;cycle<cycles;cycle++)
                {
                    first.SetScene(empty);second.SetScene(empty);
                    if(first.NativeGeometryCount!=0||second.NativeGeometryCount!=0||first.NativeXdeContextCount!=0||second.NativeXdeContextCount!=0||first.DimensionCount!=0||second.DimensionCount!=0)
                        throw new InvalidOperationException("Native scene resource owners remain after clear.");
                    timer.Restart();first.SetScene(scene);second.SetScene(scene);first.FitAll();second.FitAll();first.Redraw();second.Redraw();
                    double rebuild=timer.Elapsed.TotalMilliseconds;
                    if(cycle%3==0){window.Width=1100+cycle%2*120;window.Height=740+cycle%2*60;await Idle();}
                    process.Refresh();lifecycle.Add(new{cycle=cycle+1,rebuildMs=rebuild,privateBytes=process.PrivateMemorySize64,handles=process.HandleCount,
                        primaryGeometry=first.NativeGeometryCount,secondaryGeometry=second.NativeGeometryCount,primaryXdeContexts=first.NativeXdeContextCount,secondaryXdeContexts=second.NativeXdeContextCount});
                    await File.AppendAllTextAsync(Path.Combine(output,"lifecycle.jsonl"),JsonSerializer.Serialize(new{tier,cycle=cycle+1,utc=DateTimeOffset.UtcNow,
                        privateBytes=process.PrivateMemorySize64,handles=process.HandleCount,rebuildMs=rebuild})+Environment.NewLine);
                    if(cycle%10==0)await File.WriteAllTextAsync(Path.Combine(output,"progress.txt"),$"{tier}: {cycle+1}/{cycles}");
                }
                long faces=0;foreach(var geometry in scene.Items.Select(i=>i.Geometry).DistinctBy(g=>g.AssetId))
                {using var shape=OcctGeometryBridge.ReadShape(geometry,assets);var sub=shape.GetSubShapes(OcctSharp.ShapeKind.Face);faces+=sub.Length;foreach(var face in sub)face.Dispose();}
                process.Refresh();frames.Sort();inputServiceMs.Sort();var dpi=VisualTreeHelper.GetDpi(window);
                using var hashStream=File.OpenRead(path);string hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(hashStream));
                if(inputHash!=hash)throw new IOException("Benchmark input changed while running: "+path);
                reports.Add(new{tier,path,sourceSha256=hash,sourceBytes=new FileInfo(path).Length,loadMs,deferredAtOpen,
                    firstSubmitMs,firstVisibleSubmitMs,firstBatchItems,inputServiceP95Ms=inputServiceMs.Count==0?0:inputServiceMs[(int)(inputServiceMs.Count*.95)],
                    firstScreenshotMs=captureMs,visibleInstances=scene.Items.Length,uniqueGeometryFaces=faces,
                    scale=DocumentScaleReport.Measure(loaded.Snapshot,assets),interactionCpuMedianMs=frames[frames.Count/2],interactionCpuP95Ms=frames[(int)(frames.Count*.95)],
                    initialCamera,nativeFitCamera,nativeFitOrbitMedianMs,nativeFitOrbitP95Ms,
                    privateBytesBefore=baseline,privateBytesAfter=process.PrivateMemorySize64,peakWorkingSetBytes=process.PeakWorkingSet64,dpiX=dpi.PixelsPerInchX,dpiY=dpi.PixelsPerInchY,lifecycle});
                await File.WriteAllTextAsync(Path.Combine(output,tier+"-report.json"),JsonSerializer.Serialize(reports[^1],new JsonSerializerOptions{WriteIndented=true}));
                first.SetScene(empty);second.SetScene(empty);
                if(tier=="small-exact")
                {
                    first.BeginProgressiveScene(scene);
                    first.AppendProgressiveScene(1,TimeSpan.FromMilliseconds(35),redraw:true);
                    first.AbortProgressiveScene();
                    if(first.VisibleBodyCount!=0||first.NativeGeometryCount!=0||first.NativeXdeContextCount!=0)
                        throw new InvalidOperationException("Cancelled progressive scene retained native resources.");
                    first.BeginProgressiveScene(scene);
                    first.AppendProgressiveScene(1,TimeSpan.FromMilliseconds(35),redraw:false);
                    first.SetScene(empty);
                    if(first.VisibleBodyCount!=0||first.NativeGeometryCount!=0)
                        throw new InvalidOperationException("Scene replacement did not cancel an in-flight progressive scene.");
                }
            }
            grid.Children.Clear();primary.Dispose();secondary.Dispose();await Idle();
            if(assets.Count!=0)throw new InvalidOperationException("Assets remain after benchmark: "+assets.Count);
            listener.Flush();bindings.Flush();if(new FileInfo(Path.Combine(output,"bindings.log")).Length>0)throw new InvalidOperationException("WPF binding errors recorded.");
            var report=new{schemaVersion=1,recordedUtc=DateTimeOffset.UtcNow,kernel=kernel.Version,os=RuntimeInformation.OSDescription,
                architecture=RuntimeInformation.ProcessArchitecture.ToString(),runtime=RuntimeInformation.FrameworkDescription,
                cpu=Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),logicalProcessors=Environment.ProcessorCount,remoteSession=SystemParameters.IsRemoteSession,cycles,reports,
                limitations="The user-model tier is this file only, not proof for all large assemblies. Repeated tier is synthetic. First-visible and full CPU submit timings are not GPU/display latency; PNG capture includes encoding/IO. Memory and handles are observed trends, not a proof of absence of all leaks. This host DPI is recorded; physical mixed-DPI and RDP transitions require a separate interactive run."};
            await File.WriteAllTextAsync(Path.Combine(output,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: three labeled tiers, native first-frame capture, 60 orbit submissions each, repeated dual-viewport rebuild/resize, zero owned geometry after clears, zero assets after unload.");
            window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static async Task Idle(){await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(120);}
}
