using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Antelcat.I18N.WPF;
using AvalonDock.Core;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class AssemblySolveSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;var main=(MainWindowViewModel)window.DataContext;var markup=CultureInfo.GetCultureInfo(main.CurrentCultureLCID);
        void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        void Progress(string value)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {value}{Environment.NewLine}");
        try
        {
            await Idle();var document=main.ActiveDocument!;var session=document.Session;var editor=document.AssemblyConstraints;
            var storage=services.GetRequiredService<IDocumentStorage>();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Shared box"));
            var body=session.Snapshot.Bodies.Values.Single();var first=session.Snapshot.EnumerateOccurrences().Single().Path;
            var slot=ComponentSlotId.New();await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(new(session.Snapshot.Id,[]),body.PartId,slot,"Moving box",RigidTransform3d.Translate(30,0,0)));
            var second=new OccurrencePath(session.Snapshot.Id,[slot]);
            main.LayoutService.HideAnchorable(main.Modeling);main.LayoutService.HideAnchorable(main.Messages);main.LayoutService.ShowAnchorable(main.Properties);
            main.LayoutService.HideAnchorable(main.AiAssistant);main.LayoutService.HideAnchorable(main.CommandLine);
            window.WindowState=WindowState.Maximized;await Idle();
            var view=Find<AssemblyConstraintsView>(window)??throw new InvalidOperationException("Production assembly pane is unavailable.");
            view.RelationsExpander.IsExpanded=true;await Idle();var viewport=WindowSmokeRunner.Host(document).Viewport!;
            viewport.SetProjection(CadProjection.Top);viewport.FitAll();await Idle();
            document.Selection.SelectOccurrence(first);await ClickButton(view.FixButton);if(editor.AddFixedCommand.ExecutionTask is {} fix)await fix;
            async Task Pick(bool reference,Vector3d world)
            {
                view.DatumKindInput.SelectedItem=TopologyKind.Face;
                await ClickButton(reference?view.PickReferenceDatumButton:view.PickMovingDatumButton);
                var pixel=viewport.WorldToScreen(world);viewport.PointerPressed(0,pixel.X,pixel.Y,0);viewport.PointerReleased(0,pixel.X,pixel.Y,0);
                for(int wait=0;wait<80;wait++){await Idle();if((reference?editor.ReferenceDatumLabel:editor.MovingDatumLabel)!="—")return;}
                throw new InvalidOperationException("Native exact datum pick did not reach production relation input: "+editor.Status);
            }
            await Pick(true,new(5,5,10));await Pick(false,new(35,5,10));
            view.DatumRelationInput.SelectedItem=AssemblyConstraintKind.Coincident;await ClickButton(view.AddDatumRelationButton);
            if(editor.AddDatumRelationCommand.ExecutionTask is {} added)await added;
            Check(session.Snapshot.AssemblyConstraints.Count==2&&session.Snapshot.AssemblyConstraints.Values.Any(c=>c.PrimaryDatum is not null),"Production buttons did not create exact relation and fixed reference");
            document.Selection.Replace([]);var baseline=session.Snapshot;var assetCount=((IAssetStoreStatistics)session.Assets).Count;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;main.Properties.RefreshLanguage();await Idle();
                Check(Equals(view.PreviewSolveButton.Content,Strings.AssemblyPreviewSolve)&&Equals(view.RelationsExpander.Header,Strings.AssemblyRelations),"Assembly label did not update with language");
                Progress(language+": actual preview/cancel/confirm");
                await ClickButton(view.PreviewSolveButton);if(editor.PreviewSolveCommand.ExecutionTask is {} preview)await preview;
                Check(editor.HasSolvePreview&&ReferenceEquals(baseline,session.Snapshot),editor.Status);
                Check(editor.LastSolveReport is {FailureReason:AssemblySolveFailureReason.None,HasGlobalUniquenessProof:false},"Missing local solve evidence");
                view.RelationsExpander.BringIntoView(new Rect(0,0,view.ActualWidth,80));await Idle();Save(window,Path.Combine(output,$"solve-preview-{language}.png"));
                await ClickButton(view.CancelSolveButton);Check(document.PreviewScene is null&&!editor.HasSolvePreview&&ReferenceEquals(baseline,session.Snapshot),"Cancel published a pose");
                await ClickButton(view.PreviewSolveButton);if(editor.PreviewSolveCommand.ExecutionTask is {} again)await again;
                var displayed=document.PreviewScene!.Items.ToDictionary(i=>i.Path,i=>i.WorldTransform);
                viewport.FitAll();await Idle();viewport.SaveScreenshot(Path.Combine(output,$"native-preview-{language}.png"));
                File.WriteAllText(Path.Combine(output,$"solve-report-{language}.json"),JsonSerializer.Serialize(editor.LastSolveReport,new JsonSerializerOptions{WriteIndented=true}));
                await ClickButton(view.ConfirmSolveButton);if(editor.ConfirmSolveCommand.ExecutionTask is {} confirm)await confirm;
                var accepted=session.Snapshot;Check(accepted.StateId!=baseline.StateId&&!editor.HasSolvePreview&&document.PreviewScene is null,"Confirm failed");
                Check(document.Scene.Items.All(i=>displayed[i.Path]==i.WorldTransform),"Confirmation changed displayed solution branch");
                Check(accepted.AssemblyConstraints.Values.All(c=>c.Evaluate(accepted).Status==AssemblyConstraintStatus.Satisfied),"Confirmed residuals");
                view.ConfirmSolveButton.BringIntoView();await Idle();Save(window,Path.Combine(output,$"solve-confirm-{language}.png"));
                var file=Path.Combine(output,$"accepted-{language}.cadoryx");await session.SaveAsync(storage,file);
                using(var reopened=await storage.LoadAsync(file,session.Assets)){reopened.Snapshot.Validate();Check(reopened.Snapshot.AssemblyConstraints.Values.All(c=>c.Evaluate(reopened.Snapshot).Status==AssemblyConstraintStatus.Satisfied),"Reopened solved pose");}
                await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Single undo");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"Exact redo");await session.UndoAsync();
                Check(((IAssetStoreStatistics)session.Assets).Count==assetCount,"Pose solve copied geometry assets");
            }
            document.Selection.SelectOccurrence(second);await ClickButton(view.FixButton);if(editor.AddFixedCommand.ExecutionTask is {} conflictFix)await conflictFix;
            var conflict=session.Snapshot;var conflictId=conflict.AssemblyConstraints.Keys.Except(baseline.AssemblyConstraints.Keys).Single();
            await ClickButton(view.PreviewSolveButton);if(editor.PreviewSolveCommand.ExecutionTask is {} failed)await failed;
            Check(!editor.HasSolvePreview&&document.PreviewScene is null&&ReferenceEquals(conflict,session.Snapshot),"Conflicting fixed drivers published partial positions");
            Check(editor.LastSolveReport?.FailureReason==AssemblySolveFailureReason.NoConvergence,"Conflict classification");
            view.SolveStatusText.BringIntoView();await Idle();Save(window,Path.Combine(output,"solve-conflict.png"));view.RelationList.SelectedItem=editor.Constraints.Single(c=>c.Id==conflictId);
            await ClickButton(view.RemoveButton);if(editor.RemoveCommand.ExecutionTask is {} removed)await removed;
            await session.SaveAsync(storage,Path.Combine(output,"final.cadoryx"));
            Check(await main.CloseAllAsync(),"Close production document");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Close leaked assets");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"Assembly solve en-US/zh-CN/ja-JP: actual fixed/datum/preview/cancel/confirm/remove controls, native analytic face API picks, verified shared-definition datums, exact preview solution confirmation, local freedom evidence, fixed-driver conflict atomicity, save/reopen and one undo/redo, no copied pose assets, zero final assets. Physical pointer acceptance remains separate.");window.CloseAfterSmoke();
        }
        catch(Exception error){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+error);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static T? Find<T>(DependencyObject parent)where T:DependencyObject
    {if(parent is T match)return match;for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)if(Find<T>(VisualTreeHelper.GetChild(parent,i)) is {} child)return child;return null;}
}
