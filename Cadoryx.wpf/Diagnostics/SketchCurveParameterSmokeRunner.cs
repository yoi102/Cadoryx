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

internal static class SketchCurveParameterSmokeRunner
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
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})for(int kind=0;kind<3;kind++)
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {language}: curve parameter {kind}{Environment.NewLine}");
                var d=new SketchDraft(CadSketch.Create(part,$"Curve {kind+1}",RigidTransform3d.Identity));SketchEntityId id;
                if(kind==0){d.AddEllipse(new(new(20,0),new(5,0),new(0,2),0,Math.PI));id=d.Value.Ellipses.Single().Id;}
                else
                {
                    var g=kind==1?new SketchFreeformGeometry(SketchFreeformKind.Bezier,[new(10,0),new(15,5),new(20,0)],[1,.7,1],[],[],2,false,0,1):
                        new(SketchFreeformKind.BSpline,[new(10,0),new(12,4),new(14,6),new(16,3),new(18,0)],[1,.8,1.2,.7,1],[0,.45,1],[4,1,4],3,false,.1,.9);
                    d.AddFreeform(g);id=d.Value.Freeforms.Single().Id;
                }
                d.ExposeCurveEndpoints(id);var ends=SketchFixedCurveEndpoints.All(d.Value).Single().Ends;var(a,b)=SketchFixedCurveEndpoints.Positions(d.Value,id);
                d.AddLine(b,a,start:ends.End,end:ends.Start);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var sketch=session.Snapshot.Sketches[d.Value.Id];
                var source=SketchProfileReference.CreateMixed(sketch,[id,sketch.Lines.Single().Id]);
                await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,sketch.Plane)),"Consumer",part,sketchSource:source));
                var baseline=session.Snapshot;var feature=baseline.Features.Values.Single(f=>f.SketchSource?.SketchId==sketch.Id);
                var primary=ParameterTarget.SketchCurve(sketch.Id,id,kind==0?"SineY":"PoleY",kind==0?null:kind==1?1:2);string expression=kind==0?"3 mm":kind==1?"7.5 mm":"9 mm";
                var scalar=ParameterTarget.SketchCurve(sketch.Id,id,kind==0?"SweepAngle":"Weight",kind==0?null:1);
                double previewVolume=0;
                await using(var editor=new ParameterEditorViewModel(doc,kernel))
                {
                    var host=new ParameterEditorWindow(editor){Owner=window};host.Show();await Idle();
                    try
                    {
                        async Task Add(string name,string value,ParameterDimension dimension,ParameterTarget target,bool first=false)
                        {
                            if(!first)await ClickButton(host.NewParameterButton);
                            host.ParameterNameInput.Text=name;host.DimensionPicker.SelectedValue=dimension;host.ExpressionInput.Text=value;await ClickButton(host.ApplyParameterButton);
                            host.DrivingPicker.SelectedItem=editor.Targets.Single(t=>t.Target==target);await ClickButton(host.BindDimensionButton);
                        }
                        await Add("height",expression,ParameterDimension.Length,primary,true);
                        Check(editor.Targets.Single(t=>t.Target==primary).Label.Contains(kind==0?Strings.CurveParameterSineY:Strings.CurveParameterPoleY),"Localized numeric target");
                        await Add("shape_value",kind==0?"180 deg":"0.9",kind==0?ParameterDimension.Angle:ParameterDimension.Scalar,scalar);
                        await ClickButton(host.NewParameterButton);host.ParameterNameInput.Text="observed_height";await ClickButton(host.ReferenceMode);
                        host.ReferencePicker.SelectedItem=editor.Targets.Single(t=>t.Target==primary);await ClickButton(host.ApplyParameterButton);
                        Check(editor.Parameters.Count==3&&editor.Bindings.Count==2,"Actual driving/reference controls");
                        async Task Preview(){await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} task)await task;await Idle();}
                        await Preview();Check(editor.CanConfirm,editor.Status);Check(ReferenceEquals(baseline,session.Snapshot),"Curve parameter preview committed");
                        var item=doc.PreviewScene!.Items.Single(i=>i.Geometry.Revision!=baseline.Features.Values.Single(f=>f.OutputBodyId==i.BodyId).Result.Revision);
                        previewVolume=item.Geometry.VolumeMm3;Check(previewVolume>feature.Result.VolumeMm3,"Native curve parameter preview did not enlarge area");
                        Check(editor.Parameters.Single(p=>p.Name=="height").Value==editor.Parameters.Single(p=>p.Name=="observed_height").Value,"Reference did not follow staged driver");
                        Save(host,Path.Combine(output,$"curve-parameters-{kind}-{language}.png"));
                        host.ParameterList.SelectedItem=editor.Parameters.Single(p=>p.Name=="height");host.ExpressionInput.Text="1 rad";await ClickButton(host.ApplyParameterButton);await Preview();
                        Check(!editor.CanConfirm&&ReferenceEquals(baseline,session.Snapshot),"Invalid curve edit published");
                        host.ExpressionInput.Text=expression;await ClickButton(host.ApplyParameterButton);await Preview();Check(editor.CanConfirm,editor.Status);
                        await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;await Idle();
                    }
                    finally{host.Close();await Idle();}
                }
                var accepted=session.Snapshot;Check(Math.Abs(accepted.Features[feature.Id].Result.VolumeMm3-previewVolume)<1e-7,"Confirmed native volume differs from preview");
                Check(SketchFixedCurveEndpoints.All(accepted.Sketches[sketch.Id]).Single().Ends==ends,"Parameters changed endpoint author identity");
                await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"One parameter undo");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"One parameter redo");
                string path=Path.Combine(output,$"curve-parameters-{kind}-{language}.cadoryx");await session.SaveAsync(storage,path);
                using(var loaded=await storage.LoadAsync(path,session.Assets))
                {loaded.Snapshot.Validate();Check(loaded.Snapshot.Parameters.Bindings.All(b=>b.Target.Curve==id.Value),"Storage lost explicit curve target");}
                await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Restore baseline before next sample");
            }
            await session.SaveAsync(storage,Path.Combine(output,"baseline.cadoryx"));Check(await main.CloseAllAsync(),"Close all curve parameter documents");
            await ((App)Application.Current).StopRecoveryAsync();await Idle();Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Curve parameter assets remain");
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"B11 exact ellipse, rational Bezier and nonuniform BSpline parameter fields in en-US/zh-CN/ja-JP. Actual length, angle/scalar and reference controls, explicit curve/table targets, native preview, invalid candidate rejection, confirm, endpoint identity, one undo/redo, save/reopen and zero remaining assets. Physical input is separate.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);window.CloseAfterSmoke();}
        finally{CultureInfo.CurrentUICulture=original;I18NExtension.Culture=markup;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
