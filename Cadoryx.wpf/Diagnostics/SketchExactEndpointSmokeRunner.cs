using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Antelcat.I18N.WPF;
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

internal static class SketchExactEndpointSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var original=CultureInfo.CurrentUICulture;var main=(MainWindowViewModel)window.DataContext;var markup=CultureInfo.GetCultureInfo(main.CurrentCultureLCID);
        try
        {
            await Idle();var doc=main.ActiveDocument!;var session=doc.Session;var kernel=services.GetRequiredService<IGeometryKernel>();
            var solver=services.GetRequiredService<ISketchConstraintSolver>();var storage=services.GetRequiredService<IDocumentStorage>();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(80,0,0)),"Support"));var part=session.Snapshot.Features.Values.Single().PartId;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                for(int kind=0;kind<3;kind++)
                {
                    File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {language}: finite boundary {kind}{Environment.NewLine}");
                    var draft=new SketchDraft(CadSketch.Create(part,$"B11 endpoints {kind} {language}",RigidTransform3d.Identity));SketchEntityId id;
                    if(kind==0){draft.AddEllipse(new(new(20,0),new(5,0),new(0,2),0,Math.PI));id=draft.Value.Ellipses[0].Id;}
                    else
                    {
                        var geometry=kind==1?new SketchFreeformGeometry(SketchFreeformKind.Bezier,[new(10,0),new(15,5),new(20,0)],[1,.7,1],[],[],2,false,0,1):
                            new(SketchFreeformKind.BSpline,[new(10,0),new(12,4),new(14,6),new(16,3),new(18,0)],[1,.8,1.2,.7,1],[0,.45,1],[4,1,4],3,false,.1,.9);
                        draft.AddFreeform(geometry);id=draft.Value.Freeforms[0].Id;
                    }
                    var baseline=session.Snapshot;await using(var editor=new SketchEditorViewModel(doc,kernel,solver,draft.Value,true))
                    {
                        var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();
                        try
                        {
                            editor.Select(id);host.ProjectionSection.IsExpanded=true;host.ExposeCurveEndpointsButton.BringIntoView();await Idle();
                            Check(Equals(host.ExposeCurveEndpointsButton.Content,Strings.ExposeCurveEndpoints),"Endpoint control translation");await ClickButton(host.ExposeCurveEndpointsButton);
                            Check(editor.Sketch.Points.Length==2&&editor.Sketch.Constraints.Length==2,"Explicit endpoint control");
                            await ClickButton(host.UndoButton);Check(editor.Sketch.Points.IsEmpty,"Endpoint draft undo");editor.Select(id);await ClickButton(host.ExposeCurveEndpointsButton);
                            var ends=SketchFixedCurveEndpoints.All(editor.Sketch).Single().Ends;var(a,b)=SketchFixedCurveEndpoints.Positions(editor.Sketch,id);
                            host.ToolPicker.SelectedValue="Line";await Idle();editor.Snap=true;host.Canvas.Fit();
                            // This is the same pointer-down path used by WPF, including endpoint snapping.
                            foreach(var(p,q) in new[]{(b,new Point2d(b.X,-2)),(new Point2d(b.X,-2),new Point2d(a.X,-2)),(new Point2d(a.X,-2),a)})
                            {
                                host.Canvas.PointerPressed(host.Canvas.ToScreen(p),MouseButton.Left,ModifierKeys.None);await Idle();
                                host.Canvas.PointerPressed(host.Canvas.ToScreen(q),MouseButton.Left,ModifierKeys.None);await Idle();
                            }
                            Check(editor.Sketch.Lines.Length==3,editor.Status);Check(editor.Sketch.Lines[0].Start==ends.End&&editor.Sketch.Lines[^1].End==ends.Start,"Canvas did not share exact endpoint IDs");
                            Check(SketchMixedLoops.Find(editor.Sketch).Length==1,"Finite mixed profile unavailable");host.Canvas.Fit();await Idle();Save(host,Path.Combine(output,$"endpoints-{kind}-{language}.png"));
                            await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} preview)await preview;Check(editor.CanConfirm,editor.Status);Check(ReferenceEquals(baseline,session.Snapshot),"Endpoint preview published");
                            await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;var accepted=session.Snapshot;
                            await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Endpoint document undo");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"Endpoint document redo");
                        }
                        finally{host.Close();await Idle();}
                    }
                    var sketch=session.Snapshot.Sketches[draft.Value.Id];var ids=SketchMixedLoops.Find(sketch).Single();var reference=SketchProfileReference.CreateMixed(sketch,ids);
                    await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,sketch.Plane)),"Mixed endpoint solid",part,sketchSource:reference));
                    Check(session.Snapshot.Features.Values.Single(f=>f.SketchSource?.SketchId==sketch.Id).Result.VolumeMm3>0,"Native finite boundary solid");
                    string file=Path.Combine(output,$"endpoints-{kind}-{language}.cadoryx");await session.SaveAsync(storage,file);
                    using(var loaded=await storage.LoadAsync(file,session.Assets)){Check(SketchFixedCurveEndpoints.All(loaded.Snapshot.Sketches[sketch.Id]).Single().Ends==SketchFixedCurveEndpoints.All(sketch).Single().Ends,"Endpoint storage identity");loaded.Snapshot.Validate();}
                }
            }
            Check(await main.CloseAllAsync(),"Close all endpoint documents");await Idle();Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Endpoint assets remain");
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"B11 finite ellipse, rational Bezier and nonuniform BSpline endpoint controls in en-US/zh-CN/ja-JP, draft undo, canvas PointerPressed path with shared endpoint snapping, exact mixed closed boundary, preview/confirm, document undo/redo, native solids, save/reopen and zero remaining assets. Canvas input uses the production pointer API; physical input is separate.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);window.CloseAfterSmoke();}
        finally{CultureInfo.CurrentUICulture=original;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
