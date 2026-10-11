using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Cadoryx.Db;
using Cadoryx.Editor;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.ViewModels;

public interface ISketchRegionSourceSelectionHost {Task ShowAsync(SketchRegionSourceSelectionViewModel editor);}
public sealed record RegionSourceCurveChoice(SketchEntityId Id,string Label)
{public string Identity=>Id.Value.ToString("D");}
public sealed partial class RegionSourceSlotRow(int loop,int slot,SketchEntityId original,string label,IReadOnlyList<RegionSourceCurveChoice> choices):ObservableObject
{
    public int Loop {get;}=loop;
    public int Slot {get;}=slot;
    public int SlotNumber=>Slot+1;
    public string OriginalIdentity {get;}=original.Value.ToString("D");
    public string OriginalLabel {get;}=label;
    public string LoopLabel=>string.Format(Strings.RegionReselectLoop,Loop+1);
    public IReadOnlyList<RegionSourceCurveChoice> Choices {get;}=choices;
    [ObservableProperty] private RegionSourceCurveChoice? replacement;
}

public sealed partial class SketchRegionSourceSelectionViewModel:ObservableObject,IAsyncDisposable
{
    private readonly SketchEditorViewModel owner;
    private readonly CadSketch captured;
    private readonly Guid group;
    private SketchRegionSourceReselection.Prepared? prepared;
    private CancellationTokenSource? cancellation;
    private bool disposed;
    private int selectionVersion;
    private Task? disposal;
    public ObservableCollection<RegionSourceSlotRow> Rows {get;}=[];
    [ObservableProperty] private SketchEditorViewModel? previewEditor;
    [ObservableProperty] private bool isWorking;
    [ObservableProperty] private string status=Strings.RegionReselectHint;
    [ObservableProperty] private string details="";
    public bool CanEdit=>!disposed&&!IsWorking;
    public bool CanApply=>CanEdit&&prepared is not null;
    public event EventHandler? CloseRequested;
    public SketchRegionSourceSelectionViewModel(SketchEditorViewModel owner,Guid group)
    {
        this.owner=owner;this.group=group;captured=owner.Sketch;
        var author=captured.RegionOffsets.Single(g=>g.Id==group);var own=author.Loops.SelectMany(l=>l.Outputs).Select(o=>o.Curve).ToHashSet();
        var ids=captured.Lines.Where(v=>!v.IsConstruction).Select(v=>v.Id).Concat(captured.Circles.Where(v=>!v.IsConstruction).Select(v=>v.Id))
            .Concat(captured.Arcs.Where(v=>!v.IsConstruction).Select(v=>v.Id)).Concat(captured.Beziers.Where(v=>!v.IsConstruction).Select(v=>v.Id))
            .Concat(captured.Splines.Where(v=>!v.IsConstruction).Select(v=>v.Id)).Concat(captured.Ellipses.Where(v=>!v.IsConstruction).Select(v=>v.Id))
            .Concat(captured.Freeforms.Where(v=>!v.IsConstruction).Select(v=>v.Id)).Concat(captured.ExpressionCurves.Where(v=>!v.IsConstruction&&!own.Contains(v.Id)).Select(v=>v.Id));
        var labels=owner.Entities.ToDictionary(e=>e.Id,e=>e.Label);
        string Label(SketchEntityId id)=>labels.GetValueOrDefault(id,Strings.RegionReselectSource)+" · "+id.Value.ToString("D")[..8];
        var choices=ids.Distinct().Select(id=>new RegionSourceCurveChoice(id,Label(id))).ToArray();
        for(int i=0;i<author.Sources.Length;i++)for(int j=0;j<author.Sources[i].Length;j++)
        {
            var row=new RegionSourceSlotRow(i,j,author.Sources[i][j].Entity,Label(author.Sources[i][j].Entity),choices);
            row.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(RegionSourceSlotRow.Replacement)){selectionVersion++;prepared=null;Status=Strings.RegionReselectPreviewOutdated;Details="";NotifyState();}};
            Rows.Add(row);
        }
        PreviewEditor=owner.CreateRegionSourcePreview(captured);
    }
    partial void OnIsWorkingChanged(bool value)=>NotifyState();
    private void NotifyState(){OnPropertyChanged(nameof(CanEdit));OnPropertyChanged(nameof(CanApply));PreviewCommand.NotifyCanExecuteChanged();ApplyCommand.NotifyCanExecuteChanged();}
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task PreviewAsync()
    {
        prepared=null;Details="";
        if(Rows.Any(r=>r.Replacement is null)){Status=Strings.RegionReselectIncomplete;NotifyState();return;}
        if(!ReferenceEquals(owner.Sketch,captured)||!owner.CanEdit){Status=Strings.RegionReselectStale;NotifyState();return;}
        var slots=Rows.GroupBy(r=>r.Loop).OrderBy(g=>g.Key).Select(g=>g.OrderBy(r=>r.Slot).Select(r=>r.Replacement!.Id).ToImmutableArray()).ToImmutableArray();
        int requestedVersion=selectionVersion;IsWorking=true;Status=Strings.SolvingSketch;using var cancel=new CancellationTokenSource();cancellation=cancel;
        try
        {
            var result=await Task.Run(()=>owner.PrepareRegionSources(captured,group,slots,cancel.Token),cancel.Token);
            if(disposed||cancel.IsCancellationRequested)return;
            if(requestedVersion!=selectionVersion){Status=Strings.RegionReselectPreviewOutdated;return;}
            if(!owner.CanEdit||!ReferenceEquals(owner.Sketch,captured)){Status=Strings.RegionReselectStale;return;}
            owner.RequireRegionSourceParameters(result);
            if(PreviewEditor is {} old){PreviewEditor=null;await old.DisposeAsync();}
            PreviewEditor=owner.CreateRegionSourcePreview(result.Candidate);prepared=result;Status=Strings.RegionReselectPreviewReady;
        }
        catch(OperationCanceledException){if(!disposed)Status=Strings.CanceledStatus;}
        catch(Exception error) when(error is CadValidationException or NotSupportedException or InvalidOperationException)
        {if(disposed)return;Status=error.Data["RegionReselect.ParameterGuard"] is true?Strings.RegionReselectParameterGuard:Strings.RegionReselectIncompatible;Details=error.Message;}
        finally{IsWorking=false;if(ReferenceEquals(cancellation,cancel))cancellation=null;}
    }
    [RelayCommand(CanExecute=nameof(CanApply))] private void Apply()
    {
        if(prepared is null)return;
        string? error=owner.AcceptRegionSources(prepared);
        if(error is not null){Status=error;prepared=null;NotifyState();return;}
        CloseRequested?.Invoke(this,EventArgs.Empty);
    }
    [RelayCommand] private void Cancel(){cancellation?.Cancel();prepared=null;NotifyState();CloseRequested?.Invoke(this,EventArgs.Empty);}
    public ValueTask DisposeAsync()=>new(disposal??=DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        disposed=true;cancellation?.Cancel();prepared=null;NotifyState();
        if(PreviewCommand.ExecutionTask is {} task)await task;
        if(PreviewEditor is {} editor){PreviewEditor=null;await editor.DisposeAsync();}
    }
}
