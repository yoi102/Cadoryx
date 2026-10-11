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

internal static class ParameterEditorSmokeRunner
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
            await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,10,5,RigidTransform3d.Identity),"Parameter plate"));
            await document.Session.SaveAsync(storage,Path.Combine(output,"baseline.cadoryx"));var baseline=document.Session.Snapshot;var feature=baseline.Features.Values.Single();
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                await Operate(()=>vm.EditParametersCommand.ExecuteAsync(null),async editor=>
                {
                    Progress($"C1 {language}: expression and driving bindings");editor.Activate();await Idle();
                    editor.ParameterNameInput.Text="plate_width";editor.ExpressionInput.Text="40 mm";await ClickButton(editor.ApplyParameterButton);
                    Check(editor.Editor.Parameters.Count==1,"Apply row binding");editor.DrivingPicker.SelectedItem=editor.Editor.Targets.Single(t=>t.Target==ParameterTarget.Feature(feature.Id,"X"));
                    await ClickButton(editor.BindDimensionButton);Check(editor.Editor.Bindings.Count==1,"Actual bind button");
                    editor.ExpressionInput.Text="plate_width + 1 mm";await ClickButton(editor.ApplyParameterButton);await Preview(editor);
                    Check(!editor.Editor.CanConfirm&&editor.Editor.Status.Contains("cycle",StringComparison.OrdinalIgnoreCase),"Cycle refusal");
                    editor.ExpressionInput.Text="40 mm";await ClickButton(editor.ApplyParameterButton);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    Check(ReferenceEquals(baseline,document.Session.Snapshot),"Parameter preview must not commit");
                    Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-2000)<1e-6),"Native parameter preview volume");
                    Check(editor.Editor.Parameters.Single().Value.Contains("40"),"Evaluated parameter row");
                    Save(editor,Path.Combine(output,$"parameters-{language}.png"));
                    editor.ExpressionInput.Text="50 mm";await Idle();Check(!editor.Editor.CanConfirm,"Typed expression invalidates preview");await Preview(editor);Check(!editor.Editor.CanConfirm,"Unapplied input cannot confirm an older row");
                    editor.Close();
                });
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"Closing parameter preview cancels");
            }
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;
            await Operate(()=>vm.EditParametersCommand.ExecuteAsync(null),async editor=>
            {
                editor.ParameterNameInput.Text="plate_width";editor.ExpressionInput.Text="30 mm";await ClickButton(editor.ApplyParameterButton);
                editor.DrivingPicker.SelectedItem=editor.Editor.Targets.Single(t=>t.Target==ParameterTarget.Feature(feature.Id,"X"));await ClickButton(editor.BindDimensionButton);await Preview(editor);
                Check(editor.Editor.CanConfirm,editor.Editor.Status);await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
            });
            var committed=document.Session.Snapshot;Check(Math.Abs(committed.Features[feature.Id].Result.VolumeMm3-1500)<1e-6,"Confirm native volume");
            await document.Session.UndoAsync();Check(ReferenceEquals(baseline,document.Session.Snapshot),"One history undo");await document.Session.RedoAsync();Check(ReferenceEquals(committed,document.Session.Snapshot),"Exact redo");
            await document.Session.SaveAsync(storage,Path.Combine(output,"accepted.cadoryx"));
            Check(await vm.CloseAllAsync(),"Close documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Zero assets");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"C1 production parameter window en-US/zh-CN/ja-JP; expression and target bindings, actual apply/bind/preview/confirm buttons, cyclic refusal, unapplied-input guard, exact native preview/commit volumes, preview cancellation, one undo/redo, saved parameter schema and zero remaining assets.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markupCulture;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static async Task Preview(ParameterEditorWindow editor)
    {await ClickButton(editor.PreviewButton);if(editor.Editor.PreviewCommand.ExecutionTask is {} task)await task;await Idle();}
    private static async Task Operate(Func<Task> open,Func<ParameterEditorWindow,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>
        {
            ParameterEditorWindow? editor=null;
            try{await Idle();editor=Application.Current.Windows.OfType<ParameterEditorWindow>().Single();await action(editor);completion.TrySetResult();}
            catch(Exception ex){completion.TrySetException(ex);}finally{if(editor is {IsVisible:true})editor.Close();}
        },DispatcherPriority.ApplicationIdle);
        await open();await completion.Task;
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
