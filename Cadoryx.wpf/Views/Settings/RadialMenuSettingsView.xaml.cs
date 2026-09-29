using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Cadoryx.ViewModels.Settings;
using GongSolutions.Wpf.DragDrop;

namespace Cadoryx.wpf.Views.Settings;

public partial class RadialMenuSettingsView : UserControl, IDropTarget, IDragSource
{
    private const string DeleteTargetTag = "CadoryxRadialDeleteTarget";
    private sealed record SlotDrag(RadialSlotViewModel Source, RadialActionOption Action);

    public RadialMenuSettingsView() => InitializeComponent();

    void IDropTarget.DragOver(IDropInfo info)
    {
        if (IsDeleteTarget(info))
        {
            info.Effects = SourceSlot(info) is null ? DragDropEffects.None : DragDropEffects.Move;
            if (info.Effects != DragDropEffects.None) info.DropTargetAdorner = DropTargetAdorners.Highlight;
            return;
        }
        info.Effects = DragAction(info) is not null && TargetSlot(info) is not null
            ? DragDropEffects.Copy : DragDropEffects.None;
        if (info.Effects != DragDropEffects.None) info.DropTargetAdorner = DropTargetAdorners.Highlight;
    }

    void IDropTarget.Drop(IDropInfo info)
    {
        if (IsDeleteTarget(info)) { SourceSlot(info)?.Clear(); return; }
        if (DragAction(info) is { } action && TargetSlot(info) is { } slot)
            slot.SelectedAction = action;
    }

    private static RadialSlotViewModel? TargetSlot(IDropInfo info) =>
        info.TargetItem as RadialSlotViewModel ??
        (info.VisualTargetItem as FrameworkElement)?.DataContext as RadialSlotViewModel;
    private static RadialSlotViewModel? SourceSlot(IDropInfo info) =>
        (info.Data as SlotDrag)?.Source ?? info.DragInfo?.SourceItem as RadialSlotViewModel;
    private static RadialActionOption? DragAction(IDropInfo info) => info.Data switch
    {
        RadialActionOption action => action,
        SlotDrag slot => slot.Action,
        _ => null
    };

    private bool IsDeleteTarget(IDropInfo info)
    {
        for (DependencyObject? element = info.VisualTarget; element is not null; element = GetAncestor(element))
            if (element is FrameworkElement { Tag: DeleteTargetTag }) return true;
        if (info.VisualTarget is not UIElement target) return false;
        var point = target.TranslatePoint(info.DropPosition, RadialCanvas);
        var dx = point.X - 174; var dy = point.Y - 174;
        return dx * dx + dy * dy <= 46 * 46;
    }
    private static DependencyObject? GetAncestor(DependencyObject element) => element is Visual or Visual3D
        ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    bool IDragSource.CanStartDrag(IDragInfo info) => info.SourceItem is RadialSlotViewModel;
    void IDragSource.StartDrag(IDragInfo info)
    {
        if (info.SourceItem is not RadialSlotViewModel slot) return;
        info.Data = new SlotDrag(slot, slot.SelectedAction);
        info.Effects = DragDropEffects.Copy | DragDropEffects.Move;
    }
    void IDragSource.Dropped(IDropInfo info) { }
    void IDragSource.DragDropOperationFinished(DragDropEffects result, IDragInfo info) { }
    void IDragSource.DragCancelled() { }
    bool IDragSource.TryCatchOccurredException(Exception exception) => false;
}
