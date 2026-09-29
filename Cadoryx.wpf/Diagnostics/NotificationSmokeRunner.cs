using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.wpf.Services.Toasts;
using Cadoryx.wpf.Views.Toasts;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static class NotificationSmokeRunner
{
    internal static async Task RunAsync(IServiceProvider services,string output)
    {
        var notifications=services.GetRequiredService<CadNotificationService>();
        var log=services.GetRequiredService<ICadMessageLog>();
        var theme=services.GetRequiredService<IApplicationThemeService>();
        bool originalTheme=theme.IsDarkTheme;
        var originalAnchor=notifications.Anchor;
        var main=Application.Current.MainWindow;
        double originalLeft=main.Left,originalTop=main.Top;
        var originalState=main.WindowState;
        var shown=new List<CadNotification>();
        void OnShown(CadNotification card)=>shown.Add(card);
        notifications.Shown+=OnShown;
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? running=null;
        try
        {
            notifications.SetAnchor(CadNotificationAnchor.ApplicationWindow);
            await VerifyMotionAsync(notifications,log,output);
            foreach(bool dark in new[]{true,false})
            {
                theme.ApplyThemeLightDark(dark);
                foreach(var level in Enum.GetValues<CadMessageLevel>())
                {
                    int count=shown.Count;
                    log.Add("通知样式验证 · Information / 警告 / エラー",level,"Notification.Smoke");
                    await Idle();
                    Require(shown.Count==count+1,"Message log did not produce a notification.");
                    var card=shown[^1];
                    await Task.Delay(400); // Capture the settled appearance, not the entrance fade.
                    Require(card.IsVisible&&card.ActualWidth==360,"Custom notification is not visible.");
                    var border=(Border)card.Template.FindName("PART_AnimationRoot",card);
                    Require(border.CornerRadius.TopLeft==12&&border.BorderThickness.Left==1.5&&border.Background is SolidColorBrush,
                        "Rounded themed notification border missing.");
                    var overlay=Window.GetWindow(card);
                    Require(overlay is not null&&!overlay.ShowActivated&&!overlay.ShowInTaskbar,"Notification steals focus or adds a taskbar window.");
                    Capture(card,Path.Combine(output,$"notification-{(dark?"dark":"light")}-{level}.png"));
                    var close=(Button)card.Template.FindName("PART_CloseButton",card);
                    Require(close.IsVisible,"Ordinary notification has no close button.");
                    close.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    await card.Handle!.Completion;
                }
            }
            running=notifications.RunWithProgressAsync(async token=>{entered.SetResult(token);await release.Task;token.ThrowIfCancellationRequested();},"测试后台任务");
            var token=await entered.Task;await Idle();var progress=notifications.ActiveProgress!;
            Require(progress.IsPermanent&&!progress.ShowCloseButton,"Running task must stay visible.");
            var host=(NotificationHostWindow)Window.GetWindow(progress);
            Require(ReferenceEquals(host.Owner,main),"Application notification does not belong to the app.");
            main.WindowState=WindowState.Normal;await Idle();
            await WaitForApplicationPosition(host,main);
            main.Left+=24;main.Top+=16;await Idle();await WaitForApplicationPosition(host,main);
            main.WindowState=WindowState.Maximized;await Idle();await WaitForApplicationPosition(host,main);
            main.WindowState=WindowState.Minimized;await Idle();Require(!host.IsVisible,"Application notification did not hide on minimize.");
            main.WindowState=WindowState.Normal;await Idle();Require(host.IsVisible,"Notification did not restore with its app.");
            await WaitForApplicationPosition(host,main);
            notifications.SetAnchor(CadNotificationAnchor.WindowsDesktop);await Idle();
            Require(host.Owner is null&&host.Topmost&&ReferenceEquals(notifications.ActiveProgress,progress),"Desktop switch replaced the active operation.");
            // Other notifications can expire while this runs, changing the stack's top edge.
            // Desktop positioning promises a stable bottom-right anchor, not a fixed top-left.
            var desktopPosition=host.PointToScreen(new Point(host.ActualWidth,host.ActualHeight));
            main.Left+=16;await Idle();Require((host.PointToScreen(new Point(host.ActualWidth,host.ActualHeight))-desktopPosition).Length<2,"Desktop notification followed app movement.");
            main.WindowState=WindowState.Minimized;await Idle();Require(host.IsVisible,"Desktop notification hid with app minimize.");
            main.WindowState=WindowState.Normal;main.Left=originalLeft;main.Top=originalTop;main.WindowState=originalState;
            notifications.SetAnchor(CadNotificationAnchor.ApplicationWindow);await Idle();await WaitForApplicationPosition(host,main);
            var model=(NotificationModel)progress.DataContext;
            model.CancelCommand!.Execute(null);model.CancelCommand.Execute(null);
            Require(token.IsCancellationRequested&&!model.CancelCommand.CanExecute(null)&&!running.IsCompleted&&!progress.IsClosing,
                "Cancel must disable itself and retain progress until cleanup completes.");
            Capture(progress,Path.Combine(output,"notification-cancelling.png"));
            release.SetResult();
            try{await running;throw new InvalidOperationException("Notification swallowed cancellation.");}catch(OperationCanceledException){}
            Require(notifications.ActiveProgress is null&&progress.Handle!.Completion.IsCompleted,"Cancelled notification was not cleaned up.");
            await notifications.RunWithProgressAsync(_=>Task.CompletedTask,"快速完成");
            try{await notifications.RunWithProgressAsync(_=>Task.FromException(new IOException("Notification smoke")),"失败清理");}
            catch(IOException){}
            Require(notifications.ActiveProgress is null,"Failure left a working notification.");
            await File.WriteAllTextAsync(Path.Combine(output,"notification-result.txt"),
                "PASS: fixed-size entry/exit with pre-positioned host, no layout scaling; NuGet custom Notification, dark/light themed rounded colored borders, log forwarding, close button, no activation/taskbar, app bottom-right follows move/resize/maximize and hides/restores on minimize; desktop stays fixed/visible, live anchor switching preserves progress/cancellation; persistent progress, cancel once/wait for cleanup, fast success/error cleanup.");
        }
        finally
        {
            release.TrySetResult();
            if(running is not null)try{await running;}catch(OperationCanceledException){}
            notifications.Shown-=OnShown;theme.ApplyThemeLightDark(originalTheme);
            main.WindowState=WindowState.Normal;main.Left=originalLeft;main.Top=originalTop;main.WindowState=originalState;
            notifications.SetAnchor(originalAnchor);
            foreach(var card in shown)await card.CloseAsync();
        }
    }
    private static async Task VerifyMotionAsync(CadNotificationService notifications,ICadMessageLog log,string output)
    {
        var shown=new TaskCompletionSource<CadNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnShown(CadNotification card)
        {if(card.DataContext is NotificationModel{Message:"Notification motion"})shown.TrySetResult(card);}
        void OnFailed(Exception ex)=>shown.TrySetException(ex);
        notifications.Shown+=OnShown;
        notifications.Failed+=OnFailed;
        try
        {
            log.Add("Notification motion",CadMessageLevel.Information,"Notification.Smoke.Motion");
            var card=await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var host=(NotificationHostWindow)Window.GetWindow(card);
            var border=(Border)card.Template.FindName("PART_AnimationRoot",card);
            var samples=new List<string>();
            bool intermediate=false;
            double width=host.ActualWidth,height=host.ActualHeight;
            for(int i=0;i<12;i++)
            {
                await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render);
                CheckApplicationPosition(host,Application.Current.MainWindow);
                Require(card.LayoutTransform.Value.IsIdentity,"Library layout scaling still affects the custom notification.");
                Require(Math.Abs(host.ActualWidth-width)<1&&Math.Abs(host.ActualHeight-height)<1,"Entrance animation resized the HWND.");
                intermediate|=border.Opacity is >0 and <1;
                samples.Add($"entry {i}: opacity={border.Opacity:F3}, y={border.RenderTransform.Value.OffsetY:F3}, hwnd={width}x{height}");
                if(i is 2 or 11)Capture(card,Path.Combine(output,$"notification-motion-{i}.png"));
                await Task.Delay(35);
            }
            Require(border.Opacity==1&&border.RenderTransform.Value.OffsetY==0,"Entrance animation did not settle.");
            if(card.AnimationsEnabled&&SystemParameters.ClientAreaAnimation)Require(intermediate,"No intermediate entrance frame was observed.");
            var closing=card.Handle!.CloseAsync();
            await Task.Delay(55);
            if(card.AnimationsEnabled&&SystemParameters.ClientAreaAnimation)
            {
                Require(border.Opacity is >0 and <1,"Closing notification did not fade.");
                Require(card.LayoutTransform.Value.IsIdentity&&Math.Abs(host.ActualWidth-width)<1&&Math.Abs(host.ActualHeight-height)<1,"Closing animation changed layout size.");
            }
            samples.Add($"exit: opacity={border.Opacity:F3}, hwnd={host.ActualWidth}x{host.ActualHeight}");
            await closing;
            // Reusing the host with reduced motion must reveal at final size immediately.
            var reduced=new CadNotification(new NotificationModel{Title="Reduced motion",Message="No animation",Accent=Brushes.SteelBlue,
                Icon=MaterialDesignThemes.Wpf.PackIconKind.InformationOutline}){AnimationsEnabled=false};
            reduced.ApplyTemplate();reduced.Present();
            var reducedBorder=(Border)reduced.Template.FindName("PART_AnimationRoot",reduced);
            Require(reducedBorder.Opacity==1&&!reducedBorder.HasAnimatedProperties&&reduced.LayoutTransform.Value.IsIdentity,"Reduced motion still animates.");
            await File.WriteAllLinesAsync(Path.Combine(output,"notification-motion-result.txt"),new[]{"PASS: fixed HWND size, bottom-right correct throughout entry; gradual fade/translation, fixed-size exit fade, reduced-motion immediate display."}.Concat(samples));
        }
        finally{notifications.Shown-=OnShown;notifications.Failed-=OnFailed;}
    }
    private static void CheckApplicationPosition(NotificationHostWindow host,Window main)
    {
        var content=(FrameworkElement)main.Content;
        var edge=content.PointToScreen(new Point(content.ActualWidth,content.ActualHeight));
        var actual=host.PointToScreen(new Point(host.ActualWidth,host.ActualHeight));
        var dpi=VisualTreeHelper.GetDpi(host);
        Require(Math.Abs(edge.X-actual.X-16*dpi.DpiScaleX)<3&&Math.Abs(edge.Y-actual.Y-16*dpi.DpiScaleY)<3,
            $"App notification anchor is incorrect: expected {edge}, actual {actual}.");
    }
    private static async Task WaitForApplicationPosition(NotificationHostWindow host,Window main)
    {
        // Window movement and an expiring stack both enqueue SizeToContent/position updates.
        // Require the same strict geometry assertion on three settled frames, with a bounded deadline.
        int settled=0;
        for(int i=0;i<40;i++)
        {
            await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            try{CheckApplicationPosition(host,main);if(++settled==3)return;}
            catch(InvalidOperationException){settled=0;}
            await Task.Delay(50);
        }
        CheckApplicationPosition(host,main);
        throw new InvalidOperationException("Notification anchor did not stabilize within two seconds.");
    }
    internal static void Capture(FrameworkElement view,string path)
    {
        view.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth),(int)Math.Ceiling(view.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(view);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
    }
    private static async Task Idle(){await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);await Task.Delay(100);}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
