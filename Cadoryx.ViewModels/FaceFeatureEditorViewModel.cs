using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public enum FaceFeatureKind { Shell, Draft, SolidOffset, PlanarPushPull, Defeature }
public interface IFaceFeatureEditorHost {Task ShowAsync(FaceFeatureEditorViewModel editor);}
public sealed record FaceSourceChoice(FeatureId Feature,BodyId Body,DefinitionId Part,string Label);
public partial class DraftProgramViewModel:ObservableObject
{
    public Guid Id {get;}
    public GuidedFrameViewModel Neutral {get;}=new();
    private ExactTopologySelection face;
    private double? originalAngle;
    public ExactTopologySelection Face=>face;
    public string Label=>Strings.Face+" #"+face.FullTopologyIndex;
    [ObservableProperty] private double angleDegrees,pullX,pullY,pullZ=1;
    [ObservableProperty] private FaceDraftPropagation propagation;
    [ObservableProperty] private Guid? datum,datumRevision;
    public bool CanSetNeutral=>Datum is null;
    public DraftProgramViewModel(DraftFaceProgram value)
    {
        Id=value.Id;face=value.Face;angleDegrees=value.Angle*180/Math.PI;pullX=value.PullDirection.X;pullY=value.PullDirection.Y;pullZ=value.PullDirection.Z;
        propagation=value.Propagation;datum=value.Datum;datumRevision=value.DatumRevision;Neutral.Load(value.NeutralPlane);originalAngle=value.Angle;
    }
    public void ReplaceFace(ExactTopologySelection value){face=value;OnPropertyChanged(nameof(Face));OnPropertyChanged(nameof(Label));}
    partial void OnDatumChanged(Guid? value)=>OnPropertyChanged(nameof(CanSetNeutral));
    partial void OnAngleDegreesChanged(double value)=>originalAngle=null;
    public DraftFaceProgram Value=>new(Id,Face,originalAngle??AngleDegrees*Math.PI/180,Neutral.Value,new(PullX,PullY,PullZ),Propagation,Datum,DatumRevision);
}

