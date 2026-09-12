using System.Windows;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.wpf.Views.Dialogs;
namespace Cadoryx.wpf.Services.Dialogs;

public sealed class RecoveryDialogService(IRecoveryStore store,DialogService dialogs) : IRecoveryDialogService
{
    private Task? showing;
    public Task ShowAsync()
    {
        if (showing is { IsCompleted: false }) return Task.CompletedTask;
        var owner = System.Windows.Application.Current.MainWindow;
        var workspace = (MainWindowViewModel)owner.DataContext;
        var model = new RecoveryCenterViewModel(store, workspace.RestoreRecoveryAsync, async entry =>
        {
            var confirmation = new ConfirmationDialog(Strings.RecoveryTitle, string.Format(Strings.RecoveryDiscardQuestion, entry.Name), Strings.RecoveryDiscard);
            var result = await dialogs.ShowDialogAsync(confirmation);
            return result is string value && value == ConfirmationDialogResult.Accept;
        });
        var recovery = new RecoveryDialog { DataContext = model };
        showing = ShowCoreAsync(recovery);
        return Task.CompletedTask;
    }

    private async Task ShowCoreAsync(RecoveryDialog dialog)
    {
        try { await dialogs.ShowDialogAsync(dialog); }
        finally { showing = null; }
    }
}
