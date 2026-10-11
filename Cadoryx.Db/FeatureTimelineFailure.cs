namespace Cadoryx.Db;

public static class FeatureTimelineFailure
{
    public const string ResourceKey="FeatureTimeline.Message";
    public const string ArgumentsKey="FeatureTimeline.Arguments";
    public static CadValidationException Create(string key,string diagnostic,params object[] arguments)
    {
        var error=new CadValidationException(diagnostic);error.Data[ResourceKey]=key;error.Data[ArgumentsKey]=arguments;return error;
    }
}
