using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
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
using OcctSharp;
using Strings=Cadoryx.Lang.Strings.Strings;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static class SketchEndpointCurvatureSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var original=CultureInfo.CurrentUICulture;var main=(MainWindowViewModel)window.DataContext;var markup=CultureInfo.GetCultureInfo(main.CurrentCultureLCID);var proofs=new List<object>();
        try
        {
            await Idle();var doc=main.ActiveDocument!;var session=doc.Session;var kernel=services.GetRequiredService<IGeometryKernel>();
            var solver=services.GetRequiredService<ISketchConstraintSolver>();var storage=services.GetRequiredService<IDocumentStorage>();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(80,0,0)),"Support"));var part=session.Snapshot.Features.Values.Single().PartId;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})foreach(string kind in new[]{"Bezier","Spline","Nonuniform","Zero","Conflict"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {language}: {kind}{Environment.NewLine}");
                var seed=Example(part,kind);await session.ExecuteAsync(new UpsertSketchCommand(seed,solver));var sketch=session.Snapshot.Sketches[seed.Id];FeatureId? linked=null;
                if(kind=="Spline")
                {
                    var source=SketchProfileReference.CreateMixed(sketch,SketchMixedLoops.Find(sketch).Single());
                    await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,sketch.Plane)),"G2 mixed solid",part,sketchSource:source));
                    linked=session.Snapshot.Features.Values.Single(f=>f.SketchSource?.SketchId==sketch.Id).Id;
                }
                var baseline=session.Snapshot;SketchEntityId a=kind=="Zero"?sketch.Lines[0].Id:sketch.Arcs[0].Id;
                SketchEntityId b=kind=="Bezier"?sketch.Beziers[0].Id:sketch.Splines[0].Id;
                await using(var editor=new SketchEditorViewModel(doc,kernel,solver,sketch,false))
                {
                    var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();
                    try
                    {
                        editor.Select(a);editor.Select(b,true);host.ConstraintKindPicker.SelectedValue="EndpointCurvature";
                        Check(editor.ConstraintSelectionHint==Strings.ResourceManager.GetString("EndpointCurvatureHint"),"Localized G2 hint");
                        await ClickButton(host.AddConstraintButton);var relation=editor.Sketch.Constraints.OfType<EndpointCurvatureConstraint>().Single();
                        Check(relation.A==a&&relation.B==b&&relation.Joint==SketchEndpointCurvature.CommonEndpoint(sketch,a,b),"G2 author identities");
                        await ClickButton(host.UndoButton);Check(!editor.Sketch.Constraints.OfType<EndpointCurvatureConstraint>().Any(),"Draft G2 undo");editor.RedoCommand.Execute(null);
                        async Task Preview(){await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} task)await task;await Idle();}
                        await Preview();host.ConstraintKindPicker.BringIntoView();host.Canvas.Fit();await Idle();
                        if(kind=="Conflict")
                        {
                            Check(!editor.CanConfirm&&editor.Report is {Succeeded:false},"Fixed inconsistent G2 accepted");
                            Check(editor.Status!=editor.Report!.Diagnostic,"Failure status not localized");
                        }
                        else
                        {
                            Check(editor.CanConfirm,editor.Status);SketchEndpointCurvature.RequireSolvedRelations(editor.Sketch);
                            foreach(var curve in new[]{a,b})proofs.Add(new{language,kind,curve=curve.ToString(),native=NativeEndpoint(editor.Sketch,curve,relation.Joint)});
                            host.ConstraintList.SelectedItem=editor.Constraints.Single(c=>c.Id==relation.Id);await Idle();
                            Check(editor.Constraints.Single(c=>c.Id==relation.Id).Label.Contains(Strings.ResourceManager.GetString("EndpointCurvatureRelation")!),"Localized G2 row");
                            if(linked is {} feature)
                            {
                                var preview=doc.PreviewScene!.Items.Single(i=>i.BodyId==baseline.Features[feature].OutputBodyId);
                                Check(Math.Abs(preview.Geometry.VolumeMm3-(31.5+8*Math.PI))<1e-5,"Native mixed G2 preview volume");
                            }
                        }
                        Check(ReferenceEquals(baseline,session.Snapshot),"G2 preview committed");Save(host,Path.Combine(output,$"curvature-{kind}-{language}.png"));
                        if(kind=="Conflict"){await ClickButton(host.CancelButton);await Idle();}
                        else
                        {
                            await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;await Idle();
                        }
                    }
                    finally{host.Close();await Idle();}
                }
                if(kind=="Conflict"){Check(ReferenceEquals(baseline,session.Snapshot),"G2 conflict/cancel mutated document");continue;}
                var accepted=session.Snapshot;SketchEndpointCurvature.RequireSolvedRelations(accepted.Sketches[sketch.Id]);
                await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"G2 undo");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"G2 redo");
                string path=Path.Combine(output,$"curvature-{kind}-{language}.cadoryx");await session.SaveAsync(storage,path);
                using(var loaded=await storage.LoadAsync(path,session.Assets)){loaded.Snapshot.Validate();SketchEndpointCurvature.RequireSolvedRelations(loaded.Snapshot.Sketches[sketch.Id]);}
                // Reopened editor cancellation also leaves the exact accepted state.
                await using(var cancel=new SketchEditorViewModel(doc,kernel,solver,accepted.Sketches[sketch.Id],false))
                {await cancel.PreviewCommand.ExecuteAsync(null);Check(cancel.CanConfirm,cancel.Status);cancel.CancelCommand.Execute(null);}
                Check(ReferenceEquals(accepted,session.Snapshot)&&doc.PreviewScene is null,"G2 cancellation published or retained preview");
            }
            await File.WriteAllTextAsync(Path.Combine(output,"native-derivatives.json"),JsonSerializer.Serialize(proofs,new JsonSerializerOptions{WriteIndented=true}));
            await session.SaveAsync(storage,Path.Combine(output,"baseline.cadoryx"));Check(await main.CloseAllAsync(),"Close G2 documents");
            await ((App)Application.Current).StopRecoveryAsync();await Idle();Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"G2 assets remain");
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"B13 endpoint G2: quadratic Bezier, cubic/nonuniform spline, exact zero-curvature line/spline, fixed conflict refusal in en-US/zh-CN/ja-JP. Actual choice/add/undo/preview/confirm/cancel controls, signed native first/second derivative proofs, mixed-loop exact volume, atomic preview, undo/redo, reopen and zero assets. Physical input separate.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);window.CloseAfterSmoke();}
        finally{CultureInfo.CurrentUICulture=original;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }

    private static CadSketch Example(DefinitionId part,string kind)
    {
        var d=new SketchDraft(CadSketch.Create(part,"G2 "+kind,RigidTransform3d.Identity));d.AddPoint(new(0,0));var joint=d.Value.Points[0].Id;
        if(kind=="Zero")d.AddLine(new(-6,0),new(0,0),end:joint);
        else d.AddArc(new(0,0),new(2,4-Math.Sqrt(12)),new(4,4),existingStart:joint);
        SketchEntityId[] movable;
        if(kind=="Bezier")
        {d.AddBezier(new(0,0),new(-3,-.2),new(-6,4.9),existingStart:joint);movable=[d.Value.Beziers[0].Control,d.Value.Beziers[0].End];}
        else
        {
            var curve=kind=="Nonuniform"?new CubicSplineDefinition([new(0,0),new(-2,-.2),new(-4,18.4),new(-5,3),new(-6,4)],[2,2.25,5],[4,1,4]):
                CubicSplineDefinition.Uniform(kind=="Zero"?[new(0,0),new(2,.1),new(4,.6),new(6,4)]:[new(0,0),new(-2,-.2),new(-4,1.9),new(-6,4)]);
            d.AddSpline(curve,existingStart:joint);movable=[d.Value.Splines[0].Controls[1],d.Value.Splines[0].Controls[2]];
            if(kind=="Spline")d.AddLine(new(4,4),new(-6,4),start:d.Value.Arcs[0].End,end:d.Value.Splines[0].Controls[^1]);
        }
        d.Change(s=>s with{Constraints=[..s.Points.Where(p=>kind=="Conflict"||!movable.Contains(p.Id)).Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)),
            ..kind=="Conflict"?[]:movable.Select(id=>(SketchConstraint)new OffsetXConstraint(SketchConstraintId.New(),joint,id,s.Points.Single(p=>p.Id==id).Position.X))]});return d.Value;
    }

    private static object NativeEndpoint(CadSketch sketch,SketchEntityId curve,SketchEntityId joint)
    {
        var points=sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);SketchPoint2d P(SketchEntityId id)=>new(points[id].X,points[id].Y);SketchCurve2d native;
        if(sketch.Arcs.SingleOrDefault(a=>a.Id==curve) is {} arc)
        {var g=SketchArcGeometry.Through(points[arc.Start],points[arc.Middle],points[arc.End]);native=SketchCurve2d.CircularArc(new(g.Center.X,g.Center.Y),g.Radius,g.StartAngle,g.SweepAngle);}
        else if(sketch.Beziers.SingleOrDefault(b=>b.Id==curve) is {} bezier)native=SketchCurve2d.Bezier([P(bezier.Start),P(bezier.Control),P(bezier.End)]);
        else if(sketch.Splines.SingleOrDefault(s=>s.Id==curve) is {} spline)
        {var g=CubicSplineKnotVector.Definition(spline.Controls.Select(id=>points[id]).ToImmutableArray(),spline.Knots);native=SketchCurve2d.BSpline(g.Controls.Select(p=>new SketchPoint2d(p.X,p.Y)).ToArray(),g.Knots,g.Multiplicities,3);}
        else{var line=sketch.Lines.Single(l=>l.Id==curve);native=SketchCurve2d.Segment(P(line.Start),P(line.End));}
        bool start=SketchEndpointTangency.Endpoints(sketch,curve).Start==joint;var sampled=native.Evaluate(start?0:1);
        using var edge=SketchModeling.CreateEdge(native,SketchPlane.XY);Check(edge.IsValid,"Invalid native G2 curve");var range=edge.GetEdgeCurveSnapshot();
        var exact=edge.EvaluateEdgeDerivatives(start!=native.Reversed?range.FirstParameter:range.LastParameter);var first=exact.FirstDerivative;var second=exact.SecondDerivative;
        Check(double.Hypot(exact.Point.X-points[joint].X,exact.Point.Y-points[joint].Y)<=1e-7,"Native endpoint differs from explicit author");
        double direction=(first.X*sampled.FirstDerivative.X+first.Y*sampled.FirstDerivative.Y<0?-1:1)*(start?1:-1),speed=double.Hypot(first.X,first.Y);
        double curvature=direction*(first.X*second.Y-first.Y*second.X)/(speed*speed*speed);var managed=SketchEndpointCurvature.Evaluate(sketch,curve,joint);
        Check(Math.Abs(curvature-managed.CurvaturePerMm)<=Math.Max(1e-8,Math.Abs(curvature)*1e-7),"Native G2 curvature mismatch");
        Check(double.Hypot(direction*first.X/speed-managed.Tangent.X,direction*first.Y/speed-managed.Tangent.Y)<=1e-7,"Native G2 tangent mismatch");
        return new{exact.Parameter,first,second,curvaturePerMm=curvature,managedCurvaturePerMm=managed.CurvaturePerMm};
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
