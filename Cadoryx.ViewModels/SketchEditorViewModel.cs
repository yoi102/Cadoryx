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
public sealed record SketchOffsetJoinOption(SketchOffsetJoin Key,string Label);
public sealed record CircularContactOption(CircularContactBranch Key,string Label);
public sealed record SketchProjectionChoice(SketchId Sketch,SketchEntityId Entity,string Label);
public partial class SketchFeatureUpdateChoice(FeatureId id,string label):ObservableObject
{
    public FeatureId Id=>id;
    public string Label=>label;
    [ObservableProperty] private bool isSelected;
}

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
    private bool pendingContactBranch;
    private bool disposed;
    private Task? disposal;
    public SketchDraft Draft {get;}
    public CadSketch Sketch=>Draft.Value;
    public ObservableCollection<SketchEntityItem> Entities {get;}=[];
    public ObservableCollection<SketchConstraintItem> Constraints {get;}=[];
    public ObservableCollection<SketchEntityId> SelectedEntities {get;}=[];
    public IReadOnlyList<SketchOption> Tools {get;}=new[]{"Select","Point","Line","Rectangle","Circle","ArcSegment","BezierSegment","SplineRegion","TrimLine","ExtendLine","TrimCurve","ExtendCurve","OffsetCurve","OffsetLoop","OffsetRegions","Fillet","Chamfer","FilletCorners","ChamferCorners"}.Select(k=>new SketchOption(k,ToolLabel(k))).ToArray();
    public IReadOnlyList<SketchProjectionChoice> ProjectionSources {get;}
    [ObservableProperty] private SketchProjectionChoice? selectedProjectionSource;
    [ObservableProperty] private double offsetDistance=2;
    [ObservableProperty] private SketchOffsetJoin offsetJoin=SketchOffsetJoin.Miter;
    public IReadOnlyList<SketchOffsetJoinOption> OffsetJoins {get;}=
        [new(SketchOffsetJoin.Miter,Strings.OffsetJoinMiter),new(SketchOffsetJoin.Round,Strings.OffsetJoinRound),new(SketchOffsetJoin.Bevel,Strings.OffsetJoinBevel)];
    partial void OnOffsetJoinChanged(SketchOffsetJoin value)=>Invalidate();
    partial void OnOffsetDistanceChanged(double value)=>Invalidate();
    [ObservableProperty] private double filletRadius=2;
    [ObservableProperty] private bool constrainFilletRadius=true;
    [ObservableProperty] private double chamferDistanceA=2;
    [ObservableProperty] private double chamferDistanceB=2;
    public IReadOnlyList<SketchFeatureUpdateChoice> FeatureProfileUpdates {get;}
    public string CornerSelectionSummary=>string.Format(Strings.CornerSelectionSummary,SelectedEntities.Count,Draft.CornerReplacements.Length,FeatureProfileUpdates.Count(f=>f.IsSelected));
    public string LineEditingHint=>Tool switch{"TrimLine"=>Strings.TrimLineHint,"ExtendLine"=>Strings.ExtendLineHint,
        "TrimCurve"=>Strings.TrimCurveHint,"ExtendCurve"=>Strings.ExtendCurveHint,"OffsetCurve"=>Strings.OffsetCurveHint,"OffsetLoop"=>Strings.OffsetLoopHint,"OffsetRegions"=>Strings.OffsetRegionsHint,
        "Fillet"=>Strings.FilletHint,"Chamfer"=>Strings.ChamferHint,"FilletCorners" or "ChamferCorners"=>Strings.BatchCornersHint,_=>Strings.SketchHint};
    partial void OnToolChanged(string value)=>OnPropertyChanged(nameof(LineEditingHint));
    public IReadOnlyList<SketchOption> ConstraintKinds {get;}=new[]{"FixPoint","Coincident","Horizontal","Vertical","Distance","OffsetX","OffsetY","Length","Parallel","Perpendicular","EqualLength","Radius","EqualRadius","Tangent","Angle","Midpoint","Symmetry","EndpointTangent","EndpointCurvature","CircularContact","FiniteParametricContact"}.Select(k=>new SketchOption(k,ConstraintLabel(k))).ToArray();
    public string ConstraintSelectionHint=>ConstraintKind switch{"Midpoint"=>Strings.MidpointHint,"Symmetry"=>Strings.SymmetryHint,"EndpointTangent"=>Strings.EndpointTangentHint,"EndpointCurvature"=>CurvatureText("EndpointCurvatureHint"),"CircularContact"=>Strings.CircularContactHint,"FiniteParametricContact"=>Strings.FiniteContactHint,"Radius" or "EqualRadius"=>Strings.ArcRadiusHint,_=>""};
    partial void OnConstraintKindChanged(string value){OnPropertyChanged(nameof(ConstraintSelectionHint));OnPropertyChanged(nameof(ShowContactBranch));OnPropertyChanged(nameof(ShowFiniteContact));}
    public IReadOnlyList<CircularContactOption> ContactBranches {get;}=Enum.GetValues<CircularContactBranch>().Select(b=>new CircularContactOption(b,ContactBranchLabel(b))).ToArray();
    public bool ShowContactBranch=>ConstraintKind=="CircularContact"||Sketch.Constraints.Any(c=>c.Id==SelectedConstraint?.Id&&c is CircularContactConstraint);
    [ObservableProperty] private CircularContactBranch contactBranch=CircularContactBranch.External;
    partial void OnContactBranchChanged(CircularContactBranch value)
    {if(!loadingInputs&&Sketch.Constraints.Any(c=>c.Id==SelectedConstraint?.Id&&c is CircularContactConstraint)){pendingContactBranch=true;Invalidate();}}
    public IReadOnlyList<string> Planes=>CanChangePlane?["Document","XY","XZ","YZ",Strings.ExistingSketchPlane]:[Plane];
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
    [ObservableProperty] private int splineControlCount=5;
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

    private readonly Guid? datum;
    public SketchEditorViewModel(CadDocumentViewModel document,IGeometryKernel kernel,ISketchConstraintSolver solver,CadSketch sketch,bool isNew,Guid? datum=null)
    {
        this.document=document;this.kernel=kernel;this.solver=solver;baseline=document.Session.Capture();generation=document.Session.Generation;
        this.datum=datum;Draft=new(sketch,kernel as IExactSketchExpressionRegions);name=sketch.Name;status=Strings.DraftChanged;CanChangePlane=isNew&&datum is null;
        ProjectionSources=baseline.Snapshot.Sketches.Values.Where(s=>s.Id!=sketch.Id&&s.PartId==sketch.PartId).OrderBy(s=>s.Name)
            .SelectMany(s=>SketchProjectionGeometry.Entities(s).Select((id,index)=>new SketchProjectionChoice(s.Id,id,$"{s.Name} · {index+1} · "+
                (s.Lines.Any(l=>l.Id==id)?Strings.Line:s.Circles.Any(c=>c.Id==id)?Strings.Circle:s.Ellipses.Any(e=>e.Id==id)?Strings.ExactEllipse:s.Freeforms.Any(e=>e.Id==id)?Strings.ExactFreeform:Strings.ThreePointArc)))).ToArray();
        selectedProjectionSource=ProjectionSources.FirstOrDefault();
        FeatureProfileUpdates=baseline.Snapshot.Features.Values.Where(f=>f.SketchSource?.SketchId==sketch.Id&&f.Recipe is ExtrudeRecipe or RevolveRecipe)
            .OrderBy(f=>f.Name).Select(f=>new SketchFeatureUpdateChoice(f.Id,f.Name)).ToArray();
        foreach(var choice in FeatureProfileUpdates)choice.PropertyChanged+=(_,_)=>{Invalidate();OnPropertyChanged(nameof(CornerSelectionSummary));};
        plane=isNew&&sketch.Plane.Rotation==document.WorkPlaneSettings.Rotation?"Document":
            sketch.Plane.Rotation==Quaterniond.Identity?"XY":sketch.Plane.Rotation==Quaterniond.FromAxisAngle(new(1,0,0),Math.PI/2)?"XZ":
            sketch.Plane.Rotation==Quaterniond.FromAxisAngle(new(0,1,0),Math.PI/2)?"YZ":Strings.ExistingSketchPlane;
        originX=sketch.Plane.Translation.X;originY=sketch.Plane.Translation.Y;originZ=sketch.Plane.Translation.Z;
        newPartName=baseline.Snapshot.Definitions.ContainsKey(sketch.PartId)?null:Cadoryx.Lang.Strings.Strings.Part;
        document.InvalidatePreview();document.Session.Changed+=OnDocumentChanged;document.Detaching+=OnDetached;
        SelectedEntities.CollectionChanged+=(_,_)=>{UpdateCoordinates();OnPropertyChanged(nameof(CornerSelectionSummary));DrawingChanged?.Invoke(this,EventArgs.Empty);};Refresh();
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
    private void NotifyState(){OnPropertyChanged(nameof(CanEdit));OnPropertyChanged(nameof(CanConfirm));OnPropertyChanged(nameof(CanEditExpressionDistance));ApplyExpressionDistanceCommand.NotifyCanExecuteChanged();PreviewCommand.NotifyCanExecuteChanged();ConfirmCommand.NotifyCanExecuteChanged();NotifyRegionSourceState();}
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
        if(!refreshingSelection)LoadFiniteContactInputs();
        OnPropertyChanged(nameof(ShowContactBranch));pendingContactBranch=false;
        if(value is null||refreshingSelection)return;var constraint=Sketch.Constraints.FirstOrDefault(c=>c.Id==value.Id);if(constraint is null)return;
        loadingInputs=true;DimensionValue=SketchConstraintEditing.Value(constraint)??0;DimensionY=constraint is FixPointConstraint f?f.Position.Y:0;
        if(constraint is CircularContactConstraint contact)ContactBranch=contact.Branch;loadingInputs=false;pendingDimension=false;
        SelectedEntities.Clear();foreach(var target in SketchConstraintEditing.Targets(constraint))SelectedEntities.Add(target);
    }
    private void UpdateCoordinates()
    {
        OnPropertyChanged(nameof(HasSelectedPoint));OnPropertyChanged(nameof(HasSelectedCircle));
        UpdateSplineInputs();
        UpdateEllipseInputs();
        UpdateFreeformInputs();
        UpdateExpressionInputs();
        pendingCoordinates=false;if(SelectedEntities.Count!=1)return;var id=SelectedEntities[0];loadingInputs=true;
        if(Sketch.Points.FirstOrDefault(p=>p.Id==id) is {} point){PointX=point.Position.X;PointY=point.Position.Y;}
        if(Sketch.Circles.FirstOrDefault(c=>c.Id==id) is {} circle)CircleRadius=circle.Radius;
        loadingInputs=false;
    }
    public void Mutate(Action<SketchDraft> action)
    {
        if(!CanEdit)return;
        try{action(Draft);Invalidate();Refresh();Status=Strings.DraftChanged;}catch(Exception ex){Status=ErrorMessage(ex);}
    }
    private static string ErrorMessage(Exception ex)=>ex.Data["SketchCorner.Message"] is string key?
        string.Format(Strings.ResourceManager.GetString(key,Strings.Culture)??ex.Message,ex.Data["SketchCorner.Argument"]):
        ex.Data["SketchCurve.Message"] is string curveKey?Strings.ResourceManager.GetString(curveKey,Strings.Culture)??ex.Message:
        ex.Data["SketchContact.Message"] is string contactKey?Strings.ResourceManager.GetString(contactKey,Strings.Culture)??ex.Message:ex.Message;
    public void MovePoint(SketchEntityId point,Point2d position)=>Mutate(d=>d.MovePoint(point,position));
    [RelayCommand] private void ApplyFillet()
    {
        var before=Sketch;
        Mutate(d=>
        {
            if(Tool=="FilletCorners")d.EditCorners(SelectedEntities.ToArray(),true,FilletRadius,0,0,ConstrainFilletRadius);
            else
            {
                if(SelectedEntities.Count!=2)throw new CadValidationException(Strings.FilletHint);
                d.Fillet(SelectedEntities[0],SelectedEntities[1],FilletRadius,ConstrainFilletRadius);
            }
        });
        if(!ReferenceEquals(before,Sketch))Status=string.Format(Strings.CornerEditResult,before.Constraints.Count(c=>!Sketch.Constraints.Any(n=>n.Id==c.Id)),Draft.CornerReplacements.Length);
    }
    [RelayCommand] private void ApplyChamfer()
    {
        var before=Sketch;
        Mutate(d=>
        {
            if(Tool=="ChamferCorners")d.EditCorners(SelectedEntities.ToArray(),false,0,ChamferDistanceA,ChamferDistanceB);
            else
            {
                if(SelectedEntities.Count!=2)throw new CadValidationException(Strings.ChamferHint);
                d.Chamfer(SelectedEntities[0],SelectedEntities[1],ChamferDistanceA,ChamferDistanceB);
            }
        });
        if(!ReferenceEquals(before,Sketch))Status=string.Format(Strings.CornerEditResult,before.Constraints.Count(c=>!Sketch.Constraints.Any(n=>n.Id==c.Id)),Draft.CornerReplacements.Length);
    }
    public void EditLine(SketchEntityId? id,Point2d click)
    {
        if(id is null){Status=Strings.SelectStraightLine;return;}
        var before=Sketch;
        Mutate(d=>{if(Tool=="TrimLine")d.TrimLine(id.Value,click);else if(Tool=="ExtendLine")d.ExtendLine(id.Value,click);});
        if(!ReferenceEquals(before,Sketch))Status=string.Format(Strings.LineEditResult,before.Constraints.Length-Sketch.Constraints.Length);
    }
    public void EditCurve(SketchEntityId? id,Point2d click)
    {
        if(id is null){Status=Strings.SelectAnalyticalCurve;return;}
        var before=Sketch;SketchLoopOffset.CreatedBoundary? offset=null;Guid? regionGroup=null;
        Mutate(d=>
        {
            if(Sketch.ExpressionCurves.Any(c=>c.Id==id.Value)&&Tool is "TrimCurve" or "ExtendCurve")
            {
                var exact=kernel as IExactSketchGeometry??throw new CadValidationException(Strings.ExactSplineUnavailable);
                var expressions=kernel as IExactSketchExpressionGeometry??throw new CadValidationException(Strings.ExactSplineUnavailable);
                if(Tool=="TrimCurve")d.TrimExpressionAt(id.Value,click,exact,expressions);else d.ExtendExpressionAt(id.Value,click,exact,expressions);
            }
            else if(Tool=="OffsetCurve"&&(Sketch.ExpressionCurves.Any(c=>c.Id==id.Value)||Sketch.Ellipses.Any(c=>c.Id==id.Value)||
                Sketch.Freeforms.Any(c=>c.Id==id.Value)||Sketch.Beziers.Any(c=>c.Id==id.Value)||Sketch.Splines.Any(c=>c.Id==id.Value)))
                d.OffsetExactCurve(id.Value,OffsetDistance,kernel as IExactSketchExpressionGeometry??throw new CadValidationException(Strings.ExactSplineUnavailable));
            else if(Sketch.Splines.Any(c=>c.Id==id.Value)&&Tool is "TrimCurve" or "ExtendCurve")
            {
                var exact=kernel as IExactSketchGeometry??throw new CadValidationException(Strings.ExactSplineUnavailable);
                if(Tool=="TrimCurve")d.TrimSplineAt(id.Value,click,exact);else d.ExtendSplineAt(id.Value,click,exact);
            }
            else if(Sketch.Beziers.Any(c=>c.Id==id.Value)&&Tool is "TrimCurve" or "ExtendCurve")
            {
                var exact=kernel as IExactSketchGeometry??throw new CadValidationException(Strings.ExactSplineUnavailable);
                if(Tool=="TrimCurve")d.TrimBezierAt(id.Value,click,exact);else d.ExtendBezierAt(id.Value,click,exact);
            }
            else if(Tool=="TrimCurve")d.TrimCurve(id.Value,click);
            else if(Tool=="ExtendCurve")d.ExtendCurve(id.Value,click);
            else if(Tool=="OffsetCurve")d.OffsetCurve(id.Value,OffsetDistance);
            else if(Tool=="OffsetLoop")
            {
                if(NeedsExpressionRegion([id.Value]))regionGroup=d.OffsetExpressionRegions([id.Value],OffsetDistance,(SketchRegionAuthorJoin)OffsetJoin,
                    kernel as IExactSketchExpressionRegions??throw new CadValidationException(Strings.ExactSplineUnavailable));
                else offset=d.OffsetLoop(id.Value,OffsetDistance,OffsetJoin);
            }
        });
        if(!ReferenceEquals(before,Sketch)&&offset is not null)
        {
            SelectedEntities.Clear();SelectedEntities.Add(offset.Reference.CircleId??(!offset.Reference.MixedCurveIds.IsEmpty?
                offset.Reference.MixedCurveIds[0]:offset.Reference.PolygonLineIds[0]));
        }
        if(!ReferenceEquals(before,Sketch)&&regionGroup is {} group)SelectRegionRoots(group);
        if(!ReferenceEquals(before,Sketch))Status=Tool is "OffsetCurve" or "OffsetLoop"?Strings.OffsetCreated:string.Format(Strings.CurveEditResult,before.Constraints.Length-Sketch.Constraints.Length);
    }
    [RelayCommand] private void ApplyRegionOffset()
    {
        var before=Sketch;SketchRegionOffset.Result? result=null;Guid? regionGroup=null;
        Mutate(d=>
        {
            var seeds=SelectedEntities.ToArray();
            if(NeedsExpressionRegion(seeds))regionGroup=d.OffsetExpressionRegions(seeds,OffsetDistance,(SketchRegionAuthorJoin)OffsetJoin,
                kernel as IExactSketchExpressionRegions??throw new CadValidationException(Strings.ExactSplineUnavailable));
            else result=d.OffsetRegions(seeds,OffsetDistance,OffsetJoin);
        });
        if(!ReferenceEquals(before,Sketch)&&result is not null)
        {
            SelectedEntities.Clear();
            foreach(var boundary in result.Boundaries)
                SelectedEntities.Add(boundary.Created.CircleId??(!boundary.Created.MixedCurveIds.IsEmpty?
                    boundary.Created.MixedCurveIds[0]:boundary.Created.PolygonLineIds[0]));
            Status=string.Format(Strings.RegionOffsetCreated,result.Boundaries.Length,result.Roots.Length);
        }
        if(!ReferenceEquals(before,Sketch)&&regionGroup is {} group)
        {
            SelectRegionRoots(group);var g=Sketch.RegionOffsets.Single(g=>g.Id==group);
            Status=string.Format(Strings.RegionOffsetCreated,g.Loops.Length,g.Loops.Count(l=>l.Parent is null));
        }
    }
    private bool NeedsExpressionRegion(IEnumerable<SketchEntityId> seeds)=>SketchExpressionRegionSelection.Select(Sketch,seeds)
        .SelectMany(l=>l).Any(s=>!Sketch.Lines.Any(c=>c.Id==s.Id)&&!Sketch.Arcs.Any(c=>c.Id==s.Id)&&!Sketch.Circles.Any(c=>c.Id==s.Id));
    private void SelectRegionRoots(Guid group)
    {
        var ids=Sketch.RegionOffsets.Single(g=>g.Id==group).Loops.Select(l=>l.Outputs[0].Curve).ToArray();
        SelectedEntities.Clear();foreach(var id in ids)SelectedEntities.Add(id);
    }
    [RelayCommand] private void ProjectGeometry()=>Mutate(d=>
    {
        var source=SelectedProjectionSource??throw new CadValidationException(Strings.SelectProjectionSource);
        d.Project(baseline.Snapshot.Sketches[source.Sketch],source.Entity);
    });
    [RelayCommand] private void DetachProjection()=>Mutate(d=>
    {
        if(SelectedEntities.Count!=1)throw new CadValidationException(Strings.SelectAnalyticalCurve);
        d.DetachProjection(SelectedEntities[0]);
    });
    public void ResizeCircle(SketchEntityId circle,double radius)=>Mutate(d=>d.Change(s=>s with
    {
        Circles=s.Circles.Select(c=>c.Id==circle?c with{Radius=radius}:c).ToImmutableArray(),
        Constraints=s.Constraints.Select(c=>c is RadiusConstraint r&&r.Circle==circle?r with{Radius=radius}:c).ToImmutableArray()
    }));
    [RelayCommand] private void ApplyCoordinates()=>Mutate(ApplyCoordinatesCore);
    private void ApplyCoordinatesCore(SketchDraft d)
    {
        if(SelectedEntities.Count!=1)throw new CadValidationException(string.Format(Strings.SketchTargets,1));var id=SelectedEntities[0];
        if(Sketch.Points.Any(p=>p.Id==id))d.MovePoint(id,new(PointX,PointY));
        else if(Sketch.Circles.Any(c=>c.Id==id))d.Change(s=>s with{
            Circles=s.Circles.Select(c=>c.Id==id?c with{Radius=CircleRadius}:c).ToImmutableArray(),
            Constraints=s.Constraints.Select(c=>c is RadiusConstraint radius&&radius.Circle==id?radius with{Radius=CircleRadius}:c).ToImmutableArray()});
        pendingCoordinates=false;
    }
    private void ApplyMetadata()
    {
        var next=Sketch with{Name=Name,Plane=CanChangePlane?new(new(OriginX,OriginY,OriginZ),Plane switch
            {"Document"=>document.WorkPlaneSettings.Rotation,"XY"=>Quaterniond.Identity,"XZ"=>Quaterniond.FromAxisAngle(new(1,0,0),Math.PI/2),"YZ"=>Quaterniond.FromAxisAngle(new(0,1,0),Math.PI/2),_=>Sketch.Plane.Rotation}):Sketch.Plane};
        next.Validate();if(next!=Sketch)Draft.Change(_=>next);
    }
    [RelayCommand] private void Undo(){if(!CanEdit)return;Draft.Undo();RestoreMetadata();Invalidate();Refresh();}
    [RelayCommand] private void Redo(){if(!CanEdit)return;Draft.Redo();RestoreMetadata();Invalidate();Refresh();}
    private void RestoreMetadata(){Name=Sketch.Name;OriginX=Sketch.Plane.Translation.X;OriginY=Sketch.Plane.Translation.Y;OriginZ=Sketch.Plane.Translation.Z;
        if(CanChangePlane)Plane=Sketch.Plane.Rotation==document.WorkPlaneSettings.Rotation?"Document":
            Sketch.Plane.Rotation==Quaterniond.Identity?"XY":Sketch.Plane.Rotation==Quaterniond.FromAxisAngle(new(1,0,0),Math.PI/2)?"XZ":
            Sketch.Plane.Rotation==Quaterniond.FromAxisAngle(new(0,1,0),Math.PI/2)?"YZ":Strings.ExistingSketchPlane;
        pendingDimension=false;pendingCoordinates=false;pendingContactBranch=false;LoadFiniteContactInputs();
        if(Sketch.Constraints.FirstOrDefault(c=>c.Id==SelectedConstraint?.Id) is CircularContactConstraint contact)
        {loadingInputs=true;ContactBranch=contact.Branch;loadingInputs=false;}}
    [RelayCommand] private void DeleteEntities()=>Mutate(d=>d.RemoveEntities(SelectedEntities.ToArray()));
    [RelayCommand] private void ToggleConstruction()
    {
        var ids=SelectedEntities.ToArray();
        bool allConstruction=ids.Length>0&&ids.All(id=>Sketch.Points.Any(p=>p.Id==id&&p.IsConstruction)||
            Sketch.Lines.Any(l=>l.Id==id&&l.IsConstruction)||Sketch.Circles.Any(c=>c.Id==id&&c.IsConstruction)||
            Sketch.Arcs.Any(a=>a.Id==id&&a.IsConstruction)||Sketch.Beziers.Any(b=>b.Id==id&&b.IsConstruction)||
            Sketch.Splines.Any(b=>b.Id==id&&b.IsConstruction)||Sketch.Ellipses.Any(e=>e.Id==id&&e.IsConstruction)||Sketch.Freeforms.Any(e=>e.Id==id&&e.IsConstruction)||Sketch.ExpressionCurves.Any(e=>e.Id==id&&e.IsConstruction));
        Mutate(d=>d.SetConstruction(ids,!allConstruction));
    }
    [RelayCommand] private void DeleteConstraint()=>Mutate(d=>d.Change(s=>s with{Constraints=s.Constraints.Where(c=>c.Id!=SelectedConstraint?.Id).ToImmutableArray()}));
    [RelayCommand] private void ToggleConstraint()=>Mutate(d=>d.Change(s=>s with{Constraints=s.Constraints.Select(c=>c.Id==SelectedConstraint?.Id?c with{IsEnabled=!c.IsEnabled}:c).ToImmutableArray()}));
    [RelayCommand] private void ApplyDimension()=>Mutate(ApplyDimensionCore);
    [RelayCommand] private void ApplyContactBranch()=>Mutate(ApplyContactBranchCore);
    private void ApplyContactBranchCore(SketchDraft d)
    {d.Change(s=>s with{Constraints=s.Constraints.Select(c=>c.Id==SelectedConstraint?.Id&&c is CircularContactConstraint contact?
        contact with{Branch=ContactBranch}:c).ToImmutableArray()});pendingContactBranch=false;}
    private void ApplyDimensionCore(SketchDraft d)
    {d.Change(s=>s with{Constraints=s.Constraints.Select(c=>c.Id==SelectedConstraint?.Id?SketchConstraintEditing.SetValue(c,DimensionValue,DimensionY):c).ToImmutableArray()});pendingDimension=false;}
    [RelayCommand] private void AddConstraint()=>Mutate(d=>
    {
        var ids=SelectedEntities.ToArray();int count=ConstraintKind=="Symmetry"?3:ConstraintKind is "FixPoint" or "Horizontal" or "Vertical" or "Length" or "Radius"?1:2;
        if(ids.Length!=count)throw new CadValidationException(string.Format(Strings.SketchTargets,count));
        var id=SketchConstraintId.New();var a=ids[0];var b=count==2?ids[1]:default;
        SketchConstraint BasicRelation()
        {
            var line=ids.Where(id=>Sketch.Lines.Any(l=>l.Id==id)).ToArray();var points=ids.Where(id=>Sketch.Points.Any(p=>p.Id==id)).ToArray();
            if(line.Length!=1||points.Length!=count-1)throw new CadValidationException(ConstraintSelectionHint);
            return ConstraintKind=="Midpoint"?new MidpointConstraint(id,points[0],line[0]):new SymmetryConstraint(id,points[0],points[1],line[0]);
        }
        SketchConstraint c=ConstraintKind switch
        {
            "FixPoint"=>new FixPointConstraint(id,a,Sketch.Points.Single(p=>p.Id==a).Position),"Coincident"=>new CoincidentConstraint(id,a,b),
            "Horizontal"=>new HorizontalConstraint(id,a),"Vertical"=>new VerticalConstraint(id,a),"Distance"=>new DistanceConstraint(id,a,b,DimensionValue),
            "OffsetX"=>new OffsetXConstraint(id,a,b,DimensionValue),"OffsetY"=>new OffsetYConstraint(id,a,b,DimensionValue),"Length"=>new LengthConstraint(id,a,DimensionValue),
            "Parallel"=>new ParallelConstraint(id,a,b),"Perpendicular"=>new PerpendicularConstraint(id,a,b),"EqualLength"=>new EqualLengthConstraint(id,a,b),
            "Radius"=>new RadiusConstraint(id,a,DimensionValue),"EqualRadius"=>new EqualRadiusConstraint(id,a,b),
            "Tangent"=>Sketch.Lines.Any(l=>l.Id==a)&&Sketch.Circles.Any(c=>c.Id==b)?new TangentConstraint(id,a,b):
                Sketch.Lines.Any(l=>l.Id==b)&&Sketch.Circles.Any(c=>c.Id==a)?new TangentConstraint(id,b,a):
                throw new CadValidationException("Tangency requires one line and one circle."),
            "EndpointTangent"=>new EndpointTangentConstraint(id,a,b,SketchEndpointTangency.CommonEndpoint(Sketch,a,b)),
            "EndpointCurvature"=>SelectedEntities.Count==2?new EndpointCurvatureConstraint(id,a,b,SketchEndpointCurvature.CommonEndpoint(Sketch,a,b)):throw new CadValidationException(CurvatureText("EndpointCurvatureHint")),
            "CircularContact"=>SelectedEntities.Count==2?new CircularContactConstraint(id,a,b,ContactBranch):throw new CadValidationException(Strings.ContactInvalidTargets),
            "FiniteParametricContact"=>SelectedEntities.Count==2?ContactIntent(new FiniteParametricContactConstraint(id,a,b,ContactParameterA,ContactParameterB,FiniteContactBranch)):throw new CadValidationException(Strings.FiniteContactHint),
            "Angle"=>new AngleConstraint(id,a,b,DimensionValue*Math.PI/180),"Midpoint" or "Symmetry"=>BasicRelation(),_=>throw new NotSupportedException()
        };
        d.Change(s=>s with{Constraints=s.Constraints.Add(c)});
        pendingDimension=false;
    });
    private void Refresh()
    {
        var selected=SelectedConstraint?.Id;Entities.Clear();int i=0;
        foreach(var p in Sketch.Points)Entities.Add(new(p.Id,$"P{++i} ({Display(p.Position.X)}, {Display(p.Position.Y)})"));i=0;
        foreach(var l in Sketch.Lines)Entities.Add(new(l.Id,$"L{++i}"+(l.IsConstruction?" · "+Strings.ConstructionGeometry:"")));i=0;
        foreach(var c in Sketch.Circles)Entities.Add(new(c.Id,$"C{++i} · R {c.Radius:G6}"+(c.IsConstruction?" · "+Strings.ConstructionGeometry:"")));
        i=0;foreach(var arc in Sketch.Arcs)Entities.Add(new(arc.Id,$"A{++i} · "+(Strings.ResourceManager.GetString("ThreePointArc",Strings.Culture)??"Three-point arc")+
            (arc.IsConstruction?" · "+Strings.ConstructionGeometry:"")));
        i=0;foreach(var bezier in Sketch.Beziers)Entities.Add(new(bezier.Id,$"B{++i} · "+(Strings.ResourceManager.GetString("QuadraticBezier",Strings.Culture)??"Quadratic Bezier")+
            (bezier.IsConstruction?" · "+Strings.ConstructionGeometry:"")));
        i=0;foreach(var spline in Sketch.Splines)Entities.Add(new(spline.Id,$"S{++i} · "+
            (Strings.ResourceManager.GetString("CubicSpline",Strings.Culture)??"Cubic B-spline")+
            $" ({spline.Controls.Length})"+(spline.IsConstruction?" · "+Strings.ConstructionGeometry:"")));
        i=0;foreach(var ellipse in Sketch.Ellipses)Entities.Add(new(ellipse.Id,$"E{++i} · "+Strings.ExactEllipse+
            (ellipse.IsConstruction?" · "+Strings.ConstructionGeometry:"")));
        i=0;foreach(var freeform in Sketch.Freeforms)Entities.Add(new(freeform.Id,$"F{++i} · "+Strings.ExactFreeform+
            $" ({freeform.Geometry.Degree}, {freeform.Geometry.Poles.Length})"+(freeform.IsConstruction?" · "+Strings.ConstructionGeometry:"")));
        i=0;foreach(var expression in Sketch.ExpressionCurves)Entities.Add(new(expression.Id,$"X{++i} · "+Strings.ExactAssociatedOffset+
            (expression.IsConstruction?" · "+Strings.ConstructionGeometry:"")));
        foreach(var projection in Sketch.NativeProjections)
        {
            var item=Entities.Single(e=>e.Id==projection.TargetEntity);var suffix=SketchNativeProjectionGeometry.IsCurrent(baseline.Snapshot,Sketch,projection)?
                baseline.Snapshot.Features[projection.Selection.FeatureId].Name:Strings.NativeProjectionFrozen;
            Entities[Entities.IndexOf(item)]=item with{Label=item.Label+" · "+suffix};
        }
        var labels=Entities.ToDictionary(e=>e.Id,e=>e.Label.Split(' ')[0]);Constraints.Clear();i=0;
        foreach(var c in Sketch.Constraints)
        {
            string kind=c.GetType().Name.Replace("Constraint","");string targets=string.Join(", ",SketchConstraintEditing.Targets(c).Select(t=>labels[t]));
            string marker=Report?.ConflictingConstraints.Contains(c.Id)==true?"! ":Report?.RedundantConstraints.Contains(c.Id)==true?"≈ ":"";
            Constraints.Add(new(c.Id,$"{marker}{++i}. {ConstraintLabel(kind)} ({targets})"+(c is CircularContactConstraint contact?" · "+ContactBranchLabel(contact.Branch):"")+
                (c is FiniteParametricContactConstraint finite?$" · {finite.ParameterA:G6}/{finite.ParameterB:G6} · {FiniteContactBranchLabel(finite.Branch)} · {FiniteContactContinuityLabel(finite.Continuity)} · {FiniteContactParameterModeLabel(finite.ParameterMode)}":"")+(SketchConstraintEditing.Value(c) is {} v?$" = {v:G6}":"")+(c.IsEnabled?"":" [—]")));
        }
        // Refreshing diagnostic rows must not replace the user's canvas selection.
        refreshingSelection=true;SelectedConstraint=Constraints.FirstOrDefault(c=>c.Id==selected);refreshingSelection=false;
        foreach(var id in SelectedEntities.Where(id=>!Entities.Any(e=>e.Id==id)).ToArray())SelectedEntities.Remove(id);
        OnPropertyChanged(nameof(Sketch));OnPropertyChanged(nameof(CornerSelectionSummary));UpdateCoordinates();DrawingChanged?.Invoke(this,EventArgs.Empty);NotifyState();
    }
    private static string ToolLabel(string key)=>key switch
    {
        "Select"=>Strings.SelectOrDrag,
        "Point"=>Strings.Point,
        "Line"=>Strings.Line,
        "Rectangle"=>Strings.Rectangle,
        "Circle"=>Strings.Circle,
        "ArcSegment"=>Strings.ResourceManager.GetString("ThreePointArc",Strings.Culture)??"Three-point arc",
        "BezierSegment"=>Strings.ResourceManager.GetString("QuadraticBezier",Strings.Culture)??"Quadratic Bezier",
        "SplineRegion"=>Strings.ResourceManager.GetString("CubicSpline",Strings.Culture)??"Cubic B-spline region",
        "TrimLine"=>Strings.TrimLine,
        "ExtendLine"=>Strings.ExtendLine,
        "TrimCurve"=>Strings.TrimCurve,
        "ExtendCurve"=>Strings.ExtendCurve,
        "OffsetCurve"=>Strings.OffsetCurve,
        "OffsetLoop"=>Strings.OffsetLoop,
        "OffsetRegions"=>Strings.OffsetRegions,
        "Fillet"=>Strings.SketchFillet,
        "Chamfer"=>Strings.SketchChamfer,
        "FilletCorners"=>Strings.BatchFillet,
        "ChamferCorners"=>Strings.BatchChamfer,
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
        "Radius"=>Strings.CircularRadiusDimension,
        "EqualRadius"=>Strings.EqualCircularRadii,
        "Tangent"=>Strings.ResourceManager.GetString("TangentLineCircle",Strings.Culture)??"Line-circle tangent",
        "Angle"=>Strings.ResourceManager.GetString("AngleBetweenLines",Strings.Culture)??"Angle between lines",
        "Midpoint"=>Strings.MidpointRelation,
        "Symmetry"=>Strings.SymmetryRelation,
        "EndpointTangent"=>Strings.EndpointTangentRelation,
        "EndpointCurvature"=>CurvatureText("EndpointCurvatureRelation"),
        "FiniteParametricContact"=>Strings.FiniteContactRelation,
        "CircularContact"=>Strings.CircularContact,
        _=>key
    };
    private static string CurvatureText(string key)=>Strings.ResourceManager.GetString(key,Strings.Culture)??key;
    private static string ContactBranchLabel(CircularContactBranch branch)=>branch switch
    {CircularContactBranch.LineLeft=>Strings.ContactLineLeft,CircularContactBranch.LineRight=>Strings.ContactLineRight,
        CircularContactBranch.External=>Strings.ContactExternal,CircularContactBranch.AContainsB=>Strings.ContactAContainsB,
        CircularContactBranch.BContainsA=>Strings.ContactBContainsA,_=>Strings.ContactInvalidTargets};
    private static string Display(double value)=>(Math.Abs(value)<1e-9?0:value).ToString("G6");
    private void Invalidate()
    {
        cancellation?.Cancel();document.SetSketchPreview(null);prepared?.Dispose();prepared=null;Report=null;NotifyState();
    }
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task PreviewAsync()
    {
        Invalidate();if(document.Session.Generation!=generation){IsStale=true;Status=Strings.Stale;return;}
        // Metadata is part of the same candidate as geometry and dimensions.
        try{ApplyMetadata();if(pendingDimension)ApplyDimensionCore(Draft);if(pendingCoordinates)ApplyCoordinatesCore(Draft);if(pendingEllipse)ApplyEllipseCore(Draft);if(pendingFreeform)ApplyFreeformCore(Draft);if(pendingExpression)ApplyExpressionDistanceCore(Draft);if(pendingContactBranch)ApplyContactBranchCore(Draft);if(pendingFiniteContact)ApplyFiniteContactCore(Draft);}
        catch(Exception ex){Status=ErrorMessage(ex);return;}
        IsWorking=true;Status=Strings.SolvingSketch;var cancel=new CancellationTokenSource();cancellation=cancel;
        var reporting=new ReportingSolver(solver);
        try
        {
            var edit=await UpdateAssociatedSections.PrepareCommandAsync(new UpsertSketchCommand(Sketch,reporting,newPartName:newPartName,
                cornerReplacements:Draft.CornerReplacements,updateProfiles:FeatureProfileUpdates.Where(f=>f.IsSelected).Select(f=>f.Id).ToArray(),datum:datum),new(baseline.Snapshot,generation,document.Session.Assets,kernel),cancel.Token);
            if(cancel.IsCancellationRequested||IsStale||disposed||document.Session.Generation!=generation){edit.Dispose();return;}
            try{edit.Snapshot.Validate();document.SetSketchPreview(edit.Snapshot);}catch{edit.Dispose();throw;}
            prepared=edit;Report=reporting.Report!;Draft.UseSolvedCoordinates(Report.Solution!);Refresh();
            // The selection survives Refresh, so reload the newly solved actual parameters explicitly.
            LoadFiniteContactInputs();
            int rebuilt=edit.Snapshot.Features.Values.Count(f=>baseline.Snapshot.Features.GetValueOrDefault(f.Id)?.Result.Revision!=f.Result.Revision);
            Status=string.Format(Strings.SketchPreviewReady,Report.DegreesOfFreedom,Report.RedundantConstraints.Length,rebuilt);
            var frozen=edit.Snapshot.Features.Values.Where(f=>f.IsStale&&baseline.Snapshot.Features.GetValueOrDefault(f.Id)?.IsStale==false).ToArray();
            if(frozen.Length>0)Status+=" "+string.Format(Strings.SketchPreviewFrozen,frozen.Length,string.Join(", ",frozen.Take(5).Select(f=>f.Name)));
        }
        catch(OperationCanceledException){if(!IsStale)Status=Strings.DraftChanged;}
        catch(SketchSolveException ex){Report=ex.Report;Refresh();Status=ex.Report.Diagnostic switch
            {"SKETCH.CONTACT_OUTSIDE_DOMAIN"=>Strings.ContactOutsideDomain,"SKETCH.CONTACT_BRANCH"=>Strings.ContactBranchFailure,
                "SKETCH.CURVATURE_OUTPUT"=>CurvatureText("CurvatureOutputFailure"),
                "SKETCH.TANGENCY_BRANCH" when Sketch.Constraints.OfType<CircularContactConstraint>().Any(c=>c.IsEnabled)=>Strings.ContactBranchFailure,
                _=>string.Format(Strings.ConstraintSolveFailure,ex.Report.Status,ex.Report.ConflictingConstraints.Length)};}
        catch(Exception ex){Status=ErrorMessage(ex);}
        finally{IsWorking=false;cancel.Dispose();if(ReferenceEquals(cancellation,cancel))cancellation=null;}
    }
    [RelayCommand(CanExecute=nameof(CanConfirm))] private async Task ConfirmAsync()
    {
        if(prepared is null)return;IsWorking=true;committing=true;
        try
        {
            await document.Session.ExecuteAsync(new CommitPreparedDocumentCommand(prepared.Snapshot,generation,Strings.EditSketch));
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
        try{if(nativePicker is {} picker)await picker.DisposeAsync();await Task.WhenAll(tasks);}finally{baseline.Dispose();}
    }
    private sealed class ReportingSolver(ISketchConstraintSolver inner):ISketchConstraintSolver
    {
        public SketchSolveReport? Report {get;private set;}
        public string Version=>inner.Version;
        public async Task<SketchSolveReport> SolveAsync(CadSketch sketch,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
        {
            var result=await inner.SolveAsync(sketch,options,cancellationToken).ConfigureAwait(false);
            Report??=result;return result; // Downstream projection solves must not replace the edited sketch's report.
        }
    }
}
