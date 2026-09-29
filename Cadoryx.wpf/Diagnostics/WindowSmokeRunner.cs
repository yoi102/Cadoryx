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
using ViewerManipulatorMode = OcctSharp.ViewerManipulatorMode;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Settings;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Views;
using Cadoryx.wpf.Views.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static class WindowSmokeRunner
{
    internal static async Task RunRadialAsync(MainWindow window,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindingOutput=new StreamWriter(Path.Combine(output,"bindings.log"));
        using var listener=new TextWriterTraceListener(bindingOutput);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        int exitCode=1;
        try
        {
            window.Activate();await Idle();
            var vm=(MainWindowViewModel)window.DataContext;
            await RadialMenuGesture(vm,vm.ActiveDocument!,output);
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),
                "PASS: radial queued wheel rendering without pointer movement, page accumulation, reversal, capture and IME gestures.");
            exitCode=0;
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),ex.ToString());}
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            listener.Flush();
            System.Windows.Application.Current.Shutdown(exitCode);
        }
    }

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
            await RadialMenuGesture(vm, drawingDoc,output);
            Check(drawingDoc.BackgroundTopArgb==DocumentSettings.DefaultBackgroundTopArgb&&
                drawingDoc.BackgroundBottomArgb==DocumentSettings.DefaultBackgroundBottomArgb,"New document uses sky gradient");
            drawingViewport.FitAll();
            // This probe needs a visible cube even when the user's saved app preference hides it.
            drawingViewport.SetViewCubeVisible(true);
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
            Camera(initialCubeCamera,drawingViewport.CaptureCamera());
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-cube.png"));
            drawingViewport.PointerPressed(0,cubeX,cubeY,0);
            drawingViewport.PointerReleased(0,cubeX,cubeY,0);
            Camera(initialCubeCamera,drawingViewport.CaptureCamera());
            await WaitForCamera(drawingViewport,cubeTarget!);
            drawingViewport.SetProjection(CadProjection.Front);drawingViewport.Redraw();
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-cube-face-arrows.png"));
            drawingViewport.PointerMoved(cubeBounds.Right-82,82,0,0);
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-cube-active.png"));
            SendMessage(cubeHost.Handle,0x2A3,0,0); // WM_MOUSELEAVE from the native host.
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-cube-idle.png"));
            Check(!SHA256.HashData(File.ReadAllBytes(Path.Combine(output,"view-cube-active.png"))).SequenceEqual(
                SHA256.HashData(File.ReadAllBytes(Path.Combine(output,"view-cube-idle.png")))),
                "ViewCube becomes translucent after the pointer leaves");
            int originalCulture=vm.CurrentCultureLCID;
            vm.ChangeCultureCommand.Execute("1033");
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-cube-en.png"));
            vm.ChangeCultureCommand.Execute("2052");
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-cube-zh.png"));
            Check(!SHA256.HashData(File.ReadAllBytes(Path.Combine(output,"view-cube-en.png"))).SequenceEqual(
                SHA256.HashData(File.ReadAllBytes(Path.Combine(output,"view-cube-zh.png")))),
                "ViewCube labels update when the application language changes");
            vm.ChangeCultureCommand.Execute("1041");
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-cube-ja.png"));
            vm.ChangeCultureCommand.Execute(originalCulture.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
            Camera(beforeTurn,drawingViewport.CaptureCamera());
            drawingViewport.PointerPressed(0,left.X,left.Y,0);drawingViewport.PointerReleased(0,left.X,left.Y,0);
            Camera(beforeTurn,drawingViewport.CaptureCamera());
            await WaitForCamera(drawingViewport,leftTarget);
            var cubeRightTarget=drawingViewport.CaptureCubeTurnTarget(OcctSharp.ViewerCubeTurn.Right,45);
            drawingViewport.PointerPressed(0,right.X,right.Y,0);drawingViewport.PointerReleased(0,right.X,right.Y,0);
            await WaitForCamera(drawingViewport,cubeRightTarget);
            await WaitForCamera(drawingViewport,beforeTurn);
            vm.ApplicationSettings.Viewport.ViewCubeRotationDegrees=30;
            var customTarget=drawingViewport.CaptureCubeTurnTarget(OcctSharp.ViewerCubeTurn.Right,30);
            drawingViewport.PointerPressed(0,right.X,right.Y,0);drawingViewport.PointerReleased(0,right.X,right.Y,0);
            await WaitForCamera(drawingViewport,customTarget);
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
            await WaitForCamera(drawingViewport,frontTarget);
            drawingViewport.SaveScreenshot(Path.Combine(output,"view-front-complete.png"));
            drawingDoc.SetView(CadProjection.Top);await Task.Delay(90);
            var interrupted=drawingViewport.CaptureCamera();
            var rightTarget=drawingViewport.CaptureProjectionTarget(CadProjection.Right);
            drawingDoc.SetView(CadProjection.Right);
            Camera(interrupted,drawingViewport.CaptureCamera());
            await WaitForCamera(drawingViewport,rightTarget);
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
            settingsEditor.SelectedWorkPlane=settingsEditor.WorkPlaneOptions.Single(o=>o.Kind==DocumentWorkPlaneKind.Custom);
            settingsEditor.WorkPlaneOffsetMm=2;settingsEditor.WorkPlaneOriginX=3;
            settingsEditor.WorkPlaneOriginY=4;settingsEditor.WorkPlaneOriginZ=5;
            settingsEditor.WorkPlaneAngleX=15;settingsEditor.WorkPlaneAngleY=20;settingsEditor.WorkPlaneAngleZ=30;
            Check(await settingsEditor.TryApplyAsync(),"Custom work plane applied in document settings");
            var freePlane=drawingDoc.WorkPlaneSettings;
            Check(freePlane.Kind==DocumentWorkPlaneKind.Custom&&freePlane.CustomOrigin==new Vector3d(3,4,5)&&
                freePlane.CustomRotation==Quaterniond.FromEulerDegrees(15,20,30),"Document owns the free plane transform");
            var freeTarget=freePlane.ToWorld(6,8);var freePixel=drawingViewport.WorldToScreen(freeTarget);
            Check(drawingViewport.TryWorkplanePoint(freePixel.X,freePixel.Y,5,false,freePlane,out var freeHit)&&
                (freeHit-freeTarget).Length<2,"Native camera ray intersects the free plane");
            drawingViewport.SaveScreenshot(Path.Combine(output,"work-grid-free-plane.png"));
            settingsEditor.ResetToDefaults();Check(await settingsEditor.TryApplyAsync(),"Document settings reset to defaults");
            settingsWindow.Close();await Idle();
            int beforeBoxBodies=drawingDoc.Session.Snapshot.Bodies.Count;
            drawingDoc.StartTool("Box");Check(drawingDoc.IsViewportConstructing,"Box viewport construction begins");
            var start=drawingViewport.WorldToScreen(new(0,0,0));var end=drawingViewport.WorldToScreen(new(6,8,0));
            Check(drawingViewport.TryWorkplanePoint(start.X,start.Y,drawingDoc.GridSpacingMm,false,out _),"Native XY workplane ray intersection");
            Click(start);drawingViewport.PointerMoved(end.X,end.Y,0,0);Click(end);
            drawingViewport.PointerMoved(end.X,end.Y-60,0,0);Click((end.X,end.Y-60));
            await Idle();if(drawingDoc.ConfirmCommand.ExecutionTask is {} constructionCommit)await constructionCommit;
            Check(!drawingDoc.HasPreview&&drawingDoc.Session.Snapshot.Bodies.Count==beforeBoxBodies+1,
                $"Mouse-defined box commits on its final click: {drawingDoc.ToolStatus}");
            drawingViewport.SaveScreenshot(Path.Combine(output,"mouse-box-committed.png"));
            drawingDoc.CancelCommand.Execute(null);
            Check(drawingDoc.Session.Snapshot.Bodies.Count==beforeBoxBodies+1,
                "Cancel after primitive commit leaves the modeled box intact");
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
            await SolidHandleSmoke(drawingDoc,drawingViewport,output);
            await OccurrenceHandleSmoke(drawingDoc,drawingViewport,output);
            var storage=services.GetRequiredService<IDocumentStorage>();
            foreach(var initial in vm.Documents)await initial.Session.SaveAsync(storage,Path.Combine(output,"initial.cadoryx"));
            Check(await vm.CloseAllAsync(),"Initial documents close");await Idle();
            await LegacyStorage(vm,services,storage,output,Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fixtures))!,"Storage"));
            string saved=Path.Combine(output,"colors.cadoryx");
            await vm.OpenPathAsync(Path.Combine(fixtures,"rotated-colors.step"));await Idle();
            var doc=vm.ActiveDocument??throw new InvalidOperationException("Fixture did not open");
            await doc.Session.SaveAsync(storage,saved);
            var viewport=Host(doc).Viewport!;viewport.FitAll();
            Check(viewport.NativeXdeContextCount==doc.Scene.Items.Where(i=>i.Geometry.Source is not null)
                .Select(i=>(i.Geometry.Source!.ContextAssetId,i.Geometry.Source.Format)).Distinct().Count(),"One XDE context per distinct source asset in this viewport");
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
                if(cycle==0)
                {
                    NavigationGestures(viewport);
                    await ContextMenuGesture(host);
                    SelectionModifiers(viewport,doc);
                }
                if(cycle==0)
                {
                    doc.Selection.Replace([]);await Idle();CaptureColors(viewport,output,"redocked");
                    var dpi=VisualTreeHelper.GetDpi(host);
                    observations.Add(new{environment=new{monitors=GetSystemMetrics(80),remoteSession=GetSystemMetrics(0x1000)!=0,dpiX=dpi.PixelsPerInchX,dpiY=dpi.PixelsPerInchY}});
                }
                Check(await vm.CloseAllAsync(),"Repeated document close");await Idle();
                if(cycle==0)await BlankCylinderGesture(vm,output);
                Check(OcctViewportHost.LiveCount==0,"No HWND hosts after close");
                Check(((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"No assets after close");
                using var process=Process.GetCurrentProcess();process.Refresh();
                observations.Add(new{cycle=cycle+1,hosts=OcctViewportHost.LiveCount,handles=process.HandleCount,privateBytes=process.PrivateMemorySize64});
            }
            await ((App)System.Windows.Application.Current).StopRecoveryAsync();
            listener.Flush();bindingOutput.Flush();
            Check(new FileInfo(Path.Combine(output,"bindings.log")).Length==0,"No WPF binding errors");
            await File.WriteAllTextAsync(Path.Combine(output,"observations.json"),System.Text.Json.JsonSerializer.Serialize(new{baselineHandles,observations},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: three frozen legacy files opened, migrated, saved and reopened with exact IDs/assets and source colors; source/face colors, selection/deselection, explicit override/undo, save/reopen, 12 float/resize/redock/close cycles, middle-drag pan, right-drag rotation, right-click menu, backtick radial menu with four wheel pages, blank-document cylinder gesture/commit, camera and selection continuity, three-button capture loss and late release, zero hosts/assets at every close. Environment and resource samples are observations, not mixed-DPI/RDP/long-run acceptance.");
            window.CloseAfterSmoke();
        }
        catch(Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);
            System.Windows.Application.Current.Shutdown(1);
        }
        finally{PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static async Task SolidHandleSmoke(CadDocumentViewModel doc,OcctViewport viewport,string output)
    {
        await doc.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(30,20,15,
            RigidTransform3d.Translate(200,0,0)),"M8 handle box"));
        var feature=doc.Session.Snapshot.Features.Values.Single(f=>f.Name=="M8 handle box");
        var item=doc.Scene.Items.Single(i=>i.BodyId==feature.OutputBodyId);
        doc.EditFeature(feature.Id);
        viewport.RestoreCamera(SceneEnvelope.FitVisible(viewport.CaptureCamera(),[item]));
        viewport.SetSolidHandles(doc.SolidHandleRecipe(),item.WorldTransform);
        viewport.Redraw();await Idle();
        var solidRadius=viewport.SolidHandleScreenRadii.ToArray();
        var handleCamera=viewport.CaptureCamera();
        viewport.MouseWheel(480,100,100,0);
        Check(viewport.SolidHandleScreenRadii.Count==solidRadius.Length&&
            viewport.SolidHandleScreenRadii.Zip(solidRadius).All(pair=>Math.Abs(pair.First-pair.Second)<2),
            "Solid handle balls retain their pixel radius after zoom");
        viewport.RestoreCamera(handleCamera);
        viewport.SaveScreenshot(Path.Combine(output,"solid-dimension-handles.png"));
        var handles=SolidDimensionHandles.Describe(feature.Recipe,item.WorldTransform);
        Check(GetClientRect(Host(doc).Handle,out var bounds),"M8 viewport bounds");
        var candidates=handles.Select(h=>(Handle:h,Pixel:viewport.WorldToScreen(h.WorldPoint),
                Axis:viewport.WorldToScreen(h.WorldPoint+h.WorldAxis))).ToArray();
        var chosen=candidates.Where(h=>h.Pixel.X>20&&h.Pixel.X<bounds.Right-20&&h.Pixel.Y>20&&h.Pixel.Y<bounds.Bottom-20&&
                Math.Pow(h.Axis.X-h.Pixel.X,2)+Math.Pow(h.Axis.Y-h.Pixel.Y,2)>16).FirstOrDefault();
        Check(chosen.Handle is not null,"M8 visible solid handle: "+string.Join("; ",candidates.Select(h=>$"{h.Handle.Dimension} {h.Pixel} {h.Axis}")));
        var start=chosen.Pixel;int endX=start.X+(chosen.Axis.X-start.X)*24;
        int endY=start.Y+(chosen.Axis.Y-start.Y)*24;
        double before=feature.Result.VolumeMm3;
        viewport.PointerPressed(0,start.X,start.Y,0);
        viewport.PointerMoved(endX,endY,1,0);
        Check(doc.SizeX>30||doc.SizeY>20||doc.SizeZ>15,"M8 handle drag updates candidate");
        Check(doc.Session.Snapshot.Features[feature.Id].Result.VolumeMm3==before,"M8 drag does not commit document");
        viewport.CancelInput();await Idle();
        Check(doc.Session.Snapshot.Features[feature.Id].Result.VolumeMm3==before,"M8 canceled drag preserves document");
        viewport.PointerPressed(0,start.X,start.Y,0);
        viewport.PointerMoved(endX,endY,1,0);
        viewport.PointerReleased(0,endX,endY,0);
        if(doc.PreviewCommand.ExecutionTask is {} preview)await preview;
        Check(doc.HasPreview,"M8 mouse release prepares candidate");
        await doc.ConfirmCommand.ExecuteAsync(null);
        Check(doc.Session.Snapshot.Features[feature.Id].Result.VolumeMm3>before,"M8 confirm commits dimensional edit");
        await doc.Session.UndoAsync();
        Check(doc.Session.Snapshot.Features[feature.Id].Result.VolumeMm3==before,"M8 handle edit undoes exactly");
        doc.StartTool("Box");doc.CancelViewportConstruction();
    }
    private static async Task OccurrenceHandleSmoke(CadDocumentViewModel doc,OcctViewport viewport,string output)
    {
        viewport.SetBoxSelection(null,null);viewport.ClearExactSelectionSource();viewport.SetAssemblyDatumSelection(null);
        var item=doc.Review.Filter(doc.Scene).Items.Last();
        doc.Selection.SelectOccurrence(item.Path);await Idle();
        viewport.RestoreCamera(SceneEnvelope.FitVisible(viewport.CaptureCamera(),[item]));
        viewport.SetOccurrenceHandles(item.Path,item.BodyId);viewport.Redraw();
        Check(viewport.OccurrenceGizmoState is {ZoomPersistence:true},
            "Occurrence gizmo retains fixed screen size");
        var occurrenceCamera=viewport.CaptureCamera();
        viewport.MouseWheel(480,100,100,0);
        Check(viewport.OccurrenceGizmoState is {ZoomPersistence:true},
            "Occurrence gizmo preserves screen persistence after zoom");
        viewport.SaveScreenshot(Path.Combine(output,"occurrence-gizmo-zoomed.png"));
        viewport.RestoreCamera(occurrenceCamera);
        viewport.SaveScreenshot(Path.Combine(output,"m12-occurrence-handles.png"));
        var origin=viewport.OccurrenceGizmoState!.Position.Origin;
        var center=viewport.WorldToScreen(new(origin.X,origin.Y,origin.Z));
        Check(GetClientRect(Host(doc).Handle,out var bounds),"M12 viewport bounds");
        (int X,int Y) FindTranslation()
        {
            for(int radius=12;radius<=85;radius+=5)
                for(int angle=0;angle<360;angle+=12)
                {
                    int x=center.X+(int)Math.Round(radius*Math.Cos(angle*Math.PI/180));
                    int y=center.Y+(int)Math.Round(radius*Math.Sin(angle*Math.PI/180));
                    if(x<10||x>=bounds.Right-10||y<10||y>=bounds.Bottom-10)continue;
                    if(viewport.HitOccurrenceGizmo(x,y)==ViewerManipulatorMode.Translation)return(x,y);
                }
            throw new InvalidOperationException("No visible translation arrow hit in the gizmo.");
        }
        var start=FindTranslation();
        double radial=Math.Sqrt(Math.Pow(start.X-center.X,2)+Math.Pow(start.Y-center.Y,2));
        int endX=start.X+(int)Math.Round((start.X-center.X)*55/radial);
        int endY=start.Y+(int)Math.Round((start.Y-center.Y)*55/radial);
        var before=OccurrencePlacement.Resolve(doc.Session.Snapshot,item.Path).Slot.LocalTransform;
        int finished=0;
        void OnFinished(object? _,(OccurrencePath Path,BodyId? FocusBody,ViewerManipulatorMode Mode,double[] WorldMatrix,bool Commit) drag)
        {if(drag.Commit&&drag.Mode==ViewerManipulatorMode.Translation)finished++;}
        viewport.OccurrenceGizmoFinished+=OnFinished;
        viewport.PointerPressed(0,start.X,start.Y,0);
        viewport.PointerMoved(endX,endY,1,0);
        viewport.SaveScreenshot(Path.Combine(output,"occurrence-handles-drag.png"));
        Check(OccurrencePlacement.Resolve(doc.Session.Snapshot,item.Path).Slot.LocalTransform==before,
            "M12 drag previews without writing document");
        viewport.CancelInput();
        Check(OccurrencePlacement.Resolve(doc.Session.Snapshot,item.Path).Slot.LocalTransform==before,
            "M12 capture cancel preserves placement");
        viewport.PointerPressed(0,start.X,start.Y,0);
        viewport.PointerMoved(endX,endY,1,0);
        viewport.PointerReleased(0,endX,endY,0);
        for(int attempt=0;attempt<30&&OccurrencePlacement.Resolve(doc.Session.Snapshot,item.Path).Slot.LocalTransform==before;attempt++)await Idle();
        Check(OccurrencePlacement.Resolve(doc.Session.Snapshot,item.Path).Slot.LocalTransform!=before,
            $"M12 mouse release commits placement (finished {finished}, status {doc.ToolStatus})");
        viewport.OccurrenceGizmoFinished-=OnFinished;
        await doc.Session.UndoAsync();
        Check(OccurrencePlacement.Resolve(doc.Session.Snapshot,item.Path).Slot.LocalTransform==before,
            "M12 placement undo is exact");
        (int X,int Y) FindPart(ViewerManipulatorMode mode)
        {
            var position=viewport.OccurrenceGizmoState!.Position.Origin;
            var pixel=viewport.WorldToScreen(new(position.X,position.Y,position.Z));
            for(int radius=10;radius<=105;radius+=3)
                for(int angle=0;angle<360;angle+=8)
                {
                    int x=pixel.X+(int)Math.Round(radius*Math.Cos(angle*Math.PI/180));
                    int y=pixel.Y+(int)Math.Round(radius*Math.Sin(angle*Math.PI/180));
                    if(x<10||x>=bounds.Right-10||y<10||y>=bounds.Bottom-10)continue;
                    if(viewport.HitOccurrenceGizmo(x,y)==mode)return(x,y);
                }
            throw new InvalidOperationException($"No visible {mode} part hit in the gizmo.");
        }
        viewport.SetOccurrenceGizmoMode(OccurrenceGizmoMode.Rotate);
        viewport.SaveScreenshot(Path.Combine(output,"occurrence-gizmo-rotate.png"));
        var rotateStart=FindPart(ViewerManipulatorMode.Rotation);
        var rotateCenter=viewport.OccurrenceGizmoState!.Position.Origin;
        var rotatePixel=viewport.WorldToScreen(new(rotateCenter.X,rotateCenter.Y,rotateCenter.Z));
        int dx=rotateStart.X-rotatePixel.X,dy=rotateStart.Y-rotatePixel.Y;
        int rotateEndX=rotateStart.X-(int)Math.Round(dy*0.8);
        int rotateEndY=rotateStart.Y+(int)Math.Round(dx*0.8);
        viewport.PointerPressed(0,rotateStart.X,rotateStart.Y,0);
        viewport.PointerMoved(rotateEndX,rotateEndY,1,0);
        Check(OccurrencePlacement.Resolve(doc.Session.Snapshot,item.Path).Slot.LocalTransform==before,
            "Rotation preview leaves the document untouched");
        viewport.PointerReleased(0,rotateEndX,rotateEndY,0);
        for(int attempt=0;attempt<30&&OccurrencePlacement.Resolve(doc.Session.Snapshot,item.Path).Slot.LocalTransform==before;attempt++)await Idle();
        var rotated=OccurrencePlacement.Resolve(doc.Session.Snapshot,item.Path).Slot.LocalTransform;
        Check(rotated!=before&&rotated.Rotation!=before.Rotation,
            "Rotation ring commits an occurrence rotation");
        var storage=new CadDocumentStorage();
        var rotatedFile=Path.Combine(output,"occurrence-rotated.cadoryx");
        await doc.Session.SaveAsync(storage,rotatedFile);
        using(var loaded=await storage.LoadAsync(rotatedFile,doc.Session.Assets))
            Check(OccurrencePlacement.Resolve(loaded.Snapshot,item.Path).Slot.LocalTransform==rotated,
                "Rotation survives save and reopen");
        await doc.Session.UndoAsync();
        Check(OccurrencePlacement.Resolve(doc.Session.Snapshot,item.Path).Slot.LocalTransform==before,
            "Rotation undo restores the original placement");

        doc.Selection.Replace([new SelectionTarget(item.Path,item.BodyId,item.Geometry.Revision)]);
        await Idle();
        var body=doc.Session.Snapshot.Bodies[item.BodyId];
        var featureId=body.Producer??throw new InvalidOperationException("Selected body has no feature.");
        var recipeBefore=doc.Session.Snapshot.Features[featureId].Recipe;
        viewport.SetOccurrenceHandles(item.Path,item.BodyId,true);
        Check(viewport.CanScaleOccurrence,"Unique primitive enables the scale mode");
        viewport.SetOccurrenceGizmoMode(OccurrenceGizmoMode.Scale);
        viewport.SaveScreenshot(Path.Combine(output,"occurrence-gizmo-scale.png"));
        var scaleStart=FindPart(ViewerManipulatorMode.Scaling);
        var scaleCenter=viewport.OccurrenceGizmoState!.Position.Origin;
        var scalePixel=viewport.WorldToScreen(new(scaleCenter.X,scaleCenter.Y,scaleCenter.Z));
        dx=scaleStart.X-scalePixel.X;dy=scaleStart.Y-scalePixel.Y;
        double length=Math.Sqrt(dx*dx+dy*dy);
        int scaleEndX=scaleStart.X+(int)Math.Round(dx*50/length);
        int scaleEndY=scaleStart.Y+(int)Math.Round(dy*50/length);
        viewport.PointerPressed(0,scaleStart.X,scaleStart.Y,0);
        viewport.PointerMoved(scaleEndX,scaleEndY,1,0);
        Check(doc.Session.Snapshot.Features[featureId].Recipe==recipeBefore,
            "Scale preview leaves primitive parameters untouched");
        viewport.CancelInput();
        Check(doc.Session.Snapshot.Features[featureId].Recipe==recipeBefore,
            "Scale capture cancel keeps original parameters");
        (ViewerManipulatorMode Mode,double[] WorldMatrix,bool Commit)? scaleFinish=null;
        void OnScaleFinished(object? _,(OccurrencePath Path,BodyId? FocusBody,ViewerManipulatorMode Mode,double[] WorldMatrix,bool Commit) value)
            =>scaleFinish=(value.Mode,value.WorldMatrix,value.Commit);
        viewport.OccurrenceGizmoFinished+=OnScaleFinished;
        viewport.PointerPressed(0,scaleStart.X,scaleStart.Y,0);
        viewport.PointerMoved(scaleEndX,scaleEndY,1,0);
        viewport.PointerReleased(0,scaleEndX,scaleEndY,0);
        for(int attempt=0;attempt<30&&doc.Session.Snapshot.Features[featureId].Recipe==recipeBefore;attempt++)await Idle();
        viewport.OccurrenceGizmoFinished-=OnScaleFinished;
        Check(doc.Session.Snapshot.Features[featureId].Recipe!=recipeBefore,
            $"Scale control commits primitive dimensions (finish {scaleFinish?.Mode}/{scaleFinish?.Commit}, " +
            $"matrix {string.Join(',',scaleFinish?.WorldMatrix??[])}, status {doc.ToolStatus})");
        var scaledFile=Path.Combine(output,"occurrence-scaled.cadoryx");
        await doc.Session.SaveAsync(storage,scaledFile);
        using(var loaded=await storage.LoadAsync(scaledFile,doc.Session.Assets))
            Check(loaded.Snapshot.Features[featureId].Recipe==doc.Session.Snapshot.Features[featureId].Recipe,
                "Scale survives save and reopen");
        await doc.Session.UndoAsync();
        Check(doc.Session.Snapshot.Features[featureId].Recipe==recipeBefore,
            "Scale undo restores the original primitive");
        viewport.SetOccurrenceGizmoMode(OccurrenceGizmoMode.Move);
        doc.Selection.SelectOccurrence(null);
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
            Check(OcctViewportHost.LiveCount==0&&((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Legacy resources released");
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
            Check(OcctViewportHost.LiveCount==0&&((IAssetStoreStatistics)services.GetRequiredService<IAssetStore>()).Count==0,"Migrated resources released");
            var finalHash=SHA256.HashData(await File.ReadAllBytesAsync(original));
            Check(originalHash.SequenceEqual(finalHash),"Frozen fixture unchanged");
        }
        await File.WriteAllTextAsync(Path.Combine(output,"legacy-storage.json"),JsonSerializer.Serialize(observations,new JsonSerializerOptions{WriteIndented=true}));
    }
    private static async Task RadialMenuGesture(MainWindowViewModel vm,CadDocumentViewModel doc,string output)
    {
        var host=Host(doc);var viewport=host.Viewport!;
        Check(GetClientRect(host.Handle,out var bounds)&&bounds.Right>300&&bounds.Bottom>300,"Radial viewport bounds");
        ViewportPane? pane=null;
        for(DependencyObject? current=host;current is not null;current=VisualTreeHelper.GetParent(current))
            if(current is ViewportPane found){pane=found;break;}
        Check(pane is not null,"Radial viewport pane exists");
        doc.Review.ActivePane=pane!.IsSecondary?1:0;
        var previous=viewport.CaptureCamera();
        var radial=vm.ApplicationSettings.RadialMenu;
        var oldActions=Enum.GetValues<CadoryxRadialPage>().ToDictionary(page=>page,page=>radial.Get(page)[0]);
        var oldEnabled=host.RadialMenuEnabled;
        const nint physicalKeyData=0x00290001; // Scan code 0x29: key below Escape.
        Check(OcctViewportHost.IsRadialTrigger(0x19,physicalKeyData)&&
            OcctViewportHost.IsRadialTrigger(0xC0,physicalKeyData)&&
            !OcctViewportHost.IsRadialTrigger(0xC0,0x001E0001),
            "Radial trigger follows the physical key across IME layouts");
        GetCursorPos(out var originalCursor);
        int radialCancelled=0;
        host.RadialCancelled+=OnRadialCancelled;
        try
        {
            host.RadialMenuEnabled=true;
            foreach(var (page,key,keyMessage,action,projection) in new[]
            {
                (CadoryxRadialPage.Middle,0,0u,CadoryxRadialAction.Top,CadProjection.Top),
                (CadoryxRadialPage.Shift,0x10,0x100u,CadoryxRadialAction.Front,CadProjection.Front),
                (CadoryxRadialPage.Control,0x11,0x100u,CadoryxRadialAction.Right,CadProjection.Right),
                (CadoryxRadialPage.Alt,0x12,0x104u,CadoryxRadialAction.Axonometric,CadProjection.Axonometric)
            })
            {
                var target=viewport.CaptureProjectionTarget(projection);
                switch(page)
                {
                    case CadoryxRadialPage.Middle:radial.Middle[0]=action;break;
                    case CadoryxRadialPage.Shift:radial.Shift[0]=action;break;
                    case CadoryxRadialPage.Control:radial.Control[0]=action;break;
                    case CadoryxRadialPage.Alt:radial.Alt[0]=action;break;
                }
                SendMessage(host.Handle,0x100,0xC0,0);
                Check(host.IsRadialActive&&GetCapture()==host.Handle,"Backtick opens the radial menu and captures pointer");
                await Idle();
                GetCursorPos(out var cursor);
                var radialCenter=pane.RadialCenterScreen;
                // A transparent Popup can settle a few physical pixels after its first layout.
                Check(Math.Abs(cursor.X-radialCenter.X)<=8&&Math.Abs(cursor.Y-radialCenter.Y)<=8,
                    $"Opening radial menu moves cursor to its center (cursor {cursor.X},{cursor.Y}; center {radialCenter.X},{radialCenter.Y})");
                if(key!=0)SendMessage(host.Handle,keyMessage,(nuint)key,0);
                Check(pane.RadialPageNumber==(int)page+1,$"Radial page {(int)page+1} is displayed");
                var top=RadialPoint(0,-95);
                SendMessage(host.Handle,0x200,0,top);
                Camera(previous,viewport.CaptureCamera());
                SendMessage(host.Handle,0x101,0xC0,0);
                Check(!host.IsRadialActive&&GetCapture()!=host.Handle,$"Radial {page} closes on key release");
                await WaitForCamera(viewport,target);
                viewport.RestoreCamera(previous);
            }
            radial.Middle[0]=CadoryxRadialAction.Top;
            radial.Alt[0]=CadoryxRadialAction.Front;
            var middleTarget=viewport.CaptureProjectionTarget(CadProjection.Top);
            SendMessage(host.Handle,0x100,0xC0,0);
            await Idle();
            SendMessage(host.Handle,0x104,0x12,0);
            Check(pane.RadialPageNumber==4,
                $"Alt temporarily selects page 4 (page {pane.RadialPageNumber}, active {host.IsRadialActive}, popup {pane.IsRadialPopupOpen}, capture {GetCapture()==host.Handle})");
            SendMessage(host.Handle,0x105,0x12,0);
            Check(pane.RadialPageNumber==1,"Alt release returns to page 1");
            SendMessage(host.Handle,0x1F,0,0); // Alt can briefly cause WM_CANCELMODE.
            SendMessage(host.Handle,0x215,0,0); // And a transient native capture change.
            SendMessage(new WindowInteropHelper(Window.GetWindow(host)).Handle,0x112,0xF100,0); // SC_KEYMENU
            Check(host.IsRadialActive&&GetCapture()==host.Handle&&radialCancelled==0,
                "Releasing Alt keeps the radial menu and native capture");
            SendMessage(host.Handle,0x200,0,RadialPoint(0,-95));
            Check(pane.IsRadialPopupOpen&&pane.RadialSelectedAction==CadoryxRadialAction.Top,
                $"Alt transition restores page 1 action (popup {pane.IsRadialPopupOpen}, action {pane.RadialSelectedAction})");
            SendMessage(host.Handle,0x101,0xC0,0);
            Check(pane.LastRadialCompletedAction==CadoryxRadialAction.Top&&ReferenceEquals(vm.ActiveDocument,doc),
                $"Alt transition completes Top on active document (action {pane.LastRadialCompletedAction}, active {ReferenceEquals(vm.ActiveDocument,doc)})");
            try{await WaitForCamera(viewport,middleTarget);}
            catch(Exception ex){throw new InvalidOperationException(
                $"Alt transition camera: executed {pane.LastRadialExecutedAction}, active pane {doc.Review.ActivePane}, " +
                $"expected {middleTarget.Eye}/{middleTarget.Target}/{middleTarget.Up}, " +
                $"actual {viewport.CaptureCamera().Eye}/{viewport.CaptureCamera().Target}/{viewport.CaptureCamera().Up}",ex);}
            viewport.RestoreCamera(previous);

            await RadialStationaryWheel(pane,host,output);

            SendMessage(host.Handle,0x100,0xC0,0);
            await Idle();
            var popupHandle=pane.RadialPopupHandle;
            Check(popupHandle!=0&&popupHandle!=host.Handle,"Radial popup owns a separate HWND for hovered wheel input");
            SendMessage(popupHandle,0x20A,Wheel(-30),0);
            Check(pane.RadialPageNumber==2,"A short high-resolution wheel event changes the page immediately");
            for(int i=0;i<3;i++)SendMessage(popupHandle,0x20A,Wheel(-30),0);
            Check(pane.RadialPageNumber==2,"The rest of one wheel notch does not change the page again");
            SendMessage(host.Handle,0x200,0,RadialPoint(0,-95));
            Check(pane.RadialPageNumber==2,"Pointer movement preserves the wheel-selected page");
            SendMessage(popupHandle,0x20A,Wheel(-120),0);
            Check(pane.RadialPageNumber==3,"The next complete wheel notch changes one page");
            SendMessage(popupHandle,0x20A,Wheel(30),0);
            Check(pane.RadialPageNumber==2,"Reversing a high-resolution wheel gesture changes one page");
            SendMessage(host.Handle,0x200,0,RadialPoint(0,-95));
            Check(pane.RadialPageNumber==2,"Pointer movement cannot replay a wheel page change");
            SendMessage(popupHandle,0x20A,Wheel(120),0);
            SendMessage(popupHandle,0x20A,Wheel(120),0);
            Check(pane.RadialPageNumber==1,"Wheel input returns to page 1 before the four-page cycle");
            for(int expected=2;expected<=4;expected++)
            {
                SendMessage(popupHandle,0x20A,Wheel(-120),0);
                Check(pane.RadialPageNumber==expected,$"Wheel over the popup selects page {expected}");
                Camera(previous,viewport.CaptureCamera());
            }
            CaptureRadial(pane.RadialVisual,Path.Combine(output,"radial-page-4.png"));
            SendMessage(popupHandle,0x20A,Wheel(-120),0);
            Check(pane.RadialPageNumber==1,"Wheel over the popup wraps page 4 to page 1");
            var ownerHandle=new WindowInteropHelper(Window.GetWindow(host)).Handle;
            SendMessage(ownerHandle,0x20A,Wheel(-120),0);
            Check(pane.RadialPageNumber==2,"Wheel routed to the WPF owner selects page 2");
            SendMessage(host.Handle,0x20A,Wheel(120),0);
            Check(pane.RadialPageNumber==1,"Wheel over the viewport selects page 1");
            SendMessage(host.Handle,0x20A,Wheel(120),0);
            Check(pane.RadialPageNumber==4,"Wheel over the viewport wraps page 1 to page 4");
            SendMessage(host.Handle,0x200,0,RadialPoint(0,-95));
            var frontTarget=viewport.CaptureProjectionTarget(CadProjection.Front);
            SendMessage(host.Handle,0x101,0xC0,0);
            await WaitForCamera(viewport,frontTarget);
            viewport.RestoreCamera(previous);

            SendMessage(host.Handle,0x100,0xC0,0);
            await Idle();
            SendMessage(host.Handle,0x101,0xC0,0);
            await Idle();
            Camera(previous,viewport.CaptureCamera());
            SendMessage(host.Handle,0x100,0xC0,0);
            SendMessage(host.Handle,0x100,27,0);
            SendMessage(host.Handle,0x101,0xC0,0);
            Check(!host.IsRadialActive,"Escape cancels the key-held menu");
            Camera(previous,viewport.CaptureCamera());

            SendMessage(new WindowInteropHelper(Window.GetWindow(host)).Handle,0x100,0xC0,0);
            Check(host.IsRadialActive,"The WPF window also routes the backtick key to the active viewport");
            SendMessage(host.Handle,0x101,0xC0,0);
            SendMessage(host.Handle,0x100,0x19,physicalKeyData);
            Check(host.IsRadialActive,"Japanese IME virtual key opens the physical-key radial menu");
            SendMessage(host.Handle,0x101,0x19,physicalKeyData);
            Check(!host.IsRadialActive,"Japanese IME virtual key release closes the radial menu");
            SendMessage(new WindowInteropHelper(Window.GetWindow(host)).Handle,0x290,0x19,physicalKeyData);
            Check(host.IsRadialActive,"WPF window routes the physical key from IME key messages");
            SendMessage(new WindowInteropHelper(Window.GetWindow(host)).Handle,0x291,0x19,physicalKeyData);
            Check(!host.IsRadialActive,"IME key release closes the radial menu");
            SendMessage(host.Handle,0x100,0x19,physicalKeyData);
            SendMessage(host.Handle,0x100,27,0);
            Check(!host.IsRadialActive,"Escape cancels an IME-key radial gesture");
            SendMessage(host.Handle,0x100,0x19,physicalKeyData|(nint)(1L<<30));
            Check(!host.IsRadialActive,"Key auto-repeat cannot reopen a cancelled radial gesture");
            SendMessage(host.Handle,0x100,0x19,physicalKeyData);
            Check(host.IsRadialActive,"A fresh physical key press recovers when the IME consumed key-up");
            SendMessage(host.Handle,0x101,0x19,physicalKeyData);
        }
        finally
        {
            radial.Middle[0]=oldActions[CadoryxRadialPage.Middle];
            radial.Shift[0]=oldActions[CadoryxRadialPage.Shift];
            radial.Control[0]=oldActions[CadoryxRadialPage.Control];
            radial.Alt[0]=oldActions[CadoryxRadialPage.Alt];
            host.RadialCancelled-=OnRadialCancelled;
            host.RadialMenuEnabled=oldEnabled;
            ReleaseCapture();
            SetCursorPos(originalCursor.X,originalCursor.Y);
            viewport.RestoreCamera(previous);
        }
        void OnRadialCancelled(object? sender,EventArgs e)=>radialCancelled++;
        nint RadialPoint(int dx,int dy)
        {
            var center=pane!.RadialCenterScreen;
            var point=new NativePoint{X=(int)Math.Round(center.X)+dx,Y=(int)Math.Round(center.Y)+dy};
            Check(ScreenToClient(host.Handle,ref point),"Radial screen to client coordinates");
            return Point(point.X,point.Y);
        }
    }
    private static nuint Wheel(int delta)=>(nuint)((uint)unchecked((ushort)delta)<<16);
    private static async Task RadialStationaryWheel(ViewportPane pane,OcctViewportHost host,string output)
    {
        var observations=new List<object>();
        int moves=0;
        void Moved(object? sender,RadialPointerEventArgs args)=>moves++;
        SendMessage(host.Handle,0x100,0xC0,0);
        try
        {
            await Idle(); // Let the initial cursor warp settle before measuring stationary input.
            host.RadialMoved+=Moved;
            var owner=new WindowInteropHelper(Window.GetWindow(host)).Handle;
            foreach(var (target,expected,delta) in new[]
            {
                (pane.RadialPopupHandle,2,-30),(pane.RadialPopupHandle,2,-90),
                (host.Handle,3,-120),(owner,4,-120),(pane.RadialPopupHandle,1,-120),
                (host.Handle,4,30),(host.Handle,4,90)
            })
            {
                var clock=Stopwatch.StartNew();
                int visiblePage;
                Check(PostMessage(target,0x20A,Wheel(delta),0),"Queue stationary wheel input");
                do
                {
                    // Holding the menu key also produces repeat messages. None of
                    // these messages moves the pointer or explicitly renders a bitmap.
                    Check(PostMessage(host.Handle,0x100,0xC0,(nint)0x40290001),"Queue held-key repeat");
                    await Task.Delay(15);
                    visiblePage=ReadVisibleRadialPage(pane.RadialVisual);
                }
                while((pane.RadialPageNumber!=expected||visiblePage!=expected)&&
                    clock.ElapsedMilliseconds<400);
                Check(pane.RadialPageNumber==expected&&visiblePage==expected,
                    $"Stationary wheel displays page {expected} within 400 ms (state {pane.RadialPageNumber}, visible {visiblePage})");
                Check(moves==0,"Stationary wheel rendering must not need a pointer event");
                observations.Add(new{delta,page=pane.RadialPageNumber,visiblePage,
                    elapsedMs=clock.Elapsed.TotalMilliseconds,pointerMoves=moves});
            }
            int finalPage=pane.RadialPageNumber;
            // A subsequent real selection change must preserve the page that
            // was already rendered while stationary.
            var point=new NativePoint{X=(int)pane.RadialCenterScreen.X,Y=(int)pane.RadialCenterScreen.Y-95};
            Check(ScreenToClient(host.Handle,ref point),"Stationary wheel selection coordinates");
            SendMessage(host.Handle,0x200,0,Point(point.X,point.Y));
            await Task.Delay(30);
            Check(pane.RadialPageNumber==finalPage&&ReadVisibleRadialPage(pane.RadialVisual)==finalPage,
                "Pointer movement cannot reveal a different queued page");
            await File.WriteAllTextAsync(Path.Combine(output,"radial-stationary-wheel.json"),
                JsonSerializer.Serialize(observations,new JsonSerializerOptions{WriteIndented=true}));
        }
        finally
        {
            host.RadialMoved-=Moved;
            SendMessage(host.Handle,0x100,27,0);
            SendMessage(host.Handle,0x101,0xC0,0);
        }
    }
    private static int ReadVisibleRadialPage(FrameworkElement visual)
    {
        // Sample the already presented desktop pixels. RenderTargetBitmap,
        // UpdateLayout and PrintWindow would actively draw and hide this bug.
        var footer=Enumerable.Range(0,VisualTreeHelper.GetChildrenCount(visual))
            .Select(i=>VisualTreeHelper.GetChild(visual,i)).OfType<StackPanel>().Single();
        var dc=GetDC(0);
        Check(dc!=0,"Desktop pixels available for radial presentation check");
        try
        {
            int visible=0;
            for(int i=0;i<footer.Children.Count;i++)
            {
                var indicator=(Border)footer.Children[i];
                var point=indicator.PointToScreen(new(indicator.ActualWidth/2,4));
                uint color=GetPixel(dc,(int)Math.Round(point.X),(int)Math.Round(point.Y));
                Check(color!=uint.MaxValue,"Radial indicator is on the visible desktop");
                int red=(int)(color&255),green=(int)((color>>8)&255),blue=(int)((color>>16)&255);
                if(red>70&&blue>120&&blue>red+30&&blue>green+50)
                {
                    if(visible!=0)return 0;
                    visible=i+1;
                }
            }
            return visible;
        }
        finally{ReleaseDC(0,dc);}
    }
    private static void CaptureRadial(FrameworkElement visual,string path)
    {
        visual.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
            (int)Math.Ceiling(visual.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=File.Create(path);encoder.Save(stream);
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
    private static void NavigationGestures(OcctViewport viewport)
    {
        var initial=viewport.CaptureCamera();
        try
        {
            viewport.PointerPressed(1,120,120,0);
            viewport.PointerMoved(180,145,2,0);
            viewport.PointerReleased(1,180,145,0);
            var panned=viewport.CaptureCamera();
            Check((panned.Target-initial.Target).Length>0.01,"Middle drag pans the camera");
            Check(((panned.Eye-panned.Target)-(initial.Eye-initial.Target)).Length<1e-5&&
                (panned.Up-initial.Up).Length<1e-5,"Middle drag preserves viewing direction");

            viewport.RestoreCamera(initial);
            viewport.PointerPressed(2,120,120,0);
            viewport.PointerMoved(180,145,4,0);
            viewport.PointerReleased(2,180,145,0);
            var rotated=viewport.CaptureCamera();
            Check((rotated.Up-initial.Up).Length>0.01,
                "Right drag changes camera orientation");
        }
        finally {viewport.CancelInput();viewport.RestoreCamera(initial);}
    }
    private static async Task ContextMenuGesture(OcctViewportHost host)
    {
        ViewportPane? pane=null;
        for(DependencyObject? current=host;current is not null;current=VisualTreeHelper.GetParent(current))
            if(current is ViewportPane found){pane=found;break;}
        if(pane is null)throw new InvalidOperationException("Viewport pane missing for context menu test.");
        var before=host.Viewport!.CaptureCamera();
        SendMessage(host.Handle,0x204,2,Point(140,140));
        SendMessage(host.Handle,0x205,0,Point(140,140));
        await Idle();
        Check(pane.ReviewMenu.IsOpen,"Undragged right release opens context menu");
        Camera(before,host.Viewport.CaptureCamera());
        pane.ReviewMenu.IsOpen=false;
        SendMessage(host.Handle,0x204,2,Point(140,140));
        SendMessage(host.Handle,0x200,2,Point(175,150));
        SendMessage(host.Handle,0x205,0,Point(175,150));
        await Idle();
        Check(!pane.ReviewMenu.IsOpen,"Right drag does not open context menu");
    }
    private static async Task BlankCylinderGesture(MainWindowViewModel workspace,string output)
    {
        workspace.NewCommand.Execute(null);await Idle();
        var document=workspace.ActiveDocument??throw new InvalidOperationException("Blank cylinder document missing.");
        var viewportHost=Host(document);
        var viewport=viewportHost.Viewport??throw new InvalidOperationException("Blank cylinder viewport missing.");
        void Click((int X,int Y) pixel)
        {viewport.PointerPressed(0,pixel.X,pixel.Y,0);viewport.PointerReleased(0,pixel.X,pixel.Y,0);}
        document.StartTool("Cylinder");document.CancelViewportConstruction();
        document.SizeX=10;document.SizeZ=10;
        await document.PreviewCommand.ExecuteAsync(null);
        var previewScene=document.PreviewScene;
        Check(document.HasPreview&&previewScene?.Items.Length==1,
            $"Blank cylinder isolated preview: {document.ToolStatus}");
        var previewItem=previewScene!.Items[0];
        var previewPixel=viewport.WorldToScreen(previewItem.WorldTransform.Apply(
            (previewItem.Geometry.Bounds.Min+previewItem.Geometry.Bounds.Max)*0.5));
        bool previewWasHit=false;
        void PreviewPicked(object? sender,IReadOnlyList<SceneItem> items)=>
            previewWasHit|=items.Any(item=>item.BodyId==previewItem.BodyId);
        viewport.SelectionChanged+=PreviewPicked;
        try {Click(previewPixel);} finally {viewport.SelectionChanged-=PreviewPicked;}
        Check(previewWasHit&&document.Selection.Items.IsEmpty&&document.Session.Snapshot.Bodies.Count==0,
            "Picking an uncommitted preview cannot select a missing document body");
        document.CancelCommand.Execute(null);
        Check(!document.HasPreview&&document.Session.Snapshot.Bodies.Count==0,
            "Cancel releases only the uncommitted preview");

        document.StartTool("Cylinder");
        var start=viewport.WorldToScreen(new Vector3d(0,0,0));
        var radius=viewport.WorldToScreen(new Vector3d(10,0,0));
        Click(start);viewport.PointerMoved(radius.X,radius.Y,0,0);Click(radius);
        viewport.PointerMoved(radius.X,radius.Y-50,0,0);Click((radius.X,radius.Y-50));
        var deadline=Stopwatch.StartNew();
        while(document.Session.Snapshot.Bodies.Count==0&&deadline.Elapsed<TimeSpan.FromSeconds(5))
            await Task.Delay(20);
        Check(document.Session.Snapshot.Bodies.Count==1&&document.Scene.Items.Length==1,
            $"Third click commits blank cylinder: {document.ToolStatus}");
        Check(!document.HasPreview,"Committed cylinder is not an isolated preview");
        var committed=document.Scene.Items[0];
        var committedPixel=viewport.WorldToScreen(committed.WorldTransform.Apply(
            (committed.Geometry.Bounds.Min+committed.Geometry.Bounds.Max)*0.5));
        Click(committedPixel);
        Check(document.Selection.Items.Length==1&&document.Selection.Items[0].BodyId==committed.BodyId,
            "Clicking committed cylinder selects its document body");
        SendMessage(viewportHost.Handle,0x100,27,0);await Idle();
        Check(document.Session.Snapshot.Bodies.Count==1,
            "Escape clears selection or tool without deleting the committed cylinder");
        await document.Session.SaveAsync(new CadDocumentStorage(),Path.Combine(output,"blank-cylinder.cadoryx"));
        Check(await workspace.CloseAllAsync(),"Blank cylinder document closes");await Idle();
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
    private static async Task WaitForCamera(OcctViewport viewport,CadCamera expected)
    {
        // OCCT may round-trip the final eye position by a few ten-thousandths
        // of a model unit while applying the animated camera target.
        const double targetTolerance=1e-4;
        var deadline=Stopwatch.StartNew();
        while(deadline.Elapsed<TimeSpan.FromSeconds(2))
        {
            var actual=viewport.CaptureCamera();
            if((expected.Eye-actual.Eye).Length<targetTolerance&&(expected.Target-actual.Target).Length<targetTolerance&&
               (expected.Up-actual.Up).Length<targetTolerance&&Math.Abs(expected.Scale-actual.Scale)<targetTolerance)return;
            await Task.Delay(20);
        }
        var final=viewport.CaptureCamera();
        Check((expected.Eye-final.Eye).Length<targetTolerance&&(expected.Target-final.Target).Length<targetTolerance&&
            (expected.Up-final.Up).Length<targetTolerance&&Math.Abs(expected.Scale-final.Scale)<targetTolerance,
            "Camera animation reaches eye/target/up/scale target");
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
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool PostMessage(nint window,uint message,nuint w,nint l);
    [DllImport("user32.dll")]private static extern nint GetDC(nint window);
    [DllImport("user32.dll")]private static extern int ReleaseDC(nint window,nint dc);
    [DllImport("gdi32.dll")]private static extern uint GetPixel(nint dc,int x,int y);
    [DllImport("user32.dll")]private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")]private static extern nint GetCapture();
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool ScreenToClient(nint window,ref NativePoint point);
    [DllImport("user32.dll")]private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetClientRect(nint window,out ClientRect rect);
    [StructLayout(LayoutKind.Sequential)]private struct ClientRect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct NativePoint{public int X,Y;}
}
