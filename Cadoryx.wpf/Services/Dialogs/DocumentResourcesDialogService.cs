using Cadoryx.ViewModels;
using Cadoryx.wpf.Views.Dialogs;
namespace Cadoryx.wpf.Services.Dialogs;

public sealed class DocumentResourcesDialogService : IDocumentResourcesDialogService
{
    public void Show(DocumentResourcesViewModel model)
    {
        var dialog=new DocumentResourcesWindow{Owner=System.Windows.Application.Current.MainWindow,DataContext=model};
        dialog.ShowDialog();
    }
}
