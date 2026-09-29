using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cadoryx.ViewModels.Settings;

public interface IDocumentSettingsHost
{
    Task ShowAsync(CadDocumentViewModel document);
}

public sealed record DocumentUnitOption(LengthUnit Unit,string Label);
public sealed record DocumentOriginStyleOption(DocumentOriginStyle Style,string Label);
public sealed record DocumentWorkPlaneOption(DocumentWorkPlaneKind Kind,string Label);
public sealed record DocumentSettingsSection(string Id,string Title);

public sealed record DocumentSettingsLabels(string Title,string Display,string Grid,string Origin,string Units,
    string Background,string BackgroundTop,string BackgroundBottom,string GridVisible,string GridSpacing,string GridSnap,string DisplayUnit,
    string DecimalPlaces,string OriginVisible,string OriginStyle,string OriginSize,string OriginHint,
    string Reset,string Ok,string Apply,string Cancel,string WorkPlane,string WorkPlaneOffset,
    string WorkPlaneOrigin,string WorkPlaneAngles);

public partial class DocumentSettingsViewModel : ObservableObject
{
    private readonly CadDocumentViewModel document;
    public DocumentSettingsLabels Labels {get;}=new(
        R("DocumentSettingsTitle","Document settings"),Strings.Viewport,
        R("DocumentGridSection","Grid and snapping"),R("DocumentOriginSection","Origin and axes"),R("DocumentUnitsSection","Units and precision"),
        Strings.BackgroundColor,R("BackgroundTop","Sky top"),R("BackgroundBottom","Sky bottom"),
        R("GridVisible","Show work grid"),R("GridSpacing","Grid spacing (mm)"),
        R("SnapToGrid","Snap creation points to grid"),R("DocumentDisplayUnit","Display unit"),
        R("DocumentDecimalPlaces","Decimal places"),R("DocumentOriginVisible","Show model origin"),
        R("DocumentOriginStyle","Style"),R("DocumentOriginSize","Size (mm)"),
        R("DocumentOriginHint","Fixed at world (0, 0, 0); X red, Y green, Z blue."),
        Strings.Reset,Strings.Ok,Strings.Apply,Strings.Cancel,
        R("DocumentWorkPlane","Work plane"),R("DocumentWorkPlaneOffset","Plane offset (mm)"),
        R("WorkPlaneOrigin","Plane origin (mm)"),R("WorkPlaneAngles","Rotation X / Y / Z (degrees)"));
    public IReadOnlyList<DocumentWorkPlaneOption> WorkPlaneOptions {get;}=
    [new(DocumentWorkPlaneKind.XY,"XY"),new(DocumentWorkPlaneKind.XZ,"XZ"),new(DocumentWorkPlaneKind.YZ,"YZ"),
     new(DocumentWorkPlaneKind.Custom,R("DocumentCustomPlane","Custom"))];
    public IReadOnlyList<DocumentUnitOption> UnitOptions {get;}=
    [new(LengthUnit.Millimeter,"mm"),new(LengthUnit.Centimeter,"cm"),new(LengthUnit.Meter,"m"),new(LengthUnit.Inch,"in")];
    public IReadOnlyList<DocumentOriginStyleOption> OriginStyleOptions {get;}=
    [new(DocumentOriginStyle.ColorAxes,R("DocumentOriginColorAxes","Colored XYZ axes")),
     new(DocumentOriginStyle.SubtleAxes,R("DocumentOriginSubtleAxes","Subtle single-color axes")),
     new(DocumentOriginStyle.OriginMarker,R("DocumentOriginMarker","Origin marker"))];
    public IReadOnlyList<DocumentSettingsSection> Sections {get;}
    [ObservableProperty] public partial DocumentSettingsSection? SelectedSection {get;set;}

