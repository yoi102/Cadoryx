using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Explicit author knots and multiplicities, without duplicated point coordinates.</summary>
public sealed record CubicSplineKnotVector(ImmutableArray<double> Knots,ImmutableArray<int> Multiplicities)
{
    public CubicSplineDefinition WithControls(ImmutableArray<Point2d> controls)=>new(controls,Knots,Multiplicities);
    public static CubicSplineKnotVector From(CubicSplineDefinition definition)=>new(definition.Knots,definition.Multiplicities);
    public CubicSplineKnotVector Reverse()
    {
        if(Knots.IsDefaultOrEmpty||Multiplicities.IsDefault||Knots.Length!=Multiplicities.Length)
            throw new CadValidationException("Uninitialized cubic knots.");
        double first=Knots[0],last=Knots[^1];
        return new(Knots.Reverse().Select(k=>first+(last-k)).ToImmutableArray(),Multiplicities.Reverse().ToImmutableArray());
    }
    public static CubicSplineDefinition Definition(ImmutableArray<Point2d> controls,CubicSplineKnotVector? knots)=>
        knots is null?CubicSplineDefinition.Uniform(controls):knots.WithControls(controls);
    public static bool Same(ImmutableArray<Point2d> first,CubicSplineKnotVector? firstKnots,
        ImmutableArray<Point2d> second,CubicSplineKnotVector? secondKnots)
    {
        if(!first.SequenceEqual(second))return false;
        var a=Definition(first,firstKnots);var b=Definition(second,secondKnots);
        return a.Knots.SequenceEqual(b.Knots)&&a.Multiplicities.SequenceEqual(b.Multiplicities);
    }
}
