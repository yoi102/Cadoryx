using System.Windows;
using Cadoryx.ViewModels.Toolboxes;
namespace Cadoryx.wpf.Views.Toolboxes;
public partial class ModelTreeToolboxView
{
    public ModelTreeToolboxView()=>InitializeComponent();
    private void OnSelectedItemChanged(object sender,RoutedPropertyChangedEventArgs<object> e)
    {if(DataContext is ModelTreeToolboxViewModel vm&&e.NewValue is ModelTreeItemViewModel item)vm.Select(item);}
}
