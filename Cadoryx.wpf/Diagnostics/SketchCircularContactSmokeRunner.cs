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

internal static class SketchCircularContactSmokeRunner
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
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})for(int kind=0;kind<4;kind++)
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {language}: contact {kind}{Environment.NewLine}");
                var d=new SketchDraft(CadSketch.Create(part,"Contact "+kind,RigidTransform3d.Identity));SketchEntityId a,b;CircularContactBranch branch;
                if(kind is 0 or 3)
                {
                    d.AddLine(new(-5,0),new(5,0));d.AddArc(new(-2,2),new(0,kind==3?4:0),new(2,2));
                    a=d.Value.Arcs[0].Id;b=d.Value.Lines[0].Id;branch=CircularContactBranch.LineLeft;
                }
                else
                {
                    double ra=kind==1?5:2,rb=kind==1?2:5,cx=kind==1?7:3;
                    d.AddArc(new(0,-ra),new(kind==1?ra:-ra,0),new(0,ra));d.AddArc(new(cx,rb),new(cx-rb,0),new(cx,-rb));
                    a=d.Value.Arcs[0].Id;b=d.Value.Arcs[1].Id;branch=kind==1?CircularContactBranch.External:CircularContactBranch.BContainsA;
                }
                d.Change(s=>s with{Constraints=s.Points.Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)).ToImmutableArray()});
                await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var sketch=session.Snapshot.Sketches[d.Value.Id];var baseline=session.Snapshot;
                await using(var editor=new SketchEditorViewModel(doc,kernel,solver,sketch,false))
                {
                    var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();
                    try
                    {
                        editor.Select(a);editor.Select(b,true);host.ConstraintKindPicker.SelectedValue="CircularContact";host.ContactBranchPicker.SelectedValue=branch;
                        await ClickButton(host.AddConstraintButton);Check(editor.Sketch.Constraints.OfType<CircularContactConstraint>().Single().A==a,"First operand identity changed");
                        Check(editor.ShowContactBranch&&editor.ContactBranch==branch,"Real branch control failed");
                        async Task Preview(){await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} task)await task;await Idle();}
                        await Preview();host.ContactBranchPicker.BringIntoView();host.Canvas.Fit();await Idle();
                        if(kind==3)
                        {
                            Check(!editor.CanConfirm&&editor.Report?.Diagnostic=="SKETCH.CONTACT_OUTSIDE_DOMAIN","Arc-outside support contact accepted");
                            Check(editor.Status==Strings.ContactOutsideDomain,"Finite-domain failure not localized");
                        }
                        else
                        {
                            Check(editor.CanConfirm,editor.Status);SketchCircularContact.RequireSolvedContacts(editor.Sketch);
                            var relation=editor.Sketch.Constraints.OfType<CircularContactConstraint>().Single();
                            Check(relation.A==a&&relation.B==b&&relation.Branch==branch,"Author intent changed after solve");
                            host.ConstraintList.SelectedItem=editor.Constraints.Single(c=>c.Id==relation.Id);await Idle();
                        }
                        Check(ReferenceEquals(baseline,session.Snapshot),"Preview committed");
                        Save(host,Path.Combine(output,$"contact-{kind}-{language}.png"));
                        if(kind==3){await ClickButton(host.CancelButton);await Idle();}
                        else
                        {
                            // Failed branch edits must invalidate the previously prepared successful candidate.
                            host.ContactBranchPicker.SelectedValue=kind==0?CircularContactBranch.LineRight:kind==1?CircularContactBranch.AContainsB:CircularContactBranch.External;
                            await ClickButton(host.ApplyContactBranchButton);await Preview();Check(!editor.CanConfirm&&ReferenceEquals(baseline,session.Snapshot),"Wrong branch published");
                            host.ContactBranchPicker.SelectedValue=branch;await ClickButton(host.ApplyContactBranchButton);await Preview();Check(editor.CanConfirm,editor.Status);
                            await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;await Idle();
                        }
                    }
                    finally{host.Close();await Idle();}
                }
                if(kind==3){Check(ReferenceEquals(baseline,session.Snapshot),"Finite failure/cancel changed document");continue;}
                var accepted=session.Snapshot;SketchCircularContact.RequireSolvedContacts(accepted.Sketches[sketch.Id]);
                Check(accepted.Sketches[sketch.Id].Points.Select(p=>p.Id).SequenceEqual(sketch.Points.Select(p=>p.Id)),"Contact merged identities");
                await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Contact undo");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"Contact redo");
                string path=Path.Combine(output,$"contact-{kind}-{language}.cadoryx");await session.SaveAsync(storage,path);
                using(var loaded=await storage.LoadAsync(path,session.Assets)){loaded.Snapshot.Validate();SketchCircularContact.RequireSolvedContacts(loaded.Snapshot.Sketches[sketch.Id]);}
            }
            await session.SaveAsync(storage,Path.Combine(output,"baseline.cadoryx"));Check(await main.CloseAllAsync(),"Close all contact documents");
            await ((App)Application.Current).StopRecoveryAsync();await Idle();Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Contact assets remain");
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"B12 finite line-arc/external arc-arc/internal B-contains-A and arc-outside refusal in en-US/zh-CN/ja-JP. Actual constraint/branch controls and WPF Automation buttons, ordered distinct entity identities, preview, wrong branch rejection, confirm/cancel, undo/redo, save/reopen and zero assets. Physical input separate.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);window.CloseAfterSmoke();}
        finally{CultureInfo.CurrentUICulture=original;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
