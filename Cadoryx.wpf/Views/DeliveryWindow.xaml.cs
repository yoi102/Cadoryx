using System.IO;
using System.Windows;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels;
using Microsoft.Win32;

namespace Cadoryx.wpf.Views;

public partial class DeliveryWindow
{
    private readonly string documentName;
    public DeliveryRequest? Request {get;private set;}
    public DeliveryWindow(DeliveryViewModel model,string name){InitializeComponent();DataContext=model;documentName=name;}
    private void WriteClicked(object sender,RoutedEventArgs e)
    {
        try
        {
            var options=((DeliveryViewModel)DataContext).GetOptions();
            var ext=options.Output switch{DeliveryOutput.Package=>"zip",DeliveryOutput.BomCsv=>"csv",_=>"html"};
            var picker=new SaveFileDialog{Title=Strings.EngineeringDelivery,FileName=string.Concat(documentName.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c)),
                DefaultExt="."+ext,Filter=$"{ext.ToUpperInvariant()}|*.{ext}",AddExtension=true,OverwritePrompt=true};
            if(picker.ShowDialog(this)!=true)return;
            Request=new(picker.FileName,options);DialogResult=true;
        }
        catch(Exception ex){ErrorText.Text=ex.Message;}
    }
}
public sealed class DeliveryHost:IDeliveryHost
{
    public async Task<DeliveryRequest?> ShowAsync(DocumentSnapshot snapshot)
    {
        var model=await Task.Run(()=>new DeliveryViewModel(snapshot));
        var window=new DeliveryWindow(model,snapshot.Name){Owner=Application.Current.MainWindow};
        return window.ShowDialog()==true?window.Request:null;
    }
}
