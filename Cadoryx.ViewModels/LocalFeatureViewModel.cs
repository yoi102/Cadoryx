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
public sealed record LocalBoxChoice(FeatureId FeatureId,BodyId BodyId,string Label,FeatureId OriginBoxFeatureId)
{
    public bool IsChained=>FeatureId!=OriginBoxFeatureId;
}
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
    private ExactTopologySelection? exactEdge;
    private ExactTopologySelection? supportFace;
    public ExactTopologySelection? ExactEdge=>exactEdge;
    public ExactTopologySelection? SupportFace=>supportFace;
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
    public bool IsReselecting=>editingFeature is {} id&&Snapshot.Features[id].IsStale&&
        Snapshot.Features[id].Recipe is HistoryFilletRecipe or HistoryChamferRecipe;
    public FeatureId? ReselectSourceFeatureId=>IsReselecting?Snapshot.Features[editingFeature!.Value].Inputs[0]:null;
    public bool CanChooseSource=>CanEdit&&!IsEditing;
    public bool IsBoundEditing=>editingFeature is {} id&&Snapshot.Features[id].Recipe is HistoryFilletRecipe or HistoryChamferRecipe;
    public bool IsChainedSource=>SelectedBox?.IsChained==true;
    public bool CanChangeOperation=>CanEdit&&!IsBoundEditing;
    public bool CanPickAdditionalEdges=>CanEdit&&!IsBoundEditing&&!IsChainedSource&&HasPrimaryEdge;
    public bool CanSaveReference=>CanChooseSource&&!IsChainedSource;
    public bool CanPreview=>CanEdit&&(IsReselecting?exactEdge is not null:IsEditing||Selection?.Kind==TopologyKind.Edge||exactEdge is not null);
    public bool IsChamfer=>Operation==LocalFeatureOperation.Chamfer;
    public bool RequiresSupportFace=>IsChamfer&&(IsChainedSource||IsReselecting);
    public bool IsFillet=>Operation==LocalFeatureOperation.Fillet;
    public bool HasPrimaryEdge=>IsEditing||Selection?.Kind==TopologyKind.Edge||exactEdge is not null;
    public string PrimaryEdgeLabel=>IsReselecting&&exactEdge is {} reselected?ExactEdgeLabel(reselected.FullTopologyIndex):
        IsEditing&&editingFeature is {} id&&Snapshot.Features[id].Recipe is LocalFeatureRecipe local
        ?local.First+" / "+local.Second:IsBoundEditing&&Snapshot.Features[editingFeature!.Value].TopologyBinding is {} binding
        ?binding.Origin is {} origin?origin.Boundary+" / "+origin.SecondBoundary:ExactEdgeLabel(binding.FullTopologyIndex):
        Selection?.SecondBoundary is {} second?Selection.Boundary+" / "+second:exactEdge is {} exact?ExactEdgeLabel(exact.FullTopologyIndex):"—";
    private static string ExactEdgeLabel(int index)=>string.Format(Strings.ResourceManager.GetString("LocalExactEdge")??"Edge #{0}",index);
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
    public string SourceBodyLabel=>Strings.ResourceManager.GetString("LocalSourceBody")??"Source body";
    public string ChainedSourceHint=>Strings.ResourceManager.GetString("LocalChainedHint")??"Pick an edge; for chamfer also pick its support face.";
    public string SupportFaceLabel=>supportFace is {} face
        ?string.Format(Strings.ResourceManager.GetString("LocalSupportFaceSelected")??"Support face #{0}",face.FullTopologyIndex)
        :Strings.ResourceManager.GetString("LocalSupportFaceHint")??"Select a support face";
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
            if(!Snapshot.Features.TryGetValue(id,out var feature)||feature.Recipe is not (LocalFeatureRecipe or HistoryFilletRecipe or HistoryChamferRecipe)||
                feature.IsStale&&feature.Recipe is LocalFeatureRecipe||
                feature.Inputs.Length!=1||!Snapshot.Features.TryGetValue(feature.Inputs[0],out var source)||source.IsStale)
            {baseline.Dispose();throw new CadValidationException("Select a current local feature output to edit.");}
            if(feature.Recipe is LocalFeatureRecipe local)
            {
                operation=local.Operation;size=local.Size;useTwoDistances=local.SecondDistance is not null;secondDistance=local.SecondDistance??local.Size;
                useEndRadius=local.EndRadius is not null;endRadius=local.EndRadius??local.Size;
                SetEdgeChoices(local.First,local.Second,local.AdditionalEdges);
            }
            else if(feature.Recipe is HistoryFilletRecipe bound)
            {
                operation=LocalFeatureOperation.Fillet;size=bound.Radius;useEndRadius=bound.EndRadius is not null;endRadius=bound.EndRadius??bound.Radius;
                if(feature.TopologyBinding?.Origin is {} origin)SetEdgeChoices(origin.Boundary,origin.SecondBoundary!.Value,0);
            }
            else if(feature.Recipe is HistoryChamferRecipe chamfer)
            {
                operation=LocalFeatureOperation.Chamfer;size=chamfer.Distance;useTwoDistances=chamfer.SecondDistance is not null;
                secondDistance=chamfer.SecondDistance??chamfer.Distance;
                if(feature.TopologyBinding?.Origin is {} origin)SetEdgeChoices(origin.Boundary,origin.SecondBoundary!.Value,0);
            }
        }
        foreach(var body in Snapshot.Bodies.Values.Where(b=>b.Producer is not null))
            if(BoxAncestor(body.Producer!.Value) is {} origin)
                Boxes.Add(new(body.Producer.Value,body.Id,body.Name,origin));
        foreach(var reference in Snapshot.TopologyReferences.Values)
            References.Add(new(reference,reference.Id.Value.ToString("N")[..8]+" · "+reference.Kind+" · "+reference.Boundary+(reference.SecondBoundary is {} second?" / "+second:"")));
        session.Changed+=OnChanged;SelectedBox=IsEditing?null:Boxes.FirstOrDefault();
        Status=IsReselecting?Strings.ResourceManager.GetString("LocalReselectHint")??"Pick an edge on the current upstream result; for chamfer also pick its support face.":
            IsEditing?Strings.EditingFeatureStatus:Boxes.Count==0?Strings.NoBoxOutput:Strings.LocalFeaturePick;
        if(IsEditing)ShowSource();
    }
    private FeatureId? BoxAncestor(FeatureId featureId)
    {
        var visited=new HashSet<FeatureId>();
        for(int depth=0;depth<32&&visited.Add(featureId);depth++)
        {
            if(!Snapshot.Features.TryGetValue(featureId,out var feature)||feature.IsStale)return null;
            if(feature.Recipe is BoxRecipe)return featureId;
            if(feature.Recipe is not (LocalFeatureRecipe or HistoryFilletRecipe or HistoryChamferRecipe)||feature.Inputs.Length!=1)return null;
            featureId=feature.Inputs[0];
        }
        return null;
    }
    private void OnChanged(object? sender,DocumentChangeSet e)
    {if(!committing){IsStale=true;Invalidate();Status=Strings.LocalFeatureStale;} }
    public void Pick(BoxBoundary first,BoxBoundary? second)
    {
        if(!CanChooseSource||SelectedBox is null)return;
        if(second is {} picked&&AddEdgesOnPick&&!IsChainedSource&&Selection?.SecondBoundary is not null)
        {
            var choice=EdgeChoices.Single(c=>c.First==first&&c.Second==picked);
            if(!choice.IsPrimary)choice.IsSelected=!choice.IsSelected;
            Status=Strings.Selected+" "+PrimaryEdgeLabel+" + "+EdgeChoices.Count(c=>c.IsSelected&&!c.IsPrimary);
            return;
        }
        exactEdge=null;supportFace=null;OnPropertyChanged(nameof(ExactEdge));OnPropertyChanged(nameof(SupportFace));OnPropertyChanged(nameof(SupportFaceLabel));
        Selection=TopologyReference.Box(Snapshot,SelectedBox.OriginBoxFeatureId,first,second);
        if(!IsChainedSource&&SelectedReference is {} previous)Selection=Selection with{Id=previous.Reference.Id,Policy=previous.Reference.Policy};
        Status=Strings.Selected+" "+first+(second is {} s?" / "+s:"");
    }
    public void PickExact(ExactTopologySelection exact)
    {
        if(!CanEdit||!(SelectedBox is {IsChained:true}||IsReselecting))return;
        exact.Validate();
        var source=Snapshot.Features[IsReselecting?Snapshot.Features[editingFeature!.Value].Inputs[0]:SelectedBox!.FeatureId];
        if(exact.DocumentId!=Snapshot.Id||exact.FeatureId!=source.Id||exact.Revision!=source.Result.Revision||
            exact.Asset!=source.Result.AssetId){RejectPick("Exact selection belongs to another output.");return;}
        if(exact.Kind==HistoryShapeKind.Face&&Operation==LocalFeatureOperation.Chamfer&&
            (exactEdge is not null||Selection?.Kind==TopologyKind.Edge))
        {
            supportFace=exact;OnPropertyChanged(nameof(SupportFace));OnPropertyChanged(nameof(SupportFaceLabel));
            Invalidate();Status=SupportFaceLabel;return;
        }
        if(exact.Kind!=HistoryShapeKind.Edge){RejectPick("Select an edge first.");return;}
        Selection=null;exactEdge=exact;supportFace=null;
        OnPropertyChanged(nameof(ExactEdge));OnPropertyChanged(nameof(SupportFace));OnPropertyChanged(nameof(SupportFaceLabel));
        Invalidate();Notify();Status=Strings.Selected+" "+PrimaryEdgeLabel;
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
    public void RejectPick(string message)
    {
        Selection=null;exactEdge=null;supportFace=null;
        OnPropertyChanged(nameof(ExactEdge));OnPropertyChanged(nameof(SupportFace));OnPropertyChanged(nameof(SupportFaceLabel));
        Invalidate();Notify();Status=message;
    }
    public BoxRecipe? Box=>SelectedBox is {} b?Snapshot.Features[b.OriginBoxFeatureId].Recipe as BoxRecipe:null;
    public void ShowSource()
    {
        var feature=editingFeature is {} id?Snapshot.Features[id]:null;
        var upstream=IsReselecting?Snapshot.Features[feature!.Inputs[0]]:null;
        var bodyId=upstream?.OutputBodyId??feature?.OutputBodyId??SelectedBox?.BodyId;
        var geometry=upstream?.Result??feature?.Result??(bodyId is {} b?Snapshot.Bodies[b].Geometry:null);
        Scene=bodyId is {} output&&geometry is {} shape
            ?new(Snapshot.Id,Snapshot.StateId,[new(new OccurrencePath(Snapshot.Id,[]),output,shape,RigidTransform3d.Identity,0xFF94B8D8)]):null;
        SceneChanged?.Invoke(this,EventArgs.Empty);
    }
    partial void OnSelectedBoxChanged(LocalBoxChoice? value)
    {
        Selection=null;
        exactEdge=null;supportFace=null;
        if(value?.IsChained==true){Operation=LocalFeatureOperation.Fillet;AddEdgesOnPick=false;UseTwoDistances=false;}
        Invalidate();OnPropertyChanged(nameof(Box));OnPropertyChanged(nameof(IsChainedSource));OnPropertyChanged(nameof(RequiresSupportFace));OnPropertyChanged(nameof(CanChangeOperation));OnPropertyChanged(nameof(CanPickAdditionalEdges));
    }
    partial void OnSelectionKindChanged(TopologyKind value)
    {
        if(!IsChainedSource&&!IsReselecting)Selection=null;
        Invalidate();ShowSource();
    }
    partial void OnOperationChanged(LocalFeatureOperation value){OnPropertyChanged(nameof(IsChamfer));OnPropertyChanged(nameof(IsFillet));OnPropertyChanged(nameof(RequiresSupportFace));Invalidate();}
    partial void OnSizeChanged(double value)=>Invalidate();
    partial void OnUseTwoDistancesChanged(bool value)=>Invalidate();
    partial void OnSecondDistanceChanged(double value)=>Invalidate();
    partial void OnUseEndRadiusChanged(bool value)=>Invalidate();
    partial void OnEndRadiusChanged(double value)=>Invalidate();
    partial void OnSelectionChanged(TopologyReference? value)
    {
        if(value?.SecondBoundary is {} second)SetEdgeChoices(value.Boundary,second,0);
        else if(!IsEditing){updatingEdges=true;foreach(var choice in EdgeChoices){choice.IsPrimary=false;choice.IsSelected=false;}updatingEdges=false;OnPropertyChanged(nameof(HasPrimaryEdge));OnPropertyChanged(nameof(PrimaryEdgeLabel));}
        Invalidate();Notify();OnPropertyChanged(nameof(CanPickAdditionalEdges));
    }
    partial void OnSelectedReferenceChanged(TopologyChoice? value){Selection=null;Invalidate();Status=Strings.LocalFeaturePick;}
    partial void OnIsWorkingChanged(bool value)=>Notify();
    partial void OnIsStaleChanged(bool value)=>Notify();
    private void Notify(){OnPropertyChanged(nameof(CanEdit));OnPropertyChanged(nameof(CanChooseSource));OnPropertyChanged(nameof(CanChangeOperation));OnPropertyChanged(nameof(CanPickAdditionalEdges));OnPropertyChanged(nameof(CanSaveReference));OnPropertyChanged(nameof(CanPreview));OnPropertyChanged(nameof(CanConfirm));PreviewCommand.NotifyCanExecuteChanged();ConfirmCommand.NotifyCanExecuteChanged();SaveReferenceCommand.NotifyCanExecuteChanged();InspectReferenceCommand.NotifyCanExecuteChanged();ReselectCommand.NotifyCanExecuteChanged();}
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
            ICadDocumentCommand command;
            if(IsReselecting)
            {
                if(exactEdge is null)throw new CadValidationException("Reselect an edge on the current upstream result.");
                command=new RebindExactLocalFeatureCommand(editingFeature!.Value,exactEdge,supportFace,Size,
                    EffectiveSecondDistance(),EffectiveEndRadius());
            }
            else if(editingFeature is {} id)
                command=Snapshot.Features[id].Recipe switch
                {
                    LocalFeatureRecipe local=>new RecomputeCommand(id,local with{Operation=Operation,Size=Size,SecondDistance=EffectiveSecondDistance(),AdditionalEdges=AdditionalEdges,EndRadius=EffectiveEndRadius()}),
                    HistoryFilletRecipe bound when Operation==LocalFeatureOperation.Fillet=>new RecomputeCommand(id,bound with{Radius=Size,EndRadius=EffectiveEndRadius()}),
                    HistoryChamferRecipe chamfer when Operation==LocalFeatureOperation.Chamfer=>new RecomputeCommand(id,chamfer with{Distance=Size,SecondDistance=EffectiveSecondDistance()}),
                    _=>throw new CadValidationException("Unsupported local feature edit.")
                };
            else if(IsChainedSource)
            {
                if(AdditionalEdges!=0||Operation==LocalFeatureOperation.Fillet&&UseTwoDistances||
                    Operation==LocalFeatureOperation.Chamfer&&UseEndRadius)
                    throw new CadValidationException("Invalid bound local feature parameters.");
                var target=SelectedBox!.FeatureId;
                if(Operation==LocalFeatureOperation.Chamfer&&supportFace is null)
                    throw new CadValidationException("Select the chamfer support face on this result.");
                if(exactEdge is {} picked)
                    command=new ExactLocalFeatureCommand(picked,Operation,Size,supportFace,EffectiveSecondDistance(),EffectiveEndRadius());
                else
                {
                    var traced=await ((ITopologyHistoryResolver)kernel).TraceAsync(Snapshot,Selection!,target,Assets,cancel.Token);
                    if(traced.Status!=HistoryResolutionStatus.Resolved||traced.Target is not {Kind:HistoryShapeKind.Edge} exact)
                        throw new CadValidationException("Edge history is not uniquely verified: "+traced.Diagnostic);
                    command=Operation==LocalFeatureOperation.Fillet
                        ?new HistoryFilletCommand(Selection!,target,Size,exact,EffectiveEndRadius())
                        :new HistoryChamferCommand(Selection!,target,exact,supportFace!,Size,EffectiveSecondDistance());
                }
            }
            else command=new LocalFeatureCommand(Selection!,Operation,Size,EffectiveSecondDistance(),AdditionalEdges,EffectiveEndRadius());
            var result=await UpdateAssociatedSections.PrepareCommandAsync(command,new(Snapshot,generation,Assets,kernel),cancel.Token);
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
    [RelayCommand(CanExecute=nameof(CanSaveReference))] private async Task SaveReferenceAsync()
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
