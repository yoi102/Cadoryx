using Cadoryx.ViewModels;
using Cadoryx.wpf.Views.Dialogs;
namespace Cadoryx.wpf.Services.Dialogs;

public sealed class DocumentResourcesDialogService(DialogService dialogs) : IDocumentResourcesDialogService
{
    public async Task ShowAsync(DocumentResourcesViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var dialog=new DocumentResourcesDialog{DataContext=model};
        await dialogs.ShowDialogAsync(dialog);
    }
}
