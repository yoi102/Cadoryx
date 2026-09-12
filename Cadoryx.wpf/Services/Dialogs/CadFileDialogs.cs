using System.Windows;
using Microsoft.Win32;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.wpf.Views.Dialogs;
namespace Cadoryx.wpf.Services.Dialogs;
public sealed class CadFileDialogs:ICadFileDialogs
{
    private readonly DialogService dialogs;
    public CadFileDialogs(DialogService dialogs)=>this.dialogs=dialogs??throw new ArgumentNullException(nameof(dialogs));
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
    public async Task<CadExportRequest?> ExportDocumentAsync(string name)
    {
        var dialog=new SaveFileDialog{Title=Strings.ExportModelTitle,FileName=SafeName(name),Filter=Strings.StepModelFilter+"|"+Strings.IgesModelFilter+"|"+Strings.StlModelFilter,DefaultExt=".step",AddExtension=true};
        if(dialog.ShowDialog(Owner)!=true)return null;
        var options=new ExportOptionsDialog(dialog.FileName);
        var result=await dialogs.ShowDialogAsync(options);
        return result is string value&&value==bool.TrueString?options.Request:null;
    }
    public async Task<SaveDecision> ConfirmSaveAsync(string name)
    {
        var dialog=new ConfirmationDialog(Strings.CloseDocumentTitle,string.Format(Strings.SaveChangesQuestion,name),Strings.Save,Strings.DontSave);
        var result=await dialogs.ShowDialogAsync(dialog);
        return (result as string) switch
        {
            var value when value==ConfirmationDialogResult.Accept=>SaveDecision.Save,
            var value when value==ConfirmationDialogResult.Discard=>SaveDecision.Discard,
            _=>SaveDecision.Cancel
        };
    }
    private static string SafeName(string name)=>string.Concat(name.Select(c=>System.IO.Path.GetInvalidFileNameChars().Contains(c)?'_':c));
}
