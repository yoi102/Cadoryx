using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;
using OcctSharp;
using LocalFeatureOperation=Cadoryx.Db.LocalFeatureOperation;

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
            await editor.InspectReferenceCommand.ExecuteAsync(null);Check(editor.Status==Cadoryx.Lang.Strings.Strings.ReferenceStale,
                $"Visible stale reference diagnostic: actual='{editor.Status}', expected='{Cadoryx.Lang.Strings.Strings.ReferenceStale}'");
            editor.SelectionKind=TopologyKind.Face;var viewport=dialog.Host.Viewport!;viewport.SetProjection(CadProjection.Top);viewport.FitAll();await Idle();
            var face=viewport.WorldToScreen(new(5,10,30));viewport.PointerPressed(0,face.X,face.Y,0);viewport.PointerReleased(0,face.X,face.Y,0);
            Check(editor.Selection?.Id==oldReference.Id,"Reselection keeps reference identity");await editor.SaveReferenceCommand.ExecuteAsync(null);
        });
        var reselected=session.Snapshot.TopologyReferences[oldReference.Id];
        Check(reselected.Boundary==BoxBoundary.ZMax&&reselected.Policy==TopologyRebindPolicy.ExactRevision&&reselected.OriginRevision!=oldReference.OriginRevision,"Explicit reselection persisted");
        var before=session.Snapshot;
        await Operate(()=>vm.LocalFeatureCommand.ExecuteAsync(null),async dialog=>
        {
            var editor=dialog.Editor;editor.SelectedBox=editor.Boxes.Single(b=>b.FeatureId==producer.Id);
            editor.Pick(BoxBoundary.XMin,BoxBoundary.YMin);dialog.SizeInput.Value=.5;
            dialog.AddEdgesOnPickCheck.IsChecked=true;await Idle();
            editor.Pick(BoxBoundary.XMax,BoxBoundary.YMax);
            Check(editor.AddEdgesOnPick&&editor.AdditionalEdges==LocalFeatureRecipe.EdgeBit(BoxBoundary.XMax,BoxBoundary.YMax),"Multi-edge controls and selection");
            await editor.PreviewCommand.ExecuteAsync(null);Check(editor.CanConfirm,editor.Status);
            dialog.VariableRadiusCheck.IsChecked=true;dialog.EndRadiusInput.Value=2;await Idle();
            Check(!editor.CanConfirm,"Parameter change discards preview");
            await editor.PreviewCommand.ExecuteAsync(null);Check(!editor.CanConfirm,"Variable radius rejects multiple edges");
            editor.Pick(BoxBoundary.XMax,BoxBoundary.YMax);
            await editor.PreviewCommand.ExecuteAsync(null);Check(editor.CanConfirm,editor.Status);
            Check(editor.Scene!.Items.Single().Geometry.VolumeMm3<6000,"Variable radius preview volume");
            Check(ReferenceEquals(before,session.Snapshot),"Extended preview is uncommitted");
            dialog.Close();
        });
        Check(ReferenceEquals(before,session.Snapshot),"Extended dialog cancellation keeps document unchanged");
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
        var treeFeature=All(vm.ModelTree.Items).Single(item=>item.Feature==feature.Id);
        await Operate(()=>{vm.ModelTree.Select(treeFeature);return Task.CompletedTask;},async dialog=>
        {
            var editor=dialog.Editor;Check(editor.IsEditing&&!editor.CanChooseSource,"Model tree opens parameter editor");
            Check(editor.Operation==LocalFeatureOperation.Chamfer&&editor.Size==2,"Existing recipe initialized");
            dialog.SizeInput.Value=3;dialog.TwoDistancesCheck.IsChecked=true;dialog.SecondSizeInput.Value=2;
            await Idle();await editor.PreviewCommand.ExecuteAsync(null);
            Check(editor.UseTwoDistances&&editor.SecondDistance==2&&editor.CanConfirm&&
                Math.Abs(editor.Scene!.Items.Single().Geometry.VolumeMm3-5970)<1e-4,"Two-distance preview and MahApps binding");
            Check(ReferenceEquals(after,session.Snapshot),"Edited preview is uncommitted");
            dialog.Close();
        });
        Check(ReferenceEquals(after,session.Snapshot),"Tree edit cancellation keeps original output");
        var occurrence=after.EnumerateOccurrences().Single(o=>o.DefinitionId==part);
        vm.ActiveDocument.Selection.Replace([new SelectionTarget(occurrence.Path,feature.OutputBodyId,feature.Result.Revision)]);
        await Operate(()=>{vm.Properties.EditFeatureCommand.Execute(null);return Task.CompletedTask;},async dialog=>
        {
            var editor=dialog.Editor;Check(editor.IsEditing,"Properties opens parameter editor");
            dialog.SizeInput.Value=3;dialog.TwoDistancesCheck.IsChecked=true;dialog.SecondSizeInput.Value=2;
            await Idle();await editor.PreviewCommand.ExecuteAsync(null);
            Check(editor.CanConfirm&&Math.Abs(editor.Scene!.Items.Single().Geometry.VolumeMm3-5970)<1e-4,"Properties two-distance preview");
            await editor.ConfirmCommand.ExecuteAsync(null);
        });
        var edited=session.Snapshot;Check(edited.Features[feature.Id].OutputBodyId==feature.OutputBodyId&&
            Math.Abs(edited.Features[feature.Id].Result.VolumeMm3-5970)<1e-4&&
            ((LocalFeatureRecipe)edited.Features[feature.Id].Recipe).SecondDistance==2,"Edited feature keeps identity and both distances");
        await session.UndoAsync();Check(ReferenceEquals(after,session.Snapshot),"Edit exact undo");
        await session.RedoAsync();Check(ReferenceEquals(edited,session.Snapshot),"Edit exact redo");
        await Operate(()=>vm.LocalFeatureCommand.ExecuteAsync(null),async dialog=>
        {
            var editor=dialog.Editor;editor.SelectedBox=editor.Boxes.Single(b=>b.FeatureId==feature.Id);
            Check(editor.IsChainedSource&&editor.SelectedBox.OriginBoxFeatureId==producer.Id,"Local result offered as chained source");
            var viewport=dialog.Host.Viewport!;viewport.SetProjection(CadProjection.Back);viewport.FitAll();await Idle();
            var edge=viewport.WorldToScreen(new(10,20,15));
            viewport.PointerPressed(0,edge.X,edge.Y,0);viewport.PointerReleased(0,edge.X,edge.Y,0);
            Check(editor.Selection is {Kind:TopologyKind.Edge,Boundary:BoxBoundary.XMax,SecondBoundary:BoxBoundary.YMax},"Native unchanged result-edge picking");
            Check(editor.Selection!.FeatureId==producer.Id&&editor.CanChangeOperation,"Chained pick keeps original semantic identity and offers chamfer");
            dialog.SizeInput.Value=.5;dialog.VariableRadiusCheck.IsChecked=true;dialog.EndRadiusInput.Value=1;
            await Idle();await editor.PreviewCommand.ExecuteAsync(null);Check(editor.CanConfirm,editor.Status);
            Check(editor.Scene!.Items.Single().Geometry.VolumeMm3<edited.Features[feature.Id].Result.VolumeMm3,"Chained variable-radius preview");
            Check(ReferenceEquals(edited,session.Snapshot),"Chained preview is uncommitted");
            dialog.Close();
        });
        Check(ReferenceEquals(edited,session.Snapshot),"Chained preview cancellation keeps source result");
        await Operate(()=>vm.LocalFeatureCommand.ExecuteAsync(null),async dialog=>
        {
            var editor=dialog.Editor;editor.SelectedBox=editor.Boxes.Single(b=>b.FeatureId==feature.Id);
            var viewport=dialog.Host.Viewport!;
            using var shape=OcctGeometryBridge.ReadShape(edited.Features[feature.Id].Result,session.Assets);
            using var map=RepairSnapshot.Create(shape);
            var edges=shape.GetSubShapes(ShapeKind.Edge);
            try
            {
                var generated=edges.Select(edge=>(Shape:edge,Index:RepairSnapshot.FindTopologyIndex(shape,edge)))
                    .Where(item=>item.Index>=0&&BoxTopology.Classify(item.Shape,(BoxRecipe)producer.Recipe,TopologyKind.Edge).Count==0)
                    .DistinctBy(item=>item.Index).ToArray();
                Check(generated.Length>0,"Native result includes generated edges");
                bool picked=false;
                foreach(var projection in new[]{CadProjection.Front,CadProjection.Back,CadProjection.Left,CadProjection.Right,
                    CadProjection.Top,CadProjection.Bottom,CadProjection.Axonometric})
                {
                    viewport.SetProjection(projection);viewport.FitAll();await Idle();
                    foreach(var item in generated)
                    {
                        var curve=item.Shape.GetEdgeCurveSnapshot();
                        if(!double.IsFinite(curve.FirstParameter)||!double.IsFinite(curve.LastParameter))continue;
                        var point=item.Shape.EvaluateEdge((curve.FirstParameter+curve.LastParameter)/2).Point;
                        var pixel=viewport.WorldToScreen(new(point.X,point.Y,point.Z));
                        viewport.PointerPressed(0,pixel.X,pixel.Y,0);viewport.PointerReleased(0,pixel.X,pixel.Y,0);
                        if(editor.ExactEdge?.FullTopologyIndex!=item.Index)continue;
                        picked=true;break;
                    }
                    if(picked)break;
                }
                Check(picked,"Native Viewer generated-edge pick maps to exact full topology index");
                editor.Operation=LocalFeatureOperation.Chamfer;editor.SelectionKind=TopologyKind.Face;
                viewport.SetProjection(CadProjection.Top);viewport.FitAll();await Idle();
                var face=viewport.WorldToScreen(new(5,10,30));
                viewport.PointerPressed(0,face.X,face.Y,0);viewport.PointerReleased(0,face.X,face.Y,0);
                Check(editor.SupportFace is {Kind:HistoryShapeKind.Face}&&editor.ExactEdge is not null,
                    "Native support-face pick retains the exact edge");
                Check(ReferenceEquals(edited,session.Snapshot),"Generated topology pick is uncommitted");
            }
            finally{foreach(var edge in edges)edge.Dispose();}
            dialog.Close();
        });
        Check(ReferenceEquals(edited,session.Snapshot),"Generated topology selection cancellation keeps document unchanged");
        var storage=services.GetRequiredService<IDocumentStorage>();var path=Path.Combine(output,"local-feature.cadoryx");await session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,session.Assets))Check(loaded.Snapshot.Features[feature.Id].Recipe==edited.Features[feature.Id].Recipe,"Edited local recipe roundtrip");
        foreach(var extension in new[]{"step","iges","stl"})await services.GetRequiredService<IGeometryKernel>().ExportAsync(edited,session.Assets,Path.Combine(output,"local-feature."+extension));
        await File.WriteAllTextAsync(Path.Combine(output,"local-feature-result.json"),JsonSerializer.Serialize(new{passed=true,facePicking=true,edgePicking=true,referenceReselection=true,previewCancel=true,parameterEdit=true,twoDistanceChamfer=true,exactUndoRedo=true,volume=5970,cultures=new[]{"en-US","zh-CN","ja-JP"}}));
    }
    private static IEnumerable<Cadoryx.ViewModels.Toolboxes.ModelTreeItemViewModel> All(IEnumerable<Cadoryx.ViewModels.Toolboxes.ModelTreeItemViewModel> items)
    {foreach(var item in items){yield return item;foreach(var child in All(item.Children))yield return child;}}
    private static async Task Operate(Func<Task> open,Func<LocalFeatureWindow,Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _=System.Windows.Application.Current.Dispatcher.InvokeAsync(async()=>
        {
            LocalFeatureWindow? dialog=null;
            try
            {
                await Idle();dialog=System.Windows.Application.Current.Windows.OfType<LocalFeatureWindow>().Single();
                Check(dialog.IsVisible,"Local window did not open: "+((MainWindowViewModel)System.Windows.Application.Current.MainWindow.DataContext).StatusText);
                await action(dialog);completion.TrySetResult();
            }
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
