using System.Windows;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.wpf.Views.Dialogs;
namespace Cadoryx.wpf.Services.Dialogs;

public sealed class RecoveryDialogService(IRecoveryStore store) : IRecoveryDialogService
{
    private RecoveryWindow? window;
    public void Show()
    {
        if (window is not null) { window.Activate(); return; }
        var owner = System.Windows.Application.Current.MainWindow;
        var workspace = (MainWindowViewModel)owner.DataContext;
        var model = new RecoveryCenterViewModel(store, workspace.RestoreRecoveryAsync, entry =>
            ConfirmationWindow.Ask(window ?? owner, Strings.RecoveryTitle, string.Format(Strings.RecoveryDiscardQuestion, entry.Name),
                Strings.RecoveryDiscard) == CadConfirmationResult.Accept);
        window = new RecoveryWindow { Owner = owner, DataContext = model };
        window.Closed += (_, _) => window = null;
        window.Show();
    }
}
