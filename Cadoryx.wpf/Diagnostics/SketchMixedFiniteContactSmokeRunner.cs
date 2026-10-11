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

internal static class SketchMixedFiniteContactSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var main=(MainWindowViewModel)window.DataContext;var original=CultureInfo.CurrentUICulture;var markup=CultureInfo.GetCultureInfo(main.CurrentCultureLCID);
        void Trace(string message)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        try
        {
            await Idle();var kernel=services.GetRequiredService<IGeometryKernel>();var solver=services.GetRequiredService<ISketchConstraintSolver>();var storage=services.GetRequiredService<IDocumentStorage>();
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})foreach(string kind in new[]{"Circle","Arc","Ellipse","RationalSpline"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;Trace(language+" "+kind+" seed");
                if(language=="en-US"&&kind=="Circle")Check(main.ActiveDocument?.Session.Snapshot.Features.Count==0,"Initial diagnostic document not empty.");
                else{Check(await main.CloseAllAsync(),"Close prior saved document.");main.NewCommand.Execute(null);await Idle();}
                var document=main.ActiveDocument!;var session=document.Session;
                await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(-6,-6,-4)),"Support"));
                var part=session.Snapshot.Features.Values.Single().PartId;var(seed,a,b,branch)=Seed(part,kind);
                await session.ExecuteAsync(new UpsertSketchCommand(seed,solver));seed=session.Snapshot.Sketches[seed.Id];var baseline=session.Snapshot;
                await using(var editor=new SketchEditorViewModel(document,kernel,solver,seed,false))
                {
                    var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();bool expectedClose=false;
                    host.Closed+=(_,_)=>Trace($"editor closed; expected={expectedClose}; language={language}; kind={kind}");
                    try
                    {
                        editor.Select(a);editor.Select(b,true);host.ConstraintKindPicker.SelectedValue="FiniteParametricContact";
                        host.ContactParameterAInput.Value=.37;host.ContactParameterBInput.Value=.5;host.FiniteContactBranchPicker.SelectedValue=branch;
                        host.FiniteContactContinuityPicker.SelectedValue=FiniteContactContinuity.Curvature;await ClickButton(host.AddConstraintButton);
                        var c=editor.Sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();
                        Check(c.A==a&&c.B==b&&c.Continuity==FiniteContactContinuity.Curvature&&c.Branch==branch,"Actual controls lost G2 author intent.");
                        async Task Preview(){await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} task)await task;await Idle();}
                        await Preview();Check(editor.CanConfirm,editor.Status);SketchFiniteParametricContact.RequireSolvedRelations(editor.Sketch);
                        Check(ReferenceEquals(baseline,session.Snapshot),"Preview committed.");host.ConstraintList.SelectedItem=editor.Constraints.Single(v=>v.Id==c.Id);await Idle();
                        host.FiniteContactContinuityPicker.SelectedValue=FiniteContactContinuity.Tangent;await Idle();Check(!editor.CanConfirm,"Order edit retained prepared candidate.");
                        await ClickButton(host.ApplyFiniteContactButton);await Preview();Check(editor.CanConfirm,editor.Status);
                        Check(editor.Sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single().Continuity==FiniteContactContinuity.Tangent,"G1 selection was ignored.");
                        host.FiniteContactContinuityPicker.SelectedValue=FiniteContactContinuity.Curvature;await ClickButton(host.ApplyFiniteContactButton);await Preview();Check(editor.CanConfirm,editor.Status);
                        var beforeInvalid=editor.Sketch;host.ContactParameterAInput.Value=1;await Idle();Check(!editor.CanConfirm,"Parameter edit retained candidate.");
                        await ClickButton(host.ApplyFiniteContactButton);Check(ReferenceEquals(beforeInvalid,editor.Sketch),"Endpoint partially changed draft.");
                        Check(editor.Status==Strings.FiniteContactInvalidParameterOrder,"Endpoint feedback not localized.");
                        host.FiniteContactInputs.BringIntoView();host.Canvas.Fit();await Idle();Save(host,Path.Combine(output,$"mixed-{kind}-{language}-limits.png"));
                        host.ContactParameterAInput.Value=.41;await ClickButton(host.ApplyFiniteContactButton);await Preview();Check(editor.CanConfirm,editor.Status);
                        host.FiniteContactBranchPicker.SelectedValue=branch==FiniteContactTangentBranch.SameDirection?FiniteContactTangentBranch.OppositeDirection:FiniteContactTangentBranch.SameDirection;
                        await ClickButton(host.ApplyFiniteContactButton);await Preview();Check(!editor.CanConfirm&&ReferenceEquals(baseline,session.Snapshot),"Wrong direction published.");
                        host.FiniteContactBranchPicker.SelectedValue=branch;await ClickButton(host.ApplyFiniteContactButton);await Preview();Check(editor.CanConfirm,editor.Status);
                        SketchFiniteParametricContact.RequireSolvedRelations(editor.Sketch);
                        Check(editor.ContactParameterA==.41&&editor.ContactParameterB==.5&&editor.ContactContinuity==FiniteContactContinuity.Curvature,"Explicit author inputs substituted.");
                        Check(editor.FiniteContactContinuities.Single(v=>v.Key==FiniteContactContinuity.Curvature).Label==Strings.FiniteContactG2,"Order label not localized.");
                        host.FiniteContactInputs.BringIntoView();host.Canvas.Fit();await Idle();Save(host,Path.Combine(output,$"mixed-{kind}-{language}-author.png"));
                        expectedClose=true;await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;await Idle();
                    }
                    finally{expectedClose=true;host.Close();await Idle();}
                }
                var accepted=session.Snapshot;var current=accepted.Sketches[seed.Id];SketchFiniteParametricContact.RequireSolvedRelations(current);
                Check(current.Points.Select(p=>p.Id).SequenceEqual(seed.Points.Select(p=>p.Id)),"Point IDs merged.");
                Check(current.Ellipses.SequenceEqual(seed.Ellipses)&&current.Freeforms.SequenceEqual(seed.Freeforms),"Copied curve numeric definition changed.");
                await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"G2 undo.");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"G2 redo.");
                var source=SketchProfileReference.CreateBezierSegment(current,b);
                await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,current.Plane)),"Mixed contact result",part,sketchSource:source));
                var feature=session.Snapshot.Features.Values.Single(f=>f.Name=="Mixed contact result");
                Check(Math.Abs(feature.Result.VolumeMm3-Expected(current,b))<1e-6,"Exact G2 profile volume mismatch.");
                var saved=session.Snapshot;int assets=((IAssetStoreStatistics)session.Assets).Count;
                await using(var editor=new SketchEditorViewModel(document,kernel,solver,current,false))
                {
                    var host=new SketchEditorWindow(editor){Owner=window};bool expectedClose=false;
                    host.Closed+=(_,_)=>Trace($"cancel editor closed; expected={expectedClose}; language={language}; kind={kind}");host.Show();await Idle();
                    try
                    {
                        host.ConstraintList.SelectedItem=editor.Constraints.Single(v=>v.Id==current.Constraints.OfType<FiniteParametricContactConstraint>().Single().Id);await Idle();
                        host.FiniteContactContinuityPicker.SelectedValue=FiniteContactContinuity.Tangent;await ClickButton(host.ApplyFiniteContactButton);
                        await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} preview)await preview;await Idle();Check(editor.CanConfirm,editor.Status);
                        expectedClose=true;await ClickButton(host.CancelButton);await Idle();
                    }
                    finally{expectedClose=true;host.Close();await Idle();}
                }
                Check(ReferenceEquals(saved,session.Snapshot)&&((IAssetStoreStatistics)session.Assets).Count==assets,"Cancel changed document or retained assets.");
                string file=Path.Combine(output,$"mixed-{kind}-{language}.cadoryx");await session.SaveAsync(storage,file);
                using(var reopened=await storage.LoadAsync(file,session.Assets))
                {
                    reopened.Snapshot.Validate();SketchFiniteParametricContact.RequireSolvedRelations(reopened.Snapshot.Sketches[seed.Id]);
                    Check(reopened.Snapshot.Sketches[seed.Id].Constraints.OfType<FiniteParametricContactConstraint>().Single().Continuity==FiniteContactContinuity.Curvature,"Reopen downgraded G2.");
                }
                Trace(language+" "+kind+" PASS");
            }
            Check(await main.CloseAllAsync(),"Close final saved document.");await ((App)Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Assets remain.");
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"B15 twelve mixed Circle/Arc/Ellipse/RationalSpline G2 authors in three languages; actual G1/G2 controls, explicit parameters/directions, preview invalidation, rejected endpoint/wrong direction, confirm/undo/redo, native Bezier profile volumes, edit/cancel, save/reopen, unchanged copied curves and zero assets. Physical input separate.");window.CloseAfterSmoke();
        }
        catch(Exception error){Trace("FAIL: "+error);await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+error);window.CloseAfterSmoke();}
        finally{CultureInfo.CurrentUICulture=original;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    internal static (CadSketch,SketchEntityId,SketchEntityId,FiniteContactTangentBranch) Seed(DefinitionId part,string kind)
    {
        var draft=new SketchDraft(CadSketch.Create(part,"Mixed "+kind,RigidTransform3d.Identity));SketchEntityId a;
        if(kind=="Circle"){draft.AddCircle(new(3,-2),4,construction:true);a=draft.Value.Circles[0].Id;}
        else if(kind=="Arc")
        {
            Point2d At(double t)=>new(3+4*Math.Cos(7.8-2.1*t),-2+4*Math.Sin(7.8-2.1*t));
            draft.AddArc(At(0),At(.5),At(1),construction:true);a=draft.Value.Arcs[0].Id;
        }
        else if(kind=="Ellipse")
        {var e=new SketchEllipse(SketchEntityId.New(),new(new(3,-2),new(5,2),new(-1,3),.7,3.7),true);draft.Change(s=>s with{Ellipses=[e]});a=e.Id;}
        else
        {
            var e=new SketchFreeform(SketchEntityId.New(),new(SketchFreeformKind.BSpline,[new(8,0),new(10,4),new(12,7),new(14,6),new(16,3),new(18,0)],
                [1,.7,1.1,.8,1.2,1],[0,.35,1],[5,1,5],4,false,.1,.9),true);draft.Change(s=>s with{Freeforms=[e]});a=e.Id;
        }
        var fixedPoints=draft.Value.Points;var g=SketchFiniteParametricContact.EvaluateGeometry(draft.Value,a,.37,2);
        bool reverse=kind is "Arc" or "RationalSpline";double sign=reverse?-1:1,k=g.CurvaturePerMm*sign;
        var tangent=new Point2d(g.Tangent.X*sign,g.Tangent.Y*sign);var second=new Point2d(-tangent.Y*k*64,tangent.X*k*64);
        var mid=new Point2d(g.Position.X+second.X/8,g.Position.Y+second.Y/8);
        draft.AddBezier(new(mid.X-4*tangent.X,mid.Y-4*tangent.Y),new(g.Position.X-second.X/8+.15,g.Position.Y-second.Y/8+.12),new(mid.X+4*tangent.X,mid.Y+4*tangent.Y));
        var constraints=fixedPoints.Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)).ToImmutableArray();
        if(kind=="Circle")constraints=constraints.Add(new RadiusConstraint(SketchConstraintId.New(),a,4));
        draft.Change(s=>s with{Constraints=constraints});return(draft.Value,a,draft.Value.Beziers[^1].Id,reverse?FiniteContactTangentBranch.OppositeDirection:FiniteContactTangentBranch.SameDirection);
    }
    private static double Expected(CadSketch s,SketchEntityId id)
    {
        var b=s.Beziers.Single(b=>b.Id==id);Point2d P(SketchEntityId point)=>s.Points.Single(p=>p.Id==point).Position;
        var a=P(b.Start);var e=P(b.End);var c=P(b.Control);return 2*Math.Abs((e.X-a.X)*(c.Y-a.Y)-(e.Y-a.Y)*(c.X-a.X))/3;
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
