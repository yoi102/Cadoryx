using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Cadoryx.Db;
using Cadoryx.Rendering;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public sealed record DatumAxisRow(CadDatumAxis Axis)
{
    public string Name=>Axis.Name;
    public string Kind=>DatumEditorViewModel.AxisLabel(Axis.Kind);
    public string State=>Axis.IsStale?Strings.Stale:Strings.Ready;
    public string Frame=>DatumEditorViewModel.DisplayPosition(Axis.Frame.Translation);
}
public sealed record DatumPointRow(CadDatumPoint Point)
{
    public string Name=>Point.Name;
    public string Kind=>DatumEditorViewModel.PointLabel(Point.Kind);
    public string State=>Point.IsStale?Strings.Stale:Strings.Ready;
    public string Position=>DatumEditorViewModel.DisplayPosition(Point.Position);
}
public sealed record DatumReferenceChoice(Guid? Id,string Label);
public sealed record DatumNativeSource(FeatureId Feature,BodyId Body,DefinitionId Part,string Label);

public partial class DatumEditorViewModel
{
    internal static string DisplayPosition(Vector3d p)
    {string Coordinate(double value)=>(Math.Abs(value)<1e-10?0:value).ToString("G7",System.Globalization.CultureInfo.CurrentCulture);return $"{Coordinate(p.X)}, {Coordinate(p.Y)}, {Coordinate(p.Z)}";}
    private ExactDatumGeometry? axisExact,pointExact,pickedPlane;
    private Task? pickTask;
    private double? originalPlaneValue;
    public ObservableCollection<DatumAxisRow> Axes {get;}=[];
    public ObservableCollection<DatumPointRow> Points {get;}=[];
    public ObservableCollection<DatumNativeSource> NativeSources {get;}=[];
    public ObservableCollection<DatumReferenceChoice> PlaneReferences {get;}=[];
    public ObservableCollection<DatumReferenceChoice> AxisReferences {get;}=[];
    public ObservableCollection<DatumReferenceChoice> PointReferences {get;}=[];
    public ObservableCollection<DatumReferenceChoice> OptionalAxes {get;}=[];
    public ObservableCollection<DatumReferenceChoice> OptionalPoints {get;}=[];
    public GuidedFrameViewModel PlaneSourceFrame {get;}=new();
    public IReadOnlyList<GuidedChoice<DatumAxisKind>> AxisKinds {get;}=Enum.GetValues<DatumAxisKind>().Select(k=>new GuidedChoice<DatumAxisKind>(k,AxisLabel(k))).ToArray();
    public IReadOnlyList<GuidedChoice<DatumPointKind>> PointKinds {get;}=Enum.GetValues<DatumPointKind>().Select(k=>new GuidedChoice<DatumPointKind>(k,PointLabel(k))).ToArray();
    public IReadOnlyList<DatumBasisAxis> BasisAxes {get;}=Enum.GetValues<DatumBasisAxis>();
    [ObservableProperty] private DatumAxisRow? selectedAxis;
    [ObservableProperty] private DatumPointRow? selectedPoint;
    [ObservableProperty] private DatumNativeSource? nativeSource;
    [ObservableProperty] private int constructionTab;
    [ObservableProperty] private bool pickEdges;
    [ObservableProperty] private string axisName="",pointName="";
    [ObservableProperty] private DatumAxisKind axisKind;
    [ObservableProperty] private DatumPointKind pointKind;
    [ObservableProperty] private DatumBasisAxis basisAxis=DatumBasisAxis.Z;
    [ObservableProperty] private bool axisReverse;
    [ObservableProperty] private double axisX,axisY,axisZ,axisDx,axisDy,axisDz=1,axisOffset,pointX,pointY,pointZ;
    [ObservableProperty] private DatumReferenceChoice? axisFirst,axisSecond,pointFirst,pointSecond,pointThird,normalAxis,normalPoint,angularAxis;
    public bool HasCandidate=>prepared is not null;
    public bool CanPickNative=>CanEdit&&!HasCandidate&&NativeSource is not null;
    public CadScene? Scene {get;private set;}
    public DocumentSnapshot Snapshot=>baseline.Snapshot;
    public IAssetStore Assets=>document.Session.Assets;
    public string AxisEvidence=>axisExact is {} e?Evidence(e):Strings.DatumNone;
    public string PointEvidence=>pointExact is {} e?Evidence(e):Strings.DatumNone;
    public string PlaneEvidence=>pickedPlane is {} e?Evidence(e):Strings.DatumNone;
    public bool IsPlaneConstruction=>ConstructionTab==0;
    public string CurrentNativeEvidence=>ConstructionTab switch{0=>PlaneEvidence,1=>AxisEvidence,_=>PointEvidence};
    private static string Evidence(ExactDatumGeometry e)=>e.Kind+" · #"+e.Selection.FullTopologyIndex+" · "+e.Selection.Revision.Value.ToString("N")[..8];
    public IEnumerable<ExactTopologySelection> NativeSelections=>new[]{axisExact,pointExact,pickedPlane}.OfType<ExactDatumGeometry>().Where(e=>NativeSource?.Feature==e.Selection.FeatureId&&Snapshot.Features[e.Selection.FeatureId].Result==e.Source).Select(e=>e.Selection);
    public IReadOnlyCollection<DatumReferenceChoice> AxisFirstSources=>AxisKind==DatumAxisKind.TwoPoints?PointReferences:PlaneReferences;
    public IReadOnlyCollection<DatumReferenceChoice> AxisSecondSources=>AxisFirstSources;
    public IReadOnlyCollection<DatumReferenceChoice> PointFirstSources=>PointKind==DatumPointKind.AxisPlane?AxisReferences:PointKind==DatumPointKind.ThreePlanes?PlaneReferences:PointReferences;
    public IReadOnlyCollection<DatumReferenceChoice> PointSecondSources=>PointKind is DatumPointKind.ThreePlanes or DatumPointKind.AxisPlane?PlaneReferences:PointReferences;
    public bool AxisNeedsFirst=>AxisKind is DatumAxisKind.TwoPoints or DatumAxisKind.PlaneIntersection or DatumAxisKind.PlaneNormal;
    public bool AxisNeedsSecond=>AxisKind is DatumAxisKind.TwoPoints or DatumAxisKind.PlaneIntersection;
    public bool AxisNeedsBasis=>AxisKind==DatumAxisKind.Basis;
    public bool AxisNeedsDirection=>AxisKind==DatumAxisKind.Independent;
    public bool AxisNeedsOrigin=>AxisKind!=DatumAxisKind.TwoPoints;
    public bool PointNeedsFirst=>PointKind is not (DatumPointKind.Independent or DatumPointKind.CircleCenter);
    public bool PointNeedsSecond=>PointKind is DatumPointKind.Midpoint or DatumPointKind.ThreePlanes or DatumPointKind.AxisPlane;
    public bool PointNeedsThird=>PointKind==DatumPointKind.ThreePlanes;
    public bool PointNeedsCoordinates=>PointKind is DatumPointKind.Independent or DatumPointKind.Offset;
    public bool HasConstructionInput
    {
        get
        {
            if(SelectedAxis is null&&AxisName.Length>0||SelectedPoint is null&&PointName.Length>0)return true;
            try{return SelectedAxis is {} a&&Authored(AxisRecipe(a.Axis.Id),a.Axis)!=a.Axis||SelectedPoint is {} p&&Authored(PointRecipe(p.Point.Id),p.Point)!=p.Point;}
            catch(CadValidationException){return true;}
        }
    }
    public event EventHandler? SceneChanged;
    public static string AxisLabel(DatumAxisKind k)=>k switch{DatumAxisKind.Basis=>Strings.DatumAxisBasis,DatumAxisKind.TwoPoints=>Strings.DatumAxisTwoPoints,
        DatumAxisKind.PlaneIntersection=>Strings.DatumAxisIntersection,DatumAxisKind.PlaneNormal=>Strings.DatumAxisNormal,DatumAxisKind.Analytic=>Strings.DatumAxisAnalytic,_=>Strings.DatumIndependent};
    public static string PointLabel(DatumPointKind k)=>k switch{DatumPointKind.Offset=>Strings.DatumPointOffset,DatumPointKind.Midpoint=>Strings.DatumPointMid,
        DatumPointKind.ThreePlanes=>Strings.DatumPointThreePlanes,DatumPointKind.AxisPlane=>Strings.DatumPointAxisPlane,DatumPointKind.CircleCenter=>Strings.DatumPointCircle,_=>Strings.DatumIndependent};
    private void InitializeConstructions()
    {
        ReloadConstructions();Axes.CollectionChanged+=(_,_)=>{if(!loading)Invalidate();};Points.CollectionChanged+=(_,_)=>{if(!loading)Invalidate();};
        PlaneSourceFrame.Changed+=OnSourceFrame;RefreshConstructionSources();
    }
    private void OnSourceFrame(object? sender,EventArgs e){if(!loading)Invalidate();}
    private void ReloadConstructions()
    {
        var oldLoading=loading;loading=true;
        try
        {var a=SelectedAxis?.Axis.Id;var p=SelectedPoint?.Point.Id;Axes.Clear();Points.Clear();foreach(var axis in associations.Axes.Values.OrderBy(a=>a.Name))Axes.Add(new(axis));foreach(var point in associations.Points.Values.OrderBy(p=>p.Name))Points.Add(new(point));SelectedAxis=Axes.FirstOrDefault(row=>row.Axis.Id==a);SelectedPoint=Points.FirstOrDefault(row=>row.Point.Id==p);}
        finally{loading=oldLoading;}
    }
    private void RefreshConstructionSources()
    {
        var firstAxis=AxisFirst?.Id;var secondAxis=AxisSecond?.Id;var firstPoint=PointFirst?.Id;var secondPoint=PointSecond?.Id;var thirdPoint=PointThird?.Id;
        var normal=NormalAxis?.Id;var point=NormalPoint?.Id;var angular=AngularAxis?.Id;var native=NativeSource?.Feature;
        var oldLoading=loading;loading=true;
        try
        {
            PlaneReferences.Clear();AxisReferences.Clear();PointReferences.Clear();OptionalAxes.Clear();OptionalPoints.Clear();NativeSources.Clear();OptionalAxes.Add(new(null,Strings.DatumNone));OptionalPoints.Add(new(null,Strings.DatumNone));
            if(Part is {} part)
            {
                foreach(var row in Planes.Where(p=>p.Plane.PartId==part.Id))PlaneReferences.Add(new(row.Plane.Id,row.Name));
                foreach(var row in Axes.Where(p=>p.Axis.PartId==part.Id)){AxisReferences.Add(new(row.Axis.Id,row.Name));OptionalAxes.Add(new(row.Axis.Id,row.Name));}
                foreach(var row in Points.Where(p=>p.Point.PartId==part.Id)){PointReferences.Add(new(row.Point.Id,row.Name));OptionalPoints.Add(new(row.Point.Id,row.Name));}
                foreach(var f in Snapshot.Features.Values.Where(f=>f.PartId==part.Id&&!f.IsStale&&!f.IsSuppressed).OrderBy(f=>f.Name))NativeSources.Add(new(f.Id,f.OutputBodyId,f.PartId,f.Name));
            }
            AxisFirst=AxisFirstSources.FirstOrDefault(r=>r.Id==firstAxis);AxisSecond=AxisSecondSources.FirstOrDefault(r=>r.Id==secondAxis);
            PointFirst=PointFirstSources.FirstOrDefault(r=>r.Id==firstPoint);PointSecond=PointSecondSources.FirstOrDefault(r=>r.Id==secondPoint);PointThird=PlaneReferences.FirstOrDefault(r=>r.Id==thirdPoint);
            NormalAxis=AxisReferences.FirstOrDefault(r=>r.Id==normal);NormalPoint=OptionalPoints.FirstOrDefault(r=>r.Id==point)??OptionalPoints[0];AngularAxis=OptionalAxes.FirstOrDefault(r=>r.Id==angular)??OptionalAxes[0];
            NativeSource=NativeSources.FirstOrDefault(r=>r.Feature==native);
        }
        finally{loading=oldLoading;}
        NotifyConstruction();ShowDatumSource();
    }
    private void NotifyConstruction()
    {
        foreach(var name in new[]{nameof(AxisFirstSources),nameof(AxisSecondSources),nameof(PointFirstSources),nameof(PointSecondSources),nameof(AxisNeedsFirst),nameof(AxisNeedsSecond),nameof(AxisNeedsBasis),nameof(AxisNeedsDirection),nameof(AxisNeedsOrigin),nameof(PointNeedsFirst),nameof(PointNeedsSecond),nameof(PointNeedsThird),nameof(PointNeedsCoordinates),nameof(AxisEvidence),nameof(PointEvidence),nameof(PlaneEvidence),nameof(CurrentNativeEvidence),nameof(IsPlaneConstruction),nameof(HasCandidate),nameof(CanPickNative)})OnPropertyChanged(name);
    }
    private void ShowDatumSource()
    {
        Scene=NativeSource is {} s?new(Snapshot.Id,Snapshot.StateId,[new(new OccurrencePath(Snapshot.Id,[]),s.Body,Snapshot.Features[s.Feature].Result,RigidTransform3d.Identity,0xFF94B8D8)]):new(Snapshot.Id,Snapshot.StateId,[]);
        SceneChanged?.Invoke(this,EventArgs.Empty);
    }
    partial void OnNativeSourceChanged(DatumNativeSource? value){if(!loading)Invalidate();ShowDatumSource();}
    partial void OnPickEdgesChanged(bool value){if(!loading)Invalidate();SceneChanged?.Invoke(this,EventArgs.Empty);}
    partial void OnConstructionTabChanged(int value){NotifyConstruction();SceneChanged?.Invoke(this,EventArgs.Empty);}
    partial void OnAxisKindChanged(DatumAxisKind value){RefreshConstructionSources();}
    partial void OnPointKindChanged(DatumPointKind value){RefreshConstructionSources();}
    partial void OnSelectedAxisChanged(DatumAxisRow? value)
    {
        if(value is null)return;var was=loading;loading=true;
        try
        {
            var a=value.Axis;Part=Parts.FirstOrDefault(p=>p.Id==a.PartId);AxisName=a.Name;AxisKind=a.Kind;BasisAxis=a.Basis;AxisReverse=a.Reverse;AxisX=a.Origin.X;AxisY=a.Origin.Y;AxisZ=a.Origin.Z;AxisDx=a.Direction.X;AxisDy=a.Direction.Y;AxisDz=a.Direction.Z;AxisOffset=a.Offset;axisExact=a.Exact;
            RefreshConstructionSources();AxisFirst=AxisFirstSources.FirstOrDefault(p=>p.Id==a.First);AxisSecond=AxisSecondSources.FirstOrDefault(p=>p.Id==a.Second);NotifyConstruction();
        }
        finally{loading=was;}
    }
    partial void OnSelectedPointChanged(DatumPointRow? value)
    {
        if(value is null)return;var was=loading;loading=true;
        try
        {
            var p=value.Point;Part=Parts.FirstOrDefault(choice=>choice.Id==p.PartId);PointName=p.Name;PointKind=p.Kind;PointX=p.Value.X;PointY=p.Value.Y;PointZ=p.Value.Z;pointExact=p.Exact;
            RefreshConstructionSources();PointFirst=PointFirstSources.FirstOrDefault(r=>r.Id==p.First);PointSecond=PointSecondSources.FirstOrDefault(r=>r.Id==p.Second);PointThird=PlaneReferences.FirstOrDefault(r=>r.Id==p.Third);NotifyConstruction();
        }
        finally{loading=was;}
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(!loading&&e.PropertyName is nameof(AxisName) or nameof(AxisKind) or nameof(BasisAxis) or nameof(AxisReverse) or nameof(AxisX) or nameof(AxisY) or nameof(AxisZ) or nameof(AxisDx) or nameof(AxisDy) or nameof(AxisDz) or nameof(AxisOffset) or nameof(AxisFirst) or nameof(AxisSecond)
            or nameof(PointName) or nameof(PointKind) or nameof(PointX) or nameof(PointY) or nameof(PointZ) or nameof(PointFirst) or nameof(PointSecond) or nameof(PointThird) or nameof(AngularAxis))Invalidate();
    }
    private CadDatumAxis AxisRecipe(Guid id)
    {
        var a=new CadDatumAxis(id,Part?.Id??throw new CadValidationException(Strings.DatumSelect),AxisName.Trim(),AxisKind,
            AxisNeedsFirst?AxisFirst?.Id:null,AxisNeedsSecond?AxisSecond?.Id:null,AxisKind==DatumAxisKind.Analytic?axisExact:null,AxisNeedsBasis?BasisAxis:DatumBasisAxis.Z,AxisReverse,
            AxisNeedsOrigin?new(AxisX,AxisY,AxisZ):Vector3d.Zero,AxisNeedsDirection?new(AxisDx,AxisDy,AxisDz):Vector3d.Zero,AxisOffset);a.Validate();return a;
    }
    private CadDatumPoint PointRecipe(Guid id)
    {
        var p=new CadDatumPoint(id,Part?.Id??throw new CadValidationException(Strings.DatumSelect),PointName.Trim(),PointKind,PointNeedsCoordinates?new(PointX,PointY,PointZ):Vector3d.Zero,
            PointNeedsFirst?PointFirst?.Id:null,PointNeedsSecond?PointSecond?.Id:null,PointNeedsThird?PointThird?.Id:null,PointKind==DatumPointKind.CircleCenter?pointExact:null);p.Validate();return p;
    }
    private static CadDatumAxis Authored(CadDatumAxis a,CadDatumAxis old)=>a with{Revision=old.Revision,Frame=old.Frame,SourceRevisions=old.SourceRevisions,IsStale=old.IsStale};
    private static CadDatumPoint Authored(CadDatumPoint p,CadDatumPoint old)=>p with{Revision=old.Revision,Position=old.Position,SourceRevisions=old.SourceRevisions,IsStale=old.IsStale};
    [RelayCommand] private void NewAxis(){if(!CanEdit)return;SelectedAxis=null;AxisName="";AxisKind=DatumAxisKind.Basis;BasisAxis=DatumBasisAxis.Z;AxisReverse=false;AxisX=AxisY=AxisZ=AxisOffset=0;axisExact=null;RefreshConstructionSources();Invalidate();}
    [RelayCommand] private void NewPoint(){if(!CanEdit)return;SelectedPoint=null;PointName="";PointKind=DatumPointKind.Independent;PointX=PointY=PointZ=0;pointExact=null;RefreshConstructionSources();Invalidate();}
    [RelayCommand] private void ApplyAxis()
    {
        if(!CanEdit)return;
        try{var a=AxisRecipe(SelectedAxis?.Axis.Id??Guid.NewGuid());if(SelectedAxis is {} old){if(old.Axis.PartId!=a.PartId)throw new CadValidationException(Strings.WrongReferenceContext);a=Authored(a,old.Axis);Axes.Remove(old);}Axes.Add(new(a));RefreshConstructionSources();SelectedAxis=Axes.Single(r=>r.Axis.Id==a.Id);Status=Strings.DatumHint;Invalidate();}
        catch(Exception e){Status=string.Format(Strings.ParameterError,e.Message);}
    }
    [RelayCommand] private void ApplyPoint()
    {
        if(!CanEdit)return;
        try{var p=PointRecipe(SelectedPoint?.Point.Id??Guid.NewGuid());if(SelectedPoint is {} old){if(old.Point.PartId!=p.PartId)throw new CadValidationException(Strings.WrongReferenceContext);p=Authored(p,old.Point);Points.Remove(old);}Points.Add(new(p));RefreshConstructionSources();SelectedPoint=Points.Single(r=>r.Point.Id==p.Id);Status=Strings.DatumHint;Invalidate();}
        catch(Exception e){Status=string.Format(Strings.ParameterError,e.Message);}
    }
    [RelayCommand] private void DeleteAxis(){if(!CanEdit||SelectedAxis is not {} a)return;Axes.Remove(a);NewAxis();RefreshSources();}
    [RelayCommand] private void DeletePoint(){if(!CanEdit||SelectedPoint is not {} p)return;Points.Remove(p);NewPoint();RefreshSources();}
    [RelayCommand] private void DetachAxis()
    {if(!CanEdit||SelectedAxis is not {} a)return;AxisKind=DatumAxisKind.Independent;AxisX=a.Axis.Frame.Translation.X;AxisY=a.Axis.Frame.Translation.Y;AxisZ=a.Axis.Frame.Translation.Z;var d=a.Axis.Frame.Rotation.Rotate(Vector3d.UnitZ);AxisDx=d.X;AxisDy=d.Y;AxisDz=d.Z;AxisOffset=0;AxisReverse=false;axisExact=null;ApplyAxis();}
    [RelayCommand] private void DetachPoint()
    {if(!CanEdit||SelectedPoint is not {} p)return;PointKind=DatumPointKind.Independent;PointX=p.Point.Position.X;PointY=p.Point.Position.Y;PointZ=p.Point.Position.Z;pointExact=null;ApplyPoint();}
    [RelayCommand] private void DetachPlane()
    {if(!CanEdit||SelectedPlane is not {} p)return;PlaneSourceFrame.Load(p.Plane.Frame);Kind=DatumPlaneKind.Offset;Value=0;AngularAxis=OptionalAxes[0];SetPrimary(DatumPlaneSource.Independent(p.Plane.Frame));ApplyPlane();}
    private void SetPrimary(DatumPlaneSource source)
    {var choice=Sources.FirstOrDefault(c=>c.Source==source);if(choice is null){choice=new(source,Strings.DatumIndependent+" · "+(source.Exact is {} e?Evidence(e):source.Kind.ToString()));Sources.Add(choice);}Primary=choice;}
    [RelayCommand] private void UsePickedPlane()
    {if(!CanEdit||pickedPlane is null)return;try{SetPrimary(DatumPlaneSource.Face(pickedPlane,PlaneSourceFrame.Value));Invalidate();}catch(Exception e){Status=e.Message;}}
    [RelayCommand] private void UseIndependentPlane(){if(CanEdit)SetPrimary(DatumPlaneSource.Independent(PlaneSourceFrame.Value));}
    [RelayCommand] private void UseAxisNormal()
    {if(!CanEdit||NormalAxis?.Id is not {} id)return;SetPrimary(DatumPlaneSource.Normal(id,NormalPoint?.Id));}
    public Task PickExactAsync(ExactTopologySelection selection)=>pickTask=ResolvePickAsync(selection);
    private async Task ResolvePickAsync(ExactTopologySelection selection)
    {
        if(!CanPickNative)return;Invalidate();var target=ConstructionTab;IsWorking=true;var cancel=new CancellationTokenSource();cancellation=cancel;
        try
        {
            if(selection.FeatureId!=NativeSource!.Feature||selection.DocumentId!=Snapshot.Id||selection.Kind!=(PickEdges?HistoryShapeKind.Edge:HistoryShapeKind.Face))throw new CadValidationException(Strings.WrongReferenceContext);
            if(kernel is not IDatumGeometryResolver resolver)throw new NotSupportedException("Kernel has no analytic datum resolver.");
            var result=await resolver.ResolveDatumGeometryAsync(Snapshot,selection,Assets,cancel.Token);
            if(disposed||IsStale||cancel.IsCancellationRequested||document.Session.Generation!=generation)return;
            loading=true;
            try
            {
                if(target==0){if(result.Kind!=ExactDatumKind.Plane)throw new CadValidationException(Strings.DatumSelect);pickedPlane=result;}
                else if(target==1){if(result.Kind==ExactDatumKind.Plane)throw new CadValidationException(Strings.DatumSelect);axisExact=result;AxisKind=DatumAxisKind.Analytic;}
                else{if(result.Kind!=ExactDatumKind.Circle)throw new CadValidationException(Strings.DatumSelect);pointExact=result;PointKind=DatumPointKind.CircleCenter;}
            }
            finally{loading=false;}
            NotifyConstruction();Status=Strings.DatumNativeHint;SceneChanged?.Invoke(this,EventArgs.Empty);
        }
        catch(OperationCanceledException){if(!IsStale)Status=Strings.DatumNativeHint;}
        catch(Exception e){Status=string.Format(Strings.ParameterError,e.Message);}
        finally{IsWorking=false;cancel.Dispose();if(ReferenceEquals(cancellation,cancel))cancellation=null;}
    }
}
