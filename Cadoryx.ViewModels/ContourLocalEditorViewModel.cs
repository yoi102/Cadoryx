using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using Cadoryx.Rendering;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public interface IContourLocalEditorHost {Task ShowAsync(ContourLocalEditorViewModel editor);}
public partial class ContourLocalEditorViewModel:ObservableObject,IAsyncDisposable
{
    private readonly CadDocumentViewModel document;
    private readonly IGeometryKernel kernel;
    private readonly IContourLocalResolver resolver;
    private readonly DocumentCapture baseline;
    private readonly long generation;
    private readonly FeatureId? editing;
    private PreparedDocumentEdit? prepared;
    private CancellationTokenSource? cancellation;
    private Task? selectionTask,disposal;
    private bool loading,disposed,committing;
    private ContourProgramViewModel? replacement;
    private ExactTopologySelection? pendingSeed;
    public LocalFeatureOperation Operation{get;}
    public bool IsFillet=>Operation==LocalFeatureOperation.Fillet;
    public bool IsChamfer=>!IsFillet;
    public string Title=>IsFillet?Strings.ContourFillet:Strings.ContourChamfer;
    public string Hint=>WantsSupportFace?Strings.ContourPickSupport:Strings.ContourPickSeed;
    public bool CanEdit=>!disposed&&!IsWorking&&!IsStale;
    public bool CanChooseSource=>CanEdit&&editing is null;
    public bool CanConfirm=>CanEdit&&prepared is not null;
    public bool HasCandidate=>prepared is not null;
    public DocumentSnapshot Snapshot=>baseline.Snapshot;
    public IAssetStore Assets=>document.Session.Assets;
    public CadScene? Scene{get;private set;}
    public Task? SelectionTask=>selectionTask;
    public ObservableCollection<FaceSourceChoice> Sources{get;}=[];
    public ObservableCollection<ContourProgramViewModel> Programs{get;}=[];
    public IReadOnlyList<GuidedChoice<ContourLawKind>> Laws{get;}=[new(ContourLawKind.Constant,Strings.ContourConstant),new(ContourLawKind.Linear,Strings.ContourLinear),new(ContourLawKind.Smooth,Strings.ContourSmooth),new(ContourLawKind.Sampled,Strings.ContourSampled),new(ContourLawKind.Spline,Strings.ContourSpline)];
    public IEnumerable<ExactTopologySelection> Highlights=>Programs.Where(p=>SelectedSource is {} s&&p.Seed.Revision==Snapshot.Features[s.Feature].Result.Revision)
        .SelectMany(p=>p.Chain.Members.Select(m=>p.Seed with{FullTopologyIndex=m.EdgeIndex}));
    [ObservableProperty] private FaceSourceChoice? selectedSource;
    [ObservableProperty] private ContourProgramViewModel? selectedProgram;
    [ObservableProperty] private bool isWorking,isStale,wantsSupportFace;
    [ObservableProperty] private string objectName="",status="";
    public event EventHandler? SceneChanged,CloseRequested;
    public ContourLocalEditorViewModel(CadDocumentViewModel document,IGeometryKernel kernel,LocalFeatureOperation operation,FeatureId? feature=null)
    {
        if(!Enum.IsDefined(operation))throw new ArgumentOutOfRangeException(nameof(operation));
        this.document=document;this.kernel=kernel;resolver=kernel as IContourLocalResolver??throw new NotSupportedException(Strings.UnsupportedFeatureEdit);
        Operation=operation;editing=feature;generation=document.Session.Generation;baseline=document.Session.Capture();loading=true;
        try
        {
            if(feature is {} id)
            {
                var f=Snapshot.Features[id];if(f.Recipe is not ContourLocalRecipe recipe||recipe.Operation!=operation||f.IsSuppressed||f.Inputs.Length!=1)throw new CadValidationException(Strings.UnsupportedFeatureEdit);
                var source=Snapshot.Features[f.Inputs[0]];if(source.IsStale||source.IsSuppressed)throw new CadValidationException(Strings.ReferenceStale);
                Sources.Add(new(source.Id,source.OutputBodyId,source.PartId,source.Name));SelectedSource=Sources[0];ObjectName=f.Name;
                foreach(var p in recipe.Programs)AddProgram(new(p));SelectedProgram=Programs.FirstOrDefault();Status=f.IsStale?Strings.ContourReselectHint:Hint;
            }
            else
            {
                foreach(var body in Snapshot.Bodies.Values.Where(b=>b.Geometry.Kind==BodyKind.Solid&&b.Producer is not null&&!Snapshot.Layers[b.LayerId].IsLocked&&!Snapshot.ExternalParts.ContainsKey(b.PartId)))
                {var source=Snapshot.Features[body.Producer!.Value];if(!source.IsStale&&!source.IsSuppressed)Sources.Add(new(source.Id,body.Id,body.PartId,Snapshot.Definitions[body.PartId].Name+" · "+body.Name));}
                ObjectName=Title;Status=Hint;
            }
            document.InvalidatePreview();document.Session.Changed+=OnDocumentChanged;document.Detaching+=OnDetached;ShowSource();
        }
        catch{foreach(var row in Programs)row.Dispose();baseline.Dispose();throw;}
        finally{loading=false;}
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(e.PropertyName is nameof(Status) or nameof(CanEdit) or nameof(CanChooseSource) or nameof(CanConfirm) or nameof(HasCandidate) or nameof(Highlights) or nameof(Hint) or nameof(SelectedProgram))return;
        if(e.PropertyName is nameof(IsWorking) or nameof(IsStale)){Notify();return;}
        if(e.PropertyName==nameof(WantsSupportFace)){OnPropertyChanged(nameof(Hint));SceneChanged?.Invoke(this,EventArgs.Empty);return;}
        Edited();
    }
    partial void OnSelectedSourceChanged(FaceSourceChoice? value)
    {if(loading)return;ClearRows();replacement=null;pendingSeed=null;WantsSupportFace=false;Edited();}
    private void AddProgram(ContourProgramViewModel row){row.Changed+=OnRowChanged;Programs.Add(row);}
    private void ClearRows(){foreach(var row in Programs){row.Changed-=OnRowChanged;row.Dispose();}Programs.Clear();SelectedProgram=null;}
    private void OnRowChanged(object? sender,EventArgs e)=>Edited();
    private void Edited(){if(loading)return;Invalidate();Notify();}
    private void Notify()
    {
        foreach(string p in new[]{nameof(CanEdit),nameof(CanChooseSource),nameof(CanConfirm),nameof(HasCandidate),nameof(Highlights)})OnPropertyChanged(p);
        foreach(var c in new IRelayCommand[]{PreviewCommand,ConfirmCommand,RemoveProgramCommand,ClearProgramsCommand,ReselectProgramCommand,MoveProgramUpCommand,MoveProgramDownCommand})c.NotifyCanExecuteChanged();
    }
    private void Invalidate(){cancellation?.Cancel();var previous=prepared;prepared=null;document.SetSketchPreview(null);ShowSource();previous?.Dispose();}
    public void ShowSource()
    {Scene=SelectedSource is {} source?new(Snapshot.Id,Snapshot.StateId,[new(new OccurrencePath(Snapshot.Id,[]),source.Body,Snapshot.Features[source.Feature].Result,RigidTransform3d.Identity,0xFF94B8D8)]):new(Snapshot.Id,Snapshot.StateId,[]);SceneChanged?.Invoke(this,EventArgs.Empty);}
    public void RejectPick(string message)=>Status=message;
    public Task PickExactAsync(ExactTopologySelection pick)
    {if(!CanEdit||HasCandidate)return Task.CompletedTask;return selectionTask=PickCoreAsync(pick);}
    private async Task PickCoreAsync(ExactTopologySelection pick)
    {
        if(!CanEdit||HasCandidate)return;CancellationTokenSource? cancel=null;
        try
        {
            pick.Validate();var source=SelectedSource??throw new CadValidationException(Strings.FaceChooseSource);var geometry=Snapshot.Features[source.Feature].Result;
            if(pick.DocumentId!=Snapshot.Id||pick.FeatureId!=source.Feature||pick.Revision!=geometry.Revision||pick.Asset!=geometry.AssetId||
                pick.Kind!=(WantsSupportFace?HistoryShapeKind.Face:HistoryShapeKind.Edge))throw new CadValidationException(Strings.WrongReferenceContext);
            if(IsChamfer&&!WantsSupportFace){pendingSeed=pick;WantsSupportFace=true;Status=Hint;return;}
            var seed=WantsSupportFace?pendingSeed!:pick;var support=WantsSupportFace?pick:null;
            IsWorking=true;cancel=new();cancellation=cancel;
            var chain=await resolver.DiscoverContourAsync(Snapshot,seed,Operation,support,Assets,cancel.Token);
            if(disposed||cancel.IsCancellationRequested||IsStale||document.Session.Generation!=generation)return;
            if(Programs.Any(p=>p!=replacement&&p.Chain.Members.Any(m=>chain.Members.Any(c=>c.EdgeIndex==m.EdgeIndex))))throw new CadValidationException(Strings.ContourOverlap);
            // Discovery has finished; applying the row must not cancel its own operation.
            cancellation=null;pendingSeed=null;WantsSupportFace=false;
            if(replacement is {} row){row.Reselect(seed,support,chain);SelectedProgram=row;replacement=null;}
            else{var added=new ContourProgramViewModel(new(Guid.NewGuid(),seed,chain,IsFillet?new ConstantContourRadius(1):null,support,IsChamfer?1:null));AddProgram(added);SelectedProgram=added;}
            Edited();Status=Hint;
        }
        catch(OperationCanceledException){if(!IsStale)Status=Hint;}
        catch(Exception e){Status=string.Format(Strings.ParameterError,e.Message);}
        finally{if(cancel is not null){IsWorking=false;cancel.Dispose();if(ReferenceEquals(cancellation,cancel))cancellation=null;}}
    }
    [RelayCommand(CanExecute=nameof(CanEdit))] private void RemoveProgram()
    {if(SelectedProgram is {} row){row.Changed-=OnRowChanged;row.Dispose();Programs.Remove(row);if(replacement==row){replacement=null;pendingSeed=null;WantsSupportFace=false;}SelectedProgram=Programs.FirstOrDefault();Edited();}}
    [RelayCommand(CanExecute=nameof(CanEdit))] private void ClearPrograms(){ClearRows();replacement=null;pendingSeed=null;WantsSupportFace=false;Edited();}
    [RelayCommand(CanExecute=nameof(CanEdit))] private void ReselectProgram(){if(SelectedProgram is {} row){replacement=row;pendingSeed=null;WantsSupportFace=false;Invalidate();Notify();Status=Strings.ContourReselectHint;}}
    [RelayCommand(CanExecute=nameof(CanEdit))] private void MoveProgramUp(){if(SelectedProgram is {} p&&Programs.IndexOf(p)>0){Programs.Move(Programs.IndexOf(p),Programs.IndexOf(p)-1);Edited();}}
    [RelayCommand(CanExecute=nameof(CanEdit))] private void MoveProgramDown(){if(SelectedProgram is {} p&&Programs.IndexOf(p)<Programs.Count-1){Programs.Move(Programs.IndexOf(p),Programs.IndexOf(p)+1);Edited();}}
    public ContourLocalRecipe BuildRecipe()
    {
        if(pendingSeed is not null||replacement is not null)throw new CadValidationException(Strings.ContourCompletePick);
        var source=SelectedSource??throw new CadValidationException(Strings.FaceChooseSource);var recipe=new ContourLocalRecipe(Snapshot.Features[source.Feature].Result,Operation,Programs.Select(p=>p.Value(Operation)).ToImmutableArray());
        recipe.Validate();if(!recipe.HasCurrentSource(Snapshot))throw new CadValidationException(Strings.ContourReselectHint);return recipe;
    }
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task PreviewAsync()
    {
        Invalidate();if(document.Session.Generation!=generation){IsStale=true;Status=Strings.Stale;return;}IsWorking=true;var cancel=new CancellationTokenSource();cancellation=cancel;
        try
        {
            var recipe=BuildRecipe();ICadDocumentCommand command=editing is {} id?new ReselectContourLocalCommand(id,recipe):new AddContourLocalCommand(SelectedSource!.Body,recipe,ObjectName.Trim());
            var candidate=await UpdateAssociatedSections.PrepareCommandAsync(command,new(Snapshot,generation,Assets,kernel),cancel.Token);
            if(disposed||cancel.IsCancellationRequested||IsStale||document.Session.Generation!=generation){candidate.Dispose();return;}
            try
            {
                candidate.Snapshot.Validate();var result=editing is {} target?candidate.Snapshot.Features[target]:candidate.Snapshot.Features.Values.Single(f=>!Snapshot.Features.ContainsKey(f.Id));
                if(result.IsStale)throw new CadValidationException(Strings.ReferenceStale);
                Scene=new(Snapshot.Id,candidate.Snapshot.StateId,[new(new OccurrencePath(Snapshot.Id,[]),result.OutputBodyId,result.Result,RigidTransform3d.Identity,0xFF80CBAA)]);document.SetSketchPreview(candidate.Snapshot);
            }
            catch{candidate.Dispose();throw;}
            prepared=candidate;SceneChanged?.Invoke(this,EventArgs.Empty);Status=Strings.ParameterPreviewReady;
        }
        catch(OperationCanceledException){if(!IsStale)Status=Hint;}
        catch(Exception e){Status=string.Format(Strings.ParameterError,e.Message);}
        finally{IsWorking=false;cancel.Dispose();if(ReferenceEquals(cancellation,cancel))cancellation=null;}
    }
    [RelayCommand(CanExecute=nameof(CanConfirm))] private async Task ConfirmAsync()
    {
        if(prepared is null)return;IsWorking=true;committing=true;
        try{await document.Session.ExecuteAsync(new CommitPreparedDocumentCommand(prepared.Snapshot,generation,Title));document.FitView();CloseRequested?.Invoke(this,EventArgs.Empty);}
        catch(Exception e){Invalidate();IsStale=true;Status=string.Format(Strings.ParameterError,e.Message);}
        finally{committing=false;IsWorking=false;}
    }
    [RelayCommand] private void Cancel(){Invalidate();Notify();CloseRequested?.Invoke(this,EventArgs.Empty);}
    private void OnDocumentChanged(object? sender,DocumentChangeSet e){if(!committing){IsStale=true;Invalidate();Status=Strings.Stale;}}
    private void OnDetached(object? sender,EventArgs e){IsStale=true;Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    public ValueTask DisposeAsync()=>new(disposal??=DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        disposed=true;Invalidate();Notify();document.Session.Changed-=OnDocumentChanged;document.Detaching-=OnDetached;foreach(var row in Programs){row.Changed-=OnRowChanged;row.Dispose();}
        try{await Task.WhenAll(new[]{selectionTask,PreviewCommand.ExecutionTask,ConfirmCommand.ExecutionTask}.OfType<Task>());}finally{baseline.Dispose();}
    }
}
