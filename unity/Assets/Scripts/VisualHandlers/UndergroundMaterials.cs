using System.Collections.Generic;
using UnityEngine;

/// <summary>地下表示の半透明材質を元材質ごとに共有し、既存の色分けとGPUインスタンス描画を保つ。</summary>
public static class UndergroundMaterials
{
    public const float SurfaceOpacity = .08f;
    private static readonly Dictionary<Material, Material> ghosts = new Dictionary<Material, Material>();
    private static readonly Dictionary<Material, Material> sources = new Dictionary<Material, Material>();
    public static Material Source(Material material) => material != null && sources.TryGetValue(material, out var source) ? source : material;

    public static Material Ghost(Material source)
    {
        if (source == null) return null;
        source = Source(source);
        if (ghosts.TryGetValue(source, out var ghost) && ghost != null) return ghost;
        ghost = new Material(Resources.Load<Shader>("Shaders/UndergroundGhost")) { name = source.name + " (Underground)", enableInstancing = true };
        ghost.SetFloat("_Opacity", SurfaceOpacity);
        ghost.SetColor("_BaseColor", source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") : source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white);
        if (source.HasProperty("_BaseMap")) ghost.SetTexture("_BaseMap", source.GetTexture("_BaseMap"));
        else if (source.HasProperty("_MainTex")) ghost.SetTexture("_BaseMap", source.GetTexture("_MainTex"));
        if (source.HasProperty("_BaseMap") || source.HasProperty("_MainTex"))
        {
            ghost.mainTextureScale = source.mainTextureScale;
            ghost.mainTextureOffset = source.mainTextureOffset;
        }
        ghosts[source] = ghost;
        sources[ghost] = source;
        return ghost;
    }

    public static Material ForSurface(Material source) => UndergroundView.IsActive ? Ghost(source) : Source(source);

    public static void Clear()
    {
        foreach (var material in ghosts.Values) if (material != null) Object.Destroy(material);
        ghosts.Clear(); sources.Clear();
    }
}
