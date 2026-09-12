using System.Windows;
using Cadoryx.ViewModels;
using MahApps.Metro.Controls;
namespace Cadoryx.wpf.Views.Dialogs;
public partial class DocumentResourcesWindow : MetroWindow
{
    public DocumentResourcesWindow()
    {
        InitializeComponent();
        Closing+=(_,e)=>{if(DataContext is DocumentResourcesViewModel{IsBusy:true})e.Cancel=true;};
    }
    private void CloseClicked(object sender,RoutedEventArgs e)=>Close();
}
