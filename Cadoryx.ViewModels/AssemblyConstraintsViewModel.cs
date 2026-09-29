using System.Collections.ObjectModel;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Lang.Strings;
using Cadoryx.Kernel.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public partial class AssemblyConstraintsViewModel : ObservableObject, IDisposable
{
    private readonly CadDocumentViewModel document;
    [ObservableProperty] private AssemblyConstraintRow? selectedConstraint;
    [ObservableProperty] private AssemblyOccurrenceChoice? selectedReference;
    [ObservableProperty] private double distanceMm=10;
    [ObservableProperty] private double angleDegrees=90;
    [ObservableProperty] private double primaryX;
    [ObservableProperty] private double primaryY;
    [ObservableProperty] private double primaryZ;
    [ObservableProperty] private double secondaryX;
    [ObservableProperty] private double secondaryY;
    [ObservableProperty] private double secondaryZ;
    [ObservableProperty] private double primaryAxisX;
    [ObservableProperty] private double primaryAxisY;
    [ObservableProperty] private double primaryAxisZ=1;
    [ObservableProperty] private double secondaryAxisX;
    [ObservableProperty] private double secondaryAxisY;
    [ObservableProperty] private double secondaryAxisZ=1;
    [ObservableProperty] private bool canCreate;
    [ObservableProperty] private bool canEditSelected;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string status="";
    [ObservableProperty] private TopologyKind datumPickKind=TopologyKind.Face;
    [ObservableProperty] private AssemblyConstraintKind datumRelationKind=AssemblyConstraintKind.PlanarMate;
    [ObservableProperty] private string referenceDatumLabel="—";
    [ObservableProperty] private string movingDatumLabel="—";
    [ObservableProperty] private bool hasSolvePreview;
    [ObservableProperty] private string solvePreviewSummary="";
    private AssemblyDatumReference? referenceDatum,movingDatum;
    private DocumentStateId? solvePreviewState;
    private CancellationTokenSource? solvePreviewCancellation;
    private bool pickingReference;
    public event EventHandler<TopologyKind?>? DatumPickRequested;
    public IReadOnlyList<TopologyKind> DatumKinds {get;}=[TopologyKind.Face,TopologyKind.Edge];
    public IReadOnlyList<AssemblyConstraintKind> DatumRelationKinds {get;}=[AssemblyConstraintKind.Coincident,
        AssemblyConstraintKind.Distance,AssemblyConstraintKind.ParallelAxes,AssemblyConstraintKind.Coaxial,
        AssemblyConstraintKind.AngleAxes,AssemblyConstraintKind.PlanarMate];
    public ObservableCollection<AssemblyConstraintRow> Constraints {get;}=[];
    public ObservableCollection<AssemblyOccurrenceChoice> References {get;}=[];
    private static string Label(string key,string fallback)=>Strings.ResourceManager.GetString(key,Strings.Culture)??fallback;
    public string Title=>Label("AssemblyRelations","Assembly relations");
    public string ReferenceLabel=>Label("AssemblyReference","Reference instance");
    public string DistanceLabel=>Label("AssemblyDistance","Distance (mm)");
    public string FixLabel=>Label("AssemblyFix","Fix selected instance");
    public string CoincidentLabel=>Label("AssemblyCoincident","Make origins coincident");
    public string AddDistanceLabel=>Label("AssemblyAddDistance","Add origin distance");
    public string AdjustLabel=>Label("AssemblyAdjust","Adjust second instance");
    public string RetargetLabel=>Label("AssemblyRetarget","Retarget using selection");
    public string ToggleLabel=>Label("AssemblyToggle","Enable / disable");
    public string RemoveLabel=>Label("AssemblyRemove","Remove relation");
    public string ApplyDistanceLabel=>Label("AssemblyApplyDistance","Change distance target");
    public string PrimaryPointLabel=>Label("AssemblyPrimaryPoint","Reference local point (mm)");
    public string SecondaryPointLabel=>Label("AssemblySecondaryPoint","Moving local point (mm)");
    public string PrimaryAxisLabel=>Label("AssemblyPrimaryAxis","Reference local axis");
    public string SecondaryAxisLabel=>Label("AssemblySecondaryAxis","Moving local axis");
    public string ParallelLabel=>Label("AssemblyParallel","Add parallel axes");
    public string CoaxialLabel=>Label("AssemblyCoaxial","Add coaxial axes");
    public string ApplyAnchorsLabel=>Label("AssemblyApplyAnchors","Apply local points");
    public string ApplyAxesLabel=>Label("AssemblyApplyAxes","Apply local axes");
    public string AngleLabel=>Label("AssemblyAngle","Angle (degrees)");
    public string AddAngleLabel=>Label("AssemblyAddAngle","Add axis angle");
    public string AddPlaneLabel=>Label("AssemblyAddPlane","Add plane mate");
    public string ApplyAngleLabel=>Label("AssemblyApplyAngle","Change angle target");
    public string SolveAllLabel=>Label("AssemblySolveAll","Solve assembly relations");
    public string PreviewSolveLabel=>Label("AssemblyPreviewSolve","Preview assembly solve");
    public string ConfirmSolveLabel=>Label("AssemblyConfirmSolve","Confirm positions");
    public string CancelSolveLabel=>Label("AssemblyCancelSolve","Cancel preview");
    public string DatumSectionLabel=>Label("AssemblyDatumSection","BRep geometric datums");
    public string PickReferenceDatumLabel=>Label("AssemblyPickReferenceDatum","Pick reference face or edge");
    public string PickMovingDatumLabel=>Label("AssemblyPickMovingDatum","Pick moving face or edge");
    public string AddDatumRelationLabel=>Label("AssemblyAddDatumRelation","Add geometric relation");
    public string ReselectDatumRelationLabel=>Label("AssemblyReselectDatumRelation","Reselect selected relation datums");

    public AssemblyConstraintsViewModel(CadDocumentViewModel document)
    {
        this.document=document;document.Selection.Changed+=Refresh;document.SceneChanged+=Refresh;
        document.PropertyChanged+=OnDocumentProperty;Refresh(this,EventArgs.Empty);
    }
    private void OnDocumentProperty(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
    {if(e.PropertyName is nameof(CadDocumentViewModel.IsReadOnly) or nameof(CadDocumentViewModel.IsClosingRequested))Refresh(sender,EventArgs.Empty);}
    private void Refresh(object? sender,EventArgs e)
    {
        var snapshot=document.Session.Snapshot;
        if(HasSolvePreview&&solvePreviewState!=snapshot.StateId)CancelSolvePreview();
        if(referenceDatum is {} first&&!first.IsCurrent(snapshot,first.Path,first.DefinitionId))
        {referenceDatum=null;ReferenceDatumLabel="—";}
        if(movingDatum is {} second&&!second.IsCurrent(snapshot,second.Path,second.DefinitionId))
        {movingDatum=null;MovingDatumLabel="—";}
        var selectedId=SelectedConstraint?.Id;
        var referencePath=SelectedReference?.Path;
        Constraints.Clear();References.Clear();
        var occurrences=snapshot.EnumerateOccurrences().ToArray();
        var occurrenceMap=occurrences.ToDictionary(o=>o.Path);
        foreach(var constraint in snapshot.AssemblyConstraints.Values.OrderBy(c=>c.Name).ThenBy(c=>c.Id.Value))
        {
            var evaluation=constraint.Evaluate(snapshot,occurrenceMap);
            Constraints.Add(new(constraint.Id,constraint.Name,constraint.Kind,constraint.IsEnabled,
                evaluation.Status,evaluation.ErrorMm,evaluation.Explanation,evaluation.AngleErrorRad));
        }
        SelectedConstraint=Constraints.FirstOrDefault(c=>c.Id==selectedId);
        var selectedPath=document.Selection.Occurrence;
        foreach(var occurrence in occurrences)
            if(selectedPath is null||!occurrence.Path.Equals(selectedPath))
                References.Add(new(occurrence.Name+" · "+occurrence.Path,occurrence.Path));
        SelectedReference=References.FirstOrDefault(r=>r.Path.Equals(referencePath))??References.FirstOrDefault();
        bool editable=!document.IsReadOnly&&!document.IsClosingRequested&&!document.Session.IsClosing;
        CanCreate=editable&&selectedPath is not null;
        CanEditSelected=editable&&SelectedConstraint is not null;
    }
    partial void OnSelectedConstraintChanged(AssemblyConstraintRow? value)
    {
        CanEditSelected=value is not null&&!document.IsReadOnly&&!document.IsClosingRequested&&!document.Session.IsClosing;
        if(value is {} row&&document.Session.Snapshot.AssemblyConstraints.TryGetValue(row.Id,out var constraint))
        {
            if(constraint.Kind==AssemblyConstraintKind.Distance)DistanceMm=constraint.TargetDistanceMm;
            if(constraint.Kind==AssemblyConstraintKind.AngleAxes)AngleDegrees=constraint.TargetAngleRad*180/Math.PI;
            (PrimaryX,PrimaryY,PrimaryZ)=(constraint.PrimaryLocalPoint.X,constraint.PrimaryLocalPoint.Y,constraint.PrimaryLocalPoint.Z);
            (SecondaryX,SecondaryY,SecondaryZ)=(constraint.SecondaryLocalPoint.X,constraint.SecondaryLocalPoint.Y,constraint.SecondaryLocalPoint.Z);
            var axisA=constraint.PrimaryLocalAxis==Vector3d.Zero?Vector3d.UnitZ:constraint.PrimaryLocalAxis;
            var axisB=constraint.SecondaryLocalAxis==Vector3d.Zero?Vector3d.UnitZ:constraint.SecondaryLocalAxis;
            (PrimaryAxisX,PrimaryAxisY,PrimaryAxisZ)=(axisA.X,axisA.Y,axisA.Z);
            (SecondaryAxisX,SecondaryAxisY,SecondaryAxisZ)=(axisB.X,axisB.Y,axisB.Z);
        }
    }
    [RelayCommand] private async Task AddFixedAsync()
    {
        if(!CanCreate||IsBusy||document.Selection.Occurrence is not {} path)return;
        await ExecuteAsync(AssemblyConstraintCommands.AddFixed(AssemblyConstraintId.New(),
            Label("AssemblyFixedName","Fixed")+" "+(Constraints.Count+1),path));
    }
    [RelayCommand] private async Task AddCoincidentAsync()
    {
        if(!CanCreate||IsBusy||document.Selection.Occurrence is not {} path||SelectedReference is not {} reference)return;
        await ExecuteAsync(AssemblyConstraintCommands.AddPair(AssemblyConstraintId.New(),
            Label("AssemblyCoincidentName","Coincident")+" "+(Constraints.Count+1),AssemblyConstraintKind.Coincident,
            reference.Path,path,PrimaryPoint,SecondaryPoint,0));
    }
    [RelayCommand] private async Task AddDistanceAsync()
    {
        if(!CanCreate||IsBusy||document.Selection.Occurrence is not {} path||SelectedReference is not {} reference)return;
        await ExecuteAsync(AssemblyConstraintCommands.AddPair(AssemblyConstraintId.New(),
            Label("AssemblyDistanceName","Distance")+" "+(Constraints.Count+1),AssemblyConstraintKind.Distance,
            reference.Path,path,PrimaryPoint,SecondaryPoint,DistanceMm));
    }
    [RelayCommand] private Task AddParallelAsync()=>AddAxisAsync(AssemblyConstraintKind.ParallelAxes);
    [RelayCommand] private Task AddCoaxialAsync()=>AddAxisAsync(AssemblyConstraintKind.Coaxial);
    [RelayCommand] private Task AddAngleAsync()=>AddAxisAsync(AssemblyConstraintKind.AngleAxes);
    [RelayCommand] private Task AddPlaneAsync()=>AddAxisAsync(AssemblyConstraintKind.PlanarMate);
    private Task AddAxisAsync(AssemblyConstraintKind kind)
    {
        if(!CanCreate||IsBusy||document.Selection.Occurrence is not {} path||SelectedReference is not {} reference)
            return Task.CompletedTask;
        var name=kind switch
        {
            AssemblyConstraintKind.Coaxial=>Label("AssemblyCoaxialName","Coaxial"),
            AssemblyConstraintKind.ParallelAxes=>Label("AssemblyParallelName","Parallel"),
            AssemblyConstraintKind.AngleAxes=>Label("AssemblyAngleName","Angle"),
            _=>Label("AssemblyPlaneName","Plane")
        };
        return ExecuteAsync(AssemblyConstraintCommands.AddAxisPair(AssemblyConstraintId.New(),
            name+" "+(Constraints.Count+1),kind,reference.Path,path,PrimaryPoint,SecondaryPoint,
            PrimaryAxis,SecondaryAxis,kind==AssemblyConstraintKind.AngleAxes?AngleDegrees*Math.PI/180:0));
    }
    private Vector3d PrimaryPoint=>new(PrimaryX,PrimaryY,PrimaryZ);
    private Vector3d SecondaryPoint=>new(SecondaryX,SecondaryY,SecondaryZ);
    private Vector3d PrimaryAxis=>new(PrimaryAxisX,PrimaryAxisY,PrimaryAxisZ);
    private Vector3d SecondaryAxis=>new(SecondaryAxisX,SecondaryAxisY,SecondaryAxisZ);
    [RelayCommand] private void PickReferenceDatum()
    {
        pickingReference=true;Status="Pick one analytic face or circular edge in the viewport.";
        DatumPickRequested?.Invoke(this,DatumPickKind);
    }
    [RelayCommand] private void PickMovingDatum()
    {
        pickingReference=false;Status="Pick one analytic face or circular edge in the viewport.";
        DatumPickRequested?.Invoke(this,DatumPickKind);
    }
    public async Task ReceiveDatumPickAsync(OccurrencePath path,BodyId body,int index,string fingerprint)
    {
        try
        {
            if(document.GeometryKernel is not IAssemblyDatumResolver resolver)
                throw new CadValidationException("The active geometry kernel cannot resolve analytic assembly datums.");
            var snapshot=document.Session.Snapshot;
            var datum=await resolver.ResolveAssemblyDatumAsync(snapshot,path,body,index,fingerprint,
                document.Session.Assets);
            if(document.Session.Snapshot.StateId!=snapshot.StateId)
                throw new CadValidationException("The document changed while picking; reselect the datum.");
            if(pickingReference)
            {referenceDatum=datum;ReferenceDatumLabel=$"{datum.Geometry} · {path} · #{index}";}
            else
            {movingDatum=datum;MovingDatumLabel=$"{datum.Geometry} · {path} · #{index}";}
            Status="Datum selected. Pick the other side, then create the relation.";
        }
        catch(Exception error){Status=error.Message;document.Report(error);}
    }
    public void ReportDatumPickFailure(string message)=>Status=message;
    [RelayCommand] private async Task AddDatumRelationAsync()
    {
        if(IsBusy||referenceDatum is not {} first||movingDatum is not {} second)return;
        double target=DatumRelationKind switch
        {
            AssemblyConstraintKind.Distance=>DistanceMm,
            AssemblyConstraintKind.AngleAxes=>AngleDegrees*Math.PI/180,
            _=>0
        };
        await ExecuteAsync(AssemblyConstraintCommands.AddDatumPair(AssemblyConstraintId.New(),
            $"{DatumRelationKind} {Constraints.Count+1}",DatumRelationKind,first,second,target));
    }
    [RelayCommand] private async Task ReselectDatumRelationAsync()
    {
        if(IsBusy||SelectedConstraint is not {} selected||referenceDatum is not {} first||
           movingDatum is not {} second)return;
        await ExecuteAsync(AssemblyConstraintCommands.ReselectDatums(selected.Id,first,second));
    }
    [RelayCommand] private async Task AdjustAsync()
    {
        if(!CanEditSelected||IsBusy||SelectedConstraint is not {} row)return;
        await ExecuteAsync(AssemblyConstraintCommands.AdjustPair(row.Id));
    }
    [RelayCommand] private async Task ApplyDistanceAsync()
    {
        if(!CanEditSelected||IsBusy||SelectedConstraint is not {} row)return;
        await ExecuteAsync(AssemblyConstraintCommands.SetDistance(row.Id,DistanceMm));
    }
    [RelayCommand] private async Task ApplyAnchorsAsync()
    {
        if(!CanEditSelected||IsBusy||SelectedConstraint is not {} row)return;
        await ExecuteAsync(AssemblyConstraintCommands.SetAnchors(row.Id,PrimaryPoint,SecondaryPoint));
    }
    [RelayCommand] private async Task ApplyAxesAsync()
    {
        if(!CanEditSelected||IsBusy||SelectedConstraint is not {} row)return;
        await ExecuteAsync(AssemblyConstraintCommands.SetAxes(row.Id,PrimaryAxis,SecondaryAxis));
    }
    [RelayCommand] private async Task ApplyAngleAsync()
    {
        if(!CanEditSelected||IsBusy||SelectedConstraint is not {} row)return;
        await ExecuteAsync(AssemblyConstraintCommands.SetAngle(row.Id,AngleDegrees*Math.PI/180));
    }
    [RelayCommand] private async Task SolveAllAsync()
    {
        if(IsBusy||document.IsReadOnly||document.IsClosingRequested)return;
        var command=AssemblySolveCommands.Solve();
        await ExecuteAsync(command);
        if(command.Report is {} report)Status=report.Explanation;
    }
    [RelayCommand] private async Task PreviewSolveAsync()
    {
        if(IsBusy||document.IsReadOnly||document.IsClosingRequested||document.IsWorking||document.HasPreview)return;
        CancelSolvePreview();
        var snapshot=document.Session.Snapshot;
        using var cancel=new CancellationTokenSource();solvePreviewCancellation=cancel;IsBusy=true;
        try
        {
            var plan=await Task.Run(()=>AssemblySolveCommands.Plan(snapshot,cancel.Token),cancel.Token);
            if(cancel.IsCancellationRequested||document.Session.Snapshot.StateId!=snapshot.StateId)
            {Status="The document changed; preview the assembly again.";return;}
            var report=plan.Report;
            SolvePreviewSummary=string.Format(Label("AssemblySolvePreviewSummary",
                    "{0} Components: {1}; local free motions: {2}; redundant relations: {3}; unanchored groups: {4}."),
                report.Explanation,report.Components.Length,report.Components.Sum(c=>c.LocalFreedom),
                report.RedundantRelations.Length,report.UnanchoredRoots.Length);
            Status=SolvePreviewSummary;
            if(report.Status is AssemblySolveStatus.Conflict or AssemblySolveStatus.StaleOrUnsupported)return;
            document.SetAssemblyPreview(plan.Candidate);
            solvePreviewState=snapshot.StateId;HasSolvePreview=true;
        }
        catch(OperationCanceledException){Status="Assembly preview canceled.";}
        catch(Exception error){Status=error.Message;document.Report(error);}
        finally{if(ReferenceEquals(solvePreviewCancellation,cancel))solvePreviewCancellation=null;IsBusy=false;}
    }
    [RelayCommand] private async Task ConfirmSolveAsync()
    {
        if(!HasSolvePreview||solvePreviewState!=document.Session.Snapshot.StateId)
        {CancelSolvePreview();Status="The assembly changed; preview again.";return;}
        CancelSolvePreview();
        await SolveAllAsync(); // Rechecks current BRep evidence and recomputes before committing.
    }
    [RelayCommand] private void CancelSolvePreview()
    {
        solvePreviewCancellation?.Cancel();
        if(HasSolvePreview)document.SetAssemblyPreview(null);
        solvePreviewState=null;HasSolvePreview=false;SolvePreviewSummary="";
    }
    [RelayCommand] private async Task RetargetAsync()
    {
        if(!CanEditSelected||IsBusy||SelectedConstraint is not {} row||document.Selection.Occurrence is not {} path)return;
        await ExecuteAsync(row.Kind==AssemblyConstraintKind.Fixed
            ?AssemblyConstraintCommands.Retarget(row.Id,path)
            :SelectedReference is {} reference
                ?AssemblyConstraintCommands.Retarget(row.Id,reference.Path,path)
                :throw new CadValidationException("Select a reference instance."));
    }
    [RelayCommand] private async Task ToggleAsync()
    {
        if(!CanEditSelected||IsBusy||SelectedConstraint is not {} row)return;
        await ExecuteAsync(AssemblyConstraintCommands.SetEnabled(row.Id,!row.IsEnabled));
    }
    [RelayCommand] private async Task RemoveAsync()
    {
        if(!CanEditSelected||IsBusy||SelectedConstraint is not {} row)return;
        await ExecuteAsync(AssemblyConstraintCommands.Remove(row.Id));
    }
    private async Task ExecuteAsync(ICadDocumentCommand command)
    {
        try{IsBusy=true;await document.Session.ExecuteAsync(command);Status="";}
        catch(Exception ex){Status=ex.Message;document.Report(ex);}
        finally{IsBusy=false;}
    }
    public void Dispose()
    {CancelSolvePreview();document.Selection.Changed-=Refresh;document.SceneChanged-=Refresh;document.PropertyChanged-=OnDocumentProperty;}
}

public sealed record AssemblyConstraintRow(AssemblyConstraintId Id,string Name,AssemblyConstraintKind Kind,bool IsEnabled,
    AssemblyConstraintStatus State,double ErrorMm,string Explanation,double AngleErrorRad)
{
    public string Display=>Name+" · "+State+(State!=AssemblyConstraintStatus.Unsatisfied?"":
        Kind is AssemblyConstraintKind.ParallelAxes or AssemblyConstraintKind.Coaxial
            ?$" ({ErrorMm:G4} mm, {AngleErrorRad*180/Math.PI:G4}°)":$" ({ErrorMm:G4} mm)");
}
public sealed record AssemblyOccurrenceChoice(string Name,OccurrencePath Path);
