using System.ComponentModel;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public interface IHoleEditorHost {Task ShowAsync(HoleEditorViewModel editor);}
public sealed record HoleSourceChoice(BodyId Body,string Label);
public sealed record HoleFeatureChoice(FeatureId? Feature,string Label);
public sealed record HoleDatumChoice(Guid? Datum,string Label);
public sealed record HoleHeadChoice(HoleHead Value,string Label);
public sealed record HoleFitChoice(ClearanceFit Value,string Label);
public sealed record HoleThreadHandChoice(ThreadHand Value,string Label);
public sealed record HoleThreadRepresentationChoice(ThreadHoleRepresentation Value,string Label);
public partial class HoleEditorViewModel:ObservableObject,IAsyncDisposable
{
    private readonly CadDocumentViewModel document;
    private readonly IGeometryKernel kernel;
    private readonly DocumentCapture baseline;
    private readonly long generation;
    private PreparedDocumentEdit? prepared;
    private CancellationTokenSource? cancellation;
    private Task? disposal;
    private bool disposed,loading,committing;
    public IReadOnlyList<HoleSourceChoice> Sources {get;}
    public IReadOnlyList<HoleFeatureChoice> Features {get;}
    public IReadOnlyList<HoleDatumChoice> Datums {get;}
    public IReadOnlyList<int> Nominals=>MetricClearanceHoles.Nominals;
    public IReadOnlyList<int> ThreadNominals=>MetricThreadHoles.Sizes.Select(s=>s.NominalMm).ToArray();
    public IReadOnlyList<HoleThreadHandChoice> ThreadHands {get;}=[new(ThreadHand.Right,Strings.HoleThreadRight),new(ThreadHand.Left,Strings.HoleThreadLeft)];
    public IReadOnlyList<HoleThreadRepresentationChoice> ThreadRepresentations {get;}=[new(ThreadHoleRepresentation.Symbolic,Strings.HoleThreadSymbolic),new(ThreadHoleRepresentation.Modeled,Strings.HoleThreadModeled)];
    public IReadOnlyList<HoleHeadChoice> Heads {get;}=[new(HoleHead.Plain,Strings.HolePlain),new(HoleHead.Counterbore,Strings.HoleCounterbore),new(HoleHead.Countersink,Strings.HoleCountersink)];
    public IReadOnlyList<HoleFitChoice> Fits {get;}=[new(ClearanceFit.Close,Strings.HoleFitClose),new(ClearanceFit.Normal,Strings.HoleFitNormal),new(ClearanceFit.Loose,Strings.HoleFitLoose)];
    [ObservableProperty] private HoleSourceChoice? selectedSource;
    [ObservableProperty] private HoleFeatureChoice? selectedFeature;
    [ObservableProperty] private HoleDatumChoice? selectedDatum;
    [ObservableProperty] private HoleHead head;
    [ObservableProperty] private ClearanceFit fit=ClearanceFit.Normal;
    [ObservableProperty] private int nominal=3;
    [ObservableProperty] private bool useStandard;
    [ObservableProperty] private bool useThread;
    [ObservableProperty] private int threadNominal=3;
    [ObservableProperty] private ThreadHand threadHand;
    [ObservableProperty] private ThreadHoleRepresentation threadRepresentation;
    [ObservableProperty] private double threadDepth=5;
    [ObservableProperty] private string objectName=Strings.Hole;
    [ObservableProperty] private bool through=true;
    [ObservableProperty] private double diameter=2,depth=6,headDiameter=4,headDepth=2,headAngle=90,u,v;
    [ObservableProperty] private double originX,originY,originZ,rotationX,rotationY,rotationZ;
    [ObservableProperty] private bool isWorking,isStale;
    [ObservableProperty] private string status=Strings.HoleHint;
    public bool CanEdit=>!disposed&&!IsWorking&&!IsStale;
    public bool CanConfirm=>CanEdit&&prepared is not null;
    public bool IsNew=>SelectedFeature?.Feature is null;
    public bool CanChooseSource=>IsNew&&CanEdit;
    public bool CanSetFrame=>SelectedDatum?.Datum is null;
    public bool CanSetDiameter=>!UseStandard&&!UseThread;
    public bool CanUseStandard=>!UseThread;
    public bool CanUseThread=>!UseStandard;
    public bool CanSetHead=>!UseThread;
    public bool CanSetThreadDepth=>UseThread&&!Through;
    public string ThreadRepresentationHint=>ThreadRepresentation==ThreadHoleRepresentation.Modeled?Strings.HoleThreadModeledHint:Strings.HoleThreadHint;
    public string ThreadDescription=>ThreadNominals.Contains(ThreadNominal)?
        "M"+ThreadNominal+" · P "+MetricThreadHoles.Size(ThreadNominal).PitchMm.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+" mm":"";
    public bool IsBlind=>!Through;
    public bool HasHead=>Head!=HoleHead.Plain;
    public bool IsCounterbore=>Head==HoleHead.Counterbore;
    public bool IsCountersink=>Head==HoleHead.Countersink;
    public string SourceDescription=>SelectedFeature?.Feature is {} id?baseline.Snapshot.Features[baseline.Snapshot.Features[id].Inputs.Single()].Name:"";
    public event EventHandler? CloseRequested;
    public HoleEditorViewModel(CadDocumentViewModel document,IGeometryKernel kernel,FeatureId? feature=null)
    {
        this.document=document;this.kernel=kernel;baseline=document.Session.Capture();generation=document.Session.Generation;
        Sources=baseline.Snapshot.Bodies.Values.Where(b=>b.Producer is {} p&&!baseline.Snapshot.Features[p].IsStale&&!baseline.Snapshot.Features[p].IsSuppressed)
            .Select(b=>new HoleSourceChoice(b.Id,baseline.Snapshot.Definitions[b.PartId].Name+" · "+b.Name)).ToArray();
        Features=[new(null,Strings.HoleNew),..baseline.Snapshot.Features.Values.Where(f=>f.Recipe is HoleRecipe&&!f.IsStale&&!f.IsSuppressed).Select(f=>new HoleFeatureChoice(f.Id,baseline.Snapshot.Definitions[f.PartId].Name+" · "+f.Name))];
        Datums=[new(null,Strings.HoleManualFrame),..baseline.Snapshot.Datums.Planes.Values.Select(p=>new HoleDatumChoice(p.Id,baseline.Snapshot.Definitions[p.PartId].Name+" · "+p.Name))];
        SelectedSource=Sources.FirstOrDefault();SelectedDatum=Datums[0];SelectedFeature=Features.FirstOrDefault(f=>f.Feature==feature)??Features[0];
        document.InvalidatePreview();document.Session.Changed+=OnChanged;document.Detaching+=OnDetached;
    }
    partial void OnSelectedFeatureChanged(HoleFeatureChoice? value)
    {
        loading=true;
        try
        {
            if(value?.Feature is {} id)
            {
                var f=baseline.Snapshot.Features[id];var r=(HoleRecipe)f.Recipe;SelectedSource=null;ObjectName=f.Name;Head=r.Head;Through=r.Through;Diameter=r.Diameter;Depth=r.Through?6:r.Depth;
                HeadDiameter=r.HeadDiameter==0?r.Diameter*2:r.HeadDiameter;HeadDepth=r.HeadDepth==0?2:r.HeadDepth;HeadAngle=r.HeadAngle==0?90:r.HeadAngle*180/Math.PI;
                U=r.Position.X;V=r.Position.Y;UseStandard=r.Standard is not null;Nominal=r.Standard?.NominalMm??3;Fit=r.Standard?.Fit??ClearanceFit.Normal;SelectedDatum=Datums.Single(d=>d.Datum==r.Datum);
                UseThread=r.Thread is not null;ThreadNominal=r.Thread?.NominalMm??3;ThreadHand=r.Thread?.Hand??ThreadHand.Right;
                ThreadRepresentation=r.Thread?.Representation??ThreadHoleRepresentation.Symbolic;
                ThreadDepth=r.Thread is {Through:false} thread?thread.DepthMm:5;
                SetFrame(r.Frame);
            }
            else{ObjectName=Strings.Hole;SelectedSource=Sources.FirstOrDefault();}
        }
        finally{loading=false;Invalidate();Notify();}
    }
    partial void OnSelectedSourceChanged(HoleSourceChoice? value)
    {
        if(!loading&&value is not null)
        {
            // Only authored box placement supplies a default. Bounds of arbitrary topology do not define entry planes.
            var body=baseline.Snapshot.Bodies[value.Body];
            if(body.Producer is {} id&&baseline.Snapshot.Features[id].Recipe is BoxRecipe box)
            {SetFrame(box.Placement*RigidTransform3d.Translate(0,0,box.Z));U=box.X/2;V=box.Y/2;}
        }
    }
    private void SetFrame(RigidTransform3d frame)
    {OriginX=frame.Translation.X;OriginY=frame.Translation.Y;OriginZ=frame.Translation.Z;var e=frame.Rotation.ToEulerDegrees();RotationX=e.X;RotationY=e.Y;RotationZ=e.Z;}
    partial void OnSelectedDatumChanged(HoleDatumChoice? value)
    {if(!loading&&value?.Datum is {} id)SetFrame(baseline.Snapshot.Datums.Planes[id].Frame);}
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(e.PropertyName is nameof(UseStandard) or nameof(Nominal) or nameof(Fit))
            if(!loading&&UseStandard&&Nominals.Contains(Nominal)&&Enum.IsDefined(Fit))Diameter=MetricClearanceHoles.Diameter(new(Nominal,Fit));
        if(e.PropertyName is nameof(UseThread) or nameof(ThreadNominal))
            if(!loading&&UseThread&&ThreadNominals.Contains(ThreadNominal))Diameter=MetricThreadHoles.Size(ThreadNominal).PilotDiameterMm;
        if(e.PropertyName is nameof(SelectedSource) or nameof(SelectedDatum) or nameof(Head) or nameof(Through) or nameof(ObjectName) or nameof(UseStandard) or nameof(Nominal) or nameof(Fit)
            or nameof(Diameter) or nameof(Depth) or nameof(HeadDiameter) or nameof(HeadDepth) or nameof(HeadAngle) or nameof(U) or nameof(V)
            or nameof(OriginX) or nameof(OriginY) or nameof(OriginZ) or nameof(RotationX) or nameof(RotationY) or nameof(RotationZ)
            or nameof(UseThread) or nameof(ThreadNominal) or nameof(ThreadHand) or nameof(ThreadDepth) or nameof(ThreadRepresentation))
        {if(!loading)Invalidate();Notify();}
        else if(e.PropertyName is nameof(IsWorking) or nameof(IsStale))Notify();
    }
    private void Notify()
    {
        foreach(string name in new[]{nameof(CanEdit),nameof(CanConfirm),nameof(IsNew),nameof(CanChooseSource),nameof(CanSetFrame),nameof(CanSetDiameter),nameof(IsBlind),nameof(HasHead),nameof(IsCounterbore),nameof(IsCountersink),nameof(SourceDescription),
            nameof(CanUseStandard),nameof(CanUseThread),nameof(CanSetHead),nameof(CanSetThreadDepth),nameof(ThreadDescription),nameof(ThreadRepresentationHint)})OnPropertyChanged(name);
        PreviewCommand.NotifyCanExecuteChanged();ConfirmCommand.NotifyCanExecuteChanged();
    }
    private void Invalidate(){cancellation?.Cancel();prepared?.Dispose();prepared=null;document.SetSketchPreview(null);OnPropertyChanged(nameof(CanConfirm));ConfirmCommand.NotifyCanExecuteChanged();}
    private void OnChanged(object? sender,DocumentChangeSet change){if(!committing){IsStale=true;Invalidate();Status=Strings.Stale;}}
    private void OnDetached(object? sender,EventArgs e){IsStale=true;Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    private ICadDocumentCommand BuildCommand()
    {
        var doc=baseline.Snapshot;FeatureDefinition? edit=SelectedFeature?.Feature is {} id?doc.Features[id]:null;
        var source=edit is null?doc.Bodies.GetValueOrDefault(SelectedSource?.Body??default)??throw new CadValidationException(Strings.HoleSelectSource):null;
        var geometry=edit is null?source!.Geometry:doc.Features[edit.Inputs.Single()].Result;var part=edit?.PartId??source!.PartId;
        var datum=SelectedDatum?.Datum is {} did?doc.Datums.Planes[did]:null;
        if(datum is not null&&datum.PartId!=part)throw new CadValidationException(Strings.HoleSamePart);
        var frame=datum?.Frame??new RigidTransform3d(new(OriginX,OriginY,OriginZ),Quaterniond.FromEulerDegrees(RotationX,RotationY,RotationZ));
        var recipe=new HoleRecipe(geometry,frame,new(U,V),Diameter,Through,Through?0:Depth,Head,HasHead?HeadDiameter:0,
            IsCounterbore?HeadDepth:0,IsCountersink?HeadAngle*Math.PI/180:0,UseStandard?new(Nominal,Fit):null,datum?.Id,datum?.Revision,
            UseThread?MetricThreadHoles.Create(ThreadNominal,ThreadHand,Through,Through?0:ThreadDepth,ThreadRepresentation):null);recipe.Validate();
        return edit is null?new AddHoleCommand(source!.Id,recipe,ObjectName.Trim()):new RecomputeCommand(edit.Id,recipe);
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
        catch(OperationCanceledException){if(!IsStale)Status=Strings.HoleHint;}
        catch(Exception ex){Status=string.Format(Strings.ParameterError,ex.Message);}
        finally{IsWorking=false;cancel.Dispose();if(ReferenceEquals(cancellation,cancel))cancellation=null;}
    }
    [RelayCommand(CanExecute=nameof(CanConfirm))] private async Task ConfirmAsync()
    {
        if(prepared is null)return;IsWorking=true;committing=true;
        try{await document.Session.ExecuteAsync(new CommitPreparedDocumentCommand(prepared.Snapshot,generation,Strings.Hole));document.FitView();CloseRequested?.Invoke(this,EventArgs.Empty);}
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
