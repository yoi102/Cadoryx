using System.Collections.Immutable;
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

public enum GuidedEditorKind { Sweep, Loft }
public interface IGuidedEditorHost {Task ShowAsync(GuidedEditorViewModel editor);}
public sealed record GuidedChoice<T>(T Value,string Label);
public sealed record GuidedProfileChoice(DefinitionId Part,AuthoringSection Section,string Label);
public sealed record GuidedCurveChoice(SketchEntityId Entity,string Label);

public partial class GuidedFrameViewModel:ObservableObject
{
    private bool loading;
    private RigidTransform3d? original;
    [ObservableProperty] private double x,y,z,rx,ry,rz;
    public event EventHandler? Changed;
    public RigidTransform3d Value=>original??new(new(X,Y,Z),Quaterniond.FromEulerDegrees(Rx,Ry,Rz));
    public void Load(RigidTransform3d value)
    {
        loading=true;try{X=value.Translation.X;Y=value.Translation.Y;Z=value.Translation.Z;var angles=value.Rotation.ToEulerDegrees();Rx=angles.X;Ry=angles.Y;Rz=angles.Z;original=value;}
        finally{loading=false;}
    }
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {base.OnPropertyChanged(e);if(!loading){original=null;Changed?.Invoke(this,EventArgs.Empty);}}
}
public partial class GuidedPathEdgeViewModel:ObservableObject
{
    public SketchEntityId? Entity {get;}
    public SketchBoundaryCurve Curve {get;}
    public string Label {get;}
    [ObservableProperty] private bool reverse;
    public GuidedPathEdgeViewModel(SketchEntityId? entity,SketchBoundaryCurve curve,string label,bool reverse=false)
    {Entity=entity;Curve=curve;Label=label;this.reverse=reverse;}
}
public sealed class GuidedLoftStationViewModel:ObservableObject
{
    public GuidedFrameViewModel Frame {get;}=new();
    internal Dictionary<Guid,AuthoringSection> Assignments {get;}=[];
    public string Label=>string.Format(Strings.GuidedStationFormat,Frame.X,Frame.Y,Frame.Z);
    public bool IsLinked=>Assignments.Values.Any(s=>s.Source is not null);
    public bool CanSetFrame=>!IsLinked;
    public GuidedLoftStationViewModel(RigidTransform3d frame){Frame.Load(frame);Frame.Changed+=(_,_)=>Changed();}
    internal void Changed(){OnPropertyChanged(nameof(Label));OnPropertyChanged(nameof(IsLinked));OnPropertyChanged(nameof(CanSetFrame));}
}
public sealed record GuidedLoftChannelViewModel(Guid Id,Guid? Parent,string Label);

