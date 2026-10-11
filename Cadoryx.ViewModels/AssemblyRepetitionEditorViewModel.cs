using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public interface IAssemblyRepetitionEditorHost {Task ShowAsync(AssemblyRepetitionEditorViewModel editor);}
public sealed record AssemblyPatternSourceChoice(OccurrencePath Path,string Label);
public sealed record AssemblyPatternChoice(Guid? Id,OccurrencePath? Source,string Label);

public partial class AssemblyRepetitionEditorViewModel:ObservableObject,IAsyncDisposable
{
    private readonly CadDocumentViewModel document;private readonly IGeometryKernel kernel;private readonly DocumentCapture baseline;private readonly long generation;
    private PreparedDocumentEdit? prepared;private CancellationTokenSource? cancellation;private Task? disposal;
    private bool disposed,loading,committing;private readonly Guid newId=Guid.NewGuid();
    private AssemblyRepetition? loaded;private (double,double,double,double,double,double) loadedFrame;private double loadedStep;
    public IReadOnlyList<AssemblyPatternSourceChoice> Sources {get;}
    public IReadOnlyList<AssemblyPatternChoice> Layouts {get;}
    public IReadOnlyList<RepetitionKindChoice> Kinds {get;}=[new(RepetitionKind.Linear,Strings.RepetitionLinear),new(RepetitionKind.Circular,Strings.RepetitionCircular),new(RepetitionKind.Mirror,Strings.RepetitionMirror)];
    public IReadOnlyList<RepetitionDatumChoice> Datums {get;private set;}=[];
    [ObservableProperty] private AssemblyPatternSourceChoice? selectedSource;
    [ObservableProperty] private AssemblyPatternChoice? selectedLayout;
    [ObservableProperty] private RepetitionDatumChoice? selectedDatum;
    [ObservableProperty] private string objectName=Strings.AssemblyPattern;
    [ObservableProperty] private RepetitionKind kind=RepetitionKind.Linear;
    [ObservableProperty] private int count=3;
    [ObservableProperty] private double step=20;
    [ObservableProperty] private bool fullCircle,isWorking,isStale;
    [ObservableProperty] private double originX,originY,originZ,rotationX,rotationY,rotationZ;
    [ObservableProperty] private string status=Strings.AssemblyPatternHint;
    public bool CanEdit=>!disposed&&!IsWorking&&!IsStale;
    public bool CanConfirm=>CanEdit&&prepared is not null;
    public bool IsNew=>SelectedLayout?.Id is null;
    public bool CanChooseSource=>IsNew&&CanEdit;
    public bool CanChangeKind=>IsNew&&CanEdit;
    public bool CanDetach=>!IsNew&&CanEdit;
    public bool IsCircular=>Kind==RepetitionKind.Circular;
    public string CountLabel=>Kind==RepetitionKind.Mirror?Strings.AssemblyMirrorCount:Strings.RepetitionCount;
    public bool CanSetCount=>Kind!=RepetitionKind.Mirror;
    public bool CanSetStep=>Kind!=RepetitionKind.Mirror&&(!IsCircular||!FullCircle);
    public bool CanSetFrame=>SelectedDatum?.Datum is null;
    public string StepLabel=>IsCircular?Strings.RepetitionAngle:Strings.RepetitionSpacing;
    public event EventHandler? CloseRequested;

