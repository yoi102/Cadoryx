using System.Collections.ObjectModel;
using Cadoryx.Db;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public sealed record RevolveAxisChoice(Guid? Id,string Label);

public partial class CadDocumentViewModel
{
    private bool refreshingRevolveAxes;
    private RevolveAxisReference? independentRevolveAxis;
    private SketchProfile? preservedCurveProfile;
    private double? originalRevolveAngle;
    private double RevolveAngleValue=>originalRevolveAngle is {} original&&AngleDegrees==original*180/Math.PI?original:AngleDegrees*Math.PI/180;
    public bool IsRevolveTool=>ToolKind=="Revolve";
    public bool CanEditProfilePoints=>IsFrozenProfile&&preservedCurveProfile is null;
    public ObservableCollection<RevolveAxisChoice> RevolveAxes {get;}=[];
    [ObservableProperty] private RevolveAxisChoice? selectedRevolveAxis;
    public bool CanDetachRevolveAxis=>SelectedRevolveAxis?.Id is not null;
    partial void OnSelectedRevolveAxisChanged(RevolveAxisChoice? value)
    {
        if(!refreshingRevolveAxes){independentRevolveAxis=null;InvalidatePreview();}
        OnPropertyChanged(nameof(CanDetachRevolveAxis));
    }
    private void RefreshRevolveAxes(Guid? selected=null)
    {
        selected??=SelectedRevolveAxis?.Id;refreshingRevolveAxes=true;
        try
        {
            RevolveAxes.Clear();RevolveAxes.Add(new(null,independentRevolveAxis is null?Strings.RevolveLocalAxis:Strings.RevolveFrozenAxis));
            foreach(var axis in Session.Snapshot.Datums.Axes.Values.Where(a=>a.PartId==SelectedTargetPart).OrderBy(a=>a.Name))
                RevolveAxes.Add(new(axis.Id,axis.Name+(axis.IsStale?" · "+Strings.ReferenceStale:string.Empty)));
            if(selected is {} id&&!RevolveAxes.Any(a=>a.Id==id))RevolveAxes.Add(new(id,Strings.ReferenceStale));
            SelectedRevolveAxis=RevolveAxes.First(a=>a.Id==selected);
        }
        finally{refreshingRevolveAxes=false;}
    }
    private RevolveAxisReference? CurrentRevolveAxis()
    {
        if(SelectedRevolveAxis?.Id is not {} id)return independentRevolveAxis;
        var axis=DatumConstructions.Axis(Session.Snapshot,SelectedTargetPart??throw new CadValidationException(Strings.SelectTargetPart),id);
        return new(axis.Frame.Translation,axis.Frame.Rotation.Rotate(Vector3d.UnitZ),id,axis.Revision);
    }
    [RelayCommand] private void UseLocalRevolveAxis()
    {independentRevolveAxis=null;SelectedRevolveAxis=null;RefreshRevolveAxes();InvalidatePreview();}
    [RelayCommand] private void DetachRevolveAxis()
    {
        try
        {
            RevolveAxisReference? axis;
            if(EditingFeature is {} feature&&Session.Snapshot.Features[feature] is {IsStale:true,Recipe:RevolveRecipe {Axis:{} frozen}}&&frozen.Datum==SelectedRevolveAxis?.Id)
                axis=frozen;
            else axis=CurrentRevolveAxis();
            if(axis is null)return;
            independentRevolveAxis=axis with{Datum=null,DatumRevision=null};refreshingRevolveAxes=true;SelectedRevolveAxis=null;refreshingRevolveAxes=false;
            RefreshRevolveAxes();InvalidatePreview();
        }
        catch(Exception error){ToolStatus=error.Message;}
    }
    private void LoadRevolveAxis(RevolveAxisReference? axis)
    {refreshingRevolveAxes=true;SelectedRevolveAxis=null;refreshingRevolveAxes=false;independentRevolveAxis=axis?.Datum is null?axis:null;RefreshRevolveAxes(axis?.Datum);}
}
