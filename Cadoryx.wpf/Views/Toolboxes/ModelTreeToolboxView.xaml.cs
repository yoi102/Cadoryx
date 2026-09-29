using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cadoryx.Db;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels.Toolboxes;
namespace Cadoryx.wpf.Views.Toolboxes;
public partial class ModelTreeToolboxView
{
    private ModelTreeToolboxViewModel? observed;
    private long revealRequest;
    public ModelTreeToolboxView()
    {
        InitializeComponent();
        // Keep room for the model tree when Ribbon/docking panes reduce the available height.
        SizeChanged+=(_,_)=>HistoryScroll.MaxHeight=Math.Clamp(ActualHeight*.24,48,140);
        Loaded+=(_,_)=>Observe(DataContext as ModelTreeToolboxViewModel);
        Unloaded+=(_,_)=>Observe(null);
        DataContextChanged+=(_,_)=>Observe(IsLoaded?DataContext as ModelTreeToolboxViewModel:null);
        ModelTree.SizeChanged+=(_,e)=>
        {
            if(e.HeightChanged&&observed is not null&&ModelTree.SelectedItem is ModelTreeItemViewModel row)
                OnReveal(observed,row);
        };
    }
    private void Observe(ModelTreeToolboxViewModel? vm)
    {
        revealRequest++;
        if(observed is not null)observed.RevealRequested-=OnReveal;
        observed=vm;
        if(observed is not null)observed.RevealRequested+=OnReveal;
    }
    private void OnReveal(object? sender,ModelTreeItemViewModel row)
    {
        var request=++revealRequest;
        _=Dispatcher.InvokeAsync(()=>
        {
            if(request!=revealRequest||!IsLoaded||sender!=observed||row.Path is not {} path)return;
            ItemsControl owner=ModelTree;var prefix=new OccurrencePath(path.DocumentId,[]);
            TreeViewItem? container=null;
            foreach(var id in path.Slots)
            {
                prefix=prefix.Append(id);
                var item=owner.Items.OfType<ModelTreeItemViewModel>().FirstOrDefault(n=>Equals(n.Path,prefix)&&n.Target is null);
                if(item is null||(container=Realize(owner,item)) is null)return;
                if(!ReferenceEquals(item,row)){container.IsExpanded=true;container.UpdateLayout();}
                owner=container;
            }
            if(row.Target is not null)container=Realize(owner,row);
            container?.BringIntoView();
        },DispatcherPriority.Loaded);
    }
    private static TreeViewItem? Realize(ItemsControl owner,object item)
    {
        owner.ApplyTemplate();owner.UpdateLayout();
        if(owner.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem)
        {
            var panel=FindItemsPanel(owner,owner);
            int index=owner.Items.IndexOf(item);if(index<0)return null;
            panel?.BringIndexIntoViewPublic(index);owner.UpdateLayout();
        }
        var container=owner.ItemContainerGenerator.ContainerFromItem(item) as TreeViewItem;
        container?.BringIntoView();owner.UpdateLayout();return container;
    }
    private static VirtualizingPanel? FindItemsPanel(DependencyObject node,ItemsControl owner)
    {
        if(node is VirtualizingPanel panel&&ReferenceEquals(ItemsControl.GetItemsOwner(panel),owner))return panel;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
            if(FindItemsPanel(VisualTreeHelper.GetChild(node,i),owner) is {} found)return found;
        return null;
    }
    private void OnTreeItemSelected(object sender,RoutedEventArgs e)
    {if(ReferenceEquals(sender,e.OriginalSource)&&sender is FrameworkElement row)row.BringIntoView();}
    private void OnSelectedItemChanged(object sender,RoutedPropertyChangedEventArgs<object> e)
    {if(DataContext is ModelTreeToolboxViewModel vm&&e.NewValue is ModelTreeItemViewModel item)vm.Select(item);}
    private void OnFeatureContextMenuOpening(object sender,ContextMenuEventArgs e)
    {
        if(e.OriginalSource is not DependencyObject source||DataContext is not ModelTreeToolboxViewModel vm)return;
        var row=FindTreeRow(source);
        if(row?.DataContext is not ModelTreeItemViewModel {Feature:{}} feature)return;
        var suppressed=feature.IsSuppressed;
        var menu=new ContextMenu();
        var action=new MenuItem{Header=Strings.ResourceManager.GetString(suppressed?"RestoreFeature":"SuppressFeature",Strings.Culture)};
        action.Click+=async (_,_)=>await vm.SetFeatureSuppressionAsync(feature,!suppressed);
        menu.Items.Add(action);
        row.ContextMenu=menu;
    }
    private static TreeViewItem? FindTreeRow(DependencyObject source)
    {
        while(source is not null)
        {
            if(source is TreeViewItem row)return row;
            source=VisualTreeHelper.GetParent(source);
        }
        return null;
    }
}
