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
using Strings=Cadoryx.Lang.Strings.Strings;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class SketchExpressionAuthorSmokeRunner
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
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
            {
                main.ChangeCultureCommand.Execute(CultureInfo.GetCultureInfo(language).LCID.ToString(CultureInfo.InvariantCulture));await Idle();
                for(int kind=0;kind<2;kind++)
                {
                    File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {language}: expression author {kind}{Environment.NewLine}");
                    var draft=new SketchDraft(CadSketch.Create(part,$"Expression {kind} {language}",RigidTransform3d.Identity));SketchEntityId source;
                    if(kind==0){draft.AddCircle(new(8,1),2,construction:true);source=draft.Value.Circles[0].Id;}
                    else{draft.AddBezier(new(4,0),new(8,3),new(12,0),construction:true);source=draft.Value.Beziers[0].Id;}
                    draft.Change(s=>s with{Constraints=[..s.Constraints,..s.Points.Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position))]});
                    var baseline=session.Snapshot;await using(var editor=new SketchEditorViewModel(doc,kernel,solver,draft.Value,true))
                    {
                        var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();
                        try
                        {
                            editor.Select(source);host.ExpressionSection.IsExpanded=true;host.ExactOffsetDistanceInput.Value=.5;await Idle();
                            Check(Equals(host.CreateExactOffsetButton.Content,Strings.CreateExactOffset),"Expression create translation");await ClickButton(host.CreateExactOffsetButton);
                            var c=editor.Sketch.ExpressionCurves.Single();var ends=c.Endpoints!;Check(!c.IsConstruction&&ends is not null,"Expression boundary authors missing");
                            Check(editor.Entities.Any(e=>e.Id==c.Id&&e.Label.Contains(Strings.ExactAssociatedOffset)),"Expression entity label");
                            host.Canvas.Fit();await Idle();editor.Select(null);host.ToolPicker.SelectedValue="Select";await Idle();
                            host.Canvas.PointerPressed(host.Canvas.ToScreen(c.Geometry.At(.37)),MouseButton.Left,ModifierKeys.Control);await Idle();
                            Check(editor.SelectedEntities.SequenceEqual(new[]{c.Id}),"Rendered expression cannot be selected through the production pointer path");
                            await ClickButton(host.UndoButton);Check(editor.Sketch.ExpressionCurves.IsEmpty,"One undo must remove the entire offset author");editor.RedoCommand.Execute(null);editor.Select(c.Id);await Idle();
                            host.ExpressionDistanceInput.Value=.6;await Idle();await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} preview)await preview;
                            Check(editor.CanConfirm,editor.Status);Check(ReferenceEquals(baseline,session.Snapshot),"Expression preview published");
                            Check(editor.Sketch.ExpressionCurves.Single().Geometry.SignedOffset==.6,"Pending distance binding not applied");SketchAssociativeEndpoints.RequireSolved(editor.Sketch);
                            await ClickButton(host.FreezeExpressionButton);Check(editor.ExpressionSourceStatus==Strings.ExpressionSourceFrozen,"Freeze binding");
                            await ClickButton(host.ResumeExpressionButton);Check(editor.ExpressionSourceStatus==Strings.ExpressionSourceActive,"Resume binding");
                            await ClickButton(host.DetachExpressionButton);Check(editor.ExpressionSourceStatus==Strings.ExpressionSourceDetached,"Detach binding");await ClickButton(host.UndoButton);
                            Check(editor.Sketch.ExpressionCurves.Single().Endpoints==ends,"Author controls replaced endpoint identities");
                            host.ExpressionSection.BringIntoView();host.Canvas.Fit();await Idle();Save(host,Path.Combine(output,$"expression-{kind}-{language}.png"));
                            await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} ready)await ready;Check(editor.CanConfirm,editor.Status);
                            await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;
                            var accepted=session.Snapshot;await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Author document undo");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"Author document redo");
                        }finally{host.Close();await Idle();}
                    }
                    var sketch=session.Snapshot.Sketches[draft.Value.Id];var id=sketch.ExpressionCurves.Single().Id;var reference=SketchProfileReference.CreateMixed(sketch,[id]);
                    await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,sketch.Plane)),"Expression solid",part,sketchSource:reference));
                    string file=Path.Combine(output,$"expression-{kind}-{language}.cadoryx");await session.SaveAsync(storage,file);
                    using(var loaded=await storage.LoadAsync(file,session.Assets))Check(SketchExpressionEntities.SameTable(sketch,loaded.Snapshot.Sketches[sketch.Id]),"Author storage identity");
                    var beforeCancel=session.Snapshot;await using(var editor=new SketchEditorViewModel(doc,kernel,solver,sketch,false))
                    {
                        var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();
                        try
                        {
                            editor.Select(id);editor.ExpressionDistance=.7;await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} cancelPreview)await cancelPreview;Check(editor.CanConfirm,editor.Status);
                            await ClickButton(host.CancelButton);Check(ReferenceEquals(beforeCancel,session.Snapshot)&&doc.PreviewScene is null,"Expression cancellation published");
                        }finally{host.Close();await Idle();}
                    }
                }
            }
            main.ChangeCultureCommand.Execute(original.ToString(CultureInfo.InvariantCulture));
            Check(await main.CloseAllAsync(),"Close expression documents");await Idle();Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Expression assets remain");
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"Six circle/Bezier author workflows with actual application language switching en-US/zh-CN/ja-JP, numeric bindings, rendered Canvas PointerPressed hit selection, single-step author undo/redo, source freeze/resume/detach, structural endpoint verification, preview/confirm/cancel, native linked solids, document undo/redo, save/reopen and zero assets. Canvas uses the production pointer API; physical input is separate.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);System.Windows.Application.Current.Shutdown(1);}
        finally{main.ChangeCultureCommand.Execute(original.ToString(CultureInfo.InvariantCulture));PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
