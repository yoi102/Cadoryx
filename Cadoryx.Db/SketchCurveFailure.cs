namespace Cadoryx.Db;

/// <summary>Stable diagnostic keys keep exact author errors localizable while retaining native detail.</summary>
public static class SketchCurveFailure
{
    public const string ResourceKey="SketchCurve.Message";
    public static CadValidationException Create(string key,string diagnostic)
    {
        var error=new CadValidationException(diagnostic);error.Data[ResourceKey]=key;return error;
    }
}
