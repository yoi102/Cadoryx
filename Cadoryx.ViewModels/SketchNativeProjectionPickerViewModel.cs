using System.Collections.ObjectModel;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.ViewModels;

public interface ISketchNativeProjectionHost { Task ShowAsync(SketchNativeProjectionPickerViewModel picker); }
public sealed record SketchNativeSourceChoice(FeatureId Feature,BodyId Body,string Label);

public sealed partial class SketchNativeProjectionPickerViewModel:ObservableObject,IAsyncDisposable
{
    private readonly SketchEditorViewModel parent;
    private readonly IGeometryKernel kernel;
    private readonly DocumentCapture capture;
    private readonly long generation;
    private CancellationTokenSource? cancellation;
    private Task pickTask=Task.CompletedTask;
    private Task? disposal;
    private bool disposed;
    private ExactSketchEdgeProjection? evidence;
    public DocumentSnapshot Snapshot=>capture.Snapshot;
    public IAssetStore Assets {get;}
    public ObservableCollection<SketchNativeSourceChoice> Sources {get;}=[];
    public CadScene Scene=>SelectedSource is {} source?new(Snapshot.Id,Snapshot.StateId,
        [new(new OccurrencePath(Snapshot.Id,[]),source.Body,Snapshot.Features[source.Feature].Result,RigidTransform3d.Identity,
            Snapshot.Features[source.Feature].Result.Kind==BodyKind.Wire?0xFF243B53:0xFF94B8D8,PreserveSourceStyles:true)]):new(Snapshot.Id,Snapshot.StateId,[]);
    public ExactTopologySelection? SelectedEdge=>evidence?.Selection;
    public string Title=>Strings.NativeEdgeProjection;
    public string CurveSummary=>evidence is null?Strings.NativeEdgeProjectionHint:evidence.Curve.Freeform is {} freeform?string.Format(Strings.NativeFreeformProjectionReady,freeform.Degree,freeform.Poles.Length):evidence.Curve.Ellipse is {} ellipse?
        string.Format(Strings.NativeConicProjectionReady,ellipse.Principal().MajorRadius,ellipse.Principal().MinorRadius,ellipse.SweepAngle*180/Math.PI):Strings.NativeLineProjectionReady;
    public bool CanUse=>!disposed&&!IsWorking&&parent.NativeProjectionCurrent(generation)&&evidence is not null;
    public bool CanReplace=>parent.SelectedEntities.Count==1&&parent.Sketch.NativeProjections.Any(p=>p.TargetEntity==parent.SelectedEntities[0]);
    internal bool IsDisposed=>disposed;
    public event EventHandler? SceneChanged;
    public event EventHandler? CloseRequested;
    [ObservableProperty] private SketchNativeSourceChoice? selectedSource;
    [ObservableProperty] private bool linked=true;
    [ObservableProperty] private bool replaceSelected;
    [ObservableProperty] private bool isWorking;
    [ObservableProperty] private string status=Strings.NativeEdgeProjectionHint;
    internal SketchNativeProjectionPickerViewModel(SketchEditorViewModel parent,CadDocumentSession session,IGeometryKernel kernel)
    {
        this.parent=parent;this.kernel=kernel;capture=session.Capture();generation=session.Generation;Assets=session.Assets;
        try
        {
            if(!parent.NativeProjectionCurrent(generation))throw new CadValidationException(Strings.Stale);
            foreach(var feature in Snapshot.Features.Values.Where(f=>f.PartId==parent.Sketch.PartId&&!f.IsStale&&!f.IsSuppressed).OrderBy(f=>f.Name))
                Sources.Add(new(feature.Id,feature.OutputBodyId,feature.Name));
            SelectedSource=Sources.FirstOrDefault();if(Sources.Count==0)Status=Strings.NativeProjectionNoSource;
            parent.PropertyChanged+=ParentChanged;
        }
        catch{capture.Dispose();throw;}
    }
    private void ParentChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(SketchEditorViewModel.IsStale) or nameof(SketchEditorViewModel.CanEdit))
        {
            Notify();if(!parent.NativeProjectionCurrent(generation)){cancellation?.Cancel();Status=Strings.Stale;CloseRequested?.Invoke(this,EventArgs.Empty);}
        }
    }
    partial void OnSelectedSourceChanged(SketchNativeSourceChoice? value)
    {cancellation?.Cancel();evidence=null;Notify();OnPropertyChanged(nameof(Scene));SceneChanged?.Invoke(this,EventArgs.Empty);}
    partial void OnIsWorkingChanged(bool value)=>Notify();
    private void Notify(){OnPropertyChanged(nameof(CanUse));OnPropertyChanged(nameof(SelectedEdge));OnPropertyChanged(nameof(CurveSummary));UseCommand.NotifyCanExecuteChanged();}
    public void RejectPick(string reason){Status=reason;}
    public Task PickExactAsync(ExactTopologySelection selection)
    {
        if(disposed||IsWorking)return Task.CompletedTask;
        return pickTask=ResolveAsync(selection);
    }
    private async Task ResolveAsync(ExactTopologySelection selection)
    {
        if(!parent.NativeProjectionCurrent(generation)||selection.FeatureId!=SelectedSource?.Feature){Status=Strings.Stale;return;}
        evidence=null;IsWorking=true;cancellation?.Cancel();var cancel=new CancellationTokenSource();cancellation=cancel;
        var source=SelectedSource;
        try
        {
            if(kernel is not ISketchEdgeProjector projector)throw new NotSupportedException(Strings.NativeProjectionUnsupported);
            var result=await projector.ProjectEdgeAsync(Snapshot,parent.Sketch.PartId,selection,parent.Sketch.Plane,Assets,cancel.Token);
            if(disposed||cancel.IsCancellationRequested||!parent.NativeProjectionCurrent(generation)||source!=SelectedSource)return;
            await projector.ConfirmEdgeProjectionAsync(result,Assets,cancel.Token);
            if(disposed||cancel.IsCancellationRequested||!parent.NativeProjectionCurrent(generation)||source!=SelectedSource)return;
            evidence=result;Status=Strings.NativeProjectionReady;Notify();SceneChanged?.Invoke(this,EventArgs.Empty);
        }
        catch(OperationCanceledException){if(!disposed)Status=Strings.NativeEdgeProjectionHint;}
        catch(CadValidationException){if(!disposed)Status=Strings.NativeProjectionRejected;}
        catch(Exception e){if(!disposed)Status=e.Message;}
        finally{IsWorking=false;cancel.Dispose();if(ReferenceEquals(cancellation,cancel))cancellation=null;}
    }
    [RelayCommand(CanExecute=nameof(CanUse))] private async Task UseAsync()
    {
        if(evidence is not {} selected||!CanUse)return;IsWorking=true;var cancel=new CancellationTokenSource();cancellation=cancel;
        try
        {
            await ((ISketchEdgeProjector)kernel).ConfirmEdgeProjectionAsync(selected,Assets,cancel.Token);
            if(disposed||cancel.IsCancellationRequested||!parent.NativeProjectionCurrent(generation)||evidence!=selected)return;
            parent.AcceptNativeProjection(selected,Linked,ReplaceSelected);
            CloseRequested?.Invoke(this,EventArgs.Empty);
        }
        catch(OperationCanceledException){}
        catch(CadValidationException){if(!disposed)Status=Strings.NativeProjectionRejected;}
        catch(Exception e){if(!disposed)Status=e.Message;}
        finally{IsWorking=false;cancel.Dispose();if(ReferenceEquals(cancellation,cancel))cancellation=null;}
    }
    [RelayCommand] private void Cancel()=>CloseRequested?.Invoke(this,EventArgs.Empty);
    public ValueTask DisposeAsync()=>new(disposal??=DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        disposed=true;parent.PropertyChanged-=ParentChanged;cancellation?.Cancel();Notify();
        try{await Task.WhenAll(pickTask,UseCommand.ExecutionTask??Task.CompletedTask);}
        finally{capture.Dispose();}
    }
}

