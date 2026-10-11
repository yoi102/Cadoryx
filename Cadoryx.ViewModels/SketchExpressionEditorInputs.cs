using Cadoryx.Db;
using Cadoryx.Editor;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.ViewModels;

public partial class SketchEditorViewModel
{
    private bool loadingExpressionInputs;
    private bool pendingExpression;
    private SketchRegionOffsetAuthor? SelectedRegion=>HasSelectedExpression?Sketch.RegionOffsets.SingleOrDefault(g=>g.Loops.Any(l=>l.Outputs.Any(o=>o.Curve==SelectedEntities[0]))):null;
    public bool HasSelectedRegion=>SelectedRegion is not null;
    public bool CanEditIndividualExpression=>HasSelectedExpression&&SelectedRegion is null;
    public bool HasSelectedExpression=>SelectedEntities.Count==1&&Sketch.ExpressionCurves.Any(c=>c.Id==SelectedEntities[0]);
    public bool HasSelectedOffsetExpression=>SelectedRegion is {} group?!group.IsFrozen:HasSelectedExpression&&SelectedExpression().Geometry.Kind==SketchCurveExpressionKind.Offset;
    private bool IsExpressionDistanceDriven=>HasSelectedExpression&&baseline.Snapshot.Parameters.Bindings.Any(b=>b.Target==
        (SelectedRegion is {} group?ParameterTarget.SketchRegion(Sketch.Id,group.Id):ParameterTarget.SketchCurve(Sketch.Id,SelectedExpression().Id,"OffsetDistance",0)));
    public bool CanEditExpressionDistance=>CanEdit&&HasSelectedOffsetExpression&&!IsExpressionDistanceDriven;
    public string ExpressionSourceStatus=>SelectedRegion is {} group?string.Format(group.IsFrozen?Strings.RegionSourceFrozen:Strings.RegionSourceActive,
        group.Loops.Sum(l=>l.Outputs.Length)):!HasSelectedExpression?Strings.SelectExactExpression:SelectedExpression().Source switch
    {null=>Strings.ExpressionSourceDetached,{IsFrozen:true}=>Strings.ExpressionSourceFrozen,_=>Strings.ExpressionSourceActive};
    [ObservableProperty] private double expressionDistance;
    [ObservableProperty] private double expressionFirstParameter;
    [ObservableProperty] private double expressionLastParameter=1;
    partial void OnExpressionDistanceChanged(double value)
    {
        if(loadingExpressionInputs||!HasSelectedOffsetExpression)return;
        if(IsExpressionDistanceDriven)
        {
            loadingExpressionInputs=true;
            try{ExpressionDistance=SelectedRegion?.Distance??SelectedExpression().Geometry.SignedOffset;}
            finally{loadingExpressionInputs=false;}
            Status=Strings.ParameterAlreadyDriven;return;
        }
        pendingExpression=true;Invalidate();
    }
    private SketchExpressionCurve SelectedExpression()
    {
        if(!HasSelectedExpression)throw new CadValidationException(Strings.SelectExactExpression);
        return Sketch.ExpressionCurves.Single(c=>c.Id==SelectedEntities[0]);
    }
    private void UpdateExpressionInputs()
    {
        pendingExpression=false;loadingExpressionInputs=true;
        try
        {
            if(HasSelectedExpression){ExpressionDistance=SelectedRegion?.Distance??SelectedExpression().Geometry.SignedOffset;ExpressionFirstParameter=0;ExpressionLastParameter=1;}
        }
        finally{loadingExpressionInputs=false;}
        OnPropertyChanged(nameof(HasSelectedExpression));OnPropertyChanged(nameof(HasSelectedOffsetExpression));OnPropertyChanged(nameof(ExpressionSourceStatus));
        OnPropertyChanged(nameof(CanEditIndividualExpression));
        OnPropertyChanged(nameof(HasSelectedRegion));
        OnPropertyChanged(nameof(CanEditExpressionDistance));ApplyExpressionDistanceCommand.NotifyCanExecuteChanged();
        NotifyRegionSourceState();
    }
    [RelayCommand] private void CreateExactOffset()
    {
        SketchEntityId? created=null;
        Mutate(d=>
        {
            if(SelectedEntities.Count!=1)throw new CadValidationException(Strings.ExactOffsetHint);
            if(SelectedRegion is {} group)throw new CadValidationException(string.Format(Strings.RegionSourceActive,group.Loops.Sum(l=>l.Outputs.Length)));
            var candidate=new SketchDraft(d.Value,kernel as Cadoryx.Kernel.Abstractions.IExactSketchExpressionRegions);created=candidate.AddExactOffset(SelectedEntities[0],OffsetDistance);
            if(!Construction)candidate.SetExpressionConstruction(created.Value,false);
            d.Change(_=>candidate.Value);
        });
        if(created is {} id&&Sketch.ExpressionCurves.Any(c=>c.Id==id))Select(id);
    }
    [RelayCommand(CanExecute=nameof(CanEditExpressionDistance))] private void ApplyExpressionDistance()=>Mutate(ApplyExpressionDistanceCore);
    private void ApplyExpressionDistanceCore(SketchDraft draft)
    {
        if(IsExpressionDistanceDriven)throw new CadValidationException(Strings.ParameterAlreadyDriven);
        var c=SelectedExpression();
        if(SelectedRegion is {} group){draft.ChangeRegionDistance(group.Id,ExpressionDistance);pendingExpression=false;return;}
        if(c.Geometry.Kind!=SketchCurveExpressionKind.Offset)throw new CadValidationException(Strings.SelectExactExpression);
        draft.EditExpression(c.Id,c.Geometry with{SignedOffset=ExpressionDistance});pendingExpression=false;
    }
    [RelayCommand] private void ReverseExpression()=>Mutate(d=>{var c=SelectedExpression();d.EditExpression(c.Id,c.Geometry.Reverse());});
    [RelayCommand] private void KeepExpressionInterval()=>Mutate(d=>
    {var c=SelectedExpression();d.EditExpression(c.Id,c.Geometry.Trim(ExpressionFirstParameter,ExpressionLastParameter));});
    [RelayCommand] private void FreezeExpression()=>Mutate(d=>{if(SelectedRegion is {} group)d.FreezeRegion(group.Id,Strings.RegionFrozenReason);else d.FreezeExpression(SelectedExpression().Id);});
    [RelayCommand] private void ResumeExpression()=>Mutate(d=>{if(SelectedRegion is {} group)d.ResumeRegion(group.Id);else d.ResumeExpression(SelectedExpression().Id);});
    [RelayCommand] private void DetachExpression()=>Mutate(d=>{if(SelectedRegion is {} group)d.DetachRegion(group.Id);else d.DetachExpression(SelectedExpression().Id);});
    [RelayCommand] private void RemoveRegion()=>Mutate(d=>d.RemoveRegion((SelectedRegion??throw new CadValidationException(Strings.SelectExactExpression)).Id));
    [RelayCommand] private void AttachAssociativeEndpoints()=>Mutate(d=>
    {
        var curves=SelectedEntities.Where(id=>Sketch.ExpressionCurves.Any(c=>c.Id==id)).ToArray();
        var points=SelectedEntities.Where(id=>Sketch.Points.Any(p=>p.Id==id)).ToArray();
        if(curves.Length!=1||points.Length is not (0 or 2)||curves.Length+points.Length!=SelectedEntities.Count)
            throw new CadValidationException(Strings.AssociativeEndpointsHint);
        d.AttachExpressionEndpoints(curves[0],points.Length==0?null:points[0],points.Length==0?null:points[1]);
    });
}
