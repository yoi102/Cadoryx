using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using OcctSharp;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class ExtrusionEditorSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;var vm=(MainWindowViewModel)window.DataContext;var markupCulture=CultureInfo.GetCultureInfo(vm.CurrentCultureLCID);
        try
        {
            await Idle();var document=vm.ActiveDocument!;var storage=services.GetRequiredService<IDocumentStorage>();
            var box=new BoxRecipe(10,12,15,RigidTransform3d.Identity);
            await document.Session.ExecuteAsync(new AddBodyCommand(box,"Source"));var source=document.Session.Snapshot.Features.Values.Single();
            document.StartTool("Extrude");document.ProfilePoints.Clear();foreach(var p in new[]{new Point2d(0,0),new Point2d(2,0),new Point2d(2,2),new Point2d(0,2)})document.ProfilePoints.Add(new(p.X,p.Y));
            var baseline=document.Session.Snapshot;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                await Operate(()=>vm.EditExtrusionsCommand.ExecuteAsync(null),async editor=>
                {
                    editor.Activate();await Idle();Check(editor.IsLoaded&&editor.IsVisible,"Extrusion window did not load: "+vm.StatusText);
                    editor.DistanceInput.Value=10;editor.DirectionPicker.SelectedValue=ExtrusionSide.Symmetric;
                    await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,40);Save(editor,Path.Combine(output,$"extrusion-{language}.png"));
                    editor.DirectionPicker.SelectedValue=ExtrusionSide.TwoSides;editor.BackwardInput.Value=3;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,52);
                    editor.DirectionPicker.SelectedValue=ExtrusionSide.Positive;editor.TerminationPicker.SelectedValue=ExtrusionTermination.Plane;editor.LimitZInput.Value=10;editor.LimitRyInput.Value=45;
                    await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,36);
                    editor.LimitZInput.Value=1;await Preview(editor);Check(!editor.Editor.CanConfirm,"Crossing plane rejects confirm");
                    editor.TerminationPicker.SelectedValue=ExtrusionTermination.PlanarFace;await ClickButton(editor.LoadFacesButton);if(editor.Editor.LoadFacesCommand.ExecutionTask is {} load)await load;
                    Check(editor.Editor.SelectedFace is null&&editor.Editor.Faces.Count==6,"Face enumeration never auto-selects");
                    using var shape=OcctGeometryBridge.ReadShape(source.Result,document.Session.Assets);using var map=RepairSnapshot.Create(shape);
                    int top=map.Topology.Single(t=>{if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);return BoxTopology.Matches(face,box,TopologyKind.Face,BoxBoundary.ZMax);}).Selection.Index;
                    editor.FacePicker.SelectedItem=editor.Editor.Faces.Single(f=>f.Limit.Selection.FullTopologyIndex==top);
                    await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,60);
                    Check(ReferenceEquals(baseline,document.Session.Snapshot),"Preview keeps baseline");await ClickButton(editor.CancelButton);
                });
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"Cancel keeps baseline");
            }
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;
            await Operate(()=>vm.EditExtrusionsCommand.ExecuteAsync(null),async editor=>
            {
                editor.OperationPicker.SelectedValue=ExtrusionOperation.Cut;editor.DirectionPicker.SelectedValue=ExtrusionSide.Negative;editor.StartZInput.Value=15;editor.DistanceInput.Value=6;
                await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,1776);await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
            });
            var accepted=document.Session.Snapshot;var feature=accepted.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);Check(accepted.Bodies.Count==1,"Target consumed exactly once");
            await document.Session.UndoAsync();Check(ReferenceEquals(baseline,document.Session.Snapshot),"Single undo");await document.Session.RedoAsync();Check(ReferenceEquals(accepted,document.Session.Snapshot),"Exact redo");
            await Operate(()=>{document.EditFeature(feature.Id);return Task.CompletedTask;},async editor=>
            {
                Check(!editor.Editor.IsNew&&editor.Editor.Operation==ExtrusionOperation.Cut,"Feature tree routes advanced editor");Check(document.SolidHandleRecipe() is null,"Legacy handles cannot discard options");
                editor.DistanceInput.Value=8;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,1768);await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
            });
            await document.Session.SaveAsync(storage,Path.Combine(output,"accepted.cadoryx"));using(var loaded=await storage.LoadAsync(Path.Combine(output,"accepted.cadoryx"),document.Session.Assets))
            {loaded.Snapshot.Validate();Check(loaded.Snapshot.Features[feature.Id].Recipe is ExtrudeRecipe {Options:{Operation:ExtrusionOperation.Cut},Distance:8},"Saved advanced recipe");}
            Check(await vm.CloseAllAsync(),"Close documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Zero assets");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"D1 production advanced extrusion editor en-US/zh-CN/ja-JP; actual controls and preview/confirm/cancel, exact symmetric/two-sided/oblique/face/cut volumes, explicit face choice, invalid limit rejection, single undo/redo, feature tree edit preserving options, save/reopen, zero assets.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markupCulture;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static void Volume(CadDocumentViewModel document,double expected)=>Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-expected)<1e-5),"Exact extrusion preview volume "+expected);
    private static async Task Preview(ExtrusionEditorWindow editor)
    {await ClickButton(editor.PreviewButton);if(editor.Editor.PreviewCommand.ExecutionTask is {} task)await task;else throw new InvalidOperationException("Preview button input did not start its command.");await Idle();}
    private static async Task Operate(Func<Task> open,Func<ExtrusionEditorWindow,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>
        {ExtrusionEditorWindow? editor=null;try{await Idle();editor=Application.Current.Windows.OfType<ExtrusionEditorWindow>().Single();await action(editor);completion.TrySetResult();}catch(Exception ex){completion.TrySetException(ex);}finally{if(editor is {IsVisible:true})editor.Close();}},DispatcherPriority.ApplicationIdle);
        await open();await completion.Task;
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
