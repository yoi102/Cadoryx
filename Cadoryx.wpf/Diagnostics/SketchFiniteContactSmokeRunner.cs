using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
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

internal static class SketchFiniteContactSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var original=CultureInfo.CurrentUICulture;var main=(MainWindowViewModel)window.DataContext;var markup=CultureInfo.GetCultureInfo(main.CurrentCultureLCID);
        void Trace(string message)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        try
        {
            await Idle();var document=main.ActiveDocument!;var session=document.Session;var kernel=services.GetRequiredService<IGeometryKernel>();
            var solver=services.GetRequiredService<ISketchConstraintSolver>();var storage=services.GetRequiredService<IDocumentStorage>();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(80,0,0)),"Support"));var part=session.Snapshot.Features.Values.Single().PartId;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})for(int kind=0;kind<4;kind++)
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;Trace(language+" finite contact "+kind);
                var draft=new SketchDraft(CadSketch.Create(part,"Finite contact "+kind,RigidTransform3d.Identity));
                draft.AddBezier(new(-4,0),new(0,4),new(4,0));var first=draft.Value.Beziers[0].Id;var fixedIds=draft.Value.Points.Select(p=>p.Id).ToHashSet();
                bool reverse=kind is 1 or 3;SketchEntityId second;
                if(kind==0){draft.AddLine(new(-3,2.2),new(3,2.3));second=draft.Value.Lines[0].Id;}
                else if(kind==1){draft.AddBezier(new(3,1.8),new(0,2.8),new(-3,1.8));second=draft.Value.Beziers[^1].Id;}
                else
                {
                    var geometry=kind==3?new CubicSplineDefinition([new(-3,1),new(-2,2),new(0,3),new(2,2),new(3,1)],[2,2.5,5],[4,1,4]):
                        CubicSplineDefinition.Uniform([new(-3,1),new(-1,2.8),new(1,2.8),new(3,1)]);
                    draft.AddSpline(reverse?geometry.Reverse():geometry);second=draft.Value.Splines[0].Id;
                }
                draft.Change(s=>s with{Constraints=s.Points.Where(p=>fixedIds.Contains(p.Id)).Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)).ToImmutableArray()});
                await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));var seed=session.Snapshot.Sketches[draft.Value.Id];var baseline=session.Snapshot;
                await using(var editor=new SketchEditorViewModel(document,kernel,solver,seed,false))
                {
                    var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();bool expectedClose=false;
                    host.Closed+=(_,_)=>Trace($"editor closed; expected={expectedClose}; language={language}; kind={kind}");
                    try
                    {
                        editor.Select(first);editor.Select(second,true);host.ConstraintKindPicker.SelectedValue="FiniteParametricContact";
                        host.ContactParameterAInput.Value=.5;host.ContactParameterBInput.Value=.5;
                        var branch=reverse?FiniteContactTangentBranch.OppositeDirection:FiniteContactTangentBranch.SameDirection;
                        host.FiniteContactBranchPicker.SelectedValue=branch;await ClickButton(host.AddConstraintButton);
                        var relation=editor.Sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();
                        Check(editor.ShowFiniteContact&&relation.A==first&&relation.B==second&&relation.Branch==branch,"Actual finite contact controls lost author intent.");
                        async Task Preview(){await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} task)await task;await Idle();}
                        await Preview();Check(editor.CanConfirm,editor.Status);SketchFiniteParametricContact.RequireSolvedRelations(editor.Sketch);
                        Check(ReferenceEquals(baseline,session.Snapshot),"Preview committed.");
                        host.ConstraintList.SelectedItem=editor.Constraints.Single(c=>c.Id==relation.Id);await Idle();
                        // Input edits invalidate prepared geometry before Apply. Endpoints
                        // are rejected atomically rather than clamped to a different contact.
                        var beforeInvalid=editor.Sketch;host.ContactParameterAInput.Value=0;await Idle();Check(!editor.CanConfirm,"Changing parameter retained prepared geometry.");
                        await ClickButton(host.ApplyFiniteContactButton);Check(ReferenceEquals(beforeInvalid,editor.Sketch),"Invalid endpoint partially changed the draft.");
                        host.ContactParameterAInput.Value=.46;host.ContactParameterBInput.Value=.54;await ClickButton(host.ApplyFiniteContactButton);
                        await Preview();Check(editor.CanConfirm,editor.Status);SketchFiniteParametricContact.RequireSolvedRelations(editor.Sketch);
                        Check(editor.Sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single().ParameterA==.46,"Explicit parameter substituted.");
                        host.FiniteContactBranchPicker.SelectedValue=reverse?FiniteContactTangentBranch.SameDirection:FiniteContactTangentBranch.OppositeDirection;
                        await ClickButton(host.ApplyFiniteContactButton);await Preview();Check(!editor.CanConfirm&&ReferenceEquals(baseline,session.Snapshot),"Wrong branch published.");
                        host.FiniteContactBranchPicker.SelectedValue=branch;await ClickButton(host.ApplyFiniteContactButton);await Preview();Check(editor.CanConfirm,editor.Status);
                        host.FiniteContactInputs.BringIntoView();host.Canvas.Fit();await Idle();
                        Check(host.ApplyFiniteContactButton.Content?.ToString()==Strings.FiniteContactApply,"Apply label not localized.");
                        Save(host,Path.Combine(output,$"finite-{kind}-{language}.png"));
                        expectedClose=true;await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;await Idle();
                    }
                    finally{expectedClose=true;host.Close();await Idle();}
                }
                var accepted=session.Snapshot;SketchFiniteParametricContact.RequireSolvedRelations(accepted.Sketches[seed.Id]);
                Check(accepted.Sketches[seed.Id].Points.Select(p=>p.Id).SequenceEqual(seed.Points.Select(p=>p.Id)),"Point identities merged.");
                await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Contact undo.");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"Contact redo.");
                string path=Path.Combine(output,$"finite-{kind}-{language}.cadoryx");await session.SaveAsync(storage,path);
                using(var loaded=await storage.LoadAsync(path,session.Assets)){loaded.Snapshot.Validate();SketchFiniteParametricContact.RequireSolvedRelations(loaded.Snapshot.Sketches[seed.Id]);}
                Trace(language+" finite contact "+kind+" PASS");
            }
            await session.SaveAsync(storage,Path.Combine(output,"baseline.cadoryx"));Check(await main.CloseAllAsync(),"Close documents.");
            await ((App)Application.Current).StopRecoveryAsync();await Idle();Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Assets remain.");
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"B14 explicit interior parameters and tangent directions, line/quadratic Bezier/uniform and nonuniform cubic author curves in three languages; actual parameter/branch controls, preview invalidation, endpoint/wrong-branch rejection, confirm, undo/redo, save/reopen, distinct identities and zero assets. Physical input separate.");window.CloseAfterSmoke();
        }
        catch(Exception error){Trace("FAIL: "+error);await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+error);window.CloseAfterSmoke();}
        finally{CultureInfo.CurrentUICulture=original;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
