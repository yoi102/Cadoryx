using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang;
using Cadoryx.Sketching;
using Strings = Cadoryx.Lang.Strings.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public interface ISketchEditorHost { Task ShowAsync(SketchEditorViewModel editor); }
public sealed record SketchEntityItem(SketchEntityId Id,string Label);
public sealed record SketchConstraintItem(SketchConstraintId Id,string Label);
public sealed record SketchOption(string Key,string Label);

public partial class SketchEditorViewModel : ObservableObject,IAsyncDisposable
{
    private readonly CadDocumentViewModel document;
    private readonly IGeometryKernel kernel;
    private readonly ISketchConstraintSolver solver;
    private readonly DocumentCapture baseline;
    private readonly long generation;
    private readonly string? newPartName;
    private PreparedDocumentEdit? prepared;
    private CancellationTokenSource? cancellation;
    private bool committing;
    private bool refreshingSelection;
    private bool loadingInputs;
    private bool pendingDimension;
    private bool pendingCoordinates;
    private bool disposed;
    private Task? disposal;
    public SketchDraft Draft {get;}
    public CadSketch Sketch=>Draft.Value;
    public ObservableCollection<SketchEntityItem> Entities {get;}=[];
    public ObservableCollection<SketchConstraintItem> Constraints {get;}=[];
    public ObservableCollection<SketchEntityId> SelectedEntities {get;}=[];
    public IReadOnlyList<SketchOption> Tools {get;}=new[]{"Select","Point","Line","Rectangle","Circle"}.Select(k=>new SketchOption(k,ToolLabel(k))).ToArray();
    public IReadOnlyList<SketchOption> ConstraintKinds {get;}=new[]{"FixPoint","Coincident","Horizontal","Vertical","Distance","OffsetX","OffsetY","Length","Parallel","Perpendicular","EqualLength","Radius","EqualRadius"}.Select(k=>new SketchOption(k,ConstraintLabel(k))).ToArray();
    public IReadOnlyList<string> Planes=>CanChangePlane?["XY","XZ","YZ"]:[Plane];
    public bool CanChangePlane {get;}
    public bool CanEdit=>!IsWorking&&!IsStale&&!disposed;
    public bool CanConfirm=>CanEdit&&prepared is not null;
    public bool HasSelectedPoint=>SelectedEntities.Count==1&&Sketch.Points.Any(p=>p.Id==SelectedEntities[0]);
    public bool HasSelectedCircle=>SelectedEntities.Count==1&&Sketch.Circles.Any(c=>c.Id==SelectedEntities[0]);
    public SketchSolveReport? Report {get;private set;}
    public event EventHandler? DrawingChanged;
    public event EventHandler? CloseRequested;
    [ObservableProperty] private string tool="Select";
    [ObservableProperty] private string constraintKind="Horizontal";
    [ObservableProperty] private bool construction;
    [ObservableProperty] private bool snap=true;
    [ObservableProperty] private double gridStep=1;
    [ObservableProperty] private string name;
    [ObservableProperty] private string plane="XY";
    [ObservableProperty] private double originX;
    [ObservableProperty] private double originY;
    [ObservableProperty] private double originZ;
    [ObservableProperty] private string status;
    [ObservableProperty] private bool isWorking;
    [ObservableProperty] private bool isStale;
    [ObservableProperty] private SketchConstraintItem? selectedConstraint;
    [ObservableProperty] private double dimensionValue=10;
    [ObservableProperty] private double dimensionY;
    [ObservableProperty] private double pointX;
    [ObservableProperty] private double pointY;
    [ObservableProperty] private double circleRadius=5;

