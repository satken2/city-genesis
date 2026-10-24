using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// On/off switches for rendering features with high GPU cost. Defaults keep full quality.
/// The settings screen will write these values. Sun and prop switches are read where they apply every frame.
/// </summary>
public static class GraphicsConfig
{
    public static bool SunShadows = true;       // Sun shadow maps: four cascades drawing every shadow caster.
    public static bool SoftShadows = true;      // 7x7 filtering of sun shadow edges.
    public static bool PropShadows = true;      // Tree and prop shadows within the shadow distance.
    public static bool AmbientOcclusion = true; // SSAO and its depth-normals prepass. Takes effect through ApplyAmbientOcclusion.

    private static readonly Dictionary<ScriptableRendererFeature, bool> originalAmbientOcclusion = new Dictionary<ScriptableRendererFeature, bool>();

    /// <summary>
    /// Reflect AmbientOcclusion in the active pipeline's SSAO features. Feature state is serialized in the renderer asset,
    /// so restore it on quit; otherwise an Editor play session would leave the asset rewritten.
    /// </summary>
    public static void ApplyAmbientOcclusion()
    {
        var asset = UniversalRenderPipeline.asset;
        if (asset == null) return;
        foreach (var data in asset.rendererDataList)
        {
            if (data == null) continue;
            foreach (var feature in data.rendererFeatures)
            {
                if (feature is not ScreenSpaceAmbientOcclusion || feature.isActive == AmbientOcclusion) continue;
                if (originalAmbientOcclusion.Count == 0) Application.quitting += RestoreAmbientOcclusion;
                if (!originalAmbientOcclusion.ContainsKey(feature)) originalAmbientOcclusion.Add(feature, feature.isActive);
                feature.SetActive(AmbientOcclusion);
            }
        }
    }

    private static void RestoreAmbientOcclusion()
    {
        Application.quitting -= RestoreAmbientOcclusion;
        foreach (var pair in originalAmbientOcclusion)
            if (pair.Key != null) pair.Key.SetActive(pair.Value);
        originalAmbientOcclusion.Clear();
    }
}
