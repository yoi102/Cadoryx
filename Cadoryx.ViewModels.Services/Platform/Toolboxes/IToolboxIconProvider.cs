namespace Cadoryx.ViewModels.Services.Platform;

public interface IToolboxIconProvider
{
    object ModelTree { get; }
    object Properties { get; }
    object Modeling { get; }
    object Messages { get; }
}
