using System.Security.Cryptography;
using System.Text;

namespace Cadoryx.Db;

/// <summary>A frozen in-document part snapshot with a version-pinned source hint.
/// The source is never opened implicitly while loading or rendering a document.</summary>
public sealed record ExternalPartLink(DefinitionId TargetPartId,DocumentId SourceDocumentId,
    DefinitionId SourcePartId,DocumentStateId SourceStateId,string SourceSha256,
    string SourcePath,string? AbsolutePathHint,string LocalFingerprint)
{
    public void Validate()
    {
        CadGuard.Id(TargetPartId);CadGuard.Id(SourceDocumentId);CadGuard.Id(SourcePartId);CadGuard.Id(SourceStateId);
        if(!Hash(SourceSha256)||!Hash(LocalFingerprint)||string.IsNullOrWhiteSpace(SourcePath)||
           SourcePath.Length>4096||SourcePath.IndexOf('\0')>=0||
           AbsolutePathHint is {Length:>4096}||AbsolutePathHint?.IndexOf('\0')>=0)
            throw new CadValidationException("Invalid external part source metadata.");
    }
    private static bool Hash(string? value)=>value is {Length:64}&&value.All(Uri.IsHexDigit);

    public static string Fingerprint(DocumentSnapshot snapshot,PartDefinition part)
    {
        var data=new StringBuilder();
        foreach(var id in part.Bodies.OrderBy(id=>id.Value))
        {
            var body=snapshot.Bodies[id];var geometry=body.Geometry;
            data.Append(body.Id.Value.ToString("N")).Append('|').Append(body.Name).Append('|')
                .Append(geometry.AssetId.Sha256).Append('|').Append(geometry.Revision.Value.ToString("N"))
                .Append('|').Append(body.LayerId.Value.ToString("N")).Append('|')
                .Append(body.MaterialId?.Value.ToString("N")).Append('|').Append(body.Appearance.Argb)
                .Append('|').Append(body.Appearance.ByLayer).Append('|').Append(body.Appearance.PreserveSourceStyles)
                .Append('|').Append(body.IsVisible).Append(';');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToString())));
    }
}
