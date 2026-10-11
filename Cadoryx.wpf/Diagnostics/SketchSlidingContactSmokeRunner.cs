using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using Strings=Cadoryx.Lang.Strings.Strings;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class SketchSlidingContactSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output,FiniteContactParameterMode mode)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var main=(MainWindowViewModel)window.DataContext;var original=CultureInfo.CurrentUICulture;var markup=CultureInfo.GetCultureInfo(main.CurrentCultureLCID);
        void Trace(string message)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        bool solvesA=mode is FiniteContactParameterMode.SolveA or FiniteContactParameterMode.SolveBoth;
        bool solvesB=mode is FiniteContactParameterMode.SolveB or FiniteContactParameterMode.SolveBoth;
        try
        {
            await Idle();var kernel=services.GetRequiredService<IGeometryKernel>();var solver=services.GetRequiredService<ISketchConstraintSolver>();var storage=services.GetRequiredService<IDocumentStorage>();
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})foreach(string kind in new[]{"Circle","Arc","Ellipse","RationalSpline"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;Trace(language+" "+kind+" "+mode+" seed");
                if(language=="en-US"&&kind=="Circle")Check(main.ActiveDocument?.Session.Snapshot.Features.Count==0,"Initial diagnostic document not empty.");
                else{Check(await main.CloseAllAsync(),"Close prior saved document.");main.NewCommand.Execute(null);await Idle();}
                var document=main.ActiveDocument!;var session=document.Session;
                await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(-6,-6,-4)),"Support"));
                var part=session.Snapshot.Features.Values.Single().PartId;
                var(seed,a,b,branch)=SketchMixedFiniteContactSmokeRunner.Seed(part,kind);
                await session.ExecuteAsync(new UpsertSketchCommand(seed,solver));seed=session.Snapshot.Sketches[seed.Id];var baseline=session.Snapshot;
                await using(var editor=new SketchEditorViewModel(document,kernel,solver,seed,false))
                {
                    var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();bool expectedClose=false;
                    host.Closed+=(_,_)=>Trace($"editor closed; expected={expectedClose}; language={language}; kind={kind}; mode={mode}");
                    try
                    {
                        editor.Select(a);editor.Select(b,true);host.ConstraintKindPicker.SelectedValue="FiniteParametricContact";
                        host.ContactParameterAInput.Value=.37;host.ContactParameterBInput.Value=.5;
                        host.ContactParameterModePicker.SelectedValue=mode;await Idle();
                        host.ContactSeedAInput.Value=.34;host.ContactLowerAInput.Value=.2;host.ContactUpperAInput.Value=.6;
                        host.ContactSeedBInput.Value=.46;host.ContactLowerBInput.Value=.3;host.ContactUpperBInput.Value=.7;
                        host.FiniteContactBranchPicker.SelectedValue=branch;host.FiniteContactContinuityPicker.SelectedValue=FiniteContactContinuity.Curvature;
                        await ClickButton(host.AddConstraintButton);
                        var c=editor.Sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();
                        Check(c.ParameterMode==mode&&c.A==a&&c.B==b&&c.Branch==branch,"Actual mode controls lost author intent.");
                        CheckAuthors(host,editor,mode,solvesA,solvesB,.34,.46);
                        await Preview(host,editor);Check(editor.CanConfirm,editor.Status);SketchFiniteParametricContact.RequireSolvedRelations(editor.Sketch);
                        Check(ReferenceEquals(baseline,session.Snapshot),"Preview committed.");
                        host.ConstraintList.SelectedItem=editor.Constraints.Single(v=>v.Id==c.Id);await Idle();
                        CheckActual(host,editor,solvesA,solvesB);
                        // A fixed-mode round trip must not overwrite stored automatic seeds with actual values.
                        host.ContactParameterModePicker.SelectedValue=FiniteContactParameterMode.Fixed;await Idle();Check(!editor.CanConfirm,"Mode edit retained candidate.");
                        await ClickButton(host.ApplyFiniteContactButton);await Preview(host,editor);Check(editor.CanConfirm,editor.Status);
                        Check(host.ContactParameterAInput.IsEnabled&&host.ContactParameterBInput.IsEnabled,"Fixed result controls stayed read-only.");
                        await ClickButton(host.UndoButton);await Idle();Check(editor.ContactParameterMode==mode,"Mode undo lost automatic intent.");
                        await ClickButton(host.RedoButton);await Idle();Check(editor.ContactParameterMode==FiniteContactParameterMode.Fixed,"Mode redo.");
                        await ClickButton(host.UndoButton);await Preview(host,editor);Check(editor.CanConfirm,editor.Status);
                        host.FiniteContactContinuityPicker.SelectedValue=FiniteContactContinuity.Tangent;await ClickButton(host.ApplyFiniteContactButton);await Preview(host,editor);
                        Check(editor.CanConfirm&&editor.ContactContinuity==FiniteContactContinuity.Tangent,editor.Status);
                        host.FiniteContactContinuityPicker.SelectedValue=FiniteContactContinuity.Curvature;await ClickButton(host.ApplyFiniteContactButton);await Preview(host,editor);Check(editor.CanConfirm,editor.Status);
                        var beforeInvalid=editor.Sketch;
                        if(solvesA)host.ContactLowerAInput.Value=.45;else host.ContactLowerBInput.Value=.6;
                        await Idle();Check(!editor.CanConfirm,"Bound edit retained candidate.");await ClickButton(host.ApplyFiniteContactButton);
                        Check(ReferenceEquals(beforeInvalid,editor.Sketch)&&editor.Status==Strings.SlidingContactInvalidInterval,"Invalid interval changed draft or lost localized feedback.");
                        if(kind=="Ellipse"){host.FiniteContactInputs.BringIntoView();host.Canvas.Fit();await Idle();Save(host,Path.Combine(output,$"sliding-{mode}-{language}-limits.png"));}
                        if(solvesA)host.ContactLowerAInput.Value=.2;else host.ContactLowerBInput.Value=.3;
                        if(solvesA)host.ContactSeedAInput.Value=.39;if(solvesB)host.ContactSeedBInput.Value=.53;
                        await ClickButton(host.ApplyFiniteContactButton);await Preview(host,editor);Check(editor.CanConfirm,editor.Status);
                        host.FiniteContactBranchPicker.SelectedValue=branch==FiniteContactTangentBranch.SameDirection?FiniteContactTangentBranch.OppositeDirection:FiniteContactTangentBranch.SameDirection;
                        await ClickButton(host.ApplyFiniteContactButton);await Preview(host,editor);
                        Check(!editor.CanConfirm&&ReferenceEquals(baseline,session.Snapshot),"Wrong direction published.");
                        host.FiniteContactBranchPicker.SelectedValue=branch;await ClickButton(host.ApplyFiniteContactButton);await Preview(host,editor);Check(editor.CanConfirm,editor.Status);
                        CheckAuthors(host,editor,mode,solvesA,solvesB,.39,.53);CheckActual(host,editor,solvesA,solvesB);
                        Check(editor.FiniteContactParameterModes.Single(v=>v.Key==mode).Label==ModeLabel(mode),"Mode label not localized.");
                        if(kind=="Ellipse"||kind=="Arc"&&language=="en-US"||kind=="Circle"&&language=="zh-CN"||kind=="RationalSpline"&&language=="ja-JP")
                        {host.FiniteContactInputs.BringIntoView();host.Canvas.Fit();await Idle();Save(host,Path.Combine(output,$"sliding-{mode}-{kind}-{language}-author.png"));}
                        expectedClose=true;await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;await Idle();
                    }
                    finally{expectedClose=true;host.Close();await Idle();}
                }
                var accepted=session.Snapshot;var current=accepted.Sketches[seed.Id];var contact=current.Constraints.OfType<FiniteParametricContactConstraint>().Single();
                Check(contact.ParameterMode==mode&&contact.Continuity==FiniteContactContinuity.Curvature,"Confirm lost automatic G2 author.");
                SketchFiniteParametricContact.RequireSolvedRelations(current);
                Check(current.Points.Select(p=>p.Id).SequenceEqual(seed.Points.Select(p=>p.Id)),"Point IDs merged.");
                Check(current.Ellipses.SequenceEqual(seed.Ellipses)&&current.Freeforms.SequenceEqual(seed.Freeforms),"Copied curve definition changed.");
                await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Automatic contact undo.");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"Automatic contact redo.");
                var source=SketchProfileReference.CreateBezierSegment(current,b);
                await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,current.Plane)),"Sliding contact result",part,sketchSource:source));
                var feature=session.Snapshot.Features.Values.Single(f=>f.Name=="Sliding contact result");
                Check(Math.Abs(feature.Result.VolumeMm3-Expected(current,b))<1e-6,"Exact automatic profile volume mismatch.");
                var saved=session.Snapshot;int assetCount=((IAssetStoreStatistics)session.Assets).Count;
                await using(var editor=new SketchEditorViewModel(document,kernel,solver,current,false))
                {
                    var host=new SketchEditorWindow(editor){Owner=window};bool expectedClose=false;
                    host.Closed+=(_,_)=>Trace($"cancel/close editor closed; expected={expectedClose}; language={language}; kind={kind}; mode={mode}");host.Show();await Idle();
                    try
                    {
                        host.ConstraintList.SelectedItem=editor.Constraints.Single(v=>v.Id==contact.Id);await Idle();
                        if(solvesA)host.ContactSeedAInput.Value=.42;if(solvesB)host.ContactSeedBInput.Value=.55;
                        if(kind=="Ellipse")
                        {
                            // Close during the actual prepared body rebuild, not just after a finished preview.
                            var preview=editor.PreviewCommand.ExecuteAsync(null);Check(editor.IsWorking&&!preview.IsCompleted,"Preview did not enter running state.");
                            expectedClose=true;host.Close();await preview;await Idle();
                        }
                        else
                        {
                            await Preview(host,editor);Check(editor.CanConfirm,editor.Status);expectedClose=true;
                            if(kind=="Arc")host.Close();else await ClickButton(host.CancelButton);await Idle();
                        }
                    }
                    finally{expectedClose=true;host.Close();await Idle();}
                }
                Check(ReferenceEquals(saved,session.Snapshot)&&((IAssetStoreStatistics)session.Assets).Count==assetCount,"Cancel/close changed document or retained prepared assets.");
                if(kind=="Ellipse")
                {
                    await using var editor=new SketchEditorViewModel(document,kernel,solver,current,false);var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();
                    try
                    {
                        host.ConstraintList.SelectedItem=editor.Constraints.Single(v=>v.Id==contact.Id);await Preview(host,editor);Check(editor.CanConfirm,editor.Status);
                        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(50,50,50)),"Stale marker",part));await Idle();
                        Check(editor.IsStale&&!editor.CanConfirm&&!host.ConfirmButton.IsEnabled&&editor.Status==Strings.Stale,"Session change left confirmable preview.");
                        await session.UndoAsync();Check(ReferenceEquals(saved,session.Snapshot),"Stale marker undo.");
                    }
                    finally{host.Close();await Idle();}
                }
                string file=Path.Combine(output,$"sliding-{mode}-{kind}-{language}.cadoryx");await session.SaveAsync(storage,file);
                using(var reopened=await storage.LoadAsync(file,session.Assets))
                {
                    reopened.Snapshot.Validate();var restored=reopened.Snapshot.Sketches[seed.Id];SketchFiniteParametricContact.RequireSolvedRelations(restored);
                    Check(restored.Constraints.OfType<FiniteParametricContactConstraint>().Single()==contact,"Reopen lost mode/seed/bounds/actual.");
                }
                Trace(language+" "+kind+" "+mode+" PASS");
            }
            Check(await main.CloseAllAsync(),"Close final saved document.");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Assets remain.");
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+$"B16 {mode}: twelve Circle/Arc/Ellipse/RationalSpline authors in three languages; actual mode/seed/bound/read-only result controls, Fixed/automatic and G1/G2 round trips, invalid interval/direction, preview invalidation, confirm/undo/redo, native profile volume, cancel/close/running close/stale, save/reopen and zero assets. Physical input separate.");window.CloseAfterSmoke();
        }
        catch(Exception error){Trace("FAIL: "+error);await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+error);window.CloseAfterSmoke();}
        finally{CultureInfo.CurrentUICulture=original;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static async Task Preview(SketchEditorWindow host,SketchEditorViewModel editor)
    {await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} task)await task;await Idle();}
    private static void CheckAuthors(SketchEditorWindow host,SketchEditorViewModel editor,FiniteContactParameterMode mode,bool a,bool b,double seedA,double seedB)
    {
        var c=editor.Sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();Check(c.ParameterMode==mode,"Mode changed.");
        Check(host.SlidingContactAInputs.Visibility==(a?Visibility.Visible:Visibility.Collapsed)&&host.SlidingContactBInputs.Visibility==(b?Visibility.Visible:Visibility.Collapsed),"Automatic input visibility.");
        if(a)Check(c.SlidingA==new SlidingContactParameter(seedA,.2,.6),"A seed/bounds changed.");else Check(c.ParameterA==.37&&c.SlidingA is null,"Fixed A changed.");
        if(b)Check(c.SlidingB==new SlidingContactParameter(seedB,.3,.7),"B seed/bounds changed.");else Check(c.ParameterB==.5&&c.SlidingB is null,"Fixed B changed.");
    }
    private static void CheckActual(SketchEditorWindow host,SketchEditorViewModel editor,bool a,bool b)
    {
        var c=editor.Sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        Check(host.ContactParameterAInput.IsEnabled==!a&&host.ContactParameterBInput.IsEnabled==!b,"Actual results are editable on an automatic side.");
        Check(host.ContactParameterAInput.Value==c.ParameterA&&host.ContactParameterBInput.Value==c.ParameterB,
            $"Actual controls display seeds or stale results: UI A={host.ContactParameterAInput.Value:R}, B={host.ContactParameterBInput.Value:R}; actual A={c.ParameterA:R}, B={c.ParameterB:R}; seed A={c.SlidingA?.Seed:R}, B={c.SlidingB?.Seed:R}.");
        c.SlidingA?.Validate(c.ParameterA);c.SlidingB?.Validate(c.ParameterB);
    }
    private static string ModeLabel(FiniteContactParameterMode mode)=>mode switch
    {FiniteContactParameterMode.SolveA=>Strings.SlidingContactSolveA,FiniteContactParameterMode.SolveB=>Strings.SlidingContactSolveB,_=>Strings.SlidingContactSolveBoth};
    private static double Expected(CadSketch s,SketchEntityId id)
    {
        var b=s.Beziers.Single(b=>b.Id==id);Point2d P(SketchEntityId point)=>s.Points.Single(p=>p.Id==point).Position;
        var a=P(b.Start);var e=P(b.End);var c=P(b.Control);return 2*Math.Abs((e.X-a.X)*(c.Y-a.Y)-(e.Y-a.Y)*(c.X-a.X))/3;
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
