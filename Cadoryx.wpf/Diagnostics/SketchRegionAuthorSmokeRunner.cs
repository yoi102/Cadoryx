using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

internal static class SketchRegionAuthorSmokeRunner
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
            var regions=(IExactSketchExpressionRegions)kernel;var solver=services.GetRequiredService<ISketchConstraintSolver>();var storage=services.GetRequiredService<IDocumentStorage>();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(80,0,0)),"Support"));var part=session.Snapshot.Features.Values.Single().PartId;
            foreach(string language in new[]{"en-US","zh-CN","ja-JP"})foreach(string tool in new[]{"OffsetLoop","OffsetRegions"})
            {
                main.ChangeCultureCommand.Execute(CultureInfo.GetCultureInfo(language).LCID.ToString(CultureInfo.InvariantCulture));await Idle();
                Progress($"{language}: {tool}");var draft=new SketchDraft(CadSketch.Create(part,$"{tool} {language}",RigidTransform3d.Identity));
                foreach(var axes in tool=="OffsetLoop"?new[]{(3d,2d)}:new[]{(8d,6d),(6d,4.5),(4d,3d),(2d,1.5)})
                    draft.AddEllipse(new(new(0,0),new(axes.Item1,0),new(0,axes.Item2),0,Math.Tau));
                var baseline=session.Snapshot;Guid group=Guid.Empty;
                for(int attempt=0;attempt<2;attempt++)
                {
                    await using var editor=new SketchEditorViewModel(doc,kernel,solver,draft.Value,true);var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();
                    try
                    {
                        host.ToolPicker.SelectedValue=tool;host.GeometrySection.IsExpanded=true;await Idle();host.OffsetDistanceInput.Value=.12;
                        ((ComboBox)host.FindName("OffsetJoinPicker")).SelectedValue=SketchOffsetJoin.Miter;host.Canvas.Fit();await Idle();
                        if(tool=="OffsetLoop")host.Canvas.PointerPressed(host.Canvas.ToScreen(new(3,0)),MouseButton.Left,ModifierKeys.None);
                        else
                        {
                            foreach(var e in draft.Value.Ellipses)host.EntityList.SelectedItems.Add(editor.Entities.Single(row=>row.Id==e.Id));await Idle();
                            await ClickButton((Button)host.FindName("ApplyRegionOffsetButton"));
                        }
                        await Idle();group=editor.Sketch.RegionOffsets.Single().Id;var initial=editor.Sketch;
                        Check(initial.RegionOffsets[0].Loops.Length==draft.Value.Ellipses.Length,"Whole-region containment input lost");
                        editor.Select(null);host.ToolPicker.SelectedValue="Select";host.Canvas.Fit();await Idle();var member=editor.Sketch.ExpressionCurves[0];
                        host.Canvas.PointerPressed(host.Canvas.ToScreen(member.Geometry.At(.37)),MouseButton.Left,ModifierKeys.Control);await Idle();
                        Check(editor.SelectedEntities.SequenceEqual(new[]{member.Id})&&editor.HasSelectedRegion&&!editor.CanEditIndividualExpression,"Rendered member does not select its complete group");
                        editor.Select(null);host.Canvas.PointerPressed(host.Canvas.ToScreen(editor.Sketch.Ellipses[0].Geometry.At(.37)),MouseButton.Left,ModifierKeys.Control);await Idle();
                        Check(editor.SelectedEntities.SequenceEqual(new[]{editor.Sketch.Ellipses[0].Id}),"Nearest source curve selection is hidden by its region output");
                        editor.Select(null);host.Canvas.PointerPressed(host.Canvas.ToScreen(member.Geometry.At(.37)),MouseButton.Left,ModifierKeys.Control);await Idle();
                        Check(editor.SelectedEntities.SequenceEqual(new[]{member.Id}),"Repeat nearest output selection");
                        host.ExpressionSection.IsExpanded=true;host.ExpressionSection.BringIntoView();await Idle();
                        Check(!host.ReverseExpressionButton.IsEnabled&&!host.ExpressionFirstInput.IsEnabled,"Individual member controls remain enabled");
                        host.ExpressionDistanceInput.Value=.15;await ClickButton(host.ApplyExpressionDistanceButton);Check(editor.Sketch.RegionOffsets[0].Distance==.15,"Whole-group numeric binding");
                        await ClickButton(host.UndoButton);Check(SketchRegionAuthorModel.SameTable(initial,editor.Sketch),"Whole-distance undo");editor.RedoCommand.Execute(null);await Idle();
                        await ClickButton(host.FreezeExpressionButton);Check(editor.Sketch.RegionOffsets[0].IsFrozen&&!host.ExpressionDistanceInput.IsEnabled,"Whole-group freeze controls");
                        await ClickButton(host.ResumeExpressionButton);Check(!editor.Sketch.RegionOffsets[0].IsFrozen&&host.ExpressionDistanceInput.IsEnabled,"Whole-group resume controls");
                        await ClickButton(host.DetachExpressionButton);Check(editor.Sketch.RegionOffsets.IsEmpty&&!editor.Sketch.ExpressionCurves.IsEmpty,"Whole-group detach");await ClickButton(host.UndoButton);
                        await ClickButton(host.RemoveRegionButton);Check(editor.Sketch.RegionOffsets.IsEmpty&&editor.Sketch.ExpressionCurves.IsEmpty,"Whole-group remove");await ClickButton(host.UndoButton);
                        Check(editor.Sketch.ExpressionCurves.Select(c=>c.Id).SequenceEqual(initial.ExpressionCurves.Select(c=>c.Id)),"Group actions replace output identity");
                        host.Canvas.Fit();await Idle();host.Canvas.PointerPressed(host.Canvas.ToScreen(editor.Sketch.ExpressionCurves[0].Geometry.At(.37)),MouseButton.Left,ModifierKeys.Control);await Idle();
                        Check(editor.HasSelectedRegion&&host.ExpressionDistanceInput.IsEnabled&&!host.ReverseExpressionButton.IsEnabled,"Reselected complete-group controls after delete undo");
                        host.ExpressionSection.BringIntoView();await Idle();if(attempt==1)Save(host,Path.Combine(output,$"region-{tool}-{language}.png"));
                        await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} preview)await preview;Check(editor.CanConfirm,editor.Status);
                        Check(ReferenceEquals(baseline,session.Snapshot),"Region preview committed");SketchAssociativeEndpoints.RequireSolved(editor.Sketch);
                        if(attempt==0){await ClickButton(host.CancelButton);Check(ReferenceEquals(baseline,session.Snapshot)&&doc.PreviewScene is null,"Region cancellation published");}
                        else
                        {
                            await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;
                            var accepted=session.Snapshot;await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Region document undo");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"Region document redo");
                        }
                    }
                    finally{host.Close();await Idle();}
                }
                var sketch=session.Snapshot.Sketches[draft.Value.Id];var root=SketchRegionAuthorModel.Roots(sketch,group).Single();
                var profile=new SketchProfileReference(sketch.Id,sketch.Revision,[]){MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};
                await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,sketch.Plane)),"Region material",part,sketchSource:profile));
                var feature=session.Snapshot.Features.Values.Single(f=>f.SketchSource?.SketchId==sketch.Id);baseline=session.Snapshot;
                double width=tool=="OffsetLoop"?4:9,height=tool=="OffsetLoop"?3:7;
                await using(var editor=new ParameterEditorViewModel(doc,kernel))
                {
                    var host=new ParameterEditorWindow(editor){Owner=window};host.Show();await Idle();
                    try
                    {
                        async Task Add(string name,string expression,ParameterTarget target,bool first=false)
                        {
                            if(!first)await ClickButton(host.NewParameterButton);host.ParameterNameInput.Text=name;host.ExpressionInput.Text=expression;
                            host.DimensionPicker.SelectedValue=ParameterDimension.Length;await ClickButton(host.ApplyParameterButton);
                            host.DrivingPicker.SelectedItem=editor.Targets.Single(t=>t.Target==target);await ClickButton(host.BindDimensionButton);
                        }
                        await Add("width",width.ToString(CultureInfo.InvariantCulture)+" mm",ParameterTarget.SketchCurve(sketch.Id,sketch.Ellipses[0].Id,"CosineX"),true);
                        await Add("height",height.ToString(CultureInfo.InvariantCulture)+" mm",ParameterTarget.SketchCurve(sketch.Id,sketch.Ellipses[0].Id,"SineY"));
                        var distanceTarget=ParameterTarget.SketchRegion(sketch.Id,group);
                        Check(editor.Targets.Single(t=>t.Target==distanceTarget).Label.Contains(Strings.OffsetDistance),"Whole distance translation/target identity");
                        Check(!editor.Targets.Any(t=>sketch.ExpressionCurves.Any(c=>t.Target.Curve==c.Id.Value)),"Individual output offered as distance target");
                        await Add("distance",".3 mm",distanceTarget);await ClickButton(host.PreviewButton);
                        if(editor.PreviewCommand.ExecutionTask is {} preview)await preview;Check(editor.CanConfirm,editor.Status);Check(ReferenceEquals(baseline,session.Snapshot),"C1 preview committed");
                        double area=OffsetArea(width,height,.3);
                        for(int i=1;i<sketch.Ellipses.Length;i++){var e=sketch.Ellipses[i].Geometry;area+=(i%2==0?1:-1)*OffsetArea(e.Cosine.X,e.Sine.Y,i%2==0?.3:-.3);}
                        var previewItem=doc.PreviewScene!.Items.Single(i=>i.BodyId==feature.OutputBodyId);
                        Check(Math.Abs(previewItem.Geometry.VolumeMm3-2*area)<=1e-6,"C1 native preview volume differs from independent Steiner reference");
                        Save(host,Path.Combine(output,$"parameters-{tool}-{language}.png"));
                        await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;
                        var accepted=session.Snapshot;Check(Math.Abs(accepted.Features[feature.Id].Result.VolumeMm3-2*area)<=1e-6,"C1 native commit volume");
                        Check(sketch.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)).SequenceEqual(accepted.Sketches[sketch.Id].ExpressionCurves.Select(c=>(c.Id,c.Endpoints))),"C1 changed region identities");
                        await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"C1 single undo");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"C1 single redo");
                    }
                    finally{host.Close();await Idle();}
                }
                string file=Path.Combine(output,$"region-{tool}-{language}.cadoryx");await session.SaveAsync(storage,file);
                using(var loaded=await storage.LoadAsync(file,session.Assets))
                {
                    var stored=loaded.Snapshot.Sketches[sketch.Id];Check(SketchRegionAuthorModel.SameTable(session.Snapshot.Sketches[sketch.Id],stored),"Main file changed whole author table");
                    ((ISketchExpressionRegionVerifier)regions).ConfirmExpressionRegions(stored);ParameterGraph.ValidateApplied(loaded.Snapshot);
                    Check(loaded.Snapshot.Parameters.Bindings.Any(b=>b.Target==ParameterTarget.SketchRegion(sketch.Id,group)),"Main file lost explicit group target");
                }
                var saved=session.Snapshot;Check(await main.CloseAllAsync(),"Close saved document before native reopen");await main.OpenPathAsync(file);await Idle();
                doc=main.ActiveDocument??throw new InvalidOperationException("Reopen did not activate a document.");session=doc.Session;
                Check(SketchRegionAuthorModel.SameTable(saved.Sketches[sketch.Id],session.Snapshot.Sketches[sketch.Id]),"Production reopen changed whole author table");
                await using(var editor=new SketchEditorViewModel(doc,kernel,solver,session.Snapshot.Sketches[sketch.Id],false))
                {
                    var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();
                    try{editor.Select(sketch.ExpressionCurves[0].Id);host.ExpressionSection.IsExpanded=true;host.Canvas.Fit();await Idle();Save(host,Path.Combine(output,$"reopened-{tool}-{language}.png"));await ClickButton(host.CancelButton);}
                    finally{host.Close();await Idle();}
                }
                await session.ExecuteAsync(new SetParametersCommand(ParameterBook.Empty));
            }
            await session.SaveAsync(storage,Path.Combine(output,"baseline.cadoryx"));
            main.ChangeCultureCommand.Execute(original.ToString(CultureInfo.InvariantCulture));Check(await main.CloseAllAsync(),"Close region documents");
            await ((App)Application.Current).StopRecoveryAsync();await Idle();Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Region assets remain");
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: "+ButtonInputEvidence+"Six OffsetLoop/OffsetRegions workflows in actual en-US/zh-CN/ja-JP; production pointer/member selection, complete group numeric/freeze/resume/detach/remove controls, preview/cancel/confirm and undo/redo. Six C1 source axes + whole distance native solid checks against independent ellipse perimeter/Steiner volume, explicit four-level containment, saved/reopened author identity, 18 complete window images, empty bindings and zero remaining assets. Physical mouse/wheel acceptance remains separate.");window.CloseAfterSmoke();
        }
        catch(Exception error){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+error);Application.Current.Shutdown(1);}
        finally{main.ChangeCultureCommand.Execute(original.ToString(CultureInfo.InvariantCulture));PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
        void Progress(string value)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {value}{Environment.NewLine}");
    }
    private static double OffsetArea(double a,double b,double distance)
    {
        const int count=16384;double step=Math.Tau/count,sum=0;
        for(int i=0;i<=count;i++){double t=i*step;sum+=(i==0||i==count?1:i%2==0?2:4)*Math.Sqrt(a*a*Math.Sin(t)*Math.Sin(t)+b*b*Math.Cos(t)*Math.Cos(t));}
        return a*b*Math.PI+distance*sum*step/3+distance*distance*Math.PI;
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
