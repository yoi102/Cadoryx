using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cadoryx.Db;

// Persisted MessagePack values: append new values; never reorder or reuse a number.
public enum BodyKind { Solid=0, Sheet=1, Wire=2, Compound=3, Mesh=4, Empty=5 }
public sealed record XdeSourceRef(AssetId ContextAssetId,string DefinitionEntry);
public sealed record GeometryAssetRef(AssetId AssetId, GeometryRevisionId Revision, BodyKind Kind, Bounds3d Bounds, double VolumeMm3, XdeSourceRef? Source = null)
{
    public void Validate() { AssetId.Validate(); CadGuard.Id(Revision); Bounds.Validate(); CadGuard.Finite(VolumeMm3); if(VolumeMm3<0 || !Enum.IsDefined(Kind)) throw new CadValidationException("Invalid geometry metadata."); if(Source is {} s){s.ContextAssetId.Validate();if(string.IsNullOrWhiteSpace(s.DefinitionEntry))throw new CadValidationException("Invalid XDE source entry.");} }
}
public sealed record CadAppearance(uint Argb = 0xFF86ACC5, bool ByLayer = false);
public sealed record CadLayer(LayerId Id, string Name, uint Argb = 0xFF86ACC5, bool IsVisible = true, bool IsLocked = false);
public sealed record CadMaterial(MaterialId Id, string Name, double DensityKgPerMm3);
public sealed record CadBody(BodyId Id, DefinitionId PartId, string Name, GeometryAssetRef Geometry,
    FeatureId? Producer, LayerId LayerId, CadAppearance Appearance, bool IsVisible = true, MaterialId? MaterialId = null);
public sealed record ComponentSlot(ComponentSlotId Id, DefinitionId DefinitionId, string Name,
    RigidTransform3d LocalTransform, bool IsVisible = true, CadAppearance? AppearanceOverride = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PartDefinition), "part")]
[JsonDerivedType(typeof(AssemblyDefinition), "assembly")]
public abstract record CadDefinition(DefinitionId Id, string Name);
public sealed record PartDefinition(DefinitionId Id, string Name, ImmutableArray<BodyId> Bodies,
    ImmutableArray<FeatureId> Features) : CadDefinition(Id,Name);
public sealed record AssemblyDefinition(DefinitionId Id, string Name, ImmutableArray<ComponentSlot> Children) : CadDefinition(Id,Name);

public sealed class OccurrencePath : IEquatable<OccurrencePath>
{
    public DocumentId DocumentId { get; }
    public ImmutableArray<ComponentSlotId> Slots { get; }
    public OccurrencePath(DocumentId documentId, IEnumerable<ComponentSlotId> slots)
    {
        CadGuard.Id(documentId); DocumentId=documentId; Slots=slots.ToImmutableArray();
        foreach(var slot in Slots) CadGuard.Id(slot);
    }
    public OccurrencePath Append(ComponentSlotId slot) => new(DocumentId, Slots.Add(slot));
    public bool Equals(OccurrencePath? other) => other is not null && DocumentId==other.DocumentId && Slots.SequenceEqual(other.Slots);
    public override bool Equals(object? obj) => obj is OccurrencePath other && Equals(other);
    public override int GetHashCode() { var hash=new HashCode(); hash.Add(DocumentId); foreach(var id in Slots) hash.Add(id); return hash.ToHashCode(); }
    public override string ToString() => DocumentId + "/" + string.Join("/",Slots);
}
public sealed record CadOccurrence(OccurrencePath Path, DefinitionId DefinitionId, string Name,
    RigidTransform3d WorldTransform, bool IsVisible, CadAppearance? AppearanceOverride);
