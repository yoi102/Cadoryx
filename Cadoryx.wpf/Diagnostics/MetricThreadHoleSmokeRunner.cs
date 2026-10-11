using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
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

internal static class MetricThreadHoleSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;var main=(MainWindowViewModel)window.DataContext;var markup=CultureInfo.GetCultureInfo(main.CurrentCultureLCID);
        var proofs=new List<object>();var solids=new List<object>();void Progress(string step)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {step}{Environment.NewLine}");
        try
        {
            await Idle();var doc=main.ActiveDocument!;var storage=services.GetRequiredService<IDocumentStorage>();
            await doc.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(20,20,12,RigidTransform3d.Identity),"Block"));
            await doc.Session.SaveAsync(storage,Path.Combine(output,"baseline.cadoryx"));var baseline=doc.Session.Snapshot;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;Progress(language+" create thread");
                await Operate(()=>main.EditHolesCommand.ExecuteAsync(null),async editor=>
                {
                    Check(editor.IsVisible&&editor.IsLoaded,"Thread editor not loaded");
                    editor.NameInput.Text="M6 LH blind";editor.ThreadInput.IsChecked=true;editor.ThreadNominalPicker.SelectedItem=6;
                    editor.ThreadHandPicker.SelectedValue=ThreadHand.Left;editor.ThroughInput.IsChecked=false;editor.DepthInput.Value=8;editor.ThreadDepthInput.Value=6;
                    Check(editor.Editor.Diameter==5&&!editor.StandardInput.IsEnabled&&!editor.DiameterInput.IsEnabled,"Thread owns pilot and clearance is explicit");
                    await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(ReferenceEquals(baseline,doc.Session.Snapshot),"Preview changed baseline");
                    Check(Math.Abs(doc.PreviewScene!.Items.Single().Geometry.VolumeMm3-(4800-50*Math.PI))<1e-5,"Exact thread pilot preview volume");
                    editor.ThreadInput.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"thread-author-{language}.png"));
                    editor.ThreadDepthInput.Value=9;await Preview(editor);Check(!editor.Editor.CanConfirm,"Overdeep thread confirmed");
                    Save(editor,Path.Combine(output,$"thread-invalid-depth-{language}.png"));
                    editor.ThreadDepthInput.Value=6;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await ClickButton(editor.ConfirmButton);
                    if(editor.Editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;
                });
                var accepted=doc.Session.Snapshot;var feature=accepted.Features.Values.Single(f=>f.Recipe is HoleRecipe);var recipe=(HoleRecipe)feature.Recipe;
                Check(recipe.Thread==MetricThreadHoles.Create(6,ThreadHand.Left,false,6)&&recipe.Diameter==5,"Committed explicit thread intent");
                await doc.Session.UndoAsync();Check(ReferenceEquals(baseline,doc.Session.Snapshot),"Thread single undo");await doc.Session.RedoAsync();Check(ReferenceEquals(accepted,doc.Session.Snapshot),"Thread exact redo");
                await Operate(()=>{doc.EditFeature(feature.Id);return Task.CompletedTask;},async editor=>
                {
                    Check(editor.Editor.UseThread&&editor.Editor.ThreadNominal==6&&editor.Editor.ThreadHand==ThreadHand.Left,"Editor lost thread intent");
                    editor.ThreadHandPicker.SelectedValue=ThreadHand.Right;editor.ThreadDepthInput.Value=5;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    editor.ThreadInput.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"thread-edit-{language}.png"));
                    await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;
                });
                var edited=doc.Session.Snapshot;var actual=(HoleRecipe)edited.Features[feature.Id].Recipe;
                Check(actual.Thread==MetricThreadHoles.Create(6,ThreadHand.Right,false,5),"Thread metadata edit failed");
                Check(edited.Features[feature.Id].Result==feature.Result,"Symbolic edit replaced exact geometry");
                string path=Path.Combine(output,$"thread-{language}.cadoryx");await doc.Session.SaveAsync(storage,path);
                using(var loaded=await storage.LoadAsync(path,doc.Session.Assets))Check(loaded.Snapshot.Features[feature.Id].Recipe==actual,"Reopen lost thread");
                proofs.Add(new{language,thread=actual.Thread,pilotMm=actual.Diameter,drilledDepthMm=actual.Depth,volumeMm3=feature.Result.VolumeMm3,exactGeometryRetained=true});
                await Operate(()=>{doc.EditFeature(feature.Id);return Task.CompletedTask;},async editor=>
                {
                    editor.ThreadInput.IsChecked=false;Check(editor.Editor.Diameter==5&&editor.Editor.CanSetDiameter,"Explicit detach lost pilot");
                    editor.DiameterInput.Value=5.5;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    editor.ThreadInput.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"thread-detach-cancel-{language}.png"));await ClickButton(editor.CancelButton);
                });
                Check(ReferenceEquals(edited,doc.Session.Snapshot)&&doc.PreviewScene is null,"Cancel committed detached thread");
                await doc.Session.UndoAsync();Check(ReferenceEquals(accepted,doc.Session.Snapshot),"Undo symbolic edit");
                await doc.Session.UndoAsync();Check(ReferenceEquals(baseline,doc.Session.Snapshot),"Return to baseline");
            }
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;Progress(language+" create modeled thread");
                await Operate(()=>main.EditHolesCommand.ExecuteAsync(null),async editor=>
                {
                    editor.NameInput.Text="M6 modeled LH";editor.ThreadInput.IsChecked=true;editor.ThreadNominalPicker.SelectedItem=6;
                    editor.ThreadRepresentationPicker.SelectedValue=ThreadHoleRepresentation.Modeled;
                    editor.ThreadHandPicker.SelectedValue=ThreadHand.Left;editor.ThroughInput.IsChecked=false;
                    editor.DepthInput.Value=8;editor.ThreadDepthInput.Value=4.25;await Preview(editor);
                    Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(ReferenceEquals(baseline,doc.Session.Snapshot),"Modeled preview committed");
                    double removed=Math.Tau*4.25*((1d/16+3/Math.Sqrt(3))*(9-6.25)-2/(3*Math.Sqrt(3))*(27-15.625));
                    Check(Math.Abs(doc.PreviewScene!.Items.Single().Geometry.VolumeMm3-(4800-50*Math.PI-removed))<.002,"Modeled groove integral differs");
                    editor.ThreadRepresentationPicker.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"thread-modeled-author-{language}.png"));
                    editor.ThreadDepthInput.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"thread-modeled-depth-{language}.png"));
                    editor.ThreadDepthInput.Value=.5;await Preview(editor);Check(!editor.Editor.CanConfirm,"Sub-pitch modeled depth confirmed");
                    editor.ThreadDepthInput.Value=4.25;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;
                });
                var accepted=doc.Session.Snapshot;var feature=accepted.Features.Values.Single(f=>f.Recipe is HoleRecipe);
                Check(((HoleRecipe)feature.Recipe).Thread==MetricThreadHoles.Create(6,ThreadHand.Left,false,4.25,ThreadHoleRepresentation.Modeled),"Modeled author intent changed");
                await Operate(()=>{doc.EditFeature(feature.Id);return Task.CompletedTask;},async editor=>
                {
                    Check(editor.Editor.ThreadRepresentation==ThreadHoleRepresentation.Modeled,"Editor lost modeled representation");
                    editor.ThreadHandPicker.SelectedValue=ThreadHand.Right;editor.ThreadDepthInput.Value=3.25;await Preview(editor);
                    Check(editor.Editor.CanConfirm,editor.Editor.Status);editor.ThreadRepresentationPicker.BringIntoView();await Idle();
                    Save(editor,Path.Combine(output,$"thread-modeled-edit-{language}.png"));
                    await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;
                });
                var edited=doc.Session.Snapshot;var current=edited.Features[feature.Id];
                Check(current.Result.AssetId!=feature.Result.AssetId&&current.Result.VolumeMm3>feature.Result.VolumeMm3,"Modeled hand/depth edit did not rebuild teeth");
                string path=Path.Combine(output,$"thread-modeled-{language}.cadoryx");await doc.Session.SaveAsync(storage,path);
                using(var loaded=await storage.LoadAsync(path,doc.Session.Assets))Check(loaded.Snapshot.Features[feature.Id].Recipe==current.Recipe,"Native replay reopen lost modeled author");
                solids.Add(new{language,thread=((HoleRecipe)current.Recipe).Thread,current.Result.VolumeMm3,nativeReplayedOnReopen=true});
                await doc.Session.UndoAsync();Check(ReferenceEquals(accepted,doc.Session.Snapshot),"Modeled edit undo");
                await doc.Session.RedoAsync();Check(ReferenceEquals(edited,doc.Session.Snapshot),"Modeled edit redo");
                await doc.Session.UndoAsync();await doc.Session.UndoAsync();Check(ReferenceEquals(baseline,doc.Session.Snapshot),"Modeled creation undo");
            }
            await File.WriteAllTextAsync(Path.Combine(output,"native-pilots.json"),JsonSerializer.Serialize(proofs,new JsonSerializerOptions{WriteIndented=true}));
            await File.WriteAllTextAsync(Path.Combine(output,"native-threads.json"),JsonSerializer.Serialize(solids,new JsonSerializerOptions{WriteIndented=true}));
            await doc.Session.SaveAsync(storage,Path.Combine(output,"baseline.cadoryx"));Check(await main.CloseAllAsync(),"Close thread document");
            await ((App)Application.Current).StopRecoveryAsync();await Idle();Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Thread assets remain");
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"C5 symbolic and modeled metric threads: three-language actual controls; explicit pilot/pitch/hand/depth/representation; invalid depth rejection; preview/commit/undo/redo; geometry-preserving symbolic edit and rebuilding modeled edit; independent groove volume; native replay on save/reopen; detach cancellation; zero assets. Physical input separately pending.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);window.CloseAfterSmoke();}
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static async Task Preview(HoleEditorWindow editor)
    {await ClickButton(editor.PreviewButton);Check(editor.Editor.PreviewCommand.ExecutionTask is not null,"Preview control did not execute");await editor.Editor.PreviewCommand.ExecutionTask!;await Idle();}
    private static async Task Operate(Func<Task> open,Func<HoleEditorWindow,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>
        {HoleEditorWindow? editor=null;try{await Idle();editor=Application.Current.Windows.OfType<HoleEditorWindow>().Single();await action(editor);completion.TrySetResult();}catch(Exception ex){completion.TrySetException(ex);}finally{if(editor is {IsVisible:true})editor.Close();}},DispatcherPriority.ApplicationIdle);
        await open();await completion.Task;
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
