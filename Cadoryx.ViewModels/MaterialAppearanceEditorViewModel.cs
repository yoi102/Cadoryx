using Cadoryx.Db;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cadoryx.ViewModels;

public interface IMaterialTextureImportService
{ Task<ImportedMaterialTexture?> ImportAsync(CancellationToken token = default); }

/// <summary>Copied decoded bytes. The selected path is never retained in the document.</summary>
public sealed class ImportedMaterialTexture
{
    private readonly byte[] pixels;
    public int Width { get; }
    public int Height { get; }
    public ImportedMaterialTexture(int width, int height, ReadOnlySpan<byte> rgba)
    {
        CadTexturePayload.ValidateDimensions(width, height);
        if (rgba.Length != checked(width * height * 4)) throw new CadValidationException("Texture pixels do not match dimensions.");
        Width = width; Height = height; pixels = rgba.ToArray();
    }
    public byte[] CopyPixels() => (byte[])pixels.Clone();
}

public partial class RenderMaterialEditorViewModel : ObservableObject
{
    [ObservableProperty] private uint colorArgb = 0xFFFFFFFF;
    [ObservableProperty] private double alpha = 1;
    [ObservableProperty] private double metallic;
    [ObservableProperty] private double roughness = .5;
    [ObservableProperty] private double indexOfRefraction = 1.5;
    [ObservableProperty] private double emission;
    public void Load(CadRenderMaterial value)
    { ColorArgb = value.ColorArgb; Alpha = value.Alpha; Metallic = value.Metallic; Roughness = value.Roughness; IndexOfRefraction = value.IndexOfRefraction; Emission = value.Emission; }
    public CadRenderMaterial Build() => new(ColorArgb, Alpha, Metallic, Roughness, IndexOfRefraction, Emission);
}

