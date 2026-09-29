using System.Collections.ObjectModel;
using System.Globalization;
using Cadoryx.Commands;
using Cadoryx.Db;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public sealed record AssociatedSectionRow(FeatureId Id,string Name,string Status);
public partial class DocumentReviewViewModel
{
    [ObservableProperty] private bool associativeSection=true;
    [ObservableProperty] private bool sectionFaces;
    [ObservableProperty] private AssociatedSectionRow? selectedSection;
    public ObservableCollection<AssociatedSectionRow> AssociatedSectionRows {get;}=[];
    [ObservableProperty] private EngineeringDimension? selectedDimension;
    [ObservableProperty] private string dimensionName=R("EngineeringDimension");
    [ObservableProperty] private bool angleDimension;
    [ObservableProperty] private string dimensionFirst="0, 0, 0";
    [ObservableProperty] private string dimensionSecond="10, 0, 0";
    [ObservableProperty] private string dimensionThird="10, 10, 0";
    [ObservableProperty] private double dimensionFlyout=10;
    public ObservableCollection<EngineeringDimension> DimensionRows {get;}=[];
    private void RefreshEngineeringReview()
    {
        RefreshBookmarks();
        var s=SelectedSection?.Id;var d=SelectedDimension?.Id;var doc=document.Session.Snapshot;
        AssociatedSectionRows.Clear();
        foreach(var section in doc.AssociatedSections.Values.OrderBy(x=>doc.Features[x.FeatureId].Name))
            AssociatedSectionRows.Add(new(section.FeatureId,doc.Features[section.FeatureId].Name,section.StaleReason??R("ReviewCurrent")));
        SelectedSection=AssociatedSectionRows.FirstOrDefault(x=>x.Id==s);
        DimensionRows.Clear();foreach(var dimension in doc.Dimensions.Values.OrderBy(x=>x.Name))DimensionRows.Add(dimension);
        SelectedDimension=DimensionRows.FirstOrDefault(x=>x.Id==d);
    }
    partial void OnSelectedDimensionChanged(EngineeringDimension? value)
    {
        if(value is not {} d)return;
        DimensionName=d.Name;AngleDimension=d.Kind==EngineeringDimensionKind.Angle;DimensionFirst=PointText(d.First);
        DimensionSecond=PointText(d.Second);DimensionThird=PointText(d.Third);DimensionFlyout=d.FlyoutMm;
    }
    partial void OnSelectedSectionChanged(AssociatedSectionRow? value)
    {
        if(value is null||!document.Session.Snapshot.AssociatedSections.TryGetValue(value.Id,out var s))return;
        SectionAxis=Math.Abs(s.Normal.X)>.99?Rendering.SectionAxis.X:Math.Abs(s.Normal.Y)>.99?Rendering.SectionAxis.Y:Rendering.SectionAxis.Z;
        SectionOffsetMm=s.OffsetMm;SectionFaces=s.Output==SectionOutput.Faces;
    }
    private static string PointText(Vector3d p)=>FormattableString.Invariant($"{p.X:G17}, {p.Y:G17}, {p.Z:G17}");
    private static Vector3d ParsePoint(string text)
    {
        var parts=text.Split([',',';',' '],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(parts.Length!=3)throw new CadValidationException("Enter X, Y, Z in millimeters.");
        var values=parts.Select(x=>double.Parse(x,NumberStyles.Float,CultureInfo.InvariantCulture)).ToArray();
        var p=new Vector3d(values[0],values[1],values[2]);p.Validate();return p;
    }
    private async Task ReviewEditAsync(ICadDocumentCommand command)
    {
        if(disposed||IsReviewBusy||document.IsReadOnly)return;
        using var cancel=new CancellationTokenSource();analysisCancellation=cancel;IsReviewBusy=true;
        try{await document.Session.ExecuteAsync(command,cancel.Token);}
        catch(OperationCanceledException){ReviewStatus=R("ExactCancelled");}
        catch(Exception ex){ReviewStatus=ex.Message;document.Report(ex);}
        finally{analysisCancellation=null;IsReviewBusy=false;}
    }
    [RelayCommand] private async Task RefreshAssociatedSectionAsync()
    {
        if(SelectedSection is not {} row)return;
        var normal=SectionAxis switch{Rendering.SectionAxis.X=>new Vector3d(1,0,0),Rendering.SectionAxis.Y=>new(0,1,0),_=>Vector3d.UnitZ};
        await ReviewEditAsync(new EditAssociatedSectionCommand(row.Id,normal,SectionOffsetMm,SectionFaces?SectionOutput.Faces:SectionOutput.Curves));
    }
    [RelayCommand] private async Task DetachAssociatedSectionAsync()
    {
        if(SelectedSection is not {} row)return;
        var s=document.Session.Snapshot.AssociatedSections[row.Id];
        await ReviewEditAsync(new EditAssociatedSectionCommand(row.Id,s.Normal,s.OffsetMm,s.Output,true));
    }
    [RelayCommand] private void UseMeasuredDimension()
    {
        if(Result?.Distance is not {} d)return;
        SelectedDimension=null;AngleDimension=false;DimensionFirst=PointText(d.PointOnFirstMm);DimensionSecond=PointText(d.PointOnSecondMm);
    }
    [RelayCommand] private void NewDimension()=>SelectedDimension=null;
    [RelayCommand] private async Task SaveDimensionAsync()
    {
        try
        {
            var a=ParsePoint(DimensionFirst);var b=ParsePoint(DimensionSecond);var c=ParsePoint(DimensionThird);
            var ray=(a-b).Normalized();var helper=Math.Abs(ray.Z)<.9?Vector3d.UnitZ:new Vector3d(1,0,0);
            var normal=AngleDimension?ray.Cross((c-b).Normalized()).Normalized():ray.Cross(helper).Normalized();
            var dimension=new EngineeringDimension(SelectedDimension?.Id??Guid.NewGuid(),DimensionName,
                AngleDimension?EngineeringDimensionKind.Angle:EngineeringDimensionKind.Length,a,b,c,normal,DimensionFlyout,
                SelectedDimension?.Argb??0xFFFFC247,SelectedDimension?.IsVisible??true);
            await ReviewEditAsync(new EditDimensionCommand(dimension));SelectedDimension=DimensionRows.FirstOrDefault(x=>x.Id==dimension.Id);
        }
        catch(Exception ex){ReviewStatus=ex.Message;document.Report(ex);}
    }
    [RelayCommand] private async Task DeleteDimensionAsync(){if(SelectedDimension is {} d)await ReviewEditAsync(new EditDimensionCommand(d,true));}
    [RelayCommand] private async Task ToggleDimensionAsync(){if(SelectedDimension is {} d)await ReviewEditAsync(new EditDimensionCommand(d with{IsVisible=!d.IsVisible}));}
}
