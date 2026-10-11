using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Cadoryx.Db;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cadoryx.ViewModels;

public enum ContourLawKind { Constant, Linear, Smooth, Sampled, Spline }
public partial class ContourSampleViewModel:ObservableObject
{
    public Guid Id{get;}
    public string Identity=>Id.ToString("N")[..8];
    [ObservableProperty] private double parameter,radius;
    public ContourSampleViewModel(ContourRadiusSample row){Id=row.Id;parameter=row.Parameter;radius=row.Radius;}
    public ContourRadiusSample Value=>new(Id,Parameter,Radius);
}
public partial class ContourPoleViewModel:ObservableObject
{
    public Guid Id{get;}
    public string Identity=>Id.ToString("N")[..8];
    [ObservableProperty] private double radius;
    public ContourPoleViewModel(ContourRadiusPole row){Id=row.Id;radius=row.Radius;}
    public ContourRadiusPole Value=>new(Id,Radius);
}
public partial class ContourKnotViewModel:ObservableObject
{
    [ObservableProperty] private double parameter;
    [ObservableProperty] private int multiplicity;
    public ContourKnotViewModel(double parameter,int multiplicity){this.parameter=parameter;this.multiplicity=multiplicity;}
}
public partial class ContourProgramViewModel:ObservableObject,IDisposable
{
    public Guid Id{get;}
    public ExactTopologySelection Seed{get;private set;}
    public ExactTopologySelection? SupportFace{get;private set;}
    public ContourChainEvidence Chain{get;private set;}
    public string Label=>Strings.Edge+" #"+Seed.FullTopologyIndex+" · "+Id.ToString("N")[..8];
    public string ChainLabel=>string.Format(Strings.ContourChainSummary,Chain.Members.Length,Chain.Length,Chain.IsClosed?Strings.ContourClosed:Strings.ContourOpen);
    public string SupportLabel=>SupportFace is {} face?Strings.Face+" #"+face.FullTopologyIndex:"";
    public IEnumerable<ContourChainMember> ChainMembers=>Chain.Members;
    public bool IsConstant=>LawKind==ContourLawKind.Constant;
    public bool HasEnds=>LawKind is ContourLawKind.Linear or ContourLawKind.Smooth;
    public bool IsSmooth=>LawKind==ContourLawKind.Smooth;
    public bool IsSampled=>LawKind==ContourLawKind.Sampled;
    public bool IsSpline=>LawKind==ContourLawKind.Spline;
    public ObservableCollection<ContourSampleViewModel> Samples{get;}=[];
    public ObservableCollection<ContourPoleViewModel> Poles{get;}=[];
    public ObservableCollection<ContourKnotViewModel> Knots{get;}=[];
    private readonly HashSet<ObservableObject> observed=[];
    private bool loading;
    public event EventHandler? Changed;
    [ObservableProperty] private ContourLawKind lawKind;
    [ObservableProperty] private double radius=1,firstRadius=1,lastRadius=1,firstDerivative,lastDerivative,distance=1,secondDistance=1;
    [ObservableProperty] private int degree=2;
    [ObservableProperty] private bool useSecondDistance;
    [ObservableProperty] private ContourSampleViewModel? selectedSample;
    [ObservableProperty] private ContourPoleViewModel? selectedPole;
    [ObservableProperty] private ContourKnotViewModel? selectedKnot;
    public ContourProgramViewModel(ContourLocalProgram value)
    {
        Id=value.Id;Seed=value.Seed;SupportFace=value.SupportFace;Chain=value.Chain;loading=true;
        Distance=value.Distance??1;UseSecondDistance=value.SecondDistance is not null;SecondDistance=value.SecondDistance??1;
        switch(value.Radius)
        {
            case ConstantContourRadius r:LawKind=ContourLawKind.Constant;Radius=r.Value;break;
            case LinearContourRadius r:LawKind=ContourLawKind.Linear;FirstRadius=r.First;LastRadius=r.Last;break;
            case SmoothContourRadius r:LawKind=ContourLawKind.Smooth;FirstRadius=r.First;LastRadius=r.Last;FirstDerivative=r.FirstDerivative;LastDerivative=r.LastDerivative;break;
            case SampledContourRadius r:LawKind=ContourLawKind.Sampled;foreach(var s in r.Samples)Samples.Add(new(s));break;
            case SplineContourRadius r:
                LawKind=ContourLawKind.Spline;Degree=r.Degree;foreach(var p in r.Poles)Poles.Add(new(p));
                for(int i=0;i<r.Knots.Length;i++)Knots.Add(new(r.Knots[i],r.Multiplicities[i]));break;
        }
        if(Samples.Count==0){Samples.Add(new(new(Guid.NewGuid(),0,1)));Samples.Add(new(new(Guid.NewGuid(),1,1)));}
        if(Poles.Count==0)for(int i=0;i<3;i++)Poles.Add(new(new(Guid.NewGuid(),1)));
        if(Knots.Count==0){Knots.Add(new(0,3));Knots.Add(new(1,3));}
        Samples.CollectionChanged+=OnCollectionChanged;Poles.CollectionChanged+=OnCollectionChanged;Knots.CollectionChanged+=OnCollectionChanged;
        ObserveRows();loading=false;
    }
    public void Reselect(ExactTopologySelection seed,ExactTopologySelection? support,ContourChainEvidence chain)
    {
        Seed=seed;SupportFace=support;Chain=chain;
        foreach(string property in new[]{nameof(Seed),nameof(SupportFace),nameof(Chain),nameof(Label),nameof(SupportLabel),nameof(ChainLabel),nameof(ChainMembers)})OnPropertyChanged(property);
    }
    partial void OnLawKindChanged(ContourLawKind value)
    {foreach(string p in new[]{nameof(IsConstant),nameof(HasEnds),nameof(IsSmooth),nameof(IsSampled),nameof(IsSpline)})OnPropertyChanged(p);}
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(!loading&&e.PropertyName is not (nameof(SelectedSample) or nameof(SelectedPole) or nameof(SelectedKnot)))Changed?.Invoke(this,EventArgs.Empty);
    }
    private void OnCollectionChanged(object? sender,System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {ObserveRows();Changed?.Invoke(this,EventArgs.Empty);}
    private void ObserveRows()
    {
        foreach(var row in observed)row.PropertyChanged-=OnRowChanged;observed.Clear();
        foreach(var row in Samples.Cast<ObservableObject>().Concat(Poles).Concat(Knots)){observed.Add(row);row.PropertyChanged+=OnRowChanged;}
    }
    private void OnRowChanged(object? sender,PropertyChangedEventArgs e)=>Changed?.Invoke(this,EventArgs.Empty);
    [RelayCommand] private void AddSample()
    {
        if(Samples.Count>=256)return;
        int index=SelectedSample is {} selected?Samples.IndexOf(selected):0;if(index<0||index>=Samples.Count-1)index=Samples.Count-2;
        if(index<0){Samples.Add(new(new(Guid.NewGuid(),0,1)));return;}
        var a=Samples[index];var b=Samples[index+1];var row=new ContourSampleViewModel(new(Guid.NewGuid(),(a.Parameter+b.Parameter)/2,(a.Radius+b.Radius)/2));
        Samples.Insert(index+1,row);SelectedSample=row;
    }
    [RelayCommand] private void RemoveSample(){if(SelectedSample is {} row){Samples.Remove(row);SelectedSample=Samples.FirstOrDefault();}}
    [RelayCommand] private void AddPole(){if(Poles.Count>=256)return;var row=new ContourPoleViewModel(new(Guid.NewGuid(),SelectedPole?.Radius??1));var index=SelectedPole is {} p?Poles.IndexOf(p)+1:Poles.Count;Poles.Insert(index,row);SelectedPole=row;}
    [RelayCommand] private void RemovePole(){if(SelectedPole is {} row){Poles.Remove(row);SelectedPole=Poles.FirstOrDefault();}}
    [RelayCommand] private void MovePoleUp(){if(SelectedPole is {} row&&Poles.IndexOf(row)>0)Poles.Move(Poles.IndexOf(row),Poles.IndexOf(row)-1);}
    [RelayCommand] private void MovePoleDown(){if(SelectedPole is {} row&&Poles.IndexOf(row)<Poles.Count-1)Poles.Move(Poles.IndexOf(row),Poles.IndexOf(row)+1);}
    [RelayCommand] private void AddKnot(){if(Knots.Count>=256)return;int index=SelectedKnot is {} k?Knots.IndexOf(k):Knots.Count-2;index=Math.Clamp(index,0,Math.Max(0,Knots.Count-2));var row=new ContourKnotViewModel(Knots.Count>index+1?(Knots[index].Parameter+Knots[index+1].Parameter)/2:.5,1);Knots.Insert(Math.Min(index+1,Knots.Count),row);SelectedKnot=row;}
    [RelayCommand] private void RemoveKnot(){if(SelectedKnot is {} row){Knots.Remove(row);SelectedKnot=Knots.FirstOrDefault();}}
    public ContourLocalProgram Value(LocalFeatureOperation operation)
    {
        ContourRadiusLaw? law=operation==LocalFeatureOperation.Chamfer?null:LawKind switch
        {
            ContourLawKind.Constant=>new ConstantContourRadius(Radius),ContourLawKind.Linear=>new LinearContourRadius(FirstRadius,LastRadius),
            ContourLawKind.Smooth=>new SmoothContourRadius(FirstRadius,LastRadius,FirstDerivative,LastDerivative),
            ContourLawKind.Sampled=>new SampledContourRadius(Samples.Select(s=>s.Value).ToImmutableArray()),
            ContourLawKind.Spline=>new SplineContourRadius(Poles.Select(p=>p.Value).ToImmutableArray(),Knots.Select(k=>k.Parameter).ToImmutableArray(),Knots.Select(k=>k.Multiplicity).ToImmutableArray(),Degree),
            _=>throw new CadValidationException("Unknown contour radius law.")
        };
        return new(Id,Seed,Chain,law,operation==LocalFeatureOperation.Chamfer?SupportFace:null,operation==LocalFeatureOperation.Chamfer?Distance:null,operation==LocalFeatureOperation.Chamfer&&UseSecondDistance?SecondDistance:null);
    }
    public void Dispose()
    {foreach(var row in observed)row.PropertyChanged-=OnRowChanged;observed.Clear();Samples.CollectionChanged-=OnCollectionChanged;Poles.CollectionChanged-=OnCollectionChanged;Knots.CollectionChanged-=OnCollectionChanged;}
}
