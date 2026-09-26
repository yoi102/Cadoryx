using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cadoryx.Editor;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Views;

namespace Cadoryx.wpf.Diagnostics;

internal static class RibbonNavigationSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,MainWindowViewModel workspace,string output,OcctViewportHost[] hosts)
    {
        var doc=workspace.ActiveDocument!;var state=doc.Session.Snapshot.StateId;
        var ribbon=Find<MainRibbonView>(window).Single();var panes=Find<ViewportPane>(window).ToArray();
        var primary=hosts[0].Viewport!;var secondary=hosts[1].Viewport!;
        var saved=primary.CaptureCamera();var savedSecond=secondary.CaptureCamera();var selection=doc.Selection.Items;
        var item=doc.Scene.Items.First();doc.Selection.Replace([new SelectionTarget(item.Path,item.BodyId,item.Geometry.Revision)]);
        await Idle();
        Require(ribbon.FocusButton.Command==doc.Review.FocusCommand&&ribbon.FitButton.Command==workspace.FitViewCommand,"Ribbon navigation binding");
        var focus=primary.CaptureFocusTarget(SceneEnvelope.Measure([item])!.Value);
        var start=focus with{Scale=focus.Scale*3};primary.RestoreCamera(start);doc.Review.ActivePane=0;
        ribbon.FocusButton.Command.Execute(null);Same(start,primary.CaptureCamera(),"Focus must not jump before first animation frame");
        await Task.Delay(110);Between(primary.CaptureCamera().Scale,focus.Scale,start.Scale,"Scale-only focus animation");
        await Task.Delay(360);Same(focus,primary.CaptureCamera(),"Focus final camera");
        var fit=primary.CaptureFitTarget();start=fit with{Scale=fit.Scale*3};primary.RestoreCamera(start);
        ribbon.FitButton.Command.Execute(null);Same(start,primary.CaptureCamera(),"Fit must not present destination before animation");
        await Task.Delay(110);Between(primary.CaptureCamera().Scale,fit.Scale,start.Scale,"Fit intermediate frame");
        var interrupted=primary.CaptureCamera();ribbon.FocusButton.Command.Execute(null);
        Same(interrupted,primary.CaptureCamera(),"Retarget starts at current frame");await Task.Delay(380);
        Same(primary.CaptureFocusTarget(SceneEnvelope.Measure([item])!.Value),primary.CaptureCamera(),"Retarget completion");
        primary.RestoreCamera(start);ribbon.FitButton.Command.Execute(null);await Task.Delay(100);
        primary.MouseWheel(120,150,150,0);var navigated=primary.CaptureCamera();await Task.Delay(380);
        Same(navigated,primary.CaptureCamera(),"Wheel cancels animation");

        var menu=panes[0].ReviewMenu;var camera=primary.CaptureCamera();
        SendMessage(hosts[0].Handle,0x204,2,Point(100,100));Require(!menu.IsOpen,"No menu on right down");
        SendMessage(hosts[0].Handle,0x200,2,Point(101,101));Same(camera,primary.CaptureCamera(),"Click jitter does not navigate");
        SendMessage(hosts[0].Handle,0x205,0,Point(101,101));await Idle();Require(menu.IsOpen,"Right up opens menu");
        Require(GetCapture()!=hosts[0].Handle,"Native capture released before menu");
        var isolate=(MenuItem)menu.Items[0];var hide=(MenuItem)menu.Items[1];var show=(MenuItem)menu.Items[3];
        Require(isolate.Command==doc.Review.IsolateCommand&&hide.Command==doc.Review.HideCommand&&show.Command==doc.Review.ShowAllCommand,"Menu commands bind current document");
        Capture(menu,Path.Combine(output,"viewport-context-menu.png"));
        isolate.Command.Execute(null);menu.IsOpen=false;await Idle();Require(primary.VisibleBodyCount==1,"Menu isolation");
        show.Command.Execute(null);hide.Command.Execute(null);await Idle();Require(primary.VisibleBodyCount==doc.Scene.Items.Length-1,"Menu temporary hiding");
        show.Command.Execute(null);await Idle();Require(primary.VisibleBodyCount==doc.Scene.Items.Length,"Menu restore");
        SendMessage(hosts[0].Handle,0x204,2,Point(100,100));
        SendMessage(hosts[0].Handle,0x200,2,Point(140,125));SendMessage(hosts[0].Handle,0x200,2,Point(100,100));
        SendMessage(hosts[0].Handle,0x205,0,Point(100,100));await Idle();Require(!menu.IsOpen,"Drag returning to origin must not open menu");
        SendMessage(hosts[0].Handle,0x204,2,Point(100,100));SetCapture(new WindowInteropHelper(window).Handle);
        SendMessage(hosts[0].Handle,0x205,0,Point(100,100));ReleaseCapture();await Idle();Require(!menu.IsOpen,"Capture loss suppresses late menu");
        SendMessage(hosts[0].Handle,0x204,2,Point(100,100));SendMessage(hosts[0].Handle,0x100,27,0);
        SendMessage(hosts[0].Handle,0x205,0,Point(100,100));await Idle();Require(!menu.IsOpen,"Escape suppresses menu");
        doc.Selection.Replace([new SelectionTarget(item.Path,item.BodyId,item.Geometry.Revision)]);

        SendMessage(hosts[1].Handle,0x204,2,Point(100,100));SendMessage(hosts[1].Handle,0x205,0,Point(100,100));await Idle();
        Require(panes[1].ReviewMenu.IsOpen&&doc.Review.ActivePane==1,"Secondary right click activates its pane");panes[1].ReviewMenu.IsOpen=false;
        camera=primary.CaptureCamera();var secondFit=secondary.CaptureFitTarget();secondary.RestoreCamera(secondFit with{Scale=secondFit.Scale*3});
        ribbon.FitButton.Command.Execute(null);await Task.Delay(380);Same(camera,primary.CaptureCamera(),"Ribbon leaves inactive camera unchanged");Same(secondFit,secondary.CaptureCamera(),"Ribbon fits active second pane");
        Capture(ribbon,Path.Combine(output,"ribbon-view.png"));
        for(int tab=0;tab<3;tab++){ribbon.RibbonTabs.SelectedIndex=tab;await Idle();Capture(ribbon,Path.Combine(output,$"ribbon-tab-{tab}.png"));}
        ribbon.ViewTab.IsSelected=true;await Idle();
        doc.Review.ActivePane=0;primary.RestoreCamera(saved);secondary.RestoreCamera(savedSecond);doc.Selection.Replace(selection);
        Require(doc.Session.Snapshot.StateId==state,"Navigation and transient visibility preserve document");
        await File.WriteAllTextAsync(Path.Combine(output,"ribbon-navigation-result.txt"),"PASS: Ribbon routing, scale-only focus and fit intermediate frames, no initial jump, retarget and wheel cancellation, right-up menu, click jitter, drag return, capture loss, Escape, isolate/hide/show, active secondary pane, no document mutation.");
    }
    private static void Between(double value,double a,double b,string message)=>Require(value>Math.Min(a,b)+1e-5&&value<Math.Max(a,b)-1e-5,message);
    private static void Same(CadCamera a,CadCamera b,string message)=>Require((a.Eye-b.Eye).Length<1e-5&&(a.Target-b.Target).Length<1e-5&&(a.Up-b.Up).Length<1e-5&&Math.Abs(a.Scale-b.Scale)<1e-5,$"{message}: eye delta={(a.Eye-b.Eye).Length}, target delta={(a.Target-b.Target).Length}, scale={a.Scale}/{b.Scale}");
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static async Task Idle(){await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(100);}
    private static void Capture(FrameworkElement element,string path)
    {
        var bitmap=new RenderTargetBitmap((int)element.ActualWidth,(int)element.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(element);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T item)yield return item;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Find<T>(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
    private static nint Point(int x,int y)=>(nint)((y<<16)|(x&65535));
    [DllImport("user32.dll")]private static extern nint SendMessage(nint window,uint message,nuint w,nint l);
    [DllImport("user32.dll")]private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")]private static extern nint GetCapture();
    [DllImport("user32.dll")]private static extern bool ReleaseCapture();
}
