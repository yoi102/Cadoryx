using System.Collections.ObjectModel;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Rendering;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public sealed record InterferenceRow(BodyPairFinding Finding,string Label,string Details);
public partial class DocumentReviewViewModel
{
    private CancellationTokenSource? analysisCancellation;
    private long analysisSequence;
    private bool navigatingFinding;
    private InterferenceReport? interference;
    [ObservableProperty] private bool isReviewBusy;
    [ObservableProperty] private bool interferenceBroadPhase=true;
    [ObservableProperty] private string reviewStatus="";
    [ObservableProperty] private string sectionName=R("SectionCurves");
    [ObservableProperty] private InterferenceRow? selectedInterference;
    public ObservableCollection<InterferenceRow> InterferenceRows {get;}=[];
    partial void OnSectionAxisChanged(SectionAxis value)=>InvalidateOperations();
    partial void OnSectionOffsetMmChanged(double value)=>InvalidateOperations();
    private void InvalidateOperations()
    {
        if(navigatingFinding)return;
        analysisSequence++;analysisCancellation?.Cancel();interference=null;InterferenceRows.Clear();SelectedInterference=null;ReviewStatus="";
    }
    [RelayCommand] private void CancelReviewOperation()=>analysisCancellation?.Cancel();
    [RelayCommand] public async Task CreateSectionAsync()
    {
        if(disposed||IsReviewBusy||document.IsReadOnly)return;
        using var cancel=new CancellationTokenSource();analysisCancellation=cancel;IsReviewBusy=true;
        try
        {
            var inputs=InspectionSelection.Resolve(document.Session.Snapshot,document.Selection.Items);
            var normal=SectionAxis switch{SectionAxis.X=>new Vector3d(1,0,0),SectionAxis.Y=>new(0,1,0),_=>Vector3d.UnitZ};
            ReviewStatus=R("ReviewCalculating");
            await document.Session.ExecuteAsync(new CreateSectionCommand(inputs,new(normal,SectionOffsetMm),SectionName,AssociativeSection,
                SectionFaces?SectionOutput.Faces:SectionOutput.Curves),cancel.Token);
            if(!disposed){ShowAll();ReviewStatus=R("SectionCreated");}
        }
        catch(OperationCanceledException){if(!disposed)ReviewStatus=R("ExactCancelled");}
        catch(Exception ex){if(!disposed){ReviewStatus=ex.Message;document.Report(ex);}}
        finally{analysisCancellation=null;IsReviewBusy=false;}
    }
    [RelayCommand] public async Task CheckInterferenceAsync()
    {
        if(disposed||IsReviewBusy||inspector is not IGeometryReviewKernel review)return;
        using var cancel=new CancellationTokenSource();analysisCancellation=cancel;long request=++analysisSequence;IsReviewBusy=true;
        interference=null;InterferenceRows.Clear();SelectedInterference=null;ReviewStatus=R("ReviewCalculating");
        try
        {
            using var capture=document.Session.Capture();
            var inputs=InspectionSelection.Resolve(capture.Snapshot,document.Selection.Items);GeometryInstanceGuard.Validate(capture.Snapshot,inputs);
            var result=await (InterferenceBroadPhase?review.CheckInterferenceCandidatesAsync(inputs,capture.Snapshot.Settings.LinearToleranceMm,document.Session.Assets,cancel.Token):
                review.CheckInterferenceAsync(inputs,capture.Snapshot.Settings.LinearToleranceMm,document.Session.Assets,cancel.Token));
            if(disposed||request!=analysisSequence||cancel.IsCancellationRequested||capture.Snapshot.StateId!=document.Session.Snapshot.StateId)return;
            interference=result;RefreshAnalysisLanguage();
        }
        catch(OperationCanceledException){if(request==analysisSequence)ReviewStatus=R("ExactCancelled");}
        catch(Exception ex){if(request==analysisSequence){ReviewStatus=ex.Message;document.Report(ex);}}
        finally{analysisCancellation=null;IsReviewBusy=false;}
    }
    [RelayCommand] private void LocateInterference()
    {
        if(SelectedInterference is not {} row)return;
        try
        {
            var pair=row.Finding;GeometryInstanceGuard.Validate(document.Session.Snapshot,[pair.First,pair.Second]);
            navigatingFinding=true;
            document.Selection.Replace([new(pair.First.Path,pair.First.BodyId,pair.First.Geometry.Revision),new(pair.Second.Path,pair.Second.BodyId,pair.Second.Geometry.Revision)]);
            visibility.Isolate([new(pair.First.Path,pair.First.BodyId),new(pair.Second.Path,pair.Second.BodyId)]);Changed();Focus();
        }
        catch(Exception ex){document.Report(ex);}
        finally{navigatingFinding=false;}
    }
    private void RefreshAnalysisLanguage()
    {
        if(interference is not {} report)return;
        var snapshot=document.Session.Snapshot;var settings=snapshot.Settings;
        double factor=DocumentSettings.MillimetersPerUnit(settings.DisplayUnit);
        string unit=settings.DisplayUnit switch{LengthUnit.Centimeter=>"cm",LengthUnit.Meter=>"m",LengthUnit.Inch=>"in",_=>"mm"};
        string N(double v)=>v.ToString("F"+settings.DecimalPlaces);
        string Name(GeometryInstance i)=>OccurrencePlacement.Resolve(snapshot,i.Path).Slot.Name+" / "+snapshot.Bodies[i.BodyId].Name;
        var selected=SelectedInterference?.Finding;InterferenceRows.Clear();
        foreach(var pair in report.Pairs.OrderByDescending(p=>p.OverlapVolumeMm3).ThenBy(p=>p.DistanceMm))
        {
            var row=new InterferenceRow(pair,Name(pair.First)+" ↔ "+Name(pair.Second),
                R("Pair"+pair.Relation)+" · "+R("ExactDistance")+": "+N(pair.DistanceMm/factor)+" "+unit+" · "+R("PairOverlap")+": "+N(pair.OverlapVolumeMm3/(factor*factor*factor))+" "+unit+"³");
            InterferenceRows.Add(row);if(pair==selected)SelectedInterference=row;
        }
        ReviewStatus=string.Format(R("PairSummary"),report.Pairs.Length,report.Pairs.Count(p=>p.OverlapVolumeMm3>0));
        if(report.BroadPhaseSeparatedPairs>0)ReviewStatus+=" · "+string.Format(R("BroadPhaseSummary"),report.BroadPhaseSeparatedPairs);
    }
}
