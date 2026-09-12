namespace Cadoryx.Db;

public interface ICadId { Guid Value { get; } }

public readonly record struct DocumentId(Guid Value) : ICadId
{
    public static DocumentId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct DocumentStateId(Guid Value) : ICadId
{
    public static DocumentStateId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct DefinitionId(Guid Value) : ICadId
{
    public static DefinitionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct ComponentSlotId(Guid Value) : ICadId
{
    public static ComponentSlotId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct BodyId(Guid Value) : ICadId
{
    public static BodyId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct FeatureId(Guid Value) : ICadId
{
    public static FeatureId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct SketchId(Guid Value) : ICadId
{
    public static SketchId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct DatumId(Guid Value) : ICadId
{
    public static DatumId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct LayerId(Guid Value) : ICadId
{
    public static LayerId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct MaterialId(Guid Value) : ICadId
{
    public static MaterialId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct GeometryRevisionId(Guid Value) : ICadId
{
    public static GeometryRevisionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public readonly record struct AssetId(string Sha256)
{
    public void Validate()
    {
        if (Sha256 is null || Sha256.Length != 64 || Sha256.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("Asset ID must be a lowercase SHA-256.");
    }
    public override string ToString() => Sha256;
}
public sealed class CadValidationException(string message) : ArgumentException(message);
public static class CadGuard
{
    public static void Id(ICadId id) { if (id.Value == Guid.Empty) throw new CadValidationException("An empty identity is invalid."); }
    public static void Name(string name) { if (string.IsNullOrWhiteSpace(name) || name.Length > 512) throw new CadValidationException("Name must contain 1–512 characters."); }
    public static void Finite(params double[] values) { if (values.Any(v => !double.IsFinite(v))) throw new CadValidationException("Coordinates must be finite."); }
    public static void Positive(params double[] values) { Finite(values); if (values.Any(v => v <= 0)) throw new CadValidationException("Dimensions must be positive."); }
}
