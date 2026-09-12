using System.Collections.ObjectModel;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering;
using Cadoryx.Lang;
using Strings = Cadoryx.Lang.Strings.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public interface ILocalFeatureHost {Task ShowAsync(LocalFeatureViewModel editor);}
public sealed record LocalBoxChoice(FeatureId FeatureId,BodyId BodyId,string Label);
public sealed record TopologyChoice(TopologyReference Reference,string Label);
public sealed record LocalOption<T>(T Value,string Label);

public partial class LocalFeatureViewModel : ObservableObject,IAsyncDisposable
{
    private readonly CadDocumentSession session;
    private readonly IGeometryKernel kernel;
    private readonly DocumentCapture baseline;
    private readonly long generation;
    private PreparedDocumentEdit? prepared;
    private CancellationTokenSource? cancellation;
    private bool disposed,committing;
    private Task? disposal;
    public ObservableCollection<LocalBoxChoice> Boxes {get;}=[];
    public ObservableCollection<TopologyChoice> References {get;}=[];
    public IReadOnlyList<LocalOption<LocalFeatureOperation>> Operations {get;}=Enum.GetValues<LocalFeatureOperation>().Select(v=>new LocalOption<LocalFeatureOperation>(v,v==LocalFeatureOperation.Fillet?Strings.Fillet:Strings.Chamfer)).ToArray();
    public IReadOnlyList<LocalOption<TopologyKind>> Kinds {get;}=Enum.GetValues<TopologyKind>().Select(v=>new LocalOption<TopologyKind>(v,v==TopologyKind.Face?Strings.Face:Strings.Edge)).ToArray();
    public DocumentSnapshot Snapshot=>baseline.Snapshot;
    public IAssetStore Assets=>session.Assets;
    public CadScene? Scene {get;private set;}
    public bool CanEdit=>!disposed&&!IsWorking&&!IsStale&&session.Snapshot.Extensions.IsDefaultOrEmpty;
    public bool CanPreview=>CanEdit&&Selection?.Kind==TopologyKind.Edge;
    public bool CanConfirm=>CanEdit&&prepared is not null;
    public bool HasCandidate=>prepared is not null;
    public event EventHandler? SceneChanged;
    public event EventHandler? CloseRequested;
    [ObservableProperty] private LocalBoxChoice? selectedBox;
    [ObservableProperty] private TopologyKind selectionKind=TopologyKind.Edge;
    [ObservableProperty] private LocalFeatureOperation operation;
    [ObservableProperty] private double size=2;
    [ObservableProperty] private string status="";
    [ObservableProperty] private bool isWorking,isStale;
    [ObservableProperty] private TopologyReference? selection;
    [ObservableProperty] private TopologyChoice? selectedReference;

