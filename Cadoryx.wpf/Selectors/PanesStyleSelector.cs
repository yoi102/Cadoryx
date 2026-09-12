using System.Windows;
using System.Windows.Controls;
using AvalonDock.Controls;

namespace Cadoryx.wpf.Selectors;

public sealed class PanesStyleSelector : StyleSelector
{
    public Style? ToolboxStyle { get; set; }

    public Style? DocumentStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container)
    {
        return container switch
        {
            LayoutAnchorableItem => ToolboxStyle,
            LayoutDocumentItem => DocumentStyle,
            _ => base.SelectStyle(item, container)
        };
    }
}
