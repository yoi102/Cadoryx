using System.Collections.ObjectModel;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public interface IHistoryQueryHost { Task ShowAsync(HistoryQueryViewModel editor); }
public sealed record HistoryFeatureChoice(FeatureId Id,string Label);
public sealed record HistoryStepRow(int Number,string Feature,string Input,string State);

public sealed class HistoryQueryLabels
{
    private static string Get(string key)=>Strings.ResourceManager.GetString(key)??key;
    public string Title=>Get("HistoryQueryTitle");
    public string Saved=>Get("HistoryQuerySaved");
    public string New=>Get("HistoryQueryNew");
    public string Name=>Get("HistoryQueryName");
    public string Source=>Get("HistoryQuerySource");
    public string Target=>Get("HistoryQueryTarget");
    public string Reselect=>Get("HistoryQueryReselect");
    public string Save=>Get("HistoryQuerySave");
    public string Delete=>Get("HistoryQueryDelete");
    public string Analyze=>Get("HistoryQueryAnalyze");
    public string Steps=>Get("HistoryQuerySteps");
    public string Pending=>Get("HistoryQueryPending");
    public string Done=>Get("HistoryQueryDone");
    public string Stopped=>Get("HistoryQueryStopped");
    public string Ready=>Get("HistoryQueryReady");
    public string Changed=>Get("HistoryQueryChanged");
    public string NoSource=>Get("HistoryQueryNoSource");
    public string Missing=>Get("HistoryQueryMissing");
    public string Incomplete=>Get("HistoryQueryIncomplete");
    public string SaveFirst=>Get("HistoryQuerySaveFirst");
    public string Radius=>Get("HistoryFilletRadius");
    public string CreateFillet=>Get("HistoryFilletCreate");
    public string StaleConsumer=>Get("HistoryFilletStaleConsumer");
    public string Rebind=>Get("HistoryFilletRebind");
    public string ConfirmFirst=>Get("HistoryFilletConfirmFirst");
    public string Resolution(HistoryResolutionStatus status)=>Get("HistoryStatus"+status);
}

public partial class HistoryQueryViewModel : ObservableObject,IAsyncDisposable
{
    private readonly CadDocumentSession session;
    private readonly ITopologyHistoryResolver resolver;
    private readonly CancellationTokenSource lifetime=new();
    private CancellationTokenSource? analysis;
    private int epoch;
    private bool disposed,updating;
    private TopologyReference? draftSource;
    private FeatureId? draftTarget;
    private (HistoryQueryId Query,DocumentStateId State,HistoryTarget Target)? confirmed;
    public HistoryQueryLabels Labels {get;}=new();
    public ObservableCollection<HistoryQuery> Queries {get;}=[];
    public ObservableCollection<TopologyChoice> Sources {get;}=[];
    public ObservableCollection<HistoryFeatureChoice> Targets {get;}=[];
    public ObservableCollection<HistoryStepRow> Steps {get;}=[];
    public ObservableCollection<HistoryFeatureChoice> StaleConsumers {get;}=[];
    public string DraftSourceLabel=>draftSource is null?Labels.NoSource:Describe(draftSource);
    public string DraftTargetLabel=>draftTarget is {} id?Targets.FirstOrDefault(t=>t.Id==id)?.Label??Labels.Missing:Labels.Missing;
    public bool CanEdit=>!disposed&&!session.IsClosing&&session.Snapshot.Extensions.IsDefaultOrEmpty;

    [ObservableProperty] private HistoryQuery? selectedQuery;
    [ObservableProperty] private TopologyChoice? selectedSource;
    [ObservableProperty] private HistoryFeatureChoice? selectedTarget;
    [ObservableProperty] private string queryName="";
    [ObservableProperty] private string status="";
    [ObservableProperty] private string resultSummary="";
    [ObservableProperty] private bool isWorking;
    [ObservableProperty] private double radius=1;
    [ObservableProperty] private HistoryFeatureChoice? selectedStaleConsumer;

