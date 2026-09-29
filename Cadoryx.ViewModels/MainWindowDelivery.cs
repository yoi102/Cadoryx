using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels.Services.Platform;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public partial class MainWindowViewModel
{
    [RelayCommand(CanExecute=nameof(CanUseDocument))]
    private async Task DeliveryAsync()
    {
        if(ActiveDocument is not {} document)return;
        await RunAsync(async()=>
        {
            using var capture=document.Session.Capture();
            var request=await deliveryHost.ShowAsync(capture.Snapshot);if(request is null)return;
            await _dialogService.RunWithProgressAsync(async token=>
            {
                var progress=new Progress<DeliveryProgress>(p=>StatusText=$"{Strings.EngineeringDelivery} · {p.Completed}/{p.Total}");
                var result=await delivery.WriteAsync(capture.Snapshot,assets,request.Path,request.Options,true,progress,token);
                foreach(var diagnostic in result.Diagnostics)log.Add(diagnostic.Message,CadMessageLevel.Information,diagnostic.Code);
                StatusText=string.Format(Strings.DeliveryCompleted,System.IO.Path.GetFileName(result.Path));
            },true,Strings.DeliveryWorking);
        });
    }
}
