using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvalonDock.Core;
using Cadoryx.Db;
using Cadoryx.Editor;
namespace Cadoryx.ViewModels.Toolboxes;
public partial class ModelTreeToolboxViewModel : CadToolboxViewModelBase
{
    private CadDocumentViewModel? document;
    private bool syncing;
    public ObservableCollection<ModelTreeItemViewModel> Items {get;}=[];
    public ModelTreeToolboxViewModel():base("toolbox.model-tree","模型树",DockZone.LeftTop,"▦",true){}
    public void Bind(CadDocumentViewModel? value)
    {
        if(document is not null){document.SceneChanged-=OnScene;document.Selection.Changed-=OnSelection;}
        document=value;
        if(document is not null){document.SceneChanged+=OnScene;document.Selection.Changed+=OnSelection;}
        Refresh();
    }
    private void OnScene(object? sender,EventArgs e)=>Refresh();
    [RelayCommand] private void Refresh()
    {
        Items.Clear();if(document is null)return;
        var snapshot=document.Session.Snapshot;var nodes=new Dictionary<OccurrencePath,ModelTreeItemViewModel>();
        foreach(var occurrence in snapshot.EnumerateOccurrences())
        {
            var definition=snapshot.Definitions[occurrence.DefinitionId];
            var node=new ModelTreeItemViewModel(occurrence.Name,definition is PartDefinition?"零件":"装配");nodes.Add(occurrence.Path,node);
            var parent=occurrence.Path.Slots.Length==0?null:new OccurrencePath(snapshot.Id,occurrence.Path.Slots.RemoveAt(occurrence.Path.Slots.Length-1));
            if(parent is not null&&nodes.TryGetValue(parent,out var p))p.Children.Add(node);else Items.Add(node);
            if(definition is not PartDefinition part)continue;
            foreach(var id in part.Bodies)
            {
                var body=snapshot.Bodies[id];var target=new SelectionTarget(occurrence.Path,id,body.Geometry.Revision);
                var child=new ModelTreeItemViewModel(body.Name,body.Geometry.Kind.ToString(),target);
                child.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(child.IsChecked)&&!syncing)Toggle(child);};node.Children.Add(child);
            }
            var history=new ModelTreeItemViewModel("特征历史","History");
            foreach(var fid in part.Features){var f=snapshot.Features[fid];history.Children.Add(new(f.Name,f.Recipe.GetType().Name,null,fid));}
            node.Children.Add(history);
        }
        OnSelection(this,EventArgs.Empty);
    }
    private void Toggle(ModelTreeItemViewModel item)
    {
        if(document is null||item.Target is not {} target)return;
        document.Selection.Replace(item.IsChecked?document.Selection.Items.Add(target):document.Selection.Items.Remove(target));
    }
    public void Select(ModelTreeItemViewModel item)
    {
        if(document is null)return;
        if(item.Target is {} target)document.Selection.Replace([target]);
        else if(item.Feature is {} id)document.EditFeature(id);
    }
    private void OnSelection(object? sender,EventArgs e)
    {
        syncing=true;
        try{foreach(var item in All(Items))item.IsChecked=item.Target is {} t&&document?.Selection.Items.Contains(t)==true;}
        finally{syncing=false;}
    }
    private static IEnumerable<ModelTreeItemViewModel> All(IEnumerable<ModelTreeItemViewModel> items)
    {foreach(var item in items){yield return item;foreach(var child in All(item.Children))yield return child;}}
}
public partial class ModelTreeItemViewModel(string name,string kind,SelectionTarget? target=null,FeatureId? feature=null):ObservableObject
{
    public string Name {get;}=name;
    public string Kind {get;}=kind;
    public SelectionTarget? Target {get;}=target;
    public FeatureId? Feature {get;}=feature;
    public bool CanCheck=>Target is not null;
    public ObservableCollection<ModelTreeItemViewModel> Children {get;}=[];
    [ObservableProperty] private bool isExpanded=true;
    [ObservableProperty] private bool isChecked;
}