    public SketchEditorViewModel(CadDocumentViewModel document,IGeometryKernel kernel,ISketchConstraintSolver solver,CadSketch sketch,bool isNew)
    {
        this.document=document;this.kernel=kernel;this.solver=solver;baseline=document.Session.Capture();generation=document.Session.Generation;
        Draft=new(sketch);name=sketch.Name;status=Strings.DraftChanged;CanChangePlane=isNew;
        plane=sketch.Plane.Rotation==Quaterniond.Identity?"XY":sketch.Plane.Rotation==Quaterniond.FromAxisAngle(new(1,0,0),Math.PI/2)?"XZ":
            sketch.Plane.Rotation==Quaterniond.FromAxisAngle(new(0,1,0),Math.PI/2)?"YZ":Strings.ExistingSketchPlane;
        originX=sketch.Plane.Translation.X;originY=sketch.Plane.Translation.Y;originZ=sketch.Plane.Translation.Z;
        newPartName=baseline.Snapshot.Definitions.ContainsKey(sketch.PartId)?null:Cadoryx.Lang.Strings.Strings.Part;
        document.InvalidatePreview();document.Session.Changed+=OnDocumentChanged;document.Detaching+=OnDetached;
        SelectedEntities.CollectionChanged+=(_,_)=>{UpdateCoordinates();DrawingChanged?.Invoke(this,EventArgs.Empty);};Refresh();
    }
    partial void OnIsWorkingChanged(bool value)=>NotifyState();
    partial void OnIsStaleChanged(bool value)=>NotifyState();
    partial void OnNameChanged(string value)=>Invalidate();
    partial void OnPlaneChanged(string value)=>Invalidate();
    partial void OnOriginXChanged(double value)=>Invalidate();
    partial void OnOriginYChanged(double value)=>Invalidate();
    partial void OnOriginZChanged(double value)=>Invalidate();
    partial void OnDimensionValueChanged(double value)=>DimensionInputChanged();
    partial void OnDimensionYChanged(double value)=>DimensionInputChanged();
    partial void OnPointXChanged(double value)=>CoordinateInputChanged();
    partial void OnPointYChanged(double value)=>CoordinateInputChanged();
    partial void OnCircleRadiusChanged(double value)=>CoordinateInputChanged();
    private void DimensionInputChanged(){if(!loadingInputs&&SelectedConstraint is not null){pendingDimension=true;Invalidate();}}
    private void CoordinateInputChanged(){if(!loadingInputs&&SelectedEntities.Count==1){pendingCoordinates=true;Invalidate();}}
    private void NotifyState(){OnPropertyChanged(nameof(CanEdit));OnPropertyChanged(nameof(CanConfirm));PreviewCommand.NotifyCanExecuteChanged();ConfirmCommand.NotifyCanExecuteChanged();}
    private void OnDocumentChanged(object? sender,DocumentChangeSet e){if(!committing){IsStale=true;Invalidate();Status=Strings.Stale;}}
    private void OnDetached(object? sender,EventArgs e){IsStale=true;Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    public void Select(SketchEntityId? id,bool extend=false)
    {
        SelectedConstraint=null;pendingDimension=false;
        if(!extend)SelectedEntities.Clear();
        if(id is {} value){if(SelectedEntities.Contains(value)&&extend)SelectedEntities.Remove(value);else if(!SelectedEntities.Contains(value))SelectedEntities.Add(value);}
    }
    partial void OnSelectedConstraintChanged(SketchConstraintItem? value)
    {
        if(value is null||refreshingSelection)return;var constraint=Sketch.Constraints.FirstOrDefault(c=>c.Id==value.Id);if(constraint is null)return;
        loadingInputs=true;DimensionValue=SketchConstraintEditing.Value(constraint)??0;DimensionY=constraint is FixPointConstraint f?f.Position.Y:0;loadingInputs=false;pendingDimension=false;
        SelectedEntities.Clear();foreach(var target in SketchConstraintEditing.Targets(constraint))SelectedEntities.Add(target);
    }
    private void UpdateCoordinates()
    {
        OnPropertyChanged(nameof(HasSelectedPoint));OnPropertyChanged(nameof(HasSelectedCircle));
        pendingCoordinates=false;if(SelectedEntities.Count!=1)return;var id=SelectedEntities[0];loadingInputs=true;
        if(Sketch.Points.FirstOrDefault(p=>p.Id==id) is {} point){PointX=point.Position.X;PointY=point.Position.Y;}
        if(Sketch.Circles.FirstOrDefault(c=>c.Id==id) is {} circle)CircleRadius=circle.Radius;
        loadingInputs=false;
    }
    public void Mutate(Action<SketchDraft> action)
    {
        if(!CanEdit)return;
        try{action(Draft);Invalidate();Refresh();Status=Strings.DraftChanged;}catch(Exception ex){Status=ex.Message;}
    }
    public void MovePoint(SketchEntityId point,Point2d position)=>Mutate(d=>d.MovePoint(point,position));
    [RelayCommand] private void ApplyCoordinates()=>Mutate(ApplyCoordinatesCore);
    private void ApplyCoordinatesCore(SketchDraft d)
    {
        if(SelectedEntities.Count!=1)throw new CadValidationException(string.Format(Strings.SketchTargets,1));var id=SelectedEntities[0];
        if(Sketch.Points.Any(p=>p.Id==id))d.MovePoint(id,new(PointX,PointY));
        else if(Sketch.Circles.Any(c=>c.Id==id))d.Change(s=>s with{Circles=s.Circles.Select(c=>c.Id==id?c with{Radius=CircleRadius}:c).ToImmutableArray()});
        pendingCoordinates=false;
    }
    private void ApplyMetadata()
    {
        var next=Sketch with{Name=Name,Plane=CanChangePlane?new(new(OriginX,OriginY,OriginZ),Plane switch
            {"XZ"=>Quaterniond.FromAxisAngle(new(1,0,0),Math.PI/2),"YZ"=>Quaterniond.FromAxisAngle(new(0,1,0),Math.PI/2),_=>Quaterniond.Identity}):Sketch.Plane};
        next.Validate();if(next!=Sketch)Draft.Change(_=>next);
    }
    [RelayCommand] private void Undo(){if(!CanEdit)return;Draft.Undo();RestoreMetadata();Invalidate();Refresh();}
    [RelayCommand] private void Redo(){if(!CanEdit)return;Draft.Redo();RestoreMetadata();Invalidate();Refresh();}
    private void RestoreMetadata(){Name=Sketch.Name;OriginX=Sketch.Plane.Translation.X;OriginY=Sketch.Plane.Translation.Y;OriginZ=Sketch.Plane.Translation.Z;
        if(CanChangePlane)Plane=Sketch.Plane.Rotation==Quaterniond.Identity?"XY":Sketch.Plane.Rotation==Quaterniond.FromAxisAngle(new(1,0,0),Math.PI/2)?"XZ":"YZ";pendingDimension=false;pendingCoordinates=false;}
    [RelayCommand] private void DeleteEntities()=>Mutate(d=>d.RemoveEntities(SelectedEntities.ToArray()));
    [RelayCommand] private void DeleteConstraint()=>Mutate(d=>d.Change(s=>s with{Constraints=s.Constraints.Where(c=>c.Id!=SelectedConstraint?.Id).ToImmutableArray()}));
    [RelayCommand] private void ToggleConstraint()=>Mutate(d=>d.Change(s=>s with{Constraints=s.Constraints.Select(c=>c.Id==SelectedConstraint?.Id?c with{IsEnabled=!c.IsEnabled}:c).ToImmutableArray()}));
    [RelayCommand] private void ApplyDimension()=>Mutate(ApplyDimensionCore);
    private void ApplyDimensionCore(SketchDraft d)
    {d.Change(s=>s with{Constraints=s.Constraints.Select(c=>c.Id==SelectedConstraint?.Id?SketchConstraintEditing.SetValue(c,DimensionValue,DimensionY):c).ToImmutableArray()});pendingDimension=false;}
    [RelayCommand] private void AddConstraint()=>Mutate(d=>
    {
        var ids=SelectedEntities.ToArray();int count=ConstraintKind is "FixPoint" or "Horizontal" or "Vertical" or "Length" or "Radius"?1:2;
        if(ids.Length!=count)throw new CadValidationException(string.Format(Strings.SketchTargets,count));
        var id=SketchConstraintId.New();var a=ids[0];var b=count==2?ids[1]:default;
        SketchConstraint c=ConstraintKind switch
        {
            "FixPoint"=>new FixPointConstraint(id,a,Sketch.Points.Single(p=>p.Id==a).Position),"Coincident"=>new CoincidentConstraint(id,a,b),
            "Horizontal"=>new HorizontalConstraint(id,a),"Vertical"=>new VerticalConstraint(id,a),"Distance"=>new DistanceConstraint(id,a,b,DimensionValue),
            "OffsetX"=>new OffsetXConstraint(id,a,b,DimensionValue),"OffsetY"=>new OffsetYConstraint(id,a,b,DimensionValue),"Length"=>new LengthConstraint(id,a,DimensionValue),
            "Parallel"=>new ParallelConstraint(id,a,b),"Perpendicular"=>new PerpendicularConstraint(id,a,b),"EqualLength"=>new EqualLengthConstraint(id,a,b),
            "Radius"=>new RadiusConstraint(id,a,DimensionValue),"EqualRadius"=>new EqualRadiusConstraint(id,a,b),_=>throw new NotSupportedException()
        };
        d.Change(s=>s with{Constraints=s.Constraints.Add(c)});
        pendingDimension=false;
    });
    private void Refresh()
    {
        var selected=SelectedConstraint?.Id;Entities.Clear();int i=0;
        foreach(var p in Sketch.Points)Entities.Add(new(p.Id,$"P{++i} ({Display(p.Position.X)}, {Display(p.Position.Y)})"));i=0;
        foreach(var l in Sketch.Lines)Entities.Add(new(l.Id,$"L{++i}"+(l.IsConstruction?" · "+Strings.ConstructionGeometry:"")));i=0;
        foreach(var c in Sketch.Circles)Entities.Add(new(c.Id,$"C{++i} · R {c.Radius:G6}"));
        var labels=Entities.ToDictionary(e=>e.Id,e=>e.Label.Split(' ')[0]);Constraints.Clear();i=0;
        foreach(var c in Sketch.Constraints)
        {
            string kind=c.GetType().Name.Replace("Constraint","");string targets=string.Join(", ",SketchConstraintEditing.Targets(c).Select(t=>labels[t]));
            string marker=Report?.ConflictingConstraints.Contains(c.Id)==true?"! ":Report?.RedundantConstraints.Contains(c.Id)==true?"≈ ":"";
            Constraints.Add(new(c.Id,$"{marker}{++i}. {ConstraintLabel(kind)} ({targets})"+(SketchConstraintEditing.Value(c) is {} v?$" = {v:G6}":"")+(c.IsEnabled?"":" [—]")));
        }
        // Refreshing diagnostic rows must not replace the user's canvas selection.
        refreshingSelection=true;SelectedConstraint=Constraints.FirstOrDefault(c=>c.Id==selected);refreshingSelection=false;
        foreach(var id in SelectedEntities.Where(id=>!Entities.Any(e=>e.Id==id)).ToArray())SelectedEntities.Remove(id);
        OnPropertyChanged(nameof(Sketch));UpdateCoordinates();DrawingChanged?.Invoke(this,EventArgs.Empty);NotifyState();
    }
    private static string ToolLabel(string key)=>key switch
    {
        "Select"=>Strings.SelectOrDrag,
        "Point"=>Strings.Point,
        "Line"=>Strings.Line,
        "Rectangle"=>Strings.Rectangle,
        "Circle"=>Strings.Circle,
        _=>key
    };
    private static string ConstraintLabel(string key)=>key switch
    {
        "FixPoint"=>Strings.FixPoint,
        "Coincident"=>Strings.CoincidentPoints,
        "Horizontal"=>Strings.HorizontalLine,
        "Vertical"=>Strings.VerticalLine,
        "Distance"=>Strings.PointDistance,
        "OffsetX"=>Strings.XOffset,
        "OffsetY"=>Strings.YOffset,
        "Length"=>Strings.LineLength,
        "Parallel"=>Strings.ParallelLines,
        "Perpendicular"=>Strings.PerpendicularLines,
        "EqualLength"=>Strings.EqualLengths,
        "Radius"=>Strings.CircleRadius,
        "EqualRadius"=>Strings.EqualRadii,
        _=>key
    };
    private static string Display(double value)=>(Math.Abs(value)<1e-9?0:value).ToString("G6");
    private void Invalidate()
    {
        cancellation?.Cancel();document.SetSketchPreview(null);prepared?.Dispose();prepared=null;Report=null;NotifyState();
    }
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task PreviewAsync()
    {
        Invalidate();if(document.Session.Generation!=generation){IsStale=true;Status=Strings.Stale;return;}
        // Metadata is part of the same candidate as geometry and dimensions.
        try{ApplyMetadata();if(pendingDimension)ApplyDimensionCore(Draft);if(pendingCoordinates)ApplyCoordinatesCore(Draft);}
        catch(Exception ex){Status=ex.Message;return;}
        IsWorking=true;Status=Strings.SolvingSketch;var cancel=new CancellationTokenSource();cancellation=cancel;
        var reporting=new ReportingSolver(solver);
        try
        {
            var edit=await new UpsertSketchCommand(Sketch,reporting,newPartName:newPartName).PrepareAsync(new(baseline.Snapshot,generation,document.Session.Assets,kernel),cancel.Token);
            if(cancel.IsCancellationRequested||IsStale||disposed||document.Session.Generation!=generation){edit.Dispose();return;}
            try{edit.Snapshot.Validate();document.SetSketchPreview(edit.Snapshot);}catch{edit.Dispose();throw;}
            prepared=edit;Report=reporting.Report!;Draft.UseSolvedCoordinates(Report.Solution!);Refresh();
            int rebuilt=edit.Snapshot.Features.Values.Count(f=>baseline.Snapshot.Features.GetValueOrDefault(f.Id)?.Result.Revision!=f.Result.Revision);
            Status=string.Format(Strings.SketchPreviewReady,Report.DegreesOfFreedom,Report.RedundantConstraints.Length,rebuilt);
        }
        catch(OperationCanceledException){if(!IsStale)Status=Strings.DraftChanged;}
        catch(SketchSolveException ex){Report=ex.Report;Refresh();Status=string.Format(Strings.ConstraintSolveFailure,ex.Report.Status,ex.Report.ConflictingConstraints.Length);}
        catch(Exception ex){Status=ex.Message;}
        finally{IsWorking=false;cancel.Dispose();if(ReferenceEquals(cancellation,cancel))cancellation=null;}
    }
    [RelayCommand(CanExecute=nameof(CanConfirm))] private async Task ConfirmAsync()
    {
        if(prepared is null)return;IsWorking=true;committing=true;
        try
        {
            await document.Session.ExecuteAsync(new CommitSketchPreview(prepared.Snapshot,generation));
            document.SelectedSketchId=Sketch.Id;document.SelectedTargetPart=Sketch.PartId;document.FitView();CloseRequested?.Invoke(this,EventArgs.Empty);
        }
        catch(Exception ex){Invalidate();IsStale=true;Status=ex.Message;}
        finally{committing=false;IsWorking=false;}
    }
    [RelayCommand] private void Cancel(){Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    public ValueTask DisposeAsync()=>new(disposal??=DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        disposed=true;Invalidate();document.Session.Changed-=OnDocumentChanged;document.Detaching-=OnDetached;
        var tasks=new[]{PreviewCommand.ExecutionTask,ConfirmCommand.ExecutionTask}.Where(t=>t is not null).Cast<Task>();
        try{await Task.WhenAll(tasks);}finally{baseline.Dispose();}
    }
    private sealed class ReportingSolver(ISketchConstraintSolver inner):ISketchConstraintSolver
    {
        public SketchSolveReport? Report {get;private set;}
        public string Version=>inner.Version;
        public async Task<SketchSolveReport> SolveAsync(CadSketch sketch,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
            =>Report=await inner.SolveAsync(sketch,options,cancellationToken).ConfigureAwait(false);
    }
    private sealed class CommitSketchPreview(DocumentSnapshot snapshot,long generation):ICadDocumentCommand
    {
        public string Name=>Strings.EditSketch;
        public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {token.ThrowIfCancellationRequested();if(context.Generation!=generation)throw new StaleDocumentException();return Task.FromResult(new PreparedDocumentEdit(snapshot));}
    }
}
