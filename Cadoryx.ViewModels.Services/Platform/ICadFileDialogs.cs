namespace Cadoryx.ViewModels.Services.Platform;
public enum SaveDecision { Save, Discard, Cancel }
public sealed record CadExportRequest(string Path,double LinearDeflectionMm,double AngularDeflectionRad,bool BinaryStl,bool VisibleOnly);
public interface ICadFileDialogs
{
    string? OpenDocument();
    string? SaveDocument(string name);
    CadExportRequest? ExportDocument(string name);
    SaveDecision ConfirmSave(string name);
}
