using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using CommunityToolkit.Mvvm.Input;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.ViewModels;

public partial class SketchEditorViewModel
{
    private ISketchRegionSourceSelectionHost? regionSourceSelectionHost;
    public ISketchRegionSourceSelectionHost? RegionSourceSelectionHost
    {
        get=>regionSourceSelectionHost;
        set{regionSourceSelectionHost=value;NotifyRegionSourceState();}
    }
    public bool CanReselectRegionSources=>CanEdit&&HasSelectedRegion&&regionSourceSelectionHost is not null;
    private bool HasPendingRegionSourceFields=>pendingExpression||pendingCoordinates||pendingEllipse||pendingFreeform||pendingDimension||pendingContactBranch||pendingFiniteContact;
    private void NotifyRegionSourceState(){OnPropertyChanged(nameof(CanReselectRegionSources));ReselectRegionSourcesCommand.NotifyCanExecuteChanged();}
    [RelayCommand(CanExecute=nameof(CanReselectRegionSources))] private async Task ReselectRegionSourcesAsync()
    {
        if(SelectedRegion is not {} group||RegionSourceSelectionHost is null)return;
        if(HasPendingRegionSourceFields){Status=Strings.RegionReselectPending;return;}
        Invalidate();await using var editor=new SketchRegionSourceSelectionViewModel(this,group.Id);await RegionSourceSelectionHost.ShowAsync(editor);
    }
    internal SketchEditorViewModel CreateRegionSourcePreview(CadSketch sketch)=>new(document,kernel,solver,sketch,false);
    internal SketchRegionSourceReselection.Prepared PrepareRegionSources(CadSketch captured,Guid group,ImmutableArray<ImmutableArray<SketchEntityId>> slots,CancellationToken token=default)
        =>SketchRegionSourceReselection.Prepare(captured,group,slots,kernel as IExactSketchExpressionRegions??throw new NotSupportedException(),token);
    internal void RequireRegionSourceParameters(SketchRegionSourceReselection.Prepared prepared)
    {
        if(!CanEdit||!ReferenceEquals(Sketch,prepared.Original))throw new CadValidationException(Strings.RegionReselectStale);
        var before=baseline.Snapshot with{Sketches=baseline.Snapshot.Sketches.SetItem(Sketch.Id,Sketch)};
        var after=before with{Sketches=before.Sketches.SetItem(Sketch.Id,prepared.Candidate)};
        try{SketchRegionParameterTargets.GuardStructure(before,after);}
        catch(CadValidationException error){error.Data["RegionReselect.ParameterGuard"]=true;throw;}
    }
    internal string? AcceptRegionSources(SketchRegionSourceReselection.Prepared prepared)
    {
        try{RequireRegionSourceParameters(prepared);}catch(CadValidationException error){return error.Data["RegionReselect.ParameterGuard"] is true?Strings.RegionReselectParameterGuard:Strings.RegionReselectStale;}
        bool accepted=false;Mutate(d=>{d.ApplyPreparedRegionSources(prepared);accepted=true;});
        if(!accepted)return Status;
        Status=Strings.RegionReselectSuccess;return null;
    }
}