    public HistoryQueryViewModel(CadDocumentSession session,IGeometryKernel kernel)
    {
        this.session=session;resolver=kernel as ITopologyHistoryResolver??throw new NotSupportedException("Kernel does not support history diagnostics.");
        RefreshLists();SelectedQuery=Queries.FirstOrDefault();session.Changed+=OnChanged;Status=Labels.Ready;
    }
    private static string Describe(TopologyReference r)=>r.Id.Value.ToString("N")[..8]+" · "+r.Kind+" · "+r.Boundary+
        (r.SecondBoundary is {} b?" / "+b:"");
    private void RefreshLists(HistoryQueryId? desired=null)
    {
        var snapshot=session.Snapshot;
        Queries.Clear();foreach(var query in snapshot.HistoryQueries.Values.OrderBy(q=>q.Name).ThenBy(q=>q.Id.Value))Queries.Add(query);
        Sources.Clear();foreach(var source in snapshot.TopologyReferences.Values.Where(r=>r.Policy==TopologyRebindPolicy.Semantic).OrderBy(r=>r.Id.Value))Sources.Add(new(source,Describe(source)));
        Targets.Clear();foreach(var feature in snapshot.Features.Values.OrderBy(f=>f.Name).ThenBy(f=>f.Id.Value))Targets.Add(new(feature.Id,feature.Name+" · "+feature.Id.Value.ToString("N")[..8]));
        StaleConsumers.Clear();foreach(var feature in snapshot.Features.Values.Where(f=>f.IsStale&&f.Recipe is HistoryFilletRecipe)
            .OrderBy(f=>f.Name).ThenBy(f=>f.Id.Value))StaleConsumers.Add(new(feature.Id,feature.Name+" · "+feature.Id.Value.ToString("N")[..8]));
        SelectedStaleConsumer=null;
        if(desired is {} id)SelectedQuery=Queries.FirstOrDefault(q=>q.Id==id);
    }
    partial void OnSelectedQueryChanged(HistoryQuery? value)
    {
        if(updating)return;
        CancelAnalysis();Steps.Clear();ResultSummary="";
        if(value is null){draftSource=null;draftTarget=null;QueryName="";SelectedTarget=null;SelectedSource=null;}
        else
        {
            draftSource=value.Source;draftTarget=value.TargetFeatureId;QueryName=value.Name;
            SelectedSource=null;SelectedTarget=Targets.FirstOrDefault(t=>t.Id==draftTarget);
        }
        OnPropertyChanged(nameof(DraftSourceLabel));OnPropertyChanged(nameof(DraftTargetLabel));
    }
    partial void OnSelectedTargetChanged(HistoryFeatureChoice? value)
    {if(value is not null){draftTarget=value.Id;OnPropertyChanged(nameof(DraftTargetLabel));}}
    private void OnChanged(object? sender,DocumentChangeSet change)
    {
        CancelAnalysis();Steps.Clear();ResultSummary="";Status=Labels.Changed;
        var selected=SelectedQuery?.Id;updating=true;
        try{RefreshLists();}finally{updating=false;}
        SelectedQuery=selected is {} id?Queries.FirstOrDefault(q=>q.Id==id):null;
        if(SelectedQuery is null){draftSource=null;draftTarget=null;QueryName="";SelectedTarget=null;}
        else
        {draftSource=SelectedQuery.Source;draftTarget=SelectedQuery.TargetFeatureId;QueryName=SelectedQuery.Name;SelectedTarget=Targets.FirstOrDefault(t=>t.Id==draftTarget);}
        OnPropertyChanged(nameof(DraftSourceLabel));OnPropertyChanged(nameof(DraftTargetLabel));OnPropertyChanged(nameof(CanEdit));
    }
    private void CancelAnalysis()
    {
        ++epoch;confirmed=null;var previous=analysis;analysis=null;
        try{previous?.Cancel();}catch(ObjectDisposedException){/* The completed run already released it. */}
    }
    [RelayCommand] private void NewQuery()
    {
        SelectedQuery=null;draftSource=null;draftTarget=null;QueryName="";SelectedSource=null;SelectedTarget=null;
        OnPropertyChanged(nameof(DraftSourceLabel));OnPropertyChanged(nameof(DraftTargetLabel));Status=Labels.Ready;
    }
    [RelayCommand] private void ReselectSource()
    {
        if(!CanEdit||SelectedSource is null)return;
        draftSource=SelectedSource.Reference;
        OnPropertyChanged(nameof(DraftSourceLabel));
    }
    [RelayCommand] private async Task SaveAsync()
    {
        if(!CanEdit||draftSource is null||draftTarget is null||string.IsNullOrWhiteSpace(QueryName))
        {Status=Labels.Incomplete;return;}
        var query=new HistoryQuery(SelectedQuery?.Id??HistoryQueryId.New(),QueryName,draftSource,draftTarget.Value);
        try{await session.ExecuteAsync(new UpsertHistoryQueryCommand(query),lifetime.Token);SelectedQuery=Queries.FirstOrDefault(q=>q.Id==query.Id);Status=Labels.Ready;}
        catch(Exception ex) when(ex is not OperationCanceledException){Status=ex.Message;}
    }
    [RelayCommand] private async Task DeleteAsync()
    {
        if(!CanEdit||SelectedQuery is not {} query)return;
        try{await session.ExecuteAsync(new RemoveHistoryQueryCommand(query.Id),lifetime.Token);Status=Labels.Ready;}
        catch(Exception ex) when(ex is not OperationCanceledException){Status=ex.Message;}
    }
    [RelayCommand] private async Task AnalyzeAsync()
    {
        if(disposed)return;
        if(SelectedQuery is not {} query){Status=Labels.SaveFirst;return;}
        CancelAnalysis();int run=epoch;using var current=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);analysis=current;
        var token=current.Token;IsWorking=true;Steps.Clear();ResultSummary="";
        try
        {
            using var capture=session.Capture();var snapshot=capture.Snapshot;
            var plan=TopologyHistoryPaths.Plan(snapshot,query.Source.FeatureId,query.TargetFeatureId,token);
            var inspection=await TopologyHistoryInspection.InspectAsync(snapshot,query.Source,query.TargetFeatureId,session.Assets,resolver,token);
            if(token.IsCancellationRequested||disposed||run!=epoch||!inspection.IsCurrent(session.Snapshot)||SelectedQuery?.Id!=query.Id)return;
            var result=inspection.Result;
            if(result.Status==HistoryResolutionStatus.Resolved&&result.Target is {Kind:HistoryShapeKind.Edge} exact)
                confirmed=(query.Id,snapshot.StateId,exact);
            for(int i=0;i<plan.Steps.Length;i++)
            {
                var step=plan.Steps[i];var name=snapshot.Features.GetValueOrDefault(step.Feature)?.Name??Labels.Missing;
                Steps.Add(new(i+1,name,"#"+(step.SourceArgument+1),i<result.CompletedSteps?Labels.Done:i==result.CompletedSteps?Labels.Stopped:Labels.Pending));
            }
            ResultSummary=$"{Labels.Resolution(result.Status)} · {result.CompletedSteps}/{result.PathLength} · {result.Diagnostic}"+
                (result.StoppedAt is {} stop?" · "+(snapshot.Features.GetValueOrDefault(stop)?.Name??stop.Value.ToString("N")):"")+
                (result.Target is {} target?" · "+target.Kind+" #"+target.FullTopologyIndex:"");
            Status=Labels.Ready;
        }
        catch(OperationCanceledException){}
        catch(ObjectDisposedException){Status=Labels.Changed;}
        catch(Exception ex){if(run==epoch&&!disposed)Status=ex.Message;}
        finally{if(ReferenceEquals(analysis,current))analysis=null;if(run==epoch)IsWorking=false;}
    }
    private bool TryConfirmed(out HistoryQuery query,out HistoryTarget target)
    {
        query=SelectedQuery!;target=null!;
        if(!CanEdit||IsWorking||query is null||confirmed is not {} evidence||evidence.Query!=query.Id||
            evidence.State!=session.Snapshot.StateId||draftSource!=query.Source||draftTarget!=query.TargetFeatureId||QueryName!=query.Name)
        {Status=Labels.ConfirmFirst;return false;}
        target=evidence.Target;return true;
    }
    [RelayCommand] private async Task CreateFilletAsync()
    {
        if(!TryConfirmed(out var query,out var target))return;
        try{await session.ExecuteAsync(new HistoryFilletCommand(query.Source,query.TargetFeatureId,Radius,target),lifetime.Token);Status=Labels.Ready;}
        catch(Exception ex) when(ex is not OperationCanceledException){Status=ex.Message;}
    }
    [RelayCommand] private async Task RebindFilletAsync()
    {
        if(!TryConfirmed(out var query,out var target)||SelectedStaleConsumer is not {} choice)return;
        var doc=session.Snapshot;
        if(!doc.Features.TryGetValue(choice.Id,out var feature)||!feature.IsStale||
            feature.TopologyBinding?.TargetFeatureId!=query.TargetFeatureId){Status=Labels.ConfirmFirst;return;}
        try{await session.ExecuteAsync(new RebindHistoryFilletCommand(choice.Id,query.Source,target),lifetime.Token);Status=Labels.Ready;}
        catch(Exception ex) when(ex is not OperationCanceledException){Status=ex.Message;}
    }
    public async ValueTask DisposeAsync()
    {
        if(disposed)return;disposed=true;session.Changed-=OnChanged;CancelAnalysis();lifetime.Cancel();
        var running=AnalyzeCommand.ExecutionTask;
        if(running is not null)try{await running;}catch(OperationCanceledException){}
        analysis?.Dispose();lifetime.Dispose();
    }
}
