using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Player graphics settings: on/off switches for rendering features with high GPU cost and multipliers on render distances.
/// Defaults keep full quality and the standard distances.
/// GraphicsSettingsPanel writes these values and saves them to graphics_settings.json in the user directory; AppRoot loads them at startup.
/// Sun, prop and distance values are read where they apply.
/// </summary>
public static class GraphicsConfig
{
    public static bool SunShadows = true;       // Sun shadow maps: four cascades drawing every shadow caster.
    public static bool SoftShadows = true;      // 7x7 filtering of sun shadow edges.
    public static bool PropShadows = true;      // Tree and prop shadows within the shadow distance.
    public static bool AmbientOcclusion = true; // SSAO and its depth-normals prepass. Takes effect through ApplyAmbientOcclusion.
    public static float PropDistanceScale = 1f; // Multiplier on the camera distance beyond which props are not drawn. Takes effect through ApplyDistances.
    public static float FarDistanceScale = 1f;  // Multiplier on the distance at which objects switch to far models. Takes effect through ApplyDistances.

    /// <summary>Advanced by ApplyDistances. LOD workers that wait for camera movement also reevaluate when it changes.</summary>
    public static int DistanceVersion { get; private set; }

    private const string FILE_NAME = "graphics_settings.json";

    private static readonly Dictionary<ScriptableRendererFeature, bool> originalAmbientOcclusion = new Dictionary<ScriptableRendererFeature, bool>();
    private static float? originalLodBias;

    /// <summary>
    /// Saved form of the switches. Fields start from the current values, so keys missing from the file keep them.
    /// </summary>
    private sealed class Snapshot
    {
        public bool SunShadows = GraphicsConfig.SunShadows;
        public bool SoftShadows = GraphicsConfig.SoftShadows;
        public bool PropShadows = GraphicsConfig.PropShadows;
        public bool AmbientOcclusion = GraphicsConfig.AmbientOcclusion;
        public float PropDistanceScale = GraphicsConfig.PropDistanceScale;
        public float FarDistanceScale = GraphicsConfig.FarDistanceScale;
    }

    /// <summary>
    /// Read the saved switches. Without a user directory (browser build) or a readable file, the defaults stay.
    /// </summary>
    public static void Load()
    {
        if (UserDataDir.Root == null) return;
        string path = Path.Combine(UserDataDir.Root, FILE_NAME);
        if (!File.Exists(path)) return;
        try
        {
            var saved = JsonConvert.DeserializeObject<Snapshot>(File.ReadAllText(path));
            if (saved == null) return;
            SunShadows = saved.SunShadows;
            SoftShadows = saved.SoftShadows;
            PropShadows = saved.PropShadows;
            AmbientOcclusion = saved.AmbientOcclusion;
            PropDistanceScale = saved.PropDistanceScale;
            FarDistanceScale = saved.FarDistanceScale;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
        {
            Debug.LogWarning($"GraphicsConfig: {path} を読めません。既定のグラフィック設定で描きます: {e.Message}");
        }
    }

    public static void Save()
    {
        if (UserDataDir.Root == null) return;
        string path = Path.Combine(UserDataDir.Root, FILE_NAME);
        try
        {
            Directory.CreateDirectory(UserDataDir.Root);
            File.WriteAllText(path, JsonConvert.SerializeObject(new Snapshot(), Formatting.Indented));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Debug.LogWarning($"GraphicsConfig: {path} に保存できません: {e.Message}");
        }
    }

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

    /// <summary>
    /// Reflect the distance multipliers. DrawLodConfig reads them directly; LODGroups switch through QualitySettings.lodBias,
    /// so scale the quality level's own bias by FarDistanceScale. Restore the bias on quit so an Editor play session does not leave the project's quality settings rewritten.
    /// </summary>
    public static void ApplyDistances()
    {
        DistanceVersion++;
        if (originalLodBias == null)
        {
            originalLodBias = QualitySettings.lodBias;
            Application.quitting += RestoreLodBias;
        }
        QualitySettings.lodBias = originalLodBias.Value * FarDistanceScale;
    }

    private static void RestoreLodBias()
    {
        Application.quitting -= RestoreLodBias;
        if (originalLodBias != null) QualitySettings.lodBias = originalLodBias.Value;
        originalLodBias = null;
    }

    private static void RestoreAmbientOcclusion()
    {
        Application.quitting -= RestoreAmbientOcclusion;
        foreach (var pair in originalAmbientOcclusion)
            if (pair.Key != null) pair.Key.SetActive(pair.Value);
        originalAmbientOcclusion.Clear();
    }
}
