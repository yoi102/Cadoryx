using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering.Occt;

namespace Cadoryx.wpf.Controls;

public sealed record RadialPointerEventArgs(System.Windows.Point ScreenPoint, int Modifiers);

/// <summary>Win32-only host. OCCT ownership and input translation live in Rendering.Occt.</summary>
public sealed class OcctViewportHost(IAssetStore assets) : HwndHost
{
    private static readonly WindowProc Proc=Process;
    private static readonly Dictionary<nint,OcctViewportHost> Hosts=[];
    private static readonly string ClassName="Cadoryx.Viewport."+Environment.ProcessId;
    private static readonly ushort Atom=Register();
    private nint hwnd;
    private bool contextClick;
    private bool mouseTracking;
    private int contextX,contextY;
    private long inputSequence;
    private bool radialActive;
    private bool radialOwnsMiddle;
    private int radialModifiers;
    private long lastAltMessageTick;
    public bool RadialMenuEnabled { get; set; }
    internal bool IsRadialActive => radialActive;
    public event EventHandler<RadialPointerEventArgs>? RadialStarted;
    public event EventHandler<RadialPointerEventArgs>? RadialMoved;
    public event EventHandler<RadialPointerEventArgs>? RadialCompleted;
    public event EventHandler? RadialCancelled;
    private static bool suspended;
    internal static int LiveCount=>Hosts.Count;
    public static void SuspendAll(bool value)
    {suspended=value;foreach(var (h,host) in Hosts){if(value)host.CancelCapture();Native.ShowWindow(h,value?0:5);}}
    private void CancelCapture()
    {contextClick=false;inputSequence++;CancelRadial();Viewport?.CancelInput();if(Native.GetCapture()==hwnd)Native.ReleaseCapture();}
    private void CancelRadial()
    {if(!radialActive)return;radialActive=false;RadialCancelled?.Invoke(this,EventArgs.Empty);}
    private RadialPointerEventArgs RadialPoint(int x,int y,int modifiers)
    {var point=new Point{x=x,y=y};Native.ClientToScreen(hwnd,ref point);return new(new(point.x,point.y),modifiers);}
    private void UpdateRadialModifier(uint message,int key)
    {
        int bit=key switch
        {
            0x10 or 0xA0 or 0xA1 => 1, // Shift
            0x11 or 0xA2 or 0xA3 => 2, // Ctrl
            0x12 or 0xA4 or 0xA5 => 4, // Alt (WM_SYSKEY*)
            _ => 0
        };
        if(bit==0)return;
        if(message is 0x100 or 0x104)radialModifiers|=bit;
        else radialModifiers&=~bit;
    }
    internal void NotifyRadialKey(uint message,int key)
    {
        if(!radialActive)return;
        if(key is 0x12 or 0xA4 or 0xA5)lastAltMessageTick=Environment.TickCount64;
        UpdateRadialModifier(message,key);
        if(key is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5)
            RadialMoved?.Invoke(this,RadialPoint(contextX,contextY,radialModifiers));
    }
    private bool KeepRadialDuringAltTransition()
    {
        if(!radialActive||!radialOwnsMiddle)return false;
        var foreground=Native.GetForegroundWindow();
        var foregroundIsOurs=foreground!=0&&Native.GetWindowThreadProcessId(foreground,out var processId)!=0&&
            processId==Environment.ProcessId;
        // A key-menu transition can send cancel/capture messages while this host still owns
        // capture, even when the foreground query is temporarily stale.
        if(!foregroundIsOurs&&Native.GetCapture()!=hwnd)
            return false;
        var altJustReleased=(radialModifiers&4)!=0&&Native.GetKeyState(0x12)>=0;
        if(!altJustReleased&&Environment.TickCount64-lastAltMessageTick>500)return false;
        if(altJustReleased)
        {radialModifiers&=~4;RadialMoved?.Invoke(this,RadialPoint(contextX,contextY,radialModifiers));}
        Dispatcher.BeginInvoke(()=>
        {
            var foregroundNow=Native.GetForegroundWindow();
            if(radialActive&&radialOwnsMiddle&&hwnd!=0&&Native.GetCapture()!=hwnd&&foregroundNow!=0&&
               Native.GetWindowThreadProcessId(foregroundNow,out var owner)!=0&&owner==Environment.ProcessId)
                Native.SetCapture(hwnd);
        });
        return true;
    }
    public OcctViewport? Viewport {get;private set;}
    public event EventHandler? Ready;
    public event EventHandler? Destroying;
    public event EventHandler<Exception>? Error;
    public event EventHandler<int>? ShortcutPressed;
    public event EventHandler<System.Windows.Point>? ContextMenuRequested;
    private bool WithinContextSlop(int x,int y)
    {
        var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(this);
        return Math.Abs(x-contextX)<=System.Windows.SystemParameters.MinimumHorizontalDragDistance*dpi.DpiScaleX
            &&Math.Abs(y-contextY)<=System.Windows.SystemParameters.MinimumVerticalDragDistance*dpi.DpiScaleY;
    }
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        _=Atom;
        hwnd=Native.CreateWindowEx(0,ClassName,"",0x40000000|0x10000000|0x04000000|0x02000000,0,0,1,1,parent.Handle,0,Native.GetModuleHandle(null),0);
        if(hwnd==0)throw new Win32Exception(Marshal.GetLastPInvokeError());
        Hosts.Add(hwnd,this);
        try{Viewport=new(hwnd,assets);Viewport.Resize();}
        catch{Hosts.Remove(hwnd);Native.DestroyWindow(hwnd);hwnd=0;throw;}
        if(suspended)Native.ShowWindow(hwnd,0);
        Dispatcher.BeginInvoke(()=>{if(Viewport is not null)Ready?.Invoke(this,EventArgs.Empty);});return new(this,hwnd);
    }
    protected override void DestroyWindowCore(HandleRef handle)
    {
        try{Destroying?.Invoke(this,EventArgs.Empty);}
        finally
        {
            CancelCapture();radialOwnsMiddle=false;Viewport?.Dispose();Viewport=null;
            if(handle.Handle!=0){Hosts.Remove(handle.Handle);Native.DestroyWindow(handle.Handle);}
            hwnd=0;
        }
    }
    private static nint Process(nint hwnd,uint message,nuint w,nint l)
    {
        if(!Hosts.TryGetValue(hwnd,out var host)||host.Viewport is not {} viewer)return Native.DefWindowProc(hwnd,message,w,l);
        try
        {
            int x=unchecked((short)((long)l&65535)),y=unchecked((short)(((long)l>>16)&65535));
            int modifiers=(Native.GetKeyState(0x10)<0?1:0)|(Native.GetKeyState(0x11)<0?2:0)|(Native.GetKeyState(0x12)<0?4:0);
            switch(message)
            {
                case 0x100:
                case 0x101:
                case 0x104: // WM_SYSKEYDOWN: Alt is delivered as a system key.
                case 0x105: // WM_SYSKEYUP
                    if(host.radialActive)
                    {
                        if((message is 0x100 or 0x104)&&(int)w==27){host.CancelCapture();return 0;}
                        host.NotifyRadialKey(message,(int)w);
                        return 0;
                    }
                    if((message is 0x104 or 0x105)&&host.RadialMenuEnabled&&((int)w is 0x12 or 0xA4 or 0xA5))
                        return 0; // Bare Alt must not open the Windows menu before Alt+middle.
                    if(message is 0x101 or 0x104 or 0x105)break;
                    int key=(int)w;
                    if(key==27||((modifiers&2)!=0&&key is 83 or 90 or 89))
                    {if(key==27)host.CancelCapture();host.Dispatcher.BeginInvoke(()=>host.ShortcutPressed?.Invoke(host,key));return 0;}
                    break;
                case 0x215:
                    if(l!=hwnd)
                    {
                        if(host.KeepRadialDuringAltTransition())return 0;
                        host.contextClick=false;host.inputSequence++;host.CancelRadial();viewer.CancelInput();
                    }
                    return 0;
                case 0x7B:return 0; // Native WM_CONTEXTMENU must not duplicate the explicit mouse-up menu.
                case 0x1F:case 8:
                    if(host.KeepRadialDuringAltTransition())return 0;
                    host.CancelCapture();break; // WM_CANCELMODE / WM_KILLFOCUS
                case 5:viewer.Resize();return 0;
                case 15:viewer.Redraw();break;
                case 20:return 1;
                case 0x200:
                    if(!host.mouseTracking)
                    {
                        var tracking=new TrackMouseEventInfo{Size=(uint)Marshal.SizeOf<TrackMouseEventInfo>(),Flags=2,Window=hwnd};
                        host.mouseTracking=Native.TrackMouseEvent(ref tracking);
                    }
                    int buttons=((w&1)!=0?1:0)|((w&0x10)!=0?2:0)|((w&2)!=0?4:0);
                    if(host.radialActive)
                    {host.contextX=x;host.contextY=y;host.RadialMoved?.Invoke(host,host.RadialPoint(x,y,host.radialModifiers));return 0;}
                    if(host.contextClick)
                    {
                        if(buttons==4&&modifiers==0&&host.WithinContextSlop(x,y))return 0;
                        host.contextClick=false; // Moving back to the origin is still a drag.
                    }
                    viewer.PointerMoved(x,y,buttons,modifiers);return 0;
                case 0x2A3:host.mouseTracking=false;viewer.PointerExited();return 0;
                case 0x207 when host.RadialMenuEnabled&&!viewer.HasPointerCapture&&(w&0x03)==0:
                    host.inputSequence++;host.contextClick=false;host.radialActive=true;host.radialOwnsMiddle=true;host.radialModifiers=modifiers;
                    host.contextX=x;host.contextY=y;
                    if(Native.GetFocus()!=hwnd)Native.SetFocus(hwnd);
                    if(Native.GetCapture()!=hwnd)Native.SetCapture(hwnd);
                    host.RadialStarted?.Invoke(host,host.RadialPoint(x,y,modifiers));return 0;
                case 0x208 when host.radialOwnsMiddle:
                    var complete=host.radialActive;
                    host.radialActive=false;
                    host.radialOwnsMiddle=false;
                    if(!viewer.HasPointerCapture&&Native.GetCapture()==hwnd)Native.ReleaseCapture();
                    if(complete)host.RadialCompleted?.Invoke(host,host.RadialPoint(x,y,host.radialModifiers));return 0;
                case 0x201:case 0x207:case 0x204:
                    host.CancelRadial();
                    host.inputSequence++;
                    host.contextClick=message==0x204&&!viewer.HasPointerCapture&&modifiers==0;
                    host.contextX=x;host.contextY=y;
                    if(Native.GetFocus()!=hwnd)Native.SetFocus(hwnd);
                    if(Native.GetCapture()!=hwnd)Native.SetCapture(hwnd);
                    viewer.PointerPressed(message==0x201?0:message==0x207?1:2,x,y,modifiers);return 0;
                case 0x202:case 0x208:case 0x205:
                    bool showMenu=message==0x205&&host.contextClick&&host.WithinContextSlop(x,y)&&modifiers==0;
                    host.contextClick=false;
                    try{viewer.PointerReleased(message==0x202?0:message==0x208?1:2,x,y,modifiers);}
                    finally{if(!viewer.HasPointerCapture&&Native.GetCapture()==hwnd)Native.ReleaseCapture();}
                    if(showMenu)
                    {
                        var screen=new Point{x=x,y=y};Native.ClientToScreen(hwnd,ref screen);
                        long sequence=host.inputSequence;
                        host.Dispatcher.BeginInvoke(()=>
                        {
                            if(host.hwnd==hwnd&&host.Viewport is not null&&!suspended&&host.IsVisible&&sequence==host.inputSequence)
                                host.ContextMenuRequested?.Invoke(host,new(screen.x,screen.y));
                        });
                    }
                    return 0;
                case 0x20A:
                    host.CancelRadial();
                    host.contextClick=false;host.inputSequence++;
                    var point=new Point{x=x,y=y};Native.ScreenToClient(hwnd,ref point);
                    viewer.MouseWheel(unchecked((short)((w>>16)&65535)),point.x,point.y,modifiers);return 0;
            }
        }
        catch(Exception ex){host.Dispatcher.BeginInvoke(()=>host.Error?.Invoke(host,ex));}
        return Native.DefWindowProc(hwnd,message,w,l);
    }
    private static ushort Register()
    {
        var info=new WindowClass{Size=(uint)Marshal.SizeOf<WindowClass>(),Style=0x20,Procedure=Marshal.GetFunctionPointerForDelegate(Proc),Instance=Native.GetModuleHandle(null),Cursor=Native.LoadCursor(0,32512),Name=ClassName};
        var result=Native.RegisterClassEx(in info);return result!=0?result:throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProc(nint hwnd,uint message,nuint w,nint l);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size,Style;public nint Procedure;public int ClassExtra,WindowExtra;public nint Instance,Icon,Cursor,Background;public string? Menu;public string Name;public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point {public int x,y;}
    [StructLayout(LayoutKind.Sequential)] private struct TrackMouseEventInfo
    {public uint Size,Flags;public nint Window;public uint HoverTime;}
    private static class Native
    {
        [DllImport("kernel32.dll",EntryPoint="GetModuleHandleW",CharSet=CharSet.Unicode)]internal static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll",EntryPoint="RegisterClassExW",SetLastError=true)]internal static extern ushort RegisterClassEx(in WindowClass info);
        [DllImport("user32.dll",EntryPoint="CreateWindowExW",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
        [DllImport("user32.dll",EntryPoint="DefWindowProcW")]internal static extern nint DefWindowProc(nint hwnd,uint message,nuint w,nint l);
        [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll",EntryPoint="LoadCursorW")]internal static extern nint LoadCursor(nint instance,int name);
        [DllImport("user32.dll")]internal static extern nint SetFocus(nint hwnd);
        [DllImport("user32.dll")]internal static extern nint GetFocus();
        [DllImport("user32.dll")]internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")]internal static extern uint GetWindowThreadProcessId(nint window,out int processId);
        [DllImport("user32.dll")]internal static extern nint SetCapture(nint hwnd);
        [DllImport("user32.dll")]internal static extern nint GetCapture();
        [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool ScreenToClient(nint hwnd,ref Point point);
        [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool ClientToScreen(nint hwnd,ref Point point);
        [DllImport("user32.dll")]internal static extern short GetKeyState(int key);
        [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool TrackMouseEvent(ref TrackMouseEventInfo info);
        [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool ShowWindow(nint hwnd,int command);
    }
}
