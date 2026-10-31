using System.Collections.Generic;
using UnityEngine;

/// <summary>ホバー用の材質を元材質ごとに共有し、テクスチャ・陰影・道路の白線を保つ。</summary>
public static class HoverMaterials
{
    private const float Brightness = 1.25f;
    private static readonly Color Emission = new Color(.1f, .1f, .1f, 0f);
    private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int LegacyColor = Shader.PropertyToID("_Color");
    private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
    private static readonly int EmissionMap = Shader.PropertyToID("_EmissionMap");
    private static readonly Dictionary<Material, Material> materials = new Dictionary<Material, Material>();

    static HoverMaterials()
    {
        AssetPackStore.CityChanging += Clear;
        Application.quitting += Clear;
    }

    public static Material Get(Material source)
    {
        if (source == null) return null;
        if (materials.TryGetValue(source, out var material) && material != null) return material;
        material = new Material(source) { name = source.name + " (Hover)", hideFlags = HideFlags.DontSave };
        int colorProperty = source.HasProperty(BaseColor) ? BaseColor : LegacyColor;
        if (source.HasProperty(colorProperty))
        {
            Color color = source.GetColor(colorProperty);
            material.SetColor(colorProperty, new Color(color.r * Brightness, color.g * Brightness, color.b * Brightness, color.a));
        }
        if (source.HasProperty(EmissionColor) && source.shader.keywordSpace.FindKeyword("_EMISSION").isValid)
        {
            // 既存の発光模様は残す。発光のない材質だけ全面に弱い白を足し、影の中でも対象を識別できるようにする。
            bool emissive = source.IsKeywordEnabled("_EMISSION");
            material.SetColor(EmissionColor, (emissive ? source.GetColor(EmissionColor) : Color.black) + Emission);
            if (!emissive && source.HasProperty(EmissionMap)) material.SetTexture(EmissionMap, Texture2D.whiteTexture);
            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }
        materials[source] = material;
        return material;
    }

    public static void Clear()
    {
        foreach (var material in materials.Values) if (material != null) Object.Destroy(material);
        materials.Clear();
    }
}
