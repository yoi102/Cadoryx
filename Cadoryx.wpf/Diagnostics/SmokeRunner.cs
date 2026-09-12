using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using Cadoryx.Kernel.Abstractions;
using Microsoft.Extensions.DependencyInjection;
namespace Cadoryx.wpf.Diagnostics;

/// <summary>Opt-in integration smoke: real window, native viewer, modeling, IO and lifecycle.</summary>
internal static class SmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        using var bindingOutput=new StreamWriter(Path.Combine(output,"bindings.log"));
        using var listener=new TextWriterTraceListener(bindingOutput);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        try
        {
            await Idle();var vm=(MainWindowViewModel)window.DataContext;
            var first=vm.ActiveDocument??throw new InvalidOperationException("No active dock document.");
            first.StartTool("Box");await first.PreviewCommand.ExecuteAsync(null);
            if(!first.HasPreview)throw new InvalidOperationException(first.ToolStatus);
            await first.ConfirmCommand.ExecuteAsync(null);
            if(first.Session.Snapshot.Bodies.Count!=1)throw new InvalidOperationException("Preview did not commit.");
            await Idle();
            var host=Find<OcctViewportHost>(window)??throw new InvalidOperationException("No native viewport in the window.");
            var viewport=host.Viewport??throw new InvalidOperationException("Native viewer did not initialize.");
            viewport.FitAll();viewport.SaveScreenshot(Path.Combine(output,"viewport.png"));
            if(vm.ModelTree.Items.Count==0)throw new InvalidOperationException("Model tree was not updated.");
            var storage=services.GetRequiredService<IDocumentStorage>();var kernel=services.GetRequiredService<IGeometryKernel>();
            await first.Session.SaveAsync(storage,Path.Combine(output,"smoke.cadoryx"));
            foreach(var ext in new[]{"step","iges","stl"})await kernel.ExportAsync(first.Session.Snapshot,first.Session.Assets,Path.Combine(output,"smoke."+ext));
            await vm.OpenPathAsync(Path.Combine(output,"smoke.step"));await Idle();
            var imported=vm.ActiveDocument!;
            if(ReferenceEquals(first,imported)||imported.Session.Snapshot.Bodies.Count==0)throw new InvalidOperationException("Exchange document failed to open.");
            var importedHost=Find<OcctViewportHost>(window)??throw new InvalidOperationException("No imported document viewport.");
            importedHost.Viewport!.FitAll();importedHost.Viewport.SaveScreenshot(Path.Combine(output,"imported.png"));
            await imported.Session.SaveAsync(storage,Path.Combine(output,"imported.cadoryx"));
            first.IsActive=true;await Idle();
            if(!ReferenceEquals(vm.ActiveDocument,first))throw new InvalidOperationException("Dock activation did not route to the first document.");
            await first.Session.UndoAsync();await first.Session.RedoAsync();await Idle();
            await ResourceSmokeRunner.RunAsync(window,vm,storage,output);
            OcctViewportHost.SuspendAll(true);await Idle();OcctViewportHost.SuspendAll(false);await Idle();
            var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(output,"shell.png")))encoder.Save(file);
            if(!await vm.CloseAllAsync())throw new InvalidOperationException("Documents did not close cleanly.");
            await ((App)System.Windows.Application.Current).StopRecoveryAsync();
            await Idle();
            int assets=((MemoryAssetStore)services.GetRequiredService<IAssetStore>()).Count;
            if(assets!=0)throw new InvalidOperationException($"{assets} assets remain after document close.");
            listener.Flush();bindingOutput.Flush();
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"PASS: native viewport, preview/commit, tree, STEP import, document switching, undo/redo, MessagePack save, STEP/IGES/STL export, resource window and density bindings, target part/layer/material creation, instance position bindings, visibility, MetroWindow dialogs, three-language layouts, modal airspace suspension, zero remaining assets.");
            window.CloseAfterSmoke();
        }
        catch(Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output,"result.txt"),"FAIL: "+ex);
            System.Windows.Application.Current.Shutdown(1);
        }
        finally{PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);}
    }
    private static async Task Idle(){await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(150);}
    private static T? Find<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T found)return found;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(Find<T>(VisualTreeHelper.GetChild(root,i)) is {} child)return child;
        return null;
    }
}
