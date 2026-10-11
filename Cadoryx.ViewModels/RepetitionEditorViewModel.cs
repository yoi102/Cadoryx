using System.Collections.Immutable;
using System.ComponentModel;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public interface IRepetitionEditorHost {Task ShowAsync(RepetitionEditorViewModel editor);}
public sealed record RepetitionSourceChoice(BodyId Body,string Label);
public sealed record RepetitionFeatureChoice(FeatureId? Feature,string Label);
public sealed record RepetitionDatumChoice(Guid? Datum,string Label);
public sealed record RepetitionKindChoice(RepetitionKind Value,string Label);
public sealed record RepetitionOutputChoice(RepetitionOutput Value,string Label);
public sealed record RepetitionStyleChoice(RepetitionStylePolicy Value,string Label);
public partial class RepetitionEditorViewModel:ObservableObject,IAsyncDisposable
{
    private readonly CadDocumentViewModel document;
    private readonly IGeometryKernel kernel;
    private readonly DocumentCapture baseline;
    private readonly long generation;
    private PreparedDocumentEdit? prepared;
    private CancellationTokenSource? cancellation;
    private Task? disposal;
    private bool disposed,loading,committing;
    private ImmutableArray<Guid> copyIds=[];
    private RepetitionRecipe? loadedLayout;
    private (double X,double Y,double Z,double Rx,double Ry,double Rz) loadedFrameInputs;
    private double loadedStepInput;
    public IReadOnlyList<RepetitionSourceChoice> Sources {get;}
    public IReadOnlyList<RepetitionFeatureChoice> Features {get;}
    public IReadOnlyList<RepetitionDatumChoice> Datums {get;}
    public IReadOnlyList<RepetitionKindChoice> Kinds {get;}=[new(RepetitionKind.Mirror,Strings.RepetitionMirror),new(RepetitionKind.Linear,Strings.RepetitionLinear),new(RepetitionKind.Circular,Strings.RepetitionCircular)];
    public IReadOnlyList<RepetitionOutputChoice> Outputs {get;}=[new(RepetitionOutput.Compound,Strings.RepetitionCompound),new(RepetitionOutput.Fused,Strings.RepetitionFused)];
    public IReadOnlyList<RepetitionStyleChoice> StylePolicies {get;}=[new(RepetitionStylePolicy.VerifiedInheritance,Strings.RepetitionStyleVerified),new(RepetitionStylePolicy.WholeBody,Strings.RepetitionStyleWholeBody)];
    [ObservableProperty] private RepetitionSourceChoice? selectedSource;
    [ObservableProperty] private RepetitionFeatureChoice? selectedFeature;
    [ObservableProperty] private RepetitionDatumChoice? selectedDatum;
    [ObservableProperty] private RepetitionKind kind=RepetitionKind.Linear;
    [ObservableProperty] private RepetitionOutput output;
    [ObservableProperty] private RepetitionStylePolicy stylePolicy;
    [ObservableProperty] private string objectName=Strings.Repetition;
    [ObservableProperty] private int count=3;
    [ObservableProperty] private double step=20;
    [ObservableProperty] private bool includeSource=true;
    [ObservableProperty] private bool fullCircle;
    [ObservableProperty] private bool replayHole;
    [ObservableProperty] private bool replayExtrusion;
    [ObservableProperty] private double originX,originY,originZ,rotationX,rotationY,rotationZ;
    [ObservableProperty] private bool isWorking,isStale;
    [ObservableProperty] private string status=Strings.RepetitionHint;
    public bool CanEdit=>!disposed&&!IsWorking&&!IsStale;
    public bool CanConfirm=>CanEdit&&prepared is not null;
    public bool IsNew=>SelectedFeature?.Feature is null;
    public bool CanChangeKind=>IsNew&&CanEdit;
    public bool CanSetIncludeSource=>IsMirror&&CanChangeKind;
    public bool CanChooseSource=>IsNew&&CanEdit;
    public bool CanSetReplay=>IsNew&&CanEdit;
    public bool CanSetOutput=>!ReplayHole&&!ReplayExtrusion;
    public bool CanSetStylePolicy=>CanSetOutput;
    public string PresentationSummary
    {
        get
        {
            var current=prepared?.Snapshot??baseline.Snapshot;
            var feature=SelectedFeature?.Feature is {} id?current.Features.GetValueOrDefault(id):
                current.Features.Values.FirstOrDefault(f=>!baseline.Snapshot.Features.ContainsKey(f.Id)&&f.RepetitionPresentation is not null);
            return feature?.RepetitionPresentation is {} proof?string.Format(System.Globalization.CultureInfo.CurrentCulture,Strings.RepetitionStyleEvidence,
                proof.StyledSourceSlots,proof.CopiedResultSlots,proof.SharedResultSlots,proof.ConflictingResultIndices.Length):"";
        }
    }
    public string Hint=>ReplayHole?Strings.RepetitionReplayHoleHint:ReplayExtrusion?Strings.RepetitionReplayExtrusionHint:Strings.RepetitionHint;
    public bool IsMirror=>Kind==RepetitionKind.Mirror;
    public bool IsArray=>!IsMirror;
    public bool IsCircular=>Kind==RepetitionKind.Circular;
    public bool CanSetStep=>!IsMirror&&(!IsCircular||!FullCircle);
    public bool CanSetFrame=>SelectedDatum?.Datum is null;
    public string StepLabel=>IsCircular?Strings.RepetitionAngle:Strings.RepetitionSpacing;
    public string CountLabel=>IsMirror?Strings.RepetitionMirrorCount:Strings.RepetitionCount;
    public string IncludeSourceLabel=>ReplayHole||ReplayExtrusion?Strings.RepetitionIncludeSeed:Strings.RepetitionIncludeSource;
    public string SourceDescription=>SelectedFeature?.Feature is {} id?baseline.Snapshot.Features[baseline.Snapshot.Features[id].Inputs.Single()].Name:"";
    public event EventHandler? CloseRequested;
    public RepetitionEditorViewModel(CadDocumentViewModel document,IGeometryKernel kernel,FeatureId? feature=null)
    {
        this.document=document;this.kernel=kernel;baseline=document.Session.Capture();generation=document.Session.Generation;
        Sources=baseline.Snapshot.Bodies.Values.Where(b=>b.Producer is {} p&&!baseline.Snapshot.Features[p].IsStale&&!baseline.Snapshot.Features[p].IsSuppressed)
            .Select(b=>new RepetitionSourceChoice(b.Id,baseline.Snapshot.Definitions[b.PartId].Name+" · "+b.Name)).ToArray();
        Features=[new(null,Strings.RepetitionNew),..baseline.Snapshot.Features.Values.Where(f=>f.Recipe is RepetitionRecipe or HolePatternRecipe or ExtrusionPatternRecipe&&!f.IsStale&&!f.IsSuppressed).Select(f=>new RepetitionFeatureChoice(f.Id,baseline.Snapshot.Definitions[f.PartId].Name+" · "+f.Name))];
        Datums=[new(null,Strings.RepetitionManualFrame),..baseline.Snapshot.Datums.Planes.Values.Where(p=>!p.IsStale).Select(p=>new RepetitionDatumChoice(p.Id,baseline.Snapshot.Definitions[p.PartId].Name+" · "+p.Name)),
            ..baseline.Snapshot.Datums.Axes.Values.Where(p=>!p.IsStale).Select(p=>new RepetitionDatumChoice(p.Id,baseline.Snapshot.Definitions[p.PartId].Name+" · "+p.Name))];
        SelectedSource=Sources.FirstOrDefault();SelectedDatum=Datums[0];SelectedFeature=Features.FirstOrDefault(f=>f.Feature==feature)??Features[0];
        document.InvalidatePreview();document.Session.Changed+=OnChanged;document.Detaching+=OnDetached;
    }
    partial void OnSelectedFeatureChanged(RepetitionFeatureChoice? value)
    {
        loading=true;
        try
        {
            if(value?.Feature is {} id)
            {
                var f=baseline.Snapshot.Features[id];ReplayHole=f.Recipe is HolePatternRecipe;ReplayExtrusion=f.Recipe is ExtrusionPatternRecipe;
                var r=f.Recipe switch{HolePatternRecipe pattern=>pattern.Layout,ExtrusionPatternRecipe pattern=>pattern.Layout,_=>(RepetitionRecipe)f.Recipe};loadedLayout=r;
                SelectedSource=null;ObjectName=f.Name;Kind=r.Kind;Output=r.Output;StylePolicy=r.StylePolicy;Count=r.Count;Step=r.Kind==RepetitionKind.Circular?r.Step*180/Math.PI:r.Step;loadedStepInput=Step;
                IncludeSource=r.IncludeSource;FullCircle=r.FullCircle;copyIds=r.CopyIds;SelectedDatum=Datums.Single(d=>d.Datum==r.Datum);
                OriginX=r.Frame.Translation.X;OriginY=r.Frame.Translation.Y;OriginZ=r.Frame.Translation.Z;var e=r.Frame.Rotation.ToEulerDegrees();RotationX=e.X;RotationY=e.Y;RotationZ=e.Z;
                loadedFrameInputs=(OriginX,OriginY,OriginZ,RotationX,RotationY,RotationZ);
            }
            else{copyIds=[];loadedLayout=null;ReplayHole=false;ReplayExtrusion=false;StylePolicy=RepetitionStylePolicy.VerifiedInheritance;ObjectName=Strings.Repetition;SelectedSource=Sources.FirstOrDefault();}
        }
        finally{loading=false;Invalidate();Notify();}
    }
    partial void OnKindChanged(RepetitionKind value)
    {if(!loading&&IsNew){copyIds=[];if(value==RepetitionKind.Mirror)Count=IncludeSource?2:1;else if(Count<2)Count=3;}}
    partial void OnIncludeSourceChanged(bool value)
    {if(!loading&&IsMirror)Count=value?2:1;}
    partial void OnReplayHoleChanged(bool value){if(!loading&&value){ReplayExtrusion=false;Output=RepetitionOutput.Fused;}}
    partial void OnReplayExtrusionChanged(bool value){if(!loading&&value){ReplayHole=false;Output=RepetitionOutput.Fused;}}
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(e.PropertyName is nameof(SelectedSource) or nameof(SelectedDatum) or nameof(Kind) or nameof(Output) or nameof(StylePolicy) or nameof(ObjectName) or nameof(Count) or nameof(Step) or nameof(IncludeSource) or nameof(FullCircle) or nameof(ReplayHole) or nameof(ReplayExtrusion)
            or nameof(OriginX) or nameof(OriginY) or nameof(OriginZ) or nameof(RotationX) or nameof(RotationY) or nameof(RotationZ))
        {if(!loading)Invalidate();Notify();}
        else if(e.PropertyName is nameof(IsWorking) or nameof(IsStale))Notify();
    }
    private void Notify()
    {
        foreach(string name in new[]{nameof(CanEdit),nameof(CanConfirm),nameof(IsNew),nameof(CanChangeKind),nameof(CanSetIncludeSource),nameof(CanChooseSource),nameof(CanSetReplay),nameof(CanSetOutput),nameof(Hint),nameof(IsMirror),nameof(IsArray),nameof(IsCircular),nameof(CanSetStep),nameof(CanSetFrame),nameof(StepLabel),nameof(SourceDescription)})OnPropertyChanged(name);
        OnPropertyChanged(nameof(CountLabel));OnPropertyChanged(nameof(IncludeSourceLabel));
        OnPropertyChanged(nameof(CanSetStylePolicy));OnPropertyChanged(nameof(PresentationSummary));
        PreviewCommand.NotifyCanExecuteChanged();ConfirmCommand.NotifyCanExecuteChanged();
    }
    private void Invalidate(){cancellation?.Cancel();prepared?.Dispose();prepared=null;document.SetSketchPreview(null);OnPropertyChanged(nameof(CanConfirm));ConfirmCommand.NotifyCanExecuteChanged();}
    private void OnChanged(object? sender,DocumentChangeSet change){if(!committing){IsStale=true;Invalidate();Status=Strings.Stale;}}
    private void OnDetached(object? sender,EventArgs e){IsStale=true;Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    private ICadDocumentCommand BuildCommand()
    {
        var doc=baseline.Snapshot;FeatureDefinition? edit=SelectedFeature?.Feature is {} id?doc.Features[id]:null;
        var source=edit is null?doc.Bodies.GetValueOrDefault(SelectedSource?.Body??default)??throw new CadValidationException(Strings.RepetitionSelectSource):null;
        var geometry=edit is null?source!.Geometry:doc.Features[edit.Inputs.Single()].Result;var part=edit?.PartId??source!.PartId;
        var datum=SelectedDatum?.Datum;
        int actualCount=IsMirror?(IncludeSource?2:1):Count;
        if(actualCount<1||actualCount>256)throw new CadValidationException(Strings.RepetitionCount);
        var identities=Enumerable.Range(0,actualCount).Select(i=>i<copyIds.Length?copyIds[i]:Guid.NewGuid()).ToImmutableArray();
        var frame=datum is {} did?DatumConstructions.InputFrame(doc,part,did,!IsMirror):
            loadedLayout is not null&&loadedFrameInputs==(OriginX,OriginY,OriginZ,RotationX,RotationY,RotationZ)?loadedLayout.Frame:
                new RigidTransform3d(new(OriginX,OriginY,OriginZ),Quaterniond.FromEulerDegrees(RotationX,RotationY,RotationZ));
        double step=IsMirror||IsCircular&&FullCircle?0:loadedLayout is not null&&Step==loadedStepInput?loadedLayout.Step:IsCircular?Step*Math.PI/180:Step;
        var recipe=new RepetitionRecipe(geometry,Kind,frame,actualCount,step,
            identities,ReplayHole||ReplayExtrusion?RepetitionOutput.Fused:Output,IsMirror?IncludeSource:true,IsCircular&&FullCircle,datum,datum is {} d?DatumConstructions.Revision(doc.Datums,d):null,
            ReplayHole||ReplayExtrusion?RepetitionStylePolicy.VerifiedInheritance:StylePolicy);recipe.Validate();
        if(ReplayExtrusion)
        {
            var seed=edit is null?doc.Features[source!.Producer!.Value]:doc.Features[edit.Inputs.Single()];
            if(seed.Recipe is not ExtrudeRecipe {Options:{Operation:ExtrusionOperation.Fuse or ExtrusionOperation.Cut}} extrusion)throw new CadValidationException(Strings.RepetitionReplaySelectExtrusion);
            var pattern=new ExtrusionPatternRecipe(seed.Id,extrusion,recipe);
            return edit is null?new AddExtrusionPatternCommand(source!.Id,pattern,ObjectName.Trim()):new RecomputeCommand(edit.Id,pattern);
        }
        if(ReplayHole)
        {
            var seed=edit is null?doc.Features[source!.Producer!.Value]:doc.Features[edit.Inputs.Single()];
            if(seed.Recipe is not HoleRecipe hole)throw new CadValidationException(Strings.RepetitionReplaySelectHole);
            var pattern=new HolePatternRecipe(seed.Id,hole,recipe);
            return edit is null?new AddHolePatternCommand(source!.Id,pattern,ObjectName.Trim()):new RecomputeCommand(edit.Id,pattern);
        }
        return edit is null?new AddRepetitionCommand(source!.Id,recipe,ObjectName.Trim()):new RecomputeCommand(edit.Id,recipe);
    }
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task PreviewAsync()
    {
        Invalidate();if(document.Session.Generation!=generation){IsStale=true;Status=Strings.Stale;return;}
        IsWorking=true;var cancel=new CancellationTokenSource();cancellation=cancel;
        try
        {
            var edit=await UpdateAssociatedSections.PrepareCommandAsync(BuildCommand(),new(baseline.Snapshot,generation,document.Session.Assets,kernel),cancel.Token);
            if(cancel.IsCancellationRequested||disposed||IsStale||document.Session.Generation!=generation){edit.Dispose();return;}
            try{edit.Snapshot.Validate();document.SetSketchPreview(edit.Snapshot);}catch{edit.Dispose();throw;}
            prepared=edit;Status=Strings.ParameterPreviewReady;
        }
        catch(OperationCanceledException){if(!IsStale)Status=Strings.RepetitionHint;}
        catch(Exception ex){Status=string.Format(Strings.ParameterError,ex.Message.Contains("STYLES.CONFLICT",StringComparison.Ordinal)?Strings.RepetitionStyleConflict:
            ex.Message.StartsWith("STYLES.",StringComparison.Ordinal)?Strings.RepetitionStyleUnsupported:ex.Message);}
        finally{IsWorking=false;cancel.Dispose();if(ReferenceEquals(cancellation,cancel))cancellation=null;}
    }
    [RelayCommand(CanExecute=nameof(CanConfirm))] private async Task ConfirmAsync()
    {
        if(prepared is null)return;IsWorking=true;committing=true;
        try{await document.Session.ExecuteAsync(new CommitPreparedDocumentCommand(prepared.Snapshot,generation,Strings.Repetition));document.FitView();CloseRequested?.Invoke(this,EventArgs.Empty);}
        catch(Exception ex){Invalidate();IsStale=true;Status=string.Format(Strings.ParameterError,ex.Message);}
        finally{committing=false;IsWorking=false;}
    }
    [RelayCommand] private void Cancel(){Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    public ValueTask DisposeAsync()=>new(disposal??=DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        disposed=true;Invalidate();document.Session.Changed-=OnChanged;document.Detaching-=OnDetached;
        try{await Task.WhenAll(new[]{PreviewCommand.ExecutionTask,ConfirmCommand.ExecutionTask}.OfType<Task>());}finally{baseline.Dispose();}
    }
}
