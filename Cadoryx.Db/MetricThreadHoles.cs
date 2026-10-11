using System.Globalization;

namespace Cadoryx.Db;

public enum ThreadHand { Right=0,Left=1 }
public enum ThreadHoleRepresentation { Symbolic=0,Modeled=1 }
/// <summary>Explicit thread intent; symbolic intent does not add helical faces to the BRep.</summary>
public sealed record MetricThreadSpec(int NominalMm,double PitchMm,ThreadHand Hand=ThreadHand.Right,
    bool Through=true,double DepthMm=0,ThreadHoleRepresentation Representation=ThreadHoleRepresentation.Symbolic,string? Profile=null)
{
    public void Validate()
    {
        var size=MetricThreadHoles.Size(NominalMm);
        if(!Enum.IsDefined(Hand)||!Enum.IsDefined(Representation)||!double.IsFinite(PitchMm)||Math.Abs(PitchMm-size.PitchMm)>1e-12||
            !double.IsFinite(DepthMm)||(Through?DepthMm!=0:DepthMm is <=1e-6 or >1_000_000)||
            (Representation==ThreadHoleRepresentation.Symbolic?Profile is not null:Profile!=MetricThreadHoles.ModeledProfile)||
            Representation==ThreadHoleRepresentation.Modeled&&!Through&&(DepthMm<PitchMm||DepthMm/PitchMm>64))
            throw new CadValidationException("Invalid common metric coarse thread specification.");
    }
    public void ValidateForHole(double pilotDiameter,bool holeThrough,double holeDepth,HoleHead head,MetricClearanceSpec? clearance,double toleranceMm=1e-10)
    {
        Validate();
        if(!double.IsFinite(toleranceMm)||toleranceMm is <0 or >1e-6||!double.IsFinite(pilotDiameter)||!double.IsFinite(holeDepth)||(holeThrough?holeDepth!=0:holeDepth is <=1e-6 or >1_000_000)||
            clearance is not null||head!=HoleHead.Plain||Math.Abs(pilotDiameter-MetricThreadHoles.Size(NominalMm).PilotDiameterMm)>toleranceMm||
            Through!=holeThrough||!Through&&DepthMm>holeDepth+toleranceMm)
            throw new CadValidationException("Thread intent requires its explicit plain pilot hole, matching through mode and sufficient drilled depth.");
    }
    public string Designation=>"M"+NominalMm.ToString(CultureInfo.InvariantCulture)+" x "+PitchMm.ToString("0.###",CultureInfo.InvariantCulture)+(Hand==ThreadHand.Left?" LH":"");
}
public readonly record struct MetricThreadSize(int NominalMm,double PitchMm,double PilotDiameterMm);
/// <summary>Versioned common workshop pilot choices. Not a complete standards or tolerance-compliance catalog.</summary>
public static class MetricThreadHoles
{
    public const string Catalog="metric-coarse-thread-pilot-common-v1";
    public const string ModeledProfile="metric-basic-60-internal-v1";
    private static readonly MetricThreadSize[] Values=[new(3,.5,2.5),new(4,.7,3.3),new(5,.8,4.2),new(6,1,5),
        new(8,1.25,6.8),new(10,1.5,8.5),new(12,1.75,10.2),new(16,2,14)];
    public static IReadOnlyList<MetricThreadSize> Sizes=>Array.AsReadOnly(Values);
    public static MetricThreadSize Size(int nominal)=>Values.SingleOrDefault(s=>s.NominalMm==nominal) is {NominalMm:>0} size?size:
        throw new CadValidationException("Unsupported common metric coarse thread size.");
    public static MetricThreadSpec Create(int nominal,ThreadHand hand=ThreadHand.Right,bool through=true,double depthMm=0,
        ThreadHoleRepresentation representation=ThreadHoleRepresentation.Symbolic)
    {
        var spec=new MetricThreadSpec(nominal,Size(nominal).PitchMm,hand,through,depthMm,representation,
            representation==ThreadHoleRepresentation.Modeled?ModeledProfile:null);spec.Validate();return spec;
    }
}
