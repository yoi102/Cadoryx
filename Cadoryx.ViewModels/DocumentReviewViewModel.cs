using System.Collections.ObjectModel;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using Cadoryx.Rendering;
using Cadoryx.ViewModels.Toolboxes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public partial class DocumentReviewViewModel : ObservableObject,IDisposable
{
    private readonly CadDocumentViewModel document;
    private readonly IGeometryInspector? inspector;
    private readonly ReviewVisibility visibility=new();
    private CancellationTokenSource? pending;
    private long sequence;
    private bool disposed;
    public event EventHandler? ViewChanged;
    public event EventHandler<Bounds3d>? FocusRequested;
    public CadCamera? SecondaryCamera {get;set;}
    public int ActivePane {get;set;}
    [ObservableProperty] private bool splitView;
    [ObservableProperty] private bool sectionEnabled;
    [ObservableProperty] private SectionAxis sectionAxis=SectionAxis.Z;
    [ObservableProperty] private double sectionOffsetMm;
    [ObservableProperty] private bool sectionReverse;
    [ObservableProperty] private bool slabEnabled;
    [ObservableProperty] private double slabThicknessMm=10;
    [ObservableProperty] private bool isMeasuring;
    [ObservableProperty] private string measurementStatus="";
    public GeometryInspection? Result {get;private set;}
    public ObservableCollection<PropertyRowViewModel> Rows {get;}=[];
    public IReadOnlyList<SectionAxis> Axes {get;}=Enum.GetValues<SectionAxis>();
    public SectionView Section {get;private set;}=new();
    public bool IsFiltered=>visibility.IsFiltered;
    public DocumentReviewViewModel(CadDocumentViewModel document,IGeometryKernel kernel)
    {
        this.document=document;inspector=kernel as IGeometryInspector;
        document.Selection.Changed+=Invalidate;document.SceneChanged+=Invalidate;
        RefreshEngineeringReview();
    }
    public CadScene Filter(CadScene scene)=>visibility.Apply(scene);
    private IEnumerable<InstanceBodyKey> SelectedKeys()
    {
        if(document.Selection.Items.Length>0)return document.Selection.Items.Select(t=>new InstanceBodyKey(t.Path,t.BodyId));
        if(document.Selection.Occurrence is not {} path)return [];
        return document.Scene.Items.Where(i=>i.Path.DocumentId==path.DocumentId&&i.Path.Slots.Length>=path.Slots.Length&&
            i.Path.Slots.Take(path.Slots.Length).SequenceEqual(path.Slots)).Select(i=>new InstanceBodyKey(i.Path,i.BodyId));
    }
    private bool HasSelection()=>!disposed&&SelectedKeys().Any();
    private void Changed(){OnPropertyChanged(nameof(IsFiltered));ShowAllCommand.NotifyCanExecuteChanged();ViewChanged?.Invoke(this,EventArgs.Empty);}
    [RelayCommand(CanExecute=nameof(HasSelection))] private void Isolate(){visibility.Isolate(SelectedKeys());Changed();}
    [RelayCommand(CanExecute=nameof(HasSelection))] private void Hide(){visibility.Hide(SelectedKeys());Changed();}
    [RelayCommand(CanExecute=nameof(IsFiltered))] public void ShowAll(){visibility.Reset();Changed();}
    [RelayCommand(CanExecute=nameof(HasSelection))] private void Focus()
    {
        var keys=SelectedKeys().ToHashSet();
        if(SceneEnvelope.Measure(Filter(document.Scene).Items.Where(i=>keys.Contains(new(i.Path,i.BodyId)))) is {} b)
            FocusRequested?.Invoke(this,b);
    }
    [RelayCommand] public void ApplySection()
    {
        try
        {
            var next=new SectionView(SectionEnabled,SectionAxis,SectionOffsetMm,SectionReverse,SlabEnabled?SlabThicknessMm:null);
            next.Validate();Section=next;Changed();
        }
        catch(Exception ex){document.Report(ex);}
    }
    [RelayCommand] private void ResetSection()
    {
        SectionEnabled=false;SlabEnabled=false;SectionOffsetMm=0;SectionReverse=false;SectionAxis=SectionAxis.Z;ApplySection();
    }
    partial void OnSplitViewChanged(bool value){if(!value)ActivePane=0;}
    private void Invalidate(object? sender,EventArgs e)
    {
        FocusCommand.NotifyCanExecuteChanged();IsolateCommand.NotifyCanExecuteChanged();HideCommand.NotifyCanExecuteChanged();
        sequence++;pending?.Cancel();Result=null;Rows.Clear();MeasurementStatus="";
        InvalidateOperations();
        RefreshEngineeringReview();
    }
    [RelayCommand] private void CancelMeasurement()=>pending?.Cancel();
    [RelayCommand] public async Task MeasureAsync()
    {
        if(disposed||inspector is null||IsMeasuring)return;
        using var cancel=new CancellationTokenSource();pending=cancel;long request=++sequence;IsMeasuring=true;
        Result=null;Rows.Clear();MeasurementStatus=R("ExactMeasuring");
        try
        {
            using var capture=document.Session.Capture();
            var selection=document.Selection.Items;
            var input=InspectionSelection.Resolve(capture.Snapshot,selection);
            var result=await inspector.InspectAsync(input,document.Session.Assets,cancel.Token);
            if(disposed||request!=sequence||cancel.IsCancellationRequested||document.Session.Snapshot.StateId!=capture.Snapshot.StateId)return;
            Result=result;RefreshLanguage();
        }
        catch(OperationCanceledException){if(request==sequence)MeasurementStatus=R("ExactCancelled");}
        catch(Exception ex){if(request==sequence){MeasurementStatus=ex.Message;document.Report(ex);}}
        finally {pending=null;IsMeasuring=false;}
    }
    public void RefreshLanguage()
    {
        RefreshAnalysisLanguage();
        if(Result is not {} result)return;
        Rows.Clear();var settings=document.Session.Snapshot.Settings;
        double factor=DocumentSettings.MillimetersPerUnit(settings.DisplayUnit);
        string unit=settings.DisplayUnit switch{LengthUnit.Centimeter=>"cm",LengthUnit.Meter=>"m",LengthUnit.Inch=>"in",_=>"mm"};
        string Number(double v)=>v.ToString("F"+settings.DecimalPlaces);
        string Point(Vector3d p)=>$"({Number(p.X/factor)}, {Number(p.Y/factor)}, {Number(p.Z/factor)}) {unit}";
        void Row(string key,string value)=>Rows.Add(new(R(key),value));
        Row("ExactArea",Number(result.Bodies.Sum(b=>b.AreaMm2)/(factor*factor))+" "+unit+"²");
        if(result.Bodies.All(b=>b.VolumeMm3.HasValue))
        {
            double volume=result.Bodies.Sum(b=>b.VolumeMm3!.Value);
            Row("ExactVolume",Number(volume/(factor*factor*factor))+" "+unit+"³");
            if(volume>1e-15&&result.Bodies.All(b=>b.VolumeCentroidMm.HasValue))
                Row("ExactCentroid",Point(result.Bodies.Aggregate(Vector3d.Zero,(p,b)=>p+b.VolumeCentroidMm!.Value*b.VolumeMm3!.Value)/volume));
        }
        Row("ExactTopology",$"{result.Bodies.Sum(b=>b.Faces)} / {result.Bodies.Sum(b=>b.Edges)} / {result.Bodies.Sum(b=>b.Solids)}");
        if(result.Distance is {} d)
        {
            Row("ExactDistance",Number(d.DistanceMm/factor)+" "+unit);
            Row("ExactFirstPoint",Point(d.PointOnFirstMm));Row("ExactSecondPoint",Point(d.PointOnSecondMm));
        }
        MeasurementStatus=R("ExactMeasurementHint");
    }
    private static string R(string key)=>Strings.ResourceManager.GetString(key,Strings.Culture)??key;
    public void Dispose(){disposed=true;Invalidate(this,EventArgs.Empty);document.Selection.Changed-=Invalidate;document.SceneChanged-=Invalidate;}
}
