using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using MahApps.Metro.Controls;
using Microsoft.Extensions.DependencyInjection;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class GuidedEditorSmokeRunner
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
            await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(20,0,0)),"Other"));var part=document.Session.Snapshot.Features.Values.Single().PartId;
            var section=new SketchDraft(CadSketch.Create(part,"Sweep section",RigidTransform3d.Identity));section.AddCircle(new(0,0),1);
            var path=new SketchDraft(CadSketch.Create(part,"Directed path",new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0))));path.AddLine(new(0,5),new(0,0));path.AddLine(new(0,10),new(0,5));
            var stations=new List<CadSketch>();foreach(double z in new[]{0d,5,10})
            {var station=new SketchDraft(CadSketch.Create(part,"Loft station "+z,RigidTransform3d.Translate(0,0,z)));station.AddCircle(new(0,0),3);station.AddCircle(new(0,0),1);stations.Add(station.Value);}
            foreach(var sketch in new[]{section.Value,path.Value}.Concat(stations))await document.Session.ExecuteAsync(new UpsertSketchCommand(sketch,new ManagedSketchConstraintSolver()));
            var baseline=document.Session.Snapshot;
            async Task ConfigureSweep(GuidedEditorWindow editor)
            {
                Check(editor.Editor.SelectedSweepProfile is null&&editor.Editor.SelectedPathSketch is null,"No implicit section/path choice");
                editor.SectionPicker.SelectedItem=editor.Editor.Profiles.Single(p=>p.Section.Source?.SketchId==section.Value.Id);
                editor.PathSketchPicker.SelectedItem=editor.Editor.PathSketches.Single(p=>p.Value==path.Value.Id);
                foreach(var line in path.Value.Lines)
                {
                    editor.AvailableEdgePicker.SelectedItem=editor.Editor.AvailableEdges.Single(e=>e.Entity==line.Id);await ClickButton(editor.AddEdgeButton);
                    editor.PathEdgesList.UpdateLayout();var row=(ListBoxItem)editor.PathEdgesList.ItemContainerGenerator.ContainerFromItem(editor.Editor.PathEdges.Last());
                    await ClickButton(Descendants(row).OfType<CheckBox>().Single());
                }
                Check(editor.Editor.PathEdges.All(e=>e.Reverse),"Actual Reverse toggles");Check(!editor.SectionFrameEditor.IsEnabled&&!editor.PathFrameEditor.IsEnabled,"Linked frames disabled");
            }
            async Task ConfigureLoft(GuidedEditorWindow editor)
            {
                var root=editor.Editor.Channels.Single();editor.ChannelsList.SelectedItem=root;await ClickButton(editor.AddChildButton);var child=editor.Editor.SelectedChannel!;
                foreach(var station in stations)
                {
                    await ClickButton(editor.AddStationButton);editor.ChannelsList.SelectedItem=root;
                    editor.AssignmentProfilePicker.SelectedItem=editor.Editor.Profiles.Single(p=>p.Section.Source?.SketchId==station.Id&&p.Section.Source.CircleId==station.Circles[0].Id);await ClickButton(editor.AssignProfileButton);
                    editor.ChannelsList.SelectedItem=child;editor.AssignmentProfilePicker.SelectedItem=editor.Editor.Profiles.Single(p=>p.Section.Source?.SketchId==station.Id&&p.Section.Source.CircleId==station.Circles[1].Id);await ClickButton(editor.AssignProfileButton);
                }
                await ClickButton(editor.RuledCheckBox);Check(editor.Editor.Ruled,"Actual ruled toggle");Check(!editor.StationFrameEditor.IsEnabled,"Linked station frame disabled");
            }
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;Progress(language+" sweep");
                await Operate(()=>vm.SweepCommand.ExecuteAsync(null),async editor=>
                {
                    await ConfigureSweep(editor);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,10*Math.PI);Save(editor,Path.Combine(output,$"sweep-{language}.png"));
                    editor.CornerPicker.SelectedValue=SweepCorner.RoundCorner;Check(!editor.Editor.CanConfirm,"Option change invalidates");await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    editor.OutputPicker.SelectedValue=GuidedOutput.Sheet;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(document.PreviewScene!.Items.Any(i=>i.Geometry.Kind==BodyKind.Sheet),"Explicit sheet");
                    editor.OutputPicker.SelectedValue=GuidedOutput.Solid;editor.PathEdgesList.SelectedIndex=1;await ClickButton(editor.EdgeUpButton);await Preview(editor);Check(!editor.Editor.CanConfirm,"Wrong directed order rejected");
                    await ClickButton(editor.EdgeDownButton);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(ReferenceEquals(baseline,document.Session.Snapshot),"Sweep preview keeps baseline");
                    await ClickButton(editor.DetachPathButton);await ClickButton(editor.DetachSectionButton);Check(editor.PathFrameEditor.IsEnabled&&editor.SectionFrameEditor.IsEnabled,"Detach unlocks frames");
                    FrameInput(editor.SectionFrameEditor,"Z").Value=1;await Preview(editor);Check(!editor.Editor.CanConfirm,"Section/path pose mismatch rejects");FrameInput(editor.SectionFrameEditor,"Z").Value=0;
                    await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await ClickButton(editor.CancelButton);
                });
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"Sweep cancel baseline");Progress(language+" loft");
                await Operate(()=>vm.LoftCommand.ExecuteAsync(null),async editor=>
                {
                    await ConfigureLoft(editor);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Volume(document,80*Math.PI);Save(editor,Path.Combine(output,$"loft-{language}.png"));
                    editor.StationsList.SelectedIndex=1;await ClickButton(editor.StationUpButton);await Preview(editor);Check(!editor.Editor.CanConfirm,"Reordered station reversal rejected");
                    await ClickButton(editor.StationDownButton);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    await ClickButton(editor.AddChildButton);await Preview(editor);Check(!editor.Editor.CanConfirm,"Missing new channel assignments rejects");await ClickButton(editor.RemoveChannelButton);
                    await ClickButton(editor.DetachStationButton);Check(editor.StationFrameEditor.IsEnabled,"Detach entire station");await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    Check(ReferenceEquals(baseline,document.Session.Snapshot),"Loft preview keeps baseline");editor.Close();
                });
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"Loft close cancels");
            }
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;Progress("commit/edit/roundtrip");
            await Operate(()=>vm.SweepCommand.ExecuteAsync(null),async editor=>{await ConfigureSweep(editor);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await Confirm(editor);});
            var accepted=document.Session.Snapshot;var sweepFeature=accepted.Features.Values.Single(f=>f.Recipe is SweepRecipe);
            await document.Session.UndoAsync();Check(ReferenceEquals(baseline,document.Session.Snapshot),"Single undo");await document.Session.RedoAsync();Check(ReferenceEquals(accepted,document.Session.Snapshot),"Exact redo");
            await Operate(()=>{document.EditFeature(sweepFeature.Id);return Task.CompletedTask;},async editor=>
            {Check(!editor.Editor.IsNew&&editor.Editor.PathEdges.All(e=>e.Reverse)&&!editor.PathFrameEditor.IsEnabled,"Tree sweep retains complete linked reversed path");editor.CornerPicker.SelectedValue=SweepCorner.RightCorner;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await Confirm(editor);});
            await Operate(()=>vm.LoftCommand.ExecuteAsync(null),async editor=>{await ConfigureLoft(editor);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await Confirm(editor);});
            var loftFeature=document.Session.Snapshot.Features.Values.Single(f=>f.Recipe is LoftRecipe);var loftRecipe=(LoftRecipe)loftFeature.Recipe;
            await Operate(()=>{document.EditFeature(loftFeature.Id);return Task.CompletedTask;},async editor=>
            {
                var rebuilt=(LoftRecipe)editor.Editor.BuildRecipe();Check(rebuilt.Channels.Select(c=>c.Id).SequenceEqual(loftRecipe.Channels.Select(c=>c.Id))&&rebuilt.Stations.SequenceEqual(loftRecipe.Stations),"Tree loft retains channel identities and station order");
                await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await ClickButton(editor.CancelButton);
            });
            await document.Session.SaveAsync(storage,Path.Combine(output,"accepted.cadoryx"));using(var loaded=await storage.LoadAsync(Path.Combine(output,"accepted.cadoryx"),document.Session.Assets))
            {
                loaded.Snapshot.Validate();Check(loaded.Snapshot.Features[sweepFeature.Id].Recipe is SweepRecipe {Corner:SweepCorner.RightCorner,Path.Source:not null},"Stored sweep source and corner");
                var loadedLoft=(LoftRecipe)loaded.Snapshot.Features[loftFeature.Id].Recipe;Check(loadedLoft.Channels.Select(c=>c.Id).SequenceEqual(loftRecipe.Channels.Select(c=>c.Id))&&loadedLoft.Channels.All(c=>c.Sources.Length==3),"Stored linked explicit loft channels");
            }
            Progress("cleanup");Check(await vm.CloseAllAsync(),"Close documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Zero assets");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"D2 production sweep/loft editors en-US/zh-CN/ja-JP; actual directed edge Reverse toggles/order, explicit section/stations/child correspondence, Solid/Sheet and corner/ruled controls, exact native volumes, invalid order/pose/missing assignment rejection, linked/frozen frames, preview/cancel/close, single undo/redo, complete feature-tree recipe edit, save/reopen and zero assets.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markupCulture;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static NumericUpDown FrameInput(DependencyObject root,string property)=>Descendants(root).OfType<NumericUpDown>().Single(n=>BindingOperations.GetBinding(n,NumericUpDown.ValueProperty)?.Path.Path==property);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private static void Volume(CadDocumentViewModel document,double expected)=>Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-expected)<1e-5),"Exact native volume "+expected);
    private static async Task Preview(GuidedEditorWindow editor){await ClickButton(editor.PreviewButton);if(editor.Editor.PreviewCommand.ExecutionTask is {} task)await task;else throw new InvalidOperationException("Preview control did not execute");await Idle();}
    private static async Task Confirm(GuidedEditorWindow editor){await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;}
    private static async Task Operate(Func<Task> open,Func<GuidedEditorWindow,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>{GuidedEditorWindow? editor=null;try{await Idle();editor=Application.Current.Windows.OfType<GuidedEditorWindow>().Single();editor.Activate();await Idle();await action(editor);completion.TrySetResult();}catch(Exception ex){completion.TrySetException(ex);}finally{if(editor is {IsVisible:true})editor.Close();}},DispatcherPriority.ApplicationIdle);
        await open();await completion.Task;
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
