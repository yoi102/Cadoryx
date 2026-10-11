using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cadoryx.Db;

[JsonPolymorphic(TypeDiscriminatorPropertyName="kind")]
[JsonDerivedType(typeof(ConstantContourRadius),"constant")]
[JsonDerivedType(typeof(LinearContourRadius),"linear")]
[JsonDerivedType(typeof(SmoothContourRadius),"smooth")]
[JsonDerivedType(typeof(SampledContourRadius),"sampled")]
[JsonDerivedType(typeof(SplineContourRadius),"spline")]
public abstract record ContourRadiusLaw
{
    public abstract void Validate();
    protected static void Radius(params double[] values)
    {CadGuard.Finite(values);if(values.Any(v=>v<=1e-6||v>100000))throw new CadValidationException("Contour radii must be between 0.000001 and 100000 mm.");}
}
public sealed record ConstantContourRadius(double Value):ContourRadiusLaw
{public override void Validate()=>Radius(Value);}
public sealed record LinearContourRadius(double First,double Last):ContourRadiusLaw
{public override void Validate()=>Radius(First,Last);}
public sealed record SmoothContourRadius(double First,double Last,double FirstDerivative=0,double LastDerivative=0):ContourRadiusLaw
{
    public override void Validate()
    {
        CadGuard.Finite(FirstDerivative,LastDerivative);
        // A normalized cubic Hermite law has these four Bezier control values.
        Radius(First,First+FirstDerivative/3,Last-LastDerivative/3,Last);
    }
}
public sealed record ContourRadiusSample(Guid Id,double Parameter,double Radius)
{
    public void Validate(){if(Id==Guid.Empty)throw new CadValidationException("A radius sample requires a stable identity.");CadGuard.Finite(Parameter,Radius);if(Parameter<0||Parameter>1||Radius<=1e-6||Radius>100000)throw new CadValidationException("Invalid normalized radius sample.");}
}
public sealed record SampledContourRadius(ImmutableArray<ContourRadiusSample> Samples):ContourRadiusLaw
{
    public override void Validate()
    {
        if(Samples.IsDefault||Samples.Length is <2 or >256||Samples.Any(s=>s is null)||Samples.Select(s=>s.Id).Distinct().Count()!=Samples.Length)
            throw new CadValidationException("A sampled radius law requires 2-256 distinct samples.");
        foreach(var sample in Samples)sample.Validate();
        if(Samples[0].Parameter!=0||Samples[^1].Parameter!=1||Samples.Zip(Samples.Skip(1)).Any(p=>p.Second.Parameter<=p.First.Parameter))
            throw new CadValidationException("Samples must strictly increase over the full normalized contour [0,1].");
        // Native interpolation additionally proves a positive control hull before it can be built.
    }
}
public sealed record ContourRadiusPole(Guid Id,double Radius);
public sealed record SplineContourRadius(ImmutableArray<ContourRadiusPole> Poles,ImmutableArray<double> Knots,
    ImmutableArray<int> Multiplicities,int Degree):ContourRadiusLaw
{
    public override void Validate()
    {
        if(Degree is <1 or >8||Poles.IsDefault||Poles.Length<Degree+1||Poles.Length>256||Poles.Any(p=>p is null||p.Id==Guid.Empty)||
            Poles.Select(p=>p.Id).Distinct().Count()!=Poles.Length||Knots.IsDefault||Knots.Length is <2 or >256||Multiplicities.IsDefault||Knots.Length!=Multiplicities.Length)
            throw new CadValidationException("Invalid bounded contour spline law dimensions or identities.");
        Radius(Poles.Select(p=>p.Radius).ToArray());CadGuard.Finite(Knots.ToArray());
        if(Knots[0]!=0||Knots[^1]!=1||Multiplicities[0]!=Degree+1||Multiplicities[^1]!=Degree+1||
            Knots.Zip(Knots.Skip(1)).Any(p=>p.Second<=p.First)||Multiplicities.Skip(1).SkipLast(1).Any(m=>m<1||m>Degree)||
            Multiplicities.Sum(m=>(long)m)!=Poles.Length+Degree+1L)
            throw new CadValidationException("Radius splines must be clamped, positive and normalized over [0,1].");
    }
}
