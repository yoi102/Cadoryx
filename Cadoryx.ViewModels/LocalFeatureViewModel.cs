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
public sealed partial class LocalEdgeChoice(BoxBoundary first,BoxBoundary second) : ObservableObject
{
    public BoxBoundary First=>first;
    public BoxBoundary Second=>second;
    public string Label=>first+" / "+second;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isPrimary;
}

public partial class LocalFeatureViewModel : ObservableObject,IAsyncDisposable
{
    private readonly CadDocumentSession session;
    private readonly IGeometryKernel kernel;
    private readonly DocumentCapture baseline;
    private readonly long generation;
    private readonly FeatureId? editingFeature;
    private PreparedDocumentEdit? prepared;
    private CancellationTokenSource? cancellation;
    private bool disposed,committing;
    private bool updatingEdges;
    private Task? disposal;
    public ObservableCollection<LocalBoxChoice> Boxes {get;}=[];
    public ObservableCollection<TopologyChoice> References {get;}=[];
    public ObservableCollection<LocalEdgeChoice> EdgeChoices {get;}=[];
    public IReadOnlyList<LocalOption<LocalFeatureOperation>> Operations {get;}=Enum.GetValues<LocalFeatureOperation>().Select(v=>new LocalOption<LocalFeatureOperation>(v,v==LocalFeatureOperation.Fillet?Strings.Fillet:Strings.Chamfer)).ToArray();
    public IReadOnlyList<LocalOption<TopologyKind>> Kinds {get;}=Enum.GetValues<TopologyKind>().Select(v=>new LocalOption<TopologyKind>(v,v==TopologyKind.Face?Strings.Face:Strings.Edge)).ToArray();
    public DocumentSnapshot Snapshot=>baseline.Snapshot;
    public IAssetStore Assets=>session.Assets;
    public CadScene? Scene {get;private set;}
    public bool CanEdit=>!disposed&&!IsWorking&&!IsStale&&session.Snapshot.Extensions.IsDefaultOrEmpty;
    public bool IsEditing=>editingFeature is not null;
    public bool CanChooseSource=>CanEdit&&!IsEditing;
    public bool CanPreview=>CanEdit&&(IsEditing||Selection?.Kind==TopologyKind.Edge);
    public bool IsChamfer=>Operation==LocalFeatureOperation.Chamfer;
    public bool IsFillet=>Operation==LocalFeatureOperation.Fillet;
    public bool HasPrimaryEdge=>IsEditing||Selection?.Kind==TopologyKind.Edge;
    public string PrimaryEdgeLabel=>IsEditing&&editingFeature is {} id&&Snapshot.Features[id].Recipe is LocalFeatureRecipe local
        ?local.First+" / "+local.Second:Selection?.SecondBoundary is {} second?Selection.Boundary+" / "+second:"—";
    public int AdditionalEdges=>EdgeChoices.Where(c=>c.IsSelected&&!c.IsPrimary).Aggregate(0,(mask,c)=>mask|LocalFeatureRecipe.EdgeBit(c.First,c.Second));
    public IEnumerable<TopologyReference> HighlightEdges()
    {
        if(Selection is not {} primary)yield break;
        yield return primary;
        foreach(var choice in EdgeChoices.Where(c=>c.IsSelected&&!c.IsPrimary))
            yield return primary with{Boundary=choice.First,SecondBoundary=choice.Second};
    }
    public string TwoDistancesLabel=>Strings.ResourceManager.GetString("TwoDistances")??"Two distances";
    public string SecondDistanceLabel=>Strings.ResourceManager.GetString("SecondDistance")??"Second distance (mm)";
    public string VariableRadiusLabel=>Strings.ResourceManager.GetString("LocalVariableRadius")??"Linear variable radius";
    public string EndRadiusLabel=>Strings.ResourceManager.GetString("LocalEndRadius")??"End radius (mm)";
    public string AddEdgesOnPickLabel=>Strings.ResourceManager.GetString("LocalAddEdgesOnPick")??"Click to add or remove edges";
    public string EdgeListLabel=>Strings.ResourceManager.GetString("LocalEdgeList")??"Box edges (primary edge is fixed)";
    public bool CanConfirm=>CanEdit&&prepared is not null;
    public bool HasCandidate=>prepared is not null;
    public event EventHandler? SceneChanged;
    public event EventHandler? CloseRequested;
    [ObservableProperty] private LocalBoxChoice? selectedBox;
    [ObservableProperty] private TopologyKind selectionKind=TopologyKind.Edge;
    [ObservableProperty] private LocalFeatureOperation operation;
    [ObservableProperty] private double size=2;
    [ObservableProperty] private bool useTwoDistances;
    [ObservableProperty] private double secondDistance=2;
    [ObservableProperty] private bool useEndRadius;
    [ObservableProperty] private double endRadius=2;
    [ObservableProperty] private bool addEdgesOnPick;
    [ObservableProperty] private string status="";
    [ObservableProperty] private bool isWorking,isStale;
    [ObservableProperty] private TopologyReference? selection;
    [ObservableProperty] private TopologyChoice? selectedReference;

