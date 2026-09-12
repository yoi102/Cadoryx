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
    [ObservableProperty] private string instanceName="";
    [ObservableProperty] private string context="";
    [ObservableProperty] private double x;
    [ObservableProperty] private double y;
    [ObservableProperty] private double z;
    [ObservableProperty] private double axisX;
    [ObservableProperty] private double axisY;
    [ObservableProperty] private double axisZ=1;
    [ObservableProperty] private double angleDegrees;
    [ObservableProperty] private bool isBusy;
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
            path=document.Selection.Occurrence;HasOccurrence=path is not null;CanMove=false;
            if(path is null){Context=Strings.SelectInstance;return;}
            var snapshot=document.Session.Snapshot;var resolved=OccurrencePlacement.Resolve(snapshot,path);
            InstanceName=resolved.Slot.Name;Context=resolved.CanMoveIndependently?Strings.InstanceLocalCoordinates:Strings.SharedParentMoveBlocked;
            CanMove=resolved.CanMoveIndependently&&!document.IsReadOnly&&!document.IsClosingRequested&&!document.Session.IsClosing;
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
    public void Dispose()
    {document.Selection.Changed-=Refresh;document.SceneChanged-=Refresh;document.PropertyChanged-=OnDocumentProperty;PropertyChanged-=OnProperty;}
}
