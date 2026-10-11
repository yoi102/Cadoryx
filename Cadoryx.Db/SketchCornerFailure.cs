namespace Cadoryx.Db;

/// <summary>Resource-key metadata keeps authoring errors localizable without a UI dependency in geometry.</summary>
public static class SketchCornerFailure
{
    public static CadValidationException Create(string key,object? argument=null)
    {
        var error=new CadValidationException(key);error.Data["SketchCorner.Message"]=key;error.Data["SketchCorner.Argument"]=argument?.ToString()??"";return error;
    }
}
