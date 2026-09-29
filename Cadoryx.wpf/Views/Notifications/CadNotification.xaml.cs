using System.Windows.Media;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaterialDesignThemes.Wpf;

namespace Cadoryx.wpf.Views.Toasts;

public partial class CadNotification : global::Notifications.Controls.Notification
{
    internal global::Notifications.INotificationHandle? Handle {get;set;}
    public CadNotification(NotificationModel model)
    {
        InitializeComponent();DataContext=model;
    }
    internal void Present()=>PlayEntranceAnimation();
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // Esc must not silently dismiss the only cancellation control for a running operation.
        if(e.Key==System.Windows.Input.Key.Escape&&DataContext is NotificationModel{IsWorking:true} model)
        {if(model.CancelCommand?.CanExecute(null)==true)model.CancelCommand.Execute(null);e.Handled=true;return;}
        base.OnKeyDown(e);
    }
}

public partial class NotificationModel : ObservableObject
{
    public required string Title {get;init;}
    [ObservableProperty] private string message="";
    public required Brush Accent {get;init;}
    public required PackIconKind Icon {get;init;}
    public bool IsWorking {get;init;}
    public IRelayCommand? CancelCommand {get;set;}
}
