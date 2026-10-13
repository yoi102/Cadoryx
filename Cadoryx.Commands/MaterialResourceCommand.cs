using Cadoryx.Db;

namespace Cadoryx.Commands;

/// <summary>Validate texture bytes as well as metadata before publishing any material resource edit.</summary>
internal sealed class MaterialResourceCommand(string name, Func<DocumentSnapshot, DocumentSnapshot> edit) : ICadDocumentCommand
{
    public string Name => name;
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context, CancellationToken cancellationToken)
    {
        var prepared = await new EditDocumentCommand(name, edit).PrepareAsync(context, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var texture in prepared.Snapshot.MaterialTextures())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var lease = context.Assets.Acquire(texture.AssetId);
                CadTexturePayload.Verify(texture, lease.Content.Span);
            }
            return prepared;
        }
        catch { prepared.Dispose(); throw; }
    }
}
