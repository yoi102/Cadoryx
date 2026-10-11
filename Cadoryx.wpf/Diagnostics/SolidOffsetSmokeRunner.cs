using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;
using static Cadoryx.wpf.Diagnostics.FaceFeatureEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class SolidOffsetSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;var vm=(MainWindowViewModel)window.DataContext;
        var markupCulture=CultureInfo.GetCultureInfo(vm.CurrentCultureLCID);
        void Progress(string step)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {step}{Environment.NewLine}");
        try
        {
            await Idle();var document=vm.ActiveDocument!;var storage=services.GetRequiredService<IDocumentStorage>();
            await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Source box"));
            var baseline=document.Session.Snapshot;var source=baseline.Features.Values.Single();
            async Task Configure(FaceFeatureEditorWindow editor)
            {
                Check(editor.Editor.IsOffset&&!editor.Editor.UsesFaces,"Whole solid editor");
                Check(editor.Editor.SelectedSource is null,"No implicit source");
                editor.SourcePicker.SelectedItem=editor.Editor.Sources.Single(s=>s.Feature==source.Id);await Idle();
                Check(editor.OffsetDistanceInput.IsVisible&&!editor.ThicknessInput.IsVisible&&!editor.ProgramList.IsVisible&&
                    !editor.RemoveFaceButton.IsVisible&&!editor.ClearFacesButton.IsVisible,"Only offset controls visible");
                editor.OffsetDistanceInput.Value=-1;await Idle();
                Check(editor.Editor.OffsetDistance==-1,"Actual signed numeric binding");
            }
            foreach(var language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                Progress(language+" controls");
                await Operate(()=>vm.SolidOffsetCommand.ExecuteAsync(null),async editor=>
                {
                    await Configure(editor);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,1040);
                    Check(ReferenceEquals(baseline,document.Session.Snapshot),"Preview leaves author unchanged");
                    editor.Host.Viewport!.SetProjection(CadProjection.Axonometric);editor.Host.Viewport.FitAll();await Idle();
                    Save(editor,Path.Combine(output,$"offset-{language}.png"));editor.Host.Viewport.SaveScreenshot(Path.Combine(output,$"offset-preview-{language}.png"));
                    editor.OffsetDistanceInput.Value=0;Check(!editor.Editor.CanConfirm,"Zero invalidates candidate");
                    await Preview(editor);Check(!editor.Editor.CanConfirm,"Zero rejected");
                    editor.OffsetDistanceInput.Value=-20;await Preview(editor);Check(!editor.Editor.CanConfirm,"Collapse rejected");
                    Check(ReferenceEquals(baseline,document.Session.Snapshot),"Failures preserve baseline");
                    editor.OffsetDistanceInput.Value=1;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    Volume(document,2700+37*Math.PI+4*Math.PI/3);
                    await ClickButton(editor.CancelButton);
                });
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"Cancel restores baseline");
            }
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;
            Progress("confirm undo redo tree edit store");
            await Operate(()=>vm.SolidOffsetCommand.ExecuteAsync(null),async editor=>
            {await Configure(editor);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await Confirm(editor);});
            var accepted=document.Session.Snapshot;var offset=accepted.Features.Values.Single(f=>f.Recipe is SolidOffsetRecipe);
            Check(Math.Abs(offset.Result.VolumeMm3-1040)<1e-5,"Confirmed exact volume");
            await document.Session.UndoAsync();Check(ReferenceEquals(baseline,document.Session.Snapshot),"Single undo");
            await document.Session.RedoAsync();Check(ReferenceEquals(accepted,document.Session.Snapshot),"Exact redo");
            await Operate(()=>{document.EditFeature(offset.Id);return Task.CompletedTask;},async editor=>
            {
                Check(editor.Editor.IsEditing&&!editor.SourcePicker.IsEnabled&&editor.Editor.OffsetDistance==-1,"Complete tree edit route");
                editor.OffsetDistanceInput.Value=-2;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,528);
                await Confirm(editor);
            });
            var edited=document.Session.Snapshot;Check(((SolidOffsetRecipe)edited.Features[offset.Id].Recipe).Distance==-2,"Tree edit committed");
            await Operate(()=>{document.EditFeature(offset.Id);return Task.CompletedTask;},async editor=>
            {await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);editor.Close();});
            Check(ReferenceEquals(edited,document.Session.Snapshot),"Window close cancels exact candidate");
            var path=Path.Combine(output,"accepted-offset.cadoryx");await document.Session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,document.Session.Assets))
            {loaded.Snapshot.Validate();Check(((SolidOffsetRecipe)loaded.Snapshot.Features[offset.Id].Recipe).Distance==-2,"Stored signed author");Check(Math.Abs(loaded.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3-528)<1e-5,"Reopened exact geometry");}
            Progress("cleanup");Check(await vm.CloseAllAsync(),"Close documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Zero assets");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"D4 production whole-solid offset windows en-US/zh-CN/ja-JP; signed numeric controls, positive/negative exact native volumes, zero/collapse rejection, atomic preview/cancel/close, confirm, single undo/redo, complete tree editing, storage reopen and zero assets. Physical pointer NOT VERIFIED; arbitrary geometry and original topology continuation NOT VERIFIED.");
            window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markupCulture;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static void Volume(CadDocumentViewModel document,double expected)=>Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-expected)<1e-5),"Exact native volume "+expected);
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
