using System.Globalization;

namespace Cadoryx.Db;

public static class DrawingScale
{
    /// <summary>Paper/model ratio: enlargement 2:1, full size 1:1, reduction 1:2.</summary>
    public static string Format(double scale)
    {
        if(!double.IsFinite(scale)||scale is <.0001 or >10000)throw new CadValidationException("Invalid drawing scale.");
        return scale>=1?scale.ToString("G6",CultureInfo.InvariantCulture)+":1":"1:"+(1/scale).ToString("G6",CultureInfo.InvariantCulture);
    }
}
