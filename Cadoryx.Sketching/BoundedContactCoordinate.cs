using Cadoryx.Db;

namespace Cadoryx.Sketching;

/// <summary>Open finite contact interval mapped to an unconstrained local coordinate.
/// Rounded endpoints and singular gradients are rejected, never clamped to a valid parameter.</summary>
internal sealed record BoundedContactCoordinate
{
    internal double Lower {get;}
    internal double Upper {get;}
    internal BoundedContactCoordinate(double lower,double upper)
    {
        if(!double.IsFinite(lower)||!double.IsFinite(upper)||lower<0||upper>1||lower>=upper)
            throw new CadValidationException("Contact bounds must satisfy 0 <= lower < upper <= 1.");
        Lower=lower;Upper=upper;
    }
    internal double Coordinate(double parameter)
    {
        if(!double.IsFinite(parameter)||parameter<=Lower||parameter>=Upper||parameter<=0||parameter>=1)
            throw new CadValidationException("A contact seed or solution must be strictly inside its allowed interval.");
        // Separate logarithms avoid overflow in an otherwise valid distance ratio.
        double coordinate=Math.Log(parameter-Lower)-Math.Log(Upper-parameter);
        if(!double.IsFinite(coordinate))throw new CadValidationException("Contact parameter has no finite local coordinate.");
        return coordinate;
    }
    internal bool TryEvaluate(double coordinate,int index,int count,out SketchContactDifferentials.Scalar parameter)
    {
        parameter=default;
        if(!double.IsFinite(coordinate)||index<0||index>=count)return false;
        double e=Math.Exp(-Math.Abs(coordinate)),tail=e/(1+e),range=Upper-Lower;
        double value=coordinate>=0?Upper-range*tail:Lower+range*tail;
        double derivative=range*tail/(1+e);
        if(!double.IsFinite(value)||value<=Lower||value>=Upper||value<=0||value>=1||
           !double.IsFinite(derivative)||derivative<=0)return false;
        parameter=SketchContactDifferentials.Scalar.Variable(coordinate,index,count).Compose(value,derivative);
        return true;
    }
}
