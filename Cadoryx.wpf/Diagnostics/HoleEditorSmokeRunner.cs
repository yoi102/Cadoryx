using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class HoleEditorSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;var vm=(MainWindowViewModel)window.DataContext;var markupCulture=CultureInfo.GetCultureInfo(vm.CurrentCultureLCID);
        void Progress(string step)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {step}{Environment.NewLine}");
        try
        {
            await Idle();var document=vm.ActiveDocument!;var storage=services.GetRequiredService<IDocumentStorage>();
            await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Source"));
            await document.Session.SaveAsync(storage,Path.Combine(output,"baseline.cadoryx"));var baseline=document.Session.Snapshot;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                await Operate(()=>vm.EditHolesCommand.ExecuteAsync(null),async editor=>
                {
                    Progress($"C4 {language}: hole types and standard");editor.Activate();await Idle();Check(editor.IsLoaded&&editor.IsVisible,"Window did not load: "+vm.StatusText);
                    Check(editor.Editor.OriginZ==15&&editor.Editor.U==5&&editor.Editor.V==6,"Authored box default entry plane");
                    Save(editor,Path.Combine(output,$"hole-initial-{language}.png"));
                    editor.PreviewButton.Click+=(_,_)=>Progress("Preview Click event");
                    editor.NameInput.Text="Hole";await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,1800-15*Math.PI);
                    Check(ReferenceEquals(baseline,document.Session.Snapshot),"Preview keeps baseline");
                    editor.HeadPicker.SelectedValue=HoleHead.Counterbore;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,1800-21*Math.PI);
                    editor.HeadPicker.SelectedValue=HoleHead.Countersink;editor.HeadDiameterInput.Value=6;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,1800-65*Math.PI/3);
                    editor.DiameterInput.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"hole-{language}.png"));
                    editor.StandardInput.IsChecked=true;Check(!editor.Editor.CanConfirm,"Standard invalidates preview");Check(Math.Abs(editor.Editor.Diameter-3.4)<1e-8,"M3 normal clearance");
                    editor.NominalPicker.SelectedItem=4;editor.FitPicker.SelectedValue=ClearanceFit.Close;Check(Math.Abs(editor.Editor.Diameter-4.3)<1e-8,"M4 close clearance");
                    await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);editor.StandardInput.IsChecked=false;Check(editor.Editor.Diameter==4.3,"Custom detach retains diameter");
                    editor.HeadPicker.SelectedValue=HoleHead.Plain;editor.ThroughInput.IsChecked=false;editor.DepthInput.Value=100;
                    await Preview(editor);Check(!editor.Editor.CanConfirm,"Overdeep blind input rejects confirm");editor.Close();
                });
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"Closing cancels preview");
            }
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;
            await Operate(()=>vm.EditHolesCommand.ExecuteAsync(null),async editor=>
            {
                editor.NameInput.Text="Blind";editor.ThroughInput.IsChecked=false;editor.DepthInput.Value=6;await Preview(editor);
                Check(editor.Editor.CanConfirm,editor.Editor.Status);await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
            });
            var committed=document.Session.Snapshot;var feature=committed.Features.Values.Single(f=>f.Recipe is HoleRecipe);
            Check(Math.Abs(committed.Bodies.Values.Single().Geometry.VolumeMm3-(1800-6*Math.PI))<1e-5,"Commit exact blind hole");
            await document.Session.UndoAsync();Check(ReferenceEquals(baseline,document.Session.Snapshot),"Single undo");await document.Session.RedoAsync();Check(ReferenceEquals(committed,document.Session.Snapshot),"Exact redo");
            await Operate(()=>{document.EditFeature(feature.Id);return Task.CompletedTask;},async editor=>
            {
                Check(!editor.Editor.IsNew&&editor.Editor.SourceDescription=="Source","Feature tree routes existing hole editor");editor.DiameterInput.Value=3;editor.DepthInput.Value=8;
                await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
            });
            var edited=document.Session.Snapshot;Check(Math.Abs(edited.Bodies.Values.Single().Geometry.VolumeMm3-(1800-18*Math.PI))<1e-5,"Edited dimensions use source solid");
            await document.Session.SaveAsync(storage,Path.Combine(output,"accepted.cadoryx"));using(var loaded=await storage.LoadAsync(Path.Combine(output,"accepted.cadoryx"),document.Session.Assets)){loaded.Snapshot.Validate();Check(loaded.Snapshot.Features[feature.Id].Recipe==edited.Features[feature.Id].Recipe,"Persistent hole recipe");}
            Check(await vm.CloseAllAsync(),"Close documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Zero assets");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"C4 production hole editor en-US/zh-CN/ja-JP; actual preview/confirm and input controls; exact through/blind/counterbore/countersink volumes, authored entry frame, clearance catalog/custom detach, input invalidation, overdeep rejection, close-cancel, single undo/redo, feature-tree edit, save/reopen and zero assets.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markupCulture;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static void Volume(CadDocumentViewModel document,double expected)=>Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-expected)<1e-5),"Exact hole preview volume");
    private static async Task Preview(HoleEditorWindow editor)
    {
        await ClickButton(editor.PreviewButton);if(editor.Editor.PreviewCommand.ExecutionTask is {} task)await task;await Idle();
        if(editor.Editor.PreviewCommand.ExecutionTask is null)
        {
            var point=editor.PreviewButton.PointToScreen(new(editor.PreviewButton.ActualWidth/2,editor.PreviewButton.ActualHeight/2));
            GetCursorPos(out var cursor);var foreground=GetForegroundWindow();var at=WindowFromPoint(new(){X=(int)point.X,Y=(int)point.Y});
            var handle=new System.Windows.Interop.WindowInteropHelper(editor).Handle;GetWindowRect(handle,out var rect);GetWindowThreadProcessId(foreground,out uint pid);GetWindowThreadProcessId(at,out uint hitPid);
            _=DwmGetWindowAttribute(handle,14,out int cloaked,4);
            throw new InvalidOperationException(
                $"Active={editor.IsActive}, Enabled={editor.PreviewButton.IsEnabled}, Point={point}, Cursor={cursor.X},{cursor.Y}, Foreground={foreground}, AtPoint={at}, EditorHandle={handle}, DirectlyOver={System.Windows.Input.Mouse.DirectlyOver}, Rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}, ForegroundPID={pid}, AtPID={hitPid}, PID={Environment.ProcessId}, NativeVisible={IsWindowVisible(handle)}, Cloaked={cloaked}, Topmost={editor.Topmost}");
        }
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativePoint {public int X,Y;}
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativeRect {public int Left,Top,Right,Bottom;}
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(nint window,out NativeRect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window,int attribute,out int value,int size);
    private static async Task Operate(Func<Task> open,Func<HoleEditorWindow,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>
        {HoleEditorWindow? editor=null;try{await Idle();editor=Application.Current.Windows.OfType<HoleEditorWindow>().Single();await action(editor);completion.TrySetResult();}catch(Exception ex){completion.TrySetException(ex);}finally{if(editor is {IsVisible:true})editor.Close();}},DispatcherPriority.ApplicationIdle);
        await open();await completion.Task;
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
