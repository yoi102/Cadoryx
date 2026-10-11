using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using Strings=Cadoryx.Lang.Strings.Strings;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static partial class SketchNativeProjectionSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        using var nativeTrace=new TextWriterTraceListener(Path.Combine(output,"native-selection.log"));Trace.Listeners.Add(nativeTrace);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var original=CultureInfo.CurrentUICulture;var main=(MainWindowViewModel)window.DataContext;var markup=CultureInfo.GetCultureInfo(main.CurrentCultureLCID);
        try
        {
            await Idle();var document=main.ActiveDocument!;var session=document.Session;var kernel=services.GetRequiredService<IGeometryKernel>();
            var solver=services.GetRequiredService<ISketchConstraintSolver>();var storage=services.GetRequiredService<IDocumentStorage>();
            await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(5,10,RigidTransform3d.Translate(12,3,0)),"B11 native source"));
            var source=session.Snapshot.Features.Values.Single();
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                Progress(language+": exact native source picker");
                var plane=new RigidTransform3d(new(0,0,25),Quaterniond.FromAxisAngle(new(1,0,0),.4));var sketch=CadSketch.Create(source.PartId,"B11 "+language,plane);var baseline=session.Snapshot;
                CadSketch acceptedDraft=sketch;
                await using(var editor=new SketchEditorViewModel(document,kernel,solver,sketch,true))
                {
                    var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();host.ProjectionSection.IsExpanded=true;host.ProjectNativeEdgeButton.BringIntoView();await Idle();
                    try
                    {
                        await Operate(host,async picker=>
                        {
                            await Pick(picker,source.Id);Check(picker.Editor.CanUse,picker.Editor.Status);
                            Check(Equals(picker.UseProjectionButton.Content,Strings.UseEdgeProjection),"Picker translation");
                            Save(picker,Path.Combine(output,$"picker-{language}.png"));picker.Host.Viewport!.SaveScreenshot(Path.Combine(output,$"edge-{language}.png"));
                            await ClickButton(picker.CancelButton);
                        });
                        Check(ReferenceEquals(sketch,editor.Sketch)&&ReferenceEquals(baseline,session.Snapshot),"Picker cancel changed draft/document");
                        await Operate(host,async picker=>{await Pick(picker,source.Id);await ClickButton(picker.UseProjectionButton);if(picker.Editor.UseCommand.ExecutionTask is {} task)await task;});
                        Check(editor.Sketch.Ellipses.Length==1&&editor.Sketch.NativeProjections.Length==1,"Linked conic was not authored");
                        var linked=editor.Sketch.Ellipses[0];Check(editor.Sketch.Plane==plane,"Oblique author plane changed");
                        Check(Math.Abs(linked.Geometry.Principal().MinorRadius-5*Math.Cos(.4))<1e-7,"Projected minor radius");
                        host.EllipseSection.IsExpanded=true;host.EllipseCenterXInput.BringIntoView();await Idle();
                        host.EllipseCenterXInput.Value=100;await Idle();await ClickButton(host.ApplyEllipseButton);
                        Check(editor.Sketch.Ellipses[0].Geometry==linked.Geometry&&editor.Status==Strings.DetachEllipseBeforeEditing,"Driven conic edit/translation");
                        Save(host,Path.Combine(output,$"reject-{language}.png"));editor.Select(linked.Id);
                        host.OriginZInput.Value=40;await Idle();await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} stalePlane)await stalePlane;
                        Check(!editor.CanConfirm&&ReferenceEquals(baseline,session.Snapshot),"Changed native projection plane was silently accepted");
                        host.ProjectionSection.IsExpanded=true;host.ProjectNativeEdgeButton.BringIntoView();await Idle();
                        await Operate(host,async picker=>{Check(picker.Editor.CanReplace,"Explicit reselection unavailable");picker.ReplaceProjectionInput.IsChecked=true;await Pick(picker,source.Id);await ClickButton(picker.UseProjectionButton);if(picker.Editor.UseCommand.ExecutionTask is {} task)await task;});
                        Check(editor.Sketch.Ellipses[0].Id==linked.Id&&editor.Sketch.NativeProjections[0].TargetPlane.Translation.Z==40,"Reselection did not preserve author identity on new plane");
                        host.DetachProjectionButton.BringIntoView();await Idle();await ClickButton(host.DetachProjectionButton);Check(editor.Sketch.NativeProjections.IsEmpty,"Actual detach button");
                        await ClickButton(host.UndoButton);Check(editor.Sketch.NativeProjections.Length==1,"Draft undo did not restore exact source");editor.Select(linked.Id);
                        host.ToggleConstructionButton.BringIntoView();await Idle();await ClickButton(host.ToggleConstructionButton);Check(!editor.Sketch.Ellipses[0].IsConstruction,"Actual construction toggle");
                        host.ProjectionSection.IsExpanded=true;host.ProjectNativeEdgeButton.BringIntoView();await Idle();
                        await Operate(host,async picker=>{picker.LinkProjectionInput.IsChecked=false;await Pick(picker,source.Id);await ClickButton(picker.UseProjectionButton);if(picker.Editor.UseCommand.ExecutionTask is {} task)await task;});
                        Check(editor.Sketch.Ellipses.Length==2&&editor.Sketch.NativeProjections.Length==1,"Independent projection did not detach its source");
                        editor.Select(linked.Id);host.Canvas.Fit();await Idle();Save(host,Path.Combine(output,$"conic-{language}.png"));
                        await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} preview)await preview;
                        Check(editor.CanConfirm,editor.Status);Check(ReferenceEquals(baseline,session.Snapshot),"Preview published");
                        acceptedDraft=editor.Sketch;await ClickButton(host.CancelButton);await Idle();Check(ReferenceEquals(baseline,session.Snapshot)&&document.PreviewScene is null,"Preview cancel published");
                    }
                    finally{host.Close();await Idle();}
                }
                await using(var confirming=new SketchEditorViewModel(document,kernel,solver,acceptedDraft,true))
                {
                    var host=new SketchEditorWindow(confirming){Owner=window};host.Show();await Idle();
                    try
                    {
                        await ClickButton(host.PreviewButton);if(confirming.PreviewCommand.ExecutionTask is {} again)await again;Check(confirming.CanConfirm,confirming.Status);
                        await ClickButton(host.ConfirmButton);if(confirming.ConfirmCommand.ExecutionTask is {} confirm)await confirm;await Idle();
                        var accepted=session.Snapshot;Check(accepted.Sketches[sketch.Id].Ellipses.Length==2,"Accepted conic data");
                        await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Document undo");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"Document redo");
                    }
                    finally{host.Close();await Idle();}
                }
                Progress(language+": exact conic profile and native storage");
                var committed=session.Snapshot.Sketches[sketch.Id];var id=committed.NativeProjections[0].TargetEntity;
                var reference=SketchProfileReference.CreateMixed(committed,[id]);var recipe=reference.Resolve(session.Snapshot,source.PartId,new ExtrudeRecipe(new([]),4,committed.Plane));
                await session.ExecuteAsync(new AddBodyCommand(recipe,"B11 conic solid "+language,source.PartId,sketchSource:reference));
                var feature=session.Snapshot.Features.Values.Single(f=>f.SketchSource?.SketchId==sketch.Id);double volume=25*Math.PI*Math.Cos(.4)*4;
                Check(Math.Abs(feature.Result.VolumeMm3-volume)<1e-5,"Native projected conic volume");
                await Idle();var viewport=WindowSmokeRunner.Host(document).Viewport!;viewport.FitAll();await Idle();viewport.SaveScreenshot(Path.Combine(output,$"native-{language}.png"));
                string file=Path.Combine(output,$"accepted-{language}.cadoryx");await session.SaveAsync(storage,file);
                using(var loaded=await storage.LoadAsync(file,session.Assets))
                {loaded.Snapshot.Validate();Check(loaded.Snapshot.Sketches[sketch.Id].Ellipses[0]==committed.Ellipses[0]&&loaded.Snapshot.Sketches[sketch.Id].NativeProjections.Length==1,"Exact conic/native source lost on reopen");}
                Progress(language+": rational native freeform production controls");
                await FreeformAsync(window,document,kernel,solver,storage,output,language,source.PartId);
            }
            Check(await main.CloseAllAsync(),"Close all");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Source/preview assets leaked");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"B11 en-US/zh-CN/ja-JP production native edge picker, original BRep identity, exact oblique conic, rational Bezier/quartic and periodic BSpline, full numeric values, linked/independent choice, picker cancellation, protected numeric edit, changed-plane rejection, explicit source reselection, detach/edit/draft undo, construction toggle, preview/cancel/confirm, document undo/redo, native solid, save/reopen and zero remaining assets. Edge input uses the actual viewport PointerPressed/Released API; physical pointer remains separate.");window.CloseAfterSmoke();
        }
        catch(Exception e){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+e);Application.Current.Shutdown(1);}
        finally{nativeTrace.Flush();Trace.Listeners.Remove(nativeTrace);CultureInfo.CurrentUICulture=original;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
        void Progress(string value)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {value}{Environment.NewLine}");
    }
    private static void Check(bool condition,string reason){if(!condition)throw new InvalidOperationException(reason);}
    private static async Task Pick(SketchNativeProjectionWindow picker,FeatureId source)
    {
        picker.NativeSourcePicker.SelectedItem=picker.Editor.Sources.Single(s=>s.Feature==source);await Idle();
        var viewport=picker.Host.Viewport??throw new InvalidOperationException("Native picker viewport is not ready.");viewport.SetProjection(CadProjection.Top);viewport.FitAll();await Idle();
        var at=viewport.WorldToScreen(new(12+5*Math.Cos(.23),3+5*Math.Sin(.23),10));viewport.PointerPressed(0,at.X,at.Y,0);viewport.PointerReleased(0,at.X,at.Y,0);
        var deadline=DateTime.UtcNow.AddSeconds(15);while(!picker.Editor.CanUse){await Idle();if(DateTime.UtcNow>deadline)throw new InvalidOperationException("Native edge pick: "+picker.Editor.Status);}
    }
    private static async Task Operate(SketchEditorWindow parent,Func<SketchNativeProjectionWindow,Task> action)
    {
        var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>
        {
            SketchNativeProjectionWindow? picker=null;
            try
            {
                var deadline=DateTime.UtcNow.AddSeconds(15);
                do{await Idle();picker=Application.Current.Windows.OfType<SketchNativeProjectionWindow>().SingleOrDefault();if(picker is {IsLoaded:true,IsVisible:true,Host.Viewport:not null})break;if(DateTime.UtcNow>deadline)throw new InvalidOperationException("Native picker did not open: "+parent.Editor.Status);}while(true);
                picker.Activate();await Idle();await action(picker);completed.TrySetResult();
            }
            catch(Exception e){completed.TrySetException(e);picker?.Close();}
        });
        await ClickButton(parent.ProjectNativeEdgeButton);if(parent.Editor.ProjectNativeEdgeCommand.ExecutionTask is {} opened)await opened;await completed.Task;
    }
}
