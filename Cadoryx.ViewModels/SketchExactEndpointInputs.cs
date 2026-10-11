using Cadoryx.Db;
using CommunityToolkit.Mvvm.Input;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.ViewModels;

public partial class SketchEditorViewModel
{
    [RelayCommand] private void ExposeCurveEndpoints()=>Mutate(d=>
    {
        if(SelectedEntities.Count!=1)throw new CadValidationException(Strings.ExactEndpointHint);
        if(Sketch.ExpressionCurves.Any(c=>c.Id==SelectedEntities[0]))d.AttachExpressionEndpoints(SelectedEntities[0]);
        else d.ExposeCurveEndpoints(SelectedEntities[0]);
    });
}
