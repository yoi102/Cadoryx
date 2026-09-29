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
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public partial class CadDocumentViewModel : ObservableDocument
{
    private readonly IGeometryKernel kernel;
    internal IGeometryKernel GeometryKernel=>kernel;
    private readonly ICadMessageLog log;
    private readonly SemaphoreSlim gridEditQueue=new(1,1);
    private DocumentSnapshot observedSnapshot;
    private CadScene? cachedScene;
    private DocumentScaleReport scaleReport;
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
    public SessionHistoryViewModel History {get;}
    public DocumentReviewViewModel Review {get;}
    public IDocumentStorage? ExternalPartStorage {get;}
    public ICadFileDialogs? ExternalPartFiles {get;}
    public SelectionService Selection {get;}=new();
    private bool drawingDatumPickPending;
    public event EventHandler<TopologyKind?>? DrawingDatumPickRequested;
    public event EventHandler<AssemblyDatumReference>? DrawingDatumPicked;
    public event EventHandler<string>? DrawingDatumPickFailed;
    public bool IsDrawingDatumPickPending=>drawingDatumPickPending;
    public void RequestDrawingDatumPick(TopologyKind kind)
    {
        if(kind is not (TopologyKind.Face or TopologyKind.Edge))
            throw new ArgumentOutOfRangeException(nameof(kind));
        drawingDatumPickPending=true;
        DrawingDatumPickRequested?.Invoke(this,kind);
    }
    public void CancelDrawingDatumPick()
    {
        if(!drawingDatumPickPending)return;
        drawingDatumPickPending=false;
        DrawingDatumPickRequested?.Invoke(this,null);
    }
    public async Task ReceiveDrawingDatumPickAsync(OccurrencePath path,BodyId body,int index,string fingerprint)
    {
        drawingDatumPickPending=false;
        try
        {
            var current=Session.Snapshot;
            var resolver=kernel as IAssemblyDatumResolver??throw new CadValidationException("Analytic datum resolver unavailable.");
            var datum=await resolver.ResolveAssemblyDatumAsync(current,path,body,index,fingerprint,Session.Assets);
            if(Session.Snapshot.StateId!=current.StateId)throw new StaleDocumentException();
            DrawingDatumPicked?.Invoke(this,datum);
        }
        catch(Exception error){DrawingDatumPickFailed?.Invoke(this,error.Message);}
    }
    public void RejectDrawingDatumPick(string message)
    {drawingDatumPickPending=false;DrawingDatumPickFailed?.Invoke(this,message);}
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
    public ObservableCollection<SketchHoleChoice> SketchSplineHoleChoices {get;}=[];
    private SketchProfileReference? autoNestedSource;
    [ObservableProperty] private string autoNestStatus=string.Empty;
    public ObservableCollection<SketchHoleChoice> SketchIslandCircleChoices {get;}=[];
    public ObservableCollection<SketchPolygonHoleChoice> SketchIslandPolygonChoices {get;}=[];
    public ObservableCollection<SketchPolygonHoleChoice> SketchIslandMixedChoices {get;}=[];
    public bool CanChooseSketchProfile=>UseLinkedSketch&&(EditingFeature is null||
        Session.Snapshot.Features[EditingFeature.Value].Recipe is ExtrudeRecipe);
    public bool CanChooseSketchHoles=>CanChooseSketchProfile&&ToolKind=="Extrude"&&
        SelectedSketchProfile is {} selected&&selected.Source.ArcId is null&&selected.Source.BezierId is null;
    public bool CanChangeSketchAssociation=>EditingFeature is null;
    [ObservableProperty] private DefinitionId? selectedTargetPart;
    [ObservableProperty] private LayerId? selectedCreationLayer;
    [ObservableProperty] private MaterialId? selectedCreationMaterial;
    public bool IsCreating=>EditingFeature is null;
    public string TargetPartHint=>TargetParts.Count==0?Strings.FirstPartCreatedAutomatically:Strings.SharedPartEditingHint;
    public string ContentId=>Id;
    public CadScene Scene
    {
        get
        {
            var current=Session.Snapshot;
            return cachedScene is {} cached&&cached.StateId==current.StateId
                ?cached:cachedScene=CadScene.FromDocument(current);
        }
    }
    public CadScene? PreviewScene {get;private set;}
    public DocumentScaleReport ScaleReport=>scaleReport;
    public string ScaleSummary=>string.Format(Strings.ResourceManager.GetString("DocumentScaleFormat",Strings.Culture)
        ??"{0} definitions · {1} instances · {2} unique assets ({3:F1} MiB)",
        scaleReport.Definitions,scaleReport.Occurrences,scaleReport.UniqueAssets,
        scaleReport.AssetBytes/1048576d);
    public void RefreshScaleSummaryLanguage(){OnPropertyChanged(nameof(ScaleSummary));History.Refresh();Review.RefreshLanguage();}
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

    public CadDocumentViewModel(CadDocumentSession session,IGeometryKernel kernel,ICadMessageLog log,
        IDocumentStorage? externalPartStorage=null,ICadFileDialogs? externalPartFiles=null)
    {
        Session=session;History=new(this);observedSnapshot=session.Snapshot;scaleReport=DocumentScaleReport.Measure(session.Snapshot,session.Assets);
        this.kernel=kernel;this.log=log;
        Review=new(this,kernel);
        ExternalPartStorage=externalPartStorage;ExternalPartFiles=externalPartFiles;
        Id="document."+Guid.NewGuid().ToString("N");Context=this;
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
        var feature=Session.Snapshot.Features[id];IsViewportConstructing=false;InvalidatePreview();EditingFeature=id;SelectedTargetPart=feature.PartId;ObjectName=feature.Name;
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
        ToolStatus=feature.Recipe is BoxRecipe or CylinderRecipe or ExtrudeRecipe
            ?Strings.EditingFeatureStatus+" "+(Strings.ResourceManager.GetString("SolidHandleHint")??"Drag the orange handles to change dimensions, then confirm the preview.")
            :Strings.EditingFeatureStatus;
        UseLinkedSketch=feature.SketchSource is not null;
        SelectedSketchProfile=feature.SketchSource is {} source?SketchProfiles.FirstOrDefault(p=>SameProfileSource(p.Source,source)):null;
        if(feature.SketchSource is {} linked&&SelectedSketchProfile is null){var choice=new SketchProfileChoice(Session.Snapshot.Sketches[linked.SketchId].Name,linked);SketchProfiles.Add(choice);SelectedSketchProfile=choice;}
        if(feature.SketchSource is {Regions.IsEmpty:false} nested)
        {autoNestedSource=nested;AutoNestStatus=$"{nested.Regions.Length} nested root regions";}
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
    public GeometryRecipe? SolidHandleRecipe()
    {
        if(IsReadOnly||IsWorking||IsViewportConstructing||EditingFeature is not {} id||
            !Session.Snapshot.Features.TryGetValue(id,out var feature)||feature.IsStale||
            !Session.Snapshot.Bodies.ContainsKey(feature.OutputBodyId))return null;
        if(feature.Recipe is not (BoxRecipe or CylinderRecipe or ExtrudeRecipe or RevolveRecipe)||
            feature.Recipe is BoxRecipe&&ToolKind!="Box"||
            feature.Recipe is CylinderRecipe&&ToolKind!="Cylinder"||
            feature.Recipe is ExtrudeRecipe&&ToolKind!="Extrude"||
            feature.Recipe is RevolveRecipe&&ToolKind!="Revolve")return null;
        return Recipe();
    }
    public void SetSolidHandleValue(SolidDimension dimension,double value)
    {
        if(SolidHandleRecipe() is null)throw new CadValidationException(Strings.UnsupportedFeatureEdit);
        if(!double.IsFinite(value)||value<=0||value>1_000_000)throw new CadValidationException("Invalid handle dimension.");
        switch(dimension)
        {
            case SolidDimension.BoxX when ToolKind=="Box":SizeX=value;break;
            case SolidDimension.BoxY when ToolKind=="Box":SizeY=value;break;
            case SolidDimension.BoxZ when ToolKind=="Box":SizeZ=value;break;
            case SolidDimension.CylinderRadius when ToolKind=="Cylinder":SizeX=value;break;
            case SolidDimension.CylinderHeight when ToolKind=="Cylinder":SizeZ=value;break;
            case SolidDimension.ExtrudeDistance when ToolKind=="Extrude":SizeZ=value;break;
            case SolidDimension.RevolveAngle when ToolKind=="Revolve"&&value<=Math.PI*2:AngleDegrees=value*180/Math.PI;break;
            default:throw new CadValidationException(Strings.UnsupportedFeatureEdit);
        }
    }
    private SketchProfileReference? CurrentSketchSource()=>SelectedSketchProfile?.Source is {} source&&UseLinkedSketch?
        (autoNestedSource??(source with{Regions=[],HoleCircleIds=ToolKind=="Extrude"?SketchHoleChoices.Where(c=>c.IsSelected).Select(c=>c.Id).ToImmutableArray():[],
            PolygonHoleLines=ToolKind=="Extrude"?SketchPolygonHoleChoices.Where(c=>c.IsSelected).Select(c=>c.Lines).ToImmutableArray():[],
            MixedHoleIds=ToolKind=="Extrude"?SketchMixedHoleChoices.Where(c=>c.IsSelected).Select(c=>c.Lines).ToImmutableArray():[],
            HoleSplineIds=ToolKind=="Extrude"?SketchSplineHoleChoices.Where(c=>c.IsSelected).Select(c=>c.Id).ToImmutableArray():[],
            IslandCircleIds=ToolKind=="Extrude"?SketchIslandCircleChoices.Where(c=>c.IsSelected).Select(c=>c.Id).ToImmutableArray():[],
            IslandPolygonLines=ToolKind=="Extrude"?SketchIslandPolygonChoices.Where(c=>c.IsSelected).Select(c=>c.Lines).ToImmutableArray():[],
            IslandMixedIds=ToolKind=="Extrude"?SketchIslandMixedChoices.Where(c=>c.IsSelected).Select(c=>c.Lines).ToImmutableArray():[]})):null;
    private ICadDocumentCommand ToolCommand()=>EditingFeature is {} feature?new RecomputeCommand(feature,Recipe(),
            UseLinkedSketch&&ToolKind=="Extrude"?CurrentSketchSource():null):
        new AddBodyCommand(Recipe(),ObjectName,SelectedTargetPart,SelectedCreationLayer,SelectedCreationMaterial,
            UseLinkedSketch&&ToolKind is "Extrude" or "Revolve"?CurrentSketchSource():null);
    partial void OnUseLinkedSketchChanged(bool value){InvalidatePreview();OnPropertyChanged(nameof(IsFrozenProfile));OnPropertyChanged(nameof(ProfileInputHint));OnPropertyChanged(nameof(CanChooseSketchProfile));OnPropertyChanged(nameof(CanChooseSketchHoles));}
    partial void OnToolKindChanged(string value){OnPropertyChanged(nameof(IsProfileTool));OnPropertyChanged(nameof(IsFrozenProfile));OnPropertyChanged(nameof(ViewportDrawingHint));RefreshSketchProfiles();}
    partial void OnSelectedSketchProfileChanged(SketchProfileChoice? value){autoNestedSource=null;AutoNestStatus=string.Empty;RefreshHoleChoices();InvalidatePreview();OnPropertyChanged(nameof(CanChooseSketchHoles));}
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
        if(source is {Regions.IsEmpty:false}&&SelectedSketchProfile is not null)
        {
            autoNestedSource=source;
            AutoNestStatus=string.Format(Strings.ResourceManager.GetString("NestedRegionsCount",Strings.Culture)??"{0} nested sketch regions",
                source.Regions.Length);
        }
        OnPropertyChanged(nameof(CanChooseSketchHoles));
    }
    private void RefreshHoleChoices()
    {
        SketchHoleChoices.Clear();SketchPolygonHoleChoices.Clear();SketchMixedHoleChoices.Clear();SketchSplineHoleChoices.Clear();
        SketchIslandCircleChoices.Clear();SketchIslandPolygonChoices.Clear();SketchIslandMixedChoices.Clear();
        if(ToolKind!="Extrude"||SelectedSketchProfile?.Source is not {} boundary||boundary.ArcId is not null||boundary.BezierId is not null||
            !Session.Snapshot.Sketches.TryGetValue(boundary.SketchId,out var sketch))return;
        var selected=boundary.HoleCircleIds.IsDefaultOrEmpty?ImmutableArray<SketchEntityId>.Empty:boundary.HoleCircleIds;
        foreach(var (circle,index) in sketch.Circles.Where(c=>!c.IsConstruction&&c.Id!=boundary.CircleId)
                     .OrderBy(c=>c.Id.Value).Select((circle,index)=>(circle,index)))
        {
            var candidate=boundary with{HoleCircleIds=[circle.Id]};
            try{candidate.Resolve(Session.Snapshot,sketch.PartId,new ExtrudeRecipe(new([]),1,sketch.Plane));}
            catch(CadValidationException){continue;}
            var choice=new SketchHoleChoice(circle.Id,$"◉ {index+1} · R {circle.Radius:G6}",selected.Contains(circle.Id));
            choice.PropertyChanged+=(_,_)=>{ClearAutoNested();RefreshIslandChoices();InvalidatePreview();};SketchHoleChoices.Add(choice);
        }
        foreach(var (loop,index) in SketchLoops.Find(sketch).Select((loop,index)=>(loop,index)))
        {
            if(loop.SequenceEqual(boundary.Lines)||loop.Intersect(boundary.Lines).Any())continue;
            var candidate=boundary with{HoleCircleIds=[],PolygonHoleLines=[loop]};
            try{candidate.Resolve(Session.Snapshot,sketch.PartId,new ExtrudeRecipe(new([]),1,sketch.Plane));}
            catch(CadValidationException){continue;}
            bool polygonSelected=boundary.PolygonHoleLines.Any(h=>h.Length==loop.Length&&h.ToHashSet().SetEquals(loop));
            var choice=new SketchPolygonHoleChoice(loop,$"◇ {index+1} ({loop.Length})",polygonSelected);
            choice.PropertyChanged+=(_,_)=>{ClearAutoNested();RefreshIslandChoices();InvalidatePreview();};SketchPolygonHoleChoices.Add(choice);
        }
        foreach(var (loop,index) in SketchMixedLoops.Find(sketch).Select((loop,index)=>(loop,index)))
        {
            if(loop.Intersect(boundary.MixedBoundaryIds).Any()||loop.Intersect(boundary.Lines).Any())continue;
            var candidate=boundary with{HoleCircleIds=[],PolygonHoleLines=[],MixedHoleIds=[loop]};
            try{candidate.Resolve(Session.Snapshot,sketch.PartId,new ExtrudeRecipe(new([]),1,sketch.Plane));}
            catch(CadValidationException){continue;}
            bool selectedMixed=boundary.MixedHoleIds.Any(h=>h.Length==loop.Length&&h.ToHashSet().SetEquals(loop));
            var choice=new SketchPolygonHoleChoice(loop,$"⌒ {index+1} ({loop.Length})",selectedMixed);
            choice.PropertyChanged+=(_,_)=>{ClearAutoNested();RefreshIslandChoices();InvalidatePreview();};SketchMixedHoleChoices.Add(choice);
        }
        foreach(var (spline,index) in sketch.Splines.Where(s=>!s.IsConstruction&&s.Id!=boundary.SplineId)
                     .OrderBy(s=>s.Id.Value).Select((spline,index)=>(spline,index)))
        {
            var candidate=boundary with{HoleCircleIds=[],PolygonHoleLines=[],MixedHoleIds=[],HoleSplineIds=[spline.Id]};
            try{candidate.Resolve(Session.Snapshot,sketch.PartId,new ExtrudeRecipe(new([]),1,sketch.Plane));}
            catch(CadValidationException){continue;}
            var choice=new SketchHoleChoice(spline.Id,$"S {index+1} ({spline.Controls.Length})",boundary.HoleSplineIds.Contains(spline.Id));
            choice.PropertyChanged+=(_,_)=>{ClearAutoNested();RefreshIslandChoices();InvalidatePreview();};SketchSplineHoleChoices.Add(choice);
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
            HoleSplineIds=SketchSplineHoleChoices.Where(c=>c.IsSelected).Select(c=>c.Id).ToImmutableArray(),
            IslandCircleIds=[],IslandPolygonLines=[],IslandMixedIds=[]
        };
        if(parent.HoleCircleIds.IsEmpty&&parent.PolygonHoleLines.IsEmpty&&parent.MixedHoleIds.IsEmpty&&parent.HoleSplineIds.IsEmpty)return;
        bool Valid(SketchProfileReference source)
        {
            try{source.Resolve(Session.Snapshot,sketch.PartId,new ExtrudeRecipe(new([]),1,sketch.Plane));return true;}
            catch(CadValidationException){return false;}
        }
        foreach(var circle in sketch.Circles.Where(c=>!c.IsConstruction).OrderBy(c=>c.Id.Value))
        {
            if(!Valid(parent with{IslandCircleIds=[circle.Id]}))continue;
            var choice=new SketchHoleChoice(circle.Id,$"○ R {circle.Radius:G6}",boundary.IslandCircleIds.Contains(circle.Id));
            choice.PropertyChanged+=(_,_)=>{ClearAutoNested();InvalidatePreview();};SketchIslandCircleChoices.Add(choice);
        }
        foreach(var loop in SketchLoops.Find(sketch))
        {
            if(!Valid(parent with{IslandPolygonLines=[loop]}))continue;
            var choice=new SketchPolygonHoleChoice(loop,$"◇ ({loop.Length})",
                boundary.IslandPolygonLines.Any(ids=>ids.Length==loop.Length&&ids.ToHashSet().SetEquals(loop)));
            choice.PropertyChanged+=(_,_)=>{ClearAutoNested();InvalidatePreview();};SketchIslandPolygonChoices.Add(choice);
        }
        foreach(var loop in SketchMixedLoops.Find(sketch))
        {
            if(!Valid(parent with{IslandMixedIds=[loop]}))continue;
            var choice=new SketchPolygonHoleChoice(loop,$"⌒ ({loop.Length})",
                boundary.IslandMixedIds.Any(ids=>ids.Length==loop.Length&&ids.ToHashSet().SetEquals(loop)));
            choice.PropertyChanged+=(_,_)=>{ClearAutoNested();InvalidatePreview();};SketchIslandMixedChoices.Add(choice);
        }
    }
    private void ClearAutoNested(){autoNestedSource=null;AutoNestStatus=string.Empty;}
    [RelayCommand]
    private void AutoNestSketchRegions()
    {
        if(!CanChooseSketchHoles||SelectedSketchProfile?.Source is not {} source||
            !Session.Snapshot.Sketches.TryGetValue(source.SketchId,out var sketch))return;
        try
        {
            autoNestedSource=null;AutoNestStatus=string.Empty;
            var boundary=source with{HoleCircleIds=[],PolygonHoleLines=[],MixedHoleIds=[],HoleSplineIds=[],
                IslandCircleIds=[],IslandPolygonLines=[],IslandMixedIds=[],Regions=[]};
            var outer=((ExtrudeRecipe)boundary.Resolve(Session.Snapshot,sketch.PartId,
                new ExtrudeRecipe(new SketchProfile([]),1,sketch.Plane))).Profile;
            static ImmutableArray<SketchBoundaryCurve> Curves(SketchProfile p)=>p.Circle is {} c?SketchMixedProfile.Circle(c):
                p.Spline is {} s?SketchSplineGeometry.ValidationBoundary(s):
                !p.BoundaryCurves.IsEmpty?p.BoundaryCurves:SketchMixedProfile.Polygon(p.Points);
            var outerCurves=Curves(outer);
            var outerIds=boundary.Lines.Concat(boundary.MixedBoundaryIds).ToHashSet();
            if(boundary.CircleId is {} circleId)outerIds.Add(circleId);
            if(boundary.SplineId is {} splineId)outerIds.Add(splineId);
            var candidates=new List<(SketchRegionReference Ref,ImmutableArray<SketchBoundaryCurve> Curves)>();
            void Add(SketchRegionReference region,SketchProfile shape,IEnumerable<SketchEntityId> ids)
            {
                if(ids.Any(outerIds.Contains))return;
                var curves=Curves(shape);
                if(SketchMixedProfile.Touches(outerCurves,curves))
                    throw new CadValidationException("A sketch region touches or crosses the selected outer boundary.");
                if(SketchMixedProfile.StrictlyContains(outerCurves,curves))candidates.Add((region,curves));
                if(candidates.Count>64)throw new CadValidationException("A nested profile has too many regions.");
            }
            foreach(var circle in sketch.Circles.Where(c=>!c.IsConstruction).OrderBy(c=>c.Id.Value))
                Add(new(CircleId:circle.Id),SketchProfileBuilder.Circle(sketch,circle.Id),[circle.Id]);
            foreach(var loop in SketchLoops.Find(sketch))
                Add(new(PolygonLineIds:loop),SketchProfileBuilder.Polygon(sketch,loop),loop);
            var mixedLoops=SketchMixedLoops.Find(sketch);
            foreach(var loop in mixedLoops)
                Add(new(MixedCurveIds:loop),SketchProfileBuilder.Mixed(sketch,loop),loop);
            var mixedMembers=mixedLoops.SelectMany(loop=>loop).ToHashSet();
            foreach(var spline in sketch.Splines.Where(s=>!s.IsConstruction&&!mixedMembers.Contains(s.Id)).OrderBy(s=>s.Id.Value))
                Add(new(SplineId:spline.Id),SketchProfileBuilder.SplineSegment(sketch,spline.Id),[spline.Id]);
            var parents=new int[candidates.Count];
            for(int i=0;i<candidates.Count;i++)
            {
                var containers=new List<int>();
                for(int j=0;j<candidates.Count;j++)if(i!=j)
                {
                    if(SketchMixedProfile.Touches(candidates[i].Curves,candidates[j].Curves))
                        throw new CadValidationException("Sketch regions touch or cross; select boundaries explicitly.");
                    if(SketchMixedProfile.StrictlyContains(candidates[j].Curves,candidates[i].Curves))containers.Add(j);
                }
                parents[i]=containers.Count==0?-1:containers.Single(j=>containers.All(k=>k==j||
                    SketchMixedProfile.StrictlyContains(candidates[k].Curves,candidates[j].Curves)));
            }
            ImmutableArray<SketchRegionReference> Children(int parent)=>Enumerable.Range(0,candidates.Count)
                .Where(i=>parents[i]==parent).Select(i=>candidates[i].Ref with{Children=Children(i)}).ToImmutableArray();
            var nested=boundary with{Regions=Children(-1)};
            _=nested.Resolve(Session.Snapshot,sketch.PartId,new ExtrudeRecipe(new SketchProfile([]),1,sketch.Plane));
            foreach(var choice in SketchHoleChoices)choice.IsSelected=false;
            foreach(var choice in SketchPolygonHoleChoices)choice.IsSelected=false;
            foreach(var choice in SketchMixedHoleChoices)choice.IsSelected=false;
            foreach(var choice in SketchSplineHoleChoices)choice.IsSelected=false;
            foreach(var choice in SketchIslandCircleChoices)choice.IsSelected=false;
            foreach(var choice in SketchIslandPolygonChoices)choice.IsSelected=false;
            foreach(var choice in SketchIslandMixedChoices)choice.IsSelected=false;
            autoNestedSource=nested;
            AutoNestStatus=string.Format(Strings.ResourceManager.GetString("NestedRegionsCount",Strings.Culture)??"{0} nested sketch regions",candidates.Count);
            InvalidatePreview();
        }
        catch(Exception ex){autoNestedSource=null;AutoNestStatus=ex.Message;InvalidatePreview();}
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
        a.HoleSplineIds.SequenceEqual(b.HoleSplineIds)&&
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
            var candidate=await UpdateAssociatedSections.PrepareCommandAsync(command(),new(capture.Snapshot,generation,Session.Assets,kernel),cancel.Token);
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
    public void SetAssemblyPreview(DocumentSnapshot? candidate)=>SetSketchPreview(candidate);
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
        Placement.Dispose();AssemblyConstraints.Dispose();History.Dispose();Review.Dispose();
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
        cachedScene=null;
        scaleReport=DocumentScaleReport.Measure(next,Session.Assets);
        OnPropertyChanged(nameof(ScaleReport));OnPropertyChanged(nameof(ScaleSummary));
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
