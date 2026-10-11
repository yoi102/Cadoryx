using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using OcctSharp;
using Strings=Cadoryx.Lang.Strings.Strings;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static partial class SketchNativeProjectionSmokeRunner
{
    private static FreeformCurveDefinition FreeformFixture(int kind)=>kind switch
    {
        0=>FreeformCurveDefinition.Bezier([new(8,0,2),new(12,6,3),new(18,0,2)],[1,.65,1.2],new(.2,.85)),
        1=>FreeformCurveDefinition.BSpline([new(8,0,2),new(10,4,3),new(12,7,3),new(14,6,2),new(16,3,3),new(18,0,2)],
            [0,.35,1],[5,1,5],4,weights:[1,.7,1.1,.8,1.2,1],parameterRange:new(.1,.9)),
        _=>FreeformCurveDefinition.BSpline([new(8,0,2),new(12,4,3),new(16,0,2),new(12,-4,3)],
            [0,1,2,3,4],[1,1,1,1,1],3,true,[1,.7,1.2,.8])
    };
    private static async Task FreeformAsync(MainWindow window,CadDocumentViewModel document,IGeometryKernel kernel,ISketchConstraintSolver solver,
        IDocumentStorage storage,string output,string language,DefinitionId part)
    {
        var session=document.Session;
        for(int kind=0;kind<3;kind++)
        {
            Vector3d pickAt;
            using(var shape=FreeformAuthoring.CreateCurve(FreeformFixture(kind)))
            using(var stored=OcctGeometryBridge.StoreShape(shape,session.Assets))
            {
                var info=shape.GetEdgeCurveSnapshot();var p=shape.EvaluateEdgeDerivatives(info.FirstParameter+.37*(info.LastParameter-info.FirstParameter)).Point;
                pickAt=new(p.X,p.Y,p.Z);
                await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),$"B11 freeform source {language} {kind}",part));
            }
            var source=session.Snapshot.Features.Values.Single(f=>f.Name==$"B11 freeform source {language} {kind}");var baseline=session.Snapshot;
            using(var saved=OcctGeometryBridge.ReadShape(source.Result,session.Assets))
            {
                var info=saved.GetEdgeCurveSnapshot();var p=saved.EvaluateEdgeDerivatives(info.FirstParameter+.37*(info.LastParameter-info.FirstParameter)).Point;
                var savedAt=new Vector3d(p.X,p.Y,p.Z);Trace.WriteLine($"Freeform BRep roundtrip point delta={(savedAt-pickAt).Length:G17}.");pickAt=savedAt;
            }
            var plane=new RigidTransform3d(new(0,0,25),Quaterniond.FromAxisAngle(new(1,0,0),.4));var sketch=CadSketch.Create(part,$"Freeform {language} {kind}",plane);
            await using(var editor=new SketchEditorViewModel(document,kernel,solver,sketch,true))
            {
                var host=new SketchEditorWindow(editor){Owner=window};host.Show();await Idle();host.ProjectionSection.IsExpanded=true;host.ProjectNativeEdgeButton.BringIntoView();await Idle();
                try
                {
                    await Operate(host,async picker=>{await PickFreeform(picker,source.Id,pickAt);Check(picker.Editor.CurveSummary!=Strings.NativeLineProjectionReady,"Freeform family was lost");await ClickButton(picker.CancelButton);});
                    Check(ReferenceEquals(sketch,editor.Sketch)&&ReferenceEquals(baseline,session.Snapshot),"Freeform picker cancel published");
                    await Operate(host,async picker=>
                    {
                        await PickFreeform(picker,source.Id,pickAt);picker.Host.Viewport!.SaveScreenshot(Path.Combine(output,$"freeform-edge-{kind}-{language}.png"));
                        await ClickButton(picker.UseProjectionButton);if(picker.Editor.UseCommand.ExecutionTask is {} task)await task;
                    });
                    var curve=editor.Sketch.Freeforms.Single();Check(editor.Sketch.NativeProjections.Length==1,"Missing freeform source");
                    Check(curve.Geometry.Degree==(kind==1?4:kind==0?2:3)&&(!curve.Geometry.Weights.IsEmpty),"Rational degree/weights lost");
                    Check(kind!=2||curve.Geometry.Periodic,"Full native period lost");editor.Select(curve.Id);
                    host.FreeformSection.IsExpanded=true;host.FreeformWeightsInput.BringIntoView();await Idle();
                    host.FreeformWeightsInput.Text=string.Join("; ",curve.Geometry.Weights.Select((w,i)=>(i==1?w*1.2:w).ToString("G17",CultureInfo.InvariantCulture)));
                    await Idle();await ClickButton(host.ApplyFreeformButton);
                    Check(editor.Status==Strings.NativeProjectionDriven&&curve.Geometry.SameValues(editor.Sketch.Freeforms[0].Geometry),"Freeform driven numeric protection/translation");
                    editor.Select(curve.Id);host.ProjectionSection.IsExpanded=true;host.DetachProjectionButton.BringIntoView();await Idle();await ClickButton(host.DetachProjectionButton);
                    Check(editor.Sketch.NativeProjections.IsEmpty,"Freeform detach control");host.FreeformSection.IsExpanded=true;host.FreeformReverseInput.BringIntoView();await Idle();
                    host.FreeformReverseInput.IsChecked=true;await Idle();await ClickButton(host.ApplyFreeformButton);Check(editor.Sketch.Freeforms.Single().Geometry.Reversed,"Full freeform numeric edit control");
                    await ClickButton(host.UndoButton);await ClickButton(host.UndoButton);Check(editor.Sketch.NativeProjections.Length==1&&!editor.Sketch.Freeforms.Single().Geometry.Reversed,"Freeform edit/detach undo");
                    editor.Select(curve.Id);host.ToggleConstructionButton.BringIntoView();await Idle();await ClickButton(host.ToggleConstructionButton);
                    Check(!editor.Sketch.Freeforms.Single().IsConstruction,"Freeform construction control");
                    host.ProjectionSection.IsExpanded=true;host.ProjectNativeEdgeButton.BringIntoView();await Idle();
                    await Operate(host,async picker=>{picker.LinkProjectionInput.IsChecked=false;await PickFreeform(picker,source.Id,pickAt);await ClickButton(picker.UseProjectionButton);if(picker.Editor.UseCommand.ExecutionTask is {} task)await task;});
                    Check(editor.Sketch.Freeforms.Length==2&&editor.Sketch.NativeProjections.Length==1,"Freeform independent copy");editor.Select(curve.Id);host.Canvas.Fit();
                    host.FreeformSection.IsExpanded=true;host.FreeformPolesInput.BringIntoView();await Idle();Save(host,Path.Combine(output,$"freeform-editor-{kind}-{language}.png"));
                    await ClickButton(host.PreviewButton);if(editor.PreviewCommand.ExecutionTask is {} preview)await preview;Check(editor.CanConfirm,editor.Status);Check(ReferenceEquals(baseline,session.Snapshot),"Freeform preview published");
                    await ClickButton(host.ConfirmButton);if(editor.ConfirmCommand.ExecutionTask is {} confirm)await confirm;
                    var accepted=session.Snapshot;await session.UndoAsync();Check(ReferenceEquals(baseline,session.Snapshot),"Freeform document undo");await session.RedoAsync();Check(ReferenceEquals(accepted,session.Snapshot),"Freeform document redo");
                }
                finally{host.Close();await Idle();}
            }
            var committed=session.Snapshot.Sketches[sketch.Id];var id=committed.NativeProjections.Single().TargetEntity;var reference=SketchProfileReference.CreateMixed(committed,[id]);
            var recipe=reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,plane));await session.ExecuteAsync(new AddBodyCommand(recipe,$"B11 freeform solid {language} {kind}",part,sketchSource:reference));
            var feature=session.Snapshot.Features.Values.Single(f=>f.SketchSource?.SketchId==sketch.Id);Check(feature.Result.VolumeMm3>0&&!feature.IsStale,"Freeform native solid");
            string file=Path.Combine(output,$"freeform-{kind}-{language}.cadoryx");await session.SaveAsync(storage,file);
            using(var loaded=await storage.LoadAsync(file,session.Assets))
            {
                Check(committed.Freeforms[0].Geometry.SameValues(loaded.Snapshot.Sketches[sketch.Id].Freeforms[0].Geometry),"Full freeform definition lost on reopen");
                loaded.Snapshot.Validate();
            }
        }
    }
    private static async Task PickFreeform(SketchNativeProjectionWindow picker,FeatureId source,Vector3d location)
    {
        picker.NativeSourcePicker.SelectedItem=picker.Editor.Sources.Single(s=>s.Feature==source);await Idle();
        var viewport=picker.Host.Viewport??throw new InvalidOperationException("Native picker viewport is not ready.");viewport.SetProjection(CadProjection.Top);viewport.FitAll();await Idle();
        var at=viewport.WorldToScreen(location);viewport.PointerPressed(0,at.X,at.Y,0);viewport.PointerReleased(0,at.X,at.Y,0);
        Trace.WriteLine($"Freeform input: world={location}, pixel={at}, source={source}.");
        var deadline=DateTime.UtcNow.AddSeconds(15);while(!picker.Editor.CanUse){await Idle();if(DateTime.UtcNow>deadline){viewport.SaveScreenshot(Path.Combine(Path.GetTempPath(),"Cadoryx-freeform-pick-reject.png"));throw new InvalidOperationException("Native freeform pick: "+picker.Editor.Status);}}
    }
}
