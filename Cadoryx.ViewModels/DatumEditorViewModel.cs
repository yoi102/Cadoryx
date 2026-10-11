using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public interface IDatumEditorHost {Task ShowAsync(DatumEditorViewModel editor);}
public sealed record DatumPartChoice(DefinitionId Id,string Label);
public sealed record DatumSourceChoice(DatumPlaneSource Source,string Label);
public sealed record DatumSketchChoice(SketchId Id,string Label);
public sealed record DatumOccurrenceChoice(OccurrencePath Path,string Label);
public sealed record DatumKindChoice(DatumPlaneKind Kind,string Label);
public sealed record DatumPlaneRow(CadDatumPlane Plane)
{
    public string Name=>Plane.Name;
    public string Kind=>DatumEditorViewModel.KindLabel(Plane.Kind);
    public string Frame=>$"{Plane.Frame.Translation.X:G7}, {Plane.Frame.Translation.Y:G7}, {Plane.Frame.Translation.Z:G7} mm";
}
public partial class DatumEditorViewModel:ObservableObject,IAsyncDisposable
{
    private readonly CadDocumentViewModel document;private readonly IGeometryKernel kernel;
    private readonly DocumentCapture baseline;private readonly long generation;
    private DatumBook associations;private PreparedDocumentEdit? prepared;private CancellationTokenSource? cancellation;
    private bool disposed,committing,loading;private Task? disposal;
    public ObservableCollection<DatumPlaneRow> Planes {get;}=[];
    public ObservableCollection<DatumSourceChoice> Sources {get;}=[];
    public ObservableCollection<DatumSketchChoice> Sketches {get;}=[];
    public ObservableCollection<DatumOccurrenceChoice> Occurrences {get;}=[];
    public IReadOnlyList<DatumPartChoice> Parts {get;}
    public IReadOnlyList<DatumKindChoice> Kinds {get;}=Enum.GetValues<DatumPlaneKind>().Select(k=>new DatumKindChoice(k,KindLabel(k))).ToArray();
    public IReadOnlyList<DatumHinge> Hinges {get;}=Enum.GetValues<DatumHinge>();
    [ObservableProperty] private DatumPlaneRow? selectedPlane;
    [ObservableProperty] private DatumPartChoice? part;
    [ObservableProperty] private DatumSourceChoice? primary;
    [ObservableProperty] private DatumSourceChoice? secondary;
    [ObservableProperty] private DatumSketchChoice? sketch;
    [ObservableProperty] private DatumOccurrenceChoice? occurrence;
    [ObservableProperty] private DatumPlaneKind kind;
    [ObservableProperty] private DatumHinge hinge;
    [ObservableProperty] private string planeName="";
    [ObservableProperty] private double value;
    [ObservableProperty] private bool isWorking;
    [ObservableProperty] private bool isStale;
    [ObservableProperty] private string status=Strings.DatumHint;
    public bool CanEdit=>!disposed&&!IsWorking&&!IsStale;
    public bool CanConfirm=>CanEdit&&prepared is not null;
    public bool IsMidplane=>Kind==DatumPlaneKind.Midplane;
    public bool IsAngle=>Kind==DatumPlaneKind.Angle;
    public string AssociationSummary=>string.Join("; ",associations.SketchBindings.Values.Select(b=>
        (baseline.Snapshot.Sketches.GetValueOrDefault(b.Sketch)?.Name??b.Sketch.ToString())+" → "+(Planes.FirstOrDefault(p=>p.Plane.Id==b.Datum)?.Name??b.Datum.ToString()))
        .Concat(associations.WorkPlane is {} w?[Strings.DocumentWorkPlane+" → "+(Planes.FirstOrDefault(p=>p.Plane.Id==w.Datum)?.Name??w.Datum.ToString())]:[]));
    public DatumBook Book=>associations with{Planes=Planes.ToImmutableDictionary(p=>p.Plane.Id,p=>p.Plane),Axes=Axes.ToImmutableDictionary(p=>p.Axis.Id,p=>p.Axis),Points=Points.ToImmutableDictionary(p=>p.Point.Id,p=>p.Point)};
    public bool HasPendingInput=>HasConstructionInput||(SelectedPlane is not {} row?PlaneName.Length>0:
        row.Plane.PartId!=Part?.Id||row.Name!=PlaneName.Trim()||row.Plane.Kind!=Kind||row.Plane.Primary!=Primary?.Source||
        row.Plane.Secondary!=(IsMidplane?Secondary?.Source:null)||row.Plane.Hinge!=(IsAngle?Hinge:DatumHinge.U)||
        row.Plane.HingeAxis!=(IsAngle?AngularAxis?.Id:null)||Math.Abs(row.Plane.Value-PlaneValue)>1e-12);
    private double PlaneValue=>IsMidplane?0:originalPlaneValue??(IsAngle?Value*Math.PI/180:Value);
    public event EventHandler? CloseRequested;
    public static string KindLabel(DatumPlaneKind kind)=>kind switch{DatumPlaneKind.Offset=>Strings.DatumOffset,DatumPlaneKind.Midplane=>Strings.DatumMidplane,_=>Strings.DatumAngle};
    public DatumEditorViewModel(CadDocumentViewModel document,IGeometryKernel kernel)
    {
        this.document=document;this.kernel=kernel;baseline=document.Session.Capture();generation=document.Session.Generation;associations=baseline.Snapshot.Datums;
        Parts=baseline.Snapshot.Definitions.Values.OfType<PartDefinition>().OrderBy(p=>p.Name).Select(p=>new DatumPartChoice(p.Id,p.Name)).ToArray();
        foreach(var p in associations.Planes.Values.OrderBy(p=>p.Name))Planes.Add(new(p));Part=Parts.FirstOrDefault();
        InitializeConstructions();
        SelectedPlane=Planes.FirstOrDefault();
        document.InvalidatePreview();document.Session.Changed+=OnChanged;document.Detaching+=OnDetached;
    }
    private void OnChanged(object? sender,DocumentChangeSet change){if(!committing){IsStale=true;Invalidate();Status=Strings.Stale;}}
    private void OnDetached(object? sender,EventArgs e){IsStale=true;Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    private void RefreshSources()
    {
        var first=Primary?.Source;var second=Secondary?.Source;Sources.Clear();Sketches.Clear();Occurrences.Clear();
        foreach(var basis in new[]{DocumentWorkPlaneKind.XY,DocumentWorkPlaneKind.XZ,DocumentWorkPlaneKind.YZ})Sources.Add(new(DatumPlaneSource.Base(basis),basis.ToString()));
        if(Part is {} part)
        {
            foreach(var p in Planes.Where(p=>p.Plane.PartId==part.Id))Sources.Add(new(DatumPlaneSource.Named(p.Plane.Id),p.Name));
            foreach(var source in Planes.Where(p=>p.Plane.PartId==part.Id).SelectMany(p=>new[]{p.Plane.Primary,p.Plane.Secondary}).OfType<DatumPlaneSource>().Where(s=>s.Kind>DatumSourceKind.BoxFace).Distinct())
                Sources.Add(new(source,source.Exact is {} exact?Evidence(exact):source.Kind.ToString()));
            foreach(var f in baseline.Snapshot.Features.Values.Where(f=>f.PartId==part.Id&&f.Recipe is BoxRecipe&&!f.IsStale&&!f.IsSuppressed))
                foreach(var b in Enum.GetValues<BoxBoundary>())Sources.Add(new(DatumPlaneSource.Box(f.Id,b),f.Name+" · "+b));
            foreach(var s in baseline.Snapshot.Sketches.Values.Where(s=>s.PartId==part.Id).OrderBy(s=>s.Name))Sketches.Add(new(s.Id,s.Name));
            foreach(var o in baseline.Snapshot.EnumerateOccurrences().Where(o=>o.DefinitionId==part.Id))Occurrences.Add(new(o.Path,OccurrenceLabel(o.Path)));
        }
        foreach(var source in new[]{first,second}.OfType<DatumPlaneSource>().Where(s=>s.Kind>DatumSourceKind.BoxFace&&!Sources.Any(c=>c.Source==s)))
            Sources.Add(new(source,source.Exact is {} exact?Evidence(exact):source.Kind.ToString()));
        Primary=Sources.FirstOrDefault(s=>s.Source==first)??Sources.FirstOrDefault();Secondary=Sources.FirstOrDefault(s=>s.Source==second)??Sources.LastOrDefault();
        Sketch=Sketches.FirstOrDefault();Occurrence=Occurrences.FirstOrDefault();
        RefreshConstructionSources();
    }
    private string OccurrenceLabel(OccurrencePath path)
    {
        var owner=baseline.Snapshot.RootAssemblyId;var labels=new List<string>();
        foreach(var slot in path.Slots)
        {
            var assembly=(AssemblyDefinition)baseline.Snapshot.Definitions[owner];int index=assembly.Children.IndexOf(assembly.Children.Single(c=>c.Id==slot));
            var child=assembly.Children[index];labels.Add(child.Name+" ["+(index+1)+"]");owner=child.DefinitionId;
        }
        return string.Join(" / ",labels);
    }
    partial void OnPartChanged(DatumPartChoice? value){RefreshSources();if(!loading)Invalidate();}
    partial void OnSelectedPlaneChanged(DatumPlaneRow? value)
    {
        if(value is null)return;loading=true;
        try{Part=Parts.FirstOrDefault(p=>p.Id==value.Plane.PartId);RefreshSources();PlaneName=value.Name;Kind=value.Plane.Kind;Hinge=value.Plane.Hinge;
            Value=Kind==DatumPlaneKind.Angle?value.Plane.Value*180/Math.PI:value.Plane.Value;Primary=Sources.FirstOrDefault(s=>s.Source==value.Plane.Primary);Secondary=Sources.FirstOrDefault(s=>s.Source==value.Plane.Secondary);
            AngularAxis=OptionalAxes.FirstOrDefault(a=>a.Id==value.Plane.HingeAxis)??OptionalAxes[0];PlaneSourceFrame.Load(value.Plane.Primary.AuthoredFrame??value.Plane.Frame);pickedPlane=value.Plane.Primary.Exact;originalPlaneValue=value.Plane.Value;NotifyConstruction();}
        finally{loading=false;}
    }
    partial void OnKindChanged(DatumPlaneKind value){OnPropertyChanged(nameof(IsMidplane));OnPropertyChanged(nameof(IsAngle));if(!loading){originalPlaneValue=null;Invalidate();}}
    partial void OnHingeChanged(DatumHinge value){if(!loading)Invalidate();}
    partial void OnPrimaryChanged(DatumSourceChoice? value){if(!loading)Invalidate();}
    partial void OnSecondaryChanged(DatumSourceChoice? value){if(!loading)Invalidate();}
    partial void OnPlaneNameChanged(string value){if(!loading)Invalidate();}
    partial void OnValueChanged(double value){if(!loading){originalPlaneValue=null;Invalidate();}}
    partial void OnIsWorkingChanged(bool value)=>Notify();
    partial void OnIsStaleChanged(bool value)=>Notify();
    private void Notify(){OnPropertyChanged(nameof(CanEdit));OnPropertyChanged(nameof(CanConfirm));OnPropertyChanged(nameof(AssociationSummary));NotifyConstruction();PreviewCommand.NotifyCanExecuteChanged();ConfirmCommand.NotifyCanExecuteChanged();}
    private void Invalidate(){cancellation?.Cancel();prepared?.Dispose();prepared=null;document.SetSketchPreview(null);Notify();}
    [RelayCommand] private void NewPlane(){if(!CanEdit)return;SelectedPlane=null;PlaneName=Strings.DatumPlanes+" "+(Planes.Count+1);Kind=DatumPlaneKind.Offset;Value=0;originalPlaneValue=null;AngularAxis=OptionalAxes[0];Invalidate();}
    [RelayCommand] private void ApplyPlane()
    {
        if(!CanEdit)return;
        try
        {
            if(Part is null||Primary is null||IsMidplane&&Secondary is null)throw new CadValidationException(Strings.DatumSelect);
            var p=new CadDatumPlane(SelectedPlane?.Plane.Id??Guid.NewGuid(),Part.Id,PlaneName.Trim(),Kind,Primary.Source,IsMidplane?Secondary!.Source:null,
                PlaneValue,IsAngle?Hinge:DatumHinge.U){HingeAxis=IsAngle?AngularAxis?.Id:null};p.Validate();
            if(SelectedPlane is {} old){if(old.Plane.PartId!=p.PartId)throw new CadValidationException("Datum ownership cannot change. Create a new datum explicitly.");p=p with{Frame=old.Plane.Frame,SourceRevisions=old.Plane.SourceRevisions,Revision=old.Plane.Revision};}
            Invalidate();if(SelectedPlane is {} row)Planes.Remove(row);var next=new DatumPlaneRow(p);Planes.Add(next);RefreshSources();SelectedPlane=next;Status=Strings.DatumHint;
        }
        catch(Exception ex){Status=string.Format(Strings.ParameterError,ex.Message);}
    }
    [RelayCommand] private void DeletePlane(){if(!CanEdit||SelectedPlane is not {} p)return;Invalidate();Planes.Remove(p);SelectedPlane=null;PlaneName="";RefreshSources();Status=Strings.DatumHint;}
    [RelayCommand] private void BindSketch()
    {
        if(!CanEdit)return;
        try{if(Sketch is null||SelectedPlane is null||HasPendingInput)throw new CadValidationException(Strings.DatumSelect);Invalidate();
            associations=associations with{SketchBindings=associations.SketchBindings.SetItem(Sketch.Id,new(Sketch.Id,SelectedPlane.Plane.Id,SelectedPlane.Plane.Revision))};Notify();}
        catch(Exception ex){Status=string.Format(Strings.ParameterError,ex.Message);}
    }
    [RelayCommand] private void DetachSketch(){if(!CanEdit||Sketch is null)return;Invalidate();associations=associations with{SketchBindings=associations.SketchBindings.Remove(Sketch.Id)};Notify();}
    [RelayCommand] private void BindWorkPlane()
    {
        if(!CanEdit)return;
        try{if(Occurrence is null||SelectedPlane is null||HasPendingInput)throw new CadValidationException(Strings.DatumSelect);Invalidate();
            var o=baseline.Snapshot.EnumerateOccurrences().Single(o=>o.Path.Equals(Occurrence.Path));
            associations=associations with{WorkPlane=new(SelectedPlane.Plane.Id,o.Path,SelectedPlane.Plane.Revision,o.WorldTransform)};Notify();}
        catch(Exception ex){Status=string.Format(Strings.ParameterError,ex.Message);}
    }
    [RelayCommand] private void DetachWorkPlane(){if(!CanEdit)return;Invalidate();associations=associations with{WorkPlane=null};Notify();}
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task PreviewAsync()
    {
        Invalidate();if(document.Session.Generation!=generation){IsStale=true;Status=Strings.Stale;return;}
        if(HasPendingInput){Status=Strings.DatumHint;return;}IsWorking=true;Status=Strings.ParameterEvaluating;var cancel=new CancellationTokenSource();cancellation=cancel;
        try
        {
            var edit=await UpdateAssociatedSections.PrepareCommandAsync(new SetDatumsCommand(Book),new(baseline.Snapshot,generation,document.Session.Assets,kernel),cancel.Token);
            if(cancel.IsCancellationRequested||disposed||IsStale||document.Session.Generation!=generation){edit.Dispose();return;}
            try{edit.Snapshot.Validate();document.SetSketchPreview(edit.Snapshot);}catch{edit.Dispose();throw;}prepared=edit;
            loading=true;
            try
            {
                var selected=SelectedPlane?.Plane.Id;associations=edit.Snapshot.Datums;Planes.Clear();
                foreach(var plane in associations.Planes.Values.OrderBy(p=>p.Name))Planes.Add(new(plane));
                ReloadConstructions();
                RefreshSources();SelectedPlane=Planes.FirstOrDefault(p=>p.Plane.Id==selected);
            }
            finally{loading=false;}
            Notify();Status=Strings.DatumPreviewReady;
        }
        catch(OperationCanceledException){if(!IsStale)Status=Strings.DatumHint;}
        catch(Exception ex){Status=string.Format(Strings.ParameterError,ex.Message);}
        finally{IsWorking=false;cancel.Dispose();if(ReferenceEquals(cancellation,cancel))cancellation=null;}
    }
    [RelayCommand(CanExecute=nameof(CanConfirm))] private async Task ConfirmAsync()
    {
        if(prepared is null)return;IsWorking=true;committing=true;
        try{await document.Session.ExecuteAsync(new CommitPreparedDocumentCommand(prepared.Snapshot,generation,Strings.DatumPlanes));document.FitView();CloseRequested?.Invoke(this,EventArgs.Empty);}
        catch(Exception ex){Invalidate();IsStale=true;Status=string.Format(Strings.ParameterError,ex.Message);}
        finally{committing=false;IsWorking=false;}
    }
    [RelayCommand] private void Cancel(){Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    public ValueTask DisposeAsync()=>new(disposal??=DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        disposed=true;Invalidate();document.Session.Changed-=OnChanged;document.Detaching-=OnDetached;
        PlaneSourceFrame.Changed-=OnSourceFrame;
        try{await Task.WhenAll(new[]{PreviewCommand.ExecutionTask,ConfirmCommand.ExecutionTask,pickTask}.OfType<Task>());}finally{baseline.Dispose();}
    }
}
