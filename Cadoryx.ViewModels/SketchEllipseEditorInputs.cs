using Cadoryx.Db;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.ViewModels;

public partial class SketchEditorViewModel
{
    private bool loadingEllipseInputs,pendingEllipse;
    [ObservableProperty] private double ellipseCenterX;
    [ObservableProperty] private double ellipseCenterY;
    [ObservableProperty] private double ellipseCosineX=10;
    [ObservableProperty] private double ellipseCosineY;
    [ObservableProperty] private double ellipseSineX;
    [ObservableProperty] private double ellipseSineY=5;
    [ObservableProperty] private double ellipseStartDegrees;
    [ObservableProperty] private double ellipseSweepDegrees=360;
    public bool HasSelectedEllipse=>SelectedEntities.Count==1&&Sketch.Ellipses.Any(e=>e.Id==SelectedEntities[0]);
    private void EllipseInputChanged(){if(!loadingEllipseInputs&&HasSelectedEllipse){pendingEllipse=true;Invalidate();}}
    partial void OnEllipseCenterXChanged(double value)=>EllipseInputChanged();
    partial void OnEllipseCenterYChanged(double value)=>EllipseInputChanged();
    partial void OnEllipseCosineXChanged(double value)=>EllipseInputChanged();
    partial void OnEllipseCosineYChanged(double value)=>EllipseInputChanged();
    partial void OnEllipseSineXChanged(double value)=>EllipseInputChanged();
    partial void OnEllipseSineYChanged(double value)=>EllipseInputChanged();
    partial void OnEllipseStartDegreesChanged(double value)=>EllipseInputChanged();
    partial void OnEllipseSweepDegreesChanged(double value)=>EllipseInputChanged();
    private SketchEllipseGeometry EllipseInput()=>new(new(EllipseCenterX,EllipseCenterY),new(EllipseCosineX,EllipseCosineY),
        new(EllipseSineX,EllipseSineY),EllipseStartDegrees*Math.PI/180,EllipseSweepDegrees*Math.PI/180);
    private void UpdateEllipseInputs()
    {
        OnPropertyChanged(nameof(HasSelectedEllipse));pendingEllipse=false;if(!HasSelectedEllipse)return;loadingEllipseInputs=true;
        var e=Sketch.Ellipses.Single(e=>e.Id==SelectedEntities[0]).Geometry;
        EllipseCenterX=e.Center.X;EllipseCenterY=e.Center.Y;EllipseCosineX=e.Cosine.X;EllipseCosineY=e.Cosine.Y;
        EllipseSineX=e.Sine.X;EllipseSineY=e.Sine.Y;EllipseStartDegrees=e.StartAngle*180/Math.PI;EllipseSweepDegrees=e.SweepAngle*180/Math.PI;
        loadingEllipseInputs=false;
    }
    [RelayCommand] private void AddEllipse()=>Mutate(d=>d.AddEllipse(EllipseInput(),Construction));
    [RelayCommand] private void ApplyEllipse()=>Mutate(ApplyEllipseCore);
    private void ApplyEllipseCore(Cadoryx.Editor.SketchDraft d)
    {
        if(!HasSelectedEllipse)throw new CadValidationException(Strings.SelectExactEllipse);
        if(Sketch.Projections.Any(p=>p.TargetEntity==SelectedEntities[0])||Sketch.NativeProjections.Any(p=>p.TargetEntity==SelectedEntities[0]))throw new CadValidationException(Strings.DetachEllipseBeforeEditing);
        d.EditEllipse(SelectedEntities[0],EllipseInput());pendingEllipse=false;
    }
}
