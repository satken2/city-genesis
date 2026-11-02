using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Night lights drawn only as material emission; light does not fall on the surroundings.
/// Window glass (a material name containing WINDOW or GLASS) glows with indoor light, and road surfaces
/// (the body materials of street and motorway variants) return street light by their reflectance.
/// The weather sets the level from the share of daylight no longer reaching the ground: lights come on as the sun sets
/// and go out at sunrise. Under the fixed exposure even a faint glow by day washes the colour out of dark glass.
/// Only the copies made by the shared material pools change, never assets (in the Editor that would rewrite the files).
/// </summary>
public static class NightLights
{
    private const float StreetLight = 0.3f; // Street light on road surfaces, multiplied by their reflectance.
    private static readonly Color WindowLight = new Color(1f, 0.8f, 0.55f); // Linear emission of a lit window at full night.

    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
    private static readonly int EmissionMapId = Shader.PropertyToID("_EmissionMap");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    private static readonly Dictionary<Material, Color> lights = new Dictionary<Material, Color>(); // Linear emission at full night.
    private static readonly List<Material> destroyed = new List<Material>();
    private static HashSet<string> streetSurfaces;
    private static float level;

    /// <summary>
    /// Make a pool's copy glow at night when its source is window glass, a road surface, or a copy that already glows.
    /// Other materials, and shaders without emission, stay as they are.
    /// </summary>
    public static void Adopt(Material copy, Material source)
    {
        if (copy == null || source == null) return;
        if (!lights.TryGetValue(source, out Color light) && !LightOf(source, out light)) return;
        if (!copy.HasProperty(EmissionColorId) || !copy.shader.keywordSpace.FindKeyword("_EMISSION").isValid) return;
        copy.SetTexture(EmissionMapId, Texture2D.whiteTexture);
        copy.EnableKeyword("_EMISSION");
        copy.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        lights[copy] = light;
        Apply(copy, light);
    }

    /// <summary>Lights from 0 (sun up) to 1 (sun down). Changes below 1/256 are skipped.</summary>
    public static void SetLevel(float value)
    {
        float next = Mathf.Round(Mathf.Clamp01(value) * 256f) / 256f;
        if (next == level) return;
        level = next;
        foreach (var pair in lights)
        {
            if (pair.Key == null) destroyed.Add(pair.Key);
            else Apply(pair.Key, pair.Value);
        }
        foreach (Material material in destroyed) lights.Remove(material);
        destroyed.Clear();
    }

    private static bool LightOf(Material source, out Color light)
    {
        string name = source.name;
        if (name.Contains("WINDOW") || name.Contains("GLASS"))
        {
            light = WindowLight;
            return true;
        }
        if (StreetSurfaces().Contains(name) && source.HasProperty(BaseColorId))
        {
            light = source.GetColor(BaseColorId).linear * StreetLight;
            return true;
        }
        light = default;
        return false;
    }

    private static HashSet<string> StreetSurfaces()
    {
        if (streetSurfaces != null) return streetSurfaces;
        var masters = MasterDataStore.Instance?.GetPathMasterDictionary();
        var names = new HashSet<string>();
        if (masters == null || masters.Count == 0) return names; // Not kept until the master arrives.
        foreach (PathMasterItem master in masters.Values)
        {
            if ((master.type == Constants.PATH_STREET || master.type == Constants.PATH_MOTORWAY) && !string.IsNullOrEmpty(master.body_material))
                names.Add(master.body_material);
        }
        return streetSurfaces = names;
    }

    /// <remarks>_EmissionColor is an [HDR] property: SetColor passes it to the shader as linear without conversion.</remarks>
    private static void Apply(Material material, Color light)
    {
        material.SetColor(EmissionColorId, light * level);
        HoverMaterials.Refresh(material);
    }
}
