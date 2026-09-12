using Cadoryx.Editor;
namespace Cadoryx.wpf.Services.Application;
public sealed class WpfSessionDispatcher : ISessionDispatcher
{
    public Task InvokeAsync(Action action)
    {
        var dispatcher=System.Windows.Application.Current.Dispatcher;
        if(dispatcher.CheckAccess()){action();return Task.CompletedTask;}
        return dispatcher.InvokeAsync(action).Task;
    }
}
