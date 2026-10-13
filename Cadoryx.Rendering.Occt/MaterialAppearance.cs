using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Rendering.Occt;

public sealed partial class OcctViewport
{
    // GPU textures and their asset leases belong only to this viewport and its UI thread.
    private readonly Dictionary<CadTextureRef, MaterialTextureResource> materialTextures = [];
    private ViewerRenderProfile? progressiveOriginalMaterialProfile;
    public int NativeMaterialTextureCount => materialTextures.Count;
    private static ViewerReviewMaterial Material(CadRenderMaterial value)
    {
        static double Linear(uint channel)
        { double s = channel / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        return new(new(Linear((value.ColorArgb >> 16) & 255), Linear((value.ColorArgb >> 8) & 255), Linear(value.ColorArgb & 255)))
        { Alpha = value.Alpha, Metallic = value.Metallic, Roughness = value.Roughness, IndexOfRefraction = value.IndexOfRefraction, Emission = value.Emission };
    }
    private void ApplyMaterialAppearance(ViewerPresentation presentation, CadMaterialAppearance value)
    {
        value.Validate();
        var mapping = value.Mapping;
        var profile = new ViewerAppearanceProfile
        {
            Front = Material(value.Front), Back = Material(value.Back ?? value.Front), DistinguishSides = value.Back is not null,
            Shading = value.Shading == CadMaterialShading.Pbr ? ViewerShading.Pbr : ViewerShading.Phong,
            Culling = (ViewerFaceCulling)value.Culling, AlphaMode = (ViewerAlphaMode)value.AlphaMode, AlphaCutoff = value.AlphaCutoff,
            Mapping = new()
            {
                Planar = mapping.Planar, PlaneS = new(mapping.PlaneS.A, mapping.PlaneS.B, mapping.PlaneS.C, mapping.PlaneS.D),
                PlaneT = new(mapping.PlaneT.A, mapping.PlaneT.B, mapping.PlaneT.C, mapping.PlaneT.D),
                ScaleS = mapping.ScaleS, ScaleT = mapping.ScaleT, TranslationS = mapping.TranslationS, TranslationT = mapping.TranslationT,
                RotationDegrees = mapping.RotationDegrees, Repeat = mapping.Repeat,
                Filter = (ViewerTextureFilter)mapping.Filter, Anisotropy = (ViewerTextureAnisotropy)mapping.Anisotropy
            }
        };
        viewer.Rendering.SetAppearance(presentation, profile, value.Texture is { } texture ? MaterialTexture(texture).Texture : null);
    }
    private MaterialTextureResource MaterialTexture(CadTextureRef reference)
    {
        reference.Validate();
        if (materialTextures.TryGetValue(reference, out var resource)) return resource;
        resource = new(viewer, reference, assets); materialTextures.Add(reference, resource); return resource;
    }
    private void SetMaterialSceneProfile(CadScene scene)
    {
        var previous = viewer.Rendering.GetProfile();
        var shading = scene.Items.Any(i => i.MaterialAppearance?.Shading == CadMaterialShading.Pbr) ? ViewerShading.Pbr : ViewerShading.Phong;
        if (previous.Shading != shading) viewer.Rendering.SetProfile(previous with { Shading = shading });
    }
    private void PruneMaterialTextures()
    {
        var used = entries.Values.Select(e => e.Item.MaterialAppearance?.Texture).OfType<CadTextureRef>().ToHashSet();
        Exception? failure=null;
        foreach (var key in materialTextures.Keys.Where(k => !used.Contains(k)).ToArray())
        { DisposeOwnedResource(materialTextures[key],ref failure); materialTextures.Remove(key); }
        ThrowCleanupFailure(failure);
    }
    private sealed class MaterialTextureResource : IDisposable
    {
        private readonly IAssetLease lease;
        public ViewerTexture Texture { get; }
        public MaterialTextureResource(OcctViewer viewer, CadTextureRef reference, IAssetStore assets)
        {
            lease = assets.Acquire(reference.AssetId);
            try
            {
                CadTexturePayload.Verify(reference, lease.Content.Span);
                Texture = viewer.Rendering.CreateTexture(new ViewerPixelImage(reference.Width, reference.Height,
                    lease.Content.Span[CadTexturePayload.HeaderBytes..]));
            }
            catch { lease.Dispose(); throw; }
        }
        public void Dispose() { try { Texture.Dispose(); } finally { lease.Dispose(); } }
    }
}