/// <summary>One captured candidate; selection order and directed path choices are authored explicitly.</summary>
public partial class GuidedEditorViewModel:ObservableObject,IAsyncDisposable
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
    private SketchId? pathSource;
    public GuidedEditorKind Kind {get;}
    public bool IsSweep=>Kind==GuidedEditorKind.Sweep;
    public bool IsLoft=>!IsSweep;
    public bool IsNew=>editing is null;
    public string Title=>IsSweep?Strings.GuidedSweep:Strings.GuidedLoft;
    public bool CanEdit=>!disposed&&!IsWorking&&!IsStale;
    public bool CanConfirm=>CanEdit&&prepared is not null;
    public bool CanSetSectionFrame=>SelectedSweepProfile?.Section.Source is null;
    public bool CanSetPathFrame=>pathSource is null;
    public string AssignmentDescription=>SelectedStation is {} station&&SelectedChannel is {} channel&&station.Assignments.TryGetValue(channel.Id,out var section)?
        section.Source is {} source?baseline.Snapshot.Sketches[source.SketchId].Name:Strings.GuidedFrozen:Strings.GuidedUnassigned;
    public IReadOnlyList<GuidedChoice<DefinitionId>> Parts {get;}
    public ObservableCollection<GuidedProfileChoice> Profiles {get;}=[];
    public IReadOnlyList<GuidedChoice<SketchId>> PathSketches {get;}
    public ObservableCollection<GuidedCurveChoice> AvailableEdges {get;}=[];
    public ObservableCollection<GuidedPathEdgeViewModel> PathEdges {get;}=[];
    public ObservableCollection<GuidedLoftStationViewModel> Stations {get;}=[];
    public ObservableCollection<GuidedLoftChannelViewModel> Channels {get;}=[];
    public IReadOnlyList<GuidedChoice<GuidedOutput>> Outputs {get;}=[new(GuidedOutput.Solid,Strings.GuidedSolid),new(GuidedOutput.Sheet,Strings.GuidedSheet)];
    public IReadOnlyList<GuidedChoice<SweepCorner>> Corners {get;}=[new(SweepCorner.Transformed,Strings.GuidedTransformed),new(SweepCorner.RightCorner,Strings.GuidedRightCorner),new(SweepCorner.RoundCorner,Strings.GuidedRoundCorner)];
    public GuidedFrameViewModel SectionFrame {get;}=new();
    public GuidedFrameViewModel PathFrame {get;}=new();
    [ObservableProperty] private GuidedChoice<DefinitionId>? selectedPart;
    [ObservableProperty] private GuidedProfileChoice? selectedSweepProfile,selectedAssignmentProfile;
    [ObservableProperty] private GuidedChoice<SketchId>? selectedPathSketch;
    [ObservableProperty] private GuidedCurveChoice? selectedAvailableEdge;
    [ObservableProperty] private GuidedPathEdgeViewModel? selectedPathEdge;
    [ObservableProperty] private GuidedLoftStationViewModel? selectedStation;
    [ObservableProperty] private GuidedLoftChannelViewModel? selectedChannel;
    [ObservableProperty] private string objectName="",status=Strings.GuidedHint;
    [ObservableProperty] private GuidedOutput output;
    [ObservableProperty] private SweepCorner corner;
    [ObservableProperty] private bool ruled,isWorking,isStale;

    public GuidedEditorViewModel(CadDocumentViewModel document,IGeometryKernel kernel,GuidedEditorKind kind,FeatureId? feature=null)
    {
        this.document=document;this.kernel=kernel;Kind=kind;editing=feature;baseline=document.Session.Capture();generation=document.Session.Generation;
        loading=true;
        try
        {
            var doc=baseline.Snapshot;var existing=feature is {} id?doc.Features[id]:null;
            if(existing is not null&&(existing.IsSuppressed||existing.Recipe is not (SweepRecipe or LoftRecipe)||IsSweep!=(existing.Recipe is SweepRecipe)))
                throw new CadValidationException(Strings.UnsupportedFeatureEdit);
            Parts=doc.Definitions.Values.OfType<PartDefinition>().Where(p=>!doc.ExternalParts.ContainsKey(p.Id)).Select(p=>new GuidedChoice<DefinitionId>(p.Id,p.Name)).ToArray();
            SelectedPart=existing is not null?Parts.Single(p=>p.Value==existing.PartId):Parts.FirstOrDefault(p=>p.Value==document.SelectedTargetPart)??(Parts.Count==1?Parts[0]:null);
            PathSketches=doc.Sketches.Values.Where(s=>Parts.Any(p=>p.Value==s.PartId)).Select(s=>new GuidedChoice<SketchId>(s.Id,doc.Definitions[s.PartId].Name+" · "+s.Name)).ToArray();
            foreach(var sketch in doc.Sketches.Values.Where(s=>Parts.Any(p=>p.Value==s.PartId)))
            {
                void Offer(SketchProfileReference source,string suffix)
                {
                    try
                    {
                        var e=(ExtrudeRecipe)source.Resolve(doc,sketch.PartId,new ExtrudeRecipe(new([]),1,sketch.Plane));
                        Profiles.Add(new(sketch.PartId,new(e.Profile,e.Placement){Source=source},doc.Definitions[sketch.PartId].Name+" · "+sketch.Name+" · "+suffix));
                    }
                    catch(CadValidationException){ }
                }
                int n=0;
                foreach(var circle in sketch.Circles.Where(c=>!c.IsConstruction))Offer(new(sketch.Id,sketch.Revision,[],circle.Id),"◉ "+(++n));
                foreach(var loop in SketchLoops.Find(sketch))Offer(SketchProfileReference.Create(sketch,loop),"◇ "+(++n));
                foreach(var loop in SketchMixedLoops.Find(sketch))Offer(new(sketch.Id,sketch.Revision,[]){MixedBoundaryIds=loop},"⌒ "+(++n));
                foreach(var arc in sketch.Arcs.Where(a=>!a.IsConstruction))Offer(SketchProfileReference.CreateArcSegment(sketch,arc.Id),"⌒ "+(++n));
                foreach(var bezier in sketch.Beziers.Where(b=>!b.IsConstruction))Offer(SketchProfileReference.CreateBezierSegment(sketch,bezier.Id),"∿ "+(++n));
                foreach(var spline in sketch.Splines.Where(s=>!s.IsConstruction))Offer(SketchProfileReference.CreateSplineSegment(sketch,spline.Id),"S "+(++n));
            }
            ObjectName=existing?.Name??Title;
            if(existing?.Recipe is SweepRecipe sweep)
            {
                var choice=new GuidedProfileChoice(existing.PartId,sweep.Section,Strings.GuidedExistingSection);Profiles.Add(choice);SelectedSweepProfile=choice;
                SectionFrame.Load(sweep.Section.Placement);PathFrame.Load(sweep.Path.Placement);Output=sweep.Output;Corner=sweep.Corner;
                pathSource=sweep.Path.Source?.Sketch;SelectedPathSketch=PathSketches.FirstOrDefault(p=>p.Value==pathSource);
                for(int i=0;i<sweep.Path.Curves.Length;i++)
                {
                    var selected=sweep.Path.Source?.Edges[i];bool reverse=selected?.Reverse??false;
                    AddPathRow(new(selected?.Entity,reverse?Reverse(sweep.Path.Curves[i]):sweep.Path.Curves[i],Strings.GuidedPath+" "+(i+1),reverse));
                }
                FillAvailableEdges();
            }
            else if(existing?.Recipe is LoftRecipe loft)
            {
                Output=loft.Output;Ruled=loft.Ruled;
                string ChannelLabel(LoftChannel c)=>c.Parent is null?Strings.GuidedOuter:
                    ChannelLabel(loft.Channels.Single(parent=>parent.Id==c.Parent))+" / "+Strings.GuidedChild+" "+(Array.IndexOf(loft.Channels.ToArray(),c)+1);
                foreach(var c in loft.Channels)Channels.Add(new(c.Id,c.Parent,ChannelLabel(c)));
                for(int i=0;i<loft.Stations.Length;i++)
                {
                    var station=new GuidedLoftStationViewModel(loft.Stations[i]);
                    foreach(var c in loft.Channels)station.Assignments[c.Id]=new(c.Profiles[i],loft.Stations[i]){Source=c.Sources.IsEmpty?null:c.Sources[i]};
                    AddStationRow(station);
                }
                SelectedChannel=Channels.Single(c=>c.Parent is null);SelectedStation=Stations[0];
            }
            else if(IsLoft){var root=new GuidedLoftChannelViewModel(Guid.NewGuid(),null,Strings.GuidedOuter);Channels.Add(root);SelectedChannel=root;}
            else
            {
                // The current profile is offered as an explicit choice; it is never automatically selected.
                if(SelectedPart is {} part)
                {
                    try{var seed=document.ExtrusionSeed();seed.Recipe.Profile.Validate();Profiles.Add(new(part.Value,new(seed.Recipe.Profile,seed.Recipe.Placement){Source=seed.Source},Strings.GuidedCurrentSection));}
                    catch(CadValidationException){ }
                }
            }
            SectionFrame.Changed+=OnChildChanged;PathFrame.Changed+=OnChildChanged;
            Stations.CollectionChanged+=(_,_)=>Edited();PathEdges.CollectionChanged+=(_,_)=>Edited();Channels.CollectionChanged+=(_,_)=>Edited();
            document.InvalidatePreview();document.Session.Changed+=OnDocumentChanged;document.Detaching+=OnDetached;
        }
        catch{baseline.Dispose();throw;}
        finally{loading=false;}
    }
    private static SketchBoundaryCurve Reverse(SketchBoundaryCurve c)=>c.Reversed();
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(e.PropertyName is nameof(Status) or nameof(CanEdit) or nameof(CanConfirm) or nameof(CanSetSectionFrame) or nameof(CanSetPathFrame) or nameof(AssignmentDescription))return;
        if(e.PropertyName is nameof(IsWorking) or nameof(IsStale)){Notify();return;}
        Edited();
    }
    partial void OnSelectedSweepProfileChanged(GuidedProfileChoice? value)
    {if(!loading&&value is not null)SectionFrame.Load(value.Section.Placement);}
    partial void OnSelectedPathSketchChanged(GuidedChoice<SketchId>? value)
    {
        if(loading)return;pathSource=value?.Value;foreach(var row in PathEdges)row.PropertyChanged-=OnRowChanged;PathEdges.Clear();
        if(value is not null)PathFrame.Load(baseline.Snapshot.Sketches[value.Value].Plane);FillAvailableEdges();Notify();
    }
    partial void OnSelectedStationChanged(GuidedLoftStationViewModel? value){Notify();}
    partial void OnSelectedChannelChanged(GuidedLoftChannelViewModel? value){Notify();}
    private void FillAvailableEdges()
    {
        AvailableEdges.Clear();SelectedAvailableEdge=null;
        if(pathSource is not {} id)return;var s=baseline.Snapshot.Sketches[id];
        IEnumerable<SketchEntityId> ids=s.Lines.Where(e=>!e.IsConstruction).Select(e=>e.Id).Concat(s.Arcs.Where(e=>!e.IsConstruction).Select(e=>e.Id))
            .Concat(s.Beziers.Where(e=>!e.IsConstruction).Select(e=>e.Id)).Concat(s.Splines.Where(e=>!e.IsConstruction).Select(e=>e.Id))
            .Concat(s.Ellipses.Where(e=>!e.IsConstruction).Select(e=>e.Id)).Concat(s.Freeforms.Where(e=>!e.IsConstruction).Select(e=>e.Id))
            .Concat(s.ExpressionCurves.Where(e=>!e.IsConstruction&&double.Hypot(e.Geometry.At(0).X-e.Geometry.At(1).X,e.Geometry.At(0).Y-e.Geometry.At(1).Y)>1e-7).Select(e=>e.Id));
        var points=s.Points.ToDictionary(p=>p.Id,p=>p.Position);
        foreach(var entity in ids)
        {
            string type;Point2d start,end;
            if(s.Lines.FirstOrDefault(e=>e.Id==entity) is {} line){type=Strings.Line;start=points[line.Start];end=points[line.End];}
            else if(s.Arcs.FirstOrDefault(e=>e.Id==entity) is {} arc){type=Strings.ThreePointArc;start=points[arc.Start];end=points[arc.End];}
            else if(s.Beziers.FirstOrDefault(e=>e.Id==entity) is {} bezier){type=Strings.QuadraticBezier;start=points[bezier.Start];end=points[bezier.End];}
            else if(s.Ellipses.FirstOrDefault(e=>e.Id==entity) is {} ellipse){type=Strings.ExactEllipse;start=ellipse.Geometry.At(0);end=ellipse.Geometry.At(1);}
            else if(s.Freeforms.FirstOrDefault(e=>e.Id==entity) is {} freeform){type=Strings.ExactFreeform;start=freeform.Geometry.At(0);end=freeform.Geometry.At(1);}
            else if(s.ExpressionCurves.FirstOrDefault(e=>e.Id==entity) is {} expression){type=Strings.ExactAssociatedOffset;start=expression.Geometry.At(0);end=expression.Geometry.At(1);}
            else{var spline=s.Splines.Single(e=>e.Id==entity);type=Strings.CubicSpline;start=points[spline.Controls[0]];end=points[spline.Controls[^1]];}
            AvailableEdges.Add(new(entity,$"{type} ({start.X:G4}, {start.Y:G4}) → ({end.X:G4}, {end.Y:G4})"));
        }
    }
    private void AddPathRow(GuidedPathEdgeViewModel row){row.PropertyChanged+=OnRowChanged;PathEdges.Add(row);}
    private void AddStationRow(GuidedLoftStationViewModel row){row.PropertyChanged+=OnRowChanged;Stations.Add(row);}
    private void OnRowChanged(object? sender,PropertyChangedEventArgs e)=>Edited();
    private void OnChildChanged(object? sender,EventArgs e)=>Edited();
    private void Edited(){if(loading)return;Invalidate();Notify();}
    private void Notify()
    {
        foreach(var property in new[]{nameof(CanEdit),nameof(CanConfirm),nameof(CanSetSectionFrame),nameof(CanSetPathFrame),nameof(AssignmentDescription)})OnPropertyChanged(property);
        PreviewCommand.NotifyCanExecuteChanged();ConfirmCommand.NotifyCanExecuteChanged();
        foreach(var command in new IRelayCommand[]{AddEdgeCommand,RemoveEdgeCommand,MoveEdgeUpCommand,MoveEdgeDownCommand,DetachPathCommand,DetachSectionCommand,
            AddStationCommand,RemoveStationCommand,MoveStationUpCommand,MoveStationDownCommand,AddChildCommand,RemoveChannelCommand,AssignProfileCommand,DetachStationCommand})command.NotifyCanExecuteChanged();
    }
    private void Invalidate(){cancellation?.Cancel();prepared?.Dispose();prepared=null;document.SetSketchPreview(null);}
    private void OnDocumentChanged(object? sender,DocumentChangeSet e){if(!committing){IsStale=true;Invalidate();Status=Strings.Stale;}}
    private void OnDetached(object? sender,EventArgs e){IsStale=true;Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    public event EventHandler? CloseRequested;
    private void Edit(Action edit)
    {if(!CanEdit)return;try{edit();Status=Strings.GuidedHint;Edited();}catch(Exception e){Status=string.Format(Strings.ParameterError,e.Message);}}
    [RelayCommand(CanExecute=nameof(CanEdit))] private void AddEdge()=>Edit(()=>
    {
        if(pathSource is not {} id||SelectedAvailableEdge is not {} edge)throw new CadValidationException(Strings.GuidedChoosePath);
        if(PathEdges.Any(e=>e.Entity==edge.Entity))throw new CadValidationException(Strings.GuidedDuplicateEdge);
        var s=baseline.Snapshot.Sketches[id];var curve=new SketchPathReference(id,s.Revision,[new(edge.Entity)]).Resolve(baseline.Snapshot,s.PartId).Curves[0];
        var row=new GuidedPathEdgeViewModel(edge.Entity,curve,edge.Label);AddPathRow(row);SelectedPathEdge=row;
    });
    [RelayCommand(CanExecute=nameof(CanEdit))] private void RemoveEdge()=>Edit(()=>{if(SelectedPathEdge is {} edge){edge.PropertyChanged-=OnRowChanged;PathEdges.Remove(edge);}});
    private void MoveEdge(int delta)=>Edit(()=>{if(SelectedPathEdge is not {} edge)return;int i=PathEdges.IndexOf(edge);if(i+delta>=0&&i+delta<PathEdges.Count)PathEdges.Move(i,i+delta);});
    [RelayCommand(CanExecute=nameof(CanEdit))] private void MoveEdgeUp()=>MoveEdge(-1);
    [RelayCommand(CanExecute=nameof(CanEdit))] private void MoveEdgeDown()=>MoveEdge(1);
    [RelayCommand(CanExecute=nameof(CanEdit))] private void DetachPath()=>Edit(()=>{loading=true;try{pathSource=null;SelectedPathSketch=null;AvailableEdges.Clear();}finally{loading=false;}});
    [RelayCommand(CanExecute=nameof(CanEdit))] private void DetachSection()=>Edit(()=>
    {
        if(SelectedSweepProfile is not {} profile)return;
        var choice=profile with{Section=profile.Section with{Placement=SectionFrame.Value,Source=null},Label=Strings.GuidedFrozen};Profiles.Add(choice);SelectedSweepProfile=choice;
    });
    [RelayCommand(CanExecute=nameof(CanEdit))] private void AddStation()=>Edit(()=>{var row=new GuidedLoftStationViewModel(RigidTransform3d.Identity);AddStationRow(row);SelectedStation=row;});
    [RelayCommand(CanExecute=nameof(CanEdit))] private void RemoveStation()=>Edit(()=>{if(SelectedStation is {} row){row.PropertyChanged-=OnRowChanged;Stations.Remove(row);SelectedStation=Stations.FirstOrDefault();}});
    private void MoveStation(int delta)=>Edit(()=>{if(SelectedStation is not {} row)return;int i=Stations.IndexOf(row);if(i+delta>=0&&i+delta<Stations.Count)Stations.Move(i,i+delta);});
    [RelayCommand(CanExecute=nameof(CanEdit))] private void MoveStationUp()=>MoveStation(-1);
    [RelayCommand(CanExecute=nameof(CanEdit))] private void MoveStationDown()=>MoveStation(1);
    [RelayCommand(CanExecute=nameof(CanEdit))] private void AddChild()=>Edit(()=>
    {
        if(SelectedChannel is not {} parent)throw new CadValidationException(Strings.GuidedChooseChannel);
        var id=Guid.NewGuid();var child=new GuidedLoftChannelViewModel(id,parent.Id,parent.Label+" / "+Strings.GuidedChild+" "+(Channels.Count));Channels.Add(child);SelectedChannel=child;
    });
    [RelayCommand(CanExecute=nameof(CanEdit))] private void RemoveChannel()=>Edit(()=>
    {
        if(SelectedChannel is not {} row)return;
        if(row.Parent is null||Channels.Any(c=>c.Parent==row.Id))throw new CadValidationException(Strings.GuidedRemoveLeaf);
        foreach(var station in Stations){station.Assignments.Remove(row.Id);station.Changed();}Channels.Remove(row);SelectedChannel=Channels.Single(c=>c.Parent is null);
    });
    [RelayCommand(CanExecute=nameof(CanEdit))] private void AssignProfile()=>Edit(()=>
    {
        if(SelectedStation is not {} station||SelectedChannel is not {} channel||SelectedAssignmentProfile is not {} profile||SelectedPart?.Value!=profile.Part)
            throw new CadValidationException(Strings.GuidedChooseAssignment);
        AuthoringSection.RequireSingleBoundary(profile.Section.Profile);
        if(channel.Parent is null)station.Frame.Load(profile.Section.Placement);
        else if(profile.Section.Source is not null&&!DatumPlanes.SameFrame(station.Frame.Value,profile.Section.Placement))
            throw new CadValidationException(Strings.GuidedSameStationPlane);
        station.Assignments[channel.Id]=profile.Section;station.Changed();
    });
    [RelayCommand(CanExecute=nameof(CanEdit))] private void DetachStation()=>Edit(()=>
    {
        if(SelectedStation is not {} station)return;foreach(var key in station.Assignments.Keys.ToArray())station.Assignments[key]=station.Assignments[key] with{Placement=station.Frame.Value,Source=null};station.Changed();
    });
    public GeometryRecipe BuildRecipe()
    {
        var part=SelectedPart?.Value??throw new CadValidationException(Strings.SelectTargetPart);GeometryRecipe recipe;
        if(IsSweep)
        {
            var profile=SelectedSweepProfile??throw new CadValidationException(Strings.GuidedChooseSection);
            if(profile.Part!=part)throw new CadValidationException(Strings.HoleSamePart);
            var section=profile.Section with{Placement=SectionFrame.Value};var path=new SweepPath(PathEdges.Select(e=>e.Reverse?Reverse(e.Curve):e.Curve).ToImmutableArray(),PathFrame.Value);
            if(pathSource is {} id)
            {
                var sketch=baseline.Snapshot.Sketches[id];
                path=path with{Source=new(id,sketch.Revision,PathEdges.Select(e=>new SketchPathEdge(e.Entity??throw new CadValidationException(Strings.GuidedChoosePath),e.Reverse)).ToImmutableArray())};
            }
            recipe=new SweepRecipe(section,path,Output,Corner);
        }
        else
        {
            var channels=Channels.Select(c=>new LoftChannel(c.Id,c.Parent,Stations.Select(s=>s.Assignments.GetValueOrDefault(c.Id)?.Profile??throw new CadValidationException(Strings.GuidedUnassigned)).ToImmutableArray())
            {Sources=Stations.Select(s=>s.Assignments[c.Id].Source).ToImmutableArray()}).ToImmutableArray();
            recipe=new LoftRecipe(Stations.Select(s=>s.Frame.Value).ToImmutableArray(),channels,Output,Ruled);
        }
        recipe=GuidedAuthoringSources.Refresh(baseline.Snapshot,part,recipe);recipe.Validate();return recipe;
    }
    private ICadDocumentCommand BuildCommand()
    {var recipe=BuildRecipe();return editing is {} id?new RecomputeCommand(id,recipe):new AddBodyCommand(recipe,ObjectName.Trim(),SelectedPart!.Value);}
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task PreviewAsync()
    {
        Invalidate();if(document.Session.Generation!=generation){IsStale=true;Status=Strings.Stale;return;}IsWorking=true;var cancel=new CancellationTokenSource();cancellation=cancel;
        try
        {
            var candidate=await UpdateAssociatedSections.PrepareCommandAsync(BuildCommand(),new(baseline.Snapshot,generation,document.Session.Assets,kernel),cancel.Token);
            if(cancel.IsCancellationRequested||disposed||IsStale||document.Session.Generation!=generation){candidate.Dispose();return;}
            try{candidate.Snapshot.Validate();document.SetSketchPreview(candidate.Snapshot);}catch{candidate.Dispose();throw;}
            prepared=candidate;Status=Strings.ParameterPreviewReady;
        }
        catch(OperationCanceledException){if(!IsStale)Status=Strings.GuidedHint;}
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
    [RelayCommand] private void Cancel(){Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    public ValueTask DisposeAsync()=>new(disposal??=DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        disposed=true;Invalidate();document.Session.Changed-=OnDocumentChanged;document.Detaching-=OnDetached;
        SectionFrame.Changed-=OnChildChanged;PathFrame.Changed-=OnChildChanged;
        foreach(var row in PathEdges)row.PropertyChanged-=OnRowChanged;foreach(var row in Stations)row.PropertyChanged-=OnRowChanged;
        try{await Task.WhenAll(new[]{PreviewCommand.ExecutionTask,ConfirmCommand.ExecutionTask}.OfType<Task>());}finally{baseline.Dispose();}
    }
}
