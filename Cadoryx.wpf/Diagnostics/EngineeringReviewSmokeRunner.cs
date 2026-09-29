using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;
internal static class EngineeringReviewSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,MainWindowViewModel workspace,IServiceProvider services,string output)
    {
        var doc=workspace.ActiveDocument!;var before=doc.Session.Snapshot;var review=doc.Review;
        var source=doc.Scene.Items.First();
        await doc.Session.ExecuteAsync(new CreateSectionCommand([new(source.Path,source.BodyId,source.Geometry,source.WorldTransform)],
            new(Vector3d.UnitZ,(source.Geometry.Bounds.Min.Z+source.Geometry.Bounds.Max.Z)/2),"Associated filled section",true,SectionOutput.Faces));
        review.DimensionName="Length 20 mm";review.DimensionFirst="0, 0, 0";review.DimensionSecond="20, 0, 0";review.DimensionFlyout=8;
        await review.SaveDimensionCommand.ExecuteAsync(null);
        review.NewDimensionCommand.Execute(null);review.DimensionName="Angle 90°";review.AngleDimension=true;
        review.DimensionFirst="20, 0, 0";review.DimensionSecond="0, 0, 0";review.DimensionThird="0, 20, 0";
        await review.SaveDimensionCommand.ExecuteAsync(null);review.SplitView=true;await Idle();
        var hosts=Find<OcctViewportHost>(window).ToArray();
        Require(hosts.Length==2&&hosts.All(h=>h.Viewport?.DimensionCount==2),"Native dimensions missing in dual views.");
        hosts[0].Viewport!.FitAll();hosts[1].Viewport!.SetProjection(CadProjection.Top);hosts[1].Viewport!.FitAll();
        review.BookmarkName="Dimension review";await review.SaveBookmarkCommand.ExecuteAsync(null);
        Require(doc.Session.Snapshot.ReviewBookmarks.Count==1,"Review bookmark was not saved.");
        var primary=hosts[0].Viewport!.CaptureCamera();hosts[0].Viewport!.SetProjection(CadProjection.Right);
        review.RestoreBookmarkCommand.Execute(null);await Idle();
        Require((hosts[0].Viewport!.CaptureCamera().Eye-primary.Eye).Length<1e-7,"Bookmark camera restore failed.");
        foreach(var (host,index) in hosts.Select((h,i)=>(h,i)))host.Viewport!.SaveScreenshot(Path.Combine(output,$"engineering-review-{index}.png"));
        var storage=services.GetRequiredService<IDocumentStorage>();await storage.SaveAsync(doc.Session.Snapshot,doc.Session.Assets,Path.Combine(output,"engineering-review.cadoryx"));
        using(var loaded=await storage.LoadAsync(Path.Combine(output,"engineering-review.cadoryx"),doc.Session.Assets))
            Require(loaded.Snapshot.Dimensions.Count==2&&loaded.Snapshot.ReviewBookmarks.Count==1&&loaded.Snapshot.AssociatedSections.Count==1,"Review persistence failed.");
        var selected=review.DimensionRows.First();review.SelectedDimension=selected;await review.ToggleDimensionCommand.ExecuteAsync(null);
        Require(!doc.Session.Snapshot.Dimensions[selected.Id].IsVisible,"Dimension hide failed.");await doc.Session.UndoAsync();
        for(int i=0;i<8&&doc.Session.Snapshot.StateId!=before.StateId;i++)await doc.Session.UndoAsync();
        Require(doc.Session.Snapshot.StateId==before.StateId&&hosts.All(h=>h.Viewport?.DimensionCount==0),"Review undo leaked annotations.");
        review.SplitView=false;review.AngleDimension=false;review.SelectedDimension=null;await Idle();
        await File.WriteAllTextAsync(Path.Combine(output,"engineering-review-result.txt"),"PASS: solid/plane filled section, persistent source association, native length/angle in both viewports, edit/hide/undo/disposal, independent-camera bookmark capture/restore, MessagePack roundtrip.");
    }
    private static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
    private static async Task Idle(){await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(140);}
    internal static IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T item)yield return item;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Find<T>(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
}
