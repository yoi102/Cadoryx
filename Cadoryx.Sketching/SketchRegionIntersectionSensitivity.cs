using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Sketching;

/// <summary>First source-coordinate sensitivities at an already proven native
/// intersection. This is not a root finder, branch selector or topology proof.</summary>
public sealed record SketchRegionCurveSensitivity(Point2d Position,Point2d NativeDerivative,ImmutableArray<Point2d> PositionGradient);
public sealed record SketchRegionParameterSensitivity(ImmutableArray<double> First,ImmutableArray<double> Second);

public static class SketchRegionIntersectionSensitivity
{
    public static SketchRegionParameterSensitivity Differentiate(SketchRegionCurveSensitivity first,SketchRegionCurveSensitivity second)
    {
        if(first.PositionGradient.IsDefault||second.PositionGradient.IsDefault||first.PositionGradient.Length!=second.PositionGradient.Length||
            first.PositionGradient.Length>256)throw new CadValidationException("Intersection sensitivity exceeds the existing variable budget or has mismatched coordinates.");
        CadGuard.Finite(first.Position.X,first.Position.Y,second.Position.X,second.Position.Y,
            first.NativeDerivative.X,first.NativeDerivative.Y,second.NativeDerivative.X,second.NativeDerivative.Y);
        if(double.Hypot(first.Position.X-second.Position.X,first.Position.Y-second.Position.Y)>1e-7)
            throw new CadValidationException("Intersection sensitivities require the same proven native intersection point.");
        double a=double.Hypot(first.NativeDerivative.X,first.NativeDerivative.Y),b=double.Hypot(second.NativeDerivative.X,second.NativeDerivative.Y);
        if(!double.IsFinite(a)||!double.IsFinite(b)||a<=1e-15||b<=1e-15)
            throw new CadValidationException("Intersection sensitivity has a singular native tangent.");
        double ax=first.NativeDerivative.X/a,ay=first.NativeDerivative.Y/a,bx=second.NativeDerivative.X/b,by=second.NativeDerivative.Y/b;
        double determinant=ay*bx-ax*by;
        if(Math.Abs(determinant)<=1e-9)throw new CadValidationException("An ambiguous tangent intersection cannot provide finite implicit parameter derivatives.");
        var u=ImmutableArray.CreateBuilder<double>(first.PositionGradient.Length);var v=ImmutableArray.CreateBuilder<double>(u.Capacity);
        for(int i=0;i<first.PositionGradient.Length;i++)
        {
            var ga=first.PositionGradient[i];var gb=second.PositionGradient[i];CadGuard.Finite(ga.X,ga.Y,gb.X,gb.Y);
            double x=gb.X-ga.X,y=gb.Y-ga.Y;
            double du=(bx*y-by*x)/(determinant*a),dv=(ax*y-ay*x)/(determinant*b);CadGuard.Finite(du,dv);
            u.Add(du);v.Add(dv);
        }
        return new(u.ToImmutable(),v.ToImmutable());
    }
}
