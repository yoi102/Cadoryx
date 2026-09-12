using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

public sealed record CadDiagnostic(string Code,string Message,bool IsError=false);
public sealed class GeometryResult(GeometryAssetRef geometry,IAssetLease lease,ImmutableArray<CadDiagnostic> diagnostics=default) : IDisposable
{
    public GeometryAssetRef Geometry { get; }=geometry;
    public ImmutableArray<CadDiagnostic> Diagnostics { get; }=diagnostics.IsDefault?[]:diagnostics;
    public void Dispose()=>lease.Dispose();
}
public sealed class LoadedDocument(DocumentSnapshot snapshot,IEnumerable<IAssetLease> assets,ImmutableArray<CadDiagnostic> diagnostics=default) : IDisposable
{
    private readonly IAssetLease[] leases=assets.ToArray();
    public DocumentSnapshot Snapshot { get; }=snapshot;
    public ImmutableArray<CadDiagnostic> Diagnostics { get; }=diagnostics.IsDefault?[]:diagnostics;
    public void Dispose(){foreach(var lease in leases)lease.Dispose();}
}
public interface IGeometryKernel
{
    string Version { get; }
    bool Supports(GeometryRecipe recipe);
    Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken cancellationToken=default);
    Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken cancellationToken=default);
    Task ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken cancellationToken=default);
    Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CadExportOptions options,CancellationToken cancellationToken=default);
}
public sealed record CadExportOptions(double LinearDeflectionMm=0.1,double AngularDeflectionRad=0.5,bool BinaryStl=true,bool VisibleOnly=true)
{
    public void Validate(){CadGuard.Positive(LinearDeflectionMm,AngularDeflectionRad);if(AngularDeflectionRad>Math.PI)throw new CadValidationException("Mesh angle must not exceed PI.");}
}
public sealed record CadExportReport(string Path,string Format,ImmutableArray<CadDiagnostic> Diagnostics);
public interface IDocumentStorage
{
    Task<LoadedDocument> LoadAsync(string path,IAssetStore assets,CancellationToken cancellationToken=default);
    Task SaveAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken cancellationToken=default);
    Task<DocumentSettings> ReadSettingsAsync(string path,CancellationToken cancellationToken=default);
}
