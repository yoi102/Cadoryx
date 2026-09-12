using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvalonDock.Core;
using Cadoryx.Db;
using Cadoryx.Commands;
using Cadoryx.Editor;
namespace Cadoryx.ViewModels.Toolboxes;
public partial class PropertiesToolboxViewModel:CadToolboxViewModelBase
{
    private CadDocumentViewModel? document;
    private SelectionTarget? target;
    [ObservableProperty] private string selectionSummary="未选择对象";
    [ObservableProperty] private string objectName="";
    [ObservableProperty] private uint argb=0xFF86ACC5;
    [ObservableProperty] private bool isBodyVisible;
    [ObservableProperty] private bool byLayer;
    [ObservableProperty] private bool hasSelection;
    [ObservableProperty] private MaterialId? selectedMaterial;
    public ObservableCollection<CadMaterial> Materials {get;}=[];
    public ObservableCollection<PropertyRowViewModel> Properties {get;}=[];
    public PropertiesToolboxViewModel():base("toolbox.properties","属性",DockZone.RightTop,"◈",true){}
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
        if(!HasSelection){SelectionSummary="未选择对象";return;}
        var snapshot=document!.Session.Snapshot;var body=snapshot.Bodies[target!.BodyId];
        SelectionSummary=$"{document.Selection.Items.Length} 个选择 · {body.Geometry.Kind}";
        ObjectName=body.Name;Argb=body.Appearance.Argb;ByLayer=body.Appearance.ByLayer;IsBodyVisible=body.IsVisible;
        foreach(var m in snapshot.Materials.Values)Materials.Add(m);SelectedMaterial=body.MaterialId;
        Properties.Add(new("体积 (mm³)",body.Geometry.VolumeMm3.ToString("G8")));
        Properties.Add(new("包围盒最小值",body.Geometry.Bounds.Min.ToString()));
        Properties.Add(new("包围盒最大值",body.Geometry.Bounds.Max.ToString()));
        Properties.Add(new("图层",snapshot.Layers[body.LayerId].Name));
        Properties.Add(new("实例路径",target.Path.ToString()));
    }
    [RelayCommand] private async Task ApplyAsync()
    {
        if(document is not {} doc||target is not {} selection)return;
        if(doc.IsClosingRequested)return;
        string name=ObjectName;uint color=Argb;bool visible=IsBodyVisible,layer=ByLayer;var material=SelectedMaterial;
        try
        {
            await doc.Session.ExecuteAsync(new EditDocumentCommand("修改实体属性",snapshot=>
            {
                CadGuard.Name(name);var body=snapshot.Bodies[selection.BodyId];
                if(snapshot.Layers[body.LayerId].IsLocked)throw new CadValidationException("图层已锁定。");
                return snapshot with {Bodies=snapshot.Bodies.SetItem(body.Id,body with{Name=name,Appearance=new(color,layer),IsVisible=visible,MaterialId=material})};
            }));
        }
        catch(Exception ex){doc.Report(ex);}
    }
    [RelayCommand] private void EditFeature()
    {if(document is {} doc&&target is {} t&&doc.Session.Snapshot.Bodies[t.BodyId].Producer is {} id)doc.EditFeature(id);}
}
public sealed record PropertyRowViewModel(string Name,string Value);
