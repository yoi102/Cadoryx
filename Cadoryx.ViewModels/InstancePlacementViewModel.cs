using System.Collections.ObjectModel;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public partial class InstancePlacementViewModel : ObservableObject, IDisposable
{
    private readonly CadDocumentViewModel document;
    private OccurrencePath? path;
    private Quaterniond originalRotation=Quaterniond.Identity;
    private bool refreshing,rotationEdited;
    [ObservableProperty] private bool hasOccurrence;
    [ObservableProperty] private bool canMove;
    [ObservableProperty] private bool canInsertChild;
    [ObservableProperty] private bool canMakeIndependent;
    [ObservableProperty] private bool canMakePartIndependent;
    [ObservableProperty] private bool canRenameDefinition;
    [ObservableProperty] private bool canPruneUnused;
    [ObservableProperty] private bool canLinkExternal;
    [ObservableProperty] private bool hasExternalLink;
    [ObservableProperty] private string externalSourcePath="";
    [ObservableProperty] private string externalStatus="";
    [ObservableProperty] private ExternalPartChoice? selectedExternalPart;
    [ObservableProperty] private string newAssemblyName=Strings.Assembly;
    [ObservableProperty] private InstanceDefinitionChoice? selectedDefinition;
    [ObservableProperty] private AssemblyParentChoice? selectedParent;
    [ObservableProperty] private string instanceName="";
    [ObservableProperty] private string definitionName="";
    [ObservableProperty] private string definitionScope="";
    [ObservableProperty] private string context="";
    [ObservableProperty] private double x;
    [ObservableProperty] private double y;
    [ObservableProperty] private double z;
    [ObservableProperty] private double axisX;
    [ObservableProperty] private double axisY;
    [ObservableProperty] private double axisZ=1;
    [ObservableProperty] private double angleDegrees;
    [ObservableProperty] private bool isBusy;
    public ObservableCollection<InstanceDefinitionChoice> Definitions {get;}=[];
    public ObservableCollection<AssemblyParentChoice> Parents {get;}=[];
    public ObservableCollection<ExternalPartChoice> ExternalSourceParts {get;}=[];
    public string InsertLabel=>Label("InsertInstance","Insert instance");
    public string ReplaceLabel=>Label("ReplaceInstance","Replace instance");
    public string RemoveLabel=>Label("RemoveInstance","Remove instance");
    public string ReparentLabel=>Label("ReparentInstance","Move to assembly");
    public string MakeIndependentLabel=>Label("MakeAssemblyIndependent","Make assembly independent");
    public string DefinitionLabel=>Label("InstanceDefinition","Definition");
    public string DestinationLabel=>Label("DestinationAssembly","Destination assembly");
    public string CreateAssemblyLabel=>Label("CreateSiblingAssembly","Create assembly beside selection");
    public string NewAssemblyNameLabel=>Label("NewAssemblyName","New assembly name");
    public string MakePartIndependentLabel=>Label("MakePartIndependent","Make this part independent");
    public string InstanceNameLabel=>Label("OccurrenceName","Instance name (this placement)");
    public string DefinitionNameLabel=>Label("SharedDefinitionName","Definition name (all instances)");
    public string RenameOccurrenceLabel=>Label("RenameOccurrence","Rename instance");
    public string RenameDefinitionLabel=>Label("RenameDefinition","Rename definition");
    public string PruneUnusedLabel=>Label("PruneUnusedDefinitions","Remove unused definitions");
    public string ExternalSourceLabel=>Label("ExternalPartSource","External .cadoryx source");
    public string ExternalInspectLabel=>Label("ExternalPartInspect","Inspect source");
    public string ExternalBrowseLabel=>Label("ExternalPartBrowse","Browse…");
    public string ExternalLinkLabel=>Label("ExternalPartLink","Link selected source part");
    public string ExternalCheckLabel=>Label("ExternalPartCheck","Check source version");
    public string ExternalRefreshLabel=>Label("ExternalPartRefresh","Refresh from source");
    public string ExternalDetachLabel=>Label("ExternalPartDetach","Detach link");
    private static string Label(string key,string fallback)=>Strings.ResourceManager.GetString(key,Strings.Culture)??fallback;
    public InstancePlacementViewModel(CadDocumentViewModel document)
    {
        this.document=document;document.Selection.Changed+=Refresh;document.SceneChanged+=Refresh;
        document.PropertyChanged+=OnDocumentProperty;PropertyChanged+=OnProperty;Refresh(this,EventArgs.Empty);
    }
    private void OnProperty(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
    {if(!refreshing&&e.PropertyName is nameof(AxisX) or nameof(AxisY) or nameof(AxisZ) or nameof(AngleDegrees))rotationEdited=true;}
    private void OnDocumentProperty(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
    {if(e.PropertyName is nameof(CadDocumentViewModel.IsClosingRequested) or nameof(CadDocumentViewModel.IsReadOnly))Refresh(sender,EventArgs.Empty);}
    private void Refresh(object? sender,EventArgs e)
    {
        refreshing=true;
        try
        {
            path=document.Selection.Occurrence;HasOccurrence=path is not null;CanMove=false;CanInsertChild=false;
            CanMakeIndependent=false;CanMakePartIndependent=false;CanRenameDefinition=false;CanPruneUnused=false;
            CanLinkExternal=false;HasExternalLink=false;
            Definitions.Clear();Parents.Clear();
            if(path is null){Context=Strings.SelectInstance;return;}
            var snapshot=document.Session.Snapshot;var resolved=OccurrencePlacement.Resolve(snapshot,path);
            InstanceName=resolved.Slot.Name;Context=resolved.CanMoveIndependently?Strings.InstanceLocalCoordinates:Strings.SharedParentMoveBlocked;
            bool editable=!document.IsReadOnly&&!document.IsClosingRequested&&!document.Session.IsClosing;
            CanMove=resolved.CanMoveIndependently&&editable;
            bool assembly=snapshot.Definitions[resolved.Slot.DefinitionId] is AssemblyDefinition;
            bool part=snapshot.Definitions[resolved.Slot.DefinitionId] is PartDefinition;
            var occurrences=snapshot.EnumerateOccurrences().ToArray();
            var counts=occurrences.GroupBy(o=>o.DefinitionId).ToDictionary(g=>g.Key,g=>g.Count());
            int count=counts[resolved.Slot.DefinitionId];
            DefinitionName=snapshot.Definitions[resolved.Slot.DefinitionId].Name;
            DefinitionScope=string.Format(Label("DefinitionUsageCount","This definition is used by {0} instance(s)."),count);
            CanInsertChild=assembly&&count==1&&editable;
            CanLinkExternal=CanInsertChild&&document.ExternalPartStorage is not null;
            CanMakeIndependent=assembly&&count>1&&editable;
            CanMakePartIndependent=part&&count>1&&editable;
            CanRenameDefinition=editable;
            CanPruneUnused=editable&&UnusedDefinitionCommands.CountRemovable(snapshot)>0;
            if(snapshot.ExternalParts.TryGetValue(resolved.Slot.DefinitionId,out var external))
            {
                HasExternalLink=true;
                ExternalStatus=Label("ExternalPartPinned","Pinned source snapshot")+" · "+external.SourceStateId;
            }
            else ExternalStatus="";
            foreach(var definition in snapshot.Definitions.Values.Where(d=>d.Id!=snapshot.RootAssemblyId).OrderBy(d=>d.Name))
                Definitions.Add(new(definition.Id,definition.Name));
            SelectedDefinition=Definitions.FirstOrDefault(d=>d.Id==resolved.Slot.DefinitionId)??Definitions.FirstOrDefault();
            Parents.Add(new(snapshot.Name+" (root)",new(snapshot.Id,[])));
            foreach(var occurrence in occurrences)
            {
                if(snapshot.Definitions[occurrence.DefinitionId] is not AssemblyDefinition)continue;
                if(occurrence.Path.Slots.Length>=path.Slots.Length&&
                   occurrence.Path.Slots.Take(path.Slots.Length).SequenceEqual(path.Slots))continue;
                if(counts[occurrence.DefinitionId]>1)continue;
                Parents.Add(new(occurrence.Name+" ("+occurrence.Path.Slots[^1].Value.ToString("N")[..8]+")",occurrence.Path));
            }
            SelectedParent=Parents.FirstOrDefault();
            var transform=resolved.Slot.LocalTransform;X=transform.Translation.X;Y=transform.Translation.Y;Z=transform.Translation.Z;
            originalRotation=transform.Rotation;double w=Math.Clamp(originalRotation.W,-1,1);double s=Math.Sqrt(Math.Max(0,1-w*w));
            AngleDegrees=2*Math.Acos(w)*180/Math.PI;
            AxisX=s<1e-10?0:originalRotation.X/s;AxisY=s<1e-10?0:originalRotation.Y/s;AxisZ=s<1e-10?1:originalRotation.Z/s;
            rotationEdited=false;
        }
        catch(CadValidationException){HasOccurrence=false;Context=Strings.SelectInstance;}
        finally{refreshing=false;}
    }
    [RelayCommand] private async Task ApplyAsync()
    {
        if(!CanMove||IsBusy||path is null)return;
        var selectedPath=path;
        try
        {
            IsBusy=true;
            var rotation=rotationEdited?Quaterniond.FromAxisAngle(new(AxisX,AxisY,AxisZ),AngleDegrees*Math.PI/180):originalRotation;
            var transform=new RigidTransform3d(new(X,Y,Z),rotation);
            await document.Session.ExecuteAsync(DocumentEdits.MoveOccurrence(selectedPath,transform));
        }
        catch(Exception ex){Context=ex.Message;document.Report(ex);}
        finally{IsBusy=false;}
    }
    [RelayCommand] private async Task InsertAsync()
    {
        if(!CanInsertChild||IsBusy||path is null||SelectedDefinition is not {} definition)return;
        var parent=path;var id=ComponentSlotId.New();
        await ExecuteAsync(AssemblyOccurrenceCommands.Insert(parent,definition.Id,id,definition.Name,RigidTransform3d.Identity),
            parent.Append(id));
    }
    [RelayCommand] private async Task CreateAssemblyAsync()
    {
        if(!CanMove||IsBusy||path is null)return;
        var parent=new OccurrencePath(path.DocumentId,path.Slots.RemoveAt(path.Slots.Length-1));
        var id=ComponentSlotId.New();
        await ExecuteAsync(AssemblyOccurrenceCommands.CreateAssembly(parent,DefinitionId.New(),id,NewAssemblyName,
            RigidTransform3d.Identity),parent.Append(id));
    }
    [RelayCommand] private async Task ReplaceAsync()
    {
        if(!CanMove||IsBusy||path is null||SelectedDefinition is not {} definition)return;
        await ExecuteAsync(AssemblyOccurrenceCommands.Replace(path,definition.Id),path);
    }
    [RelayCommand] private async Task RemoveAsync()
    {
        if(!CanMove||IsBusy||path is null)return;
        await ExecuteAsync(AssemblyOccurrenceCommands.Remove(path),null);
    }
    [RelayCommand] private async Task ReparentAsync()
    {
        if(!CanMove||IsBusy||path is null||SelectedParent is not {} target)return;
        var destination=target.Path.Append(path.Slots[^1]);
        await ExecuteAsync(AssemblyOccurrenceCommands.Reparent(path,target.Path),destination);
    }
    [RelayCommand] private async Task MakeIndependentAsync()
    {
        if(!CanMakeIndependent||IsBusy||path is null)return;
        var command=AssemblyOccurrenceCommands.MakeIndependent(path);
        try
        {
            IsBusy=true;await document.Session.ExecuteAsync(command);
            if(command.ResultPath is {} result)document.Selection.SelectOccurrence(result);
        }
        catch(Exception ex){Context=ex.Message;document.Report(ex);}
        finally{IsBusy=false;}
    }
    [RelayCommand] private async Task MakePartIndependentAsync()
    {
        if(!CanMakePartIndependent||IsBusy||path is null)return;
        var command=AssemblyOccurrenceCommands.MakePartIndependent(path);
        try
        {
            IsBusy=true;await document.Session.ExecuteAsync(command);
            if(command.ResultPath is {} result)document.Selection.SelectOccurrence(result);
            if(command.ResultPartId is {} part)document.SelectedTargetPart=part;
        }
        catch(Exception ex){Context=ex.Message;document.Report(ex);}
        finally{IsBusy=false;}
    }
    [RelayCommand] private async Task RenameOccurrenceAsync()
    {
        if(!CanMove||IsBusy||path is null)return;
        await ExecuteAsync(AssemblyOccurrenceCommands.RenameOccurrence(path,InstanceName),path);
    }
    [RelayCommand] private async Task RenameDefinitionAsync()
    {
        if(!CanRenameDefinition||IsBusy||path is null)return;
        var id=OccurrencePlacement.Resolve(document.Session.Snapshot,path).Slot.DefinitionId;
        await ExecuteAsync(AssemblyOccurrenceCommands.RenameDefinition(id,DefinitionName),path);
    }
    [RelayCommand] private async Task PruneUnusedAsync()
    {
        if(!CanPruneUnused||IsBusy)return;
        await ExecuteAsync(UnusedDefinitionCommands.Prune(),path);
    }
    [RelayCommand] private async Task BrowseExternalAsync()
    {
        var source=document.ExternalPartFiles?.OpenDocument();
        if(source is null)return;
        ExternalSourcePath=source;
        await InspectExternalAsync();
    }
    [RelayCommand] private async Task InspectExternalAsync()
    {
        if(IsBusy||document.ExternalPartStorage is not {} storage||string.IsNullOrWhiteSpace(ExternalSourcePath))return;
        try
        {
            IsBusy=true;
            using var source=await storage.LoadAsync(ExternalSourcePath,document.Session.Assets);
            ExternalSourceParts.Clear();
            foreach(var part in source.Snapshot.Definitions.Values.OfType<PartDefinition>().Where(p=>!p.Bodies.IsEmpty)
                .OrderBy(p=>p.Name))ExternalSourceParts.Add(new(part.Id,part.Name));
            SelectedExternalPart=ExternalSourceParts.Count==1?ExternalSourceParts[0]:null;
            ExternalStatus=ExternalSourceParts.Count==0?"No nonempty source part was found.":
                ExternalSourceParts.Count==1?"Source part ready.":"Select one source part.";
        }
        catch(Exception ex){ExternalStatus=ex.Message;document.Report(ex);}
        finally{IsBusy=false;}
    }
    [RelayCommand] private async Task LinkExternalAsync()
    {
        if(!CanLinkExternal||IsBusy||path is null||SelectedExternalPart is not {} source||
           document.ExternalPartStorage is not {} storage)return;
        var command=ExternalPartCommands.Link(storage,ExternalSourcePath,path,DefinitionId.New(),ComponentSlotId.New(),
            source.Name,source.Id,document.Session.FilePath);
        await ExecuteAsync(command,command.ResultPath);
    }
    [RelayCommand] private void CheckExternal()
    {
        if(!HasExternalLink||path is null)return;
        try
        {
            var id=OccurrencePlacement.Resolve(document.Session.Snapshot,path).Slot.DefinitionId;
            ExternalStatus=ExternalPartCommands.Check(document.Session.Snapshot,id,document.Session.FilePath).ToString();
        }
        catch(Exception ex){ExternalStatus=ex.Message;document.Report(ex);}
    }
    [RelayCommand] private async Task RefreshExternalAsync()
    {
        if(!HasExternalLink||IsBusy||path is null||document.ExternalPartStorage is not {} storage)return;
        var id=OccurrencePlacement.Resolve(document.Session.Snapshot,path).Slot.DefinitionId;
        await ExecuteAsync(ExternalPartCommands.Refresh(storage,id,document.Session.FilePath),path);
    }
    [RelayCommand] private async Task DetachExternalAsync()
    {
        if(!HasExternalLink||IsBusy||path is null)return;
        var id=OccurrencePlacement.Resolve(document.Session.Snapshot,path).Slot.DefinitionId;
        await ExecuteAsync(ExternalPartCommands.Detach(id),path);
    }
    private async Task ExecuteAsync(ICadDocumentCommand command,OccurrencePath? select)
    {
        try
        {
            IsBusy=true;await document.Session.ExecuteAsync(command);
            document.Selection.SelectOccurrence(select);
        }
        catch(Exception ex){Context=ex.Message;document.Report(ex);}
        finally{IsBusy=false;}
    }
    public void Dispose()
    {document.Selection.Changed-=Refresh;document.SceneChanged-=Refresh;document.PropertyChanged-=OnDocumentProperty;PropertyChanged-=OnProperty;}
}
public sealed record InstanceDefinitionChoice(DefinitionId Id,string Name);
public sealed record AssemblyParentChoice(string Name,OccurrencePath Path);
public sealed record ExternalPartChoice(DefinitionId Id,string Name);
