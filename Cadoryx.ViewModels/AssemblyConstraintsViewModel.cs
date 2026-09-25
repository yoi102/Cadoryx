using System.Collections.ObjectModel;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public partial class AssemblyConstraintsViewModel : ObservableObject, IDisposable
{
    private readonly CadDocumentViewModel document;
    [ObservableProperty] private AssemblyConstraintRow? selectedConstraint;
    [ObservableProperty] private AssemblyOccurrenceChoice? selectedReference;
    [ObservableProperty] private double distanceMm=10;
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
    private Task AddAxisAsync(AssemblyConstraintKind kind)
    {
        if(!CanCreate||IsBusy||document.Selection.Occurrence is not {} path||SelectedReference is not {} reference)
            return Task.CompletedTask;
        return ExecuteAsync(AssemblyConstraintCommands.AddAxisPair(AssemblyConstraintId.New(),
            Label(kind==AssemblyConstraintKind.Coaxial?"AssemblyCoaxialName":"AssemblyParallelName",
                kind==AssemblyConstraintKind.Coaxial?"Coaxial":"Parallel")+" "+(Constraints.Count+1),
            kind,reference.Path,path,PrimaryPoint,SecondaryPoint,PrimaryAxis,SecondaryAxis));
    }
    private Vector3d PrimaryPoint=>new(PrimaryX,PrimaryY,PrimaryZ);
    private Vector3d SecondaryPoint=>new(SecondaryX,SecondaryY,SecondaryZ);
    private Vector3d PrimaryAxis=>new(PrimaryAxisX,PrimaryAxisY,PrimaryAxisZ);
    private Vector3d SecondaryAxis=>new(SecondaryAxisX,SecondaryAxisY,SecondaryAxisZ);
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
    {document.Selection.Changed-=Refresh;document.SceneChanged-=Refresh;document.PropertyChanged-=OnDocumentProperty;}
}

public sealed record AssemblyConstraintRow(AssemblyConstraintId Id,string Name,AssemblyConstraintKind Kind,bool IsEnabled,
    AssemblyConstraintStatus State,double ErrorMm,string Explanation,double AngleErrorRad)
{
    public string Display=>Name+" · "+State+(State!=AssemblyConstraintStatus.Unsatisfied?"":
        Kind is AssemblyConstraintKind.ParallelAxes or AssemblyConstraintKind.Coaxial
            ?$" ({ErrorMm:G4} mm, {AngleErrorRad*180/Math.PI:G4}°)":$" ({ErrorMm:G4} mm)");
}
public sealed record AssemblyOccurrenceChoice(string Name,OccurrencePath Path);