/// <summary>Exact, explicit face authoring against one captured source; no geometric rebinding.</summary>
public partial class FaceFeatureEditorViewModel:ObservableObject,IAsyncDisposable
{
    private readonly CadDocumentViewModel document;
    private readonly IGeometryKernel kernel;
    private readonly DocumentCapture baseline;
    private readonly long generation;
    private readonly FeatureId? editing;
    private PreparedDocumentEdit? prepared;
    private CancellationTokenSource? cancellation;
    private Task? disposal;
    private bool loading,disposed,committing;
    private DraftProgramViewModel? replacement;
    private ExactTopologySelection? normalFace;
    private Vector3d? confirmedNormal;
    public FaceFeatureKind Kind {get;}
    public bool IsShell=>Kind==FaceFeatureKind.Shell;
    public bool IsDraft=>Kind==FaceFeatureKind.Draft;
    public bool IsOffset=>Kind==FaceFeatureKind.SolidOffset;
    public bool IsPushPull=>Kind==FaceFeatureKind.PlanarPushPull;
    public bool IsDefeature=>Kind==FaceFeatureKind.Defeature;
    public bool UsesFaceList=>IsShell||IsPushPull||IsDefeature;
    public bool UsesFaces=>!IsOffset;
    public string Hint=>IsOffset?Strings.SolidOffsetHint:IsPushPull?Strings.FacePushPullHint:IsDefeature?Strings.FaceDefeatureHint:Strings.FacePickHint;
    public string Title=>IsShell?Strings.FaceShell:IsDraft?Strings.FaceDraft:IsOffset?Strings.SolidOffset:IsPushPull?Strings.FacePushPull:Strings.FaceDefeature;
    public string FaceListLabel=>IsShell?Strings.FaceClosingFaces:Strings.FaceEditSelectedFaces;
    public string PlanarNormalLabel=>confirmedNormal is {} n?string.Format(Strings.FaceEditNormal,n.X,n.Y,n.Z):Strings.FaceEditNormalPending;
    public bool CanEdit=>!disposed&&!IsWorking&&!IsStale;
    public bool CanChooseSource=>CanEdit&&editing is null;
    public bool CanConfirm=>CanEdit&&prepared is not null;
    public bool HasCandidate=>prepared is not null;
    public bool IsEditing=>editing is not null;
    public string NeutralDatumLabel=>SelectedProgram?.Datum is {} id&&Snapshot.Datums.Planes.TryGetValue(id,out var plane)?plane.Name:Strings.FaceNoDatum;
    public DocumentSnapshot Snapshot=>baseline.Snapshot;
    public IAssetStore Assets=>document.Session.Assets;
    public CadScene? Scene {get;private set;}
    public ObservableCollection<FaceSourceChoice> Sources {get;}=[];
    public ObservableCollection<ExactTopologySelection> ClosingFaces {get;}=[];
    public ObservableCollection<DraftProgramViewModel> Programs {get;}=[];
    public ObservableCollection<GuidedChoice<Guid>> Datums {get;}=[];
    public IReadOnlyList<GuidedChoice<FaceDraftPropagation>> Propagations {get;}=[new(FaceDraftPropagation.SelectedFaceOnly,Strings.FaceSelectedOnly),new(FaceDraftPropagation.TangentChain,Strings.FaceTangentChain)];
    public IEnumerable<ExactTopologySelection> Faces=>UsesFaceList?ClosingFaces:Programs.Select(p=>p.Face);
    [ObservableProperty] private FaceSourceChoice? selectedSource;
    [ObservableProperty] private ExactTopologySelection? selectedClosingFace;
    [ObservableProperty] private DraftProgramViewModel? selectedProgram;
    [ObservableProperty] private GuidedChoice<Guid>? selectedDatum;
    [ObservableProperty] private double thickness=1;
    [ObservableProperty] private double offsetDistance=1;
    [ObservableProperty] private double pushPullDistance=1;
    [ObservableProperty] private bool outward,isWorking,isStale;
    [ObservableProperty] private string objectName="",status=Strings.FacePickHint;
    public event EventHandler? SceneChanged,CloseRequested;

