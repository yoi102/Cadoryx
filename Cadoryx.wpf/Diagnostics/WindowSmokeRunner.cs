using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using MahApps.Metro.Controls;
using System.Windows.Threading;
using AvalonDock.Layout;
using Cadoryx.Db;
using Cadoryx.Commands;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering;
using Cadoryx.Rendering.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Settings;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Views;
using Cadoryx.wpf.Views.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static class WindowSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output,string fixtures)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindingOutput=new StreamWriter(Path.Combine(output,"bindings.log"));
        using var listener=new TextWriterTraceListener(bindingOutput);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        var observations=new List<object>();
        try
        {
            await Idle();var vm=(MainWindowViewModel)window.DataContext;
            var historySession=vm.ActiveDocument!.Session;var historyKernel=services.GetRequiredService<IGeometryKernel>();
            await historySession.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Window history box"));
            var historyBox=historySession.Snapshot.Features.Values.Single(f=>f.Name=="Window history box");
            await historySession.ExecuteAsync(new LocalFeatureCommand(TopologyReference.Box(historySession.Snapshot,historyBox.Id,BoxBoundary.XMin,BoxBoundary.YMin),LocalFeatureOperation.Fillet,1));
            var historyFirst=historySession.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            var historyOrigin=TopologyReference.Box(historySession.Snapshot,historyBox.Id,BoxBoundary.XMax,BoxBoundary.YMax);
            await historySession.ExecuteAsync(new UpsertHistoryQueryCommand(new(HistoryQueryId.New(),"Window confirmed edge",historyOrigin,historyFirst.Id)));
            await using(var historyEditor=new HistoryQueryViewModel(historySession,historyKernel))
            {
                var historyWindow=new Views.HistoryQueryWindow(historyEditor){Owner=window};
                historyWindow.Show();await Idle();
                Check(historyWindow.IsLoaded&&historyWindow.Owner==window&&historyWindow.Title==historyEditor.Labels.Title,"History MetroWindow and localized title");
                if(historyEditor.AnalyzeCommand.ExecutionTask is {} analysis)await analysis;
                Check(historyEditor.ResultSummary.Contains("Edge #",StringComparison.Ordinal),"Window analyzed an exact edge history target");
                await historyEditor.CreateFilletCommand.ExecuteAsync(null);
                var historyBound=historySession.Snapshot.Features.Values.Single(f=>f.Recipe is HistoryFilletRecipe);
                Check(!historyBound.IsStale,"Window created a bound fillet from confirmed edge");
                await historySession.ExecuteAsync(new RecomputeCommand(historyBox.Id,new BoxRecipe(11,12,15,RigidTransform3d.Identity)));
                Check(historySession.Snapshot.Features[historyBound.Id].IsStale,"Window upstream edit invalidated bound fillet");
                await historyEditor.AnalyzeCommand.ExecuteAsync(null);
                historyEditor.SelectedStaleConsumer=historyEditor.StaleConsumers.Single(c=>c.Id==historyBound.Id);
                await historyEditor.RebindFilletCommand.ExecuteAsync(null);
                Check(!historySession.Snapshot.Features[historyBound.Id].IsStale,"Window explicitly rebound the fillet");
                historyWindow.UpdateLayout();
                var historyImage=new RenderTargetBitmap((int)historyWindow.ActualWidth,(int)historyWindow.ActualHeight,96,96,PixelFormats.Pbgra32);
                historyImage.Render(historyWindow);var historyPng=new PngBitmapEncoder();historyPng.Frames.Add(BitmapFrame.Create(historyImage));
                using(var file=File.Create(Path.Combine(output,"history-query-window.png")))historyPng.Save(file);
                historyWindow.Close();await Idle();
            }
            var drawingDoc=vm.ActiveDocument!;var drawingViewport=Host(drawingDoc).Viewport!;
            Check(drawingDoc.BackgroundTopArgb==DocumentSettings.DefaultBackgroundTopArgb&&
                drawingDoc.BackgroundBottomArgb==DocumentSettings.DefaultBackgroundBottomArgb,"New document uses sky gradient");
            drawingViewport.FitAll();
            var cubeHost=Host(drawingDoc);
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-cube-controls-before-hit.png"));
            Check(GetClientRect(cubeHost.Handle,out var cubeBounds),"ViewCube client bounds available");
            (int X,int Y)? cubePixel=null;
            CadCamera? cubeTarget=null;
            var initialCubeCamera=drawingViewport.CaptureCamera();
            for(int y=12;y<Math.Min(cubeBounds.Bottom,165)&&cubePixel is null;y+=5)
                for(int x=Math.Max(0,cubeBounds.Right-165);x<cubeBounds.Right-12&&cubePixel is null;x+=5)
                    if(drawingViewport.HitViewCube(x,y) is {} orientation)
                    {
                        var candidate=drawingViewport.CaptureCubeTarget(orientation);
                        if((candidate.Eye-initialCubeCamera.Eye).Length>0.01)
                        {cubePixel=(x,y);cubeTarget=candidate;}
                    }
            Check(cubePixel is not null&&cubeTarget is not null,"Upper-right ViewCube has a selectable direction");
            var (cubeX,cubeY)=cubePixel ?? throw new InvalidOperationException("ViewCube hit disappeared.");
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-cube.png"));
            drawingViewport.PointerPressed(0,cubeX,cubeY,0);
            drawingViewport.PointerReleased(0,cubeX,cubeY,0);
            await Task.Delay(380);Camera(cubeTarget!,drawingViewport.CaptureCamera());
            drawingViewport.SetProjection(CadProjection.Front);drawingViewport.Redraw();
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-cube-face-arrows.png"));
            drawingViewport.PointerMoved(cubeBounds.Right-82,82,0,0);
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-cube-face-hover.png"));
            (int X,int Y)? leftArrow=null,rightArrow=null;
            for(int y=6;y<Math.Min(cubeBounds.Bottom,90)&&(leftArrow is null||rightArrow is null);y+=2)
                for(int x=Math.Max(0,cubeBounds.Right-175);x<cubeBounds.Right-8&&(leftArrow is null||rightArrow is null);x+=2)
                {
                    var turn=drawingViewport.HitViewCubeControl(x,y)?.Turn;
                    if(turn==OcctSharp.ViewerCubeTurn.Left)leftArrow=(x,y);
                    if(turn==OcctSharp.ViewerCubeTurn.Right)rightArrow=(x,y);
                }
            Check(leftArrow is not null&&rightArrow is not null,"Both curved ViewCube arrows are hit-testable");
            var left=leftArrow!.Value;var right=rightArrow!.Value;
            drawingViewport.PointerMoved(left.X,left.Y,0,0);
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-cube-left-hover.png"));
            Check(!SHA256.HashData(File.ReadAllBytes(Path.Combine(output,"view-cube-face-arrows.png"))).SequenceEqual(
                SHA256.HashData(File.ReadAllBytes(Path.Combine(output,"view-cube-face-hover.png")))),
                "ViewCube face hover visibly highlights the detected face");
            Check(!SHA256.HashData(File.ReadAllBytes(Path.Combine(output,"view-cube-face-arrows.png"))).SequenceEqual(
                SHA256.HashData(File.ReadAllBytes(Path.Combine(output,"view-cube-left-hover.png")))),
                "ViewCube arrow hover visibly highlights the detected arrow");
            var beforeTurn=drawingViewport.CaptureCamera();
            var leftTarget=drawingViewport.CaptureCubeTurnTarget(OcctSharp.ViewerCubeTurn.Left,45);
            drawingViewport.PointerPressed(0,left.X,left.Y,0);drawingViewport.PointerReleased(0,left.X,left.Y,0);
            await Task.Delay(380);Camera(leftTarget,drawingViewport.CaptureCamera());
            var cubeRightTarget=drawingViewport.CaptureCubeTurnTarget(OcctSharp.ViewerCubeTurn.Right,45);
            drawingViewport.PointerPressed(0,right.X,right.Y,0);drawingViewport.PointerReleased(0,right.X,right.Y,0);
            await Task.Delay(380);Camera(cubeRightTarget,drawingViewport.CaptureCamera());Camera(beforeTurn,drawingViewport.CaptureCamera());
            vm.ApplicationSettings.Viewport.ViewCubeRotationDegrees=30;
            var customTarget=drawingViewport.CaptureCubeTurnTarget(OcctSharp.ViewerCubeTurn.Right,30);
            drawingViewport.PointerPressed(0,right.X,right.Y,0);drawingViewport.PointerReleased(0,right.X,right.Y,0);
            await Task.Delay(380);Camera(customTarget,drawingViewport.CaptureCamera());
            vm.ApplicationSettings.Viewport.ViewCubeRotationDegrees=45;
            drawingViewport.RestoreCamera(initialCubeCamera);drawingViewport.Redraw();
            Check(drawingViewport.HitViewCubeControl(left.X,left.Y)?.Turn!=OcctSharp.ViewerCubeTurn.Left&&
                drawingViewport.HitViewCubeControl(right.X,right.Y)?.Turn!=OcctSharp.ViewerCubeTurn.Right,
                "Oblique ViewCube hides both roll arrows");
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-cube-oblique-no-arrows.png"));
            drawingViewport.SetViewCubeVisible(false);
            Check(drawingViewport.HitViewCube(cubeX,cubeY) is null,"Hidden ViewCube is not hit-testable");
            Check(drawingViewport.HitViewCubeControl(left.X,left.Y) is null&&drawingViewport.HitViewCubeControl(right.X,right.Y) is null,
                "Hidden ViewCube arrows are not hit-testable");
            drawingViewport.SetViewCubeVisible(true);
            Check(drawingViewport.HitViewCube(cubeX,cubeY) is not null,"ViewCube reappears after showing");
            var cameraBefore=drawingViewport.CaptureCamera();
            var frontTarget=drawingViewport.CaptureProjectionTarget(CadProjection.Front);
            Camera(cameraBefore,drawingViewport.CaptureCamera());
            drawingDoc.SetView(CadProjection.Front);
            Camera(cameraBefore,drawingViewport.CaptureCamera());
            await Task.Delay(90);
            var frontMid=drawingViewport.CaptureCamera();
            Check((frontMid.Eye-cameraBefore.Eye).Length>0.01&&
                (frontMid.Eye-frontTarget.Eye).Length>0.01,"Front view passes through an intermediate camera frame");
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-front-transition.png"));
            await Task.Delay(380);Camera(frontTarget,drawingViewport.CaptureCamera());
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-front-complete.png"));
            drawingDoc.SetView(CadProjection.Top);await Task.Delay(90);
            var interrupted=drawingViewport.CaptureCamera();
            var rightTarget=drawingViewport.CaptureProjectionTarget(CadProjection.Right);
            drawingDoc.SetView(CadProjection.Right);
            Camera(interrupted,drawingViewport.CaptureCamera());
            await Task.Delay(380);Camera(rightTarget,drawingViewport.CaptureCamera());
            drawingDoc.SetView(CadProjection.Top);await Task.Delay(90);
            drawingViewport.MouseWheel(120,200,200,0);
            var navigated=drawingViewport.CaptureCamera();
            await Task.Delay(380);Camera(navigated,drawingViewport.CaptureCamera());
            drawingViewport.RestoreCamera(cameraBefore);drawingViewport.Redraw();
            drawingViewport.SaveScreenshot(Path.Combine(output,"work-grid.png"));
            var settingsEditor=new DocumentSettingsViewModel(drawingDoc);
            var settingsWindow=new DocumentSettingsWindow(settingsEditor){Owner=window};
            settingsWindow.Show();await Task.Delay(450);await Idle();
            Check(settingsWindow.IsLoaded&&settingsWindow.Title==settingsEditor.Labels.Title,"Metro document settings window opened");
            var settingsSections=(ListBox)settingsWindow.FindName("Sections")!;
            Check(settingsSections.Items.Count==4&&settingsEditor.SelectedSection?.Id=="Display",
                "Document settings opens with Direct2dCad-style left navigation");
            CaptureSettings("document-settings-display.png");
            settingsSections.SelectedIndex=1;await Idle();
            Check(settingsEditor.SelectedSection?.Id=="Grid","Grid section selected from left navigation");
            CaptureSettings("document-settings-grid.png");
            settingsSections.SelectedIndex=2;await Idle();
            Check(settingsEditor.SelectedSection?.Id=="Origin","Origin section selected from left navigation");
            CaptureSettings("document-settings-origin.png");
            settingsSections.SelectedIndex=3;await Idle();
            Check(settingsEditor.SelectedSection?.Id=="Units","Units section selected from left navigation");
            CaptureSettings("document-settings-units.png");
            settingsSections.SelectedIndex=0;await Idle();
            void CaptureSettings(string name)
            {
                settingsWindow.UpdateLayout();
                var image=new RenderTargetBitmap((int)settingsWindow.ActualWidth,(int)settingsWindow.ActualHeight,96,96,PixelFormats.Pbgra32);
                image.Render(settingsWindow);
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));
                using var file=File.Create(Path.Combine(output,name));png.Save(file);
            }
            settingsEditor.BackgroundTopArgb=0xFF335577;
            settingsEditor.BackgroundBottomArgb=0xFFD1E4F1;
            settingsEditor.GridVisible=false;settingsEditor.GridSpacingMm=5;
            Check(await settingsEditor.TryApplyAsync(),"Document settings window applied background and grid");
            Check(!drawingDoc.GridVisible&&drawingDoc.GridSpacingMm==5,"Grid settings belong to the active CAD document");
            Check(drawingDoc.BackgroundTopArgb==0xFF335577&&drawingDoc.BackgroundBottomArgb==0xFFD1E4F1,
                "Native viewport gradient follows document setting");
            await Idle();
            var statusBar=FindStatusBar(window)??throw new InvalidOperationException("Status bar missing");
            Check(((CheckBox)statusBar.FindName("GridVisibleInput")!).IsChecked==false &&
                ((NumericUpDown)statusBar.FindName("GridSpacingInput")!).Value==5,
                "Status bar follows the current document grid");
            drawingViewport.SaveScreenshot(Path.Combine(output,"work-grid-hidden.png"));
            CheckBackground(Path.Combine(output,"work-grid-hidden.png"),0xFF335577,0xFFD1E4F1);
            var originCamera=drawingViewport.CaptureCamera();
            string colorAxes=Path.Combine(output,"origin-color-axes.png");
            drawingViewport.SaveScreenshot(colorAxes);
            settingsEditor.SelectedOriginStyle=settingsEditor.OriginStyleOptions.Single(o=>o.Style==DocumentOriginStyle.SubtleAxes);
            Check(await settingsEditor.TryApplyAsync(),"Subtle origin axes applied");
            string subtleAxes=Path.Combine(output,"origin-subtle-axes.png");
            drawingViewport.SaveScreenshot(subtleAxes);
            settingsEditor.SelectedOriginStyle=settingsEditor.OriginStyleOptions.Single(o=>o.Style==DocumentOriginStyle.OriginMarker);
            Check(await settingsEditor.TryApplyAsync(),"Origin marker applied");
            string originMarker=Path.Combine(output,"origin-marker.png");
            drawingViewport.SaveScreenshot(originMarker);
            settingsEditor.OriginVisible=false;
            Check(await settingsEditor.TryApplyAsync(),"Origin axes hidden");
            string hiddenOrigin=Path.Combine(output,"origin-hidden.png");
            drawingViewport.SaveScreenshot(hiddenOrigin);
            Check(!SHA256.HashData(File.ReadAllBytes(colorAxes)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(subtleAxes)))&&
                !SHA256.HashData(File.ReadAllBytes(subtleAxes)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(originMarker)))&&
                !SHA256.HashData(File.ReadAllBytes(originMarker)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(hiddenOrigin))),
                "Native origin presentation changes for every style and hidden state");
            Camera(originCamera,drawingViewport.CaptureCamera());
            settingsSections.SelectedIndex=1;await Idle();
            settingsEditor.GridVisible=true;
            settingsEditor.SelectedWorkPlane=settingsEditor.WorkPlaneOptions.Single(o=>o.Kind==DocumentWorkPlaneKind.XZ);
            settingsEditor.WorkPlaneOffsetMm=12;
            Check(await settingsEditor.TryApplyAsync(),"XZ work plane applied in document settings");
            Check(drawingDoc.WorkPlaneSettings==new DocumentWorkPlaneSettings(DocumentWorkPlaneKind.XZ,12),"Active document owns XZ work plane");
            var xzTarget=drawingDoc.WorkPlaneSettings.ToWorld(6,8);var xzPixel=drawingViewport.WorldToScreen(xzTarget);
            Check(drawingViewport.TryWorkplanePoint(xzPixel.X,xzPixel.Y,5,false,drawingDoc.WorkPlaneSettings,out var xzHit)&&
                (xzHit-xzTarget).Length<2,"Native XZ camera ray intersects offset plane");
            string xzGrid=Path.Combine(output,"work-grid-xz.png");drawingViewport.SaveScreenshot(xzGrid);
            CaptureSettings("document-settings-work-plane.png");
            settingsEditor.SelectedWorkPlane=settingsEditor.WorkPlaneOptions.Single(o=>o.Kind==DocumentWorkPlaneKind.YZ);
            Check(await settingsEditor.TryApplyAsync(),"YZ work plane applied in document settings");
            var yzTarget=drawingDoc.WorkPlaneSettings.ToWorld(6,8);var yzPixel=drawingViewport.WorldToScreen(yzTarget);
            Check(drawingViewport.TryWorkplanePoint(yzPixel.X,yzPixel.Y,5,false,drawingDoc.WorkPlaneSettings,out var yzHit)&&
                (yzHit-yzTarget).Length<2,"Native YZ camera ray intersects offset plane");
            string yzGrid=Path.Combine(output,"work-grid-yz.png");drawingViewport.SaveScreenshot(yzGrid);
            Check(!SHA256.HashData(File.ReadAllBytes(xzGrid)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(yzGrid))),
                "Native work grid changes orientation");
            drawingDoc.StartTool("Box");
            var yzStart=drawingViewport.WorldToScreen(drawingDoc.WorkPlaneSettings.ToWorld(2,3));
            var yzEnd=drawingViewport.WorldToScreen(drawingDoc.WorkPlaneSettings.ToWorld(8,9));
            Click(yzStart);drawingViewport.PointerMoved(yzEnd.X,yzEnd.Y,0,0);Click(yzEnd);
            drawingViewport.PointerMoved(yzEnd.X,yzEnd.Y-45,0,0);
            Check(drawingDoc.IsViewportConstructing&&drawingDoc.SizeX>4&&drawingDoc.SizeY>4&&
                Math.Abs(drawingDoc.PositionX-12)<1,"YZ pointer creates a work-plane-oriented box");
            drawingViewport.SaveScreenshot(Path.Combine(output,"mouse-box-yz-ghost.png"));
            drawingDoc.CancelViewportConstruction();
            settingsEditor.ResetToDefaults();Check(await settingsEditor.TryApplyAsync(),"Document settings reset to defaults");
            settingsWindow.Close();await Idle();
            drawingDoc.StartTool("Box");Check(drawingDoc.IsViewportConstructing,"Box viewport construction begins");
            var start=drawingViewport.WorldToScreen(new(0,0,0));var end=drawingViewport.WorldToScreen(new(6,8,0));
            Check(drawingViewport.TryWorkplanePoint(start.X,start.Y,drawingDoc.GridSpacingMm,false,out _),"Native XY workplane ray intersection");
            Click(start);drawingViewport.PointerMoved(end.X,end.Y,0,0);Click(end);
            drawingViewport.PointerMoved(end.X,end.Y-60,0,0);Click((end.X,end.Y-60));
            await Idle();if(drawingDoc.PreviewCommand.ExecutionTask is {} constructionPreview)await constructionPreview;
            Check(drawingDoc.HasPreview&&drawingDoc.PreviewScene is not null,"Mouse-defined box has a real uncommitted preview");
            drawingViewport.SaveScreenshot(Path.Combine(output,"mouse-box-preview.png"));
            drawingDoc.CancelCommand.Execute(null);Check(!drawingDoc.HasPreview,"Mouse construction cancel releases preview");
            void Click((int X,int Y) pixel){drawingViewport.PointerPressed(0,pixel.X,pixel.Y,0);drawingViewport.PointerReleased(0,pixel.X,pixel.Y,0);}
            int beforeProfileFeatures=drawingDoc.Session.Snapshot.Features.Count;
            string beforeGhost=Path.Combine(output,"mouse-profile-before.png");
            drawingViewport.SaveScreenshot(beforeGhost);
            drawingDoc.StartTool("Extrude");Click(start);
            drawingViewport.PointerMoved(start.X,start.Y-80,0,0);await Idle();
            Check(drawingDoc.IsViewportConstructing&&!drawingDoc.HasPreview&&
                drawingDoc.Session.Snapshot.Features.Count==beforeProfileFeatures,"Extrude movement stays uncommitted");
            string extrusionGhost=Path.Combine(output,"mouse-extrude-ghost.png");
            drawingViewport.SaveScreenshot(extrusionGhost);
            Check(!SHA256.HashData(File.ReadAllBytes(beforeGhost)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(extrusionGhost))),
                "Extrude movement shows a native profile solid");
            Click((start.X,start.Y-80));await Idle();
            if(drawingDoc.PreviewCommand.ExecutionTask is {} extrusionPreview)await extrusionPreview;
            Check(drawingDoc.HasPreview&&drawingDoc.Session.Snapshot.Features.Count==beforeProfileFeatures,
                "Extrude final click enters the existing uncommitted preview");
            drawingDoc.CancelCommand.Execute(null);Check(!drawingDoc.HasPreview,"Extrude preview cancels");
            drawingDoc.StartTool("Revolve");Click(start);
            drawingViewport.PointerMoved(start.X+180,start.Y,0,0);await Idle();
            Check(drawingDoc.IsViewportConstructing&&!drawingDoc.HasPreview&&
                drawingDoc.Session.Snapshot.Features.Count==beforeProfileFeatures,"Revolve movement stays uncommitted");
            string revolutionGhost=Path.Combine(output,"mouse-revolve-ghost.png");
            drawingViewport.SaveScreenshot(revolutionGhost);
            Check(!SHA256.HashData(File.ReadAllBytes(beforeGhost)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(revolutionGhost))),
                "Revolve movement shows a native profile solid");
            drawingDoc.CancelCommand.Execute(null);Check(!drawingDoc.IsViewportConstructing&&
                drawingDoc.Session.Snapshot.Features.Count==beforeProfileFeatures,"Revolve gesture cancels without model changes");
            await Idle();
            string afterGhostCancel=Path.Combine(output,"mouse-profile-canceled.png");
            drawingViewport.SaveScreenshot(afterGhostCancel);
            Check(SHA256.HashData(File.ReadAllBytes(beforeGhost)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(afterGhostCancel))),
                "Cancel removes the native construction shape");
            var storage=services.GetRequiredService<IDocumentStorage>();
            foreach(var initial in vm.Documents)await initial.Session.SaveAsync(storage,Path.Combine(output,"initial.cadoryx"));
            Check(await vm.CloseAllAsync(),"Initial documents close");await Idle();
            await LegacyStorage(vm,services,storage,output,Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fixtures))!,"Storage"));
            string saved=Path.Combine(output,"colors.cadoryx");
            await vm.OpenPathAsync(Path.Combine(fixtures,"rotated-colors.step"));await Idle();
            var doc=vm.ActiveDocument??throw new InvalidOperationException("Fixture did not open");
            await doc.Session.SaveAsync(storage,saved);
            var viewport=Host(doc).Viewport!;viewport.FitAll();
            CaptureColors(viewport,output,"source");
            var item=doc.Scene.Items[0];var selection=new SelectionTarget(item.Path,item.BodyId,item.Geometry.Revision);
            doc.Selection.Replace([selection]);await Idle();
            viewport.SaveScreenshot(Path.Combine(output,"selected.png"));
            doc.Selection.Replace([]);await Idle();CaptureColors(viewport,output,"deselected");
            doc.Selection.Replace([selection]);vm.Properties.ObjectName="Renamed colored box";
            await vm.Properties.ApplyCommand.ExecuteAsync(null);doc.Selection.Replace([]);await Idle();
            CaptureColors(viewport,output,"rename-colors");await doc.Session.UndoAsync();
            await doc.Session.ExecuteAsync(DocumentEdits.SetAppearance(item.BodyId,new(0xFFFFFF00)));
            await Idle();viewport.SaveScreenshot(Path.Combine(output,"override.png"));
            await doc.Session.UndoAsync();await Idle();CaptureColors(viewport,output,"undo-colors");
            Check(await vm.CloseAllAsync(),"Saved fixture close");await Idle();
            Check(OcctViewportHost.LiveCount==0,"Initial host release");
            int baselineHandles=Process.GetCurrentProcess().HandleCount;
            for(int cycle=0;cycle<12;cycle++)
            {
                await vm.OpenPathAsync(saved);await Idle();doc=vm.ActiveDocument!;
                var host=Host(doc);viewport=host.Viewport!;viewport.FitAll();viewport.MouseWheel(120,0,0,0);
                var camera=viewport.CaptureCamera();item=doc.Scene.Items[0];
                doc.Selection.Replace([new(item.Path,item.BodyId,item.Geometry.Revision)]);
                var layout=window.dockManager.Layout.Descendents().OfType<LayoutDocument>().Single(d=>ReferenceEquals(d.Content,doc));
                layout.FloatingWidth=900;layout.FloatingHeight=600;layout.Float();await Idle();
                Check(layout.IsFloating,"Document floats");host=Host(doc);viewport=host.Viewport!;
                Camera(camera,viewport.CaptureCamera());Check(doc.Selection.Items.Length==1,"Selection after float");
                if(cycle==0)viewport.SaveScreenshot(Path.Combine(output,"floating.png"));
                var floating=System.Windows.Application.Current.Windows.OfType<AvalonDock.Controls.LayoutDocumentFloatingWindowControl>().Single();
                floating.Width+=80;floating.Height+=40;await Idle();
                layout.DockAsDocument();await Idle();Check(!layout.IsFloating,"Document redocks");
                host=Host(doc);viewport=host.Viewport!;Camera(camera,viewport.CaptureCamera());
                Check(doc.Selection.Items.Length==1,"Selection after redock");
                CaptureLoss(host,doc);
                if(cycle==0)SelectionModifiers(viewport,doc);
                if(cycle==0)
                {
                    doc.Selection.Replace([]);await Idle();CaptureColors(viewport,output,"redocked");
                    var dpi=VisualTreeHelper.GetDpi(host);
                    observations.Add(new{environment=new{monitors=GetSystemMetrics(80),remoteSession=GetSystemMetrics(0x1000)!=0,dpiX=dpi.PixelsPerInchX,dpiY=dpi.PixelsPerInchY}});
                }
                Check(await vm.CloseAllAsync(),"Repeated document close");await Idle();
                Check(OcctViewportHost.LiveCount==0,"No HWND hosts after close");
                Check(((MemoryAssetStore)services.GetRequiredService<IAssetStore>()).Count==0,"No assets after close");
                using var process=Process.GetCurrentProcess();process.Refresh();
                observations.Add(new{cycle=cycle+1,hosts=OcctViewportHost.LiveCount,handles=process.HandleCount,privateBytes=process.PrivateMemorySize64});
            }
            await ((App)System.Windows.Application.Current).StopRecoveryAsync();
            listener.Flush();bindingOutput.Flush();
            Check(new FileInfo(Path.Combine(output,"bindings.log")).Length==0,"No WPF binding errors");
            await File.WriteAllTextAsync(Path.Combine(output,"observations.json"),System.Text.Json.JsonSerializer.Serialize(new{baselineHandles,observations},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: three frozen legacy files opened, migrated, saved and reopened with exact IDs/assets and source colors; source/face colors, selection/deselection, explicit override/undo, save/reopen, 12 float/resize/redock/close cycles, camera and selection continuity, three-button capture loss and late release, zero hosts/assets at every close. Environment and resource samples are observations, not mixed-DPI/RDP/long-run acceptance.");
            window.CloseAfterSmoke();
        }
        catch(Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);
            System.Windows.Application.Current.Shutdown(1);
        }
        finally{PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static async Task LegacyStorage(MainWindowViewModel vm,IServiceProvider services,IDocumentStorage storage,string output,string fixtures)
    {
        var observations=new List<object>();
        foreach(string name in new[]{"v1-box.cadoryx","v2-box.cadoryx","v2-colored-assembly.cadoryx"})
        {
            string original=Path.Combine(fixtures,name),saved=Path.Combine(output,"migrated-"+name);
            var originalHash=SHA256.HashData(await File.ReadAllBytesAsync(original));
            await vm.OpenPathAsync(original);await Idle();
            var doc=vm.ActiveDocument??throw new InvalidOperationException("Legacy file did not open: "+name);
            var before=doc.Session.Snapshot;
            Check(!doc.Session.IsDirty,"Legacy migration does not dirty the document");
            var viewport=Host(doc).Viewport!;viewport.FitAll();
            if(name.Contains("colored"))CaptureColors(viewport,output,"legacy-colors");
            else viewport.SaveScreenshot(Path.Combine(output,"legacy-"+name+".png"));
            await doc.Session.SaveAsync(storage,saved);Check(!doc.Session.IsDirty,"Migrated savepoint");
            Check(await vm.CloseAllAsync(),"Legacy document close");await Idle();
            Check(OcctViewportHost.LiveCount==0&&((MemoryAssetStore)services.GetRequiredService<IAssetStore>()).Count==0,"Legacy resources released");
            await vm.OpenPathAsync(saved);await Idle();doc=vm.ActiveDocument??throw new InvalidOperationException("Migrated file did not reopen");
            var after=doc.Session.Snapshot;
            Check(before.Id==after.Id&&before.StateId==after.StateId,"Migrated stable identity");
            Check(before.Bodies.Count==after.Bodies.Count&&before.Bodies.All(b=>after.Bodies.TryGetValue(b.Key,out var body)&&body==b.Value),"Migrated exact bodies and geometry");
            Check(before.ReferencedAssets().OrderBy(a=>a.Sha256).SequenceEqual(after.ReferencedAssets().OrderBy(a=>a.Sha256)),"Migrated exact assets");
            if(name.Contains("colored"))CaptureColors(Host(doc).Viewport!,output,"migrated-colors");
            using(var zip=ZipFile.OpenRead(saved))using(var stream=zip.GetEntry("manifest.json")!.Open())
            {
                var manifest=JsonSerializer.Deserialize<CadManifest>(stream,CadJson.Options)!;
                Check(manifest.AssetCatalogVersion==1&&manifest.Assets.All(a=>a.Format is not null),"Current asset catalog");
                Check(manifest.Sections.Length==CadSectionMigrationRegistry.CurrentFormats.Count&&manifest.Sections.All(s=>new SectionFormat(s.Kind,s.SchemaVersion,s.Encoding)==CadSectionMigrationRegistry.CurrentFormats[s.Kind]),"Current migrated sections");
                observations.Add(new{name,documentId=after.Id,stateId=after.StateId,assetCount=manifest.Assets.Length,sections=manifest.Sections.Select(s=>new{s.Kind,s.SchemaVersion,s.Encoding})});
            }
            Check(await vm.CloseAllAsync(),"Migrated document close");await Idle();
            Check(OcctViewportHost.LiveCount==0&&((MemoryAssetStore)services.GetRequiredService<IAssetStore>()).Count==0,"Migrated resources released");
            var finalHash=SHA256.HashData(await File.ReadAllBytesAsync(original));
            Check(originalHash.SequenceEqual(finalHash),"Frozen fixture unchanged");
        }
        await File.WriteAllTextAsync(Path.Combine(output,"legacy-storage.json"),JsonSerializer.Serialize(observations,new JsonSerializerOptions{WriteIndented=true}));
    }
    private static void CaptureLoss(OcctViewportHost host,CadDocumentViewModel doc)
    {
        var viewport=host.Viewport!;int events=0;viewport.SelectionChanged+=Changed;
        try
        {
            foreach(var (down,up,mask) in new[]{(0x201u,0x202u,1u),(0x207u,0x208u,0x10u),(0x204u,0x205u,2u)})
            {
                var camera=viewport.CaptureCamera();var selection=doc.Selection.Items.ToArray();
                SendMessage(host.Handle,down,mask,Point(20,20));Check(GetCapture()==host.Handle,"Native mouse capture acquired");
                SetCapture(new WindowInteropHelper(Window.GetWindow(host)).Handle); // Real WM_CAPTURECHANGED.
                Check(!viewport.HasPointerCapture,"Capture loss resets gesture");
                SendMessage(host.Handle,0x200,mask,Point(45,45));SendMessage(host.Handle,up,0,Point(45,45));
                Camera(camera,viewport.CaptureCamera());Check(events==0,"Late release must not publish selection");
                Check(doc.Selection.Items.SequenceEqual(selection),"Capture loss preserves selection");ReleaseCapture();
            }
            SendMessage(host.Handle,0x201,1,Point(10,10));SendMessage(host.Handle,0x204,3,Point(10,10));
            SendMessage(host.Handle,0x205,1,Point(10,10));Check(GetCapture()==host.Handle,"Capture retained for remaining left button");
            SendMessage(host.Handle,0x202,0,Point(10,10));Check(GetCapture()!=host.Handle,"Final button releases capture");
        }
        finally{viewport.SelectionChanged-=Changed;ReleaseCapture();}
        void Changed(object? sender,IReadOnlyList<SceneItem> items)=>events++;
    }
    private static nint Point(int x,int y)=>(nint)((y<<16)|(x&65535));
    private static void SelectionModifiers(OcctViewport viewport,CadDocumentViewModel doc)
    {
        var centers=doc.Scene.Items.Select(i=>viewport.WorldToScreen(i.WorldTransform.Apply((i.Geometry.Bounds.Min+i.Geometry.Bounds.Max)*0.5))).ToArray();
        Check(centers.Length==2,"Two source instances for selection");
        Click(0,0);Check(doc.Selection.Items.Length==1,"Replace selection");
        Click(1,2);Check(doc.Selection.Items.Length==2,"Ctrl adds second instance");
        Click(0,2);Check(doc.Selection.Items.Length==1,"Ctrl removes first instance");
        Click(0,1);Check(doc.Selection.Items.Length==2,"Shift adds first instance");
        Click(1,4);Check(doc.Selection.Items.Length==1,"Alt removes second instance");
        void Click(int index,int modifiers)
        {
            var point=centers[index];viewport.PointerPressed(0,point.X,point.Y,modifiers);viewport.PointerReleased(0,point.X,point.Y,modifiers);
        }
    }
    private static void CaptureColors(OcctViewport viewport,string output,string name)
    {
        var camera=viewport.CaptureCamera();
        int red=0,green=0,blue=0;
        try
        {
            foreach(bool opposite in new[]{false,true})
            {
                if(opposite)viewport.RestoreCamera(camera with {Eye=camera.Target+(camera.Target-camera.Eye)});
                viewport.PointerMoved(-1,-1,0,0);viewport.Redraw();string path=Path.Combine(output,name+(opposite?"-opposite":"")+".png");viewport.SaveScreenshot(path);
                var frame=BitmapFrame.Create(new Uri(path),BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
                var image=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);byte[] pixels=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(pixels,image.PixelWidth*4,0);
                for(int i=0;i<pixels.Length;i+=4)
                {
                    int b=pixels[i],g=pixels[i+1],r=pixels[i+2];
                    if(r>60&&r>g*2&&r>b*2)red++;if(g>60&&g>r*2&&g>b*2)green++;if(b>60&&b>r*2&&b>g*2)blue++;
                }
            }
        }
        finally{viewport.RestoreCamera(camera);}
        File.WriteAllText(Path.Combine(output,name+"-pixels.txt"),$"red={red}; green={green}; blue={blue}");
        Check(red>100&&green>100&&blue>100,$"Source face colors visible ({name}): R={red}, G={green}, B={blue}");
    }
    private static void CheckBackground(string path,uint expectedTop,uint expectedBottom)
    {
        var frame=BitmapFrame.Create(new Uri(path),BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
        var image=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);
        byte[] pixels=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(pixels,image.PixelWidth*4,0);
        CheckPixel(5,expectedTop,"top");
        CheckPixel(image.PixelHeight-6,expectedBottom,"bottom");
        void CheckPixel(int y,uint expected,string location)
        {
            int offset=(y*image.PixelWidth+5)*4;
            int blue=pixels[offset],green=pixels[offset+1],red=pixels[offset+2];
            Check(Math.Abs(red-((expected>>16)&255))<=8&&Math.Abs(green-((expected>>8)&255))<=8&&
                Math.Abs(blue-(expected&255))<=8,
                $"Viewport {location} gradient pixel matches #{expected:X8}: {red},{green},{blue}");
        }
    }
    private static void Camera(CadCamera expected,CadCamera actual)
    {
        Check((expected.Eye-actual.Eye).Length<1e-6&&(expected.Target-actual.Target).Length<1e-6&&(expected.Up-actual.Up).Length<1e-6&&Math.Abs(expected.Scale-actual.Scale)<1e-6,"Camera eye/target/up/scale preserved");
    }
    // Floating AvalonDock content is hosted in a separate HwndSource, outside its Window visual tree.
    private static OcctViewportHost Host(CadDocumentViewModel doc)=>PresentationSource.CurrentSources.Cast<PresentationSource>()
        .Where(s=>s.RootVisual is not null).SelectMany(s=>FindHosts(s.RootVisual)).Distinct()
        .Single(h=>ReferenceEquals(h.DataContext,doc)&&h.Viewport is not null);
    private static IEnumerable<OcctViewportHost> FindHosts(DependencyObject root)
    {
        if(root is OcctViewportHost host)yield return host;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in FindHosts(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
    private static MainStatusBarView? FindStatusBar(DependencyObject root)
    {
        if(root is MainStatusBarView bar)return bar;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            if(FindStatusBar(VisualTreeHelper.GetChild(root,i)) is {} found)return found;
        return null;
    }
    private static async Task Idle(){await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(150);}
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    [DllImport("user32.dll")]private static extern nint SendMessage(nint window,uint message,nuint w,nint l);
    [DllImport("user32.dll")]private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")]private static extern nint GetCapture();
    [DllImport("user32.dll")]private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetClientRect(nint window,out ClientRect rect);
    [StructLayout(LayoutKind.Sequential)]private struct ClientRect{public int Left,Top,Right,Bottom;}
}
