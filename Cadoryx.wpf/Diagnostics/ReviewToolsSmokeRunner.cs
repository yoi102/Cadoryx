using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock.Core;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Controls;
using Cadoryx.wpf.Views;

namespace Cadoryx.wpf.Diagnostics;

internal static class ReviewToolsSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,MainWindowViewModel workspace,string output)
    {
        var doc=workspace.ActiveDocument!;var body=doc.Session.Snapshot.Bodies.Values.First();
        await doc.Session.ExecuteAsync(new EditDocumentCommand("Review fixture",snapshot=>
        {
            var root=(AssemblyDefinition)snapshot.Definitions[snapshot.RootAssemblyId];
            return snapshot with{Definitions=snapshot.Definitions.SetItem(root.Id,root with{Children=root.Children.Add(
                new(ComponentSlotId.New(),body.PartId,"Review second instance",RigidTransform3d.Translate(100,0,0)))})};
        }));
        await Idle();var state=doc.Session.Snapshot.StateId;
        var targets=doc.Scene.Items.Where(i=>i.BodyId==body.Id).Select(i=>new SelectionTarget(i.Path,i.BodyId,i.Geometry.Revision)).ToArray();
        doc.Selection.Replace(targets);workspace.LayoutService.ShowAnchorable(workspace.Properties);await Idle();
        var review=Find<DocumentReviewView>(window).Single();
        await doc.Review.MeasureCommand.ExecuteAsync(null);await Idle();
        Require(doc.Review.Result?.Distance is {DistanceMm:>0},"Exact distance did not reach the properties panel.");
        Require(review.MeasureButton.Command==doc.Review.MeasureCommand,"Exact measurement command binding failed.");
        var ribbon=Find<MainRibbonView>(window).Single();var selectedTab=ribbon.RibbonTabs.SelectedIndex;ribbon.ViewTab.IsSelected=true;await Idle();var view=Find<ViewportView>(window).Single();ribbon.SplitCheck.SetCurrentValue(CheckBox.IsCheckedProperty,true);await Idle();
        var hosts=Find<OcctViewportHost>(view).ToArray();Require(hosts.Length==2&&hosts.All(h=>h.Viewport is not null),"Two native views did not initialize.");
        await RibbonNavigationSmokeRunner.RunAsync(window,workspace,output,hosts);
        var primary=hosts[0].Viewport!;var secondary=hosts[1].Viewport!;
        var before=primary.CaptureCamera();secondary.SetProjection(CadProjection.Right);secondary.FitAll();var second=secondary.CaptureCamera();
        Require((before.Eye-primary.CaptureCamera().Eye).Length<1e-7,"Changing second camera changed first view.");
        doc.Selection.Replace([targets[1]]);doc.Review.IsolateCommand.Execute(null);await Idle();
        Require(primary.VisibleBodyCount==1&&secondary.VisibleBodyCount==1,"Isolation is not synchronized by exact instance.");
        doc.Review.FocusCommand.Execute(null);await Task.Delay(420);await Idle();
        Require(primary.CaptureCamera().Target.X>90,"Focus did not use selected world placement.");
        doc.Review.ShowAllCommand.Execute(null);await Idle();primary.FitAll();
        primary.SaveScreenshot(Path.Combine(output,"review-unclipped.png"));
        doc.Review.SectionEnabled=true;doc.Review.SectionAxis=SectionAxis.Z;doc.Review.SectionOffsetMm=body.Geometry.Bounds.Max.Z/2;
        review.ApplySectionButton.Command.Execute(null);await Idle();
        Require(primary.SectionPlaneCount==1&&secondary.SectionPlaneCount==1,"Section plane not applied to both viewers.");
        primary.SaveScreenshot(Path.Combine(output,"review-section.png"));
        Require(!SHA256.HashData(File.ReadAllBytes(Path.Combine(output,"review-unclipped.png"))).SequenceEqual(
            SHA256.HashData(File.ReadAllBytes(Path.Combine(output,"review-section.png")))),"Clipping did not change rendered pixels.");
        doc.Review.SlabEnabled=true;doc.Review.SlabThicknessMm=4;doc.Review.ApplySection();await Idle();
        Require(primary.SectionPlaneCount==2&&secondary.SectionPlaneCount==2,"Slab planes missing.");
        primary.SaveScreenshot(Path.Combine(output,"review-slab.png"));secondary.SaveScreenshot(Path.Combine(output,"review-secondary.png"));
        doc.Review.ResetSectionCommand.Execute(null);Require(primary.SectionPlaneCount==0,"Section planes leaked after reset.");
        ribbon.SplitCheck.SetCurrentValue(CheckBox.IsCheckedProperty,false);await Idle();
        Require(Find<OcctViewportHost>(view).Count()==1,$"Secondary host leaked after disabling dual view: split={doc.Review.SplitView}, checked={ribbon.SplitCheck.IsChecked}, hosts={Find<OcctViewportHost>(view).Count()}.");
        ribbon.SplitCheck.SetCurrentValue(CheckBox.IsCheckedProperty,true);await Idle();
        var reopened=Find<OcctViewportHost>(view).Last().Viewport!.CaptureCamera();
        Require((reopened.Eye-second.Eye).Length<1e-6&&(reopened.Up-second.Up).Length<1e-6,"Secondary camera was not preserved.");
        ribbon.SplitCheck.SetCurrentValue(CheckBox.IsCheckedProperty,false);await Idle();
        Require(doc.Session.Snapshot.StateId==state,"Review controls mutated document state.");
        doc.Selection.Replace([]);await doc.Session.UndoAsync();ribbon.RibbonTabs.SelectedIndex=selectedTab;await Idle();
        await File.WriteAllTextAsync(Path.Combine(output,"review-tools-result.txt"),
            "PASS: exact distance/property command binding; two independent native cameras; shared exact-instance isolation/focus; half-space and slab rendering; clip reset; dual-view disable/reopen camera continuity; no document mutation.");
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static async Task Idle(){await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(140);}
    private static IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T item)yield return item;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Find<T>(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
}
