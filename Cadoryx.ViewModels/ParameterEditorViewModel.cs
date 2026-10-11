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

public interface IParameterEditorHost {Task ShowAsync(ParameterEditorViewModel editor);}
public sealed record ParameterDimensionChoice(ParameterDimension Value,string Label);
public sealed record ParameterTargetChoice(ParameterTarget Target,string Label,ParameterDimension Dimension);
public partial class ParameterRow(CadParameter parameter):ObservableObject
{
    public CadParameter Parameter {get;}=parameter;
    public string Name=>Parameter.Name;
    public string Expression=>Parameter.Reference is null?Parameter.Expression:Strings.ParameterReference;
    public string Dimension=>ParameterEditorViewModel.DimensionLabel(Parameter.Dimension);
    [ObservableProperty] private string value="";
}
public sealed record ParameterBindingRow(ParameterBinding Binding,string Label);

public partial class ParameterEditorViewModel:ObservableObject,IAsyncDisposable
{
    private readonly CadDocumentViewModel document;
    private readonly IGeometryKernel kernel;
    private readonly DocumentCapture baseline;
    private readonly long generation;
    private PreparedDocumentEdit? prepared;
    private CancellationTokenSource? cancellation;
    private bool committing,disposed,loading;
    private Task? disposal;
    public ObservableCollection<ParameterRow> Parameters {get;}=[];
    public ObservableCollection<ParameterBindingRow> Bindings {get;}=[];
    public IReadOnlyList<ParameterTargetChoice> Targets {get;}
    public IReadOnlyList<ParameterDimensionChoice> Dimensions {get;}=Enum.GetValues<ParameterDimension>().Select(d=>new ParameterDimensionChoice(d,DimensionLabel(d))).ToArray();
    [ObservableProperty] private ParameterRow? selectedParameter;
    [ObservableProperty] private ParameterBindingRow? selectedBinding;
    [ObservableProperty] private ParameterTargetChoice? referenceTarget;
    [ObservableProperty] private ParameterTargetChoice? drivingTarget;
    [ObservableProperty] private string parameterName="";
    [ObservableProperty] private string expression="";
    [ObservableProperty] private ParameterDimension dimension=ParameterDimension.Length;
    [ObservableProperty] private bool isReference;
    [ObservableProperty] private bool isWorking;
    [ObservableProperty] private bool isStale;
    [ObservableProperty] private string status=Strings.ParameterDraftHint;
    public bool CanEdit=>!disposed&&!IsWorking&&!IsStale;
    public bool CanConfirm=>CanEdit&&prepared is not null;
    public bool HasPendingInput=>SelectedParameter is not {} row?ParameterName.Length>0:
        row.Name!=ParameterName.Trim()||row.Parameter.Dimension!=Dimension||
        row.Parameter.Expression!=(IsReference?"":Expression.Trim())||row.Parameter.Reference!=(IsReference?ReferenceTarget?.Target:null);
    public event EventHandler? CloseRequested;
    public ParameterBook Book=>new(Parameters.Select(p=>p.Parameter).ToImmutableArray(),Bindings.Select(b=>b.Binding).ToImmutableArray());
    public static string DimensionLabel(ParameterDimension dimension)=>dimension switch
    {ParameterDimension.Length=>Strings.ParameterLength,ParameterDimension.Angle=>Strings.ParameterAngle,_=>Strings.ParameterScalar};
    public ParameterEditorViewModel(CadDocumentViewModel document,IGeometryKernel kernel)
    {
        this.document=document;this.kernel=kernel;baseline=document.Session.Capture();generation=document.Session.Generation;
        Targets=ParameterTargets.All(baseline.Snapshot).Select(target=>
        {
            var quantity=ParameterTargets.Read(baseline.Snapshot,target);var dimension=quantity.HasDimension(ParameterDimension.Angle)?ParameterDimension.Angle:quantity.HasDimension(ParameterDimension.Length)?ParameterDimension.Length:ParameterDimension.Scalar;
            string owner=target.Kind==ParameterTargetKind.SketchRegion?
                baseline.Snapshot.Sketches[new(target.Owner)].Name+" · "+Strings.OffsetRegions+" "+
                (Array.FindIndex(baseline.Snapshot.Sketches[new(target.Owner)].RegionOffsets.ToArray(),g=>g.Id==target.Curve)+1):
                target.Kind==ParameterTargetKind.ExactSketchCurve?CurveOwner(baseline.Snapshot,target):
                target.Kind==ParameterTargetKind.AssemblyPattern?baseline.Snapshot.AssemblyPatterns[target.Owner].Name:
                target.Kind==ParameterTargetKind.Datum?baseline.Snapshot.Datums.Planes.GetValueOrDefault(target.Owner)?.Name??
                    baseline.Snapshot.Datums.Axes.GetValueOrDefault(target.Owner)?.Name??baseline.Snapshot.Datums.Points[target.Owner].Name:
                target.Kind==ParameterTargetKind.Feature?baseline.Snapshot.Features[new(target.Owner)].Name:
                baseline.Snapshot.Sketches[new(target.Owner)].Name+" · "+(baseline.Snapshot.Sketches[new(target.Owner)].Constraints.Select((c,i)=>(c,i)).Single(v=>v.c.Id.Value==target.Constraint).i+1);
            string field=target.Kind==ParameterTargetKind.SketchRegion?Strings.OffsetDistance:target.Kind==ParameterTargetKind.ExactSketchCurve?CurveField(target):target.Property;
            return new ParameterTargetChoice(target,target.Kind==ParameterTargetKind.ExactSketchCurve?
                $"{field} ({DimensionLabel(dimension)}) · {owner}":$"{owner} · {field} ({DimensionLabel(dimension)})",dimension);
        }).ToArray();
        foreach(var p in baseline.Snapshot.Parameters.Parameters)Parameters.Add(new(p));
        foreach(var b in baseline.Snapshot.Parameters.Bindings)Bindings.Add(Row(b));
        SelectedParameter=Parameters.FirstOrDefault();
        document.InvalidatePreview();document.Session.Changed+=OnChanged;document.Detaching+=OnDetached;
    }
    private static string CurveOwner(DocumentSnapshot snapshot,ParameterTarget target)
    {
        var sketch=snapshot.Sketches[new(target.Owner)];int index=Array.FindIndex(sketch.Ellipses.ToArray(),e=>e.Id.Value==target.Curve);
        if(index>=0)return $"{sketch.Name} · {Strings.ExactEllipse} {index+1}";
        index=Array.FindIndex(sketch.ExpressionCurves.ToArray(),e=>e.Id.Value==target.Curve);
        if(index>=0)return $"{sketch.Name} · {Strings.OffsetCurve} {index+1}";
        index=Array.FindIndex(sketch.Freeforms.ToArray(),e=>e.Id.Value==target.Curve);return $"{sketch.Name} · {Strings.ExactFreeform} {index+1}";
    }
    private static string CurveField(ParameterTarget target)
    {
        string label=target.Property switch
        {
            "CenterX"=>Strings.CurveParameterCenterX,"CenterY"=>Strings.CurveParameterCenterY,
            "CosineX"=>Strings.CurveParameterCosineX,"CosineY"=>Strings.CurveParameterCosineY,
            "SineX"=>Strings.CurveParameterSineX,"SineY"=>Strings.CurveParameterSineY,
            "StartAngle"=>Strings.CurveParameterStartAngle,"SweepAngle"=>Strings.CurveParameterSweepAngle,
            "PoleX"=>Strings.CurveParameterPoleX,"PoleY"=>Strings.CurveParameterPoleY,"Weight"=>Strings.CurveParameterWeight,
            "Knot"=>Strings.CurveParameterKnot,"FirstParameter"=>Strings.CurveParameterFirst,"LastParameter"=>Strings.CurveParameterLast,
            "OffsetDistance"=>Strings.OffsetDistance,
            _=>throw new CadValidationException("Unsupported exact curve target label.")
        };
        return target.Index is {} index?$"{label} {index+1}":label;
    }
    private ParameterBindingRow Row(ParameterBinding b)=>new(b,$"{Parameters.Single(p=>p.Parameter.Id==b.Parameter).Name} → {Targets.Single(t=>t.Target==b.Target).Label}");
    private void OnChanged(object? sender,DocumentChangeSet change){if(!committing){IsStale=true;Invalidate();Status=Strings.Stale;}}
    private void OnDetached(object? sender,EventArgs e){IsStale=true;Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    partial void OnIsWorkingChanged(bool value)=>Notify();
    partial void OnIsStaleChanged(bool value)=>Notify();
    partial void OnSelectedParameterChanged(ParameterRow? value)
    {
        if(value is null)return;loading=true;
        try{ParameterName=value.Name;Expression=value.Parameter.Expression;Dimension=value.Parameter.Dimension;IsReference=value.Parameter.Reference is not null;ReferenceTarget=Targets.FirstOrDefault(t=>t.Target==value.Parameter.Reference);}
        finally{loading=false;}
    }
    partial void OnParameterNameChanged(string value){if(!loading)Invalidate();}
    partial void OnExpressionChanged(string value){if(!loading)Invalidate();}
    partial void OnDimensionChanged(ParameterDimension value){if(!loading)Invalidate();}
    partial void OnIsReferenceChanged(bool value){if(!loading)Invalidate();}
    partial void OnReferenceTargetChanged(ParameterTargetChoice? value){if(!loading){if(IsReference&&value is not null)Dimension=value.Dimension;Invalidate();}}
    private void Notify(){OnPropertyChanged(nameof(CanEdit));OnPropertyChanged(nameof(CanConfirm));PreviewCommand.NotifyCanExecuteChanged();ConfirmCommand.NotifyCanExecuteChanged();}
    private void Invalidate(){cancellation?.Cancel();prepared?.Dispose();prepared=null;document.SetSketchPreview(null);foreach(var row in Parameters)row.Value="";Notify();}
    [RelayCommand] private void NewParameter(){SelectedParameter=null;ParameterName="parameter"+(Parameters.Count+1);Expression="20 mm";Dimension=ParameterDimension.Length;IsReference=false;ReferenceTarget=null;Invalidate();}
    [RelayCommand] private void ApplyParameter()
    {
        if(!CanEdit)return;
        try
        {
            var target=IsReference?ReferenceTarget?.Target??throw new CadValidationException(Strings.ParameterSelectTarget):null;
            var p=new CadParameter(SelectedParameter?.Parameter.Id??Guid.NewGuid(),ParameterName.Trim(),Dimension,IsReference?"":Expression.Trim(),target);
            if(!ParameterExpressions.IsName(p.Name)||Parameters.Any(r=>r.Parameter.Id!=p.Id&&r.Name.Equals(p.Name,StringComparison.OrdinalIgnoreCase)))throw new CadValidationException(Strings.ParameterInvalidName);
            if(!IsReference)ParameterExpressions.Dependencies(p.Expression);
            Invalidate();var old=SelectedParameter;
            if(old is not null)Parameters.Remove(old);var row=new ParameterRow(p);Parameters.Add(row);SelectedParameter=row;
            var oldBindings=Bindings.Select(b=>b.Binding).ToArray();Bindings.Clear();foreach(var b in oldBindings)Bindings.Add(Row(b));Status=Strings.ParameterDraftHint;
        }
        catch(Exception ex){Status=string.Format(Strings.ParameterError,ex.Message);}
    }
    [RelayCommand] private void DeleteParameter()
    {
        if(!CanEdit||SelectedParameter is not {} row)return;Invalidate();
        foreach(var b in Bindings.Where(b=>b.Binding.Parameter==row.Parameter.Id).ToArray())Bindings.Remove(b);
        Parameters.Remove(row);SelectedParameter=null;Status=Strings.ParameterDraftHint;
    }
    [RelayCommand] private void BindDimension()
    {
        if(!CanEdit)return;
        try
        {
            var p=SelectedParameter?.Parameter??throw new CadValidationException(Strings.ParameterSelectParameter);
            var target=DrivingTarget??throw new CadValidationException(Strings.ParameterSelectTarget);
            if(p.Reference is not null||p.Dimension!=target.Dimension)throw new CadValidationException(Strings.ParameterBindingMismatch);
            if(Bindings.Any(b=>b.Binding.Target==target.Target))throw new CadValidationException(Strings.ParameterAlreadyDriven);
            Invalidate();Bindings.Add(Row(new(Guid.NewGuid(),p.Id,target.Target)));Status=Strings.ParameterDraftHint;
        }
        catch(Exception ex){Status=string.Format(Strings.ParameterError,ex.Message);}
    }
    [RelayCommand] private void DetachBinding(){if(CanEdit&&SelectedBinding is {} row){Invalidate();Bindings.Remove(row);Status=Strings.ParameterDraftHint;}}
    [RelayCommand(CanExecute=nameof(CanEdit))] private async Task PreviewAsync()
    {
        Invalidate();if(document.Session.Generation!=generation){IsStale=true;Status=Strings.Stale;return;}
        if(HasPendingInput){Status=Strings.ParameterDraftHint;return;}
        IsWorking=true;Status=Strings.ParameterEvaluating;var cancel=new CancellationTokenSource();cancellation=cancel;
        try
        {
            var edit=await UpdateAssociatedSections.PrepareCommandAsync(new SetParametersCommand(Book),new(baseline.Snapshot,generation,document.Session.Assets,kernel),cancel.Token);
            if(cancel.IsCancellationRequested||disposed||IsStale||document.Session.Generation!=generation){edit.Dispose();return;}
            try{edit.Snapshot.Validate();document.SetSketchPreview(edit.Snapshot);}catch{edit.Dispose();throw;}
            prepared=edit;var values=ParameterGraph.Evaluate(edit.Snapshot);
            foreach(var row in Parameters){var value=values[row.Parameter.Id];row.Value=value.Value.ToString("G12",System.Globalization.CultureInfo.CurrentCulture)+(row.Parameter.Dimension switch{ParameterDimension.Length=>" mm",ParameterDimension.Angle=>" rad",_=>""});}
            Status=Strings.ParameterPreviewReady;
        }
        catch(OperationCanceledException){if(!IsStale)Status=Strings.ParameterDraftHint;}
        catch(Exception ex){Status=string.Format(Strings.ParameterError,ex.Message);}
        finally{IsWorking=false;cancel.Dispose();if(ReferenceEquals(cancellation,cancel))cancellation=null;}
    }
    [RelayCommand(CanExecute=nameof(CanConfirm))] private async Task ConfirmAsync()
    {
        if(prepared is null)return;IsWorking=true;committing=true;
        try{await document.Session.ExecuteAsync(new CommitPreparedDocumentCommand(prepared.Snapshot,generation,Strings.DocumentParameters));document.FitView();CloseRequested?.Invoke(this,EventArgs.Empty);}
        catch(Exception ex){Invalidate();IsStale=true;Status=string.Format(Strings.ParameterError,ex.Message);}
        finally{committing=false;IsWorking=false;}
    }
    [RelayCommand] private void Cancel(){Invalidate();CloseRequested?.Invoke(this,EventArgs.Empty);}
    public ValueTask DisposeAsync()=>new(disposal??=DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        disposed=true;Invalidate();document.Session.Changed-=OnChanged;document.Detaching-=OnDetached;
        try{await Task.WhenAll(new[]{PreviewCommand.ExecutionTask,ConfirmCommand.ExecutionTask}.OfType<Task>());}finally{baseline.Dispose();}
    }
}
