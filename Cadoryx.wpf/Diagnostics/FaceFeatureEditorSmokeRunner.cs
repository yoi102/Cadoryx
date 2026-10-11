using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class FaceFeatureEditorSmokeRunner
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
            await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Box"));var source=document.Session.Snapshot.Features.Values.Single();
            var datum=new CadDatumPlane(Guid.NewGuid(),source.PartId,"Neutral",DatumPlaneKind.Offset,DatumPlaneSource.Base(DocumentWorkPlaneKind.XY),Value:0);
            await document.Session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Planes=System.Collections.Immutable.ImmutableDictionary<Guid,CadDatumPlane>.Empty.Add(datum.Id,datum)}));
            var baseline=document.Session.Snapshot;
            async Task ConfigureShell(FaceFeatureEditorWindow editor)
            {
                Check(editor.Editor.SelectedSource is null&&!editor.Editor.Faces.Any(),"No implicit source or face");editor.SourcePicker.SelectedItem=editor.Editor.Sources.Single(s=>s.Feature==source.Id);await Idle();
                await Pick(editor,CadProjection.Top,new(5,6,15));Check(editor.Editor.ClosingFaces.Count==1,"Native viewport exact top face");editor.ThicknessInput.Value=1;
            }
            async Task ConfigureDraft(FaceFeatureEditorWindow editor)
            {
                editor.SourcePicker.SelectedItem=editor.Editor.Sources.Single(s=>s.Feature==source.Id);await Idle();
                await Pick(editor,CadProjection.Left,new(0,6,7.5));Check(editor.Editor.Programs.Count==1,"Native viewport exact left face");editor.AngleInput.Value=.05*180/Math.PI;
                await Pick(editor,CadProjection.Right,new(10,6,7.5));Check(editor.Editor.Programs.Count==2,"Native viewport exact right face");editor.AngleInput.Value=.1*180/Math.PI;await Idle();
            }
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;Progress(language+" shell");
                await Operate(()=>vm.ShellCommand.ExecuteAsync(null),async editor=>
                {
                    await ConfigureShell(editor);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,680);editor.Host.Viewport!.SetProjection(CadProjection.Axonometric);editor.Host.Viewport.FitAll();await Idle();Save(editor,Path.Combine(output,$"shell-{language}.png"));editor.Host.Viewport.SaveScreenshot(Path.Combine(output,$"shell-preview-{language}.png"));
                    editor.ThicknessInput.Value=6;Check(!editor.Editor.CanConfirm,"Thickness invalidates");await Preview(editor);Check(!editor.Editor.CanConfirm,"Overthick rejection");
                    editor.ThicknessInput.Value=1;await ClickButton(editor.ClearFacesButton);Check(!editor.Editor.Faces.Any(),"Actual clear faces");await Preview(editor);Check(!editor.Editor.CanConfirm,"Empty faces rejected");
                    await Pick(editor,CadProjection.Top,new(5,6,15));await Pick(editor,CadProjection.Bottom,new(5,6,0));Check(editor.Editor.ClosingFaces.Count==2,"Multiple explicit closing faces");
                    await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,600);await ClickButton(editor.RemoveFaceButton);Check(editor.Editor.ClosingFaces.Count==1,"Actual remove selected face");
                    editor.ThicknessInput.Value=1;await ClickButton(editor.OutwardCheck);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(((ShellRecipe)editor.Editor.BuildRecipe()).Outward,"Actual outward toggle");
                    await ClickButton(editor.CancelButton);
                });
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"Shell cancel baseline");Progress(language+" draft");
                await Operate(()=>vm.DraftCommand.ExecuteAsync(null),async editor=>
                {
                    await ConfigureDraft(editor);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,1800-12*15*15*(Math.Tan(.05)+Math.Tan(.1))/2);
                    editor.Host.Viewport!.SetProjection(CadProjection.Axonometric);editor.Host.Viewport.FitAll();await Idle();Save(editor,Path.Combine(output,$"draft-{language}.png"));editor.Host.Viewport.SaveScreenshot(Path.Combine(output,$"draft-preview-{language}.png"));
                    editor.PullZInput.Value=0;Check(!editor.Editor.CanConfirm,"Direction invalidates");await Preview(editor);Check(!editor.Editor.CanConfirm,"Zero direction rejected");editor.PullZInput.Value=1;
                    editor.AngleInput.Value=-.1*180/Math.PI;editor.PropagationPicker.SelectedValue=FaceDraftPropagation.TangentChain;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,1800-12*15*15*(Math.Tan(.05)-Math.Tan(.1))/2);
                    editor.NeutralZInput.Value=2;editor.NeutralRxInput.Value=10;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    editor.NeutralRxInput.Value=0;editor.NeutralZInput.Value=0;editor.AngleInput.Value=.1*180/Math.PI;editor.PropagationPicker.SelectedValue=FaceDraftPropagation.SelectedFaceOnly;
                    editor.DatumPicker.SelectedItem=editor.Editor.Datums.Single();await ClickButton(editor.LinkDatumButton);Check(!editor.Editor.SelectedProgram!.CanSetNeutral,"Linked plane locked");var linked=editor.Editor.SelectedProgram.Value;
                    await ClickButton(editor.DetachDatumButton);Check(editor.Editor.SelectedProgram.CanSetNeutral&&editor.Editor.SelectedProgram.Value.NeutralPlane==linked.NeutralPlane,"Detach preserves exact frame");
                    editor.ProgramList.SelectedIndex=0;var id=editor.Editor.SelectedProgram!.Id;await ClickButton(editor.ReplaceFaceButton);await Pick(editor,CadProjection.Left,new(0,6,7.5));Check(editor.Editor.SelectedProgram.Id==id&&editor.Editor.Programs.Count==2,"Replacement retains program identity");
                    await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);editor.Close();
                });
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"Draft close baseline");
            }
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;Progress("commit/edit/roundtrip");
            await Operate(()=>vm.DraftCommand.ExecuteAsync(null),async editor=>{await ConfigureDraft(editor);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await Confirm(editor);});
            var accepted=document.Session.Snapshot;var draftFeature=accepted.Features.Values.Single(f=>f.Recipe is DraftRecipe);var recipe=(DraftRecipe)draftFeature.Recipe;
            await document.Session.UndoAsync();Check(ReferenceEquals(baseline,document.Session.Snapshot),"Single undo");await document.Session.RedoAsync();Check(ReferenceEquals(accepted,document.Session.Snapshot),"Exact redo");
            await Operate(()=>{document.EditFeature(draftFeature.Id);return Task.CompletedTask;},async editor=>
            {Check(editor.Editor.IsEditing&&!editor.SourcePicker.IsEnabled,"Feature tree route");Check(((DraftRecipe)editor.Editor.BuildRecipe()).Programs.SequenceEqual(recipe.Programs),"All exact programs preserved");await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await ClickButton(editor.CancelButton);});
            await document.Session.SaveAsync(storage,Path.Combine(output,"accepted.cadoryx"));using(var loaded=await storage.LoadAsync(Path.Combine(output,"accepted.cadoryx"),document.Session.Assets))
            {loaded.Snapshot.Validate();Check(((DraftRecipe)loaded.Snapshot.Features[draftFeature.Id].Recipe).Programs.SequenceEqual(recipe.Programs),"Stored exact programs");}
            await document.Session.UndoAsync();await Operate(()=>vm.ShellCommand.ExecuteAsync(null),async editor=>{await ConfigureShell(editor);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await Confirm(editor);});
            var shell=document.Session.Snapshot.Features.Values.Single(f=>f.Recipe is ShellRecipe);
            await Operate(()=>{document.EditFeature(shell.Id);return Task.CompletedTask;},async editor=>{Check(editor.Editor.ClosingFaces.Count==1,"Shell tree complete recipe");editor.ThicknessInput.Value=.5;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,364.5);await Confirm(editor);});
            await document.Session.SaveAsync(storage,Path.Combine(output,"accepted-shell.cadoryx"));
            Progress("cleanup");Check(await vm.CloseAllAsync(),"Close documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Zero assets");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"D3 production shell/draft windows en-US/zh-CN/ja-JP; native viewport API face picking (desktop pointer NOT VERIFIED), actual thickness/direction/angle/pull/datum/replace/preview/confirm/cancel controls, exact native volumes, atomic failure and close, stable complete recipe tree editing, single undo/redo, storage and zero assets.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markupCulture;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static async Task Pick(FaceFeatureEditorWindow editor,CadProjection projection,Vector3d point)
    {
        await Idle();
        var host=editor.Host??throw new InvalidOperationException("Editor host not initialized");
        var viewport=host.Viewport??throw new InvalidOperationException("Native viewport not ready: "+editor.Editor.Status);
        viewport.SetProjection(projection);
        viewport.FitAll();
        await Idle();
        var at=viewport.WorldToScreen(point);viewport.PointerPressed(0,at.X,at.Y,0);viewport.PointerReleased(0,at.X,at.Y,0);await Idle();
    }
    private static void Volume(CadDocumentViewModel document,double expected)=>Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-expected)<1e-5),"Exact native volume "+expected);
    internal static async Task Preview(FaceFeatureEditorWindow editor){await ClickButton(editor.PreviewButton);if(editor.Editor.PreviewCommand.ExecutionTask is {} task)await task;else throw new InvalidOperationException("Preview did not execute");await Idle();}
    internal static async Task Confirm(FaceFeatureEditorWindow editor){await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;}
    internal static async Task Operate(Func<Task> open,Func<FaceFeatureEditorWindow,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>
        {
            FaceFeatureEditorWindow? editor=null;
            try
            {
                var deadline=DateTime.UtcNow.AddSeconds(15);
                do
                {
                    await Idle();editor=Application.Current.Windows.OfType<FaceFeatureEditorWindow>().SingleOrDefault();
                    if(editor is {IsLoaded:true,IsVisible:true,Host.Viewport:not null})break;
                    if(DateTime.UtcNow>=deadline)throw new InvalidOperationException("Face window not initialized: "+((MainWindowViewModel)Application.Current.MainWindow.DataContext).StatusText);
                }while(true);
                editor.Activate();await Idle();await action(editor);completion.TrySetResult();
            }
            catch(Exception ex){completion.TrySetException(ex);}
            finally{if(editor is {IsVisible:true})editor.Close();}
        },DispatcherPriority.ApplicationIdle);
        await open();await completion.Task;
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
