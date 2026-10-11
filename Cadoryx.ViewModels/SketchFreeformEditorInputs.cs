using System.Collections.Immutable;
using System.Globalization;
using Cadoryx.Db;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.ViewModels;

public partial class SketchEditorViewModel
{
    private bool loadingFreeformInputs,pendingFreeform;
    [ObservableProperty] private string freeformPolesText="";
    [ObservableProperty] private string freeformWeightsText="";
    [ObservableProperty] private string freeformKnotsText="";
    [ObservableProperty] private int freeformDegree=3;
    [ObservableProperty] private bool freeformPeriodic;
    [ObservableProperty] private double freeformFirstParameter;
    [ObservableProperty] private double freeformLastParameter=1;
    [ObservableProperty] private bool freeformReversed;
    public bool HasSelectedFreeform=>SelectedEntities.Count==1&&Sketch.Freeforms.Any(e=>e.Id==SelectedEntities[0]);
    private void FreeformInputChanged(){if(!loadingFreeformInputs&&HasSelectedFreeform){pendingFreeform=true;Invalidate();}}
    partial void OnFreeformPolesTextChanged(string value)=>FreeformInputChanged();
    partial void OnFreeformWeightsTextChanged(string value)=>FreeformInputChanged();
    partial void OnFreeformKnotsTextChanged(string value)=>FreeformInputChanged();
    partial void OnFreeformDegreeChanged(int value)=>FreeformInputChanged();
    partial void OnFreeformPeriodicChanged(bool value)=>FreeformInputChanged();
    partial void OnFreeformFirstParameterChanged(double value)=>FreeformInputChanged();
    partial void OnFreeformLastParameterChanged(double value)=>FreeformInputChanged();
    partial void OnFreeformReversedChanged(bool value)=>FreeformInputChanged();
    private void UpdateFreeformInputs()
    {
        OnPropertyChanged(nameof(HasSelectedFreeform));pendingFreeform=false;if(!HasSelectedFreeform)return;
        loadingFreeformInputs=true;
        try
        {
            var g=Sketch.Freeforms.Single(e=>e.Id==SelectedEntities[0]).Geometry;
            string Number(double value)=>value.ToString("G17",CultureInfo.InvariantCulture);
            FreeformPolesText=string.Join("; ",g.Poles.Select(p=>Number(p.X)+","+Number(p.Y)));
            FreeformWeightsText=string.Join("; ",g.Weights.Select(Number));
            FreeformKnotsText=string.Join("; ",g.Knots.Zip(g.Multiplicities).Select(k=>Number(k.First)+":"+k.Second.ToString(CultureInfo.InvariantCulture)));
            FreeformDegree=g.Degree;FreeformPeriodic=g.Periodic;FreeformFirstParameter=g.FirstParameter;
            FreeformLastParameter=g.LastParameter;FreeformReversed=g.Reversed;
        }
        finally{loadingFreeformInputs=false;}
    }
    [RelayCommand] private void ApplyFreeform()=>Mutate(ApplyFreeformCore);
    private void ApplyFreeformCore(Cadoryx.Editor.SketchDraft draft)
    {
        if(!HasSelectedFreeform)throw new CadValidationException(Strings.SelectExactFreeform);
        var id=SelectedEntities[0];
        if(Sketch.Projections.Any(p=>p.TargetEntity==id)||Sketch.NativeProjections.Any(p=>p.TargetEntity==id))
            throw new CadValidationException(Strings.NativeProjectionDriven);
        string[] Entries(string value,int maximum)
        {
            if(value.Length>200000)throw new CadValidationException(Strings.FreeformDefinitionHint);
            var values=value.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
            if(values.Length>maximum)throw new CadValidationException(Strings.FreeformDefinitionHint);return values;
        }
        double Number(string value)=>double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out double result)&&double.IsFinite(result)?
            result:throw new CadValidationException(Strings.FreeformDefinitionHint);
        var poles=Entries(FreeformPolesText,SketchFreeformGeometry.MaximumPoles).Select(value=>
        {
            var pair=value.Split(',',StringSplitOptions.TrimEntries);
            if(pair.Length!=2)throw new CadValidationException(Strings.FreeformPolesHint);return new Point2d(Number(pair[0]),Number(pair[1]));
        }).ToImmutableArray();
        var knots=ImmutableArray.CreateBuilder<double>();var multiplicities=ImmutableArray.CreateBuilder<int>();
        foreach(string value in Entries(FreeformKnotsText,SketchFreeformGeometry.MaximumPoles+26))
        {
            var pair=value.Split(':',StringSplitOptions.TrimEntries);
            if(pair.Length!=2||!int.TryParse(pair[1],NumberStyles.Integer,CultureInfo.InvariantCulture,out int mult))
                throw new CadValidationException(Strings.SplineKnotsHint);
            knots.Add(Number(pair[0]));multiplicities.Add(mult);
        }
        var geometry=new SketchFreeformGeometry(Sketch.Freeforms.Single(e=>e.Id==id).Geometry.Kind,poles,
            Entries(FreeformWeightsText,SketchFreeformGeometry.MaximumPoles).Select(Number).ToImmutableArray(),
            knots.ToImmutable(),multiplicities.ToImmutable(),FreeformDegree,FreeformPeriodic,FreeformFirstParameter,FreeformLastParameter,FreeformReversed);
        draft.EditFreeform(id,geometry);pendingFreeform=false;
    }
}
