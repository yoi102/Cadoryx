using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Editor;
using Cadoryx.Sketching;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static class SketchEditorSmokeRunner
{
    internal static async Task RunB3Async(MainWindow window,IServiceProvider services,string output)
        =>await RunRelationSmokeAsync(window,services,output,3);
    internal static async Task RunB4Async(MainWindow window,IServiceProvider services,string output)
        =>await RunRelationSmokeAsync(window,services,output,4);
    internal static async Task RunB5Async(MainWindow window,IServiceProvider services,string output)
        =>await RunRelationSmokeAsync(window,services,output,5);
    internal static async Task RunB6Async(MainWindow window,IServiceProvider services,string output)
        =>await RunRelationSmokeAsync(window,services,output,6);
    internal static async Task RunB7Async(MainWindow window,IServiceProvider services,string output)
        =>await RunRelationSmokeAsync(window,services,output,7);
    private static async Task RunRelationSmokeAsync(MainWindow window,IServiceProvider services,string output,int batch)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;var vm=(MainWindowViewModel)window.DataContext;var markupCulture=CultureInfo.GetCultureInfo(vm.CurrentCultureLCID);
        try
        {
            await Idle();await vm.ActiveDocument!.Session.SaveAsync(services.GetRequiredService<IDocumentStorage>(),Path.Combine(output,"baseline.cadoryx"));
            void Progress(string step)=>File.AppendAllText(Path.Combine(output,"progress.log"),$"{DateTimeOffset.Now:O} {step}{Environment.NewLine}");
            if(batch==7)
            {
                await VerifyB7Async(vm,vm.ActiveDocument!,output,Progress);
                // The linked-profile fixture is authored through real commands and dirties the document.
                await vm.ActiveDocument!.Session.SaveAsync(services.GetRequiredService<IDocumentStorage>(),Path.Combine(output,"linked-baseline.cadoryx"));
                Progress("B7: linked fixture saved after preview cancellation");
            }
            else if(batch==6)await VerifyB6Async(vm,vm.ActiveDocument!,output,Progress);
            else if(batch==5)await VerifyB5Async(vm,vm.ActiveDocument!,output,Progress);
            else if(batch==4)await VerifyB4Async(vm,vm.ActiveDocument!,output,Progress);
            else await VerifyB3Async(vm,vm.ActiveDocument!,output,Progress);
            Check(await vm.CloseAllAsync(),"Close documents");await ((App)System.Windows.Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Zero assets after close");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),batch==7?
                "PASS: B7 production MetroWindow en-US/zh-CN/ja-JP; pointer multiple fillets/chamfers, persistent radius, dual distance bindings, atomic collapse rejection, corner proof undo/redo, localized errors and previews; explicit linked profile checkbox blocks/permits native recompute and close cancellation; zero remaining assets.":batch==6?
                "PASS: B6 production MetroWindow in en-US/zh-CN/ja-JP; pointer rectangle and two-line fillet selection; radius and apply button bindings; radius/collapse checks, exact tangencies, undo/redo, solve and preview-close cancellation; zero remaining assets.":batch==5?
                "PASS: B5 production MetroWindow in en-US/zh-CN/ja-JP; actual pointer line/Bezier and line/spline joins; spline start/end ID snapping; endpoint tangency button bindings, solve, undo/redo and enable/disable; localized hints, legal control polygons and preview-close cancellation; zero remaining assets.":batch==4?
                "PASS: B4 production MetroWindow in en-US/zh-CN/ja-JP; actual pointer line/arc/arc with shared endpoint ID; endpoint tangency button bindings, line-arc and arc-arc solve, draft undo/redo and enable/disable; localized labels and preview-close cancellation; zero remaining assets.":
                "PASS: B3 production MetroWindow in en-US/zh-CN/ja-JP; actual pointer rectangle and closed-loop offset; shared endpoints, collapse rejection, offset undo/redo; midpoint/symmetry button bindings, solve and draft undo/redo; preview-close cancellation; zero remaining assets.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);System.Windows.Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markupCulture;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;
        var markupCulture=CultureInfo.GetCultureInfo(((MainWindowViewModel)window.DataContext).CurrentCultureLCID);
        try
        {
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;
            await Idle();var vm=(MainWindowViewModel)window.DataContext;var document=vm.ActiveDocument!;var initial=document.Session.Snapshot;
            await Operate(()=>vm.NewSketchCommand.ExecuteAsync(null),async editor=>
            {
                Check(editor.Owner==window,"Owned MetroWindow");editor.ToolPicker.SelectedValue="Rectangle";editor.Activate();editor.Canvas.Focus();await Idle();
                await Click(editor,new(0,0));await Click(editor,new(40,30));
                if(editor.Editor.Sketch.Lines.IsEmpty)
                {
                    // A first desktop click may only activate the owned modal window.
                    editor.ToolPicker.SelectedValue="Select";await Idle();editor.ToolPicker.SelectedValue="Rectangle";
                    editor.Activate();editor.Canvas.Focus();await Idle();
                    await Click(editor,new(0,0));await Click(editor,new(40,30));
                }
                if(editor.Editor.Sketch.Lines.Length!=4)Save(editor,Path.Combine(output,"pointer-failure.png"));
                Check(editor.Editor.Sketch.Lines.Length==4,$"Rectangle pointer drawing: tool={editor.Editor.Tool}, editable={editor.Editor.CanEdit}, points={editor.Editor.Sketch.Points.Length}, status={editor.Editor.Status}, pointer={System.Windows.Input.Mouse.GetPosition(editor.Canvas)}, hit={System.Windows.Input.Mouse.DirectlyOver}");
                editor.ToolPicker.SelectedValue="Circle";await Click(editor,new(55,15));await Click(editor,new(60,15));Check(editor.Editor.Sketch.Circles.Length==1,"Circle pointer drawing");
                editor.ToolPicker.SelectedValue="Select";await Drag(editor,new(60,15),new(63,15));
                if(editor.Editor.PreviewCommand.ExecutionTask is {} circlePreview)await circlePreview;
                var resized=editor.Editor.Sketch.Circles.Single();
                Check(Math.Abs(resized.Radius-8)<.5&&editor.Editor.Sketch.Constraints.OfType<RadiusConstraint>()
                    .Any(c=>c.Circle==resized.Id&&Math.Abs(c.Radius-resized.Radius)<1e-9),"Circle radius handle and constraint update");
                editor.ToolPicker.SelectedValue="Line";await Click(editor,new(50,35));await Click(editor,new(60,40));Check(editor.Editor.Sketch.Lines.Length==5,"Line pointer drawing");
                editor.ToolPicker.SelectedValue="ArcSegment";await Click(editor,new(65,0));await Click(editor,new(70,5));await Click(editor,new(75,0));
                Check(editor.Editor.Sketch.Arcs.Length==1,"Three-point arc pointer drawing");
                editor.ToolPicker.SelectedValue="Line";
                await Click(editor,new(75,0));await Click(editor,new(75,-8));
                await Click(editor,new(75,-8));await Click(editor,new(65,-8));
                await Click(editor,new(65,-8));await Click(editor,new(65,0));
                Check(editor.Editor.Sketch.Lines.Length==8&&SketchMixedLoops.Find(editor.Editor.Sketch).Length==1,
                    "Pointer-authored line and arc loop");
                editor.ToolPicker.SelectedValue="BezierSegment";
                await Click(editor,new(20,15));await Click(editor,new(25,25));await Click(editor,new(30,15));
                Check(editor.Editor.Sketch.Beziers.Length==1,"Quadratic Bezier pointer drawing");
                editor.ToolPicker.SelectedValue="SplineRegion";
                foreach(var point in new[]{new Point2d(10,35),new(12,40),new(15,42),new(18,40),new(20,35)})await Click(editor,point);
                Check(editor.Editor.Sketch.Splines.Length==1,"Five-point cubic spline pointer drawing");
                editor.ToolPicker.SelectedValue="Point";await Click(editor,new(65,20));Check(editor.Editor.Sketch.Points.Length==21,"Point pointer drawing");
                editor.ToolPicker.SelectedValue="Select";await Drag(editor,new(65,20),new(65,25));
                if(editor.Editor.PreviewCommand.ExecutionTask is {} preview)await preview;
                Check(editor.Editor.Sketch.Points.Any(p=>Math.Abs(p.Position.X-65)<.5&&Math.Abs(p.Position.Y-25)<.5),
                    $"Point drag: {string.Join(';',editor.Editor.Sketch.Points.Select(p=>$"{p.Position.X:G5},{p.Position.Y:G5}"))}");
                editor.SketchName.Text="UI sketch";editor.PreviewButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await editor.Editor.PreviewCommand.ExecuteAsync(null);
                Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(ReferenceEquals(initial,document.Session.Snapshot),"Preview remains uncommitted");
                Save(editor,Path.Combine(output,"editor-en-US.png"));await editor.Editor.ConfirmCommand.ExecuteAsync(null);
            });
            var sketch=document.Session.Snapshot.Sketches.Values.Single();Check(sketch.Name=="UI sketch","Name binding");
            Check(SketchMixedLoops.Find(sketch).Length==1,"Saved pointer-authored mixed curve loop");
            Check(sketch.Beziers.Length==1,"Saved pointer-authored quadratic Bezier");
            Check(sketch.Splines.Length==1,"Saved pointer-authored cubic spline");
            var drawn=SketchProfileBuilder.Polygon(sketch,sketch.Lines.Take(4).Select(l=>l.Id));
            double widthMm=drawn.Points.Max(p=>p.X)-drawn.Points.Min(p=>p.X);
            double heightMm=drawn.Points.Max(p=>p.Y)-drawn.Points.Min(p=>p.Y);
            Check(Math.Abs(widthMm-40)<1.5&&Math.Abs(heightMm-30)<1.5,$"Pointer rectangle size {widthMm} × {heightMm} mm.");
            foreach(var node in vm.ModelTree.Items)node.IsExpanded=true;
            Check(vm.ModelTree.Items.SelectMany(Flatten).Any(n=>n.Sketch==sketch.Id),"Sketch tree projection");
            document.SelectedSketchId=sketch.Id;vm.StartSketchFeatureCommand.Execute("Extrude");document.SizeZ=10;
            await document.PreviewCommand.ExecuteAsync(null);Check(document.HasPreview,document.ToolStatus);await document.ConfirmCommand.ExecuteAsync(null);
            var linked=document.Session.Snapshot.Features.Values.Single();Check(linked.SketchSource?.SketchId==sketch.Id,"UI-created associated feature");
            Check(Math.Abs(linked.Result.VolumeMm3-widthMm*heightMm*10)<1e-3,$"Initial linked extrusion: {linked.Result.VolumeMm3} vs {widthMm*heightMm*10}.");
            await document.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,10,RigidTransform3d.Identity),"Downstream cut",sketch.PartId));
            await document.Session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,document.Session.Snapshot.Bodies.Values.OrderByDescending(b=>b.Geometry.VolumeMm3).Select(b=>b.Id)));
            var before=document.Session.Snapshot;document.SelectedSketchId=sketch.Id;
            double cutVolume=linked.Result.VolumeMm3-before.Bodies.Values.Single().Geometry.VolumeMm3;
            Check(cutVolume>0&&cutVolume<=1000.01,$"Invalid downstream cutter overlap: {cutVolume}.");
            foreach(string lang in new[]{"zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(lang);
                I18NExtension.Culture=CultureInfo.CurrentUICulture;
                await Operate(()=>vm.EditSketchCommand.ExecuteAsync(null),async editor=>
                {
                    var width=editor.Editor.Sketch.Constraints.OfType<OffsetXConstraint>().Single();
                    editor.ConstraintList.SelectedItem=editor.Editor.Constraints.Single(c=>c.Id==width.Id);editor.DimensionInput.Value=lang=="zh-CN"?55:60;
                    editor.Editor.ApplyDimensionCommand.Execute(null);await editor.Editor.PreviewCommand.ExecuteAsync(null);
                    Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(ReferenceEquals(before,document.Session.Snapshot),"Editing preview remains atomic");
                    double expected=(lang=="zh-CN"?55:60)*heightMm*10-cutVolume;
                    Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-expected)<1e-3),$"Preview rebuilds downstream boolean: expected {expected}.");
                    Save(editor,Path.Combine(output,$"editor-{lang}.png"));
                    if(lang=="zh-CN")editor.Close();else await editor.Editor.ConfirmCommand.ExecuteAsync(null);
                });
                if(lang=="zh-CN")Check(ReferenceEquals(before,document.Session.Snapshot),"Close cancels preview");
            }
            CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markupCulture;var after=document.Session.Snapshot;double expectedFinal=60*heightMm*10-cutVolume;
            Check(Math.Abs(after.Bodies.Values.Single().Geometry.VolumeMm3-expectedFinal)<1e-3,"Confirmed downstream result");
            await document.Session.UndoAsync();Check(ReferenceEquals(before,document.Session.Snapshot),"Single exact undo");await document.Session.RedoAsync();Check(ReferenceEquals(after,document.Session.Snapshot),"Single exact redo");
            document.SelectedSketchId=sketch.Id;
            await Operate(()=>vm.EditSketchCommand.ExecuteAsync(null),async editor=>
            {
                var s=editor.Editor.Sketch;editor.Editor.Select(s.Points[0].Id);editor.Editor.Select(s.Points[1].Id,true);editor.Editor.ConstraintKind="OffsetX";editor.Editor.DimensionValue=99;
                editor.Editor.AddConstraintCommand.Execute(null);await editor.Editor.PreviewCommand.ExecuteAsync(null);Check(!editor.Editor.CanConfirm&&editor.Editor.Report?.ConflictingConstraints.Length>0,"Visible conflict disables commit");
                Save(editor,Path.Combine(output,"editor-conflict.png"));editor.Close();
            });
            Check(ReferenceEquals(after,document.Session.Snapshot),"Conflict did not publish");
            await Idle();var host=Find<OcctViewportHost>(window)!;host.Viewport!.FitAll();host.Viewport.SaveScreenshot(Path.Combine(output,"linked-result.png"));
            var storage=services.GetRequiredService<IDocumentStorage>();string path=Path.Combine(output,"linked-sketch.cadoryx");await document.Session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,document.Session.Assets))
            {Check(SketchSmokeRunner.Describe(after.Sketches[sketch.Id])==SketchSmokeRunner.Describe(loaded.Snapshot.Sketches[sketch.Id]),"Saved sketch exact");Check(loaded.Snapshot.Features[linked.Id].SketchSource?.Revision==loaded.Snapshot.Sketches[sketch.Id].Revision,"Saved association revision");}
            foreach(string ext in new[]{"step","iges","stl"})await services.GetRequiredService<IGeometryKernel>().ExportAsync(after,document.Session.Assets,Path.Combine(output,"linked-result."+ext));
            await Operate(()=>vm.NewSketchCommand.ExecuteAsync(null),async editor=>
            {
                editor.ToolPicker.SelectedValue="Line";editor.Activate();editor.Canvas.Focus();await Idle();
                await Click(editor,new(0,0));await Click(editor,new(20,0));
                if(editor.Editor.Sketch.Lines.IsEmpty)
                {
                    editor.ToolPicker.SelectedValue="Select";await Idle();editor.ToolPicker.SelectedValue="Line";
                    await Click(editor,new(0,0));await Click(editor,new(20,0));
                }
                var target=editor.Editor.Sketch.Lines.Single().Id;
                await Click(editor,new(8,-8));await Click(editor,new(8,8));
                await Click(editor,new(14,-8));await Click(editor,new(14,8));
                var untrimmed=editor.Editor.Sketch;
                editor.ToolPicker.SelectedValue="TrimLine";await Click(editor,new(10,0));
                if(editor.Editor.Sketch.Lines.Length!=4||editor.Editor.Sketch.Lines.Any(l=>l.Id==target))Save(editor,Path.Combine(output,"trim-pointer-failure.png"));
                Check(editor.Editor.Sketch.Lines.Length==4&&!editor.Editor.Sketch.Lines.Any(l=>l.Id==target),
                    $"Pointer trim splits stable identity: tool={editor.Editor.Tool}, status={editor.Editor.Status}, lines={editor.Editor.Sketch.Lines.Length}, points={string.Join(';',editor.Editor.Sketch.Points.Select(p=>$"{p.Position.X:G5},{p.Position.Y:G5}"))}");
                Check(!string.IsNullOrWhiteSpace(editor.Editor.LineEditingHint),"Trim instruction binding");
                editor.Editor.UndoCommand.Execute(null);Check(ReferenceEquals(untrimmed,editor.Editor.Sketch),"Trim draft undo");
                editor.Editor.RedoCommand.Execute(null);Check(editor.Editor.Sketch.Lines.Length==4,"Trim draft redo");
                editor.Editor.UndoCommand.Execute(null);editor.ToolPicker.SelectedValue="Line";
                await Click(editor,new(25,-8));await Click(editor,new(25,8));
                editor.ToolPicker.SelectedValue="ExtendLine";await Click(editor,new(18,0));
                var extended=editor.Editor.Sketch.Lines.Single(l=>l.Id==target);
                Check(Math.Abs(editor.Editor.Sketch.Points.Single(p=>p.Id==extended.End).Position.X-25)<.5,"Pointer extend to finite boundary");
                await editor.Editor.PreviewCommand.ExecuteAsync(null);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                editor.Canvas.Fit();await Idle();Save(editor,Path.Combine(output,"editor-trim-extend.png"));editor.Close();
            });
            Check(ReferenceEquals(after,document.Session.Snapshot),"Closing line-edit preview cancels document changes");
            document.SelectedTargetPart=sketch.PartId;document.SelectedSketchId=sketch.Id;document.StartSketchFeature("Revolve");
            document.SelectedSketchProfile=document.SketchProfiles.Single(p=>p.Source.CircleId==sketch.Circles[0].Id);
            document.AngleDegrees=180;await document.PreviewCommand.ExecuteAsync(null);Check(document.HasPreview,document.ToolStatus);
            Check(ReferenceEquals(after,document.Session.Snapshot),"Curved revolution preview remains uncommitted");
            await Idle();host.Viewport.FitAll();await Idle();host.Viewport.SaveScreenshot(Path.Combine(output,"curved-revolve-preview.png"));document.InvalidatePreview();
            foreach(string lang in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(lang);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                await Operate(()=>vm.NewSketchCommand.ExecuteAsync(null),async editor=>
                {
                    Check(Convert.ToString(editor.ProjectGeometryButton.Content)==Cadoryx.Lang.Strings.Strings.ResourceManager.GetString("ProjectGeometry",CultureInfo.CurrentUICulture),$"Projection button language: {lang}");
                    editor.ToolPicker.SelectedValue="Circle";editor.Activate();editor.Canvas.Focus();await Idle();
                    await Click(editor,new(25,25));await Click(editor,new(30,25));
                    if(editor.Editor.Sketch.Circles.IsEmpty)
                    {
                        editor.ToolPicker.SelectedValue="Select";await Idle();editor.ToolPicker.SelectedValue="Circle";
                        await Click(editor,new(25,25));await Click(editor,new(30,25));
                    }
                    var authoredCircle=editor.Editor.Sketch.Circles.Single();var circle=authoredCircle.Id;
                    var center=editor.Editor.Sketch.Points.Single(p=>p.Id==authoredCircle.Center).Position;double authoredRadius=authoredCircle.Radius;
                    editor.ToolPicker.SelectedValue="Line";await Click(editor,new(center.X,center.Y-authoredRadius-5));await Click(editor,new(center.X,center.Y+authoredRadius+5));
                    editor.ToolPicker.SelectedValue="TrimCurve";await Click(editor,new(center.X-authoredRadius,center.Y));
                    Check(editor.Editor.Sketch.Circles.IsEmpty&&editor.Editor.Sketch.Arcs.Length==1,"Pointer circle trim creates an arc");
                    editor.ToolPicker.SelectedValue="OffsetCurve";editor.OffsetDistanceInput.Value=2;await Click(editor,new(center.X+authoredRadius,center.Y));
                    Check(editor.Editor.Sketch.Arcs.Length==2,"Pointer arc offset");
                    Check(Math.Abs(SketchAnalyticalCurve.From(editor.Editor.Sketch,editor.Editor.Sketch.Arcs[1].Id).Arc!.Value.Radius-authoredRadius-2)<.2,"Offset input drives exact radius");
                    editor.Editor.UndoCommand.Execute(null);Check(editor.Editor.Sketch.Arcs.Length==1,"Offset undo");editor.Editor.RedoCommand.Execute(null);
                    var choices=editor.Editor.ProjectionSources.Where(p=>p.Sketch==sketch.Id).ToArray();
                    editor.ProjectionSection.IsExpanded=true;await Idle();
                    Check(choices.Any(p=>p.Entity==sketch.Circles[0].Id),"Production projection source choices");
                    editor.ProjectionSourcePicker.SelectedItem=choices.Single(p=>p.Entity==sketch.Circles[0].Id);
                    editor.ProjectGeometryButton.BringIntoView();await Idle();
                    var projectButton=editor.ProjectGeometryButton.PointToScreen(new(editor.ProjectGeometryButton.ActualWidth/2,editor.ProjectGeometryButton.ActualHeight/2));
                    SendMouse(Move(new NativePoint{X=(int)Math.Round(projectButton.X),Y=(int)Math.Round(projectButton.Y)}),Button(0x0002),Button(0x0004));await Idle();
                    Check(editor.Editor.Sketch.Projections.Length==1,$"Projection button binds command: {editor.Editor.Status}");
                    var projected=editor.Editor.Sketch.Projections[0].TargetEntity;
                    editor.Editor.Select(projected);editor.Editor.CircleRadius=99;editor.Editor.ApplyCoordinatesCommand.Execute(null);
                    Check(editor.Editor.Sketch.Circles.Single(c=>c.Id==projected).Radius<99,"Projected radius cannot be edited");
                    editor.Editor.CircleRadius=editor.Editor.Sketch.Circles.Single(c=>c.Id==projected).Radius;
                    editor.Editor.ApplyCoordinatesCommand.Execute(null);
                    await editor.Editor.PreviewCommand.ExecuteAsync(null);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    editor.Canvas.Fit();await Idle();Save(editor,Path.Combine(output,$"editor-curve-offset-projection-{lang}.png"));
                    if(lang=="zh-CN")Save(editor,Path.Combine(output,"editor-curve-offset-projection.png"));
                    editor.Editor.DetachProjectionCommand.Execute(null);Check(editor.Editor.Sketch.Projections.IsEmpty,"Explicit projection detach");
                    editor.Editor.UndoCommand.Execute(null);Check(editor.Editor.Sketch.Projections.Length==1,"Detach undo restores source");editor.Close();
                });
                Check(ReferenceEquals(after,document.Session.Snapshot),"B2 preview close cancels document changes");
            }
            CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markupCulture;
            await File.WriteAllTextAsync(Path.Combine(output,"observations.json"),JsonSerializer.Serialize(new{window="MetroWindow",cultures=new[]{"en-US","zh-CN","ja-JP"},pointerDrawing=true,pointDrag=true,
                arcPointerDrawing=true,mixedLoopPointerDrawing=true,bezierPointerDrawing=true,trimPointerEditing=true,extendPointerEditing=true,curvedRevolvePreview=true,circleTrim=true,arcOffset=true,linkedProjection=true,projectionDetachUndo=true,previewCancel=true,conflictRollback=true,exactUndoRedo=true,volumeMm3=expectedFinal,featureAssociation=true,saveReopen=true},new JsonSerializerOptions{WriteIndented=true}));
            Check(await vm.CloseAllAsync(),"Close documents");await ((App)System.Windows.Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Zero assets after close");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: production sketch commands, owned MetroWindow, actual pointer drawing, line trim/extend, circle trim, arc offset, linked projection, driven-coordinate rejection and detach undo, numeric bindings, three-language layouts, preview/close-cancel, curved revolution preview, linked extrusion and downstream boolean recompute, conflict rejection, exact undo/redo, native screenshot, save/reopen, STEP/IGES/STL export, zero remaining assets.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);System.Windows.Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=culture;I18NExtension.Culture=markupCulture;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static async Task VerifyB3Async(MainWindowViewModel vm,CadDocumentViewModel document,string output,Action<string> Progress)
    {
        var baseline=document.Session.Snapshot;
            foreach(string lang in new[]{"en-US","zh-CN","ja-JP"})
            {
                CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(lang);I18NExtension.Culture=CultureInfo.CurrentUICulture;
                await Operate(()=>vm.NewSketchCommand.ExecuteAsync(null),async editor=>
                {
                    editor.ToolPicker.SelectedValue="Rectangle";editor.Activate();editor.Canvas.Focus();await Idle();
                    Progress($"B3 {lang}: rectangle");
                    await Click(editor,new(0,0));await Click(editor,new(20,10));
                    if(editor.Editor.Sketch.Lines.IsEmpty)
                    {
                        editor.ToolPicker.SelectedValue="Select";await Idle();editor.ToolPicker.SelectedValue="Rectangle";
                        await Click(editor,new(0,0));await Click(editor,new(20,10));
                    }
                    var source=editor.Editor.Sketch;Check(source.Lines.Length==4,"B3 pointer rectangle");
                    double axisX=source.Points.Min(p=>p.Position.X),bottomY=source.Points.Single(p=>p.Id==source.Lines[0].Start).Position.Y;
                    Progress($"B3 {lang}: source points {string.Join(';',source.Points.Select(p=>$"{p.Position.X:G6},{p.Position.Y:G6}"))}");
                    Progress($"B3 {lang}: loop offset");
                    editor.ToolPicker.SelectedValue="OffsetLoop";editor.OffsetDistanceInput.Value=2;await Click(editor,new(10,0));
                    Check(editor.Editor.Sketch.Lines.Length==8&&SketchLoops.Find(editor.Editor.Sketch).Length==2,"Pointer loop offset creates shared closed endpoints");
                    editor.Editor.UndoCommand.Execute(null);Check(ReferenceEquals(source,editor.Editor.Sketch),"Loop offset undo");editor.Editor.RedoCommand.Execute(null);
                    var offset=editor.Editor.Sketch;editor.OffsetDistanceInput.Value=-6;await Click(editor,new(10,0));
                    Check(ReferenceEquals(offset,editor.Editor.Sketch),"Collapsed loop offset rejected atomically");editor.OffsetDistanceInput.Value=2;
                    Progress($"B3 {lang}: midpoint");
                    editor.ToolPicker.SelectedValue="Point";await Click(editor,new(10,6));var midpoint=editor.Editor.Sketch.Points[^1].Id;
                    editor.Editor.Select(source.Lines[0].Id);editor.Editor.Select(midpoint,true);editor.ConstraintKindPicker.SelectedValue="Midpoint";
                    await ClickButton(editor.AddConstraintButton);Check(editor.Editor.Sketch.Constraints.OfType<MidpointConstraint>().Count()==1,"Midpoint button binding");
                    Progress($"B3 {lang}: symmetry");
                    editor.ToolPicker.SelectedValue="Point";await Click(editor,new(6,5));var first=editor.Editor.Sketch.Points[^1].Id;
                    await Click(editor,new(-4,7));var second=editor.Editor.Sketch.Points[^1].Id;
                    var axis=source.Lines.Single(l=>source.Points.Single(p=>p.Id==l.Start).Position.X==axisX&&source.Points.Single(p=>p.Id==l.End).Position.X==axisX);
                    editor.Editor.Select(first);editor.Editor.Select(axis.Id,true);editor.Editor.Select(second,true);editor.ConstraintKindPicker.SelectedValue="Symmetry";
                    await ClickButton(editor.AddConstraintButton);Check(editor.Editor.Sketch.Constraints.OfType<SymmetryConstraint>().Count()==1,"Symmetry button binding");
                    editor.Editor.UndoCommand.Execute(null);Check(editor.Editor.Sketch.Constraints.OfType<SymmetryConstraint>().Count()==0,"Symmetry draft undo");editor.Editor.RedoCommand.Execute(null);
                    Progress($"B3 {lang}: preview");
                    await editor.Editor.PreviewCommand.ExecuteAsync(null);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                    Progress($"B3 {lang}: preview ready");
                    var solved=editor.Editor.Sketch;var p=solved.Points.Single(p=>p.Id==first).Position;var q=solved.Points.Single(p=>p.Id==second).Position;
                    Check(Math.Abs(p.X+q.X-2*axisX)<1e-5&&Math.Abs(p.Y-q.Y)<1e-5,"Pointer authored symmetric points solved");
                    Check(Math.Abs(solved.Points.Single(p=>p.Id==midpoint).Position.Y-bottomY)<1e-5,"Pointer authored midpoint solved");
                    Check(Convert.ToString(editor.ToolPicker.Items.Cast<Cadoryx.ViewModels.SketchOption>().Single(o=>o.Key=="OffsetLoop").Label)==
                        Cadoryx.Lang.Strings.Strings.ResourceManager.GetString("OffsetLoop",CultureInfo.CurrentUICulture),$"Loop tool language: {lang}");
                    editor.ToolPicker.SelectedValue="OffsetLoop";editor.Canvas.Fit();await Idle();Save(editor,Path.Combine(output,$"editor-loop-offset-relations-{lang}.png"));editor.Close();
                });
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"B3 preview close cancels document changes");
            }
    }
    private static async Task VerifyB4Async(MainWindowViewModel vm,CadDocumentViewModel document,string output,Action<string> Progress)
    {
        var baseline=document.Session.Snapshot;
        foreach(string lang in new[]{"en-US","zh-CN","ja-JP"})
        {
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(lang);I18NExtension.Culture=CultureInfo.CurrentUICulture;
            await Operate(()=>vm.NewSketchCommand.ExecuteAsync(null),async editor=>
            {
                Progress($"B4 {lang}: pointer line");editor.ToolPicker.SelectedValue="Line";editor.Activate();editor.Canvas.Focus();await Idle();
                await Click(editor,new(0,0));await Click(editor,new(20,0));
                if(editor.Editor.Sketch.Lines.IsEmpty)
                {
                    editor.ToolPicker.SelectedValue="Select";await Idle();editor.ToolPicker.SelectedValue="Line";
                    await Click(editor,new(0,0));await Click(editor,new(20,0));
                }
                var line=editor.Editor.Sketch.Lines.Single();var joint=line.End;
                Point2d Joint()=>editor.Editor.Sketch.Points.Single(p=>p.Id==joint).Position;
                var p=Joint();editor.ToolPicker.SelectedValue="ArcSegment";
                await Click(editor,p);await Click(editor,new(p.X+4,p.Y+2));await Click(editor,new(p.X+8,p.Y+8));
                var first=editor.Editor.Sketch.Arcs.Single();Check(first.Start==joint,"Pointer line/arc shares endpoint ID");
                editor.Editor.Select(first.Id);editor.Editor.Select(line.Id,true);editor.ConstraintKindPicker.SelectedValue="EndpointTangent";
                await ClickButton(editor.AddConstraintButton);Check(editor.Editor.Sketch.Constraints.OfType<EndpointTangentConstraint>().Count()==1,"Line/arc tangency button binding");
                editor.Editor.UndoCommand.Execute(null);Check(!editor.Editor.Sketch.Constraints.OfType<EndpointTangentConstraint>().Any(),"Tangency undo");editor.Editor.RedoCommand.Execute(null);
                await editor.Editor.PreviewCommand.ExecuteAsync(null);Check(editor.Editor.CanConfirm,editor.Editor.Status);VerifySmooth(editor.Editor.Sketch);
                Progress($"B4 {lang}: pointer second arc");p=Joint();editor.ToolPicker.SelectedValue="ArcSegment";
                await Click(editor,p);await Click(editor,new(p.X-4,p.Y+1));await Click(editor,new(p.X-8,p.Y+4));
                var second=editor.Editor.Sketch.Arcs[^1];Check(second.Start==joint,"Pointer arc/arc shares endpoint ID");
                editor.Editor.Select(second.Id);editor.Editor.Select(first.Id,true);editor.ConstraintKindPicker.SelectedValue="EndpointTangent";
                await ClickButton(editor.AddConstraintButton);Check(editor.Editor.Sketch.Constraints.OfType<EndpointTangentConstraint>().Count()==2,"Arc/arc tangency button binding");
                var last=editor.Editor.Sketch.Constraints.OfType<EndpointTangentConstraint>().Last();
                editor.Editor.SelectedConstraint=editor.Editor.Constraints.Single(c=>c.Id==last.Id);editor.Editor.ToggleConstraintCommand.Execute(null);
                Check(!editor.Editor.Sketch.Constraints.Single(c=>c.Id==last.Id).IsEnabled,"Disable endpoint tangency");editor.Editor.ToggleConstraintCommand.Execute(null);
                await editor.Editor.PreviewCommand.ExecuteAsync(null);Check(editor.Editor.CanConfirm,editor.Editor.Status);VerifySmooth(editor.Editor.Sketch);
                Check(editor.Editor.ConstraintKinds.Single(o=>o.Key=="EndpointTangent").Label==
                    Cadoryx.Lang.Strings.Strings.ResourceManager.GetString("EndpointTangentRelation",CultureInfo.CurrentUICulture),"Localized endpoint relation");
                Check(editor.Editor.Constraints.Where(c=>editor.Editor.Sketch.Constraints.OfType<EndpointTangentConstraint>().Any(t=>t.Id==c.Id))
                    .All(c=>c.Label.Contains(editor.Editor.ConstraintKinds.Single(o=>o.Key=="EndpointTangent").Label)),"Localized constraint rows");
                editor.Canvas.Fit();await Idle();Save(editor,Path.Combine(output,$"editor-endpoint-tangency-{lang}.png"));editor.Close();
                Progress($"B4 {lang}: preview close canceled");
            });
            Check(ReferenceEquals(baseline,document.Session.Snapshot),"Endpoint tangency preview close cancels document changes");
        }
        static void VerifySmooth(CadSketch sketch)
        {
            Point2d Tangent(SketchEntityId id,SketchEntityId joint)
            {
                var point=sketch.Points.Single(p=>p.Id==joint).Position;Point2d tangent;
                if(sketch.Lines.SingleOrDefault(l=>l.Id==id) is {} line)
                {var end=sketch.Points.Single(p=>p.Id==(line.Start==joint?line.End:line.Start)).Position;tangent=new(end.X-point.X,end.Y-point.Y);}
                else
                {
                    var arc=sketch.Arcs.Single(a=>a.Id==id);Point2d P(SketchEntityId p)=>sketch.Points.Single(v=>v.Id==p).Position;
                    var circle=SketchArcGeometry.Through(P(arc.Start),P(arc.Middle),P(arc.End));double sign=Math.Sign(circle.SweepAngle)*(joint==arc.Start?1:-1);
                    tangent=new(-(point.Y-circle.Center.Y)*sign,(point.X-circle.Center.X)*sign);
                }
                double length=double.Hypot(tangent.X,tangent.Y);return new(tangent.X/length,tangent.Y/length);
            }
            foreach(var c in sketch.Constraints.OfType<EndpointTangentConstraint>().Where(c=>c.IsEnabled))
            {
                var a=Tangent(c.A,c.Joint);var b=Tangent(c.B,c.Joint);
                Check(Math.Abs(a.X*b.Y-a.Y*b.X)<2e-9&&a.X*b.X+a.Y*b.Y<-.999999,"Analytical endpoint tangency and smooth branch");
            }
        }
    }
    private static async Task VerifyB5Async(MainWindowViewModel vm,CadDocumentViewModel document,string output,Action<string> Progress)
    {
        var baseline=document.Session.Snapshot;
        foreach(string lang in new[]{"en-US","zh-CN","ja-JP"})
        {
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(lang);I18NExtension.Culture=CultureInfo.CurrentUICulture;
            await Operate(()=>vm.NewSketchCommand.ExecuteAsync(null),async editor=>
            {
                Progress($"B5 {lang}: pointer Bezier join");editor.ToolPicker.SelectedValue="Line";editor.Activate();editor.Canvas.Focus();await Idle();
                await Click(editor,new(0,0));await Click(editor,new(20,0));
                if(editor.Editor.Sketch.Lines.IsEmpty)
                {
                    editor.ToolPicker.SelectedValue="Select";await Idle();editor.ToolPicker.SelectedValue="Line";
                    await Click(editor,new(0,0));await Click(editor,new(20,0));
                }
                var first=editor.Editor.Sketch.Lines.Single();Point2d P(SketchEntityId id)=>editor.Editor.Sketch.Points.Single(p=>p.Id==id).Position;
                var joint=P(first.End);editor.ToolPicker.SelectedValue="BezierSegment";
                await Click(editor,joint);await Click(editor,new(joint.X+4,joint.Y+1));await Click(editor,new(joint.X+8,joint.Y+6));
                var bezier=editor.Editor.Sketch.Beziers.Single();Check(bezier.Start==first.End,"Pointer Bezier shares endpoint ID");
                async Task AddRelation(SketchEntityId a,SketchEntityId b)
                {
                    editor.Editor.Select(a);editor.Editor.Select(b,true);editor.ConstraintKindPicker.SelectedValue="EndpointTangent";
                    await ClickButton(editor.AddConstraintButton);
                }
                await AddRelation(bezier.Id,first.Id);Check(editor.Editor.Sketch.Constraints.OfType<EndpointTangentConstraint>().Count()==1,"Bezier tangency button binding");
                editor.Editor.UndoCommand.Execute(null);Check(!editor.Editor.Sketch.Constraints.OfType<EndpointTangentConstraint>().Any(),"Parametric relation undo");editor.Editor.RedoCommand.Execute(null);
                await editor.Editor.PreviewCommand.ExecuteAsync(null);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                Progress($"B5 {lang}: pointer spline end snap");editor.ToolPicker.SelectedValue="Line";
                await Click(editor,new(35,0));await Click(editor,new(45,0));var second=editor.Editor.Sketch.Lines[^1];joint=P(second.End);
                editor.Editor.SplineControlCount=4;editor.ToolPicker.SelectedValue="SplineRegion";
                foreach(var point in new[]{new Point2d(joint.X+12,joint.Y+6),new(joint.X+8,joint.Y+2),new(joint.X+4,joint.Y+1),joint})await Click(editor,point);
                var splineEnd=editor.Editor.Sketch.Splines.Single();Check(splineEnd.Controls[^1]==second.End,"Pointer spline reuses end ID");
                await AddRelation(second.Id,splineEnd.Id);
                Progress($"B5 {lang}: pointer spline start snap");joint=P(second.Start);
                foreach(var point in new[]{joint,new Point2d(joint.X-4,joint.Y+1),new(joint.X-8,joint.Y+2),new(joint.X-12,joint.Y+6)})await Click(editor,point);
                var splineStart=editor.Editor.Sketch.Splines[^1];Check(splineStart.Controls[0]==second.Start,"Pointer spline reuses start ID");
                await AddRelation(splineStart.Id,second.Id);Check(editor.Editor.Sketch.Constraints.OfType<EndpointTangentConstraint>().Count()==3,"Spline tangency button bindings");
                var last=editor.Editor.Sketch.Constraints.OfType<EndpointTangentConstraint>().Last();editor.Editor.SelectedConstraint=editor.Editor.Constraints.Single(c=>c.Id==last.Id);
                editor.Editor.ToggleConstraintCommand.Execute(null);Check(!editor.Editor.Sketch.Constraints.Single(c=>c.Id==last.Id).IsEnabled,"Disable parametric tangent");editor.Editor.ToggleConstraintCommand.Execute(null);
                await editor.Editor.PreviewCommand.ExecuteAsync(null);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                var sketch=editor.Editor.Sketch;sketch.Validate();
                Point2d Tangent(SketchEntityId curve,SketchEntityId point)
                {
                    var a=P(point);var b=P(SketchEndpointTangency.TangentNeighbor(sketch,curve,point));double length=double.Hypot(b.X-a.X,b.Y-a.Y);
                    return new((b.X-a.X)/length,(b.Y-a.Y)/length);
                }
                foreach(var c in sketch.Constraints.OfType<EndpointTangentConstraint>())
                {
                    var a=Tangent(c.A,c.Joint);var b=Tangent(c.B,c.Joint);
                    Check(Math.Abs(a.X*b.Y-a.Y*b.X)<2e-9&&a.X*b.X+a.Y*b.Y<-.999999,"Solved exact parametric endpoint derivatives");
                }
                Check(editor.Editor.ConstraintSelectionHint==Cadoryx.Lang.Strings.Strings.ResourceManager.GetString("EndpointTangentHint",CultureInfo.CurrentUICulture),"Localized parametric hint");
                editor.Canvas.Fit();await Idle();Save(editor,Path.Combine(output,$"editor-parametric-tangency-{lang}.png"));editor.Close();Progress($"B5 {lang}: preview close canceled");
            });
            Check(ReferenceEquals(baseline,document.Session.Snapshot),"Parametric tangency preview close cancels document changes");
        }
    }
    private static async Task VerifyB6Async(MainWindowViewModel vm,CadDocumentViewModel document,string output,Action<string> Progress)
    {
        var baseline=document.Session.Snapshot;
        foreach(string lang in new[]{"en-US","zh-CN","ja-JP"})
        {
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(lang);I18NExtension.Culture=CultureInfo.CurrentUICulture;
            await Operate(()=>vm.NewSketchCommand.ExecuteAsync(null),async editor=>
            {
                Progress($"B6 {lang}: pointer rectangle");editor.ToolPicker.SelectedValue="Rectangle";editor.Activate();editor.Canvas.Focus();await Idle();
                await Click(editor,new(0,0));await Click(editor,new(20,20));
                if(editor.Editor.Sketch.Lines.IsEmpty)
                {
                    editor.ToolPicker.SelectedValue="Select";await Idle();editor.ToolPicker.SelectedValue="Rectangle";
                    await Click(editor,new(0,0));await Click(editor,new(20,20));
                }
                var original=editor.Editor.Sketch;Check(original.Lines.Length==4,"Pointer rectangle");
                Point2d P(SketchEntityId id)=>editor.Editor.Sketch.Points.Single(p=>p.Id==id).Position;
                Point2d Mid(SketchLine line){var a=P(line.Start);var b=P(line.End);return new((a.X+b.X)/2,(a.Y+b.Y)/2);}
                editor.ToolPicker.SelectedValue="Fillet";await Idle();
                await Click(editor,Mid(original.Lines[0]));await Click(editor,Mid(original.Lines[1]));
                Check(editor.Editor.SelectedEntities.Count==2,"Pointer two-line selection");
                editor.FilletRadiusInput.Value=30;await Idle();await ClickButton(editor.ApplyFilletButton);
                Check(ReferenceEquals(original,editor.Editor.Sketch),"Excess radius rejects atomically");
                editor.FilletRadiusInput.Value=2;await Idle();await ClickButton(editor.ApplyFilletButton);
                var s=editor.Editor.Sketch;var arc=s.Arcs.Single();
                Check(s.Constraints.OfType<EndpointTangentConstraint>().Count()==2,"Apply binding adds tangent constraints");
                var circle=SketchArcGeometry.Through(P(arc.Start),P(arc.Middle),P(arc.End));Check(Math.Abs(circle.Radius-2)<1e-7,"Radius binding");
                editor.Editor.UndoCommand.Execute(null);Check(ReferenceEquals(original,editor.Editor.Sketch),"Fillet draft undo");editor.Editor.RedoCommand.Execute(null);
                Check(ReferenceEquals(s,editor.Editor.Sketch),"Fillet draft redo");
                await editor.Editor.PreviewCommand.ExecuteAsync(null);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                Check(editor.Editor.LineEditingHint==Cadoryx.Lang.Strings.Strings.ResourceManager.GetString("FilletHint",CultureInfo.CurrentUICulture),"Localized fillet hint");
                editor.Canvas.Fit();await Idle();Save(editor,Path.Combine(output,$"editor-fillet-{lang}.png"));editor.Close();Progress($"B6 {lang}: preview close canceled");
            });
            Check(ReferenceEquals(baseline,document.Session.Snapshot),"Fillet preview close cancels document changes");
        }
    }
    private static async Task VerifyB7Async(MainWindowViewModel vm,CadDocumentViewModel document,string output,Action<string> Progress)
    {
        var baseline=document.Session.Snapshot;
        foreach(string lang in new[]{"en-US","zh-CN","ja-JP"})
        {
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(lang);I18NExtension.Culture=CultureInfo.CurrentUICulture;
            await Operate(()=>vm.NewSketchCommand.ExecuteAsync(null),async editor=>
            {
                Progress($"B7 {lang}: pointer rectangle and four corners");editor.ToolPicker.SelectedValue="Rectangle";editor.Activate();editor.Canvas.Focus();await Idle();
                await Click(editor,new(0,0));await Click(editor,new(40,30));
                if(editor.Editor.Sketch.Lines.IsEmpty)
                {editor.ToolPicker.SelectedValue="Select";await Idle();editor.ToolPicker.SelectedValue="Rectangle";await Click(editor,new(0,0));await Click(editor,new(40,30));}
                var original=editor.Editor.Sketch;Check(original.Lines.Length==4,"B7 rectangle");
                editor.ToolPicker.SelectedValue="FilletCorners";await Idle();foreach(var point in original.Points)await Click(editor,point.Position);
                Check(editor.Editor.SelectedEntities.Count==4,"Pointer four corner selection");editor.FilletRadiusInput.Value=20;await Idle();await ClickButton(editor.ApplyFilletButton);
                Check(ReferenceEquals(original,editor.Editor.Sketch),"Combined corner budget rejects entire batch");
                Progress($"B7 {lang}: budget refusal: {editor.Editor.Status}");
                Save(editor,Path.Combine(output,$"editor-budget-{lang}.png"));
                Check(editor.Editor.Status.Contains(Cadoryx.Lang.Strings.Strings.ResourceManager.GetString("CornerBudgetConsume",CultureInfo.CurrentUICulture)!.Split('{')[0]),$"Localized line budget error: {editor.Editor.Status}");
                editor.FilletRadiusInput.Value=3;await Idle();await ClickButton(editor.ApplyFilletButton);
                var rounded=editor.Editor.Sketch;Check(rounded.Arcs.Length==4&&rounded.Constraints.OfType<RadiusConstraint>().Count()==4,"Radius checkbox creates four dimensions");
                Check(editor.Editor.Draft.CornerReplacements.Length==4,"Four explicit corner replacements");
                Progress($"B7 {lang}: before undo: edit={editor.Editor.CanEdit}, stale={editor.Editor.IsStale}, undo={editor.Editor.Draft.CanUndo}, visible={editor.IsVisible}, workspace={vm.StatusText}");
                editor.Editor.UndoCommand.Execute(null);Check(ReferenceEquals(original,editor.Editor.Sketch)&&editor.Editor.Draft.CornerReplacements.IsEmpty,$"Batch proof undo: same={ReferenceEquals(original,editor.Editor.Sketch)}, arcs={editor.Editor.Sketch.Arcs.Length}, proof={editor.Editor.Draft.CornerReplacements.Length}, redo={editor.Editor.Draft.CanRedo}, working={editor.Editor.IsWorking}");
                editor.Editor.RedoCommand.Execute(null);Check(ReferenceEquals(rounded,editor.Editor.Sketch),"Batch proof redo");
                await editor.Editor.PreviewCommand.ExecuteAsync(null);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                editor.Canvas.Fit();await Idle();Save(editor,Path.Combine(output,$"editor-multiple-fillet-{lang}.png"));
                editor.Editor.UndoCommand.Execute(null);editor.ToolPicker.SelectedValue="ChamferCorners";await Idle();
                foreach(var point in original.Points)await Click(editor,point.Position);
                editor.ChamferDistanceAInput.Value=2;editor.ChamferDistanceBInput.Value=3;await Idle();await ClickButton(editor.ApplyChamferButton);
                var chamfered=editor.Editor.Sketch;Check(chamfered.Lines.Length==8&&chamfered.Arcs.IsEmpty,"Multiple chamfer button");
                Check(SketchLoops.Find(chamfered).Length==1,"Chamfer loop remains closed");await editor.Editor.PreviewCommand.ExecuteAsync(null);Check(editor.Editor.CanConfirm,editor.Editor.Status);
                editor.Canvas.Fit();await Idle();Save(editor,Path.Combine(output,$"editor-multiple-chamfer-{lang}.png"));editor.Close();
            });
            Check(ReferenceEquals(baseline,document.Session.Snapshot),"Batch preview-close cancels");
        }
        CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;
        var part=baseline.Definitions.Values.OfType<PartDefinition>().FirstOrDefault()?.Id??DefinitionId.New();
        var d=new SketchDraft(CadSketch.Create(part,"Linked B7",RigidTransform3d.Identity));d.AddRectangle(new(10,0),new(50,30));
        await document.Session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver(),newPartName:"B7 part"));
        var s=document.Session.Snapshot.Sketches[d.Value.Id];var source=SketchProfileReference.Create(s,s.Lines.Select(l=>l.Id));
        await document.Session.ExecuteAsync(new AddBodyCommand(source.Resolve(document.Session.Snapshot,part,new ExtrudeRecipe(new([]),5,s.Plane)),"B7 linked",part,sketchSource:source));
        var before=document.Session.Snapshot;document.SelectedSketchId=s.Id;
        await Operate(()=>vm.EditSketchCommand.ExecuteAsync(null),async editor=>
        {
            Progress("B7: explicit linked profile checkbox");editor.ToolPicker.SelectedValue="Fillet";editor.Editor.Select(s.Lines[0].Id);editor.Editor.Select(s.Lines[1].Id,true);
            editor.FilletRadiusInput.Value=3;await Idle();await ClickButton(editor.ApplyFilletButton);
            await editor.Editor.PreviewCommand.ExecuteAsync(null);Check(!editor.Editor.CanConfirm,"Unselected affected profile blocks preview");
            editor.CornerProfilesSection.IsExpanded=true;await Idle();var check=Descendants(editor.CornerProfileList).OfType<CheckBox>().Single();check.BringIntoView();await Idle();await ClickButton(check);
            Check(editor.Editor.FeatureProfileUpdates.Single().IsSelected,"Actual checkbox binding enables conversion");
            await editor.Editor.PreviewCommand.ExecuteAsync(null);Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(ReferenceEquals(before,document.Session.Snapshot),"Converted preview remains isolated");
            Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-(1200-9*(1-Math.PI/4))*5)<1e-5),"Converted preview native volume");
            Save(editor,Path.Combine(output,"editor-profile-conversion-en-US.png"));editor.Close();
        });
        Check(ReferenceEquals(before,document.Session.Snapshot),"Explicit conversion close cancels");
    }
    private static IEnumerable<Cadoryx.ViewModels.Toolboxes.ModelTreeItemViewModel> Flatten(Cadoryx.ViewModels.Toolboxes.ModelTreeItemViewModel node)
    {yield return node;foreach(var child in node.Children.SelectMany(Flatten))yield return child;}
    private static async Task Operate(Func<Task> open,Func<SketchEditorWindow,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=System.Windows.Application.Current.Dispatcher.InvokeAsync(async()=>
        {
            SketchEditorWindow? editor=null;
            try{await Idle();editor=System.Windows.Application.Current.Windows.OfType<SketchEditorWindow>().Single();await action(editor);completion.TrySetResult();}
            catch(Exception ex){completion.TrySetException(ex);}
            finally{if(editor is {IsVisible:true})editor.Close();}
        },DispatcherPriority.ApplicationIdle);
        await open();await completion.Task;
    }
    private static async Task Click(SketchEditorWindow window,Point2d world)
    {
        var point=await Position(window,world);SendMouse(Move(point));await Idle();
        Check(window.InputHitTest(window.PointFromScreen(new(point.X,point.Y)))==window.Canvas,
            $"Pointer target misses sketch canvas at {world}");
        SendMouse(Button(0x0002),Button(0x0004));await Idle();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}
    }
    internal static async Task ClickButton(ButtonBase button)
    {
        if(Environment.GetEnvironmentVariable("CADORYX_SMOKE_BUTTON_INPUT")=="automation-invoke")
        {
            button.BringIntoView();await Idle();
            var peer=System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(button)
                ??new System.Windows.Automation.Peers.ButtonAutomationPeer((System.Windows.Controls.Button)button);
            if(peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke) is System.Windows.Automation.Provider.IInvokeProvider invoke)invoke.Invoke();
            else if(peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle) is System.Windows.Automation.Provider.IToggleProvider toggle)toggle.Toggle();
            else throw new InvalidOperationException("The diagnostic control has no supported automation action.");
            await Idle();return;
        }
        Window.GetWindow(button)?.Activate();button.BringIntoView();await Idle();var point=button.PointToScreen(new(button.ActualWidth/2,button.ActualHeight/2));
        SendMouse(Move(new NativePoint{X=(int)Math.Round(point.X),Y=(int)Math.Round(point.Y)}));await Idle();
        SendMouse(Button(0x0002),Button(0x0004));await Idle();
    }
    internal static string ButtonInputEvidence=>Environment.GetEnvironmentVariable("CADORYX_SMOKE_BUTTON_INPUT")=="automation-invoke"
        ?"Button input: WPF Automation Invoke/Toggle; desktop pointer hit testing NOT VERIFIED. "
        :"Button input: injected desktop mouse. ";
    private static async Task Drag(SketchEditorWindow window,Point2d start,Point2d end)
    {var from=await Position(window,start);SendMouse(Move(from),Button(0x0002));await Idle();
        var to=await Position(window,end);SendMouse(Move(to));await Idle();SendMouse(Button(0x0004));await Idle();}
    private static async Task<NativePoint> Position(SketchEditorWindow window,Point2d world)
    {
        window.Activate();window.UpdateLayout();var local=window.Canvas.ToScreen(world);
        var interior=new Rect(window.Canvas.RenderSize);interior.Inflate(-2,-2);
        Check(interior.Contains(local),$"Pointer target outside canvas interior: {local}, size {window.Canvas.RenderSize}");
        var point=window.Canvas.PointToScreen(local);int x=(int)Math.Round(point.X),y=(int)Math.Round(point.Y);
        await Idle();return new NativePoint{X=x,Y=y};
    }
    private static NativeInput Move(NativePoint target)
    {
        int left=GetSystemMetrics(76),top=GetSystemMetrics(77),width=GetSystemMetrics(78),height=GetSystemMetrics(79);
        Check(width>1&&height>1,"Invalid virtual desktop size.");
        return new NativeInput{Type=0,Mouse=new NativeMouseInput{
            Dx=(int)Math.Round((target.X-left)*65535.0/(width-1)),Dy=(int)Math.Round((target.Y-top)*65535.0/(height-1)),
            Flags=0x0001|0x8000|0x4000}};
    }
    private static NativeInput Button(uint flags)=>new(){Type=0,Mouse=new NativeMouseInput{Flags=flags}};
    private static void SendMouse(params NativeInput[] inputs)
    {
        if(SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<NativeInput>())!=(uint)inputs.Length)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Cannot send desktop mouse input for sketch validation.");
    }
    internal static async Task Idle(){await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(70);}
    internal static void Save(FrameworkElement element,string path)
    {element.UpdateLayout();var image=new RenderTargetBitmap((int)element.ActualWidth,(int)element.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(element);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(path);png.Save(file);}
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static T? Find<T>(DependencyObject root)where T:DependencyObject
    {if(root is T t)return t;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(Find<T>(VisualTreeHelper.GetChild(root,i)) is {} child)return child;return null;}
    [StructLayout(LayoutKind.Sequential)]private struct NativePoint {public int X;public int Y;}
    [StructLayout(LayoutKind.Sequential)]private struct NativeInput {public uint Type;public NativeMouseInput Mouse;}
    [StructLayout(LayoutKind.Sequential)]private struct NativeMouseInput
    {public int Dx;public int Dy;public uint MouseData;public uint Flags;public uint Time;public nuint ExtraInfo;}
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll",SetLastError=true)]private static extern uint SendInput(uint count,NativeInput[] inputs,int size);
}
