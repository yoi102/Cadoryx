using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Editor;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.ViewModels;

public sealed record FiniteContactBranchOption(FiniteContactTangentBranch Key,string Label);
public sealed record FiniteContactContinuityOption(FiniteContactContinuity Key,string Label);
public sealed record FiniteContactParameterModeOption(FiniteContactParameterMode Key,string Label);

public partial class SketchEditorViewModel
{
    private bool pendingFiniteContact;
    public IReadOnlyList<FiniteContactBranchOption> FiniteContactBranches {get;}=
        Enum.GetValues<FiniteContactTangentBranch>().Select(b=>new FiniteContactBranchOption(b,FiniteContactBranchLabel(b))).ToArray();
    public IReadOnlyList<FiniteContactContinuityOption> FiniteContactContinuities {get;}=
        Enum.GetValues<FiniteContactContinuity>().Select(c=>new FiniteContactContinuityOption(c,FiniteContactContinuityLabel(c))).ToArray();
    public bool ShowFiniteContact=>ConstraintKind=="FiniteParametricContact"||Sketch.Constraints.Any(c=>c.Id==SelectedConstraint?.Id&&c is FiniteParametricContactConstraint);
    public IReadOnlyList<FiniteContactParameterModeOption> FiniteContactParameterModes {get;}=
        Enum.GetValues<FiniteContactParameterMode>().Select(m=>new FiniteContactParameterModeOption(m,FiniteContactParameterModeLabel(m))).ToArray();
    public bool HasSlidingContactA=>ContactParameterMode is FiniteContactParameterMode.SolveA or FiniteContactParameterMode.SolveBoth;
    public bool HasSlidingContactB=>ContactParameterMode is FiniteContactParameterMode.SolveB or FiniteContactParameterMode.SolveBoth;
    public bool ContactParameterAWritable=>!HasSlidingContactA;
    public bool ContactParameterBWritable=>!HasSlidingContactB;
    [ObservableProperty] private double contactParameterA=.5;
    [ObservableProperty] private double contactParameterB=.5;
    [ObservableProperty] private FiniteContactTangentBranch finiteContactBranch=FiniteContactTangentBranch.SameDirection;
    [ObservableProperty] private FiniteContactContinuity contactContinuity=FiniteContactContinuity.Tangent;
    [ObservableProperty] private FiniteContactParameterMode contactParameterMode=FiniteContactParameterMode.Fixed;
    [ObservableProperty] private double contactSeedA=.5;
    [ObservableProperty] private double contactLowerA;
    [ObservableProperty] private double contactUpperA=1;
    [ObservableProperty] private double contactSeedB=.5;
    [ObservableProperty] private double contactLowerB;
    [ObservableProperty] private double contactUpperB=1;
    partial void OnContactParameterAChanged(double value)=>FiniteContactInputChanged();
    partial void OnContactParameterBChanged(double value)=>FiniteContactInputChanged();
    partial void OnFiniteContactBranchChanged(FiniteContactTangentBranch value)=>FiniteContactInputChanged();
    partial void OnContactContinuityChanged(FiniteContactContinuity value)=>FiniteContactInputChanged();
    partial void OnContactParameterModeChanged(FiniteContactParameterMode value)
    {
        OnPropertyChanged(nameof(HasSlidingContactA));OnPropertyChanged(nameof(HasSlidingContactB));
        OnPropertyChanged(nameof(ContactParameterAWritable));OnPropertyChanged(nameof(ContactParameterBWritable));FiniteContactInputChanged();
    }
    partial void OnContactSeedAChanged(double value)=>FiniteContactInputChanged();
    partial void OnContactLowerAChanged(double value)=>FiniteContactInputChanged();
    partial void OnContactUpperAChanged(double value)=>FiniteContactInputChanged();
    partial void OnContactSeedBChanged(double value)=>FiniteContactInputChanged();
    partial void OnContactLowerBChanged(double value)=>FiniteContactInputChanged();
    partial void OnContactUpperBChanged(double value)=>FiniteContactInputChanged();
    private void FiniteContactInputChanged()
    {
        if(!loadingInputs&&Sketch.Constraints.Any(c=>c.Id==SelectedConstraint?.Id&&c is FiniteParametricContactConstraint))
        {pendingFiniteContact=true;Invalidate();}
    }
    private void LoadFiniteContactInputs()
    {
        pendingFiniteContact=false;
        if(Sketch.Constraints.FirstOrDefault(c=>c.Id==SelectedConstraint?.Id) is FiniteParametricContactConstraint c)
        {
            bool previous=loadingInputs;loadingInputs=true;
            ContactParameterA=c.ParameterA;ContactParameterB=c.ParameterB;FiniteContactBranch=c.Branch;ContactContinuity=c.Continuity;
            ContactParameterMode=c.ParameterMode;ContactSeedA=c.SlidingA?.Seed??c.ParameterA;ContactLowerA=c.SlidingA?.Lower??0;ContactUpperA=c.SlidingA?.Upper??1;
            ContactSeedB=c.SlidingB?.Seed??c.ParameterB;ContactLowerB=c.SlidingB?.Lower??0;ContactUpperB=c.SlidingB?.Upper??1;
            loadingInputs=previous;
        }
        OnPropertyChanged(nameof(ShowFiniteContact));
    }
    [RelayCommand] private void ApplyFiniteContact()=>Mutate(ApplyFiniteContactCore);
    private void ApplyFiniteContactCore(SketchDraft draft)
    {
        if(SelectedConstraint is not {} item||!draft.Value.Constraints.Any(c=>c.Id==item.Id&&c is FiniteParametricContactConstraint))
            throw new CadValidationException(Strings.FiniteContactHint);
        var old=draft.Value.Constraints.OfType<FiniteParametricContactConstraint>().Single(c=>c.Id==item.Id);
        var intent=ContactIntent(old);
        if(intent==old){pendingFiniteContact=false;return;}
        var candidate=draft.Value with{Constraints=draft.Value.Constraints.Replace(old,intent)};
        _=ParameterGraph.Evaluate(document.Session.Snapshot with{Sketches=document.Session.Snapshot.Sketches.SetItem(candidate.Id,candidate)});
        draft.Change(_=>candidate);
        pendingFiniteContact=false;
    }
    private FiniteParametricContactConstraint ContactIntent(FiniteParametricContactConstraint c)
    {
        var next=c with{ParameterA=HasSlidingContactA?ContactSeedA:ContactParameterA,ParameterB=HasSlidingContactB?ContactSeedB:ContactParameterB,
            ParameterMode=ContactParameterMode,SlidingA=HasSlidingContactA?new(ContactSeedA,ContactLowerA,ContactUpperA):null,
            SlidingB=HasSlidingContactB?new(ContactSeedB,ContactLowerB,ContactUpperB):null,Branch=FiniteContactBranch,Continuity=ContactContinuity};
        if((next with{ParameterA=c.ParameterA,ParameterB=c.ParameterB})==c&&
           (next.SolvesA||next.ParameterA==c.ParameterA)&&(next.SolvesB||next.ParameterB==c.ParameterB))return c;
        return next;
    }
    private static string FiniteContactBranchLabel(FiniteContactTangentBranch branch)=>branch switch
    {FiniteContactTangentBranch.SameDirection=>Strings.FiniteContactSameDirection,FiniteContactTangentBranch.OppositeDirection=>Strings.FiniteContactOppositeDirection,_=>Strings.ContactInvalidTargets};
    private static string FiniteContactContinuityLabel(FiniteContactContinuity continuity)=>continuity switch
    {FiniteContactContinuity.Tangent=>Strings.FiniteContactG1,FiniteContactContinuity.Curvature=>Strings.FiniteContactG2,_=>Strings.ContactInvalidTargets};
    private static string FiniteContactParameterModeLabel(FiniteContactParameterMode mode)=>mode switch
    {FiniteContactParameterMode.Fixed=>Strings.SlidingContactFixed,FiniteContactParameterMode.SolveA=>Strings.SlidingContactSolveA,
     FiniteContactParameterMode.SolveB=>Strings.SlidingContactSolveB,FiniteContactParameterMode.SolveBoth=>Strings.SlidingContactSolveBoth,_=>Strings.ContactInvalidTargets};
}
