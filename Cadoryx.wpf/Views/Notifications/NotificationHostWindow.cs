using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Notifications.Controls;
using Notifications.Enums;

namespace Cadoryx.wpf.Views.Toasts;

/// <summary>A compact non-activating HWND keeps custom notifications above the native CAD viewport.</summary>
internal sealed class NotificationHostWindow : Window
{
    private readonly Window applicationWindow;
    private readonly NotificationArea area;
    private bool hasItems;
    private bool repositionQueued;
    internal string AreaIdentifier {get;}="Cadoryx.Notifications."+Guid.NewGuid().ToString("N");
    internal CadNotificationAnchor Anchor {get;private set;}
    internal NotificationHostWindow(Window applicationWindow,CadNotificationAnchor anchor)
    {
        this.applicationWindow=applicationWindow;
        WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;
        ShowActivated=false;ShowInTaskbar=false;Focusable=false;SizeToContent=SizeToContent.WidthAndHeight;
        WindowStartupLocation=WindowStartupLocation.Manual;Left=-32000;Top=-32000;
        area=new NotificationArea{Identifier=AreaIdentifier,Position=NotificationPosition.BottomRight,
            MaxItems=3,AllowRemovingPermanentOnOverflow=false,Margin=new Thickness(0),NotificationMargin=new Thickness(0,8,0,0)};
        Content=area;
        SourceInitialized+=(_,_)=>
        {
            var hwnd=new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(hwnd,-20,GetWindowLongPtr(hwnd,-20)|0x08000000); // WS_EX_NOACTIVATE
        };
        SizeChanged+=(_,_)=>QueueReposition();
        DpiChanged+=(_,_)=>QueueReposition();
        applicationWindow.LocationChanged+=OnOwnerChanged;
        applicationWindow.SizeChanged+=OnOwnerSizeChanged;
        applicationWindow.StateChanged+=OnOwnerChanged;
        applicationWindow.IsVisibleChanged+=OnOwnerVisibilityChanged;
        if(applicationWindow.Content is FrameworkElement content)content.SizeChanged+=OnOwnerSizeChanged;
        SetAnchor(anchor);
    }
    internal void SetAnchor(CadNotificationAnchor anchor)
    {
        Anchor=anchor;
        Owner=anchor==CadNotificationAnchor.ApplicationWindow?applicationWindow:null;
        Topmost=anchor==CadNotificationAnchor.WindowsDesktop;
        UpdateVisibility();Reposition();
    }
    internal async Task PrepareAsync()
    {
        // Load/register NotificationArea without briefly painting a misplaced window.
        if(!IsVisible){Opacity=0;Show();}
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Loaded);
    }
    internal async Task PresentAsync()
    {
        hasItems=true;
        // SizeToContent must settle before the first visible frame, not after setting Opacity=1.
        UpdateLayout();
        await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Loaded);
        Reposition();UpdateVisibility();
    }
    internal void SetHasItems(bool value){hasItems=value;UpdateVisibility();Reposition();}
    private void UpdateVisibility()
    {
        bool visible=hasItems&&(Anchor==CadNotificationAnchor.WindowsDesktop||
            (applicationWindow.IsVisible&&applicationWindow.WindowState!=WindowState.Minimized));
        if(visible){if(!IsVisible){Opacity=0;Show();UpdateLayout();}Reposition();Opacity=1;}
        else if(IsVisible)Hide();
    }
    private void OnOwnerChanged(object? sender,EventArgs args){UpdateVisibility();Reposition();QueueReposition();}
    private void OnOwnerSizeChanged(object sender,SizeChangedEventArgs args)=>QueueReposition();
    private void OnOwnerVisibilityChanged(object sender,DependencyPropertyChangedEventArgs args)=>UpdateVisibility();
    private void QueueReposition()
    {
        if(repositionQueued)return;repositionQueued=true;
        Dispatcher.BeginInvoke(()=>{repositionQueued=false;Reposition();},DispatcherPriority.Loaded);
    }
    internal void Reposition()
    {
        var hwnd=new WindowInteropHelper(this).Handle;
        if(hwnd==0||!IsVisible||applicationWindow.WindowState==WindowState.Minimized&&Anchor==CadNotificationAnchor.ApplicationWindow)return;
        double right,bottom,left,top;
        if(Anchor==CadNotificationAnchor.ApplicationWindow&&applicationWindow.Content is FrameworkElement content&&content.IsLoaded)
        {
            var origin=content.PointToScreen(new Point());var corner=content.PointToScreen(new Point(content.ActualWidth,content.ActualHeight));
            left=origin.X;top=origin.Y;right=corner.X;bottom=corner.Y;
        }
        else
        {
            var monitor=MonitorFromWindow(new WindowInteropHelper(applicationWindow).Handle,2);
            var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
            if(!GetMonitorInfo(monitor,ref info))return;
            left=info.Work.Left;top=info.Work.Top;right=info.Work.Right;bottom=info.Work.Bottom;
        }
        var dpi=VisualTreeHelper.GetDpi(this);
        MaxHeight=Math.Max(40,(bottom-top)/dpi.DpiScaleY-32);
        if(!GetWindowRect(hwnd,out var bounds))return;
        int x=(int)Math.Round(Math.Max(left+16*dpi.DpiScaleX,right-(bounds.Right-bounds.Left)-16*dpi.DpiScaleX));
        int y=(int)Math.Round(Math.Max(top+16*dpi.DpiScaleY,bottom-(bounds.Bottom-bounds.Top)-16*dpi.DpiScaleY));
        SetWindowPos(hwnd,0,x,y,0,0,0x0015); // no size, no z-order, no activation
    }
    protected override void OnClosed(EventArgs e)
    {
        applicationWindow.LocationChanged-=OnOwnerChanged;applicationWindow.SizeChanged-=OnOwnerSizeChanged;
        applicationWindow.StateChanged-=OnOwnerChanged;applicationWindow.IsVisibleChanged-=OnOwnerVisibilityChanged;
        if(applicationWindow.Content is FrameworkElement content)content.SizeChanged-=OnOwnerSizeChanged;
        base.OnClosed(e);
    }
    [StructLayout(LayoutKind.Sequential)]private struct NativeRect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct MonitorInfo{public int Size;public NativeRect Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")]private static extern nint GetWindowLongPtr(nint hwnd,int index);
    [DllImport("user32.dll")]private static extern nint SetWindowLongPtr(nint hwnd,int index,nint value);
    [DllImport("user32.dll")]private static extern nint MonitorFromWindow(nint hwnd,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Auto)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetMonitorInfo(nint monitor,ref MonitorInfo info);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetWindowRect(nint hwnd,out NativeRect rect);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
}
