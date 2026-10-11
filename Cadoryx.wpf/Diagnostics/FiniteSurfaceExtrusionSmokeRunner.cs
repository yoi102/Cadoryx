using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using OcctSharp;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class FiniteSurfaceExtrusionSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var main=(MainWindowViewModel)window.DataContext;var original=CultureInfo.CurrentUICulture;var markup=CultureInfo.GetCultureInfo(main.CurrentCultureLCID);
        void Trace(string message)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        try
        {
            await Idle();var storage=services.GetRequiredService<IDocumentStorage>();var solver=services.GetRequiredService<ISketchConstraintSolver>();
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})foreach(string kind in new[]{"plane","sphere","cylinder","cut"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                Trace(language+" "+kind+" seed");
                if(language=="en-US"&&kind=="plane")
                    Check(main.ActiveDocument?.Session.Snapshot.Features.Count==0,"Initial diagnostic document is not empty");
                else
                {
                    Check(await main.CloseAllAsync(),"Close previous saved document");Trace("previous saved documents closed");
                    main.NewCommand.Execute(null);await Idle();Trace("new document created");
                }
                var document=main.ActiveDocument!;var session=document.Session;var assets=session.Assets;double start=kind=="cut"?20:5;
                await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,start,RigidTransform3d.Identity),"Target"));
                Trace("source box ready");
                var target=session.Snapshot.Bodies.Values.Single();var support=Pick(session.Snapshot,target.Producer!.Value,assets,f=>f.GetBoundingBox().Minimum.Z>start-.001);
                using(var stop=kind switch
                {
                    "sphere"=>SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Sphere,new(new(5,5,18),new(1,0,0),new(0,1,0)),new(0,Math.Tau,-Math.PI/2,0),6),
                    "cylinder"=>SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Cylinder,new(new(5,0,18),new(1,0,0),new(0,0,-1)),new(0,Math.PI,0,10),6),
                    _=>SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Plane,new(new(5,5,12),new(1,0,0),new(0,1,0)),new(-5,5,-5,5))
                })
                using(var stored=OcctGeometryBridge.StoreShape(stop,assets))
                    await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Finite stop",target.PartId));
                var limit=session.Snapshot.Features.Values.Single(f=>f.Name=="Finite stop");var stopSelection=Pick(session.Snapshot,limit.Id,assets,_=>true);
                Trace("finite stop ready");
                ExactTopologySelection? smallSelection=null;
                if(kind=="plane")
                {
                    using var small=SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Plane,new(new(5,5,12),new(1,0,0),new(0,1,0)),new(-.5,.5,-.5,.5));
                    using var stored=OcctGeometryBridge.StoreShape(small,assets);
                    await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Small stop",target.PartId));
                    smallSelection=Pick(session.Snapshot,session.Snapshot.Features.Values.Single(f=>f.Name=="Small stop").Id,assets,_=>true);
                }
                SketchProfileReference? circle=null;
                if(kind=="sphere")
                {
                    var draft=new SketchDraft(CadSketch.Create(target.PartId,"Circular profile",RigidTransform3d.Translate(0,0,5)));draft.AddCircle(new(5,5),2);
                    await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));var sketch=session.Snapshot.Sketches[draft.Value.Id];
                    circle=SketchProfileReference.CreateCircle(sketch,sketch.Circles.Single().Id);
                }
                document.StartTool("Extrude");document.SelectedTargetPart=target.PartId;document.ProfilePoints.Clear();
                foreach(var p in new[]{new Point2d(3,3),new(7,3),new(7,7),new(3,7)})document.ProfilePoints.Add(new(p.X,p.Y));
                var baseline=session.Snapshot;double expected=kind switch
                {
                    "sphere"=>500+Math.PI*4*13-2*Math.PI/3*(216-Math.Pow(32,1.5)),
                    "cylinder"=>500+4*(52-(2*Math.Sqrt(32)+36*Math.Asin(1d/3))),"cut"=>1872,_=>612
                };
                Trace("open production editor");
                await Operate(()=>main.EditExtrusionsCommand.ExecuteAsync(null),async (host,closing)=>
                {
                    Trace("production editor action entered");
                    host.OperationPicker.SelectedValue=kind=="cut"?ExtrusionOperation.Cut:ExtrusionOperation.Fuse;
                    host.TargetPicker.SelectedItem=host.Editor.Targets.Single(t=>t.Value==target.Id);
                    host.DirectionPicker.SelectedValue=kind=="cut"?ExtrusionSide.Negative:ExtrusionSide.Positive;
                    host.TerminationPicker.SelectedValue=ExtrusionTermination.FiniteSurface;host.StartZInput.Value=start;
                    if(circle is {} source)host.ProfilePicker.SelectedItem=host.Editor.Profiles.Single(p=>p.Source==source);
                    await ClickButton(host.LoadSurfacesButton);if(host.Editor.LoadFacesCommand.ExecutionTask is {} load)await load;await Idle();
                    Check(host.Editor.SelectedSurface is null&&host.Editor.SelectedSupport is null,"Inventory implicitly chose a reference");
                    host.SupportPicker.SelectedItem=host.Editor.Supports.Single(s=>s.Limit.Selection==support);
                    host.SurfacePicker.SelectedItem=host.Editor.Surfaces.Single(s=>s.Limit.Selection==stopSelection);
                    host.SurfaceBranchPicker.SelectedValue=ExtrusionSurfaceBranch.Near;await Preview(host);
                    Trace("first preview completed: "+host.Editor.Status);
                    Check(host.Editor.CanConfirm,host.Editor.Status);Volume(document,expected);Check(ReferenceEquals(baseline,session.Snapshot),"Preview committed author");
                    host.FeaturePicker.BringIntoView();await Idle();Save(host,Path.Combine(output,$"surface-{kind}-{language}-author.png"));
                    host.SupportPicker.BringIntoView();host.SurfaceBranchPicker.BringIntoView();await Idle();
                    Save(host,Path.Combine(output,$"surface-{kind}-{language}-limits.png"));
                    var viewport=FindViewport(window)?.Viewport??throw new InvalidOperationException("Production viewport missing");viewport.FitAll();await Idle();
                    viewport.SaveScreenshot(Path.Combine(output,$"surface-{kind}-{language}-native.png"));
                    host.SurfaceBranchPicker.SelectedValue=ExtrusionSurfaceBranch.Far;await Idle();
                    Check(!host.Editor.CanConfirm&&document.PreviewScene is null,"Branch change retained candidate");
                    await Preview(host);Check(!host.Editor.CanConfirm&&ReferenceEquals(baseline,session.Snapshot),"Wrong branch published");
                    host.SurfaceBranchPicker.SelectedValue=ExtrusionSurfaceBranch.Near;
                    if(smallSelection is {} small)
                    {
                        host.SurfacePicker.SelectedItem=host.Editor.Surfaces.Single(s=>s.Limit.Selection==small);await Preview(host);
                        Check(!host.Editor.CanConfirm&&ReferenceEquals(baseline,session.Snapshot),"Small finite stop accepted");
                        host.SurfacePicker.SelectedItem=host.Editor.Surfaces.Single(s=>s.Limit.Selection==stopSelection);
                    }
                    await Preview(host);Check(host.Editor.CanConfirm,host.Editor.Status);Volume(document,expected);
                    closing();await ClickButton(host.ConfirmButton);if(host.Editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;
                },Trace);
                var accepted=session.Snapshot;var feature=accepted.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);var options=((ExtrudeRecipe)feature.Recipe).Options!;
                Check(Math.Abs(feature.Result.VolumeMm3-expected)<1e-5&&options.SupportFace==support&&options.LimitFace==stopSelection&&options.SurfaceBranch==ExtrusionSurfaceBranch.Near,"Confirmed author or geometry differs");
                await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Single undo");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"Exact redo");
                await Operate(()=>{document.EditFeature(feature.Id);return Task.CompletedTask;},async (host,closing)=>
                {
                    Check(!host.Editor.IsNew&&host.Editor.SelectedSupport?.Limit.Selection==support&&host.Editor.SelectedSurface?.Limit.Selection==stopSelection,"Tree edit lost exact selections");
                    await Preview(host);Check(host.Editor.CanConfirm,host.Editor.Status);Volume(document,expected);
                    closing();if(kind=="plane")await ClickButton(host.CancelButton);else host.Close();
                },Trace);
                Check(ReferenceEquals(accepted,session.Snapshot),"Cancel/close changed author");
                string path=Path.Combine(output,$"surface-{kind}-{language}.cadoryx");await session.SaveAsync(storage,path);
                using(var loaded=await storage.LoadAsync(path,assets))
                {loaded.Snapshot.Validate();Check(((ExtrudeRecipe)loaded.Snapshot.Features[feature.Id].Recipe).Options==options,"Storage lost exact author");}
                Trace(language+" "+kind+" PASS");
            }
            Check(await main.CloseAllAsync(),"Close all documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Assets remain");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"D5 production finite plane/sphere/cylinder Fuse and negative planar Cut in en-US/zh-CN/ja-JP; actual source/support/stop/branch/profile controls, explicit inventory selection, exact preview volumes, wrong branch and small finite stop rejection, confirm/undo/redo/tree edit/cancel/close/save/reopen, expected close logging and zero assets. Physical input and arbitrary surface geometry separate.");
            window.CloseAfterSmoke();
        }
        catch(Exception error){Trace("FAIL: "+error);await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+error);window.CloseAfterSmoke();}
        finally{CultureInfo.CurrentUICulture=original;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static async Task Preview(ExtrusionEditorWindow host)
    {await ClickButton(host.PreviewButton);if(host.Editor.PreviewCommand.ExecutionTask is {} task)await task;else throw new InvalidOperationException("Preview command not started");await Idle();}
    private static async Task Operate(Func<Task> open,Func<ExtrusionEditorWindow,Action,Task> action,Action<string> trace)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>
        {
            ExtrusionEditorWindow? host=null;bool expectedClose=false;
            try
            {
                await Idle();host=Application.Current.Windows.OfType<ExtrusionEditorWindow>().Single();
                host.Closed+=(_,_)=>trace("editor closed; expected="+expectedClose);
                await action(host,()=>expectedClose=true);completion.TrySetResult();
            }
            catch(Exception error){completion.TrySetException(error);}
            finally{expectedClose=true;if(host is {IsVisible:true})host.Close();}
        },DispatcherPriority.ApplicationIdle);
        await open();await completion.Task;
    }
    private static ExactTopologySelection Pick(Cadoryx.Db.DocumentSnapshot snapshot,FeatureId id,IAssetStore assets,Func<Shape,bool> predicate)
    {
        var feature=snapshot.Features[id];using var shape=OcctGeometryBridge.ReadShape(feature.Result,assets);using var map=RepairSnapshot.Create(shape);var faces=shape.GetFaces();
        try{return new(snapshot.Id,id,feature.Result.Revision,feature.Result.AssetId,map.Fingerprint,RepairSnapshot.FindTopologyIndex(shape,faces.Single(predicate)),HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion);}
        finally{foreach(var face in faces)face.Dispose();}
    }
    private static OcctViewportHost? FindViewport(DependencyObject root)
    {if(root is OcctViewportHost host)return host;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(FindViewport(VisualTreeHelper.GetChild(root,i)) is {} child)return child;return null;}
    private static void Volume(CadDocumentViewModel document,double expected)=>Check(document.PreviewScene?.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-expected)<1e-5)==true,"Exact preview volume "+expected);
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
