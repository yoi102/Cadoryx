using Cadoryx.Editor;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public partial class SessionHistoryViewModel : ObservableObject,IDisposable
{
    private readonly CadDocumentViewModel document;
    [ObservableProperty] private int entryLimit;
    [ObservableProperty] private double assetBudgetMiB;
    public DocumentHistoryUsage Usage {get;private set;}
    public string Summary=>string.Format(Strings.ResourceManager.GetString("HistoryUsageFormat",Strings.Culture)!,
        Usage.UndoEntries,Usage.RedoEntries,Usage.AdditionalAssetBytes/1048576d);
    public SessionHistoryViewModel(CadDocumentViewModel document)
    {
        this.document=document;Usage=document.Session.HistoryUsage;EntryLimit=Usage.EntryLimit;AssetBudgetMiB=Usage.AssetBudgetBytes/1048576d;
        document.Session.StatusChanged+=OnStatus;
    }
    private void OnStatus(object? sender,EventArgs e)=>Refresh();
    public void Refresh(){Usage=document.Session.HistoryUsage;OnPropertyChanged(nameof(Usage));OnPropertyChanged(nameof(Summary));}
    [RelayCommand] private async Task ApplyAsync()
    {
        try
        {
            if(!double.IsFinite(AssetBudgetMiB)||AssetBudgetMiB is < 0 or > 32768)
                throw new ArgumentOutOfRangeException(nameof(AssetBudgetMiB));
            await document.Session.ConfigureHistoryAsync(EntryLimit,checked((long)(AssetBudgetMiB*1048576)));
        }
        catch(Exception ex){document.Report(ex);}
    }
    [RelayCommand] private async Task ClearAsync()
    {
        try{await document.Session.ClearHistoryAsync();}catch(Exception ex){document.Report(ex);}
    }
    public void Dispose()=>document.Session.StatusChanged-=OnStatus;
}
