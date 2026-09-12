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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public partial class CadDocumentViewModel : ObservableDocument
{
    private readonly IGeometryKernel kernel;
    private readonly ICadMessageLog log;
    private PreparedDocumentEdit? prepared;
    private long preparedGeneration;
    private long previewSequence;
    private CancellationTokenSource? previewCancellation;
    private bool allowClose;
    private bool refreshingTargets;
    private Quaterniond placementRotation=Quaterniond.Identity;
    public CadDocumentSession Session {get;}
    public SelectionService Selection {get;}=new();
    public InstancePlacementViewModel Placement {get;}
    public ObservableCollection<PartDefinition> TargetParts {get;}=[];
    public ObservableCollection<CadLayer> CreationLayers {get;}=[];
    public ObservableCollection<CadMaterial> CreationMaterials {get;}=[];
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
        Session=session;this.kernel=kernel;this.log=log;Id="document."+Guid.NewGuid().ToString("N");Context=this;
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
        Placement=new(this);RefreshTargets();UpdateTitle();
    }
    public override void OnSelected(){Activated?.Invoke(this,EventArgs.Empty);}
    public override bool OnClose(){if(allowClose)return true;CloseRequested?.Invoke(this,EventArgs.Empty);return false;}
    public void PermitClose()=>allowClose=true;
    public void FitView()=>FitRequested?.Invoke(this,EventArgs.Empty);
    public void SetView(CadProjection projection)=>ProjectionRequested?.Invoke(this,projection);
    public void SetDisplay(CadDisplayMode mode){CurrentDisplayMode=mode;DisplayModeRequested?.Invoke(this,mode);}
    public void StartTool(string kind)
    {
        InvalidatePreview();EditingFeature=null;placementRotation=Quaterniond.Identity;ToolKind=kind;ObjectName=kind;ToolStatus=Strings.ToolStatusHint;
        if(Selection.Items.FirstOrDefault() is {} selection)SelectedTargetPart=Session.Snapshot.Bodies[selection.BodyId].PartId;
        else if(Selection.Occurrence is {} path&&Session.Snapshot.Definitions[OccurrencePlacement.Resolve(Session.Snapshot,path).Slot.DefinitionId] is PartDefinition part)SelectedTargetPart=part.Id;
    }
    public void EditFeature(FeatureId id)
    {
        var feature=Session.Snapshot.Features[id];InvalidatePreview();EditingFeature=id;SelectedTargetPart=feature.PartId;ObjectName=feature.Name;
        switch(feature.Recipe)
        {
            case BoxRecipe b:ToolKind="Box";SizeX=b.X;SizeY=b.Y;SizeZ=b.Z;SetPosition(b.Placement);break;
            case CylinderRecipe c:ToolKind="Cylinder";SizeX=c.Radius;SizeZ=c.Height;SetPosition(c.Placement);break;
            case ExtrudeRecipe e:ToolKind="Extrude";SizeZ=e.Distance;SetPosition(e.Placement);LoadProfile(e.Profile);break;
            case RevolveRecipe r:ToolKind="Revolve";AngleDegrees=r.AngleRadians*180/Math.PI;SetPosition(r.Placement);LoadProfile(r.Profile);break;
            default:EditingFeature=null;ToolStatus=Strings.UnsupportedFeatureEdit;return;
        }
        ToolStatus=Strings.EditingFeatureStatus;
    }
    private void SetPosition(RigidTransform3d p){PositionX=p.Translation.X;PositionY=p.Translation.Y;PositionZ=p.Translation.Z;placementRotation=p.Rotation;}
    private void LoadProfile(SketchProfile profile){ProfilePoints.Clear();foreach(var p in profile.Points)ProfilePoints.Add(new(p.X,p.Y));}
    private GeometryRecipe Recipe()
    {
        var placement=new RigidTransform3d(new(PositionX,PositionY,PositionZ),placementRotation);
        var profile=new SketchProfile(ProfilePoints.Select(p=>new Point2d(p.X,p.Y)).ToImmutableArray());
        return ToolKind switch
        {
            "Box"=>new BoxRecipe(SizeX,SizeY,SizeZ,placement),
            "Cylinder"=>new CylinderRecipe(SizeX,SizeZ,placement),
            "Extrude"=>new ExtrudeRecipe(profile,SizeZ,placement),
            "Revolve"=>new RevolveRecipe(profile,AngleDegrees*Math.PI/180,placement),
            _=>throw new CadValidationException("Choose a supported modeling tool.")
        };
    }
    private ICadDocumentCommand ToolCommand()=>EditingFeature is {} feature?new RecomputeCommand(feature,Recipe()):
        new AddBodyCommand(Recipe(),ObjectName,SelectedTargetPart,SelectedCreationLayer,SelectedCreationMaterial);
    partial void OnEditingFeatureChanged(FeatureId? value)=>OnPropertyChanged(nameof(IsCreating));
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
            InvalidatePreview();ToolStatus=Strings.CommittedStatus;FitView();
        }
        catch(Exception ex){Report(ex);InvalidatePreview();}
        finally{IsWorking=false;}
    }
    [RelayCommand] private void Cancel()=>InvalidatePreview();
    public void InvalidatePreview()
    {
        previewSequence++;previewCancellation?.Cancel();prepared?.Dispose();prepared=null;
        PreviewGeometry=null;PreviewScene=null;HasPreview=false;IsWorking=false;PreviewChanged?.Invoke(this,EventArgs.Empty);
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
        Placement.Dispose();
    }
    public void Report(Exception error){ToolStatus=error.Message;log.Add(error.Message,CadMessageLevel.Error,"Document");}
    private void OnObserverFailed(object? sender,Exception error)=>Report(error);
    private void OnDocumentChanged(object? sender,DocumentChangeSet change){InvalidatePreview();Selection.Reconcile(Session.Snapshot);RefreshTargets();UpdateTitle();SceneChanged?.Invoke(this,EventArgs.Empty);}
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
public partial class ProfilePointViewModel(double x,double y) : ObservableObject
{
    [ObservableProperty] private double x=x;
    [ObservableProperty] private double y=y;
}