    public LocalFeatureViewModel(CadDocumentSession session,IGeometryKernel kernel)
    {
        this.session=session;this.kernel=kernel;baseline=session.Capture();generation=session.Generation;
        foreach(var body in Snapshot.Bodies.Values.Where(b=>b.Producer is {} f&&Snapshot.Features[f].Recipe is BoxRecipe))
            Boxes.Add(new(body.Producer!.Value,body.Id,body.Name));
        foreach(var reference in Snapshot.TopologyReferences.Values)
            References.Add(new(reference,reference.Id.Value.ToString("N")[..8]+" · "+reference.Kind+" · "+reference.Boundary+(reference.SecondBoundary is {} second?" / "+second:"")));
        session.Changed+=OnChanged;SelectedBox=Boxes.FirstOrDefault();Status=Boxes.Count==0?Strings.NoBoxOutput:Strings.LocalFeaturePick;
    }
    private void OnChanged(object? sender,DocumentChangeSet e)
    {if(!committing){IsStale=true;Invalidate();Status=Strings.LocalFeatureStale;} }
    public void Pick(BoxBoundary first,BoxBoundary? second)
    {
        if(!CanEdit||SelectedBox is null)return;
        Selection=TopologyReference.Box(Snapshot,SelectedBox.FeatureId,first,second);
        if(SelectedReference is {} previous)Selection=Selection with{Id=previous.Reference.Id,Policy=previous.Reference.Policy};
        Status=Strings.Selected+" "+first+(second is {} s?" / "+s:"");
    }
    public void RejectPick(string message){Selection=null;Status=message;}
    public BoxRecipe? Box=>SelectedBox is {} b?Snapshot.Features[b.FeatureId].Recipe as BoxRecipe:null;
    public void ShowSource()
    {
        Scene=SelectedBox is {} b?new(Snapshot.Id,Snapshot.StateId,[new(new OccurrencePath(Snapshot.Id,[]),b.BodyId,Snapshot.Bodies[b.BodyId].Geometry,RigidTransform3d.Identity,0xFF94B8D8)]):null;
        SceneChanged?.Invoke(this,EventArgs.Empty);
    }
    partial void OnSelectedBoxChanged(LocalBoxChoice? value){Selection=null;Invalidate();ShowSource();OnPropertyChanged(nameof(Box));}
    partial void OnSelectionKindChanged(TopologyKind value){Selection=null;Invalidate();ShowSource();}
    partial void OnOperationChanged(LocalFeatureOperation value)=>Invalidate();
    partial void OnSizeChanged(double value)=>Invalidate();
    partial void OnSelectionChanged(TopologyReference? value){Invalidate();Notify();}
    partial void OnSelectedReferenceChanged(TopologyChoice? value){Selection=null;Invalidate();Status=Strings.LocalFeaturePick;}
    partial void OnIsWorkingChanged(bool value)=>Notify();
    partial void OnIsStaleChanged(bool value)=>Notify();
    private void Notify(){OnPropertyChanged(nameof(CanEdit));OnPropertyChanged(nameof(CanPreview));OnPropertyChanged(nameof(CanConfirm));PreviewCommand.NotifyCanExecuteChanged();ConfirmCommand.NotifyCanExecuteChanged();SaveReferenceCommand.NotifyCanExecuteChanged();InspectReferenceCommand.NotifyCanExecuteChanged();ReselectCommand.NotifyCanExecuteChanged();}
    private void Invalidate(){cancellation?.Cancel();var previous=prepared;prepared=null;ShowSource();previous?.Dispose();Notify();}
    private static string TopologyStatusText(TopologyResolutionStatus status)=>status switch
    {
        TopologyResolutionStatus.Resolved=>Strings.ReferenceResolved,
        TopologyResolutionStatus.Missing=>Strings.MissingReference,
        TopologyResolutionStatus.Stale=>Strings.ReferenceStale,
        TopologyResolutionStatus.Ambiguous=>Strings.AmbiguousSelection,
        TopologyResolutionStatus.Unsupported=>Strings.UnsupportedTopology,
        TopologyResolutionStatus.WrongContext=>Strings.WrongReferenceContext,
        _=>Strings.MissingReference
    };
    [RelayCommand(CanExecute=nameof(CanEdit))] private void Reselect(){Selection=null;Invalidate();Status=Strings.LocalFeaturePick;}

    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task InspectReferenceAsync()
    {
        if(SelectedReference is not {} item)return;
        IsWorking=true;
        try{var result=await ((ITopologyResolver)kernel).ResolveAsync(Snapshot,item.Reference,Assets);if(!IsStale&&!disposed)Status=TopologyStatusText(result.Status);}
        catch(Exception ex){Status=ex.Message;}
        finally{IsWorking=false;}
    }
    [RelayCommand(CanExecute=nameof(CanPreview))] private async Task PreviewAsync()
    {
        Invalidate();IsWorking=true;using var cancel=new CancellationTokenSource();cancellation=cancel;
        try
        {
            var result=await new LocalFeatureCommand(Selection!,Operation,Size).PrepareAsync(new(Snapshot,generation,Assets,kernel),cancel.Token);
            if(disposed||IsStale||cancel.IsCancellationRequested||session.Generation!=generation){result.Dispose();return;}
            try{result.Snapshot.Validate();}catch{result.Dispose();throw;}prepared=result;
            var body=result.Snapshot.Bodies.Values.Single(b=>!Snapshot.Bodies.ContainsKey(b.Id));
            Scene=new(Snapshot.Id,result.Snapshot.StateId,[new(new OccurrencePath(Snapshot.Id,[]),body.Id,body.Geometry,RigidTransform3d.Identity,0xFF80CBAA)]);
            SceneChanged?.Invoke(this,EventArgs.Empty);Status=Strings.PreviewReadyStatus;
        }
        catch(OperationCanceledException){Status=Strings.LocalFeaturePick;}
        catch(Exception ex){Status=ex.Message;}
        finally{cancellation=null;IsWorking=false;}
    }
    [RelayCommand(CanExecute=nameof(CanConfirm))] private async Task ConfirmAsync()
    {
        if(prepared is null)return;IsWorking=true;committing=true;
        try{await session.ExecuteAsync(new Commit(prepared.Snapshot,generation));CloseRequested?.Invoke(this,EventArgs.Empty);}
        catch(Exception ex){IsStale=true;Invalidate();Status=ex.Message;}
        finally{committing=false;IsWorking=false;}
    }
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task SaveReferenceAsync()
    {
        if(Selection is null)return;IsWorking=true;committing=true;
        try
        {
            if(session.Generation!=generation)throw new StaleDocumentException();
            // Run through a generation-guarded command: reselect is an explicit document edit.
            await session.ExecuteAsync(new Register(Selection,generation));CloseRequested?.Invoke(this,EventArgs.Empty);
        }
        catch(Exception ex){Status=ex.Message;}
        finally{committing=false;IsWorking=false;}
    }
    [RelayCommand] private void Cancel(){Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    public ValueTask DisposeAsync()=>new(disposal??=DisposeCore());
    private async Task DisposeCore()
    {
        disposed=true;cancellation?.Cancel();session.Changed-=OnChanged;
        try{await Task.WhenAll(new[]{PreviewCommand.ExecutionTask,ConfirmCommand.ExecutionTask,SaveReferenceCommand.ExecutionTask,InspectReferenceCommand.ExecutionTask}.OfType<Task>());}
        finally{prepared?.Dispose();prepared=null;baseline.Dispose();}
    }
    private sealed class Commit(DocumentSnapshot snapshot,long generation):ICadDocumentCommand
    {
        public string Name=>Strings.LocalFeatureTitle;
        public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {token.ThrowIfCancellationRequested();if(context.Generation!=generation)throw new StaleDocumentException();return Task.FromResult(new PreparedDocumentEdit(snapshot));}
    }
    private sealed class Register(TopologyReference reference,long generation):ICadDocumentCommand
    {
        public string Name=>Strings.SaveReference;
        public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {if(context.Generation!=generation)throw new StaleDocumentException();return new UpsertTopologyReferenceCommand(reference).PrepareAsync(context,token);}
    }
}
