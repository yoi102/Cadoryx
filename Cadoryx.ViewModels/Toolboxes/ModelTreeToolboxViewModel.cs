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
public partial class ModelTreeToolboxViewModel : CadToolboxViewModelBase
{
    private CadDocumentViewModel? document;
    private bool syncing;
    private bool revealing;
    private ModelTreeItemViewModel? revealedRow;
    private CancellationTokenSource? searchCancellation;
    private DocumentStateId? searchState;
    private bool searchTruncated;
    [ObservableProperty] private string searchText="";
    [ObservableProperty] private string searchSummary="";
    [ObservableProperty] private bool showSearchResults;
    [ObservableProperty] private ModelSearchHit? selectedSearchHit;
    public ObservableCollection<ModelSearchHit> SearchResults {get;}=[];
    public event EventHandler<ModelTreeItemViewModel>? RevealRequested;
    public SessionHistoryViewModel? History=>document?.History;
    private readonly Dictionary<OccurrencePath,ModelTreeItemViewModel> occurrenceNodes=[];
    public ObservableCollection<ModelTreeItemViewModel> Items {get;}=[];
    public ModelTreeToolboxViewModel(IToolboxIconProvider iconProvider):base("toolbox.model-tree",Strings.Model,DockZone.LeftTop,"",true)
    {
        ArgumentNullException.ThrowIfNull(iconProvider);
        Icon=iconProvider.ModelTree;
    }
    public void Bind(CadDocumentViewModel? value)
    {
        if(document is not null){document.SceneChanged-=OnScene;document.Selection.Changed-=OnSelection;}
        document=value;
        OnPropertyChanged(nameof(History));
        if(document is not null){document.SceneChanged+=OnScene;document.Selection.Changed+=OnSelection;}
        Refresh();
    }
    private void OnScene(object? sender,EventArgs e)=>Refresh();
    [RelayCommand] private void Refresh()
    {
        InvalidateSearch();
        var expandedHistory=All(Items).Where(n=>n.IsExpanded&&n.Path is not null&&n.Kind==Strings.FeatureHistory)
            .Select(n=>n.Path!).ToHashSet();
        var expanded=All(Items).Where(n=>n.IsExpanded&&n.Path is not null&&n.Target is null&&n.Feature is null&&
            n.Kind!=Strings.FeatureHistory)
            .Select(n=>n.Path!).ToHashSet();
        Items.Clear();occurrenceNodes.Clear();if(document is null)return;
        var snapshot=document.Session.Snapshot;var root=(AssemblyDefinition)snapshot.Definitions[snapshot.RootAssemblyId];
        var rootPath=new OccurrencePath(snapshot.Id,[]);
        foreach(var slot in root.Children)Items.Add(OccurrenceNode(snapshot,slot,rootPath.Append(slot.Id)));
        foreach(var path in expanded.OrderBy(p=>p.Slots.Length))
            if(occurrenceNodes.TryGetValue(path,out var node))node.IsExpanded=true;
        foreach(var path in expandedHistory)
            if(occurrenceNodes.TryGetValue(path,out var node))
                foreach(var history in node.Children.Where(c=>c.Kind==Strings.FeatureHistory))history.IsExpanded=true;
        OnSelection(this,EventArgs.Empty);
    }
    partial void OnSearchTextChanged(string value)=>InvalidateSearch();
    private void InvalidateSearch()
    {
        searchCancellation?.Cancel();searchState=null;SearchResults.Clear();SelectedSearchHit=null;SearchSummary="";ShowSearchResults=false;
    }
    [RelayCommand] private async Task SearchAsync()
    {
        InvalidateSearch();if(document is not {} doc||string.IsNullOrWhiteSpace(SearchText))return;
        var snapshot=doc.Session.Snapshot;var query=SearchText;
        using var cancel=new CancellationTokenSource();searchCancellation=cancel;
        try
        {
            var result=await Task.Run(()=>ModelTreeSearch.Find(snapshot,query,200,cancel.Token),cancel.Token);
            if(cancel.IsCancellationRequested||document!=doc||doc.Session.Snapshot.StateId!=snapshot.StateId)return;
            searchState=snapshot.StateId;
            foreach(var hit in result.Hits)SearchResults.Add(hit);
            searchTruncated=result.Truncated;RefreshLanguage();ShowSearchResults=result.Hits.Count>0;
        }
        catch(OperationCanceledException){}
        catch(Exception ex){doc.Report(ex);}
        finally{if(ReferenceEquals(searchCancellation,cancel))searchCancellation=null;}
    }
    public void RefreshLanguage()=>SearchSummary=searchState is null?"":string.Format(
        Strings.ResourceManager.GetString(searchTruncated?"ModelSearchLimitedFormat":"ModelSearchCountFormat",Strings.Culture)!,SearchResults.Count);
    [RelayCommand] private void LocateSearchResult()
    {
        if(document is null||SelectedSearchHit is not {} hit)return;
        if(searchState!=document.Session.Snapshot.StateId){InvalidateSearch();return;}
        ShowSearchResults=false;
        Reveal(hit.Path,hit.Body);
        if(hit.Body is {} body)document.Selection.Replace([body]);else document.Selection.SelectOccurrence(hit.Path);
    }
    [RelayCommand] private void LocateSelection()
    {
        ShowSearchResults=false;
        if(document?.Selection.Occurrence is {} path)Reveal(path,document.Selection.Items.FirstOrDefault());
    }
    public ModelTreeItemViewModel? Reveal(OccurrencePath path,SelectionTarget? body=null)
    {
        if(document is null||path.DocumentId!=document.Session.Snapshot.Id||path.Slots.IsEmpty)return null;
        try
        {
            // Reject stale/foreign paths before materializing any row.
            var snapshot=document.Session.Snapshot;var definition=ModelTreeSearch.ResolveDefinition(snapshot,path);
            if(body is not null&&(!body.Path.Equals(path)||!snapshot.Bodies.TryGetValue(body.BodyId,out var b)||
                b.PartId!=definition||b.Geometry.Revision!=body.GeometryRevision))return null;
            revealing=true;
            var prefix=new OccurrencePath(path.DocumentId,[]);ModelTreeItemViewModel? node=null;
            foreach(var id in path.Slots)
            {
                prefix=prefix.Append(id);if(!occurrenceNodes.TryGetValue(prefix,out node))return null;
                if(prefix.Slots.Length<path.Slots.Length||body is not null)node.IsExpanded=true;
            }
            if(body is not null)node=node!.Children.SingleOrDefault(n=>n.Target==body);
            revealedRow=node;
            foreach(var row in All(Items))row.IsSelected=ReferenceEquals(row,node);
            if(node is not null)RevealRequested?.Invoke(this,node);
            return node;
        }
        catch(CadValidationException){return null;}
        finally{revealing=false;}
    }
    private ModelTreeItemViewModel OccurrenceNode(DocumentSnapshot snapshot,ComponentSlot slot,OccurrencePath path)
    {
        var definition=snapshot.Definitions[slot.DefinitionId];
        var label=slot.Name==definition.Name?slot.Name:$"{slot.Name} [{definition.Name}]";
        IEnumerable<ModelTreeItemViewModel> Load()
        {
            if(definition is AssemblyDefinition assembly)
            {
                foreach(var child in assembly.Children)
                    yield return OccurrenceNode(snapshot,child,path.Append(child.Id));
                yield break;
            }
            var part=(PartDefinition)definition;
            foreach(var id in part.Bodies)
            {
                var body=snapshot.Bodies[id];var target=new SelectionTarget(path,id,body.Geometry.Revision);
                var child=new ModelTreeItemViewModel(body.Name,BodyKindText(body.Geometry.Kind),target);
                child.IsChecked=document?.Selection.Items.Contains(target)==true;
                child.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(child.IsChecked)&&!syncing)Toggle(child);};
                yield return child;
            }
            if(!part.Features.IsEmpty)
                yield return new ModelTreeItemViewModel(Strings.FeatureHistory,Strings.FeatureHistory,path:path,
                    loadChildren:()=>part.Features.Select(fid=>
                    {
                        var f=snapshot.Features[fid];return new ModelTreeItemViewModel(f.Name,RecipeText(f.Recipe),
                            null,fid,path,isSuppressed:f.IsSuppressed);
                    }));
            foreach(var sketch in snapshot.Sketches.Values.Where(s=>s.PartId==part.Id).OrderBy(s=>s.Name))
                yield return new ModelTreeItemViewModel(sketch.Name,Strings.Sketch,path:path,sketch:sketch.Id);
        }
        bool hasChildren=definition is AssemblyDefinition a?a.Children.Length>0:
            definition is PartDefinition p&&(!p.Bodies.IsEmpty||!p.Features.IsEmpty||
                snapshot.Sketches.Values.Any(s=>s.PartId==p.Id));
        var node=new ModelTreeItemViewModel(label,definition is PartDefinition?Strings.Part:Strings.Assembly,
            path:path,loadChildren:hasChildren?Load:null);
        occurrenceNodes.Add(path,node);return node;
    }
    private void Toggle(ModelTreeItemViewModel item)
    {
        if(document is null||item.Target is not {} target)return;
        document.Selection.Replace(item.IsChecked?document.Selection.Items.Add(target):document.Selection.Items.Remove(target));
    }
    public async Task SetFeatureSuppressionAsync(ModelTreeItemViewModel row,bool suppressed)
    {
        if(document is not {} doc||row.Feature is not {} id||doc.IsClosingRequested)return;
        try
        {
            if(!doc.Session.Snapshot.Features.TryGetValue(id,out var feature)||feature.IsSuppressed!=row.IsSuppressed)
                throw new CadValidationException("Feature changed; refresh the model tree.");
            await doc.Session.ExecuteAsync(new SetFeatureSuppressionCommand(id,suppressed));
        }
        catch(Exception ex){doc.Report(ex);}
    }
    public void Select(ModelTreeItemViewModel item)
    {
        bool programmatic=ReferenceEquals(item,revealedRow);revealedRow=null;
        if(programmatic)return;
        if(document is null||revealing)return;
        if(item.Target is {} target)document.Selection.Replace([target]);
        else
        {
            document.Selection.SelectOccurrence(item.Path);
            document.SelectedSketchId=item.Sketch;
            if(item.Feature is {} id)document.EditFeature(id);
            else if(item.Path is {} path&&document.Session.Snapshot.Definitions[OccurrencePlacement.Resolve(document.Session.Snapshot,path).Slot.DefinitionId] is PartDefinition part)
                document.SelectedTargetPart=part.Id;
        }
    }
    private void OnSelection(object? sender,EventArgs e)
    {
        syncing=true;
        try{foreach(var item in All(Items))item.IsChecked=item.Target is {} t&&document?.Selection.Items.Contains(t)==true;}
        finally{syncing=false;}
        if(document?.Selection.Occurrence is {} path)
            Reveal(path,document.Selection.Items.FirstOrDefault());
        else
        {
            revealedRow=null;
            foreach(var item in All(Items))item.IsSelected=false;
        }
    }
    private static IEnumerable<ModelTreeItemViewModel> All(IEnumerable<ModelTreeItemViewModel> items)
    {foreach(var item in items){yield return item;foreach(var child in All(item.Children))yield return child;}}
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
    private static string RecipeText(GeometryRecipe recipe)=>recipe switch
    {
        BoxRecipe=>Strings.Box,
        CylinderRecipe=>Strings.Cylinder,
        BooleanRecipe boolean=>boolean.Operation switch
        {
            BooleanOperation.Fuse=>Strings.Union,
            BooleanOperation.Cut=>Strings.Difference,
            BooleanOperation.Common=>Strings.Intersection,
            _=>Strings.Modeling
        },
        ImportedRecipe=>Strings.Import,
        TransformRecipe=>Strings.Transform,
        ExtrudeRecipe=>Strings.Extrude,
        RevolveRecipe=>Strings.Revolve,
        _=>Strings.Modeling
    };
}
public partial class ModelTreeItemViewModel(string name,string kind,SelectionTarget? target=null,FeatureId? feature=null,
    OccurrencePath? path=null,SketchId? sketch=null,Func<IEnumerable<ModelTreeItemViewModel>>? loadChildren=null,
    bool isSuppressed=false):ObservableObject
{
    private Func<IEnumerable<ModelTreeItemViewModel>>? loader=loadChildren;
    public string Name {get;}=name;
    public string Kind {get;}=kind;
    public SelectionTarget? Target {get;}=target;
    public FeatureId? Feature {get;}=feature;
    public bool IsSuppressed {get;}=isSuppressed;
    public SketchId? Sketch {get;}=sketch;
    public OccurrencePath? Path {get;}=path??target?.Path;
    public bool CanCheck=>Target is not null;
    public ObservableCollection<ModelTreeItemViewModel> Children {get;}=loadChildren is null?[]:[new("","")];
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private bool isChecked;
    [ObservableProperty] private bool isSelected;
    partial void OnIsExpandedChanged(bool value)
    {
        if(!value||loader is null)return;
        var load=loader;loader=null;
        var children=load().ToArray();Children.Clear();
        foreach(var child in children)Children.Add(child);
    }
}
