using System.Collections.ObjectModel;
using System.ComponentModel;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public interface IExtrusionEditorHost {Task ShowAsync(ExtrusionEditorViewModel editor);}
public sealed record ExtrusionChoice<T>(T Value,string Label);
public sealed record ExtrusionProfileChoice(SketchProfileReference? Source,string Label);
public sealed record ExtrusionFaceChoice(ExactPlanarLimit Limit,string Label);
public sealed record ExtrusionSurfaceChoice(ExactSurfaceLimit Limit,string Label);
public partial class ExtrusionEditorViewModel:ObservableObject,IAsyncDisposable
{
    private readonly CadDocumentViewModel document;
    private readonly IGeometryKernel kernel;
    private readonly DocumentCapture baseline;
    private readonly long generation;
    private readonly ExtrudeRecipe seed;
    private readonly ExtrusionProfileChoice seedProfile;
    private PreparedDocumentEdit? prepared;
    private CancellationTokenSource? cancellation;
    private Task? disposal;
    private bool disposed,loading,committing;
    public IReadOnlyList<ExtrusionChoice<BodyId>> Targets {get;}
    public IReadOnlyList<ExtrusionChoice<FeatureId?>> Features {get;}
    public IReadOnlyList<ExtrusionChoice<DefinitionId?>> Parts {get;}
    public IReadOnlyList<ExtrusionChoice<Guid?>> Datums {get;}
    public ObservableCollection<ExtrusionProfileChoice> Profiles {get;}=[];
    public ObservableCollection<ExtrusionFaceChoice> Faces {get;}=[];
    public ObservableCollection<ExtrusionSurfaceChoice> Surfaces {get;}=[];
    public ObservableCollection<ExtrusionFaceChoice> Supports {get;}=[];
    public IReadOnlyList<ExtrusionChoice<ExtrusionSurfaceBranch>> SurfaceBranches {get;}=[new(ExtrusionSurfaceBranch.Near,Strings.ExtrusionSurfaceNear),new(ExtrusionSurfaceBranch.Far,Strings.ExtrusionSurfaceFar)];
    public IReadOnlyList<ExtrusionChoice<ExtrusionSide>> Directions {get;}=[new(ExtrusionSide.Positive,Strings.ExtrusionPositive),new(ExtrusionSide.Negative,Strings.ExtrusionNegative),new(ExtrusionSide.Symmetric,Strings.ExtrusionSymmetric),new(ExtrusionSide.TwoSides,Strings.ExtrusionTwoSides)];
    public IReadOnlyList<ExtrusionChoice<ExtrusionOperation>> Operations {get;}=[new(ExtrusionOperation.NewBody,Strings.ExtrusionNewBody),new(ExtrusionOperation.Fuse,Strings.ExtrusionFuse),new(ExtrusionOperation.Cut,Strings.ExtrusionCut)];
    public IReadOnlyList<ExtrusionChoice<ExtrusionTermination>> Terminations {get;}=[new(ExtrusionTermination.Distance,Strings.ExtrusionFixed),new(ExtrusionTermination.Through,Strings.ExtrusionThrough),new(ExtrusionTermination.Plane,Strings.ExtrusionPlane),new(ExtrusionTermination.PlanarFace,Strings.ExtrusionFace),new(ExtrusionTermination.FiniteSurface,Strings.ExtrusionSurface)];
    [ObservableProperty] private ExtrusionChoice<BodyId>? selectedTarget;
    [ObservableProperty] private ExtrusionChoice<FeatureId?>? selectedFeature;
    [ObservableProperty] private ExtrusionChoice<DefinitionId?>? selectedPart;
    [ObservableProperty] private ExtrusionChoice<Guid?>? selectedDatum;
    [ObservableProperty] private ExtrusionProfileChoice? selectedProfile;
    [ObservableProperty] private ExtrusionFaceChoice? selectedFace;
    [ObservableProperty] private ExtrusionSurfaceChoice? selectedSurface;
    [ObservableProperty] private ExtrusionFaceChoice? selectedSupport;
    [ObservableProperty] private ExtrusionSurfaceBranch surfaceBranch;
    [ObservableProperty] private ExtrusionSide side;
    [ObservableProperty] private ExtrusionOperation operation;
    [ObservableProperty] private ExtrusionTermination termination;
    [ObservableProperty] private string objectName=Strings.ExtrusionAdvanced;
    [ObservableProperty] private double distance=10,backward=5;
    [ObservableProperty] private double startX,startY,startZ,startRx,startRy,startRz;
    [ObservableProperty] private double limitX,limitY,limitZ=20,limitRx,limitRy,limitRz;
    [ObservableProperty] private bool isWorking,isStale;
    [ObservableProperty] private string status=Strings.ExtrusionHint;
    public bool CanEdit=>!disposed&&!IsWorking&&!IsStale;
    public bool CanConfirm=>CanEdit&&prepared is not null;
    public bool IsNew=>SelectedFeature?.Value is null;
    public bool CanChooseTarget=>IsNew&&Operation!=ExtrusionOperation.NewBody;
    public bool CanChooseSupport=>CanEdit&&Termination==ExtrusionTermination.FiniteSurface&&Operation!=ExtrusionOperation.NewBody;
    public bool CanChooseProfile=>IsNew||SelectedFeature?.Value is {} id&&baseline.Snapshot.Features[id].SketchSource is not null;
    public bool CanSetDistance=>Termination==ExtrusionTermination.Distance;
    public bool CanSetBackward=>CanSetDistance&&Side==ExtrusionSide.TwoSides;
    public bool CanSetStart=>SelectedProfile?.Source is null;
    public bool CanSetLimit=>Termination==ExtrusionTermination.Plane&&SelectedDatum?.Value is null;
    public string SourceDescription=>SelectedFeature?.Value is {} id&&baseline.Snapshot.Features[id].Inputs.Length==1?
        baseline.Snapshot.Features[baseline.Snapshot.Features[id].Inputs[0]].Name:"";
    public ExtrusionEditorViewModel(CadDocumentViewModel document,IGeometryKernel kernel,FeatureId? feature=null)
    {
        this.document=document;this.kernel=kernel;baseline=document.Session.Capture();generation=document.Session.Generation;
        try
        {
            (ExtrudeRecipe Recipe,SketchProfileReference? Source) initial=feature is {} fid?
                ((ExtrudeRecipe)baseline.Snapshot.Features[fid].Recipe,baseline.Snapshot.Features[fid].SketchSource):document.ExtrusionSeed();
            seed=initial.Recipe;
            var doc=baseline.Snapshot;
            Targets=doc.Bodies.Values.Where(b=>b.Producer is {} p&&!doc.Features[p].IsStale&&!doc.Features[p].IsSuppressed).Select(b=>new ExtrusionChoice<BodyId>(b.Id,doc.Definitions[b.PartId].Name+" · "+b.Name)).ToArray();
            Features=[new(null,Strings.ExtrusionNew),..doc.Features.Values.Where(f=>f.Recipe is ExtrudeRecipe&&!f.IsSuppressed).Select(f=>new ExtrusionChoice<FeatureId?>(f.Id,doc.Definitions[f.PartId].Name+" · "+f.Name))];
            Parts=[new(null,Strings.SelectTargetPart),..doc.Definitions.Values.OfType<PartDefinition>().Select(p=>new ExtrusionChoice<DefinitionId?>(p.Id,p.Name))];
            Datums=[new(null,Strings.HoleManualFrame),..doc.Datums.Planes.Values.Select(p=>new ExtrusionChoice<Guid?>(p.Id,doc.Definitions[p.PartId].Name+" · "+p.Name))];
            Profiles.Add(new(null,Strings.ExtrusionProfile));foreach(var p in document.SketchProfiles)Profiles.Add(new(p.Source,p.Label));
            // The advanced editor can open while the main tool is a primitive or another part is active.
            // Its replacement choices must come from the captured author graph, not that tool's filtered list.
            foreach(var sketch in doc.Sketches.Values.OrderBy(s=>s.Name))
            {
                string label=doc.Definitions[sketch.PartId].Name+" · "+sketch.Name;
                void Add(SketchProfileReference source,string suffix)
                {
                    if(!Profiles.Any(p=>p.Source is {} existing&&CadDocumentViewModel.SameProfileSource(existing,source)))
                        Profiles.Add(new(source,label+" · "+suffix));
                }
                foreach(var (loop,index) in SketchLoops.Find(sketch).Select((loop,index)=>(loop,index)))
                    Add(SketchProfileReference.Create(sketch,loop),$"{index+1} ({loop.Length})");
                foreach(var (loop,index) in SketchMixedLoops.Find(sketch).Select((loop,index)=>(loop,index)))
                    Add(SketchProfileReference.CreateMixed(sketch,loop),$"{Strings.MixedCurveLoop} {index+1}");
                foreach(var c in sketch.Circles.Where(c=>!c.IsConstruction))Add(SketchProfileReference.CreateCircle(sketch,c.Id),$"○ R {c.Radius:G6}");
                foreach(var (a,index) in sketch.Arcs.Where(a=>!a.IsConstruction).Select((a,index)=>(a,index)))Add(SketchProfileReference.CreateArcSegment(sketch,a.Id),$"⌒ {index+1}");
                foreach(var (b,index) in sketch.Beziers.Where(b=>!b.IsConstruction).Select((b,index)=>(b,index)))Add(SketchProfileReference.CreateBezierSegment(sketch,b.Id),$"∿ {index+1}");
                foreach(var (s,index) in sketch.Splines.Where(s=>!s.IsConstruction).Select((s,index)=>(s,index)))Add(SketchProfileReference.CreateSplineSegment(sketch,s.Id),$"S {index+1} ({s.Controls.Length})");
            }
            seedProfile=initial.Source is {} source?new(source,doc.Sketches[source.SketchId].Name):Profiles[0];
            if(!Profiles.Contains(seedProfile))Profiles.Add(seedProfile);
            SelectedPart=Parts.FirstOrDefault(p=>p.Value==document.SelectedTargetPart)??Parts[0];SelectedDatum=Datums[0];SelectedTarget=Targets.FirstOrDefault();
            SelectedProfile=seedProfile;
            SelectedFeature=Features.FirstOrDefault(f=>f.Value==feature)??Features[0];
            document.InvalidatePreview();document.Session.Changed+=OnChanged;document.Detaching+=OnDetached;
        }
        catch{baseline.Dispose();throw;}
    }
    partial void OnSelectedFeatureChanged(ExtrusionChoice<FeatureId?>? value)
    {
        loading=true;
        try
        {
            var f=value?.Value is {} id?baseline.Snapshot.Features[id]:null;var r=f?.Recipe as ExtrudeRecipe??seed;var o=r.Options??new();
            ObjectName=f?.Name??Strings.ExtrusionAdvanced;Side=o.Side;Operation=o.Operation;Termination=o.Termination;Distance=r.Distance==0?10:r.Distance;Backward=o.Backward==0?5:o.Backward;
            if(f is not null)
            {
                SelectedPart=Parts.Single(p=>p.Value==f.PartId);var choice=new ExtrusionProfileChoice(f.SketchSource,Strings.ExtrusionProfile);
                if(!Profiles.Contains(choice))Profiles.Add(choice);SelectedProfile=choice;
            }
            else SelectedProfile=seedProfile;
            SetStart(r.Placement);if(o.LimitPlane is {} plane)SetLimit(plane);
            SelectedDatum=Datums.Single(d=>d.Value==o.Datum);SelectedFace=o.LimitFace is {} face?new(new(face,o.LimitGeometry!),Strings.ExtrusionFace+" #"+face.FullTopologyIndex):null;
            if(SelectedFace is {} chosen&&!Faces.Contains(chosen))Faces.Add(chosen);
            SurfaceBranch=o.SurfaceBranch??ExtrusionSurfaceBranch.Near;
            SelectedSurface=o.Termination==ExtrusionTermination.FiniteSurface&&o.LimitFace is {} stop?
                new(new(stop,o.LimitGeometry!,ExtrusionSurfaceKind.Plane),Strings.ExtrusionSurface+" #"+stop.FullTopologyIndex):null;
            SelectedSupport=o.SupportFace is {} support?new(new(support,o.Source!),Strings.ExtrusionSurfaceSupport+" #"+support.FullTopologyIndex):null;
            if(SelectedSurface is {} surface&&!Surfaces.Contains(surface))Surfaces.Add(surface);
            if(SelectedSupport is {} start&&!Supports.Contains(start))Supports.Add(start);
        }
        finally{loading=false;Invalidate();Notify();}
    }
    private void SetStart(RigidTransform3d frame){StartX=frame.Translation.X;StartY=frame.Translation.Y;StartZ=frame.Translation.Z;var a=frame.Rotation.ToEulerDegrees();StartRx=a.X;StartRy=a.Y;StartRz=a.Z;}
    private void SetLimit(RigidTransform3d frame){LimitX=frame.Translation.X;LimitY=frame.Translation.Y;LimitZ=frame.Translation.Z;var a=frame.Rotation.ToEulerDegrees();LimitRx=a.X;LimitRy=a.Y;LimitRz=a.Z;}
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(e.PropertyName is nameof(CanEdit) or nameof(CanConfirm) or nameof(IsNew) or nameof(CanChooseTarget) or nameof(CanChooseSupport) or nameof(CanChooseProfile) or nameof(CanSetDistance) or nameof(CanSetBackward) or nameof(CanSetStart) or nameof(CanSetLimit) or nameof(SourceDescription) or nameof(Status))return;
        if(e.PropertyName is nameof(IsWorking) or nameof(IsStale)){Notify();return;}
        if(!loading)Invalidate();Notify();
    }
    private void Notify()
    {
        foreach(var name in new[]{nameof(CanEdit),nameof(CanConfirm),nameof(IsNew),nameof(CanChooseTarget),nameof(CanChooseSupport),nameof(CanChooseProfile),nameof(CanSetDistance),nameof(CanSetBackward),nameof(CanSetStart),nameof(CanSetLimit),nameof(SourceDescription)})OnPropertyChanged(name);
        PreviewCommand.NotifyCanExecuteChanged();ConfirmCommand.NotifyCanExecuteChanged();LoadFacesCommand.NotifyCanExecuteChanged();
    }
    private void Invalidate(){cancellation?.Cancel();prepared?.Dispose();prepared=null;document.SetSketchPreview(null);OnPropertyChanged(nameof(CanConfirm));ConfirmCommand.NotifyCanExecuteChanged();}
    private void OnChanged(object? sender,DocumentChangeSet change){if(!committing){IsStale=true;Invalidate();Status=Strings.Stale;}}
    private void OnDetached(object? sender,EventArgs e){IsStale=true;Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    public event EventHandler? CloseRequested;
    private ICadDocumentCommand BuildCommand()
    {
        var doc=baseline.Snapshot;var edit=SelectedFeature?.Value is {} id?doc.Features[id]:null;
        var part=edit?.PartId??SelectedPart?.Value;var old=edit?.Recipe as ExtrudeRecipe??seed;
        var target=Operation==ExtrusionOperation.NewBody?null:edit is null?doc.Bodies.GetValueOrDefault(SelectedTarget?.Value??default):null;
        var source=Operation==ExtrusionOperation.NewBody?null:edit is null?target?.Geometry??throw new CadValidationException(Strings.HoleSelectSource):doc.Features[edit.Inputs.Single()].Result;
        if(target is not null)part=target.PartId;
        var datum=Termination==ExtrusionTermination.Plane&&SelectedDatum?.Value is {} did?doc.Datums.Planes[did]:null;
        if(datum is not null&&datum.PartId!=part)throw new CadValidationException(Strings.HoleSamePart);
        ExactPlanarLimit? face=Termination==ExtrusionTermination.PlanarFace?SelectedFace?.Limit??throw new CadValidationException(Strings.ExtrusionSelectLimit):null;
        if(Termination==ExtrusionTermination.FiniteSurface)
        {
            var selected=SelectedSurface?.Limit??throw new CadValidationException(Strings.ExtrusionSelectLimit);
            face=new(selected.Selection,selected.Geometry);
            if(Operation!=ExtrusionOperation.NewBody&&SelectedSupport?.Limit.Geometry!=source)throw new CadValidationException(Strings.ExtrusionSurfaceSupport);
        }
        var plane=Termination==ExtrusionTermination.Plane?datum?.Frame??new RigidTransform3d(new(LimitX,LimitY,LimitZ),Quaterniond.FromEulerDegrees(LimitRx,LimitRy,LimitRz)):(RigidTransform3d?)null;
        var frame=new RigidTransform3d(new(StartX,StartY,StartZ),Quaterniond.FromEulerDegrees(StartRx,StartRy,StartRz));
        var recipe=new ExtrudeRecipe(old.Profile,CanSetDistance?Distance:0,frame,new(Side,CanSetBackward?Backward:0,Operation,source,Termination,plane,datum?.Id,datum?.Revision,face?.Selection,face?.Geometry,
            Termination==ExtrusionTermination.FiniteSurface&&Operation!=ExtrusionOperation.NewBody?SelectedSupport?.Limit.Selection:null,Termination==ExtrusionTermination.FiniteSurface?SurfaceBranch:null));
        if(edit?.SketchSource is not null&&SelectedProfile?.Source is null)
            throw new CadValidationException("Select an explicit replacement profile for the linked extrusion.");
        return edit is null?new AddExtrusionCommand(recipe,ObjectName.Trim(),part,target?.Id,SelectedProfile?.Source):
            new RecomputeCommand(edit.Id,recipe,edit.SketchSource is not null?SelectedProfile?.Source:null);
    }
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task LoadFacesAsync()
    {
        Invalidate();IsWorking=true;var cancel=new CancellationTokenSource();cancellation=cancel;
        try
        {
            if(Termination==ExtrusionTermination.FiniteSurface)
            {
                var surfaceResolver=kernel as IFiniteSurfaceExtrusionResolver??throw new CadValidationException(Strings.UnsupportedFeatureEdit);
                var surfaces=await surfaceResolver.EnumerateSurfaceLimitsAsync(baseline.Snapshot,document.Session.Assets,cancel.Token);
                if(cancel.IsCancellationRequested||disposed||IsStale)return;
                Surfaces.Clear();Supports.Clear();SelectedSurface=null;SelectedSupport=null;
                foreach(var item in surfaces)
                {
                    var f=baseline.Snapshot.Features[item.Selection.FeatureId];string label=baseline.Snapshot.Definitions[f.PartId].Name+" · "+f.Name+" #"+item.Selection.FullTopologyIndex;
                    Surfaces.Add(new(item,label));
                    if(item.Kind==ExtrusionSurfaceKind.Plane)Supports.Add(new(new(item.Selection,item.Geometry),label));
                }
                Status=Strings.ExtrusionSurfaceHint;return;
            }
            var resolver=kernel as IExtrusionLimitResolver??throw new CadValidationException(Strings.UnsupportedFeatureEdit);
            var faces=await resolver.EnumeratePlanarLimitsAsync(baseline.Snapshot,document.Session.Assets,cancel.Token);
            if(cancel.IsCancellationRequested||disposed||IsStale)return;
            Faces.Clear();SelectedFace=null;
            foreach(var face in faces){var f=baseline.Snapshot.Features[face.Selection.FeatureId];Faces.Add(new(face,baseline.Snapshot.Definitions[f.PartId].Name+" · "+f.Name+" #"+face.Selection.FullTopologyIndex));}
            Status=Strings.ExtrusionFaceHint;
        }
        catch(OperationCanceledException){ }
        catch(Exception ex){Status=string.Format(Strings.ParameterError,ex.Message);}
        finally{IsWorking=false;cancel.Dispose();if(ReferenceEquals(cancellation,cancel))cancellation=null;}
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
        catch(OperationCanceledException){if(!IsStale)Status=Strings.ExtrusionHint;}
        catch(Exception ex){Status=string.Format(Strings.ParameterError,ex.Message);}
        finally{IsWorking=false;cancel.Dispose();if(ReferenceEquals(cancellation,cancel))cancellation=null;}
    }
    [RelayCommand(CanExecute=nameof(CanConfirm))] private async Task ConfirmAsync()
    {
        if(prepared is null)return;IsWorking=true;committing=true;
        try{await document.Session.ExecuteAsync(new CommitPreparedDocumentCommand(prepared.Snapshot,generation,Strings.ExtrusionAdvanced));document.FitView();CloseRequested?.Invoke(this,EventArgs.Empty);}
        catch(Exception ex){Invalidate();IsStale=true;Status=string.Format(Strings.ParameterError,ex.Message);}
        finally{committing=false;IsWorking=false;}
    }
    [RelayCommand] private void Cancel(){Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    public ValueTask DisposeAsync()=>new(disposal??=DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        disposed=true;Invalidate();document.Session.Changed-=OnChanged;document.Detaching-=OnDetached;
        try{await Task.WhenAll(new[]{PreviewCommand.ExecutionTask,ConfirmCommand.ExecutionTask,LoadFacesCommand.ExecutionTask}.OfType<Task>());}finally{baseline.Dispose();}
    }
}