    public AssemblyRepetitionEditorViewModel(CadDocumentViewModel document,IGeometryKernel kernel,OccurrencePath? source=null)
    {
        this.document=document;this.kernel=kernel;baseline=document.Session.Capture();generation=document.Session.Generation;
        var doc=baseline.Snapshot;var generated=doc.AssemblyPatterns.Values.SelectMany(p=>p.CopySlots).ToHashSet();
        if(source?.Slots.IsEmpty==true)source=null;
        if(source is not null&&doc.AssemblyPatterns.Values.FirstOrDefault(p=>p.CopySlots.Contains(source.Slots.Last())) is {} existing)
            source=new(source.DocumentId,source.Slots.Take(source.Slots.Length-1).Append(existing.Source));
        Sources=doc.EnumerateOccurrences().Where(o=>!generated.Contains(o.Path.Slots.Last())).Select(o=>new AssemblyPatternSourceChoice(o.Path,Breadcrumb(doc,o.Path))).ToArray();
        Layouts=[new(null,null,Strings.AssemblyPatternNew),..doc.AssemblyPatterns.Values.OrderBy(p=>p.Name).SelectMany(p=>Sources.Where(s=>s.Path.Slots.Last()==p.Source)
            .Select(s=>new AssemblyPatternChoice(p.Id,s.Path,p.Name+" · "+s.Label)))];
        SelectedSource=Sources.FirstOrDefault(s=>s.Path.Equals(source))??Sources.FirstOrDefault();
        SelectedLayout=Layouts.FirstOrDefault(p=>p.Source?.Equals(source)==true)??Layouts[0];
        document.InvalidatePreview();document.Session.Changed+=OnChanged;document.Detaching+=OnDetached;
    }
    public static string Breadcrumb(DocumentSnapshot doc,OccurrencePath path)
    {
        var names=new List<string>();var owner=doc.RootAssemblyId;
        foreach(var id in path.Slots)
        {
            var assembly=(AssemblyDefinition)doc.Definitions[owner];var slot=assembly.Children.Single(s=>s.Id==id);
            names.Add(slot.Name+(assembly.Children.Count(s=>s.Name==slot.Name)>1?" ("+(assembly.Children.IndexOf(slot)+1)+")":""));owner=slot.DefinitionId;
        }
        return string.Join(" / ",names);
    }
    private void RefreshDatums()
    {
        DefinitionId? part=SelectedSource is {} source?OccurrencePlacement.Resolve(baseline.Snapshot,source.Path).Slot.DefinitionId:null;
        var doc=baseline.Snapshot;Datums=[new(null,Strings.AssemblyPatternManualFrame),
            ..doc.Datums.Planes.Values.Where(p=>p.PartId==part&&!p.IsStale).Select(p=>new RepetitionDatumChoice(p.Id,p.Name)),
            ..doc.Datums.Axes.Values.Where(p=>p.PartId==part&&!p.IsStale).Select(p=>new RepetitionDatumChoice(p.Id,p.Name))];
        OnPropertyChanged(nameof(Datums));SelectedDatum=Datums[0];
    }
    partial void OnSelectedSourceChanged(AssemblyPatternSourceChoice? value){RefreshDatums();if(!loading)Invalidate();}
    partial void OnSelectedLayoutChanged(AssemblyPatternChoice? value)
    {
        loading=true;
        try
        {
            loaded=value?.Id is {} id?baseline.Snapshot.AssemblyPatterns[id]:null;
            if(loaded is not null)
            {
                SelectedSource=Sources.Single(s=>s.Path.Equals(value!.Source));RefreshDatums();ObjectName=loaded.Name;Kind=loaded.Kind;Count=loaded.Count;
                Step=loaded.Kind==RepetitionKind.Circular?loaded.Step*180/Math.PI:loaded.Step;FullCircle=loaded.FullCircle;
                OriginX=loaded.Frame.Translation.X;OriginY=loaded.Frame.Translation.Y;OriginZ=loaded.Frame.Translation.Z;
                var angles=loaded.Frame.Rotation.ToEulerDegrees();RotationX=angles.X;RotationY=angles.Y;RotationZ=angles.Z;
                SelectedDatum=Datums.FirstOrDefault(d=>d.Datum==loaded.Datum)??throw new CadValidationException("Assembly array datum is unavailable.");
                loadedFrame=(OriginX,OriginY,OriginZ,RotationX,RotationY,RotationZ);loadedStep=Step;
            }
            else{ObjectName=Strings.AssemblyPattern;Kind=RepetitionKind.Linear;Count=3;Step=20;FullCircle=false;OriginX=OriginY=OriginZ=RotationX=RotationY=RotationZ=0;RefreshDatums();}
        }
        finally{loading=false;Invalidate();Notify();}
    }
    partial void OnSelectedDatumChanged(RepetitionDatumChoice? value){if(!loading)Invalidate();OnPropertyChanged(nameof(CanSetFrame));}
    partial void OnObjectNameChanged(string value){if(!loading)Invalidate();}
    partial void OnKindChanged(RepetitionKind value){if(!loading){if(value==RepetitionKind.Mirror){Count=2;Step=0;FullCircle=false;}Invalidate();}Notify();}
    partial void OnCountChanged(int value){if(!loading)Invalidate();}
    partial void OnStepChanged(double value){if(!loading)Invalidate();}
    partial void OnFullCircleChanged(bool value){if(!loading)Invalidate();Notify();}
    partial void OnOriginXChanged(double value){if(!loading)Invalidate();}partial void OnOriginYChanged(double value){if(!loading)Invalidate();}partial void OnOriginZChanged(double value){if(!loading)Invalidate();}
    partial void OnRotationXChanged(double value){if(!loading)Invalidate();}partial void OnRotationYChanged(double value){if(!loading)Invalidate();}partial void OnRotationZChanged(double value){if(!loading)Invalidate();}
    partial void OnIsWorkingChanged(bool value)=>Notify();partial void OnIsStaleChanged(bool value)=>Notify();
    private void Notify()
    {
        foreach(string p in new[]{nameof(CanEdit),nameof(CanConfirm),nameof(IsNew),nameof(CanChooseSource),nameof(CanChangeKind),nameof(CanDetach),nameof(IsCircular),nameof(CountLabel),nameof(CanSetCount),nameof(CanSetStep),nameof(CanSetFrame),nameof(StepLabel)})OnPropertyChanged(p);
        PreviewCommand.NotifyCanExecuteChanged();ConfirmCommand.NotifyCanExecuteChanged();DetachCommand.NotifyCanExecuteChanged();
    }
    private void Invalidate(){cancellation?.Cancel();prepared?.Dispose();prepared=null;document.SetSketchPreview(null);Notify();}
    private void OnChanged(object? sender,DocumentChangeSet e){if(!committing){IsStale=true;Invalidate();Status=Strings.Stale;}}
    private void OnDetached(object? sender,EventArgs e){IsStale=true;Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    private ICadDocumentCommand BuildCommand()
    {
        var doc=baseline.Snapshot;var source=SelectedSource??throw new CadValidationException(Strings.AssemblyPatternSelectSource);var placement=OccurrencePlacement.Resolve(doc,source.Path);
        var frame=loaded is not null&&loadedFrame==(OriginX,OriginY,OriginZ,RotationX,RotationY,RotationZ)?loaded.Frame:
            new RigidTransform3d(new(OriginX,OriginY,OriginZ),Quaterniond.FromEulerDegrees(RotationX,RotationY,RotationZ));
        double spacing=IsCircular&&FullCircle?0:loaded is not null&&Step==loadedStep?loaded.Step:IsCircular?Step*Math.PI/180:Step;
        if(Count<2||Count>256)throw new CadValidationException(Strings.RepetitionCount);
        var slots=Enumerable.Range(0,Count-1).Select(i=>loaded is not null&&i<loaded.CopySlots.Length?loaded.CopySlots[i]:ComponentSlotId.New()).ToImmutableArray();
        Guid? datum=SelectedDatum?.Datum;var pattern=new AssemblyRepetition(loaded?.Id??newId,placement.OwnerId,placement.Slot.Id,ObjectName.Trim(),Kind,frame,Count,spacing,slots,
            IsCircular&&FullCircle,datum,datum is {} id?DatumConstructions.Revision(doc.Datums,id):null){Mirrors=loaded?.Mirrors??[]};
        return AssemblyRepetitionCommands.Set(source.Path,pattern);
    }
    [RelayCommand(CanExecute=nameof(CanEdit))] private Task PreviewAsync()=>PrepareAsync(BuildCommand);
    [RelayCommand(CanExecute=nameof(CanDetach))] private Task DetachAsync()=>PrepareAsync(()=>AssemblyRepetitionCommands.Detach(SelectedLayout!.Id!.Value),true);
    private async Task PrepareAsync(Func<ICadDocumentCommand> command,bool detach=false)
    {
        Invalidate();if(document.Session.Generation!=generation){IsStale=true;Status=Strings.Stale;return;}
        IsWorking=true;var cancel=new CancellationTokenSource();cancellation=cancel;
        try
        {
            var edit=await UpdateAssociatedSections.PrepareCommandAsync(command(),new(baseline.Snapshot,generation,document.Session.Assets,kernel),cancel.Token);
            if(cancel.IsCancellationRequested||disposed||IsStale||document.Session.Generation!=generation){edit.Dispose();return;}
            try{edit.Snapshot.Validate();document.SetSketchPreview(edit.Snapshot);}catch{edit.Dispose();throw;}
            prepared=edit;Status=detach?Strings.AssemblyPatternDetachReady:Strings.ParameterPreviewReady;
        }
        catch(OperationCanceledException){if(!IsStale)Status=Strings.AssemblyPatternHint;}
        catch(Exception ex){Status=string.Format(Strings.ParameterError,ex.Message);}
        finally{IsWorking=false;cancel.Dispose();if(ReferenceEquals(cancellation,cancel))cancellation=null;}
    }
    [RelayCommand(CanExecute=nameof(CanConfirm))] private async Task ConfirmAsync()
    {
        if(prepared is null)return;IsWorking=true;committing=true;
        try{await document.Session.ExecuteAsync(new CommitPreparedDocumentCommand(prepared.Snapshot,generation,Strings.AssemblyPattern));document.FitView();CloseRequested?.Invoke(this,EventArgs.Empty);}
        catch(Exception ex){Invalidate();IsStale=true;Status=string.Format(Strings.ParameterError,ex.Message);}
        finally{committing=false;IsWorking=false;}
    }
    [RelayCommand] private void Cancel(){Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    public ValueTask DisposeAsync()=>new(disposal??=DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        disposed=true;Invalidate();document.Session.Changed-=OnChanged;document.Detaching-=OnDetached;
        try{await Task.WhenAll(new[]{PreviewCommand.ExecutionTask,ConfirmCommand.ExecutionTask,DetachCommand.ExecutionTask}.OfType<Task>());}finally{baseline.Dispose();}
    }
}