    public LocalFeatureViewModel(CadDocumentSession session,IGeometryKernel kernel,FeatureId? editingFeature=null)
    {
        this.session=session;this.kernel=kernel;baseline=session.Capture();generation=session.Generation;this.editingFeature=editingFeature;
        foreach(var first in Enum.GetValues<BoxBoundary>())
            foreach(var second in Enum.GetValues<BoxBoundary>())
                if((int)first/2<(int)second/2)
                {
                    var choice=new LocalEdgeChoice(first,second);choice.PropertyChanged+=OnEdgeChoiceChanged;EdgeChoices.Add(choice);
                }
        if(editingFeature is {} id)
        {
            if(!Snapshot.Features.TryGetValue(id,out var feature)||feature.Recipe is not LocalFeatureRecipe local||feature.IsStale||
                feature.Inputs.Length!=1||!Snapshot.Features.TryGetValue(feature.Inputs[0],out var source)||source.IsStale)
            {baseline.Dispose();throw new CadValidationException("Select a current local feature output to edit.");}
            operation=local.Operation;size=local.Size;useTwoDistances=local.SecondDistance is not null;secondDistance=local.SecondDistance??local.Size;
            useEndRadius=local.EndRadius is not null;endRadius=local.EndRadius??local.Size;
            SetEdgeChoices(local.First,local.Second,local.AdditionalEdges);
        }
        foreach(var body in Snapshot.Bodies.Values.Where(b=>b.Producer is {} f&&Snapshot.Features[f].Recipe is BoxRecipe))
            Boxes.Add(new(body.Producer!.Value,body.Id,body.Name));
        foreach(var reference in Snapshot.TopologyReferences.Values)
            References.Add(new(reference,reference.Id.Value.ToString("N")[..8]+" · "+reference.Kind+" · "+reference.Boundary+(reference.SecondBoundary is {} second?" / "+second:"")));
        session.Changed+=OnChanged;SelectedBox=IsEditing?null:Boxes.FirstOrDefault();
        Status=IsEditing?Strings.EditingFeatureStatus:Boxes.Count==0?Strings.NoBoxOutput:Strings.LocalFeaturePick;
        if(IsEditing)ShowSource();
    }
    private void OnChanged(object? sender,DocumentChangeSet e)
    {if(!committing){IsStale=true;Invalidate();Status=Strings.LocalFeatureStale;} }
    public void Pick(BoxBoundary first,BoxBoundary? second)
    {
        if(!CanChooseSource||SelectedBox is null)return;
        if(second is {} picked&&AddEdgesOnPick&&Selection?.SecondBoundary is not null)
        {
            var choice=EdgeChoices.Single(c=>c.First==first&&c.Second==picked);
            if(!choice.IsPrimary)choice.IsSelected=!choice.IsSelected;
            Status=Strings.Selected+" "+PrimaryEdgeLabel+" + "+EdgeChoices.Count(c=>c.IsSelected&&!c.IsPrimary);
            return;
        }
        Selection=TopologyReference.Box(Snapshot,SelectedBox.FeatureId,first,second);
        if(SelectedReference is {} previous)Selection=Selection with{Id=previous.Reference.Id,Policy=previous.Reference.Policy};
        Status=Strings.Selected+" "+first+(second is {} s?" / "+s:"");
    }
    private void SetEdgeChoices(BoxBoundary first,BoxBoundary second,int mask)
    {
        updatingEdges=true;
        foreach(var choice in EdgeChoices)
        {
            choice.IsPrimary=choice.First==first&&choice.Second==second;
            choice.IsSelected=choice.IsPrimary||(mask&LocalFeatureRecipe.EdgeBit(choice.First,choice.Second))!=0;
        }
        updatingEdges=false;
        OnPropertyChanged(nameof(PrimaryEdgeLabel));OnPropertyChanged(nameof(HasPrimaryEdge));
    }
    private void OnEdgeChoiceChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
    {
        if(updatingEdges||e.PropertyName!=nameof(LocalEdgeChoice.IsSelected))return;
        if(sender is LocalEdgeChoice choice&&choice.IsPrimary){choice.IsSelected=true;return;}
        Invalidate();
    }
    public void RejectPick(string message){Selection=null;Status=message;}
    public BoxRecipe? Box=>SelectedBox is {} b?Snapshot.Features[b.FeatureId].Recipe as BoxRecipe:null;
    public void ShowSource()
    {
        var feature=editingFeature is {} id?Snapshot.Features[id]:null;
        var bodyId=feature?.OutputBodyId??SelectedBox?.BodyId;
        var geometry=feature?.Result??(bodyId is {} b?Snapshot.Bodies[b].Geometry:null);
        Scene=bodyId is {} output&&geometry is {} shape
            ?new(Snapshot.Id,Snapshot.StateId,[new(new OccurrencePath(Snapshot.Id,[]),output,shape,RigidTransform3d.Identity,0xFF94B8D8)]):null;
        SceneChanged?.Invoke(this,EventArgs.Empty);
    }
    partial void OnSelectedBoxChanged(LocalBoxChoice? value){Selection=null;Invalidate();ShowSource();OnPropertyChanged(nameof(Box));}
    partial void OnSelectionKindChanged(TopologyKind value){Selection=null;Invalidate();ShowSource();}
    partial void OnOperationChanged(LocalFeatureOperation value){OnPropertyChanged(nameof(IsChamfer));OnPropertyChanged(nameof(IsFillet));Invalidate();}
    partial void OnSizeChanged(double value)=>Invalidate();
    partial void OnUseTwoDistancesChanged(bool value)=>Invalidate();
    partial void OnSecondDistanceChanged(double value)=>Invalidate();
    partial void OnUseEndRadiusChanged(bool value)=>Invalidate();
    partial void OnEndRadiusChanged(double value)=>Invalidate();
    partial void OnSelectionChanged(TopologyReference? value)
    {
        if(value?.SecondBoundary is {} second)SetEdgeChoices(value.Boundary,second,0);
        else if(!IsEditing){updatingEdges=true;foreach(var choice in EdgeChoices){choice.IsPrimary=false;choice.IsSelected=false;}updatingEdges=false;OnPropertyChanged(nameof(HasPrimaryEdge));OnPropertyChanged(nameof(PrimaryEdgeLabel));}
        Invalidate();Notify();
    }
    partial void OnSelectedReferenceChanged(TopologyChoice? value){Selection=null;Invalidate();Status=Strings.LocalFeaturePick;}
    partial void OnIsWorkingChanged(bool value)=>Notify();
    partial void OnIsStaleChanged(bool value)=>Notify();
    private void Notify(){OnPropertyChanged(nameof(CanEdit));OnPropertyChanged(nameof(CanChooseSource));OnPropertyChanged(nameof(CanPreview));OnPropertyChanged(nameof(CanConfirm));PreviewCommand.NotifyCanExecuteChanged();ConfirmCommand.NotifyCanExecuteChanged();SaveReferenceCommand.NotifyCanExecuteChanged();InspectReferenceCommand.NotifyCanExecuteChanged();ReselectCommand.NotifyCanExecuteChanged();}
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
    [RelayCommand(CanExecute=nameof(CanChooseSource))] private void Reselect(){Selection=null;Invalidate();Status=Strings.LocalFeaturePick;}

