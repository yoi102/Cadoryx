using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public interface IRepetitionPresentationVerifier
{
    Task ConfirmRepetitionPresentationAsync(FeatureDefinition feature,IAssetStore assets,CancellationToken cancellationToken=default);
}
