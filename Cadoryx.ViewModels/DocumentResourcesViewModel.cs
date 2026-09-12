using System.Collections.ObjectModel;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public interface IDocumentResourcesDialogService { Task ShowAsync(DocumentResourcesViewModel model); }

/// <summary>Applies explicit, undoable edits to the document that opened the window.</summary>
public partial class DocumentResourcesViewModel : ObservableObject, IDisposable
{
    private readonly CadDocumentViewModel document;
    public string DocumentName => document.Session.Snapshot.Name;
    public ObservableCollection<PartDefinition> Parts {get;}=[];
    public ObservableCollection<CadLayer> Layers {get;}=[];
    public ObservableCollection<CadMaterial> Materials {get;}=[];
    [ObservableProperty] private PartDefinition? selectedPart;
    [ObservableProperty] private CadLayer? selectedLayer;
    [ObservableProperty] private CadMaterial? selectedMaterial;
    [ObservableProperty] private string partName="";
    [ObservableProperty] private string layerName="";
    [ObservableProperty] private uint layerArgb=0xFF86ACC5;
    [ObservableProperty] private bool layerVisible=true;
    [ObservableProperty] private bool layerLocked;
    [ObservableProperty] private string materialName="";
    [ObservableProperty] private double densityKgPerM3=7850;
    [ObservableProperty] private string status="";
    [ObservableProperty] private bool isBusy;
    public bool CanEdit=>!IsBusy&&!document.IsReadOnly&&!document.IsClosingRequested&&!document.Session.IsClosing;
    public int LayerReferences=>SelectedLayer is {} layer?ResourceCommands.LayerReferences(document.Session.Snapshot,layer.Id):0;
    public int MaterialReferences=>SelectedMaterial is {} material?ResourceCommands.MaterialReferences(document.Session.Snapshot,material.Id):0;
    public DocumentResourcesViewModel(CadDocumentViewModel document)
    {
        this.document=document;document.SceneChanged+=OnScene;document.PropertyChanged+=OnDocumentProperty;Refresh();
    }
    private void OnScene(object? sender,EventArgs e)=>Refresh();
    private void OnDocumentProperty(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
    {if(e.PropertyName is nameof(CadDocumentViewModel.IsClosingRequested) or nameof(CadDocumentViewModel.IsReadOnly))OnPropertyChanged(nameof(CanEdit));}
    partial void OnIsBusyChanged(bool value)=>OnPropertyChanged(nameof(CanEdit));
    partial void OnSelectedPartChanged(PartDefinition? value){PartName=value?.Name??"";}
    partial void OnSelectedLayerChanged(CadLayer? value)
    {LayerName=value?.Name??"";LayerArgb=value?.Argb??0xFF86ACC5;LayerVisible=value?.IsVisible??true;LayerLocked=value?.IsLocked??false;OnPropertyChanged(nameof(LayerReferences));}
    partial void OnSelectedMaterialChanged(CadMaterial? value)
    {MaterialName=value?.Name??"";DensityKgPerM3=value is null?7850:value.DensityKgPerMm3*1e9;OnPropertyChanged(nameof(MaterialReferences));}
    private void Refresh()
    {
        var part=SelectedPart?.Id;var layer=SelectedLayer?.Id;var material=SelectedMaterial?.Id;
        var snapshot=document.Session.Snapshot;
        Parts.Clear();foreach(var value in snapshot.Definitions.Values.OfType<PartDefinition>().OrderBy(p=>p.Name).ThenBy(p=>p.Id.Value))Parts.Add(value);
        Layers.Clear();foreach(var value in snapshot.Layers.Values.OrderBy(l=>l.Name))Layers.Add(value);
        Materials.Clear();foreach(var value in snapshot.Materials.Values.OrderBy(m=>m.Name))Materials.Add(value);
        SelectedPart=Parts.FirstOrDefault(p=>p.Id==part)??Parts.FirstOrDefault();
        SelectedLayer=Layers.FirstOrDefault(l=>l.Id==layer)??Layers.FirstOrDefault();
        SelectedMaterial=Materials.FirstOrDefault(m=>m.Id==material)??Materials.FirstOrDefault();
        OnPropertyChanged(nameof(DocumentName));OnPropertyChanged(nameof(LayerReferences));OnPropertyChanged(nameof(MaterialReferences));
    }
    private async Task<bool> RunAsync(ICadDocumentCommand command)
    {
        if(!CanEdit)return false;IsBusy=true;
        try{await document.Session.ExecuteAsync(command);Status=Strings.ResourceEditApplied;return true;}
        catch(Exception ex){Status=ex.Message;document.Report(ex);return false;}
        finally{IsBusy=false;}
    }
    [RelayCommand] private async Task AddPartAsync()
    {
        var id=DefinitionId.New();if(await RunAsync(ResourceCommands.AddPart(id,PartName.Trim())))
        {SelectedPart=Parts.First(p=>p.Id==id);document.SelectedTargetPart=id;}
    }
    [RelayCommand] private async Task RenamePartAsync()
    {if(SelectedPart is {} part)await RunAsync(ResourceCommands.RenamePart(part.Id,PartName.Trim()));}
    [RelayCommand] private void UsePart(){if(SelectedPart is {} part)document.SelectedTargetPart=part.Id;}
    [RelayCommand] private async Task AddLayerAsync()
    {
        var value=new CadLayer(LayerId.New(),LayerName.Trim(),LayerArgb,LayerVisible,LayerLocked);
        if(await RunAsync(ResourceCommands.AddLayer(value)))SelectedLayer=Layers.First(l=>l.Id==value.Id);
    }
    [RelayCommand] private async Task ApplyLayerAsync()
    {if(SelectedLayer is {} layer)await RunAsync(ResourceCommands.UpdateLayer(new(layer.Id,LayerName.Trim(),LayerArgb,LayerVisible,LayerLocked)));}
    [RelayCommand] private async Task DeleteLayerAsync()
    {if(SelectedLayer is {} layer)await RunAsync(ResourceCommands.DeleteLayer(layer.Id));}
    [RelayCommand] private async Task AddMaterialAsync()
    {
        var value=new CadMaterial(MaterialId.New(),MaterialName.Trim(),DensityKgPerM3/1e9);
        if(await RunAsync(ResourceCommands.AddMaterial(value)))SelectedMaterial=Materials.First(m=>m.Id==value.Id);
    }
    [RelayCommand] private async Task ApplyMaterialAsync()
    {if(SelectedMaterial is {} material)await RunAsync(ResourceCommands.UpdateMaterial(new(material.Id,MaterialName.Trim(),DensityKgPerM3/1e9)));}
    [RelayCommand] private async Task DeleteMaterialAsync()
    {if(SelectedMaterial is {} material)await RunAsync(ResourceCommands.DeleteMaterial(material.Id));}
    public void Dispose(){document.SceneChanged-=OnScene;document.PropertyChanged-=OnDocumentProperty;}
}