public partial class SketchEditorViewModel
{
    private SketchNativeProjectionPickerViewModel? nativePicker;
    public ISketchNativeProjectionHost? NativeProjectionHost {get;set;}
    internal bool NativeProjectionCurrent(long expectedGeneration)=>CanEdit&&document.Session.Generation==expectedGeneration&&expectedGeneration==generation;
    public SketchNativeProjectionPickerViewModel CreateNativeProjectionPicker()
    {
        if(!NativeProjectionCurrent(generation))throw new CadValidationException(Strings.Stale);
        if(nativePicker is {IsDisposed:false})throw new InvalidOperationException("Close the existing edge picker first.");
        ApplyMetadata();return nativePicker=new(this,document.Session,kernel);
    }
    internal void AcceptNativeProjection(ExactSketchEdgeProjection evidence,bool linked,bool replace)
    {
        if(!NativeProjectionCurrent(generation))throw new CadValidationException(Strings.Stale);
        if(replace&&(SelectedEntities.Count!=1||Sketch.NativeProjections.All(p=>p.TargetEntity!=SelectedEntities[0])))
            throw new CadValidationException(Strings.SelectNativeProjectionToReplace);
        var id=Draft.ProjectNative(evidence,linked,replace?SelectedEntities[0]:null);Invalidate();Refresh();Select(id);Status=Strings.DraftChanged;
    }
    [RelayCommand] private async Task ProjectNativeEdgeAsync()
    {
        if(!CanEdit||NativeProjectionHost is null)return;
        try{await using var picker=CreateNativeProjectionPicker();await NativeProjectionHost.ShowAsync(picker);}
        catch(Exception e){Status=ErrorMessage(e);}
    }
}