    [RelayCommand(CanExecute=nameof(CanChooseSource))] private async Task InspectReferenceAsync()
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
            var command=editingFeature is {} id
                ?(ICadDocumentCommand)new RecomputeCommand(id,((LocalFeatureRecipe)Snapshot.Features[id].Recipe) with{Operation=Operation,Size=Size,SecondDistance=EffectiveSecondDistance(),AdditionalEdges=AdditionalEdges,EndRadius=EffectiveEndRadius()})
                :new LocalFeatureCommand(Selection!,Operation,Size,EffectiveSecondDistance(),AdditionalEdges,EffectiveEndRadius());
            var result=await command.PrepareAsync(new(Snapshot,generation,Assets,kernel),cancel.Token);
            if(disposed||IsStale||cancel.IsCancellationRequested||session.Generation!=generation){result.Dispose();return;}
            try
            {
                result.Snapshot.Validate();
                var edited=editingFeature is {} target?result.Snapshot.Features[target]:null;
                if(edited?.IsStale==true)throw new CadValidationException("The edited feature became stale.");
                var body=edited is null?result.Snapshot.Bodies.Values.Single(b=>!Snapshot.Bodies.ContainsKey(b.Id)):null;
                var outputId=edited?.OutputBodyId??body!.Id;
                var geometry=edited?.Result??body!.Geometry;
                Scene=new(Snapshot.Id,result.Snapshot.StateId,[new(new OccurrencePath(Snapshot.Id,[]),outputId,geometry,RigidTransform3d.Identity,0xFF80CBAA)]);
            }
            catch{result.Dispose();throw;}
            prepared=result;
            SceneChanged?.Invoke(this,EventArgs.Empty);Status=Strings.PreviewReadyStatus;
        }
        catch(OperationCanceledException){Status=Strings.LocalFeaturePick;}
        catch(Exception ex){Status=ex.Message;}
        finally{cancellation=null;IsWorking=false;}
    }
    private double? EffectiveSecondDistance()=>IsChamfer&&UseTwoDistances?SecondDistance:null;
    private double? EffectiveEndRadius()=>IsFillet&&UseEndRadius?EndRadius:null;
    [RelayCommand(CanExecute=nameof(CanConfirm))] private async Task ConfirmAsync()
    {
        if(prepared is null)return;IsWorking=true;committing=true;
        try{await session.ExecuteAsync(new Commit(prepared.Snapshot,generation));CloseRequested?.Invoke(this,EventArgs.Empty);}
        catch(Exception ex){IsStale=true;Invalidate();Status=ex.Message;}
        finally{committing=false;IsWorking=false;}
    }
    [RelayCommand(CanExecute=nameof(CanChooseSource))] private async Task SaveReferenceAsync()
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
