namespace Cadoryx.ViewModels.Services.Platform.Notifications;

/// <summary>Non-modal task feedback; the operation must honor the supplied cancellation token.</summary>
public interface ICadNotificationService
{
    void SetAnchor(Settings.CadNotificationAnchor anchor);
    Task RunWithProgressAsync(Func<CancellationToken,Task> operation,string message,CancellationToken cancellationToken=default);
}