public partial class MaterialAppearanceEditorViewModel : ObservableObject
{
    [ObservableProperty] private bool enabled;
    [ObservableProperty] private bool distinguishSides;
    [ObservableProperty] private bool overrideSourceStyles;
    [ObservableProperty] private CadMaterialShading shading = CadMaterialShading.Pbr;
    [ObservableProperty] private CadMaterialCulling culling = CadMaterialCulling.DoubleSided;
    [ObservableProperty] private CadMaterialAlphaMode alphaMode;
    [ObservableProperty] private double alphaCutoff = .5;
    [ObservableProperty] private bool planar;
    [ObservableProperty] private double planeSA = 1;
    [ObservableProperty] private double planeSB;
    [ObservableProperty] private double planeSC;
    [ObservableProperty] private double planeSD;
    [ObservableProperty] private double planeTA;
    [ObservableProperty] private double planeTB = 1;
    [ObservableProperty] private double planeTC;
    [ObservableProperty] private double planeTD;
    [ObservableProperty] private double scaleS = 1;
    [ObservableProperty] private double scaleT = 1;
    [ObservableProperty] private double translationS;
    [ObservableProperty] private double translationT;
    [ObservableProperty] private double rotationDegrees;
    [ObservableProperty] private bool repeat;
    [ObservableProperty] private CadTextureFilter filter = CadTextureFilter.Bilinear;
    [ObservableProperty] private CadTextureAnisotropy anisotropy;
    [ObservableProperty] private string textureSummary = "";
    public RenderMaterialEditorViewModel Front { get; } = new();
    public RenderMaterialEditorViewModel Back { get; } = new();
    public sealed record Choice<T>(T Value, string Name);
    public IReadOnlyList<Choice<CadMaterialShading>> ShadingChoices { get; } =
        [new(CadMaterialShading.Phong, "Phong"), new(CadMaterialShading.Pbr, "PBR")];
    public IReadOnlyList<Choice<CadMaterialCulling>> CullingChoices =>
        [new(CadMaterialCulling.Automatic, Cadoryx.Lang.Strings.Strings.MaterialAutomatic),
         new(CadMaterialCulling.DoubleSided, Cadoryx.Lang.Strings.Strings.MaterialDoubleSided),
         new(CadMaterialCulling.Back, Cadoryx.Lang.Strings.Strings.MaterialCullBack),
         new(CadMaterialCulling.Front, Cadoryx.Lang.Strings.Strings.MaterialCullFront)];
    public IReadOnlyList<Choice<CadMaterialAlphaMode>> AlphaModeChoices =>
        [new(CadMaterialAlphaMode.Opaque, Cadoryx.Lang.Strings.Strings.MaterialOpaque),
         new(CadMaterialAlphaMode.Mask, Cadoryx.Lang.Strings.Strings.MaterialMask),
         new(CadMaterialAlphaMode.Blend, Cadoryx.Lang.Strings.Strings.MaterialBlend),
         new(CadMaterialAlphaMode.MaskBlend, Cadoryx.Lang.Strings.Strings.MaterialMaskBlend)];
    public IReadOnlyList<Choice<CadTextureFilter>> FilterChoices =>
        [new(CadTextureFilter.Nearest, Cadoryx.Lang.Strings.Strings.MaterialNearest),
         new(CadTextureFilter.Bilinear, Cadoryx.Lang.Strings.Strings.MaterialBilinear),
         new(CadTextureFilter.Trilinear, Cadoryx.Lang.Strings.Strings.MaterialTrilinear)];
    public IReadOnlyList<Choice<CadTextureAnisotropy>> AnisotropyChoices =>
        [new(CadTextureAnisotropy.Off, Cadoryx.Lang.Strings.Strings.MaterialAnisotropyOff),
         new(CadTextureAnisotropy.Fast, Cadoryx.Lang.Strings.Strings.MaterialAnisotropyFast),
         new(CadTextureAnisotropy.Middle, Cadoryx.Lang.Strings.Strings.MaterialAnisotropyMiddle),
         new(CadTextureAnisotropy.Quality, Cadoryx.Lang.Strings.Strings.MaterialAnisotropyQuality)];
    public CadTextureRef? Texture { get; private set; }
    public ImportedMaterialTexture? PendingTexture { get; private set; }
    public MaterialAppearanceEditorViewModel() => Load(null);
    public void Load(CadMaterialAppearance? appearance)
    {
        Enabled = appearance is not null; var value = appearance ?? new();
        Front.Load(value.Front); Back.Load(value.Back ?? value.Front); DistinguishSides = value.Back is not null;
        OverrideSourceStyles = value.OverrideSourceStyles; Shading = value.Shading; Culling = value.Culling;
        AlphaMode = value.AlphaMode; AlphaCutoff = value.AlphaCutoff;
        var m = value.Mapping; Planar = m.Planar;
        PlaneSA = m.PlaneS.A; PlaneSB = m.PlaneS.B; PlaneSC = m.PlaneS.C; PlaneSD = m.PlaneS.D;
        PlaneTA = m.PlaneT.A; PlaneTB = m.PlaneT.B; PlaneTC = m.PlaneT.C; PlaneTD = m.PlaneT.D;
        ScaleS = m.ScaleS; ScaleT = m.ScaleT; TranslationS = m.TranslationS; TranslationT = m.TranslationT;
        RotationDegrees = m.RotationDegrees; Repeat = m.Repeat; Filter = m.Filter; Anisotropy = m.Anisotropy;
        Texture = value.Texture; PendingTexture = null; UpdateTextureSummary();
    }
    public void Import(ImportedMaterialTexture texture)
    { ArgumentNullException.ThrowIfNull(texture); PendingTexture = texture; Texture = null; Enabled = true; UpdateTextureSummary(); }
    public void ClearTexture() { Texture = null; PendingTexture = null; UpdateTextureSummary(); }
    private void UpdateTextureSummary() => TextureSummary = PendingTexture is { } pending ? $"{pending.Width} × {pending.Height}" :
        Texture is { } stored ? $"{stored.Width} × {stored.Height}" : Cadoryx.Lang.Strings.Strings.MaterialNoTexture;
    public CadMaterialAppearance? Build()
    {
        if (!Enabled) return null;
        var value = new CadMaterialAppearance
        {
            Front = Front.Build(), Back = DistinguishSides ? Back.Build() : null, OverrideSourceStyles = OverrideSourceStyles,
            Shading = Shading, Culling = Culling, AlphaMode = AlphaMode, AlphaCutoff = AlphaCutoff, Texture = Texture,
            Mapping = new()
            {
                Planar = Planar, PlaneS = new(PlaneSA, PlaneSB, PlaneSC, PlaneSD), PlaneT = new(PlaneTA, PlaneTB, PlaneTC, PlaneTD),
                ScaleS = ScaleS, ScaleT = ScaleT, TranslationS = TranslationS, TranslationT = TranslationT, RotationDegrees = RotationDegrees,
                Repeat = Repeat, Filter = Filter, Anisotropy = Anisotropy
            }
        };
        value.Validate(); return value;
    }
}
