using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvalonDock.Core;
using Cadoryx.Db;
using Cadoryx.Commands;
using Cadoryx.Editor;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels.Services.Platform;
namespace Cadoryx.ViewModels.Toolboxes;
public partial class PropertiesToolboxViewModel:CadToolboxViewModelBase
{
    private CadDocumentViewModel? document;
    private SelectionTarget? target;
    [ObservableProperty] private string selectionSummary=Strings.NoSelection;
    [ObservableProperty] private string objectName="";
    [ObservableProperty] private uint argb=0xFF86ACC5;
    [ObservableProperty] private bool isBodyVisible;
    [ObservableProperty] private bool byLayer;
    [ObservableProperty] private bool hasSelection;
    [ObservableProperty] private MaterialId? selectedMaterial;
    [ObservableProperty] private LayerId? selectedLayer;
    public InstancePlacementViewModel? Instance=>document?.Placement;
    public AssemblyConstraintsViewModel? AssemblyRelations=>document?.AssemblyConstraints;
    public DocumentReviewViewModel? Review=>document?.Review;
    public ObservableCollection<CadMaterial> Materials {get;}=[];
    public ObservableCollection<CadLayer> Layers {get;}=[];
    public ObservableCollection<PropertyRowViewModel> Properties {get;}=[];
    public ObservableCollection<PropertyRowViewModel> Measurements {get;}=[];
    public void RefreshLanguage()=>Refresh(this,EventArgs.Empty);
    public PropertiesToolboxViewModel(IToolboxIconProvider iconProvider):base("toolbox.properties",Strings.Properties,DockZone.RightTop,"",true)
    {
        ArgumentNullException.ThrowIfNull(iconProvider);
        Icon=iconProvider.Properties;
    }
    public void Bind(CadDocumentViewModel? value)
    {
        if(document is not null){document.Selection.Changed-=Refresh;document.SceneChanged-=Refresh;}
        document=value;
        OnPropertyChanged(nameof(Instance));
        OnPropertyChanged(nameof(AssemblyRelations));
        OnPropertyChanged(nameof(Review));
        if(document is not null){document.Selection.Changed+=Refresh;document.SceneChanged+=Refresh;}
        Refresh(this,EventArgs.Empty);
    }
    private void Refresh(object? sender,EventArgs e)
    {
        target=document?.Selection.Items.FirstOrDefault();Properties.Clear();Measurements.Clear();Materials.Clear();Layers.Clear();
        HasSelection=target is not null&&document is not null;
        if(!HasSelection)
        {
            SelectionSummary=document?.Selection.Occurrence is {} path?OccurrencePlacement.Resolve(document.Session.Snapshot,path).Slot.Name:Strings.NoSelection;return;
        }
        var snapshot=document!.Session.Snapshot;var body=snapshot.Bodies[target!.BodyId];
        RefreshMeasurements(snapshot);
        SelectionSummary=string.Format(Strings.SelectionSummaryFormat,document.Selection.Items.Length,BodyKindText(body.Geometry.Kind));
        ObjectName=body.Name;Argb=body.Appearance.Argb;ByLayer=body.Appearance.ByLayer;IsBodyVisible=body.IsVisible;
        foreach(var m in snapshot.Materials.Values)Materials.Add(m);SelectedMaterial=body.MaterialId;
        foreach(var l in snapshot.Layers.Values)Layers.Add(l);SelectedLayer=body.LayerId;
        Properties.Add(new(Strings.SharedPartEditingHint,snapshot.Definitions[body.PartId].Name));
        Properties.Add(new(Strings.Volume,body.Geometry.VolumeMm3.ToString("G8")));
        if(body.MaterialId is {} material)Properties.Add(new(Strings.MassKg,(body.Geometry.VolumeMm3*snapshot.Materials[material].DensityKgPerMm3).ToString("G8")));
        Properties.Add(new(Strings.BoundsMinimum,body.Geometry.Bounds.Min.ToString()));
        Properties.Add(new(Strings.BoundsMaximum,body.Geometry.Bounds.Max.ToString()));
        Properties.Add(new(Strings.Layer,snapshot.Layers[body.LayerId].Name));
        Properties.Add(new(Strings.InstancePath,target.Path.ToString()));
    }
    private void RefreshMeasurements(DocumentSnapshot snapshot)
    {
        try
        {
            if(SelectionMeasurement.Measure(snapshot,document!.Selection.Items) is not {} m)return;
            var settings=snapshot.Settings;var factor=DocumentSettings.MillimetersPerUnit(settings.DisplayUnit);
            var unit=settings.DisplayUnit switch{LengthUnit.Centimeter=>"cm",LengthUnit.Meter=>"m",LengthUnit.Inch=>"in",_=>"mm"};
            string Number(double value)=>value.ToString("F"+settings.DecimalPlaces);
            string Point(Vector3d p)=>$"({Number(p.X/factor)}, {Number(p.Y/factor)}, {Number(p.Z/factor)}) {unit}";
            string R(string key)=>Strings.ResourceManager.GetString(key,Strings.Culture)!;
            Measurements.Add(new(R("MeasuredInstances"),m.BodyInstances.ToString()));
            Measurements.Add(new(R("WorldEnvelopeMin"),Point(m.WorldEnvelope.Min)));
            Measurements.Add(new(R("WorldEnvelopeMax"),Point(m.WorldEnvelope.Max)));
            Measurements.Add(new(R("WorldEnvelopeSize"),Point(m.WorldEnvelope.Max-m.WorldEnvelope.Min)));
            Measurements.Add(new(R("SelectionVolumeSum"),$"{Number(m.VolumeSumMm3/(factor*factor*factor))} {unit}³ ({m.VolumetricInstances}/{m.BodyInstances})"));
            Measurements.Add(new(R("SelectionMassSum"),m.MassSumKg is {} mass?Number(mass)+" kg":R("MeasurementMassUnavailable")));
            if(m.EnvelopeCenterDistanceMm is {} distance)
                Measurements.Add(new(R("EnvelopeCenterDistance"),Number(distance/factor)+" "+unit));
        }
        catch(CadValidationException ex){Measurements.Add(new(Strings.ResourceManager.GetString("SelectionMeasurements",Strings.Culture)!,ex.Message));}
    }
    [RelayCommand] private async Task ApplyAsync()
    {
        if(document is not {} doc||target is not {} selection)return;
        if(doc.IsClosingRequested)return;
        string name=ObjectName;uint color=Argb;bool visible=IsBodyVisible,byLayer=ByLayer;var material=SelectedMaterial;var layer=SelectedLayer;
        try
        {
            if(layer is null)throw new CadValidationException(Strings.SelectLayer);
            var previous=doc.Session.Snapshot.Bodies[selection.BodyId].Appearance;
            var appearance=previous.Argb==color&&previous.ByLayer==byLayer?previous:new CadAppearance(color,byLayer);
            await doc.Session.ExecuteAsync(DocumentEdits.SetBodyProperties(selection.BodyId,name,appearance,visible,layer.Value,material));
        }
        catch(Exception ex){doc.Report(ex);}
    }
    [RelayCommand] private void ClearMaterial()=>SelectedMaterial=null;
    [RelayCommand] private void EditFeature()
    {if(document is {} doc&&target is {} t&&doc.Session.Snapshot.Bodies[t.BodyId].Producer is {} id)doc.EditFeature(id);}
    private static string BodyKindText(BodyKind kind)=>kind switch
    {
        BodyKind.Solid=>Strings.Solid,
        BodyKind.Sheet=>Strings.Sheet,
        BodyKind.Wire=>Strings.Wire,
        BodyKind.Compound=>Strings.Compound,
        BodyKind.Mesh=>Strings.Mesh,
        BodyKind.Empty=>Strings.Empty,
        _=>Strings.Empty
    };
}
public sealed record PropertyRowViewModel(string Name,string Value);
