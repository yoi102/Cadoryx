using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.wpf.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static class DeliverySmokeRunner
{
    internal static async Task RunAsync(MainWindow owner,MainWindowViewModel main,IServiceProvider services,string output)
    {
        var doc=main.ActiveDocument!;var before=doc.Session.Snapshot;bool dirty=doc.Session.IsDirty;
        var original=CultureInfo.GetCultureInfo(main.CurrentCultureLCID);
        try
        {
            foreach(var language in new[]{"en-US","zh-CN","ja-JP"})
            {
                Antelcat.I18N.WPF.I18NExtension.Culture=CultureInfo.GetCultureInfo(language);
                var inspect=InspectWindow(owner,output,language);
                await main.DeliveryCommand.ExecuteAsync(null);await inspect;
                if(before.StateId!=doc.Session.Snapshot.StateId||dirty!=doc.Session.IsDirty)throw new InvalidOperationException("Delivery preview changed document state.");
            }
            var delivery=services.GetRequiredService<IDocumentDeliveryService>();
            await services.GetRequiredService<IDialogService>().RunWithProgressAsync(async token=>
            {
                var result=await delivery.WriteAsync(before,doc.Session.Assets,Path.Combine(output,"delivery.zip"),new(IncludeIges:true,IncludeStl:true),token:token);
                if(result.StateId!=before.StateId)throw new InvalidOperationException("Delivery used another snapshot.");
            },true,"Delivery smoke");
            var manifest=await DocumentDeliveryService.VerifyAsync(Path.Combine(output,"delivery.zip"));
            if(manifest.Files.Length!=8||!manifest.Files.Any(file=>file.Name=="exchange-loss.json"))
                throw new InvalidOperationException("Incomplete delivery bundle.");
            await File.WriteAllTextAsync(Path.Combine(output,"delivery-result.txt"),"PASS: Ribbon command opens owner MetroWindow, BOM preview and options in three languages, cancel preserves document and save point; progress dialog, eight payload files including exchange-loss report and SHA-256 verification.");
        }
        finally{Antelcat.I18N.WPF.I18NExtension.Culture=original;}
    }
    private static async Task InspectWindow(MainWindow owner,string output,string language)
    {
        DeliveryWindow? window=null;
        for(int i=0;i<100&&window is null;i++){await Task.Delay(50);window=Application.Current.Windows.OfType<DeliveryWindow>().FirstOrDefault();}
        if(window is null)throw new InvalidOperationException("Delivery window not opened by command.");
        try
        {
            await Task.Delay(500); // Let the MetroWindow content entrance reach its final opacity.
            if(!window.IsVisible||window.Owner!=owner||window.BomGrid.Items.Count==0)throw new InvalidOperationException("Delivery preview not populated.");
            window.VisibleOnlyCheck.IsChecked=false;window.OutputChoice.SelectedIndex=2;
            var model=(DeliveryViewModel)window.DataContext;
            if(model.GetOptions().Output!=DeliveryOutput.ReviewHtml||model.VisibleOnly)throw new InvalidOperationException("Delivery option binding failed.");
            window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(Path.Combine(output,"delivery-"+language+".png"));encoder.Save(file);
        }
        finally{window.Close();}
    }
}
