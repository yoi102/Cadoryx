using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering.Occt;

namespace Cadoryx.wpf.Controls;

/// <summary>Win32-only host. OCCT ownership and input translation live in Rendering.Occt.</summary>
public sealed class OcctViewportHost(IAssetStore assets) : HwndHost
{
    private static readonly WindowProc Proc=Process;
    private static readonly Dictionary<nint,OcctViewportHost> Hosts=[];
    private static readonly string ClassName="Cadoryx.Viewport."+Environment.ProcessId;
    private static readonly ushort Atom=Register();
    private nint hwnd;
    private static bool suspended;
    public static void SuspendAll(bool value)
    {suspended=value;foreach(var h in Hosts.Keys)Native.ShowWindow(h,value?0:5);}
    public OcctViewport? Viewport {get;private set;}
    public event EventHandler? Ready;
    public event EventHandler? Destroying;
    public event EventHandler<Exception>? Error;
    public event EventHandler<int>? ShortcutPressed;
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
            Viewport?.Dispose();Viewport=null;
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
                    int key=(int)w;
                    if(key==27||((modifiers&2)!=0&&key is 83 or 90 or 89))
                    {host.Dispatcher.BeginInvoke(()=>host.ShortcutPressed?.Invoke(host,key));return 0;}
                    break;
                case 0x215:viewer.CancelInput();return 0;
                case 5:viewer.Resize();return 0;
                case 15:viewer.Redraw();break;
                case 20:return 1;
                case 0x200:
                    int buttons=((w&1)!=0?1:0)|((w&0x10)!=0?2:0)|((w&2)!=0?4:0);
                    viewer.PointerMoved(x,y,buttons,modifiers);return 0;
                case 0x201:case 0x207:case 0x204:
                    Native.SetFocus(hwnd);Native.SetCapture(hwnd);
                    viewer.PointerPressed(message==0x201?0:message==0x207?1:2,x,y,modifiers);return 0;
                case 0x202:case 0x208:case 0x205:
                    try{viewer.PointerReleased(message==0x202?0:message==0x208?1:2,x,y,modifiers);}finally{Native.ReleaseCapture();}return 0;
                case 0x20A:
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
    private static class Native
    {
        [DllImport("kernel32.dll",EntryPoint="GetModuleHandleW",CharSet=CharSet.Unicode)]internal static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll",EntryPoint="RegisterClassExW",SetLastError=true)]internal static extern ushort RegisterClassEx(in WindowClass info);
        [DllImport("user32.dll",EntryPoint="CreateWindowExW",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
        [DllImport("user32.dll",EntryPoint="DefWindowProcW")]internal static extern nint DefWindowProc(nint hwnd,uint message,nuint w,nint l);
        [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll",EntryPoint="LoadCursorW")]internal static extern nint LoadCursor(nint instance,int name);
        [DllImport("user32.dll")]internal static extern nint SetFocus(nint hwnd);
        [DllImport("user32.dll")]internal static extern nint SetCapture(nint hwnd);
        [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool ScreenToClient(nint hwnd,ref Point point);
        [DllImport("user32.dll")]internal static extern short GetKeyState(int key);
        [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool ShowWindow(nint hwnd,int command);
    }
}
