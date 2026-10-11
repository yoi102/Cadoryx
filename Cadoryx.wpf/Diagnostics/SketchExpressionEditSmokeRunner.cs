using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class SketchExpressionEditSmokeRunner
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
            var solver=services.GetRequiredService<ISketchConstraintSolver>();var storage=services.GetRequiredService<IDocumentStorage>();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(80,0,0)),"Support"));var part=session.Snapshot.Features.Values.Single().PartId;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})foreach(string operation in new[]{"trim","split","extend","offset"})
            {
                main.ChangeCultureCommand.Execute(CultureInfo.GetCultureInfo(language).LCID.ToString(CultureInfo.InvariantCulture));await Idle();
                File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {language}: {operation}{Environment.NewLine}");
                var draft=new SketchDraft(CadSketch.Create(part,$"Expression {operation} {language}",RigidTransform3d.Identity));
                draft.AddLine(new(0,0),new(8,0),construction:true);var id=draft.AddExactOffset(draft.Value.Lines.Single().Id,.1);draft.SetExpressionConstruction(id,false);
                if(operation is "trim" or "split")draft.AddLine(new(2,-2),new(2,2),construction:true);
                if(operation=="split")draft.AddLine(new(6,-2),new(6,2),construction:true);
                if(operation=="extend")draft.AddLine(new(10,-2),new(10,2),construction:true);
                var sourcePoints=draft.Value.Lines.SelectMany(l=>new[]{l.Start,l.End}).ToHashSet();
                draft.Change(s=>s with{Constraints=s.Constraints.AddRange(s.Points.Where(p=>sourcePoints.Contains(p.Id)).Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)))});
                var baseline=session.Snapshot;await using(var editor=new SketchEditorViewModel(doc,kernel,solver,draft.Value,true))
                {
                    var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();
                    try
                    {
                        host.ToolPicker.SelectedValue=operation=="offset"?"OffsetCurve":operation=="extend"?"ExtendCurve":"TrimCurve";
                        if(operation=="offset")host.OffsetDistanceInput.Value=.2;
                        host.Canvas.Fit();await Idle();var before=editor.Sketch;
                        var click=operation switch{"trim"=>new Point2d(1,-.1),"extend"=>new(7.5,-.1),_=>new(4,-.1)};
                        host.Canvas.PointerPressed(host.Canvas.ToScreen(click),MouseButton.Left,ModifierKeys.None);await Idle();
                        Check(!ReferenceEquals(before,editor.Sketch),$"{operation} production ToolPicker/PointerPressed did not edit: {editor.Status}");
                        if(operation=="split")Check(editor.Sketch.ExpressionCurves.Length==2&&!editor.Sketch.ExpressionCurves.Any(c=>c.Id==id),"Split retained old author identity");
                        else if(operation=="offset")Check(editor.Sketch.ExpressionCurves.Length==2&&Math.Abs(editor.Sketch.ExpressionCurves[^1].Geometry.At(.5).Y-.1)<1e-8,"Offset field/left side binding");
                        else
                        {
                            var c=editor.Sketch.ExpressionCurves.Single();Check(c.Id==id,"Finite edit replaced curve identity");
                            Check(Math.Abs((operation=="trim"?c.Geometry.At(0).X:c.Geometry.At(1).X)-(operation=="trim"?2:10))<1e-8,"Finite native interval");
                        }
                        var after=editor.Sketch;await ClickButton(host.UndoButton);Check(SketchExpressionEntities.SameTable(before,editor.Sketch),"One operation undo");editor.RedoCommand.Execute(null);Check(SketchExpressionEntities.SameTable(after,editor.Sketch),"One operation redo");
                        host.Canvas.Fit();await Idle();Save(host,Path.Combine(output,$"edit-{operation}-{language}.png"));
                        await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} preview)await preview;Check(editor.CanConfirm,editor.Status);
                        Check(ReferenceEquals(baseline,session.Snapshot),"Preview committed");SketchAssociativeEndpoints.RequireSolved(editor.Sketch);
                        await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;var accepted=session.Snapshot;
                        Check(!ReferenceEquals(accepted,baseline),"Confirm did not publish");await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Document undo");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"Document redo");
                    }finally{host.Close();await Idle();}
                }
                var stored=session.Snapshot.Sketches[draft.Value.Id];string file=Path.Combine(output,$"edit-{operation}-{language}.cadoryx");await session.SaveAsync(storage,file);
                using(var loaded=await storage.LoadAsync(file,session.Assets))
                {Check(SketchExpressionEntities.SameTable(stored,loaded.Snapshot.Sketches[stored.Id]),"Stored author/endpoint rows");Check(stored.Points.SequenceEqual(loaded.Snapshot.Sketches[stored.Id].Points),"Stored point origins");}
                await using(var editor=new SketchEditorViewModel(doc,kernel,solver,stored,false))
                {
                    var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();
                    try{var baselineCancel=session.Snapshot;await ClickButton(host.CancelButton);Check(ReferenceEquals(baselineCancel,session.Snapshot)&&doc.PreviewScene is null,"Actual cancel altered document");}
                    finally{host.Close();await Idle();}
                }
            }
            main.ChangeCultureCommand.Execute(original.ToString(CultureInfo.InvariantCulture));Check(await main.CloseAllAsync(),"Close documents");await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Assets remain");
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"12 actual en-US/zh-CN/ja-JP ToolPicker and Canvas.PointerPressed trim/split/extend/offset workflows; source/fragment/endpoint identities, actual offset field, undo/redo, preview/confirm/cancel, document undo/redo, save/reopen point origins and zero assets. Physical input and generic region offsets are separate.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);System.Windows.Application.Current.Shutdown(1);}
        finally{main.ChangeCultureCommand.Execute(original.ToString(CultureInfo.InvariantCulture));PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
