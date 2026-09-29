using System.Windows;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.ViewModels.Settings;
using Cadoryx.wpf.Views.Dialogs;
using Cadoryx.wpf.Views.Settings;
using MaterialDesignThemes.Wpf;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Threading;
using Strings = Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.wpf.Services.Dialogs;

/// <summary>
/// WPF 对话框实现。所有 UI 操作都切回应用 Dispatcher，调用方可以安全地从后台任务使用它。
/// </summary>
public sealed class DialogService : IDialogService, IApplicationSettingsDialogService
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
        string dialogIdentifier = ViewServiceIdentifiers.RootDialogHost,
        bool showCancelButton = false, Action? cancel = null, string? message = null)
    {
        ProgressScope? scope = null;
        InvokeOnUi(() => scope = new ProgressScope(NormalizeIdentifier(dialogIdentifier), showCancelButton, cancel, message));
        return scope!;
    }

    public Task RunWithProgressAsync(Func<CancellationToken, Task> operation, bool canCancel = false,
        string? message = null, string dialogIdentifier = ViewServiceIdentifiers.RootDialogHost)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return InvokeOnUiAsync(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            await using var scope = new ProgressScope(NormalizeIdentifier(dialogIdentifier), canCancel, cancellation.Cancel, message);
            await scope.WaitForOpenAsync();
            await Dispatcher.Yield(DispatcherPriority.Background);
            cancellation.Token.ThrowIfCancellationRequested();
            await operation(canCancel ? cancellation.Token : CancellationToken.None);
            return true;
        });
    }

    public Task<object?> ShowDialogAsync(
        object content,
        string dialogIdentifier = ViewServiceIdentifiers.RootDialogHost)
    {
        ArgumentNullException.ThrowIfNull(content);
        return ShowReplacingCurrentAsync(() => content, dialogIdentifier);
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
                DataContext = new ApplicationSettingsViewModel(
                    settings,
                    _settingsStore,
                    applySettings)
            };
            _ = ShowDialogAsync(dialog);
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

    private sealed class ProgressScope : IDisposable, IAsyncDisposable
    {
        private readonly ProgressDialog dialog;
        private readonly TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task completion;
        private DialogSession? session;
        private bool disposed;
        public ProgressScope(string identifier, bool showCancelButton, Action? cancel, string? message)
        {
            if (showCancelButton && cancel is null) throw new ArgumentException("A visible cancel button requires a cancellation callback.", nameof(cancel));
            if (DialogHost.GetDialogSession(identifier) is not null) throw new InvalidOperationException("A dialog is already open.");
            dialog = new ProgressDialog { ShowCancelButton = showCancelButton, Message = message ?? Strings.ProgressWorking };
            bool requested = false;
            RelayCommand? command = null;
            command = new RelayCommand(() =>
            {
                if (requested || disposed) return;
                requested = true;command!.NotifyCanExecuteChanged();
                dialog.Message = Strings.ProgressCancelling;
                cancel!();
            }, () => showCancelButton && !requested && !disposed);
            dialog.CancelCommand = command;
            completion = DialogHost.Show(dialog, identifier, (_, e) =>
            {
                session = e.Session;opened.TrySetResult();
                if (disposed && !session.IsEnded) session.Close();
            }, (_, e) => { if (!disposed && ReferenceEquals(e.Session.Content, dialog)) e.Cancel(); });
            _ = completion.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        public async Task WaitForOpenAsync()
        {
            if (await Task.WhenAny(opened.Task, completion) == completion)
            { await completion; throw new InvalidOperationException("Progress dialog closed before opening."); }
            await opened.Task;
        }
        public void Dispose() => InvokeOnUi(() =>
        {
            if (disposed) return;
            disposed = true;
            if (session is { IsEnded: false } && ReferenceEquals(session.Content, dialog)) session.Close();
        });
        public async ValueTask DisposeAsync()
        {
            bool replaced = session is { IsEnded: false } && !ReferenceEquals(session.Content, dialog);
            Dispose();
            if (!replaced) await completion;
        }
    }
}
