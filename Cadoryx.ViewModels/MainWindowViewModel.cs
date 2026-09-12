using AvalonDock.Core;
using AvalonDock.Mvvm;
using Cadoryx.Lang.Strings;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Settings;
using Cadoryx.ViewModels.Toolboxes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Text;
using System.Collections.ObjectModel;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering;
using Cadoryx.ViewModels.Services.Platform.Notifications;

namespace Cadoryx.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IDockLayoutService _dockLayoutService;
    private readonly SideToggleManager _sideToggleManager;
    private readonly IApplicationCultureService _cultureSettingService;
    private readonly IApplicationThemeService _themeSettingService;
    private readonly IApplicationSettingsStore _applicationSettingsStore;
    private readonly IDialogService _dialogService;
    private readonly CadoryxApplicationSettings _applicationSettings;
    private readonly CadWorkspace workspace;
    private readonly IGeometryKernel kernel;
    private readonly IAssetStore assets;
    private readonly IDocumentStorage storage;
    private readonly ICadFileDialogs files;
    private readonly ICadMessageLog log;
    private readonly DocumentRecoveryService recovery;
    private readonly IRecoveryStore recoveryStore;
    private readonly IRecoveryDialogService recoveryDialog;
    private readonly IDocumentResourcesDialogService resourcesDialog;
    private readonly ISketchEditorHost sketchEditor;
    private readonly ILocalFeatureHost localFeatureHost;
    private readonly Cadoryx.Sketching.ISketchConstraintSolver sketchSolver;
    private readonly HashSet<CadDocumentViewModel> closingDocuments=[];
    public ObservableCollection<CadDocumentViewModel> Documents {get;}=[];
    [ObservableProperty] private CadDocumentViewModel? activeDocument;
    [ObservableProperty] private bool isBusy;
    public bool IsShuttingDown {get;private set;}

    public MainWindowViewModel(IDockLayoutService dockLayoutService, SideToggleManager sideToggleManager,
    IApplicationCultureService cultureSettingService,
    IApplicationThemeService themeSettingService,
    IApplicationSettingsStore applicationSettingsStore,
    IDialogService dialogService, CadWorkspace workspace, IGeometryKernel kernel, IAssetStore assets,
    IDocumentStorage storage, ICadFileDialogs files, ICadMessageLog log,
    DocumentRecoveryService recovery, IRecoveryStore recoveryStore, IRecoveryDialogService recoveryDialog, IDocumentResourcesDialogService resourcesDialog,
    ISketchEditorHost sketchEditor,Cadoryx.Sketching.ISketchConstraintSolver sketchSolver,ILocalFeatureHost localFeatureHost
    )
    {
        this._dockLayoutService = dockLayoutService;
        this._sideToggleManager = sideToggleManager;
        this._cultureSettingService = cultureSettingService;
        this._themeSettingService = themeSettingService;
        _applicationSettingsStore = applicationSettingsStore;
        _dialogService = dialogService;
        this.workspace=workspace;this.kernel=kernel;this.assets=assets;this.storage=storage;this.files=files;this.log=log;
        this.recovery=recovery;this.recoveryStore=recoveryStore;this.recoveryDialog=recoveryDialog;
        this.localFeatureHost=localFeatureHost;
        this.resourcesDialog=resourcesDialog;
        this.sketchEditor=sketchEditor;this.sketchSolver=sketchSolver;
        _applicationSettings = applicationSettingsStore.Load();

        ApplySettingsToServices(_applicationSettings);
        CurrentCultureLCID = _applicationSettings.General.CultureLcid;
        IsDarkTheme = _applicationSettings.General.IsDarkTheme;
    }

    public IRootDock DockLayout => _dockLayoutService.Layout;

    public IDockLayoutService LayoutService => _dockLayoutService;

    public ModelTreeToolboxViewModel ModelTree =>
        _dockLayoutService.GetAnchorable<ModelTreeToolboxViewModel>()
        ?? throw new InvalidOperationException("The model tree toolbox is not registered.");

    public PropertiesToolboxViewModel Properties =>
        _dockLayoutService.GetAnchorable<PropertiesToolboxViewModel>()
        ?? throw new InvalidOperationException("The properties toolbox is not registered.");

    public ModelingToolboxViewModel Modeling =>
        _dockLayoutService.GetAnchorable<ModelingToolboxViewModel>()
        ?? throw new InvalidOperationException("The modeling toolbox is not registered.");

    public MessagesToolboxViewModel Messages =>
        _dockLayoutService.GetAnchorable<MessagesToolboxViewModel>()
        ?? throw new InvalidOperationException("The messages toolbox is not registered.");

    public CadoryxApplicationSettings ApplicationSettings => _applicationSettings;


    [ObservableProperty]
    public partial bool Topmost { get; set; }

    [ObservableProperty]
    public partial int CurrentCultureLCID { get; set; }

    [ObservableProperty]
    public partial bool IsDarkTheme { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = Strings.Ready;

    partial void OnIsDarkThemeChanged(bool value)
    {
        if (value == _applicationSettings.General.IsDarkTheme)
            return;

        _applicationSettings.General.IsDarkTheme = value;
        _themeSettingService.ApplyThemeLightDark(value);
        _applicationSettingsStore.Save(_applicationSettings);
      

    }

    [RelayCommand]
    private void ChangeCulture(string lcidString)
    {
        if (!int.TryParse(lcidString, out var lcid))
            return;
        CurrentCultureLCID = lcid;
        _cultureSettingService.ChangeCulture(lcid);
        _applicationSettings.General.CultureLcid = lcid;
        _applicationSettingsStore.Save(_applicationSettings);
    }

    [RelayCommand]
    private void ChangeTopmost()
    {
        Topmost = !Topmost;
    }

    [RelayCommand(CanExecute=nameof(CanStartOperation))]
    private void New(){if(!IsShuttingDown)Attach(workspace.Create(string.Format(Strings.UntitledDocumentFormat,Documents.Count+1)));}

    [RelayCommand(CanExecute=nameof(CanStartOperation))]
    private async Task OpenFileAsync()
    {
        if(IsShuttingDown)return;var path=files.OpenDocument();if(path is null)return;
        await OpenPathAsync(path);
    }
    public async Task OpenPathAsync(string path)=>await RunAsync(async()=>
    {
        var existing=Documents.FirstOrDefault(d=>string.Equals(d.Session.FilePath,Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase));
        if(existing is not null){existing.IsActive=true;ActiveDocument=existing;return;}
        bool own=Path.GetExtension(path).Equals(".cadoryx",StringComparison.OrdinalIgnoreCase);
        using var loaded=own?await storage.LoadAsync(path,assets):await kernel.ImportAsync(path,assets);
        Attach(workspace.Attach(loaded.Snapshot,own?Path.GetFullPath(path):null));
        foreach(var diagnostic in loaded.Diagnostics)log.Add(diagnostic.Message,CadMessageLevel.Information,diagnostic.Code);
        StatusText=string.Format(Strings.OpenedFormat,Path.GetFileName(path));
    });

    [RelayCommand(CanExecute=nameof(CanUseDocument))]
    private async Task SaveAsync(){if(ActiveDocument is {} doc)await RunAsync(async()=>{await SaveDocumentAsync(doc,false);});}
    [RelayCommand(CanExecute=nameof(CanUseDocument))] private async Task SaveAsAsync(){if(ActiveDocument is {} doc)await RunAsync(async()=>{await SaveDocumentAsync(doc,true);});}
    private async Task<bool> SaveDocumentAsync(CadDocumentViewModel doc,bool saveAs)
    {
        var path=saveAs?null:doc.Session.FilePath;path??=files.SaveDocument(doc.Session.Snapshot.Name);
        if(path is null)return false;
        await doc.Session.SaveAsync(storage,path);
        try{await recovery.CheckpointAsync(doc.Session);}catch(Exception ex){ReportRecoveryFailure(ex);}
        StatusText=string.Format(Strings.SavedFormat,Path.GetFileName(path));return !doc.Session.IsDirty;
    }
    [RelayCommand(CanExecute=nameof(CanUseDocument))] private async Task ExportAsync()
    {
        if(ActiveDocument is not {} doc)return;
        var request=await files.ExportDocumentAsync(doc.Session.Snapshot.Name);if(request is null)return;
        await RunAsync(async()=>
        {
            using var capture=doc.Session.Capture();
            var report=await kernel.ExportAsync(capture.Snapshot,assets,request.Path,new CadExportOptions(request.LinearDeflectionMm,request.AngularDeflectionRad,request.BinaryStl,request.VisibleOnly));
            foreach(var diagnostic in report.Diagnostics)log.Add(diagnostic.Message,CadMessageLevel.Information,diagnostic.Code);
            StatusText=string.Format(Strings.ExportedFormat,report.Format,Path.GetFileName(report.Path));
        });
    }

    [RelayCommand(CanExecute=nameof(CanUndo))]
    private async Task UndoAsync(){if(ActiveDocument is {} doc)await RunAsync(doc.Session.UndoAsync);}

    [RelayCommand(CanExecute=nameof(CanRedo))]
    private async Task RedoAsync(){if(ActiveDocument is {} doc)await RunAsync(doc.Session.RedoAsync);}

    [RelayCommand(CanExecute=nameof(CanUseDocument))]
    private void FitView()=>ActiveDocument?.FitView();

    [RelayCommand(CanExecute=nameof(CanUseDocument))]
    private void SetView(string viewName)=>ActiveDocument?.SetView(viewName switch{"Front"=>CadProjection.Front,"Top"=>CadProjection.Top,"Right"=>CadProjection.Right,_=>CadProjection.Axonometric});
    [RelayCommand] private void StartTool(string kind){if(ActiveDocument is null)New();ActiveDocument?.StartTool(kind);}
    [RelayCommand] private void SetDisplay(string mode)=>ActiveDocument?.SetDisplay(mode=="Wireframe"?CadDisplayMode.Wireframe:CadDisplayMode.Shaded);
    [RelayCommand(CanExecute=nameof(CanStartOperation))] private async Task NewSketchAsync()
    {
        if(ActiveDocument is null)New();if(ActiveDocument is not {} doc||doc.IsReadOnly)return;
        var part=doc.SelectedTargetPart??Cadoryx.Db.DefinitionId.New();
        var sketch=Cadoryx.Db.CadSketch.Create(part,Strings.Sketch+" "+(doc.Session.Snapshot.Sketches.Count+1),Cadoryx.Db.RigidTransform3d.Identity);
        await RunAsync(async()=>{await using var editor=new SketchEditorViewModel(doc,kernel,sketchSolver,sketch,true);await sketchEditor.ShowAsync(editor);});
    }
    [RelayCommand(CanExecute=nameof(CanUseDocument))] private async Task LocalFeatureAsync()
    {
        if(ActiveDocument is not {} doc||doc.IsReadOnly)return;
        await RunAsync(async()=>{await using var editor=new LocalFeatureViewModel(doc.Session,kernel);await localFeatureHost.ShowAsync(editor);});
    }
    [RelayCommand(CanExecute=nameof(CanUseDocument))] private async Task EditSketchAsync()
    {
        if(ActiveDocument is not {} doc||doc.IsReadOnly)return;
        if(doc.SelectedSketchId is not {} id||!doc.Session.Snapshot.Sketches.TryGetValue(id,out var sketch)){StatusText=Strings.PickSketch;return;}
        await RunAsync(async()=>{await using var editor=new SketchEditorViewModel(doc,kernel,sketchSolver,sketch,false);await sketchEditor.ShowAsync(editor);});
    }
    [RelayCommand(CanExecute=nameof(CanUseDocument))] private async Task DeleteSketchAsync()
    {
        if(ActiveDocument is not {} doc||doc.SelectedSketchId is not {} id){StatusText=Strings.PickSketch;return;}
        await RunAsync(()=>doc.Session.ExecuteAsync(new Cadoryx.Commands.RemoveSketchCommand(id)));
    }
    [RelayCommand(CanExecute=nameof(CanUseDocument))] private void StartSketchFeature(string kind)=>ActiveDocument?.StartSketchFeature(kind);
    partial void OnActiveDocumentChanged(CadDocumentViewModel? value)
    {
        foreach(var doc in Documents)
        {
            if(!ReferenceEquals(doc,value)){doc.IsActive=false;doc.InvalidatePreview();}
        }
        if(value is not null){value.IsActive=true;if(!ReferenceEquals(_dockLayoutService.ActiveDockable,value))_dockLayoutService.ActiveDockable=value;}
        ModelTree.Bind(value);Properties.Bind(value);Modeling.Bind(value);
        RefreshCommandState();
    }
    private bool CanStartOperation()=>!IsBusy&&!IsShuttingDown;
    private bool CanUseDocument()=>CanStartOperation()&&ActiveDocument is {IsClosingRequested:false};
    private bool CanUndo()=>CanUseDocument()&&ActiveDocument!.Session.CanUndo;
    private bool CanRedo()=>CanUseDocument()&&ActiveDocument!.Session.CanRedo;
    partial void OnIsBusyChanged(bool value)=>RefreshCommandState();
    private void OnDocumentStatus(object? sender,EventArgs e)=>RefreshCommandState();
    private void RefreshCommandState()
    {
        NewCommand.NotifyCanExecuteChanged();OpenFileCommand.NotifyCanExecuteChanged();SaveCommand.NotifyCanExecuteChanged();
        SaveAsCommand.NotifyCanExecuteChanged();ExportCommand.NotifyCanExecuteChanged();UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();FitViewCommand.NotifyCanExecuteChanged();SetViewCommand.NotifyCanExecuteChanged();
        OpenRecoveryCommand.NotifyCanExecuteChanged();
        ManageResourcesCommand.NotifyCanExecuteChanged();
        NewSketchCommand.NotifyCanExecuteChanged();EditSketchCommand.NotifyCanExecuteChanged();DeleteSketchCommand.NotifyCanExecuteChanged();StartSketchFeatureCommand.NotifyCanExecuteChanged();LocalFeatureCommand.NotifyCanExecuteChanged();
    }
    private void Attach(CadDocumentSession session)
    {
        var doc=new CadDocumentViewModel(session,kernel,log);Documents.Add(doc);
        doc.Activated+=OnDocumentActivated;doc.CloseRequested+=OnDocumentCloseRequested;
        session.StatusChanged+=OnDocumentStatus;
        try{_dockLayoutService.OpenDocument(doc);ActiveDocument=doc;doc.IsActive=true;}
        catch
        {
            // A failed dock attachment must not leave a view model observing a discarded recovery candidate.
            try{doc.PermitClose();_dockLayoutService.CloseDocument(doc);}
            finally
            {
                doc.Detach();doc.Activated-=OnDocumentActivated;doc.CloseRequested-=OnDocumentCloseRequested;
                session.StatusChanged-=OnDocumentStatus;Documents.Remove(doc);
                if(ReferenceEquals(ActiveDocument,doc))ActiveDocument=Documents.LastOrDefault();
            }
            throw;
        }
    }
    private void OnDocumentActivated(object? sender,EventArgs e)=>ActiveDocument=(CadDocumentViewModel)sender!;
    private async void OnDocumentCloseRequested(object? sender,EventArgs e)
    {
        if(sender is not CadDocumentViewModel doc||IsShuttingDown||IsBusy||!closingDocuments.Add(doc))return;
        doc.IsClosingRequested=true;
        RefreshCommandState();
        try{if(await CanCloseAsync(doc))await CloseDocumentAsync(doc);}catch(Exception ex){Report(ex);}finally{doc.IsClosingRequested=false;closingDocuments.Remove(doc);RefreshCommandState();}
    }
    private async Task<bool> CanCloseAsync(CadDocumentViewModel doc)
    {
        await doc.StopToolsAsync();if(!doc.Session.IsDirty)return true;
        return await files.ConfirmSaveAsync(doc.Session.Snapshot.Name) switch
        {SaveDecision.Discard=>true,SaveDecision.Save=>await SaveDocumentAsync(doc,false),_=>false};
    }
    private async Task CloseDocumentAsync(CadDocumentViewModel doc)
    {
        doc.PermitClose();_dockLayoutService.CloseDocument(doc);doc.Detach();
        doc.Activated-=OnDocumentActivated;doc.CloseRequested-=OnDocumentCloseRequested;
        doc.Session.StatusChanged-=OnDocumentStatus;
        await workspace.CloseAsync(doc.Session);Documents.Remove(doc);
        if(ReferenceEquals(ActiveDocument,doc))ActiveDocument=Documents.LastOrDefault();
        if(ActiveDocument is {} next)next.IsActive=true;
    }
    public async Task<bool> CloseAllAsync()
    {
        if(IsBusy||closingDocuments.Count>0)return false;
        IsShuttingDown=true;
        foreach(var doc in Documents)doc.IsClosingRequested=true;
        RefreshCommandState();
        try
        {
            foreach(var doc in Documents.ToArray())if(!await CanCloseAsync(doc))return false;
            foreach(var doc in Documents.ToArray())await CloseDocumentAsync(doc);
            return true;
        }
        catch(Exception ex){Report(ex);return false;}
        finally{IsShuttingDown=false;foreach(var doc in Documents)doc.IsClosingRequested=false;RefreshCommandState();}
    }
    private async Task RunAsync(Func<Task> action)
    {
        if(IsShuttingDown||IsBusy)return;IsBusy=true;
        try{await action();}catch(Exception ex){Report(ex);}finally{IsBusy=false;}
    }
    public void Report(Exception ex){StatusText=ex.Message;log.Add(ex.Message,CadMessageLevel.Error,"Workspace");}
    public void ReportRecoveryFailure(Exception ex)
    {log.Add(string.Format(Strings.RecoveryWriteFailed,ex.Message),CadMessageLevel.Warning,"Recovery");}
    [RelayCommand(CanExecute=nameof(CanStartOperation))]
    private async Task OpenRecoveryAsync()=>await recoveryDialog.ShowAsync();
    [RelayCommand(CanExecute=nameof(CanUseDocument))]
    private async Task ManageResourcesAsync()
    {
        if(ActiveDocument is not {} doc)return;
        using var model=new DocumentResourcesViewModel(doc);await resourcesDialog.ShowAsync(model);
    }
    public async Task CheckForRecoveryAsync()
    {
        try
        {
            var scan=await recoveryStore.ScanAsync();
            foreach(var diagnostic in scan.Diagnostics)log.Add(diagnostic.Message,CadMessageLevel.Warning,diagnostic.Code);
            if(scan.Entries.Count>0)await recoveryDialog.ShowAsync();
        }
        catch(Exception ex){ReportRecoveryFailure(ex);}
    }
    public async Task<bool> RestoreRecoveryAsync(RecoveryKey key)
    {
        if(!CanStartOperation())return false;IsBusy=true;
        CadDocumentSession? candidate=null;bool attached=false;
        try
        {
            using var source=await recoveryStore.OpenAsync(key,assets);
            candidate=workspace.AttachRecovered(source.Document.Snapshot,source.Entry.OriginalPath);
            // Establish a durable checkpoint in this process before retiring the old source.
            await recovery.CheckpointAsync(candidate);
            Attach(candidate);attached=true;
            foreach(var diagnostic in source.Document.Diagnostics)log.Add(diagnostic.Message,CadMessageLevel.Warning,diagnostic.Code);
            try{await source.RetireAsync();}catch(Exception ex){ReportRecoveryFailure(ex);}
            StatusText=Strings.RecoveryOpened;return true;
        }
        catch(Exception ex)
        {
            if(candidate is not null&&!attached)await workspace.CloseAsync(candidate);
            Report(ex);return false;
        }
        finally{IsBusy=false;}
    }

    [RelayCommand]
    private void ToggleLeftPanel() => _sideToggleManager.Toggle(ToolboxSide.Left);

    [RelayCommand]
    private void ToggleRightPanel() => _sideToggleManager.Toggle(ToolboxSide.Right);

    [RelayCommand]
    private void ToggleBottomPanel() => _sideToggleManager.Toggle(ToolboxSide.Bottom);

    [RelayCommand]
    private void OpenApplicationSettings()
    {
        _dialogService.ShowApplicationSettingsDialog(
            _applicationSettings.Clone(),
            ApplyApplicationSettings);
    }

    private void ApplyApplicationSettings(CadoryxApplicationSettings settings)
    {
        _applicationSettings.CopyFrom(settings);
        ApplySettingsToServices(_applicationSettings);
        CurrentCultureLCID = _applicationSettings.General.CultureLcid;
        IsDarkTheme = _applicationSettings.General.IsDarkTheme;
        StatusText = Strings.ApplicationSettingsApplied;
    }

    private void ApplySettingsToServices(CadoryxApplicationSettings settings)
    {
        _themeSettingService.ApplyTheme(
            settings.General.IsDarkTheme,
            settings.General.PrimaryColor,
            settings.General.SecondaryColor);
        _cultureSettingService.ChangeCulture(settings.General.CultureLcid);
    }



}
