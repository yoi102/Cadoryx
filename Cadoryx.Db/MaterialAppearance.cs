namespace Cadoryx.Db;

// Persisted values: append; never reorder or reuse.
public enum CadMaterialShading { Phong = 0, Pbr = 1 }
public enum CadMaterialCulling { Automatic = 0, DoubleSided = 1, Back = 2, Front = 3 }
public enum CadMaterialAlphaMode { Opaque = 0, Mask = 1, Blend = 2, MaskBlend = 3 }
public enum CadTextureFilter { Nearest = 0, Bilinear = 1, Trilinear = 2 }
public enum CadTextureAnisotropy { Off = 0, Fast = 1, Middle = 2, Quality = 3 }

/// <summary>Document-owned sRGB color; conversion to linear light occurs at the renderer boundary.</summary>
public sealed record CadRenderMaterial(uint ColorArgb = 0xFFFFFFFF, double Alpha = 1,
    double Metallic = 0, double Roughness = .5, double IndexOfRefraction = 1.5, double Emission = 0)
{
    public void Validate()
    {
        if ((ColorArgb >> 24) != 255) throw new CadValidationException("Material color uses opaque RGB; use Alpha for opacity.");
        Unit(Alpha); Unit(Metallic); Unit(Roughness);
        CadGuard.Finite(IndexOfRefraction); CadGuard.Finite(Emission);
        if (IndexOfRefraction is < 1 or > 3 || Emission is < 0 or > 100)
            throw new CadValidationException("Invalid material refraction or emission.");
    }
    internal static void Unit(double value)
    { CadGuard.Finite(value); if (value is < 0 or > 1) throw new CadValidationException("Expected a value between zero and one."); }
}

public sealed record CadTexturePlane(double A, double B, double C, double D)
{
    public void Validate()
    {
        foreach (var value in new[] { A, B, C, D })
        { CadGuard.Finite(value); if (Math.Abs(value) > 1e6) throw new CadValidationException("Texture plane coefficients must be within -1000000 and 1000000."); }
        if (Math.Max(Math.Abs(A), Math.Max(Math.Abs(B), Math.Abs(C))) < 1e-12)
            throw new CadValidationException("Texture plane requires a direction.");
    }
}

/// <summary>Appearance coordinates only; authored BRep UVs and topology remain unchanged.</summary>
public sealed record CadTextureMapping
{
    public bool Planar { get; init; }
    public CadTexturePlane PlaneS { get; init; } = new(1, 0, 0, 0);
    public CadTexturePlane PlaneT { get; init; } = new(0, 1, 0, 0);
    public double ScaleS { get; init; } = 1;
    public double ScaleT { get; init; } = 1;
    public double TranslationS { get; init; }
    public double TranslationT { get; init; }
    public double RotationDegrees { get; init; }
    public bool Repeat { get; init; }
    public CadTextureFilter Filter { get; init; } = CadTextureFilter.Bilinear;
    public CadTextureAnisotropy Anisotropy { get; init; }
    public void Validate()
    {
        if (PlaneS is null || PlaneT is null || !Enum.IsDefined(Filter) || !Enum.IsDefined(Anisotropy))
            throw new CadValidationException("Invalid texture mapping.");
        PlaneS.Validate(); PlaneT.Validate();
        foreach (var value in new[] { ScaleS, ScaleT, TranslationS, TranslationT, RotationDegrees }) CadGuard.Finite(value);
        if (ScaleS is < 1e-6 or > 1e6 || ScaleT is < 1e-6 or > 1e6)
            throw new CadValidationException("Texture scale must be between 0.000001 and 1000000; negative scales are unsupported.");
        if (Math.Abs(TranslationS) > 1e6 || Math.Abs(TranslationT) > 1e6 || Math.Abs(RotationDegrees) > 360000)
            throw new CadValidationException("Texture translation or rotation exceeds the renderer range.");
        if (Planar)
        {
            // Bounded coefficients keep this exact native-contract check finite.
            var s = PlaneS; var t = PlaneT;
            var x = s.B * t.C - s.C * t.B; var y = s.C * t.A - s.A * t.C; var z = s.A * t.B - s.B * t.A;
            if (x * x + y * y + z * z <= 1e-24) throw new CadValidationException("Texture planes must be independent within the native renderer range.");
        }
    }
}

/// <summary>A complete owned pixel envelope. No external path is persisted or used as a fallback.</summary>
public sealed record CadTextureRef(AssetId AssetId, int Width, int Height, AssetFormat Format, int SchemaVersion = 1)
{
    public void Validate()
    {
        AssetId.Validate(); CadTexturePayload.ValidateDimensions(Width, Height);
        if (SchemaVersion != 1 || Format is null) throw new CadValidationException("Unsupported material texture schema.");
        CadTexturePayload.RequireFormat(Format);
    }
}

/// <summary>Optional complete material override; null on legacy materials retains the existing display behavior.</summary>
public sealed record CadMaterialAppearance
{
    public CadRenderMaterial Front { get; init; } = new();
    public CadRenderMaterial? Back { get; init; }
    public CadMaterialShading Shading { get; init; } = CadMaterialShading.Pbr;
    public CadMaterialCulling Culling { get; init; } = CadMaterialCulling.DoubleSided;
    public CadMaterialAlphaMode AlphaMode { get; init; }
    public double AlphaCutoff { get; init; } = .5;
    /// <summary>Explicit consent in the authored material to replace imported per-face colors/textures.</summary>
    public bool OverrideSourceStyles { get; init; }
    public CadTextureRef? Texture { get; init; }
    public CadTextureMapping Mapping { get; init; } = new();
    public void Validate()
    {
        if (Front is null || Mapping is null || !Enum.IsDefined(Shading) || !Enum.IsDefined(Culling) || !Enum.IsDefined(AlphaMode))
            throw new CadValidationException("Invalid material appearance.");
        Front.Validate(); Back?.Validate(); Mapping.Validate(); Texture?.Validate(); CadRenderMaterial.Unit(AlphaCutoff);
    }
}
