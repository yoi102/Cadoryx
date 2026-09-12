using Cadoryx.ViewModels.Services.Platform.Settings;

namespace Cadoryx.ViewModels.Services.Platform;

/// <summary>
/// 应用层对话框抽象。ViewModel 只依赖这个接口，不直接引用 WPF 或 MaterialDesign。
/// </summary>
public interface IDialogService
{
    void Close(string dialogIdentifier = ViewServiceIdentifiers.RootDialogHost);

    Task ShowOrReplaceMessageDialogAsync(
        string message,
        string header = "",
        string dialogIdentifier = ViewServiceIdentifiers.RootDialogHost);

    Task<bool> ShowOrReplaceMessageDialogWithCancelAsync(
        string message,
        string header = "",
        string dialogIdentifier = ViewServiceIdentifiers.RootDialogHost);

    IDisposable ShowProgressBarDialog(
        string dialogIdentifier = ViewServiceIdentifiers.RootDialogHost);

    Task<bool> ShowExitConfirmation(
        string dialogIdentifier = ViewServiceIdentifiers.RootDialogHost);

    void ShowApplicationSettingsDialog(
        CadoryxApplicationSettings settings,
        Action<CadoryxApplicationSettings> applySettings);
}