    public FaceFeatureEditorViewModel(CadDocumentViewModel document,IGeometryKernel kernel,FaceFeatureKind kind,FeatureId? feature=null)
    {
        if(!Enum.IsDefined(kind))throw new ArgumentOutOfRangeException(nameof(kind));
        this.document=document;this.kernel=kernel;Kind=kind;editing=feature;baseline=document.Session.Capture();generation=document.Session.Generation;loading=true;
        try
        {
            var doc=Snapshot;
            if(feature is {} id)
            {
                var existing=doc.Features[id];
                if(existing.IsSuppressed||existing.Inputs.Length!=1||existing.Recipe is not (ShellRecipe or DraftRecipe or SolidOffsetRecipe or PlanarPushPullRecipe or DefeatureRecipe)||
                    IsShell!=(existing.Recipe is ShellRecipe)||IsDraft!=(existing.Recipe is DraftRecipe)||IsOffset!=(existing.Recipe is SolidOffsetRecipe)||
                    IsPushPull!=(existing.Recipe is PlanarPushPullRecipe)||IsDefeature!=(existing.Recipe is DefeatureRecipe))throw new CadValidationException(Strings.UnsupportedFeatureEdit);
                var source=doc.Features[existing.Inputs[0]];
                if(source.IsStale||source.IsSuppressed)throw new CadValidationException(Strings.ReferenceStale);
                Sources.Add(new(source.Id,source.OutputBodyId,source.PartId,source.Name));SelectedSource=Sources[0];ObjectName=existing.Name;
                if(existing.Recipe is ShellRecipe shell){Thickness=shell.Thickness;Outward=shell.Outward;foreach(var face in shell.ClosingFaces)ClosingFaces.Add(face);}
                else if(existing.Recipe is SolidOffsetRecipe offset)OffsetDistance=offset.Distance;
                else if(existing.Recipe is PlanarPushPullRecipe push){PushPullDistance=push.Distance;ClosingFaces.Add(push.Face);normalFace=push.Face;confirmedNormal=push.OutwardNormal;}
                else if(existing.Recipe is DefeatureRecipe remove)foreach(var face in remove.Faces)ClosingFaces.Add(face);
                else foreach(var p in ((DraftRecipe)existing.Recipe).Programs)AddProgram(new(p));
                if(existing.IsStale)Status=Strings.FaceReselectHint;
                else Status=Hint;
            }
            else
            {
                foreach(var body in doc.Bodies.Values.Where(b=>b.Geometry.Kind==BodyKind.Solid&&b.Producer is not null&&!doc.Layers[b.LayerId].IsLocked&&!doc.ExternalParts.ContainsKey(b.PartId)))
                {
                    var source=doc.Features[body.Producer!.Value];if(!source.IsStale&&!source.IsSuppressed)Sources.Add(new(source.Id,body.Id,body.PartId,doc.Definitions[body.PartId].Name+" · "+body.Name));
                }
                ObjectName=Title;Status=Hint;
            }
            ClosingFaces.CollectionChanged+=(_,_)=>Edited();Programs.CollectionChanged+=(_,_)=>Edited();FillDatums();
            document.InvalidatePreview();document.Session.Changed+=OnDocumentChanged;document.Detaching+=OnDetached;ShowSource();
        }
        catch{baseline.Dispose();throw;}
        finally{loading=false;}
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(e.PropertyName is nameof(Status) or nameof(CanEdit) or nameof(CanChooseSource) or nameof(CanConfirm) or nameof(HasCandidate) or nameof(Faces) or nameof(NeutralDatumLabel) or nameof(PlanarNormalLabel))return;
        if(e.PropertyName is nameof(IsWorking) or nameof(IsStale)){Notify();return;}
        if(e.PropertyName is nameof(SelectedProgram) or nameof(SelectedClosingFace) or nameof(SelectedDatum))return;
        Edited();
    }
    partial void OnSelectedSourceChanged(FaceSourceChoice? value)
    {
        if(loading)return;ClearRows();replacement=null;FillDatums();Edited();
    }
    private void FillDatums()
    {Datums.Clear();SelectedDatum=null;if(SelectedSource is {} source)foreach(var p in Snapshot.Datums.Planes.Values.Where(p=>p.PartId==source.Part))Datums.Add(new(p.Id,p.Name));}
    partial void OnSelectedProgramChanged(DraftProgramViewModel? value)
    {SelectedDatum=Datums.FirstOrDefault(p=>p.Value==value?.Datum);OnPropertyChanged(nameof(NeutralDatumLabel));}
    private void AddProgram(DraftProgramViewModel row){row.PropertyChanged+=OnRowChanged;row.Neutral.Changed+=OnNeutralChanged;Programs.Add(row);}
    private void Unsubscribe(DraftProgramViewModel row){row.PropertyChanged-=OnRowChanged;row.Neutral.Changed-=OnNeutralChanged;}
    private void ClearRows(){foreach(var row in Programs)Unsubscribe(row);Programs.Clear();ClosingFaces.Clear();SelectedProgram=null;SelectedClosingFace=null;normalFace=null;confirmedNormal=null;OnPropertyChanged(nameof(PlanarNormalLabel));}
    private void OnRowChanged(object? sender,PropertyChangedEventArgs e)=>Edited();
    private void OnNeutralChanged(object? sender,EventArgs e)=>Edited();
    private void Edited(){if(loading)return;Invalidate();Notify();}
    private void Notify()
    {
        foreach(var p in new[]{nameof(CanEdit),nameof(CanChooseSource),nameof(CanConfirm),nameof(HasCandidate),nameof(Faces),nameof(NeutralDatumLabel),nameof(PlanarNormalLabel)})OnPropertyChanged(p);
        foreach(var c in new IRelayCommand[]{PreviewCommand,ConfirmCommand,InspectFaceCommand,RemoveFaceCommand,ClearFacesCommand,ReplaceFaceCommand,LinkDatumCommand,DetachDatumCommand})c.NotifyCanExecuteChanged();
    }
    private void Invalidate(){cancellation?.Cancel();var previous=prepared;prepared=null;document.SetSketchPreview(null);ShowSource();previous?.Dispose();}
    public void ShowSource()
    {
        Scene=SelectedSource is {} source?new(Snapshot.Id,Snapshot.StateId,[new(new OccurrencePath(Snapshot.Id,[]),source.Body,Snapshot.Features[source.Feature].Result,RigidTransform3d.Identity,0xFF94B8D8)]):new(Snapshot.Id,Snapshot.StateId,[]);
        SceneChanged?.Invoke(this,EventArgs.Empty);
    }
    public void RejectPick(string message)=>Status=message;
    public Task PickExactAsync(ExactTopologySelection face)
    {if(IsPushPull)return InspectFaceCommand.ExecuteAsync(face);PickExact(face);return Task.CompletedTask;}
    private void ValidatePick(ExactTopologySelection face)
    {
        face.Validate();var source=SelectedSource??throw new CadValidationException(Strings.FaceChooseSource);var result=Snapshot.Features[source.Feature].Result;
        if(face.Kind!=HistoryShapeKind.Face||face.DocumentId!=Snapshot.Id||face.FeatureId!=source.Feature||face.Asset!=result.AssetId||face.Revision!=result.Revision)throw new CadValidationException(Strings.WrongReferenceContext);
    }
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task InspectFaceAsync(ExactTopologySelection face)
    {
        if(!IsPushPull||!CanEdit)return;Invalidate();IsWorking=true;var cancel=new CancellationTokenSource();cancellation=cancel;var source=SelectedSource;
        try
        {
            ValidatePick(face);if(kernel is not IFaceEditResolver resolver)throw new NotSupportedException("Kernel has no native face-edit resolver.");
            var normal=await resolver.InspectPlanarFaceAsync(Snapshot,face,Assets,cancel.Token);
            if(cancel.IsCancellationRequested||disposed||IsStale||document.Session.Generation!=generation||SelectedSource!=source)return;
            loading=true;
            try{ClearRows();ClosingFaces.Add(face);SelectedClosingFace=face;normalFace=face;confirmedNormal=normal;}
            finally{loading=false;}
            Notify();ShowSource();Status=Hint;
        }
        catch(OperationCanceledException){if(!disposed&&!IsStale)Status=Hint;}
        catch(Exception e){if(!disposed&&!IsStale&&!cancel.IsCancellationRequested)Status=string.Format(Strings.ParameterError,e.Message);}
        finally{IsWorking=false;cancel.Dispose();if(ReferenceEquals(cancellation,cancel))cancellation=null;}
    }
    public void PickExact(ExactTopologySelection face)
    {
        if(IsOffset)return;
        if(IsPushPull){_=InspectFaceCommand.ExecuteAsync(face);return;}
        if(!CanEdit)return;
        try
        {
            ValidatePick(face);
            if(Faces.Any(f=>f==face&&(!IsDraft||replacement?.Face!=f)))throw new CadValidationException(Strings.FaceDuplicate);
            if(UsesFaceList){ClosingFaces.Add(face);SelectedClosingFace=face;}
            else if(replacement is {} row){row.ReplaceFace(face);SelectedProgram=row;replacement=null;}
            else{var added=new DraftProgramViewModel(new(Guid.NewGuid(),face,5*Math.PI/180,RigidTransform3d.Identity,new(0,0,1)));AddProgram(added);SelectedProgram=added;}
            Edited();Status=Hint;
        }
        catch(Exception e){Status=string.Format(Strings.ParameterError,e.Message);}
    }
    [RelayCommand(CanExecute=nameof(CanEdit))] private void RemoveFace()
    {
        if(UsesFaceList&&SelectedClosingFace is {} face){ClosingFaces.Remove(face);if(IsPushPull){normalFace=null;confirmedNormal=null;OnPropertyChanged(nameof(PlanarNormalLabel));}}
        else if(SelectedProgram is {} row){Unsubscribe(row);Programs.Remove(row);if(replacement==row)replacement=null;SelectedProgram=Programs.FirstOrDefault();}
        Edited();
    }
    [RelayCommand(CanExecute=nameof(CanEdit))] private void ClearFaces(){ClearRows();replacement=null;Edited();Status=Hint;}
    [RelayCommand(CanExecute=nameof(CanEdit))] private void ReplaceFace()
    {if(IsDraft&&SelectedProgram is {} row){replacement=row;Invalidate();Notify();Status=Strings.FaceReplaceHint;}}
    [RelayCommand(CanExecute=nameof(CanEdit))] private void LinkDatum()
    {
        if(SelectedProgram is not {} row||SelectedDatum is not {} selected)return;var plane=Snapshot.Datums.Planes[selected.Value];
        row.Neutral.Load(plane.Frame);row.Datum=plane.Id;row.DatumRevision=plane.Revision;Edited();
    }
    [RelayCommand(CanExecute=nameof(CanEdit))] private void DetachDatum()
    {if(SelectedProgram is {} row){row.Datum=null;row.DatumRevision=null;Edited();}}
    public GeometryRecipe BuildRecipe()
    {
        var source=SelectedSource??throw new CadValidationException(Strings.FaceChooseSource);var geometry=Snapshot.Features[source.Feature].Result;
        if(IsPushPull&&(ClosingFaces.Count!=1||normalFace!=ClosingFaces[0]||confirmedNormal is null))throw new CadValidationException(Strings.FaceEditNormalPending);
        GeometryRecipe recipe=IsOffset?new SolidOffsetRecipe(geometry,OffsetDistance):IsShell?new ShellRecipe(geometry,ClosingFaces.ToImmutableArray(),Thickness,Outward):
            IsPushPull?new PlanarPushPullRecipe(geometry,ClosingFaces[0],PushPullDistance,confirmedNormal!.Value):IsDefeature?new DefeatureRecipe(geometry,ClosingFaces.ToImmutableArray()):new DraftRecipe(geometry,Programs.Select(p=>p.Value).ToImmutableArray());
        recipe.Validate();if(!IsOffset&&!FaceFeatureRecipes.HasCurrentSource(Snapshot,recipe))throw new CadValidationException(Strings.FaceReselectHint);return recipe;
    }
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task PreviewAsync()
    {
        Invalidate();if(document.Session.Generation!=generation){IsStale=true;Status=Strings.Stale;return;}IsWorking=true;var cancel=new CancellationTokenSource();cancellation=cancel;
        try
        {
            var recipe=BuildRecipe();ICadDocumentCommand command=IsOffset?
                editing is {} offsetId?new RecomputeCommand(offsetId,recipe):new AddSolidOffsetCommand(SelectedSource!.Body,(SolidOffsetRecipe)recipe,ObjectName.Trim()):
                editing is {} id?new ReselectFaceFeatureCommand(id,recipe):new AddFaceFeatureCommand(SelectedSource!.Body,recipe,ObjectName.Trim());
            var candidate=await UpdateAssociatedSections.PrepareCommandAsync(command,new(Snapshot,generation,Assets,kernel),cancel.Token);
            if(cancel.IsCancellationRequested||disposed||IsStale||document.Session.Generation!=generation){candidate.Dispose();return;}
            try
            {
                candidate.Snapshot.Validate();var result=editing is {} target?candidate.Snapshot.Features[target]:candidate.Snapshot.Features.Values.Single(f=>!Snapshot.Features.ContainsKey(f.Id));
                if(result.IsStale)throw new CadValidationException(Strings.ReferenceStale);
                Scene=new(Snapshot.Id,candidate.Snapshot.StateId,[new(new OccurrencePath(Snapshot.Id,[]),result.OutputBodyId,result.Result,RigidTransform3d.Identity,0xFF80CBAA)]);
                document.SetSketchPreview(candidate.Snapshot);
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
        disposed=true;Invalidate();Notify();document.Session.Changed-=OnDocumentChanged;document.Detaching-=OnDetached;foreach(var row in Programs)Unsubscribe(row);
        try{await Task.WhenAll(new[]{InspectFaceCommand.ExecutionTask,PreviewCommand.ExecutionTask,ConfirmCommand.ExecutionTask}.OfType<Task>());}finally{baseline.Dispose();}
    }
}
