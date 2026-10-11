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

internal static partial class RepetitionEditorSmokeRunner
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
            await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,3,4,RigidTransform3d.Identity),"Source"));var source=document.Session.Snapshot.Bodies.Values.Single();
            await document.Session.SaveAsync(storage,Path.Combine(output,"baseline.cadoryx"));var baseline=document.Session.Snapshot;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                await Operate(()=>vm.EditRepetitionsCommand.ExecuteAsync(null),async editor=>
                {
                    Progress($"C3 {language}: exact mirror and arrays");editor.Activate();await Idle();Check(editor.IsLoaded&&editor.IsVisible,"Window did not load: "+vm.StatusText);
                    editor.NameInput.Text="Mirror";editor.KindPicker.SelectedValue=RepetitionKind.Mirror;editor.OriginXInput.Value=5;editor.RotationYInput.Value=90;
                    await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(ReferenceEquals(baseline,document.Session.Snapshot),"Preview keeps baseline");
                    Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-48)<1e-5&&Math.Abs(i.Geometry.Bounds.Max.X-10)<1e-5),"Exact mirrored BRep preview");
                    editor.StepInput.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"repetition-{language}.png"));
                    editor.KindPicker.SelectedValue=RepetitionKind.Linear;editor.CountInput.Value=3;editor.StepInput.Value=6;editor.RotationYInput.Value=0;
                    Check(!editor.Editor.CanConfirm,"Inputs invalidate preview");await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-72)<1e-5&&Math.Abs(i.Geometry.Bounds.Max.X-14)<1e-5),"Linear copies preview");
                    editor.KindPicker.SelectedValue=RepetitionKind.Circular;editor.CountInput.Value=4;editor.FullCircleInput.IsChecked=true;
                    await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-96)<1e-5),"Full-ring copies preview");
                    editor.Close();
                });
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"Closing cancels preview");
            }
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;
            await Operate(()=>vm.EditRepetitionsCommand.ExecuteAsync(null),async editor=>
            {
                editor.NameInput.Text="Linear";editor.CountInput.Value=3;editor.StepInput.Value=6;await Preview(editor);
                Check(editor.Editor.CanConfirm,editor.Editor.Status);await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
            });
            var committed=document.Session.Snapshot;var feature=committed.Features.Values.Single(f=>f.Recipe is RepetitionRecipe);var identities=((RepetitionRecipe)feature.Recipe).CopyIds;
            Check(Math.Abs(committed.Bodies.Values.Single().Geometry.VolumeMm3-72)<1e-5,"Commit exact copies");
            await document.Session.UndoAsync();Check(ReferenceEquals(baseline,document.Session.Snapshot),"Single undo");await document.Session.RedoAsync();Check(ReferenceEquals(committed,document.Session.Snapshot),"Exact redo");
            await Operate(()=>{document.EditFeature(feature.Id);return Task.CompletedTask;},async editor=>
            {
                Check(!editor.Editor.IsNew&&editor.Editor.SourceDescription=="Source","Feature tree routes existing array editor");editor.CountInput.Value=4;editor.StepInput.Value=7;
                await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
            });
            var edited=document.Session.Snapshot;var grown=(RepetitionRecipe)edited.Features[feature.Id].Recipe;Check(grown.CopyIds.Take(3).SequenceEqual(identities),"Stable surviving copy IDs");
            await document.Session.SaveAsync(storage,Path.Combine(output,"accepted.cadoryx"));using(var loaded=await storage.LoadAsync(Path.Combine(output,"accepted.cadoryx"),document.Session.Assets)){loaded.Snapshot.Validate();Check(((RepetitionRecipe)loaded.Snapshot.Features[feature.Id].Recipe).CopyIds.SequenceEqual(grown.CopyIds),"Persistent identities");}
            await Operate(()=>vm.EditRepetitionsCommand.ExecuteAsync(null),async editor=>
            {editor.FeaturePicker.SelectedItem=editor.Editor.Features.Single(f=>f.Feature==feature.Id);editor.StepInput.Value=0;await Preview(editor);Check(!editor.Editor.CanConfirm,"Invalid spacing rejects confirmation");editor.Close();});
            Check(ReferenceEquals(edited,document.Session.Snapshot),"Invalid edit atomicity");
            await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(30,30,5,RigidTransform3d.Identity),"Plate"));
            var plate=document.Session.Snapshot.Bodies.Values.Single(b=>b.Name=="Plate");
            await document.Session.ExecuteAsync(new AddHoleCommand(plate.Id,new(plate.Geometry,RigidTransform3d.Translate(0,0,5),new(5,5),2),"Seed hole"));
            var seed=document.Session.Snapshot.Features.Values.Single(f=>f.Recipe is HoleRecipe);var holeBaseline=document.Session.Snapshot;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                await Operate(()=>vm.EditRepetitionsCommand.ExecuteAsync(null),async editor=>
                {
                    Progress($"C3 {language}: one-base hole replay");editor.Activate();await Idle();
                    editor.SourcePicker.SelectedItem=editor.Editor.Sources.Single(s=>s.Body==seed.OutputBodyId);
                    await ClickButton(editor.ReplayHoleInput);Check(editor.Editor.ReplayHole&&!editor.OutputPicker.IsEnabled,"Actual replay checkbox locks fused output");
                    editor.NameInput.Text="Hole array";editor.KindPicker.SelectedValue=RepetitionKind.Linear;editor.CountInput.Value=3;editor.StepInput.Value=8;
                    await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(ReferenceEquals(holeBaseline,document.Session.Snapshot),"Hole replay preview keeps baseline");
                    Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-(4500-15*Math.PI))<1e-5),"Three drills on one plate");
                    editor.ReplayHoleInput.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"hole-replay-{language}.png"));
                    editor.KindPicker.SelectedValue=RepetitionKind.Circular;editor.OriginXInput.Value=10;editor.Editor.OriginY=10;editor.Editor.OriginZ=5;editor.FullCircleInput.IsChecked=true;
                    await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-(4500-15*Math.PI))<1e-5),"Circular replay removes three tools");
                    editor.KindPicker.SelectedValue=RepetitionKind.Mirror;editor.OriginXInput.Value=15;editor.Editor.OriginY=0;editor.Editor.OriginZ=0;editor.RotationYInput.Value=90;
                    await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-(4500-10*Math.PI))<1e-5),"Mirror replay removes two holes");
                    await ClickButton(editor.IncludeSourceInput);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-(4500-5*Math.PI))<1e-5),"Mirror-only restores original seed cut");
                    editor.OriginXInput.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"hole-replay-frame-{language}.png"));
                    await ClickButton(editor.CancelButton);
                });
                Check(ReferenceEquals(holeBaseline,document.Session.Snapshot),"Actual hole replay cancel is atomic");
            }
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;
            await Operate(()=>vm.EditRepetitionsCommand.ExecuteAsync(null),async editor=>
            {
                editor.SourcePicker.SelectedItem=editor.Editor.Sources.Single(s=>s.Body==seed.OutputBodyId);await ClickButton(editor.ReplayHoleInput);
                editor.NameInput.Text="Hole array";editor.CountInput.Value=3;editor.StepInput.Value=8;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
            });
            var replayCommitted=document.Session.Snapshot;var replayFeature=replayCommitted.Features.Values.Single(f=>f.Recipe is HolePatternRecipe);
            var replayIds=((HolePatternRecipe)replayFeature.Recipe).Layout.CopyIds;
            await document.Session.UndoAsync();Check(ReferenceEquals(holeBaseline,document.Session.Snapshot),"Hole replay single undo");
            await document.Session.RedoAsync();Check(ReferenceEquals(replayCommitted,document.Session.Snapshot),"Hole replay exact redo");
            await Operate(()=>{document.EditFeature(replayFeature.Id);return Task.CompletedTask;},async editor=>
            {
                Check(editor.Editor.ReplayHole&&!editor.ReplayHoleInput.IsEnabled&&!editor.SourcePicker.IsEnabled,"Feature tree retains hole replay kind and seed");
                Check(editor.Editor.SourceDescription=="Seed hole","Hole replay authoring seed description");editor.CountInput.Value=4;editor.StepInput.Value=6;
                await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
            });
            var replayEdited=document.Session.Snapshot;var replay=(HolePatternRecipe)replayEdited.Features[replayFeature.Id].Recipe;
            Check(replay.Layout.CopyIds.Take(3).SequenceEqual(replayIds),"Hole replay surviving identities");Check(replayEdited.Bodies.Count==2,"One replay base plus original independent array");
            Check(Math.Abs(replayEdited.Features[replayFeature.Id].Result.VolumeMm3-(4500-20*Math.PI))<1e-5,"Four repeated drills");
            var replayFile=Path.Combine(output,"accepted-hole-replay.cadoryx");await document.Session.SaveAsync(storage,replayFile);
            using(var loaded=await storage.LoadAsync(replayFile,document.Session.Assets))
            {loaded.Snapshot.Validate();var restored=(HolePatternRecipe)loaded.Snapshot.Features[replayFeature.Id].Recipe;Check(restored.Seed==seed.Id&&restored.Layout.CopyIds.SequenceEqual(replay.Layout.CopyIds),"Reopen exact replay authoring identities");}
            FeatureDefinition? additiveSeed=null;
            foreach(var operation in new[]{ExtrusionOperation.Fuse,ExtrusionOperation.Cut})
            {
                string mode=operation==ExtrusionOperation.Fuse?"add":"remove";
                await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(30,30,5,RigidTransform3d.Identity),mode+" plate"));
                var target=document.Session.Snapshot.Bodies.Values.Single(b=>b.Name==mode+" plate");
                var profile=new SketchProfile([new(0,0),new(2,0),new(2,2),new(0,2)]);
                await document.Session.ExecuteAsync(new AddExtrusionCommand(new(profile,operation==ExtrusionOperation.Fuse?3:2,RigidTransform3d.Translate(5,5,5),
                    new(operation==ExtrusionOperation.Fuse?ExtrusionSide.Positive:ExtrusionSide.Negative,Operation:operation,Source:target.Geometry)),mode+" seed",targetBody:target.Id));
                var operationSeed=document.Session.Snapshot.Features.Values.Single(f=>f.Name==mode+" seed");
                if(operation==ExtrusionOperation.Fuse)additiveSeed=operationSeed;
                var operationBaseline=document.Session.Snapshot;double delta=operation==ExtrusionOperation.Fuse?12:-8;
                foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
                {
                    CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                    await Operate(()=>vm.EditRepetitionsCommand.ExecuteAsync(null),async editor=>
                    {
                        Progress($"C3 {language}: one-base {mode} operation replay");editor.Activate();await Idle();
                        editor.SourcePicker.SelectedItem=editor.Editor.Sources.Single(s=>s.Body==operationSeed.OutputBodyId);await ClickButton(editor.ReplayExtrusionInput);
                        Check(editor.Editor.ReplayExtrusion&&!editor.Editor.ReplayHole&&!editor.OutputPicker.IsEnabled,"Material replay toggle and fixed output");
                        editor.NameInput.Text=mode+" replay";editor.CountInput.Value=3;editor.StepInput.Value=8;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                        Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-(4500+3*delta))<1e-4),"Three material operations on one base");
                        editor.ReplayExtrusionInput.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"extrusion-replay-{mode}-{language}.png"));
                        editor.KindPicker.SelectedValue=RepetitionKind.Circular;editor.OriginXInput.Value=10;editor.Editor.OriginY=10;editor.Editor.OriginZ=5;editor.FullCircleInput.IsChecked=true;
                        await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-(4500+3*delta))<1e-4),"Circular material replay");
                        editor.KindPicker.SelectedValue=RepetitionKind.Mirror;editor.OriginXInput.Value=15;editor.Editor.OriginY=0;editor.Editor.OriginZ=0;editor.RotationYInput.Value=90;
                        await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-(4500+2*delta))<1e-4),"Mirrored material operations");
                        await ClickButton(editor.IncludeSourceInput);await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                        Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-(4500+delta))<1e-4),"Mirror-only removes original operation");
                        editor.RotationYInput.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"extrusion-replay-frame-{mode}-{language}.png"));
                        await ClickButton(editor.CancelButton);
                    });
                    Check(ReferenceEquals(operationBaseline,document.Session.Snapshot),"Material replay actual cancel is atomic");
                }
            }
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;
            var materialBaseline=document.Session.Snapshot;
            await Operate(()=>vm.EditRepetitionsCommand.ExecuteAsync(null),async editor=>
            {
                editor.SourcePicker.SelectedItem=editor.Editor.Sources.Single(s=>s.Body==additiveSeed!.OutputBodyId);await ClickButton(editor.ReplayExtrusionInput);
                editor.NameInput.Text="Material array";editor.CountInput.Value=3;editor.StepInput.Value=8;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
            });
            var materialCommitted=document.Session.Snapshot;var materialFeature=materialCommitted.Features.Values.Single(f=>f.Recipe is ExtrusionPatternRecipe);
            var materialIds=((ExtrusionPatternRecipe)materialFeature.Recipe).Layout.CopyIds;
            await document.Session.UndoAsync();Check(ReferenceEquals(materialBaseline,document.Session.Snapshot),"Material replay single undo");
            await document.Session.RedoAsync();Check(ReferenceEquals(materialCommitted,document.Session.Snapshot),"Material replay exact redo");
            await Operate(()=>{document.EditFeature(materialFeature.Id);return Task.CompletedTask;},async editor=>
            {
                Check(editor.Editor.ReplayExtrusion&&!editor.ReplayExtrusionInput.IsEnabled&&!editor.SourcePicker.IsEnabled,"Tree routes complete material replay");
                editor.CountInput.Value=4;editor.StepInput.Value=6;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
            });
            var materialEdited=document.Session.Snapshot;var material=(ExtrusionPatternRecipe)materialEdited.Features[materialFeature.Id].Recipe;
            Check(material.Layout.CopyIds.Take(3).SequenceEqual(materialIds),"Material replay surviving identities");
            Check(Math.Abs(materialEdited.Features[materialFeature.Id].Result.VolumeMm3-4548)<1e-4,"Four additive tools");
            var materialFile=Path.Combine(output,"accepted-extrusion-replay.cadoryx");await document.Session.SaveAsync(storage,materialFile);
            using(var loaded=await storage.LoadAsync(materialFile,document.Session.Assets))
            {loaded.Snapshot.Validate();var restored=(ExtrusionPatternRecipe)loaded.Snapshot.Features[materialFeature.Id].Recipe;Check(material.SameTemplate(restored.Template)&&material.Layout.CopyIds.SequenceEqual(restored.Layout.CopyIds),"Reopen complete material replay template");}
            await RunPresentationAsync(vm,document,storage,services.GetRequiredService<IGeometryKernel>(),output,Progress);
            Check(await vm.CloseAllAsync(),"Close documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Zero assets");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"C3 production result copies, one-base hole and extrusion add/remove replay en-US/zh-CN/ja-JP; actual replay/include-source toggles and preview/cancel/confirm buttons; linear/circular/mirror/mirror-only exact drilling and material operations; locked fused output, complete feature-tree edit, persistent surviving identities, save/reopen, single undo/redo, input invalidation, close-cancel, invalid edit atomicity; actual style policy selector, fused conflict rejection, explicit whole-body preview and cancellation, distinct native face colors/layers/materials/visibility on three/four copies, presentation history and report replay on save/reopen, stable IDs on tree edit and zero assets.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markupCulture;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static async Task Preview(RepetitionEditorWindow editor)
    {await ClickButton(editor.PreviewButton);if(editor.Editor.PreviewCommand.ExecutionTask is {} task)await task;await Idle();}
    private static async Task Operate(Func<Task> open,Func<RepetitionEditorWindow,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>
        {RepetitionEditorWindow? editor=null;try{await Idle();editor=Application.Current.Windows.OfType<RepetitionEditorWindow>().Single();await action(editor);completion.TrySetResult();}catch(Exception ex){completion.TrySetException(ex);}finally{if(editor is {IsVisible:true})editor.Close();}},DispatcherPriority.ApplicationIdle);
        await open();await completion.Task;
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
