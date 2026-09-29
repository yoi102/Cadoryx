using Cadoryx.ViewModels.Toolboxes;
using MahApps.Metro.Controls;

namespace Cadoryx.wpf.Views.Toolboxes;

public partial class AiAssistantSettingsWindow : MetroWindow
{
    public AiAssistantSettingsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as AiAssistantToolboxViewModel)?.BeginSettingsEdit();
        Closing += (_, _) => { if (DialogResult != true && DataContext is AiAssistantToolboxViewModel model) model.CancelSettingsEdit(); };
    }

    private void OnSaveClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not AiAssistantToolboxViewModel model) return;
        if (model.TrySaveSettings()) DialogResult = true;
    }
}
