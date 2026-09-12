using System.Collections.ObjectModel;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public partial class RecoveryCenterViewModel(IRecoveryStore store, Func<RecoveryKey, Task<bool>> restore,
    Func<RecoveryEntry, Task<bool>> confirmDiscard) : ObservableObject
{
    public RecoveryCenterViewModel(IRecoveryStore store, Func<RecoveryKey, Task<bool>> restore,
        Func<RecoveryEntry, bool> confirmDiscard)
        : this(store, restore, entry => Task.FromResult(confirmDiscard(entry)))
    {
    }

    public ObservableCollection<RecoveryEntry> Entries { get; } = [];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(DiscardCommand))]
    private RecoveryEntry? selectedEntry;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(RestoreCommand), nameof(DiscardCommand))]
    private bool isBusy;
    [ObservableProperty] private string status = "";
    private bool CanRefresh() => !IsBusy;
    private bool CanChoose() => !IsBusy && SelectedEntry is not null;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try { await ReloadAsync(); }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }
    private async Task ReloadAsync()
    {
        var scan = await store.ScanAsync(); Entries.Clear();
        foreach (var entry in scan.Entries) Entries.Add(entry);
        SelectedEntry = Entries.FirstOrDefault();
        Status = string.Join(Environment.NewLine, scan.Diagnostics.Select(d => d.Message));
        if (Entries.Count == 0 && Status.Length == 0) Status = Strings.RecoveryEmpty;
    }
    [RelayCommand(CanExecute = nameof(CanChoose))]
    private async Task RestoreAsync()
    {
        if (SelectedEntry is not {} entry) return;
        IsBusy = true;
        try
        {
            if (await restore(entry.Key)) { await ReloadAsync(); Status = Strings.RecoveryOpened; }
            else Status = Strings.RecoveryRestoreFailed;
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanChoose))]
    private async Task DiscardAsync()
    {
        if (SelectedEntry is not {} entry || !await confirmDiscard(entry)) return;
        IsBusy = true;
        try { await store.DiscardAsync(entry.Key); await ReloadAsync(); }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }
}
