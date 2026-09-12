using System.Windows.Threading;
using Cadoryx.Editor;
using Cadoryx.ViewModels;
namespace Cadoryx.wpf.Services.Application;

public sealed class RecoveryHost(DocumentRecoveryService recovery) : IDisposable
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource lifetime = new();
    private MainWindowViewModel? workspace;
    private Task tick = Task.CompletedTask;
    public void Start(MainWindowViewModel workspace)
    {
        this.workspace = workspace;
        recovery.Failed += OnFailure; timer.Tick += OnTick; timer.Start();
    }
    private void OnFailure(object? sender, Exception ex)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => workspace?.ReportRecoveryFailure(ex));
    }
    private void OnTick(object? sender, EventArgs e)
    {
        if (!tick.IsCompleted) return;
        tick = CheckpointAsync();
    }
    private async Task CheckpointAsync()
    {
        try { await recovery.CheckpointAllAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { workspace?.ReportRecoveryFailure(ex); }
    }
    public async Task StopAsync()
    {
        timer.Stop(); lifetime.Cancel(); await tick; await recovery.DrainAsync();
    }
    public void Dispose()
    {
        timer.Stop(); timer.Tick -= OnTick; recovery.Failed -= OnFailure; lifetime.Cancel();
    }
}
