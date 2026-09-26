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
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static class SketchEditorSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);using var bindings=new StreamWriter(Path.Combine(output,"bindings.log"));using var listener=new TextWriterTraceListener(bindings);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var culture=CultureInfo.CurrentUICulture;
        try
        {
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
            CultureInfo.CurrentUICulture=culture;var after=document.Session.Snapshot;double expectedFinal=60*heightMm*10-cutVolume;
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
            await File.WriteAllTextAsync(Path.Combine(output,"observations.json"),JsonSerializer.Serialize(new{window="MetroWindow",cultures=new[]{"en-US","zh-CN","ja-JP"},pointerDrawing=true,pointDrag=true,
                arcPointerDrawing=true,mixedLoopPointerDrawing=true,bezierPointerDrawing=true,previewCancel=true,conflictRollback=true,exactUndoRedo=true,volumeMm3=expectedFinal,featureAssociation=true,saveReopen=true},new JsonSerializerOptions{WriteIndented=true}));
            Check(await vm.CloseAllAsync(),"Close documents");await ((App)System.Windows.Application.Current).StopRecoveryAsync();await Idle();
            Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Zero assets after close");listener.Flush();bindings.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: production sketch commands, owned MetroWindow, actual pointer point/line/rectangle/circle/arc and mixed-loop drawing with point drag, numeric dimension bindings, three-language layouts, preview/close-cancel, linked extrusion and downstream boolean recompute, conflict rejection, exact undo/redo, native screenshot, save/reopen, STEP/IGES/STL export, zero remaining assets.");window.CloseAfterSmoke();
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);System.Windows.Application.Current.Shutdown(1);}
        finally{CultureInfo.CurrentUICulture=culture;PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
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
    {var point=await Position(window,world);SendMouse(Move(point),Button(0x0002),Button(0x0004));await Idle();}
    private static async Task Drag(SketchEditorWindow window,Point2d start,Point2d end)
    {var from=await Position(window,start);SendMouse(Move(from),Button(0x0002));await Idle();
        var to=await Position(window,end);SendMouse(Move(to));await Idle();SendMouse(Button(0x0004));await Idle();}
    private static async Task<NativePoint> Position(SketchEditorWindow window,Point2d world)
    {
        window.Activate();window.UpdateLayout();var local=window.Canvas.ToScreen(world);
        Check(new Rect(window.Canvas.RenderSize).Contains(local),$"Pointer target outside canvas: {local}, size {window.Canvas.RenderSize}");
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
    private static async Task Idle(){await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(70);}
    private static void Save(FrameworkElement element,string path)
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
