using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using Strings=Cadoryx.Lang.Strings.Strings;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class SketchRegionReselectionSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var main=(MainWindowViewModel)window.DataContext;int original=main.CurrentCultureLCID;
        try
        {
            await Idle();var doc=main.ActiveDocument!;var session=doc.Session;var kernel=services.GetRequiredService<IGeometryKernel>();
            var regions=(IExactSketchExpressionRegions)kernel;var solver=services.GetRequiredService<ISketchConstraintSolver>();var storage=services.GetRequiredService<IDocumentStorage>();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(80,0,0)),"Support"));var part=session.Snapshot.Features.Values.Single().PartId;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})foreach(bool nested in new[]{false,true})
            {
                main.ChangeCultureCommand.Execute(CultureInfo.GetCultureInfo(language).LCID.ToString(CultureInfo.InvariantCulture));await Idle();
                string kind=nested?"nested":"mixed";Progress($"{language}: {kind}");var draft=new SketchDraft(CadSketch.Create(part,$"Source selection {kind} {language}",RigidTransform3d.Identity));
                ImmutableArray<ImmutableArray<SketchEntityId>> slots;SketchEntityId[] sources;
                if(nested)
                {
                    foreach(double radius in new[]{8d,6,4,2})draft.AddCircle(new(0,0),radius);sources=draft.Value.Circles.Select(c=>c.Id).ToArray();
                    foreach(var axes in new[]{(9d,6d),(7d,4.5),(5d,3d),(3d,1.5)})draft.AddEllipse(new(new(20,0),new(axes.Item1,0),new(0,axes.Item2),0,Math.Tau));
                    slots=draft.Value.Ellipses.Select(e=>ImmutableArray.Create(e.Id)).ToImmutableArray();
                }
                else
                {
                    draft.AddRectangle(new(0,0),new(8,4));sources=[draft.Value.Lines[0].Id];
                    draft.AddBezier(new(20,0),new(25,-1),new(30,0));var b=draft.Value.Beziers[0];
                    draft.AddLine(new(30,0),new(30,6),b.End);var right=draft.Value.Lines[^1];
                    draft.AddLine(new(30,6),new(20,6),right.End);var top=draft.Value.Lines[^1];
                    draft.AddLine(new(20,6),new(20,0),top.End,b.Start);slots=[[b.Id,right.Id,top.Id,draft.Value.Lines[^1].Id]];
                }
                var created=SketchRegionOffsetAuthoring.Create(draft.Value,sources,.2,nested?SketchRegionAuthorJoin.Miter:SketchRegionAuthorJoin.Round,regions);
                await session.ExecuteAsync(new UpsertSketchCommand(created.Sketch,solver));var sketch=session.Snapshot.Sketches[draft.Value.Id];
                var root=SketchRegionAuthorModel.Roots(sketch,created.Group).Single();
                var profile=new SketchProfileReference(sketch.Id,sketch.Revision,[]){MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};
                await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,sketch.Plane)),"Mapped solid",part,sketchSource:profile));
                var feature=session.Snapshot.Features.Values.Single(f=>f.SketchSource?.SketchId==sketch.Id);
                for(int attempt=0;attempt<2;attempt++)
                {
                    var baseline=session.Snapshot;await using var editor=new SketchEditorViewModel(doc,kernel,solver,sketch,false);var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();
                    try
                    {
                        host.Canvas.Fit();editor.Select(null);host.Canvas.PointerPressed(host.Canvas.ToScreen(editor.Sketch.ExpressionCurves[0].Geometry.At(.37)),MouseButton.Left,ModifierKeys.Control);await Idle();
                        Check(editor.HasSelectedRegion,"Canvas selection did not expose whole-group reselect");host.ExpressionSection.IsExpanded=true;host.ExpressionSection.BringIntoView();await Idle();
                        Check(host.ReselectRegionSourcesButton.IsEnabled,"Source chooser command disabled");var originalDraft=editor.Sketch;
                        host.ExpressionDistanceInput.Value=.21;await Idle();await ClickButton(host.ReselectRegionSourcesButton);
                        Check(editor.Status==Strings.RegionReselectPending&&!Application.Current.Windows.OfType<SketchRegionSourceSelectionWindow>().Any(),"Pending numeric input was silently discarded by source selection");
                        Check(ReferenceEquals(originalDraft,editor.Sketch),"Pending numeric field changed draft");
                        editor.Select(null);editor.Select(originalDraft.ExpressionCurves[0].Id);await Idle();
                        await Choose(host,async chooser=>
                        {
                            Check(chooser.Owner==host,"Source chooser owner");Check(chooser.Editor.Rows.Count==slots.Sum(l=>l.Length),"Incomplete source rows");
                            Check(chooser.Editor.Rows.All(r=>r.Replacement is null)&&!chooser.ApplyButton.IsEnabled,"Sources guessed instead of explicitly selected");
                            await ClickButton(chooser.PreviewButton);Check(chooser.Editor.Status==Strings.RegionReselectIncomplete,"Incomplete source status");
                            await SelectSlots(chooser,slots);await Preview(chooser);Check(ReferenceEquals(originalDraft,editor.Sketch)&&ReferenceEquals(baseline,session.Snapshot),"Chooser preview published");
                            Save(chooser,Path.Combine(output,$"chooser-{kind}-{language}.png"));
                            // Select an old source after a verified preview: old drawing remains clearly labeled and Apply becomes disabled.
                            var row=chooser.Editor.Rows[0];var old=originalDraft.RegionOffsets[0].Sources[0][0].Entity;
                            var combo=await Combo(chooser,row);combo.SelectedItem=row.Choices.Single(c=>c.Id==old);await Idle();
                            Check(!chooser.ApplyButton.IsEnabled&&chooser.Editor.Status==Strings.RegionReselectPreviewOutdated,"Changed mapping retained an applicable preview");
                            await ClickButton(chooser.CancelButton);
                        });
                        Check(ReferenceEquals(originalDraft,editor.Sketch)&&!editor.Draft.CanUndo,"Chooser cancellation changed draft");
                        await Choose(host,async chooser=>{await SelectSlots(chooser,slots);await Preview(chooser);await ClickButton(chooser.ApplyButton);});
                        var applied=editor.Sketch;Check(!ReferenceEquals(originalDraft,applied),editor.Status);Check(ReferenceEquals(baseline,session.Snapshot),"Apply changed document before sketch confirmation");
                        Check(originalDraft.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)).SequenceEqual(applied.ExpressionCurves.Select(c=>(c.Id,c.Endpoints))),"Reselection replaced proved output identities");
                        await ClickButton(host.UndoButton);Check(ReferenceEquals(originalDraft,editor.Sketch)&&!editor.Draft.CanUndo,"Source mapping requires more than one undo");
                        editor.RedoCommand.Execute(null);await Idle();Check(ReferenceEquals(applied,editor.Sketch),"Source mapping redo");
                        await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} preview)await preview;Check(editor.CanConfirm,editor.Status);
                        Check(Math.Abs(doc.PreviewScene!.Items.Single(i=>i.BodyId==feature.OutputBodyId).Geometry.VolumeMm3-Expected(nested))<=1e-6,"Mapped solid differs from independent area / perimeter volume");
                        if(attempt==0){await ClickButton(host.CancelButton);Check(ReferenceEquals(baseline,session.Snapshot)&&doc.PreviewScene is null,"Main preview cancellation committed");}
                        else
                        {
                            Save(host,Path.Combine(output,$"applied-{kind}-{language}.png"));await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;
                            var accepted=session.Snapshot;Check(Math.Abs(accepted.Features[feature.Id].Result.VolumeMm3-Expected(nested))<=1e-6,"Mapped committed volume");
                            await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Document reselection single undo");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"Document reselection redo");
                        }
                    }
                    finally{host.Close();await Idle();}
                }
                // The same dialog enforces retained driver and reference structure guards; detaching restores editing.
                foreach(bool reference in new[]{false,true})
                {
                    var target=ParameterTarget.SketchRegion(sketch.Id,created.Group);var p=new CadParameter(Guid.NewGuid(),"distance",ParameterDimension.Length,reference?"":".2 mm",reference?target:null);
                    await session.ExecuteAsync(new SetParametersCommand(new([p],reference?[]:[new(Guid.NewGuid(),p.Id,target)])));
                    await using var editor=new SketchEditorViewModel(doc,kernel,solver,session.Snapshot.Sketches[sketch.Id],false);var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();
                    try
                    {
                        editor.Select(sketch.ExpressionCurves[0].Id);host.ExpressionSection.IsExpanded=true;await Idle();var captured=editor.Sketch;
                        var oldSlots=created.Sketch.RegionOffsets[0].Sources.Select(l=>l.Select(s=>s.Entity).ToImmutableArray()).ToImmutableArray();
                        await Choose(host,async chooser=>{await SelectSlots(chooser,oldSlots);await ClickButton(chooser.PreviewButton);if(chooser.Editor.PreviewCommand.ExecutionTask is {} work)await work;
                            Check(chooser.Editor.Status==Strings.RegionReselectParameterGuard&&!chooser.ApplyButton.IsEnabled,chooser.Editor.Status+chooser.Editor.Details);await ClickButton(chooser.CancelButton);});
                        Check(ReferenceEquals(captured,editor.Sketch)&&!editor.Draft.CanUndo,"Parameter rejection changed draft");await ClickButton(host.CancelButton);
                    }
                    finally{host.Close();await Idle();}
                    await session.ExecuteAsync(new SetParametersCommand(ParameterBook.Empty));
                }
                string file=Path.Combine(output,$"reselected-{kind}-{language}.cadoryx");await session.SaveAsync(storage,file);var saved=session.Snapshot;
                Check(await main.CloseAllAsync(),"Close saved source mapping before reopen");await main.OpenPathAsync(file);await Idle();doc=main.ActiveDocument!;session=doc.Session;
                var reopened=session.Snapshot.Sketches[sketch.Id];Check(SketchRegionAuthorModel.SameTable(saved.Sketches[sketch.Id],reopened),"Production reopen lost source mapping");
                ((ISketchExpressionRegionVerifier)regions).ConfirmExpressionRegions(reopened);
                await using(var editor=new SketchEditorViewModel(doc,kernel,solver,reopened,false))
                {
                    var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();
                    try{editor.Select(reopened.ExpressionCurves[0].Id);host.ExpressionSection.IsExpanded=true;host.ExpressionSection.BringIntoView();host.Canvas.Fit();await Idle();
                        Save(host,Path.Combine(output,$"reopened-{kind}-{language}.png"));await Choose(host,async chooser=>{Check(chooser.Editor.Rows.All(r=>r.Replacement is null),"Reopen guessed source map");await ClickButton(chooser.CancelButton);});await ClickButton(host.CancelButton);}
                    finally{host.Close();await Idle();}
                }
            }
            await session.SaveAsync(storage,Path.Combine(output,"baseline.cadoryx"));Check(await main.CloseAllAsync(),"Close source mapping documents");
            await ((App)Application.Current).StopRecoveryAsync();await Idle();Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Reselection assets remain");
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"Six actual three-language source chooser workflows: every replacement explicitly selected through ComboBox binding, incomplete and invalidated previews, chooser/main cancellation, one draft undo, preview/confirm document undo, line-to-Bezier and four-level circle-to-ellipse native volume against independent area/perimeter, retained region driver/reference refusal, real close and production reopen, 18 full window screenshots, empty binding log and zero assets. Physical mouse/keyboard input remains separate.");window.CloseAfterSmoke();
        }
        catch(Exception error){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+error);Application.Current.Shutdown(1);}
        finally{main.ChangeCultureCommand.Execute(original.ToString(CultureInfo.InvariantCulture));PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
        void Progress(string value)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {value}{Environment.NewLine}");
    }
    private static async Task Choose(SketchEditorWindow parent,Func<SketchRegionSourceSelectionWindow,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=Application.Current.Dispatcher.InvokeAsync(async()=>
        {
            SketchRegionSourceSelectionWindow? chooser=null;
            try
            {
                var waiting=Stopwatch.StartNew();
                while(chooser is null)
                {
                    await Idle();chooser=Application.Current.Windows.OfType<SketchRegionSourceSelectionWindow>().SingleOrDefault();
                    if(waiting.Elapsed.TotalSeconds>5)throw new InvalidOperationException("The real source chooser did not open.");
                }
                await action(chooser);completion.TrySetResult();
            }
            catch(Exception error){completion.TrySetException(error);}finally{if(chooser is {IsVisible:true})chooser.Close();}
        },DispatcherPriority.ApplicationIdle);
        await ClickButton(parent.ReselectRegionSourcesButton);await completion.Task;await Idle();
        if(parent.Editor.ReselectRegionSourcesCommand.ExecutionTask is {} command)await command;
    }
    private static async Task<ComboBox> Combo(SketchRegionSourceSelectionWindow window,RegionSourceSlotRow row)
    {
        window.SourceRows.ScrollIntoView(row);window.SourceRows.UpdateLayout();await Idle();
        return Descendants(window.SourceRows).OfType<ComboBox>().Single(c=>ReferenceEquals(c.DataContext,row));
    }
    private static async Task SelectSlots(SketchRegionSourceSelectionWindow window,ImmutableArray<ImmutableArray<SketchEntityId>> slots)
    {foreach(var row in window.Editor.Rows){var combo=await Combo(window,row);combo.SelectedItem=row.Choices.Single(c=>c.Id==slots[row.Loop][row.Slot]);await Idle();}}
    private static async Task Preview(SketchRegionSourceSelectionWindow window)
    {await ClickButton(window.PreviewButton);if(window.Editor.PreviewCommand.ExecutionTask is {} work)await work;await Idle();Check(window.ApplyButton.IsEnabled,window.Editor.Status+window.Editor.Details);}
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var value in Descendants(child))yield return value;}}
    private static double Expected(bool nested)
    {
        const int count=16384;double sum=0;
        if(!nested){for(int i=0;i<=count;i++){double t=(double)i/count;sum+=(i==0||i==count?1:i%2==0?2:4)*Math.Sqrt(100+Math.Pow(4*t-2,2));}return 2*(60+10d/3+.2*(22+sum/(3*count))+.04*Math.PI);}
        int index=0;foreach(var axes in new[]{(9d,6d),(7d,4.5),(5d,3d),(3d,1.5)})
        {
            double p=0,step=Math.Tau/count;for(int i=0;i<=count;i++){double t=i*step;p+=(i==0||i==count?1:i%2==0?2:4)*Math.Sqrt(axes.Item1*axes.Item1*Math.Sin(t)*Math.Sin(t)+axes.Item2*axes.Item2*Math.Cos(t)*Math.Cos(t));}
            double sign=index++%2==0?1:-1;sum+=sign*(Math.PI*axes.Item1*axes.Item2+sign*.2*p*step/3+.04*Math.PI);
        }
        return 2*sum;
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
