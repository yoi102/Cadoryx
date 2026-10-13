using System.Collections.Immutable;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackMaterialAppearances([property: Key(0)] PackMaterialAppearanceEntry[] Entries);
[MessagePackObject]
public sealed record PackMaterialAppearanceEntry([property: Key(0)] Guid Material, [property: Key(1)] byte[] AppearanceJson);
public sealed record MaterialAppearanceEntry(MaterialId Material, CadMaterialAppearance Appearance);

/// <summary>Required independent partition. Existing presentation keys remain name/density only.</summary>
public static class MaterialAppearanceSectionStorage
{
    public const string Kind = "material-appearances", Capability = "cadoryx.material-appearances.1";
    public static SectionFormat Format { get; } = new(Kind, 1, "messagepack");
    private static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard
        .WithSecurity(MessagePackSecurity.UntrustedData.WithMaximumObjectGraphDepth(64));
    public static byte[] Encode(IEnumerable<CadMaterial> materials)
    {
        var entries = materials.Where(m => m.Appearance is not null).OrderBy(m => m.Id.Value).Select(m =>
        {
            m.Appearance!.Validate();
            return new PackMaterialAppearanceEntry(m.Id.Value, JsonSerializer.SerializeToUtf8Bytes(m.Appearance, CadJson.Options));
        }).ToArray();
        if (entries.Length == 0) throw new InvalidDataException("Material appearance partition requires actual appearances.");
        return MessagePackSerializer.Serialize(new PackMaterialAppearances(entries), Options);
    }
    public static MaterialAppearanceEntry[] Read(ReadOnlyMemory<byte> bytes, string encoding, int version)
    {
        if (version != 1 || encoding != "messagepack") throw new NotSupportedException("Unsupported material appearance partition.");
        var reader = new MessagePackReader(bytes);
        var data = MessagePackSerializer.Deserialize<PackMaterialAppearances>(ref reader, Options);
        if (!reader.End || data?.Entries is not { Length: > 0 } || data.Entries.Any(e => e is null || e.AppearanceJson is null) ||
            data.Entries.Select(e => e.Material).Distinct().Count() != data.Entries.Length)
            throw new InvalidDataException("Empty, duplicate, trailing or malformed material appearances.");
        return data.Entries.Select(e =>
        {
            if (e.Material == Guid.Empty || e.AppearanceJson.Length is < 2 or > 65536)
                throw new InvalidDataException("Invalid material appearance identity or length.");
            var value = JsonSerializer.Deserialize<CadMaterialAppearance>(e.AppearanceJson, CadJson.Options)
                ?? throw new InvalidDataException("Missing material appearance.");
            value.Validate(); return new MaterialAppearanceEntry(new(e.Material), value);
        }).ToArray();
    }
    public static void AddAssetRoles(MaterialAppearanceEntry[] entries, Dictionary<AssetId, string> roles)
    {
        foreach (var texture in entries.Select(e => e.Appearance.Texture).OfType<CadTextureRef>())
        {
            texture.Validate();
            if (roles.TryGetValue(texture.AssetId, out var role) && role != CadTexturePayload.MediaType)
                throw new InvalidDataException("Conflicting material texture asset role.");
            roles[texture.AssetId] = CadTexturePayload.MediaType;
        }
    }
    public static ImmutableDictionary<MaterialId, CadMaterial> Attach(MaterialAppearanceEntry[] entries,
        ImmutableDictionary<MaterialId, CadMaterial> materials, IReadOnlyDictionary<AssetId, AssetFormat> formats, IAssetStore assets)
    {
        foreach (var entry in entries)
        {
            if (!materials.TryGetValue(entry.Material, out var material) || material.Appearance is not null)
                throw new InvalidDataException("Appearance refers to a missing or already declared material.");
            if (entry.Appearance.Texture is { } texture)
            {
                if (!formats.TryGetValue(texture.AssetId, out var format) || format != texture.Format)
                    throw new InvalidDataException("Texture format/provenance differs from the asset catalog.");
                using var lease = assets.Acquire(texture.AssetId);
                CadTexturePayload.Verify(texture, lease.Content.Span);
            }
            materials = materials.SetItem(entry.Material, material with { Appearance = entry.Appearance });
        }
        return materials;
    }
}
