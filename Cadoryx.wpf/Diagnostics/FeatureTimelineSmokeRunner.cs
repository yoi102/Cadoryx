using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using Antelcat.I18N.WPF;
using AvalonDock.Core;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Toolboxes;
using Cadoryx.wpf.Views.Toolboxes;
using MahApps.Metro.Controls;
using Microsoft.Extensions.DependencyInjection;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class FeatureTimelineSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var original=CultureInfo.CurrentUICulture;var main=(MainWindowViewModel)window.DataContext;var markup=CultureInfo.GetCultureInfo(main.CurrentCultureLCID);
        void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        void Progress(string value)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {value}{Environment.NewLine}");
        try
        {
            await Idle();var document=main.ActiveDocument!;var session=document.Session;var storage=services.GetRequiredService<IDocumentStorage>();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Base"));var source=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,2,2,RigidTransform3d.Translate(2,2,2)),"Tool",source.PartId));var tool=session.Snapshot.Features.Values.Single(f=>f.Id!=source.Id);
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[source.OutputBodyId,tool.OutputBodyId]));var cut=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
            source=session.Snapshot.Features[source.Id];tool=session.Snapshot.Features[tool.Id];var baseline=session.Snapshot;
            await session.SaveAsync(storage,Path.Combine(output,"baseline.cadoryx"));
            main.LayoutService.HideAnchorable(main.Properties);main.LayoutService.HideAnchorable(main.Messages);
            main.LayoutService.ShowAnchorable(main.Modeling);window.WindowState=WindowState.Maximized;await Idle();
            var tree=Find<ModelTreeToolboxView>(window)??throw new InvalidOperationException("Production model tree is not visible.");
            var modeling=Find<ModelingToolboxView>(window)??throw new InvalidOperationException("Production modeling pane is not visible.");
            ModelTreeItemViewModel Row(FeatureId id)=>Rows(main.ModelTree.Items).Single(r=>r.Feature==id);
            async Task Invoke(FeatureId id,string header,string? screenshot=null)
            {
                var menu=tree.CreateFeatureMenu(Row(id));menu.PlacementTarget=tree.ModelTree;menu.IsOpen=true;await Idle();
                try
                {
                    if(screenshot is not null)Save(menu,screenshot);
                    var item=menu.Items.OfType<MenuItem>().Single(m=>Equals(m.Header,header));
                    var peer=UIElementAutomationPeer.CreatePeerForElement(item)??new MenuItemAutomationPeer(item);
                    if(peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)throw new InvalidOperationException("History menu has no Invoke pattern.");
                    invoke.Invoke();await Idle();
                    var clock=Stopwatch.StartNew();while(session.IsBusy){if(clock.Elapsed.TotalSeconds>30)throw new TimeoutException("History action timed out.");await Task.Delay(100);}
                    await Idle();
                }
                finally{menu.IsOpen=false;await Idle();}
            }
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;Progress(language+": cursor and production menu");
                await Invoke(source.Id,Strings.RollbackAfter,Path.Combine(output,$"history-menu-{language}.png"));
                Check(session.FeatureCursor?.Position==1&&ReferenceEquals(baseline,session.Snapshot),"Menu did not move a temporary cursor");
                Check(document.Scene.Items.Single().BodyId==source.OutputBodyId,"Native scene still shows the final body");
                Check(main.ModelTree.History!.CursorSummary==string.Format(Strings.HistoryCursorFormat,baseline.Definitions[source.PartId].Name,1,3),"Cursor summary binding/localization");
                Check(Rows(main.ModelTree.Items).Where(r=>r.Feature is not null&&r.Feature!=source.Id).All(r=>r.IsAfterCursor),"Future feature rows are not marked");
                Save(tree,Path.Combine(output,$"cursor-{language}.png"));
                var viewport=WindowSmokeRunner.Host(document).Viewport!;viewport.SetProjection(CadProjection.Axonometric);viewport.FitAll();await Idle();
                viewport.SaveScreenshot(Path.Combine(output,$"native-cursor-{language}.png"));
                await ClickButton(tree.ReturnHistoryEndButton);if(main.ModelTree.History.ReturnToEndCommand.ExecutionTask is {} returned)await returned;
                Check(!session.IsRolledBack&&ReferenceEquals(baseline,session.DisplaySnapshot),"Actual return button did not restore the end");
                Progress(language+": rejected and legal order");
                await Invoke(cut.Id,Strings.MoveFeatureEarlier);Check(ReferenceEquals(baseline,session.Snapshot),"Invalid dependency reorder published");
                Check(document.ToolStatus.Contains("Cut")||document.ToolStatus.Contains(cut.Name),"Invalid order did not identify the dependent feature");
                modeling.ModelingStatusText.BringIntoView();await Idle();Save(modeling,Path.Combine(output,$"refused-order-{language}.png"));
                await Invoke(tool.Id,Strings.MoveFeatureEarlier);var reordered=session.Snapshot;
                Check(((PartDefinition)reordered.Definitions[source.PartId]).Features.SequenceEqual(new[]{tool.Id,source.Id,cut.Id}),"Independent feature menu move did not publish");
                Check(reordered.Features[cut.Id]==cut,"Chronology move rewrote Boolean operands/history");
                var path=Path.Combine(output,$"reordered-{language}.cadoryx");await session.SaveAsync(storage,path);
                using(var loaded=await storage.LoadAsync(path,session.Assets)){loaded.Snapshot.Validate();Check(((PartDefinition)loaded.Snapshot.Definitions[source.PartId]).Features.SequenceEqual(new[]{tool.Id,source.Id,cut.Id}),"Reopened order differs");}
                await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Reorder single undo");
                await Invoke(source.Id,Strings.RollbackAfter);Progress(language+": modeling controls and insertion");
                modeling.ModelingToolInput.SelectedValue="Cylinder";modeling.ModelingNameInput.Text="Inserted cylinder";
                modeling.ModelingSizeXInput.Value=1;modeling.ModelingSizeZInput.Value=2;modeling.ModelingPositionXInput.Value=20;await Idle();
                await ClickButton(modeling.ModelingPreviewButton);if(document.PreviewCommand.ExecutionTask is {} preview)await preview;
                Check(document.HasPreview,document.ToolStatus);Check(ReferenceEquals(baseline,session.Snapshot),"Cursor preview committed");
                document.CancelCommand.Execute(null);Check(session.IsRolledBack&&document.PreviewScene is null,"Cancel lost cursor or retained preview");
                await ClickButton(modeling.ModelingPreviewButton);if(document.PreviewCommand.ExecutionTask is {} again)await again;
                Check(document.HasPreview,document.ToolStatus);modeling.ModelingToolInput.BringIntoView();await Idle();Save(modeling,Path.Combine(output,$"insert-{language}.png"));
                modeling.ModelingConfirmButton.BringIntoView();await Idle();Save(modeling,Path.Combine(output,$"confirm-{language}.png"));
                viewport.FitAll();await Idle();viewport.SaveScreenshot(Path.Combine(output,$"native-insert-{language}.png"));
                await ClickButton(modeling.ModelingConfirmButton);if(document.ConfirmCommand.ExecutionTask is {} confirm)await confirm;
                var after=session.Snapshot;after.Validate();Check(!session.IsRolledBack,"Insertion did not return to complete graph");
                var added=after.Features.Values.Single(f=>f.Name=="Inserted cylinder");
                Check(((PartDefinition)after.Definitions[source.PartId]).Features[1]==added.Id&&after.Features[cut.Id]==cut,"Insertion did not preserve exact downstream branch");
                Check(Math.Abs(added.Result.VolumeMm3-2*Math.PI)<1e-5,"Production radius/height controls");
                path=Path.Combine(output,$"inserted-{language}.cadoryx");await session.SaveAsync(storage,path);
                using(var loaded=await storage.LoadAsync(path,session.Assets)){loaded.Snapshot.Validate();Check(loaded.Snapshot.Features[cut.Id].Result==cut.Result,"Insertion roundtrip changed old result");}
                await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Insertion single undo");await session.RedoAsync();Check(ReferenceEquals(after,session.Snapshot),"Insertion single redo");await session.UndoAsync();
            }
            await session.SaveAsync(storage,Path.Combine(output,"final-baseline.cadoryx"));
            Check(await main.CloseAllAsync(),"Close documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"History/preview assets were not released");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"E1 production feature menu factory and MenuItem Automation Invoke, temporary cursor/marked rows/return button, exact cached intermediate native scene, legal order and dependency refusal, actual modeling radius/height/placement and preview/cancel/confirm at cursor in en-US/zh-CN/ja-JP; original downstream branch/history preserved, single undo/redo, save/reopen and zero assets. OS context-menu opening and physical pointer are separate and unverified.");window.CloseAfterSmoke();
        }
        catch(Exception error){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+error);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=original;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static IEnumerable<ModelTreeItemViewModel> Rows(IEnumerable<ModelTreeItemViewModel> roots)
    {foreach(var row in roots){row.IsExpanded=true;yield return row;foreach(var child in Rows(row.Children))yield return child;}}
    private static T? Find<T>(DependencyObject root)where T:DependencyObject
    {if(root is T value)return value;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(Find<T>(VisualTreeHelper.GetChild(root,i)) is {} child)return child;return null;}
}
