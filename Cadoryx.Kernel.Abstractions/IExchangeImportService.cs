namespace Cadoryx.Kernel.Abstractions;

/// <summary>Imports an exchange file with caller-owned result leases and cancellable execution.</summary>
public interface IExchangeImportService
{
    Task<LoadedDocument> ImportAsync(string path, IAssetStore assets, CancellationToken cancellationToken = default);
}
