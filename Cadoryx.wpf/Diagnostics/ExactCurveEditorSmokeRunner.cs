using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using MahApps.Metro.Controls;
using Microsoft.Extensions.DependencyInjection;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class ExactCurveEditorSmokeRunner
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
            await Idle();var document=main.ActiveDocument!;var session=document.Session;var kernel=services.GetRequiredService<IGeometryKernel>();
            var solver=services.GetRequiredService<ISketchConstraintSolver>();var storage=services.GetRequiredService<IDocumentStorage>();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(40,0,0)),"Support"));
            var part=session.Snapshot.Features.Values.Single().PartId;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})foreach(bool spline in new[]{true,false})
            {
                string kind=spline?"spline":"bezier";Progress($"{language} {kind}: production numeric editing");
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                await session.ExecuteAsync(new EditDocumentCommand("Isolate B9 sample",doc=>doc with
                    {Bodies=doc.Bodies.ToImmutableDictionary(p=>p.Key,p=>p.Value with{IsVisible=false})}));
                var d=new SketchDraft(CadSketch.Create(part,"B9 "+kind,RigidTransform3d.Identity));
                if(spline)d.AddSpline(new CubicSplineDefinition([new(8,0),new(20,2),new(16,4),new(18,6),new(16,9)],[0,.2,1],[4,1,4]));
                else d.AddBezier(new(8,0),new(10,3),new(12,0));
                await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var sketch=session.Snapshot.Sketches[d.Value.Id];
                var id=spline?sketch.Splines[0].Id:sketch.Beziers[0].Id;
                var source=spline?SketchProfileReference.CreateSplineSegment(sketch,id):SketchProfileReference.CreateBezierSegment(sketch,id);
                await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,sketch.Plane)),"B9 linked "+kind,part,sketchSource:source));
                var feature=session.Snapshot.Features.Values.Single(f=>f.SketchSource?.SketchId==sketch.Id);var baseline=session.Snapshot;
                for(int attempt=0;attempt<2;attempt++)
                {
                    await using var editor=new SketchEditorViewModel(document,kernel,solver,sketch,false);
                    var host=new SketchEditorWindow(editor){Owner=window};
                    try
                    {
                        host.Show();host.Activate();await Idle();
                        host.ToolPicker.SelectedValue="SplineRegion";((NumericUpDown)host.FindName("SplineControlCountInput")).Value=17;
                        await Idle();Check(editor.SplineControlCount==17,"Production control count still capped at sixteen");host.ToolPicker.SelectedValue="Select";
                        host.GeometrySection.IsExpanded=true;await Idle();
                        host.EntityList.SelectedItem=editor.Entities.Single(e=>e.Id==id);await Idle();
                        host.EntityList.ScrollIntoView(host.EntityList.SelectedItem);await Idle();
                        Check(editor.HasSelectedExactCurve&&editor.HasSelectedSpline==spline,"Actual entity list selection did not enable exact curve inputs");
                        if(spline)
                        {
                            var text=(TextBox)host.FindName("SplineKnotsInput");text.Text="0:4; .6:1; 1:4";
                            await ClickButton((Button)host.FindName("ApplySplineKnotsButton"));
                            Check(editor.Sketch.Splines[0].Knots!.Knots.SequenceEqual(new[]{0d,.6,1}),"Knot TextBox/Apply binding");
                            var before=editor.Sketch;text.Text="0:4; 0:1; 1:4";await ClickButton((Button)host.FindName("ApplySplineKnotsButton"));
                            Check(ReferenceEquals(before,editor.Sketch),"Invalid knots changed the draft");
                            Check(editor.Status==Cadoryx.Lang.Strings.Strings.CurveInvalidKnots,"Invalid knot diagnostic was not localized");text.Text="0:4; .6:1; 1:4";
                            ((Button)host.FindName("ApplySplineKnotsButton")).BringIntoView();await Idle();
                            if(attempt==0)Save(host,Path.Combine(output,$"knots-{language}.png"));
                        }
                        ((NumericUpDown)host.FindName("SplineFirstInput")).Value=.1;((NumericUpDown)host.FindName("SplineLastInput")).Value=.8;
                        await Idle();await ClickButton((Button)host.FindName("KeepSplineIntervalButton"));
                        Check((spline?editor.Sketch.Splines[0].Id:editor.Sketch.Beziers[0].Id)==id,"Single retained interval replaced the author curve identity");
                        ((NumericUpDown)host.FindName("SplineExtensionInput")).Value=.1;await Idle();
                        await ClickButton((Button)host.FindName("ExtendSplinePolynomialButton"));Check(editor.Sketch.Revision==sketch.Revision,"Draft revision advanced before publication");
                        ((Button)host.FindName("ExtendSplinePolynomialButton")).BringIntoView();await Idle();
                        if(attempt==0)Save(host,Path.Combine(output,$"interval-{kind}-{language}.png"));
                        await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} preview)await preview;
                        Check(editor.CanConfirm,editor.Status);Check(ReferenceEquals(baseline,session.Snapshot),"Preview committed the document");
                        Check(document.PreviewScene!.Items.Any(i=>i.BodyId==feature.OutputBodyId&&i.Geometry.VolumeMm3>0&&Math.Abs(i.Geometry.VolumeMm3-feature.Result.VolumeMm3)>.0001),"Linked native solid did not recompute");
                        var viewport=WindowSmokeRunner.Host(document).Viewport!;viewport.SetProjection(CadProjection.Axonometric);viewport.FitAll();await Idle();
                        if(attempt==0)viewport.SaveScreenshot(Path.Combine(output,$"native-{kind}-{language}.png"));
                        if(attempt==0)
                        {
                            await ClickButton(host.CancelButton);await Idle();Check(ReferenceEquals(baseline,session.Snapshot)&&document.PreviewScene is null,"Actual cancel altered committed state or retained preview");
                        }
                        else
                        {
                            await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;await Idle();
                            Check(!ReferenceEquals(baseline,session.Snapshot),"Actual confirm did not publish");session.Snapshot.Validate();
                            string file=Path.Combine(output,$"accepted-{kind}-{language}.cadoryx");await session.SaveAsync(storage,file);
                            using(var loaded=await storage.LoadAsync(file,session.Assets))
                            {
                                loaded.Snapshot.Validate();Check(SketchProfileReference.SameProfile(((ExtrudeRecipe)session.Snapshot.Features[feature.Id].Recipe).Profile,
                                    ((ExtrudeRecipe)loaded.Snapshot.Features[feature.Id].Recipe).Profile),"Roundtrip lost trimmed/extended author data");
                            }
                            var after=session.Snapshot;await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Single document undo");
                            await session.RedoAsync();Check(ReferenceEquals(after,session.Snapshot),"Single document redo");await session.UndoAsync();
                        }
                    }
                    finally{host.Close();await Idle();}
                }
                Progress($"{language} {kind}: split and explicit production reselection");
                await using(var splitEditor=new SketchEditorViewModel(document,kernel,solver,sketch,false))
                {
                    var host=new SketchEditorWindow(splitEditor){Owner=window};
                    try
                    {
                        host.Show();await Idle();host.GeometrySection.IsExpanded=true;await Idle();
                        host.EntityList.SelectedItem=splitEditor.Entities.Single(e=>e.Id==id);await Idle();
                        ((NumericUpDown)host.FindName("SplineFirstInput")).Value=.2;((NumericUpDown)host.FindName("SplineLastInput")).Value=.8;await Idle();
                        await ClickButton((Button)host.FindName("RemoveCurveIntervalButton"));
                        Check(!splitEditor.Sketch.Splines.Any(c=>c.Id==id)&&!splitEditor.Sketch.Beziers.Any(c=>c.Id==id),"Actual split button retained ambiguous source identity");
                        await ClickButton(host.PreviewButton);if(splitEditor.PreviewCommand.ExecutionTask is {} preview)await preview;
                        Check(splitEditor.CanConfirm,splitEditor.Status);
                        Check(splitEditor.Status.Contains(string.Format(Cadoryx.Lang.Strings.Strings.SketchPreviewFrozen,1,feature.Name)),"Preview did not explain the frozen linked feature");
                        Check(ReferenceEquals(baseline,session.Snapshot),"Split preview committed");Save(host,Path.Combine(output,$"split-{kind}-{language}.png"));
                        await ClickButton(host.ConfirmButton);if(splitEditor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;await Idle();
                        Check(session.Snapshot.Features[feature.Id].IsStale&&session.Snapshot.Features[feature.Id].Result==feature.Result,"Split did not retain/freeze original result");
                        string saved=Path.Combine(output,$"accepted-split-{kind}-{language}.cadoryx");await session.SaveAsync(storage,saved);
                        using var loaded=await storage.LoadAsync(saved,session.Assets);loaded.Snapshot.Validate();Check(loaded.Snapshot.Features[feature.Id].IsStale,"Split roundtrip lost freeze");
                    }
                    finally{host.Close();await Idle();}
                }
                var frozen=session.Snapshot;var freshSketch=frozen.Sketches[sketch.Id];var replacement=spline?freshSketch.Splines[0].Id:freshSketch.Beziers[0].Id;
                await using(var reselect=new ExtrusionEditorViewModel(document,kernel,feature.Id))
                {
                    var host=new ExtrusionEditorWindow(reselect){Owner=window};
                    try
                    {
                        host.Show();await Idle();var picker=(ComboBox)host.FindName("ProfilePicker");Check(picker.IsEnabled,"Frozen extrusion cannot explicitly reselect its profile");
                        picker.SelectedItem=reselect.Profiles.Single(p=>p.Source?.SketchId==sketch.Id&&(spline?p.Source.SplineId:p.Source.BezierId)==replacement);await Idle();
                        await ClickButton((Button)host.FindName("PreviewButton"));if(reselect.PreviewCommand.ExecutionTask is {} preview)await preview;
                        Check(reselect.CanConfirm,reselect.Status);Check(ReferenceEquals(frozen,session.Snapshot),"Reselection preview committed");
                        Save(host,Path.Combine(output,$"reselect-{kind}-{language}.png"));
                        await ClickButton((Button)host.FindName("ConfirmButton"));if(reselect.ConfirmCommand.ExecutionTask is {} confirm)await confirm;await Idle();
                        var accepted=session.Snapshot.Features[feature.Id];Check(!accepted.IsStale&&accepted.SketchSource?.SketchId==sketch.Id&&
                            (spline?accepted.SketchSource.SplineId:accepted.SketchSource.BezierId)==replacement,"Production profile reselection did not restore the same feature");
                        await session.UndoAsync();Check(ReferenceEquals(frozen,session.Snapshot),"Reselection undo");
                        await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Split undo");
                    }
                    finally{host.Close();await Idle();}
                }
            }
            await session.SaveAsync(storage,Path.Combine(output,"final-baseline.cadoryx"));
            Check(await main.CloseAllAsync(),"Close all documents");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Author/preview assets were not released");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"B9 production sketch entity selection, knots/rejected input, 17-control count, exact Bezier/spline retained/removed interval and polynomial extension in en-US/zh-CN/ja-JP; linked native solids, explicit split freeze warning and advanced extrusion profile reselection, preview/cancel/confirm, undo/redo, save/reopen and zero remaining assets. Physical pointer is separate and unverified.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=original;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
}
