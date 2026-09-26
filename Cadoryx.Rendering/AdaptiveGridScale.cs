namespace Cadoryx.Rendering;

/// <summary>Choose a visual 1-2-5 multiple; the document's snapping interval is unchanged.</summary>
public static class AdaptiveGridScale
{
    public static double Choose(double modelSpacingMm,double pixelsPerMm,double minimumPixels=12)
    {
        if(!double.IsFinite(modelSpacingMm)||modelSpacingMm<=0||
           !double.IsFinite(pixelsPerMm)||pixelsPerMm<=0||
           !double.IsFinite(minimumPixels)||minimumPixels<=0)
            throw new ArgumentOutOfRangeException(nameof(modelSpacingMm));
        double required=minimumPixels/(modelSpacingMm*pixelsPerMm);
        if(required<=1)return modelSpacingMm;
        double decade=Math.Pow(10,Math.Floor(Math.Log10(required)));
        foreach(double factor in new[]{1d,2d,5d,10d})
            if(decade*factor>=required)return Math.Min(1_000_000,modelSpacingMm*decade*factor);
        return Math.Min(1_000_000,modelSpacingMm*decade*10);
    }
}
