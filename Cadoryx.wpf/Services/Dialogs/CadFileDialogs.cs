using System.Windows;
using Microsoft.Win32;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.wpf.Views.Dialogs;
namespace Cadoryx.wpf.Services.Dialogs;
public sealed class CadFileDialogs:ICadFileDialogs
{
    private static Window Owner=>System.Windows.Application.Current.MainWindow;
    public string? OpenDocument()
    {
        var dialog=new OpenFileDialog{Title=Strings.OpenCadDocumentTitle,Filter=Strings.CadoryxDocumentsFilter+"|"+Strings.CadoryxFilter+"|"+Strings.StepFilter+"|"+Strings.IgesFilter,CheckFileExists=true};
        return dialog.ShowDialog(Owner)==true?dialog.FileName:null;
    }
    public string? SaveDocument(string name)
    {
        var dialog=new SaveFileDialog{Title=Strings.SaveCadoryxDocumentTitle,FileName=SafeName(name),Filter=Strings.CadoryxDocumentFilter,DefaultExt=".cadoryx",AddExtension=true};
        return dialog.ShowDialog(Owner)==true?dialog.FileName:null;
    }
    public CadExportRequest? ExportDocument(string name)
    {
        var dialog=new SaveFileDialog{Title=Strings.ExportModelTitle,FileName=SafeName(name),Filter=Strings.StepModelFilter+"|"+Strings.IgesModelFilter+"|"+Strings.StlModelFilter,DefaultExt=".step",AddExtension=true};
        if(dialog.ShowDialog(Owner)!=true)return null;
        var options=new ExportOptionsWindow(dialog.FileName){Owner=Owner};
        return options.ShowDialog()==true?options.Request:null;
    }
    public SaveDecision ConfirmSave(string name)=>MessageBox.Show(Owner,string.Format(Strings.SaveChangesQuestion,name),Strings.CloseDocumentTitle,MessageBoxButton.YesNoCancel,MessageBoxImage.Question) switch
    {MessageBoxResult.Yes=>SaveDecision.Save,MessageBoxResult.No=>SaveDecision.Discard,_=>SaveDecision.Cancel};
    private static string SafeName(string name)=>string.Concat(name.Select(c=>System.IO.Path.GetInvalidFileNameChars().Contains(c)?'_':c));
}
