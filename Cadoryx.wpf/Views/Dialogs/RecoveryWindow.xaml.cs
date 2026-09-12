using System.Windows;
using Cadoryx.ViewModels;
namespace Cadoryx.wpf.Views.Dialogs;
public partial class RecoveryWindow : MahApps.Metro.Controls.MetroWindow
{
    public RecoveryWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (DataContext is RecoveryCenterViewModel vm) await vm.RefreshCommand.ExecuteAsync(null); };
        Closing += (_, e) => { if (DataContext is RecoveryCenterViewModel { IsBusy: true }) e.Cancel = true; };
    }
    private void LaterClicked(object sender, RoutedEventArgs e) => Close();
}
