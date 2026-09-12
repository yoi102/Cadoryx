using Cadoryx.ViewModels;
namespace Cadoryx.wpf.Views.Dialogs;
public partial class RecoveryDialog
{
    public RecoveryDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (DataContext is RecoveryCenterViewModel vm) await vm.RefreshCommand.ExecuteAsync(null); };
    }
}
