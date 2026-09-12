using System.Windows;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.ViewModels.Settings;
using Cadoryx.wpf.Views.Dialogs;
using Cadoryx.wpf.Views.Settings;
using MaterialDesignThemes.Wpf;

namespace Cadoryx.wpf.Services.Dialogs;

/// <summary>
/// WPF 对话框实现。所有 UI 操作都切回应用 Dispatcher，调用方可以安全地从后台任务使用它。
/// </summary>
internal sealed class DialogService : IDialogService, IApplicationSettingsDialogService
{
    private readonly IApplicationSettingsStore _settingsStore;
    private readonly ICadMessageLog _messageLog;

    public DialogService(
        IApplicationSettingsStore settingsStore,
        ICadMessageLog messageLog)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _messageLog = messageLog ?? throw new ArgumentNullException(nameof(messageLog));
    }

    public void Close(string dialogIdentifier = ViewServiceIdentifiers.RootDialogHost)
    {
        var identifier = NormalizeIdentifier(dialogIdentifier);
        InvokeOnUi(() => CloseCurrentDialog(identifier));
    }

    public Task ShowOrReplaceMessageDialogAsync(
        string message,
        string header = "",
        string dialogIdentifier = ViewServiceIdentifiers.RootDialogHost)
    {
        ArgumentNullException.ThrowIfNull(message);
        InvokeOnUi(() => _messageLog.Add(
            message,
            CadMessageLevel.Error,
            string.IsNullOrWhiteSpace(header) ? "Dialog" : header));
        return ShowMessageAsync(header, message, MessageDialogButton.OK, dialogIdentifier);
    }

    public async Task<bool> ShowOrReplaceMessageDialogWithCancelAsync(
        string message,
        string header = "",
        string dialogIdentifier = ViewServiceIdentifiers.RootDialogHost)
    {
        ArgumentNullException.ThrowIfNull(message);
        var result = await ShowMessageAsync(
            header,
            message,
            MessageDialogButton.OKCancel,
            dialogIdentifier);

        return result is string value && value == bool.TrueString;
    }

    public IDisposable ShowProgressBarDialog(
        string dialogIdentifier = ViewServiceIdentifiers.RootDialogHost)
    {
        var identifier = NormalizeIdentifier(dialogIdentifier);

        InvokeOnUi(() =>
        {
            var progressDialog = new ProgressDialog();
            var session = DialogHost.GetDialogSession(identifier);
            if (session is not null)
            {
                session.UpdateContent(progressDialog);
            }
            else
            {
                _ = DialogHost.Show(progressDialog, identifier);
            }
        });

        return new DeferredScope(() => Close(identifier));
    }

    public async Task<bool> ShowExitConfirmation(
        string dialogIdentifier = ViewServiceIdentifiers.RootDialogHost)
    {
        var result = await ShowReplacingCurrentAsync(
            () => new ExitConfirmationDialog(),
            dialogIdentifier);
        return result is string value && value == bool.TrueString;
    }

    public void ShowApplicationSettingsDialog(
        CadoryxApplicationSettings settings,
        Action<CadoryxApplicationSettings> applySettings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(applySettings);

        InvokeOnUi(() =>
        {
            var dialog = new ApplicationSettingsWindow
            {
                Owner = System.Windows.Application.Current?.MainWindow,
                DataContext = new ApplicationSettingsViewModel(
                    settings,
                    _settingsStore,
                    applySettings)
            };
            dialog.ShowDialog();
        });
    }

    // 保留之前设置服务的契约，已有调用方无需立即迁移。
    public void Show(
        CadoryxApplicationSettings settings,
        Action<CadoryxApplicationSettings> applySettings)
        => ShowApplicationSettingsDialog(settings, applySettings);

    private static Task<object?> ShowMessageAsync(
        string header,
        string message,
        MessageDialogButton buttonType,
        string dialogIdentifier)
    {
        return ShowReplacingCurrentAsync(
            () => new MessageDialog(header, message, buttonType),
            dialogIdentifier);
    }

    private static Task<object?> ShowReplacingCurrentAsync(
        Func<object> dialogFactory,
        string dialogIdentifier)
    {
        var identifier = NormalizeIdentifier(dialogIdentifier);

        return InvokeOnUiAsync(async () =>
        {
            CloseCurrentDialog(identifier);
            // DialogHost.Close 是异步完成的，给它一个调度机会再显示新内容。
            await Task.Yield();
            return await DialogHost.Show(dialogFactory(), identifier);
        });
    }

    private static void CloseCurrentDialog(string identifier)
    {
        DialogHost.GetDialogSession(identifier)?.Close();
    }

    private static string NormalizeIdentifier(string? dialogIdentifier)
        => string.IsNullOrWhiteSpace(dialogIdentifier)
            ? ViewServiceIdentifiers.RootDialogHost
            : dialogIdentifier;

    private static void InvokeOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    private static Task<T> InvokeOnUiAsync<T>(Func<Task<T>> action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            return action();

        return dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    private sealed class DeferredScope : IDisposable
    {
        private readonly Action _disposeAction;
        private int _disposed;

        public DeferredScope(Action disposeAction)
            => _disposeAction = disposeAction ?? throw new ArgumentNullException(nameof(disposeAction));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _disposeAction();
        }
    }
}
