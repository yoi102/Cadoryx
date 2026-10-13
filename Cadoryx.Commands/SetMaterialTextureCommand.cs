using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Lang.Strings;

namespace Cadoryx.Commands;

/// <summary>One atomic material edit with an owned, validated image; session history acquires it before temporary release.</summary>
public sealed class SetMaterialTextureCommand : ICadDocumentCommand
{
    private readonly CadMaterial material;
    private readonly byte[] envelope;
    private readonly int width, height;
    private readonly bool add;
    public string Name => Strings.EditMaterial;
    public SetMaterialTextureCommand(CadMaterial material, int width, int height, ReadOnlySpan<byte> rgba, bool add = false)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (material.Appearance is null) throw new CadValidationException("Enable material appearance before assigning a texture.");
        material.Appearance.Validate(); this.material = material; this.width = width; this.height = height; this.add = add;
        envelope = CadTexturePayload.Encode(width, height, rgba);
    }
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!add && !context.Snapshot.Materials.ContainsKey(material.Id)) throw new CadValidationException(Strings.SelectMaterial);
        var lease = context.Assets.Stage(envelope);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var texture = new CadTextureRef(lease.Id, width, height, CadTexturePayload.CurrentFormat);
            var updated = material with { Appearance = material.Appearance! with { Texture = texture } };
            using var prepared = await (add ? ResourceCommands.AddMaterial(updated) : ResourceCommands.UpdateMaterial(updated))
                .PrepareAsync(context, cancellationToken).ConfigureAwait(false);
            prepared.Snapshot.Validate(); cancellationToken.ThrowIfCancellationRequested();
            return new PreparedDocumentEdit(prepared.Snapshot, [lease]);
        }
        catch { lease.Dispose(); throw; }
    }
}
