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

internal static class AssemblyRepetitionEditorSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;var vm=(MainWindowViewModel)window.DataContext;var markup=CultureInfo.GetCultureInfo(vm.CurrentCultureLCID);
        void Progress(string message)=>File.AppendAllText(Path.Combine(output,"progress.txt"),DateTimeOffset.Now.ToString("O")+" "+message+Environment.NewLine);
        try
        {
            // MainWindow initializes a blank document before dispatching diagnostics.
            // Reuse it so normal close does not wait on an unrelated unsaved tab.
            if(vm.Documents.Count==0)vm.NewCommand.Execute(null);
            var document=vm.ActiveDocument??throw new InvalidOperationException("Missing initial document.");
            Check(vm.Documents.Count==1&&document.Session.Snapshot.Bodies.Count==0,"Single blank diagnostic document");
            var storage=services.GetRequiredService<IDocumentStorage>();
            await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(4,3,2,RigidTransform3d.Identity),"Source"));var sourceSlot=document.Session.Snapshot.EnumerateOccurrences().Single().Path.Slots.Single();
            var doc=document.Session.Snapshot;var root=new OccurrencePath(doc.Id,[]);var group=DefinitionId.New();var groupSlot=ComponentSlotId.New();
            await document.Session.ExecuteAsync(AssemblyOccurrenceCommands.CreateAssembly(root,group,groupSlot,"Parent",RigidTransform3d.Identity));
            var parent=root.Append(groupSlot);await document.Session.ExecuteAsync(AssemblyOccurrenceCommands.Reparent(root.Append(sourceSlot),parent));
            await document.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(parent,new(new(40,20,0),Quaterniond.FromAxisAngle(Vector3d.UnitZ,Math.PI/2))));
            var source=parent.Append(sourceSlot);document.Selection.SelectOccurrence(source);var baseline=document.Session.Snapshot;var baseAssets=((IAssetStoreStatistics)document.Session.Assets).Count;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                await Operate(()=>vm.EditAssemblyRepetitionsCommand.ExecuteAsync(null),async editor=>
                {
                    editor.NameInput.Text="Instance array";editor.CountInput.Value=3;editor.StepInput.Value=8;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    var items=document.PreviewScene!.Items;Check(items.Length==3&&items.Select(i=>i.Geometry.AssetId).Distinct().Count()==1,"Native definition sharing");
                    var first=items.Single(i=>i.Path.Equals(source));Check(items.Any(i=>(i.WorldTransform.Translation-first.WorldTransform.Translation-new Vector3d(0,8,0)).Length<1e-8),"Parent-local direction under rotated parent");
                    Check(ReferenceEquals(baseline,document.Session.Snapshot),"Instance preview keeps baseline");Save(editor,Path.Combine(output,$"assembly-array-linear-{language}.png"));
                    editor.KindPicker.SelectedValue=RepetitionKind.Circular;editor.CountInput.Value=4;editor.OriginXInput.Value=5;
                    await ClickButton(editor.FullCircleInput);Check(editor.Editor.FullCircle,"Actual full circle toggle");await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    Check(document.PreviewScene!.Items.Length==4&&document.PreviewScene.Items.Select(i=>i.WorldTransform.Translation).Distinct().Count()==4,"Four distinct circular placements");
                    editor.OriginXInput.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"assembly-array-circular-{language}.png"));
                    editor.KindPicker.SelectedValue=RepetitionKind.Mirror;editor.OriginZInput.Value=8;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    Check(document.PreviewScene!.Items.Length==2&&document.PreviewScene.Items.Select(i=>i.Geometry.AssetId).Distinct().Count()==2,"Exact mirror derives geometry instead of a reflected quaternion");
                    Check(!editor.CountInput.IsEnabled&&!editor.StepInput.IsEnabled,"Mirror fixed output count and spacing");
                    Save(editor,Path.Combine(output,$"assembly-array-mirror-{language}.png"));
                    await ClickButton(editor.CancelButton);
                });
                Check(ReferenceEquals(baseline,document.Session.Snapshot)&&document.PreviewScene is null,"Actual cancel releases instance preview");
                Check(((IAssetStoreStatistics)document.Session.Assets).Count==baseAssets,"Instance arrays add no copied geometry assets");
            }
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;
            await Operate(()=>vm.EditAssemblyRepetitionsCommand.ExecuteAsync(null),async editor=>
            {editor.CountInput.Value=3;editor.StepInput.Value=8;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await Confirm(editor);});
            var committed=document.Session.Snapshot;var pattern=committed.AssemblyPatterns.Values.Single();var ids=pattern.CopySlots;
            await document.Session.UndoAsync();Check(ReferenceEquals(baseline,document.Session.Snapshot),"Single undo");await document.Session.RedoAsync();Check(ReferenceEquals(committed,document.Session.Snapshot),"Exact redo");
            await Operate(()=>{document.EditAssemblyRepetition(parent.Append(ids[0]));return Task.CompletedTask;},async editor=>
            {
                Check(!editor.SourcePicker.IsEnabled&&!editor.KindPicker.IsEnabled&&editor.Editor.SelectedSource!.Path.Equals(source),"Tree generated instance routes exact source array");
                editor.CountInput.Value=4;editor.StepInput.Value=9;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await Confirm(editor);
            });
            var edited=document.Session.Snapshot;var changed=edited.AssemblyPatterns[pattern.Id];Check(changed.CopySlots.Take(2).SequenceEqual(ids),"Surviving persistent instance slots");
            var file=Path.Combine(output,"accepted-assembly-array.cadoryx");await document.Session.SaveAsync(storage,file);
            using(var loaded=await storage.LoadAsync(file,document.Session.Assets)){loaded.Snapshot.Validate();Check(changed.CopySlots.SequenceEqual(loaded.Snapshot.AssemblyPatterns[pattern.Id].CopySlots),"Reopen exact layout identities");}
            await Operate(()=>{document.EditAssemblyRepetition(source);return Task.CompletedTask;},async editor=>
            {editor.StepInput.Value=0;await Preview(editor);Check(!editor.Editor.CanConfirm,"Invalid input disables confirm");await ClickButton(editor.CancelButton);});
            Check(ReferenceEquals(edited,document.Session.Snapshot),"Invalid edit atomicity");
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                await Operate(()=>{document.EditAssemblyRepetition(source);return Task.CompletedTask;},async editor=>
                {
                    await ClickButton(editor.DetachButton);if(editor.Editor.DetachCommand.ExecutionTask is {} task)await task;
                    Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(document.PreviewScene!.Items.Length==4,"Detach retains copies");
                    Save(editor,Path.Combine(output,$"assembly-array-detach-{language}.png"));await ClickButton(editor.CancelButton);
                });
                Check(ReferenceEquals(edited,document.Session.Snapshot),"Detach actual cancel is atomic");
            }
            Progress("Final array detach: opening production editor.");
            await Operate(()=>{document.EditAssemblyRepetition(source);return Task.CompletedTask;},async editor=>
            {Progress("Final array detach: preview button.");await ClickButton(editor.DetachButton);if(editor.Editor.DetachCommand.ExecutionTask is {} task)await task;Check(editor.Editor.CanConfirm,editor.Editor.Status);Progress("Final array detach: confirm button.");await Confirm(editor);Progress("Final array detach: confirmation completed.");});
            Progress("Final array detach: window closed.");
            Check(document.Session.Snapshot.AssemblyPatterns.Count==0&&document.Scene.Items.Length==4,"Confirmed detach keeps generated occurrences");
            await document.Session.UndoAsync();Check(ReferenceEquals(edited,document.Session.Snapshot),"Detach undo restores authored layout");
            Progress("Detach undo complete; saving array.");
            await document.Session.SaveAsync(storage,file);
            await document.Session.ExecuteAsync(AssemblyRepetitionCommands.Detach(pattern.Id));document.Selection.SelectOccurrence(source);
            await Operate(()=>vm.EditAssemblyRepetitionsCommand.ExecuteAsync(null),async editor=>
            {editor.KindPicker.SelectedValue=RepetitionKind.Mirror;editor.OriginZInput.Value=8;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await Confirm(editor);});
            var mirrored=document.Session.Snapshot;var mirror=mirrored.AssemblyPatterns.Values.Single();Check(mirror.Kind==RepetitionKind.Mirror&&mirror.Mirrors.Length==1,"Mirror actual confirmation");
            var derived=mirror.Mirrors.Single().Bodies.Single();var revision=mirrored.Bodies[derived.Target].Geometry.Revision;
            await Operate(()=>{document.EditAssemblyRepetition(parent.Append(mirror.CopySlots.Single()));return Task.CompletedTask;},async editor=>
            {Check(editor.Editor.Kind==RepetitionKind.Mirror&&!editor.KindPicker.IsEnabled,"Mirror generated instance routes complete layout");editor.OriginZInput.Value=10;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await Confirm(editor);});
            Check(document.Session.Snapshot.Bodies[derived.Target].Geometry.Revision==revision,"Editing mirror plane preserves local reflected geometry");
            var mirrorFile=Path.Combine(output,"accepted-assembly-mirror.cadoryx");await document.Session.SaveAsync(storage,mirrorFile);
            using(var restored=await storage.LoadAsync(mirrorFile,document.Session.Assets)){restored.Snapshot.Validate();Check(restored.Snapshot.AssemblyPatterns[mirror.Id].Mirrors.Single().Target==mirror.Mirrors.Single().Target,"Mirror definition identity roundtrip");}
            await MirrorStyleSmokeRunner.RunAsync(window,vm,storage,output);
            Progress("Saved final mirror; closing documents.");
            var closingProbe=new DispatcherTimer(TimeSpan.FromSeconds(1),DispatcherPriority.Background,(_,_)=>
                Progress("Close state: "+string.Join("; ",vm.Documents.Select(d=>$"{d.Session.Snapshot.Name}: dirty={d.Session.IsDirty}, busy={d.Session.IsBusy}, closing={d.Session.IsClosing}, preview={d.PreviewCommand.ExecutionTask?.Status}, confirm={d.ConfirmCommand.ExecutionTask?.Status}"))+
                    "; windows="+string.Join(",",Application.Current.Windows.Cast<Window>().Select(w=>w.GetType().Name+":"+w.Title))),window.Dispatcher);
            try{Check(await vm.CloseAllAsync(),"Close documents");Progress("Documents closed; stopping recovery.");await ((App)Application.Current).StopRecoveryAsync();Progress("Recovery stopped.");await Idle();}
            finally{closingProbe.Stop();}
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Zero assets");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"Assembly instance arrays and exact reflected definitions en-US/zh-CN/ja-JP; actual full-circle toggle, preview/cancel/confirm/detach; parent-local linear/circular/mirror placement, array definition sharing, mirror geometry and fixed count, generated-instance complete edits, persistent slots/definitions, save/reopen, exact undo/redo, rejected invalid spacing; independently colored native reflected faces, select/deselect and saved XDE styles; zero assets. Physical pointer input remains separate.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static async Task Preview(AssemblyRepetitionEditorWindow editor)
    {await ClickButton(editor.PreviewButton);if(editor.Editor.PreviewCommand.ExecutionTask is {} task)await task;await Idle();}
    private static async Task Confirm(AssemblyRepetitionEditorWindow editor)
    {await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;}
    private static async Task Operate(Func<Task> open,Func<AssemblyRepetitionEditorWindow,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>
        {AssemblyRepetitionEditorWindow? editor=null;try{await Idle();editor=Application.Current.Windows.OfType<AssemblyRepetitionEditorWindow>().Single();await action(editor);completion.TrySetResult();}catch(Exception ex){completion.TrySetException(ex);}finally{if(editor is {IsVisible:true})editor.Close();}},DispatcherPriority.ApplicationIdle);
        await open();await completion.Task;
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
