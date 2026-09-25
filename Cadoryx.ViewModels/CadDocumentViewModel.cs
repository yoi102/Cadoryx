using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel;
using AvalonDock.Mvvm.CommunityToolkit;
using Cadoryx.Db;
using Cadoryx.Lang.Strings;
using Cadoryx.Commands;
using Cadoryx.Editor;
using Cadoryx.Rendering;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Services.Platform.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public partial class CadDocumentViewModel : ObservableDocument
{
    private readonly IGeometryKernel kernel;
    private readonly ICadMessageLog log;
    private readonly SemaphoreSlim gridEditQueue=new(1,1);
    private DocumentSnapshot observedSnapshot;
    private PreparedDocumentEdit? prepared;
    private long preparedGeneration;
    private long previewSequence;
    private CancellationTokenSource? previewCancellation;
    private bool allowClose;
    private bool refreshingTargets;
    private Quaterniond placementRotation=Quaterniond.Identity;
    private Vector3d constructionAnchor;
    private int constructionStage,constructionBaseY,constructionStartX;
    private double constructionScale=1;
    public bool GridVisible => Session.Snapshot.Settings.Grid.Visible;
    public double GridSpacingMm => Session.Snapshot.Settings.Grid.SpacingMm;
    public bool SnapToGrid => Session.Snapshot.Settings.Grid.Snap;
    public DocumentWorkPlaneSettings WorkPlaneSettings => Session.Snapshot.Settings.WorkPlane;
    public uint BackgroundTopArgb => Session.Snapshot.Settings.BackgroundTopArgb;
    public uint BackgroundBottomArgb => Session.Snapshot.Settings.BackgroundBottomArgb;
    public DocumentOriginSettings OriginSettings => Session.Snapshot.Settings.Origin;
    public string ViewportDrawingLabel=>Strings.ResourceManager.GetString("DrawInViewport")??"Draw in viewport";
    public string ViewportDrawingHint=>Strings.ResourceManager.GetString(ToolKind is "Extrude" or "Revolve"?"ViewportProfileDrawingHint":"ViewportDrawingHint")??"Click to set the dimensions.";
    public event EventHandler? ViewportSettingsChanged;
    public async Task SetGridAsync(bool? visible=null,double? spacingMm=null,bool? snap=null)
    {
        await gridEditQueue.WaitAsync();
        try
        {
            var grid=Session.Snapshot.Settings.Grid;
            await Session.ExecuteAsync(DocumentEdits.SetGrid(grid with
            { Visible=visible??grid.Visible, SpacingMm=spacingMm??grid.SpacingMm, Snap=snap??grid.Snap }));
        }
        finally {gridEditQueue.Release();}
    }
    public async Task SetWorkPlaneAsync(DocumentWorkPlaneSettings plane)
    {
        ArgumentNullException.ThrowIfNull(plane);
        await Session.ExecuteAsync(DocumentEdits.SetSettings(Session.Snapshot.Settings with { WorkPlane=plane }));
    }
    public CadDocumentSession Session {get;}
    public SelectionService Selection {get;}=new();
    public InstancePlacementViewModel Placement {get;}
    public AssemblyConstraintsViewModel AssemblyConstraints {get;}
    public ObservableCollection<PartDefinition> TargetParts {get;}=[];
    public ObservableCollection<CadLayer> CreationLayers {get;}=[];
    public ObservableCollection<CadMaterial> CreationMaterials {get;}=[];
    [ObservableProperty] private SketchId? selectedSketchId;
    [ObservableProperty] private SketchProfileChoice? selectedSketchProfile;
    [ObservableProperty] private bool useLinkedSketch;
    public bool IsProfileTool=>ToolKind is "Extrude" or "Revolve";
    public bool IsFrozenProfile=>!UseLinkedSketch||!IsProfileTool;
    public string ProfileInputHint=>UseLinkedSketch?Strings.LinkedSketchHint:Strings.FrozenPolygon;
    public ObservableCollection<SketchProfileChoice> SketchProfiles {get;}=[];
    public ObservableCollection<SketchHoleChoice> SketchHoleChoices {get;}=[];
    public ObservableCollection<SketchPolygonHoleChoice> SketchPolygonHoleChoices {get;}=[];
    public ObservableCollection<SketchPolygonHoleChoice> SketchMixedHoleChoices {get;}=[];
    public ObservableCollection<SketchHoleChoice> SketchIslandCircleChoices {get;}=[];
    public ObservableCollection<SketchPolygonHoleChoice> SketchIslandPolygonChoices {get;}=[];
    public ObservableCollection<SketchPolygonHoleChoice> SketchIslandMixedChoices {get;}=[];
    public bool CanChooseSketchProfile=>UseLinkedSketch&&(EditingFeature is null||
        Session.Snapshot.Features[EditingFeature.Value].Recipe is ExtrudeRecipe);
    public bool CanChooseSketchHoles=>CanChooseSketchProfile&&ToolKind=="Extrude"&&
        SelectedSketchProfile is {} selected&&selected.Source.ArcId is null&&selected.Source.BezierId is null&&selected.Source.SplineId is null;
    public bool CanChangeSketchAssociation=>EditingFeature is null;
    [ObservableProperty] private DefinitionId? selectedTargetPart;
    [ObservableProperty] private LayerId? selectedCreationLayer;
    [ObservableProperty] private MaterialId? selectedCreationMaterial;
    public bool IsCreating=>EditingFeature is null;
    public string TargetPartHint=>TargetParts.Count==0?Strings.FirstPartCreatedAutomatically:Strings.SharedPartEditingHint;
    public string ContentId=>Id;
    public CadScene Scene=>CadScene.FromDocument(Session.Snapshot);
    public CadScene? PreviewScene {get;private set;}
    public CadCamera? Camera {get;set;}
    public CadDisplayMode CurrentDisplayMode {get;private set;}
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand),nameof(BooleanPreviewCommand),nameof(ConfirmCommand))]
    private bool isClosingRequested;
    public event EventHandler? Activated;
    public event EventHandler<FeatureId>? LocalFeatureEditRequested;
    public event EventHandler? Detaching;
    public bool IsDetached {get;private set;}
    public event EventHandler? CloseRequested;
    public event EventHandler? SceneChanged;
    public event EventHandler? PreviewChanged;
    public event EventHandler? FitRequested;
    public event EventHandler<CadProjection>? ProjectionRequested;
    public event EventHandler<CadDisplayMode>? DisplayModeRequested;
    public ObservableCollection<ProfilePointViewModel> ProfilePoints {get;}=[new(0,0),new(30,0),new(30,20),new(0,20)];
    [ObservableProperty] private string toolKind="Box";
    [ObservableProperty] private bool isViewportConstructing;
    [ObservableProperty] private string objectName="Box";
    [ObservableProperty] private double sizeX=40;
    [ObservableProperty] private double sizeY=30;
    [ObservableProperty] private double sizeZ=20;
    [ObservableProperty] private double positionX;
    [ObservableProperty] private double positionY;
    [ObservableProperty] private double positionZ;
    [ObservableProperty] private double angleDegrees=360;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand),nameof(BooleanPreviewCommand),nameof(ConfirmCommand))]
    private bool isWorking;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool hasPreview;
    [ObservableProperty] private string toolStatus=Strings.ToolStatusHint;
    [ObservableProperty] private GeometryAssetRef? previewGeometry;
    [ObservableProperty] private FeatureId? editingFeature;
    public bool IsReadOnly=>!Session.Snapshot.Extensions.IsDefaultOrEmpty;

    public CadDocumentViewModel(CadDocumentSession session,IGeometryKernel kernel,ICadMessageLog log)
    {
        Session=session;observedSnapshot=session.Snapshot;this.kernel=kernel;this.log=log;Id="document."+Guid.NewGuid().ToString("N");Context=this;
        Session.Changed+=OnDocumentChanged;Session.StatusChanged+=OnSessionStatus;
        Session.ObserverFailed+=OnObserverFailed;
        PropertyChanged+=OnPropertiesChanged;
        foreach(var p in ProfilePoints)p.PropertyChanged+=OnProfilePointChanged;
        ProfilePoints.CollectionChanged+=(_,e)=>
        {
            if(e.OldItems is not null)foreach(ProfilePointViewModel p in e.OldItems)p.PropertyChanged-=OnProfilePointChanged;
            if(e.NewItems is not null)foreach(ProfilePointViewModel p in e.NewItems)p.PropertyChanged+=OnProfilePointChanged;
            InvalidatePreview();
        };
        Placement=new(this);AssemblyConstraints=new(this);RefreshTargets();UpdateTitle();
    }
    public override void OnSelected(){Activated?.Invoke(this,EventArgs.Empty);}
    public override bool OnClose(){if(allowClose)return true;CloseRequested?.Invoke(this,EventArgs.Empty);return false;}
    public void PermitClose()=>allowClose=true;
    public void FitView()=>FitRequested?.Invoke(this,EventArgs.Empty);
    public void SetView(CadProjection projection)=>ProjectionRequested?.Invoke(this,projection);
    public void SetDisplay(CadDisplayMode mode){CurrentDisplayMode=mode;DisplayModeRequested?.Invoke(this,mode);}
    public void StartTool(string kind)
    {
        IsViewportConstructing=false;InvalidatePreview();EditingFeature=null;UseLinkedSketch=false;SelectedSketchProfile=null;placementRotation=Quaterniond.Identity;ToolKind=kind;ObjectName=kind;ToolStatus=Strings.ToolStatusHint;
        if(Selection.Items.FirstOrDefault() is {} selection)SelectedTargetPart=Session.Snapshot.Bodies[selection.BodyId].PartId;
        else if(Selection.Occurrence is {} path&&Session.Snapshot.Definitions[OccurrencePlacement.Resolve(Session.Snapshot,path).Slot.DefinitionId] is PartDefinition part)SelectedTargetPart=part.Id;
        BeginViewportConstruction();
    }
    [RelayCommand] public void BeginViewportConstruction()
    {
        if(IsReadOnly||EditingFeature is not null||ToolKind is not ("Box" or "Cylinder" or "Extrude" or "Revolve"))return;
        InvalidatePreview();constructionStage=0;IsViewportConstructing=true;
        ToolStatus=ViewportDrawingHint;
    }
    public void CancelViewportConstruction()
    {constructionStage=0;IsViewportConstructing=false;InvalidatePreview();}
    public (GeometryRecipe? Ghost,bool PreviewNow) ConstructionPointer(Vector3d point,int x,int y,double pixelsPerMm,bool click)
    {
        if(!IsViewportConstructing)return(null,false);
        bool primitive=ToolKind is "Box" or "Cylinder";
        if(constructionStage==0)
        {
            if(!click)return(null,false);
            constructionAnchor=point;constructionBaseY=y;constructionStartX=x;constructionScale=pixelsPerMm;constructionStage=1;
            if(!UseLinkedSketch){PositionX=point.X;PositionY=point.Y;PositionZ=point.Z;placementRotation=WorkPlaneSettings.Rotation;}
            return(null,false);
        }
        if(constructionStage==1&&primitive)
        {
            var anchor=WorkPlaneSettings.ToLocal(constructionAnchor);
            var local=WorkPlaneSettings.ToLocal(point);
            double dx=local.X-anchor.X,dy=local.Y-anchor.Y;
            if(ToolKind=="Box")
            {
                var corner=WorkPlaneSettings.ToWorld(Math.Min(local.X,anchor.X),Math.Min(local.Y,anchor.Y));
                PositionX=corner.X;PositionY=corner.Y;PositionZ=corner.Z;
                SizeX=Math.Max(0.001,Math.Abs(dx));SizeY=Math.Max(0.001,Math.Abs(dy));
            }
            else{PositionX=constructionAnchor.X;PositionY=constructionAnchor.Y;PositionZ=constructionAnchor.Z;SizeX=Math.Max(0.001,Math.Sqrt(dx*dx+dy*dy));}
            bool valid=ToolKind=="Box"?Math.Abs(dx)>0.001&&Math.Abs(dy)>0.001:Math.Sqrt(dx*dx+dy*dy)>0.001;
            if(click&&valid){constructionStage=2;constructionBaseY=y;constructionScale=pixelsPerMm;}
            return(Recipe(),false);
        }
        if(constructionStage==1&&!primitive)
        {
            double measure=ToolKind=="Extrude"?Math.Abs(y-constructionBaseY)/Math.Max(constructionScale,0.01):Math.Abs(x-constructionStartX)*0.5;
            if(ToolKind=="Extrude")SizeZ=Math.Max(0.001,measure);
            else AngleDegrees=Math.Clamp(measure,0.01,360);
            if(click&&measure>0.001){constructionStage=0;IsViewportConstructing=false;return(null,true);}
            return(TryProfileConstructionGhost(),false);
        }
        double height=Math.Abs(y-constructionBaseY)/Math.Max(constructionScale,0.01);
        if(SnapToGrid)height=Math.Round(height/GridSpacingMm)*GridSpacingMm;
        SizeZ=Math.Max(0.001,height);
        var ghost=Recipe();
        if(click&&height>0.001){constructionStage=0;IsViewportConstructing=false;return(null,true);}
        return(ghost,false);
    }
    private GeometryRecipe? TryProfileConstructionGhost()
    {
        // An unselected or temporarily invalid profile must not abort the pointer gesture.
        if(UseLinkedSketch&&SelectedSketchProfile is null)return null;
        try{var recipe=Recipe();recipe.Validate();return recipe;}
        catch(CadValidationException){return null;}
    }
    public void EditFeature(FeatureId id)
    {
        var feature=Session.Snapshot.Features[id];InvalidatePreview();EditingFeature=id;SelectedTargetPart=feature.PartId;ObjectName=feature.Name;
        if(feature.Recipe is LocalFeatureRecipe or HistoryFilletRecipe or HistoryChamferRecipe)
        {
            EditingFeature=null;
            LocalFeatureEditRequested?.Invoke(this,id);
            return;
        }
        switch(feature.Recipe)
        {
            case BoxRecipe b:ToolKind="Box";SizeX=b.X;SizeY=b.Y;SizeZ=b.Z;SetPosition(b.Placement);break;
            case CylinderRecipe c:ToolKind="Cylinder";SizeX=c.Radius;SizeZ=c.Height;SetPosition(c.Placement);break;
            case ExtrudeRecipe e:ToolKind="Extrude";SizeZ=e.Distance;SetPosition(e.Placement);LoadProfile(e.Profile);break;
            case RevolveRecipe r:ToolKind="Revolve";AngleDegrees=r.AngleRadians*180/Math.PI;SetPosition(r.Placement);LoadProfile(r.Profile);break;
            default:EditingFeature=null;ToolStatus=Strings.UnsupportedFeatureEdit;return;
        }
        ToolStatus=Strings.EditingFeatureStatus;
        UseLinkedSketch=feature.SketchSource is not null;
        SelectedSketchProfile=feature.SketchSource is {} source?SketchProfiles.FirstOrDefault(p=>SameProfileSource(p.Source,source)):null;
        if(feature.SketchSource is {} linked&&SelectedSketchProfile is null){var choice=new SketchProfileChoice(Session.Snapshot.Sketches[linked.SketchId].Name,linked);SketchProfiles.Add(choice);SelectedSketchProfile=choice;}
    }
    public void StartSketchFeature(string kind)
    {
        var sketch=SelectedSketchId;StartTool(kind);UseLinkedSketch=true;
        SelectedSketchProfile=SketchProfiles.FirstOrDefault(p=>p.Source.SketchId==sketch)??SketchProfiles.FirstOrDefault();
        ToolStatus=SelectedSketchProfile is null?Strings.PickProfile:Strings.LinkedSketchHint;
    }
    private void SetPosition(RigidTransform3d p){PositionX=p.Translation.X;PositionY=p.Translation.Y;PositionZ=p.Translation.Z;placementRotation=p.Rotation;}
    private void LoadProfile(SketchProfile profile){ProfilePoints.Clear();foreach(var p in profile.Points)ProfilePoints.Add(new(p.X,p.Y));}
    private GeometryRecipe Recipe()
    {
        var placement=new RigidTransform3d(new(PositionX,PositionY,PositionZ),placementRotation);
        var profile=new SketchProfile(ProfilePoints.Select(p=>new Point2d(p.X,p.Y)).ToImmutableArray());
        GeometryRecipe recipe=ToolKind switch
        {
            "Box"=>new BoxRecipe(SizeX,SizeY,SizeZ,placement),
            "Cylinder"=>new CylinderRecipe(SizeX,SizeZ,placement),
            "Extrude"=>new ExtrudeRecipe(profile,SizeZ,placement),
            "Revolve"=>new RevolveRecipe(profile,AngleDegrees*Math.PI/180,placement),
            _=>throw new CadValidationException("Choose a supported modeling tool.")
        };
        if(UseLinkedSketch&&ToolKind is "Extrude" or "Revolve")
        {
            var source=CurrentSketchSource()??(EditingFeature is {} id?Session.Snapshot.Features[id].SketchSource:null)
                ??throw new CadValidationException(Strings.PickProfile);
            return source.Resolve(Session.Snapshot,SelectedTargetPart??throw new CadValidationException(Strings.SelectTargetPart),recipe);
        }
        return recipe;
    }
    private SketchProfileReference? CurrentSketchSource()=>SelectedSketchProfile?.Source is {} source&&UseLinkedSketch?
        source with{HoleCircleIds=ToolKind=="Extrude"?SketchHoleChoices.Where(c=>c.IsSelected).Select(c=>c.Id).ToImmutableArray():[],
            PolygonHoleLines=ToolKind=="Extrude"?SketchPolygonHoleChoices.Where(c=>c.IsSelected).Select(c=>c.Lines).ToImmutableArray():[],
            MixedHoleIds=ToolKind=="Extrude"?SketchMixedHoleChoices.Where(c=>c.IsSelected).Select(c=>c.Lines).ToImmutableArray():[],
            IslandCircleIds=ToolKind=="Extrude"?SketchIslandCircleChoices.Where(c=>c.IsSelected).Select(c=>c.Id).ToImmutableArray():[],
            IslandPolygonLines=ToolKind=="Extrude"?SketchIslandPolygonChoices.Where(c=>c.IsSelected).Select(c=>c.Lines).ToImmutableArray():[],
            IslandMixedIds=ToolKind=="Extrude"?SketchIslandMixedChoices.Where(c=>c.IsSelected).Select(c=>c.Lines).ToImmutableArray():[]}:null;
    private ICadDocumentCommand ToolCommand()=>EditingFeature is {} feature?new RecomputeCommand(feature,Recipe(),
            UseLinkedSketch&&ToolKind=="Extrude"?CurrentSketchSource():null):
        new AddBodyCommand(Recipe(),ObjectName,SelectedTargetPart,SelectedCreationLayer,SelectedCreationMaterial,
            UseLinkedSketch&&ToolKind is "Extrude" or "Revolve"?CurrentSketchSource():null);
    partial void OnUseLinkedSketchChanged(bool value){InvalidatePreview();OnPropertyChanged(nameof(IsFrozenProfile));OnPropertyChanged(nameof(ProfileInputHint));OnPropertyChanged(nameof(CanChooseSketchProfile));OnPropertyChanged(nameof(CanChooseSketchHoles));}
    partial void OnToolKindChanged(string value){OnPropertyChanged(nameof(IsProfileTool));OnPropertyChanged(nameof(IsFrozenProfile));OnPropertyChanged(nameof(ViewportDrawingHint));RefreshSketchProfiles();}
    partial void OnSelectedSketchProfileChanged(SketchProfileChoice? value){RefreshHoleChoices();InvalidatePreview();OnPropertyChanged(nameof(CanChooseSketchHoles));}
    partial void OnSelectedTargetPartChanged(DefinitionId? value){if(!refreshingTargets)RefreshSketchProfiles();}
    private void RefreshSketchProfiles()
    {
        var source=SelectedSketchProfile?.Source;SketchProfiles.Clear();
        foreach(var sketch in Session.Snapshot.Sketches.Values.Where(s=>s.PartId==SelectedTargetPart).OrderBy(s=>s.Name))
        {
            foreach(var (loop,index) in SketchLoops.Find(sketch).Select((loop,index)=>(loop,index)))
            {
                var boundary=SketchProfileReference.Create(sketch,loop);
                SketchProfiles.Add(new($"{sketch.Name} · {index+1} ({loop.Length})",boundary));
                if(ToolKind=="Extrude")AddHoleChoices(sketch,boundary,$"{sketch.Name} · {index+1} ({loop.Length})");
            }
            if(ToolKind=="Extrude")foreach(var (circle,index) in sketch.Circles.Where(c=>!c.IsConstruction).OrderBy(c=>c.Id.Value).Select((circle,index)=>(circle,index)))
            {
                var boundary=SketchProfileReference.CreateCircle(sketch,circle.Id);
                var label=$"{sketch.Name} · ○ {index+1} (R {circle.Radius:G6})";
                SketchProfiles.Add(new(label,boundary));
                AddHoleChoices(sketch,boundary,label);
            }
            if(ToolKind=="Extrude")foreach(var (arc,index) in sketch.Arcs.Where(a=>!a.IsConstruction).OrderBy(a=>a.Id.Value).Select((arc,index)=>(arc,index)))
                SketchProfiles.Add(new($"{sketch.Name} · ⌒ {index+1}",SketchProfileReference.CreateArcSegment(sketch,arc.Id)));
            if(ToolKind=="Extrude")foreach(var (bezier,index) in sketch.Beziers.Where(b=>!b.IsConstruction).OrderBy(b=>b.Id.Value).Select((bezier,index)=>(bezier,index)))
                SketchProfiles.Add(new($"{sketch.Name} · ∿ {index+1}",SketchProfileReference.CreateBezierSegment(sketch,bezier.Id)));
            if(ToolKind=="Extrude")foreach(var (spline,index) in sketch.Splines.Where(s=>!s.IsConstruction).OrderBy(s=>s.Id.Value).Select((spline,index)=>(spline,index)))
                SketchProfiles.Add(new($"{sketch.Name} · S {index+1} ({spline.Controls.Length})",SketchProfileReference.CreateSplineSegment(sketch,spline.Id)));
            if(ToolKind=="Extrude")foreach(var (loop,index) in SketchMixedLoops.Find(sketch).Select((loop,index)=>(loop,index)))
                SketchProfiles.Add(new($"{sketch.Name} · "+(Strings.ResourceManager.GetString("MixedCurveLoop",Strings.Culture)??"Mixed curve loop")+
                    $" {index+1} ({loop.Length})",SketchProfileReference.CreateMixed(sketch,loop)));
        }
        SelectedSketchProfile=source is null?null:SketchProfiles.FirstOrDefault(p=>SameProfileSource(p.Source,source));
        OnPropertyChanged(nameof(CanChooseSketchHoles));
    }
    private void RefreshHoleChoices()
    {
        SketchHoleChoices.Clear();SketchPolygonHoleChoices.Clear();SketchMixedHoleChoices.Clear();
        SketchIslandCircleChoices.Clear();SketchIslandPolygonChoices.Clear();SketchIslandMixedChoices.Clear();
        if(ToolKind!="Extrude"||SelectedSketchProfile?.Source is not {} boundary||boundary.ArcId is not null||boundary.BezierId is not null||boundary.SplineId is not null||
            !Session.Snapshot.Sketches.TryGetValue(boundary.SketchId,out var sketch))return;
        var selected=boundary.HoleCircleIds.IsDefaultOrEmpty?ImmutableArray<SketchEntityId>.Empty:boundary.HoleCircleIds;
        foreach(var (circle,index) in sketch.Circles.Where(c=>!c.IsConstruction&&c.Id!=boundary.CircleId)
                     .OrderBy(c=>c.Id.Value).Select((circle,index)=>(circle,index)))
        {
            var candidate=boundary with{HoleCircleIds=[circle.Id]};
            try{candidate.Resolve(Session.Snapshot,sketch.PartId,new ExtrudeRecipe(new([]),1,sketch.Plane));}
            catch(CadValidationException){continue;}
            var choice=new SketchHoleChoice(circle.Id,$"◉ {index+1} · R {circle.Radius:G6}",selected.Contains(circle.Id));
            choice.PropertyChanged+=(_,_)=>{RefreshIslandChoices();InvalidatePreview();};SketchHoleChoices.Add(choice);
        }
        foreach(var (loop,index) in SketchLoops.Find(sketch).Select((loop,index)=>(loop,index)))
        {
            if(loop.SequenceEqual(boundary.Lines)||loop.Intersect(boundary.Lines).Any())continue;
            var candidate=boundary with{HoleCircleIds=[],PolygonHoleLines=[loop]};
            try{candidate.Resolve(Session.Snapshot,sketch.PartId,new ExtrudeRecipe(new([]),1,sketch.Plane));}
            catch(CadValidationException){continue;}
            bool polygonSelected=boundary.PolygonHoleLines.Any(h=>h.Length==loop.Length&&h.ToHashSet().SetEquals(loop));
            var choice=new SketchPolygonHoleChoice(loop,$"◇ {index+1} ({loop.Length})",polygonSelected);
            choice.PropertyChanged+=(_,_)=>{RefreshIslandChoices();InvalidatePreview();};SketchPolygonHoleChoices.Add(choice);
        }
        foreach(var (loop,index) in SketchMixedLoops.Find(sketch).Select((loop,index)=>(loop,index)))
        {
            if(loop.Intersect(boundary.MixedBoundaryIds).Any()||loop.Intersect(boundary.Lines).Any())continue;
            var candidate=boundary with{HoleCircleIds=[],PolygonHoleLines=[],MixedHoleIds=[loop]};
            try{candidate.Resolve(Session.Snapshot,sketch.PartId,new ExtrudeRecipe(new([]),1,sketch.Plane));}
            catch(CadValidationException){continue;}
            bool selectedMixed=boundary.MixedHoleIds.Any(h=>h.Length==loop.Length&&h.ToHashSet().SetEquals(loop));
            var choice=new SketchPolygonHoleChoice(loop,$"⌒ {index+1} ({loop.Length})",selectedMixed);
            choice.PropertyChanged+=(_,_)=>{RefreshIslandChoices();InvalidatePreview();};SketchMixedHoleChoices.Add(choice);
        }
        RefreshIslandChoices();
    }
    private void RefreshIslandChoices()
    {
        SketchIslandCircleChoices.Clear();SketchIslandPolygonChoices.Clear();SketchIslandMixedChoices.Clear();
        if(SelectedSketchProfile?.Source is not {} boundary||
            !Session.Snapshot.Sketches.TryGetValue(boundary.SketchId,out var sketch))return;
        var parent=boundary with
        {
            HoleCircleIds=SketchHoleChoices.Where(c=>c.IsSelected).Select(c=>c.Id).ToImmutableArray(),
            PolygonHoleLines=SketchPolygonHoleChoices.Where(c=>c.IsSelected).Select(c=>c.Lines).ToImmutableArray(),
            MixedHoleIds=SketchMixedHoleChoices.Where(c=>c.IsSelected).Select(c=>c.Lines).ToImmutableArray(),
            IslandCircleIds=[],IslandPolygonLines=[],IslandMixedIds=[]
        };
        if(parent.HoleCircleIds.IsEmpty&&parent.PolygonHoleLines.IsEmpty&&parent.MixedHoleIds.IsEmpty)return;
        bool Valid(SketchProfileReference source)
        {
            try{source.Resolve(Session.Snapshot,sketch.PartId,new ExtrudeRecipe(new([]),1,sketch.Plane));return true;}
            catch(CadValidationException){return false;}
        }
        foreach(var circle in sketch.Circles.Where(c=>!c.IsConstruction).OrderBy(c=>c.Id.Value))
        {
            if(!Valid(parent with{IslandCircleIds=[circle.Id]}))continue;
            var choice=new SketchHoleChoice(circle.Id,$"○ R {circle.Radius:G6}",boundary.IslandCircleIds.Contains(circle.Id));
            choice.PropertyChanged+=(_,_)=>InvalidatePreview();SketchIslandCircleChoices.Add(choice);
        }
        foreach(var loop in SketchLoops.Find(sketch))
        {
            if(!Valid(parent with{IslandPolygonLines=[loop]}))continue;
            var choice=new SketchPolygonHoleChoice(loop,$"◇ ({loop.Length})",
                boundary.IslandPolygonLines.Any(ids=>ids.Length==loop.Length&&ids.ToHashSet().SetEquals(loop)));
            choice.PropertyChanged+=(_,_)=>InvalidatePreview();SketchIslandPolygonChoices.Add(choice);
        }
        foreach(var loop in SketchMixedLoops.Find(sketch))
        {
            if(!Valid(parent with{IslandMixedIds=[loop]}))continue;
            var choice=new SketchPolygonHoleChoice(loop,$"⌒ ({loop.Length})",
                boundary.IslandMixedIds.Any(ids=>ids.Length==loop.Length&&ids.ToHashSet().SetEquals(loop)));
            choice.PropertyChanged+=(_,_)=>InvalidatePreview();SketchIslandMixedChoices.Add(choice);
        }
    }
    private static bool SameProfileSource(SketchProfileReference a,SketchProfileReference b)=>
        a.SketchId==b.SketchId&&a.CircleId==b.CircleId&&a.ArcId==b.ArcId&&a.BezierId==b.BezierId&&a.SplineId==b.SplineId&&a.Lines.SequenceEqual(b.Lines)&&
        a.MixedBoundaryIds.SequenceEqual(b.MixedBoundaryIds)&&
        (a.HoleCircleIds.IsDefaultOrEmpty?ImmutableArray<SketchEntityId>.Empty:a.HoleCircleIds)
            .SequenceEqual(b.HoleCircleIds.IsDefaultOrEmpty?ImmutableArray<SketchEntityId>.Empty:b.HoleCircleIds)&&
        a.PolygonHoleLines.Length==b.PolygonHoleLines.Length&&
        a.PolygonHoleLines.Zip(b.PolygonHoleLines).All(pair=>pair.First.SequenceEqual(pair.Second))&&
        a.MixedHoleIds.Length==b.MixedHoleIds.Length&&
        a.MixedHoleIds.Zip(b.MixedHoleIds).All(pair=>pair.First.SequenceEqual(pair.Second))&&
        a.IslandCircleIds.SequenceEqual(b.IslandCircleIds)&&
        a.IslandPolygonLines.Length==b.IslandPolygonLines.Length&&
        a.IslandPolygonLines.Zip(b.IslandPolygonLines).All(pair=>pair.First.SequenceEqual(pair.Second))&&
        a.IslandMixedIds.Length==b.IslandMixedIds.Length&&
        a.IslandMixedIds.Zip(b.IslandMixedIds).All(pair=>pair.First.SequenceEqual(pair.Second));
    private void AddHoleChoices(CadSketch sketch,SketchProfileReference boundary,string label)
    {
        var circles=sketch.Circles.Where(c=>!c.IsConstruction&&c.Id!=boundary.CircleId).OrderBy(c=>c.Id.Value).ToArray();
        if(circles.Length==0)return;
        var valid=new List<SketchEntityId>();
        foreach(var circle in circles)
        {
            var candidate=boundary with{HoleCircleIds=[circle.Id]};
            try
            {
                candidate.Resolve(Session.Snapshot,sketch.PartId,new ExtrudeRecipe(new([]),1,sketch.Plane));
                SketchProfiles.Add(new($"{label} · ◉ {Array.IndexOf(circles,circle)+1}",candidate));valid.Add(circle.Id);
            }
            catch(CadValidationException) { }
        }
        // Explicit multi-hole choices stay bounded; larger sketches can still choose each hole separately.
        if(valid.Count is <2 or >8)return;
        for(int mask=3;mask<(1<<valid.Count);mask++)
        {
            if(System.Numerics.BitOperations.PopCount((uint)mask)<2)continue;
            var holes=Enumerable.Range(0,valid.Count).Where(i=>(mask&(1<<i))!=0).Select(i=>valid[i]).ToImmutableArray();
            var candidate=boundary with{HoleCircleIds=holes};
            try
            {
                candidate.Resolve(Session.Snapshot,sketch.PartId,new ExtrudeRecipe(new([]),1,sketch.Plane));
                var indices=string.Join(",",Enumerable.Range(0,valid.Count).Where(i=>(mask&(1<<i))!=0)
                    .Select(i=>Array.FindIndex(circles,c=>c.Id==valid[i])+1));
                SketchProfiles.Add(new($"{label} · ◉ [{indices}]",candidate));
            }
            catch(CadValidationException) { }
        }
    }
    partial void OnEditingFeatureChanged(FeatureId? value){OnPropertyChanged(nameof(IsCreating));OnPropertyChanged(nameof(CanChooseSketchProfile));OnPropertyChanged(nameof(CanChooseSketchHoles));OnPropertyChanged(nameof(CanChangeSketchAssociation));}
    [RelayCommand] private void ClearCreationMaterial()=>SelectedCreationMaterial=null;
    private void RefreshTargets()
    {
        refreshingTargets=true;
        try
        {
            var part=SelectedTargetPart;var layer=SelectedCreationLayer;var material=SelectedCreationMaterial;var snapshot=Session.Snapshot;
            TargetParts.Clear();foreach(var p in snapshot.Definitions.Values.OfType<PartDefinition>().OrderBy(p=>p.Name).ThenBy(p=>p.Id.Value))TargetParts.Add(p);
            CreationLayers.Clear();foreach(var l in snapshot.Layers.Values.OrderBy(l=>l.Name))CreationLayers.Add(l);
            CreationMaterials.Clear();foreach(var m in snapshot.Materials.Values.OrderBy(m=>m.Name))CreationMaterials.Add(m);
            SelectedTargetPart=TargetParts.FirstOrDefault(p=>p.Id==part)?.Id??TargetParts.FirstOrDefault()?.Id;
            SelectedCreationLayer=CreationLayers.FirstOrDefault(l=>l.Id==layer)?.Id??CreationLayers.OrderBy(l=>l.IsLocked).ThenBy(l=>l.Id.Value).FirstOrDefault()?.Id;
            SelectedCreationMaterial=CreationMaterials.FirstOrDefault(m=>m.Id==material)?.Id;
            OnPropertyChanged(nameof(TargetPartHint));
            RefreshSketchProfiles();
            if(SelectedSketchId is {} sketch&&!snapshot.Sketches.ContainsKey(sketch))SelectedSketchId=null;
        }
        finally{refreshingTargets=false;}
    }
    [RelayCommand] private void AddProfilePoint()=>ProfilePoints.Add(new(10,10));
    [RelayCommand] private void RemoveProfilePoint(){if(ProfilePoints.Count>3)ProfilePoints.RemoveAt(ProfilePoints.Count-1);}
    private bool CanPreview()=>!IsClosingRequested&&!IsWorking&&!IsReadOnly;
    private bool CanConfirm()=>CanPreview()&&HasPreview;
    [RelayCommand(CanExecute=nameof(CanPreview))] private async Task PreviewAsync()=>await PreparePreviewAsync(ToolCommand);
    [RelayCommand(CanExecute=nameof(CanPreview))] private async Task BooleanPreviewAsync(string operation)
    {
        await PreparePreviewAsync(()=>new BooleanCommand(Enum.Parse<BooleanOperation>(operation),Selection.Items.Select(x=>x.BodyId)));
    }
    private async Task PreparePreviewAsync(Func<ICadDocumentCommand> command)
    {
        if(IsClosingRequested)return;
        InvalidatePreview();long request=previewSequence;var cancel=new CancellationTokenSource();previewCancellation=cancel;
        IsWorking=true;ToolStatus=Strings.CalculatingPreview;
        try
        {
            if(IsReadOnly)throw new NotSupportedException(Strings.UnsupportedDocumentReadOnly);
            using var capture=Session.Capture();long generation=Session.Generation;
            var candidate=await command().PrepareAsync(new(capture.Snapshot,generation,Session.Assets,kernel),cancel.Token);
            if(cancel.IsCancellationRequested||request!=previewSequence||Session.IsClosing||generation!=Session.Generation){candidate.Dispose();return;}
            try
            {
                candidate.Snapshot.Validate();var resultScene=CadScene.FromDocument(candidate.Snapshot);
                PreviewScene=resultScene with{Items=resultScene.Items.Select(item=>
                    !capture.Snapshot.Bodies.TryGetValue(item.BodyId,out var old)||old.Geometry.Revision!=item.Geometry.Revision
                        ?item with{Argb=0xFF32CCA0,PreserveSourceStyles=false}:item).ToImmutableArray()};
            }
            catch{candidate.Dispose();throw;}
            prepared=candidate;preparedGeneration=generation;
            PreviewGeometry=candidate.Snapshot.Bodies.Values.FirstOrDefault(b=>!capture.Snapshot.Bodies.TryGetValue(b.Id,out var old)||old.Geometry.Revision!=b.Geometry.Revision)?.Geometry;
            HasPreview=true;ToolStatus=PreviewGeometry is null?Strings.EmptyPreviewStatus:Strings.PreviewReadyStatus;
            PreviewChanged?.Invoke(this,EventArgs.Empty);
        }
        catch(OperationCanceledException){if(request==previewSequence)ToolStatus=Strings.CanceledStatus;}
        catch(Exception ex){if(request==previewSequence)Report(ex);}
        finally{if(request==previewSequence)IsWorking=false;cancel.Dispose();if(ReferenceEquals(previewCancellation,cancel))previewCancellation=null;}
    }
    [RelayCommand(CanExecute=nameof(CanConfirm))] private async Task ConfirmAsync()
    {
        if(prepared is null||!HasPreview||IsClosingRequested)return;
        var edit=prepared;long expected=preparedGeneration;IsWorking=true;
        try
        {
            await Session.ExecuteAsync(new PreparedCommand(edit.Snapshot,expected));
            IsViewportConstructing=false;InvalidatePreview();ToolStatus=Strings.CommittedStatus;FitView();
        }
        catch(Exception ex){Report(ex);InvalidatePreview();}
        finally{IsWorking=false;}
    }
    [RelayCommand] private void Cancel()=>CancelViewportConstruction();
    public void InvalidatePreview()
    {
        previewSequence++;previewCancellation?.Cancel();prepared?.Dispose();prepared=null;
        PreviewGeometry=null;PreviewScene=null;HasPreview=false;IsWorking=false;PreviewChanged?.Invoke(this,EventArgs.Empty);
    }
    public void SetSketchPreview(DocumentSnapshot? candidate)
    {
        PreviewScene=candidate is null?null:CadScene.FromDocument(candidate);
        PreviewChanged?.Invoke(this,EventArgs.Empty);
    }
    public async Task StopToolsAsync()
    {
        InvalidatePreview();
        var tasks=new[]{PreviewCommand.ExecutionTask,BooleanPreviewCommand.ExecutionTask,ConfirmCommand.ExecutionTask}.Where(t=>t is not null).Cast<Task>().ToArray();
        try{await Task.WhenAll(tasks);}catch(OperationCanceledException){}
    }
    public void Detach()
    {
        if(IsDetached)return;IsDetached=true;
        // Release native views before the session leases, without waiting for a deferred WPF Unloaded event.
        foreach(var observer in Detaching?.GetInvocationList()??[])
            try{((EventHandler)observer)(this,EventArgs.Empty);}catch(Exception ex){Report(ex);}
        Detaching=null;
        InvalidatePreview();Session.Changed-=OnDocumentChanged;Session.StatusChanged-=OnSessionStatus;Session.ObserverFailed-=OnObserverFailed;
        PropertyChanged-=OnPropertiesChanged;foreach(var p in ProfilePoints)p.PropertyChanged-=OnProfilePointChanged;
        Placement.Dispose();AssemblyConstraints.Dispose();
    }
    public void Report(Exception error){ToolStatus=error.Message;log.Add(error.Message,CadMessageLevel.Error,"Document");}
    private void OnObserverFailed(object? sender,Exception error)=>Report(error);
    private void OnDocumentChanged(object? sender,DocumentChangeSet change)
    {
        var next=Session.Snapshot;
        bool planeChanged=observedSnapshot.Settings.WorkPlane!=next.Settings.WorkPlane;
        bool viewOnly=(observedSnapshot with {StateId=next.StateId,Settings=next.Settings})==next &&
                      (observedSnapshot.Settings with {Grid=next.Settings.Grid,Origin=next.Settings.Origin,WorkPlane=next.Settings.WorkPlane,
                          BackgroundTopArgb=next.Settings.BackgroundTopArgb,
                          BackgroundBottomArgb=next.Settings.BackgroundBottomArgb})==next.Settings;
        observedSnapshot=next;
        if(!viewOnly)
        {
            IsViewportConstructing=false;InvalidatePreview();Selection.Reconcile(next);RefreshTargets();
            SceneChanged?.Invoke(this,EventArgs.Empty);
        }
        UpdateTitle();
        OnPropertyChanged(nameof(GridVisible));OnPropertyChanged(nameof(GridSpacingMm));OnPropertyChanged(nameof(SnapToGrid));
        OnPropertyChanged(nameof(BackgroundTopArgb));OnPropertyChanged(nameof(BackgroundBottomArgb));
        OnPropertyChanged(nameof(OriginSettings));
        OnPropertyChanged(nameof(WorkPlaneSettings));
        if(planeChanged&&IsViewportConstructing)
            CancelViewportConstruction();
        ViewportSettingsChanged?.Invoke(this,EventArgs.Empty);
    }
    private void OnSessionStatus(object? sender,EventArgs e){UpdateTitle();OnPropertyChanged(nameof(IsReadOnly));}
    private void UpdateTitle(){Title=Session.Snapshot.Name+(Session.IsDirty?" *":"");IsModified=Session.IsDirty;}
    private void OnProfilePointChanged(object? sender,PropertyChangedEventArgs e)=>InvalidatePreview();
    private void OnPropertiesChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(e.PropertyName==nameof(IsActive)&&IsActive)Activated?.Invoke(this,EventArgs.Empty);
        if(e.PropertyName is nameof(ToolKind) or nameof(ObjectName) or nameof(SizeX) or nameof(SizeY) or nameof(SizeZ) or nameof(PositionX) or nameof(PositionY) or nameof(PositionZ) or nameof(AngleDegrees))
            InvalidatePreview();
        if(!refreshingTargets&&e.PropertyName is nameof(SelectedTargetPart) or nameof(SelectedCreationLayer) or nameof(SelectedCreationMaterial))InvalidatePreview();
    }
    private sealed class PreparedCommand(DocumentSnapshot snapshot,long generation) : ICadDocumentCommand
    {
        public string Name=>Strings.Modeling;
        public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();if(context.Generation!=generation)throw new StaleDocumentException();
            return Task.FromResult(new PreparedDocumentEdit(snapshot));
        }
    }
}
public sealed record SketchProfileChoice(string Label,SketchProfileReference Source);
public partial class SketchHoleChoice(SketchEntityId id,string label,bool selected) : ObservableObject
{
    public SketchEntityId Id {get;}=id;
    public string Label {get;}=label;
    [ObservableProperty] private bool isSelected=selected;
}
public partial class SketchPolygonHoleChoice(ImmutableArray<SketchEntityId> lines,string label,bool selected) : ObservableObject
{
    public ImmutableArray<SketchEntityId> Lines {get;}=lines;
    public string Label {get;}=label;
    [ObservableProperty] private bool isSelected=selected;
}
public partial class ProfilePointViewModel(double x,double y) : ObservableObject
{
    [ObservableProperty] private double x=x;
    [ObservableProperty] private double y=y;
}
