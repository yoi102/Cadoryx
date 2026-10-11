using System.Collections.Immutable;
using System.Globalization;
using Cadoryx.Db;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.ViewModels;

public partial class SketchEditorViewModel
{
    [ObservableProperty] private string splineKnotsText="";
    [ObservableProperty] private double splineFirstParameter;
    [ObservableProperty] private double splineLastParameter=1;
    [ObservableProperty] private double splineExtensionAmount=.2;
    [ObservableProperty] private bool splineExtendStart;
    public bool HasSelectedSpline=>SelectedEntities.Count==1&&Sketch.Splines.Any(c=>c.Id==SelectedEntities[0]);
    public bool HasSelectedExactCurve=>SelectedEntities.Count==1&&(HasSelectedSpline||Sketch.Beziers.Any(c=>c.Id==SelectedEntities[0]));
    private SketchEntityId SelectedSpline()=>HasSelectedSpline?SelectedEntities[0]:throw new CadValidationException(Strings.SelectExactSpline);
    private void UpdateSplineInputs()
    {
        OnPropertyChanged(nameof(HasSelectedSpline));
        OnPropertyChanged(nameof(HasSelectedExactCurve));
        if(!HasSelectedSpline){SplineKnotsText="";return;}
        var spline=Sketch.Splines.Single(c=>c.Id==SelectedEntities[0]);
        var points=Sketch.Points.ToDictionary(p=>p.Id,p=>p.Position);
        var definition=CubicSplineKnotVector.Definition(spline.Controls.Select(id=>points[id]).ToImmutableArray(),spline.Knots);
        SplineKnotsText=string.Join("; ",definition.Knots.Zip(definition.Multiplicities).Select(k=>
            k.First.ToString("G17",CultureInfo.InvariantCulture)+":"+k.Second.ToString(CultureInfo.InvariantCulture)));
    }
    [RelayCommand] private void ApplySplineKnots()=>Mutate(d=>
    {
        var entries=SplineKnotsText.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        if(entries.Length is <2 or >CubicSplineDefinition.MaximumControls)throw new CadValidationException(Strings.SplineKnotsHint);
        var knots=ImmutableArray.CreateBuilder<double>();var multiplicities=ImmutableArray.CreateBuilder<int>();
        foreach(string entry in entries)
        {
            var pair=entry.Split(':',StringSplitOptions.TrimEntries);
            if(pair.Length!=2||!double.TryParse(pair[0],NumberStyles.Float,CultureInfo.InvariantCulture,out var knot)||
                !int.TryParse(pair[1],NumberStyles.Integer,CultureInfo.InvariantCulture,out var multiplicity))
                throw new CadValidationException(Strings.SplineKnotsHint);
            knots.Add(knot);multiplicities.Add(multiplicity);
        }
        d.SetSplineKnots(SelectedSpline(),new(knots.ToImmutable(),multiplicities.ToImmutable()));
    });
    private SketchEntityId SelectedExactCurve()=>HasSelectedExactCurve?SelectedEntities[0]:throw new CadValidationException(Strings.SelectExactCurve);
    [RelayCommand] private void KeepSplineInterval()=>Mutate(d=>
    {
        var id=SelectedExactCurve();if(HasSelectedSpline)d.TrimSpline(id,SplineFirstParameter,SplineLastParameter);
        else d.TrimBezier(id,SplineFirstParameter,SplineLastParameter);
    });
    [RelayCommand] private void ExtendSplinePolynomial()=>Mutate(d=>
    {
        var id=SelectedExactCurve();if(HasSelectedSpline)d.ExtendSpline(id,SplineExtendStart,SplineExtensionAmount);
        else d.ExtendBezier(id,SplineExtendStart,SplineExtensionAmount);
    });
    [RelayCommand] private void RemoveCurveInterval()=>Mutate(d=>
    {
        var id=SelectedExactCurve();if(HasSelectedSpline)d.RemoveSplineInterval(id,SplineFirstParameter,SplineLastParameter);
        else d.RemoveBezierInterval(id,SplineFirstParameter,SplineLastParameter);
    });
}
