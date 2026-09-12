namespace Cadoryx.ViewModels.Services.Platform;
public enum SaveDecision { Save, Discard, Cancel }
public sealed record CadExportRequest(string Path,double LinearDeflectionMm,double AngularDeflectionRad,bool BinaryStl,bool VisibleOnly);
public interface ICadFileDialogs
{
    string? OpenDocument();
    string? SaveDocument(string name);
    Task<CadExportRequest?> ExportDocumentAsync(string name);
    Task<SaveDecision> ConfirmSaveAsync(string name);
}