    [ObservableProperty] public partial uint BackgroundTopArgb {get;set;}
    [ObservableProperty] public partial uint BackgroundBottomArgb {get;set;}
    [ObservableProperty] public partial bool GridVisible {get;set;}
    [ObservableProperty] public partial double GridSpacingMm {get;set;}
    [ObservableProperty] public partial bool GridSnap {get;set;}
    [ObservableProperty] public partial DocumentWorkPlaneOption? SelectedWorkPlane {get;set;}
    public bool IsCustomWorkPlane=>SelectedWorkPlane?.Kind==DocumentWorkPlaneKind.Custom;
    partial void OnSelectedWorkPlaneChanged(DocumentWorkPlaneOption? value)=>OnPropertyChanged(nameof(IsCustomWorkPlane));
    [ObservableProperty] public partial double WorkPlaneOffsetMm {get;set;}
    [ObservableProperty] public partial double WorkPlaneOriginX {get;set;}
    [ObservableProperty] public partial double WorkPlaneOriginY {get;set;}
    [ObservableProperty] public partial double WorkPlaneOriginZ {get;set;}
    [ObservableProperty] public partial double WorkPlaneAngleX {get;set;}
    [ObservableProperty] public partial double WorkPlaneAngleY {get;set;}
    [ObservableProperty] public partial double WorkPlaneAngleZ {get;set;}
    [ObservableProperty] public partial bool OriginVisible {get;set;}
    [ObservableProperty] public partial DocumentOriginStyleOption? SelectedOriginStyle {get;set;}
    [ObservableProperty] public partial double OriginSizeMm {get;set;}
    [ObservableProperty] public partial DocumentUnitOption? SelectedUnit {get;set;}
    [ObservableProperty] public partial int DecimalPlaces {get;set;}
    [ObservableProperty] public partial string? ValidationError {get;private set;}
    [ObservableProperty] public partial bool IsApplying {get;private set;}

    public DocumentSettingsViewModel(CadDocumentViewModel document)
    {
        this.document=document;
        Sections=[new("Display",Labels.Display),new("Grid",Labels.Grid),new("Origin",Labels.Origin),new("Units",Labels.Units)];
        SelectedSection=Sections[0];
        Load(document.Session.Snapshot.Settings);
    }

    public void ResetToDefaults()=>Load(new DocumentSettings());

    public async Task<bool> TryApplyAsync()
    {
        if(IsApplying)return false;
        IsApplying=true;
        try
        {
            var current=document.Session.Snapshot.Settings;
            var settings=current with
            {
                DisplayUnit=SelectedUnit?.Unit??throw new CadValidationException("Select a display unit."),
                DecimalPlaces=DecimalPlaces,
                BackgroundTopArgb=BackgroundTopArgb,
                BackgroundBottomArgb=BackgroundBottomArgb,
                Grid=new(GridVisible,GridSpacingMm,GridSnap),
                WorkPlane=new(SelectedWorkPlane?.Kind??throw new CadValidationException("Select a work plane."),WorkPlaneOffsetMm)
                {CustomOrigin=new(WorkPlaneOriginX,WorkPlaneOriginY,WorkPlaneOriginZ),
                 CustomRotation=Quaterniond.FromEulerDegrees(WorkPlaneAngleX,WorkPlaneAngleY,WorkPlaneAngleZ)},
                Origin=new(OriginVisible,SelectedOriginStyle?.Style??throw new CadValidationException("Select an origin style."),OriginSizeMm)
            };
            settings.Validate();
            await document.Session.ExecuteAsync(DocumentEdits.SetSettings(settings));
            ValidationError=null;
            return true;
        }
        catch(Exception ex)
        {
            ValidationError=ex.Message;
            return false;
        }
        finally {IsApplying=false;}
    }

    private void Load(DocumentSettings settings)
    {
        BackgroundTopArgb=settings.BackgroundTopArgb;
        BackgroundBottomArgb=settings.BackgroundBottomArgb;
        GridVisible=settings.Grid.Visible;
        GridSpacingMm=settings.Grid.SpacingMm;
        GridSnap=settings.Grid.Snap;
        SelectedWorkPlane=WorkPlaneOptions.Single(option=>option.Kind==settings.WorkPlane.Kind);
        WorkPlaneOffsetMm=settings.WorkPlane.OffsetMm;
        WorkPlaneOriginX=settings.WorkPlane.CustomOrigin.X;
        WorkPlaneOriginY=settings.WorkPlane.CustomOrigin.Y;
        WorkPlaneOriginZ=settings.WorkPlane.CustomOrigin.Z;
        var angles=settings.WorkPlane.CustomRotation.ToEulerDegrees();
        WorkPlaneAngleX=angles.X;WorkPlaneAngleY=angles.Y;WorkPlaneAngleZ=angles.Z;
        OriginVisible=settings.Origin.Visible;
        SelectedOriginStyle=OriginStyleOptions.Single(option=>option.Style==settings.Origin.Style);
        OriginSizeMm=settings.Origin.SizeMm;
        SelectedUnit=UnitOptions.Single(option=>option.Unit==settings.DisplayUnit);
        DecimalPlaces=settings.DecimalPlaces;
        ValidationError=null;
    }

    private static string R(string key,string fallback)=>Strings.ResourceManager.GetString(key)??fallback;
}
