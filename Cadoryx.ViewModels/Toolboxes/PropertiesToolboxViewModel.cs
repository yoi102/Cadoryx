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
    public ObservableCollection<CadMaterial> Materials {get;}=[];
    public ObservableCollection<PropertyRowViewModel> Properties {get;}=[];
    public PropertiesToolboxViewModel(IToolboxIconProvider iconProvider):base("toolbox.properties",Strings.Properties,DockZone.RightTop,"",true)
    {
        ArgumentNullException.ThrowIfNull(iconProvider);
        Icon=iconProvider.Properties;
    }
    public void Bind(CadDocumentViewModel? value)
    {
        if(document is not null){document.Selection.Changed-=Refresh;document.SceneChanged-=Refresh;}
        document=value;
        if(document is not null){document.Selection.Changed+=Refresh;document.SceneChanged+=Refresh;}
        Refresh(this,EventArgs.Empty);
    }
    private void Refresh(object? sender,EventArgs e)
    {
        target=document?.Selection.Items.FirstOrDefault();Properties.Clear();Materials.Clear();
        HasSelection=target is not null&&document is not null;
        if(!HasSelection){SelectionSummary=Strings.NoSelection;return;}
        var snapshot=document!.Session.Snapshot;var body=snapshot.Bodies[target!.BodyId];
        SelectionSummary=string.Format(Strings.SelectionSummaryFormat,document.Selection.Items.Length,BodyKindText(body.Geometry.Kind));
        ObjectName=body.Name;Argb=body.Appearance.Argb;ByLayer=body.Appearance.ByLayer;IsBodyVisible=body.IsVisible;
        foreach(var m in snapshot.Materials.Values)Materials.Add(m);SelectedMaterial=body.MaterialId;
        Properties.Add(new(Strings.Volume,body.Geometry.VolumeMm3.ToString("G8")));
        Properties.Add(new(Strings.BoundsMinimum,body.Geometry.Bounds.Min.ToString()));
        Properties.Add(new(Strings.BoundsMaximum,body.Geometry.Bounds.Max.ToString()));
        Properties.Add(new(Strings.Layer,snapshot.Layers[body.LayerId].Name));
        Properties.Add(new(Strings.InstancePath,target.Path.ToString()));
    }
    [RelayCommand] private async Task ApplyAsync()
    {
        if(document is not {} doc||target is not {} selection)return;
        if(doc.IsClosingRequested)return;
        string name=ObjectName;uint color=Argb;bool visible=IsBodyVisible,layer=ByLayer;var material=SelectedMaterial;
        try
        {
            await doc.Session.ExecuteAsync(new EditDocumentCommand(Strings.EditEntityProperties,snapshot=>
            {
                CadGuard.Name(name);var body=snapshot.Bodies[selection.BodyId];
                if(snapshot.Layers[body.LayerId].IsLocked)throw new CadValidationException(Strings.LayerLocked);
                return snapshot with {Bodies=snapshot.Bodies.SetItem(body.Id,body with{Name=name,Appearance=new(color,layer),IsVisible=visible,MaterialId=material})};
            }));
        }
        catch(Exception ex){doc.Report(ex);}
    }
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
