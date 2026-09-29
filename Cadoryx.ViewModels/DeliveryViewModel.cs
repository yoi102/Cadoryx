using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cadoryx.ViewModels;

public sealed record DeliveryRequest(string Path,DeliveryOptions Options);
public interface IDeliveryHost
{
    Task<DeliveryRequest?> ShowAsync(DocumentSnapshot snapshot);
}
public partial class DeliveryViewModel:ObservableObject
{
    private readonly DocumentSnapshot snapshot;
    [ObservableProperty] private bool visibleOnly=true;
    [ObservableProperty] private bool includeStep=true;
    [ObservableProperty] private bool includeIges;
    [ObservableProperty] private bool includeStl;
    [ObservableProperty] private double linearDeflectionMm=.1;
    [ObservableProperty] private double angularDeflectionDegrees=28.64788975654116;
    [ObservableProperty] private int outputIndex;
    public BillOfMaterials Bom {get;private set;}
    public string Summary=>string.Format(Strings.DeliverySummary,Bom.Parts.Length,Bom.Parts.Sum(p=>p.Quantity));
    public DeliveryViewModel(DocumentSnapshot snapshot){this.snapshot=snapshot;Bom=BillOfMaterials.Create(snapshot);}
    partial void OnVisibleOnlyChanged(bool value)
    {Bom=BillOfMaterials.Create(snapshot,value);OnPropertyChanged(nameof(Bom));OnPropertyChanged(nameof(Summary));}
    public DeliveryOptions GetOptions()
    {
        var options=new DeliveryOptions((DeliveryOutput)OutputIndex,VisibleOnly,IncludeStep,IncludeIges,IncludeStl,
            LinearDeflectionMm,AngularDeflectionDegrees*Math.PI/180);options.Validate();return options;
    }
}
