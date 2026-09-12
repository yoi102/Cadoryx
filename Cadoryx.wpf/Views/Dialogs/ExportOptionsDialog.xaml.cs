using System.IO;
using System.Windows;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.Lang.Strings;
using MaterialDesignThemes.Wpf;
namespace Cadoryx.wpf.Views.Dialogs;
public partial class ExportOptionsDialog
{
    private readonly string path;
    public CadExportRequest? Request {get;private set;}
    public ExportOptionsDialog(string path)
    {
        InitializeComponent();this.path=path;bool stl=Path.GetExtension(path).Equals(".stl",StringComparison.OrdinalIgnoreCase);
        var format=Path.GetExtension(path).ToUpperInvariant().TrimStart('.');
        FormatText.Text=stl?Strings.StlMesh:string.Format(Strings.ModelFormat,format);
        SemanticsText.Text=stl?Strings.StlExportSemantics:Strings.ModelExportSemantics;
        StlSettings.Visibility=stl?Visibility.Visible:Visibility.Collapsed;
    }
    private void ExportClicked(object sender,RoutedEventArgs e)
    {
        if(LinearInput.Value is not {} linear||AngleInput.Value is not {} angle)return;
        Request=new(path,linear,angle*Math.PI/180,BinaryCheck.IsChecked==true,VisibleCheck.IsChecked==true);
        DialogHost.CloseDialogCommand.Execute(bool.TrueString, this);
    }
}
