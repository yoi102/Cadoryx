using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public partial class MainWindowViewModel
{
    public event EventHandler<CadDocumentViewModel>? TechnicalDrawingRequested;
    [RelayCommand(CanExecute=nameof(CanEditDocument))]
    private void OpenTechnicalDrawing()
    {
        if(ActiveDocument is {IsReadOnly:false} document)
            TechnicalDrawingRequested?.Invoke(this,document);
    }
}
