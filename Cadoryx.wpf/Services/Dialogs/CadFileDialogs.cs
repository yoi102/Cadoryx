using System.Windows;
using Microsoft.Win32;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.wpf.Views.Dialogs;
namespace Cadoryx.wpf.Services.Dialogs;
public sealed class CadFileDialogs:ICadFileDialogs
{
    private static Window Owner=>System.Windows.Application.Current.MainWindow;
    public string? OpenDocument()
    {
        var dialog=new OpenFileDialog{Title="打开 CAD 文档",Filter="CAD 文档|*.cadoryx;*.step;*.stp;*.iges;*.igs|Cadoryx|*.cadoryx|STEP|*.step;*.stp|IGES|*.iges;*.igs",CheckFileExists=true};
        return dialog.ShowDialog(Owner)==true?dialog.FileName:null;
    }
    public string? SaveDocument(string name)
    {
        var dialog=new SaveFileDialog{Title="保存 Cadoryx 文档",FileName=SafeName(name),Filter="Cadoryx 文档|*.cadoryx",DefaultExt=".cadoryx",AddExtension=true};
        return dialog.ShowDialog(Owner)==true?dialog.FileName:null;
    }
    public CadExportRequest? ExportDocument(string name)
    {
        var dialog=new SaveFileDialog{Title="导出模型",FileName=SafeName(name),Filter="STEP 模型|*.step|IGES 模型|*.iges|STL 网格|*.stl",DefaultExt=".step",AddExtension=true};
        if(dialog.ShowDialog(Owner)!=true)return null;
        var options=new ExportOptionsWindow(dialog.FileName){Owner=Owner};
        return options.ShowDialog()==true?options.Request:null;
    }
    public SaveDecision ConfirmSave(string name)=>MessageBox.Show(Owner,$"保存对“{name}”的修改？","关闭文档",MessageBoxButton.YesNoCancel,MessageBoxImage.Question) switch
    {MessageBoxResult.Yes=>SaveDecision.Save,MessageBoxResult.No=>SaveDecision.Discard,_=>SaveDecision.Cancel};
    private static string SafeName(string name)=>string.Concat(name.Select(c=>System.IO.Path.GetInvalidFileNameChars().Contains(c)?'_':c));
}
