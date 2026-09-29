using System.IO;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AvalonDock.Core;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.ViewModels;
using Cadoryx.wpf.Views.Toolboxes;
using MahApps.Metro.Controls;

namespace Cadoryx.wpf.Diagnostics;

internal static class WorkspaceToolsSmokeRunner
{
    internal static async Task RunAsync(MainWindow window,MainWindowViewModel workspace,string output)
    {
        var document=workspace.ActiveDocument!;var snapshot=document.Session.Snapshot;
        var body=snapshot.Bodies.Values.First();var tree=workspace.ModelTree;
        var slot=ComponentSlotId.New();var group=DefinitionId.New();
        await document.Session.ExecuteAsync(new EditDocumentCommand("Create searchable smoke assembly",s=>
        {
            var root=(AssemblyDefinition)s.Definitions[s.RootAssemblyId];
            var neighbors=Enumerable.Range(0,150).Select(i=>new ComponentSlot(ComponentSlotId.New(),group,
                "Search neighbor "+i,RigidTransform3d.Translate(i*100,0,0))).ToImmutableArray();
            return s with{Definitions=s.Definitions.Add(group,new AssemblyDefinition(group,"Search group",
                [new(ComponentSlotId.New(),body.PartId,"Nested part",RigidTransform3d.Translate(0,50,0))]))
                .SetItem(root.Id,root with{Children=root.Children.AddRange(neighbors).Add(
                    new(slot,group,"Search smoke instance",RigidTransform3d.Translate(16000,0,0)))})};
        }));
        await Idle();
        var view=Find<ModelTreeToolboxView>(window)??throw new InvalidOperationException("Missing model tree view.");
        if(!ReferenceEquals(view.SearchButton.Command,tree.SearchCommand)||
           !ReferenceEquals(view.LocateSelectionButton.Command,tree.LocateSelectionCommand)||
           view.SearchButton.Content is not FrameworkElement||view.LocateSelectionButton.Content is not FrameworkElement||
           view.SearchButton.ToolTip is not string||view.LocateSelectionButton.ToolTip is not string)
            throw new InvalidOperationException("Model-tree search and locate icon actions are not accessible or bound.");
        view.SearchInput.SetCurrentValue(TextBox.TextProperty,"Search smoke instance");
        await tree.SearchCommand.ExecuteAsync(null);await Idle();
        if(tree.SearchResults.Count!=1||view.SearchResultsList.Items.Count!=1)throw new InvalidOperationException("Search UI failed.");
        view.SearchResultsList.SelectedIndex=0;await Idle();
        view.LocateResultButton.Command.Execute(null);await Idle();
        if(document.Selection.Occurrence?.Slots.Last()!=slot)throw new InvalidOperationException("Search locate failed.");
        var targets=document.Session.Snapshot.EnumerateOccurrences().Where(o=>o.DefinitionId==body.PartId)
            .Select(o=>new SelectionTarget(o.Path,body.Id,body.Geometry.Revision)).TakeLast(2).ToArray();
        document.Selection.Replace(targets);await Idle();
        if(view.ModelTree.SelectedItem is not Cadoryx.ViewModels.Toolboxes.ModelTreeItemViewModel automatic||automatic.Target!=targets[0])
            throw new InvalidOperationException("Viewport selection did not select the corresponding model-tree row.");
        var selectionBefore=document.Selection.Items;
        tree.LocateSelectionCommand.Execute(null);await Idle();
        if(!document.Selection.Items.SequenceEqual(selectionBefore))throw new InvalidOperationException("Locate selection changed the multi-selection.");
        if(view.ModelTree.SelectedItem is not Cadoryx.ViewModels.Toolboxes.ModelTreeItemViewModel selected||selected.Target!=targets[0])
            throw new InvalidOperationException("Virtualized deep result did not become the visible tree selection.");
        if(workspace.Properties.Measurements.Count<7)throw new InvalidOperationException("Selection measurement UI failed.");
        workspace.LayoutService.ShowAnchorable(workspace.Properties);await Idle();
        if(Find<PropertiesToolboxView>(window) is not {} properties||!properties.IsVisible)
            throw new InvalidOperationException("Measurement properties panel is not visible.");
        view.HistoryExpander.IsExpanded=true;await Idle();
        view.HistoryBudgetInput.SetCurrentValue(NumericUpDown.ValueProperty,8d);await Idle();
        view.ApplyHistoryButton.Command.Execute(null);await Idle();
        if(document.Session.HistoryAssetBudgetBytes!=8L*1048576)throw new InvalidOperationException("History budget UI failed.");
        Capture(window,Path.Combine(output,"workspace-tools.png"));
        if(view.ModelTree.ActualHeight<60)throw new InvalidOperationException($"History settings consume the entire model tree: tree={view.ModelTree.ActualHeight}, view={view.ActualHeight}, history={view.HistoryExpander.ActualHeight}.");
        var selectedContainer=FindSelected(view.ModelTree)??throw new InvalidOperationException("Selected row was recycled out of view.");
        var selectedY=selectedContainer.TransformToAncestor(view.ModelTree).Transform(new Point(0,0)).Y;
        if(selectedY<0||selectedY>=view.ModelTree.ActualHeight-10)
            throw new InvalidOperationException("Selected row left the viewport after opening history settings.");
        await document.Session.ConfigureHistoryAsync(50,512L*1048576);
        document.History.AssetBudgetMiB=512;view.HistoryExpander.IsExpanded=false;
        view.SearchInput.SetCurrentValue(TextBox.TextProperty,"");
        document.Selection.Replace([]);
        await document.Session.UndoAsync(); // remove only this smoke's temporary assembly
        await Idle();
        await File.WriteAllTextAsync(Path.Combine(output,"workspace-tools-result.txt"),
            "PASS: model search input/results/locate bindings, preserving multi-selection while revealing, measurement rows, session history budget controls.");
    }
    private static async Task Idle(){await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(100);}
    private static T? Find<T>(DependencyObject root) where T:DependencyObject
    {
        if(root is T match)return match;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(Find<T>(VisualTreeHelper.GetChild(root,i)) is {} child)return child;
        return null;
    }
    private static TreeViewItem? FindSelected(DependencyObject root)
    {
        if(root is TreeViewItem {IsSelected:true} row)return row;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            if(FindSelected(VisualTreeHelper.GetChild(root,i)) is {} child)return child;
        return null;
    }
    private static void Capture(FrameworkElement element,string path)
    {
        element.UpdateLayout();var bitmap=new RenderTargetBitmap((int)element.ActualWidth,(int)element.ActualHeight,96,96,PixelFormats.Pbgra32);
        bitmap.Render(element);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=File.Create(path);encoder.Save(stream);
    }
}
