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
using Cadoryx.wpf.Controls;
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
    private static async Task Idle(){await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(150);}
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    [DllImport("user32.dll")]private static extern nint SendMessage(nint window,uint message,nuint w,nint l);
    [DllImport("user32.dll")]private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")]private static extern nint GetCapture();
    [DllImport("user32.dll")]private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
}
