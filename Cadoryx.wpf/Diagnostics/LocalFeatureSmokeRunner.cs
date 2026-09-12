using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static class LocalFeatureSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,IServiceProvider services,string output)
    {
        var vm=(MainWindowViewModel)window.DataContext;var session=vm.ActiveDocument!.Session;
        var part=DefinitionId.New();await session.ExecuteAsync(ResourceCommands.AddPart(part,"Local verification"));
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Local test box",part));
        var producer=session.Snapshot.Features.Values.Single(f=>f.Name=="Local test box");
        var oldReference=TopologyReference.Box(session.Snapshot,producer.Id,BoxBoundary.XMin,policy:TopologyRebindPolicy.ExactRevision);
        await session.ExecuteAsync(new UpsertTopologyReferenceCommand(oldReference));
        await session.ExecuteAsync(new RecomputeCommand(producer.Id,producer.Recipe));
        await Operate(()=>vm.LocalFeatureCommand.ExecuteAsync(null),async dialog=>
        {
            var editor=dialog.Editor;editor.SelectedBox=editor.Boxes.Single(b=>b.FeatureId==producer.Id);editor.SelectedReference=editor.References.Single(r=>r.Reference.Id==oldReference.Id);
            await editor.InspectReferenceCommand.ExecuteAsync(null);Check(editor.Status==Cadoryx.Lang.Strings.Strings.ReferenceStale,"Visible stale reference diagnostic");
            editor.SelectionKind=TopologyKind.Face;var viewport=dialog.Host.Viewport!;viewport.SetProjection(CadProjection.Top);viewport.FitAll();await Idle();
            var face=viewport.WorldToScreen(new(5,10,30));viewport.PointerPressed(0,face.X,face.Y,0);viewport.PointerReleased(0,face.X,face.Y,0);
            Check(editor.Selection?.Id==oldReference.Id,"Reselection keeps reference identity");await editor.SaveReferenceCommand.ExecuteAsync(null);
        });
        var reselected=session.Snapshot.TopologyReferences[oldReference.Id];
        Check(reselected.Boundary==BoxBoundary.ZMax&&reselected.Policy==TopologyRebindPolicy.ExactRevision&&reselected.OriginRevision!=oldReference.OriginRevision,"Explicit reselection persisted");
        var before=session.Snapshot;
        foreach(var language in new[]{"en-US","zh-CN","ja-JP"})
        {
            var original=CultureInfo.CurrentUICulture;CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);
            try
            {
                await Operate(()=>vm.LocalFeatureCommand.ExecuteAsync(null),async dialog=>
                {
                    Check(dialog.Owner==window,"Owned MetroWindow");var editor=dialog.Editor;
                    editor.SelectedBox=editor.Boxes.Single(b=>b.FeatureId==producer.Id);await Idle();
                    var viewport=dialog.Host.Viewport!;viewport.SetProjection(CadProjection.Top);viewport.FitAll();await Idle();
                    editor.SelectionKind=TopologyKind.Face;await Idle();var face=viewport.WorldToScreen(new(5,10,30));
                    viewport.PointerPressed(0,face.X,face.Y,0);viewport.PointerReleased(0,face.X,face.Y,0);
                    Check(editor.Selection?.Kind==TopologyKind.Face&&editor.Selection.Boundary==BoxBoundary.ZMax,"Native top face picking");
                    Check(!editor.CanPreview,"Face cannot fillet");
                    editor.SelectionKind=TopologyKind.Edge;viewport.SetProjection(CadProjection.Front);viewport.FitAll();await Idle();
                    var edge=viewport.WorldToScreen(new(5,0,30));viewport.PointerPressed(0,edge.X,edge.Y,0);viewport.PointerReleased(0,edge.X,edge.Y,0);
                    Check(editor.Selection is {Kind:TopologyKind.Edge,Boundary:BoxBoundary.YMin,SecondBoundary:BoxBoundary.ZMax},"Native semantic edge picking");
                    editor.Operation=LocalFeatureOperation.Chamfer;dialog.SizeInput.Value=2;await Idle();
                    await editor.PreviewCommand.ExecuteAsync(null);Check(editor.CanConfirm,editor.Status);
                    var preview=editor.Scene!.Items.Single().Geometry;Check(Math.Abs(preview.VolumeMm3-5980)<1e-4,"Single edge chamfer volume");
                    Check(ReferenceEquals(before,session.Snapshot),"Preview is uncommitted");
                    viewport.SetProjection(CadProjection.Axonometric);viewport.FitAll();await Idle();
                    viewport.SaveScreenshot(Path.Combine(output,"local-preview-"+language+".png"));Save(dialog,Path.Combine(output,"local-window-"+language+".png"));
                    if(language=="ja-JP")await editor.ConfirmCommand.ExecuteAsync(null);else dialog.Close();
                });
            }
            finally{CultureInfo.CurrentUICulture=original;}
            if(language!="ja-JP")Check(ReferenceEquals(before,session.Snapshot),"Cancel leaves snapshot unchanged");
        }
        var after=session.Snapshot;var feature=after.Features.Values.Single(f=>f.PartId==part&&f.Recipe is LocalFeatureRecipe);
        Check(Math.Abs(feature.Result.VolumeMm3-5980)<1e-4,"Confirmed volume");
        await session.UndoAsync();Check(ReferenceEquals(before,session.Snapshot),"Exact undo");await session.RedoAsync();Check(ReferenceEquals(after,session.Snapshot),"Exact redo");
        var storage=services.GetRequiredService<IDocumentStorage>();var path=Path.Combine(output,"local-feature.cadoryx");await session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,session.Assets))Check(loaded.Snapshot.Features[feature.Id].Recipe==feature.Recipe,"Local recipe roundtrip");
        foreach(var extension in new[]{"step","iges","stl"})await services.GetRequiredService<IGeometryKernel>().ExportAsync(after,session.Assets,Path.Combine(output,"local-feature."+extension));
        await File.WriteAllTextAsync(Path.Combine(output,"local-feature-result.json"),JsonSerializer.Serialize(new{passed=true,facePicking=true,edgePicking=true,referenceReselection=true,previewCancel=true,exactUndoRedo=true,volume=5980,cultures=new[]{"en-US","zh-CN","ja-JP"}}));
    }
    private static async Task Operate(Func<Task> open,Func<LocalFeatureWindow,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=System.Windows.Application.Current.Dispatcher.InvokeAsync(async()=>
        {
            LocalFeatureWindow? dialog=null;
            try{await Idle();dialog=System.Windows.Application.Current.Windows.OfType<LocalFeatureWindow>().Single();await action(dialog);completion.TrySetResult();}
            catch(Exception ex){completion.TrySetException(ex);}
            finally{if(dialog is {IsVisible:true})dialog.Close();}
        },DispatcherPriority.ApplicationIdle);
        await open();await completion.Task;
    }
    private static async Task Idle(){await System.Windows.Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(130);}
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException("Local feature: "+message);}
    private static void Save(FrameworkElement element,string path)
    {element.UpdateLayout();var bitmap=new RenderTargetBitmap((int)element.ActualWidth,(int)element.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(element);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);png.Save(file);}
}
